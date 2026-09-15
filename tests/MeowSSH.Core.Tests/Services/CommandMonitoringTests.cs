using MeowSSH.Core.Licensing;
using MeowSSH.Core.Model;
using MeowSSH.Core.Security;
using MeowSSH.Core.Services;
using MeowSSH.Core.Ssh;

namespace MeowSSH.Core.Tests.Services;

public sealed class CommandMonitoringTests
{
    [Fact]
    public async Task FreeTierCannotSaveOrRunMonitor()
    {
        var host = Host();
        var store = new MemoryCommandMonitorStore();
        var engine = new FakeEngine(_ => new SshCommandResult(0, "ok", string.Empty));
        var service = Service(store, host, engine, new RecordingAlerts(), pro: false);
        var monitor = Monitor(host.Id);

        var saveError = await Assert.ThrowsAsync<InvalidOperationException>(() => service.SaveAsync(monitor));
        Assert.Contains("Pro", saveError.Message, StringComparison.Ordinal);

        await store.SaveAsync(monitor);
        var runError = await Assert.ThrowsAsync<InvalidOperationException>(() => service.RunNowAsync(monitor.Id));
        Assert.Contains("Pro", runError.Message, StringComparison.Ordinal);
        Assert.Equal(0, engine.ConnectionCount);
    }

    [Fact]
    public async Task FirstHealthyRunEstablishesBaselineAndSecondChangeAlerts()
    {
        var host = Host();
        var store = new MemoryCommandMonitorStore();
        var outputs = new Queue<string>(["up", "up", "down"]);
        var engine = new FakeEngine(_ => new SshCommandResult(0, outputs.Dequeue(), string.Empty));
        var alerts = new RecordingAlerts();
        var service = Service(store, host, engine, alerts, pro: true);
        var monitor = Monitor(host.Id);
        await service.SaveAsync(monitor);

        var first = await service.RunNowAsync(monitor.Id);
        var second = await service.RunNowAsync(monitor.Id);
        var third = await service.RunNowAsync(monitor.Id);

        Assert.Null(first.AlertKind);
        Assert.False(first.OutputChanged);
        Assert.Null(second.AlertKind);
        Assert.False(second.OutputChanged);
        Assert.Equal(CommandMonitorAlertKind.OutputChanged, third.AlertKind);
        Assert.True(third.OutputChanged);
        var alert = Assert.Single(alerts.Items);
        Assert.Equal(CommandMonitorAlertKind.OutputChanged, alert.Kind);
        Assert.Equal(host.Label, alert.HostLabel);
    }

    [Fact]
    public async Task FailureAlertsOnceAndRecoveryAlertsOnce()
    {
        var host = Host();
        var store = new MemoryCommandMonitorStore();
        var results = new Queue<SshCommandResult>(
        [
            new SshCommandResult(1, string.Empty, "bad"),
            new SshCommandResult(1, string.Empty, "still bad"),
            new SshCommandResult(0, "healthy", string.Empty),
        ]);
        var engine = new FakeEngine(_ => results.Dequeue());
        var alerts = new RecordingAlerts();
        var service = Service(store, host, engine, alerts, pro: true);
        var monitor = Monitor(host.Id) with { NotifyOnChange = false };
        await service.SaveAsync(monitor);

        var first = await service.RunNowAsync(monitor.Id);
        var second = await service.RunNowAsync(monitor.Id);
        var third = await service.RunNowAsync(monitor.Id);

        Assert.Equal(CommandMonitorAlertKind.Failed, first.AlertKind);
        Assert.Null(second.AlertKind);
        Assert.Equal(CommandMonitorAlertKind.Recovered, third.AlertKind);
        Assert.Collection(alerts.Items,
            alert => Assert.Equal(CommandMonitorAlertKind.Failed, alert.Kind),
            alert => Assert.Equal(CommandMonitorAlertKind.Recovered, alert.Kind));
    }

    [Fact]
    public async Task DueRunnerHonorsIntervalAndEnabledFlag()
    {
        var host = Host();
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-09-15T12:00:00Z"));
        var store = new MemoryCommandMonitorStore();
        var engine = new FakeEngine(_ => new SshCommandResult(0, "ok", string.Empty));
        var service = Service(store, host, engine, new RecordingAlerts(), pro: true, clock);
        var enabled = Monitor(host.Id) with { IntervalSeconds = 300 };
        var disabled = Monitor(host.Id) with { Id = Guid.NewGuid(), Name = "Disabled", Enabled = false };
        await service.SaveAsync(enabled);
        await service.SaveAsync(disabled);

        Assert.Equal(1, await service.RunDueAsync());
        Assert.Equal(1, engine.ConnectionCount);
        Assert.Equal(0, await service.RunDueAsync());

        clock.Advance(TimeSpan.FromMinutes(4));
        Assert.Equal(0, await service.RunDueAsync());
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(1, await service.RunDueAsync());
        Assert.Equal(2, engine.ConnectionCount);
    }

    [Fact]
    public async Task ExistingMonitorCanBeDisabledAndDeletedAfterEntitlementExpires()
    {
        var host = Host();
        var store = new MemoryCommandMonitorStore();
        var entitlement = new MutableEntitlements(pro: true);
        var service = new CommandMonitoringService(
            store,
            new FakeDirectory([host]),
            new FakeCredentials(),
            new FakeEngine(_ => new SshCommandResult(0, "ok", string.Empty)),
            new FakePrompts(),
            entitlement,
            new RecordingAlerts());
        var monitor = Monitor(host.Id);
        await service.SaveAsync(monitor);

        entitlement.Pro = false;
        await service.SetEnabledAsync(monitor.Id, enabled: false);
        Assert.False(Assert.Single(await service.GetAllAsync()).Enabled);
        await service.DeleteAsync(monitor.Id);
        Assert.Empty(await service.GetAllAsync());
    }

