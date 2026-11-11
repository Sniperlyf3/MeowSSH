using MeowSSH.Core.Diagnostics;
using MeowSSH.Core.Licensing;
using MeowSSH.Core.Security;
using MeowSSH.Core.Services;
using MeowSSH.Core.Ssh;
using MeowSSH.Core.Storage;
using MeowSSH.UI.Services;
using Microsoft.Extensions.DependencyInjection;

namespace MeowSSH.UI.Composition;

/// <summary>Where the application keeps its files, and where the native engine binaries are.</summary>
/// <param name="AppDataDirectory">Private, persistent app data (the vault, stores, the agent's known_hosts).</param>
/// <param name="CacheDirectory">Disposable working files.</param>
/// <param name="NativeBinaryDirectory">
/// Where libmeowshell/libtailcat live. On Android this must be
/// ApplicationInfo.NativeLibraryDir: Meowshell's own search looks beside the
/// assemblies, where nothing is executable on Android. Null lets Meowshell find
/// the binaries its runtime package placed beside the assemblies (desktop/Linux).
/// </param>
/// <param name="TailcatDerpMapUrl">The relay map baked into the build, or null for Tailcat's default.</param>
public sealed record MeowSshApplicationPaths(
    string AppDataDirectory,
    string CacheDirectory,
    string? NativeBinaryDirectory,
    string? TailcatDerpMapUrl = null);

/// <summary>
/// The application's platform-neutral service graph: everything the app runs
/// that does not touch Android itself.
/// </summary>
/// <remarks>
/// <para>
/// The app's host (MauiProgram) and the on-device end-to-end tests both build
/// from this one method. Before it existed the graph was written out only in
/// MauiProgram, so a test that assembled its own copy could pass while the app
/// was wired differently -- and a missing registration only showed up as a
/// crash on a phone.
/// </para>
/// <para>
/// The caller still registers the platform services this graph depends on
/// (key store, biometrics, purchases, Play Integrity, secure-storage-backed
/// stores, notifications, file pickers, serial, QR, the VPN controller), plus
/// <see cref="LicensingApiOptions"/> and <see cref="IEntitlementGrantProvider"/>,
/// both of which come from build configuration. Order matters only where one
/// interface has several registrations (<see cref="IProtocolConnectionEngine"/>),
/// and that order is kept exactly as it was.
/// </para>
/// </remarks>
public static class MeowSshApplicationServices
{
    public static IServiceCollection AddMeowSshApplication(this IServiceCollection services, MeowSshApplicationPaths paths)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(paths);
        var data = paths.AppDataDirectory;

        services.AddSingleton<IVaultStorage>(_ => new FileVaultStorage(Path.Combine(data, "meowssh.vault")));
        services.AddSingleton<IDeviceIdentity>(_ => new FileDeviceIdentity(Path.Combine(data, "device.seed")));
        services.AddSingleton(sp => new VaultStore(sp.GetRequiredService<IVaultStorage>()));
        services.AddSingleton<IVaultSession, StoredVaultSession>();

        services.AddSingleton(_ => new HttpClient());
        services.AddSingleton<LicensingApiGrantProvider>();
        services.AddSingleton<IManagedDerpGrantProvider>(sp => sp.GetRequiredService<LicensingApiGrantProvider>());
        services.AddSingleton<IManagedDerpRegistrationService, ManagedDerpRegistrationService>();
        services.AddSingleton<IManagedDerpUsageService, ManagedDerpUsageService>();
        services.AddSingleton<EntitlementService>();
        services.AddSingleton<IEntitlementService>(sp => sp.GetRequiredService<EntitlementService>());

        services.AddSingleton<VaultHostDirectory>();
        services.AddSingleton<IHostDirectory>(sp => sp.GetRequiredService<VaultHostDirectory>());
        services.AddSingleton<IHostEditor>(sp => sp.GetRequiredService<VaultHostDirectory>());
        services.AddSingleton<ICredentialResolver>(sp => sp.GetRequiredService<VaultHostDirectory>());

