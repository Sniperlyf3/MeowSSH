using System.Net;
using System.Text;
using System.Text.Json;
using MeowSSH.Core.Licensing;
using MeowSSH.Core.Services;
using MeowSSH.Core.Tests.Fakes;

namespace MeowSSH.Core.Tests.Services;

public sealed class HeartbeatMonitorServiceTests
{
    private static readonly LicensingApiOptions Options = new(
        new Uri("https://api.meowssh.test/"), "cHVibGljLWtleQ==", "dev.sniperlyf3.meowssh");

    private sealed class FakeApi : IHeartbeatMonitorApi
    {
        public List<HeartbeatMonitorInfo> Monitors { get; } = [];
        public int ListCalls { get; private set; }
        public string? LastSecret { get; private set; }

        public Task<CreatedHeartbeatMonitor> CreateAsync(string ownerSecret, SignedEntitlementGrant grant, string name, int periodSeconds, int graceSeconds, CancellationToken cancellationToken = default)
        {
            LastSecret = ownerSecret;
            var monitor = new HeartbeatMonitorInfo($"m{Monitors.Count + 1}", name, periodSeconds, graceSeconds, DateTimeOffset.UtcNow, null, null, HeartbeatStates.New, null);
            Monitors.Add(monitor);
            return Task.FromResult(new CreatedHeartbeatMonitor(monitor, "tok_" + monitor.Id));
        }

        public Task<IReadOnlyList<HeartbeatMonitorInfo>> ListAsync(string ownerSecret, CancellationToken cancellationToken = default)
        {
            ListCalls++;
            LastSecret = ownerSecret;
            return Task.FromResult<IReadOnlyList<HeartbeatMonitorInfo>>([.. Monitors]);
        }

        public Task DeleteAsync(string ownerSecret, string id, CancellationToken cancellationToken = default)
        {
            Monitors.RemoveAll(m => m.Id == id);
            return Task.CompletedTask;
        }

        public void SetState(string id, string state) =>
            Monitors[Monitors.FindIndex(m => m.Id == id)] = Monitors.Single(m => m.Id == id) with { State = state, LastPingAtUtc = DateTimeOffset.UtcNow.AddHours(-2) };
    }

    private sealed class RecordingSink : IHeartbeatAlertSink
    {
        public List<HeartbeatAlert> Alerts { get; } = [];
        public Task NotifyAsync(HeartbeatAlert alert, CancellationToken cancellationToken = default)
        {
            Alerts.Add(alert);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingScheduler : IHeartbeatCheckScheduler
    {
        public bool? Scheduled { get; private set; }
        public void Update(bool anyMonitors) => Scheduled = anyMonitors;
    }

    private sealed record Rig(HeartbeatMonitorService Service, FakeApi Api, RecordingSink Sink, RecordingScheduler Scheduler, FakeTier Tier, MemoryMonitorOwnerSecretStore Owners);

    private static Rig Create(EntitlementTier tier = EntitlementTier.ProCloud)
    {
        var api = new FakeApi();
        var sink = new RecordingSink();
        var scheduler = new RecordingScheduler();
        var entitlement = new FakeTier(tier);
        var owners = new MemoryMonitorOwnerSecretStore();
        var service = new HeartbeatMonitorService(api, new FakeCloudGrants(tier), entitlement, owners, new MemoryHeartbeatLocalStore(), sink, scheduler, Options);
        return new Rig(service, api, sink, scheduler, entitlement, owners);
    }

    [Fact]
    public async Task APhoneThatNeverUsedTheFeatureNeverContactsTheServer()
    {
        var rig = Create();

        Assert.Empty(await rig.Service.ListAsync());
        Assert.Empty(await rig.Service.CheckForAlertsAsync());

        Assert.Equal(0, rig.Api.ListCalls);
        Assert.Null(await rig.Owners.LoadAsync());
    }

    [Fact]
    public async Task CreatingMintsOneIdentityKeepsTheTokenAndSchedulesChecks()
    {
        var rig = Create();

        var first = await rig.Service.CreateAsync("nightly backup", TimeSpan.FromHours(24), TimeSpan.FromHours(1));
        var secret = rig.Api.LastSecret;
        await rig.Service.CreateAsync("cert renew", TimeSpan.FromDays(1), TimeSpan.FromHours(1));

        Assert.Equal(secret, rig.Api.LastSecret);
        Assert.Equal(43, secret!.Length);
        Assert.Equal(new Uri("https://api.meowssh.test/v1/ping/tok_m1"), await rig.Service.PingUrlAsync(first.Id));
        Assert.True(rig.Scheduler.Scheduled);
    }

    [Theory]
    [InlineData(EntitlementTier.Free)]
    [InlineData(EntitlementTier.Pro)]
    public async Task CreatingNeedsProCloud(EntitlementTier tier)
    {
        var rig = Create(tier);

        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Service.CreateAsync("x", TimeSpan.FromHours(1), TimeSpan.FromMinutes(10)));

        Assert.Empty(rig.Api.Monitors);
    }

    [Fact]
    public async Task AMonitorThatStaysDownAlertsOnceThenOnceMoreWhenItRecovers()
    {
        var rig = Create();
        var monitor = await rig.Service.CreateAsync("nightly backup", TimeSpan.FromHours(1), TimeSpan.FromMinutes(10));

        rig.Api.SetState(monitor.Id, HeartbeatStates.Down);
        await rig.Service.CheckForAlertsAsync();
        await rig.Service.CheckForAlertsAsync();
        await rig.Service.CheckForAlertsAsync();
        rig.Api.SetState(monitor.Id, HeartbeatStates.Up);
        await rig.Service.CheckForAlertsAsync();
        await rig.Service.CheckForAlertsAsync();

        Assert.Equal(["nightly backup stopped checking in", "nightly backup recovered"], rig.Sink.Alerts.Select(a => a.Title));
    }

