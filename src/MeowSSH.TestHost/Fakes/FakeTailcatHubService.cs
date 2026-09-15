using MeowSSH.Core.Services;

namespace MeowSSH.TestHost.Fakes;

public sealed class FakeTailcatHubService : ITailcatHubService
{
    private readonly List<TailcatForwardSnapshot> _forwards = [];
    private readonly List<string> _keys = ["client-default"];
    private TailcatServerSnapshot? _server;
    private TailcatSocksSnapshot? _socks;

    public event EventHandler? Changed;

    public TailcatHubSnapshot Snapshot => new(_server, _socks, [.. _forwards], []);

    public Task StartServerAsync(TailcatServeRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _server = new TailcatServerSnapshot(
            request.FullAddress ? "tc-test-full-address" : "tc-test-address",
            DateTimeOffset.UtcNow + request.Lifetime,
            request.EnableShell, request.EnableFiles, request.EnableExitNode, request.AllowAnyClient, request.UseTailcatCredentialForShell,
            request.SharedFolder, request.FileMode);
        RaiseChanged();
        return Task.CompletedTask;
    }

    public Task StopServerAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _server = null;
        RaiseChanged();
        return Task.CompletedTask;
    }

    public Task StartSocksAsync(TailcatSocksRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _socks = new TailcatSocksSnapshot("127.0.0.1:19080", request.ClientKey);
        RaiseChanged();
        return Task.CompletedTask;
    }

    public Task StopSocksAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _socks = null;
        RaiseChanged();
        return Task.CompletedTask;
    }

    public Task<TailcatForwardSnapshot> StartForwardAsync(TailcatForwardRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = new TailcatForwardSnapshot(Guid.NewGuid(), request.Address, request.Mappings, ["127.0.0.1:18080"], request.ClientKey);
        _forwards.Add(snapshot);
        RaiseChanged();
        return Task.FromResult(snapshot);
    }

    public Task StopForwardAsync(Guid id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _forwards.RemoveAll(forward => forward.Id == id);
        RaiseChanged();
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> ListKeysAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<string>>([.. _keys]);
    }

    public Task<TailcatGeneratedKey> GenerateKeyAsync(TailcatGenerateKeyRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_keys.Contains(request.Name, StringComparer.Ordinal)) _keys.Add(request.Name);
        var generated = new TailcatGeneratedKey(request.Name, request.Client, request.Client ? "nodekey:test-client" : "tc-test-generated");
        RaiseChanged();
        return Task.FromResult(generated);
    }

    public Task DeleteKeyAsync(string name, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _keys.Remove(name);
        RaiseChanged();
        return Task.CompletedTask;
    }

    public Task<string> GetClientPublicKeyAsync(string? name = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult("nodekey:test-client-public");
    }

    public Task<string> ResolveAddressAsync(string address, string? derpMapUrl = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult("tc-test-resolved-full-address");
    }

    public Task<TailcatAddressDetails> InspectAddressAsync(string address, string? derpMapUrl = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new TailcatAddressDetails(
            "tc-test-resolved-full-address",
            "nodekey:test-server",
            "discokey:test-server",
            true,
            0,
            [new TailcatDerpRegionDetails(1, "test", "Test DERP", [new TailcatDerpNodeDetails("test-1", "derp.example.test", null, "192.0.2.10", null, 3478, 443)])]));
    }

    public Task<TailcatDiagnosticResult> DiagnoseAsync(string address, bool waitForDirect, string? derpMapUrl = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new TailcatDiagnosticResult(
            "tc-test-resolved", true, TimeSpan.FromMilliseconds(18), true,
            "192.0.2.1:41641", 1, "test", "nodekey:test-server", true));
    }

    public Task<IReadOnlyList<TailcatRemoteFile>> ListRemoteFilesAsync(string address, string path, string? derpMapUrl = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<TailcatRemoteFile>>
        ([
            new("Documents", true, "drwxr-xr-x", 0, DateTime.UtcNow),
            new("hello.txt", false, "-rw-r--r--", 12, DateTime.UtcNow),
        ]);
    }

    public Task UploadAsync(string localPath, string address, string remotePath, bool recursive = false, string? derpMapUrl = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task DownloadAsync(string address, string remotePath, string localPath, bool recursive = false, string? derpMapUrl = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
}
