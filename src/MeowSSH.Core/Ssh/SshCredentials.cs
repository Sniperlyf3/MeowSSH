using MeowSSH.Core.Security;

namespace MeowSSH.Core.Ssh;

/// <summary>
/// The authentication material offered for one connection attempt.
/// </summary>
/// <remarks>
/// Passed per connection rather than configured on the engine, because secrets
/// come out of the vault and should exist in memory only for as long as the
/// handshake needs them. The caller owns this and should dispose it as soon as
/// the connection is established.
/// </remarks>
public sealed class SshCredentials : IDisposable
{
    /// <summary>
    /// Username attached to the selected credential. When present it takes
    /// precedence over the host's own username for this connection.
    /// </summary>
    public string? Username { get; init; }

    /// <summary>Stored password for password authentication.</summary>
    public SecretBuffer? Password { get; init; }

    /// <summary>Passphrase for an encrypted private key, when one is stored.</summary>
    public SecretBuffer? KeyPassphrase { get; init; }

    /// <summary>Private keys in OpenSSH format, decrypted from the vault.</summary>
    public IReadOnlyList<SecretBuffer> PrivateKeys { get; init; } = [];

    /// <summary>
    /// Certificates matching <see cref="PrivateKeys"/> by position, for servers
    /// that authenticate against a CA rather than an authorized_keys file.
    /// </summary>
    public IReadOnlyList<byte[]> Certificates { get; init; } = [];

    /// <summary>
    /// Identifiers for keys held in the device's hardware key store, which sign
    /// without the key ever being readable. Paired with their SSH wire-format public keys.
    /// </summary>
    public IReadOnlyList<string> KeyStoreKeyIds { get; init; } = [];

    public IReadOnlyList<byte[]> KeyStorePublicKeys { get; init; } = [];

    /// <summary>
    /// Platform signer used only when Meowshell asks one of <see cref="KeyStoreKeyIds"/>
    /// to sign an authentication challenge. Private key material never enters this object.
    /// </summary>
    public ISshHardwareKeySigner? HardwareKeySigner { get; init; }

    /// <summary>Offers no stored authentication material.</summary>
    public static SshCredentials None { get; } = new();

    /// <summary>Reads an OpenSSH private key file into a credential set.</summary>
    public static SshCredentials FromPrivateKeyFile(string path) =>
        new() { PrivateKeys = [SecretBuffer.TakeOwnershipOf(File.ReadAllBytes(path))] };

    public void Dispose()
    {
        Password?.Dispose();
        KeyPassphrase?.Dispose();
        foreach (var key in PrivateKeys) key.Dispose();
    }
}
