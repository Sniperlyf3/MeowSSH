using Android.Content;
using Android.Provider;
using Android.Webkit;
using MeowSSH.UI.Services;

namespace MeowSSH.App;

public sealed class AndroidLocalFileTransferService : ILocalFileTransferService
{
    public async Task<LocalFileSelection?> PickFileAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var picked = await FilePicker.Default.PickAsync(new PickOptions
        {
            PickerTitle = "Choose a file to upload",
        });
        if (picked is null) return null;

        cancellationToken.ThrowIfCancellationRequested();
        var name = SafeName(picked.FileName);
        var temporaryPath = CreateTemporaryPath(name);

        await using var input = await picked.OpenReadAsync();
        await using var output = File.Create(temporaryPath);
        await input.CopyToAsync(output, cancellationToken);

        return new LocalFileSelection(temporaryPath, name);
    }

    public string CreateTemporaryPath(string suggestedName)
    {
        var directory = Path.Combine(FileSystem.CacheDirectory, "sftp");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, $"{Guid.NewGuid():N}-{SafeName(suggestedName)}");
    }

    public async Task<string> PublishDownloadAsync(
        string temporaryPath,
        string suggestedName,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var fileName = SafeName(suggestedName);

        if (OperatingSystem.IsAndroidVersionAtLeast(29))
            return await PublishToDownloadsAsync(temporaryPath, fileName, cancellationToken);

        // Android 9 has no scoped-storage Downloads API. Keep the file in app
        // storage rather than requesting broad storage access for one legacy API.
        var fallback = Path.Combine(FileSystem.AppDataDirectory, "Downloads");
        Directory.CreateDirectory(fallback);
        var destination = UniquePath(fallback, fileName);
        File.Copy(temporaryPath, destination, overwrite: false);
        return destination;
    }

    public void DeleteTemporaryFile(string path)
    {
        try { File.Delete(path); } catch (IOException) { }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("android29.0")]
    private static async Task<string> PublishToDownloadsAsync(
        string temporaryPath,
        string fileName,
        CancellationToken cancellationToken)
    {
        var resolver = global::Android.App.Application.Context.ContentResolver
            ?? throw new InvalidOperationException("Android content resolver is unavailable.");

        using var values = new ContentValues();
        values.Put(MediaStore.IMediaColumns.DisplayName, fileName);
        values.Put(MediaStore.IMediaColumns.MimeType, MimeType(fileName));
        values.Put(MediaStore.IMediaColumns.RelativePath,
            $"{global::Android.OS.Environment.DirectoryDownloads}/MeowSSH");
        values.Put(MediaStore.IMediaColumns.IsPending, 1);

        var uri = resolver.Insert(MediaStore.Downloads.ExternalContentUri, values)
            ?? throw new IOException("Android could not create the download.");

        try
        {
            await using (var input = File.OpenRead(temporaryPath))
            await using (var output = resolver.OpenOutputStream(uri)
                ?? throw new IOException("Android could not open the download destination."))
            {
                await input.CopyToAsync(output, cancellationToken);
            }

            values.Clear();
            values.Put(MediaStore.IMediaColumns.IsPending, 0);
            resolver.Update(uri, values, null, null);
            return $"Downloads/MeowSSH/{fileName}";
        }
        catch
        {
            resolver.Delete(uri, null, null);
            throw;
        }
    }

    private static string MimeType(string fileName)
    {
        var extension = Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant();
        return MimeTypeMap.Singleton?.GetMimeTypeFromExtension(extension)
            ?? "application/octet-stream";
    }

    private static string SafeName(string name)
    {
        var fileName = Path.GetFileName(name);
        return string.Concat(fileName.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
    }

    private static string UniquePath(string directory, string fileName)
    {
        var candidate = Path.Combine(directory, fileName);
        if (!File.Exists(candidate)) return candidate;

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        for (var i = 2; ; i++)
        {
            candidate = Path.Combine(directory, $"{stem} ({i}){extension}");
            if (!File.Exists(candidate)) return candidate;
        }
    }
}
