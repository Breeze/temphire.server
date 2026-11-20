using System.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.AI;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using TempHire.Server.Agent;
using TempHire.Server.Agent.Usage;
using TempHire.Server.Auth;

namespace TempHire.Server.Controllers
{
    /// <summary>
    /// Quinn's endpoint.  The client app sends the conversation so far and gets Quinn's answer, in
    /// Markdown.  The agent runs here, on the server, never in the browser: the model, its API key and
    /// the tools stay out of the client's reach.
    ///
    /// <para>
    /// The conversation lives in the client and is sent whole with each question, so the server keeps
    /// no chat state.  Only user and assistant messages are accepted; the system prompt is the
    /// server's alone.
    /// </para>
    /// </summary>
    [ApiController]
    [Authorize]
    [Route("api/agent/[action]")]
    public class AgentController : ControllerBase
    {
        private const int MaxMessages = 30;
        private const int MaxMessageLength = 4000;

        /// <summary>How the stream's updates are written: camelCase, without empty properties.</summary>
        private static readonly System.Text.Json.JsonSerializerOptions UpdateJson =
            new(System.Text.Json.JsonSerializerOptions.Web)
            {
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
            };

        private readonly QuinnAgent _agent;
        private readonly ChatClientFactory _chatClients;
        private readonly QuinnUsageStore _usage;
        private readonly ILogger<AgentController> _logger;

        public AgentController(QuinnAgent agent, ChatClientFactory chatClients, QuinnUsageStore usage, ILogger<AgentController> logger)
        {
            _agent = agent;
            _chatClients = chatClients;
            _usage = usage;
            _logger = logger;
        }

        /// <summary>Who answers: Quinn's name and the model, for the chat pane's header.</summary>
        [HttpGet]
        public ActionResult<AgentInfo> Info() => new AgentInfo
        {
            Name = QuinnPersona.Name,
            Model = _chatClients.Options.ModelLabel,
            CompanyName = CompanyName
        };

        /// <summary>
        /// Answers the last question of the conversation, streamed as newline-delimited JSON
        /// (<c>application/x-ndjson</c>): one <see cref="QuinnUpdate"/> per line - <c>text</c> pieces as
        /// the model writes them, the <c>people</c> the answer can mention as the tools return them,
        /// and <c>done</c> at the end.  Turning those people into links is up to the client.
        ///
        /// <para>
        /// Nothing is sent until the first piece is ready, so a failure to start - Ollama not
        /// running, no API key - still answers with an error status.  A failure after that can only
        /// be reported in the stream, as an <c>error</c> line.
        /// </para>
        /// <para>
        /// Quotas: a question is refused (429) while the user or their company has used up a daily,
        /// weekly or monthly token quota (<c>Agent:Quotas</c>).  Once a question is let through it is
        /// answered in full, so a quota can be overrun by the last question's tokens.  The tokens are
        /// recorded when the answer ends, however it ends - finished, failed or cancelled - since the
        /// model calls made so far have been paid for either way.
        /// </para>
        /// </summary>
        [HttpPost]
        public async Task<IActionResult> Chat([FromBody] ChatRequest request, CancellationToken cancellationToken)
        {
            if (UsageCaller.From(User) is not { } caller)
            {
                return Forbid();
            }

            // The model that will answer; its usage and quotas are its own.
            var model = new UsageModel(_chatClients.Options.CurrentProvider, _chatClients.Options.CurrentModel);

            var now = DateTimeOffset.UtcNow;
            var usage = await _usage.ReportAsync(caller.TenantId, caller.UserId, model.Provider, model.Model, now, cancellationToken);
            if (usage.Current?.Exceeded() is { } exceeded)
            {
                Response.Headers.RetryAfter = ((int)Math.Ceiling((exceeded.Period.ResetsAt - now).TotalSeconds)).ToString();
                return StatusCode(
                    StatusCodes.Status429TooManyRequests,
                    new ErrorResult { Message = QuotaMessages.Refusal(exceeded.Period, exceeded.IsCompany, usage.CompanyName, now) });
            }

            var conversation = (request?.Messages ?? [])
                .Where(m => !string.IsNullOrWhiteSpace(m?.Content))
                .TakeLast(MaxMessages)
                .Select(m => new ChatMessage(
                    m.Role == "assistant" ? ChatRole.Assistant : ChatRole.User,
                    m.Content.Length > MaxMessageLength ? m.Content[..MaxMessageLength] : m.Content))
                .ToList();

            if (conversation.Count == 0 || conversation[^1].Role != ChatRole.User)
            {
                return BadRequest(new ErrorResult { Message = "Ask Quinn a question." });
            }

            var tokens = new TokenTally();
            var updates = _agent.AskStreamingAsync(
                    conversation,
                    new Uri($"{Request.Scheme}://{Request.Host}/mcp"),
                    AccessToken,
                    CompanyName,
                    User.Identity?.Name ?? "a TempHire user",
                    TempHirePolicies.CanRead(User),
                    tokens,
                    cancellationToken)
                .GetAsyncEnumerator(cancellationToken);

            try
            {
                bool more;
                try
                {
                    more = await updates.MoveNextAsync();
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    return Unavailable(FailureMessage(e));
                }

                Response.ContentType = "application/x-ndjson; charset=utf-8";
                Response.Headers.CacheControl = "no-cache";

                try
                {
                    for (; more; more = await updates.MoveNextAsync())
                    {
                        await WriteLineAsync(updates.Current, cancellationToken);
                    }
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    await WriteLineAsync(QuinnUpdate.Error(FailureMessage(e)), cancellationToken);
                }

                return new EmptyResult();
            }
            finally
            {
                await updates.DisposeAsync();
                await RecordAsync(caller, model, now, tokens);
            }
        }

