using MeowSSH.Core.Diagnostics;

namespace MeowSSH.Core.Tests;

public sealed class FilePendingDiagnosticReportStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "meowssh-diagnostic-store-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task SavesAndLoadsOneSanitizedSnapshot()
    {
        var store = new FilePendingDiagnosticReportStore(_directory);
        var capturedAt = new DateTimeOffset(2026, 9, 16, 13, 45, 0, TimeSpan.Zero);
        var report = BuildReport("first crash", capturedAt);

        await store.SaveAsync(report);
        var loaded = await store.LoadAsync();

        Assert.NotNull(loaded);
        Assert.Equal(report.FormatVersion, loaded.FormatVersion);
        Assert.Equal(capturedAt, loaded.CapturedAtUtc);
        Assert.Equal("first crash", loaded.Exceptions[0].Message);
    }

    [Fact]
    public async Task ANewCrashReplacesTheOlderPendingReport()
    {
        var store = new FilePendingDiagnosticReportStore(_directory);
        await store.SaveAsync(BuildReport("older", DateTimeOffset.UtcNow.AddMinutes(-1)));
        await store.SaveAsync(BuildReport("newer", DateTimeOffset.UtcNow));

        var loaded = await store.LoadAsync();

        Assert.NotNull(loaded);
        Assert.Equal("newer", loaded.Exceptions[0].Message);
    }

    [Fact]
    public async Task ClearRemovesThePendingReport()
    {
        var store = new FilePendingDiagnosticReportStore(_directory);
        await store.SaveAsync(BuildReport("dismiss me", DateTimeOffset.UtcNow));

        await store.ClearAsync();

        Assert.Null(await store.LoadAsync());
    }

    [Fact]
    public async Task CorruptPendingDataIsDiscardedInsteadOfCausingAStartupCrashLoop()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, FilePendingDiagnosticReportStore.FileName);
        await File.WriteAllTextAsync(path, "{ definitely not valid json");
        var store = new FilePendingDiagnosticReportStore(_directory);

        var loaded = await store.LoadAsync();

        Assert.Null(loaded);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task OversizedPendingDataIsDiscardedBeforeDeserialization()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, FilePendingDiagnosticReportStore.FileName);
        await File.WriteAllBytesAsync(path, new byte[FilePendingDiagnosticReportStore.MaxStoredBytes + 1]);
        var store = new FilePendingDiagnosticReportStore(_directory);

        var loaded = await store.LoadAsync();

        Assert.Null(loaded);
        Assert.False(File.Exists(path));
    }

    private static DiagnosticReportSnapshot BuildReport(string message, DateTimeOffset capturedAt) =>
        DiagnosticReportSnapshotBuilder.Capture(
            new InvalidOperationException(message),
            new DiagnosticBreadcrumbBuffer(),
            "2.0.0",
            "Android",
            capturedAt);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Test cleanup only.
        }
        catch (UnauthorizedAccessException)
        {
            // Test cleanup only.
        }
    }
}
