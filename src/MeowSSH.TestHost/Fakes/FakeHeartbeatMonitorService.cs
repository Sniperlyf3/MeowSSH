using MeowSSH.Core.Licensing;
using MeowSSH.Core.Services;
using Microsoft.AspNetCore.Components;

namespace MeowSSH.TestHost.Fakes;

/// <summary>
/// Stands in for HeartbeatMonitorService, which needs MeowSSHAPI. The alerting
/// and gating rules are tested in Core; this reproduces what the page renders.
/// </summary>
/// <remarks>
/// "heartbeatseeded" starts with one monitor down and one created on another
/// phone (so this one has no ping URL for it).
/// </remarks>
public sealed class FakeHeartbeatMonitorService : IHeartbeatMonitorService
{
    private static readonly Uri BaseUri = new("https://api.meowssh.test/");
    private readonly IEntitlementService _entitlements;
    private readonly List<HeartbeatMonitorInfo> _monitors = [];
    private readonly Dictionary<string, string> _tokens = new(StringComparer.Ordinal);

    public FakeHeartbeatMonitorService(IEntitlementService entitlements, NavigationManager navigation)
    {
        _entitlements = entitlements;
        if (new Uri(navigation.Uri).Query.Contains("heartbeatseeded", StringComparison.OrdinalIgnoreCase))
        {
            var created = new DateTimeOffset(2026, 9, 20, 2, 0, 0, TimeSpan.Zero);
            _monitors.Add(new HeartbeatMonitorInfo("aa11", "nightly backup", 86400, 3600, created, created.AddDays(3), null, HeartbeatStates.Down, created.AddDays(4)));
            _tokens["aa11"] = "seededtokenseededtokenseededtok1";
            _monitors.Add(new HeartbeatMonitorInfo("bb22", "made on the tablet", 3600, 900, created, null, null, HeartbeatStates.New, null));
        }
    }

    public bool CanUse => _entitlements.Has(PremiumFeature.PushMonitoring);

    public Task<IReadOnlyList<HeartbeatMonitorInfo>> ListAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<HeartbeatMonitorInfo>>([.. _monitors]);

    public Task<HeartbeatMonitorInfo> CreateAsync(string name, TimeSpan period, TimeSpan grace, CancellationToken cancellationToken = default)
    {
        if (!CanUse) throw new InvalidOperationException("Push monitoring requires MeowSSH Pro Cloud.");
        var id = $"m{_monitors.Count + 1:x4}";
        var monitor = new HeartbeatMonitorInfo(id, name.Trim(), (int)period.TotalSeconds, (int)grace.TotalSeconds,
            new DateTimeOffset(2026, 9, 25, 12, 0, 0, TimeSpan.Zero), null, null, HeartbeatStates.New, null);
        _monitors.Add(monitor);
        _tokens[id] = $"faketoken{id}".PadRight(32, 'x');
        return Task.FromResult(monitor);
    }

    public Task DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        _monitors.RemoveAll(m => m.Id == id);
        _tokens.Remove(id);
        return Task.CompletedTask;
    }

    public Task<Uri?> PingUrlAsync(string id, CancellationToken cancellationToken = default) =>
        Task.FromResult(_tokens.TryGetValue(id, out var token) ? new Uri(BaseUri, "v1/ping/" + token) : null);

    public Task<IReadOnlyList<HeartbeatAlert>> CheckForAlertsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<HeartbeatAlert>>([]);
}
