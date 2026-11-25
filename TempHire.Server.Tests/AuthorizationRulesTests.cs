using System.Reflection;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.Extensions.DependencyInjection;
using TempHire.Model.Identity;
using TempHire.Server.Auth;
using TempHire.Server.Controllers;
using Xunit;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace TempHire.Server.Tests;

/// <summary>
/// Who may call what: the policies (<see cref="TempHirePolicies"/>) and the rule each controller
/// action declares.  The table in <see cref="EveryActionDeclaresTheExpectedRule"/> lists every
/// action, so a new endpoint fails the test until someone decides who may call it.
/// </summary>
public sealed class AuthorizationRulesTests
{
    private const string Anonymous = "anonymous";
    private const string SignedIn = "signed in";

    private static readonly ClaimsPrincipal NotSignedIn = new(new ClaimsIdentity());
    private static readonly ClaimsPrincipal NoRole = User();
    private static readonly ClaimsPrincipal Reader = User(TempHireRoles.Reader);
    private static readonly ClaimsPrincipal Editor = User(TempHireRoles.Editor);

    public static TheoryData<string, string, bool> PolicyCases => new()
    {
        // policy, user, allowed
        { SignedIn, nameof(NotSignedIn), false },
        { SignedIn, nameof(NoRole), true },
        { SignedIn, nameof(Reader), true },
        { SignedIn, nameof(Editor), true },
        { TempHirePolicies.ReadStaffingData, nameof(NotSignedIn), false },
        { TempHirePolicies.ReadStaffingData, nameof(NoRole), false },
        { TempHirePolicies.ReadStaffingData, nameof(Reader), true },
        { TempHirePolicies.ReadStaffingData, nameof(Editor), true },
        { TempHirePolicies.EditStaffingData, nameof(NotSignedIn), false },
        { TempHirePolicies.EditStaffingData, nameof(NoRole), false },
        { TempHirePolicies.EditStaffingData, nameof(Reader), false },
        { TempHirePolicies.EditStaffingData, nameof(Editor), true },
    };

    [Theory]
    [MemberData(nameof(PolicyCases))]
    public async Task EachPolicyAdmitsTheRightUsers(string policy, string user, bool allowed)
    {
        var services = new ServiceCollection()
            .AddLogging()
            .AddAuthorization(options => options.AddTempHirePolicies())
            .BuildServiceProvider();
        var authorization = services.GetRequiredService<IAuthorizationService>();
        var options = services.GetRequiredService<Microsoft.Extensions.Options.IOptions<AuthorizationOptions>>().Value;

        var principal = user switch
        {
            nameof(NotSignedIn) => NotSignedIn,
            nameof(NoRole) => NoRole,
            nameof(Reader) => Reader,
            _ => Editor
        };
        var result = policy == SignedIn
            ? await authorization.AuthorizeAsync(principal, null, options.DefaultPolicy)
            : await authorization.AuthorizeAsync(principal, policy);

        Assert.Equal(allowed, result.Succeeded);
    }

    [Fact]
    public void AnEndpointThatSaysNothingNeedsASignedInUser()
    {
        var options = new AuthorizationOptions().AddTempHirePolicies();

        Assert.NotNull(options.FallbackPolicy);
        Assert.Contains(options.FallbackPolicy.Requirements,
            r => r is Microsoft.AspNetCore.Authorization.Infrastructure.DenyAnonymousAuthorizationRequirement);
    }

    [Fact]
    public void TheSaveAndToolChecksAgreeWithThePolicies()
    {
        Assert.False(TempHirePolicies.CanEdit(Reader));
        Assert.True(TempHirePolicies.CanEdit(Editor));
        Assert.False(TempHirePolicies.CanRead(NoRole));
        Assert.True(TempHirePolicies.CanRead(Reader));
        Assert.False(TempHirePolicies.CanRead(NotSignedIn));
    }

