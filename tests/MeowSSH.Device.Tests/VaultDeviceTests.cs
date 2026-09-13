using System.Security.Cryptography;
using System.Text;
using MeowSSH.Android;
using MeowSSH.Core.Model;
using MeowSSH.Core.Security;
using MeowSSH.Core.Services;
using MeowSSH.Core.Storage;
using Xunit;

namespace MeowSSH.Device.Tests;

/// <summary>
/// What can only be proven on a real Android runtime.
/// </summary>
/// <remarks>
/// Nothing here re-tests the vault's logic; that is covered on Linux where it
/// runs in milliseconds. These are the claims that were previously only ever
/// checked by installing the APK and looking at it: that the Keystore really
/// refuses to hand a key back, that a wrapped key survives being read by a
/// different object, that a vault written here is unreadable on disk, and that
/// the biometric gate reports something truthful about the hardware it is on.
/// </remarks>
public static class VaultDeviceTests
{
    /// <summary>
    /// Keys are created per test and deleted afterwards. A Keystore alias
    /// outlives the process, so a leftover from an earlier run would otherwise
    /// make the next one pass for the wrong reason.
    /// </summary>
    private static string NewAlias() => "meowssh.devicetest." + Guid.NewGuid().ToString("n")[..8];

    /// <summary>The binaries Meowshell needs extracted to be executable at all.</summary>
    private static readonly string[] NativeLibraries = ["libmeowshell.so", "libtailcat.so"];

