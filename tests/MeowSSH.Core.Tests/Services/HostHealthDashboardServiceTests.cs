using MeowSSH.Core.Licensing;
using MeowSSH.Core.Model;
using MeowSSH.Core.Security;
using MeowSSH.Core.Services;
using MeowSSH.Core.Ssh;

namespace MeowSSH.Core.Tests.Services;

public sealed class HostHealthDashboardServiceTests
{
    [Fact]
    public async Task FreeTierFailsBeforeCredentialsOrNetwork()
    {
        var host = Host("one");
        var credentials = new FakeCredentials();
        var engine = new FakeEngine(_ => HealthyResult());
        var service = Service([host], credentials, engine, pro: false);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.CheckAllAsync());

        Assert.Contains("Pro", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, credentials.ResolveCount);
        Assert.Equal(0, engine.ConnectionCount);
    }

    [Fact]
    public async Task NonSshHostsAreReportedUnsupportedWithoutCredentialsOrNetwork()
    {
        var host = Host("serial") with { Protocol = HostProtocol.Serial };
        var credentials = new FakeCredentials();
        var engine = new FakeEngine(_ => HealthyResult());
        var service = Service([host], credentials, engine, pro: true);

        var result = Assert.Single(await service.CheckAllAsync());

        Assert.Equal(HostHealthState.Unsupported, result.State);
        Assert.Equal(0, credentials.ResolveCount);
        Assert.Equal(0, engine.ConnectionCount);
    }

    [Fact]
    public async Task HealthyProbeReturnsParsedMetrics()
    {
        var host = Host("server");
        var credentials = new FakeCredentials();
        var engine = new FakeEngine(_ => HealthyResult());
        var service = Service([host], credentials, engine, pro: true);

        var result = await service.CheckAsync(host.Id);

        Assert.Equal(HostHealthState.Healthy, result.State);
        Assert.Equal("Linux", result.OperatingSystem);
        Assert.Equal("up 2 hours", result.Uptime);
        Assert.Equal("0.10 0.20 0.30", result.Load);
        Assert.Equal("42%", result.Disk);
        Assert.Equal("37%", result.Memory);
        Assert.Contains("Connected in", result.Message, StringComparison.Ordinal);
        Assert.Equal(1, credentials.ResolveCount);
        Assert.Equal(1, engine.ConnectionCount);
        Assert.Contains("meowssh_uptime=", engine.LastCommand, StringComparison.Ordinal);
        Assert.Equal(TimeSpan.FromSeconds(10), engine.LastTimeout);
    }

    [Fact]
    public void ParserIgnoresUnrelatedLinesAndBoundsMetricValues()
    {
        var longValue = new string('x', 800);
        var metrics = HostHealthDashboardService.ParseMetrics(
            $"noise\r\nmeowssh_os=Linux\r\nother=value\r\nmeowssh_uptime={longValue}\r\n");

        Assert.Equal("Linux", metrics["meowssh_os"]);
        Assert.Equal(512, metrics["meowssh_uptime"].Length);
        Assert.False(metrics.ContainsKey("other"));
    }

    private static SshCommandResult HealthyResult() => new(
        0,
        "meowssh_os=Linux\n" +
        "meowssh_uptime=up 2 hours\n" +
        "meowssh_load=0.10 0.20 0.30\n" +
        "meowssh_disk=42%\n" +
        "meowssh_memory=37%\n",
        string.Empty);

    private static HostHealthDashboardService Service(
        IReadOnlyList<HostRecord> hosts,
        FakeCredentials credentials,
        FakeEngine engine,
        bool pro) =>
        new(
            new FakeDirectory(hosts),
            credentials,
            engine,
            new FakePrompts(),
            new FakeEntitlements(pro));

    private static HostRecord Host(string label) => new()
    {
        Id = Guid.NewGuid(),
        Label = label,
        Address = $"{label}.example.test",
        Protocol = HostProtocol.Ssh,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

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
        public int ResolveCount { get; private set; }

        public ValueTask<SshCredentials> ResolveAsync(HostRecord host, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ResolveCount++;
            return ValueTask.FromResult(new SshCredentials());
        }

        public ValueTask<SecretBuffer?> ResolvePasswordAsync(HostRecord host, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<SecretBuffer?>(null);
    }

    private sealed class FakeEntitlements(bool pro) : IEntitlementService
    {
        public EntitlementSnapshot Current { get; } = pro
            ? new EntitlementSnapshot(EntitlementTier.Pro, EntitlementSource.Promotional, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1))
            : EntitlementSnapshot.Free(DateTimeOffset.UtcNow);

        public event EventHandler? Changed
        {
            add { }
            remove { }
        }

        public bool Has(PremiumFeature feature) => pro && feature == PremiumFeature.HostHealthDashboard;
        public Task<EntitlementSnapshot> RefreshAsync(CancellationToken cancellationToken = default) => Task.FromResult(Current);
        public Task<EntitlementSnapshot> RestorePurchasesAsync(CancellationToken cancellationToken = default) => Task.FromResult(Current);
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
        public string? LastCommand { get; set; }
        public TimeSpan? LastTimeout { get; set; }

        public Task<ISshConnection> ConnectAsync(
            HostRecord host,
            SshCredentials credentials,
            ISshPrompts prompts,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ConnectionCount++;
            return Task.FromResult<ISshConnection>(new FakeConnection(host.Id, this, execute));
        }
    }

    private sealed class FakeConnection(Guid hostId, FakeEngine owner, Func<string, SshCommandResult> execute) : ISshConnection
    {
        public Guid HostId { get; } = hostId;
        public bool IsConnected { get; private set; } = true;
        public event EventHandler<SshConnectionLost>? ConnectionLost
        {
            add { }
            remove { }
        }

        public Task<SshCommandResult> RunCommandAsync(string command, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            owner.LastCommand = command;
            owner.LastTimeout = timeout;
            return Task.FromResult(execute(command));
        }

        public Task<ISshShell> OpenShellAsync(int columns, int rows, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ISftpSession> OpenSftpAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ISshForward> OpenLocalForwardAsync(string listenAddress, string remoteAddress, bool allowNonLoopbackBind = false, int maxConnections = 256, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ISshForward> OpenLocalForwardOnUnixSocketAsync(string socketPath, string remoteAddress, int maxConnections = 256, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ISshForward> OpenRemoteForwardAsync(string listenAddress, string localAddress, int maxConnections = 256, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ISshForward> OpenSocksForwardAsync(string listenAddress, bool requireAuth = true, string? socksUsername = null, string? socksPassword = null, bool allowNonLoopbackBind = false, int maxConnections = 256, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ISshForward> OpenSocksForwardOnUnixSocketAsync(string socketPath, bool requireAuth = false, string? socksUsername = null, string? socksPassword = null, int maxConnections = 256, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask DisposeAsync()
        {
            IsConnected = false;
            return ValueTask.CompletedTask;
        }
    }
}
