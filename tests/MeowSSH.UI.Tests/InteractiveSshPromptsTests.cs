using MeowSSH.Core.Ssh;
using MeowSSH.UI.Services;

namespace MeowSSH.UI.Tests;

/// <summary>
/// The questions a handshake asks, and how they reach a person.
/// </summary>
/// <remarks>
/// No browser: these are about what the engine gets back, which is where the
/// bugs were. The integration suite proves the host-key path against a real
/// server; keyboard-interactive it cannot, because the test server authenticates
/// with the password method and offering the other needs PAM.
/// </remarks>
public class InteractiveSshPromptsTests
{
    private static KeyboardInteractivePrompt Challenge(params string[] questions) =>
        new("Login", "Enter your credentials", questions, [.. questions.Select(_ => false)]);

    [Fact]
    public async Task AChallengeWithNoQuestionsIsAnsweredWithoutAskingAnyone()
    {
        // Servers probe with an empty challenge to discover what the client
        // supports. Putting an empty dialog on screen for that would be
        // baffling, and the correct answer is simply no answers.
        using var prompts = new InteractiveSshPrompts();

        var answers = await prompts.AnswerChallengeAsync(Challenge());

        Assert.NotNull(answers);
        Assert.Empty(answers);
        Assert.Null(prompts.PendingChallenge);
    }

    [Fact]
    public async Task EveryQuestionGetsAnAnswer()
    {
        // The bug this replaces: declining returned null, the engine turned that
        // into an empty array, and the server refused with "incorrect number of
        // answers from keyboard-interactive callback 0 (expected 1)". A short
        // list is malformed, not wrong.
        using var prompts = new InteractiveSshPrompts();

        var asking = prompts.AnswerChallengeAsync(Challenge("Password: ", "One-time code: "));
        var pending = await WaitForAsync(() => prompts.PendingChallenge);

        pending.Answers[0] = "hunter2";
        pending.Answers[1] = "123456";
        pending.Answer([.. pending.Answers]);

        var answers = await asking;
        Assert.Equal(["hunter2", "123456"], answers);
    }

    [Fact]
    public async Task CancellingAChallengeAnswersNothingRatherThanBlanks()
    {
        // Blank answers are an attempt, and the server counts them as a failed
        // one. Declining has to be distinguishable from getting it wrong.
        using var prompts = new InteractiveSshPrompts();

        var asking = prompts.AnswerChallengeAsync(Challenge("Password: "));
        var pending = await WaitForAsync(() => prompts.PendingChallenge);
        pending.Answer(null);

        Assert.Null(await asking);
    }

    [Fact]
    public async Task AQuestionIsWithdrawnOnceItIsAnswered()
    {
        // Left behind, the sheet would stay on screen over the session it just
        // let the user into.
        using var prompts = new InteractiveSshPrompts();

        var asking = prompts.AnswerChallengeAsync(Challenge("Password: "));
        var pending = await WaitForAsync(() => prompts.PendingChallenge);
        pending.Answer(["x"]);
        await asking;

        Assert.Null(prompts.PendingChallenge);
    }

    [Fact]
    public async Task AnUnknownHostKeyIsNotTrustedWhenTheConnectionIsCancelled()
    {
        // The default has to be refusal. Defaulting the other way would trust a
        // key nobody looked at.
        using var prompts = new InteractiveSshPrompts();
        using var cancellation = new CancellationTokenSource();

        var asking = prompts.ConfirmUnknownHostKeyAsync(
            new HostKeyPrompt("example", "SHA256:abc", "ssh-ed25519"), cancellation.Token);

        await WaitForAsync(() => prompts.PendingHostKey);
        await cancellation.CancelAsync();

        Assert.False(await asking);
    }

    [Fact]
    public async Task OnlyOneQuestionIsOnScreenAtATime()
    {
        // Two racing for the same sheet would leave one invisible and
        // unanswerable, and the handshake behind it hanging.
        using var prompts = new InteractiveSshPrompts();

        var first = prompts.AnswerChallengeAsync(Challenge("First: "));
        var pending = await WaitForAsync(() => prompts.PendingChallenge);

        var second = prompts.RequestPasswordAsync("Second: ");
        Assert.Null(prompts.PendingPassword);

        pending.Answer(["done"]);
        await first;

        // Released only once the first is answered.
        var password = await WaitForAsync(() => prompts.PendingPassword);
        password.Answer(null);
        (await second)?.Dispose();
    }

    /// <summary>
    /// Waits for a question to appear.
    /// </summary>
    /// <remarks>
    /// The call that raises it runs on another thread, so the property is not
    /// set by the time the task is handed back. Polling rather than a signal
    /// keeps the production type free of test-only plumbing.
    /// </remarks>
    private static async Task<T> WaitForAsync<T>(Func<T?> read) where T : class
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            if (read() is { } value) return value;
            await Task.Delay(10);
        }

        throw new TimeoutException("the prompt never appeared");
    }
}
