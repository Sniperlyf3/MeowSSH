namespace MeowSSH.Core.Security;

public enum BiometricAvailability
{
    Available,
    /// <summary>The device has the hardware but the user has not enrolled anything.</summary>
    NotEnrolled,
    /// <summary>No biometric hardware, or it is permanently unavailable.</summary>
    NoHardware,
    /// <summary>Hardware exists but is temporarily unavailable — in use, or locked out after too many attempts.</summary>
    TemporarilyUnavailable,
}

public enum BiometricResult
{
    Succeeded,
    /// <summary>The user dismissed the prompt.</summary>
    Cancelled,
    /// <summary>Too many failed attempts; the sensor is locked out.</summary>
    LockedOut,
    Failed,
}

/// <summary>
/// Presents the platform's biometric prompt. On Android this is
/// <c>BiometricPrompt</c> with a <c>CryptoObject</c>, so a success is not merely
/// a boolean the app could be tricked into believing — it unlocks the Keystore
/// key itself.
/// </summary>
public interface IBiometricGate
{
    ValueTask<BiometricAvailability> GetAvailabilityAsync(CancellationToken cancellationToken = default);

    ValueTask<BiometricResult> AuthenticateAsync(BiometricPromptOptions options, CancellationToken cancellationToken = default);
}

/// <param name="Title">Headline shown on the system prompt.</param>
/// <param name="Subtitle">Optional second line, e.g. the vault being opened.</param>
/// <param name="Description">Optional body text.</param>
/// <param name="AllowDeviceCredential">
/// Let the user fall back to their PIN, pattern or password. Keep this on: a
/// user whose sensor fails in the cold should still reach their hosts.
/// </param>
public sealed record BiometricPromptOptions(
    string Title,
    string? Subtitle = null,
    string? Description = null,
    bool AllowDeviceCredential = true);
