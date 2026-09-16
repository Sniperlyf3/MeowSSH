using MeowSSH.Core.Diagnostics;

namespace MeowSSH.Core.Tests;

public sealed class DiagnosticReportSnapshotTests
{
    [Fact]
    public void CaptureSanitizesExceptionTextBeforeItCanLeaveTheBuilder()
    {
        var breadcrumbs = new DiagnosticBreadcrumbBuffer();
        var exception = new InvalidOperationException(
            "password=hunter2 host=prod.internal user=alice token=abc123 192.168.1.9 alice@example.com");

        var report = DiagnosticReportSnapshotBuilder.Capture(
            exception,
            breadcrumbs,
            "1.2.3",
            "Android");

        var frame = Assert.Single(report.Exceptions);
        Assert.DoesNotContain("hunter2", frame.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("prod.internal", frame.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("alice", frame.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("abc123", frame.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("192.168.1.9", frame.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("alice@example.com", frame.Message, StringComparison.Ordinal);
        Assert.Contains("[redacted]", frame.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CaptureIncludesOnlyTheBoundedInnerExceptionChain()
    {
        Exception current = new InvalidOperationException("root");
        for (var i = 0; i < DiagnosticReportSnapshotBuilder.MaxExceptionDepth + 3; i++)
            current = new InvalidOperationException($"layer-{i}", current);

        var report = DiagnosticReportSnapshotBuilder.Capture(
            current,
            new DiagnosticBreadcrumbBuffer(),
            "1.2.3",
            "Android");

        Assert.Equal(DiagnosticReportSnapshotBuilder.MaxExceptionDepth, report.Exceptions.Count);
    }

    [Fact]
    public void CaptureClampsLargeTextFields()
    {
        var huge = new string('x', DiagnosticReportSnapshotBuilder.MaxStackTraceLength * 2);
        var exception = new InvalidOperationException(huge);

        var report = DiagnosticReportSnapshotBuilder.Capture(
            exception,
            new DiagnosticBreadcrumbBuffer(),
            new string('v', 1_000),
            new string('p', 1_000));

        var frame = Assert.Single(report.Exceptions);
        Assert.True(frame.Message.Length <= DiagnosticReportSnapshotBuilder.MaxMessageLength + "…[truncated]".Length);
        Assert.True(report.AppVersion.Length <= DiagnosticReportSnapshotBuilder.MaxMetadataLength + "…[truncated]".Length);
        Assert.True(report.Platform.Length <= DiagnosticReportSnapshotBuilder.MaxMetadataLength + "…[truncated]".Length);
    }

    [Fact]
    public void CaptureKeepsOnlyRecentSemanticBreadcrumbs()
    {
        var breadcrumbs = new DiagnosticBreadcrumbBuffer(DiagnosticBreadcrumbBuffer.MaxCapacity);
        var start = new DateTimeOffset(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);
        for (var i = 0; i < DiagnosticBreadcrumbBuffer.MaxCapacity; i++)
            breadcrumbs.Add(DiagnosticBreadcrumbKind.OpenedSettings, start.AddSeconds(i));

        var report = DiagnosticReportSnapshotBuilder.Capture(
            new InvalidOperationException("boom"),
            breadcrumbs,
            "1.2.3",
            "Android",
            start.AddMinutes(5));

        Assert.Equal(DiagnosticReportSnapshotBuilder.MaxBreadcrumbs, report.Breadcrumbs.Count);
        Assert.Equal(start.AddSeconds(DiagnosticBreadcrumbBuffer.MaxCapacity - DiagnosticReportSnapshotBuilder.MaxBreadcrumbs), report.Breadcrumbs[0].TimestampUtc);
        Assert.All(report.Breadcrumbs, item => Assert.Equal(DiagnosticBreadcrumbKind.OpenedSettings, item.Kind));
    }

    [Fact]
    public void CaptureUsesAStableVersionedShapeAndExplicitTimestamp()
    {
        var capturedAt = new DateTimeOffset(2026, 9, 16, 13, 30, 0, TimeSpan.Zero);
        var report = DiagnosticReportSnapshotBuilder.Capture(
            new InvalidOperationException("boom"),
            new DiagnosticBreadcrumbBuffer(),
            "2.0.0",
            "Android",
            capturedAt);

        Assert.Equal(DiagnosticReportSnapshotBuilder.CurrentFormatVersion, report.FormatVersion);
        Assert.Equal(capturedAt, report.CapturedAtUtc);
        Assert.Equal("2.0.0", report.AppVersion);
        Assert.Equal("Android", report.Platform);
    }
}
