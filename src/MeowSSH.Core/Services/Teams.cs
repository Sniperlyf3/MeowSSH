using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using MeowSSH.Core.Licensing;
using MeowSSH.Core.Model;

namespace MeowSSH.Core.Services;

/// <summary>Mirrors MeowSSHAPI's TeamRoles.</summary>
public static class TeamRoles
{
    public const string Owner = "owner";
    public const string Member = "member";
}

public sealed record TeamMemberInfo(string Id, string DisplayName, string Role, DateTimeOffset JoinedAtUtc);

/// <summary>An address-book entry: there is deliberately nowhere to put a key or password.</summary>
public sealed record TeamSharedHost(string Id, string Label, string Host, int Port, string Username, DateTimeOffset SharedAtUtc);

/// <summary>A saved command as the team shares it: no hosts (those are each member's own) and no variable values.</summary>
public sealed record TeamSharedAction(string Id, string Name, string Command, int TimeoutSeconds, DateTimeOffset SharedAtUtc);

public sealed record TeamInviteInfo(string Id, DateTimeOffset CreatedAtUtc, DateTimeOffset ExpiresAtUtc);

/// <summary>A team as its member sees it. Invites (the open ones) are sent to the owner only.</summary>
public sealed record TeamInfo(
    string Id,
    string Name,
    string YourMemberId,
    string YourRole,
    IReadOnlyList<TeamMemberInfo> Members,
    IReadOnlyList<TeamSharedHost> Hosts,
    IReadOnlyList<TeamInviteInfo> Invites)
{
    public bool IsOwner => YourRole == TeamRoles.Owner;

    private readonly IReadOnlyList<TeamSharedAction> _actions = [];

    /// <summary>
    /// Empty, never null, when a server from before shared Actions leaves it
    /// out. The initializer alone is not enough: the source-generated reader
    /// builds this record through its constructor and then assigns every
    /// init property, writing null over the default for one that is absent.
    /// </summary>
    public IReadOnlyList<TeamSharedAction> Actions
    {
        get => _actions;
        init => _actions = value ?? [];
    }
}

/// <summary>The only time the code exists outside the owner's clipboard: the server keeps a hash.</summary>
public sealed record CreatedTeamInvite(TeamInviteInfo Invite, string Code);

public sealed record TeamAuditEntry(DateTimeOffset AtUtc, string Action, string ActorMemberId, string ActorName, string? Subject);

/// <summary><see cref="Exception.Message"/> is written for the user.</summary>
public sealed class TeamException(string? code, string message, Exception? inner = null) : Exception(message, inner)
{
    public string? Code { get; } = code;
}

/// <summary>MeowSSHAPI's /v1/teams surface.</summary>
public interface ITeamApi
{
    /// <returns>Null when this identity is in no team.</returns>
    Task<TeamInfo?> GetMineAsync(string secret, CancellationToken cancellationToken = default);
    Task<TeamInfo> CreateAsync(string secret, SignedEntitlementGrant grant, string name, string displayName, CancellationToken cancellationToken = default);
    Task DeleteAsync(string secret, CancellationToken cancellationToken = default);
    Task<TeamInfo> JoinAsync(string secret, SignedEntitlementGrant grant, string code, string displayName, CancellationToken cancellationToken = default);
    Task LeaveAsync(string secret, CancellationToken cancellationToken = default);
    Task<CreatedTeamInvite> CreateInviteAsync(string secret, SignedEntitlementGrant grant, CancellationToken cancellationToken = default);
    Task RevokeInviteAsync(string secret, string inviteId, CancellationToken cancellationToken = default);
    Task RemoveMemberAsync(string secret, string memberId, CancellationToken cancellationToken = default);
    Task<TeamSharedHost> ShareHostAsync(string secret, SignedEntitlementGrant grant, string label, string host, int port, string username, CancellationToken cancellationToken = default);
    Task UnshareHostAsync(string secret, string hostId, CancellationToken cancellationToken = default);
    Task<TeamSharedAction> ShareActionAsync(string secret, SignedEntitlementGrant grant, string name, string command, int timeoutSeconds, CancellationToken cancellationToken = default);
    Task UnshareActionAsync(string secret, string actionId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TeamAuditEntry>> AuditAsync(string secret, CancellationToken cancellationToken = default);
}

/// <summary>
/// This install's team identity: a random secret of its own, separate from the
/// monitor and cloud-backup secrets, so none of them can stand in for another.
/// </summary>
public interface ITeamMemberSecretStore
{
    Task<string?> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(string secret, CancellationToken cancellationToken = default);
}

public sealed class MemoryTeamMemberSecretStore : ITeamMemberSecretStore
{
    private string? _secret;
    public Task<string?> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(_secret);
    public Task SaveAsync(string secret, CancellationToken cancellationToken = default)
    {
        _secret = secret;
        return Task.CompletedTask;
    }
}

public interface ITeamService
{
    /// <summary>Creating a team, inviting and sharing hosts need Team.</summary>
    bool CanOwn { get; }

