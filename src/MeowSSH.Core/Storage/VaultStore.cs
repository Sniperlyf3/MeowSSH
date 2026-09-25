using System.Security.Cryptography;
using System.Text;
using MeowSSH.Core.Security;

namespace MeowSSH.Core.Storage;

/// <summary>
/// Owns the vault: the file, the master key while it is open, and the routes
/// back in.
/// </summary>
/// <remarks>
/// <para>
/// Every mutation goes through <c>UpdateAsync</c>, which bumps the
/// revision, re-seals both sections and writes the file before the change is
/// visible. There is no "save later": a host added and then lost to a crash is
/// worse than a save that costs a few milliseconds, and holding dirty state would
/// mean deciding what to do with it at lock time.
/// </para>
/// <para>
/// Two wrapped copies of the master key exist from the moment the vault is
/// created — the device's hardware key and the recovery code — because the first
/// one can be destroyed by something the user does elsewhere on the phone.
/// Adding a fingerprint retires the Keystore key by design, and without the
/// second copy that would take the vault with it.
/// </para>
/// </remarks>
public sealed class VaultStore(IVaultStorage storage, TimeProvider? timeProvider = null) : IDisposable
{
    /// <summary>The id of the device-key copy inside the file.</summary>
    public const string DeviceKeyId = "device";

    /// <summary>The id of the recovery-code copy inside the file.</summary>
    public const string RecoveryKeyId = "recovery";

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private VaultKeyRing? _keyRing;
    private VaultDocument? _document;
    private bool _disposed;

    public bool IsUnlocked => _keyRing is not null;

    /// <summary>The open vault.</summary>
    /// <exception cref="InvalidOperationException">The vault is locked.</exception>
    public VaultDocument Document =>
        _document ?? throw new InvalidOperationException("The vault is locked; unlock it before reading it.");

    /// <summary>Raised after the vault is opened, changed, or locked.</summary>
    public event EventHandler? Changed;

    /// <summary>Whether a vault has ever been created on this device.</summary>
    public async ValueTask<bool> ExistsAsync(CancellationToken cancellationToken = default) =>
        await storage.ReadAsync(cancellationToken).ConfigureAwait(false) is not null;

    /// <summary>
    /// Reads what the file says about itself without opening it, so the unlock
    /// screen can tell "no vault yet" from "vault whose device key is gone".
    /// </summary>
    public async ValueTask<VaultHeader?> ReadHeaderAsync(CancellationToken cancellationToken = default)
    {
        var bytes = await storage.ReadAsync(cancellationToken).ConfigureAwait(false);
        return bytes is null ? null : VaultFile.ReadHeader(bytes);
    }

    /// <summary>
    /// Creates a new vault and returns the recovery code, which is the only time
    /// it can ever be read.
    /// </summary>
    /// <remarks>
    /// The code is shown once and stored only as the key it wraps. Keeping it
    /// anywhere else would make it a second copy of the vault's password sitting
    /// beside the vault.
    /// </remarks>
    public async ValueTask<string> CreateAsync(
        IDeviceKeyStore keyStore,
        string deviceId,
        IKeyDerivation? kdf = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keyStore);
        ArgumentException.ThrowIfNullOrEmpty(deviceId);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (await storage.ReadAsync(cancellationToken).ConfigureAwait(false) is not null)
                throw new InvalidOperationException("A vault already exists on this device.");

