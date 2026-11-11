using System.Collections.Concurrent;
using Android.Content;
using MeowSSH.Android;
using MeowSSH.Core.Security;
using MeowSSH.Core.Ssh;
using MeowSSH.E2E;
using Microsoft.Extensions.DependencyInjection;

namespace MeowSSH.Device.Tests;

/// <summary>
/// The shared E2E scenarios on Android: the real Keystore and the engine
/// binaries from the APK's native library directory, exactly as the app has them.
/// </summary>
public sealed class AndroidE2EPlatform(Context context) : IE2EPlatform
{
    private readonly ConcurrentDictionary<string, (AndroidDeviceKeyStore Vault, TrackingSshKeyStore Ssh)> _stores = new();

    public string Name => "android";

    // Not AppContext.BaseDirectory: nothing there is executable on Android,
    // which is why the app passes this same directory (see MauiProgram).
    public string? NativeBinaryDirectory => context.ApplicationInfo!.NativeLibraryDir;

    public string ScratchRoot { get; } = Directory.CreateDirectory(Path.Combine(context.FilesDir!.AbsolutePath, "e2e")).FullName;

    public void AddPlatformServices(IServiceCollection services, string instanceId)
    {
        var stores = _stores.GetOrAdd(instanceId, id => (
            new AndroidDeviceKeyStore("meowssh.e2e." + id),
            new TrackingSshKeyStore(new AndroidSshHardwareKeyStore())));

        services.AddSingleton<IDeviceKeyStore>(new WithoutUserAuthentication(stores.Vault));
        services.AddSingleton<ISshHardwareKeyStore>(stores.Ssh);
        services.AddSingleton<ISshHardwareKeySigner>(stores.Ssh);
    }

    public async Task CleanUpAsync(string instanceId)
    {
        if (!_stores.TryRemove(instanceId, out var stores)) return;
        await stores.Vault.DeleteWrappingKeyAsync();
        await stores.Ssh.DeleteCreatedAsync();
    }

    /// <summary>
    /// The real Keystore key, created without the user-authentication
    /// requirement.
    /// </summary>
    /// <remarks>
    /// The emulator has no enrolled fingerprint or screen lock, and a key that
    /// requires one cannot be created there at all. The gate itself is covered
    /// by VaultDeviceTests; this keeps everything else on the real Keystore
    /// rather than falling back to a software key.
    /// </remarks>
    private sealed class WithoutUserAuthentication(IDeviceKeyStore inner) : IDeviceKeyStore
    {
        public ValueTask<bool> IsAvailableAsync(CancellationToken cancellationToken = default) => inner.IsAvailableAsync(cancellationToken);
        public ValueTask<bool> IsStrongBoxBackedAsync(CancellationToken cancellationToken = default) => inner.IsStrongBoxBackedAsync(cancellationToken);
        public ValueTask CreateWrappingKeyAsync(bool requireUserAuthentication, CancellationToken cancellationToken = default) =>
            inner.CreateWrappingKeyAsync(requireUserAuthentication: false, cancellationToken);
        public ValueTask<byte[]> WrapAsync(ReadOnlyMemory<byte> masterKey, CancellationToken cancellationToken = default) => inner.WrapAsync(masterKey, cancellationToken);
        public ValueTask<SecretBuffer> UnwrapAsync(byte[] wrapped, CancellationToken cancellationToken = default) => inner.UnwrapAsync(wrapped, cancellationToken);
        public ValueTask DeleteWrappingKeyAsync(CancellationToken cancellationToken = default) => inner.DeleteWrappingKeyAsync(cancellationToken);
    }

    /// <summary>Remembers the keys a scenario generated, so none are left in the emulator's Keystore.</summary>
    private sealed class TrackingSshKeyStore(AndroidSshHardwareKeyStore inner) : ISshHardwareKeyStore
    {
        private readonly ConcurrentBag<string> _created = [];

        public ValueTask<bool> IsAvailableAsync(CancellationToken cancellationToken = default) => inner.IsAvailableAsync(cancellationToken);

        public async ValueTask<SshHardwareKeyInfo> GenerateP256Async(string keyId, CancellationToken cancellationToken = default)
        {
            _created.Add(keyId);
            return await inner.GenerateP256Async(keyId, cancellationToken);
        }

        public ValueTask<SshHardwareKeyInfo?> GetInfoAsync(string keyId, CancellationToken cancellationToken = default) => inner.GetInfoAsync(keyId, cancellationToken);
        public ValueTask DeleteAsync(string keyId, CancellationToken cancellationToken = default) => inner.DeleteAsync(keyId, cancellationToken);
        public ValueTask<byte[]> SignAsync(string keyId, string algorithm, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default) =>
            inner.SignAsync(keyId, algorithm, data, cancellationToken);

        public async Task DeleteCreatedAsync()
        {
            foreach (var keyId in _created) await inner.DeleteAsync(keyId);
        }
    }
}
