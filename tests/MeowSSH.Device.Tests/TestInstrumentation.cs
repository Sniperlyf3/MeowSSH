using Android.App;
using Android.Content;
using Android.OS;
using Android.Runtime;
using Android.Util;
using MeowSSH.E2E;

namespace MeowSSH.Device.Tests;

/// <summary>
/// The entry point <c>adb shell am instrument</c> starts.
/// </summary>
[Instrumentation(Name = "dev.sniperlyf3.meowssh.devicetests.TestInstrumentation")]
public sealed class TestInstrumentation : Instrumentation
{
    private const string Tag = "MeowSSHDeviceTests";

    public TestInstrumentation(IntPtr handle, JniHandleOwnership transfer) : base(handle, transfer) { }

    private E2EServer? _e2eServer;
    private Exception? _e2eArgumentError;

    public override void OnCreate(Bundle? arguments)
    {
        base.OnCreate(arguments);
        // "am instrument -e key value" arrives here. Parsed now, reported in
        // OnStart: a half-configured server must fail the run, not skip E2E.
        try { _e2eServer = E2EServer.From(key => arguments?.GetString(key)); }
        catch (Exception ex) { _e2eArgumentError = ex; }
        Start();
    }

    public override async void OnStart()
    {
        base.OnStart();

        var results = new Bundle();
        try
        {
            if (_e2eArgumentError is not null) throw _e2eArgumentError;

            var tests = VaultDeviceTests.All.Concat(HardwareSshKeyDeviceTests.All);
            var outcomes = (await DeviceTestRunner.RunAsync(tests).ConfigureAwait(false)).ToList();
            var e2eRun = 0;
            if (_e2eServer is not null)
            {
                var platform = new AndroidE2EPlatform(TargetContext!);
                foreach (var scenario in E2EScenarios.All)
                {
                    Log.Info(Tag, "e2e: " + scenario.Name);
                    var result = await E2ERunner.RunAsync(scenario, platform, _e2eServer, line => Log.Info(Tag, line)).ConfigureAwait(false);
                    outcomes.Add(new DeviceTestResult("e2e: " + result.Name, result.Passed, result.Failure, result.Duration));
                    e2eRun++;
                }
            }
            // A line the CI script checks for whenever it passed a server: the
            // pass line below is also printed by a run that never saw the
            // arguments, and that run must not count as E2E coverage.
            var report = DeviceTestRunner.Report(outcomes) + $"E2E scenarios run: {e2eRun}\n";

            foreach (var line in report.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                Log.Info(Tag, line.TrimEnd());

            results.PutString("stream", "\n" + report);

            var failed = outcomes.Count(r => !r.Passed);
            Finish(failed == 0 ? Result.Ok : Result.Canceled, results);
        }
        catch (Exception ex)
        {
            Log.Error(Tag, "the device test run itself failed: " + ex);
            results.PutString("stream", "\nthe device test run itself failed: " + ex);
            Finish(Result.Canceled, results);
        }
    }
}