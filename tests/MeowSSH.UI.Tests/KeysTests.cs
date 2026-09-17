using Microsoft.Playwright;

namespace MeowSSH.UI.Tests;

[Collection(nameof(TestHostCollection))]
public class KeysTests(TestHostFixture fixture)
{
    private const string SampleKey = """
        -----BEGIN OPENSSH PRIVATE KEY-----
        b3BlbnNzaC1rZXktdjEAAAAABG5vbmUAAAAEbm9uZQAAAAAAAAABAAAAMwAAAAtzc2gtZW
        -----END OPENSSH PRIVATE KEY-----
        """;

    private static async Task OpenImportPrivateKeyAsync(IPage page)
    {
        await page.GetByTestId("add-credential").ClickAsync();
        await Assertions.Expect(page.GetByTestId("credential-flow-picker")).ToBeVisibleAsync();
        await page.GetByTestId("flow-import-key").ClickAsync();
        await Assertions.Expect(page.GetByTestId("credential-editor")).ToBeVisibleAsync();
    }

    private static async Task SaveImportedKeyAsync(IPage page, string name = "deploy key")
    {
        await OpenImportPrivateKeyAsync(page);
        await page.GetByTestId("credential-label").FillAsync(name);
        await page.GetByTestId("credential-secret").FillAsync(SampleKey);
        await page.GetByTestId("save-credential").ClickAsync();
    }

