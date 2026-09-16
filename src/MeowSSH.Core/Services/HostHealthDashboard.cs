using MeowSSH.Core.Licensing;
using MeowSSH.Core.Model;
using MeowSSH.Core.Ssh;

namespace MeowSSH.Core.Services;

public enum HostHealthState
{
    Healthy,
    Degraded,
    Unreachable,
    Unsupported,
}

/// <summary>One on-demand health snapshot for a saved host.</summary>
public sealed record HostHealthSnapshot(
    Guid HostId,
    string HostLabel,
    HostHealthState State,
    DateTimeOffset CheckedAtUtc,
    TimeSpan Duration,
    string? OperatingSystem = null,
    string? Uptime = null,
    string? Load = null,
    string? Disk = null,
    string? Memory = null,
    string? Message = null);

public interface IHostHealthDashboardService
{
    Task<IReadOnlyList<HostHealthSnapshot>> CheckAllAsync(CancellationToken cancellationToken = default);
    Task<HostHealthSnapshot> CheckAsync(Guid hostId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Pro-only, user-initiated host health checks. The service does not schedule background work,
/// persist command output, or retain authentication material after each check completes.
/// </summary>
public sealed class HostHealthDashboardService(
    IHostDirectory hostDirectory,
    ICredentialResolver credentials,
    ISshEngine engine,
    ISshPrompts prompts,
    IEntitlementService entitlements,
    TimeProvider? timeProvider = null) : IHostHealthDashboardService
{
    private const int MaxParallelHosts = 4;
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    internal const string ProbeCommand =
        "printf 'meowssh_os='; (uname -s 2>/dev/null || printf 'unknown'); " +
        "printf '\\nmeowssh_uptime='; (uptime -p 2>/dev/null || uptime 2>/dev/null || true); " +
        "printf '\\nmeowssh_load='; (cut -d ' ' -f 1-3 /proc/loadavg 2>/dev/null || sysctl -n vm.loadavg 2>/dev/null || true); " +
        "printf '\\nmeowssh_disk='; (df -Pk / 2>/dev/null | awk 'NR==2 {print $5}' || true); " +
        "printf '\\nmeowssh_memory='; (free -m 2>/dev/null | awk '/^Mem:/ {if ($2 > 0) printf \"%.0f%%\", $3*100/$2}' || true); " +
        "printf '\\n'";

    public async Task<IReadOnlyList<HostHealthSnapshot>> CheckAllAsync(CancellationToken cancellationToken = default)
    {
        EnsureEntitled();
        var hosts = await hostDirectory.GetHostsAsync(cancellationToken).ConfigureAwait(false);
        using var concurrency = new SemaphoreSlim(MaxParallelHosts, MaxParallelHosts);

        var tasks = hosts.Select((status, index) => CheckBoundedAsync(status.Host, index, concurrency, cancellationToken)).ToArray();
        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        return [.. results.OrderBy(static item => item.Index).Select(static item => item.Snapshot)];
    }

    public async Task<HostHealthSnapshot> CheckAsync(Guid hostId, CancellationToken cancellationToken = default)
    {
        EnsureEntitled();
        if (hostId == Guid.Empty) throw new ArgumentException("A host ID is required.", nameof(hostId));

        var hosts = await hostDirectory.GetHostsAsync(cancellationToken).ConfigureAwait(false);
        var host = hosts.FirstOrDefault(status => status.Host.Id == hostId)?.Host
            ?? throw new KeyNotFoundException("The saved host no longer exists.");
        return await CheckHostAsync(host, cancellationToken).ConfigureAwait(false);
    }

    private void EnsureEntitled()
    {
        if (!entitlements.Has(PremiumFeature.HostHealthDashboard))
            throw new InvalidOperationException("Host Health Dashboard requires MeowSSH Pro.");
    }

    private async Task<IndexedSnapshot> CheckBoundedAsync(
        HostRecord host,
        int index,
        SemaphoreSlim concurrency,
        CancellationToken cancellationToken)
    {
        await concurrency.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return new IndexedSnapshot(index, await CheckHostAsync(host, cancellationToken).ConfigureAwait(false));
        }
        finally
        {
            concurrency.Release();
        }
    }

    private async Task<HostHealthSnapshot> CheckHostAsync(HostRecord host, CancellationToken cancellationToken)
    {
        var checkedAt = _timeProvider.GetUtcNow();
        if (host.Protocol != HostProtocol.Ssh)
        {
            return new HostHealthSnapshot(
                host.Id,
                host.Label,
                HostHealthState.Unsupported,
                checkedAt,
                TimeSpan.Zero,
                Message: "Health checks currently support saved SSH hosts only.");
        }

        ISshConnection? connection = null;
        try
        {
            using var resolved = await credentials.ResolveAsync(host, cancellationToken).ConfigureAwait(false);
            var target = !string.IsNullOrWhiteSpace(resolved.Username)
                ? host with { Username = resolved.Username }
                : host;

            connection = await engine.ConnectAsync(target, resolved, prompts, cancellationToken).ConfigureAwait(false);
            var connectedAt = _timeProvider.GetUtcNow();
            var result = await connection.RunCommandAsync(ProbeCommand, ProbeTimeout, cancellationToken).ConfigureAwait(false);
            var completed = _timeProvider.GetUtcNow();
            var metrics = ParseMetrics(result.StandardOutput);
            var hasMetrics = metrics.Values.Any(static value => !string.IsNullOrWhiteSpace(value));
            var state = result.Succeeded && hasMetrics ? HostHealthState.Healthy : HostHealthState.Degraded;

            return new HostHealthSnapshot(
                host.Id,
                host.Label,
                state,
                checkedAt,
                completed - checkedAt,
                Metric(metrics, "meowssh_os"),
                Metric(metrics, "meowssh_uptime"),
                Metric(metrics, "meowssh_load"),
                Metric(metrics, "meowssh_disk"),
                Metric(metrics, "meowssh_memory"),
                state == HostHealthState.Healthy
                    ? $"Connected in {Math.Max(0, (connectedAt - checkedAt).TotalMilliseconds):0} ms."
                    : result.Succeeded
                        ? "Connected, but the remote system did not expose standard Unix health metrics."
                        : $"Health probe exited with status {result.ExitCode}.");
        }
        catch (SshException exception)
        {
            return new HostHealthSnapshot(
                host.Id,
                host.Label,
                HostHealthState.Unreachable,
                checkedAt,
                _timeProvider.GetUtcNow() - checkedAt,
                Message: exception.Message);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or ObjectDisposedException)
        {
            return new HostHealthSnapshot(
                host.Id,
                host.Label,
                HostHealthState.Unreachable,
                checkedAt,
                _timeProvider.GetUtcNow() - checkedAt,
                Message: exception.Message);
        }
        finally
        {
            if (connection is not null)
                await connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    internal static IReadOnlyDictionary<string, string> ParseMetrics(string output)
    {
        if (string.IsNullOrEmpty(output)) return new Dictionary<string, string>(StringComparer.Ordinal);
        if (output.Length > 32 * 1024) output = output[..(32 * 1024)];

        var metrics = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var rawLine in output.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n'))
        {
            var separator = rawLine.IndexOf('=');
            if (separator <= 0) continue;
            var key = rawLine[..separator].Trim();
            if (!key.StartsWith("meowssh_", StringComparison.Ordinal)) continue;
            var value = rawLine[(separator + 1)..].Trim();
            metrics[key] = value.Length <= 512 ? value : value[..512];
        }
        return metrics;
    }

    private static string? Metric(IReadOnlyDictionary<string, string> metrics, string key) =>
        metrics.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;

    private sealed record IndexedSnapshot(int Index, HostHealthSnapshot Snapshot);
}
