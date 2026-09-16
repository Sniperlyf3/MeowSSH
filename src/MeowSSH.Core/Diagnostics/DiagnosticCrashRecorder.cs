namespace MeowSSH.Core.Diagnostics;

/// <summary>
/// Captures one bounded, sanitized crash snapshot into the local pending-report
/// store. This class has no networking and deliberately never marks exceptions as
/// handled. Fatal-event callers may use <see cref="RecordSynchronously"/> so the
/// bounded file write completes before the process is torn down.
/// </summary>
public sealed class DiagnosticCrashRecorder(
    IPendingDiagnosticReportStore store,
    DiagnosticBreadcrumbBuffer breadcrumbs)
{
    private readonly IPendingDiagnosticReportStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly DiagnosticBreadcrumbBuffer _breadcrumbs = breadcrumbs ?? throw new ArgumentNullException(nameof(breadcrumbs));

    public async Task<bool> RecordAsync(
        Exception exception,
        string appVersion,
        string platform,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(exception);

        try
        {
            var report = DiagnosticReportSnapshotBuilder.Capture(
                exception,
                _breadcrumbs,
                appVersion,
                platform);
            await _store.SaveAsync(report, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception captureFailure) when (captureFailure is not OperationCanceledException)
        {
            // Crash recording is strictly best-effort. A failure here must never
            // replace or mask the original application failure.
            return false;
        }
    }

    public bool RecordSynchronously(
        Exception exception,
        string appVersion,
        string platform)
    {
        ArgumentNullException.ThrowIfNull(exception);

        try
        {
            return RecordAsync(exception, appVersion, platform).GetAwaiter().GetResult();
        }
        catch (Exception)
        {
            // The async path already contains capture/storage failures, but keep
            // this guard so a fatal exception handler can never throw another one.
            return false;
        }
    }
}
