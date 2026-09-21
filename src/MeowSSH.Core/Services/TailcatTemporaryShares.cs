using System.Text.Json;
using System.Text.Json.Serialization;
using MeowSSH.Core.Licensing;

namespace MeowSSH.Core.Services;

/// <summary>Why a temporary share stopped being usable.</summary>
public enum TailcatTemporaryShareEndReason
{
    /// <summary>The user tapped revoke before the lifetime ran out.</summary>
    Revoked,

    /// <summary>
    /// The lifetime ran out (or the underlying Tailcat server otherwise stopped
    /// without a matching <see cref="ITailcatTemporaryShareService.RevokeAsync"/>
    /// call, e.g. it was stopped from the general "Share this phone" panel).
    /// Both cases mean the same thing to the person who received the address:
    /// it no longer works.
    /// </summary>
    Expired,
}

/// <summary>
/// A single grant of time-bounded Tailcat access to one or more local
/// services/ports. Every share gets a brand-new ephemeral Tailcat identity, so
/// once it ends the address is gone for good -- restarting sharing later never
/// resurrects an address someone was already handed.
/// </summary>
public sealed record TailcatTemporaryShare(
    Guid Id,
    string Label,
    IReadOnlyList<string> ServeTargets,
    string AllowedClientKeys,
    string Address,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    DateTimeOffset? EndedAtUtc,
    TailcatTemporaryShareEndReason? EndReason)
{
    /// <summary>
    /// True only while this service's own bookkeeping still considers the
    /// share live. Callers that need to know whether the address can actually
    /// still be reached should prefer <see cref="ITailcatTemporaryShareService.Active"/>,
    /// which is re-derived from the Tailcat hub's live snapshot rather than
    /// this stored flag -- see the remarks on that property.
    /// </summary>
    public bool IsActive => EndedAtUtc is null;
}

public sealed record TailcatTemporaryShareRequest(
    string Label,
    IReadOnlyList<string> ServeTargets,
    string AllowedClientKeys,
    TimeSpan Lifetime,
    string? DerpMapUrl = null);

public interface ITailcatTemporaryShareStore
{
    Task<IReadOnlyList<TailcatTemporaryShare>> GetAllAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(TailcatTemporaryShare share, CancellationToken cancellationToken = default);
}

public interface ITailcatTemporaryShareService
{
    /// <summary>
    /// The share this service currently believes is live, or null.
    /// </summary>
    /// <remarks>
    /// Deliberately re-checks the Tailcat hub's own snapshot on every read
    /// instead of returning a cached flag: the hub's snapshot is the only
    /// place a real, still-running server is reflected. A share whose stored
    /// record has not yet been finalized but whose Tailcat process already
    /// died (lifetime elapsed, or stopped from elsewhere) must read as
    /// inactive here -- otherwise the UI could tell a user "expired" is only
    /// cosmetic, which is the one thing this feature must never do.
    /// </remarks>
    TailcatTemporaryShare? Active { get; }

    event EventHandler? Changed;

    /// <summary>Most recent shares first, including ones that have already ended.</summary>
    Task<IReadOnlyList<TailcatTemporaryShare>> GetHistoryAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts a brand-new, time-bounded Tailcat server scoped to the requested
    /// service(s)/port(s) only -- never shell, files or exit-node access, and
    /// never an "allow any client" address. Requires MeowSSH Pro and requires
    /// at least one Tailcat client key to be named as allowed to connect: a
    /// temporary share narrows how long an address works, not who may use it.
    /// </summary>
    Task<TailcatTemporaryShare> CreateAsync(TailcatTemporaryShareRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops the active share's underlying Tailcat server immediately -- the
    /// real OS process is terminated, the same as the general "Stop sharing"
    /// action -- and records it as revoked. A no-op when nothing is active.
    /// </summary>
    Task RevokeAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Small local JSON store for temporary-share history, following the same
/// atomic-write/tmp-then-move shape as <see cref="FileTailcatWorkspaceStore"/>.
/// Bounded like <c>FileSessionLogService</c> so a device that is shared from
/// often does not grow this file forever.
/// </summary>
public sealed class FileTailcatTemporaryShareStore(string path) : ITailcatTemporaryShareStore
{
    private const int MaxHistory = 50;
    private readonly object _gate = new();
    private readonly string _path = string.IsNullOrWhiteSpace(path)
        ? throw new ArgumentException("A temporary-share file path is required.", nameof(path))
        : path;

    public Task<IReadOnlyList<TailcatTemporaryShare>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            IReadOnlyList<TailcatTemporaryShare> shares =
                [.. ReadUnsafe().OrderByDescending(static share => share.CreatedAtUtc)];
            return Task.FromResult(shares);
        }
    }

