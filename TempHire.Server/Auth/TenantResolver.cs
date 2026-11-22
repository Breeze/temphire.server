using Microsoft.EntityFrameworkCore;
using TempHire.Model;

namespace TempHire.Server.Auth;

/// <summary>
/// Decides which company a signed-in user belongs to: the tenant whose email domain matches the
/// user's email address.
///
/// <para>
/// Nothing on the user record names the tenant; this rule is applied at every sign-in.  It holds
/// for local accounts and for users of an external identity provider alike: with Microsoft Entra
/// ID, for example, the email domain is one the company has verified.  <see cref="TokenClaims"/>
/// calls this whenever a token is issued or refreshed.
/// </para>
/// </summary>
public class TenantResolver(TempHireDbContext context)
{
    private readonly TempHireDbContext _context = context;

    /// <summary>The tenant for <paramref name="email"/>, or null if no company has its domain.</summary>
    public async Task<Tenant> ForEmailAsync(string email, CancellationToken cancellationToken = default)
    {
        var domain = Tenant.DomainOf(email);
        return domain is null
            ? null
            : await _context.Tenants.SingleOrDefaultAsync(t => t.EmailDomain == domain, cancellationToken);
    }
}
