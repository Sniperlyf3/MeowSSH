using MeowSSH.Android;
using MeowSSH.App.Services;
using MeowSSH.Core.Licensing;
using MeowSSH.Core.Security;
using MeowSSH.Core.Services;
using MeowSSH.Core.Ssh;
using MeowSSH.Core.Storage;
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

        builder.Services.AddSingleton<IDeviceKeyStore>(_ => new AndroidDeviceKeyStore());
        builder.Services.AddSingleton<AndroidSshHardwareKeyStore>();
        builder.Services.AddSingleton<ISshHardwareKeyStore>(sp => sp.GetRequiredService<AndroidSshHardwareKeyStore>());
        builder.Services.AddSingleton<ISshHardwareKeySigner>(sp => sp.GetRequiredService<AndroidSshHardwareKeyStore>());
        builder.Services.AddSingleton<IBiometricGate>(_ => new AndroidBiometricGate(() => Platform.CurrentActivity));

        builder.Services.AddSingleton<IVaultStorage>(_ => new FileVaultStorage(Path.Combine(FileSystem.AppDataDirectory, "meowssh.vault")));
        builder.Services.AddSingleton<IDeviceIdentity>(_ => new FileDeviceIdentity(Path.Combine(FileSystem.AppDataDirectory, "device.seed")));
        builder.Services.AddSingleton(sp => new VaultStore(sp.GetRequiredService<IVaultStorage>()));
        builder.Services.AddSingleton<IVaultSession, StoredVaultSession>();

        builder.Services.AddSingleton<IStorePurchaseService, GooglePlayPurchaseService>();
        builder.Services.AddSingleton<IPlayIntegrityService, GooglePlayIntegrityService>();
        builder.Services.AddSingleton<IEntitlementCache, SecureStorageEntitlementCache>();
        builder.Services.AddSingleton(new LicensingApiOptions(
            Uri.TryCreate(LicensingBuildConfig.ApiBaseUrl, UriKind.Absolute, out var licensingUri) ? licensingUri : null,
            LicensingBuildConfig.PublicKeySubjectPublicKeyInfoBase64,
            "dev.sniperlyf3.meowssh"));
        builder.Services.AddSingleton(_ => new HttpClient());
        builder.Services.AddSingleton<IEntitlementGrantProvider, LicensingApiGrantProvider>();
        builder.Services.AddSingleton<EntitlementService>();
        builder.Services.AddSingleton<IEntitlementService>(sp => sp.GetRequiredService<EntitlementService>());

        builder.Services.AddSingleton<VaultHostDirectory>();
        builder.Services.AddSingleton<IHostDirectory>(sp => sp.GetRequiredService<VaultHostDirectory>());
        builder.Services.AddSingleton<IHostEditor>(sp => sp.GetRequiredService<VaultHostDirectory>());
        builder.Services.AddSingleton<ICredentialResolver>(sp => sp.GetRequiredService<VaultHostDirectory>());

        builder.Services.AddSingleton<InteractiveSshPrompts>();
        builder.Services.AddSingleton<ISshPrompts>(sp => sp.GetRequiredService<InteractiveSshPrompts>());
        builder.Services.AddSingleton<IActiveSessionLifetime, AndroidActiveSessionLifetime>();
        builder.Services.AddSingleton<ILocalFileTransferService, AndroidLocalFileTransferService>();
        builder.Services.AddSingleton<IExternalUriLauncher, AndroidExternalUriLauncher>();
        builder.Services.AddSingleton<IQrScanner, AndroidQrScanner>();
        builder.Services.AddSingleton<ISerialDeviceService, AndroidUsbSerialDeviceService>();
        builder.Services.AddSingleton<ISessionLogService>(sp => new FileSessionLogService(
            Path.Combine(FileSystem.AppDataDirectory, "session-logs"),
            sp.GetRequiredService<IEntitlementService>()));
        builder.Services.AddSingleton<IAdvancedSftpService, AdvancedSftpService>();
        builder.Services.AddSingleton<IEncryptedVaultBackupService, EncryptedVaultBackupService>();

        var nativeDirectory = global::Android.App.Application.Context.ApplicationInfo!.NativeLibraryDir!;
        var engineOptions = new MeowshellSshEngineOptions(
            WorkingDirectory: Path.Combine(FileSystem.AppDataDirectory, "agent"),
            KnownHostsPath: Path.Combine(FileSystem.AppDataDirectory, "agent", "known_hosts"),
            BinaryDirectory: nativeDirectory);
        builder.Services.AddSingleton(engineOptions);
        builder.Services.AddSingleton(new TailcatHubRuntimeOptions(
            nativeDirectory,
            Path.Combine(FileSystem.AppDataDirectory, "tailcat-home"),
            Path.Combine(FileSystem.CacheDirectory, "tailcat-work")));
        builder.Services.AddSingleton<MeowshellTailcatHubService>();
        builder.Services.AddSingleton<ITailcatHubService>(sp => new EntitlementTailcatHubService(
            sp.GetRequiredService<MeowshellTailcatHubService>(),
            sp.GetRequiredService<IEntitlementService>()));
        builder.Services.AddSingleton<ITailcatIdentityStore, TailcatIdentityStore>();
        builder.Services.AddSingleton<ITailcatWorkspaceStore>(_ => new FileTailcatWorkspaceStore(
            Path.Combine(FileSystem.AppDataDirectory, "tailcat-workspaces.json")));
        builder.Services.AddSingleton<ITailcatWorkspaceService, TailcatWorkspaceService>();
        builder.Services.AddSingleton<ICommandActionStore>(_ => new FileCommandActionStore(
            Path.Combine(FileSystem.AppDataDirectory, "actions.json")));
        builder.Services.AddSingleton<ICommandActionService, CommandActionService>();
        builder.Services.AddSingleton<ICommandMonitorStore>(_ => new FileCommandMonitorStore(
            Path.Combine(FileSystem.AppDataDirectory, "command-monitors.json")));
        builder.Services.AddSingleton<ICommandMonitorAlertSink, AndroidCommandMonitorAlertSink>();
        builder.Services.AddSingleton<CommandMonitoringService>();
        builder.Services.AddSingleton<ICommandMonitoringService>(sp => sp.GetRequiredService<CommandMonitoringService>());
        builder.Services.AddSingleton<AndroidTailcatVpnController>();
        builder.Services.AddSingleton<ITailcatVpnController>(sp => new EntitlementTailcatVpnController(
            sp.GetRequiredService<AndroidTailcatVpnController>(),
            sp.GetRequiredService<IEntitlementService>()));

        builder.Services.AddSingleton<ISshEngine>(sp => new MeowshellSshEngine(sp.GetRequiredService<MeowshellSshEngineOptions>()));
        builder.Services.AddSingleton<IProtocolConnectionEngine>(sp => new SshProtocolConnectionEngine(sp.GetRequiredService<ISshEngine>()));
        builder.Services.AddSingleton<IProtocolConnectionEngine>(sp => new MoshConnectionEngine(sp.GetRequiredService<MeowshellSshEngineOptions>()));
        builder.Services.AddSingleton<IProtocolConnectionEngine, TelnetConnectionEngine>();
        builder.Services.AddSingleton<IProtocolConnectionEngine, SerialConnectionEngine>();
        builder.Services.AddSingleton<IProtocolConnectionEngine>(sp => new LocalTerminalConnectionEngine(sp.GetRequiredService<MeowshellSshEngineOptions>()));
        builder.Services.AddSingleton<ConnectionEngine>();
        builder.Services.AddSingleton<ProxyJumpConnector>();
        builder.Services.AddSingleton<IConnectionEngine>(sp => new SessionLoggingConnectionEngine(
            sp.GetRequiredService<ProxyJumpConnector>(),
            sp.GetRequiredService<ISessionLogService>()));

#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
#endif

        return builder.Build();
    }
}