using System.Text.Json;
using System.Text.Json.Serialization;
using MeowSSH.Core.Licensing;

namespace MeowSSH.Core.Services;

public sealed record TailcatWorkspace(
    Guid Id,
    string Name,
    string Address,
    string? ClientKey,
    string? DerpMapUrl,
    bool StartSocks,
    string SocksListenAddress,
    IReadOnlyList<TailcatWorkspaceForward> Forwards)
{
    public static TailcatWorkspace Create(string name) => new(
        Guid.NewGuid(),
        name,
        string.Empty,
        null,
        null,
        StartSocks: true,
        SocksListenAddress: "127.0.0.1:0",
        Forwards: []);
}

public sealed record TailcatWorkspaceForward(
    IReadOnlyList<string> Mappings,
    string BindAddress = "127.0.0.1",
    bool Udp = false);

public sealed record TailcatWorkspaceActivation(
    Guid WorkspaceId,
    string WorkspaceName,
    bool OwnsSocks,
    IReadOnlyList<Guid> ForwardIds,
    DateTimeOffset StartedAtUtc);

public interface ITailcatWorkspaceStore
{
    Task<IReadOnlyList<TailcatWorkspace>> GetAllAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(TailcatWorkspace workspace, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}

public interface ITailcatWorkspaceService
{
    TailcatWorkspaceActivation? Active { get; }
    event EventHandler? Changed;

    Task<IReadOnlyList<TailcatWorkspace>> GetAllAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(TailcatWorkspace workspace, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
    Task StartAsync(Guid id, CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Small local JSON store for reusable Tailcat connection bundles. The file contains no
/// entitlement secrets or private signing material; it only stores user-entered Tailcat
/// addresses, key names and forwarding definitions.
/// </summary>
public sealed class FileTailcatWorkspaceStore(string path) : ITailcatWorkspaceStore
{
    private readonly object _gate = new();
    private readonly string _path = string.IsNullOrWhiteSpace(path)
        ? throw new ArgumentException("A workspace file path is required.", nameof(path))
        : path;

    public Task<IReadOnlyList<TailcatWorkspace>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!File.Exists(_path))
                return Task.FromResult<IReadOnlyList<TailcatWorkspace>>([]);

            try
            {
                var json = File.ReadAllText(_path);
                var document = JsonSerializer.Deserialize(json, TailcatWorkspaceJsonContext.Default.TailcatWorkspaceDocument);
                IReadOnlyList<TailcatWorkspace> workspaces = document?.Workspaces
                    .Select(Normalize)
                    .OrderBy(static workspace => workspace.Name, StringComparer.OrdinalIgnoreCase)
                    .ToArray() ?? [];
                return Task.FromResult(workspaces);
            }
            catch (JsonException)
            {
                // Corrupt user configuration should fail closed without taking the rest of
                // Tailcat down. Preserve the bad file for support/recovery instead of deleting it.
                return Task.FromResult<IReadOnlyList<TailcatWorkspace>>([]);
            }
        }
    }

    public Task SaveAsync(TailcatWorkspace workspace, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        cancellationToken.ThrowIfCancellationRequested();
        var normalized = NormalizeAndValidate(workspace);

        lock (_gate)
        {
            var existing = ReadUnsafe();
            var index = existing.FindIndex(item => item.Id == normalized.Id);
            if (index >= 0) existing[index] = normalized;
            else existing.Add(normalized);

            WriteUnsafe(existing);
        }

        return Task.CompletedTask;
    }

    public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (id == Guid.Empty) throw new ArgumentException("A workspace ID is required.", nameof(id));

        lock (_gate)
        {
            var existing = ReadUnsafe();
            if (existing.RemoveAll(item => item.Id == id) > 0)
                WriteUnsafe(existing);
        }

        return Task.CompletedTask;
    }

