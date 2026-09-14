using MeowSSH.Core.Security;
using MeowSSH.Core.Services;
using MeowSSH.Core.Ssh;
using MeowSSH.TestHost.Components;
using MeowSSH.TestHost.Fakes;
using MeowSSH.UI.Services;

var builder = WebApplication.CreateBuilder(args);

// Detailed circuit errors: this host exists to be debugged from a browser, and
// the default "an exception occurred" tells a failing interop call's story badly.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents(options => options.DetailedErrors = true);

// The fakes stand in for Android's Keystore, BiometricPrompt and the Meowshell
// agent, none of which exist on Linux. Everything above them is the same code
// the Android build runs, which is the point: the UI can be driven in CI.
builder.Services.AddScoped<FakeHostDirectory>();
builder.Services.AddScoped<IHostDirectory>(sp => sp.GetRequiredService<FakeHostDirectory>());
builder.Services.AddScoped<IHostEditor>(sp => sp.GetRequiredService<FakeHostDirectory>());
builder.Services.AddScoped<ISshEngine, FakeSshEngine>();
builder.Services.AddScoped<ICredentialResolver, FakeCredentialResolver>();
// Registered concretely as well as behind the interface: the playground picks
// its scenario from the query string and needs to put the vault into a state
// the interface deliberately has no way to ask for.
builder.Services.AddScoped(_ => new FakeVaultSession(
    VaultState.Locked,
    recoveryCode: MeowSSH.TestHost.TestHostDefaults.RecoveryCode));
builder.Services.AddScoped<IVaultSession>(sp => sp.GetRequiredService<FakeVaultSession>());

builder.Services.AddScoped<IActiveSessionLifetime, NoOpActiveSessionLifetime>();
builder.Services.AddScoped<ILocalFileTransferService, LocalFileTransferService>();

// The same prompts object the Android app uses, so the browser tests drive the
// real trust-on-first-use flow rather than a stand-in for it.
builder.Services.AddScoped<InteractiveSshPrompts>();
builder.Services.AddScoped<ISshPrompts>(sp => sp.GetRequiredService<InteractiveSshPrompts>());

var app = builder.Build();

if (!app.Environment.IsDevelopment()) app.UseExceptionHandler("/Error", createScopeForErrors: true);
app.UseStaticFiles();
app.UseAntiforgery();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

app.Run();

