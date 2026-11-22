namespace TempHire.Server.Agent.Usage;

/// <summary>
/// The tokens Quinn used for one user, with one model, on one day (UTC): one row per tenant,
/// user, model and day, which the server adds to after every question.  Per model, because each
/// model has quotas of its own (see <see cref="QuinnQuotaRule"/>).
///
/// <para>
/// Daily rows are enough for every quota: a day's usage is one row, a week's or a month's the
/// sum of at most 31, and a company's the sum over its users.  Unlike a row per question, the
/// table grows by at most one row per user per day.
/// </para>
/// <para>
/// Kept by <see cref="QuinnUsageDbContext"/>, apart from the business model: Breeze never sees
/// it, and only <see cref="QuinnUsageStore"/> reads or writes it, always naming the tenant itself.
/// </para>
/// </summary>
public class QuinnUsage
{
    /// <summary>The user's company: the <c>tenant_id</c> claim of their access token.</summary>
    public Guid TenantId { get; set; }

    /// <summary>The user's id: the <c>sub</c> claim of their access token.</summary>
    public Guid UserId { get; set; }

    /// <summary>The provider that answered: <c>Claude</c> or <c>Ollama</c>.</summary>
    public string Provider { get; set; }

    /// <summary>The model that answered, as the provider names it: <c>claude-sonnet-5-5</c>, <c>qwen3:4b-instruct</c>.</summary>
    public string Model { get; set; }

    /// <summary>The day, in UTC.</summary>
    public DateOnly Day { get; set; }

    /// <summary>
    /// Tokens sent to the model: system prompt, conversation, tool definitions and results.  All
    /// of them, including those read from or written to the prompt cache.
    /// </summary>
    public long InputTokens { get; set; }

    /// <summary>Input tokens read from Claude's prompt cache, at the cheaper cache read price.</summary>
    public long CacheReadTokens { get; set; }

    /// <summary>Input tokens written to Claude's prompt cache, at the dearer cache write price.</summary>
    public long CacheWriteTokens { get; set; }

    /// <summary>Tokens the model wrote: answers and tool calls.</summary>
    public long OutputTokens { get; set; }

    /// <summary>
    /// What the tokens cost, in micro-dollars (millionths of a dollar), at the model's prices when
    /// each question was asked (see <see cref="ModelPrices"/>), so a later price change doesn't
    /// rewrite the past.  Whole micro-dollars rather than a decimal, so the database can add to it
    /// exactly in one <c>UPDATE</c> on every provider, SQLite included.
    /// </summary>
    public long CostMicrodollars { get; set; }

    /// <summary>Questions answered.  One question can mean several model calls (the tool loop).</summary>
    public int Questions { get; set; }
}
