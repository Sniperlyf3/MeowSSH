using MeowSSH.Core.Licensing;

namespace MeowSSH.Core.Services;

/// <summary>
/// Enforces paid Tailcat capabilities independently of the UI. Stop/cleanup operations
/// intentionally remain unrestricted so an entitlement change can never strand network
/// listeners or a phone server that was started while Pro access was valid.
/// </summary>
public sealed class EntitlementTailcatHubService(
    ITailcatHubService inner,
    IEntitlementService entitlements) : ITailcatHubService
{
    public TailcatHubSnapshot Snapshot => inner.Snapshot;

    public event EventHandler? Changed
    {
        add => inner.Changed += value;
        remove => inner.Changed -= value;
    }

    public Task StartServerAsync(TailcatServeRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        Require(PremiumFeature.TailcatPhoneServer, "Sharing this phone over Tailcat requires MeowSSH Pro.");

        if (request.EnableExitNode)
            Require(PremiumFeature.TailcatExitNode, "Using this phone as a Tailcat exit node requires MeowSSH Pro.");

        if (UsesAdvancedServeTargets(request.ServeTargets))
            Require(PremiumFeature.TailcatAdvancedRouting, "Tailcat port ranges and all-port serving require MeowSSH Pro.");

        return inner.StartServerAsync(request, cancellationToken);
    }

    public Task StopServerAsync(CancellationToken cancellationToken = default) =>
        inner.StopServerAsync(cancellationToken);

    public Task StartSocksAsync(TailcatSocksRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return inner.StartSocksAsync(request, cancellationToken);
    }

    public Task StopSocksAsync(CancellationToken cancellationToken = default) =>
        inner.StopSocksAsync(cancellationToken);

    public Task<TailcatForwardSnapshot> StartForwardAsync(
        TailcatForwardRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Udp)
            Require(PremiumFeature.TailcatAdvancedRouting, "UDP Tailcat forwarding requires MeowSSH Pro.");

        return inner.StartForwardAsync(request, cancellationToken);
    }

    public Task StopForwardAsync(Guid id, CancellationToken cancellationToken = default) =>
        inner.StopForwardAsync(id, cancellationToken);

    public Task<IReadOnlyList<string>> ListKeysAsync(CancellationToken cancellationToken = default) =>
        inner.ListKeysAsync(cancellationToken);

    public Task<TailcatGeneratedKey> GenerateKeyAsync(
        TailcatGenerateKeyRequest request,
        CancellationToken cancellationToken = default) =>
        inner.GenerateKeyAsync(request, cancellationToken);

    public Task DeleteKeyAsync(string name, CancellationToken cancellationToken = default) =>
        inner.DeleteKeyAsync(name, cancellationToken);

    public Task<string> GetClientPublicKeyAsync(string? name = null, CancellationToken cancellationToken = default) =>
        inner.GetClientPublicKeyAsync(name, cancellationToken);

    public Task<string> ResolveAddressAsync(
        string address,
        string? derpMapUrl = null,
        CancellationToken cancellationToken = default) =>
        inner.ResolveAddressAsync(address, derpMapUrl, cancellationToken);

    public Task<TailcatAddressDetails> InspectAddressAsync(
        string address,
        string? derpMapUrl = null,
        CancellationToken cancellationToken = default) =>
        inner.InspectAddressAsync(address, derpMapUrl, cancellationToken);

    public Task<TailcatDiagnosticResult> DiagnoseAsync(
        string address,
        bool waitForDirect,
        string? derpMapUrl = null,
        CancellationToken cancellationToken = default) =>
        inner.DiagnoseAsync(address, waitForDirect, derpMapUrl, cancellationToken);

    public Task<IReadOnlyList<TailcatRemoteFile>> ListRemoteFilesAsync(
        string address,
        string path,
        string? derpMapUrl = null,
        CancellationToken cancellationToken = default) =>
        inner.ListRemoteFilesAsync(address, path, derpMapUrl, cancellationToken);

    public Task UploadAsync(
        string localPath,
        string address,
        string remotePath,
        bool recursive = false,
        string? derpMapUrl = null,
        CancellationToken cancellationToken = default) =>
        inner.UploadAsync(localPath, address, remotePath, recursive, derpMapUrl, cancellationToken);

    public Task DownloadAsync(
        string address,
        string remotePath,
        string localPath,
        bool recursive = false,
        string? derpMapUrl = null,
        CancellationToken cancellationToken = default) =>
        inner.DownloadAsync(address, remotePath, localPath, recursive, derpMapUrl, cancellationToken);

    public ValueTask DisposeAsync() => inner.DisposeAsync();

    private void Require(PremiumFeature feature, string message)
    {
        if (!entitlements.Has(feature))
            throw new InvalidOperationException(message);
    }

    private static bool UsesAdvancedServeTargets(IReadOnlyList<string>? targets) =>
        targets?.Any(static target =>
            string.Equals(target.Trim(), "all", StringComparison.OrdinalIgnoreCase) ||
            target.Contains('-', StringComparison.Ordinal)) == true;
}
