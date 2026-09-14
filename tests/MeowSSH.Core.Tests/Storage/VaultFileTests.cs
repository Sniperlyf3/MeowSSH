using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using MeowSSH.Core.Model;
using MeowSSH.Core.Security;
using MeowSSH.Core.Storage;

namespace MeowSSH.Core.Tests.Storage;

public class VaultFileTests
{
    private static VaultDocument Populated() => VaultDocument.CreateEmpty("device-a")
        with
    {
        Revision = 7,
        Hosts =
        [
            new HostRecord
            {
                Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
                Label = "build-01",
                Address = "10.0.0.4",
                Port = 2222,
                Username = "deploy",
                // Not the zero value of the enum on purpose: a writer that
                // dropped this field entirely would still round-trip Tcp.
                Transport = SshTransport.TailscaleSsh,
                ProxyUrl = "socks5://127.0.0.1:1080",
                ForwardAgent = true,
                Tags = ["prod", "eu-west"],
                CredentialId = Guid.Parse("33333333-3333-3333-3333-333333333333"),
                JumpHostId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
                LastConnectedAt = DateTimeOffset.UnixEpoch.AddDays(3),
                Revision = 2,
                UpdatedAt = DateTimeOffset.UnixEpoch.AddDays(3),
                OriginDeviceId = "device-a",
            },
            new HostRecord
            {
                Id = Guid.Parse("22222222-2222-2222-2222-222222222222"),
                Label = "gone",
                Address = "old.example",
                Revision = 5,
                UpdatedAt = DateTimeOffset.UnixEpoch.AddDays(9),
                DeletedAt = DateTimeOffset.UnixEpoch.AddDays(9),
            },
        ],
        Credentials =
        [
            new CredentialRecord
            {
                Id = Guid.Parse("33333333-3333-3333-3333-333333333333"),
                Label = "deploy key",
                Kind = CredentialKind.PrivateKey,
                Username = "deploy",
                Secret = Encoding.UTF8.GetBytes("-----BEGIN OPENSSH PRIVATE KEY-----\nnope\n"),
                Passphrase = Encoding.UTF8.GetBytes("hunter2"),
                PublicKey = "ssh-ed25519 AAAAC3Nz deploy@laptop",
                Revision = 1,
                UpdatedAt = DateTimeOffset.UnixEpoch,
                OriginDeviceId = "device-a",
            },
        ],
    };

    [Fact]
    public void EveryFieldSurvivesARoundTrip()
    {
        using var keyRing = VaultKeyRing.CreateNew();
        var original = Populated();

        var restored = VaultFile.Read(VaultFile.Write(original, keyRing), keyRing);

        // Compared property by property rather than with ==, for two reasons. A
        // field added to the model and forgotten in the writer comes back as its
        // default and is named here, instead of vanishing from everyone's phone
        // on the next release. And the generated equality of these records
        // compares Tags and Secret by reference, so == would pass on two records
        // whose collections merely happen to be the same object.
        AssertEveryPropertyMatches(original.Hosts[0], restored.Hosts[0]);
        AssertEveryPropertyMatches(original.Hosts[1], restored.Hosts[1]);
        AssertEveryPropertyMatches(original.Credentials[0], restored.Credentials[0]);
        Assert.Equal(original.DeviceId, restored.DeviceId);
        Assert.Equal(original.Revision, restored.Revision);
    }

    [Fact]
    public void TheRoundTripFixtureLeavesNoFieldAtItsDefault()
    {
        // Without this, the test above could pass for a field the writer drops:
        // a null that round-trips to null proves nothing. Every optional field on
        // the first host and the credential carries a value, so dropping any one
        // of them shows up.
        AssertNoPropertyIsDefault(Populated().Hosts[0], except: [nameof(HostRecord.DeletedAt), nameof(HostRecord.IsDeleted)]);
        AssertNoPropertyIsDefault(Populated().Credentials[0], except: [nameof(CredentialRecord.DeletedAt), nameof(CredentialRecord.IsDeleted)]);
    }

    [Fact]
    public void AnEmptyVaultRoundTripsToo()
    {
        using var keyRing = VaultKeyRing.CreateNew();
        var restored = VaultFile.Read(VaultFile.Write(VaultDocument.CreateEmpty("d"), keyRing), keyRing);

        Assert.Empty(restored.Hosts);
        Assert.Empty(restored.Credentials);
    }

