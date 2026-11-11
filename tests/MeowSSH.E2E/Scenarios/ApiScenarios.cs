using MeowSSH.Core.Licensing;
using MeowSSH.Core.Model;
using MeowSSH.Core.Security;
using MeowSSH.Core.Services;
using Xunit;

namespace MeowSSH.E2E.Scenarios;

/// <summary>
/// The app and MeowSSHAPI together: the app's own licensing, relay, backup,
/// sync, heartbeat and team clients against a real running API, with only
/// Google Play stood in (see <see cref="E2EApi"/>). Each scenario is what a
/// user does on the matching screen.
/// </summary>
public static class ApiScenarios
{
    public static IReadOnlyList<E2EScenario> All { get; } =
    [
        new("api.licensing.purchase-grants-tier", PurchasesGrantTheirTier),
        new("api.licensing.refused-when-google-does-not-vouch", UnvouchedPurchasesStayFree),
        new("api.managed-relay.register-and-usage", RelayIdentityIsRegisteredAndMetered),
        new("api.cloud-backup.new-phone-finds-and-restores", CloudBackupRestoresOnANewPhone),
        new("api.cloud-sync.two-phones", SyncCarriesAHostToTheOtherPhone),
        new("api.heartbeat.create-ping-delete", HeartbeatMonitorSeesPings),
        new("api.team.invite-join-share", TeamSharesAHostWithAJoinedMember),
        new("api.free-tier.refused-by-the-service", ServiceRefusesFreeUsers),
        new("api.hosted-ai.explain-and-command", AskAiExplainsOutputAndSuggestsACommandThatRuns),
        new("api.managed-relay.guest-on-another-account", AGuestOnAnotherAccountIsCarriedAndBilledToTheOwner),
    ];

    private static async Task PurchasesGrantTheirTier(E2EContext c)
    {
        c.RequireApi();
        foreach (var tier in new[] { EntitlementTier.Free, EntitlementTier.Pro, EntitlementTier.ProCloud, EntitlementTier.Team })
        {
            var app = await c.StartAppAsync(tier);
            var current = app.Get<IEntitlementService>().Current;
            Assert.Equal(tier, current.Tier);
        }

        // Buying later: Restore purchases picks it up without a reinstall.
        var upgrading = await c.StartAppAsync(EntitlementTier.Free);
        upgrading.Store.Buy(E2EPlayStore.ProCloud);
        var restored = await upgrading.Get<IEntitlementService>().RestorePurchasesAsync();
        Assert.Equal(EntitlementTier.ProCloud, restored.Tier);
        Assert.True(upgrading.Get<IEntitlementService>().Has(PremiumFeature.CloudBackup));
    }

    private static async Task UnvouchedPurchasesStayFree(E2EContext c)
    {
        c.RequireApi();
        // Refunded, and a subscription Google no longer vouches for: the API
        // grants nothing for either, however the device presents them.
        var refunded = new E2EPlayStore();
        refunded.Buy(E2EPlayStore.ProLifetime, voided: true);
        Assert.Equal(EntitlementTier.Free, (await c.StartAppAsync(store: refunded)).Get<IEntitlementService>().Current.Tier);

        var lapsed = new E2EPlayStore();
        lapsed.Buy(E2EPlayStore.Team, valid: false);
        Assert.Equal(EntitlementTier.Free, (await c.StartAppAsync(store: lapsed)).Get<IEntitlementService>().Current.Tier);

        // A sideloaded or re-signed build: Play does not license it, so even a
        // genuine purchase earns nothing.
        var app = await c.StartAppAsync(EntitlementTier.Free);
        app.Get<E2EPlayIntegrity>().AppLicensingVerdict = "UNLICENSED";
        app.Store.Buy(E2EPlayStore.ProLifetime);
        var refreshed = await app.Get<IEntitlementService>().RefreshAsync();
        Assert.Equal(EntitlementTier.Free, refreshed.Tier);
    }

