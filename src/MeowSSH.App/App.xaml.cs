using MeowSSH.Core.Diagnostics;

namespace MeowSSH.App;

public partial class App : Application
{
    private readonly DiagnosticCrashRecorder _crashRecorder;
    private int _fatalRecorded;

    public App(
        DiagnosticCrashRecorder crashRecorder,
        DiagnosticBreadcrumbBuffer breadcrumbs)
    {
        _crashRecorder = crashRecorder;
        InitializeComponent();

        breadcrumbs.Add(DiagnosticBreadcrumbKind.AppStarted);
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        global::Android.Runtime.AndroidEnvironment.UnhandledExceptionRaiser += OnAndroidUnhandledException;
    }

    protected override Window CreateWindow(IActivationState? activationState) =>
        new(new MainPage()) { Title = "MeowSSH.App" };

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs args)
    {
        var exception = args.ExceptionObject as Exception
            ?? new InvalidOperationException("Unhandled non-Exception runtime failure.");
        RecordFatal(exception);
    }

    private void OnAndroidUnhandledException(object? sender, global::Android.Runtime.RaiseThrowableEventArgs args)
    {
        RecordFatal(args.Exception);
        // Deliberately leave Handled=false. Crash reporting must never turn a
        // fatal application error into a silently continued process.
    }

    private void RecordFatal(Exception exception)
    {
        if (Interlocked.Exchange(ref _fatalRecorded, 1) != 0) return;
        _crashRecorder.RecordSynchronously(exception, MeowSSH.Core.BuildInfo.Version, "Android");
    }
}
