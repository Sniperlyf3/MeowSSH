using System.Text;
using MeowSSH.Core.Licensing;
using MeowSSH.Core.Model;
using MeowSSH.Core.Security;
using MeowSSH.Core.Services;
using MeowSSH.Core.Ssh;
using MeowSSH.UI.Composition;
using MeowSSH.UI.Services;
using Microsoft.Extensions.DependencyInjection;

namespace MeowSSH.E2E;

/// <summary>Where a scenario runs: the platform's key stores and native binaries.</summary>
public interface IE2EPlatform
{
    string Name { get; }

    /// <summary>Where the engine binaries live, or null to let Meowshell find its own.</summary>
    string? NativeBinaryDirectory { get; }

    /// <summary>A writable directory scenarios can create app data under.</summary>
    string ScratchRoot { get; }

    /// <summary>
    /// Registers <see cref="IDeviceKeyStore"/>, <see cref="ISshHardwareKeyStore"/>
    /// and <see cref="ISshHardwareKeySigner"/>. <paramref name="instanceId"/> is
    /// unique per app instance, for platforms whose stores are device-global.
    /// </summary>
    void AddPlatformServices(IServiceCollection services, string instanceId);

    /// <summary>Removes whatever <see cref="AddPlatformServices"/> left on the device.</summary>
    Task CleanUpAsync(string instanceId) => Task.CompletedTask;
}

/// <summary>Software key stores; the Linux run.</summary>
public sealed class SoftwareE2EPlatform(string scratchRoot) : IE2EPlatform
{
    public string Name => "linux";
    public string? NativeBinaryDirectory => null;
    public string ScratchRoot { get; } = Directory.CreateDirectory(scratchRoot).FullName;

    public void AddPlatformServices(IServiceCollection services, string instanceId)
    {
        services.AddSingleton<IDeviceKeyStore, SoftwareDeviceKeyStore>();
        services.AddSingleton<SoftwareSshHardwareKeyStore>();
        services.AddSingleton<ISshHardwareKeyStore>(sp => sp.GetRequiredService<SoftwareSshHardwareKeyStore>());
        services.AddSingleton<ISshHardwareKeySigner>(sp => sp.GetRequiredService<SoftwareSshHardwareKeyStore>());
    }
}

/// <summary>
/// One install of the app: its real service graph, a created and unlocked
/// vault, and a "user" who answers prompts the way a scenario says to.
/// </summary>
public sealed class E2EApp : IAsyncDisposable
{
    private readonly ServiceProvider _services;
    private readonly IE2EPlatform _platform;
    private readonly string _instanceId;
    private readonly InteractiveSshPrompts _prompts;
    private readonly List<HostKeyPrompt> _hostKeyPrompts = [];
    private int _keyCount;

    private E2EApp(ServiceProvider services, IE2EPlatform platform, string instanceId, string dataDirectory)
    {
        _services = services;
        _platform = platform;
        _instanceId = instanceId;
        DataDirectory = dataDirectory;
        _prompts = services.GetRequiredService<InteractiveSshPrompts>();
        _prompts.Changed += OnPromptsChanged;
    }

    public IServiceProvider Services => _services;
    public string DataDirectory { get; }
    public T Get<T>() where T : notnull => _services.GetRequiredService<T>();
    public FixedEntitlementGrantProvider Entitlement => Get<FixedEntitlementGrantProvider>();
    public DirectoryLocalFileTransferService LocalFiles => Get<DirectoryLocalFileTransferService>();
    public string KnownHostsPath => Get<MeowshellSshEngineOptions>().KnownHostsPath!;

    /// <summary>What the user says to an unknown host key. Default: accept, as after reading the fingerprint.</summary>
    public bool AcceptUnknownHostKeys { get; set; } = true;

    /// <summary>The host-key questions the app actually put to the user, in order.</summary>
    public IReadOnlyList<HostKeyPrompt> HostKeyPrompts { get { lock (_hostKeyPrompts) return [.. _hostKeyPrompts]; } }

    /// <summary>What the user types at a password prompt the vault could not answer. Null cancels.</summary>
    public string? TypedPassword { get; set; }

    /// <summary>How the user answers a keyboard-interactive challenge. Null cancels.</summary>
    public Func<KeyboardInteractivePrompt, IReadOnlyList<string>?>? ChallengeAnswers { get; set; }

