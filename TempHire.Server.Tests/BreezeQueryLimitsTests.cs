using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Breeze.Core;
using IdeaBlade.Validation;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using TempHire.Model;
using TempHire.Model.Identity;
using TempHire.Server.Auth;
using TempHire.Server.Controllers;
using TempHire.Server.Persistence;
using TempHire.Server.Validation;
using Xunit;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace TempHire.Server.Tests;

/// <summary>
/// What a client-composed Breeze query may cost (<see cref="BreezeQueryLimits"/>), over HTTP through
/// the real filter pipeline: the global filter Program.cs registers, and the tighter ones on the
/// lookups and on Resource Management's detail queries.
///
/// <para>
/// Through HTTP because the limits live in MVC's filters, and their order is the point: the filter
/// nearest the action answers the query.  A controller attribute that left the limits out would
/// lift them, and only a request shows it.
/// </para>
/// </summary>
public sealed class BreezeQueryLimitsTests : IAsyncLifetime
{
    private readonly SaveFixture _db = new();
    private WebApplication _app;
    private HttpClient _client;

    public async Task InitializeAsync()
    {
        // As Program.cs does at startup: without it, expand is ignored until a persistence manager exists.
        TempHirePersistenceManager.InstallQueryHooks();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();

        // The server's MVC setup (Program.cs): its controllers, Json.NET as Breeze needs it, and the
        // query limits, registered by the same call.
        builder.Services.AddControllers()
            .AddApplicationPart(typeof(ResourceMgtController).Assembly)
            .AddNewtonsoftJson(opt =>
            {
                var ss = JsonSerializationFns.UpdateWithDefaults(opt.SerializerSettings);
                if (ss.ContractResolver is DefaultContractResolver resolver)
                {
                    resolver.NamingStrategy = null;
                }
            })
            .AddMvcOptions(o => BreezeQueryLimits.AddTo(o));

        // The test tenant's data, and an editor of it signed in.
        builder.Services.AddScoped(_ => _db.NewContext());
        var metadata = new ValidationMetadataFile(Path.Combine(AppContext.BaseDirectory, "validation.metadata.json"));
        builder.Services.AddIdeaBladeValidation(v => v.AddMetadataJson(metadata.Json).AddDataAnnotations("breeze"));
        builder.Services.AddAuthentication(SignedInEditor.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, SignedInEditor>(SignedInEditor.SchemeName, null);
        builder.Services.AddAuthorization(o => o.AddTempHirePolicies());

        _app = builder.Build();
        _app.UseAuthentication();
        _app.UseAuthorization();
        _app.MapControllers();
        await _app.StartAsync();
        _client = _app.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_app is not null)
        {
            await _app.DisposeAsync();
        }

        _db.Dispose();
    }

    // ---- MaxTake: 1000 rows, everywhere ---------------------------------------------------------

    [Fact]
    public async Task A_query_returns_at_most_1000_rows_even_if_it_asks_for_more()
    {
        StoreResources(1001);

        Assert.Equal(1000, await CountAsync("resourcemgt/StaffingResources"));
        Assert.Equal(1000, await CountAsync("resourcemgt/StaffingResources", new { take = 5000 }));
        Assert.Equal(5, await CountAsync("resourcemgt/StaffingResources", new { take = 5 }));
    }

    [Fact]
    public async Task The_tighter_filters_keep_the_row_cap()
    {
        // An attribute of its own replaces the global filter; it must restate MaxTake, or the cap is gone.
        StoreStates(1001);
        Assert.Equal(1000, await CountAsync("lookups/States"));

        StoreResources(1001, withAddress: true);
        Assert.Equal(1000, await CountAsync("resourcemgt/Addresses"));
        Assert.Equal(1000, await CountAsync("resourcemgt/StaffingResourceListItems"));
    }

    // ---- MaxDepth: 2 for the aggregate root, 1 for the rest, 0 for the lookups -----------------

    [Fact]
    public async Task A_staffing_resource_expands_two_levels_but_not_three()
    {
        _db.StoreValidResource();

        var loaded = await QueryAsync("resourcemgt/StaffingResources", new { expand = new[] { "Addresses.State" } });
        Assert.Equal(HttpStatusCode.OK, loaded.Status);
        Assert.Equal("WA", (string)Rows(loaded.Body).Single()["Addresses"]![0]!["State"]!["ShortName"]);

        var refused = await QueryAsync("resourcemgt/StaffingResources", new { expand = new[] { "Addresses.StaffingResource.PhoneNumbers" } });
        Assert.Equal(HttpStatusCode.BadRequest, refused.Status);
    }

