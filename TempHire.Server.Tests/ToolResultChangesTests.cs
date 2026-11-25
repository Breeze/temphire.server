using System.Text.Json;
using Microsoft.Extensions.AI;
using TempHire.Server.Agent;
using Xunit;

namespace TempHire.Server.Tests;

/// <summary>
/// What the tools of an answer saved (<see cref="ToolResultChanges"/>), as Quinn's stream reports it
/// to the client: only what a tool result says under <c>changed</c>.
/// </summary>
public sealed class ToolResultChangesTests
{
    private static readonly Guid Address = Guid.NewGuid();
    private static readonly Guid Resource = Guid.NewGuid();

    [Fact]
    public void A_change_is_read_from_the_JSON_text_of_a_tool_result()
    {
        // As MCP hands a tool result to the agent: its text content is the tool's JSON.
        var text = JsonSerializer.Serialize(new
        {
            updated = true,
            changed = new[] { new { entityType = "Address", id = Address, staffingResourceId = Resource, summary = "Updated the address." } }
        });
        var result = new FunctionResultContent("call1", new { content = new[] { new { type = "text", text } } });

        Assert.Equal([new SavedEntity("Address", Address, Resource, "Updated the address.")], ToolResultChanges.In([result]));
    }

    [Fact]
    public void A_result_without_changes_reports_none()
    {
        var result = new FunctionResultContent("call1", new { updated = false, message = "The user cancelled the update." });

        Assert.Empty(ToolResultChanges.In([result]));
    }
}
