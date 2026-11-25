using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using TempHire.Server.Auth;

namespace TempHire.Server.Mcp;

/// <summary>
/// Who may use which MCP tool, checked by the MCP server's filters for every request.
///
/// <list type="bullet">
///   <item>Reading: the tools read the company's staffing data, so they need the Editor or Reader
///   role, as the Breeze queries do.  A signed-in user with neither may still connect and list the
///   tools - Quinn connects before every answer, and can still answer questions that need no data,
///   such as the one about its name - but every tool call answers that the user has no access.</item>
///   <item>Changing: the address update tools (<see cref="AddressUpdateTools"/>) are for editors.
///   The tool list leaves them out for anyone else, so the model isn't offered what it can't use,
///   and a call is refused all the same.</item>
/// </list>
/// <para>
/// A refusal is the tool's result, so the model gets the reason and passes it on, rather than being
/// left to guess.  The tools check the roles again themselves, where the work happens.
/// </para>
/// </summary>
public static class McpAccessRules
{
    public static IMcpServerBuilder WithTempHireAccessRules(this IMcpServerBuilder builder) =>
        builder.WithRequestFilters(filters => filters
            .AddListToolsFilter(next => async (context, cancellationToken) =>
            {
                var result = await next(context, cancellationToken);
                if (!TempHirePolicies.CanEdit(context.User))
                {
                    result.Tools = [.. result.Tools.Where(tool => !AddressUpdateTools.Names.Contains(tool.Name))];
                }

                return result;
            })
            .AddCallToolFilter(next => async (context, cancellationToken) =>
                !TempHirePolicies.CanRead(context.User)
                    ? Refusal(
                        "Access denied: this user's account has no access to the company's staffing data " +
                        "(it needs the Editor or Reader role). Tell the user they don't have access to " +
                        "that data. Do not answer the question from other knowledge.")
                    : AddressUpdateTools.Names.Contains(context.Params?.Name ?? "") && !TempHirePolicies.CanEdit(context.User)
                        ? Refusal(
                            "Access denied: only editors can change data, and this user can only read it. " +
                            "Tell the user they can't make changes. Nothing was changed.")
                        : await next(context, cancellationToken)));

    private static CallToolResult Refusal(string reason) => new()
    {
        IsError = true,
        Content = [new TextContentBlock { Text = reason }]
    };
}