    [Fact]
    public async Task An_address_expands_its_own_navigations_but_nothing_beyond_them()
    {
        _db.StoreValidResource();

        var loaded = await QueryAsync("resourcemgt/Addresses", new { expand = new[] { "AddressType", "StaffingResource", "State" } });
        Assert.Equal(HttpStatusCode.OK, loaded.Status);
        var address = Rows(loaded.Body).Single();
        Assert.Equal("Home", (string)address["AddressType"]!["Name"]);
        Assert.Equal("Ann", (string)address["StaffingResource"]!["FirstName"]);

        var refused = await QueryAsync("resourcemgt/Addresses", new { expand = new[] { "StaffingResource.PhoneNumbers" } });
        Assert.Equal(HttpStatusCode.BadRequest, refused.Status);
    }

    [Theory]
    [InlineData("resourcemgt/PhoneNumbers", "StaffingResource.Addresses")]
    [InlineData("resourcemgt/Rates", "StaffingResource.Addresses")]
    [InlineData("resourcemgt/Skills", "StaffingResource.Addresses")]
    [InlineData("resourcemgt/WorkExperienceItems", "StaffingResource.Addresses")]
    public async Task The_other_detail_queries_expand_one_level(string endpoint, string twoLevels)
    {
        var (_, status) = await QueryAsync(endpoint, new { expand = new[] { "StaffingResource" } });
        Assert.Equal(HttpStatusCode.OK, status);

        var refused = await QueryAsync(endpoint, new { expand = new[] { twoLevels } });
        Assert.Equal(HttpStatusCode.BadRequest, refused.Status);
    }

    [Fact]
    public async Task A_select_is_bounded_like_an_expand()
    {
        // A navigation reached through select counts as much as one reached through expand.
        _db.StoreValidResource();

        var refused = await QueryAsync("resourcemgt/Addresses", new { select = new[] { "StaffingResource.PhoneNumbers" } });
        Assert.Equal(HttpStatusCode.BadRequest, refused.Status);
    }

    [Theory]
    [InlineData("lookups/AddressTypes")]
    [InlineData("lookups/PhoneNumberTypes")]
    [InlineData("lookups/RateTypes")]
    [InlineData("lookups/States")]
    public async Task A_lookup_expands_nothing(string endpoint)
    {
        var plain = await QueryAsync(endpoint, new { take = 1 });
        Assert.Equal(HttpStatusCode.OK, plain.Status);

        // A lookup has no navigations today; MaxDepth = 0 refuses any expand before the model is asked.
        var refused = await QueryAsync(endpoint, new { expand = new[] { "Anything" } });
        Assert.Equal(HttpStatusCode.BadRequest, refused.Status);
    }

    // ---- Ordering: by key unless the client orders ------------------------------------------------

    /// <summary>
    /// Every query action orders by key, so the cap's <c>Take(1000)</c> takes the same rows every time
    /// (and EF Core has no "Take without OrderBy" to warn about).
    /// </summary>
    [Fact]
    public async Task Without_an_orderBy_rows_come_in_key_order()
    {
        StoreResources(20);
        using var db = _db.NewContext();
        var byKey = db.StaffingResources.OrderBy(r => r.Id).Select(r => r.Id).ToList();

        var (body, _) = await QueryAsync("resourcemgt/StaffingResources", new { take = 20 });

        Assert.Equal(byKey, Rows(body).Select(r => (Guid)r["Id"]!).ToList());
    }

    /// <summary>
    /// Breeze applies the client's first <c>orderBy</c> term with <c>OrderBy</c>, which replaces the
    /// action's ordering by key: the client's ordering wins.
    /// </summary>
    [Fact]
    public async Task A_client_orderBy_replaces_the_order_by_key()
    {
        var names = new[] { "Baker", "Young", "Adams", "Moore", "Clark" };
        _db.Store([.. names.Select(name => SaveFixture.Resource() is var r ? Named(r, name) : null)]);

        var (descending, _) = await QueryAsync("resourcemgt/StaffingResources", new { orderBy = new[] { "LastName desc" } });
        Assert.Equal(names.OrderByDescending(n => n), Rows(descending).Select(r => (string)r["LastName"]));

        // The search orders by last name, ascending; a client's orderBy replaces that too.
        var (search, _) = await QueryAsync("resourcemgt/StaffingResourceListItems");
        Assert.Equal(names.OrderBy(n => n), Rows(search).Select(r => (string)r["LastName"]));
        var (searchDescending, _) = await QueryAsync("resourcemgt/StaffingResourceListItems", new { orderBy = new[] { "LastName desc" } });
        Assert.Equal(names.OrderByDescending(n => n), Rows(searchDescending).Select(r => (string)r["LastName"]));

        static StaffingResource Named(StaffingResource resource, string lastName)
        {
            resource.LastName = lastName;
            return resource;
        }
    }

    // ---- No attribute loosens them ----------------------------------------------------------------

