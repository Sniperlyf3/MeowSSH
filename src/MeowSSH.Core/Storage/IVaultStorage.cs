namespace MeowSSH.Core.Storage;

/// <summary>
/// Where the vault's bytes live.
/// </summary>
/// <remarks>
/// The only thing between <see cref="VaultStore"/> and a filesystem, and it is
/// deliberately this narrow: the store never learns whether it is writing to a
/// phone's private directory or to a test's dictionary, so the encryption cannot
/// quietly depend on the platform providing any of it.
/// </remarks>
public interface IVaultStorage
{
    /// <summary>Reads the vault, or null if none has been written on this device.</summary>
    ValueTask<byte[]?> ReadAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces the vault's contents.
    /// </summary>
    /// <remarks>
    /// Implementations must be atomic: a process killed part way through has to
    /// leave either the old vault or the new one, never a prefix of the new one.
    /// A half-written vault is an unopenable vault, and the user's hosts and keys
    /// are the thing inside it.
    /// </remarks>
    ValueTask WriteAsync(byte[] contents, CancellationToken cancellationToken = default);

    /// <summary>Removes the vault entirely. Used when the user resets the app.</summary>
    ValueTask DeleteAsync(CancellationToken cancellationToken = default);
}

/// <summary>Keeps the vault in a file, replacing it by rename.</summary>
public sealed class FileVaultStorage(string path) : IVaultStorage, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public string Path { get; } = path;

    public async ValueTask<byte[]?> ReadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(Path))
            {
                // A temp file left by a write that was killed after the data was
                // flushed but before the rename is a complete vault; the rename
                // is finished here rather than losing the save.
                if (File.Exists(TempPath)) File.Move(TempPath, Path);
                else return null;
            }
            return await File.ReadAllBytesAsync(Path, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask WriteAsync(byte[] contents, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contents);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var directory = System.IO.Path.GetDirectoryName(Path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            // Write beside the vault, force it to the platter, then rename over
            // the old one. Writing in place would mean a phone that died mid-save
            // left a file that is neither version.
            var handle = new FileStream(TempPath, FileMode.Create, FileAccess.Write, FileShare.None);
            await using (handle.ConfigureAwait(false))
            {
                await handle.WriteAsync(contents, cancellationToken).ConfigureAwait(false);
                await handle.FlushAsync(cancellationToken).ConfigureAwait(false);
                handle.Flush(flushToDisk: true);
            }

            File.Move(TempPath, Path, overwrite: true);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DeleteAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (File.Exists(Path)) File.Delete(Path);
            if (File.Exists(TempPath)) File.Delete(TempPath);
        }
        finally
        {
            _gate.Release();
        }
    }

    private string TempPath => Path + ".new";

    public void Dispose() => _gate.Dispose();
}

/// <summary>Keeps the vault in memory, for tests and for previews.</summary>
public sealed class InMemoryVaultStorage : IVaultStorage
{
    private byte[]? _contents;

    /// <summary>How many times the vault has been written. Tests assert on saves actually happening.</summary>
    public int WriteCount { get; private set; }

    public ValueTask<byte[]?> ReadAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(_contents?.ToArray());

    public ValueTask WriteAsync(byte[] contents, CancellationToken cancellationToken = default)
    {
        _contents = contents.ToArray();
        WriteCount++;
        return ValueTask.CompletedTask;
    }

    public ValueTask DeleteAsync(CancellationToken cancellationToken = default)
    {
        _contents = null;
        return ValueTask.CompletedTask;
    }

    /// <summary>Corrupts a byte, so a test can prove tampering is detected rather than tolerated.</summary>
    public void Corrupt(int offset)
    {
        if (_contents is null) throw new InvalidOperationException("Nothing has been written yet.");
        _contents[offset] ^= 0xFF;
    }
}
