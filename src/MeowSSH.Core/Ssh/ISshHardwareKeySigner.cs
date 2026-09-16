namespace MeowSSH.Core.Ssh;

/// <summary>
/// Signs SSH authentication challenges with a platform-held non-exportable key.
/// </summary>
/// <remarks>
/// The private key never crosses this interface. The caller supplies only the
/// opaque platform key id, the SSH signature algorithm negotiated by Meowshell,
/// and the exact bytes that must be signed.
/// </remarks>
public interface ISshHardwareKeySigner
{
    ValueTask<byte[]> SignAsync(
        string keyId,
        string algorithm,
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken = default);
}
