using MeowSSH.Core.Security;
using MeowSSH.Core.Ssh;

namespace MeowSSH.UI.Services;

/// <summary>
/// A question the handshake is waiting on an answer to.
/// </summary>
/// <remarks>
/// The handshake is blocked while this exists. Every path out of the UI must
/// answer it — including dismissing the sheet, which answers "no" — or the
/// connection hangs until its own timeout.
/// </remarks>
public abstract class SshPromptRequest<T>
{
    private readonly TaskCompletionSource<T> _completion =
        // The answer arrives on the renderer's thread and is awaited on the
        // agent's; resuming the handshake inline on the UI thread would block
        // the screen for the length of a network round trip.
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal Task<T> Completion => _completion.Task;

    /// <summary>Answers the question, releasing the handshake.</summary>
    public void Answer(T answer) => _completion.TrySetResult(answer);
}

/// <summary>A host key the user has not seen before.</summary>
public sealed class HostKeyRequest(HostKeyPrompt prompt) : SshPromptRequest<bool>
{
    public HostKeyPrompt Prompt { get; } = prompt;
}

/// <summary>A password the server is asking for.</summary>
public sealed class PasswordRequest(string prompt) : SshPromptRequest<string?>
{
    public string Prompt { get; } = prompt;
}

/// <summary>
/// Puts the handshake's questions on screen and waits for a person.
/// </summary>
/// <remarks>
/// <para>
/// These are raised by the agent on its own thread, part way through a
/// handshake that is blocked until they are answered. So each one is published
/// as a property the shell watches, and the call waits on a completion source
/// rather than returning a default. Returning a default is what the previous
/// implementation did, and it is why connecting to a host that was not already
/// trusted failed with "host key prompt was cancelled": nobody had declined, it
/// was simply that nobody had answered.
/// </para>
/// <para>
/// One at a time. Two prompts racing for the same sheet would leave one of them
/// invisible and unanswerable, and the handshake behind it hanging.
/// </para>
/// </remarks>
public sealed class InteractiveSshPrompts : ISshPrompts, IDisposable
{
    private readonly SemaphoreSlim _oneAtATime = new(1, 1);

    /// <summary>The host key awaiting a decision, if any.</summary>
    public HostKeyRequest? PendingHostKey { get; private set; }

    /// <summary>The password being asked for, if any.</summary>
    public PasswordRequest? PendingPassword { get; private set; }

    /// <summary>Raised when a question appears or is answered.</summary>
    public event EventHandler? Changed;

    public async Task<bool> ConfirmUnknownHostKeyAsync(HostKeyPrompt prompt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prompt);

        var request = new HostKeyRequest(prompt);
        return await AskAsync<HostKeyRequest, bool>(
            request,
            r => PendingHostKey = r,
            () => PendingHostKey = null,
            // A cancelled connection is not a trusted host. Defaulting the other
            // way would silently accept a key nobody looked at.
            declined: false,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<SecretBuffer?> RequestPasswordAsync(string prompt, CancellationToken cancellationToken = default)
    {
        var request = new PasswordRequest(prompt);
        var answer = await AskAsync<PasswordRequest, string?>(
            request,
            r => PendingPassword = r,
            () => PendingPassword = null,
            declined: null,
            cancellationToken).ConfigureAwait(false);

        // Copied into a buffer that can be wiped, and the string left to the GC.
        // It came from a bound input and cannot be cleared, which is a weakness
        // of the web view rather than one to add to here.
        return answer is null ? null : SecretBuffer.CopyFrom(System.Text.Encoding.UTF8.GetBytes(answer));
    }

    /// <summary>
    /// Not asked for interactively: a key's passphrase comes from the vault
    /// alongside the key it unlocks, so being asked here means the stored
    /// passphrase was wrong or absent, and a prompt would not know any better.
    /// </summary>
    public Task<SecretBuffer?> RequestKeyPassphraseAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<SecretBuffer?>(null);

    /// <summary>
    /// Declined for now. A challenge is a variable list of questions, which
    /// needs a form this app does not have yet; answering it with nothing is
    /// honest, where answering with blanks would look like a wrong password.
    /// </summary>
    public Task<IReadOnlyList<string>?> AnswerChallengeAsync(
        KeyboardInteractivePrompt prompt, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>?>(null);

    private async Task<T> AskAsync<TRequest, T>(
        TRequest request,
        Action<TRequest> publish,
        Action withdraw,
        T declined,
        CancellationToken cancellationToken)
        where TRequest : SshPromptRequest<T>
    {
        await _oneAtATime.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            publish(request);
            Changed?.Invoke(this, EventArgs.Empty);

            // A cancelled connection must release the handshake too, or the
            // agent waits on an answer that can no longer arrive.
            using var registration = cancellationToken.Register(() => request.Answer(declined));
            return await request.Completion.ConfigureAwait(false);
        }
        finally
        {
            withdraw();
            Changed?.Invoke(this, EventArgs.Empty);
            _oneAtATime.Release();
        }
    }

    public void Dispose() => _oneAtATime.Dispose();
}