    /// <summary>Every controller action, and who may call it.</summary>
    private static readonly Dictionary<string, string> ExpectedRules = new()
    {
        // Before sign-in: the sign-in page, the OAuth endpoints, client registration.
        ["SignIn.Login"] = Anonymous,
        ["Authorization.Authorize"] = Anonymous,
        ["Authorization.Exchange"] = Anonymous,
        ["Authorization.Logout"] = Anonymous,
        ["ClientRegistration.Register"] = Anonymous,

        // Any signed-in user: who they are, the model and lookups the client loads at sign-in,
        // Quinn (whose tools check the role for each call), and their own Quinn usage.
        ["Account.Me"] = SignedIn,
        ["Usage.Get"] = SignedIn,
        ["Validation.Metadata"] = SignedIn,
        ["Model.Metadata"] = SignedIn,
        ["Lookups.AllTypes"] = SignedIn,
        ["Lookups.AddressTypes"] = SignedIn,
        ["Lookups.PhoneNumberTypes"] = SignedIn,
        ["Lookups.RateTypes"] = SignedIn,
        ["Lookups.States"] = SignedIn,
        ["Agent.Answer"] = SignedIn,
        ["Agent.Info"] = SignedIn,
        ["Agent.Chat"] = SignedIn,

        // Editor or Reader: the company's staffing data.
        ["ResourceMgt.StaffingResources"] = TempHirePolicies.ReadStaffingData,
        ["ResourceMgt.StaffingResourceListItems"] = TempHirePolicies.ReadStaffingData,
        ["ResourceMgt.Addresses"] = TempHirePolicies.ReadStaffingData,
        ["ResourceMgt.PhoneNumbers"] = TempHirePolicies.ReadStaffingData,
        ["ResourceMgt.Rates"] = TempHirePolicies.ReadStaffingData,
        ["ResourceMgt.Skills"] = TempHirePolicies.ReadStaffingData,
        ["ResourceMgt.WorkExperienceItems"] = TempHirePolicies.ReadStaffingData,

        // Editor: saving, and the address suggestions for the address form.
        ["ResourceMgt.SaveChanges"] = TempHirePolicies.EditStaffingData,
        ["Address.Suggest"] = TempHirePolicies.EditStaffingData,
    };

    [Fact]
    public void EveryActionDeclaresTheExpectedRule()
    {
        var actual = Actions().ToDictionary(a => a.Name, a => a.Rule);

        Assert.Equal(
            ExpectedRules.OrderBy(r => r.Key).ToList(),
            [.. actual.OrderBy(r => r.Key)]);
    }

    /// <summary>Each action with the rule its attributes declare.</summary>
    private static IEnumerable<(string Name, string Rule)> Actions()
    {
        var controllers = typeof(ResourceMgtController).Assembly.GetTypes()
            .Where(t => typeof(ControllerBase).IsAssignableFrom(t) && !t.IsAbstract);

        foreach (var controller in controllers)
        {
            var actions = controller
                .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => m.GetCustomAttributes<HttpMethodAttribute>().Any());

            foreach (var action in actions.DistinctBy(m => m.Name))
            {
                yield return ($"{controller.Name.Replace("Controller", "")}.{action.Name}", RuleOf(controller, action));
            }
        }
    }

    private static string RuleOf(Type controller, MethodInfo action)
    {
        if (action.IsDefined(typeof(AllowAnonymousAttribute)) || controller.IsDefined(typeof(AllowAnonymousAttribute)))
        {
            return Anonymous;
        }

        var authorize = controller.GetCustomAttributes<AuthorizeAttribute>()
            .Concat(action.GetCustomAttributes<AuthorizeAttribute>())
            .ToList();

        // Roles go through the policies, so the rules stay in one place.
        Assert.All(authorize, a => Assert.True(string.IsNullOrEmpty(a.Roles),
            $"{controller.Name}.{action.Name} names roles; use a TempHirePolicies policy instead."));

        var policies = authorize.Select(a => a.Policy).Where(p => !string.IsNullOrEmpty(p)).Distinct().ToList();
        return policies.Count > 0 ? string.Join(", ", policies.Order()) : SignedIn;
    }

    private static ClaimsPrincipal User(params string[] roles)
    {
        var identity = new ClaimsIdentity("test", Claims.Name, Claims.Role);
        identity.AddClaim(new Claim(Claims.Name, "someone@harbor.test"));
        identity.AddClaims(roles.Select(role => new Claim(Claims.Role, role)));
        return new ClaimsPrincipal(identity);
    }
}
