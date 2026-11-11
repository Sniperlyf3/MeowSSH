using MeowSSH.Core.Ssh;

namespace MeowSSH.Core.Services;

/// <summary>The user declined the public relay for an operation that needed it.</summary>
public sealed class TailcatPublicRelayDeclinedException(string message) : InvalidOperationException(message);

/// <summary>
/// The public-relay rule for everything the Tailcat hub dials that is not a
/// saved host: pasted addresses for files, forwards and diagnostics, and this
/// phone's own sharing. Saved hosts go through <see cref="TailcatRelaySshEngine"/>.
/// </summary>
/// <remarks>
/// A pasted address has nowhere to keep an answer, so it asks every time
/// (the prompt suggests saving it as a host). Sharing asks only when it would
/// land on the public relay without the user having picked it: a build with
/// no managed relay, where "share" used to mean "share publicly" silently.
/// Choosing "Anyone" on the share panel is that pick, and is not asked again.
/// </remarks>
public sealed class PublicRelayGuardTailcatHubService(
    ITailcatHubService inner,
    TailcatRelayPolicy policy,
    ISshPrompts prompts) : ITailcatHubService
{
    public TailcatHubSnapshot Snapshot => inner.Snapshot;

    public event EventHandler? Changed
    {
        add => inner.Changed += value;
        remove => inner.Changed -= value;
    }

    public async Task StartServerAsync(TailcatServeRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.DerpMapUrl) && policy.ManagedDerpMapUrl is null)
            await ConfirmAsync("Sharing this phone", "", "share", cancellationToken).ConfigureAwait(false);
        await inner.StartServerAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public Task StopServerAsync(CancellationToken cancellationToken = default) => inner.StopServerAsync(cancellationToken);

    public async Task StartSocksAsync(TailcatSocksRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        // A SOCKS gateway dials whatever it is later asked for, so the
        // question is asked once, when it would otherwise default to public.
        if (string.IsNullOrWhiteSpace(request.DerpMapUrl) && policy.ManagedDerpMapUrl is null)
            await ConfirmAsync("The Tailcat gateway", "", "start the gateway", cancellationToken).ConfigureAwait(false);
        await inner.StartSocksAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public Task StopSocksAsync(CancellationToken cancellationToken = default) => inner.StopSocksAsync(cancellationToken);

    public async Task<TailcatForwardSnapshot> StartForwardAsync(TailcatForwardRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var map = await RouteAsync(request.Address, request.DerpMapUrl, "forward to it", cancellationToken).ConfigureAwait(false);
        return await inner.StartForwardAsync(request with { DerpMapUrl = map }, cancellationToken).ConfigureAwait(false);
    }

    public Task StopForwardAsync(Guid id, CancellationToken cancellationToken = default) => inner.StopForwardAsync(id, cancellationToken);

    public Task<IReadOnlyList<string>> ListKeysAsync(CancellationToken cancellationToken = default) => inner.ListKeysAsync(cancellationToken);

    public Task<TailcatGeneratedKey> GenerateKeyAsync(TailcatGenerateKeyRequest request, CancellationToken cancellationToken = default) =>
        inner.GenerateKeyAsync(request, cancellationToken);

    public Task DeleteKeyAsync(string name, CancellationToken cancellationToken = default) => inner.DeleteKeyAsync(name, cancellationToken);

    public Task<string> GetClientPublicKeyAsync(string? name = null, CancellationToken cancellationToken = default) =>
        inner.GetClientPublicKeyAsync(name, cancellationToken);

    // Reading an address dials nothing.
    public Task<string> ResolveAddressAsync(string address, string? derpMapUrl = null, CancellationToken cancellationToken = default) =>
        inner.ResolveAddressAsync(address, derpMapUrl, cancellationToken);

    public Task<TailcatAddressDetails> InspectAddressAsync(string address, string? derpMapUrl = null, CancellationToken cancellationToken = default) =>
        inner.InspectAddressAsync(address, derpMapUrl, cancellationToken);

    public async Task<TailcatDiagnosticResult> DiagnoseAsync(string address, bool waitForDirect, string? derpMapUrl = null, CancellationToken cancellationToken = default)
    {
        var map = await RouteAsync(address, derpMapUrl, "test the connection", cancellationToken).ConfigureAwait(false);
        return await inner.DiagnoseAsync(address, waitForDirect, map, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<TailcatRemoteFile>> ListRemoteFilesAsync(string address, string path, string? derpMapUrl = null, CancellationToken cancellationToken = default)
    {
        var map = await RouteAsync(address, derpMapUrl, "list its files", cancellationToken).ConfigureAwait(false);
        return await inner.ListRemoteFilesAsync(address, path, map, cancellationToken).ConfigureAwait(false);
    }

    public async Task UploadAsync(string localPath, string address, string remotePath, bool recursive = false, string? derpMapUrl = null, CancellationToken cancellationToken = default)
    {
        var map = await RouteAsync(address, derpMapUrl, "upload to it", cancellationToken).ConfigureAwait(false);
        await inner.UploadAsync(localPath, address, remotePath, recursive, map, cancellationToken).ConfigureAwait(false);
    }

    public async Task DownloadAsync(string address, string remotePath, string localPath, bool recursive = false, string? derpMapUrl = null, CancellationToken cancellationToken = default)
    {
        var map = await RouteAsync(address, derpMapUrl, "download from it", cancellationToken).ConfigureAwait(false);
        await inner.DownloadAsync(address, remotePath, localPath, recursive, map, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync() => inner.DisposeAsync();

    /// <returns>The relay map to dial with: the user's own, or the one the route needs.</returns>
    private async Task<string?> RouteAsync(string address, string? derpMapUrl, string action, CancellationToken cancellationToken)
    {
        var decision = await policy.DecideAsync(address, derpMapUrl, cancellationToken).ConfigureAwait(false);
        if (decision.Route == TailcatRoute.Public)
            await ConfirmAsync(Shorten(address), address, action, cancellationToken).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(derpMapUrl) ? decision.DerpMapUrl : derpMapUrl;
    }

    private async Task ConfirmAsync(string target, string address, string action, CancellationToken cancellationToken)
    {
        var choice = await prompts.ConfirmPublicRelayAsync(new PublicRelayPrompt(target, address, CanRemember: false), cancellationToken).ConfigureAwait(false);
        if (choice == PublicRelayChoice.Cancel)
            throw new TailcatPublicRelayDeclinedException(
                $"Did not {action}: that needs the public tailcat.dev relay, and it was declined.");
    }

    private static string Shorten(string address) =>
        address.Length > 22 ? string.Concat(address.AsSpan(0, 20), "…") : address;
}
