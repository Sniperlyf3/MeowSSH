namespace MeowSSH.Core.Diagnostics;

/// <summary>
/// Holds at most one sanitized crash report locally until the user chooses whether
/// to review/send it or dismiss it. Implementations must not submit reports.
/// </summary>
public interface IPendingDiagnosticReportStore
{
    Task SaveAsync(DiagnosticReportSnapshot report, CancellationToken cancellationToken = default);

    Task<DiagnosticReportSnapshot?> LoadAsync(CancellationToken cancellationToken = default);

    Task ClearAsync(CancellationToken cancellationToken = default);
}
