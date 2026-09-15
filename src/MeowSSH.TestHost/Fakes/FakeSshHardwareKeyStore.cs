using MeowSSH.Core.Ssh;

namespace MeowSSH.TestHost.Fakes;

public sealed class FakeSshHardwareKeyStore : ISshHardwareKeyStore
{
    private readonly Dictionary<string, SshHardwareKeyInfo> _keys = [];

    public ValueTask<bool> IsAvailableAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(true);

    public ValueTask<SshHardwareKeyInfo> GenerateP256Async(
        string keyId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var info = new SshHardwareKeyInfo(
            keyId,
            $"ecdsa-sha2-nistp256 AAAAE2VjZHNhLXNoYTItbmlzdHAyNTYAAAAIbmlzdHAyNTYAAAAhBAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8g meowssh:{keyId}",
            HardwareBacked: true,
            StrongBoxBacked: false);
        _keys.Add(keyId, info);
        return ValueTask.FromResult(info);
    }

    public ValueTask<SshHardwareKeyInfo?> GetInfoAsync(
        string keyId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _keys.TryGetValue(keyId, out var info);
        return ValueTask.FromResult(info);
    }

    public ValueTask DeleteAsync(string keyId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _keys.Remove(keyId);
        return ValueTask.CompletedTask;
    }

    public ValueTask<byte[]> SignAsync(
        string keyId,
        string algorithm,
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The browser test host does not perform real SSH hardware signing.");
}