    private List<TailcatWorkspace> ReadUnsafe()
    {
        if (!File.Exists(_path)) return [];
        try
        {
            var json = File.ReadAllText(_path);
            return JsonSerializer.Deserialize(json, TailcatWorkspaceJsonContext.Default.TailcatWorkspaceDocument)?.Workspaces
                .Select(Normalize)
                .ToList() ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private void WriteUnsafe(IReadOnlyList<TailcatWorkspace> workspaces)
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);

        var document = new TailcatWorkspaceDocument([.. workspaces.OrderBy(static item => item.Name, StringComparer.OrdinalIgnoreCase)]);
        var json = JsonSerializer.Serialize(document, TailcatWorkspaceJsonContext.Default.TailcatWorkspaceDocument);
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, json);
        File.Move(temporary, _path, overwrite: true);
    }

    private static TailcatWorkspace NormalizeAndValidate(TailcatWorkspace workspace)
    {
        var normalized = Normalize(workspace);
        if (normalized.Id == Guid.Empty)
            throw new ArgumentException("A workspace ID is required.", nameof(workspace));
        if (string.IsNullOrWhiteSpace(normalized.Name))
            throw new ArgumentException("A workspace name is required.", nameof(workspace));
        if (normalized.Name.Length > 80)
            throw new ArgumentException("Workspace names must be 80 characters or fewer.", nameof(workspace));
        if (normalized.StartSocks && string.IsNullOrWhiteSpace(normalized.SocksListenAddress))
            throw new ArgumentException("A SOCKS listen address is required when the workspace starts SOCKS.", nameof(workspace));
        if (normalized.Forwards.Count > 20)
            throw new ArgumentException("A workspace can contain at most 20 forwarding groups.", nameof(workspace));
        if (normalized.Forwards.Any(static forward => forward.Mappings.Count is < 1 or > 50))
            throw new ArgumentException("Each forwarding group must contain between 1 and 50 mappings.", nameof(workspace));
        return normalized;
    }

    private static TailcatWorkspace Normalize(TailcatWorkspace workspace) => workspace with
    {
        Name = workspace.Name.Trim(),
        Address = workspace.Address.Trim(),
        ClientKey = Empty(workspace.ClientKey),
        DerpMapUrl = Empty(workspace.DerpMapUrl),
        SocksListenAddress = string.IsNullOrWhiteSpace(workspace.SocksListenAddress)
            ? "127.0.0.1:0"
            : workspace.SocksListenAddress.Trim(),
        Forwards = workspace.Forwards
            .Where(static forward => forward.Mappings.Count > 0)
            .Select(static forward => forward with
            {
                Mappings = forward.Mappings
                    .Where(static mapping => !string.IsNullOrWhiteSpace(mapping))
                    .Select(static mapping => mapping.Trim())
                    .Distinct(StringComparer.Ordinal)
                    .ToArray(),
                BindAddress = string.IsNullOrWhiteSpace(forward.BindAddress) ? "127.0.0.1" : forward.BindAddress.Trim(),
            })
            .Where(static forward => forward.Mappings.Count > 0)
            .ToArray(),
    };

    private static string? Empty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed class MemoryTailcatWorkspaceStore : ITailcatWorkspaceStore
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, TailcatWorkspace> _workspaces = [];

    public Task<IReadOnlyList<TailcatWorkspace>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            IReadOnlyList<TailcatWorkspace> result = [.. _workspaces.Values.OrderBy(static item => item.Name, StringComparer.OrdinalIgnoreCase)];
            return Task.FromResult(result);
        }
    }

    public Task SaveAsync(TailcatWorkspace workspace, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate) _workspaces[workspace.Id] = workspace;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate) _workspaces.Remove(id);
        return Task.CompletedTask;
    }
}

