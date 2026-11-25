using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using IdeaBlade.Validation;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using TempHire.Model;
using TempHire.Model.Identity;
using TempHire.Server.Addresses;
using TempHire.Server.Auth;
using TempHire.Server.Mcp;
using TempHire.Server.Validation;
using Xunit;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace TempHire.Server.Tests;

/// <summary>
/// Changing an address through the MCP tools (<see cref="AddressUpdateTools"/>), as Quinn does:
/// an MCP client over HTTP, against the stateless MCP server with TempHire's access rules, and a
/// stand-in for the user, who answers the tools' questions (elicitation).  Photon is a stand-in too.
///
/// <para>
/// Through a real client and server because the questions travel as multi round-trip requests:
/// the tool answers its first call with the question, and the client calls again with the user's
/// answer.  Only the whole round trip shows that a change waits for the user, and that their
/// answer reaches the tool.
/// </para>
/// </summary>
public sealed class AddressUpdateToolsTests : IAsyncLifetime
{
    private static readonly Guid WorkTypeId = Guid.NewGuid();
    private static readonly Guid OregonId = Guid.NewGuid();

    private readonly SaveFixture _db = new();
    private readonly FakePhoton _photon = new();
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());
    private WebApplication _app;

    /// <summary>The questions the user was asked, in order.</summary>
    private readonly List<ElicitRequestParams> _asked = [];

    /// <summary>How the user answers each question.</summary>
    private Func<ElicitRequestParams, ElicitResult> _user = _ => throw new InvalidOperationException("The user was asked nothing in this test.");

    public async Task InitializeAsync()
    {
        _db.Store(
            new AddressType { Id = WorkTypeId, Name = "Work", DisplayName = "Work Address" },
            new State { Id = OregonId, Name = "Oregon", ShortName = "OR" });

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();

        builder.Services.AddScoped(_ => _db.NewContext());
        var metadata = new ValidationMetadataFile(Path.Combine(AppContext.BaseDirectory, "validation.metadata.json"));
        builder.Services.AddIdeaBladeValidation(v => v.AddMetadataJson(metadata.Json).AddDataAnnotations("breeze"));
        builder.Services.AddSingleton(new PhotonAddressSuggester(
            new HttpClient(_photon) { BaseAddress = new Uri("https://photon.test/") },
            _cache,
            Options.Create(new AddressSuggestionOptions()),
            NullLogger<PhotonAddressSuggester>.Instance));
        builder.Services.AddDataProtection();
        builder.Services.AddAuthentication(SignedInAs.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, SignedInAs>(SignedInAs.SchemeName, null);
        builder.Services.AddAuthorization(o => o.AddTempHirePolicies());

        // The MCP server as Program.cs sets it up.
        builder.Services.AddMcpServer()
            .WithHttpTransport(o => o.Stateless = true)
            .WithTools<StaffingTools>()
            .WithTools<AddressUpdateTools>()
            .WithTempHireAccessRules();

        _app = builder.Build();
        _app.UseAuthentication();
        _app.UseAuthorization();
        _app.MapMcp("/mcp").RequireAuthorization();
        await _app.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _app.DisposeAsync();
        _cache.Dispose();
        _db.Dispose();
    }

    // ------------------------------------------------------------------------------------------
    // Step 1: which address
    // ------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_resource_with_one_address_needs_no_choice()
    {
        var (resource, address, _) = _db.StoreValidResource();

        var result = await CallAsync("choose_address_to_update", new { staffingResourceId = resource.Id });

        Assert.True(result.GetProperty("chosen").GetBoolean());
        Assert.Equal(address.Id, result.GetProperty("addressId").GetGuid());
        Assert.Equal("1 Main St, Seattle, WA 98101", result.GetProperty("currentAddress").GetString());
        Assert.Empty(_asked);
    }

    [Fact]
    public async Task With_several_addresses_and_none_named_the_user_picks_one()
    {
        var (resource, home, _) = _db.StoreValidResource();
        var work = Work(resource.Id);
        _db.Store(work);
        _user = question => Choose(question, work.Id.ToString());

        var result = await CallAsync("choose_address_to_update", new { staffingResourceId = resource.Id });

        Assert.Equal(work.Id, result.GetProperty("addressId").GetGuid());
        var choices = Choices(Assert.Single(_asked));
        Assert.Equal([home.Id.ToString(), work.Id.ToString()], choices.Select(c => c.Const));
        Assert.Equal("Work Address: 200 Pine St, Portland, OR 97204", choices[1].Title);
    }

    [Fact]
    public async Task A_named_address_type_needs_no_choice()
    {
        var (resource, _, _) = _db.StoreValidResource();
        var work = Work(resource.Id);
        _db.Store(work);

        var result = await CallAsync("choose_address_to_update", new { staffingResourceId = resource.Id, addressType = "work" });

        Assert.Equal(work.Id, result.GetProperty("addressId").GetGuid());
        Assert.Empty(_asked);
    }

    [Fact]
    public async Task Cancelling_the_choice_chooses_nothing()
    {
        var (resource, _, _) = _db.StoreValidResource();
        _db.Store(Work(resource.Id));
        _user = _ => new ElicitResult { Action = "cancel" };

        var result = await CallAsync("choose_address_to_update", new { staffingResourceId = resource.Id });

        Assert.False(result.GetProperty("chosen").GetBoolean());
        Assert.Single(_asked);
    }

    [Fact]
    public async Task Another_tenants_resource_is_not_found()
    {
        var other = Guid.NewGuid();
        var resource = SaveFixture.Resource();
        resource.TenantId = other;
        var address = SaveFixture.Address(resource.Id);
        address.TenantId = other;
        _db.Store(new Tenant { Id = other, Name = "Other", EmailDomain = "other.test" });
        _db.StoreFor(other, resource, address);

        var result = await CallAsync("choose_address_to_update", new { staffingResourceId = resource.Id });

        Assert.False(result.GetProperty("chosen").GetBoolean());
        Assert.Contains("No staffing resource", result.GetProperty("message").GetString());
    }

    // ------------------------------------------------------------------------------------------
    // Step 2: complete the new address
    // ------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_street_without_its_house_number_gets_the_one_the_user_gave()
    {
        _photon.Answer = Photon(("", "West Capital Way", "Tacoma", "Washington", "98401"));

        var result = await CallAsync("complete_address", new { address = "908 W Capital Way, Tacoma WA" });

        Assert.True(result.GetProperty("found").GetBoolean());
        Assert.Equal("908 West Capital Way", result.GetProperty("address1").GetString());
        Assert.Equal("WA", result.GetProperty("state").GetString());
        Assert.Equal("98401", result.GetProperty("zipcode").GetString());
        Assert.Empty(_asked);
    }

    [Fact]
    public async Task A_numbered_street_without_its_house_number_gets_it_too()
    {
        _photon.Answer = Photon(("", "11th Avenue West", "Seattle", "Washington", "98119"));

        var result = await CallAsync("complete_address", new { address = "449 11th Ave W, Seattle, WA" });

        Assert.Equal("449 11th Avenue West", result.GetProperty("address1").GetString());
    }

    [Fact]
    public async Task A_missing_ZIP_code_is_the_one_the_user_gave()
    {
        _photon.Answer = Photon(("12", "Pine Street", "Seattle", "Washington", null));

        var result = await CallAsync("complete_address", new { address = "12 Pine St, Seattle, WA 98101" });

        Assert.Equal("98101", result.GetProperty("zipcode").GetString());
    }

    [Fact]
    public async Task With_several_matches_the_user_picks_one_or_cancels()
    {
        _photon.Answer = Photon(
            ("908", "Capital Way", "Tacoma", "Washington", "98401"),
            ("908", "Capital Way", "Olympia", "Washington", "98501"),
            ("908", "Capital Avenue", "Paris", "Texas", "75460"));
        _user = question => Choose(question, "1");

        var result = await CallAsync("complete_address", new { address = "908 Capital Way" });

        Assert.Equal("Olympia", result.GetProperty("city").GetString());
        // Texas is not one of TempHire's states (in this test), so it isn't offered.
        Assert.Equal(
            ["908 Capital Way, Tacoma, WA 98401", "908 Capital Way, Olympia, WA 98501"],
            Choices(Assert.Single(_asked)).Select(c => c.Title));

        _asked.Clear();
        _user = _ => new ElicitResult { Action = "cancel" };
        var cancelled = await CallAsync("complete_address", new { address = "908 Capital Way" });

        Assert.False(cancelled.GetProperty("found").GetBoolean());
        Assert.True(cancelled.GetProperty("cancelled").GetBoolean());
    }

    // ------------------------------------------------------------------------------------------
    // Step 3: check, confirm and save
    // ------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_confirmed_change_is_saved_through_the_save_interceptor()
    {
        var (resource, address, _) = _db.StoreValidResource();
        _user = _ => new ElicitResult { Action = "accept", Content = new Dictionary<string, JsonElement>() };

        var result = await CallAsync("update_staffing_resource_address", Change(resource.Id, address.Id));

        Assert.True(result.GetProperty("updated").GetBoolean());
        var changed = Assert.Single(result.GetProperty("changed").EnumerateArray());
        Assert.Equal("Address", changed.GetProperty("entityType").GetString());
        Assert.Equal(address.Id, changed.GetProperty("id").GetGuid());
        Assert.Equal(resource.Id, changed.GetProperty("staffingResourceId").GetGuid());
        Assert.Equal(
            "Updated Ann Lee's Home Address from 1 Main St, Seattle, WA 98101 to 200 Pine St, Portland, OR 97204.",
            changed.GetProperty("summary").GetString());
        var question = Assert.Single(_asked);
        Assert.Contains("**Ann Lee**'s Home Address", question.Message);
        Assert.Contains("**Current:** 1 Main St, Seattle, WA 98101", question.Message);
        Assert.Contains("**New:** 200 Pine St, Portland, OR 97204", question.Message);
        Assert.Empty(question.RequestedSchema.Properties);

        var saved = _db.Load<Address>(address.Id);
        Assert.Equal(("200 Pine St", "Portland", OregonId, "97204"), (saved.Address1, saved.City, saved.StateId, saved.Zipcode));
        // The save interceptor's audit columns and row version; the rest of the address unchanged.
        Assert.Equal("anna@harbor.test", saved.ModifyUser);
        Assert.Equal(2, saved.RowVersion);
        Assert.Equal((address.AddressTypeId, address.Primary, resource.Id), (saved.AddressTypeId, saved.Primary, saved.StaffingResourceId));
    }

    [Fact]
    public async Task A_cancelled_change_is_not_saved()
    {
        var (resource, address, _) = _db.StoreValidResource();
        _user = _ => new ElicitResult { Action = "cancel" };

        var result = await CallAsync("update_staffing_resource_address", Change(resource.Id, address.Id));

        Assert.False(result.GetProperty("updated").GetBoolean());
        Assert.False(result.TryGetProperty("changed", out _));
        Assert.Single(_asked);
        Assert.Equal("1 Main St", _db.Load<Address>(address.Id).Address1);
    }

    [Fact]
    public async Task An_address_of_another_resource_is_refused_without_asking()
    {
        var (resource, _, _) = _db.StoreValidResource();
        var (_, othersAddress, _) = _db.StoreValidResource();

        var result = await CallAsync("update_staffing_resource_address", Change(resource.Id, othersAddress.Id));

        Assert.False(result.GetProperty("updated").GetBoolean());
        Assert.Contains("isn't one of Ann Lee's", result.GetProperty("message").GetString());
        Assert.Empty(_asked);
        Assert.Equal("1 Main St", _db.Load<Address>(othersAddress.Id).Address1);
    }

    [Fact]
    public async Task An_address_of_another_tenant_is_not_found()
    {
        var (resource, _, _) = _db.StoreValidResource();
        var other = Guid.NewGuid();
        _db.Store(new Tenant { Id = other, Name = "Other", EmailDomain = "other.test" });
        var theirs = SaveFixture.Resource();
        theirs.TenantId = other;
        var theirAddress = SaveFixture.Address(theirs.Id);
        theirAddress.TenantId = other;
        _db.StoreFor(other, theirs, theirAddress);

        var result = await CallAsync("update_staffing_resource_address", Change(resource.Id, theirAddress.Id));

        Assert.Contains("No address with that id", result.GetProperty("message").GetString());
        Assert.Empty(_asked);
    }

    [Fact]
    public async Task A_change_that_breaks_the_rules_is_refused_before_the_user_is_asked()
    {
        var (resource, address, _) = _db.StoreValidResource();

        var result = await CallAsync("update_staffing_resource_address",
            new { staffingResourceId = resource.Id, addressId = address.Id, address1 = "200 Pine St", city = "Portland", state = "OR", zipcode = "972" });

        Assert.False(result.GetProperty("updated").GetBoolean());
        Assert.NotEmpty(result.GetProperty("errors").EnumerateArray());
        Assert.Empty(_asked);
    }

    /// <summary>
    /// A client that sends an answer without having been asked - a forged "confirm" - gets the
    /// question instead: an answer only counts with the request state the tool gave with it.
    /// </summary>
    [Fact]
    public async Task An_answer_the_tool_did_not_ask_for_does_not_count()
    {
        var (resource, address, _) = _db.StoreValidResource();
        var http = _app.GetTestClient();
        http.DefaultRequestHeaders.Add(SignedInAs.RoleHeader, TempHireRoles.Editor);
        http.DefaultRequestHeaders.Add("MCP-Protocol-Version", "2026-07-28");
        http.DefaultRequestHeaders.Add("Mcp-Method", "tools/call");
        http.DefaultRequestHeaders.Add("Mcp-Name", "update_staffing_resource_address");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        http.DefaultRequestHeaders.Accept.ParseAdd("text/event-stream");

        var call = new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "tools/call",
            @params = new
            {
                name = "update_staffing_resource_address",
                arguments = Change(resource.Id, address.Id),
                inputResponses = new Dictionary<string, object> { ["confirm"] = new { action = "accept", content = new { } } },
                _meta = new Dictionary<string, object>
                {
                    ["io.modelcontextprotocol/protocolVersion"] = "2026-07-28",
                    ["io.modelcontextprotocol/clientCapabilities"] = new { elicitation = new { form = new { } } }
                }
            }
        };
        var response = await http.PostAsync("mcp", new StringContent(JsonSerializer.Serialize(call), System.Text.Encoding.UTF8, "application/json"));
        var body = await response.Content.ReadAsStringAsync();

        Assert.True(body.Contains("inputRequests"), body);
        Assert.Equal("1 Main St", _db.Load<Address>(address.Id).Address1);
    }

    // ------------------------------------------------------------------------------------------
    // Readers
    // ------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_reader_is_neither_offered_the_tools_nor_allowed_to_call_them()
    {
        var (resource, address, _) = _db.StoreValidResource();
        await using var client = await ConnectAsync(TempHireRoles.Reader);

        var tools = await client.ListToolsAsync();
        Assert.Contains(tools, t => t.Name == "get_staffing_resource");
        Assert.DoesNotContain(tools, t => AddressUpdateTools.Names.Contains(t.Name));

        var result = await client.CallToolAsync("update_staffing_resource_address",
            ToArguments(Change(resource.Id, address.Id)));
        Assert.True(result.IsError);
        Assert.Contains("only editors", ((TextContentBlock)result.Content[0]).Text);
        Assert.Empty(_asked);
        Assert.Equal("1 Main St", _db.Load<Address>(address.Id).Address1);
    }

    // ------------------------------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------------------------------

    private static Address Work(Guid resourceId) => new()
    {
        Id = Guid.NewGuid(), StaffingResourceId = resourceId, AddressTypeId = WorkTypeId, StateId = OregonId,
        Address1 = "200 Pine St", City = "Portland", Zipcode = "97204", TenantId = SaveFixture.Tenant,
    };

    private static object Change(Guid resourceId, Guid addressId) =>
        new { staffingResourceId = resourceId, addressId, address1 = "200 Pine St", city = "Portland", state = "OR", zipcode = "97204" };

    private ElicitResult Choose(ElicitRequestParams question, string value) => new()
    {
        Action = "accept",
        Content = new Dictionary<string, JsonElement> { [question.RequestedSchema.Properties.Keys.Single()] = JsonSerializer.SerializeToElement(value) }
    };

    private static IList<ElicitRequestParams.EnumSchemaOption> Choices(ElicitRequestParams question) =>
        ((ElicitRequestParams.TitledSingleSelectEnumSchema)question.RequestedSchema.Properties.Values.Single()).OneOf;

    /// <summary>Calls a tool as an editor and returns its JSON result.</summary>
    private async Task<JsonElement> CallAsync(string tool, object arguments)
    {
        await using var client = await ConnectAsync(TempHireRoles.Editor);
        var result = await client.CallToolAsync(tool, ToArguments(arguments));
        Assert.True(result.IsError != true, (result.Content.FirstOrDefault() as TextContentBlock)?.Text);
        return JsonDocument.Parse(((TextContentBlock)result.Content[0]).Text).RootElement;
    }

    private static Dictionary<string, object> ToArguments(object arguments) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(arguments))!
            .ToDictionary(p => p.Key, p => (object)p.Value);

    private async Task<McpClient> ConnectAsync(string role)
    {
        var http = _app.GetTestClient();
        http.DefaultRequestHeaders.Add(SignedInAs.RoleHeader, role);
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = new Uri(http.BaseAddress!, "mcp") }, http, NullLoggerFactory.Instance);

        return await McpClient.CreateAsync(transport, new McpClientOptions
        {
            Handlers = new McpClientHandlers
            {
                ElicitationHandler = (question, _) =>
                {
                    _asked.Add(question!);
                    return ValueTask.FromResult(_user(question!));
                }
            }
        });
    }

    private static string Photon(params (string Number, string Street, string City, string State, string Zip)[] addresses) =>
        JsonSerializer.Serialize(new
        {
            type = "FeatureCollection",
            features = addresses.Select(a => new
            {
                properties = new Dictionary<string, string>
                {
                    ["type"] = string.IsNullOrEmpty(a.Number) ? "street" : "house",
                    ["housenumber"] = string.IsNullOrEmpty(a.Number) ? null : a.Number,
                    ["street"] = string.IsNullOrEmpty(a.Number) ? null : a.Street,
                    ["name"] = string.IsNullOrEmpty(a.Number) ? a.Street : null,
                    ["city"] = a.City,
                    ["state"] = a.State,
                    ["postcode"] = a.Zip,
                    ["countrycode"] = "US"
                }.Where(p => p.Value is not null).ToDictionary()
            })
        });

    private sealed class FakePhoton : HttpMessageHandler
    {
        public string Answer { get; set; } = """{"type":"FeatureCollection","features":[]}""";

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Answer) });
    }

    /// <summary>Signs every request in as anna@harbor.test, in the role its header names.</summary>
    private sealed class SignedInAs(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string SchemeName = "Test";
        public const string RoleHeader = "X-Test-Role";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var identity = new ClaimsIdentity(SchemeName, Claims.Name, Claims.Role);
            identity.AddClaim(new Claim(Claims.Subject, "anna"));
            identity.AddClaim(new Claim(Claims.Name, "anna@harbor.test"));
            identity.AddClaim(new Claim(Claims.Role, Request.Headers[RoleHeader].ToString()));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
        }
    }
}
