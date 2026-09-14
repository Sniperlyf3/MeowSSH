using MeowSSH.Core.Security;
using MeowSSH.Core.Services;
using MeowSSH.Core.Ssh;
using MeowSSH.TestHost.Components;
using MeowSSH.TestHost.Fakes;
using MeowSSH.UI.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents(options => options.DetailedErrors = true);

builder.Services.AddScoped<FakeHostDirectory>();
builder.Services.AddScoped<IHostDirectory>(sp => sp.GetRequiredService<FakeHostDirectory>());
builder.Services.AddScoped<IHostEditor>(sp => sp.GetRequiredService<FakeHostDirectory>());
builder.Services.AddScoped<ISshEngine, FakeSshEngine>();
builder.Services.AddScoped<IProtocolConnectionEngine>(sp =>
    new SshProtocolConnectionEngine(sp.GetRequiredService<ISshEngine>()));
builder.Services.AddScoped<IConnectionEngine, ConnectionEngine>();
builder.Services.AddScoped<ICredentialResolver, FakeCredentialResolver>();
builder.Services.AddScoped(_ => new FakeVaultSession(
    VaultState.Locked,
    recoveryCode: MeowSSH.TestHost.TestHostDefaults.RecoveryCode));
builder.Services.AddScoped<IVaultSession>(sp => sp.GetRequiredService<FakeVaultSession>());

builder.Services.AddScoped<IActiveSessionLifetime, NoOpActiveSessionLifetime>();
builder.Services.AddScoped<ILocalFileTransferService, LocalFileTransferService>();

builder.Services.AddScoped<InteractiveSshPrompts>();
builder.Services.AddScoped<ISshPrompts>(sp => sp.GetRequiredService<InteractiveSshPrompts>());

var app = builder.Build();

if (!app.Environment.IsDevelopment()) app.UseExceptionHandler("/Error", createScopeForErrors: true);
app.UseStaticFiles();
app.UseAntiforgery();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

app.Run();
