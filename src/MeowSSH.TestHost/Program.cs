using MeowSSH.Core.Security;
using MeowSSH.Core.Services;
using MeowSSH.TestHost.Components;
using MeowSSH.TestHost.Fakes;

var builder = WebApplication.CreateBuilder(args);

// Detailed circuit errors: this host exists to be debugged from a browser, and
// the default "an exception occurred" tells a failing interop call's story badly.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents(options => options.DetailedErrors = true);

// The fakes stand in for Android's Keystore, BiometricPrompt and the Meowshell
// agent, none of which exist on Linux. Everything above them is the same code
// the Android build runs, which is the point: the UI can be driven in CI.
builder.Services.AddScoped<IHostDirectory>(_ => new FakeHostDirectory());
builder.Services.AddScoped<IVaultSession>(_ => new FakeVaultSession(
    VaultState.Locked,
    recoveryCode: MeowSSH.TestHost.TestHostDefaults.RecoveryCode));

var app = builder.Build();

if (!app.Environment.IsDevelopment()) app.UseExceptionHandler("/Error", createScopeForErrors: true);
app.UseStaticFiles();
app.UseAntiforgery();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

app.Run();
