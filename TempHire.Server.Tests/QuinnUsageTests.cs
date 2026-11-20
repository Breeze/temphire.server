using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using TempHire.Server.Agent;
using TempHire.Server.Agent.Usage;
using Xunit;

namespace TempHire.Server.Tests
{
    /// <summary>The quota periods: days, ISO weeks and calendar months, in UTC.</summary>
    public class UsagePeriodsTests
    {
        private static (DateOnly Start, DateOnly End)[] At(string utc) =>
            [.. UsagePeriods.At(DateTimeOffset.Parse(utc)).Select(p => (p.Start, p.End))];

        [Fact]
        public void A_wednesday_is_in_the_week_from_monday()
        {
            var periods = At("2026-11-18T15:30:00Z");

            Assert.Equal((new DateOnly(2026, 11, 18), new DateOnly(2026, 11, 19)), periods[0]);
            Assert.Equal((new DateOnly(2026, 11, 16), new DateOnly(2026, 11, 23)), periods[1]);
            Assert.Equal((new DateOnly(2026, 11, 1), new DateOnly(2026, 12, 1)), periods[2]);
        }

        [Fact]
        public void A_sunday_ends_the_week_and_a_monday_starts_one()
        {
            Assert.Equal(new DateOnly(2026, 11, 16), At("2026-11-22T23:59:59Z")[1].Start);
            Assert.Equal(new DateOnly(2026, 11, 23), At("2026-11-23T00:00:00Z")[1].Start);
        }

        [Fact]
        public void The_day_is_the_utc_day()
        {
            // 8 pm in Seattle on the 18th is already the 19th in UTC.
            Assert.Equal(new DateOnly(2026, 11, 19), At("2026-11-18T20:00:00-08:00")[0].Start);
        }

        [Fact]
        public void The_last_day_of_the_year_ends_its_month_and_week_in_the_next_year()
        {
            var periods = At("2026-12-31T12:00:00Z");

            Assert.Equal((new DateOnly(2026, 12, 1), new DateOnly(2027, 1, 1)), periods[2]);
            Assert.Equal(new DateOnly(2027, 1, 4), periods[1].End);
        }
    }

    /// <summary>Which quota rule applies to a model, what its tokens cost, and what unlimited means.</summary>
    public class QuinnQuotaRuleTests
    {
        private static readonly List<QuinnQuotaRule> Rules =
        [
            new()
            {
                Provider = "Claude", Model = "claude-opus-5-5",
                Prices = new ModelPrices { InputPerMillion = 4m, OutputPerMillion = 20m },
                User = new CostLimits { Daily = 0.10m }
            },
            new()
            {
                Provider = "Claude",
                Prices = new ModelPrices { InputPerMillion = 2m, OutputPerMillion = 10m },
                User = new CostLimits { Daily = 0.50m, Monthly = 3m },
                Tenant = new CostLimits { Daily = 1.50m, Weekly = 4m, Monthly = 10m },
                Tenants = new(StringComparer.OrdinalIgnoreCase) { ["summit.test"] = new CostLimits { Monthly = 25m } }
            },
            new() { Provider = "Ollama" }
        ];

        [Fact]
        public void A_rule_for_one_model_goes_before_its_providers_rule()
        {
            Assert.Equal(0.10m, QuinnQuotaRule.For(Rules, "Claude", "claude-opus-5-5").User.Daily);
            Assert.Equal(0.50m, QuinnQuotaRule.For(Rules, "claude", "claude-sonnet-5-5").User.Daily);
        }

        [Fact]
        public void A_rule_without_limits_and_a_model_without_a_rule_are_unlimited_and_free()
        {
            var ollama = QuinnQuotaRule.For(Rules, "Ollama", "qwen3:4b-instruct");
            Assert.Null(ollama.User.Daily);
            Assert.Null(ollama.Tenant.Monthly);
            Assert.Equal(0, ollama.Prices.CostOf(1_000_000, 1_000_000));

            Assert.Same(QuinnQuotaRule.Unlimited, QuinnQuotaRule.For(Rules, "Other", "some-model"));
            Assert.Same(QuinnQuotaRule.Unlimited, QuinnQuotaRule.For(null, "Claude", "claude-sonnet-5-5"));
        }