    /// <summary>Joining needs the joiner's own Pro; staying in a team does not.</summary>
    bool CanJoin { get; }

    /// <returns>Null when this phone is in no team.</returns>
    Task<TeamInfo?> GetMineAsync(CancellationToken cancellationToken = default);

    Task<TeamInfo> CreateAsync(string name, string displayName, CancellationToken cancellationToken = default);

    Task<TeamInfo> JoinAsync(string code, string displayName, CancellationToken cancellationToken = default);

    Task<CreatedTeamInvite> CreateInviteAsync(CancellationToken cancellationToken = default);

    Task RevokeInviteAsync(string inviteId, CancellationToken cancellationToken = default);

    Task RemoveMemberAsync(string memberId, CancellationToken cancellationToken = default);

    Task LeaveAsync(CancellationToken cancellationToken = default);

    Task DeleteAsync(CancellationToken cancellationToken = default);

    /// <summary>Shares the host's address only; see <see cref="TeamHosts.CanShare"/> for which hosts qualify.</summary>
    Task<TeamSharedHost> ShareHostAsync(HostRecord host, CancellationToken cancellationToken = default);

    Task UnshareHostAsync(string hostId, CancellationToken cancellationToken = default);

    /// <summary>Shares the Action's name, command text and timeout; never its hosts.</summary>
    Task<TeamSharedAction> ShareActionAsync(CommandAction action, CancellationToken cancellationToken = default);

    Task UnshareActionAsync(string actionId, CancellationToken cancellationToken = default);

    /// <returns>Newest first. Owner only.</returns>
    Task<IReadOnlyList<TeamAuditEntry>> AuditAsync(CancellationToken cancellationToken = default);
}

/// <summary>Between a vault host and a team's address book, in both directions.</summary>
public static class TeamHosts
{
    /// <summary>
    /// Plain SSH over TCP only. A tailcat address is itself the credential
    /// (whoever has it can dial the server), so sharing one would share
    /// access; a proxy URL can embed a password. Neither ever leaves the vault.
    /// </summary>
    public static bool CanShare(HostRecord host) =>
        host is { IsDeleted: false, Protocol: HostProtocol.Ssh, Transport: SshTransport.Tcp }
        && !string.IsNullOrWhiteSpace(host.Address);

    /// <summary>
    /// A new vault host from a shared entry, with no credential: each member
    /// signs in as themselves, and the team never held a key to hand out.
    /// </summary>
    public static HostRecord ToVaultHost(TeamSharedHost shared, string teamName) => new()
    {
        Id = Guid.NewGuid(),
        Label = shared.Label,
        Address = shared.Host,
        Port = shared.Port,
        Username = string.IsNullOrEmpty(shared.Username) ? null : shared.Username,
        Group = teamName,
    };

