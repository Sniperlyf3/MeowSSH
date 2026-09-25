using System.Net;
using System.Text;
using System.Text.Json;
using MeowSSH.Core.Licensing;
using MeowSSH.Core.Model;
using MeowSSH.Core.Services;
using MeowSSH.Core.Tests.Fakes;

namespace MeowSSH.Core.Tests.Services;

public sealed class TeamServiceTests
{
    private static readonly LicensingApiOptions Options = new(
        new Uri("https://api.meowssh.test/"), "cHVibGljLWtleQ==", "dev.sniperlyf3.meowssh");

    private static readonly TeamInfo SomeTeam = new("t1", "Ops", "m1", TeamRoles.Owner, [], [], []);

    /// <summary>Any paid tier gets a grant, as the real provider does; the server decides what it is good for.</summary>
    private sealed class PaidGrants(FakeTier tier) : ICloudEntitlementGrantSource
    {
        public Task<SignedEntitlementGrant> GetPaidGrantAsync(CancellationToken cancellationToken = default) =>
            tier.Tier >= EntitlementTier.Pro
                ? Task.FromResult(new SignedEntitlementGrant("cA==", "cw=="))
                : throw new InvalidOperationException("No MeowSSH purchase was found on this Google account.");
    }

    private sealed class FakeApi : ITeamApi
    {
        public List<string> Calls { get; } = [];
        public string? LastSecret { get; private set; }
        public (string Label, string Host, int Port, string Username)? LastShare { get; private set; }

        private T Record<T>(string call, string secret, T result)
        {
            Calls.Add(call);
            LastSecret = secret;
            return result;
        }

        public Task<TeamInfo?> GetMineAsync(string secret, CancellationToken cancellationToken = default) => Task.FromResult<TeamInfo?>(Record("get", secret, SomeTeam));
        public Task<TeamInfo> CreateAsync(string secret, SignedEntitlementGrant grant, string name, string displayName, CancellationToken cancellationToken = default) => Task.FromResult(Record("create", secret, SomeTeam));
        public Task DeleteAsync(string secret, CancellationToken cancellationToken = default) => Task.FromResult(Record("delete", secret, 0));
        public Task<TeamInfo> JoinAsync(string secret, SignedEntitlementGrant grant, string code, string displayName, CancellationToken cancellationToken = default) => Task.FromResult(Record("join", secret, SomeTeam with { YourRole = TeamRoles.Member }));
        public Task LeaveAsync(string secret, CancellationToken cancellationToken = default) => Task.FromResult(Record("leave", secret, 0));
        public Task<CreatedTeamInvite> CreateInviteAsync(string secret, SignedEntitlementGrant grant, CancellationToken cancellationToken = default) =>
            Task.FromResult(Record("invite", secret, new CreatedTeamInvite(new TeamInviteInfo("i1", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(7)), "AAAAA-BBBBB-CCCCC-DDDDD")));
        public Task RevokeInviteAsync(string secret, string inviteId, CancellationToken cancellationToken = default) => Task.FromResult(Record("revoke", secret, 0));
        public Task RemoveMemberAsync(string secret, string memberId, CancellationToken cancellationToken = default) => Task.FromResult(Record("remove", secret, 0));
        public Task<TeamSharedHost> ShareHostAsync(string secret, SignedEntitlementGrant grant, string label, string host, int port, string username, CancellationToken cancellationToken = default)
        {
            LastShare = (label, host, port, username);
            return Task.FromResult(Record("share", secret, new TeamSharedHost("h1", label, host, port, username, DateTimeOffset.UtcNow)));
        }
        public Task UnshareHostAsync(string secret, string hostId, CancellationToken cancellationToken = default) => Task.FromResult(Record("unshare", secret, 0));
        public Task<IReadOnlyList<TeamAuditEntry>> AuditAsync(string secret, CancellationToken cancellationToken = default) => Task.FromResult(Record<IReadOnlyList<TeamAuditEntry>>("audit", secret, []));
    }

