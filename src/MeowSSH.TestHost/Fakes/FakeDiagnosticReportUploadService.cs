using MeowSSH.Core.Diagnostics;

namespace MeowSSH.TestHost.Fakes;

/// <summary>
/// Records every upload attempt instead of making a network call, so a UI
/// test can assert the page sent (or didn't send) a report without a real
/// server. Always reports success -- no test currently exercises the
/// send-failed message, and TryUploadAsync's own doc comment already covers
/// why a real failure never throws.
/// </summary>
public sealed class FakeDiagnosticReportUploadService : IDiagnosticReportUploadService
{
    public List<(DiagnosticReportSnapshot Report, string? InstallId)> Uploads { get; } = [];

    public Task<bool> TryUploadAsync(
        DiagnosticReportSnapshot report,
        string? installId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Uploads.Add((report, installId));
        return Task.FromResult(true);
    }
}