    public Task SaveAsync(TailcatTemporaryShare share, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(share);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var existing = ReadUnsafe();
            var index = existing.FindIndex(item => item.Id == share.Id);
            if (index >= 0) existing[index] = share;
            else existing.Add(share);

            var retained = existing
                .OrderByDescending(static item => item.CreatedAtUtc)
                .Take(MaxHistory)
                .ToList();
            WriteUnsafe(retained);
        }
        return Task.CompletedTask;
    }

    private List<TailcatTemporaryShare> ReadUnsafe()
    {
        if (!File.Exists(_path)) return [];
        try
        {
            var json = File.ReadAllText(_path);
            return JsonSerializer.Deserialize(json, TailcatTemporaryShareJsonContext.Default.TailcatTemporaryShareDocument)
                ?.Shares.ToList() ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private void WriteUnsafe(IReadOnlyList<TailcatTemporaryShare> shares)
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        var document = new TailcatTemporaryShareDocument(shares);
        var json = JsonSerializer.Serialize(document, TailcatTemporaryShareJsonContext.Default.TailcatTemporaryShareDocument);
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, json);
        File.Move(temporary, _path, overwrite: true);
    }
}

public sealed class MemoryTailcatTemporaryShareStore : ITailcatTemporaryShareStore
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, TailcatTemporaryShare> _shares = [];

    public Task<IReadOnlyList<TailcatTemporaryShare>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
            return Task.FromResult<IReadOnlyList<TailcatTemporaryShare>>(
                [.. _shares.Values.OrderByDescending(static item => item.CreatedAtUtc)]);
    }

    public Task SaveAsync(TailcatTemporaryShare share, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(share);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate) _shares[share.Id] = share;
        return Task.CompletedTask;
    }
}

/// <summary>
/// Narrow, safety-focused wrapper over <see cref="ITailcatHubService"/>'s
/// existing serve capability. It never asks the hub to do anything the
/// general "Share this phone" flow cannot already do -- it only removes
/// choices (shell, files, exit-node, "allow any client", a reusable saved
/// identity) so that what is left is exactly "share one or more ports, for a
/// bounded time, to named clients, then it is gone".
/// </summary>
public sealed class TailcatTemporaryShareService : ITailcatTemporaryShareService, IDisposable
{
    private readonly ITailcatTemporaryShareStore _store;
    private readonly ITailcatHubService _hub;
    private readonly IEntitlementService _entitlements;
    private readonly TimeProvider _timeProvider;
    private readonly object _gate = new();
    private TailcatTemporaryShare? _active;
    private bool _revoking;

    public TailcatTemporaryShareService(
        ITailcatTemporaryShareStore store,
        ITailcatHubService hub,
        IEntitlementService entitlements,
        TimeProvider? timeProvider = null)
    {
        _store = store;
        _hub = hub;
        _entitlements = entitlements;
        _timeProvider = timeProvider ?? TimeProvider.System;
        // Reacting to the hub directly -- not only to our own CreateAsync/
        // RevokeAsync calls -- is what lets a panel left open notice a share
        // expiring on its own, and what catches a share stopped from the
        // general "Share this phone" panel instead of through RevokeAsync.
        _hub.Changed += OnHubChanged;
    }

    public event EventHandler? Changed;

    private void OnHubChanged(object? sender, EventArgs e)
    {
        bool changed;
        lock (_gate)
        {
            var before = _active;
            _ = Active; // forces the expiry re-check below to run under this lock pass too
            changed = !ReferenceEquals(before, _active);
        }
        if (changed) RaiseChanged();
    }

    public TailcatTemporaryShare? Active
    {
        get
        {
            lock (_gate)
            {
                // The hub's own snapshot is the only authoritative signal that
                // the underlying process is still alive. If it says the server
                // is gone -- lifetime elapsed, or stopped from elsewhere -- the
                // share is not active here even if nothing has told this
                // service to finalize it yet.
                // Skipped mid-RevokeAsync: that call finalizes the share itself,
                // as Revoked, once its own StopServerAsync call returns. Without
                // this guard, StopServerAsync's own Changed event -- raised
                // before RevokeAsync gets a chance to run -- would race it and
                // record every revoke as "Expired" instead.
                if (_active is not null && !_revoking && _hub.Snapshot.Server is null)
                {
                    FinalizeActiveUnsafe(TailcatTemporaryShareEndReason.Expired);
                }
                return _active;
            }
        }
    }

    public Task<IReadOnlyList<TailcatTemporaryShare>> GetHistoryAsync(CancellationToken cancellationToken = default) =>
        _store.GetAllAsync(cancellationToken);