    private sealed record Rig(TeamService Service, FakeApi Api, FakeTier Tier, MemoryTeamMemberSecretStore Secrets);

    private static Rig Create(EntitlementTier tier = EntitlementTier.Team)
    {
        var api = new FakeApi();
        var entitlement = new FakeTier(tier);
        var secrets = new MemoryTeamMemberSecretStore();
        return new Rig(new TeamService(api, new PaidGrants(entitlement), entitlement, secrets), api, entitlement, secrets);
    }

    private static HostRecord Host(string address = "db.internal", SshTransport transport = SshTransport.Tcp, HostProtocol protocol = HostProtocol.Ssh) => new()
    {
        Id = Guid.NewGuid(),
        Label = "Prod DB",
        Address = address,
        Port = 2222,
        Username = "deploy",
        Transport = transport,
        Protocol = protocol,
        CredentialId = Guid.NewGuid(),
        ProxyUrl = "socks5://user:secret@proxy:1080",
    };

    [Fact]
    public async Task APhoneThatNeverUsedTeamsNeverContactsTheServer()
    {
        var rig = Create();

        Assert.Null(await rig.Service.GetMineAsync());

        Assert.Empty(rig.Api.Calls);
        Assert.Null(await rig.Secrets.LoadAsync());
    }

    [Fact]
    public async Task CreatingMintsOneIdentityAndKeepsUsingIt()
    {
        var rig = Create();

        await rig.Service.CreateAsync(" Ops ", " Sam ");
        var secret = rig.Api.LastSecret;
        await rig.Service.GetMineAsync();

        Assert.Equal(43, secret!.Length);
        Assert.Equal(secret, rig.Api.LastSecret);
        Assert.Equal(secret, await rig.Secrets.LoadAsync());
    }

