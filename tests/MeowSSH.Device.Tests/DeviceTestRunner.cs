using System.Diagnostics;
using System.Text;

namespace MeowSSH.Device.Tests;

/// <summary>One named check that runs on the device.</summary>
/// <param name="Name">Shown in the report, so it has to say what failed.</param>
/// <param name="Run">The check itself. Throws to fail, as an assertion does.</param>
public sealed record DeviceTest(string Name, Func<Task> Run);

/// <summary>The outcome of one <see cref="DeviceTest"/>.</summary>
public sealed record DeviceTestResult(string Name, bool Passed, string? Failure, TimeSpan Duration);

/// <summary>
/// Runs a list of checks and reports what happened.
/// </summary>
/// <remarks>
/// <para>
/// Hand-rolled rather than a discovery framework, and that is the trade being
/// made deliberately: the runners that discover <c>[Fact]</c> on a device live
/// on a dotnet-eng feed rather than nuget.org, and adding a package feed to a
/// repository is a larger decision than a dozen checks justify. An explicit list
/// cannot silently stop discovering a test, which is the failure mode that
/// matters most in a suite nobody watches run.
/// </para>
/// <para>
/// Every check is isolated from the next: each gets its own directory and its
/// own key store alias, and a failure is caught rather than ending the run, so
/// one broken check does not hide the state of the others.
/// </para>
/// </remarks>
public static class DeviceTestRunner
{
    public static async Task<IReadOnlyList<DeviceTestResult>> RunAsync(IEnumerable<DeviceTest> tests)
    {
        var results = new List<DeviceTestResult>();

        foreach (var test in tests)
        {
            var clock = Stopwatch.StartNew();
            try
            {
                await test.Run().ConfigureAwait(false);
                results.Add(new DeviceTestResult(test.Name, true, null, clock.Elapsed));
            }
            catch (Exception ex)
            {
                // The type matters as much as the message: an Android binding
                // problem surfaces as a Java exception name that says far more
                // than its text does.
                results.Add(new DeviceTestResult(
                    test.Name, false, $"{ex.GetType().Name}: {ex.Message}", clock.Elapsed));
            }
        }

        return results;
    }

    /// <summary>Renders results the way a person reads a test run.</summary>
    public static string Report(IReadOnlyList<DeviceTestResult> results)
    {
        var report = new StringBuilder();
        foreach (var result in results)
        {
            report.Append(result.Passed ? "  PASS  " : "  FAIL  ")
                  .Append(result.Name)
                  .Append(" (")
                  .Append((int)result.Duration.TotalMilliseconds)
                  .AppendLine("ms)");

            if (result.Failure is not null) report.Append("          ").AppendLine(result.Failure);
        }

        var failed = results.Count(r => !r.Passed);
        report.AppendLine()
              .Append(failed == 0 ? "All " : $"{failed} of ")
              .Append(results.Count)
              .AppendLine(failed == 0 ? " device tests passed." : " device tests failed.");

        return report.ToString();
    }
}
