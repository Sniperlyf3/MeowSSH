namespace MeowSSH.E2E;

/// <summary>The real OpenSSH server a run connects to, and how to log in to it.</summary>
/// <param name="Host">As this device sees it: 127.0.0.1 on Linux, 10.0.2.2 from an emulator.</param>
/// <param name="Port">The sshd port.</param>
/// <param name="Username">A dedicated, unprivileged test user.</param>
/// <param name="Password">That user's password.</param>
/// <param name="ClientPrivateKey">An unencrypted OpenSSH private key already in the user's authorized_keys.</param>
/// <param name="HostPublicKey">The server's host key, "type base64", for seeding known_hosts.</param>
public sealed record E2EServer(
    string Host,
    int Port,
    string Username,
    string Password,
    string ClientPrivateKey,
    string HostPublicKey)
{
    /// <summary>The host key's fingerprint as OpenSSH prints it: "SHA256:" and unpadded base64.</summary>
    public string HostKeyFingerprint =>
        "SHA256:" + Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(
            Convert.FromBase64String(HostPublicKey.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1]))).TrimEnd('=');

    /// <summary>
    /// The base64 public-key blob of <see cref="ClientPrivateKey"/>, as it
    /// appears in authorized_keys or "ssh-add -L". Read from the private key
    /// file's own public-key section (unencrypted in every OpenSSH key).
    /// </summary>
    public string ClientPublicKeyBody
    {
        get
        {
            var body = string.Concat(ClientPrivateKey.Split('\n')
                .Select(l => l.Trim())
                .Where(l => l.Length > 0 && !l.StartsWith("-----", StringComparison.Ordinal)));
            var bytes = Convert.FromBase64String(body);
            var offset = "openssh-key-v1\0"u8.Length;
            int ReadLength() { var n = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(offset)); offset += 4; return n; }
            for (var i = 0; i < 3; i++) { var skip = ReadLength(); offset += skip; } // cipher, kdf, kdf options
            offset += 4; // key count
            var length = ReadLength();
            return Convert.ToBase64String(bytes, offset, length);
        }
    }

    /// <summary>Instrumentation/environment keys, shared by the CI script and both runners.</summary>
    public static class Keys
    {
        public const string Host = "e2e_host";
        public const string Port = "e2e_port";
        public const string Username = "e2e_user";
        public const string Password = "e2e_password";
        public const string ClientKeyBase64 = "e2e_client_key_b64";
        public const string HostKey = "e2e_host_key";
    }

    /// <summary>
    /// Reads the server from key/value pairs (instrumentation arguments or
    /// environment). Returns null only when none were given, so a partly
    /// configured run fails loudly instead of quietly running nothing.
    /// </summary>
    public static E2EServer? From(Func<string, string?> get)
    {
        string[] all = [Keys.Host, Keys.Port, Keys.Username, Keys.Password, Keys.ClientKeyBase64, Keys.HostKey];
        var present = all.Where(k => !string.IsNullOrWhiteSpace(get(k))).ToArray();
        if (present.Length == 0) return null;
        if (present.Length != all.Length)
            throw new InvalidOperationException(
                "E2E server partly configured; missing: " + string.Join(", ", all.Except(present)));

        if (!int.TryParse(get(Keys.Port), out var port) || port is <= 0 or > 65535)
            throw new InvalidOperationException($"{Keys.Port} is not a port: '{get(Keys.Port)}'");

        return new E2EServer(
            get(Keys.Host)!,
            port,
            get(Keys.Username)!,
            get(Keys.Password)!,
            System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(get(Keys.ClientKeyBase64)!)),
            get(Keys.HostKey)!.Trim());
    }
}