    [Theory]
    [InlineData(EntitlementTier.Pro)]
    [InlineData(EntitlementTier.ProCloud)]
    public async Task OwningATeamNeedsTeam(EntitlementTier tier)
    {
        var rig = Create(tier);

        Assert.False(rig.Service.CanOwn);
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Service.CreateAsync("Ops", "Sam"));
        Assert.Empty(rig.Api.Calls);
    }

    [Fact]
    public async Task JoiningNeedsProButNotTeam()
    {
        var pro = Create(EntitlementTier.Pro);
        var joined = await pro.Service.JoinAsync(" aaaaa-bbbbb-ccccc-ddddd ", "Alice");
        Assert.Equal(TeamRoles.Member, joined.YourRole);

        var free = Create(EntitlementTier.Free);
        Assert.False(free.Service.CanJoin);
        await Assert.ThrowsAsync<InvalidOperationException>(() => free.Service.JoinAsync("AAAAA-BBBBB-CCCCC-DDDDD", "Bob"));
        Assert.Empty(free.Api.Calls);
    }

    [Fact]
    public async Task SharingSendsTheAddressAndNothingElse()
    {
        var rig = Create();
        await rig.Service.CreateAsync("Ops", "Sam");

        await rig.Service.ShareHostAsync(Host());

        Assert.Equal(("Prod DB", "db.internal", 2222, "deploy"), rig.Api.LastShare);
    }

    [Theory]
    [InlineData(SshTransport.Tailcat, HostProtocol.Ssh)]
    [InlineData(SshTransport.Tcp, HostProtocol.Serial)]
    [InlineData(SshTransport.Tcp, HostProtocol.Local)]
    public async Task ATailcatAddressOrANonSshHostIsNeverShared(SshTransport transport, HostProtocol protocol)
    {
        var rig = Create();
        await rig.Service.CreateAsync("Ops", "Sam");
        var host = Host("100.64.0.1", transport, protocol);

        Assert.False(TeamHosts.CanShare(host));
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Service.ShareHostAsync(host));
        Assert.DoesNotContain("share", rig.Api.Calls);
    }

    [Fact]
    public async Task ALapsedOwnerCanStillTidyUpButNotAdd()
    {
        var rig = Create();
        await rig.Service.CreateAsync("Ops", "Sam");
        rig.Tier.Tier = EntitlementTier.Pro;

        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Service.CreateInviteAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Service.ShareHostAsync(Host()));
        await rig.Service.RevokeInviteAsync("i1");
        await rig.Service.RemoveMemberAsync("m2");
        await rig.Service.UnshareHostAsync("h1");
        await rig.Service.AuditAsync();
        await rig.Service.DeleteAsync();

        Assert.Equal(["create", "revoke", "remove", "unshare", "audit", "delete"], rig.Api.Calls);
    }

    [Fact]
    public async Task AMemberWhoseProLapsedCanStillLeave()
    {
        var rig = Create(EntitlementTier.Pro);
        await rig.Service.JoinAsync("AAAAA-BBBBB-CCCCC-DDDDD", "Alice");
        rig.Tier.Tier = EntitlementTier.Free;

        await rig.Service.LeaveAsync();

        Assert.Equal(["join", "leave"], rig.Api.Calls);
    }

    [Fact]
    public void ASharedHostBecomesAVaultHostWithNoCredential()
    {
        var shared = new TeamSharedHost("h1", "Prod DB", "db.internal", 2222, "", DateTimeOffset.UtcNow);

        var host = TeamHosts.ToVaultHost(shared, "Ops");

        Assert.Equal(("Prod DB", "db.internal", 2222, "Ops"), (host.Label, host.Address, host.Port, host.Group));
        Assert.Null(host.Username);
        Assert.Null(host.CredentialId);
        Assert.Equal((HostProtocol.Ssh, SshTransport.Tcp), (host.Protocol, host.Transport));
        Assert.True(TeamHosts.Matches(Host() with { Address = "DB.internal" }, shared));
        Assert.False(TeamHosts.Matches(Host() with { Port = 22 }, shared));
    }

    [Fact]
    public async Task TheWireShapeIsWhatMeowSshApiExpects()
    {
        var handler = new RecordingHandler(_ => Json(HttpStatusCode.OK, """
            {"id":"h1","label":"Prod DB","host":"db.internal","port":2222,"username":"deploy","sharedAtUtc":"2026-09-25T12:00:00Z"}
            """));
        var api = new HttpTeamApi(new HttpClient(handler), Options);

        var shared = await api.ShareHostAsync("member-secret", new SignedEntitlementGrant("cA==", "cw=="), "Prod DB", "db.internal", 2222, "deploy");

        var sent = Assert.Single(handler.Requests);
        Assert.Equal("https://api.meowssh.test/v1/teams/mine/hosts", sent.Uri);
        Assert.Equal("Bearer member-secret", sent.Authorization);
        Assert.Equal("cA==.cw==", sent.Grant);
        using var body = JsonDocument.Parse(sent.Body);
        Assert.Equal(["label", "host", "port", "username"], body.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.Equal("h1", shared.Id);
    }

    [Fact]
    public async Task NotBeingInATeamIsAnAnswerNotAnError()
    {
        var api = new HttpTeamApi(new HttpClient(new RecordingHandler(_ =>
            Json(HttpStatusCode.NotFound, """{"error":"not_in_team","message":"You are not in a team."}"""))), Options);

        Assert.Null(await api.GetMineAsync("s"));
    }

    [Theory]
    [InlineData(HttpStatusCode.Conflict, "team_full", "This team is full. Ask its owner to make room.", "This team is full. Ask its owner to make room.")]
    [InlineData(HttpStatusCode.Forbidden, "expired_grant", "The entitlement grant has expired.", "could not be verified right now")]
    public async Task ServerRefusalsReachTheUserAsSentences(HttpStatusCode status, string code, string serverMessage, string expected)
    {
        var api = new HttpTeamApi(new HttpClient(new RecordingHandler(_ =>
            Json(status, JsonSerializer.Serialize(new { error = code, message = serverMessage })))), Options);

        var error = await Assert.ThrowsAsync<TeamException>(() =>
            api.JoinAsync("s", new SignedEntitlementGrant("cA==", "cw=="), "AAAAA-BBBBB-CCCCC-DDDDD", "Alice"));

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
