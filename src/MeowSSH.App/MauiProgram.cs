using MeowSSH.Android;
using MeowSSH.Core.Security;
using MeowSSH.Core.Services;
using MeowSSH.Core.Ssh;
using MeowSSH.Core.Storage;
using MeowSSH.UI.Services;

namespace MeowSSH.App;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();
        builder.Services.AddMauiBlazorWebView();

        builder.Services.AddSingleton<IDeviceKeyStore>(_ => new AndroidDeviceKeyStore());
        builder.Services.AddSingleton<IBiometricGate>(_ => new AndroidBiometricGate(() => Platform.CurrentActivity));

        builder.Services.AddSingleton<IVaultStorage>(_ =>
            new FileVaultStorage(Path.Combine(FileSystem.AppDataDirectory, "meowssh.vault")));
        builder.Services.AddSingleton<IDeviceIdentity>(_ =>
            new FileDeviceIdentity(Path.Combine(FileSystem.AppDataDirectory, "device.seed")));
        builder.Services.AddSingleton(sp => new VaultStore(sp.GetRequiredService<IVaultStorage>()));
        builder.Services.AddSingleton<IVaultSession, StoredVaultSession>();

        builder.Services.AddSingleton<VaultHostDirectory>();
        builder.Services.AddSingleton<IHostDirectory>(sp => sp.GetRequiredService<VaultHostDirectory>());
        builder.Services.AddSingleton<IHostEditor>(sp => sp.GetRequiredService<VaultHostDirectory>());
        builder.Services.AddSingleton<ICredentialResolver>(sp => sp.GetRequiredService<VaultHostDirectory>());

        builder.Services.AddSingleton<InteractiveSshPrompts>();
        builder.Services.AddSingleton<ISshPrompts>(sp => sp.GetRequiredService<InteractiveSshPrompts>());
        builder.Services.AddSingleton<IActiveSessionLifetime, AndroidActiveSessionLifetime>();
        builder.Services.AddSingleton<ILocalFileTransferService, AndroidLocalFileTransferService>();

        var engineOptions = new MeowshellSshEngineOptions(
            WorkingDirectory: Path.Combine(FileSystem.AppDataDirectory, "agent"),
            KnownHostsPath: Path.Combine(FileSystem.AppDataDirectory, "agent", "known_hosts"),
            BinaryDirectory: global::Android.App.Application.Context.ApplicationInfo!.NativeLibraryDir);
        builder.Services.AddSingleton(engineOptions);
        builder.Services.AddSingleton<ISshEngine>(sp =>
            new MeowshellSshEngine(sp.GetRequiredService<MeowshellSshEngineOptions>()));
        builder.Services.AddSingleton<IProtocolConnectionEngine>(sp =>
            new SshProtocolConnectionEngine(sp.GetRequiredService<ISshEngine>()));
        builder.Services.AddSingleton<IProtocolConnectionEngine, TelnetConnectionEngine>();
        builder.Services.AddSingleton<IProtocolConnectionEngine>(sp =>
            new LocalTerminalConnectionEngine(sp.GetRequiredService<MeowshellSshEngineOptions>()));
        builder.Services.AddSingleton<IConnectionEngine, ConnectionEngine>();

#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
#endif

        return builder.Build();
    }
}
