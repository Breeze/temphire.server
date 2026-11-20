namespace TempHire.Server.Agent.Usage
{
    /// <summary>
    /// What Quinn may spend with one model, or with every model of one provider: the model's prices,
    /// and dollar limits per user and per company (tenant), per day, week and month.  One entry of
    /// the <c>Agent:Quotas</c> list in appsettings.json.
    ///
    /// <para>
    /// <b>Dollars, not tokens.</b>  An output token costs several times what an input token does
    /// (Claude Sonnet 5.5: $10 against $2 per million), so a token limit can't promise a budget: the
    /// same number of tokens costs five times as much when it's all output.  Each question is
    /// recorded at its cost, from <see cref="Prices"/>, and the limits are in dollars, so a company
    /// limit of $10 a month is $10 a month whatever the mix.
    /// </para>
    /// <para>
    /// Usage is counted per model, so each model has quotas of its own: a company can have a budget
    /// for Claude and none for a local Ollama model, which costs nothing per token.
    /// </para>
    /// <para>
    /// <b>Unlimited:</b> a limit that is left out or <c>null</c> is no limit, and a rule without any
    /// limits makes the model unlimited.  A model that no rule matches is unlimited and free too - so
    /// give every paid model a rule with its prices.  A rule with limits but without prices counts
    /// every question as free, so its limits never stop anyone.  Usage is counted and shown whatever
    /// the limits.
    /// </para>
    /// <para>
    /// The rules are a list, not keyed by model name, because Ollama's model names contain colons
    /// (<c>qwen3:4b-instruct</c>), and a colon separates sections in .NET configuration.
    /// </para>
    /// <example>
    /// <code>
    /// "Quotas": [
    ///   {
    ///     "Provider": "Claude", "Model": "claude-opus-5-5",
    ///     "Prices": { "InputPerMillion": 4.00, "OutputPerMillion": 20.00 },
    ///     "User": { "Daily": 0.50 }
    ///   },
    ///   {
    ///     "Provider": "Claude",
    ///     "Prices":  { "InputPerMillion": 2.00, "OutputPerMillion": 10.00 },
    ///     "User":    { "Daily": 0.50, "Weekly": 1.00, "Monthly": 2.50 },
    ///     "Tenant":  { "Daily": 1.50, "Weekly": 4.00, "Monthly": 10.00 },
    ///     "Tenants": { "summit.test": { "Monthly": 25.00 } }
    ///   },
    ///   { "Provider": "Ollama" }
    /// ]
    /// </code>
    /// The first rule that matches wins, so a rule for one model goes before its provider's rule -
    /// and its prices are that model's.
    /// </example>
    /// </summary>
    public class QuinnQuotaRule
    {
        /// <summary><c>Claude</c> or <c>Ollama</c>, as in <c>Agent:Provider</c>.</summary>
        public string Provider { get; set; }

        /// <summary>One model of the provider, such as <c>claude-sonnet-5-5</c>; null for all its models.</summary>
        public string Model { get; set; }

        /// <summary>What the matched models cost; none for a free model.</summary>
        public ModelPrices Prices { get; set; } = new();

        /// <summary>The limits for each user, in dollars.</summary>
        public CostLimits User { get; set; } = new();

        /// <summary>The limits for each company - all its users together - in dollars.</summary>
        public CostLimits Tenant { get; set; } = new();

        /// <summary>
        /// Per-company exceptions, by the company's email domain (<c>harbor.test</c>): a company on a
        /// bigger plan, say.  Only the limits given replace <see cref="Tenant"/>'s.
        /// </summary>
        public Dictionary<string, CostLimits> Tenants { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        public bool Matches(string provider, string model) =>
            string.Equals(Provider, provider, StringComparison.OrdinalIgnoreCase)
            && (string.IsNullOrEmpty(Model) || string.Equals(Model, model, StringComparison.OrdinalIgnoreCase));

        /// <summary>The company limits for the tenant with <paramref name="emailDomain"/>.</summary>
        public CostLimits ForTenant(string emailDomain) =>
            emailDomain != null && Tenants.TryGetValue(emailDomain, out var exception)
                ? new CostLimits
                {
                    Daily = exception.Daily ?? Tenant.Daily,
                    Weekly = exception.Weekly ?? Tenant.Weekly,
                    Monthly = exception.Monthly ?? Tenant.Monthly
                }
                : Tenant;

        /// <summary>The rule for a model: the first that matches, or <see cref="Unlimited"/>.</summary>
        public static QuinnQuotaRule For(IEnumerable<QuinnQuotaRule> rules, string provider, string model) =>
            rules?.FirstOrDefault(rule => rule.Matches(provider, model)) ?? Unlimited;

        /// <summary>No prices and no limits: for a model no rule matches.</summary>
        public static readonly QuinnQuotaRule Unlimited = new();
    }

    /// <summary>A model's prices, in US dollars per million tokens, as the provider lists them.</summary>
    public class ModelPrices
    {
        public decimal InputPerMillion { get; set; }
        public decimal OutputPerMillion { get; set; }

        /// <summary>
        /// What the tokens cost, in micro-dollars (millionths of a dollar), rounded up: at $1 per
        /// million tokens, one token is one micro-dollar.
        /// </summary>
        public long CostOf(long inputTokens, long outputTokens) =>
            (long)Math.Ceiling(inputTokens * InputPerMillion + outputTokens * OutputPerMillion);
    }

    /// <summary>Dollar limits per day, week and month; null for no limit.</summary>
    public class CostLimits
    {
        public decimal? Daily { get; set; }
        public decimal? Weekly { get; set; }
        public decimal? Monthly { get; set; }

        public decimal? For(UsagePeriodKind period) => period switch
        {
            UsagePeriodKind.Day => Daily,
            UsagePeriodKind.Week => Weekly,
            _ => Monthly
        };
    }

    /// <summary>Micro-dollars, as the usage table stores them, to and from dollars.</summary>
    public static class Microdollars
    {
        public const decimal PerDollar = 1_000_000m;

        public static decimal ToDollars(long microdollars) => microdollars / PerDollar;
    }
}
