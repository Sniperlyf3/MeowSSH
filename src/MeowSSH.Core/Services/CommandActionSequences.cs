using System.Text.Json;
using System.Text.Json.Serialization;
using MeowSSH.Core.Licensing;
using MeowSSH.Core.Model;
using MeowSSH.Core.Ssh;

namespace MeowSSH.Core.Services;

public sealed record CommandActionSequence(
    Guid ActionId,
    IReadOnlyList<string> AdditionalCommands,
    bool StopOnError = true);

public sealed record CommandActionStepResult(
    int StepNumber,
    string CommandTemplate,
    int ExitCode,
    string StandardOutput,
    string StandardError,
    TimeSpan Duration)
{
    public bool Succeeded => ExitCode == 0;
}

public sealed record CommandActionSequenceHostResult(
    Guid HostId,
    string HostLabel,
    IReadOnlyList<CommandActionStepResult> Steps,
    string? ConnectionError,
    TimeSpan Duration)
{
    public bool Succeeded => ConnectionError is null && Steps.Count > 0 && Steps.All(static step => step.Succeeded);
}

public sealed record CommandActionSequenceRun(
    Guid ActionId,
    string ActionName,
    DateTimeOffset StartedAtUtc,
    TimeSpan Duration,
    IReadOnlyList<CommandActionSequenceHostResult> Hosts)
{
    public bool Succeeded => Hosts.Count > 0 && Hosts.All(static host => host.Succeeded);
}

public interface ICommandActionSequenceStore
{
    Task<CommandActionSequence?> GetAsync(Guid actionId, CancellationToken cancellationToken = default);
    Task SaveAsync(CommandActionSequence sequence, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid actionId, CancellationToken cancellationToken = default);
}

public interface ICommandActionSequenceService
{
    Task<CommandActionSequence?> GetAsync(Guid actionId, CancellationToken cancellationToken = default);
    Task SaveAsync(CommandActionSequence sequence, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid actionId, CancellationToken cancellationToken = default);
    Task<CommandActionSequenceRun> RunAsync(
        Guid actionId,
        IReadOnlyDictionary<string, string>? values = null,
        CancellationToken cancellationToken = default);
}

