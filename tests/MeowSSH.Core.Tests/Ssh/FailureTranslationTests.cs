using Meowshell;
using MeowSSH.Core.Ssh;

namespace MeowSSH.Core.Tests.Ssh;

public class FailureTranslationTests
{
    // (message, exitCode, stderr, code) -- the exit code and stderr are what the
    // agent process reported and are not what this translation is about.
    private static SshException Translate(MeowshellErrorCode code, string message, string diagnostics = "") =>
        MeowshellSshEngine.Translate(new TailcatException(message, 1, diagnostics, code));

    [Theory]
    [InlineData(MeowshellErrorCode.AuthFailed, SshFailure.AuthenticationFailed)]
    [InlineData(MeowshellErrorCode.HostKeyUnknown, SshFailure.HostKeyUnknown)]
    [InlineData(MeowshellErrorCode.HostKeyChanged, SshFailure.HostKeyChanged)]
    [InlineData(MeowshellErrorCode.NetworkUnreachable, SshFailure.NetworkUnreachable)]
    [InlineData(MeowshellErrorCode.Timeout, SshFailure.Timeout)]
    public void ATypedReasonKeepsItsMeaning(MeowshellErrorCode code, SshFailure expected)
    {
        Assert.Equal(expected, Translate(code, "raw engine diagnostics").Failure);
    }

    [Theory]
    [InlineData(MeowshellErrorCode.AuthFailed)]
    [InlineData(MeowshellErrorCode.HostKeyChanged)]
    public void ARecognisedReasonIsRewrittenForAPerson(MeowshellErrorCode code)
    {
        // The engine's diagnostics are useful in a log and alarming in a dialog,
        // so a code MeowSSH understands gets words MeowSSH chose.
        var message = Translate(code, "ssh: handshake failed: EOF").Message;

        Assert.DoesNotContain("handshake failed", message, StringComparison.Ordinal);
        Assert.NotEmpty(message);
    }

    [Fact]
    public void AnUnrecognisedReasonPassesTheEnginesOwnWordsThrough()
    {
        // This is the case that sent a real debugging session in circles: the
        // fallback said "The connection failed." and discarded the only
        // description of what actually went wrong.
        var message = Translate(MeowshellErrorCode.None, "dial tcp 100.64.0.3:22: connect: network is unreachable").Message;

        Assert.Contains("network is unreachable", message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnrecognisedReasonWithNothingToSayStillSaysSomething()
    {
        Assert.Equal("The connection failed.", Translate(MeowshellErrorCode.None, "").Message);
    }

    [Fact]
    public void TheAgentsOwnOutputIsPreferredToTheWrapperMessage()
    {
        // A failure with no typed reason is one this app did not anticipate, so
        // the process that did the work is the only thing that knows why.
        var message = Translate(
            MeowshellErrorCode.None,
            "meowshell exited with code 1",
            diagnostics: "ssh: no key found; permission denied").Message;

        Assert.Contains("permission denied", message, StringComparison.Ordinal);
    }

    [Fact]
    public void AWallOfOutputIsTrimmedToWhatFitsInADialog()
    {
        var noisy = string.Join("\n", Enumerable.Range(0, 40).Select(i => $"line {i} of context"));

        var message = Translate(MeowshellErrorCode.None, "failed", diagnostics: noisy).Message;

        Assert.Contains("line 0 of context", message, StringComparison.Ordinal);
        Assert.DoesNotContain("line 20 of context", message, StringComparison.Ordinal);
        Assert.True(message.Length <= 301, $"message was {message.Length} characters");
    }

    [Fact]
    public void TheOriginalIsKeptAsTheInnerException()
    {
        // Whatever the message says, the real one has to survive for a log.
        Assert.IsType<TailcatException>(Translate(MeowshellErrorCode.None, "raw").InnerException);
    }
}