    private static async Task RelayIdentityIsRegisteredAndMetered(E2EContext c)
    {
        c.RequireApi();
        foreach (var tier in new[] { EntitlementTier.Free, EntitlementTier.ProCloud })
        {
            var app = await c.StartAppAsync(tier);
            // What the Keys screen shows: also registers it with the relay.
            var node = await app.Get<ITailcatHubService>().GetClientPublicKeyAsync();
            var usage = await app.Get<IManagedDerpUsageService>().GetCurrentAsync(node);
            Assert.Equal(node, usage.ClientNodePublic);
            Assert.Equal(tier, usage.Tier);
            Assert.Equal(0UL, usage.UsedBytes);
            Assert.False(usage.IsExceeded);
            Assert.True(usage.AllowanceBytes > 0, "every tier has some relay allowance");
            Assert.True(usage.PeriodEndUtc > DateTimeOffset.UtcNow);
            c.Log($"{tier}: relay allowance {usage.AllowanceBytes:N0} bytes");
        }
    }

    private static async Task CloudBackupRestoresOnANewPhone(E2EContext c)
    {
        c.RequireApi();
        var (oldPhone, host) = await StartWithHostAsync(c, EntitlementTier.ProCloud);
        var recoveryCode = oldPhone.RecoveryCode!;
        var cloud = oldPhone.Get<ICloudVaultBackupService>();
        Assert.True(cloud.CanUpload);

        // A mistyped code is refused before anything is uploaded under it.
        await Assert.ThrowsAnyAsync<Exception>(() => cloud.EnableAsync(recoveryCode[..^1] + (recoveryCode[^1] == 'A' ? 'B' : 'A')));
        await cloud.EnableAsync(recoveryCode);
        var first = await cloud.BackupNowAsync();
        // Versions are content-addressed: back up a changed vault, as a user
        // would after editing, or the second upload is the same version.
        await oldPhone.AddHostAsync("added after the first backup", c.Server.Host, c.Server.Port, null);
        var second = await cloud.BackupNowAsync();
        Assert.Equal(
            new[] { first.Id, second.Id }.Order(),
            (await cloud.ListAsync()).Select(v => v.Id).Order());

        // The new phone has nothing but the recovery code, and no subscription:
        // finding and restoring are free, so a lapsed user is never locked out.
        var newPhone = await c.StartAppAsync(EntitlementTier.Free);
        var found = await newPhone.Get<ICloudVaultBackupService>().FindAsync(recoveryCode);
        Assert.Equal(2, found.Count);
        await newPhone.Get<ICloudVaultBackupService>().RestoreAsync(recoveryCode, found.MaxBy(v => v.CreatedAtUtc)!.Id);
        var restored = Assert.Single(await newPhone.Get<IHostDirectory>().GetHostsAsync(), h => h.Host.Id == host.Id).Host;
        await using (var ssh = await newPhone.ConnectSshAsync(restored))
            Assert.Equal(c.Server.Username, (await ssh.RunCommandAsync("whoami")).StandardOutput.Trim());

        // Delete everything: gone for both phones.
        await cloud.DeleteAllAsync();
        Assert.Empty(await newPhone.Get<ICloudVaultBackupService>().FindAsync(recoveryCode));
    }

