using System.Text;
using MeowSSH.Core.Licensing;
using MeowSSH.Core.Model;
using MeowSSH.Core.Services;

namespace MeowSSH.Core.Tests.Services;

public sealed class SessionLogServiceTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("meowssh-session-logs-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public async Task FreeTierCannotEnableOrStartCapture()
    {
        var entitlements = new MutableEntitlements(pro: false);
        var service = new FileSessionLogService(_directory, entitlements);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SetAutoRecordAsync(true));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync(Host("free")));
        Assert.False(service.AutoRecord);
    }

    [Fact]
    public async Task CompletedLogRemainsReadableAfterEntitlementExpires()
    {
        var entitlements = new MutableEntitlements(pro: true);
        var service = new FileSessionLogService(_directory, entitlements);
        await service.SetAutoRecordAsync(true);

        var host = Host("prod");
        await using (var capture = await service.StartAsync(host))
        {
            Assert.True(capture.TryAppend(Encoding.UTF8.GetBytes("hello\n\u001b[31mred\u001b[0m\n")));
            await capture.CompleteAsync(exitCode: 7, endReason: "Remote shell exited.");
        }

        entitlements.SetPro(false);
        Assert.True(service.AutoRecord);
        await service.SetAutoRecordAsync(false);

        var log = Assert.Single(await service.GetAllAsync());
        Assert.Equal(host.Id, log.HostId);
        Assert.Equal(7, log.ExitCode);
        Assert.Equal("Remote shell exited.", log.EndReason);
        Assert.NotNull(log.EndedAtUtc);

        var transcript = await service.ReadAsync(log.Id);
        Assert.Contains("hello", transcript, StringComparison.Ordinal);
        Assert.Contains("red", transcript, StringComparison.Ordinal);
        Assert.DoesNotContain("\u001b", transcript, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SearchMatchesHostMetadataAndTranscriptContent()
    {
        var service = new FileSessionLogService(_directory, new MutableEntitlements(pro: true));

        await using (var one = await service.StartAsync(Host("database")))
        {
            one.TryAppend(Encoding.UTF8.GetBytes("postgres ready\n"));
            await one.CompleteAsync(exitCode: 0);
        }

        await using (var two = await service.StartAsync(Host("web")))
        {
            two.TryAppend(Encoding.UTF8.GetBytes("nginx healthy\n"));
            await two.CompleteAsync(exitCode: 0);
        }

        Assert.Single(await service.SearchAsync("database"));
        var contentMatch = Assert.Single(await service.SearchAsync("nginx"));
        Assert.Equal("web", contentMatch.HostLabel);
    }

    [Fact]
    public async Task ClearKeepsAnActiveCaptureButDeletesCompletedLogs()
    {
        var service = new FileSessionLogService(_directory, new MutableEntitlements(pro: true));
        await using var active = await service.StartAsync(Host("active"));
        await using (var completed = await service.StartAsync(Host("completed")))
        {
            completed.TryAppend(Encoding.UTF8.GetBytes("done"));
            await completed.CompleteAsync();
        }

        await service.ClearAsync();

        var remaining = Assert.Single(await service.GetAllAsync());
        Assert.Equal("active", remaining.HostLabel);
        Assert.Null(remaining.EndedAtUtc);
    }

    private static HostRecord Host(string label) => new()
    {
        Id = Guid.NewGuid(),
        Label = label,
        Address = $"{label}.example.test",
        Protocol = HostProtocol.Ssh,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    private sealed class MutableEntitlements(bool pro) : IEntitlementService
    {
        private bool _pro = pro;

        public EntitlementSnapshot Current => _pro
            ? new EntitlementSnapshot(
                EntitlementTier.Pro,
                EntitlementSource.Promotional,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow.AddDays(1))
            : EntitlementSnapshot.Free(DateTimeOffset.UtcNow);

        public event EventHandler? Changed;

        public bool Has(PremiumFeature feature) => _pro;

        public void SetPro(bool value)
        {
            _pro = value;
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public Task<EntitlementSnapshot> RefreshAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Current);

        public Task<EntitlementSnapshot> RestorePurchasesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Current);
    }
}
