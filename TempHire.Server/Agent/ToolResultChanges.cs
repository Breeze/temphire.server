using System.Text.Json;
using Microsoft.Extensions.AI;

namespace TempHire.Server.Agent;

/// <summary>
/// What the tools of an answer saved: the entities a tool reports in its result under
/// <c>changed</c>, such as the address <c>update_staffing_resource_address</c> updated.  Quinn's
/// stream sends them to the client, which refreshes what it shows of them, as after a save of its
/// own, and keeps each change's summary with the answer.
///
/// <para>
/// The summaries matter because the conversation comes back without the tool calls of earlier
/// answers: only their text.  An earlier answer that says "I've updated the address", with no
/// update in sight, looked to the model like something it had made up, and it took it back.  The
/// client sends the summaries back with that answer, and <c>AgentController</c> puts them in front
/// of it, so the model knows what was saved.
/// </para>
/// <para>
/// Taken from the tool results, never from the model: a tool reports a change only once it has
/// saved it, and nothing the model writes reaches the client as a change.
/// </para>
/// </summary>
public static class ToolResultChanges
{
    /// <summary>
    /// The changes reported in <paramref name="results"/>: each <c>changed</c> array's entries with
    /// an <c>entityType</c>, an <c>id</c> and a <c>staffingResourceId</c>, and a <c>summary</c> if
    /// the tool gave one.  The tool result's text
    /// content is itself JSON, as for <see cref="ToolResultPeople"/>.
    /// </summary>
    public static IReadOnlyList<SavedEntity> In(IEnumerable<FunctionResultContent> results)
    {
        var changes = new List<SavedEntity>();

        foreach (var result in results)
        {
            if (result.Result is null)
            {
                continue;
            }

            try
            {
                var json = result.Result is JsonElement element ? element : JsonSerializer.SerializeToElement(result.Result);
                Collect(json, changes, depth: 0);
            }
            catch (Exception e) when (e is JsonException or NotSupportedException)
            {
                // Not JSON: nothing was reported.
            }
        }

        return [.. changes.Distinct()];
    }

    private static void Collect(JsonElement json, List<SavedEntity> changes, int depth)
    {
        if (depth > 6)
        {
            return;
        }

        switch (json.ValueKind)
        {
            case JsonValueKind.Object:
                if (json.TryGetProperty("changed", out var changed) && changed.ValueKind == JsonValueKind.Array)
                {
                    foreach (var entry in changed.EnumerateArray())
                    {
                        if (Read(entry) is { } saved)
                        {
                            changes.Add(saved);
                        }
                    }
                }

                foreach (var property in json.EnumerateObject())
                {
                    if (property.Name != "changed")
                    {
                        Collect(property.Value, changes, depth + 1);
                    }
                }
                break;

            case JsonValueKind.Array:
                foreach (var item in json.EnumerateArray())
                {
                    Collect(item, changes, depth + 1);
                }
                break;

            case JsonValueKind.String:
                var text = json.GetString()!.TrimStart();
                if (text.StartsWith('{') || text.StartsWith('['))
                {
                    try
                    {
                        using var document = JsonDocument.Parse(text);
                        Collect(document.RootElement, changes, depth + 1);
                    }
                    catch (JsonException)
                    {
                        // A string that only looked like JSON.
                    }
                }
                break;
        }
    }

    private static SavedEntity Read(JsonElement entry) =>
        entry.ValueKind == JsonValueKind.Object
        && entry.TryGetProperty("entityType", out var type) && type.ValueKind == JsonValueKind.String
        && entry.TryGetProperty("id", out var id) && Guid.TryParse(id.GetString(), out var entityId)
        && entry.TryGetProperty("staffingResourceId", out var resource) && Guid.TryParse(resource.GetString(), out var resourceId)
            ? new SavedEntity(
                type.GetString(), entityId, resourceId,
                entry.TryGetProperty("summary", out var summary) && summary.ValueKind == JsonValueKind.String ? summary.GetString() : null)
            : null;
}

/// <summary>An entity a tool saved, the staffing resource it belongs to, and what was done, in a sentence.</summary>
public record SavedEntity(string EntityType, Guid Id, Guid StaffingResourceId, string Summary = null);