    private static async Task SyncCarriesAHostToTheOtherPhone(E2EContext c)
    {
        c.RequireApi();
        var (phoneA, _) = await StartWithHostAsync(c, EntitlementTier.ProCloud);
        var recoveryCode = phoneA.RecoveryCode!;
        await phoneA.Get<ICloudVaultBackupService>().EnableAsync(recoveryCode);
        // Phone B joins the way a user would: restore A's cloud backup first.
        await phoneA.Get<ICloudVaultBackupService>().BackupNowAsync();
        var syncA = phoneA.Get<ICloudVaultSyncService>();
        await syncA.EnableAsync();
        await syncA.SyncNowAsync();

        // Phone B restores the same vault, then syncs too.
        var phoneB = await c.StartAppAsync(EntitlementTier.ProCloud);
        var backupB = phoneB.Get<ICloudVaultBackupService>();
        await backupB.RestoreAsync(recoveryCode, (await backupB.FindAsync(recoveryCode)).MaxBy(v => v.CreatedAtUtc)!.Id);
        var syncB = phoneB.Get<ICloudVaultSyncService>();
        await syncB.EnableAsync();
        await syncB.SyncNowAsync();

        // A host added on A reaches B on B's next sync, and B's edit comes back.
        var added = await phoneA.AddHostAsync("added on A", c.Server.Host, c.Server.Port, null);
        await syncA.SyncNowAsync();
        await syncB.SyncNowAsync();
        var onB = Assert.Single(await phoneB.Get<IHostDirectory>().GetHostsAsync(), h => h.Host.Id == added.Id).Host;
        Assert.Null(syncB.Status.LastError);

        await phoneB.Get<IHostEditor>().SaveHostAsync(onB with { Label = "renamed on B" });
        await syncB.SyncNowAsync();
        await syncA.SyncNowAsync();
        Assert.Equal("renamed on B", Assert.Single(await phoneA.Get<IHostDirectory>().GetHostsAsync(), h => h.Host.Id == added.Id).Host.Label);
    }

    private static async Task HeartbeatMonitorSeesPings(E2EContext c)
    {
        var api = c.RequireApi();
        var app = await c.StartAppAsync(EntitlementTier.ProCloud);
        var monitors = app.Get<IHeartbeatMonitorService>();
        Assert.True(monitors.CanUse);

        var created = await monitors.CreateAsync("nightly backup", TimeSpan.FromHours(1), TimeSpan.FromMinutes(10));
        var pingUrl = await monitors.PingUrlAsync(created.Id) ?? throw new InvalidOperationException("No ping URL for a monitor this phone created.");
        Assert.Null(Assert.Single(await monitors.ListAsync(), m => m.Id == created.Id).LastPingAtUtc);

        // What the user's cron job does with the URL the app shows.
        using (var http = api.CreateHttpClient())
            (await http.PostAsync(pingUrl, null)).EnsureSuccessStatusCode();
        var pinged = Assert.Single(await monitors.ListAsync(), m => m.Id == created.Id);
        Assert.NotNull(pinged.LastPingAtUtc);
        Assert.Empty(await monitors.CheckForAlertsAsync());

        await monitors.DeleteAsync(created.Id);
        Assert.DoesNotContain(await monitors.ListAsync(), m => m.Id == created.Id);
        using (var http = api.CreateHttpClient())
            Assert.False((await http.PostAsync(pingUrl, null)).IsSuccessStatusCode, "a deleted monitor still accepts pings");
    }

    private static async Task TeamSharesAHostWithAJoinedMember(E2EContext c)
    {
        c.RequireApi();
        var (owner, host) = await StartWithHostAsync(c, EntitlementTier.Team);
        var ownerTeams = owner.Get<ITeamService>();
        Assert.True(ownerTeams.CanOwn);
        var team = await ownerTeams.CreateAsync("ops", "Alex");
        var invite = await ownerTeams.CreateInviteAsync();

        // Joining needs the member's own Pro, not Team.
        var member = await c.StartAppAsync(EntitlementTier.Pro);
        var memberTeams = member.Get<ITeamService>();
        Assert.True(memberTeams.CanJoin);
        Assert.False(memberTeams.CanOwn);
        var joined = await memberTeams.JoinAsync(invite.Code, "Sam");
        Assert.Equal(team.Id, joined.Id);
        // An invite works once.
        var third = await c.StartAppAsync(EntitlementTier.Pro);
        await Assert.ThrowsAnyAsync<Exception>(() => third.Get<ITeamService>().JoinAsync(invite.Code, "Eve"));

        var shared = await ownerTeams.ShareHostAsync(host);
        var seen = Assert.Single((await memberTeams.GetMineAsync())!.Hosts);
        Assert.Equal((shared.Id, host.Address, host.Port), (seen.Id, seen.Host, seen.Port));
        Assert.Contains((await ownerTeams.GetMineAsync())!.Members, m => m.DisplayName == "Sam");
        var audit = await ownerTeams.AuditAsync();
        Assert.Contains(audit, e => e.ActorName == "Sam");

        await memberTeams.LeaveAsync();
        Assert.Null(await memberTeams.GetMineAsync());
        Assert.DoesNotContain((await ownerTeams.GetMineAsync())!.Members, m => m.DisplayName == "Sam");
        await ownerTeams.DeleteAsync();
        Assert.Null(await ownerTeams.GetMineAsync());
    }