        [Fact]
        public void A_company_exception_replaces_only_the_limits_it_gives()
        {
            var limits = QuinnQuotaRule.For(Rules, "Claude", "claude-sonnet-5-5").ForTenant("summit.test");

            Assert.Equal(1.50m, limits.Daily);
            Assert.Equal(4m, limits.Weekly);
            Assert.Equal(25m, limits.Monthly);
        }

        [Fact]
        public void Tokens_cost_what_the_price_list_says_in_micro_dollars_rounded_up()
        {
            var sonnet = QuinnQuotaRule.For(Rules, "Claude", "claude-sonnet-5-5").Prices;

            // The question measured in the app: 5,183 in at $2/M and 239 out at $10/M.
            Assert.Equal(12_756, sonnet.CostOf(5183, 239));
            Assert.Equal(0.012756m, Microdollars.ToDollars(sonnet.CostOf(5183, 239)));

            // Output costs five times as much: the same tokens, a different bill.
            Assert.Equal(2_000_000, sonnet.CostOf(1_000_000, 0));
            Assert.Equal(10_000_000, sonnet.CostOf(0, 1_000_000));

            // A fraction of a micro-dollar is rounded up, never down.
            Assert.Equal(1, new ModelPrices { InputPerMillion = 0.10m }.CostOf(1, 0));
        }
    }

    /// <summary>
    /// Recording usage and reporting it against the dollar quotas, on a real (SQLite) database: the
    /// counters are added to in SQL, per user, model and day.
    /// </summary>
    public sealed class QuinnUsageStoreTests : IDisposable
    {
        private const string Claude = "Claude";
        private const string Sonnet = "claude-sonnet-5-5";
        private const string Ollama = "Ollama";
        private const string Qwen = "qwen3:4b-instruct";

        private static readonly Guid Anna = Guid.NewGuid();
        private static readonly Guid Ben = Guid.NewGuid();
        private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-18T15:00:00Z");
        private static readonly DateOnly Today = new(2026, 11, 18);

        // Sonnet 5.5's prices: an input token is 2 micro-dollars, an output token 10.
        private readonly AgentOptions _options = new()
        {
            Quotas =
            [
                new()
                {
                    Provider = Claude,
                    Prices = new ModelPrices { InputPerMillion = 2m, OutputPerMillion = 10m },
                    User = new CostLimits { Daily = 0.010m, Weekly = 0.030m, Monthly = 0.100m },
                    Tenant = new CostLimits { Daily = 0.015m }
                },
                new() { Provider = Ollama }
            ]
        };

        private readonly SaveFixture _business = new();
        private readonly SqliteConnection _connection = new("DataSource=:memory:");

        public QuinnUsageStoreTests()
        {
            _connection.Open();
            using var usage = NewUsageContext();
            usage.Database.EnsureCreated();
        }

        public void Dispose()
        {
            _connection.Dispose();
            _business.Dispose();
        }

        [Fact]
        public async Task Questions_on_the_same_day_add_up_in_one_row_with_their_cost()
        {
            Assert.Equal(1100, await RecordAsync(Anna, Claude, Sonnet, Today, input: 300, output: 50));
            Assert.Equal(650, await RecordAsync(Anna, Claude, Sonnet, Today, input: 200, output: 25));

            using var usage = NewUsageContext();
            var row = await usage.QuinnUsage.SingleAsync();
            Assert.Equal(500, row.InputTokens);
            Assert.Equal(75, row.OutputTokens);
            Assert.Equal(1750, row.CostMicrodollars);
            Assert.Equal(2, row.Questions);
        }

