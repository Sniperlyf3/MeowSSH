using Android.App;
using Android.Content;
using Android.OS;
using Android.Runtime;
using Android.Util;

namespace MeowSSH.Device.Tests;

/// <summary>
/// The entry point <c>adb shell am instrument</c> starts.
/// </summary>
[Instrumentation(Name = "dev.sniperlyf3.meowssh.devicetests.TestInstrumentation")]
public sealed class TestInstrumentation : Instrumentation
{
    private const string Tag = "MeowSSHDeviceTests";

    public TestInstrumentation(IntPtr handle, JniHandleOwnership transfer) : base(handle, transfer) { }

    public override void OnCreate(Bundle? arguments)
    {
        base.OnCreate(arguments);
        Start();
    }

    public override async void OnStart()
    {
        base.OnStart();

        var results = new Bundle();
        try
        {
            var tests = VaultDeviceTests.All.Concat(HardwareSshKeyDeviceTests.All);
            var outcomes = await DeviceTestRunner.RunAsync(tests).ConfigureAwait(false);
            var report = DeviceTestRunner.Report(outcomes);

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