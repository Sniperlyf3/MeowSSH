using MeowSSH.Core.Model;
using MeowSSH.Core.Services;

namespace MeowSSH.Core.Ssh;

/// <summary>
/// Picks the relay for a saved Tailcat host before the engine dials it, and
/// asks before that relay is the public one.
/// </summary>
/// <remarks>
/// Saved Tailcat hosts used to be dialled with no relay map at all, which is
/// the public tailcat.dev relay: never asked about, and wrong for an address
/// on MeowSSH's relay, which it cannot resolve. Every Tailcat connect now has
/// a decided route (<see cref="TailcatRelayPolicy"/>), and the public one is
/// reached only on the user's answer: this time, always for this host (kept on
/// the host, editable in its settings), or never.
/// </remarks>
public sealed class TailcatRelaySshEngine(
    ISshEngine inner,
    TailcatRelayPolicy policy,
    IHostDirectory hosts,
    IHostEditor editor) : ISshEngine
{
    public async Task<ISshConnection> ConnectAsync(
        HostRecord host, SshCredentials credentials, ISshPrompts prompts,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(prompts);
        if (host.Transport != SshTransport.Tailcat)
            return await inner.ConnectAsync(host, credentials, prompts, cancellationToken).ConfigureAwait(false);

        var decision = await policy.DecideAsync(host.Address, host.TailcatDerpMapUrl, cancellationToken).ConfigureAwait(false);
        if (decision.Route == TailcatRoute.Public)
            await ConfirmPublicRelayAsync(host, prompts, cancellationToken).ConfigureAwait(false);

        return await inner.ConnectAsync(host with { TailcatDerpMapUrl = decision.DerpMapUrl }, credentials, prompts, cancellationToken).ConfigureAwait(false);
    }

    private async Task ConfirmPublicRelayAsync(HostRecord host, ISshPrompts prompts, CancellationToken cancellationToken)
    {
        // The stored host, not the one handed in: a session reconnecting with
        // a copy from before "Always" was chosen must not ask again.
        var saved = (await hosts.GetHostsAsync(cancellationToken).ConfigureAwait(false))
            .Select(static status => status.Host)
            .FirstOrDefault(stored => stored.Id == host.Id && !stored.IsDeleted);
        switch (saved?.PublicRelay ?? host.PublicRelay)
        {
            case PublicRelayConsent.Always:
                return;
            case PublicRelayConsent.Never:
                throw new SshException(
                    SshFailure.PublicRelayDeclined,
                    $"{host.Label} can only be reached over the public tailcat.dev relay, and it is set never to use it. Change \"Public relay\" in this host's settings to connect.");
        }

        var choice = await prompts.ConfirmPublicRelayAsync(
            new PublicRelayPrompt(host.Label, host.Address, CanRemember: saved is not null), cancellationToken).ConfigureAwait(false);
        if (choice == PublicRelayChoice.Cancel)
            throw new SshException(
                SshFailure.PublicRelayDeclined,
                $"Not connected: {host.Label} can only be reached over the public tailcat.dev relay. Connect once, or allow it under \"Public relay\" in this host's settings.");
        if (choice == PublicRelayChoice.AlwaysForHost && saved is not null)
            await editor.SaveHostAsync(saved with { PublicRelay = PublicRelayConsent.Always }, cancellationToken).ConfigureAwait(false);
    }
}