        [Fact]
        public async Task Each_model_is_counted_on_its_own_and_a_free_model_costs_nothing()
        {
            await RecordAsync(Anna, Claude, Sonnet, Today, input: 400, output: 100);
            await RecordAsync(Anna, Ollama, Qwen, Today, input: 7000, output: 1000);

            var report = await ReportAsync(Anna, Claude, Sonnet);

            var claude = report.Models[0];
            Assert.True(claude.IsCurrent);
            Assert.False(claude.IsUnlimited);
            Assert.Equal(0.0018m, claude.Periods[0].User.Used);
            Assert.Equal(0.010m, claude.Periods[0].User.Limit);
            Assert.Equal(500, claude.Periods[0].User.Tokens);

            var ollama = Assert.Single(report.Models, m => m.Provider == Ollama);
            Assert.False(ollama.IsCurrent);
            Assert.True(ollama.IsUnlimited);
            Assert.Equal(0m, ollama.Periods[0].User.Used);
            Assert.Equal(8000, ollama.Periods[0].User.Tokens);
            Assert.Null(ollama.Periods[0].User.Limit);
        }

        [Fact]
        public async Task The_current_model_is_reported_even_before_it_is_used()
        {
            var report = await ReportAsync(Anna, Claude, Sonnet);

            var model = Assert.Single(report.Models);
            Assert.Equal((Claude, Sonnet), (model.Provider, model.Model));
            Assert.Equal("Harbor Staffing", report.CompanyName);
            Assert.Equal(["day", "week", "month"], model.Periods.Select(p => p.Period));
            Assert.Equal(0m, model.Periods[0].User.Used);
        }

        [Fact]
        public async Task The_company_counts_every_user_and_the_week_and_month_count_earlier_days()
        {
            await RecordAsync(Anna, Claude, Sonnet, Today, input: 300, output: 0);                        // $0.0006
            await RecordAsync(Ben, Claude, Sonnet, Today, input: 200, output: 0);                         // $0.0004
            await RecordAsync(Anna, Claude, Sonnet, new DateOnly(2026, 11, 16), input: 100, output: 0);   // Monday
            await RecordAsync(Anna, Claude, Sonnet, new DateOnly(2026, 11, 2), input: 50, output: 0);     // last week

            var periods = (await ReportAsync(Anna, Claude, Sonnet)).Models[0].Periods;

            Assert.Equal((0.0006m, 0.0010m), (periods[0].User.Used, periods[0].Company.Used));
            Assert.Equal((0.0008m, 0.0012m), (periods[1].User.Used, periods[1].Company.Used));
            Assert.Equal((0.0009m, 0.0013m), (periods[2].User.Used, periods[2].Company.Used));
        }

        [Fact]
        public async Task Output_tokens_use_a_quota_up_five_times_as_fast()
        {
            // 1,000 output tokens are $0.01: the user's daily $0.010.  As input they'd be $0.002.
            await RecordAsync(Anna, Claude, Sonnet, Today, input: 0, output: 1000);

            var exceeded = (await ReportAsync(Anna, Claude, Sonnet)).Current.Exceeded();

            Assert.NotNull(exceeded);
            Assert.Equal("day", exceeded.Value.Period.Period);
            Assert.False(exceeded.Value.IsCompany);
            Assert.Equal(new DateTimeOffset(2026, 11, 19, 0, 0, 0, TimeSpan.Zero), exceeded.Value.Period.ResetsAt);
        }

        [Fact]
        public async Task The_company_quota_stops_a_user_who_has_quota_left()
        {
            await RecordAsync(Anna, Claude, Sonnet, Today, input: 4500, output: 0);   // $0.009
            await RecordAsync(Ben, Claude, Sonnet, Today, input: 3000, output: 0);    // $0.006: company $0.015

            var exceeded = (await ReportAsync(Ben, Claude, Sonnet)).Current.Exceeded();

            Assert.NotNull(exceeded);
            Assert.True(exceeded.Value.IsCompany);
        }

        [Fact]
        public async Task An_unlimited_model_is_never_used_up()
        {
            await RecordAsync(Anna, Ollama, Qwen, Today, input: 5_000_000, output: 0);

            Assert.Null((await ReportAsync(Anna, Ollama, Qwen)).Current.Exceeded());
        }