public sealed class CommandActionSequenceService(
    ICommandActionSequenceStore sequences,
    ICommandActionStore actions,
    IHostDirectory hostDirectory,
    ICredentialResolver credentials,
    ISshEngine engine,
    ISshPrompts prompts,
    IEntitlementService entitlements,
    TimeProvider? timeProvider = null) : ICommandActionSequenceService
{
    internal const int MaxAdditionalSteps = 9;
    private const int MaxParallelHosts = 4;
    private const int MaxOutputLength = 256 * 1024;
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<CommandActionSequence?> GetAsync(Guid actionId, CancellationToken cancellationToken = default)
    {
        RequirePro();
        return await sequences.GetAsync(actionId, cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveAsync(CommandActionSequence sequence, CancellationToken cancellationToken = default)
    {
        RequirePro();
        ArgumentNullException.ThrowIfNull(sequence);
        if (sequence.ActionId == Guid.Empty) throw new ArgumentException("An Action ID is required.", nameof(sequence));
        if (sequence.AdditionalCommands.Count is < 1 or > MaxAdditionalSteps)
            throw new ArgumentException($"A sequence must contain between 1 and {MaxAdditionalSteps} additional commands.", nameof(sequence));

        var normalized = sequence with
        {
            AdditionalCommands = [.. sequence.AdditionalCommands.Select(static command => command.Trim())],
        };
        if (normalized.AdditionalCommands.Any(static command => string.IsNullOrWhiteSpace(command)))
            throw new ArgumentException("Sequence commands cannot be empty.", nameof(sequence));
        if (normalized.AdditionalCommands.Any(static command => command.Length > 16_384))
            throw new ArgumentException("Each sequence command must be 16 KiB or smaller.", nameof(sequence));

        var actionExists = (await actions.GetAllAsync(cancellationToken).ConfigureAwait(false))
            .Any(action => action.Id == sequence.ActionId);
        if (!actionExists) throw new KeyNotFoundException("The Action no longer exists.");

        await sequences.SaveAsync(normalized, cancellationToken).ConfigureAwait(false);
    }

    public Task DeleteAsync(Guid actionId, CancellationToken cancellationToken = default)
    {
        RequirePro();
        return sequences.DeleteAsync(actionId, cancellationToken);
    }

    public async Task<CommandActionSequenceRun> RunAsync(
        Guid actionId,
        IReadOnlyDictionary<string, string>? values = null,
        CancellationToken cancellationToken = default)
    {
        RequirePro();
        var action = (await actions.GetAllAsync(cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(item => item.Id == actionId)
            ?? throw new KeyNotFoundException("The Action no longer exists.");
        var sequence = await sequences.GetAsync(actionId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Configure additional steps before running this Action as a sequence.");

        if (action.HostIds.Count > 1 && !entitlements.Has(PremiumFeature.MultiHostActions))
            throw new InvalidOperationException("Running an Action on multiple hosts requires MeowSSH Pro.");

        var templates = new[] { action.Command }.Concat(sequence.AdditionalCommands).ToArray();
        var runtimeValues = values ?? new Dictionary<string, string>();
        // Render all steps before resolving credentials so missing values fail without network activity.
        var rendered = templates.Select(template => CommandActionTemplate.Render(template, runtimeValues)).ToArray();

        var allHosts = await hostDirectory.GetHostsAsync(cancellationToken).ConfigureAwait(false);
        var byId = allHosts.ToDictionary(static status => status.Host.Id, static status => status.Host);
        var started = _timeProvider.GetUtcNow();
        using var concurrency = new SemaphoreSlim(MaxParallelHosts, MaxParallelHosts);
        var tasks = action.HostIds.Select((hostId, index) => RunTargetAsync(
            action,
            sequence,
            templates,
            rendered,
            hostId,
            index,
            byId,
            concurrency,
            cancellationToken)).ToArray();
        var indexed = await Task.WhenAll(tasks).ConfigureAwait(false);
        var completed = _timeProvider.GetUtcNow();

        return new CommandActionSequenceRun(
            action.Id,
            action.Name,
            started,
            completed - started,
            [.. indexed.OrderBy(static item => item.Index).Select(static item => item.Result)]);
    }

    private async Task<IndexedResult> RunTargetAsync(
        CommandAction action,
        CommandActionSequence sequence,
        IReadOnlyList<string> templates,
        IReadOnlyList<string> rendered,
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
                return Failure(index, hostId, "Deleted host", "This saved host no longer exists.");
            if (host.Protocol != HostProtocol.Ssh)
                return Failure(index, host.Id, host.Label, "Action sequences currently support SSH hosts only.");

            var started = _timeProvider.GetUtcNow();
            ISshConnection? connection = null;
            try
            {
                using var resolvedCredentials = await credentials.ResolveAsync(host, cancellationToken).ConfigureAwait(false);
                var target = !string.IsNullOrWhiteSpace(resolvedCredentials.Username)
                    ? host with { Username = resolvedCredentials.Username }
                    : host;
                connection = await engine.ConnectAsync(target, resolvedCredentials, prompts, cancellationToken).ConfigureAwait(false);

                var stepResults = new List<CommandActionStepResult>();
                for (var step = 0; step < rendered.Count; step++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var stepStarted = _timeProvider.GetUtcNow();
                    var result = await connection.RunCommandAsync(
                        rendered[step],
                        TimeSpan.FromSeconds(action.TimeoutSeconds),
                        cancellationToken).ConfigureAwait(false);
                    stepResults.Add(new CommandActionStepResult(
                        step + 1,
                        templates[step],
                        result.ExitCode,
                        LimitOutput(result.StandardOutput),
                        LimitOutput(result.StandardError),
                        _timeProvider.GetUtcNow() - stepStarted));
                    if (!result.Succeeded && sequence.StopOnError) break;
                }

                return new IndexedResult(index, new CommandActionSequenceHostResult(
                    host.Id,
                    host.Label,
                    stepResults,
                    null,
                    _timeProvider.GetUtcNow() - started));
            }
            catch (SshException exception)
            {
                return Failure(index, host.Id, host.Label, exception.Message, _timeProvider.GetUtcNow() - started);
            }
            finally
            {
                if (connection is not null) await connection.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            concurrency.Release();
        }
    }

    private static IndexedResult Failure(int index, Guid hostId, string label, string message, TimeSpan? duration = null) =>
        new(index, new CommandActionSequenceHostResult(hostId, label, [], message, duration ?? TimeSpan.Zero));

    private void RequirePro()
    {
        if (!entitlements.Has(PremiumFeature.ActionSequences))
            throw new InvalidOperationException("Multi-step Actions require MeowSSH Pro.");
    }

    private static string LimitOutput(string value) =>
        value.Length <= MaxOutputLength ? value : value[..MaxOutputLength] + "\n… output truncated by MeowSSH …";

    private sealed record IndexedResult(int Index, CommandActionSequenceHostResult Result);
}

public sealed class MemoryCommandActionSequenceStore : ICommandActionSequenceStore
{
    private readonly Dictionary<Guid, CommandActionSequence> _items = [];
    public Task<CommandActionSequence?> GetAsync(Guid actionId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_items.GetValueOrDefault(actionId));
    public Task SaveAsync(CommandActionSequence sequence, CancellationToken cancellationToken = default)
    {
        _items[sequence.ActionId] = sequence;
        return Task.CompletedTask;
    }
    public Task DeleteAsync(Guid actionId, CancellationToken cancellationToken = default)
    {
        _items.Remove(actionId);
        return Task.CompletedTask;
    }
}

public sealed class FileCommandActionSequenceStore(string path) : ICommandActionSequenceStore
{
    private readonly object _gate = new();
    private readonly string _path = string.IsNullOrWhiteSpace(path)
        ? throw new ArgumentException("A sequence file path is required.", nameof(path))
        : path;

    public Task<CommandActionSequence?> GetAsync(Guid actionId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate) return Task.FromResult(ReadUnsafe().FirstOrDefault(item => item.ActionId == actionId));
    }

    public Task SaveAsync(CommandActionSequence sequence, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var items = ReadUnsafe();
            var index = items.FindIndex(item => item.ActionId == sequence.ActionId);
            if (index >= 0) items[index] = sequence;
            else items.Add(sequence);
            WriteUnsafe(items);
        }
        return Task.CompletedTask;
    }

    public Task DeleteAsync(Guid actionId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var items = ReadUnsafe();
            if (items.RemoveAll(item => item.ActionId == actionId) > 0) WriteUnsafe(items);
        }
        return Task.CompletedTask;
    }

    private List<CommandActionSequence> ReadUnsafe()
    {
        if (!File.Exists(_path)) return [];
        try
        {
            return JsonSerializer.Deserialize(
                File.ReadAllText(_path),
                CommandActionSequenceJsonContext.Default.CommandActionSequenceDocument)?.Sequences.ToList() ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private void WriteUnsafe(IReadOnlyList<CommandActionSequence> items)
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(
            new CommandActionSequenceDocument([.. items.OrderBy(static item => item.ActionId)]),
            CommandActionSequenceJsonContext.Default.CommandActionSequenceDocument));
        File.Move(temporary, _path, overwrite: true);
    }
}

public sealed record CommandActionSequenceDocument(IReadOnlyList<CommandActionSequence> Sequences);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, WriteIndented = true)]
[JsonSerializable(typeof(CommandActionSequenceDocument))]
internal sealed partial class CommandActionSequenceJsonContext : JsonSerializerContext
{
}
