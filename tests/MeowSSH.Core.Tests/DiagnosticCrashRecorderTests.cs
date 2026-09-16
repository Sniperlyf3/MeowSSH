using MeowSSH.Core.Diagnostics;

namespace MeowSSH.Core.Tests;

public sealed class DiagnosticCrashRecorderTests
{
    [Fact]
    public async Task RecordAsyncPersistsSanitizedSnapshotWithSemanticBreadcrumbs()
    {
        var store = new MemoryPendingStore();
        var breadcrumbs = new DiagnosticBreadcrumbBuffer();
        breadcrumbs.Add(DiagnosticBreadcrumbKind.OpenedSftp, new DateTimeOffset(2026, 9, 16, 12, 0, 0, TimeSpan.Zero));
        var recorder = new DiagnosticCrashRecorder(store, breadcrumbs);

        var recorded = await recorder.RecordAsync(
            new InvalidOperationException("password=hunter2 host=prod.internal 192.168.1.20"),
            "2.0.0",
            "Android");

        Assert.True(recorded);
        Assert.NotNull(store.Report);
        var frame = Assert.Single(store.Report!.Exceptions);
        Assert.DoesNotContain("hunter2", frame.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("prod.internal", frame.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("192.168.1.20", frame.Message, StringComparison.Ordinal);
        Assert.Equal(DiagnosticBreadcrumbKind.OpenedSftp, Assert.Single(store.Report.Breadcrumbs).Kind);
    }

    [Fact]
    public async Task NewestCrashReplacesThePreviousPendingReport()
    {
        var store = new MemoryPendingStore();
        var recorder = new DiagnosticCrashRecorder(store, new DiagnosticBreadcrumbBuffer());

        await recorder.RecordAsync(new InvalidOperationException("first"), "2.0.0", "Android");
        await recorder.RecordAsync(new InvalidOperationException("second"), "2.0.0", "Android");

        Assert.NotNull(store.Report);
        Assert.Equal("second", Assert.Single(store.Report!.Exceptions).Message);
        Assert.Equal(2, store.SaveCount);
    }

    [Fact]
    public async Task StorageFailureNeverEscapesOrMasksOriginalCrash()
    {
        var recorder = new DiagnosticCrashRecorder(new FailingPendingStore(), new DiagnosticBreadcrumbBuffer());

        var recorded = await recorder.RecordAsync(
            new InvalidOperationException("original failure"),
            "2.0.0",
            "Android");

        Assert.False(recorded);
    }

    [Fact]
    public void SynchronousFatalPathWaitsForPersistenceAndDoesNotThrow()
    {
        var store = new MemoryPendingStore();
        var recorder = new DiagnosticCrashRecorder(store, new DiagnosticBreadcrumbBuffer());

        var recorded = recorder.RecordSynchronously(
            new InvalidOperationException("fatal"),
            "2.0.0",
            "Android");

        Assert.True(recorded);
        Assert.NotNull(store.Report);
    }

    private sealed class MemoryPendingStore : IPendingDiagnosticReportStore
    {
        public DiagnosticReportSnapshot? Report { get; private set; }
        public int SaveCount { get; private set; }

        public Task SaveAsync(DiagnosticReportSnapshot report, CancellationToken cancellationToken = default)
        {
            Report = report;
            SaveCount++;
            return Task.CompletedTask;
        }

        public Task<DiagnosticReportSnapshot?> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Report);

        public Task ClearAsync(CancellationToken cancellationToken = default)
        {
            Report = null;
            return Task.CompletedTask;
        }
    }

    private sealed class FailingPendingStore : IPendingDiagnosticReportStore
    {
        public Task SaveAsync(DiagnosticReportSnapshot report, CancellationToken cancellationToken = default) =>
            throw new IOException("disk unavailable");

        public Task<DiagnosticReportSnapshot?> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<DiagnosticReportSnapshot?>(null);

        public Task ClearAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