public sealed class TailcatWorkspaceService(
    ITailcatWorkspaceStore store,
    ITailcatHubService hub,
    IEntitlementService entitlements,
    TimeProvider? timeProvider = null) : ITailcatWorkspaceService
{
    private readonly object _stateGate = new();
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private TailcatWorkspaceActivation? _active;
    private bool _busy;

    public TailcatWorkspaceActivation? Active
    {
        get { lock (_stateGate) return _active; }
    }

    public event EventHandler? Changed;

    public Task<IReadOnlyList<TailcatWorkspace>> GetAllAsync(CancellationToken cancellationToken = default) =>
        store.GetAllAsync(cancellationToken);

    public async Task SaveAsync(TailcatWorkspace workspace, CancellationToken cancellationToken = default)
    {
        RequirePro();
        ArgumentNullException.ThrowIfNull(workspace);
        await store.SaveAsync(workspace, cancellationToken).ConfigureAwait(false);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        RequirePro();
        if (Active?.WorkspaceId == id)
            throw new InvalidOperationException("Stop the active workspace before deleting it.");
        await store.DeleteAsync(id, cancellationToken).ConfigureAwait(false);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task StartAsync(Guid id, CancellationToken cancellationToken = default)
    {
        RequirePro();
        EnterOperation();
        try
        {
            if (Active is not null)
                throw new InvalidOperationException("Stop the active Tailcat workspace before starting another one.");

            var workspace = (await store.GetAllAsync(cancellationToken).ConfigureAwait(false))
                .FirstOrDefault(item => item.Id == id)
                ?? throw new KeyNotFoundException("The Tailcat workspace no longer exists.");
            if (string.IsNullOrWhiteSpace(workspace.Address) && workspace.Forwards.Count > 0)
                throw new InvalidOperationException("A Tailcat server address is required when the workspace contains forwards.");

            var startedForwards = new List<Guid>();
            var ownsSocks = false;
            try
            {
                if (workspace.StartSocks && hub.Snapshot.Socks is null)
                {
                    await hub.StartSocksAsync(new TailcatSocksRequest(
                        workspace.SocksListenAddress,
                        workspace.ClientKey,
                        workspace.DerpMapUrl), cancellationToken).ConfigureAwait(false);
                    ownsSocks = true;
                }

                foreach (var forward in workspace.Forwards)
                {
                    var snapshot = await hub.StartForwardAsync(new TailcatForwardRequest(
                        workspace.Address,
                        forward.Mappings,
                        forward.BindAddress,
                        workspace.ClientKey,
                        workspace.DerpMapUrl,
                        forward.Udp), cancellationToken).ConfigureAwait(false);
                    startedForwards.Add(snapshot.Id);
                }

                lock (_stateGate)
                {
                    _active = new TailcatWorkspaceActivation(
                        workspace.Id,
                        workspace.Name,
                        ownsSocks,
                        [.. startedForwards],
                        _timeProvider.GetUtcNow());
                }
                Changed?.Invoke(this, EventArgs.Empty);
            }
            catch
            {
                for (var index = startedForwards.Count - 1; index >= 0; index--)
                {
                    try { await hub.StopForwardAsync(startedForwards[index], CancellationToken.None).ConfigureAwait(false); }
                    catch { }
                }
                if (ownsSocks)
                {
                    try { await hub.StopSocksAsync(CancellationToken.None).ConfigureAwait(false); }
                    catch { }
                }
                throw;
            }
        }
        finally
        {
            ExitOperation();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        // Deliberately no entitlement check: users must always be able to stop network
        // activity that was started while an entitlement was valid.
        EnterOperation();
        try
        {
            var active = Active;
            if (active is null) return;

            List<Exception>? failures = null;
            for (var index = active.ForwardIds.Count - 1; index >= 0; index--)
            {
                try { await hub.StopForwardAsync(active.ForwardIds[index], cancellationToken).ConfigureAwait(false); }
                catch (Exception exception) { (failures ??= []).Add(exception); }
            }

            if (active.OwnsSocks)
            {
                try { await hub.StopSocksAsync(cancellationToken).ConfigureAwait(false); }
                catch (Exception exception) { (failures ??= []).Add(exception); }
            }

            lock (_stateGate) _active = null;
            Changed?.Invoke(this, EventArgs.Empty);
            if (failures is { Count: > 0 })
                throw new AggregateException("One or more Tailcat workspace resources could not be stopped.", failures);
        }
        finally
        {
            ExitOperation();
        }
    }

    private void RequirePro()
    {
        if (!entitlements.Has(PremiumFeature.TailcatWorkspaces))
            throw new InvalidOperationException("Tailcat Workspaces require MeowSSH Pro.");
    }

    private void EnterOperation()
    {
        lock (_stateGate)
        {
            if (_busy) throw new InvalidOperationException("Another Tailcat workspace operation is already in progress.");
            _busy = true;
        }
    }

    private void ExitOperation()
    {
        lock (_stateGate) _busy = false;
    }
}

public sealed record TailcatWorkspaceDocument(IReadOnlyList<TailcatWorkspace> Workspaces);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, WriteIndented = true)]
[JsonSerializable(typeof(TailcatWorkspaceDocument))]
internal sealed partial class TailcatWorkspaceJsonContext : JsonSerializerContext
{
}
