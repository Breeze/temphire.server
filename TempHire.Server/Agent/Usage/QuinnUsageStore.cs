using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using TempHire.Model;
using TempHire.Server.Controllers;

namespace TempHire.Server.Agent.Usage
{
    /// <summary>
    /// Quinn's usage - tokens, and what they cost - recorded after every question, and read back
    /// against the dollar quotas: before a question, and for the usage page.
    ///
    /// <para>
    /// The counts live in the database, in <see cref="QuinnUsage"/>: one row per user, model and day.  A
    /// database rather than memory, because the counts must survive a restart and be shared by every
    /// instance of the server; the same database as the business data rather than another store
    /// (Redis, say), because the volume is small - one row per user per day, one update per question -
    /// and nothing new has to be run.
    /// </para>
    /// <para>
    /// Every method is given the tenant and user from the caller's token.  The usage table has no
    /// query filter, and this class is the only code that reads or writes it.
    /// </para>
    /// </summary>
    public class QuinnUsageStore
    {
        private readonly QuinnUsageDbContext _usage;
        private readonly TempHireDbContext _business;
        private readonly IOptionsMonitor<AgentOptions> _options;

        public QuinnUsageStore(QuinnUsageDbContext usage, TempHireDbContext business, IOptionsMonitor<AgentOptions> options)
        {
            _usage = usage;
            _business = business;
            _options = options;
        }

        /// <summary>
        /// Where the user and their company stand, model by model, in the day, week and month that
        /// <paramref name="now"/> falls in: the model Quinn answers with now (<paramref name="provider"/>,
        /// <paramref name="model"/>), first, and any other model the company used in those periods.
        /// The quotas are read from the configuration on every call, so a change to appsettings.json
        /// applies without a restart.
        /// </summary>
        public async Task<UsageDto> ReportAsync(
            Guid tenantId, Guid userId, string provider, string model, DateTimeOffset now, CancellationToken cancellationToken)
        {
            var periods = UsagePeriods.At(now);
            var from = periods.Min(p => p.Start);

            // Every row of the company since the earliest period start: a few dozen per user and model.
            var rows = await _usage.QuinnUsage.AsNoTracking()
                .Where(u => u.TenantId == tenantId && u.Day >= from)
                .Select(u => new UsageRow(
                    u.UserId, u.Provider, u.Model, u.Day, u.InputTokens + u.OutputTokens, u.CostMicrodollars))
                .ToListAsync(cancellationToken);

            // Tenants is not tenant-filtered; the id comes from the user's token.
            var tenant = await _business.Tenants.AsNoTracking()
                .Where(t => t.Id == tenantId)
                .Select(t => new { t.Name, t.EmailDomain })
                .SingleOrDefaultAsync(cancellationToken);

            var models = new[] { (Provider: provider, Model: model) }
                .Concat(rows.Select(r => (r.Provider, r.Model)))
                .Distinct()
                .ToList();

            var rules = _options.CurrentValue.Quotas;

            return new UsageDto
            {
                CompanyName = tenant?.Name,
                Models =
                [
                    .. models.Select(m =>
                    {
                        var rule = QuinnQuotaRule.For(rules, m.Provider, m.Model);
                        var companyLimits = rule.ForTenant(tenant?.EmailDomain);
                        var modelRows = rows.Where(r => r.Provider == m.Provider && r.Model == m.Model).ToList();

                        static UsageMeterDto Meter(IEnumerable<UsageRow> meterRows, decimal? limit)
                        {
                            var list = meterRows.ToList();
                            return new UsageMeterDto(
                                Microdollars.ToDollars(list.Sum(r => r.CostMicrodollars)), limit, list.Sum(r => r.Tokens));
                        }

                        var modelPeriods = periods.Select(period => new UsagePeriodDto
                        {
                            Period = period.Kind.ToString().ToLowerInvariant(),
                            Start = period.Start.ToString("yyyy-MM-dd"),
                            End = period.End.ToString("yyyy-MM-dd"),
                            ResetsAt = period.ResetsAt,
                            User = Meter(
                                modelRows.Where(r => r.UserId == userId && period.Contains(r.Day)),
                                rule.User.For(period.Kind)),
                            Company = Meter(
                                modelRows.Where(r => period.Contains(r.Day)),
                                companyLimits.For(period.Kind))
                        }).ToList();

                        return new UsageModelDto
                        {
                            Provider = m.Provider,
                            Model = m.Model,
                            IsCurrent = m == (provider, model),
                            IsUnlimited = modelPeriods.All(p => p.User.Limit == null && p.Company.Limit == null),
                            Periods = modelPeriods
                        };
                    })
                ]
            };
        }

