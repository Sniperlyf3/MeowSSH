using Microsoft.Playwright;

namespace MeowSSH.UI.Tests;

/// <summary>
/// Tailcat identities past the generate-and-export basics TailcatHubTests
/// covers: a server identity with its relay options, the list, a client's
/// public key, and moving a private identity in -- including the overwrite
/// guard.
/// </summary>
[Collection(nameof(TestHostCollection))]
public sealed class TailcatIdentityTests(TestHostFixture fixture)
{
    private async Task<IPage> OpenKeysAsync()
    {
        var page = await fixture.NewPageAsync();
        await page.GetByTestId("tab-tools").ClickAsync();
        await page.GetByTestId("open-tool-tailcat").ClickAsync();
        await page.GetByTestId("tailcat-section-keys").ClickAsync();
        await Assertions.Expect(page.GetByTestId("tailcat-identity-portability")).ToBeVisibleAsync();
        return page;
    }

    [Fact]
    public async Task AServerIdentityWithRelayOptionsJoinsTheList()
    {
        var page = await OpenKeysAsync();
        var generate = page.GetByTestId("tailcat-generate-key");
        await Assertions.Expect(generate).ToBeDisabledAsync();

        await page.GetByTestId("tailcat-key-name").FillAsync("nas-server");
        await page.GetByTestId("tailcat-key-type").SelectOptionAsync("false");
        await page.GetByTestId("tailcat-key-region").FillAsync("901");
        await page.GetByTestId("tailcat-key-fixed-region").CheckAsync();
        await page.GetByTestId("tailcat-key-embed-derp").CheckAsync();
        await Assertions.Expect(page.GetByTestId("tailcat-key-psk")).ToBeCheckedAsync();
        await page.GetByTestId("tailcat-key-derpmap").FillAsync("https://relay.home.example/derpmap.json");
        await generate.ClickAsync();

        await Assertions.Expect(page.GetByTestId("tailcat-generated-key")).ToHaveTextAsync("tc-test-generated");
        await Assertions.Expect(page.GetByTestId("tailcat-copy-generated-key")).ToContainTextAsync("Copy address");
        await page.GetByTestId("tailcat-refresh-keys").ClickAsync();
        await Assertions.Expect(page.GetByTestId("tailcat-key-row").Filter(new() { HasText = "nas-server" })).ToHaveCountAsync(1);
    }

    [Fact]
    public async Task AClientsPublicKeyIsShownForSharing()
    {
        var page = await OpenKeysAsync();
        await page.GetByTestId("tailcat-public-key-name").FillAsync("client-default");
        await page.GetByTestId("tailcat-show-public-key").ClickAsync();
        await Assertions.Expect(page.GetByTestId("tailcat-public-key")).ToHaveTextAsync("nodekey:test-client-public");
        await Assertions.Expect(page.GetByTestId("tailcat-copy-public-key")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task AnIdentityImportsButDoesNotReplaceOneWithoutConsent()
    {
        var page = await OpenKeysAsync();
        var import = page.GetByTestId("tailcat-import-key");
        await Assertions.Expect(import).ToBeDisabledAsync();

        await page.GetByTestId("tailcat-import-name").FillAsync("client-phone");
        await page.GetByTestId("tailcat-import-json").FillAsync("{\"Private\":\"privkey:imported\"}");
        await import.ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Status).Filter(new() { HasText = "Imported client-phone." })).ToBeVisibleAsync();

        // The same name again: refused until the overwrite box says otherwise.
        await page.GetByTestId("tailcat-import-name").FillAsync("client-phone");
        await page.GetByTestId("tailcat-import-json").FillAsync("{\"Private\":\"privkey:other\"}");
        await import.ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Alert)).ToContainTextAsync("already exists");
        await page.GetByTestId("tailcat-import-overwrite").CheckAsync();
        await import.ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Status).Filter(new() { HasText = "Imported client-phone." })).ToBeVisibleAsync();
    }
}
