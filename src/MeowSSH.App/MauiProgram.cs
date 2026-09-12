using MeowSSH.App.Platforms.Android;
using MeowSSH.Core.Security;
using MeowSSH.Core.Services;
using MeowSSH.Core.Ssh;

namespace MeowSSH.App;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();
        builder.Services.AddMauiBlazorWebView();

        // Everything the app runs on is the real implementation: the Keystore,
        // the biometric prompt, and the Meowshell agent. Only the host list is
        // still in memory, pending the encrypted store.
        builder.Services.AddSingleton<IDeviceKeyStore>(_ => new AndroidDeviceKeyStore());
        builder.Services.AddSingleton<IBiometricGate, AndroidBiometricGate>();
        builder.Services.AddSingleton<IVaultSession, AndroidVaultSession>();
        builder.Services.AddSingleton<IHostDirectory, PreviewHostDirectory>();
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
