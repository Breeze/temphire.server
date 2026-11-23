using System.Reflection;
using Breeze.AspNetCore;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.OpenApi;
using Newtonsoft.Json.Linq;
using Swashbuckle.AspNetCore.SwaggerGen;
using TempHire.Server.Auth;
using TempHire.Server.Controllers;

namespace TempHire.Server.Swagger;

/// <summary>
/// Swagger: an OpenAPI description of the API at <c>/swagger/v1/swagger.json</c>, and Swagger UI
/// at <c>/swagger</c> to read and try it.  Development only (see Program.cs).
///
/// <para>
/// <b>Json.NET schemas.</b>  The API speaks Json.NET with PascalCase names (see Program.cs), so the
/// schemas come from Swashbuckle's Newtonsoft support, which reads the same contract resolver.  The
/// generator built into ASP.NET Core describes System.Text.Json and would show camelCase names
/// the API never sends.
/// </para>
/// <para>
/// <b>Signing in.</b>  Every API endpoint needs an access token for the API resource.  Swagger UI
/// gets one like any other OAuth client: the Authorize button runs the Authorization Code flow with
/// PKCE against this server, the user signs in on its sign-in page, and Swagger UI sends the token
/// with each request.  It is a client of its own, <see cref="OAuthSettings.SwaggerClientId"/>,
/// registered at startup (<see cref="OAuthClients.RegisterSwaggerUiAsync"/>); its tokens are good
/// at the API only, not at the MCP endpoint.
/// </para>
/// <para>
/// <b>What is left out.</b>  The authorization server's endpoints (<c>connect/*</c>, the sign-in
/// pages, client registration) are OAuth protocol, not API, and are marked
/// <c>[ApiExplorerSettings(IgnoreApi = true)]</c>; the MCP endpoint describes its tools itself.
/// </para>
/// </summary>
public static class SwaggerSetup
{
    public const string RoutePrefix = "swagger";

    private const string SecuritySchemeName = "oauth2";

    public static IServiceCollection AddTempHireSwagger(this IServiceCollection services)
    {
        services.AddSwaggerGen(options =>
        {
            options.SwaggerDoc("v1", new OpenApiInfo
            {
                Title = "TempHire API",
                Version = "v1",
                Description = """
                    The endpoints the TempHire client apps call.  Sign in with **Authorize** first: every endpoint needs an access token for the API.

                    Every answer is Json.NET with the settings Breeze needs: `$id`/`$ref` for an object that appears more than once, and `$type` naming each object's .NET type, which the schemas below leave out.  Property names are PascalCase on the **Breeze endpoints** (Lookups, Model, ResourceMgt), as Breeze expects, and camelCase on the others.
                    """
            });

            // The controllers' and DTOs' /// comments: summaries, remarks (as descriptions), parameters
            // and responses; a controller's summary describes its group.
            options.IncludeXmlComments(Assembly.GetExecutingAssembly(), includeControllerXmlComments: true);

            options.AddSecurityDefinition(SecuritySchemeName, new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.OAuth2,
                Description = "Sign in on TempHire's own sign-in page (Authorization Code with PKCE).",
                Flows = new OpenApiOAuthFlows
                {
                    AuthorizationCode = new OpenApiOAuthFlow
                    {
                        // Relative: Swagger UI resolves them against the server it was loaded from.
                        AuthorizationUrl = new Uri("/connect/authorize", UriKind.Relative),
                        TokenUrl = new Uri("/connect/token", UriKind.Relative),
                        Scopes = new Dictionary<string, string>
                        {
                            [OAuthSettings.StaffingScope] = "Access to the company's staffing data"
                        }
                    }
                }
            });
            options.AddSecurityRequirement(document => new OpenApiSecurityRequirement
            {
                [new OpenApiSecuritySchemeReference(SecuritySchemeName, document)] = [OAuthSettings.StaffingScope]
            });

            // A Breeze save bundle is a JObject: describe it as the object it is, not JObject's members.
            options.MapType<JObject>(() => new OpenApiSchema { Type = JsonSchemaType.Object });

            options.OperationFilter<BreezeQueryOperationFilter>();
            options.OperationFilter<AuthorizationResponsesOperationFilter>();
        });

        // Schemas as Json.NET writes them (must come after AddSwaggerGen).
        services.AddSwaggerGenNewtonsoftSupport();

