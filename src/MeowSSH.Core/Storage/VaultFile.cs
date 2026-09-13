using System.Globalization;
using System.Security.Cryptography;
using MeowSSH.Core.Model;
using MeowSSH.Core.Security;

namespace MeowSSH.Core.Storage;

/// <summary>
/// Turns a <see cref="VaultDocument"/> into the bytes that go on disk, and back.
/// </summary>
/// <remarks>
/// <para>
/// The file has a plaintext header and two sealed sections. The header holds only
/// what has to be readable before the vault is open: the schema version, the
/// device id, the save counter, and the wrapped copies of the master key. Each
/// wrapped key is already authenticated under its own wrapping key, so leaving
/// the header unencrypted gives an attacker the number of unlock routes and
/// nothing else.
/// </para>
/// <para>
/// Hosts and credentials are sealed as two blobs rather than row by row. Sealing
/// each row would publish how many hosts exist and roughly how long each label
/// is, on a file an attacker can watch change; sealing whole sections costs a
/// full rewrite per save, which on a phone's host list is nothing.
/// </para>
/// </remarks>
public static class VaultFile
{
    /// <summary>Identifies the file on sight, and stops an unrelated file from being read as an empty vault.</summary>
    private static ReadOnlySpan<byte> Magic => "MEOWVLT1"u8;

    private const string HostSection = "hosts";
    private const string CredentialSection = "credentials";

    public static byte[] Write(VaultDocument document, VaultKeyRing keyRing)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(keyRing);

        using var secretsKey = keyRing.DerivePurposeKey(VaultKeyPurpose.Secrets);

        using var hosts = new VaultWriter();
        hosts.WriteInt32(document.Hosts.Count);
        foreach (var host in document.Hosts) WriteHost(hosts, host);

        using var credentials = new VaultWriter();
        credentials.WriteInt32(document.Credentials.Count);
        foreach (var credential in document.Credentials) WriteCredential(credentials, credential);

        using var file = new VaultWriter();
        file.WriteRaw(Magic);
        file.WriteInt32(VaultDocument.SchemaVersion);
        file.WriteString(document.DeviceId);
        file.WriteInt64(document.Revision);

        file.WriteInt32(document.WrappedKeys.Count);
        foreach (var key in document.WrappedKeys) WriteWrappedKey(file, key);

