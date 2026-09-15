using System.Text.Json;

namespace MeowSSH.Core.Services;

public interface ITailcatIdentityStore
{
    Task<string> ExportAsync(string name, CancellationToken cancellationToken = default);
    Task ImportAsync(string name, string privateKeyJson, bool overwrite = false, CancellationToken cancellationToken = default);
}

/// <summary>Imports and exports Tailcat's documented *.private.json identity files.</summary>
public sealed class TailcatIdentityStore(TailcatHubRuntimeOptions runtime) : ITailcatIdentityStore
{
    private const int MaxIdentityBytes = 64 << 10;

    public async Task<string> ExportAsync(string name, CancellationToken cancellationToken = default)
    {
        var path = KeyPath(name);
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException($"Tailcat identity '{name}' does not exist.", path);
        if (info.Length > MaxIdentityBytes)
            throw new InvalidDataException($"Tailcat identity exceeds {MaxIdentityBytes} bytes.");
        return await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
    }

    public async Task ImportAsync(string name, string privateKeyJson, bool overwrite = false, CancellationToken cancellationToken = default)
    {
        var normalizedName = ValidateName(name);
        if (string.IsNullOrWhiteSpace(privateKeyJson))
            throw new ArgumentException("Private identity JSON is required.", nameof(privateKeyJson));
        if (System.Text.Encoding.UTF8.GetByteCount(privateKeyJson) > MaxIdentityBytes)
            throw new ArgumentException($"Tailcat identity exceeds {MaxIdentityBytes} bytes.", nameof(privateKeyJson));

        ValidateIdentityJson(privateKeyJson);
        var directory = KeyDirectory();
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var target = Path.Combine(directory, normalizedName + ".private.json");
        if (File.Exists(target) && !overwrite)
            throw new IOException($"Tailcat identity '{normalizedName}' already exists. Enable overwrite to replace it.");
        if (File.Exists(target) && (File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Refusing to overwrite a symlinked Tailcat identity.");

        var temporary = Path.Combine(directory, $".{normalizedName}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(temporary, privateKeyJson, cancellationToken).ConfigureAwait(false);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(temporary, target, overwrite);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        finally
        {
            try { File.Delete(temporary); } catch (IOException) { }
        }
    }

    private string KeyDirectory() => Path.Combine(runtime.HomeDirectory, ".config", "tailcat", "keys");

    private string KeyPath(string name) => Path.Combine(KeyDirectory(), ValidateName(name) + ".private.json");

    private static string ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A Tailcat identity name is required.", nameof(name));
        var trimmed = name.Trim();
        if (trimmed is "." or ".." || trimmed.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || trimmed.Contains('/') || trimmed.Contains('\\'))
            throw new ArgumentException("Tailcat identity name contains invalid path characters.", nameof(name));
        return trimmed;
    }

    private static void ValidateIdentityJson(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("Private", out var privateValue)
                || privateValue.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(privateValue.GetString()))
            {
                throw new ArgumentException("Tailcat identity JSON must contain a non-empty Private field.", nameof(json));
            }
        }
        catch (JsonException ex)
        {
            throw new ArgumentException("Tailcat identity is not valid JSON.", nameof(json), ex);
        }
    }
}
