using System.Runtime.Versioning;
using System.Security.Cryptography;
using MeowSSH.Core.Model;
using MeowSSH.Core.Ssh;
using Xunit.Abstractions;

namespace MeowSSH.Integration.Tests;

/// <summary>
/// File operations against a real OpenSSH SFTP subsystem. The fake used by the
/// browser tests answers whatever it is asked; only a real server enforces
/// permissions, refuses to remove a non-empty directory, and reports the mode
/// bits the UI renders.
/// </summary>
[Collection(nameof(SshServerCollection))]
[SupportedOSPlatform("linux")]
public class SftpTests(SshServerFixture server, ITestOutputHelper output)
{
    private async Task<(ISshConnection Connection, ISftpSession Sftp, string Home)> ConnectAsync(string name)
    {
        server.AuthorizeClientKey();
        var workspace = server.WorkspaceFor(name);
        var knownHosts = await server.SeedKnownHostsAsync(workspace);
        using var credentials = SshCredentials.FromPrivateKeyFile(server.ClientKeyPath);

        var engine = new MeowshellSshEngine(new MeowshellSshEngineOptions(
            WorkingDirectory: workspace,
            KnownHostsPath: knownHosts,
            BinaryDirectory: Environment.GetEnvironmentVariable("MEOWSSH_BINARIES"),
            ConnectTimeout: TimeSpan.FromSeconds(45)));

        var host = new HostRecord
        {
            Id = Guid.NewGuid(),
            Label = "test-server",
            Address = "127.0.0.1",
            Port = server.Port,
            Username = SshServerFixture.Username,
            Transport = SshTransport.Tcp,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        var connection = await engine.ConnectAsync(host, credentials, new RecordingPrompts());
        var sftp = await connection.OpenSftpAsync();

        // SFTP has no "home directory" request; resolving "." is how a client
        // finds where the session opened.
        var home = await sftp.ResolveAsync(".");

        // Each test gets its own directory under it. The remote home outlives
        // the suite -- it is a real user's home on a real server -- so tests
        // sharing it would collide with each other and with the previous run,
        // which is exactly what happened the first time these were written.
        var scratch = RemotePath.Join(home, $"meowssh-{name}-{Guid.NewGuid().ToString("N")[..6]}");
        await sftp.CreateDirectoryAsync(scratch);
        return (connection, sftp, scratch);
    }

    [SkippableFact]
    public async Task ListsARealDirectoryWithItsModeBitsAndTimestamps()
    {
        Skip.IfNot(SshServerFixture.IsSupported, "No sshd on this machine.");
        var (connection, sftp, home) = await ConnectAsync(nameof(ListsARealDirectoryWithItsModeBitsAndTimestamps));
        await using var _ = connection;

        var dir = RemotePath.Join(home, "listing");
        await sftp.CreateDirectoryAsync(dir);
        await File.WriteAllTextAsync(Path.Combine(Path.GetTempPath(), "meow-list.txt"), "hello");
        await sftp.UploadAsync(Path.Combine(Path.GetTempPath(), "meow-list.txt"),
            RemotePath.Join(dir, "hello.txt"));

        var entries = await sftp.ListAsync(dir);
        foreach (var e in entries) output.WriteLine($"{e.PermissionString} {e.DisplaySize,8}  {e.Name}");

        var file = Assert.Single(entries, e => e.Name == "hello.txt");
        Assert.False(file.IsDirectory);
        Assert.Equal(5, file.Size);
        // Real mode bits, not zero: the UI renders these, and a server that
        // reported nothing would leave every file looking like ---------.
        Assert.NotEqual(0u, file.Mode);
        Assert.Contains("r", file.PermissionString, StringComparison.Ordinal);
        // Written seconds ago, so a timestamp the server actually filled in.
        Assert.True(DateTimeOffset.UtcNow - file.ModifiedAt < TimeSpan.FromMinutes(5),
            $"ModifiedAt was {file.ModifiedAt}, which is not a time this file was written.");
    }

    [SkippableFact]
    public async Task ADirectoryIsReportedAsOneAndCanBeNavigatedInto()
    {
        Skip.IfNot(SshServerFixture.IsSupported, "No sshd on this machine.");
        var (connection, sftp, home) = await ConnectAsync(nameof(ADirectoryIsReportedAsOneAndCanBeNavigatedInto));
        await using var _ = connection;

        var outer = RemotePath.Join(home, "outer");
        var inner = RemotePath.Join(outer, "inner");
        await sftp.CreateDirectoryAsync(outer);
        await sftp.CreateDirectoryAsync(inner);

        var entry = Assert.Single(await sftp.ListAsync(outer), e => e.Name == "inner");
        Assert.True(entry.IsDirectory);
        Assert.Equal("", entry.DisplaySize);   // a size for a directory is noise
        Assert.Empty(await sftp.ListAsync(entry.Path));
    }

    [SkippableFact]
    public async Task ARoundTripPreservesFileContentsExactly()
    {
        Skip.IfNot(SshServerFixture.IsSupported, "No sshd on this machine.");
        var (connection, sftp, home) = await ConnectAsync(nameof(ARoundTripPreservesFileContentsExactly));
        await using var _ = connection;

        // Random bytes across a size that spans several SFTP reads, so a lost or
        // duplicated chunk shows up rather than hiding in a short file.
        var payload = RandomNumberGenerator.GetBytes(512 * 1024);
        var localUp = Path.Combine(Path.GetTempPath(), $"meow-up-{Guid.NewGuid():N}");
        var localDown = Path.Combine(Path.GetTempPath(), $"meow-down-{Guid.NewGuid():N}");
        await File.WriteAllBytesAsync(localUp, payload);

        var remote = RemotePath.Join(home, "payload.bin");
        var sent = new List<TransferProgress>();
        var received = new List<TransferProgress>();

        await sftp.UploadAsync(localUp, remote, new Collect(sent));
        await sftp.DownloadAsync(remote, localDown, new Collect(received));

        Assert.Equal(payload, await File.ReadAllBytesAsync(localDown));
        Assert.Equal(payload.Length, (await sftp.StatAsync(remote)).Size);

        output.WriteLine($"upload reports: {sent.Count}, download reports: {received.Count}");
        Assert.NotEmpty(sent);
        Assert.NotEmpty(received);

        // An upload gets no total from the engine, so the session supplies it
        // from the local file -- without which a progress bar has no denominator.
        Assert.All(sent, p => Assert.Equal(payload.Length, p.TotalBytes));
        Assert.All(received, p => Assert.Equal(payload.Length, p.TotalBytes));
        Assert.Equal(payload.Length, sent[^1].BytesTransferred);
        Assert.Equal(1d, received[^1].Fraction);

        File.Delete(localUp);
        File.Delete(localDown);
    }

    [SkippableFact]
    public async Task RenameMovesAFileAndDeleteRemovesIt()
    {
        Skip.IfNot(SshServerFixture.IsSupported, "No sshd on this machine.");
        var (connection, sftp, home) = await ConnectAsync(nameof(RenameMovesAFileAndDeleteRemovesIt));
        await using var _ = connection;

        var local = Path.Combine(Path.GetTempPath(), $"meow-rn-{Guid.NewGuid():N}");
        await File.WriteAllTextAsync(local, "contents");
        var before = RemotePath.Join(home, "before.txt");
        var after = RemotePath.Join(home, "after.txt");
        await sftp.UploadAsync(local, before);

        await sftp.RenameAsync(before, after);
        var listing = await sftp.ListAsync(home);
        Assert.DoesNotContain(listing, e => e.Name == "before.txt");
        var renamed = Assert.Single(listing, e => e.Name == "after.txt");

        await sftp.DeleteAsync(renamed);
        Assert.DoesNotContain(await sftp.ListAsync(home), e => e.Name == "after.txt");

        File.Delete(local);
    }

    [SkippableFact]
    public async Task ANonEmptyDirectoryIsNotDeletedSilently()
    {
        Skip.IfNot(SshServerFixture.IsSupported, "No sshd on this machine.");
        var (connection, sftp, home) = await ConnectAsync(nameof(ANonEmptyDirectoryIsNotDeletedSilently));
        await using var _ = connection;

        var dir = RemotePath.Join(home, "not-empty");
        await sftp.CreateDirectoryAsync(dir);
        var local = Path.Combine(Path.GetTempPath(), $"meow-ne-{Guid.NewGuid():N}");
        await File.WriteAllTextAsync(local, "x");
        await sftp.UploadAsync(local, RemotePath.Join(dir, "child.txt"));

        var entry = Assert.Single(await sftp.ListAsync(home), e => e.Name == "not-empty");

        // Recursive deletion has to be an explicit decision, not something a
        // delete button does by surprise.
        var ex = await Assert.ThrowsAsync<SshException>(() => sftp.DeleteAsync(entry));
        output.WriteLine($"{ex.Failure}: {ex.Message}");
        Assert.Contains(entry.Name, (await sftp.ListAsync(home)).Select(e => e.Name));

        File.Delete(local);
    }

    [SkippableFact]
    public async Task PermissionChangesReachTheServerAndComeBack()
    {
        Skip.IfNot(SshServerFixture.IsSupported, "No sshd on this machine.");
        var (connection, sftp, home) = await ConnectAsync(nameof(PermissionChangesReachTheServerAndComeBack));
        await using var _ = connection;

        var local = Path.Combine(Path.GetTempPath(), $"meow-cm-{Guid.NewGuid():N}");
        await File.WriteAllTextAsync(local, "#!/bin/sh\necho hi\n");
        var remote = RemotePath.Join(home, "script.sh");
        await sftp.UploadAsync(local, remote);

        await sftp.SetPermissionsAsync(remote, 0b111_101_101);   // 0755

        var after = await sftp.StatAsync(remote);
        // Convert.ToString for the octal: .NET has no "o" numeric format
        // specifier, and asking for one throws rather than being ignored.
        output.WriteLine($"mode 0{Convert.ToString(after.Mode & 0xFFF, 8)} -> {after.PermissionString}");
        Assert.Equal("rwxr-xr-x", after.PermissionString);

        File.Delete(local);
    }

    [SkippableFact]
    public async Task AMissingPathReportsNotFoundRatherThanSomethingGeneric()
    {
        Skip.IfNot(SshServerFixture.IsSupported, "No sshd on this machine.");
        var (connection, sftp, home) = await ConnectAsync(nameof(AMissingPathReportsNotFoundRatherThanSomethingGeneric));
        await using var _ = connection;

        var ex = await Assert.ThrowsAsync<SshException>(
            () => sftp.ListAsync(RemotePath.Join(home, "no-such-directory")));

        output.WriteLine($"{ex.Failure}: {ex.Message}");
        // A typed failure so the file browser can say "that folder is gone"
        // rather than showing a spinner or a raw protocol message.
        Assert.Equal(SshFailure.NotFound, ex.Failure);
    }

    [SkippableFact]
    public async Task APathThisUserCannotReadReportsPermissionDenied()
    {
        Skip.IfNot(SshServerFixture.IsSupported, "No sshd on this machine.");
        var (connection, sftp, _) = await ConnectAsync(nameof(APathThisUserCannotReadReportsPermissionDenied));
        await using var _c = connection;

        // Root's own directory, which the unprivileged test user cannot read.
        var ex = await Assert.ThrowsAsync<SshException>(() => sftp.ListAsync("/root"));

        output.WriteLine($"{ex.Failure}: {ex.Message}");
        Assert.Equal(SshFailure.PermissionDenied, ex.Failure);
    }

    [SkippableFact]
    public async Task FileBrowsingSharesTheShellsConnection()
    {
        Skip.IfNot(SshServerFixture.IsSupported, "No sshd on this machine.");
        var (connection, sftp, home) = await ConnectAsync(nameof(FileBrowsingSharesTheShellsConnection));
        await using var _ = connection;

        // A shell and a file listing at once, over one authenticated connection.
        // If these were separate connections, a one-time code would have to be
        // entered twice to reach this point.
        await using var shell = await connection.OpenShellAsync(80, 24);
        var listing = await sftp.ListAsync(home);

        Assert.True(connection.IsConnected);
        Assert.NotNull(listing);
    }

    private sealed class Collect(List<TransferProgress> into) : IProgress<TransferProgress>
    {
        public void Report(TransferProgress value) { lock (into) into.Add(value); }
    }
}