    /// <summary>
    /// A <c>[BreezeQueryFilter]</c> on a controller or action runs before the global one and answers
    /// the query, so the global filter's limits don't reach it: one that leaves a limit out (-1,
    /// Breeze's default) lifts it for every query with a query string.  The global filter covers a
    /// controller with no attribute; this covers one whose attribute forgot the limits.
    /// </summary>
    [Fact]
    public void Every_query_filter_attribute_keeps_the_limits()
    {
        var attributes = typeof(ResourceMgtController).Assembly.GetTypes()
            .Where(t => typeof(Microsoft.AspNetCore.Mvc.ControllerBase).IsAssignableFrom(t))
            .SelectMany(t => t.GetMethods().Cast<System.Reflection.MemberInfo>().Prepend(t)
                .SelectMany(m => m.GetCustomAttributes(typeof(Breeze.AspNetCore.BreezeQueryFilterAttribute), inherit: false)
                    .Cast<Breeze.AspNetCore.BreezeQueryFilterAttribute>()
                    .Select(a => (Where: m is Type ? t.Name : $"{t.Name}.{m.Name}", a.MaxTake, a.MaxDepth))))
            .ToList();

        Assert.NotEmpty(attributes);
        Assert.All(attributes, a =>
        {
            Assert.True(a.MaxTake is >= 0 and <= BreezeQueryLimits.MaxTake, $"{a.Where}: MaxTake {a.MaxTake}");
            Assert.True(a.MaxDepth is >= 0 and <= BreezeQueryLimits.MaxDepth, $"{a.Where}: MaxDepth {a.MaxDepth}");
        });
    }

    [Fact]
    public async Task A_controller_without_an_attribute_gets_the_global_limits()
    {
        // StaffingResources carries no [BreezeQueryFilter]: what bounds it is the global filter.
        Assert.Null(System.Reflection.CustomAttributeExtensions.GetCustomAttribute<Breeze.AspNetCore.BreezeQueryFilterAttribute>(
            typeof(ResourceMgtController).GetMethod(nameof(ResourceMgtController.StaffingResources))!));
        Assert.Null(System.Reflection.CustomAttributeExtensions.GetCustomAttribute<Breeze.AspNetCore.BreezeQueryFilterAttribute>(
            typeof(ResourceMgtController)));

        var refused = await QueryAsync("resourcemgt/StaffingResources", new { expand = new[] { "Addresses.StaffingResource.Addresses" } });
        Assert.Equal(HttpStatusCode.BadRequest, refused.Status);
    }

    // ---- helpers --------------------------------------------------------------------------------

    private void StoreResources(int count, bool withAddress = false)
    {
        var entities = new List<object>();
        for (var i = 0; i < count; i++)
        {
            var resource = SaveFixture.Resource();
            entities.Add(resource);
            if (withAddress)
            {
                entities.Add(SaveFixture.Address(resource.Id));
            }
        }

        _db.Store([.. entities]);
    }

    private void StoreStates(int count)
    {
        using var db = _db.NewContext();
        db.States.AddRange(Enumerable.Range(0, count).Select(i => new State { Id = Guid.NewGuid(), Name = $"State {i}", ShortName = "S" }));
        db.SaveChanges();
    }

    private async Task<int> CountAsync(string endpoint, object query = null)
    {
        var (body, status) = await QueryAsync(endpoint, query);
        Assert.Equal(HttpStatusCode.OK, status);
        return Rows(body).Count;
    }

    /// <summary>A Breeze query, as the client sends it: the JSON query is the whole query string.</summary>
    private async Task<(JToken Body, HttpStatusCode Status)> QueryAsync(string endpoint, object query = null)
    {
        var url = "/api/" + endpoint + (query is null ? "" : "?" + Uri.EscapeDataString(JObject.FromObject(query).ToString(Newtonsoft.Json.Formatting.None)));
        using var response = await _client.GetAsync(url);
        var text = await response.Content.ReadAsStringAsync();
        return (response.IsSuccessStatusCode ? JToken.Parse(text) : null, response.StatusCode);
    }

    /// <summary>The rows of an answer: an array, or a QueryResult's <c>Results</c>.</summary>
    private static List<JToken> Rows(JToken body) =>
        [.. (body is JObject result ? result["Results"]! : body).Children()];

    /// <summary>Signs every request in as an editor of the test tenant.</summary>
    internal sealed class SignedInEditor(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string SchemeName = "Test";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var identity = new ClaimsIdentity(SchemeName, Claims.Name, Claims.Role);
            identity.AddClaim(new Claim(Claims.Name, "anna@harbor.test"));
            identity.AddClaim(new Claim(Claims.Role, TempHireRoles.Editor));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
        }
    }
}