    private static CommandMonitoringService Service(
        ICommandMonitorStore store,
        HostRecord host,
        ISshEngine engine,
        ICommandMonitorAlertSink alerts,
        bool pro,
        TimeProvider? timeProvider = null) =>
        new(
            store,
            new FakeDirectory([host]),
            new FakeCredentials(),
            engine,
            new FakePrompts(),
            new MutableEntitlements(pro),
            alerts,
            timeProvider);

    private static CommandMonitor Monitor(Guid hostId) => new(
        Guid.NewGuid(),
        "Health check",
        hostId,
        "health-check",
        IntervalSeconds: 300,
        TimeoutSeconds: 20);

    private static HostRecord Host() => new()
    {
        Id = Guid.NewGuid(),
        Label = "prod-web-01",
        Address = "prod.example.test",
        Protocol = HostProtocol.Ssh,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    private sealed class RecordingAlerts : ICommandMonitorAlertSink
    {
        public List<CommandMonitorAlert> Items { get; } = [];

        public Task NotifyAsync(CommandMonitorAlert alert, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Items.Add(alert);
            return Task.CompletedTask;
        }
    }

    private sealed class MutableEntitlements(bool pro) : IEntitlementService
    {
        public bool Pro { get; set; } = pro;
        public EntitlementSnapshot Current => Pro
            ? new EntitlementSnapshot(
                EntitlementTier.Pro,
                EntitlementSource.Promotional,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow.AddDays(1))
            : EntitlementSnapshot.Free(DateTimeOffset.UtcNow);

        public event EventHandler? Changed
        {
            add { }
            remove { }
        }

        public bool Has(PremiumFeature feature) => Pro;
        public Task<EntitlementSnapshot> RefreshAsync(CancellationToken cancellationToken = default) => Task.FromResult(Current);
        public Task<EntitlementSnapshot> RestorePurchasesAsync(CancellationToken cancellationToken = default) => Task.FromResult(Current);
    }

    private sealed class FakeDirectory(IReadOnlyList<HostRecord> hosts) : IHostDirectory
    {
        public event EventHandler? Changed
        {
            add { }
            remove { }
        }

        public ValueTask<IReadOnlyList<HostStatus>> GetHostsAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IReadOnlyList<HostStatus>>([.. hosts.Select(static host => new HostStatus(host))]);
        }
    }

    private sealed class FakeCredentials : ICredentialResolver
    {
        public ValueTask<SshCredentials> ResolveAsync(HostRecord host, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new SshCredentials());
        }

        public ValueTask<SecretBuffer?> ResolvePasswordAsync(HostRecord host, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<SecretBuffer?>(null);
    }

    private sealed class FakePrompts : ISshPrompts
    {
        public Task<bool> ConfirmUnknownHostKeyAsync(HostKeyPrompt prompt, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<SecretBuffer?> RequestPasswordAsync(string prompt, CancellationToken cancellationToken = default) => Task.FromResult<SecretBuffer?>(null);
        public Task<SecretBuffer?> RequestKeyPassphraseAsync(CancellationToken cancellationToken = default) => Task.FromResult<SecretBuffer?>(null);
        public Task<IReadOnlyList<string>?> AnswerChallengeAsync(KeyboardInteractivePrompt prompt, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>?>([]);
    }

    private sealed class FakeEngine(Func<string, SshCommandResult> execute) : ISshEngine
    {
        public int ConnectionCount { get; private set; }

        public Task<ISshConnection> ConnectAsync(
            HostRecord host,
            SshCredentials credentials,
            ISshPrompts prompts,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ConnectionCount++;
            return Task.FromResult<ISshConnection>(new FakeConnection(host.Id, execute));
        }
    }

    private sealed class FakeConnection(Guid hostId, Func<string, SshCommandResult> execute) : ISshConnection
    {
        public Guid HostId { get; } = hostId;
        public bool IsConnected => true;
        public event EventHandler<SshConnectionLost>? ConnectionLost
        {
            add { }
            remove { }
        }

        public Task<SshCommandResult> RunCommandAsync(string command, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(execute(command));
        }

        public Task<ISshShell> OpenShellAsync(int columns, int rows, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ISftpSession> OpenSftpAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ISshForward> OpenLocalForwardAsync(string listenAddress, string remoteAddress, bool allowNonLoopbackBind = false, int maxConnections = 256, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ISshForward> OpenLocalForwardOnUnixSocketAsync(string socketPath, string remoteAddress, int maxConnections = 256, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ISshForward> OpenRemoteForwardAsync(string listenAddress, string localAddress, int maxConnections = 256, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ISshForward> OpenSocksForwardAsync(string listenAddress, bool requireAuth = true, string? socksUsername = null, string? socksPassword = null, bool allowNonLoopbackBind = false, int maxConnections = 256, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ISshForward> OpenSocksForwardOnUnixSocketAsync(string socketPath, bool requireAuth = false, string? socksUsername = null, string? socksPassword = null, int maxConnections = 256, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;
        public override DateTimeOffset GetUtcNow() => _utcNow;
        public void Advance(TimeSpan value) => _utcNow += value;
    }
}
