using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;

namespace MeowSSH.Integration.Tests;

/// <summary>
/// A real OpenSSH server, started fresh for the suite, that the engine connects
/// to over a real TCP socket.
/// </summary>
/// <remarks>
/// <para>
/// The fakes elsewhere prove the UI reacts correctly to an engine. Only this
/// proves the engine works: the handshake, the host key policy, public-key and
/// password auth, a pseudo-terminal, and the exact signature and framing bytes
/// have no stand-in that would catch getting them wrong.
/// </para>
/// <para>
/// The server owns its host key, so a test can replace it and make the server
/// look like a different machine — the only way to exercise the host-key-changed
/// path, which is the one case where being wrong means telling a user they are
/// safe while they are not.
/// </para>
/// </remarks>
// Linux-only by construction: it drives the system sshd and Unix file modes.
// Every test that uses it skips elsewhere, which the attribute makes explicit
// to the platform analyzer rather than leaving as an unchecked assumption.
[SupportedOSPlatform("linux")]
public sealed class SshServerFixture : IAsyncLifetime
{
    private string _root = "";
    private string _configPath = "";

    public int Port { get; private set; }

    public const string Username = "meowtest";

    public const string Password = "meow-correct-horse";

    /// <summary>Path to an OpenSSH private key the server accepts.</summary>
    public string ClientKeyPath => Path.Combine(_root, "client_key");

    public string HostKeyPath => Path.Combine(_root, "host_ed25519");

    /// <summary>A scratch directory for each test's known_hosts and agent HOME.</summary>
    public string WorkspaceFor(string name)
    {
        var path = Path.Combine(_root, "work", name);
        Directory.CreateDirectory(path);
        return path;
    }

    public static bool IsSupported => OperatingSystem.IsLinux() && File.Exists("/usr/sbin/sshd");

    public async Task InitializeAsync()
    {
        if (!IsSupported) return;

        _root = Path.Combine(Path.GetTempPath(), "meowssh-sshd-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
        Port = FindFreePort();

        Run("ssh-keygen", "-q", "-t", "ed25519", "-f", HostKeyPath, "-N", "", "-C", "meowssh-test-host");
        Run("ssh-keygen", "-q", "-t", "ed25519", "-f", ClientKeyPath, "-N", "", "-C", "meowssh-test-client");
        File.SetUnixFileMode(HostKeyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.SetUnixFileMode(ClientKeyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        EnsureUser();

        _configPath = Path.Combine(_root, "sshd_config");
        await File.WriteAllTextAsync(_configPath, $"""
            Port {Port}
            ListenAddress 127.0.0.1
            HostKey {HostKeyPath}
            PidFile {Path.Combine(_root, "sshd.pid")}
            LogLevel VERBOSE
            UsePAM no
            PasswordAuthentication yes
            PubkeyAuthentication yes
            PermitRootLogin no
            AllowUsers {Username}
            StrictModes no
            Subsystem sftp /usr/lib/openssh/sftp-server
            """);

        Start();
        await WaitForPortAsync();
    }

    /// <summary>
    /// Replaces the server's host key and restarts it, so the same address now
    /// presents a different identity.
    /// </summary>
    public async Task RotateHostKeyAsync()
    {
        Stop();
        File.Delete(HostKeyPath);
        File.Delete(HostKeyPath + ".pub");
        Run("ssh-keygen", "-q", "-t", "ed25519", "-f", HostKeyPath, "-N", "", "-C", "meowssh-test-host-rotated");
        File.SetUnixFileMode(HostKeyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        Start();
        await WaitForPortAsync();
    }

    /// <summary>
    /// Writes a known_hosts recording the server's current key.
    /// </summary>
    /// <remarks>
    /// Uses ssh-keyscan rather than the app, because the agent API offers no way
    /// to accept a key on a first connection — see the note in the test that
    /// covers it. This is the harness standing in for a decision the user would
    /// otherwise make.
    /// </remarks>
    public async Task<string> SeedKnownHostsAsync(string workspace)
    {
        var path = Path.Combine(workspace, "known_hosts");
        var scan = Run("ssh-keyscan", "-p", Port.ToString(), "-t", "ed25519", "127.0.0.1");
        await File.WriteAllTextAsync(path, scan);
        return path;
    }

    private static void EnsureUser()
    {
        if (Directory.Exists($"/home/{Username}")) return;
        Run("useradd", "-m", "-s", "/bin/bash", Username);
        RunShell($"echo '{Username}:{Password}' | chpasswd");
    }

    /// <summary>Authorises this fixture's client key for the test user.</summary>
    public void AuthorizeClientKey()
    {
        var sshDir = $"/home/{Username}/.ssh";
        Directory.CreateDirectory(sshDir);
        File.Copy(ClientKeyPath + ".pub", Path.Combine(sshDir, "authorized_keys"), overwrite: true);
        RunShell($"chown -R {Username}:{Username} {sshDir} && chmod 700 {sshDir} && chmod 600 {sshDir}/authorized_keys");
    }

    private void Start()
    {
        Directory.CreateDirectory("/run/sshd");
        Run("/usr/sbin/sshd", "-f", _configPath, "-E", Path.Combine(_root, "sshd.log"));
    }

    private void Stop()
    {
        var pidFile = Path.Combine(_root, "sshd.pid");
        if (!File.Exists(pidFile)) return;
        if (int.TryParse(File.ReadAllText(pidFile).Trim(), out var pid))
        {
            try { Process.GetProcessById(pid).Kill(entireProcessTree: true); } catch (ArgumentException) { }
        }
        File.Delete(pidFile);
    }

    private async Task WaitForPortAsync()
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var probe = new TcpClient();
                await probe.ConnectAsync(IPAddress.Loopback, Port);
                return;
            }
            catch (SocketException)
            {
                await Task.Delay(150);
            }
        }
        throw new TimeoutException($"sshd did not start listening on {Port}.");
    }

    private static int FindFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static string Run(string file, params string[] args)
    {
        var psi = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0 && file != "useradd")
            throw new InvalidOperationException($"{file} exited {p.ExitCode}: {stderr}{stdout}");
        return stdout;
    }

    private static void RunShell(string command) => Run("/bin/sh", "-c", command);

    public Task DisposeAsync()
    {
        if (IsSupported)
        {
            Stop();
            try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        }
        return Task.CompletedTask;
    }
}

[CollectionDefinition(nameof(SshServerCollection))]
public sealed class SshServerCollection : ICollectionFixture<SshServerFixture>;
