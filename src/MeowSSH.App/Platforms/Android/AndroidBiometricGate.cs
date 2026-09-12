using Android.Hardware.Biometrics;
using Android.OS;
using AndroidX.Core.Content;
using MeowSSH.Core.Security;
using JavaObject = Java.Lang.Object;

namespace MeowSSH.App.Platforms.Android;

/// <summary>
/// The system biometric prompt.
/// </summary>
/// <remarks>
/// Uses the platform <c>BiometricPrompt</c> rather than AndroidX, whose
/// transitive AndroidX constraints conflict with MAUI's own. The platform class
/// needs API 28, which is older than any device this would be installed on.
/// </remarks>
internal sealed class AndroidBiometricGate : IBiometricGate
{
    // Raw platform constants rather than the generated enums: the .NET bindings
    // do not surface BiometricManager.Authenticators, and these values are fixed
    // by the Android API rather than by the binding.
    private const int AuthenticatorBiometricStrong = 0x000F;
    private const int AuthenticatorDeviceCredential = 1 << 15;
    private const int StrongOrCredential = AuthenticatorBiometricStrong | AuthenticatorDeviceCredential;

    private const int BiometricSuccess = 0;
    private const int BiometricErrorHwUnavailable = 1;
    private const int BiometricErrorNoneEnrolled = 11;
    private const int BiometricErrorNoHardware = 12;

    /// <summary>The error code the prompt reports when its negative button is pressed.</summary>
    private const int ErrorNegativeButton = 13;

    public ValueTask<BiometricAvailability> GetAvailabilityAsync(CancellationToken cancellationToken = default)
    {
        // BiometricManager itself only arrived in API 29. On 28 the prompt still
        // works, so the honest answer is "ask and find out" rather than claiming
        // there is no hardware.
        if (!OperatingSystem.IsAndroidVersionAtLeast(29))
            return ValueTask.FromResult(BiometricAvailability.Available);

        var context = Platform.CurrentActivity ?? (global::Android.Content.Context?)global::Android.App.Application.Context;
        if (context is null) return ValueTask.FromResult(BiometricAvailability.NoHardware);

        var manager = (BiometricManager?)context.GetSystemService(global::Android.Content.Context.BiometricService);
        if (manager is null) return ValueTask.FromResult(BiometricAvailability.NoHardware);

        var status = (int)(OperatingSystem.IsAndroidVersionAtLeast(30)
            ? manager.CanAuthenticate(StrongOrCredential)
            : manager.CanAuthenticate());

        return ValueTask.FromResult(status switch
        {
            BiometricSuccess => BiometricAvailability.Available,
            BiometricErrorNoneEnrolled => BiometricAvailability.NotEnrolled,
            BiometricErrorNoHardware => BiometricAvailability.NoHardware,
            BiometricErrorHwUnavailable => BiometricAvailability.TemporarilyUnavailable,
            _ => BiometricAvailability.NoHardware,
        });
    }

    public async ValueTask<BiometricResult> AuthenticateAsync(
        BiometricPromptOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var activity = Platform.CurrentActivity;
        if (activity is null) return BiometricResult.Failed;

        var builder = new BiometricPrompt.Builder(activity)
            .SetTitle(options.Title)!;
        if (options.Subtitle is not null) builder = builder.SetSubtitle(options.Subtitle)!;
        if (options.Description is not null) builder = builder.SetDescription(options.Description)!;

        if (options.AllowDeviceCredential && OperatingSystem.IsAndroidVersionAtLeast(30))
        {
            // A user whose sensor fails in the cold should still reach their
            // hosts, so the PIN is an accepted answer rather than a dead end.
            builder = builder.SetAllowedAuthenticators(StrongOrCredential)!;
        }
        else
        {
            // Without a device-credential fallback the prompt must offer its own
            // way out, or it cannot be dismissed at all.
            builder = builder.SetNegativeButton("Cancel",
                ContextCompat.GetMainExecutor(activity)!,
                new DialogClickListener())!;
        }

        var completion = new TaskCompletionSource<BiometricResult>();
        using var cancellation = new CancellationSignal();
        using var registration = cancellationToken.Register(cancellation.Cancel);

        builder.Build().Authenticate(
            cancellation,
            ContextCompat.GetMainExecutor(activity)!,
            new Callback(completion));

        return await completion.Task.ConfigureAwait(false);
    }

    private sealed class Callback(TaskCompletionSource<BiometricResult> completion)
        : BiometricPrompt.AuthenticationCallback
    {
        public override void OnAuthenticationSucceeded(BiometricPrompt.AuthenticationResult? result) =>
            completion.TrySetResult(BiometricResult.Succeeded);

        public override void OnAuthenticationError(BiometricErrorCode errorCode, Java.Lang.ICharSequence? errString) =>
            completion.TrySetResult((int)errorCode switch
            {
                (int)BiometricErrorCode.Canceled or (int)BiometricErrorCode.UserCanceled
                    or ErrorNegativeButton => BiometricResult.Cancelled,
                (int)BiometricErrorCode.Lockout or (int)BiometricErrorCode.LockoutPermanent => BiometricResult.LockedOut,
                _ => BiometricResult.Failed,
            });

        // Deliberately not completing here: a single unrecognised finger is not
        // a failure, it is a retry. The prompt stays up and the user tries
        // again; only OnAuthenticationError ends the attempt.
        public override void OnAuthenticationFailed() { }
    }

    private sealed class DialogClickListener : JavaObject, global::Android.Content.IDialogInterfaceOnClickListener
    {
        public void OnClick(global::Android.Content.IDialogInterface? dialog, int which) { }
    }
}
