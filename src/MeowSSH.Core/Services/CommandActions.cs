using System.Text.Json;
using System.Text.Json.Serialization;
using MeowSSH.Core.Licensing;
using MeowSSH.Core.Model;
using MeowSSH.Core.Ssh;

namespace MeowSSH.Core.Services;

/// <summary>A reusable command that can target one or more saved SSH hosts.</summary>
public sealed record CommandAction(
    Guid Id,
    string Name,
    string Command,
    IReadOnlyList<Guid> HostIds,
    int TimeoutSeconds = 30)
{
    public static CommandAction Create(string name) => new(
        Guid.NewGuid(), name, string.Empty, [], TimeoutSeconds: 30);
}

public enum CommandActionHostState
{
    Succeeded,
    Failed,
    ConnectionFailed,
}

/// <summary>The result of running one action against one host.</summary>
public sealed record CommandActionHostResult(
    Guid HostId,
    string HostLabel,
    CommandActionHostState State,
    int? ExitCode,
    string StandardOutput,
    string StandardError,
    SshFailure? Failure,
    string? Message,
    TimeSpan Duration);

/// <summary>A completed action run, preserving one independently inspectable result per host.</summary>
public sealed record CommandActionRun(
    Guid ActionId,
    string ActionName,
    DateTimeOffset StartedAtUtc,
    TimeSpan Duration,
    IReadOnlyList<CommandActionHostResult> Hosts)
{
    public bool Succeeded => Hosts.Count > 0 && Hosts.All(static host => host.State == CommandActionHostState.Succeeded);
}

public interface ICommandActionStore
{
    Task<IReadOnlyList<CommandAction>> GetAllAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(CommandAction action, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}

public interface ICommandActionService
{
    event EventHandler? Changed;

    Task<IReadOnlyList<CommandAction>> GetAllAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(CommandAction action, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
    Task<CommandActionRun> RunAsync(Guid id, CancellationToken cancellationToken = default);
}

/// <summary>
/// App-private JSON persistence for Actions. Only action definitions and saved-host IDs are
/// stored here; authentication material remains exclusively in the vault.
/// </summary>
public sealed class FileCommandActionStore(string path) : ICommandActionStore
{
    private readonly object _gate = new();
    private readonly string _path = string.IsNullOrWhiteSpace(path)
        ? throw new ArgumentException("An Actions file path is required.", nameof(path))
        : path;

    public Task<IReadOnlyList<CommandAction>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            IReadOnlyList<CommandAction> actions = ReadUnsafe()
                .OrderBy(static action => action.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return Task.FromResult(actions);
        }
    }

    public Task SaveAsync(CommandAction action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        cancellationToken.ThrowIfCancellationRequested();
        var normalized = NormalizeAndValidate(action);

        lock (_gate)
        {
            var actions = ReadUnsafe();
            var index = actions.FindIndex(item => item.Id == normalized.Id);
            if (index >= 0) actions[index] = normalized;
            else actions.Add(normalized);
            WriteUnsafe(actions);
        }

        return Task.CompletedTask;
    }

    public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (id == Guid.Empty) throw new ArgumentException("An Action ID is required.", nameof(id));

        lock (_gate)
        {
            var actions = ReadUnsafe();
            if (actions.RemoveAll(action => action.Id == id) > 0)
                WriteUnsafe(actions);
        }
        return Task.CompletedTask;
    }

    private List<CommandAction> ReadUnsafe()
    {
        if (!File.Exists(_path)) return [];
        try
        {
            var json = File.ReadAllText(_path);
            return JsonSerializer.Deserialize(json, CommandActionJsonContext.Default.CommandActionDocument)?.Actions
                .Select(Normalize)
                .ToList() ?? [];
        }
        catch (JsonException)
        {
            // Preserve a corrupt file for recovery/support instead of destroying it.
            return [];
        }
    }

    private void WriteUnsafe(IReadOnlyList<CommandAction> actions)
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);

        var document = new CommandActionDocument([
            .. actions.OrderBy(static action => action.Name, StringComparer.OrdinalIgnoreCase),
        ]);
        var json = JsonSerializer.Serialize(document, CommandActionJsonContext.Default.CommandActionDocument);
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, json);
        File.Move(temporary, _path, overwrite: true);
    }

    internal static CommandAction NormalizeAndValidate(CommandAction action)
    {
        var normalized = Normalize(action);
        if (normalized.Id == Guid.Empty)
            throw new ArgumentException("An Action ID is required.", nameof(action));
        if (string.IsNullOrWhiteSpace(normalized.Name))
            throw new ArgumentException("An Action name is required.", nameof(action));
        if (normalized.Name.Length > 80)
            throw new ArgumentException("Action names must be 80 characters or fewer.", nameof(action));
        if (string.IsNullOrWhiteSpace(normalized.Command))
            throw new ArgumentException("An Action command is required.", nameof(action));
        if (normalized.Command.Length > 16_384)
            throw new ArgumentException("Action commands must be 16 KiB or smaller.", nameof(action));
        if (normalized.HostIds.Count is < 1 or > 20)
            throw new ArgumentException("An Action must target between 1 and 20 hosts.", nameof(action));
        if (normalized.TimeoutSeconds is < 1 or > 3_600)
            throw new ArgumentException("Action timeout must be between 1 and 3600 seconds.", nameof(action));
        return normalized;
    }

    private static CommandAction Normalize(CommandAction action) => action with
    {
        Name = action.Name.Trim(),
        Command = action.Command.Trim(),
        HostIds = action.HostIds.Where(static id => id != Guid.Empty).Distinct().ToArray(),
    };
}

