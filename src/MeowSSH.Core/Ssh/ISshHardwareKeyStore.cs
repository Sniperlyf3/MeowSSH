namespace MeowSSH.Core.Ssh;

/// <summary>Metadata for a platform-held SSH key whose private half cannot be exported.</summary>
public sealed record SshHardwareKeyInfo(
    string KeyId,
    string PublicKey,
    bool HardwareBacked,
    bool StrongBoxBacked);

/// <summary>
/// Creates and manages non-exportable SSH keys while also servicing Meowshell
/// signature requests through <see cref="ISshHardwareKeySigner"/>.
/// </summary>
public interface ISshHardwareKeyStore : ISshHardwareKeySigner
{
    ValueTask<bool> IsAvailableAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a P-256 SSH key. Implementations may prefer StrongBox when it is
    /// available, but must report the actual backing rather than assuming it.
    /// </summary>
    ValueTask<SshHardwareKeyInfo> GenerateP256Async(
        string keyId,
        CancellationToken cancellationToken = default);

    ValueTask<SshHardwareKeyInfo?> GetInfoAsync(
        string keyId,
        CancellationToken cancellationToken = default);

    ValueTask DeleteAsync(string keyId, CancellationToken cancellationToken = default);
}