        [Fact]
        public async Task A_question_keeps_the_cost_at_the_prices_it_was_asked_at()
        {
            await RecordAsync(Anna, Claude, Sonnet, Today, input: 1000, output: 0);   // $0.002
            _options.Quotas[0].Prices.InputPerMillion = 4m;                            // the price doubles

            var used = (await ReportAsync(Anna, Claude, Sonnet)).Models[0].Periods[0].User.Used;

            Assert.Equal(0.002m, used);
        }

        private QuinnUsageDbContext NewUsageContext() =>
            new(new DbContextOptionsBuilder<QuinnUsageDbContext>().UseSqlite(_connection).Options);

        private QuinnUsageStore NewStore(QuinnUsageDbContext usage, Model.TempHireDbContext business) =>
            new(usage, business, new FixedOptions(_options));

        private async Task<long> RecordAsync(Guid user, string provider, string model, DateOnly day, long input, long output)
        {
            var tally = new TokenTally();
            tally.Add(new UsageDetails { InputTokenCount = input, OutputTokenCount = output });

            using var usage = NewUsageContext();
            using var business = _business.NewContext();
            return await NewStore(usage, business).RecordAsync(SaveFixture.Tenant, user, provider, model, day, tally, CancellationToken.None);
        }

        private async Task<Controllers.UsageDto> ReportAsync(Guid user, string provider, string model)
        {
            using var usage = NewUsageContext();
            using var business = _business.NewContext();
            return await NewStore(usage, business).ReportAsync(SaveFixture.Tenant, user, provider, model, Now, CancellationToken.None);
        }

        private sealed class FixedOptions(AgentOptions value) : IOptionsMonitor<AgentOptions>
        {
            public AgentOptions CurrentValue => value;
            public AgentOptions Get(string name) => value;
            public IDisposable OnChange(Action<AgentOptions, string> listener) => null;
        }
    }

    /// <summary>What a user is told when a quota stops their question.</summary>
    public class QuotaMessagesTests
    {
        [Theory]
        [InlineData(0.5, "1 minute")]
        [InlineData(12, "12 minutes")]
        [InlineData(60, "1 hour")]
        [InlineData(401, "6 hours and 41 minutes")]
        [InlineData(1440, "1 day")]
        [InlineData(4560, "3 days and 4 hours")]
        public void The_wait_is_in_words_and_rounded_up(double minutes, string expected)
        {
            Assert.Equal(expected, QuotaMessages.Wait(TimeSpan.FromMinutes(minutes)));
        }

        [Fact]
        public void A_users_daily_quota_says_how_long_until_midnight_utc()
        {
            var period = Period("day", "2026-11-19T00:00:00Z", userLimit: 0.50m);

            var message = QuotaMessages.Refusal(period, isCompany: false, "Harbor Staffing", DateTimeOffset.Parse("2026-11-18T17:19:00Z"));

            Assert.Equal(
                "You've used up your daily Quinn allowance ($0.50). " +
                "You can ask again in 6 hours and 41 minutes, when it starts over at midnight UTC.",
                message);
        }

        [Fact]
        public void A_companys_monthly_quota_names_the_company_and_the_day_it_starts_over()
        {
            var period = Period("month", "2026-12-01T00:00:00Z", companyLimit: 10m);

            var message = QuotaMessages.Refusal(period, isCompany: true, "Harbor Staffing", DateTimeOffset.Parse("2026-11-18T12:00:00Z"));

            Assert.Equal(
                "Harbor Staffing has used up its monthly Quinn allowance ($10.00). " +
                "You can ask again in 12 days and 12 hours, when it starts over at midnight UTC on Tuesday, December 1.",
                message);
        }

        private static Controllers.UsagePeriodDto Period(string name, string resetsAt, decimal? userLimit = null, decimal? companyLimit = null) =>
            new()
            {
                Period = name,
                ResetsAt = DateTimeOffset.Parse(resetsAt),
                User = new Controllers.UsageMeterDto(0, userLimit, 0),
                Company = new Controllers.UsageMeterDto(0, companyLimit, 0)
            };
    }
}
