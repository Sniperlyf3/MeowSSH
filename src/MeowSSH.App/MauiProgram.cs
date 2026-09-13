using MeowSSH.App.Platforms.Android;
using MeowSSH.Core.Security;
using MeowSSH.Core.Services;
using MeowSSH.Core.Ssh;
using MeowSSH.Core.Storage;

namespace MeowSSH.App;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();
        builder.Services.AddMauiBlazorWebView();

        // Everything the app runs on is the real implementation: the Keystore,
        // the biometric prompt, the encrypted vault on disk, and the Meowshell
        // agent.
        builder.Services.AddSingleton<IDeviceKeyStore>(_ => new AndroidDeviceKeyStore());
        builder.Services.AddSingleton<IBiometricGate, AndroidBiometricGate>();

        builder.Services.AddSingleton<IVaultStorage>(_ =>
            new FileVaultStorage(Path.Combine(FileSystem.AppDataDirectory, "meowssh.vault")));
        builder.Services.AddSingleton<IDeviceIdentity>(_ =>
            new FileDeviceIdentity(Path.Combine(FileSystem.AppDataDirectory, "device.seed")));
        builder.Services.AddSingleton(sp => new VaultStore(sp.GetRequiredService<IVaultStorage>()));
        builder.Services.AddSingleton<IVaultSession, StoredVaultSession>();

        // One object, three roles, so the list, the editor and the connection
        // path all see the same open vault rather than three views of it.
        builder.Services.AddSingleton<VaultHostDirectory>();
        builder.Services.AddSingleton<IHostDirectory>(sp => sp.GetRequiredService<VaultHostDirectory>());
        builder.Services.AddSingleton<IHostEditor>(sp => sp.GetRequiredService<VaultHostDirectory>());
        builder.Services.AddSingleton<ICredentialResolver>(sp => sp.GetRequiredService<VaultHostDirectory>());

        builder.Services.AddSingleton<ISshPrompts, DecliningPrompts>();
        builder.Services.AddSingleton<ISshEngine>(_ => new MeowshellSshEngine(
            new MeowshellSshEngineOptions(
                // App-private storage: the agent needs a writable HOME, and
                // known_hosts must not be readable by other apps.
                WorkingDirectory: Path.Combine(FileSystem.AppDataDirectory, "agent"),
                KnownHostsPath: Path.Combine(FileSystem.AppDataDirectory, "agent", "known_hosts"))));

#if DEBUG
        // Lets the WebView be inspected from chrome://inspect on a tethered
        // machine, which is the only way to see a JS error from the terminal.
        builder.Services.AddBlazorWebViewDeveloperTools();
#endif

        return builder.Build();
    }
}