    public async Task<TailcatTemporaryShare> CreateAsync(TailcatTemporaryShareRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!_entitlements.Has(PremiumFeature.TailcatPhoneServer))
            throw new InvalidOperationException("Temporary Tailcat sharing requires MeowSSH Pro.");

        var label = string.IsNullOrWhiteSpace(request.Label) ? "Temporary share" : request.Label.Trim();
        var targets = Normalize(request.ServeTargets);
        if (targets.Count == 0)
            throw new ArgumentException("Choose at least one service or port to share.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.AllowedClientKeys))
            throw new ArgumentException("At least one Tailcat client key is required. A temporary share narrows how long an address works, not who may use it.", nameof(request));
        if (request.Lifetime <= TimeSpan.Zero || request.Lifetime > TimeSpan.FromHours(24))
            throw new ArgumentOutOfRangeException(nameof(request), "A temporary share must expire between 1 second and 24 hours from now.");

        lock (_gate)
        {
            if (Active is not null)
                throw new InvalidOperationException("Revoke the active temporary share before starting another one.");
        }

        // EphemeralKey (PrivateKeyJson: null below) means Tailcat mints a brand
        // new identity for this call: the resulting address cannot be the same
        // one a previous, now-expired-or-revoked share handed out.
        await _hub.StartServerAsync(new TailcatServeRequest(
            Lifetime: request.Lifetime,
            AllowedClientKeys: request.AllowedClientKeys.Trim(),
            EnableShell: false,
            AuthorizedSshKeys: null,
            EnableFiles: false,
            SharedFolder: null,
            FileMode: "ro",
            EnableExitNode: false,
            AllowAnyClient: false,
            UseTailcatCredentialForShell: false,
            FullAddress: false,
            UsePresharedKey: true,
            DerpMapUrl: request.DerpMapUrl,
            PrivateKeyJson: null,
            ServeTargets: targets), cancellationToken).ConfigureAwait(false);

        var server = _hub.Snapshot.Server
            ?? throw new InvalidOperationException("Tailcat did not publish a server address for this share.");
        var share = new TailcatTemporaryShare(
            Guid.NewGuid(),
            label,
            targets,
            request.AllowedClientKeys.Trim(),
            server.Address,
            _timeProvider.GetUtcNow(),
            server.ExpiresAt,
            EndedAtUtc: null,
            EndReason: null);

        lock (_gate) _active = share;
        await _store.SaveAsync(share, cancellationToken).ConfigureAwait(false);
        RaiseChanged();
        return share;
    }

    public async Task RevokeAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (Active is null) return;
            _revoking = true;
        }

        try
        {
            // Kills the real OS process (SIGTERM/SIGKILL inside MeowshellServer),
            // the same call the general "Share this phone" panel's Stop button
            // makes. This is what makes revocation genuine rather than cosmetic:
            // the address stops accepting connections because nothing is
            // listening any more, not because a UI flag changed.
            await _hub.StopServerAsync(cancellationToken).ConfigureAwait(false);

            TailcatTemporaryShare? finalized;
            lock (_gate) finalized = FinalizeActiveUnsafe(TailcatTemporaryShareEndReason.Revoked);
            if (finalized is not null) await _store.SaveAsync(finalized, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate) _revoking = false;
        }
        RaiseChanged();
    }

    /// <summary>Must be called with <see cref="_gate"/> held. Returns the finalized record, or null if nothing was active.</summary>
    private TailcatTemporaryShare? FinalizeActiveUnsafe(TailcatTemporaryShareEndReason reason)
    {
        if (_active is null) return null;
        var ended = _active with { EndedAtUtc = _timeProvider.GetUtcNow(), EndReason = reason };
        _active = null;
        // Best-effort: persisting the end reason is a courtesy for the history
        // list, not what makes the share inactive. Active already reads null
        // by the time this line could ever throw.
        _ = _store.SaveAsync(ended, CancellationToken.None);
        return ended;
    }

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    private static IReadOnlyList<string> Normalize(IReadOnlyList<string>? targets) =>
        targets is null
            ? []
            : [.. targets.Select(static target => target?.Trim() ?? string.Empty).Where(static target => target.Length > 0).Distinct(StringComparer.Ordinal)];

    public void Dispose() => _hub.Changed -= OnHubChanged;
}

public sealed record TailcatTemporaryShareDocument(IReadOnlyList<TailcatTemporaryShare> Shares);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, WriteIndented = true)]
[JsonSerializable(typeof(TailcatTemporaryShareDocument))]
internal sealed partial class TailcatTemporaryShareJsonContext : JsonSerializerContext
{
}
