using MeowSSH.Core.Licensing;
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
builder.Services.AddScoped<ConnectionEngine>();
builder.Services.AddScoped<ICredentialResolver, FakeCredentialResolver>();
builder.Services.AddScoped<ISerialDeviceService, UnsupportedSerialDeviceService>();
builder.Services.AddScoped<ITailcatHubService, FakeTailcatHubService>();
builder.Services.AddScoped<ITailcatIdentityStore, FakeTailcatIdentityStore>();
builder.Services.AddScoped<ITailcatWorkspaceStore, MemoryTailcatWorkspaceStore>();
builder.Services.AddScoped<ITailcatWorkspaceService, TailcatWorkspaceService>();
builder.Services.AddScoped<ICommandActionStore, MemoryCommandActionStore>();
builder.Services.AddScoped<ICommandActionService, CommandActionService>();
builder.Services.AddScoped<ICommandMonitorStore, MemoryCommandMonitorStore>();
builder.Services.AddScoped<ICommandMonitorAlertSink, NoOpCommandMonitorAlertSink>();
builder.Services.AddScoped<CommandMonitoringService>();
builder.Services.AddScoped<ICommandMonitoringService>(sp => sp.GetRequiredService<CommandMonitoringService>());
builder.Services.AddScoped<IQrScanner, FakeQrScanner>();
builder.Services.AddScoped<ITailcatVpnController, FakeTailcatVpnController>();
builder.Services.AddScoped<IEntitlementService, FakeEntitlementService>();
builder.Services.AddScoped<IStorePurchaseService, FakeStorePurchaseService>();
builder.Services.AddScoped<ISessionLogService>(sp => new FileSessionLogService(
    Path.Combine(Path.GetTempPath(), "meowssh-testhost-session-logs", Guid.NewGuid().ToString("N")),
    sp.GetRequiredService<IEntitlementService>()));
builder.Services.AddScoped<IAdvancedSftpService, AdvancedSftpService>();
builder.Services.AddScoped<IEncryptedVaultBackupService, FakeEncryptedVaultBackupService>();
builder.Services.AddScoped<FakeSshHardwareKeyStore>();
builder.Services.AddScoped<ISshHardwareKeyStore>(sp => sp.GetRequiredService<FakeSshHardwareKeyStore>());
builder.Services.AddScoped<ISshHardwareKeySigner>(sp => sp.GetRequiredService<FakeSshHardwareKeyStore>());
builder.Services.AddScoped<ProxyJumpConnector>();
builder.Services.AddScoped<IConnectionEngine>(sp => new SessionLoggingConnectionEngine(
    sp.GetRequiredService<ProxyJumpConnector>(),
    sp.GetRequiredService<ISessionLogService>()));
builder.Services.AddScoped(_ => new FakeVaultSession(
    VaultState.Locked,
    recoveryCode: MeowSSH.TestHost.TestHostDefaults.RecoveryCode));
builder.Services.AddScoped<IVaultSession>(sp => sp.GetRequiredService<FakeVaultSession>());

builder.Services.AddScoped<IActiveSessionLifetime, NoOpActiveSessionLifetime>();
builder.Services.AddScoped<ILocalFileTransferService, LocalFileTransferService>();
builder.Services.AddScoped<IExternalUriLauncher, NoOpExternalUriLauncher>();
builder.Services.AddScoped(_ => new AppExternalLinks(
    new Uri("https://example.test/privacy"),
    new Uri("https://example.test/support"),
    new Uri("https://example.test/terms")));

builder.Services.AddScoped<InteractiveSshPrompts>();
builder.Services.AddScoped<ISshPrompts>(sp => sp.GetRequiredService<InteractiveSshPrompts>());

var app = builder.Build();

if (!app.Environment.IsDevelopment()) app.UseExceptionHandler("/Error", createScopeForErrors: true);
app.UseStaticFiles();
app.UseAntiforgery();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

app.Run();