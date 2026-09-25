using MeowSSH.Core.Licensing;
using MeowSSH.Core.Model;
using MeowSSH.Core.Services;
using Microsoft.AspNetCore.Components;

namespace MeowSSH.TestHost.Fakes;

/// <summary>
/// Stands in for TeamService, which needs MeowSSHAPI. The server enforces the
/// real rules (tested there) and the client gating is tested in Core; this
/// reproduces what the page renders and keeps a small audit trail of its own.
/// </summary>
/// <remarks>
/// "teamowned" starts as the owner of "Ops" with Alice as a member, one open
/// invite, two shared hosts -- build-runner (already in the sample vault)
/// and a web server that is not -- and one shared Action. "teammember"
/// starts as Alice in the same team. The one invite code that joins is
/// <see cref="WorkingCode"/>.
/// </remarks>
public sealed class FakeTeamService : ITeamService
{
    public const string WorkingCode = "7K2QM-ABCDE-FGHJK-MNPQR";

    private static readonly DateTimeOffset Seeded = new(2026, 9, 20, 9, 0, 0, TimeSpan.Zero);
    private readonly IEntitlementService _entitlements;
    private readonly List<TeamAuditEntry> _audit = [];
    private TeamInfo? _team;
    private int _next;

    public FakeTeamService(IEntitlementService entitlements, NavigationManager navigation)
    {
        _entitlements = entitlements;
        var query = new Uri(navigation.Uri).Query;
        var owned = query.Contains("teamowned", StringComparison.OrdinalIgnoreCase);
        if (!owned && !query.Contains("teammember", StringComparison.OrdinalIgnoreCase)) return;

        _team = new TeamInfo(
            "t1",
            "Ops",
            owned ? "sam" : "alice",
            owned ? TeamRoles.Owner : TeamRoles.Member,
            [
                new TeamMemberInfo("sam", "Sam", TeamRoles.Owner, Seeded),
                new TeamMemberInfo("alice", "Alice", TeamRoles.Member, Seeded.AddDays(1)),
            ],
            [
                new TeamSharedHost("h1", "build-runner", "build.internal", 2222, "ci", Seeded),
                new TeamSharedHost("h2", "Shared web", "web.internal", 22, "deploy", Seeded),
            ],
            owned ? [new TeamInviteInfo("i1", Seeded, Seeded.AddDays(30))] : [])
        {
            Actions = [new TeamSharedAction("a1", "Restart app", "sudo systemctl restart app", 60, Seeded)],
        };
        _audit.Add(new TeamAuditEntry(Seeded, "team_created", "sam", "Sam", "Ops"));
        _audit.Add(new TeamAuditEntry(Seeded.AddDays(1), "member_joined", "alice", "Alice", null));
    }

    public bool CanOwn => _entitlements.Has(PremiumFeature.TeamSharing);

    public bool CanJoin => _entitlements.Current.Tier >= EntitlementTier.Pro;

    public Task<TeamInfo?> GetMineAsync(CancellationToken cancellationToken = default) => Task.FromResult(_team);

    public Task<TeamInfo> CreateAsync(string name, string displayName, CancellationToken cancellationToken = default)
    {
        if (!CanOwn) throw new InvalidOperationException("Creating a team requires MeowSSH Team.");
        _team = new TeamInfo("t2", name.Trim(), "me", TeamRoles.Owner, [new TeamMemberInfo("me", displayName.Trim(), TeamRoles.Owner, Now)], [], []);
        Log("team_created", _team.Name);
        return Task.FromResult(_team);
    }

    public Task<TeamInfo> JoinAsync(string code, string displayName, CancellationToken cancellationToken = default)
    {
        if (!CanJoin) throw new InvalidOperationException("Joining a team requires MeowSSH Pro.");
        if (!string.Equals(code.Trim().Replace(' ', '-'), WorkingCode, StringComparison.OrdinalIgnoreCase))
            throw new TeamException("invalid_invite", "That invite code is not valid. It may have expired, been revoked or already been used.");
        _team = new TeamInfo("t1", "Ops", "me", TeamRoles.Member,
            [new TeamMemberInfo("sam", "Sam", TeamRoles.Owner, Seeded), new TeamMemberInfo("me", displayName.Trim(), TeamRoles.Member, Now)],
            [new TeamSharedHost("h2", "Shared web", "web.internal", 22, "deploy", Seeded)],
            []);
        return Task.FromResult(_team);
    }

