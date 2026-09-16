using System.Text.Json;
using System.Text.Json.Serialization;

namespace MeowSSH.Core.Diagnostics;

/// <summary>
/// Persists one already-sanitized diagnostic snapshot on the local device. The
/// newest crash replaces the previous pending report. This type has no networking.
/// </summary>
public sealed class FilePendingDiagnosticReportStore : IPendingDiagnosticReportStore
{
    public const int MaxStoredBytes = 128 * 1024;
    public const string FileName = "pending-diagnostic-report.json";

    private readonly string _path;

    public FilePendingDiagnosticReportStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _path = Path.Combine(directory, FileName);
    }

    public async Task SaveAsync(
        DiagnosticReportSnapshot report,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        Validate(report);

        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            report,
            DiagnosticJsonContext.Default.DiagnosticReportSnapshot);
        if (bytes.Length > MaxStoredBytes)
            throw new InvalidDataException($"Diagnostic report exceeds the {MaxStoredBytes}-byte local storage limit.");

        var directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);

        var temporaryPath = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporaryPath, bytes, cancellationToken);
            File.Move(temporaryPath, _path, overwrite: true);
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    public async Task<DiagnosticReportSnapshot?> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_path)) return null;

        try
        {
            var info = new FileInfo(_path);
            if (info.Length is <= 0 or > MaxStoredBytes)
                throw new InvalidDataException("Pending diagnostic report has an invalid size.");

            await using var stream = new FileStream(
                _path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 16 * 1024,
                useAsync: true);

            var report = await JsonSerializer.DeserializeAsync(
                stream,
                DiagnosticJsonContext.Default.DiagnosticReportSnapshot,
                cancellationToken);
            if (report is null)
                throw new InvalidDataException("Pending diagnostic report is empty.");

            Validate(report);
            return report;
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            TryDelete(_path);
            return null;
        }
    }

    public Task ClearAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TryDelete(_path);
        return Task.CompletedTask;
    }

    private static void Validate(DiagnosticReportSnapshot report)
    {
        if (report.FormatVersion != DiagnosticReportSnapshotBuilder.CurrentFormatVersion)
            throw new InvalidDataException($"Unsupported diagnostic report format version {report.FormatVersion}.");
        if (report.Exceptions.Count is < 1 or > DiagnosticReportSnapshotBuilder.MaxExceptionDepth)
            throw new InvalidDataException("Diagnostic report has an invalid exception count.");
        if (report.Breadcrumbs.Count > DiagnosticReportSnapshotBuilder.MaxBreadcrumbs)
            throw new InvalidDataException("Diagnostic report has too many breadcrumbs.");
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
            // Cleanup is best effort; a later save/load can recover the file.
        }
        catch (UnauthorizedAccessException)
        {
            // Cleanup is best effort; callers should not crash because cleanup failed.
        }
    }
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(DiagnosticReportSnapshot))]
internal partial class DiagnosticJsonContext : JsonSerializerContext;