    private static async Task ServiceRefusesFreeUsers(E2EContext c)
    {
        c.RequireApi();
        var (free, _) = await StartWithHostAsync(c, EntitlementTier.Free);
        Assert.False(free.Get<ICloudVaultBackupService>().CanUpload);
        Assert.False(free.Get<IHeartbeatMonitorService>().CanUse);
        Assert.False(free.Get<ITeamService>().CanOwn);

        // The UI hides these, but the refusal that counts is the service's:
        // a Free grant presented straight to the API is turned away.
        var grant = await free.Get<IManagedDerpGrantProvider>().GetNodeBoundGrantAsync(
            await free.Get<ITailcatHubService>().GetClientPublicKeyAsync());
        var credential = CloudBackupCredential.FromRecoveryCode(free.RecoveryCode!);
        await Assert.ThrowsAnyAsync<Exception>(() => free.Get<ICloudBackupApi>().UploadAsync(credential, grant, new byte[] { 1, 2, 3 }));
        await Assert.ThrowsAnyAsync<Exception>(() => free.Get<IHeartbeatMonitorApi>().CreateAsync("owner-" + Guid.NewGuid().ToString("N"), grant, "x", 3600, 600));
        await Assert.ThrowsAnyAsync<Exception>(() => free.Get<ITeamApi>().CreateAsync("member-" + Guid.NewGuid().ToString("N"), grant, "t", "n"));
    }

    /// <summary>
    /// Ask AI from the terminal: the app sends output the server really
    /// printed, gets an explanation, asks for a command and runs the one it is
    /// given. The model is MeowSSHAPI's Anthropic stand-in; everything between
    /// the panel and the provider's HTTP endpoint is real.
    /// </summary>
    private static async Task AskAiExplainsOutputAndSuggestsACommandThatRuns(E2EContext c)
    {
        c.RequireApi();
        var (app, host) = await StartWithHostAsync(c, EntitlementTier.ProCloud);
        var ai = app.Get<IHostedAiService>();
        Assert.True(ai.CanUse);
        await using var connection = await app.ConnectSshAsync(host);

        var failed = await connection.RunCommandAsync("meowssh-no-such-command 2>&1; true");
        Assert.Contains("not found", failed.StandardOutput, StringComparison.Ordinal);
        var explained = await ai.AskAsync(new AiAssistRequest(AiAssistMode.Explain, failed.StandardOutput, "why?"));
        Assert.Equal("E2E explanation of the terminal output.", explained.Answer);
        Assert.Null(explained.Command);

        var suggested = await ai.AskAsync(new AiAssistRequest(AiAssistMode.Command, "", "print a line"));
        var command = suggested.Command ?? throw new InvalidOperationException("A command request came back with no command: " + suggested.Answer);
        Assert.Equal("meowssh-ai-e2e", (await connection.RunCommandAsync(command)).StandardOutput.Trim());

        // A model refusal reaches the panel as a refusal, not an empty answer.
        var refused = await Assert.ThrowsAsync<HostedAiException>(() => ai.AskAsync(new AiAssistRequest(AiAssistMode.Explain, "E2E_REFUSE", null)));
        Assert.Equal("ai_refused", refused.Code);

        // Pro is not Pro Cloud: the app hides Ask AI, and the API turns the
        // Pro grant away when it is presented anyway.
        var pro = await c.StartAppAsync(EntitlementTier.Pro);
        Assert.False(pro.Get<IHostedAiService>().CanUse);
        var proGrant = await pro.Get<ICloudEntitlementGrantSource>().GetPaidGrantAsync();
        var denied = await Assert.ThrowsAsync<HostedAiException>(() => pro.Get<IHostedAiApi>()
            .AssistAsync(proGrant, new AiAssistRequest(AiAssistMode.Explain, "anything", null)));
        Assert.NotNull(denied.Code);
        c.Log("a Pro grant is refused with " + denied.Code);
    }

