using System.Text;

namespace MeowSSH.Core.Diagnostics;

/// <summary>
/// Builds a user-reviewable local bug-report export. This type performs no network
/// activity. Crash diagnostics are attached only when the caller explicitly opts in.
/// </summary>
public static class BugReportExportBuilder
{
    public const int MaxDescriptionLength = 4_096;
    public const int MaxExpectedLength = 2_048;
    public const int MaxStepsLength = 4_096;

    public static string Build(
        string description,
        string? expectedBehavior,
        string? reproductionSteps,
        DiagnosticReportSnapshot? diagnosticReport = null)
    {
        if (string.IsNullOrWhiteSpace(description))
            throw new ArgumentException("Describe what went wrong before exporting the report.", nameof(description));

        var builder = new StringBuilder();
        builder.AppendLine("MeowSSH bug report");
        builder.AppendLine("==================");
        builder.AppendLine();
        AppendSection(builder, "What went wrong", Clamp(description.Trim(), MaxDescriptionLength));

        if (!string.IsNullOrWhiteSpace(expectedBehavior))
            AppendSection(builder, "Expected behavior", Clamp(expectedBehavior.Trim(), MaxExpectedLength));

        if (!string.IsNullOrWhiteSpace(reproductionSteps))
            AppendSection(builder, "Steps to reproduce", Clamp(reproductionSteps.Trim(), MaxStepsLength));

        if (diagnosticReport is null)
        {
            builder.AppendLine("Anonymized diagnostics: not attached");
            return builder.ToString();
        }

        builder.AppendLine("Anonymized diagnostics: attached by user choice");
        builder.AppendLine($"Format: {diagnosticReport.FormatVersion}");
        builder.AppendLine($"App version: {diagnosticReport.AppVersion}");
        builder.AppendLine($"Platform: {diagnosticReport.Platform}");
        builder.AppendLine($"Captured UTC: {diagnosticReport.CapturedAtUtc:O}");
        builder.AppendLine();

        for (var i = 0; i < diagnosticReport.Exceptions.Count; i++)
        {
            var exception = diagnosticReport.Exceptions[i];
            builder.AppendLine($"Exception {i + 1}: {exception.Type}");
            if (!string.IsNullOrWhiteSpace(exception.Message))
                builder.AppendLine(exception.Message);
            if (!string.IsNullOrWhiteSpace(exception.StackTrace))
                builder.AppendLine(exception.StackTrace);
            builder.AppendLine();
        }

        if (diagnosticReport.Breadcrumbs.Count > 0)
        {
            builder.AppendLine("Recent app actions:");
            foreach (var breadcrumb in diagnosticReport.Breadcrumbs)
                builder.AppendLine($"- {breadcrumb.TimestampUtc:O} {breadcrumb.Kind}");
        }

        return builder.ToString();
    }

    private static void AppendSection(StringBuilder builder, string heading, string value)
    {
        builder.AppendLine(heading + ":");
        builder.AppendLine(value);
        builder.AppendLine();
    }

    private static string Clamp(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength] + "…[truncated]";
}
