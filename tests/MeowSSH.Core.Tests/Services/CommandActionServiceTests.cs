using MeowSSH.Core.Licensing;
using MeowSSH.Core.Model;
using MeowSSH.Core.Security;
using MeowSSH.Core.Services;
using MeowSSH.Core.Ssh;

namespace MeowSSH.Core.Tests.Services;

public sealed class CommandActionServiceTests
{
    [Fact]
    public async Task SingleHostActionRunsOnFreeTier()
    {
        var host = Host("one");
        var store = new MemoryCommandActionStore();
        var action = new CommandAction(Guid.NewGuid(), "Uptime", "uptime", [host.Id], 30);
        await store.SaveAsync(action);
        var engine = new FakeEngine((_, command) => new SshCommandResult(0, $"{command}: ok", string.Empty));
        var service = Service(store, [host], engine, pro: false);

        var run = await service.RunAsync(action.Id);

        var result = Assert.Single(run.Hosts);
        Assert.Equal(CommandActionHostState.Succeeded, result.State);
        Assert.Equal("uptime: ok", result.StandardOutput);
        Assert.True(run.Succeeded);
        Assert.Equal(1, engine.ConnectionCount);
    }

    [Fact]
    public async Task MultiHostActionRequiresProBeforeConnecting()
    {
        var one = Host("one");
        var two = Host("two");
        var store = new MemoryCommandActionStore();
        var action = new CommandAction(Guid.NewGuid(), "Fleet", "hostname", [one.Id, two.Id], 30);
        await store.SaveAsync(action);
        var engine = new FakeEngine((_, _) => new SshCommandResult(0, "ok", string.Empty));
        var service = Service(store, [one, two], engine, pro: false);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.RunAsync(action.Id));

        Assert.Contains("Pro", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, engine.ConnectionCount);
    }

    [Fact]
    public async Task ProMultiHostActionReturnsResultsInSavedHostOrder()
    {
        var one = Host("one");
        var two = Host("two");
        var store = new MemoryCommandActionStore();
        var action = new CommandAction(Guid.NewGuid(), "Fleet", "hostname", [two.Id, one.Id], 30);
        await store.SaveAsync(action);
        var engine = new FakeEngine((host, _) => host.Label == "one"
            ? new SshCommandResult(3, string.Empty, "failed")
            : new SshCommandResult(0, "two-ok", string.Empty));
        var service = Service(store, [one, two], engine, pro: true);

        var run = await service.RunAsync(action.Id);

        Assert.Collection(run.Hosts,
            result =>
            {
                Assert.Equal("two", result.HostLabel);
                Assert.Equal(CommandActionHostState.Succeeded, result.State);
                Assert.Equal(0, result.ExitCode);
            },
            result =>
            {
                Assert.Equal("one", result.HostLabel);
                Assert.Equal(CommandActionHostState.Failed, result.State);
                Assert.Equal(3, result.ExitCode);
                Assert.Equal("failed", result.StandardError);
            });
        Assert.False(run.Succeeded);
        Assert.Equal(2, engine.ConnectionCount);
    }

    [Fact]
    public async Task MissingHostProducesPerHostFailureInsteadOfAbortingRun()
    {
        var existing = Host("one");
        var missing = Guid.NewGuid();
        var store = new MemoryCommandActionStore();
        var action = new CommandAction(Guid.NewGuid(), "Mixed", "true", [existing.Id, missing], 30);
        await store.SaveAsync(action);
        var engine = new FakeEngine((_, _) => new SshCommandResult(0, "ok", string.Empty));
        var service = Service(store, [existing], engine, pro: true);

        var run = await service.RunAsync(action.Id);

        Assert.Equal(2, run.Hosts.Count);
        Assert.Equal(CommandActionHostState.Succeeded, run.Hosts[0].State);
        Assert.Equal(CommandActionHostState.ConnectionFailed, run.Hosts[1].State);
        Assert.Contains("no longer exists", run.Hosts[1].Message, StringComparison.OrdinalIgnoreCase);
    }

    private static CommandActionService Service(
        ICommandActionStore store,
        IReadOnlyList<HostRecord> hosts,
        ISshEngine engine,
        bool pro) =>
        new(
            store,
            new FakeDirectory(hosts),
            new FakeCredentials(),
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
        public ValueTask<SshCredentials> ResolveAsync(HostRecord host, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new SshCredentials());
        }

        public ValueTask<SecretBuffer?> ResolvePasswordAsync(HostRecord host, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<SecretBuffer?>(null);
    }

    private sealed class FakeEntitlements(bool pro) : IEntitlementService
    {
        public EntitlementSnapshot Current { get; } = pro
            ? new EntitlementSnapshot(EntitlementTier.Pro, EntitlementSource.Promotional, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1))
            : EntitlementSnapshot.Free;

        public event EventHandler? Changed
        {
            add { }
            remove { }
        }

        public bool Has(PremiumFeature feature) => pro;
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

    private sealed class FakeEngine(Func<HostRecord, string, SshCommandResult> execute) : ISshEngine
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
            return Task.FromResult<ISshConnection>(new FakeConnection(host, execute));
        }
    }

    private sealed class FakeConnection(HostRecord host, Func<HostRecord, string, SshCommandResult> execute) : ISshConnection
    {
        public Guid HostId => host.Id;
        public bool IsConnected { get; private set; } = true;
        public event EventHandler<SshConnectionLost>? ConnectionLost
        {
            add { }
            remove { }
        }

        public Task<SshCommandResult> RunCommandAsync(string command, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(execute(host, command));
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
