using Anthropic;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using OllamaSharp;

namespace TempHire.Server.Agent;

/// <summary>
/// Builds the chat client Quinn talks to, for the configured provider.
///
/// <para>
/// Both providers are used through <see cref="IChatClient"/> from Microsoft.Extensions.AI, so the
/// rest of the agent does not know which one it has.  <c>UseFunctionInvocation</c> adds the tool
/// loop: when the model asks for a tool, the client calls it (here: an MCP tool) and sends the
/// result back, until the model answers in text.  That is the loop the RAG example wrote by hand
/// in Part 4 and got from the Vercel AI SDK in Part 5.
/// </para>
/// <para>A singleton: the underlying clients hold an HttpClient and are safe to share.</para>
/// </summary>
public class ChatClientFactory : IDisposable
{
    private readonly AgentOptions _options;
    private readonly Lazy<IChatClient> _client;

    public ChatClientFactory(IOptions<AgentOptions> options)
    {
        _options = options.Value;
        _client = new Lazy<IChatClient>(Build);
    }

    public AgentOptions Options => _options;

    /// <summary>The chat client with the tool loop.  Throws <see cref="AgentNotConfiguredException"/>.</summary>
    public IChatClient Client => _client.Value;

    /// <summary>
    /// The options for one question: the tools, and with Claude, prompt caching for the
    /// conversation.
    ///
    /// <para>
    /// A question is several model calls - one per round of the tool loop - and each sends
    /// everything so far again: the tools, the system prompt, the conversation, and the tool calls
    /// and results of this question.  Claude's automatic caching (a top-level
    /// <c>cache_control</c>) caches up to the end of each request, so the next round, and the next
    /// question within a few minutes, read all of that from the cache and pay full price only for
    /// what is new.  The breakpoint on the system prompt (<see cref="QuinnAgent"/>) is the second,
    /// fixed read point.  Ollama runs locally and has no such cache, so it gets the tools only.
    /// </para>
    /// </summary>
    public ChatOptions OptionsFor(IList<AITool> tools)
    {
        var options = new ChatOptions { Tools = tools };
        if (_options.UsesClaude)
        {
            // The Anthropic client starts each request from these parameters and adds the
            // conversation, the system prompt and the tools.  It takes the model and the output
            // limit from here as they are, so they repeat the client's configuration.
            options.RawRepresentationFactory = _ => new Anthropic.Models.Messages.MessageCreateParams
            {
                Model = _options.Claude.Model,
                MaxTokens = _options.Claude.MaxTokens,
                Messages = [],
                CacheControl = new Anthropic.Models.Messages.CacheControlEphemeral()
            };
        }

        return options;
    }

    private IChatClient Build()
    {
        IChatClient model;

        if (_options.UsesClaude)
        {
            if (string.IsNullOrWhiteSpace(_options.Claude.ApiKey))
            {
                throw new AgentNotConfiguredException(
                    "Quinn is set up to use Claude, but there is no API key. " +
                    "Run server\\Set-ClaudeApiKey.ps1 to store one, then restart the server.");
            }

            model = new AnthropicClient { ApiKey = _options.Claude.ApiKey }
                .AsIChatClient(_options.Claude.Model, _options.Claude.MaxTokens);
        }
        else
        {
            model = new OllamaApiClient(new Uri(_options.Ollama.Endpoint), _options.Ollama.Model);
        }

        return new ChatClientBuilder(model)
            .UseFunctionInvocation(configure: loop => loop.MaximumIterationsPerRequest = _options.MaxToolCalls)
            .Build();
    }

    public void Dispose()
    {
        if (_client.IsValueCreated)
        {
            _client.Value.Dispose();
        }
    }
}

/// <summary>The agent cannot run as configured; the message says what to do.</summary>
public class AgentNotConfiguredException(string message) : InvalidOperationException(message)
{
}
