using MeowSSH.Core.Security;

namespace MeowSSH.Core.Ssh;

/// <summary>
/// The key material offered for one connection attempt.
/// </summary>
/// <remarks>
/// Passed per connection rather than configured on the engine, because keys come
/// out of the vault and should exist in memory only for as long as the handshake
/// needs them. The caller owns this and should dispose it as soon as the
/// connection is established.
/// </remarks>
public sealed class SshCredentials : IDisposable
{
    /// <summary>Private keys in OpenSSH format, decrypted from the vault.</summary>
    public IReadOnlyList<SecretBuffer> PrivateKeys { get; init; } = [];

    /// <summary>
    /// Certificates matching <see cref="PrivateKeys"/> by position, for servers
    /// that authenticate against a CA rather than an authorized_keys file.
    /// </summary>
    public IReadOnlyList<byte[]> Certificates { get; init; } = [];

    /// <summary>
    /// Identifiers for keys held in the device's hardware key store, which sign
    /// without the key ever being readable. Paired with their public keys.
    /// </summary>
    public IReadOnlyList<string> KeyStoreKeyIds { get; init; } = [];

    public IReadOnlyList<byte[]> KeyStorePublicKeys { get; init; } = [];

    /// <summary>Offers no keys; auth falls to a password or a challenge.</summary>
    public static SshCredentials None { get; } = new();

    /// <summary>Reads an OpenSSH private key file into a credential set.</summary>
    public static SshCredentials FromPrivateKeyFile(string path) =>
        new() { PrivateKeys = [SecretBuffer.TakeOwnershipOf(File.ReadAllBytes(path))] };

    public void Dispose()
    {
        foreach (var key in PrivateKeys) key.Dispose();
    }
}
