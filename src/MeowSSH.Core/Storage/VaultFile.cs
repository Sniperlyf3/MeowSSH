using System.Globalization;
using System.Security.Cryptography;
using MeowSSH.Core.Model;
using MeowSSH.Core.Security;

namespace MeowSSH.Core.Storage;

/// <summary>Turns a <see cref="VaultDocument"/> into the encrypted bytes stored on disk, and back.</summary>
public static class VaultFile
{
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
            secretsKey.ReadOnlySpan,
            hosts.ToArray(),
            SectionContext(HostSection, document.Revision, VaultDocument.SchemaVersion)));
        file.WriteBytes(VaultCrypto.Seal(
            secretsKey.ReadOnlySpan,
            credentials.ToArray(),
            SectionContext(CredentialSection, document.Revision, VaultDocument.SchemaVersion)));
        return file.ToArray();
    }

    public static VaultHeader ReadHeader(ReadOnlySpan<byte> bytes)
    {
        var reader = new VaultReader(bytes);
        return ReadHeader(ref reader);
    }

    public static VaultDocument Read(ReadOnlySpan<byte> bytes, VaultKeyRing keyRing)
    {
        ArgumentNullException.ThrowIfNull(keyRing);

        var reader = new VaultReader(bytes);
        var header = ReadHeader(ref reader);
        var sealedHosts = reader.ReadBytes();
        var sealedCredentials = reader.ReadBytes();

        using var secretsKey = keyRing.DerivePurposeKey(VaultKeyPurpose.Secrets);

        using var hostBytes = VaultCrypto.Open(
            secretsKey.ReadOnlySpan,
            sealedHosts,
            SectionContext(HostSection, header.Revision, header.SchemaVersion));
        var hostReader = new VaultReader(hostBytes.ReadOnlySpan);
        var hosts = new HostRecord[ReadCount(ref hostReader)];
        for (var i = 0; i < hosts.Length; i++) hosts[i] = ReadHost(ref hostReader, header.SchemaVersion);

        using var credentialBytes = VaultCrypto.Open(
            secretsKey.ReadOnlySpan,
            sealedCredentials,
            SectionContext(CredentialSection, header.Revision, header.SchemaVersion));
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
        if (schemaVersion is < 1 or > VaultDocument.SchemaVersion)
            throw new VaultFormatException(
                schemaVersion > VaultDocument.SchemaVersion
                    ? $"This vault was written by a newer version of MeowSSH (schema {schemaVersion}). Update the app to open it."
                    : $"Unsupported vault schema {schemaVersion}.");

        var deviceId = reader.ReadString();
        var revision = reader.ReadInt64();

        var keyCount = reader.ReadInt32();
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

    private static byte[] SectionContext(string section, long revision, int schemaVersion) =>
        VaultCrypto.RecordContext(
            "vault.section." + section,
            revision.ToString(CultureInfo.InvariantCulture),
            schemaVersion);

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
        writer.WriteInt32((int)host.Protocol);
        writer.WriteBoolean(host.AutoReconnect);
        writer.WriteInt32(host.SerialBaudRate);
        writer.WriteInt32(host.SerialDataBits);
        writer.WriteInt32((int)host.SerialStopBits);
        writer.WriteInt32((int)host.SerialParity);
        writer.WriteNullableString(host.ProxyUrl);
        writer.WriteBoolean(host.ForwardAgent);
        writer.WriteNullableString(host.Group);
        writer.WriteBoolean(host.IsFavorite);
    }

    private static HostRecord ReadHost(ref VaultReader reader, int schemaVersion)
    {
        var id = reader.ReadGuid();
        var label = reader.ReadString();
        var address = reader.ReadString();
        var port = reader.ReadInt32();
        var username = reader.ReadNullableString();
        var transport = (SshTransport)reader.ReadInt32();
        var tags = reader.ReadStringList();
        var jumpHostId = reader.ReadNullableGuid();
        var credentialId = reader.ReadNullableGuid();
        var lastConnectedAt = reader.ReadNullableTimestamp();
        var revision = reader.ReadInt64();
        var updatedAt = reader.ReadTimestamp();
        var originDeviceId = reader.ReadNullableString();
        var deletedAt = reader.ReadNullableTimestamp();

        var protocol = HostProtocol.Ssh;
        var autoReconnect = true;
        var serialBaudRate = 115200;
        var serialDataBits = 8;
        var serialStopBits = SerialStopBits.One;
        var serialParity = SerialParity.None;
        if (schemaVersion >= 2)
        {
            protocol = (HostProtocol)reader.ReadInt32();
            if (!Enum.IsDefined(protocol))
                throw new VaultFormatException($"A host contains unsupported protocol value {(int)protocol}.");
            autoReconnect = reader.ReadBoolean();
            serialBaudRate = reader.ReadInt32();
            serialDataBits = reader.ReadInt32();
            serialStopBits = (SerialStopBits)reader.ReadInt32();
            serialParity = (SerialParity)reader.ReadInt32();
            if (!Enum.IsDefined(serialStopBits) || !Enum.IsDefined(serialParity))
                throw new VaultFormatException("A host contains unsupported serial line settings.");
        }

        string? proxyUrl = null;
        var forwardAgent = false;
        if (schemaVersion >= 3)
        {
            proxyUrl = reader.ReadNullableString();
            forwardAgent = reader.ReadBoolean();
        }

        string? group = null;
        var isFavorite = false;
        if (schemaVersion >= 4)
        {
            group = reader.ReadNullableString();
            isFavorite = reader.ReadBoolean();
        }

        return new HostRecord
        {
            Id = id,
            Label = label,
            Address = address,
            Port = port,
            Username = username,
            Transport = transport,
            Protocol = protocol,
            AutoReconnect = autoReconnect,
            ProxyUrl = proxyUrl,
            ForwardAgent = forwardAgent,
            Group = group,
            IsFavorite = isFavorite,
            SerialBaudRate = serialBaudRate,
            SerialDataBits = serialDataBits,
            SerialStopBits = serialStopBits,
            SerialParity = serialParity,
            Tags = tags,
            JumpHostId = jumpHostId,
            CredentialId = credentialId,
            LastConnectedAt = lastConnectedAt,
            Revision = revision,
            UpdatedAt = updatedAt,
            OriginDeviceId = originDeviceId,
            DeletedAt = deletedAt,
        };
    }

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
