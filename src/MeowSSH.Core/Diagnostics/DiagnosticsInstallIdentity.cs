namespace MeowSSH.Core.Diagnostics;

/// <summary>
/// Mints and persists the random, anonymous id attached to a diagnostic report so a
/// receiving server can rate-limit and de-duplicate reports per install rather than
/// per IP. This id identifies nothing about the device or the person using it — see
/// the remarks on <see cref="DiagnosticsInstallIdentity"/> for why that matters and
/// why this is a separate file from every other per-install id in this codebase.
/// </summary>
public interface IDiagnosticsInstallIdStore
{
    /// <summary>Returns the current id, minting one on first use.</summary>
    Task<string> GetOrCreateAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes the stored id so the next <see cref="GetOrCreateAsync"/> mints a
    /// fresh one with no relationship to the old one.
    /// </summary>
    Task ClearAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Keeps the diagnostics install id in a small text file, mirroring
/// <c>FileDeviceIdentity</c>'s "one random value, written once, read forever" shape.
/// </summary>
/// <remarks>
/// This is deliberately its own file rather than a value derived from
/// <c>IDeviceIdentity</c> (see <see cref="DiagnosticsInstallIdentity"/>). It is also
/// not secret — like the vault's install seed, it identifies an install, not a
/// person — so it lives unencrypted next to the other diagnostics state.
/// </remarks>
public sealed class FileDiagnosticsInstallIdStore(string path) : IDiagnosticsInstallIdStore
{
    private readonly string _path = path ?? throw new ArgumentNullException(nameof(path));
    private string? _cached;

    public async Task<string> GetOrCreateAsync(CancellationToken cancellationToken = default)
    {
        if (_cached is not null) return _cached;

        if (File.Exists(_path))
        {
            var existing = (await File.ReadAllTextAsync(_path, cancellationToken).ConfigureAwait(false)).Trim();
            // Only accept a value shaped like the ones this store writes. A file
            // that fails to parse (truncated write, manual edit, future format
            // change) falls through to minting a fresh id below rather than
            // silently sending malformed or attacker-supplied text as the id.
            if (Guid.TryParseExact(existing, "D", out var parsed)) return _cached = parsed.ToString("D");
        }

        // Guid.NewGuid() draws from the OS CSPRNG, not from any device property —
        // that randomness, not the GUID format, is the actual privacy requirement.
        // Do not "improve" this into anything derived from IMEI, Android ID, MAC,
        // advertising ID or the account email: any of those turns an anonymous
        // counter into a pseudonymous device fingerprint with extra steps.
        var minted = Guid.NewGuid().ToString("D");
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(_path, minted, cancellationToken).ConfigureAwait(false);
        return _cached = minted;
    }

    public Task ClearAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _cached = null;
        try
        {
            if (File.Exists(_path)) File.Delete(_path);
        }
        catch (IOException)
        {
            // Best effort, same as the other diagnostics stores: a failed delete
            // here must not block the user's reset action or crash the settings
            // page. GetOrCreateAsync re-validates the file's shape on every call
            // it doesn't have a cached answer for, so a half-deleted file cannot
            // wedge the id in a bad state.
        }
        catch (UnauthorizedAccessException)
        {
        }
        return Task.CompletedTask;
    }
}

/// <summary>Persists whether the person has turned on sending anonymized diagnostics.</summary>
public interface IDiagnosticsSendPreferenceStore
{
    /// <summary>Off unless a stored value says otherwise — see <see cref="DiagnosticsInstallIdentity"/>.</summary>
    Task<bool> GetEnabledAsync(CancellationToken cancellationToken = default);

    Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default);
}

public sealed class FileDiagnosticsSendPreferenceStore(string path) : IDiagnosticsSendPreferenceStore
{
    private readonly string _path = path ?? throw new ArgumentNullException(nameof(path));

    public async Task<bool> GetEnabledAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_path)) return false;
        try
        {
            var text = (await File.ReadAllTextAsync(_path, cancellationToken).ConfigureAwait(false)).Trim();
            return text == "1";
        }
        catch (IOException)
        {
            // Same "fail closed to the private default" reasoning as GetEnabledAsync
            // returning false when the file is missing: an unreadable preference
            // file must never be interpreted as consent.
            return false;
        }
    }

    public async Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(_path, enabled ? "1" : "0", cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// The one thing a diagnostic report may carry that identifies the install it came
/// from, and the on/off switch that governs whether it exists at all.
/// </summary>
/// <remarks>
/// <para>
/// This is a second, independent id from <c>IDeviceIdentity</c>
/// (<c>MeowSSH.Core.Services.StoredVaultSession</c>), whose own doc comment calls it
/// "the stable per-install id that sync attributes edits to". That id is designed to
/// be linkable to one install's vault-sync history by construction — exactly what a
/// diagnostics id must not be. Deriving this one from that one, or from any device
/// property (IMEI, Android ID, MAC, advertising ID) or the account email, would let
/// whoever receives a diagnostic report correlate it with a user's hosts, keys or
/// Tailcat identity, which defeats the entire point of the identifier.
/// </para>
/// <para>
/// Enabling and disabling are wired to the id's lifetime, not just to whether it is
/// used, so the setting means what the settings page says: turning diagnostics off
/// deletes the id (<see cref="SetEnabledAsync"/>), and it stays off — no id is
/// minted — until the person turns it back on (<see cref="GetIdForReportAsync"/>
/// never calls <see cref="IDiagnosticsInstallIdStore.GetOrCreateAsync"/> while
/// disabled). Without that, "off" would just mean "stop attaching the id I already
/// generated", and a person who turned diagnostics off specifically to stop being
/// tracked by it would still have it sitting on disk, one toggle away from resuming
/// as the exact same install it always was.
/// </para>
/// </remarks>
public sealed class DiagnosticsInstallIdentity(
    IDiagnosticsInstallIdStore idStore,
    IDiagnosticsSendPreferenceStore preferenceStore)
{
    private readonly IDiagnosticsInstallIdStore _idStore = idStore ?? throw new ArgumentNullException(nameof(idStore));
    private readonly IDiagnosticsSendPreferenceStore _preferenceStore = preferenceStore ?? throw new ArgumentNullException(nameof(preferenceStore));

    public Task<bool> IsEnabledAsync(CancellationToken cancellationToken = default) =>
        _preferenceStore.GetEnabledAsync(cancellationToken);

    public async Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        await _preferenceStore.SetEnabledAsync(enabled, cancellationToken).ConfigureAwait(false);
        if (!enabled) await _idStore.ClearAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Clears the id without touching the enabled/disabled setting, for a person who
    /// wants a fresh id without turning diagnostics off and back on. Clearing app
    /// data does the same thing implicitly, since it deletes the underlying file.
    /// </summary>
    public Task ResetIdAsync(CancellationToken cancellationToken = default) =>
        _idStore.ClearAsync(cancellationToken);

    /// <summary>
    /// The id to attach to a diagnostic report, or null when diagnostics are off.
    /// Never mints an id while off, so a person who never turns this on leaves
    /// nothing on disk that could identify their install.
    /// </summary>
    public async Task<string?> GetIdForReportAsync(CancellationToken cancellationToken = default)
    {
        if (!await IsEnabledAsync(cancellationToken).ConfigureAwait(false)) return null;
        return await _idStore.GetOrCreateAsync(cancellationToken).ConfigureAwait(false);
    }
}
