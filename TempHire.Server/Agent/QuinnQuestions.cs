using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using ModelContextProtocol.Protocol;

namespace TempHire.Server.Agent;

/// <summary>
/// The questions a tool is asking the user through Quinn, waiting for their answer.
///
/// <para>
/// A tool that needs the user - pick an address, confirm a change - asks through MCP elicitation
/// (<c>Mcp/UserInput.cs</c>).  Quinn's agent is the MCP client: it puts the question in the
/// answer stream, where the chat pane shows it as a card, and waits here for the user's click,
/// which the client posts to <c>api/agent/answer</c>.  The answer then goes back to the tool.
/// </para>
/// <para>
/// A question can only be answered by the user it was asked of, once, with one of the choices it
/// offered.  It is forgotten when it is answered, when the answer stream ends, or after
/// <see cref="AnswerTime"/>.  Held in memory: an answer must reach the server instance that is
/// streaming the question, which is the only instance in TempHire.  Several instances behind a load
/// balancer would need session affinity for <c>api/agent</c>, or a shared store here.
/// </para>
/// </summary>
public sealed class QuinnQuestions
{
    /// <summary>How long a question waits for its answer; then the tool hears that the user cancelled.</summary>
    public static readonly TimeSpan AnswerTime = TimeSpan.FromMinutes(10);

    private readonly ConcurrentDictionary<string, Pending> _pending = new();

    private sealed record Pending(Guid UserId, string Field, IReadOnlySet<string> Choices, TaskCompletionSource<ElicitResult> Answer);

    /// <summary>
    /// Opens a question for <paramref name="userId"/>: its id, for the client, and the answer, once
    /// given.  The answer is "cancel" if the user doesn't answer in time, or if
    /// <paramref name="cancellationToken"/> is cancelled (the user closed the pane).
    /// </summary>
    /// <param name="userId">The user asked, the only one who can answer.</param>
    /// <param name="field">The form field a choice is made in; null for a confirmation.</param>
    /// <param name="choices">The values the user may choose; empty for a confirmation.</param>
    /// <param name="cancellationToken">Cancelled when the answer stream ends.</param>
    public (string Id, Task<ElicitResult> Answer) Open(
        Guid userId, string field, IReadOnlySet<string> choices, CancellationToken cancellationToken)
    {
        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var pending = new Pending(userId, field, choices, new TaskCompletionSource<ElicitResult>(TaskCreationOptions.RunContinuationsAsynchronously));
        _pending[id] = pending;

        return (id, WaitAsync(id, pending, cancellationToken));
    }

    private async Task<ElicitResult> WaitAsync(string id, Pending pending, CancellationToken cancellationToken)
    {
        try
        {
            return await pending.Answer.Task.WaitAsync(AnswerTime, cancellationToken);
        }
        catch (Exception e) when (e is TimeoutException or OperationCanceledException)
        {
            return new ElicitResult { Action = "cancel" };
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    /// <summary>
    /// The user's answer: accepted with <paramref name="choice"/> (or, for a confirmation, without),
    /// or cancelled.  False if there is no such question for this user - answered already, expired,
    /// someone else's - or the choice isn't one it offered.
    /// </summary>
    public bool Answer(string id, Guid userId, bool accept, string choice)
    {
        if (id is null || !_pending.TryGetValue(id, out var pending) || pending.UserId != userId)
        {
            return false;
        }

        ElicitResult result;
        if (!accept)
        {
            result = new ElicitResult { Action = "cancel" };
        }
        else if (pending.Field is null)
        {
            result = new ElicitResult { Action = "accept", Content = new Dictionary<string, JsonElement>() };
        }
        else if (choice is not null && pending.Choices.Contains(choice))
        {
            result = new ElicitResult
            {
                Action = "accept",
                Content = new Dictionary<string, JsonElement> { [pending.Field] = JsonSerializer.SerializeToElement(choice) }
            };
        }
        else
        {
            return false;
        }

        return _pending.TryRemove(id, out _) && pending.Answer.TrySetResult(result);
    }
}
