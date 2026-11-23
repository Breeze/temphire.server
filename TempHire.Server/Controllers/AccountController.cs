using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using TempHire.Server.Auth;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace TempHire.Server.Controllers;

/// <summary>
/// Who is signed in.
/// </summary>
/// <remarks>
/// What is left of the account API: who the token belongs to.
///
/// <para>
/// This controller used to replace DevForce's <c>IEntityLoginManager</c> by taking the user's
/// password and returning a token.  With OAuth the password goes to the authorization server's
/// sign-in page instead (<see cref="SignInController"/>), and the client gets its tokens from
/// <c>connect/token</c> (<see cref="AuthorizationController"/>); signing out is
/// <c>connect/logout</c>.  The tenant is still decided from the email domain and signed into the
/// token, now by <see cref="TokenClaims"/>.
/// </para>
/// </remarks>
[ApiController]
[Route("api/account/[action]")]
public class AccountController : ControllerBase
{
    /// <summary>Who the access token belongs to: the user's name, company and roles.</summary>
    /// <remarks>
    /// The client shows the name, company and roles, and checks after signing in or reloading that
    /// its token is good.  camelCase names, unlike the Breeze endpoints.
    /// </remarks>
    /// <response code="200">The signed-in user.</response>
    [HttpGet]
    [Authorize]
    public ActionResult<UserInfo> Me() => new UserInfo
    {
        Username = User.Identity?.Name,
        TenantName = User.FindFirst(HttpCurrentTenant.TenantNameClaim)?.Value,
        Roles = [.. User.FindAll(Claims.Role).Select(c => c.Value)]
    };
}

// ----------------------------------------------------------------------------------------------
// DTOs
//
// Every one of these carries [JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))],
// and that is load-bearing rather than cosmetic.
//
// Program.cs hands the whole MVC pipeline to Json.NET via Breeze's
// JsonSerializationFns.UpdateWithDefaults, which sets ContractResolver.NamingStrategy = null - so
// by default property names go out verbatim, in PascalCase.  Breeze *needs* that: entity property
// names on the wire have to match the CLR names in the metadata, and the client app converts
// them itself with NamingConvention.camelCase.
//
// The account endpoints are not Breeze endpoints.  The client reads them with a plain HttpClient
// and nothing converts the names, so a PascalCase "TenantName" would arrive as
// response.TenantName while auth.service.ts reads response.tenantName - silently undefined.
//
// A per-type naming strategy overrides the resolver's and applies to properties added later, so
// extending one of these DTOs cannot quietly reintroduce the bug.
// ----------------------------------------------------------------------------------------------

[JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
public class UserInfo
{
    /// <summary>The user's name: their email address.</summary>
    public string Username { get; set; }

    /// <summary>The user's company.</summary>
    public string TenantName { get; set; }

    /// <summary>
    /// The user's roles, so the client can show what the user may do.  The server decides from
    /// the roles signed into the token, never from what the client says.
    /// </summary>
    public List<string> Roles { get; set; } = [];
}

/// <summary>Serialises as <c>{ "message": "..." }</c>, which is what the client's
/// <c>AppErrorHandler.describe</c> looks for.</summary>
[JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
public class ErrorResult
{
    public string Message { get; set; }
}
