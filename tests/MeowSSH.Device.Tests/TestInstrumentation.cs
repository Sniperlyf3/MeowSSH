using Android.App;
using Android.Content;
using Android.OS;
using Android.Runtime;
using Android.Util;

namespace MeowSSH.Device.Tests;

/// <summary>
/// The entry point <c>adb shell am instrument</c> starts.
/// </summary>
/// <remarks>
/// <para>
/// Instrumentation rather than an activity, because <c>am instrument -w</c>
/// blocks until <see cref="Instrumentation.Finish"/> is called and turns the
/// result code into the command's exit status. CI therefore gets a pass or fail
/// without parsing a log, which is the part that usually rots.
/// </para>
/// <para>
/// The results also go to logcat so that a failure can be read without
/// downloading anything, and into the result bundle so the reason appears in the
/// command's own output.
/// </para>
/// </remarks>
[Instrumentation(Name = "dev.sniperlyf3.meowssh.devicetests.TestInstrumentation")]
public sealed class TestInstrumentation : Instrumentation
{
    private const string Tag = "MeowSSHDeviceTests";

    public TestInstrumentation(IntPtr handle, JniHandleOwnership transfer) : base(handle, transfer) { }

    public override void OnCreate(Bundle? arguments)
    {
        base.OnCreate(arguments);
        // Start() hands control to OnStart on the instrumentation thread; doing
        // the work here instead would run it before the app context is ready.
        Start();
    }

    public override async void OnStart()
    {
        base.OnStart();

        var results = new Bundle();
        try
        {
            var outcomes = await DeviceTestRunner.RunAsync(VaultDeviceTests.All).ConfigureAwait(false);
            var report = DeviceTestRunner.Report(outcomes);

            foreach (var line in report.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                Log.Info(Tag, line.TrimEnd());

            results.PutString("stream", "\n" + report);

            var failed = outcomes.Count(r => !r.Passed);
            // Zero means the harness itself worked; a failing check must not be
            // reported as success, so the code is the count of failures.
            Finish(failed == 0 ? Result.Ok : Result.Canceled, results);
        }
        catch (Exception ex)
        {
            // A throw out here is the runner breaking rather than a check
            // failing, and it must never look like a pass.
            Log.Error(Tag, "the device test run itself failed: " + ex);
            results.PutString("stream", "\nthe device test run itself failed: " + ex);
            Finish(Result.Canceled, results);
        }
    }
}