    [Fact]
    public async Task AnEmptyVaultSaysWhatKeysAreFor()
    {
        var page = await fixture.NewPageAsync("/?keys");
        await Assertions.Expect(page.GetByTestId("keys-empty")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task AddCredentialStartsWithFocusedFlowChoicesNotAFullEditor()
    {
        var page = await fixture.NewPageAsync("/?keys");
        await page.GetByTestId("add-credential").ClickAsync();

        await Assertions.Expect(page.GetByTestId("credential-flow-picker")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("flow-generate-key")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("flow-import-key")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("flow-device-key")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("flow-public-key")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("flow-password")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("credential-label")).ToHaveCountAsync(0);
        await Assertions.Expect(page.GetByTestId("credential-secret")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task SavingImportedKeyIsRefusedWithoutANameAndASecret()
    {
        var page = await fixture.NewPageAsync("/?keys");
        await OpenImportPrivateKeyAsync(page);

        var save = page.GetByTestId("save-credential");
        await Assertions.Expect(save).ToBeDisabledAsync();
        await page.GetByTestId("credential-label").FillAsync("deploy key");
        await Assertions.Expect(save).ToBeDisabledAsync();
        await page.GetByTestId("credential-secret").FillAsync(SampleKey);
        await Assertions.Expect(save).ToBeEnabledAsync();
    }

    [Fact]
    public async Task ASavedKeyIsListedWithoutItsSecret()
    {
        var page = await fixture.NewPageAsync("/?keys");
        await SaveImportedKeyAsync(page);

        await Assertions.Expect(page.GetByTestId("credential-row")).ToHaveCountAsync(1);
        await Assertions.Expect(page.GetByText("BEGIN OPENSSH PRIVATE KEY")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task AGeneratedSshKeyIsSavedWithItsPublicKeyAndCanBeReused()
    {
        var page = await fixture.NewPageAsync("/?keys");
        await page.GetByTestId("add-credential").ClickAsync();
        await page.GetByTestId("flow-generate-key").ClickAsync();
        await page.GetByTestId("credential-label").FillAsync("generated deploy key");
        await Assertions.Expect(page.GetByTestId("credential-secret")).ToHaveCountAsync(0);

        await page.GetByTestId("generate-ssh-key").ClickAsync();

        await Assertions.Expect(page.GetByTestId("generated-key-ready")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("generated-public-key"))
            .ToHaveValueAsync(new System.Text.RegularExpressions.Regex("^ssh-rsa "));
        await Assertions.Expect(page.GetByTestId("save-credential")).ToBeEnabledAsync();

        await page.GetByTestId("save-credential").ClickAsync();
        await Assertions.Expect(page.GetByTestId("show-public-key")).Not.ToBeVisibleAsync();
        await page.GetByTestId("manage-credential").Locator("summary").ClickAsync();
        await Assertions.Expect(page.GetByTestId("show-public-key")).ToBeVisibleAsync();

        await page.GetByTestId("show-public-key").ClickAsync();
        await Assertions.Expect(page.GetByTestId("public-key-value"))
            .ToHaveValueAsync(new System.Text.RegularExpressions.Regex("^ssh-rsa "));

        await page.GetByTestId("tab-hosts").ClickAsync();
        await page.GetByTestId("add-host").ClickAsync();
        await Assertions.Expect(page.GetByTestId("host-credential")).ToContainTextAsync("generated deploy key");
    }

    [Fact]
    public async Task PublicKeyCredentialCanAuthorizeTailcatShellButIsNotAnOutboundCredential()
    {
        var page = await fixture.NewPageAsync("/?keys");
        await page.GetByTestId("add-credential").ClickAsync();
        await page.GetByTestId("flow-public-key").ClickAsync();
        await page.GetByTestId("credential-label").FillAsync("ops laptop");
        await page.GetByTestId("credential-public-key").FillAsync("ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAITestKey ops@example");
        await page.GetByTestId("save-credential").ClickAsync();

        await page.GetByTestId("tab-tools").ClickAsync();
        await page.GetByTestId("open-tool-tailcat").ClickAsync();
        await Assertions.Expect(page.GetByTestId("tailcat-page")).ToBeVisibleAsync();
        await page.GetByTestId("tailcat-exit-node").UncheckAsync();
        await page.GetByTestId("tailcat-allowed-clients").FillAsync("nodekey:test-client");
        await page.GetByTestId("tailcat-shell").CheckAsync();

        await Assertions.Expect(page.GetByTestId("tailcat-saved-ssh-keys")).ToContainTextAsync("ops laptop");
        await page.GetByTestId("tailcat-saved-ssh-key").CheckAsync();
        await Assertions.Expect(page.GetByTestId("start-tailcat-server")).ToBeEnabledAsync();

        await page.GetByTestId("tab-hosts").ClickAsync();
        await page.GetByTestId("add-host").ClickAsync();
        await Assertions.Expect(page.GetByTestId("host-credential")).Not.ToContainTextAsync("ops laptop");
    }

    [Fact]
    public async Task APasswordFlowOnlyShowsPasswordFields()
    {
        var page = await fixture.NewPageAsync("/?keys");
        await page.GetByTestId("add-credential").ClickAsync();
        await page.GetByTestId("flow-password").ClickAsync();

        var secret = page.GetByTestId("credential-secret");
        await Assertions.Expect(secret).ToHaveAttributeAsync("type", "password");
        await Assertions.Expect(page.GetByTestId("credential-public-key")).ToHaveCountAsync(0);
        await Assertions.Expect(page.GetByTestId("generate-hardware-key")).ToHaveCountAsync(0);

        await page.GetByTestId("reveal-secret").ClickAsync();
        await Assertions.Expect(secret).ToHaveAttributeAsync("type", "text");
    }

    [Fact]
    public async Task APrivateKeyImportGetsARoomyFieldRatherThanASingleLine()
    {
        var page = await fixture.NewPageAsync("/?keys");
        await OpenImportPrivateKeyAsync(page);

        var tag = await page.GetByTestId("credential-secret").EvaluateAsync<string>("el => el.tagName");
        Assert.Equal("TEXTAREA", tag);
    }

    [Fact]
    public async Task APastedKeyIsNotUppercasedOrReflowed()
    {
        var page = await fixture.NewPageAsync("/?keys");
        await OpenImportPrivateKeyAsync(page);
        var secret = page.GetByTestId("credential-secret");

        await secret.FillAsync(SampleKey);

        var transform = await secret.EvaluateAsync<string>("el => getComputedStyle(el).textTransform");
        Assert.Equal("none", transform);
        await Assertions.Expect(secret).ToHaveValueAsync(SampleKey);
    }

    [Fact]
    public async Task ChangingCreationTypeClearsPrivateMaterialAfterExplicitDiscard()
    {
        var page = await fixture.NewPageAsync("/?keys");
        await OpenImportPrivateKeyAsync(page);
        await page.GetByTestId("credential-label").FillAsync("half typed");
        await page.GetByTestId("credential-secret").FillAsync(SampleKey);

        await page.GetByTestId("change-credential-type").ClickAsync();
        await Assertions.Expect(page.GetByTestId("discard-credential-draft")).ToBeVisibleAsync();
        await page.GetByTestId("discard-credential-draft-confirm").ClickAsync();
        await page.GetByTestId("flow-import-key").ClickAsync();

        await Assertions.Expect(page.GetByTestId("credential-secret")).ToHaveValueAsync("");
    }

    [Fact]
    public async Task SavedCredentialSecondaryActionsAreContextual()
    {
        var page = await fixture.NewPageAsync("/?keys");
        await SaveImportedKeyAsync(page);

        await Assertions.Expect(page.GetByTestId("delete-credential")).Not.ToBeVisibleAsync();
        await page.GetByTestId("manage-credential").Locator("summary").ClickAsync();
        await Assertions.Expect(page.GetByTestId("delete-credential")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task ADeletedKeyLeavesTheList()
    {
        var page = await fixture.NewPageAsync("/?keys");
        await SaveImportedKeyAsync(page);
        await page.GetByTestId("manage-credential").Locator("summary").ClickAsync();
        await page.GetByTestId("delete-credential").ClickAsync();

        await Assertions.Expect(page.GetByTestId("keys-empty")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task ASavedKeyCanBeChosenWhenEditingAHost()
    {
        var page = await fixture.NewPageAsync("/?keys");
        await SaveImportedKeyAsync(page);

        await page.GetByTestId("tab-hosts").ClickAsync();
        await page.GetByTestId("add-host").ClickAsync();
        await Assertions.Expect(page.GetByTestId("host-credential")).ToContainTextAsync("deploy key");
    }

    [Fact]
    public async Task KeysFlowDoesNotOverflowAtPhoneWidth()
    {
        var page = await fixture.NewPageAsync("/?keys");
        await page.GetByTestId("add-credential").ClickAsync();

        var overflows = await page.EvaluateAsync<bool>(
            "() => document.documentElement.scrollWidth > document.documentElement.clientWidth");
        Assert.False(overflows, "Credential creation picker scrolls horizontally at phone width.");
    }
}
