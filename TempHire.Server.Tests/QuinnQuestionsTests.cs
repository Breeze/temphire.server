using TempHire.Server.Agent;
using Xunit;

namespace TempHire.Server.Tests;

/// <summary>
/// The questions Quinn's answer waits on (<see cref="QuinnQuestions"/>): who may answer them, with
/// what, and how often.
/// </summary>
public sealed class QuinnQuestionsTests
{
    private static readonly Guid Anna = Guid.NewGuid();
    private static readonly Guid Ben = Guid.NewGuid();

    private readonly QuinnQuestions _questions = new();

    [Fact]
    public async Task A_choice_is_answered_with_one_of_its_options_by_the_user_asked()
    {
        var (id, answer) = _questions.Open(Anna, "choice", new HashSet<string> { "a", "b" }, CancellationToken.None);

        Assert.False(_questions.Answer(id, Ben, true, "a"));
        Assert.False(_questions.Answer(id, Anna, true, "c"));
        Assert.False(answer.IsCompleted);

        Assert.True(_questions.Answer(id, Anna, true, "b"));
        var result = await answer;
        Assert.True(result.IsAccepted);
        Assert.Equal("b", result.Content!["choice"].GetString());

        // Once.
        Assert.False(_questions.Answer(id, Anna, true, "a"));
    }

    [Fact]
    public async Task A_confirmation_is_accepted_or_cancelled()
    {
        var (confirmed, accepted) = _questions.Open(Anna, null, new HashSet<string>(), CancellationToken.None);
        var (cancelled, declined) = _questions.Open(Anna, null, new HashSet<string>(), CancellationToken.None);

        Assert.True(_questions.Answer(confirmed, Anna, true, null));
        Assert.True(_questions.Answer(cancelled, Anna, false, null));

        Assert.True((await accepted).IsAccepted);
        Assert.Equal("cancel", (await declined).Action);
    }

    [Fact]
    public async Task A_question_whose_stream_ends_is_cancelled_and_can_no_longer_be_answered()
    {
        using var stream = new CancellationTokenSource();
        var (id, answer) = _questions.Open(Anna, null, new HashSet<string>(), stream.Token);

        await stream.CancelAsync();

        Assert.Equal("cancel", (await answer).Action);
        Assert.False(_questions.Answer(id, Anna, true, null));
    }
}
