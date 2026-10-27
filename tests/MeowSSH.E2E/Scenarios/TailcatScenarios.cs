using MeowSSH.Core.Model;
using MeowSSH.Core.Services;
using MeowSSH.Core.Ssh;
using Xunit;

namespace MeowSSH.E2E.Scenarios;

/// <summary>
/// Tailcat on the device: the phone serves a shell and files over Tailcat,
/// and the same app connects to the address it published, as a second phone
/// would.
/// </summary>
/// <remarks>
/// The server runs on a loopback relay (TS_DEBUG_TAILCAT_LOCAL_DERP with
/// TAILCAT_DERPMAP_URL=none), set for the server process only, the way
/// Meowshell's own E2E suite does it. The client only ever dials the address
/// the server just published, which is exactly the case that mode is for (see
/// Meowshell's CLAUDE.md on the DERP trap). A run must never reach the public
/// relay: "tailcat Ping: context deadline exceeded" means something did.
/// </remarks>
public static class TailcatScenarios
{
    public static IReadOnlyList<E2EScenario> All { get; } =
    [
        new("tailcat.serve-shell-and-files", PhoneServesShellAndFilesOverTailcat),
    ];

    private static readonly SemaphoreSlim HermeticEnvironment = new(1, 1);

    /// <summary>Starts the hub's server with the loopback relay in its environment only.</summary>
    private static async Task<string> StartHermeticServerAsync(ITailcatHubService hub, TailcatServeRequest request)
    {
        await HermeticEnvironment.WaitAsync();
        var previousLocal = Environment.GetEnvironmentVariable("TS_DEBUG_TAILCAT_LOCAL_DERP");
        var previousMap = Environment.GetEnvironmentVariable("TAILCAT_DERPMAP_URL");
        try
        {
            // Inherited by the server process started inside StartServerAsync,
            // then put back before any client process starts.
            Environment.SetEnvironmentVariable("TS_DEBUG_TAILCAT_LOCAL_DERP", "1");
            Environment.SetEnvironmentVariable("TAILCAT_DERPMAP_URL", "none");
            await hub.StartServerAsync(request);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TS_DEBUG_TAILCAT_LOCAL_DERP", previousLocal);
            Environment.SetEnvironmentVariable("TAILCAT_DERPMAP_URL", previousMap);
            HermeticEnvironment.Release();
        }
        return hub.Snapshot.Server?.Address ?? throw new InvalidOperationException("The server started but published no address.");
    }

    private static async Task PhoneServesShellAndFilesOverTailcat(E2EContext c)
    {
        var app = await c.StartAppAsync();
        var hub = app.Get<ITailcatHubService>();
        var shared = Directory.CreateDirectory(Path.Combine(app.DataDirectory, "shared")).FullName;
        await File.WriteAllTextAsync(Path.Combine(shared, "hello.txt"), "from the phone");

        // The identity the Keys screen shows the user, and the one they would
        // paste into the other phone's allowed-clients list. Every client
        // operation below has to present this same identity to get in.
        var clientKey = await hub.GetClientPublicKeyAsync();
        Assert.StartsWith("nodekey:", clientKey, StringComparison.Ordinal);
        Assert.Equal(clientKey, await hub.GetClientPublicKeyAsync());

        // 1. Files, and a shell guarded by the Tailcat allow list alone.
        var address = await StartHermeticServerAsync(hub, new TailcatServeRequest(
            Lifetime: TimeSpan.FromMinutes(10),
            AllowedClientKeys: clientKey,
            EnableShell: true,
            AuthorizedSshKeys: null,
            EnableFiles: true,
            SharedFolder: shared,
            FileMode: "rw",
            EnableExitNode: false,
            UseTailcatCredentialForShell: true));
        c.Log("serving files at " + address);
        try
        {
            var listed = await hub.ListRemoteFilesAsync(address, "/");
            Assert.Contains(listed, f => f.Name == "hello.txt");
            var downloaded = Path.Combine(app.DataDirectory, "hello-back.txt");
            await hub.DownloadAsync(address, "/hello.txt", downloaded);
            Assert.Equal("from the phone", await File.ReadAllTextAsync(downloaded));
            var upload = Path.Combine(app.DataDirectory, "up.txt");
            await File.WriteAllTextAsync(upload, "to the phone");
            await hub.UploadAsync(upload, address, "/up.txt");
            Assert.Equal("to the phone", await File.ReadAllTextAsync(Path.Combine(shared, "up.txt")));

            // A saved host on the Tailcat transport, with no SSH key at all:
            // the allow-listed identity is the credential.
            var host = await app.AddHostAsync("this phone over tailcat", address, 22, null, h => h with { Transport = SshTransport.Tailcat, Username = "e2e" });
            await RunShellOverTailcatAsync(app, host, shared);
        }
        catch
        {
            foreach (var line in hub.Snapshot.RecentLogLines) c.Log("server: " + line);
            throw;
        }
        finally
        {
            await hub.StopServerAsync();
        }
        Assert.Null(hub.Snapshot.Server);

        // 2. A shell that also requires an authorized SSH key.
        address = await StartHermeticServerAsync(hub, new TailcatServeRequest(
            Lifetime: TimeSpan.FromMinutes(10),
            AllowedClientKeys: clientKey,
            EnableShell: true,
            AuthorizedSshKeys: "ssh-ed25519 " + c.Server.ClientPublicKeyBody + " e2e",
            EnableFiles: false,
            SharedFolder: null,
            FileMode: "ro",
            EnableExitNode: false));
        c.Log("serving a key-authenticated shell at " + address);
        try
        {
            var key = await app.AddPrivateKeyAsync("e2e", c.Server.ClientPrivateKey);
            var keyed = await app.AddHostAsync("this phone, key auth", address, 22, key, h => h with { Transport = SshTransport.Tailcat });
            await RunShellOverTailcatAsync(app, keyed, shared);

            // Without the key, the same address and identity are refused.
            var keyless = await app.AddHostAsync("this phone, no key", address, 22, null, h => h with { Transport = SshTransport.Tailcat, Username = "e2e" });
            await Assert.ThrowsAsync<SshException>(() => app.ConnectAsync(keyless));
        }
        catch
        {
            foreach (var line in hub.Snapshot.RecentLogLines) c.Log("server: " + line);
            throw;
        }
        finally
        {
            await hub.StopServerAsync();
        }
    }

    private static async Task RunShellOverTailcatAsync(E2EApp app, HostRecord host, string shared)
    {
        var asked = app.HostKeyPrompts.Count;
        await using var connection = await app.ConnectAsync(host);
        // The address is the capability: there is no host key to ask about.
        Assert.Equal(asked, app.HostKeyPrompts.Count);
        await using var terminal = new E2ETerminal(await connection.OpenTerminalAsync(80, 24));
        Assert.Equal("42", await terminal.CheckAsync("echo $((6*7))", TimeSpan.FromSeconds(60)));
        Assert.Equal("from the phone", await terminal.CheckAsync($"cat '{shared}/hello.txt'"));
    }
}