    public int PasswordPrompts { get; private set; }
    public int ChallengePrompts { get; private set; }

    public static async Task<E2EApp> StartAsync(
        IE2EPlatform platform,
        string dataDirectory,
        EntitlementTier tier = EntitlementTier.ProCloud,
        CancellationToken cancellationToken = default)
    {
        var instanceId = "e2e-" + Guid.NewGuid().ToString("N")[..12];
        Directory.CreateDirectory(dataDirectory);

        var services = new ServiceCollection();
        platform.AddPlatformServices(services, instanceId);
        services.AddSingleton<IBiometricGate, ApprovingBiometricGate>();
        services.AddSingleton<IStorePurchaseService, NoStorePurchaseService>();
        services.AddSingleton<IPlayIntegrityService, NoPlayIntegrityService>();
        services.AddSingleton<IEntitlementCache, InMemoryEntitlementCache>();
        // No backend: the licensing client is unconfigured, exactly as a build
        // without LicensingApiBaseUrl is, and entitlement comes from the scenario.
        services.AddSingleton(new LicensingApiOptions(null, "", "dev.sniperlyf3.meowssh"));
        services.AddSingleton(new FixedEntitlementGrantProvider(tier));
        services.AddSingleton<IEntitlementGrantProvider>(sp => sp.GetRequiredService<FixedEntitlementGrantProvider>());
        services.AddSingleton<IActiveSessionLifetime, NoOpActiveSessionLifetime>();
        services.AddSingleton(new DirectoryLocalFileTransferService(Path.Combine(dataDirectory, "user-files")));
        services.AddSingleton<ILocalFileTransferService>(sp => sp.GetRequiredService<DirectoryLocalFileTransferService>());
        services.AddSingleton<IExternalUriLauncher, NoOpExternalUriLauncher>();
        services.AddSingleton<IThirdPartyNoticeProvider, EmptyThirdPartyNoticeProvider>();
        services.AddSingleton(AppExternalLinks.Empty);
        services.AddSingleton<IQrScanner, NoQrScanner>();
        services.AddSingleton<ISerialDeviceService, NoSerialDeviceService>();
        services.AddSingleton<ICloudBackupCredentialStore, InMemoryCloudBackupCredentialStore>();
        services.AddSingleton<IMonitorOwnerSecretStore>(_ => new InMemorySecretStore());
        services.AddSingleton<ITeamMemberSecretStore>(_ => new InMemorySecretStore());
        services.AddSingleton<IHeartbeatAlertSink, NoOpHeartbeatAlertSink>();
        services.AddSingleton<IHeartbeatCheckScheduler, NoOpHeartbeatCheckScheduler>();
        services.AddSingleton<INotificationConsent, GrantedNotificationConsent>();
        services.AddSingleton<RecordingCommandMonitorAlertSink>();
        services.AddSingleton<ICommandMonitorAlertSink>(sp => sp.GetRequiredService<RecordingCommandMonitorAlertSink>());
        services.AddSingleton<UnavailableTailcatVpnController>();
        services.AddSingleton<ITailcatVpnController>(sp => new EntitlementTailcatVpnController(
            sp.GetRequiredService<UnavailableTailcatVpnController>(),
            sp.GetRequiredService<IEntitlementService>()));

        services.AddMeowSshApplication(new MeowSshApplicationPaths(
            dataDirectory,
            Path.Combine(dataDirectory, "cache"),
            platform.NativeBinaryDirectory));

        // ValidateOnBuild is the point of building the real graph: a service the
        // app registers but cannot construct fails here, not on a phone.
        var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        var app = new E2EApp(provider, platform, instanceId, dataDirectory);
        try
        {
            var entitlements = app.Get<EntitlementService>();
            await entitlements.InitializeAsync(cancellationToken);
            await entitlements.RefreshAsync(cancellationToken);

            var vault = app.Get<IVaultSession>();
            await vault.InitializeAsync(cancellationToken);
            var created = await vault.CreateAsync(cancellationToken);
            if (!created.Succeeded) throw new InvalidOperationException("Vault setup failed: " + created.Message);
            return app;
        }
        catch
        {
            await app.DisposeAsync();
            throw;
        }
    }