        file.WriteBytes(VaultCrypto.Seal(
            secretsKey.ReadOnlySpan, hosts.ToArray(), SectionContext(HostSection, document.Revision)));
        file.WriteBytes(VaultCrypto.Seal(
            secretsKey.ReadOnlySpan, credentials.ToArray(), SectionContext(CredentialSection, document.Revision)));
        return file.ToArray();
    }

    /// <summary>
    /// Reads the header alone.
    /// </summary>
    /// <remarks>
    /// This is what runs before the vault is unlocked: it is the only way to find
    /// the wrapped key the device — or the recovery code — has to open, and it
    /// must work when the device key is gone.
    /// </remarks>
    public static VaultHeader ReadHeader(ReadOnlySpan<byte> bytes)
    {
        var reader = new VaultReader(bytes);
        return ReadHeader(ref reader);
    }

    /// <summary>Opens the sealed sections. Requires the unlocked key ring.</summary>
    /// <exception cref="CryptographicException">
    /// The key is wrong, or the file was tampered with — including a section
    /// lifted from an older copy of this same vault.
    /// </exception>
    public static VaultDocument Read(ReadOnlySpan<byte> bytes, VaultKeyRing keyRing)
    {
        ArgumentNullException.ThrowIfNull(keyRing);

        var reader = new VaultReader(bytes);
        var header = ReadHeader(ref reader);
        var sealedHosts = reader.ReadBytes();
        var sealedCredentials = reader.ReadBytes();

        using var secretsKey = keyRing.DerivePurposeKey(VaultKeyPurpose.Secrets);

        using var hostBytes = VaultCrypto.Open(
            secretsKey.ReadOnlySpan, sealedHosts, SectionContext(HostSection, header.Revision));
        var hostReader = new VaultReader(hostBytes.ReadOnlySpan);
        var hosts = new HostRecord[ReadCount(ref hostReader)];
        for (var i = 0; i < hosts.Length; i++) hosts[i] = ReadHost(ref hostReader);

        using var credentialBytes = VaultCrypto.Open(
            secretsKey.ReadOnlySpan, sealedCredentials, SectionContext(CredentialSection, header.Revision));
        var credentialReader = new VaultReader(credentialBytes.ReadOnlySpan);
        var credentials = new CredentialRecord[ReadCount(ref credentialReader)];
        for (var i = 0; i < credentials.Length; i++) credentials[i] = ReadCredential(ref credentialReader);

        return new VaultDocument
        {
            DeviceId = header.DeviceId,
            Revision = header.Revision,
            WrappedKeys = header.WrappedKeys,
            Hosts = hosts,
            Credentials = credentials,
        };
    }

    private static VaultHeader ReadHeader(ref VaultReader reader)
    {
        if (!reader.ReadRaw(Magic.Length).SequenceEqual(Magic))
            throw new VaultFormatException("This file is not a MeowSSH vault.");

        var schemaVersion = reader.ReadInt32();
        if (schemaVersion > VaultDocument.SchemaVersion)
            throw new VaultFormatException(
                $"This vault was written by a newer version of MeowSSH (schema {schemaVersion}). Update the app to open it.");

        var deviceId = reader.ReadString();
        var revision = reader.ReadInt64();

        var keyCount = reader.ReadInt32();
        // A wrapped key is allocated from this number before any of it is
        // authenticated, so an implausible header is refused rather than sized.
        if (keyCount is < 0 or > 64)
            throw new VaultFormatException("The vault header claims an implausible number of keys.");

        var keys = new WrappedVaultKey[keyCount];
        for (var i = 0; i < keyCount; i++) keys[i] = ReadWrappedKey(ref reader);

        return new VaultHeader(schemaVersion, deviceId, revision, keys);
    }

    private static int ReadCount(ref VaultReader reader)
    {
        var count = reader.ReadInt32();
        if (count is < 0 or > 100_000)
            throw new VaultFormatException("A vault section claims an implausible number of records.");
        return count;
    }

    private static byte[] SectionContext(string section, long revision) =>
        // The revision is the record id, so a section pasted in from an earlier
        // save of this same vault fails to authenticate against the current one.
        VaultCrypto.RecordContext(
            "vault.section." + section,
            revision.ToString(CultureInfo.InvariantCulture),
            VaultDocument.SchemaVersion);

    // Records -------------------------------------------------------------

    private static void WriteHost(VaultWriter writer, HostRecord host)
    {
        writer.WriteGuid(host.Id);
        writer.WriteString(host.Label);
        writer.WriteString(host.Address);
        writer.WriteInt32(host.Port);
        writer.WriteNullableString(host.Username);
        writer.WriteInt32((int)host.Transport);
        writer.WriteStringList(host.Tags);
        writer.WriteNullableGuid(host.JumpHostId);
        writer.WriteNullableGuid(host.CredentialId);
        writer.WriteNullableTimestamp(host.LastConnectedAt);
        writer.WriteInt64(host.Revision);
        writer.WriteTimestamp(host.UpdatedAt);
        writer.WriteNullableString(host.OriginDeviceId);
        writer.WriteNullableTimestamp(host.DeletedAt);
    }

    private static HostRecord ReadHost(ref VaultReader reader) => new()
    {
        Id = reader.ReadGuid(),
        Label = reader.ReadString(),
        Address = reader.ReadString(),
        Port = reader.ReadInt32(),
        Username = reader.ReadNullableString(),
        Transport = (SshTransport)reader.ReadInt32(),
        Tags = reader.ReadStringList(),
        JumpHostId = reader.ReadNullableGuid(),
        CredentialId = reader.ReadNullableGuid(),
        LastConnectedAt = reader.ReadNullableTimestamp(),
        Revision = reader.ReadInt64(),
        UpdatedAt = reader.ReadTimestamp(),
        OriginDeviceId = reader.ReadNullableString(),
        DeletedAt = reader.ReadNullableTimestamp(),
    };

    private static void WriteCredential(VaultWriter writer, CredentialRecord credential)
    {
        writer.WriteGuid(credential.Id);
        writer.WriteString(credential.Label);
        writer.WriteInt32((int)credential.Kind);
        writer.WriteNullableString(credential.Username);
        writer.WriteBytes(credential.Secret);
        writer.WriteNullableBytes(credential.Passphrase);
        writer.WriteNullableString(credential.PublicKey);
        writer.WriteInt64(credential.Revision);
        writer.WriteTimestamp(credential.UpdatedAt);
        writer.WriteNullableString(credential.OriginDeviceId);
        writer.WriteNullableTimestamp(credential.DeletedAt);
    }

    private static CredentialRecord ReadCredential(ref VaultReader reader) => new()
    {
        Id = reader.ReadGuid(),
        Label = reader.ReadString(),
        Kind = (CredentialKind)reader.ReadInt32(),
        Username = reader.ReadNullableString(),
        Secret = reader.ReadBytes().ToArray(),
        Passphrase = reader.ReadNullableBytes(),
        PublicKey = reader.ReadNullableString(),
        Revision = reader.ReadInt64(),
        UpdatedAt = reader.ReadTimestamp(),
        OriginDeviceId = reader.ReadNullableString(),
        DeletedAt = reader.ReadNullableTimestamp(),
    };

    private static void WriteWrappedKey(VaultWriter writer, WrappedVaultKey key)
    {
        writer.WriteString(key.Id);
        writer.WriteInt32((int)key.Method);
        writer.WriteBytes(key.Payload);
        writer.WriteBoolean(key.Kdf is not null);
        if (key.Kdf is not null)
        {
            writer.WriteInt32((int)key.Kdf.Algorithm);
            writer.WriteInt32(key.Kdf.MemoryKiB);
            writer.WriteInt32(key.Kdf.Iterations);
            writer.WriteInt32(key.Kdf.Parallelism);
        }
        writer.WriteNullableBytes(key.Salt);
    }

    private static WrappedVaultKey ReadWrappedKey(ref VaultReader reader)
    {
        var id = reader.ReadString();
        var method = (KeyWrapMethod)reader.ReadInt32();
        var payload = reader.ReadBytes().ToArray();

        KdfParameters? kdf = null;
        if (reader.ReadBoolean())
        {
            var algorithm = (KdfAlgorithm)reader.ReadInt32();
            var memory = reader.ReadInt32();
            var iterations = reader.ReadInt32();
            var parallelism = reader.ReadInt32();
            kdf = new KdfParameters(algorithm, memory, iterations, parallelism);
        }

        return new WrappedVaultKey(id, method, payload, kdf, reader.ReadNullableBytes());
    }
}

/// <summary>What can be read from a vault file without unlocking it.</summary>
public sealed record VaultHeader(
    int SchemaVersion, string DeviceId, long Revision, IReadOnlyList<WrappedVaultKey> WrappedKeys);
