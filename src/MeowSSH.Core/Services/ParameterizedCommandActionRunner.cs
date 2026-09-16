using MeowSSH.Core.Licensing;
using MeowSSH.Core.Model;
using MeowSSH.Core.Ssh;

namespace MeowSSH.Core.Services;

public interface IParameterizedCommandActionRunner
{
    Task<CommandActionRun> RunAsync(
        Guid actionId,
        IReadOnlyDictionary<string, string> values,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Executes an existing Action after expanding ephemeral runtime variables. Variable values are
/// never written to the Action store or vault.
/// </summary>
public sealed class ParameterizedCommandActionRunner(
    ICommandActionStore store,
    IHostDirectory hostDirectory,
    ICredentialResolver credentials,
    ISshEngine engine,
    ISshPrompts prompts,
    IEntitlementService entitlements,
    TimeProvider? timeProvider = null) : IParameterizedCommandActionRunner
{
    private const int MaxParallelHosts = 4;
    private const int MaxOutputLength = 256 * 1024;
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<CommandActionRun> RunAsync(
        Guid actionId,
        IReadOnlyDictionary<string, string> values,
        CancellationToken cancellationToken = default)
    {
        if (!entitlements.Has(PremiumFeature.ActionVariables))
            throw new InvalidOperationException("Runtime Action variables require MeowSSH Pro.");

        var action = (await store.GetAllAsync(cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(item => item.Id == actionId)
            ?? throw new KeyNotFoundException("The Action no longer exists.");

        var variables = CommandActionTemplate.GetVariables(action.Command);
        if (variables.Count == 0)
            throw new InvalidOperationException("This Action does not contain runtime variables.");

        if (action.HostIds.Count > 1 && !entitlements.Has(PremiumFeature.MultiHostActions))
            throw new InvalidOperationException("Running an Action on multiple hosts requires MeowSSH Pro.");

        // Render before resolving hosts or credentials so invalid/missing inputs fail without network activity.
        var renderedCommand = CommandActionTemplate.Render(action.Command, values);
        var allHosts = await hostDirectory.GetHostsAsync(cancellationToken).ConfigureAwait(false);
        var byId = allHosts.ToDictionary(static status => status.Host.Id, static status => status.Host);
        var started = _timeProvider.GetUtcNow();
        using var concurrency = new SemaphoreSlim(MaxParallelHosts, MaxParallelHosts);

        var tasks = action.HostIds.Select((hostId, index) => RunTargetAsync(
            action,
            renderedCommand,
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
        string command,
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
                    command,
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

    private static string LimitOutput(string value)
    {
        if (value.Length <= MaxOutputLength) return value;
        return value[..MaxOutputLength] + "\n… output truncated by MeowSSH …";
    }

    private sealed record IndexedResult(int Index, CommandActionHostResult Result);
}
