using MeowSSH.Core.Licensing;
using MeowSSH.Core.Services;
using MeowSSH.Core.Ssh;

namespace MeowSSH.UI.Services;

public sealed record BroadcastSessionInfo(Guid Id, string Label, bool IsSource, bool IsTarget);

public sealed class TerminalBroadcastCoordinator(
    ITerminalBroadcastService broadcaster,
    IEntitlementService entitlements)
{
    private readonly Dictionary<Guid, Registration> _registrations = [];
    private Guid? _sourceId;
    private readonly HashSet<Guid> _targetIds = [];

    public event EventHandler? Changed;

    public Guid Register(string label, ITerminalSession session)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        ArgumentNullException.ThrowIfNull(session);
        var id = Guid.NewGuid();
        _registrations[id] = new Registration(label, session);
        Changed?.Invoke(this, EventArgs.Empty);
        return id;
    }

    public void Unregister(Guid id)
    {
        _registrations.Remove(id);
        _targetIds.Remove(id);
        if (_sourceId == id) Disarm();
        else Changed?.Invoke(this, EventArgs.Empty);
    }

    public IReadOnlyList<BroadcastSessionInfo> Sessions(Guid sourceId) =>
        [.. _registrations
            .OrderBy(pair => pair.Value.Label, StringComparer.OrdinalIgnoreCase)
            .Select(pair => new BroadcastSessionInfo(
                pair.Key,
                pair.Value.Label,
                pair.Key == sourceId,
                _targetIds.Contains(pair.Key)))];

    public bool IsArmed(Guid sourceId) => _sourceId == sourceId && _targetIds.Count > 0;

    public void Arm(Guid sourceId, IReadOnlyCollection<Guid> targetIds)
    {
        if (!entitlements.Has(PremiumFeature.BroadcastInput))
            throw new InvalidOperationException("Broadcast Input requires MeowSSH Pro.");
        if (!_registrations.ContainsKey(sourceId))
            throw new InvalidOperationException("The source terminal is no longer open.");

        var normalized = targetIds
            .Where(id => id != sourceId)
            .Distinct()
            .Where(_registrations.ContainsKey)
            .Take(TerminalBroadcastService.MaxTargets - 1)
            .ToArray();
        if (normalized.Length == 0)
            throw new InvalidOperationException("Select at least one additional open session to broadcast to.");

        _sourceId = sourceId;
        _targetIds.Clear();
        foreach (var id in normalized) _targetIds.Add(id);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Disarm()
    {
        _sourceId = null;
        _targetIds.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task<TerminalBroadcastResult?> WriteAsync(
        Guid sourceId,
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken = default)
    {
        if (!_registrations.TryGetValue(sourceId, out var source))
            throw new InvalidOperationException("The terminal is no longer registered.");

        if (_sourceId != sourceId || _targetIds.Count == 0)
        {
            await source.Session.WriteAsync(data, cancellationToken).ConfigureAwait(false);
            return null;
        }

        var targets = new List<TerminalBroadcastTarget>
        {
            new(sourceId, source.Session),
        };
        foreach (var id in _targetIds)
        {
            if (_registrations.TryGetValue(id, out var registration))
                targets.Add(new TerminalBroadcastTarget(id, registration.Session));
        }

        if (targets.Count < 2)
        {
            Disarm();
            await source.Session.WriteAsync(data, cancellationToken).ConfigureAwait(false);
            return null;
        }

        return await broadcaster.WriteAsync(targets, data, cancellationToken).ConfigureAwait(false);
    }

    private sealed record Registration(string Label, ITerminalSession Session);
}

/// <summary>
/// Terminal-session adapter used only by the UI. It reroutes writes through the scoped coordinator
/// but leaves output, resize, exit, and ownership/disposal semantics with the underlying session.
/// </summary>
public sealed class BroadcastTerminalSession(
    string label,
    ITerminalSession inner,
    TerminalBroadcastCoordinator coordinator) : ITerminalSession
{
    private readonly Guid _registrationId = coordinator.Register(label, inner);
    private bool _disposed;

    public Guid RegistrationId => _registrationId;

    public event EventHandler<ReadOnlyMemory<byte>>? OutputReceived
    {
        add => inner.OutputReceived += value;
        remove => inner.OutputReceived -= value;
    }

    public event EventHandler<int>? Exited
    {
        add => inner.Exited += value;
        remove => inner.Exited -= value;
    }

    public async Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        var result = await coordinator.WriteAsync(_registrationId, data, cancellationToken).ConfigureAwait(false);
        if (result is { Failures.Count: > 0 })
            throw new InvalidOperationException(
                $"Broadcast reached {result.Succeeded} of {result.Attempted} sessions. {result.Failures.Count} session(s) failed.");
    }

    public Task ResizeAsync(int columns, int rows, CancellationToken cancellationToken = default) =>
        inner.ResizeAsync(columns, rows, cancellationToken);

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        coordinator.Unregister(_registrationId);
        return ValueTask.CompletedTask;
    }
}
