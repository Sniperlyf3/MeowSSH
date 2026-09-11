namespace MeowSSH.Core.Services;

public enum VaultState
{
    /// <summary>No vault exists yet on this device.</summary>
    NotCreated,

    /// <summary>A vault exists but its key is not in memory.</summary>
    Locked,

    Unlocked,

    /// <summary>
    /// The device key is gone — usually because biometric enrolment changed —
    /// so only the recovery code can reopen the vault.
    /// </summary>
    NeedsRecovery,
}

/// <summary>
/// Owns the unlocked vault key for the lifetime of a foreground session.
/// </summary>
/// <remarks>
/// Locking is not a UI state: it zeroes the key material. Anything holding a
/// derived key past a lock is a bug, which is why nothing here hands the key out
/// — callers ask the session to perform an operation instead.
/// </remarks>
public interface IVaultSession
{
    VaultState State { get; }

    /// <summary>How long the app may sit in the background before it locks itself.</summary>
    TimeSpan AutoLockAfter { get; }

    /// <summary>Prompts for biometrics and unlocks the vault.</summary>
    ValueTask<VaultUnlockResult> UnlockAsync(CancellationToken cancellationToken = default);

    /// <summary>Reopens the vault with the user's recovery code.</summary>
    ValueTask<VaultUnlockResult> UnlockWithRecoveryCodeAsync(string recoveryCode, CancellationToken cancellationToken = default);

    /// <summary>Zeroes the in-memory key. Safe to call when already locked.</summary>
    ValueTask LockAsync();

    event EventHandler? StateChanged;
}

/// <param name="Succeeded">Whether the vault is now unlocked.</param>
/// <param name="State">Where the vault ended up.</param>
/// <param name="Message">
/// What to tell the user when it did not work. Written for a person, not copied
/// from an exception.
/// </param>
public sealed record VaultUnlockResult(bool Succeeded, VaultState State, string? Message = null)
{
    public static VaultUnlockResult Success() => new(true, VaultState.Unlocked);
    public static VaultUnlockResult Failed(VaultState state, string message) => new(false, state, message);
}
