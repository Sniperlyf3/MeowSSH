using MeowSSH.Core.Security;
using MeowSSH.Core.Services;

namespace MeowSSH.TestHost.Fakes;

/// <summary>
/// Stands in for the Android Keystore and BiometricPrompt, which have no Linux
/// equivalent, so the unlock flow can be exercised in a browser.
/// </summary>
public sealed class FakeVaultSession : IVaultSession
{
    private readonly string _recoveryCode;

    public FakeVaultSession(VaultState initialState = VaultState.Locked, string recoveryCode = "")
    {
        State = initialState;
        _recoveryCode = recoveryCode;
    }

    public VaultState State { get; private set; }

    public TimeSpan AutoLockAfter => TimeSpan.FromMinutes(2);

    /// <summary>Set to make the next biometric attempt fail, as a cancelled prompt would.</summary>
    public BiometricResult NextBiometricResult { get; set; } = BiometricResult.Succeeded;

    public event EventHandler? StateChanged;

    public ValueTask<VaultUnlockResult> UnlockAsync(CancellationToken cancellationToken = default)
    {
        if (State == VaultState.NeedsRecovery)
            return ValueTask.FromResult(VaultUnlockResult.Failed(State,
                "Biometric unlock is unavailable on this device. Enter your recovery code."));

        return ValueTask.FromResult(NextBiometricResult switch
        {
            BiometricResult.Succeeded => Transition(VaultState.Unlocked, VaultUnlockResult.Success()),
            BiometricResult.Cancelled => VaultUnlockResult.Failed(State, "Unlock cancelled."),
            BiometricResult.LockedOut => VaultUnlockResult.Failed(State,
                "Too many failed attempts. Wait a moment, or use your recovery code."),
            _ => VaultUnlockResult.Failed(State, "Biometric check failed. Try again."),
        });
    }

    public ValueTask<VaultUnlockResult> UnlockWithRecoveryCodeAsync(string recoveryCode, CancellationToken cancellationToken = default)
    {
        if (!RecoveryCode.IsWellFormed(recoveryCode))
            return ValueTask.FromResult(VaultUnlockResult.Failed(State, "That code is not complete. Check for a missing character."));

        var matches = string.Equals(
            recoveryCode.Replace("-", "").Replace(" ", ""),
            _recoveryCode.Replace("-", "").Replace(" ", ""),
            StringComparison.OrdinalIgnoreCase);

        return ValueTask.FromResult(matches
            ? Transition(VaultState.Unlocked, VaultUnlockResult.Success())
            : VaultUnlockResult.Failed(State, "That recovery code does not match this vault."));
    }

    public ValueTask LockAsync()
    {
        State = VaultState.Locked;
        StateChanged?.Invoke(this, EventArgs.Empty);
        return ValueTask.CompletedTask;
    }

    private VaultUnlockResult Transition(VaultState state, VaultUnlockResult result)
    {
        State = state;
        StateChanged?.Invoke(this, EventArgs.Empty);
        return result;
    }
}