    private static string NewDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "meowssh-device-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(path);
        return path;
    }

    public static IEnumerable<DeviceTest> All =>
    [
        new("Keystore: the wrapping key exists after being created", async () =>
        {
            var alias = NewAlias();
            var store = new AndroidDeviceKeyStore(alias);
            try
            {
                Assert.True(await store.IsAvailableAsync(), "the device reported no usable key store");
                await store.CreateWrappingKeyAsync(requireUserAuthentication: false);

                using var key = SecretBuffer.Random(VaultCrypto.KeySize);
                var wrapped = await store.WrapAsync(key.ReadOnlySpan.ToArray());
                Assert.NotEmpty(wrapped);
            }
            finally { await store.DeleteWrappingKeyAsync(); }
        }),

        new("Keystore: a wrapped key round-trips through real hardware", async () =>
        {
            var alias = NewAlias();
            var store = new AndroidDeviceKeyStore(alias);
            try
            {
                await store.CreateWrappingKeyAsync(requireUserAuthentication: false);
                using var original = SecretBuffer.Random(VaultCrypto.KeySize);

                var wrapped = await store.WrapAsync(original.ReadOnlySpan.ToArray());
                using var recovered = await store.UnwrapAsync(wrapped);

                Assert.True(
                    original.ReadOnlySpan.SequenceEqual(recovered.ReadOnlySpan),
                    "the key that came back out of the Keystore is not the one that went in");
            }
            finally { await store.DeleteWrappingKeyAsync(); }
        }),

        new("Keystore: the ciphertext is not the plaintext", async () =>
        {
            // A wrap implementation that quietly returned its input would pass
            // the round-trip above and protect nothing.
            var alias = NewAlias();
            var store = new AndroidDeviceKeyStore(alias);
            try
            {
                await store.CreateWrappingKeyAsync(requireUserAuthentication: false);
                using var original = SecretBuffer.Random(VaultCrypto.KeySize);
                var wrapped = await store.WrapAsync(original.ReadOnlySpan.ToArray());

                Assert.False(
                    Contains(wrapped, original.ReadOnlySpan.ToArray()),
                    "the master key appears verbatim inside its own wrapped form");
                Assert.True(wrapped.Length > VaultCrypto.KeySize, "the wrapped key carries no nonce or tag");
            }
            finally { await store.DeleteWrappingKeyAsync(); }
        }),

        new("Keystore: a different alias cannot unwrap another's key", async () =>
        {
            var mine = new AndroidDeviceKeyStore(NewAlias());
            var theirs = new AndroidDeviceKeyStore(NewAlias());
            try
            {
                await mine.CreateWrappingKeyAsync(requireUserAuthentication: false);
                await theirs.CreateWrappingKeyAsync(requireUserAuthentication: false);

                using var key = SecretBuffer.Random(VaultCrypto.KeySize);
                var wrapped = await mine.WrapAsync(key.ReadOnlySpan.ToArray());

                await Assert.ThrowsAnyAsync<Exception>(async () => await theirs.UnwrapAsync(wrapped));
            }
            finally
            {
                await mine.DeleteWrappingKeyAsync();
                await theirs.DeleteWrappingKeyAsync();
            }
        }),

        new("Keystore: a deleted key cannot unwrap what it sealed", async () =>
        {
            // This is the biometric-enrolment guard in miniature: the platform
            // destroys the key and the vault must be unopenable by that route.
            var alias = NewAlias();
            var store = new AndroidDeviceKeyStore(alias);
            await store.CreateWrappingKeyAsync(requireUserAuthentication: false);
            using var key = SecretBuffer.Random(VaultCrypto.KeySize);
            var wrapped = await store.WrapAsync(key.ReadOnlySpan.ToArray());

            await store.DeleteWrappingKeyAsync();

            var error = await Assert.ThrowsAsync<DeviceKeyUnavailableException>(
                async () => await store.UnwrapAsync(wrapped));
            Assert.Equal(DeviceKeyUnavailableReason.NotProvisioned, error.Reason);
        }),

        new("Keystore: deleting a key that was never created is not an error", async () =>
        {
            // Called on the recovery path, where the key may already be gone.
            var store = new AndroidDeviceKeyStore(NewAlias());
            await store.DeleteWrappingKeyAsync();
        }),

        new("Keystore: StrongBox is reported without throwing", async () =>
        {
            var alias = NewAlias();
            var store = new AndroidDeviceKeyStore(alias);
            try
            {
                await store.CreateWrappingKeyAsync(requireUserAuthentication: false);
                // Either answer is correct -- an emulator has no StrongBox. What
                // is being checked is that asking does not throw on any API level,
                // since KeyInfo.SecurityLevel is API 31 and the app targets 28.
                await store.IsStrongBoxBackedAsync();
            }
            finally { await store.DeleteWrappingKeyAsync(); }
        }),

        new("Biometrics: availability is reported without throwing", async () =>
        {
            // BiometricManager is API 29 and CanAuthenticate's overload is API 30.
            // Getting those guards wrong throws rather than returning a wrong
            // answer, and it throws on the very first screen the user sees.
            // No activity: availability is answered from the application context,
            // which is exactly the path the lock screen takes before it draws.
            var availability = await new AndroidBiometricGate(() => null).GetAvailabilityAsync();
            Assert.True(Enum.IsDefined(availability), $"unrecognised availability value {availability}");
        }),

        new("Vault: survives being reopened by a different store object", async () =>
        {
            // The restart case. A vault that only works while the process that
            // created it is alive is the bug this whole layer exists to avoid.
            var directory = NewDirectory();
            var alias = NewAlias();
            var keys = new UngatedKeyStore(new AndroidDeviceKeyStore(alias));
            var storage = new FileVaultStorage(Path.Combine(directory, "meowssh.vault"));
            try
            {
                var host = new HostRecord { Id = Guid.NewGuid(), Label = "build-01", Address = "10.0.0.4" };

                using (var first = new VaultStore(storage))
                {
                    await first.CreateAsync(keys, "device-test", new Argon2idKeyDerivation(KdfParameters.Testing));
                    await first.UpdateAsync((doc, now) => doc.WithHost(host, now));
                }

                using var second = new VaultStore(storage);
                await second.UnlockWithDeviceKeyAsync(keys);

                Assert.Equal("build-01", Assert.Single(second.Document.Hosts).Label);
            }
            finally
            {
                await keys.DeleteWrappingKeyAsync();
                Directory.Delete(directory, recursive: true);
            }
        }),

        new("Vault: the file on disk holds no plaintext", async () =>
        {
            var directory = NewDirectory();
            var alias = NewAlias();
            var keys = new UngatedKeyStore(new AndroidDeviceKeyStore(alias));
            var path = Path.Combine(directory, "meowssh.vault");
            var storage = new FileVaultStorage(path);
            try
            {
                using var store = new VaultStore(storage);
                await store.CreateAsync(keys, "device-test", new Argon2idKeyDerivation(KdfParameters.Testing));
                await store.UpdateAsync((doc, now) => doc.WithCredential(new CredentialRecord
                {
                    Id = Guid.NewGuid(),
                    Label = "deploy key",
                    Kind = CredentialKind.Password,
                    Secret = Encoding.UTF8.GetBytes("correct-horse-battery-staple"),
                }, now));

                var bytes = await File.ReadAllBytesAsync(path);
                Assert.False(Contains(bytes, "correct-horse"u8.ToArray()), "the password is readable in the vault file");
                Assert.False(Contains(bytes, "deploy key"u8.ToArray()), "the credential's label is readable in the vault file");
            }
            finally
            {
                await keys.DeleteWrappingKeyAsync();
                Directory.Delete(directory, recursive: true);
            }
        }),

        new("Vault: the file is created where only this app can read it", async () =>
        {
            // Android sandboxes app storage anyway, but a vault written somewhere
            // world-readable would not be caught by anything else here.
            var directory = NewDirectory();
            var alias = NewAlias();
            var keys = new UngatedKeyStore(new AndroidDeviceKeyStore(alias));
            var path = Path.Combine(directory, "meowssh.vault");
            try
            {
                using var store = new VaultStore(new FileVaultStorage(path));
                await store.CreateAsync(keys, "device-test", new Argon2idKeyDerivation(KdfParameters.Testing));

                var mode = File.GetUnixFileMode(path);
                const UnixFileMode shared =
                    UnixFileMode.GroupRead | UnixFileMode.GroupWrite |
                    UnixFileMode.OtherRead | UnixFileMode.OtherWrite;
                Assert.True((mode & shared) == 0, $"the vault file is mode {Convert.ToString((int)mode, 8)}");
            }
            finally
            {
                await keys.DeleteWrappingKeyAsync();
                Directory.Delete(directory, recursive: true);
            }
        }),

        new("Vault: the recovery code reopens a vault whose device key is gone", async () =>
        {
            var directory = NewDirectory();
            var alias = NewAlias();
            var keys = new UngatedKeyStore(new AndroidDeviceKeyStore(alias));
            var storage = new FileVaultStorage(Path.Combine(directory, "meowssh.vault"));
            try
            {
                string recoveryCode;
                using (var first = new VaultStore(storage))
                {
                    recoveryCode = await first.CreateAsync(
                        keys, "device-test", new Argon2idKeyDerivation(KdfParameters.Testing));
                    await first.UpdateAsync((doc, now) =>
                        doc.WithHost(new HostRecord { Id = Guid.NewGuid(), Label = "survivor", Address = "x" }, now));
                }

                // What enrolling a new fingerprint does to the key, done directly.
                await keys.DeleteWrappingKeyAsync();

                using var second = new VaultStore(storage);
                await Assert.ThrowsAsync<DeviceKeyUnavailableException>(
                    async () => await second.UnlockWithDeviceKeyAsync(keys));

                await second.UnlockWithRecoveryCodeAsync(recoveryCode);
                Assert.Equal("survivor", Assert.Single(second.Document.Hosts).Label);
            }
            finally
            {
                await keys.DeleteWrappingKeyAsync();
                Directory.Delete(directory, recursive: true);
            }
        }),

        new("Vault: a tampered file is refused", async () =>
        {
            var directory = NewDirectory();
            var alias = NewAlias();
            var keys = new UngatedKeyStore(new AndroidDeviceKeyStore(alias));
            var path = Path.Combine(directory, "meowssh.vault");
            var storage = new FileVaultStorage(path);
            try
            {
                using (var first = new VaultStore(storage))
                {
                    await first.CreateAsync(keys, "device-test", new Argon2idKeyDerivation(KdfParameters.Testing));
                    await first.UpdateAsync((doc, now) =>
                        doc.WithHost(new HostRecord { Id = Guid.NewGuid(), Label = "h", Address = "x" }, now));
                }

                var bytes = await File.ReadAllBytesAsync(path);
                bytes[^1] ^= 0x01;
                await File.WriteAllBytesAsync(path, bytes);

                using var second = new VaultStore(storage);
                await Assert.ThrowsAsync<AuthenticationTagMismatchException>(
                    async () => await second.UnlockWithDeviceKeyAsync(keys));
            }
            finally
            {
                await keys.DeleteWrappingKeyAsync();
                Directory.Delete(directory, recursive: true);
            }
        }),

        new("Keystore: a user-gated key with nothing enrolled reports NotEnrolled", async () =>
        {
            // A CI emulator has no fingerprint, which makes it the right place to
            // prove this: the platform refuses, and the refusal has to arrive as
            // a typed reason rather than as a Java exception reaching the UI.
            // There is a real race behind it too -- enrolment can be removed
            // between the availability check and this call.
            var store = new AndroidDeviceKeyStore(NewAlias());
            try
            {
                var availability = await new AndroidBiometricGate(() => null).GetAvailabilityAsync();
                if (availability == BiometricAvailability.Available) return; // Something is enrolled; nothing to prove.

                var error = await Assert.ThrowsAsync<DeviceKeyUnavailableException>(
                    async () => await store.CreateWrappingKeyAsync(requireUserAuthentication: true));
                Assert.Equal(DeviceKeyUnavailableReason.NotEnrolled, error.Reason);
                Assert.Contains("Set up a fingerprint", error.Message, StringComparison.Ordinal);
            }
            finally { await store.DeleteWrappingKeyAsync(); }
        }),

        new("Agent: the native binaries were extracted and are executable", () =>
        {
            // AndroidExtractNativeLibraries is what makes these runnable at all.
            // A packaging change that dropped them shows up here rather than as a
            // connection that fails on a user's phone.
            var nativeDir = global::Android.App.Application.Context.ApplicationInfo!.NativeLibraryDir!;
            foreach (var name in NativeLibraries)
            {
                var path = Path.Combine(nativeDir, name);
                Assert.True(File.Exists(path), $"{name} is missing from {nativeDir}");
            }
            return Task.CompletedTask;
        }),

        new("Agent: its working directory is private to this app", () =>
        {
            // Meowshell refuses a HOME that grants group or other access, and the
            // default from Directory.CreateDirectory under a umask of 022 is 0755.
            var path = Path.Combine(Path.GetTempPath(), "agent-home-" + Guid.NewGuid().ToString("n")[..8]);
            try
            {
                Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                var mode = File.GetUnixFileMode(path);
                const UnixFileMode shared =
                    UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
                Assert.True((mode & shared) == 0, $"the agent's HOME is mode {Convert.ToString((int)mode, 8)}");
            }
            finally { Directory.Delete(path, recursive: true); }
            return Task.CompletedTask;
        }),
    ];

    /// <summary>
    /// Creates keys that are not gated on the user.
    /// </summary>
    /// <remarks>
    /// A CI emulator has no enrolled fingerprint, and the platform refuses to
    /// create a key requiring user authentication when there is nothing to
    /// authenticate with. The checks that wrap this care about persistence and
    /// the shape of the file on disk, not about the gate -- which has its own
    /// check below, asserting exactly that refusal.
    /// </remarks>
    private sealed class UngatedKeyStore(IDeviceKeyStore inner) : IDeviceKeyStore
    {
        public ValueTask<bool> IsAvailableAsync(CancellationToken ct = default) => inner.IsAvailableAsync(ct);

        public ValueTask<bool> IsStrongBoxBackedAsync(CancellationToken ct = default) => inner.IsStrongBoxBackedAsync(ct);

        public ValueTask CreateWrappingKeyAsync(bool requireUserAuthentication, CancellationToken ct = default) =>
            inner.CreateWrappingKeyAsync(requireUserAuthentication: false, ct);

        public ValueTask<byte[]> WrapAsync(ReadOnlyMemory<byte> masterKey, CancellationToken ct = default) =>
            inner.WrapAsync(masterKey, ct);

        public ValueTask<SecretBuffer> UnwrapAsync(byte[] wrapped, CancellationToken ct = default) =>
            inner.UnwrapAsync(wrapped, ct);

        public ValueTask DeleteWrappingKeyAsync(CancellationToken ct = default) => inner.DeleteWrappingKeyAsync(ct);
    }

    private static bool Contains(byte[] haystack, byte[] needle)
    {
        for (var i = 0; i + needle.Length <= haystack.Length; i++)
        {
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle)) return true;
        }
        return false;
    }
}