    /// <summary>Whether a vault host already points where the shared entry does, so it is not added twice.</summary>
    public static bool Matches(HostRecord host, TeamSharedHost shared) =>
        CanShare(host)
        && string.Equals(host.Address, shared.Host, StringComparison.OrdinalIgnoreCase)
        && host.Port == shared.Port
        && (shared.Username.Length == 0 || string.Equals(host.Username, shared.Username, StringComparison.Ordinal));
}

/// <summary>Between a saved Action and a team's shared ones, in both directions.</summary>
public static class TeamActions
{
    /// <summary>
    /// A new Action from a shared one, on a host the member picks from their
    /// own vault: an Action needs at least one host, and the owner's host ids
    /// would mean nothing here.
    /// </summary>
    public static CommandAction ToCommandAction(TeamSharedAction shared, Guid hostId) =>
        new(Guid.NewGuid(), shared.Name, shared.Command, [hostId], shared.TimeoutSeconds);

    /// <summary>Same command text, whatever it is called: renaming a copy does not make it a different command.</summary>
    public static bool Matches(CommandAction action, TeamSharedAction shared) =>
        string.Equals(action.Command.Trim(), shared.Command.Trim(), StringComparison.Ordinal);
}

public sealed class TeamService(
    ITeamApi api,
    ICloudEntitlementGrantSource grants,
    IEntitlementService entitlements,
    ITeamMemberSecretStore secrets) : ITeamService
{
    public bool CanOwn => entitlements.Has(PremiumFeature.TeamSharing);

    public bool CanJoin => entitlements.Current.Tier >= EntitlementTier.Pro && !entitlements.Current.IsExpired(DateTimeOffset.UtcNow);

    public async Task<TeamInfo?> GetMineAsync(CancellationToken cancellationToken = default)
    {
        // No secret means this phone never created or joined a team; asking
        // would only tell the server a new install exists.
        var secret = await secrets.LoadAsync(cancellationToken).ConfigureAwait(false);
        return secret is null ? null : await api.GetMineAsync(secret, cancellationToken).ConfigureAwait(false);
    }

    public async Task<TeamInfo> CreateAsync(string name, string displayName, CancellationToken cancellationToken = default)
    {
        if (!CanOwn) throw new InvalidOperationException("Creating a team requires MeowSSH Team.");
        var secret = await SecretAsync(cancellationToken).ConfigureAwait(false);
        var grant = await GrantAsync("Team", cancellationToken).ConfigureAwait(false);
        return await api.CreateAsync(secret, grant, name.Trim(), displayName.Trim(), cancellationToken).ConfigureAwait(false);
    }

    public async Task<TeamInfo> JoinAsync(string code, string displayName, CancellationToken cancellationToken = default)
    {
        if (!CanJoin) throw new InvalidOperationException("Joining a team requires MeowSSH Pro.");
        var secret = await SecretAsync(cancellationToken).ConfigureAwait(false);
        var grant = await GrantAsync("Pro", cancellationToken).ConfigureAwait(false);
        return await api.JoinAsync(secret, grant, code.Trim(), displayName.Trim(), cancellationToken).ConfigureAwait(false);
    }

    public async Task<CreatedTeamInvite> CreateInviteAsync(CancellationToken cancellationToken = default)
    {
        if (!CanOwn) throw new InvalidOperationException("Inviting people requires MeowSSH Team.");
        var secret = await ExistingSecretAsync(cancellationToken).ConfigureAwait(false);
        var grant = await GrantAsync("Team", cancellationToken).ConfigureAwait(false);
        return await api.CreateInviteAsync(secret, grant, cancellationToken).ConfigureAwait(false);
    }

    public async Task<TeamSharedHost> ShareHostAsync(HostRecord host, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(host);
        if (!CanOwn) throw new InvalidOperationException("Sharing hosts requires MeowSSH Team.");
        if (!TeamHosts.CanShare(host))
            throw new InvalidOperationException("Only SSH hosts reached directly by name or IP address can be shared. Tailcat addresses are access in themselves, so they stay on this phone.");
        var secret = await ExistingSecretAsync(cancellationToken).ConfigureAwait(false);
        var grant = await GrantAsync("Team", cancellationToken).ConfigureAwait(false);
        return await api.ShareHostAsync(secret, grant, host.Label, host.Address.Trim(), host.Port, host.Username?.Trim() ?? "", cancellationToken).ConfigureAwait(false);
    }

    public async Task<TeamSharedAction> ShareActionAsync(CommandAction action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (!CanOwn) throw new InvalidOperationException("Sharing Actions requires MeowSSH Team.");
        var secret = await ExistingSecretAsync(cancellationToken).ConfigureAwait(false);
        var grant = await GrantAsync("Team", cancellationToken).ConfigureAwait(false);
        return await api.ShareActionAsync(secret, grant, action.Name.Trim(), action.Command.Trim(), action.TimeoutSeconds, cancellationToken).ConfigureAwait(false);
    }

    // Everything below only takes away, so none of it checks the tier: a
    // lapsed owner can still tidy up, and anyone can always leave.
    public async Task RevokeInviteAsync(string inviteId, CancellationToken cancellationToken = default) =>
        await api.RevokeInviteAsync(await ExistingSecretAsync(cancellationToken).ConfigureAwait(false), inviteId, cancellationToken).ConfigureAwait(false);

    public async Task RemoveMemberAsync(string memberId, CancellationToken cancellationToken = default) =>
        await api.RemoveMemberAsync(await ExistingSecretAsync(cancellationToken).ConfigureAwait(false), memberId, cancellationToken).ConfigureAwait(false);

    public async Task UnshareHostAsync(string hostId, CancellationToken cancellationToken = default) =>
        await api.UnshareHostAsync(await ExistingSecretAsync(cancellationToken).ConfigureAwait(false), hostId, cancellationToken).ConfigureAwait(false);

    public async Task UnshareActionAsync(string actionId, CancellationToken cancellationToken = default) =>
        await api.UnshareActionAsync(await ExistingSecretAsync(cancellationToken).ConfigureAwait(false), actionId, cancellationToken).ConfigureAwait(false);

    public async Task LeaveAsync(CancellationToken cancellationToken = default) =>
        await api.LeaveAsync(await ExistingSecretAsync(cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);

    public async Task DeleteAsync(CancellationToken cancellationToken = default) =>
        await api.DeleteAsync(await ExistingSecretAsync(cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<TeamAuditEntry>> AuditAsync(CancellationToken cancellationToken = default) =>
        await api.AuditAsync(await ExistingSecretAsync(cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);

    private async Task<string> SecretAsync(CancellationToken cancellationToken)
    {
        var secret = await secrets.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (secret is not null) return secret;
        secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        await secrets.SaveAsync(secret, cancellationToken).ConfigureAwait(false);
        return secret;
    }

    private async Task<string> ExistingSecretAsync(CancellationToken cancellationToken) =>
        await secrets.LoadAsync(cancellationToken).ConfigureAwait(false)
        ?? throw new InvalidOperationException("This phone is not in a team.");

    private async Task<SignedEntitlementGrant> GrantAsync(string product, CancellationToken cancellationToken)
    {
        try
        {
            return await grants.GetPaidGrantAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.Security.SecurityException or HttpRequestException)
        {
            throw new TeamException("entitlement_unavailable", $"Your MeowSSH {product} purchase could not be verified: {exception.Message}", exception);
        }
    }
}

public sealed class HttpTeamApi(HttpClient httpClient, LicensingApiOptions options) : ITeamApi
{
    private const string GrantHeader = "X-MeowSSH-Entitlement-Grant";

    public async Task<TeamInfo?> GetMineAsync(string secret, CancellationToken cancellationToken = default)
    {
        using var request = Request(HttpMethod.Get, "v1/teams/mine", secret);
        try
        {
            using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
            return await ReadAsync(response, TeamJsonContext.Default.TeamInfo, cancellationToken).ConfigureAwait(false);
        }
        catch (TeamException exception) when (exception.Code == "not_in_team")
        {
            return null;
        }
    }

    public async Task<TeamInfo> CreateAsync(string secret, SignedEntitlementGrant grant, string name, string displayName, CancellationToken cancellationToken = default)
    {
        using var request = Request(HttpMethod.Post, "v1/teams", secret, grant);
        request.Content = JsonContent.Create(new CreateBody(name, displayName), TeamJsonContext.Default.CreateBody);
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        return await ReadAsync(response, TeamJsonContext.Default.TeamInfo, cancellationToken).ConfigureAwait(false);
    }

    public Task DeleteAsync(string secret, CancellationToken cancellationToken = default) =>
        SendEmptyAsync(HttpMethod.Delete, "v1/teams/mine", secret, cancellationToken);

    public async Task<TeamInfo> JoinAsync(string secret, SignedEntitlementGrant grant, string code, string displayName, CancellationToken cancellationToken = default)
    {
        using var request = Request(HttpMethod.Post, "v1/teams/join", secret, grant);
        request.Content = JsonContent.Create(new JoinBody(code, displayName), TeamJsonContext.Default.JoinBody);
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        return await ReadAsync(response, TeamJsonContext.Default.TeamInfo, cancellationToken).ConfigureAwait(false);
    }

    public Task LeaveAsync(string secret, CancellationToken cancellationToken = default) =>
        SendEmptyAsync(HttpMethod.Post, "v1/teams/mine/leave", secret, cancellationToken);

    public async Task<CreatedTeamInvite> CreateInviteAsync(string secret, SignedEntitlementGrant grant, CancellationToken cancellationToken = default)
    {
        using var request = Request(HttpMethod.Post, "v1/teams/mine/invites", secret, grant);
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        return await ReadAsync(response, TeamJsonContext.Default.CreatedTeamInvite, cancellationToken).ConfigureAwait(false);
    }

    public Task RevokeInviteAsync(string secret, string inviteId, CancellationToken cancellationToken = default) =>
        SendEmptyAsync(HttpMethod.Delete, "v1/teams/mine/invites/" + Uri.EscapeDataString(inviteId), secret, cancellationToken);

    public Task RemoveMemberAsync(string secret, string memberId, CancellationToken cancellationToken = default) =>
        SendEmptyAsync(HttpMethod.Delete, "v1/teams/mine/members/" + Uri.EscapeDataString(memberId), secret, cancellationToken);

    public async Task<TeamSharedHost> ShareHostAsync(string secret, SignedEntitlementGrant grant, string label, string host, int port, string username, CancellationToken cancellationToken = default)
    {
        using var request = Request(HttpMethod.Post, "v1/teams/mine/hosts", secret, grant);
        request.Content = JsonContent.Create(new ShareBody(label, host, port, username), TeamJsonContext.Default.ShareBody);
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        return await ReadAsync(response, TeamJsonContext.Default.TeamSharedHost, cancellationToken).ConfigureAwait(false);
    }

    public Task UnshareHostAsync(string secret, string hostId, CancellationToken cancellationToken = default) =>
        SendEmptyAsync(HttpMethod.Delete, "v1/teams/mine/hosts/" + Uri.EscapeDataString(hostId), secret, cancellationToken);

    public async Task<TeamSharedAction> ShareActionAsync(string secret, SignedEntitlementGrant grant, string name, string command, int timeoutSeconds, CancellationToken cancellationToken = default)
    {
        using var request = Request(HttpMethod.Post, "v1/teams/mine/actions", secret, grant);
        request.Content = JsonContent.Create(new ShareActionBody(name, command, timeoutSeconds), TeamJsonContext.Default.ShareActionBody);
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        return await ReadAsync(response, TeamJsonContext.Default.TeamSharedAction, cancellationToken).ConfigureAwait(false);
    }

    public Task UnshareActionAsync(string secret, string actionId, CancellationToken cancellationToken = default) =>
        SendEmptyAsync(HttpMethod.Delete, "v1/teams/mine/actions/" + Uri.EscapeDataString(actionId), secret, cancellationToken);

    public async Task<IReadOnlyList<TeamAuditEntry>> AuditAsync(string secret, CancellationToken cancellationToken = default)
    {
        using var request = Request(HttpMethod.Get, "v1/teams/mine/audit", secret);
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        return (await ReadAsync(response, TeamJsonContext.Default.AuditBody, cancellationToken).ConfigureAwait(false)).Events;
    }

    private async Task SendEmptyAsync(HttpMethod method, string path, string secret, CancellationToken cancellationToken)
    {
        using var request = Request(method, path, secret);
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private HttpRequestMessage Request(HttpMethod method, string path, string secret, SignedEntitlementGrant? grant = null)
    {
        if (!options.IsConfigured || options.BaseUri is null)
            throw new TeamException("not_configured", "The MeowSSH team service is not configured in this build.");
        var request = new HttpRequestMessage(method, new Uri(options.BaseUri, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        if (grant is not null) request.Headers.Add(GrantHeader, grant.PayloadBase64 + "." + grant.SignatureBase64);
        return request;
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type, CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync(type, cancellationToken).ConfigureAwait(false)
                ?? throw new TeamException("empty_response", "The MeowSSH team service returned an empty response.");
        }
        catch (JsonException exception)
        {
            throw new TeamException("bad_response", "The MeowSSH team service returned something this version of the app does not understand.", exception);
        }
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            throw new TeamException("network", "Could not reach the MeowSSH team service. Check your connection and try again.", exception);
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TeamException("timeout", "The MeowSSH team service took too long to answer. Try again.", exception);
        }
        if (response.IsSuccessStatusCode) return response;

        using (response)
        {
            ErrorBody? error = null;
            try
            {
                error = await response.Content.ReadFromJsonAsync(TeamJsonContext.Default.ErrorBody, cancellationToken).ConfigureAwait(false);
            }
            catch (JsonException) { }
            catch (NotSupportedException) { }
            throw new TeamException(error?.Error, Explain(error, response.StatusCode));
        }
    }

    /// <summary>
    /// MeowSSHAPI writes its team refusals for people ("This team is full.
    /// Ask its owner to make room."), so those are shown as they are; only
    /// grant problems, which it words for developers, are rephrased here.
    /// </summary>
    private static string Explain(ErrorBody? error, HttpStatusCode status) => error?.Error switch
    {
        "expired_grant" or "invalid_grant" or "grant_from_future" or "wrong_package" =>
            "Your MeowSSH purchase could not be verified right now. Try again in a moment.",
        not null when !string.IsNullOrWhiteSpace(error.Message) => error.Message,
        _ when status == HttpStatusCode.TooManyRequests => "The MeowSSH team service is busy. Try again in a minute.",
        _ => $"The MeowSSH team service returned an error ({(int)status}).",
    };

    internal sealed record CreateBody(string Name, string DisplayName);
    internal sealed record JoinBody(string Code, string DisplayName);
    internal sealed record ShareBody(string Label, string Host, int Port, string Username);
    internal sealed record ShareActionBody(string Name, string Command, int TimeoutSeconds);
    internal sealed record AuditBody(IReadOnlyList<TeamAuditEntry> Events);
    internal sealed record ErrorBody(string? Error, string? Message);
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(HttpTeamApi.CreateBody))]
[JsonSerializable(typeof(HttpTeamApi.JoinBody))]
[JsonSerializable(typeof(HttpTeamApi.ShareBody))]
[JsonSerializable(typeof(HttpTeamApi.ShareActionBody))]
[JsonSerializable(typeof(TeamSharedAction))]
[JsonSerializable(typeof(HttpTeamApi.AuditBody))]
[JsonSerializable(typeof(HttpTeamApi.ErrorBody))]
[JsonSerializable(typeof(TeamInfo))]
[JsonSerializable(typeof(CreatedTeamInvite))]
[JsonSerializable(typeof(TeamSharedHost))]
internal sealed partial class TeamJsonContext : JsonSerializerContext
{
}