public sealed class MemoryCommandActionStore : ICommandActionStore
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, CommandAction> _actions = [];

    public Task<IReadOnlyList<CommandAction>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            IReadOnlyList<CommandAction> result = [
                .. _actions.Values.OrderBy(static action => action.Name, StringComparer.OrdinalIgnoreCase),
            ];
            return Task.FromResult(result);
        }
    }

    public Task SaveAsync(CommandAction action, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalized = FileCommandActionStore.NormalizeAndValidate(action);
        lock (_gate) _actions[normalized.Id] = normalized;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate) _actions.Remove(id);
        return Task.CompletedTask;
    }
}

public sealed class CommandActionService(
    ICommandActionStore store,
    IHostDirectory hostDirectory,
    ICredentialResolver credentials,
    ISshEngine engine,
    ISshPrompts prompts,
    IEntitlementService entitlements,
    TimeProvider? timeProvider = null) : ICommandActionService
{
    private const int MaxParallelHosts = 4;
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public event EventHandler? Changed;

    public Task<IReadOnlyList<CommandAction>> GetAllAsync(CancellationToken cancellationToken = default) =>
        store.GetAllAsync(cancellationToken);

    public async Task SaveAsync(CommandAction action, CancellationToken cancellationToken = default)
    {
        await store.SaveAsync(action, cancellationToken).ConfigureAwait(false);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await store.DeleteAsync(id, cancellationToken).ConfigureAwait(false);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task<CommandActionRun> RunAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var action = (await store.GetAllAsync(cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(item => item.Id == id)
            ?? throw new KeyNotFoundException("The Action no longer exists.");

        if (action.HostIds.Count > 1 && !entitlements.Has(PremiumFeature.MultiHostActions))
            throw new InvalidOperationException("Running an Action on multiple hosts requires MeowSSH Pro.");

        var allHosts = await hostDirectory.GetHostsAsync(cancellationToken).ConfigureAwait(false);
        var byId = allHosts.ToDictionary(static status => status.Host.Id, static status => status.Host);
        var started = _timeProvider.GetUtcNow();
        using var concurrency = new SemaphoreSlim(MaxParallelHosts, MaxParallelHosts);

        var tasks = action.HostIds.Select((hostId, index) => RunTargetAsync(
            action,
            hostId,
            index,
            byId,
            concurrency,
            cancellationToken)).ToArray();
        var indexed = await Task.WhenAll(tasks).ConfigureAwait(false);
        var completed = _timeProvider.GetUtcNow();

        return new CommandActionRun(
            action.Id,
            action.Name,
            started,
            completed - started,
            [.. indexed.OrderBy(static result => result.Index).Select(static result => result.Result)]);
    }

    private async Task<IndexedResult> RunTargetAsync(
        CommandAction action,
        Guid hostId,
        int index,
        Dictionary<Guid, HostRecord> hosts,
        SemaphoreSlim concurrency,
        CancellationToken cancellationToken)
    {
        await concurrency.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!hosts.TryGetValue(hostId, out var host))
            {
                return new IndexedResult(index, new CommandActionHostResult(
                    hostId, "Deleted host", CommandActionHostState.ConnectionFailed, null,
                    string.Empty, string.Empty, null, "This saved host no longer exists.", TimeSpan.Zero));
            }

            if (host.Protocol != HostProtocol.Ssh)
            {
                return new IndexedResult(index, new CommandActionHostResult(
                    host.Id, host.Label, CommandActionHostState.ConnectionFailed, null,
                    string.Empty, string.Empty, null, "Actions currently support SSH hosts only.", TimeSpan.Zero));
            }

            var started = _timeProvider.GetUtcNow();
            ISshConnection? connection = null;
            try
            {
                using var resolved = await credentials.ResolveAsync(host, cancellationToken).ConfigureAwait(false);
                var target = !string.IsNullOrWhiteSpace(resolved.Username)
                    ? host with { Username = resolved.Username }
                    : host;
                connection = await engine.ConnectAsync(target, resolved, prompts, cancellationToken).ConfigureAwait(false);
                var result = await connection.RunCommandAsync(
                    action.Command,
                    TimeSpan.FromSeconds(action.TimeoutSeconds),
                    cancellationToken).ConfigureAwait(false);
                var duration = _timeProvider.GetUtcNow() - started;

                return new IndexedResult(index, new CommandActionHostResult(
                    host.Id,
                    host.Label,
                    result.Succeeded ? CommandActionHostState.Succeeded : CommandActionHostState.Failed,
                    result.ExitCode,
                    LimitOutput(result.StandardOutput),
                    LimitOutput(result.StandardError),
                    null,
                    result.Succeeded ? null : $"Command exited with status {result.ExitCode}.",
                    duration));
            }
            catch (SshException exception)
            {
                return new IndexedResult(index, new CommandActionHostResult(
                    host.Id,
                    host.Label,
                    CommandActionHostState.ConnectionFailed,
                    null,
                    string.Empty,
                    string.Empty,
                    exception.Failure,
                    exception.Message,
                    _timeProvider.GetUtcNow() - started));
            }
            finally
            {
                if (connection is not null)
                    await connection.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            concurrency.Release();
        }
    }

    // Keep an accidental `cat` of a huge file from retaining unbounded text in the UI model.
    private static string LimitOutput(string value)
    {
        const int limit = 256 * 1024;
        if (value.Length <= limit) return value;
        return value[..limit] + "\n… output truncated by MeowSSH …";
    }

    private sealed record IndexedResult(int Index, CommandActionHostResult Result);
}

public sealed record CommandActionDocument(IReadOnlyList<CommandAction> Actions);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, WriteIndented = true)]
[JsonSerializable(typeof(CommandActionDocument))]
internal sealed partial class CommandActionJsonContext : JsonSerializerContext
{
}