        /// <summary>
        /// Adds the question's tokens to the user's usage of the model.  Not cancellable: the request
        /// may already be cancelled (the user closed the pane), but the tokens were used.  A failure
        /// here is logged rather than shown: the user has their answer.
        /// </summary>
        private async Task RecordAsync(UsageCaller caller, UsageModel model, DateTimeOffset askedAt, TokenTally tokens)
        {
            if (tokens.Reports == 0)
            {
                // The model was never reached (Ollama not running, no API key) - or it reported no usage.
                return;
            }

            try
            {
                var cost = await _usage.RecordAsync(
                    caller.TenantId, caller.UserId, model.Provider, model.Model,
                    DateOnly.FromDateTime(askedAt.UtcDateTime), tokens, CancellationToken.None);

                _logger.LogInformation(
                    "Quinn used {InputTokens} input tokens ({CacheReadTokens} read from the cache, {CacheWriteTokens} written to it) and {OutputTokens} output tokens of {Provider} {Model} in {Reports} model calls, costing {Dollars} dollars",
                    tokens.InputTokens, tokens.CacheReadTokens, tokens.CacheWriteTokens, tokens.OutputTokens,
                    model.Provider, model.Model, tokens.Reports, Microdollars.ToDollars(cost));
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Quinn's token usage could not be recorded.");
            }
        }

        /// <summary>The provider and model a question is answered with.</summary>
        private record UsageModel(string Provider, string Model);

        /// <summary>Sends one update and flushes it, so the client sees it now.</summary>
        private async Task WriteLineAsync(QuinnUpdate update, CancellationToken cancellationToken)
        {
            await Response.WriteAsync(
                System.Text.Json.JsonSerializer.Serialize(update, UpdateJson) + "\n",
                cancellationToken);
            await Response.Body.FlushAsync(cancellationToken);
        }

        /// <summary>What to tell the user when Quinn cannot answer; the details go to the log.</summary>
        private string FailureMessage(Exception e)
        {
            switch (e)
            {
                case AgentNotConfiguredException:
                    return e.Message;

                case HttpRequestException when !_chatClients.Options.UsesClaude:
                    _logger.LogWarning(e, "Quinn could not reach Ollama.");
                    return $"Quinn can't reach Ollama at {_chatClients.Options.Ollama.Endpoint}. Is it running, " +
                           $"with the model downloaded (ollama pull {_chatClients.Options.Ollama.Model})?";

                default:
                    _logger.LogError(e, "Quinn failed to answer.");
                    return "Quinn ran into a problem answering that. Please try again.";
            }
        }

        private string CompanyName => User.FindFirst(HttpCurrentTenant.TenantNameClaim)?.Value ?? "your company";

        /// <summary>The caller's own token, passed on to the MCP endpoint.</summary>
        private string AccessToken =>
            Request.Headers.Authorization.ToString() is var header && header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                ? header["Bearer ".Length..].Trim()
                : null;

        private ObjectResult Unavailable(string message) =>
            StatusCode((int)HttpStatusCode.ServiceUnavailable, new ErrorResult { Message = message });
    }

    // camelCase for the same reason as the account DTOs: see the comment in AccountController.cs.

    [JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
    public class ChatRequest
    {
        public List<ChatRequestMessage> Messages { get; set; }
    }

    [JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
    public class ChatRequestMessage
    {
        /// <summary><c>user</c> or <c>assistant</c>.</summary>
        public string Role { get; set; }

        public string Content { get; set; }
    }

    [JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
    public class AgentInfo
    {
        public string Name { get; set; }
        public string Model { get; set; }
        public string CompanyName { get; set; }
    }
}
