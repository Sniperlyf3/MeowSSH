using MeowSSH.Android;
using MeowSSH.App.Services;
using MeowSSH.Core.Diagnostics;
using MeowSSH.Core.Licensing;
using MeowSSH.Core.Security;
using MeowSSH.Core.Services;
using MeowSSH.Core.Ssh;
using MeowSSH.Core.Storage;
using MeowSSH.UI.Composition;
using MeowSSH.UI.Services;
using ZXing.Net.Maui.Controls;

namespace MeowSSH.App;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>().UseBarcodeReader();
        builder.Services.AddMauiBlazorWebView();

        // Platform services: Android's half of the abstractions the shared graph
        // depends on. Everything platform-neutral is registered once, in
        // AddMeowSshApplication below, which the on-device E2E tests build too.
        builder.Services.AddSingleton<IDeviceKeyStore>(_ => new AndroidDeviceKeyStore());
        builder.Services.AddSingleton<AndroidSshHardwareKeyStore>();
        builder.Services.AddSingleton<ISshHardwareKeyStore>(sp => sp.GetRequiredService<AndroidSshHardwareKeyStore>());
        builder.Services.AddSingleton<ISshHardwareKeySigner>(sp => sp.GetRequiredService<AndroidSshHardwareKeyStore>());
        builder.Services.AddSingleton<IBiometricGate>(_ => new AndroidBiometricGate(() => Platform.CurrentActivity));

        builder.Services.AddSingleton<IStorePurchaseService, GooglePlayPurchaseService>();
        builder.Services.AddSingleton<IPlayIntegrityService, GooglePlayIntegrityService>();
        builder.Services.AddSingleton<IEntitlementCache, SecureStorageEntitlementCache>();
        builder.Services.AddSingleton(new LicensingApiOptions(
            Uri.TryCreate(LicensingBuildConfig.ApiBaseUrl, UriKind.Absolute, out var licensingUri) ? licensingUri : null,
            LicensingBuildConfig.PublicKeySubjectPublicKeyInfoBase64,
            "dev.sniperlyf3.meowssh"));
#if FORCE_PRO_ENTITLEMENT
        builder.Services.AddSingleton<IEntitlementGrantProvider>(_ => new AlwaysProEntitlementGrantProvider(TimeProvider.System));
#else
        builder.Services.AddSingleton<IEntitlementGrantProvider>(sp => sp.GetRequiredService<LicensingApiGrantProvider>());
#endif

        builder.Services.AddSingleton<IActiveSessionLifetime, AndroidActiveSessionLifetime>();
        builder.Services.AddSingleton<ILocalFileTransferService, AndroidLocalFileTransferService>();
        builder.Services.AddSingleton<IExternalUriLauncher, AndroidExternalUriLauncher>();
        builder.Services.AddSingleton<IThirdPartyNoticeProvider, PackagedThirdPartyNoticeProvider>();
        builder.Services.AddSingleton(LaunchBuildConfig.ExternalLinks);
        builder.Services.AddSingleton<IQrScanner, AndroidQrScanner>();
        builder.Services.AddSingleton<ISerialDeviceService, AndroidUsbSerialDeviceService>();
        builder.Services.AddSingleton<ICloudBackupCredentialStore, SecureStorageCloudBackupCredentialStore>();
        builder.Services.AddSingleton<IMonitorOwnerSecretStore, SecureStorageMonitorOwnerSecretStore>();
        builder.Services.AddSingleton<IHeartbeatAlertSink, AndroidHeartbeatAlertSink>();
        builder.Services.AddSingleton<IHeartbeatCheckScheduler, AndroidHeartbeatCheckScheduler>();
        builder.Services.AddSingleton<INotificationConsent, AndroidNotificationConsent>();
        builder.Services.AddSingleton<ITeamMemberSecretStore, SecureStorageTeamMemberSecretStore>();
        builder.Services.AddSingleton<ICommandMonitorAlertSink, AndroidCommandMonitorAlertSink>();
#if TAILCAT_VPN
        builder.Services.AddSingleton<AndroidTailcatVpnController>();
        builder.Services.AddSingleton<ITailcatVpnController>(sp => new EntitlementTailcatVpnController(
            sp.GetRequiredService<AndroidTailcatVpnController>(),
            sp.GetRequiredService<IEntitlementService>()));
#else
        builder.Services.AddSingleton<UnavailableTailcatVpnController>();
        builder.Services.AddSingleton<ITailcatVpnController>(sp => new EntitlementTailcatVpnController(
            sp.GetRequiredService<UnavailableTailcatVpnController>(),
            sp.GetRequiredService<IEntitlementService>()));
#endif

        builder.Services.AddMeowSshApplication(new MeowSshApplicationPaths(
            FileSystem.AppDataDirectory,
            FileSystem.CacheDirectory,
            global::Android.App.Application.Context.ApplicationInfo!.NativeLibraryDir!,
            TailcatBuildConfig.DerpMapUrl));

#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
#endif

        return builder.Build();
    }
}
