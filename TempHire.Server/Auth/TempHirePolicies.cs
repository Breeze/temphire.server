using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using TempHire.Model.Identity;

namespace TempHire.Server.Auth
{
    /// <summary>
    /// Who may call what: the server's authorization policies, the one place that says which role
    /// may do what.
    ///
    /// <list type="table">
    ///   <listheader><term>Policy</term><description>Who</description></listheader>
    ///   <item><term>(default) signed in</term><description>Any signed-in user: the Breeze metadata and
    ///   lookups, the validation metadata, <c>api/account/me</c>, Quinn's chat and the MCP endpoint.
    ///   </description></item>
    ///   <item><term><see cref="ReadStaffingData"/></term><description>Editor or Reader: the company's
    ///   staffing resources and everything that belongs to them.</description></item>
    ///   <item><term><see cref="EditStaffingData"/></term><description>Editor: saving.</description></item>
    ///   <item><term><c>[AllowAnonymous]</c></term><description>Only what must work before sign-in:
    ///   the sign-in page, the OAuth endpoints, client registration and the client app's own files.
    ///   </description></item>
    /// </list>
    ///
    /// <para>
    /// <b>Deny by default.</b>  The fallback policy requires a signed-in user for any endpoint that
    /// says nothing about authorization, so an endpoint added without an <c>[Authorize]</c> is closed,
    /// not open.  Endpoints that must be reachable anonymously say so with <c>[AllowAnonymous]</c>.
    /// </para>
    /// <para>
    /// The policies are what the endpoints declare; the server also checks the same rules where the
    /// work happens, so a mistake in one place doesn't open the data: the save interceptor refuses a
    /// save by anyone but an editor, and the MCP server's call-tool filter refuses a tool call by a
    /// user with neither role.  Both use <see cref="CanEdit"/> and <see cref="CanRead"/>.
    /// </para>
    /// </summary>
    public static class TempHirePolicies
    {
        /// <summary>See the company's staffing data: Editor or Reader.</summary>
        public const string ReadStaffingData = nameof(ReadStaffingData);

        /// <summary>Change the company's staffing data: Editor.</summary>
        public const string EditStaffingData = nameof(EditStaffingData);

        /// <summary>The roles that may read the staffing data.</summary>
        public static readonly string[] ReadRoles = [TempHireRoles.Editor, TempHireRoles.Reader];

        /// <summary>The roles that may change it.</summary>
        public static readonly string[] EditRoles = [TempHireRoles.Editor];

        /// <summary>Whether <paramref name="user"/> may read the staffing data.</summary>
        public static bool CanRead(ClaimsPrincipal user) => HasAnyRole(user, ReadRoles);

        /// <summary>Whether <paramref name="user"/> may change it.</summary>
        public static bool CanEdit(ClaimsPrincipal user) => HasAnyRole(user, EditRoles);

        /// <summary>Registers the policies, and makes "signed in" the default and the fallback.</summary>
        public static AuthorizationOptions AddTempHirePolicies(this AuthorizationOptions options)
        {
            var signedIn = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
            options.DefaultPolicy = signedIn;
            options.FallbackPolicy = signedIn;

            options.AddPolicy(ReadStaffingData, policy => policy.RequireAuthenticatedUser().RequireRole(ReadRoles));
            options.AddPolicy(EditStaffingData, policy => policy.RequireAuthenticatedUser().RequireRole(EditRoles));
            return options;
        }

        private static bool HasAnyRole(ClaimsPrincipal user, string[] roles) =>
            user?.Identity?.IsAuthenticated == true && roles.Any(user.IsInRole);
    }
}