        services.AddSingleton<InteractiveSshPrompts>();
        services.AddSingleton<ISshPrompts>(sp => sp.GetRequiredService<InteractiveSshPrompts>());
        services.AddSingleton<IPendingDiagnosticReportStore>(_ => new FilePendingDiagnosticReportStore(
            Path.Combine(data, "diagnostics")));
        services.AddSingleton<IDiagnosticsInstallIdStore>(_ => new FileDiagnosticsInstallIdStore(
            Path.Combine(data, "diagnostics", "install-id")));
        services.AddSingleton<IDiagnosticsSendPreferenceStore>(_ => new FileDiagnosticsSendPreferenceStore(
            Path.Combine(data, "diagnostics", "send-enabled")));
        services.AddSingleton<DiagnosticsInstallIdentity>();
        services.AddSingleton<IDiagnosticReportUploadService, DiagnosticReportUploadService>();
        services.AddSingleton<DiagnosticBreadcrumbBuffer>();
        services.AddSingleton<DiagnosticCrashRecorder>();
        services.AddSingleton<ISessionLogService>(sp => new FileSessionLogService(
            Path.Combine(data, "session-logs"),
            sp.GetRequiredService<IEntitlementService>()));
        services.AddSingleton<IAdvancedSftpService, AdvancedSftpService>();
        services.AddSingleton<ISftpRemoteSearchService, SftpRemoteSearchService>();
        services.AddSingleton<ITerminalBroadcastService, TerminalBroadcastService>();
        services.AddSingleton<TerminalBroadcastCoordinator>();
        services.AddSingleton<ISftpBookmarkStore>(_ => new FileSftpBookmarkStore(
            Path.Combine(data, "sftp-bookmarks.json")));
        services.AddSingleton<ISftpBookmarkService, SftpBookmarkService>();
        services.AddSingleton<ITransferHistoryService>(_ => new FileTransferHistoryService(
            Path.Combine(data, "transfer-history.json")));
        services.AddSingleton<TransferQueueService>();
        services.AddSingleton<ITransferQueueService>(sp => sp.GetRequiredService<TransferQueueService>());
        services.AddSingleton<TransferQueueLocalFileCleanup>();
        services.AddSingleton<IEncryptedVaultBackupService, EncryptedVaultBackupService>();
        services.AddSingleton<ICloudEntitlementGrantSource>(sp => sp.GetRequiredService<LicensingApiGrantProvider>());
        services.AddSingleton<ICloudBackupApi>(sp => new HttpCloudBackupApi(
            sp.GetRequiredService<HttpClient>(),
            sp.GetRequiredService<LicensingApiOptions>()));
        services.AddSingleton<ICloudVaultBackupService, CloudVaultBackupService>();
        services.AddSingleton<ICloudSyncCheckpointStore>(_ => new FileCloudSyncCheckpointStore(
            Path.Combine(data, "cloud-sync.json")));
        services.AddSingleton(sp => new CloudVaultSyncService(
            sp.GetRequiredService<VaultStore>(),
            sp.GetRequiredService<IVaultStorage>(),
            sp.GetRequiredService<ICloudBackupApi>(),
            sp.GetRequiredService<ICloudEntitlementGrantSource>(),
            sp.GetRequiredService<ICloudBackupCredentialStore>(),
            sp.GetRequiredService<ICloudSyncCheckpointStore>(),
            sp.GetRequiredService<IDeviceIdentity>(),
            sp.GetRequiredService<IEntitlementService>()));
        services.AddSingleton<ICloudVaultSyncService>(sp => sp.GetRequiredService<CloudVaultSyncService>());
        services.AddSingleton<IHostedAiApi>(sp => new HttpHostedAiApi(
            sp.GetRequiredService<HttpClient>(),
            sp.GetRequiredService<LicensingApiOptions>()));
        services.AddSingleton<IHostedAiService, HostedAiService>();
        services.AddSingleton<IHeartbeatMonitorApi>(sp => new HttpHeartbeatMonitorApi(
            sp.GetRequiredService<HttpClient>(),
            sp.GetRequiredService<LicensingApiOptions>()));
        services.AddSingleton<IHeartbeatLocalStore>(_ => new FileHeartbeatLocalStore(
            Path.Combine(data, "heartbeat-monitors.json")));
        services.AddSingleton<IHeartbeatMonitorService, HeartbeatMonitorService>();
        services.AddSingleton<ITeamApi>(sp => new HttpTeamApi(
            sp.GetRequiredService<HttpClient>(),
            sp.GetRequiredService<LicensingApiOptions>()));
        services.AddSingleton<ITeamService, TeamService>();
        services.AddSingleton<IPortForwardProfileStore>(_ => new FilePortForwardProfileStore(
            Path.Combine(data, "port-forward-profiles.json")));
        services.AddSingleton<IPortForwardProfileService, PortForwardProfileService>();

