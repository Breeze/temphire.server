using System.Threading.Channels;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace TempHire.Server.Agent;

/// <summary>
/// Quinn: answers one question, using the language model and the MCP server's tools, and streams
/// the answer as it is written.
///
/// <para>
/// The tools come from this same server's MCP endpoint (<c>/mcp</c>), called over HTTP with the
/// <b>user's own access token</b>.  So the agent has no special access: the MCP endpoint checks
/// the token like any other request, and the tools see only the user's tenant.  The model never
/// sees the token or the tenant id.
/// </para>
/// <para>
/// Going over HTTP to its own server costs a little, but it keeps one security boundary: the MCP
/// endpoint.  The same endpoint serves other MCP clients, such as Claude Code, with the same rules.
/// </para>
/// </summary>
public class QuinnAgent(
    ChatClientFactory chatClients,
    IHttpClientFactory httpClients,
    QuinnQuestions questions,
    ILoggerFactory loggerFactory)
{
    private readonly ChatClientFactory _chatClients = chatClients;
    private readonly IHttpClientFactory _httpClients = httpClients;
    private readonly QuinnQuestions _questions = questions;
    private readonly ILoggerFactory _loggerFactory = loggerFactory;
    private readonly ILogger<QuinnAgent> _logger = loggerFactory.CreateLogger<QuinnAgent>();

    /// <summary>
    /// Answers as a stream: the text as the model writes it, piece by piece, the people the
    /// answer can mention - see <see cref="ToolResultPeople"/> - each time a tool returns more of
    /// them, and the questions a tool asks the user.  The tools run before the model writes its
    /// answer, so the people normally arrive before the text that names them.
    ///
    /// <para>
    /// A question - pick an address, confirm a change - comes from a tool, through MCP elicitation,
    /// while the tool call waits for the answer: an <c>input</c> update with the question and its
    /// choices, which the chat pane shows, and whose answer it posts to <c>api/agent/answer</c>
    /// (<see cref="QuinnQuestions"/>).  The model is not involved: it sees only the tool's result.
    /// So the model's stream runs in a task of its own, and both it and the questions write to one
    /// channel, which this reads.
    /// </para>
    /// <para>
    /// The tokens the model reports go into <paramref name="tokens"/> as they arrive: one usage
    /// report per model call, and a question can take several (the tool loop).  The caller records
    /// them when the answer ends, however it ends.
    /// </para>
    /// </summary>
    public async IAsyncEnumerable<QuinnUpdate> AskStreamingAsync(
        IReadOnlyList<ChatMessage> conversation,
        Uri mcpEndpoint,
        string accessToken,
        Guid userId,
        string companyName,
        string userName,
        bool canReadStaffingData,
        bool canEditStaffingData,
        Usage.TokenTally tokens,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var updates = Channel.CreateUnbounded<QuinnUpdate>(new UnboundedChannelOptions { SingleReader = true });

        var answering = AnswerAsync(
            conversation, mcpEndpoint, accessToken, userId, companyName, userName,
            canReadStaffingData, canEditStaffingData, tokens, updates.Writer, stop.Token);

        try
        {
            await foreach (var update in updates.Reader.ReadAllAsync(cancellationToken))
            {
                yield return update;
            }

            // The model's failure, if it failed: rethrown as it was, so the caller can tell the user why.
            await answering;
        }
        finally
        {
            // The caller stopped reading (the user closed the pane): stop the model and any question.
            await stop.CancelAsync();
            await answering.ContinueWith(_ => { }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    /// <summary>The model's answer, written to <paramref name="updates"/>, which this completes.</summary>
    private async Task AnswerAsync(
        IReadOnlyList<ChatMessage> conversation,
        Uri mcpEndpoint,
        string accessToken,
        Guid userId,
        string companyName,
        string userName,
        bool canReadStaffingData,
        bool canEditStaffingData,
        Usage.TokenTally tokens,
        ChannelWriter<QuinnUpdate> updates,
        CancellationToken cancellationToken)
    {
        // Off the caller's thread: the first await below may complete synchronously.
        await Task.Yield();
        try
        {
            await using var mcp = await ConnectAsync(mcpEndpoint, accessToken, userId, updates, cancellationToken);
            var tools = await mcp.ListToolsAsync(cancellationToken: cancellationToken);

            List<FunctionResultContent> results = [];
            await foreach (var update in _chatClients.Client.GetStreamingResponseAsync(
                Messages(conversation, companyName, userName, canReadStaffingData, canEditStaffingData),
                _chatClients.OptionsFor([.. tools]),
                cancellationToken))
            {
                // The model's token counts: a streamed response reports them as UsageContent, and
                // the tool loop passes on each model call's.
                foreach (var usage in update.Contents.OfType<UsageContent>())
                {
                    tokens.Add(usage.Details);
                    _logger.LogDebug(
                        "Quinn model usage: {InputTokens} in ({CacheReadTokens} read from the cache, {CacheWriteTokens} written to it), {OutputTokens} out",
                        usage.Details.InputTokenCount, usage.Details.CachedInputTokenCount ?? 0,
                        usage.Details.AdditionalCounts?.GetValueOrDefault(Usage.TokenTally.CacheWriteCount) ?? 0,
                        usage.Details.OutputTokenCount);
                }

                // A tool result: work out the people again from all results so far, since a name
                // that fit one person can fit two once another tool has answered.
                var newResults = update.Contents.OfType<FunctionResultContent>().ToList();
                if (newResults.Count > 0)
                {
                    results.AddRange(newResults);
                    updates.TryWrite(QuinnUpdate.PeopleFound(ToolResultPeople.In(results)));

                    // A tool saved something: the client refreshes what it shows of it.
                    if (ToolResultChanges.In(newResults) is { Count: > 0 } saved)
                    {
                        updates.TryWrite(QuinnUpdate.Saved(saved));
                    }
                }

                if (update.Role != ChatRole.Tool && !string.IsNullOrEmpty(update.Text))
                {
                    updates.TryWrite(QuinnUpdate.Text(update.Text));
                }
            }

            updates.TryWrite(QuinnUpdate.Done());
        }
        finally
        {
            updates.TryComplete();
        }
    }

    /// <summary>
    /// Connects to the MCP endpoint as the user, ready to ask them what a tool asks.  The caller
    /// disposes the client when the answer is done, which closes the connection.
    /// </summary>
    private async Task<McpClient> ConnectAsync(
        Uri mcpEndpoint, string accessToken, Guid userId, ChannelWriter<QuinnUpdate> updates, CancellationToken cancellationToken)
    {
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = mcpEndpoint,
                Name = "TempHire",
                AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {accessToken}" }
            },
            _httpClients.CreateClient(nameof(QuinnAgent)),
            _loggerFactory);

        var options = new McpClientOptions
        {
            Handlers = new McpClientHandlers
            {
                ElicitationHandler = (request, token) => AskUserAsync(request, userId, updates, token)
            }
        };

        return await McpClient.CreateAsync(transport, options, _loggerFactory, cancellationToken);
    }

    /// <summary>
    /// A tool's question for the user: shown in the chat pane, answered there.  The pane knows two
    /// kinds, those the tools ask: a confirmation (a form without fields) and a choice (one field,
    /// a choice among titled options).  Anything else is declined, as the user can't answer it.
    /// </summary>
    private async ValueTask<ElicitResult> AskUserAsync(
        ElicitRequestParams request, Guid userId, ChannelWriter<QuinnUpdate> updates, CancellationToken cancellationToken)
    {
        var fields = request?.RequestedSchema?.Properties
                     ?? new Dictionary<string, ElicitRequestParams.PrimitiveSchemaDefinition>();
        var field = fields.Count == 1 ? fields.Single() : default;
        if (request is null
            || request.Mode is not (null or "form")
            || fields.Count > 1
            || (fields.Count == 1 && field.Value is not ElicitRequestParams.TitledSingleSelectEnumSchema))
        {
            _logger.LogWarning("Quinn declined a question it can't show the user.");
            return new ElicitResult { Action = "decline" };
        }

        var choices = (field.Value as ElicitRequestParams.TitledSingleSelectEnumSchema)?.OneOf
            .Select(o => new QuinnChoice(o.Const, o.Title)).ToList();

        var (id, answer) = _questions.Open(
            userId, field.Key, (choices ?? []).Select(c => c.Value).ToHashSet(), cancellationToken);
        updates.TryWrite(QuinnUpdate.Input(id, request.Message, choices));

        var result = await answer;
        if (!result.IsAccepted)
        {
            // Cancelled, not answered in time, or the stream is ending: the pane closes the question.
            updates.TryWrite(QuinnUpdate.InputClosed(id));
        }

        return result;
    }

    /// <summary>
    /// The system prompt, then the conversation so far.
    ///
    /// <para>
    /// The system prompt carries a prompt-cache breakpoint (Claude only; other clients ignore
    /// it).  The tools and the system prompt come first in every request and stay the same for a
    /// user all day, so each of the user's questions reads them from the cache, however the
    /// conversation goes.  See <see cref="ChatClientFactory.OptionsFor"/> for the conversation's
    /// own breakpoint.
    /// </para>
    /// </summary>
    private static List<ChatMessage> Messages(
        IReadOnlyList<ChatMessage> conversation, string companyName, string userName,
        bool canReadStaffingData, bool canEditStaffingData)
    {
        var systemPrompt = new TextContent(
                QuinnPersona.SystemPrompt(companyName, userName, DateTime.Today, canReadStaffingData, canEditStaffingData))
            .WithCacheControl(new Anthropic.Models.Messages.CacheControlEphemeral());

        var messages = new List<ChatMessage> { new(ChatRole.System, [systemPrompt]) };
        messages.AddRange(conversation);
        return messages;
    }
}

/// <summary>
/// One piece of a streamed answer: some <c>text</c> as the model writes it, the <c>people</c> the
/// answer can mention, a question for the user (<c>input</c>, and <c>inputClosed</c> when it
/// can no longer be answered), what a tool <c>saved</c>, <c>done</c> at the end, or an
/// <c>error</c>.  Sent to the client as
/// one JSON object per line; properties without a value are left out.
/// </summary>
public record QuinnUpdate(
    string Type,
    string Content = null,
    IReadOnlyList<MentionedPerson> People = null,
    string Id = null,
    IReadOnlyList<QuinnChoice> Choices = null,
    IReadOnlyList<SavedEntity> Changes = null)
{
    public static QuinnUpdate Text(string text) => new("text", text);

    /// <summary>All the people the answer can mention so far; replaces any earlier list.</summary>
    public static QuinnUpdate PeopleFound(IReadOnlyDictionary<string, Guid> people) =>
        new("people", People: [.. people.Select(p => new MentionedPerson(p.Key, p.Value))]);

    /// <summary>
    /// A question for the user, in Markdown, to answer at <c>api/agent/answer</c> with its id: a
    /// choice among <paramref name="choices"/> (OK or Cancel), or with none, a confirmation
    /// (Confirm or Cancel).
    /// </summary>
    public static QuinnUpdate Input(string id, string message, IReadOnlyList<QuinnChoice> choices) =>
        new("input", message, Id: id, Choices: choices);

    /// <summary>The question can no longer be answered: it timed out, or the stream is ending.</summary>
    public static QuinnUpdate InputClosed(string id) => new("inputClosed", Id: id);

    /// <summary>
    /// What a tool saved, such as the address Quinn changed: the client refreshes what it shows of
    /// those entities, as after a save of its own (see <see cref="ToolResultChanges"/>).
    /// </summary>
    public static QuinnUpdate Saved(IReadOnlyList<SavedEntity> changes) => new("saved", Changes: changes);

    public static QuinnUpdate Done() => new("done");

    public static QuinnUpdate Error(string message) => new("error", message);
}

/// <summary>A name in the answer, and the staffing resource it stands for.</summary>
public record MentionedPerson(string Name, Guid Id);

/// <summary>One of the choices of a question: the value to answer with, and what the user sees.</summary>
public record QuinnChoice(string Value, string Label);
