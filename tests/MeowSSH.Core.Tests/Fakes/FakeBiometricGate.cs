using MeowSSH.Core.Security;

namespace MeowSSH.Core.Tests.Fakes;

/// <summary>A biometric prompt that answers whatever the test tells it to.</summary>
internal sealed class FakeBiometricGate : IBiometricGate
{
    public BiometricAvailability Availability { get; set; } = BiometricAvailability.Available;

    public BiometricResult Result { get; set; } = BiometricResult.Succeeded;

    /// <summary>Every prompt raised, so a test can assert the user was asked once and with what wording.</summary>
    public List<BiometricPromptOptions> Prompts { get; } = [];

    public ValueTask<BiometricAvailability> GetAvailabilityAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Availability);

    public ValueTask<BiometricResult> AuthenticateAsync(
        BiometricPromptOptions options, CancellationToken cancellationToken = default)
    {
        Prompts.Add(options);
        return ValueTask.FromResult(Result);
    }
}

/// <summary>A device id that never changes, so sync bookkeeping is predictable.</summary>
internal sealed class FakeDeviceIdentity(string deviceId = "device-a") : MeowSSH.Core.Services.IDeviceIdentity
{
    public ValueTask<string> GetDeviceIdAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(deviceId);
}