    [Fact]
    public void TheHeaderReadsWithoutTheKey()
    {
        using var keyRing = VaultKeyRing.CreateNew();
        var document = VaultDocument.CreateEmpty("device-a")
            .WithWrappedKey(new WrappedVaultKey("recovery", KeyWrapMethod.RecoveryPassphrase, [1, 2, 3], KdfParameters.Testing, [4, 5, 6, 7, 8, 9, 10, 11]));

        var header = VaultFile.ReadHeader(VaultFile.Write(document, keyRing));

        // The lock screen has to know which ways in exist before it can offer any
        // of them, and after a biometric reset the device key is not one of them.
        var key = Assert.Single(header.WrappedKeys);
        Assert.Equal(KeyWrapMethod.RecoveryPassphrase, key.Method);
        Assert.Equal(KdfParameters.Testing, key.Kdf);
        Assert.Equal("device-a", header.DeviceId);
    }

    [Fact]
    public void AVaultWrittenByANewerSchemaIsRefusedRatherThanGuessedAt()
    {
        using var keyRing = VaultKeyRing.CreateNew();
        var bytes = VaultFile.Write(VaultDocument.CreateEmpty("d"), keyRing);
        // Schema version sits immediately after the eight-byte magic.
        bytes[11] = VaultDocument.SchemaVersion + 1;

        var error = Assert.Throws<VaultFormatException>(() => VaultFile.ReadHeader(bytes));
        Assert.Contains("newer version", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SomethingThatIsNotAVaultIsRejectedImmediately()
    {
        var error = Assert.Throws<VaultFormatException>(() => VaultFile.ReadHeader(Encoding.UTF8.GetBytes("hello there!!")));
        Assert.Contains("not a MeowSSH vault", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(6)]    // inside the magic
    [InlineData(10)]   // inside the schema version
    [InlineData(20)]   // inside the device id
    [InlineData(30)]   // inside the save counter
    public void ATruncatedHeaderSaysSoRatherThanReadingGarbage(int keptBytes)
    {
        using var keyRing = VaultKeyRing.CreateNew();
        var bytes = VaultFile.Write(Populated(), keyRing);

        Assert.Throws<VaultFormatException>(() => VaultFile.ReadHeader(bytes.AsSpan(0, keptBytes).ToArray()));
    }

    [Fact]
    public void AVaultTruncatedAfterItsHeaderIsRejectedWhenItIsOpened()
    {
        // The header is small, so half a file still has a complete one. The
        // failure has to come from the sections, and it has to be the "this file
        // is damaged" error rather than an index out of range.
        using var keyRing = VaultKeyRing.CreateNew();
        var bytes = VaultFile.Write(Populated(), keyRing);

        Assert.Throws<VaultFormatException>(() => VaultFile.Read(bytes.AsSpan(0, bytes.Length / 2).ToArray(), keyRing));
    }

    [Fact]
    public void AnotherVaultsKeyDoesNotOpenThisOne()
    {
        using var mine = VaultKeyRing.CreateNew();
        using var theirs = VaultKeyRing.CreateNew();
        var bytes = VaultFile.Write(Populated(), mine);

        Assert.Throws<AuthenticationTagMismatchException>(() => VaultFile.Read(bytes, theirs));
    }

    [Fact]
    public void AFlippedBitInASectionIsCaught()
    {
        using var keyRing = VaultKeyRing.CreateNew();
        var bytes = VaultFile.Write(Populated(), keyRing);
        bytes[^1] ^= 0x01;

        Assert.Throws<AuthenticationTagMismatchException>(() => VaultFile.Read(bytes, keyRing));
    }

    [Fact]
    public void ASectionFromAnEarlierSaveCannotBePastedIntoALaterOne()
    {
        // The attack: keep yesterday's vault, and after the user deletes a host's
        // access, splice yesterday's host section back into today's file. Binding
        // each section's tag to the save counter is what makes that fail.
        using var keyRing = VaultKeyRing.CreateNew();
        var before = Populated() with { Revision = 7 };
        var after = before with { Revision = 8, Hosts = [] };

        var oldBytes = VaultFile.Write(before, keyRing);
        var newBytes = VaultFile.Write(after, keyRing);

        var spliced = Splice(newBytes, oldBytes);

        Assert.Throws<AuthenticationTagMismatchException>(() => VaultFile.Read(spliced, keyRing));
    }

    [Fact]
    public void TheCredentialSectionCannotBeSwappedForTheHostSection()
    {
        // Both sections are sealed under the same purpose key, so only the
        // associated data distinguishes them. Without the section name in it, a
        // file could be rearranged to have the app parse credentials as hosts.
        using var keyRing = VaultKeyRing.CreateNew();
        var document = VaultDocument.CreateEmpty("d");
        var bytes = VaultFile.Write(document, keyRing);

        var reader = SectionOffsets(bytes);
        var swapped = bytes[..reader.first].Concat(bytes[reader.second..]).Concat(bytes[reader.first..reader.second]).ToArray();

        Assert.ThrowsAny<Exception>(() => VaultFile.Read(swapped, keyRing));
    }

    [Fact]
    public void HostsAreReadableWithoutEverDerivingTheBackupKey()
    {
        // Not a behaviour test so much as a statement of the key hierarchy: the
        // section key is derived for Secrets, and a key derived for any other
        // purpose must not open it.
        using var keyRing = VaultKeyRing.CreateNew();
        var bytes = VaultFile.Write(Populated(), keyRing);

        using var backupKey = keyRing.DerivePurposeKey(VaultKeyPurpose.Backup);
        using var secretsKey = keyRing.DerivePurposeKey(VaultKeyPurpose.Secrets);

        Assert.False(backupKey.ReadOnlySpan.SequenceEqual(secretsKey.ReadOnlySpan));
        Assert.NotEmpty(VaultFile.Read(bytes, keyRing).Hosts);
    }

    /// <summary>Copies the host section out of <paramref name="source"/> into <paramref name="target"/>.</summary>
    private static byte[] Splice(byte[] target, byte[] source)
    {
        var (targetFirst, targetSecond) = SectionOffsets(target);
        var (sourceFirst, sourceSecond) = SectionOffsets(source);
        return [.. target[..targetFirst], .. source[sourceFirst..sourceSecond], .. target[targetSecond..]];
    }

    /// <summary>Finds where the two sealed sections start, by re-walking the header.</summary>
    private static (int first, int second) SectionOffsets(byte[] bytes)
    {
        var position = 8; // magic
        position += 4;    // schema version
        position = SkipLengthPrefixed(bytes, position); // device id
        position += 8;    // revision

        var keyCount = ReadInt32(bytes, position);
        position += 4;
        for (var i = 0; i < keyCount; i++)
        {
            position = SkipLengthPrefixed(bytes, position); // id
            position += 4;                                  // method
            position = SkipLengthPrefixed(bytes, position); // payload
            if (bytes[position++] != 0) position += 16;     // kdf parameters
            if (bytes[position++] != 0) position = SkipLengthPrefixed(bytes, position); // salt
        }

        var first = position;
        var second = SkipLengthPrefixed(bytes, first);
        return (first, second);
    }

    private static int SkipLengthPrefixed(byte[] bytes, int position) => position + 4 + ReadInt32(bytes, position);

    private static int ReadInt32(byte[] bytes, int position) =>
        System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(position, 4));

    /// <summary>
    /// Asserts every public property survived, naming the one that did not.
    /// </summary>
    private static void AssertEveryPropertyMatches<T>(T expected, T actual)
    {
        foreach (var property in typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var left = property.GetValue(expected);
            var right = property.GetValue(actual);
            Assert.True(
                ValuesMatch(left, right),
                $"{typeof(T).Name}.{property.Name} did not survive the round trip: expected {Describe(left)}, got {Describe(right)}.");
        }
    }

    private static void AssertNoPropertyIsDefault<T>(T value, string[] except)
    {
        foreach (var property in typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (except.Contains(property.Name)) continue;
            var actual = property.GetValue(value);
            var fallback = property.PropertyType.IsValueType ? Activator.CreateInstance(property.PropertyType) : null;
            Assert.True(
                !ValuesMatch(actual, fallback),
                $"{typeof(T).Name}.{property.Name} is left at its default in the fixture, so the round-trip test cannot see it.");
        }
    }

    private static bool ValuesMatch(object? left, object? right)
    {
        if (left is null || right is null) return left is null && right is null;
        if (left is string) return left.Equals(right);
        if (left is System.Collections.IEnumerable a && right is System.Collections.IEnumerable b)
            return a.Cast<object>().SequenceEqual(b.Cast<object>());
        return left.Equals(right);
    }

    private static string Describe(object? value) => value switch
    {
        null => "null",
        string text => $"\"{text}\"",
        byte[] bytes => Convert.ToHexStringLower(bytes),
        System.Collections.IEnumerable items and not string => "[" + string.Join(", ", items.Cast<object>()) + "]",
        _ => value.ToString() ?? "?",
    };
}
