using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using MeowSSH.Core.Licensing;

namespace MeowSSH.Core.Diagnostics;

/// <summary>
/// Wire shape for POST /v1/diagnostics/reports. Mirrors
/// MeowSSHAPI.Diagnostics.DiagnosticReportContracts field-for-field, including
/// DiagnosticBreadcrumbKind's ordinal values: neither side applies a
/// JsonStringEnumConverter, so the enum serializes as its underlying int and
/// the two definitions must stay numerically in sync, not just name-for-name.
/// </summary>
public sealed record DiagnosticExceptionUpload(string Type, string? Message, string? StackTrace);

public sealed record DiagnosticBreadcrumbUpload(DiagnosticBreadcrumbKind Kind, DateTimeOffset TimestampUtc);

public sealed record DiagnosticReportUploadRequest(
    int FormatVersion,
    DateTimeOffset CapturedAtUtc,
    string AppVersion,
    string Platform,
    IReadOnlyList<DiagnosticExceptionUpload> Exceptions,
    IReadOnlyList<DiagnosticBreadcrumbUpload> Breadcrumbs,
    string? InstallId);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(DiagnosticReportUploadRequest))]
internal sealed partial class DiagnosticReportUploadJsonContext : JsonSerializerContext
{
}

/// <summary>
/// Sends a report the person has already reviewed and chosen to submit to
/// MeowSSHAPI's diagnostics endpoint. This is the only place in the app that
/// performs that network call -- see PrivacyDiagnosticsPage.razor, which never
/// calls this on its own (export-only) and only reaches it from an explicit
/// "Send" action gated on the same person choosing to attach diagnostics in
/// the first place.
/// </summary>
public interface IDiagnosticReportUploadService
{
    /// <summary>
    /// Uploads <paramref name="report"/>, returning whether the server
    /// accepted it. Never throws for a network failure (offline, timeout,
    /// DNS, TLS, a non-2xx response) -- those are reported as a plain
    /// <c>false</c>, the same "nothing to gain from surfacing a hard error"
    /// reasoning DiagnosticIngestService.IngestAsync applies server-side.
    /// A crash-reporting path that can itself throw on send failure would
    /// undermine the entire feature.
    /// </summary>
    Task<bool> TryUploadAsync(
        DiagnosticReportSnapshot report,
        string? installId,
        CancellationToken cancellationToken = default);
}

public sealed class DiagnosticReportUploadService(
    HttpClient httpClient,
    LicensingApiOptions options) : IDiagnosticReportUploadService
{
    public async Task<bool> TryUploadAsync(
        DiagnosticReportSnapshot report,
        string? installId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);

        // Same precondition every other LicensingApiOptions-backed call in
        // this codebase checks first (see LicensingApiGrantProvider,
        // ManagedDerpUsageService) -- a build with no configured licensing
        // API (e.g. a local/dev build) has nowhere to send this and must not
        // attempt to.
        if (!options.IsConfigured) return false;

        var request = new DiagnosticReportUploadRequest(
            report.FormatVersion,
            report.CapturedAtUtc,
            report.AppVersion,
            report.Platform,
            [.. report.Exceptions.Select(static exception =>
                new DiagnosticExceptionUpload(exception.Type, exception.Message, exception.StackTrace))],
            [.. report.Breadcrumbs.Select(static breadcrumb =>
                new DiagnosticBreadcrumbUpload(breadcrumb.Kind, breadcrumb.TimestampUtc))],
            installId);

        try
        {
            using var response = await httpClient.PostAsJsonAsync(
                new Uri(options.BaseUri!, "v1/diagnostics/reports"),
                request,
                DiagnosticReportUploadJsonContext.Default.DiagnosticReportUploadRequest,
                cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // A timeout surfaces as TaskCanceledException with the caller's
            // own token still unset -- distinguished from the caller
            // actually cancelling, which is left to propagate as normal.
            return false;
        }
    }
}
