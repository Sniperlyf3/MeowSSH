namespace MeowSSH.UI.Services;

public sealed record TailcatVpnRequest(
    string Address,
    IReadOnlyList<string> Routes,
    string? ClientKey = null,
    string? DerpMapUrl = null);

public sealed record TailcatVpnSnapshot(
    bool IsRunning,
    string? Address,
    IReadOnlyList<string> Routes,
    string? Error = null);

public interface ITailcatVpnController
{
    TailcatVpnSnapshot Snapshot { get; }
    event EventHandler? Changed;
    Task StartAsync(TailcatVpnRequest request, CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
}