        // One Tailcat home for the engines and the hub, so the app has one
        // Tailcat client identity. The engines used to run from "agent", whose
        // HOME held no saved client key, so a saved Tailcat host connected as
        // a fresh ephemeral key -- never the one the Keys screen shows and the
        // user puts in another phone's allow list -- and was always refused.
        // known_hosts stays where it was, so trusted hosts survive the upgrade.
        var tailcatHome = Path.Combine(data, "tailcat-home");
        services.AddSingleton(new MeowshellSshEngineOptions(
            WorkingDirectory: tailcatHome,
            KnownHostsPath: Path.Combine(data, "agent", "known_hosts"),
            BinaryDirectory: paths.NativeBinaryDirectory));
        services.AddSingleton(new TailcatHubRuntimeOptions(
            paths.NativeBinaryDirectory ?? AppContext.BaseDirectory,
            tailcatHome,
            Path.Combine(paths.CacheDirectory, "tailcat-work"),
            string.IsNullOrWhiteSpace(paths.TailcatDerpMapUrl) ? null : paths.TailcatDerpMapUrl));
        services.AddSingleton<MeowshellTailcatHubService>();
        services.AddSingleton<ManagedDerpTailcatHubService>(sp => new ManagedDerpTailcatHubService(
            sp.GetRequiredService<MeowshellTailcatHubService>(),
            sp.GetRequiredService<IManagedDerpRegistrationService>()));
        services.AddSingleton<ITailcatHubService>(sp => new EntitlementTailcatHubService(
            sp.GetRequiredService<ManagedDerpTailcatHubService>(),
            sp.GetRequiredService<IEntitlementService>()));
        services.AddSingleton<ITailcatIdentityStore, TailcatIdentityStore>();
        services.AddSingleton<ITailcatWorkspaceStore>(_ => new FileTailcatWorkspaceStore(
            Path.Combine(data, "tailcat-workspaces.json")));
        services.AddSingleton<ITailcatWorkspaceService, TailcatWorkspaceService>();
        services.AddSingleton<ITailcatTemporaryShareStore>(_ => new FileTailcatTemporaryShareStore(
            Path.Combine(data, "tailcat-temporary-shares.json")));
        services.AddSingleton<ITailcatTemporaryShareService, TailcatTemporaryShareService>();
        services.AddSingleton<TailcatServerSessionLifetimeCoordinator>();
        services.AddSingleton<ICommandActionStore>(_ => new FileCommandActionStore(
            Path.Combine(data, "actions.json")));
        services.AddSingleton<ICommandActionService, CommandActionService>();
        services.AddSingleton<IParameterizedCommandActionRunner, ParameterizedCommandActionRunner>();
        services.AddSingleton<ICommandActionSequenceStore>(_ => new FileCommandActionSequenceStore(
            Path.Combine(data, "action-sequences.json")));
        services.AddSingleton<ICommandActionSequenceService, CommandActionSequenceService>();
        services.AddSingleton<IHostHealthDashboardService, HostHealthDashboardService>();
        services.AddSingleton<ICommandMonitorStore>(_ => new FileCommandMonitorStore(
            Path.Combine(data, "command-monitors.json")));
        services.AddSingleton<CommandMonitoringService>();
        services.AddSingleton<ICommandMonitoringService>(sp => sp.GetRequiredService<CommandMonitoringService>());

        services.AddSingleton<ISshEngine>(sp => new MeowshellSshEngine(sp.GetRequiredService<MeowshellSshEngineOptions>()));
        services.AddSingleton<IProtocolConnectionEngine>(sp => new SshProtocolConnectionEngine(sp.GetRequiredService<ISshEngine>()));
        services.AddSingleton<IProtocolConnectionEngine>(sp => new MoshConnectionEngine(sp.GetRequiredService<MeowshellSshEngineOptions>()));
        services.AddSingleton<IProtocolConnectionEngine, TelnetConnectionEngine>();
        services.AddSingleton<IProtocolConnectionEngine, SerialConnectionEngine>();
        services.AddSingleton<IProtocolConnectionEngine>(sp => new LocalTerminalConnectionEngine(sp.GetRequiredService<MeowshellSshEngineOptions>()));
        services.AddSingleton<ConnectionEngine>();
        services.AddSingleton<ProxyJumpConnector>();
        services.AddSingleton<IConnectionEngine>(sp => new SessionLoggingConnectionEngine(
            sp.GetRequiredService<ProxyJumpConnector>(),
            sp.GetRequiredService<ISessionLogService>()));

        return services;
    }
}
