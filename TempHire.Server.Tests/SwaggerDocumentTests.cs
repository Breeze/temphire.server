using Breeze.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;
using Newtonsoft.Json.Serialization;
using Swashbuckle.AspNetCore.Swagger;
using TempHire.Server.Controllers;
using TempHire.Server.Swagger;
using Xunit;

namespace TempHire.Server.Tests;

/// <summary>
/// The Swagger description (<see cref="SwaggerSetup"/>), generated in-process from the server's
/// controllers as the running server generates it.  Every endpoint must be documented: a new one
/// without a summary or with an undescribed response fails here.
/// </summary>
public sealed class SwaggerDocumentTests
{
    private static readonly Lazy<OpenApiDocument> Document = new(Generate);

    [Fact]
    public void DescribesTheApiAndNothingElse()
    {
        // The OAuth protocol endpoints, the sign-in pages and the MCP endpoint are not API.
        Assert.All(Document.Value.Paths.Keys, path => Assert.StartsWith("/api/", path));
        Assert.Contains("/api/resourcemgt/SaveChanges", Document.Value.Paths.Keys);
        Assert.Contains("/api/account/Me", Document.Value.Paths.Keys);
    }

    [Fact]
    public void EveryEndpointHasASummary()
    {
        Assert.All(Operations(), o => Assert.False(string.IsNullOrWhiteSpace(o.Operation.Summary), $"{o.Name} has no summary"));
    }

    [Fact]
    public void EveryEndpointDescribesItsResponses()
    {
        Assert.All(Operations(), o =>
        {
            Assert.True(o.Operation.Responses.ContainsKey("200"), $"{o.Name} documents no 200");
            Assert.All(o.Operation.Responses, r =>
                Assert.False(string.IsNullOrWhiteSpace(r.Value.Description), $"{o.Name}: {r.Key} has no description"));
        });
    }

    [Fact]
    public void EveryEndpointNeedsAToken()
    {
        Assert.All(Operations(), o => Assert.True(o.Operation.Responses.ContainsKey("401"), $"{o.Name} documents no 401"));
        Assert.Contains(Document.Value.Components.SecuritySchemes, s => s.Value.Type == SecuritySchemeType.OAuth2);
    }

    [Fact]
    public void TheRolePoliciesAnswer403()
    {
        var save = Document.Value.Paths["/api/resourcemgt/SaveChanges"].Operations[HttpMethod.Post];
        Assert.StartsWith("Needs the Editor role.", save.Responses["403"].Description);

        var query = Document.Value.Paths["/api/resourcemgt/StaffingResources"].Operations[HttpMethod.Get];
        Assert.Equal("Needs the Editor or Reader role.", query.Responses["403"].Description);
    }

    [Fact]
    public void BreezeSchemasArePascalCase()
    {
        // As Json.NET writes them for Breeze (Program.cs), not as System.Text.Json would.
        Assert.Contains("FirstName", Document.Value.Components.Schemas["StaffingResource"].Properties.Keys);
        // The account DTOs opt into camelCase.
        Assert.Contains("tenantName", Document.Value.Components.Schemas["UserInfo"].Properties.Keys);
    }

    private static IEnumerable<(string Name, OpenApiOperation Operation)> Operations() =>
        Document.Value.Paths.SelectMany(p => p.Value.Operations.Select(o => ($"{o.Key} {p.Key}", o.Value)));

    private static OpenApiDocument Generate()
    {
        // The server's MVC and Json.NET setup (Program.cs), without its databases or authentication.
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddControllers()
            .AddApplicationPart(typeof(AccountController).Assembly)
            .AddNewtonsoftJson(opt =>
            {
                var ss = JsonSerializationFns.UpdateWithDefaults(opt.SerializerSettings);
                if (ss.ContractResolver is DefaultContractResolver resolver)
                {
                    resolver.NamingStrategy = null;
                }
            });
        builder.Services.AddTempHireSwagger();

        using var app = builder.Build();
        return app.Services.GetRequiredService<ISwaggerProvider>().GetSwagger("v1");
    }
}
