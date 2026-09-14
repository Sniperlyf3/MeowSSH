using System.Text;
using MeowSSH.Core.Security;
using MeowSSH.Core.Ssh;

namespace MeowSSH.UI.Services;

/// <summary>
/// Answers authentication prompts from the credential selected on the host and
/// falls back to the interactive UI only when the stored credential does not
/// contain an answer.
/// </summary>
public sealed class CredentialSshPrompts(ISshPrompts fallback, SshCredentials credentials) : ISshPrompts
{
    public Task<bool> ConfirmUnknownHostKeyAsync(
        HostKeyPrompt prompt,
        CancellationToken cancellationToken = default) =>
        fallback.ConfirmUnknownHostKeyAsync(prompt, cancellationToken);

    public Task<SecretBuffer?> RequestPasswordAsync(
        string prompt,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return credentials.Password is { } stored
            ? Task.FromResult<SecretBuffer?>(SecretBuffer.CopyFrom(stored.ReadOnlySpan))
            : fallback.RequestPasswordAsync(prompt, cancellationToken);
    }

    public Task<SecretBuffer?> RequestKeyPassphraseAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return credentials.KeyPassphrase is { } stored
            ? Task.FromResult<SecretBuffer?>(SecretBuffer.CopyFrom(stored.ReadOnlySpan))
            : fallback.RequestKeyPassphraseAsync(cancellationToken);
    }

    public Task<IReadOnlyList<string>?> AnswerChallengeAsync(
        KeyboardInteractivePrompt prompt,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Some SSH servers expose password authentication through PAM's
        // keyboard-interactive method instead of the SSH "password" method.
        // Only auto-answer an unambiguous single hidden Password prompt; OTP/MFA
        // questions still go to the user.
        if (credentials.Password is { } stored
            && prompt.Questions.Count == 1
            && prompt.Echos.Count == 1
            && !prompt.Echos[0]
            && prompt.Questions[0].Contains("password", StringComparison.OrdinalIgnoreCase))
        {
            IReadOnlyList<string> answer = [Encoding.UTF8.GetString(stored.ReadOnlySpan)];
            return Task.FromResult<IReadOnlyList<string>?>(answer);
        }

        return fallback.AnswerChallengeAsync(prompt, cancellationToken);
    }
}