    /// <summary>
    /// Two MeowSSH accounts on the managed relay: the owner shares with a
    /// client on another account by putting it on the share's allowed-client
    /// list, and the relay -- asked exactly as it asks before forwarding --
    /// carries that pair, billed to the owner. Anyone else on another account
    /// is still refused.
    /// </summary>
    private static async Task AGuestOnAnotherAccountIsCarriedAndBilledToTheOwner(E2EContext c)
    {
        var api = c.RequireApi();
        var owner = await c.StartAppAsync(EntitlementTier.Pro);
        var guest = await c.StartAppAsync(EntitlementTier.Free);
        var stranger = await c.StartAppAsync(EntitlementTier.Free);
        var ownerHub = owner.Get<ITailcatHubService>();
        // What each Keys screen shows; showing it also registers the node.
        var ownerNode = await ownerHub.GetClientPublicKeyAsync();
        var guestNode = await guest.Get<ITailcatHubService>().GetClientPublicKeyAsync();
        var strangerNode = await stranger.Get<ITailcatHubService>().GetClientPublicKeyAsync();
        Assert.Equal(3, new[] { ownerNode, guestNode, strangerNode }.Distinct().Count());

        var address = await TailcatScenarios.StartHermeticServerAsync(ownerHub, new TailcatServeRequest(
            Lifetime: TimeSpan.FromMinutes(10),
            AllowedClientKeys: $"{ownerNode},{guestNode}",
            EnableShell: true,
            AuthorizedSshKeys: null,
            EnableFiles: false,
            SharedFolder: null,
            FileMode: "ro",
            EnableExitNode: false,
            UseTailcatCredentialForShell: true));
        try
        {
            var server = (await ownerHub.InspectAddressAsync(address)).ServerPublicKey;
            // Registration follows the share in the background, as on a phone.
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
            E2EApi.RelayPairDecision toServer;
            while (!(toServer = await api.AuthorizePairAsync(guestNode, server)).Allow && DateTime.UtcNow < deadline)
                await Task.Delay(500);
            c.Log($"guest -> server: {toServer.Reason}");

            Assert.True(toServer.Allow, $"the owner's guest was refused: {toServer.Reason}");
            // Billed to the owner's own node, never to the guest's account.
            Assert.Equal(ownerNode, toServer.ClientNodePublic);
            var back = await api.AuthorizePairAsync(server, guestNode);
            Assert.True(back.Allow);
            Assert.Equal(ownerNode, back.ClientNodePublic);
            // The owner's own phone, as before.
            Assert.Equal(ownerNode, (await api.AuthorizePairAsync(ownerNode, server)).ClientNodePublic);

            var refused = await api.AuthorizePairAsync(strangerNode, server);
            Assert.False(refused.Allow);
            Assert.Equal("DifferentOwner", refused.Reason);
            // A guest of the server, not of the owner's phone.
            Assert.False((await api.AuthorizePairAsync(guestNode, ownerNode)).Allow);
        }
        finally
        {
            await ownerHub.StopServerAsync();
        }
    }

    private static async Task<(E2EApp App, HostRecord Host)> StartWithHostAsync(E2EContext c, EntitlementTier tier)
    {
        var app = await c.StartAppAsync(tier);
        var key = await app.AddPrivateKeyAsync(c.Server.Username, c.Server.ClientPrivateKey);
        return (app, await app.AddHostAsync(c.Server, key));
    }
}
