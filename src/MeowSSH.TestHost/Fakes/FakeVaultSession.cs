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
        // A blank code would make the setup screen render an empty panel, which
        // is the one thing the first-run flow must never do.
        _recoveryCode = string.IsNullOrEmpty(recoveryCode) ? RecoveryCode.Generate() : recoveryCode;
    }

    public VaultState State { get; private set; }

    public TimeSpan AutoLockAfter => TimeSpan.FromMinutes(2);

    /// <summary>Set to make the next biometric attempt fail, as a cancelled prompt would.</summary>
    public BiometricResult NextBiometricResult { get; set; } = BiometricResult.Succeeded;

    public event EventHandler? StateChanged;

    public ValueTask InitializeAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

    /// <summary>
    /// Puts the vault into a starting state for a scenario.
    /// </summary>
    /// <remarks>
    /// Not on <see cref="IVaultSession"/>, and it should not be: nothing in the
    /// app may decide the vault has stopped existing. The playground needs it
    /// because a first run and a returning user are different screens and a
    /// browser test reaches either by navigating.
    /// </remarks>
    public void Reset(VaultState state)
    {
        if (State == state) return;
        State = state;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public ValueTask<VaultSetupResult> CreateAsync(CancellationToken cancellationToken = default)
    {
        if (State != VaultState.NotCreated)
            return ValueTask.FromResult(VaultSetupResult.Failed("A vault already exists on this device."));

        if (NextBiometricResult != BiometricResult.Succeeded)
            return ValueTask.FromResult(VaultSetupResult.Failed("Unlock cancelled."));

        State = VaultState.Unlocked;
        StateChanged?.Invoke(this, EventArgs.Empty);
        return ValueTask.FromResult(VaultSetupResult.Success(_recoveryCode));
    }

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
