using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace TempHire.Server.Mcp;

/// <summary>
/// Asks the user something in the middle of a tool call: pick one of several choices, or confirm a
/// change.  The question goes to the user, not to the model, so the model can neither answer it nor
/// skip it.
/// </summary>
public interface IUserInput
{
    /// <summary>
    /// The user's answer to <paramref name="question"/>, once they have given it.  Until then, this
    /// throws to send the question, and the tool call is made again with the answer.
    /// </summary>
    /// <param name="key">Names the question among those of the tool.</param>
    /// <param name="question">What to ask: a message, and the choices as a form (none for a confirmation).</param>
    /// <param name="fingerprint">
    /// What the question is about, such as the address and the change.  An answer only counts for the
    /// question it was given to: if anything here differs when the call is made again (the address
    /// changed meanwhile), the question is asked again.
    /// </param>
    ElicitResult Ask(string key, ElicitRequestParams question, string fingerprint);
}

/// <summary>
/// <see cref="IUserInput"/> over MCP elicitation, the way a stateless server asks: multi round-trip
/// requests (MRTR, the <c>2026-07-28</c> protocol revision).
///
/// <para>
/// The first call of the tool throws <see cref="InputRequiredException"/>: the result tells the
/// client what to ask, and carries a request state that the client sends back, unread, with the
/// user's answer when it calls the tool again.  The tool then runs again from the start with the
/// same arguments, and this time <see cref="Ask"/> returns the answer.  Nothing is kept on the
/// server between the two calls.
/// </para>
/// <para>
/// The request state is the question's fingerprint, protected (signed and encrypted) with ASP.NET
/// Core Data Protection, for this user, for 15 minutes.  So an answer counts only if it comes with
/// the state of the same question, asked of the same user, recently: a client cannot answer a
/// question it was never asked, or reuse an old answer for a different change.  (The client is
/// Quinn's agent, or another MCP client acting with the user's own token; this keeps the tools'
/// rule - nothing changes without the user's answer - in the tools, whatever the client does.)
/// </para>
/// </summary>
public sealed class McpUserInput(McpServer server, RequestContext<CallToolRequestParams> context, IDataProtectionProvider protection)
    : IUserInput
{
    private static readonly TimeSpan AnswerTime = TimeSpan.FromMinutes(15);

    private readonly ITimeLimitedDataProtector _protector =
        protection.CreateProtector("TempHire.Mcp.UserInput", UserOf(context)).ToTimeLimitedDataProtector();

    /// <summary>
    /// Whether the client can ask the user at all.  A client without MRTR (an older MCP client)
    /// would get a result it doesn't understand, so the tools refuse to change anything for it.
    /// </summary>
    public bool Supported => server.IsMrtrSupported;

    public ElicitResult Ask(string key, ElicitRequestParams question, string fingerprint)
    {
        var expected = Hash(key, fingerprint);
        if (context.Params?.InputResponses is { } responses
            && responses.TryGetValue(key, out var response)
            && Unprotect(context.Params.RequestState) == expected
            && response.Deserialize(InputResponse.ElicitResultJsonTypeInfo) is { } answer)
        {
            return answer;
        }

        throw new InputRequiredException(
            new Dictionary<string, InputRequest> { [key] = InputRequest.ForElicitation(question) },
            _protector.Protect(expected, AnswerTime));
    }

    private string Unprotect(string state)
    {
        if (string.IsNullOrEmpty(state))
        {
            return null;
        }

        try
        {
            return _protector.Unprotect(state);
        }
        catch (CryptographicException)
        {
            // Tampered with, from another user or expired: ask again.
            return null;
        }
    }

    private static string Hash(string key, string fingerprint) =>
        Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(key + "\n" + fingerprint)));

    private static string UserOf(RequestContext<CallToolRequestParams> context) =>
        context.User?.FindFirst("sub")?.Value ?? context.User?.Identity?.Name ?? "";

    /// <summary>A fingerprint of several values, for <see cref="IUserInput.Ask"/>.</summary>
    public static string Fingerprint(params object[] values) => JsonSerializer.Serialize(values);
}