        return services;
    }

    public static IApplicationBuilder UseTempHireSwagger(this IApplicationBuilder app)
    {
        // Middleware, not endpoints, so the fallback policy that asks every endpoint for a signed-in
        // user (TempHirePolicies) does not apply: the description and the UI load anonymously, and
        // the API calls made from the UI carry the token.
        app.UseSwagger();
        app.UseSwaggerUI(options =>
        {
            options.RoutePrefix = RoutePrefix;
            options.DocumentTitle = "TempHire API";
            options.SwaggerEndpoint("/swagger/v1/swagger.json", "TempHire API v1");

            options.OAuthClientId(OAuthSettings.SwaggerClientId);
            options.OAuthAppName("TempHire API (Swagger UI)");
            options.OAuthUsePkce();
            options.OAuthScopes(OAuthSettings.StaffingScope);
            // Keep the token across page reloads (in the browser's local storage).
            options.EnablePersistAuthorization();
        });

        return app;
    }

    /// <summary>
    /// Says how to query the Breeze query endpoints, which take a query Swagger UI can't send.
    /// </summary>
    /// <remarks>
    /// <c>[BreezeQueryFilter]</c> reads a Breeze query - JSON, such as
    /// <c>{"where":{"LastName":"Smith"},"take":5}</c> - from the whole query string, not from a named
    /// parameter, and applies it to the <c>IQueryable</c> the action returns.  OpenAPI has no way
    /// to describe a nameless query string, so Swagger UI can only call these endpoints without one:
    /// every row the signed-in user may see.
    /// </remarks>
    private sealed class BreezeQueryOperationFilter : IOperationFilter
    {
        public void Apply(OpenApiOperation operation, OperationFilterContext context)
        {
            // Every action that returns an IQueryable is a Breeze query: the filter is registered
            // globally (BreezeQueryLimits), and a controller or action may carry a tighter one.
            if (context.ApiDescription.ActionDescriptor is not ControllerActionDescriptor action
                || !IsQueryable(action.MethodInfo.ReturnType))
            {
                return;
            }

            // The limits that apply: the filter nearest the action answers the query.
            var filter = action.MethodInfo.GetCustomAttribute<BreezeQueryFilterAttribute>()
                         ?? action.ControllerTypeInfo.GetCustomAttribute<BreezeQueryFilterAttribute>();
            var maxTake = filter?.MaxTake ?? BreezeQueryLimits.MaxTake;
            var maxDepth = filter?.MaxDepth ?? BreezeQueryLimits.MaxDepth;

            var note = "A Breeze query: a Breeze client can add a query (JSON, such as "
                       + "`{\"where\":{\"LastName\":\"Smith\"},\"take\":5}`, URL-encoded) as the whole query string. "
                       + $"Swagger UI can't send one, so here it returns every row you may see, up to {Limit(maxTake, "rows")}. "
                       + (maxDepth == 0
                           ? "No `expand`, and no `select` of a navigation property."
                           : $"`expand` and `select` may go {Limit(maxDepth, "navigation level(s)")} deep; deeper is 400 Bad Request.");
            operation.Description = string.IsNullOrEmpty(operation.Description) ? note : operation.Description + "\n\n" + note;
        }

        private static string Limit(int value, string unit) => value < 0 ? $"any number of {unit}" : $"{value} {unit}";

        private static bool IsQueryable(Type type) =>
            type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IQueryable<>);
    }

    /// <summary>
    /// Documents the 401 every endpoint can answer, and the 403 of the role policies, from the
    /// endpoints' <c>[Authorize]</c> attributes, so the description can't drift from them.
    /// </summary>
    private sealed class AuthorizationResponsesOperationFilter : IOperationFilter
    {
        private static readonly Dictionary<string, string> PolicyRoles = new()
        {
            [TempHirePolicies.ReadStaffingData] = "Needs the Editor or Reader role.",
            [TempHirePolicies.EditStaffingData] = "Needs the Editor role."
        };

        public void Apply(OpenApiOperation operation, OperationFilterContext context)
        {
            var metadata = context.ApiDescription.ActionDescriptor.EndpointMetadata;
            if (metadata.OfType<IAllowAnonymous>().Any())
            {
                return;
            }

            operation.Responses ??= new OpenApiResponses();
            operation.Responses.TryAdd("401", new OpenApiResponse
            {
                Description = "No valid access token for the API: none, an expired one, or one issued for another resource (such as the MCP endpoint)."
            });

            var roles = metadata.OfType<IAuthorizeData>()
                .Select(a => a.Policy)
                .Where(p => p is not null && PolicyRoles.ContainsKey(p))
                .Select(p => PolicyRoles[p])
                .Distinct()
                .ToList();
            if (roles.Count > 0)
            {
                // An endpoint that documents its own 403 (the save) keeps its text, with the role added.
                var text = string.Join(" ", roles);
                operation.Responses["403"] = operation.Responses.TryGetValue("403", out var own)
                    ? new OpenApiResponse { Description = text + " " + own.Description }
                    : new OpenApiResponse { Description = text };
            }
        }
    }
}
