using MeowSSH.Core.Licensing;
using MeowSSH.Core.Ssh;

namespace MeowSSH.Core.Services;

public sealed record TerminalBroadcastTarget(Guid Id, ITerminalSession Session);
public sealed record TerminalBroadcastFailure(Guid Id, string Message);
public sealed record TerminalBroadcastResult(int Attempted, int Succeeded, IReadOnlyList<TerminalBroadcastFailure> Failures)
{
    public bool SucceededCompletely => Failures.Count == 0 && Succeeded == Attempted;
}

public interface ITerminalBroadcastService
{
    Task<TerminalBroadcastResult> WriteAsync(
        IReadOnlyList<TerminalBroadcastTarget> targets,
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Fans one terminal input frame to a bounded set of already-open sessions. No command parsing,
/// persistence, or background execution occurs here.
/// </summary>
public sealed class TerminalBroadcastService(IEntitlementService entitlements) : ITerminalBroadcastService
{
    public const int MaxTargets = 8;

    public async Task<TerminalBroadcastResult> WriteAsync(
        IReadOnlyList<TerminalBroadcastTarget> targets,
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken = default)
    {
        if (!entitlements.Has(PremiumFeature.BroadcastInput))
            throw new InvalidOperationException("Broadcast Input requires MeowSSH Pro.");
        ArgumentNullException.ThrowIfNull(targets);
        if (targets.Count is < 2 or > MaxTargets)
            throw new ArgumentException($"Broadcast Input requires between 2 and {MaxTargets} open sessions.", nameof(targets));
        if (targets.Any(static target => target.Id == Guid.Empty || target.Session is null))
            throw new ArgumentException("Broadcast targets must have valid session IDs and terminals.", nameof(targets));
        if (targets.Select(static target => target.Id).Distinct().Count() != targets.Count)
            throw new ArgumentException("Broadcast targets must be unique.", nameof(targets));
        if (data.IsEmpty)
            return new TerminalBroadcastResult(targets.Count, targets.Count, []);

        var tasks = targets.Select(target => WriteTargetAsync(target, data, cancellationToken)).ToArray();
        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        var failures = results.Where(static result => result is not null).Cast<TerminalBroadcastFailure>().ToArray();
        return new TerminalBroadcastResult(targets.Count, targets.Count - failures.Length, failures);
    }

    private static async Task<TerminalBroadcastFailure?> WriteTargetAsync(
        TerminalBroadcastTarget target,
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken)
    {
        try
        {
            await target.Session.WriteAsync(data, cancellationToken).ConfigureAwait(false);
            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new TerminalBroadcastFailure(target.Id, exception.Message);
        }
    }
}