        /// <summary>
        /// Adds one question's tokens, and what they cost at the model's prices now, to the user's row
        /// for the model and <paramref name="day"/>; returns the cost in micro-dollars.  Safe when two of
        /// the user's questions finish at once: the update is a single <c>UPDATE ... SET x = x + n</c>
        /// in the database, and when there is no row yet, an insert that loses a race is retried as an
        /// update.
        /// </summary>
        public async Task<long> RecordAsync(
            Guid tenantId, Guid userId, string provider, string model, DateOnly day, TokenTally tally, CancellationToken cancellationToken)
        {
            var cost = tally.CostAt(QuinnQuotaRule.For(_options.CurrentValue.Quotas, provider, model).Prices);

            for (var attempt = 0; ; attempt++)
            {
                var updated = await _usage.QuinnUsage
                    .Where(u => u.TenantId == tenantId && u.UserId == userId
                                && u.Provider == provider && u.Model == model && u.Day == day)
                    .ExecuteUpdateAsync(set => set
                        .SetProperty(u => u.InputTokens, u => u.InputTokens + tally.InputTokens)
                        .SetProperty(u => u.CacheReadTokens, u => u.CacheReadTokens + tally.CacheReadTokens)
                        .SetProperty(u => u.CacheWriteTokens, u => u.CacheWriteTokens + tally.CacheWriteTokens)
                        .SetProperty(u => u.OutputTokens, u => u.OutputTokens + tally.OutputTokens)
                        .SetProperty(u => u.CostMicrodollars, u => u.CostMicrodollars + cost)
                        .SetProperty(u => u.Questions, u => u.Questions + 1),
                        cancellationToken);
                if (updated > 0)
                {
                    return cost;
                }

                var row = _usage.QuinnUsage.Add(new QuinnUsage
                {
                    TenantId = tenantId,
                    UserId = userId,
                    Provider = provider,
                    Model = model,
                    Day = day,
                    InputTokens = tally.InputTokens,
                    CacheReadTokens = tally.CacheReadTokens,
                    CacheWriteTokens = tally.CacheWriteTokens,
                    OutputTokens = tally.OutputTokens,
                    CostMicrodollars = cost,
                    Questions = 1
                });
                try
                {
                    await _usage.SaveChangesAsync(cancellationToken);
                    return cost;
                }
                catch (DbUpdateException) when (attempt < 2)
                {
                    // Another question of the same user inserted the day's row first: update it instead.
                }
                finally
                {
                    row.State = EntityState.Detached;
                }
            }
        }
    }

    /// <summary>One usage row, as the report needs it.</summary>
    internal record UsageRow(Guid UserId, string Provider, string Model, DateOnly Day, long Tokens, long CostMicrodollars);

    /// <summary>
    /// The tokens of one question, added up from the usage the model reports.  One question can mean
    /// several model calls - Quinn calls tools, and the model is called again with their results -
    /// and each reports its own usage.
    ///
    /// <para>
    /// With Claude's prompt caching, part of the input is read from the cache and part written to it,
    /// each at its own price (see <see cref="ModelPrices"/>).  The Anthropic client reports all of
    /// the input as <see cref="UsageDetails.InputTokenCount"/>, the part read from the cache as
    /// <see cref="UsageDetails.CachedInputTokenCount"/>, and the part written to it in
    /// <see cref="UsageDetails.AdditionalCounts"/> under <see cref="CacheWriteCount"/>.
    /// </para>
    /// </summary>
    public class TokenTally
    {
        /// <summary>The key the Anthropic client reports cache writes under.</summary>
        public const string CacheWriteCount = nameof(Anthropic.Models.Messages.Usage.CacheCreationInputTokens);

        /// <summary>All input tokens, however they were billed.</summary>
        public long InputTokens { get; private set; }

        /// <summary>Input tokens read from the prompt cache: part of <see cref="InputTokens"/>.</summary>
        public long CacheReadTokens { get; private set; }

        /// <summary>Input tokens written to the prompt cache: part of <see cref="InputTokens"/>.</summary>
        public long CacheWriteTokens { get; private set; }

        public long OutputTokens { get; private set; }

        /// <summary>How many usage reports were added: with a streamed answer, one per model call.</summary>
        public int Reports { get; private set; }

        public long TotalTokens => InputTokens + OutputTokens;

        public void Add(UsageDetails usage)
        {
            InputTokens += usage.InputTokenCount ?? 0;
            CacheReadTokens += usage.CachedInputTokenCount ?? 0;
            CacheWriteTokens += usage.AdditionalCounts?.TryGetValue(CacheWriteCount, out var written) == true ? written : 0;
            OutputTokens += usage.OutputTokenCount ?? 0;
            Reports++;
        }

        /// <summary>What these tokens cost at <paramref name="prices"/>, in micro-dollars.</summary>
        public long CostAt(ModelPrices prices) =>
            prices.CostOf(InputTokens, OutputTokens, CacheReadTokens, CacheWriteTokens);
    }
}
