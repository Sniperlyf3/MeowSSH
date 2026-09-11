using Konscious.Security.Cryptography;

namespace MeowSSH.Core.Security;

public enum KdfAlgorithm
{
    Argon2id = 1,
}

/// <summary>
/// The cost parameters a passphrase was stretched with. These travel inside every
/// backup file: a vault restored on a future device must be able to reproduce the
/// derivation exactly, even after the app's defaults have moved on.
/// </summary>
public sealed record KdfParameters(KdfAlgorithm Algorithm, int MemoryKiB, int Iterations, int Parallelism)
{
    /// <summary>
    /// Defaults for a phone: 64 MiB of memory, three passes, four lanes.
    /// </summary>
    /// <remarks>
    /// Memory is the parameter that actually costs an attacker something — it is
    /// what makes a GPU or ASIC array expensive rather than merely parallel — so it
    /// is set well above the OWASP floor of 19 MiB while staying inside what a
    /// low-end Android device can allocate without being killed.
    /// </remarks>
    public static KdfParameters Interactive { get; } = new(KdfAlgorithm.Argon2id, MemoryKiB: 65536, Iterations: 3, Parallelism: 4);

    /// <summary>
    /// Deliberately cheap parameters for tests. Never use these for real data.
    /// </summary>
    public static KdfParameters Testing { get; } = new(KdfAlgorithm.Argon2id, MemoryKiB: 256, Iterations: 1, Parallelism: 1);

    public void Validate()
    {
        if (Algorithm != KdfAlgorithm.Argon2id)
            throw new NotSupportedException($"Unsupported key derivation algorithm: {Algorithm}.");
        ArgumentOutOfRangeException.ThrowIfLessThan(MemoryKiB, 8);
        ArgumentOutOfRangeException.ThrowIfLessThan(Iterations, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(Parallelism, 1);
    }
}

public interface IKeyDerivation
{
    KdfParameters Parameters { get; }

    /// <summary>Stretches a passphrase into <paramref name="length"/> bytes of key material.</summary>
    SecretBuffer DeriveKey(ReadOnlySpan<byte> passphrase, ReadOnlySpan<byte> salt, int length);
}

public sealed class Argon2idKeyDerivation : IKeyDerivation
{
    public const int SaltSize = 16;

    public Argon2idKeyDerivation(KdfParameters? parameters = null)
    {
        Parameters = parameters ?? KdfParameters.Interactive;
        Parameters.Validate();
    }

    public KdfParameters Parameters { get; }

    public SecretBuffer DeriveKey(ReadOnlySpan<byte> passphrase, ReadOnlySpan<byte> salt, int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
        if (salt.Length < 8)
            throw new ArgumentException("Salt must be at least 8 bytes.", nameof(salt));

        // Konscious takes byte[] rather than spans, so the passphrase is copied
        // into a pinned buffer and cleared here rather than left to the GC.
        using var password = SecretBuffer.CopyFrom(passphrase);
        using var argon2 = new Argon2id(password.Span.ToArray())
        {
            Salt = salt.ToArray(),
            MemorySize = Parameters.MemoryKiB,
            Iterations = Parameters.Iterations,
            DegreeOfParallelism = Parameters.Parallelism,
        };
        return SecretBuffer.TakeOwnershipOf(argon2.GetBytes(length));
    }
}