            var keyRing = VaultKeyRing.CreateNew();
            try
            {
                await keyStore.CreateWrappingKeyAsync(requireUserAuthentication: true, cancellationToken).ConfigureAwait(false);
                var deviceKey = await keyRing.WrapWithDeviceKeyStoreAsync(DeviceKeyId, keyStore, cancellationToken).ConfigureAwait(false);

                var recoveryCode = RecoveryCode.Generate();
                using var recoveryBytes = RecoveryCode.Parse(recoveryCode);
                var recoveryKey = keyRing.WrapWithPassphrase(
                    RecoveryKeyId, recoveryBytes.ReadOnlySpan, kdf ?? new Argon2idKeyDerivation());

                var document = VaultDocument.CreateEmpty(deviceId)
                    .WithWrappedKey(deviceKey)
                    .WithWrappedKey(recoveryKey);

                await SaveAsync(document, keyRing, cancellationToken).ConfigureAwait(false);
                _keyRing = keyRing;
                keyRing = null;
                return recoveryCode;
            }
            finally
            {
                keyRing?.Dispose();
            }
        }
        finally
        {
            _gate.Release();
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Opens the vault with the device's hardware key, prompting for biometrics.</summary>
    /// <exception cref="DeviceKeyUnavailableException">The device key is gone or the user did not authenticate.</exception>
    public async ValueTask UnlockWithDeviceKeyAsync(IDeviceKeyStore keyStore, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keyStore);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var bytes = await RequireVaultAsync(cancellationToken).ConfigureAwait(false);
            var header = VaultFile.ReadHeader(bytes);
            var wrapped = header.WrappedKeys.FirstOrDefault(k => k.Method == KeyWrapMethod.DeviceKeyStore)
                ?? throw new DeviceKeyUnavailableException(
                    DeviceKeyUnavailableReason.NotProvisioned,
                    "This vault has no device key. Open it with your recovery code.");

            var keyRing = await VaultKeyRing.UnwrapWithDeviceKeyStoreAsync(wrapped, keyStore, cancellationToken).ConfigureAwait(false);
            Adopt(bytes, keyRing);
        }
        finally
        {
            _gate.Release();
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Opens the vault with the recovery code, for when the device key is gone.
    /// </summary>
    /// <exception cref="CryptographicException">The code is wrong.</exception>
    public async ValueTask UnlockWithRecoveryCodeAsync(string recoveryCode, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var bytes = await RequireVaultAsync(cancellationToken).ConfigureAwait(false);
            var header = VaultFile.ReadHeader(bytes);
            var wrapped = header.WrappedKeys.FirstOrDefault(k => k.Method == KeyWrapMethod.RecoveryPassphrase)
                ?? throw new CryptographicException("This vault has no recovery code.");

            using var codeBytes = RecoveryCode.Parse(recoveryCode);
            var keyRing = VaultKeyRing.UnwrapWithPassphrase(wrapped, codeBytes.ReadOnlySpan);
            Adopt(bytes, keyRing);
        }
        finally
        {
            _gate.Release();
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Re-wraps the master key with a freshly created device key, after the old
    /// one was retired.
    /// </summary>
    /// <remarks>
    /// This is what makes recovery a recovery rather than a one-time read: having
    /// got back in with the code, the user gets biometric unlock back too, instead
    /// of typing the code every time from then on.
    /// </remarks>
    public async ValueTask RebindDeviceKeyAsync(IDeviceKeyStore keyStore, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keyStore);
        RequireUnlocked();

        await keyStore.DeleteWrappingKeyAsync(cancellationToken).ConfigureAwait(false);
        await keyStore.CreateWrappingKeyAsync(requireUserAuthentication: true, cancellationToken).ConfigureAwait(false);
        var deviceKey = await _keyRing!.WrapWithDeviceKeyStoreAsync(DeviceKeyId, keyStore, cancellationToken).ConfigureAwait(false);

        await UpdateAsync(document => document.WithWrappedKey(deviceKey), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Replaces the recovery code with a new one and returns it.
    /// </summary>
    /// <remarks>
    /// Used after a recovery: the code that was written down has now been typed
    /// into a device, which is one more place it has existed than the user
    /// intended.
    /// </remarks>
    public async ValueTask<string> RotateRecoveryCodeAsync(IKeyDerivation? kdf = null, CancellationToken cancellationToken = default)
    {
        RequireUnlocked();

        var recoveryCode = RecoveryCode.Generate();
        using var recoveryBytes = RecoveryCode.Parse(recoveryCode);
        var recoveryKey = _keyRing!.WrapWithPassphrase(
            RecoveryKeyId, recoveryBytes.ReadOnlySpan, kdf ?? new Argon2idKeyDerivation());

        await UpdateAsync(document => document.WithWrappedKey(recoveryKey), cancellationToken).ConfigureAwait(false);
        return recoveryCode;
    }

    /// <summary>Applies a change and writes it out before it becomes visible.</summary>
    public async ValueTask UpdateAsync(Func<VaultDocument, VaultDocument> change, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(change);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireUnlocked();
            var updated = change(_document!);
            await SaveAsync(updated, _keyRing!, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Convenience for the common shape: change a record, stamped with the current time.</summary>
    public ValueTask UpdateAsync(Func<VaultDocument, DateTimeOffset, VaultDocument> change, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(change);
        var now = _time.GetUtcNow();
        return UpdateAsync(document => change(document, now), cancellationToken);
    }

    /// <summary>
    /// Folds another phone's copy of this vault into the open one (see
    /// <see cref="VaultMerge"/>) and saves the result if anything changed.
    /// </summary>
    /// <remarks>
    /// The other copy is opened with this vault's own master key, which every
    /// phone restored from the same recovery code shares. Nothing is written
    /// when that fails, so a foreign or damaged file can never replace records.
    /// </remarks>
    /// <returns>True when anything was merged in and saved.</returns>
    /// <exception cref="CryptographicException">The file is not a copy of this vault.</exception>
    /// <exception cref="VaultFormatException">The file is malformed, or from a newer app version.</exception>
    public async ValueTask<bool> MergeAsync(ReadOnlyMemory<byte> otherCopy, string deviceId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(deviceId);

        bool changed;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireUnlocked();
            var other = VaultFile.Read(otherCopy.Span, _keyRing!);
            (var merged, changed) = VaultMerge.Merge(_document!, other, deviceId);
            if (changed) await SaveAsync(merged, _keyRing!, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        // Only on a real change: sync listens for Changed to know there is
        // something to push, and a no-op merge announcing itself would have it
        // chase its own tail.
        if (changed) Changed?.Invoke(this, EventArgs.Empty);
        return changed;
    }

    /// <summary>Zeroes the master key and drops the decrypted records.</summary>
    public void Lock()
    {
        _keyRing?.Dispose();
        _keyRing = null;
        _document = null;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private async ValueTask<byte[]> RequireVaultAsync(CancellationToken cancellationToken) =>
        await storage.ReadAsync(cancellationToken).ConfigureAwait(false)
        ?? throw new InvalidOperationException("No vault exists on this device yet.");

    private void Adopt(byte[] bytes, VaultKeyRing keyRing)
    {
        try
        {
            _document = VaultFile.Read(bytes, keyRing);
            _keyRing?.Dispose();
            _keyRing = keyRing;
        }
        catch
        {
            // The key opened the wrapped master key but not the vault body, which
            // means the file is damaged rather than the unlock being wrong. Do not
            // leave a key ring behind for a vault that never loaded.
            keyRing.Dispose();
            throw;
        }
    }

    private async ValueTask SaveAsync(VaultDocument document, VaultKeyRing keyRing, CancellationToken cancellationToken)
    {
        var next = document with { Revision = document.Revision + 1 };
        await storage.WriteAsync(VaultFile.Write(next, keyRing), cancellationToken).ConfigureAwait(false);
        _document = next;
    }

    private void RequireUnlocked()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_keyRing is null || _document is null)
            throw new InvalidOperationException("The vault is locked; unlock it before changing it.");
    }

    /// <summary>
    /// A stable per-install identifier, derived from a random value the caller
    /// persists. Sync uses it to tell this device's edits from another's.
    /// </summary>
    public static string DeriveDeviceId(ReadOnlySpan<byte> installSeed)
    {
        var digest = SHA256.HashData(installSeed);
        return Convert.ToHexStringLower(digest.AsSpan(0, 8));
    }

    /// <summary>Generates the seed <see cref="DeriveDeviceId"/> takes. Not secret; just unique.</summary>
    public static byte[] NewInstallSeed() => RandomNumberGenerator.GetBytes(16);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _keyRing?.Dispose();
        _keyRing = null;
        _document = null;
        _gate.Dispose();
    }
}
