using System.Collections.Concurrent;
using MeowSSH.Core.Licensing;

namespace MeowSSH.Core.Services;

/// <summary>
/// Keeps Tailcat node identities registered for the managed MeowSSH relay without
/// making ordinary direct/custom-relay Tailcat operations depend on control-plane
/// availability. Registration failures are retained as hub diagnostics and retried
/// by later identity/network activity; DERP admission itself remains fail-closed.
/// </summary>
public sealed class ManagedDerpTailcatHubService(
    ITailcatHubService inner,
    IManagedDerpRegistrationService registrations) : ITailcatHubService
{
    private const int MaxRegistrationLogLines = 20;
    private readonly ConcurrentDictionary<string, byte> _registered = new(StringComparer.Ordinal);
    private readonly object _logGate = new();
    private readonly List<string> _registrationLogs = [];

    public TailcatHubSnapshot Snapshot
    {
        get
        {
            var snapshot = inner.Snapshot;
            string[] registrationLogs;
            lock (_logGate) registrationLogs = [.. _registrationLogs];

            return snapshot with
            {
                RecentLogLines = [.. snapshot.RecentLogLines, .. registrationLogs],
            };
        }
    }

    public event EventHandler? Changed
    {
        add => inner.Changed += value;
        remove => inner.Changed -= value;
    }

    public async Task StartServerAsync(TailcatServeRequest request, CancellationToken cancellationToken = default)
    {
        await inner.StartServerAsync(request, cancellationToken).ConfigureAwait(false);
        if (inner.Snapshot.Server is { Address: { Length: > 0 } address })
            _ = TryRegisterServerAddressAsync(address, label: null, request.DerpMapUrl, CancellationToken.None);
    }

    public Task StopServerAsync(CancellationToken cancellationToken = default) =>
        inner.StopServerAsync(cancellationToken);

    public Task StartSocksAsync(TailcatSocksRequest request, CancellationToken cancellationToken = default)
    {
        _ = TryRegisterClientAsync(request.ClientKey, CancellationToken.None);
        return inner.StartSocksAsync(request, cancellationToken);
    }

    public Task StopSocksAsync(CancellationToken cancellationToken = default) =>
        inner.StopSocksAsync(cancellationToken);

    public Task<TailcatForwardSnapshot> StartForwardAsync(
        TailcatForwardRequest request,
        CancellationToken cancellationToken = default)
    {
        _ = TryRegisterClientAsync(request.ClientKey, CancellationToken.None);
        return inner.StartForwardAsync(request, cancellationToken);
    }

    public Task StopForwardAsync(Guid id, CancellationToken cancellationToken = default) =>
        inner.StopForwardAsync(id, cancellationToken);

    public Task<IReadOnlyList<string>> ListKeysAsync(CancellationToken cancellationToken = default) =>
        inner.ListKeysAsync(cancellationToken);

    public async Task<TailcatGeneratedKey> GenerateKeyAsync(
        TailcatGenerateKeyRequest request,
        CancellationToken cancellationToken = default)
    {
        var generated = await inner.GenerateKeyAsync(request, cancellationToken).ConfigureAwait(false);
        if (generated.Client)
            await TryRegisterClientPublicAsync(generated.Value, cancellationToken).ConfigureAwait(false);
        else
            await TryRegisterServerAddressAsync(generated.Value, generated.Name, request.DerpMapUrl, cancellationToken).ConfigureAwait(false);
        return generated;
    }

    public Task DeleteKeyAsync(string name, CancellationToken cancellationToken = default) =>
        inner.DeleteKeyAsync(name, cancellationToken);

    public async Task<string> GetClientPublicKeyAsync(
        string? name = null,
        CancellationToken cancellationToken = default)
    {
        var nodePublic = await inner.GetClientPublicKeyAsync(name, cancellationToken).ConfigureAwait(false);
        await TryRegisterClientPublicAsync(nodePublic, cancellationToken).ConfigureAwait(false);
        return nodePublic;
    }

    public Task<string> ResolveAddressAsync(
        string address,
        string? derpMapUrl = null,
        CancellationToken cancellationToken = default)
    {
        _ = TryRegisterClientAsync(null, CancellationToken.None);
        return inner.ResolveAddressAsync(address, derpMapUrl, cancellationToken);
    }

    public Task<TailcatAddressDetails> InspectAddressAsync(
        string address,
        string? derpMapUrl = null,
        CancellationToken cancellationToken = default)
    {
        _ = TryRegisterClientAsync(null, CancellationToken.None);
        return inner.InspectAddressAsync(address, derpMapUrl, cancellationToken);
    }

    public Task<TailcatDiagnosticResult> DiagnoseAsync(
        string address,
        bool waitForDirect,
        string? derpMapUrl = null,
        CancellationToken cancellationToken = default)
    {
        _ = TryRegisterClientAsync(null, CancellationToken.None);
        return inner.DiagnoseAsync(address, waitForDirect, derpMapUrl, cancellationToken);
    }

    public Task<IReadOnlyList<TailcatRemoteFile>> ListRemoteFilesAsync(
        string address,
        string path,
        string? derpMapUrl = null,
        CancellationToken cancellationToken = default)
    {
        _ = TryRegisterClientAsync(null, CancellationToken.None);
        return inner.ListRemoteFilesAsync(address, path, derpMapUrl, cancellationToken);
    }

    public Task UploadAsync(
        string localPath,
        string address,
        string remotePath,
        bool recursive = false,
        string? derpMapUrl = null,
        CancellationToken cancellationToken = default)
    {
        _ = TryRegisterClientAsync(null, CancellationToken.None);
        return inner.UploadAsync(localPath, address, remotePath, recursive, derpMapUrl, cancellationToken);
    }

    public Task DownloadAsync(
        string address,
        string remotePath,
        string localPath,
        bool recursive = false,
        string? derpMapUrl = null,
        CancellationToken cancellationToken = default)
    {
        _ = TryRegisterClientAsync(null, CancellationToken.None);
        return inner.DownloadAsync(address, remotePath, localPath, recursive, derpMapUrl, cancellationToken);
    }

    public ValueTask DisposeAsync() => inner.DisposeAsync();

    private async Task<bool> TryRegisterClientAsync(string? name, CancellationToken cancellationToken)
    {
        try
        {
            var nodePublic = await inner.GetClientPublicKeyAsync(name, cancellationToken).ConfigureAwait(false);
            return await TryRegisterClientPublicAsync(nodePublic, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            AddRegistrationLog(ex);
            return false;
        }
    }

    private async Task<bool> TryRegisterClientPublicAsync(string nodePublic, CancellationToken cancellationToken)
    {
        var key = "client:" + nodePublic;
        if (_registered.ContainsKey(key)) return true;

        try
        {
            await registrations.RegisterClientAsync(nodePublic, cancellationToken).ConfigureAwait(false);
            _registered.TryAdd(key, 0);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            AddRegistrationLog(ex);
            return false;
        }
    }

    private async Task TryRegisterServerAddressAsync(
        string address,
        string? label,
        string? derpMapUrl,
        CancellationToken cancellationToken)
    {
        try
        {
            var clientPublic = await inner.GetClientPublicKeyAsync(null, cancellationToken).ConfigureAwait(false);
            if (!await TryRegisterClientPublicAsync(clientPublic, cancellationToken).ConfigureAwait(false))
                return;

            var details = await inner.InspectAddressAsync(address, derpMapUrl, cancellationToken).ConfigureAwait(false);
            var key = "server:" + details.ServerPublicKey;
            if (_registered.ContainsKey(key)) return;

            await registrations.RegisterServerAsync(
                clientPublic,
                details.ServerPublicKey,
                label,
                cancellationToken).ConfigureAwait(false);
            _registered.TryAdd(key, 0);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            AddRegistrationLog(ex);
        }
    }

    private void AddRegistrationLog(Exception exception)
    {
        var message = string.IsNullOrWhiteSpace(exception.Message)
            ? exception.GetType().Name
            : exception.Message.Trim();
        lock (_logGate)
        {
            _registrationLogs.Add($"Managed relay registration pending; direct and custom relay paths remain available: {message}");
            if (_registrationLogs.Count > MaxRegistrationLogLines)
                _registrationLogs.RemoveRange(0, _registrationLogs.Count - MaxRegistrationLogLines);
        }
    }
}
