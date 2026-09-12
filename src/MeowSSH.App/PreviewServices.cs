using MeowSSH.Core.Model;
using MeowSSH.Core.Security;
using MeowSSH.Core.Services;
using MeowSSH.Core.Ssh;

namespace MeowSSH.App;

/// <summary>
/// Hosts kept in memory for this preview build.
/// </summary>
/// <remarks>
/// The encrypted store is the next piece of work. Everything below the UI --
/// the engine, the vault's key hierarchy, the biometric gate -- is real; only
/// where these rows live is not, and they do not survive a restart.
/// </remarks>
internal sealed class PreviewHostDirectory : IHostDirectory
{
    private readonly List<HostStatus> _hosts =
    [
        new(new HostRecord
        {
            Id = Guid.NewGuid(),
            Label = "Add a host to begin",
            Address = "edit me",
            Username = null,
            Transport = SshTransport.Tcp,
            UpdatedAt = DateTimeOffset.UtcNow,
        }),
    ];

    public event EventHandler? Changed;

    public ValueTask<IReadOnlyList<HostStatus>> GetHostsAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<IReadOnlyList<HostStatus>>(_hosts);

    public void Add(HostRecord host)
    {
        _hosts.Add(new HostStatus(host));
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

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
