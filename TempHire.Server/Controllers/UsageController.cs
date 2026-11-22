using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using TempHire.Server.Agent;
using TempHire.Server.Agent.Usage;
using TempHire.Server.Auth;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace TempHire.Server.Controllers;

/// <summary>
/// The current user's Quinn usage: per model, what they and their company have spent today,
/// this week and this month, against the quotas (<c>Agent:Quotas</c>).  For the client's usage
/// page.
///
/// <para>
/// Every signed-in user may see their own usage and their company's total; nobody sees another
/// user's.  The user and company come from the access token, never from the request.
/// </para>
/// </summary>
[ApiController]
[Authorize]
[Route("api/usage")]
public class UsageController(
    QuinnUsageStore usage,
    ChatClientFactory chatClients)
    : ControllerBase
{
    private readonly QuinnUsageStore _usage = usage;
    private readonly ChatClientFactory _chatClients = chatClients;

    [HttpGet]
    public async Task<ActionResult<UsageDto>> Get(CancellationToken cancellationToken)
    {
        if (UsageCaller.From(User) is not { } caller)
        {
            return Forbid();
        }

        var options = _chatClients.Options;
        return await _usage.ReportAsync(
            caller.TenantId, caller.UserId, options.CurrentProvider, options.CurrentModel, DateTimeOffset.UtcNow, cancellationToken);
    }
}

/// <summary>Whose usage a request counts as: the tenant and user of its access token.</summary>
public record UsageCaller(Guid TenantId, Guid UserId)
{
    /// <summary>The caller of a request, or null if the token names no tenant or user.</summary>
    public static UsageCaller From(ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirst(HttpCurrentTenant.TenantIdClaim)?.Value, out var tenantId)
        && Guid.TryParse(user.FindFirst(Claims.Subject)?.Value, out var userId)
            ? new UsageCaller(tenantId, userId)
            : null;
}

// ----------------------------------------------------------------------------------------------
// DTOs, camelCase for the same reason as the account DTOs: see the comment in AccountController.cs.
// ----------------------------------------------------------------------------------------------

/// <summary>The answer of <c>GET api/usage</c>.</summary>
[JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
public class UsageDto
{
    public string CompanyName { get; set; }

    /// <summary>
    /// One entry per model: the model Quinn answers with now, first, then any other model the
    /// company used in these periods.
    /// </summary>
    public List<UsageModelDto> Models { get; set; } = [];

    /// <summary>The model Quinn answers with now.</summary>
    [JsonIgnore]
    public UsageModelDto Current => Models.FirstOrDefault(m => m.IsCurrent);
}

/// <summary>One model's usage and quotas.</summary>
[JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
public class UsageModelDto
{
    /// <summary><c>Claude</c> or <c>Ollama</c>.</summary>
    public string Provider { get; set; }

    /// <summary>The model, as the provider names it.</summary>
    public string Model { get; set; }

    /// <summary>Whether Quinn answers with this model now.</summary>
    public bool IsCurrent { get; set; }

    /// <summary>No quota at all, for the user or the company, in any period: a local model, say.</summary>
    public bool IsUnlimited { get; set; }

    /// <summary>Today, this week and this month, in that order.</summary>
    public List<UsagePeriodDto> Periods { get; set; } = [];

    /// <summary>
    /// The first quota used up - the user's before the company's, the shortest period first - or
    /// null if there is none.  Quinn refuses new questions with this model while there is one.
    /// </summary>
    public (UsagePeriodDto Period, bool IsCompany)? Exceeded()
    {
        if (Periods.FirstOrDefault(p => p.User.IsUsedUp) is { } user)
        {
            return (user, false);
        }

        return Periods.FirstOrDefault(p => p.Company.IsUsedUp) is { } company ? (company, true) : null;
    }
}

/// <summary>One quota period, and where the user and the company stand in it.</summary>
[JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
public class UsagePeriodDto
{
    /// <summary><c>day</c>, <c>week</c> or <c>month</c>.</summary>
    public string Period { get; set; }

    /// <summary>The first day of the period (UTC), <c>yyyy-MM-dd</c>.</summary>
    public string Start { get; set; }

    /// <summary>The day after the period's last day, <c>yyyy-MM-dd</c>.</summary>
    public string End { get; set; }

    /// <summary>When the period's quota starts over: midnight UTC at its end.</summary>
    public DateTimeOffset ResetsAt { get; set; }

    public UsageMeterDto User { get; set; }
    public UsageMeterDto Company { get; set; }
}

/// <summary>
/// What was spent, in US dollars, against the limit (no limit when <see cref="Limit"/> is null),
/// and the tokens it bought.
/// </summary>
[JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
public class UsageMeterDto(decimal used, decimal? limit, long tokens)
{

    /// <summary>Dollars spent, to the micro-dollar.</summary>
    public decimal Used { get; } = used;

    /// <summary>The dollar limit; null for none.</summary>
    public decimal? Limit { get; } = limit;

    /// <summary>Tokens, input and output, for information: the limit is on <see cref="Used"/>.</summary>
    public long Tokens { get; } = tokens;

    [JsonIgnore]
    public bool IsUsedUp => Limit is { } limit && Used >= limit;
}
