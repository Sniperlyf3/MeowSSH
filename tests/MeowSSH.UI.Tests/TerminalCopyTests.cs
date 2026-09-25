using Microsoft.Playwright;

namespace MeowSSH.UI.Tests;

/// <summary>
/// Regression tests for "I can't copy text from the terminal": on a phone a
/// long press opened the hidden input's paste/password popup and nothing could
/// be selected, because xterm.js only selects with a mouse.
/// </summary>
[Collection(nameof(TestHostCollection))]
public sealed class TerminalCopyTests(TestHostFixture fixture)
{
    // Real Touch/TouchEvent objects dispatched at the element under the point,
    // which is what a finger produces in the web view.
    private const string DispatchTouch = """
        ([type, x, y]) => {
            const target = document.elementFromPoint(x, y);
            const touch = new Touch({ identifier: 7, target, clientX: x, clientY: y });
            const active = type === "touchend" ? [] : [touch];
            return target.dispatchEvent(new TouchEvent(type, {
                touches: active, targetTouches: active, changedTouches: [touch], bubbles: true, cancelable: true }));
        }
        """;

    private async Task<IPage> OpenSessionAsync()
    {
        var page = await fixture.NewPageAsync("/", touch: true);
        await page.Context.GrantPermissionsAsync(["clipboard-read", "clipboard-write"]);
        await page.GetByTestId("host-row").First.ClickAsync();
        await Assertions.Expect(page.Locator(".xterm-screen")).ToBeVisibleAsync();
        await Assertions.Expect(page.Locator(".xterm-helper-textarea")).ToBeFocusedAsync();
        await page.Keyboard.TypeAsync("pwd");
        await page.Keyboard.PressAsync("Enter");
        await Assertions.Expect(OutputRow(page)).ToHaveCountAsync(1);
        return page;
    }

    /// <summary>The row the fake shell printed for pwd: "/home/deploy" and nothing else.</summary>
    private static ILocator OutputRow(IPage page) =>
        page.Locator(".xterm-rows > div").Filter(new() { HasTextRegex = new System.Text.RegularExpressions.Regex(@"^\s*/home/deploy\s*$") });

    // Measured in the page from where the browser drew the character: an
    // earlier version derived it in C# from bounding boxes and a guessed cell
    // width, and handed elementFromPoint a non-finite point.
    private static async Task<(double X, double Y)> PointInOutputAsync(IPage page, int column = 3)
    {
        await Assertions.Expect(OutputRow(page)).ToBeVisibleAsync();
        // The terminal refits after the session opens and the output row moves
        // with it; measuring once raced that and pressed on a blank line.
        var point = await Measure();
        for (var attempt = 0; attempt < 20; attempt++)
        {
            await page.WaitForTimeoutAsync(150);
            var again = await Measure();
            if (again.SequenceEqual(point)) break;
            point = again;
        }
        return (point[0], point[1]);

        Task<double[]> Measure() => page.EvaluateAsync<double[]>("""
            (column) => {
                const rows = [...document.querySelectorAll('.xterm-rows > div')];
                const row = rows.find(r => /^\s*\/home\/deploy\s*$/.test(r.textContent));
                // Walk to the text node holding that character and ask the
                // browser where it drew it, rather than assuming a cell width.
                const offset = row.textContent.indexOf('/') + column;
                const walker = document.createTreeWalker(row, NodeFilter.SHOW_TEXT);
                let seen = 0, node;
                while ((node = walker.nextNode()) && seen + node.length <= offset) seen += node.length;
                const range = document.createRange();
                range.setStart(node, offset - seen);
                range.setEnd(node, offset - seen + 1);
                const rect = range.getBoundingClientRect();
                return [rect.left + rect.width / 2, rect.top + rect.height / 2];
            }
            """, column);
    }

    private static async Task LongPressAsync(IPage page, (double X, double Y) at, (double X, double Y)? dragTo = null)
    {
        await page.EvaluateAsync(DispatchTouch, new object[] { "touchstart", at.X, at.Y });
        await page.WaitForTimeoutAsync(700);
        if (dragTo is { } to) await page.EvaluateAsync(DispatchTouch, new object[] { "touchmove", to.X, to.Y });
        await page.EvaluateAsync(DispatchTouch, new object[] { "touchend", (dragTo ?? at).X, (dragTo ?? at).Y });
    }

    private static Task<string> ClipboardAsync(IPage page) => page.EvaluateAsync<string>("() => navigator.clipboard.readText()");

    [Fact]
    public async Task ALongPressSelectsTheWholePathUnderTheFingerAndCopiesIt()
    {
        var page = await OpenSessionAsync();

        await LongPressAsync(page, await PointInOutputAsync(page));
        await page.GetByTestId("selection-copy").ClickAsync();

        await Assertions.Expect(page.GetByTestId("terminal-copy-status")).ToHaveTextAsync("Copied");
        Assert.Equal("/home/deploy", await ClipboardAsync(page));
        await Assertions.Expect(page.GetByTestId("selection-copy")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task DraggingAfterALongPressExtendsTheSelectionAcrossLines()
    {
        var page = await OpenSessionAsync();
        var at = await PointInOutputAsync(page);
        var row = (await OutputRow(page).BoundingBoxAsync())!;

        // Up one row, onto the line where "pwd" was typed.
        await LongPressAsync(page, at, dragTo: (at.X, at.Y - row.Height));
        await page.GetByTestId("selection-copy").ClickAsync();
        // The write is async; reading before it lands read an empty clipboard.
        await Assertions.Expect(page.GetByTestId("terminal-copy-status")).ToHaveTextAsync("Copied");

        var copied = await ClipboardAsync(page);
        Assert.Contains("pwd", copied, StringComparison.Ordinal);
        Assert.EndsWith("/home/deploy", copied.TrimEnd(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AShortTapClearsTheSelection()
    {
        var page = await OpenSessionAsync();
        var at = await PointInOutputAsync(page);
        await LongPressAsync(page, at);
        await Assertions.Expect(page.GetByTestId("selection-copy")).ToBeVisibleAsync();

        await page.EvaluateAsync(DispatchTouch, new object[] { "touchstart", at.X, at.Y });
        await page.EvaluateAsync(DispatchTouch, new object[] { "touchend", at.X, at.Y });

        await Assertions.Expect(page.GetByTestId("selection-copy")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task TheLongPressMenuThatOfferedPasteAndPasswordsIsSuppressed()
    {
        var page = await OpenSessionAsync();
        var at = await PointInOutputAsync(page);

        var notCancelled = await page.EvaluateAsync<bool>("""
            ([x, y]) => document.elementFromPoint(x, y).dispatchEvent(
                new MouseEvent("contextmenu", { bubbles: true, cancelable: true, clientX: x, clientY: y }))
            """, new object[] { at.X, at.Y });

        Assert.False(notCancelled);
    }

    [Fact]
    public async Task CopyWithNothingSelectedCopiesTheScreenAndSaysSo()
    {
        var page = await OpenSessionAsync();

        await page.GetByTestId("key-copy").ClickAsync();

        await Assertions.Expect(page.GetByTestId("terminal-copy-status")).ToHaveTextAsync("Copied the screen");
        var copied = await ClipboardAsync(page);
        Assert.Contains("pwd", copied, StringComparison.Ordinal);
        Assert.Contains("/home/deploy", copied, StringComparison.Ordinal);
    }
}
