using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;

namespace TempHire.Server.Agent
{
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
    public class QuinnAgent
    {
        private readonly ChatClientFactory _chatClients;
        private readonly IHttpClientFactory _httpClients;
        private readonly ILoggerFactory _loggerFactory;
        private readonly ILogger<QuinnAgent> _logger;

        public QuinnAgent(ChatClientFactory chatClients, IHttpClientFactory httpClients, ILoggerFactory loggerFactory)
        {
            _chatClients = chatClients;
            _httpClients = httpClients;
            _loggerFactory = loggerFactory;
            _logger = loggerFactory.CreateLogger<QuinnAgent>();
        }

        /// <summary>
        /// Answers as a stream: the text as the model writes it, piece by piece, and the people the
        /// answer can mention - see <see cref="ToolResultPeople"/> - each time a tool returns more of
        /// them.  The tools run before the model writes its answer, so the people normally arrive
        /// before the text that names them, and nothing streams while the tools run.
        ///
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
            string companyName,
            string userName,
            bool canReadStaffingData,
            Usage.TokenTally tokens,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await using var mcp = await ConnectAsync(mcpEndpoint, accessToken, cancellationToken);
            var tools = await mcp.ListToolsAsync(cancellationToken: cancellationToken);

            var results = new List<FunctionResultContent>();
            await foreach (var update in _chatClients.Client.GetStreamingResponseAsync(
                Messages(conversation, companyName, userName, canReadStaffingData),
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
                    yield return QuinnUpdate.PeopleFound(ToolResultPeople.In(results));
                }

                if (update.Role != ChatRole.Tool && !string.IsNullOrEmpty(update.Text))
                {
                    yield return QuinnUpdate.Text(update.Text);
                }
            }

            yield return QuinnUpdate.Done();
        }

        /// <summary>
        /// Connects to the MCP endpoint as the user.  The caller disposes the client when the answer
        /// is done, which closes the connection.
        /// </summary>
        private async Task<McpClient> ConnectAsync(Uri mcpEndpoint, string accessToken, CancellationToken cancellationToken)
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

            return await McpClient.CreateAsync(transport, loggerFactory: _loggerFactory, cancellationToken: cancellationToken);
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
            IReadOnlyList<ChatMessage> conversation, string companyName, string userName, bool canReadStaffingData)
        {
            var systemPrompt = new TextContent(
                    QuinnPersona.SystemPrompt(companyName, userName, DateTime.Today, canReadStaffingData))
                .WithCacheControl(new Anthropic.Models.Messages.CacheControlEphemeral());

            var messages = new List<ChatMessage> { new(ChatRole.System, [systemPrompt]) };
            messages.AddRange(conversation);
            return messages;
        }
    }

    /// <summary>
    /// One piece of a streamed answer: some <c>text</c> as the model writes it, the <c>people</c> the
    /// answer can mention, <c>done</c> at the end, or an <c>error</c>.  Sent to the client as one JSON
    /// object per line; properties without a value are left out.
    /// </summary>
    public record QuinnUpdate(string Type, string Content = null, IReadOnlyList<MentionedPerson> People = null)
    {
        public static QuinnUpdate Text(string text) => new("text", text);

        /// <summary>All the people the answer can mention so far; replaces any earlier list.</summary>
        public static QuinnUpdate PeopleFound(IReadOnlyDictionary<string, Guid> people) =>
            new("people", People: [.. people.Select(p => new MentionedPerson(p.Key, p.Value))]);

        public static QuinnUpdate Done() => new("done");

        public static QuinnUpdate Error(string message) => new("error", message);
    }

    /// <summary>A name in the answer, and the staffing resource it stands for.</summary>
    public record MentionedPerson(string Name, Guid Id);
}