    private void OnPromptsChanged(object? sender, EventArgs e)
    {
        // Raised inside the handshake, the same moment SshPromptSheets would
        // show a sheet. Answering here is the user tapping a button.
        if (_prompts.PendingHostKey is { } hostKey)
        {
            lock (_hostKeyPrompts) _hostKeyPrompts.Add(hostKey.Prompt);
            hostKey.Answer(AcceptUnknownHostKeys);
        }
        if (_prompts.PendingPassword is { } password)
        {
            PasswordPrompts++;
            password.Answer(TypedPassword);
        }
        if (_prompts.PendingChallenge is { } challenge)
        {
            ChallengePrompts++;
            challenge.Answer(ChallengeAnswers?.Invoke(challenge.Prompt));
        }
    }

    public Task<CredentialRecord> AddPasswordAsync(string username, string password) =>
        SaveCredentialAsync(new CredentialRecord
        {
            Id = Guid.NewGuid(),
            Label = "password " + username,
            Kind = CredentialKind.Password,
            Username = username,
            Secret = Encoding.UTF8.GetBytes(password),
            UpdatedAt = DateTimeOffset.UtcNow,
        });

    public Task<CredentialRecord> AddPrivateKeyAsync(string username, string privateKey, string? passphrase = null) =>
        SaveCredentialAsync(new CredentialRecord
        {
            Id = Guid.NewGuid(),
            Label = "key " + username,
            Kind = CredentialKind.PrivateKey,
            Username = username,
            Secret = Encoding.UTF8.GetBytes(privateKey),
            Passphrase = passphrase is null ? null : Encoding.UTF8.GetBytes(passphrase),
            UpdatedAt = DateTimeOffset.UtcNow,
        });

    /// <summary>Generates a non-exportable key in the platform key store and saves it, as the Keys screen does.</summary>
    public async Task<CredentialRecord> AddHardwareKeyAsync(string username)
    {
        var keyId = $"{_instanceId}-ssh{Interlocked.Increment(ref _keyCount)}";
        var info = await Get<ISshHardwareKeyStore>().GenerateP256Async(keyId);
        return await SaveCredentialAsync(new CredentialRecord
        {
            Id = Guid.NewGuid(),
            Label = "keystore " + username,
            Kind = CredentialKind.HardwareKey,
            Username = username,
            Secret = Encoding.UTF8.GetBytes(info.KeyId),
            PublicKey = info.PublicKey,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
    }

    private async Task<CredentialRecord> SaveCredentialAsync(CredentialRecord credential)
    {
        await Get<IHostEditor>().SaveCredentialAsync(credential);
        return credential;
    }

    public async Task<HostRecord> AddHostAsync(
        string label,
        string address,
        int port,
        CredentialRecord? credential,
        Func<HostRecord, HostRecord>? configure = null)
    {
        var host = new HostRecord
        {
            Id = Guid.NewGuid(),
            Label = label,
            Address = address,
            Port = port,
            CredentialId = credential?.Id,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        if (configure is not null) host = configure(host);
        await Get<IHostEditor>().SaveHostAsync(host);
        return host;
    }

    public Task<HostRecord> AddHostAsync(E2EServer server, CredentialRecord? credential, Func<HostRecord, HostRecord>? configure = null) =>
        AddHostAsync("e2e " + server.Host, server.Host, server.Port, credential, configure);

    /// <summary>Connects exactly as AppRoot.ConnectHostAsync does: vault credentials first, then the user.</summary>
    public async Task<IHostConnection> ConnectAsync(HostRecord host, CancellationToken cancellationToken = default)
    {
        using var credentials = await Get<ICredentialResolver>().ResolveAsync(host, cancellationToken);
        var displayHost = !string.IsNullOrWhiteSpace(credentials.Username)
            ? host with { Username = credentials.Username }
            : host;
        var prompts = new CredentialSshPrompts(Get<ISshPrompts>(), credentials);
        return await Get<IConnectionEngine>().ConnectAsync(displayHost, credentials, prompts, cancellationToken);
    }

    public async Task<ISshConnection> ConnectSshAsync(HostRecord host, CancellationToken cancellationToken = default) =>
        await ConnectAsync(host, cancellationToken) as ISshConnection
        ?? throw new InvalidOperationException($"{host.Protocol} connection is not an SSH connection.");

    public async ValueTask DisposeAsync()
    {
        _prompts.Changed -= OnPromptsChanged;
        await _services.DisposeAsync();
        try { await _platform.CleanUpAsync(_instanceId); }
        catch (Exception) { /* best effort: never mask the scenario's own result */ }
    }
}
