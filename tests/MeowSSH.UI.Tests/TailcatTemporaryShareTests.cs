using Microsoft.Playwright;

namespace MeowSSH.UI.Tests;

[Collection(nameof(TestHostCollection))]
public sealed class TailcatTemporaryShareTests(TestHostFixture fixture)
{
    private async Task<IPage> OpenTemporaryShareAsync()
    {
        var page = await fixture.NewPageAsync();
        await page.GetByTestId("tab-tools").ClickAsync();
        await page.GetByTestId("open-tool-tailcat").ClickAsync();
        await Assertions.Expect(page.GetByTestId("tailcat-page")).ToBeVisibleAsync();
        await page.GetByTestId("tailcat-section-share").ClickAsync();
        await Assertions.Expect(page.GetByTestId("tailcat-temporary-share")).ToBeVisibleAsync();
        return page;
    }

    [Fact]
    public async Task StartingAShareShowsTheGrantAndAllowsRevoke()
    {
        var page = await OpenTemporaryShareAsync();

        await page.GetByTestId("tailcat-temporary-share-label").FillAsync("Guest dev access");
        await page.GetByTestId("tailcat-temporary-share-targets").FillAsync("8080");
        await page.GetByTestId("tailcat-temporary-share-clients").FillAsync("nodekey:test-client");
        await page.GetByTestId("start-temporary-share").ClickAsync();

        var active = page.GetByTestId("tailcat-temporary-share-active");
        await Assertions.Expect(active).ToBeVisibleAsync();
        await Assertions.Expect(active).ToContainTextAsync("Guest dev access");

        // What is granted, and what explicitly is not, must both be stated --
        // a share is a live credential and the person configuring it should
        // never have to guess its scope.
        var grant = page.GetByTestId("tailcat-temporary-share-grant");
        await Assertions.Expect(grant).ToContainTextAsync("8080");
        await Assertions.Expect(grant).ToContainTextAsync("Not granted: shell, files, or an exit node");
        await Assertions.Expect(page.GetByTestId("tailcat-temporary-share-expires")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("tailcat-temporary-share-address")).ToHaveValueAsync("tc-test-address");

        // Once a share is live, the create form is gone -- the only way to get
        // a second one is through the same revoke button below, never a second
        // concurrent "start".
        await Assertions.Expect(page.GetByTestId("start-temporary-share")).ToHaveCountAsync(0);

        await page.GetByTestId("revoke-temporary-share").ClickAsync();

        await Assertions.Expect(active).ToHaveCountAsync(0);
        await Assertions.Expect(page.GetByTestId("start-temporary-share")).ToBeVisibleAsync();

        var historyRow = page.GetByTestId("tailcat-temporary-share-history-row").First;
        await Assertions.Expect(historyRow).ToContainTextAsync("Guest dev access");
        await Assertions.Expect(page.GetByTestId("tailcat-temporary-share-history-status").First).ToContainTextAsync("Revoked");
    }

    [Fact]
    public async Task DisabledStartNamesTheMissingCondition()
    {
        var page = await OpenTemporaryShareAsync();

        var start = page.GetByTestId("start-temporary-share");
        var hint = page.GetByTestId("start-temporary-share-hint");

        await Assertions.Expect(start).ToBeDisabledAsync();
        await Assertions.Expect(hint).ToBeVisibleAsync();
        await Assertions.Expect(hint).ToContainTextAsync("Choose at least one service or port");

        await page.GetByTestId("tailcat-temporary-share-targets").FillAsync("8080");
        await Assertions.Expect(start).ToBeDisabledAsync();
        // Choosing a target is not the only condition: no client is named yet,
        // so the hint should now name that instead of vanishing outright.
        await Assertions.Expect(hint).ToContainTextAsync("say who may connect");

        await page.GetByTestId("tailcat-temporary-share-clients").FillAsync("nodekey:test-client");
        await Assertions.Expect(start).ToBeEnabledAsync();
        await Assertions.Expect(hint).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task QrCodeCanBeShownAndHiddenForTheLiveAddress()
    {
        var page = await OpenTemporaryShareAsync();
        await page.GetByTestId("tailcat-temporary-share-targets").FillAsync("8080");
        await page.GetByTestId("tailcat-temporary-share-clients").FillAsync("nodekey:test-client");
        await page.GetByTestId("start-temporary-share").ClickAsync();
        await Assertions.Expect(page.GetByTestId("tailcat-temporary-share-active")).ToBeVisibleAsync();

        var toggle = page.GetByTestId("tailcat-temporary-share-toggle-qr");
        await Assertions.Expect(page.GetByTestId("tailcat-temporary-share-qr")).ToHaveCountAsync(0);

        await toggle.ClickAsync();
        await Assertions.Expect(page.GetByTestId("tailcat-temporary-share-qr")).ToBeVisibleAsync();

        await toggle.ClickAsync();
        await Assertions.Expect(page.GetByTestId("tailcat-temporary-share-qr")).ToHaveCountAsync(0);
    }
}
