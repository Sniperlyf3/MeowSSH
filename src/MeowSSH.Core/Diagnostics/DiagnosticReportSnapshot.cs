namespace MeowSSH.Core.Diagnostics;

/// <summary>
/// One sanitized exception in a diagnostic report. This intentionally stores only
/// strings that are safe to present to the user and later submit with consent.
/// </summary>
public sealed record DiagnosticExceptionSnapshot(
    string Type,
    string Message,
    string StackTrace);

/// <summary>
/// A local-only, reviewable crash snapshot. No device identifier, account identifier,
/// host name, terminal contents, commands, file names, connection addresses or secrets
/// are accepted by this model.
/// </summary>
public sealed record DiagnosticReportSnapshot(
    int FormatVersion,
    DateTimeOffset CapturedAtUtc,
    string AppVersion,
    string Platform,
    IReadOnlyList<DiagnosticExceptionSnapshot> Exceptions,
    IReadOnlyList<DiagnosticBreadcrumb> Breadcrumbs);

/// <summary>
/// Converts an exception plus semantic breadcrumbs into a bounded, sanitized snapshot.
/// Building a snapshot never sends or persists anything.
/// </summary>
public static class DiagnosticReportSnapshotBuilder
{
    public const int CurrentFormatVersion = 1;
    public const int MaxExceptionDepth = 4;
    public const int MaxMessageLength = 2_048;
    public const int MaxStackTraceLength = 16_384;
    public const int MaxMetadataLength = 128;
    public const int MaxBreadcrumbs = 32;

    public static DiagnosticReportSnapshot Capture(
        Exception exception,
        DiagnosticBreadcrumbBuffer breadcrumbs,
        string appVersion,
        string platform,
        DateTimeOffset? capturedAtUtc = null)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentNullException.ThrowIfNull(breadcrumbs);

        var exceptions = new List<DiagnosticExceptionSnapshot>(MaxExceptionDepth);
        var current = exception;
        while (current is not null && exceptions.Count < MaxExceptionDepth)
        {
            exceptions.Add(new DiagnosticExceptionSnapshot(
                Clamp(current.GetType().FullName ?? current.GetType().Name, MaxMetadataLength),
                SanitizeAndClamp(current.Message, MaxMessageLength),
                SanitizeAndClamp(current.StackTrace, MaxStackTraceLength)));
            current = current.InnerException;
        }

        var breadcrumbSnapshot = breadcrumbs.Snapshot();
        IReadOnlyList<DiagnosticBreadcrumb> recentBreadcrumbs = breadcrumbSnapshot.Count <= MaxBreadcrumbs
            ? breadcrumbSnapshot
            : breadcrumbSnapshot.Skip(breadcrumbSnapshot.Count - MaxBreadcrumbs).ToArray();

        return new DiagnosticReportSnapshot(
            CurrentFormatVersion,
            capturedAtUtc ?? DateTimeOffset.UtcNow,
            SanitizeAndClamp(appVersion, MaxMetadataLength),
            SanitizeAndClamp(platform, MaxMetadataLength),
            exceptions,
            recentBreadcrumbs);
    }

    private static string SanitizeAndClamp(string? value, int maxLength) =>
        Clamp(DiagnosticSanitizer.SanitizeText(value), maxLength);

    private static string Clamp(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return value.Length <= maxLength
            ? value
            : value[..maxLength] + "…[truncated]";
    }
}
