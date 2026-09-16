using MeowSSH.Core.Diagnostics;

namespace MeowSSH.Core.Tests;

public sealed class BugReportExportBuilderTests
{
    [Fact]
    public void DiagnosticsAreNotAttachedUnlessExplicitlyProvided()
    {
        var export = BugReportExportBuilder.Build(
            "The connection screen closed unexpectedly.",
            null,
            null);

        Assert.Contains("Anonymized diagnostics: not attached", export, StringComparison.Ordinal);
        Assert.DoesNotContain("Exception 1", export, StringComparison.Ordinal);
    }

    [Fact]
    public void ExplicitlyAttachedDiagnosticsUseTheAlreadySanitizedSnapshot()
    {
        var snapshot = DiagnosticReportSnapshotBuilder.Capture(
            new InvalidOperationException("password=hunter2 host=prod.internal 10.2.3.4"),
            new DiagnosticBreadcrumbBuffer(),
            "2.0.0",
            "Android");

        var export = BugReportExportBuilder.Build(
            "MeowSSH closed while I was opening Files.",
            "Files should have opened.",
            "Open a host, then switch to Files.",
            snapshot);

        Assert.Contains("Anonymized diagnostics: attached by user choice", export, StringComparison.Ordinal);
        Assert.Contains("System.InvalidOperationException", export, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", export, StringComparison.Ordinal);
        Assert.DoesNotContain("prod.internal", export, StringComparison.Ordinal);
        Assert.DoesNotContain("10.2.3.4", export, StringComparison.Ordinal);
    }

    [Fact]
    public void DescriptionIsRequired()
    {
        var error = Assert.Throws<ArgumentException>(() =>
            BugReportExportBuilder.Build("  ", null, null));

        Assert.Contains("Describe what went wrong", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UserTextIsBoundedBeforeExport()
    {
        var export = BugReportExportBuilder.Build(
            new string('d', BugReportExportBuilder.MaxDescriptionLength + 100),
            new string('e', BugReportExportBuilder.MaxExpectedLength + 100),
            new string('s', BugReportExportBuilder.MaxStepsLength + 100));

        Assert.Contains("…[truncated]", export, StringComparison.Ordinal);
        Assert.True(export.Length < 20_000);
    }
}
