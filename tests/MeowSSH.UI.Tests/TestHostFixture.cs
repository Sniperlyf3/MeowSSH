using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Playwright;

namespace MeowSSH.UI.Tests;

/// <summary>
/// Starts the Blazor test host and a browser once for the whole suite.
/// </summary>
/// <remarks>
/// This is what the MAUI Blazor Hybrid choice buys: the Android app's UI is
/// ordinary Razor components in a WebView, so the same components run in a
/// browser and can be driven on Linux CI. No emulator, no device farm.
/// </remarks>
public sealed class TestHostFixture : IAsyncLifetime
{
    private Process? _host;
    private IPlaywright? _playwright;

    public IBrowser Browser { get; private set; } = default!;

    public string BaseUrl { get; private set; } = "";

    public async Task InitializeAsync()
    {
        var port = FindFreePort();
        BaseUrl = $"http://127.0.0.1:{port}";

        var projectDirectory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/MeowSSH.TestHost"));
        _host = Process.Start(new ProcessStartInfo("dotnet")
        {
            // -c matters: without it "dotnet run" defaults to Debug and looks for
            // a binary the Release build never produced. Locally that failed to
            // show up because stale Debug output was lying around and got used
            // instead; on a clean CI checkout every UI test died in 87ms.
            ArgumentList = { "run", "--project", projectDirectory, "-c", BuildConfiguration, "--no-build", "--urls", BaseUrl },
            Environment =
            {
                ["ASPNETCORE_ENVIRONMENT"] = "Development",
                // launchSettings would otherwise override --urls and bind a port
                // the test does not know about.
                ["DOTNET_LAUNCH_PROFILE"] = "",
            },
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        }) ?? throw new InvalidOperationException("Could not start the test host.");

        await WaitForReadyAsync();

        _playwright = await Playwright.CreateAsync();
        Browser = await _playwright.Chromium.LaunchAsync(new()
        {
            Headless = true,
            ExecutablePath = FindPreinstalledChromium(),
        });
    }

    /// <summary>
    /// Locates a Chromium that is already on the machine, rather than making the
    /// suite download one.
    /// </summary>
    /// <remarks>
    /// Playwright pins an exact browser build per release, so a environment that
    /// ships its own Chromium will almost never match the build the current
    /// Playwright package expects. Pointing at the installed binary is what keeps
    /// the suite runnable in a sandbox with no browser download. Returning null
    /// lets Playwright use its own managed browser wherever one is installed
    /// normally, so this costs nothing on a developer machine.
    /// </remarks>
    private static string? FindPreinstalledChromium()
    {
        if (Environment.GetEnvironmentVariable("MEOWSSH_CHROMIUM") is { Length: > 0 } explicitPath)
            return File.Exists(explicitPath) ? explicitPath : null;

        var root = Environment.GetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH");
        if (string.IsNullOrEmpty(root) || !System.IO.Directory.Exists(root)) return null;

        return System.IO.Directory
            .EnumerateFiles(root, "chrome", SearchOption.AllDirectories)
            .Concat(System.IO.Directory.EnumerateFiles(root, "headless_shell", SearchOption.AllDirectories))
            .FirstOrDefault();
    }

    /// <summary>Opens a phone-sized page, the only viewport this app ships to.</summary>
    public async Task<IPage> NewPageAsync(string path = "/")
    {
        var context = await Browser.NewContextAsync(new()
        {
            ViewportSize = new ViewportSize { Width = 390, Height = 844 },
            IsMobile = false,
        });
        var page = await context.NewPageAsync();
        await page.GotoAsync(BaseUrl + path);

        // Blazor server-renders the markup first and wires up handlers only once
        // the circuit connects. Acting before that point clicks a button that
        // exists but does nothing, which looks exactly like a broken feature.
        await WaitForInteractiveAsync(page);
        return page;
    }

    /// <summary>
    /// A reload starts a new circuit and has exactly the same not-yet-wired
    /// window as the first load. Tests that reloaded without this clicked into
    /// it and intermittently timed out 30 s later waiting for the next screen.
    /// </summary>
    public static async Task ReloadAsync(IPage page)
    {
        await page.ReloadAsync();
        await WaitForInteractiveAsync(page);
    }

    private static async Task WaitForInteractiveAsync(IPage page) =>
        await page.WaitForSelectorAsync("[data-testid=interactive]", new()
        {
            State = WaitForSelectorState.Attached,
            Timeout = 30_000,
        });

    private async Task WaitForReadyAsync()
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (DateTime.UtcNow < deadline)
        {
            if (_host!.HasExited)
                throw new InvalidOperationException(
                    $"Test host exited early with code {_host.ExitCode}: {await _host.StandardError.ReadToEndAsync()}");
            try
            {
                var response = await client.GetAsync(BaseUrl + "/");
                if (response.IsSuccessStatusCode) return;
            }
            catch (HttpRequestException) { /* not listening yet */ }
            catch (TaskCanceledException) { /* not listening yet */ }
            await Task.Delay(250);
        }
        throw new TimeoutException($"Test host did not become ready at {BaseUrl}.");
    }

    /// <summary>
    /// The configuration this test assembly was compiled in, which is the one
    /// the test host was built in too.
    /// </summary>
    /// <remarks>
    /// A compile-time constant rather than something parsed out of a path: it
    /// cannot disagree with how the assembly was actually built.
    /// </remarks>
    private static string BuildConfiguration =>
#if DEBUG
        "Debug";
#else
        "Release";
#endif

    private static int FindFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    public async Task DisposeAsync()
    {
        if (Browser is not null) await Browser.CloseAsync();
        _playwright?.Dispose();
        if (_host is { HasExited: false })
        {
            _host.Kill(entireProcessTree: true);
            await _host.WaitForExitAsync();
        }
        _host?.Dispose();
    }
}

[CollectionDefinition(nameof(TestHostCollection))]
public sealed class TestHostCollection : ICollectionFixture<TestHostFixture>;
