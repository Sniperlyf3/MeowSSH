using MeowSSH.Core.Security;
using MeowSSH.Core.Ssh;

namespace MeowSSH.App;

/// <summary>
/// Answers the engine's handshake prompts.
/// </summary>
/// <remarks>
/// Declines everything for now. These prompts are raised inside the engine's own
/// connect call, and the released Meowshell package offers no way to subscribe
/// before that happens -- so nothing here is reached yet. Declining is the safe
/// answer to a question nobody can see: an unknown host key is never accepted by
/// default. The UI for these lands with the package that can raise them.
/// </remarks>
internal sealed class DecliningPrompts : ISshPrompts
{
    public Task<bool> ConfirmUnknownHostKeyAsync(HostKeyPrompt prompt, CancellationToken cancellationToken = default) =>
        Task.FromResult(false);

    public Task<SecretBuffer?> RequestPasswordAsync(string prompt, CancellationToken cancellationToken = default) =>
        Task.FromResult<SecretBuffer?>(null);

    public Task<SecretBuffer?> RequestKeyPassphraseAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<SecretBuffer?>(null);

    public Task<IReadOnlyList<string>?> AnswerChallengeAsync(
        KeyboardInteractivePrompt prompt, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>?>(null);
}