    public Task<CreatedTeamInvite> CreateInviteAsync(CancellationToken cancellationToken = default)
    {
        var team = RequireOwner();
        if (!CanOwn) throw new InvalidOperationException("Inviting people requires MeowSSH Team.");
        var invite = new TeamInviteInfo($"i{++_next + 1}", Now, Now.AddDays(7));
        _team = team with { Invites = [.. team.Invites, invite] };
        Log("invite_created", null);
        return Task.FromResult(new CreatedTeamInvite(invite, $"ZZZZ{_next}-QQQQQ-RRRRR-SSSSS"));
    }

    public Task RevokeInviteAsync(string inviteId, CancellationToken cancellationToken = default)
    {
        var team = RequireOwner();
        _team = team with { Invites = [.. team.Invites.Where(i => i.Id != inviteId)] };
        Log("invite_revoked", null);
        return Task.CompletedTask;
    }

    public Task RemoveMemberAsync(string memberId, CancellationToken cancellationToken = default)
    {
        var team = RequireOwner();
        var member = team.Members.Single(m => m.Id == memberId);
        _team = team with { Members = [.. team.Members.Where(m => m.Id != memberId)] };
        Log("member_removed", member.DisplayName);
        return Task.CompletedTask;
    }

    public Task LeaveAsync(CancellationToken cancellationToken = default)
    {
        if (_team is null || _team.IsOwner) throw new InvalidOperationException("The owner cannot leave the team. Delete the team instead.");
        _team = null;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(CancellationToken cancellationToken = default)
    {
        RequireOwner();
        _team = null;
        return Task.CompletedTask;
    }

    public Task<TeamSharedHost> ShareHostAsync(HostRecord host, CancellationToken cancellationToken = default)
    {
        var team = RequireOwner();
        if (!CanOwn) throw new InvalidOperationException("Sharing hosts requires MeowSSH Team.");
        if (!TeamHosts.CanShare(host)) throw new InvalidOperationException("Only SSH hosts reached directly by name or IP address can be shared.");
        var shared = new TeamSharedHost($"h{++_next + 2}", host.Label, host.Address, host.Port, host.Username ?? "", Now);
        _team = team with { Hosts = [.. team.Hosts, shared] };
        Log("host_shared", $"{shared.Label} ({(shared.Username.Length > 0 ? shared.Username + "@" : "")}{shared.Host}:{shared.Port})");
        return Task.FromResult(shared);
    }

    public Task UnshareHostAsync(string hostId, CancellationToken cancellationToken = default)
    {
        var team = RequireOwner();
        var host = team.Hosts.Single(h => h.Id == hostId);
        _team = team with { Hosts = [.. team.Hosts.Where(h => h.Id != hostId)] };
        Log("host_removed", $"{host.Label} ({(host.Username.Length > 0 ? host.Username + "@" : "")}{host.Host}:{host.Port})");
        return Task.CompletedTask;
    }

    public Task<TeamSharedAction> ShareActionAsync(CommandAction action, CancellationToken cancellationToken = default)
    {
        var team = RequireOwner();
        if (!CanOwn) throw new InvalidOperationException("Sharing Actions requires MeowSSH Team.");
        var shared = new TeamSharedAction($"a{++_next + 1}", action.Name.Trim(), action.Command.Trim(), action.TimeoutSeconds, Now);
        _team = team with { Actions = [.. team.Actions, shared] };
        Log("action_shared", shared.Name);
        return Task.FromResult(shared);
    }

    public Task UnshareActionAsync(string actionId, CancellationToken cancellationToken = default)
    {
        var team = RequireOwner();
        var action = team.Actions.Single(a => a.Id == actionId);
        _team = team with { Actions = [.. team.Actions.Where(a => a.Id != actionId)] };
        Log("action_removed", action.Name);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<TeamAuditEntry>> AuditAsync(CancellationToken cancellationToken = default)
    {
        RequireOwner();
        return Task.FromResult<IReadOnlyList<TeamAuditEntry>>([.. _audit.AsEnumerable().Reverse()]);
    }

    private DateTimeOffset Now => Seeded.AddDays(5).AddMinutes(_audit.Count);

    private TeamInfo RequireOwner() =>
        _team is { IsOwner: true } team ? team : throw new InvalidOperationException("Only the team owner can do that.");

    private void Log(string action, string? subject)
    {
        var you = _team!.Members.Single(m => m.Id == _team.YourMemberId);
        _audit.Add(new TeamAuditEntry(Now, action, you.Id, you.DisplayName, subject));
    }
}
