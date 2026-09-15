using MeowSSH.UI.Services;

namespace MeowSSH.TestHost.Fakes;

public sealed class FakeQrScanner : IQrScanner
{
    public Task<string?> ScanAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<string?>("tc-test-scanned-address");
    }
}

public sealed class FakeTailcatVpnController : ITailcatVpnController
{
    private TailcatVpnSnapshot _snapshot = new(false, null, []);

    public TailcatVpnSnapshot Snapshot => _snapshot;
    public event EventHandler? Changed;

    public Task StartAsync(TailcatVpnRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _snapshot = new TailcatVpnSnapshot(true, request.Address.Trim(), [.. request.Routes]);
        Changed?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _snapshot = new TailcatVpnSnapshot(false, null, []);
        Changed?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }
}