    [Fact]
    public async Task DownThenLateThenDownIsOneOutageNotTwo()
    {
        var rig = Create();
        var monitor = await rig.Service.CreateAsync("job", TimeSpan.FromHours(1), TimeSpan.FromMinutes(10));

        rig.Api.SetState(monitor.Id, HeartbeatStates.Down);
        await rig.Service.CheckForAlertsAsync();
        rig.Api.SetState(monitor.Id, HeartbeatStates.Late);
        await rig.Service.CheckForAlertsAsync();
        rig.Api.SetState(monitor.Id, HeartbeatStates.Down);
        await rig.Service.CheckForAlertsAsync();

        Assert.Single(rig.Sink.Alerts);
    }

    [Fact]
    public async Task LateAndNewNeverPage()
    {
        var rig = Create();
        var monitor = await rig.Service.CreateAsync("job", TimeSpan.FromHours(1), TimeSpan.FromMinutes(10));

        await rig.Service.CheckForAlertsAsync();
        rig.Api.SetState(monitor.Id, HeartbeatStates.Late);
        await rig.Service.CheckForAlertsAsync();

        Assert.Empty(rig.Sink.Alerts);
    }

    [Fact]
    public async Task AFailureReportAlertsDistinctlyFromSilence()
    {
        var rig = Create();
        var monitor = await rig.Service.CreateAsync("backup", TimeSpan.FromHours(1), TimeSpan.FromMinutes(10));

        rig.Api.SetState(monitor.Id, HeartbeatStates.Down);
        await rig.Service.CheckForAlertsAsync();
        rig.Api.SetState(monitor.Id, HeartbeatStates.Failing);
        await rig.Service.CheckForAlertsAsync();

        Assert.Equal(["backup stopped checking in", "backup reported a failure"], rig.Sink.Alerts.Select(a => a.Title));
    }

    [Fact]
    public async Task ALapsedSubscriptionStopsBackgroundAlertsButKeepsMonitorsListed()
    {
        var rig = Create();
        var monitor = await rig.Service.CreateAsync("job", TimeSpan.FromHours(1), TimeSpan.FromMinutes(10));
        rig.Api.SetState(monitor.Id, HeartbeatStates.Down);
        rig.Tier.Tier = EntitlementTier.Pro;

        Assert.Empty(await rig.Service.CheckForAlertsAsync());
        Assert.Single(await rig.Service.ListAsync());
        Assert.Empty(rig.Sink.Alerts);
    }

    [Fact]
    public async Task DeletingForgetsTheTokenAndTheLastMonitorUnschedulesChecks()
    {
        var rig = Create();
        var monitor = await rig.Service.CreateAsync("job", TimeSpan.FromHours(1), TimeSpan.FromMinutes(10));

        await rig.Service.DeleteAsync(monitor.Id);
        await rig.Service.ListAsync();

        Assert.Null(await rig.Service.PingUrlAsync(monitor.Id));
        Assert.False(rig.Scheduler.Scheduled);
    }

    [Fact]
    public async Task TheWireShapeIsWhatMeowSshApiExpects()
    {
        var handler = new RecordingHandler(_ => Json(HttpStatusCode.OK, """
            {"monitor":{"id":"ab12","name":"job","periodSeconds":3600,"graceSeconds":600,"createdAtUtc":"2026-09-25T12:00:00Z","lastPingAtUtc":null,"lastFailAtUtc":null,"state":"new","dueAtUtc":null},"pingToken":"TOKEN"}
            """));
        var api = new HttpHeartbeatMonitorApi(new HttpClient(handler), Options);

        var created = await api.CreateAsync("owner-secret", new SignedEntitlementGrant("cA==", "cw=="), "job", 3600, 600);

        var sent = Assert.Single(handler.Requests);
        Assert.Equal("https://api.meowssh.test/v1/monitors", sent.Uri);
        Assert.Equal("Bearer owner-secret", sent.Authorization);
        Assert.Equal("cA==.cw==", sent.Grant);
        using var body = JsonDocument.Parse(sent.Body);
        Assert.Equal(3600, body.RootElement.GetProperty("periodSeconds").GetInt32());
        Assert.Equal("TOKEN", created.PingToken);
        Assert.Equal(HeartbeatStates.New, created.Monitor.State);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "pro_cloud_required", "requires MeowSSH Pro Cloud")]
    [InlineData(HttpStatusCode.Conflict, "monitor_limit", "monitor limit")]
    public async Task ServerCodesBecomeSentences(HttpStatusCode status, string code, string expected)
    {
        var api = new HttpHeartbeatMonitorApi(new HttpClient(new RecordingHandler(_ =>
            Json(status, $$"""{"error":"{{code}}","message":"x"}"""))), Options);

        var error = await Assert.ThrowsAsync<HeartbeatMonitorException>(() =>
            api.CreateAsync("s", new SignedEntitlementGrant("cA==", "cw=="), "job", 3600, 600));

        Assert.Equal(code, error.Code);
        Assert.Contains(expected, error.Message, StringComparison.Ordinal);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed record Sent(string Uri, string? Authorization, string? Grant, string Body);

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Sent> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new Sent(
                request.RequestUri!.ToString(),
                request.Headers.Authorization?.ToString(),
                request.Headers.TryGetValues("X-MeowSSH-Entitlement-Grant", out var grant) ? grant.Single() : null,
                request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken)));
            return respond(request);
        }
    }
}
