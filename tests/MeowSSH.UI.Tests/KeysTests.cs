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

    [Fact]
    public async Task AnEmptyVaultSaysWhatKeysAreFor()
    {
        var page = await fixture.NewPageAsync("/?keys");

        await Assertions.Expect(page.GetByTestId("keys-empty")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task SavingIsRefusedWithoutANameAndASecret()
    {
        var page = await fixture.NewPageAsync("/?keys");
        await page.GetByTestId("add-credential").ClickAsync();

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
        await page.GetByTestId("add-credential").ClickAsync();
        await page.GetByTestId("credential-label").FillAsync("deploy key");
        await page.GetByTestId("credential-secret").FillAsync(SampleKey);

        await page.GetByTestId("save-credential").ClickAsync();

        await Assertions.Expect(page.GetByTestId("credential-row")).ToHaveCountAsync(1);
        // The list is built from summaries that have nowhere to put a secret.
        // Proving the key body never reaches the page is the whole point.
        await Assertions.Expect(page.GetByText("BEGIN OPENSSH PRIVATE KEY")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task AGeneratedSshKeyIsSavedWithItsPublicKeyAndCanBeReused()
    {
        var page = await fixture.NewPageAsync("/?keys");
        await page.GetByTestId("add-credential").ClickAsync();
        await page.GetByTestId("credential-label").FillAsync("generated deploy key");

        await page.GetByTestId("generate-ssh-key").ClickAsync();

        await Assertions.Expect(page.GetByTestId("credential-secret"))
            .ToContainTextAsync("BEGIN PRIVATE KEY");
        await Assertions.Expect(page.GetByTestId("generated-public-key"))
            .ToHaveValueAsync(new System.Text.RegularExpressions.Regex("^ssh-rsa "));
        await Assertions.Expect(page.GetByTestId("save-credential")).ToBeEnabledAsync();

        await page.GetByTestId("save-credential").ClickAsync();
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
        await page.GetByTestId("credential-label").FillAsync("ops laptop");
        await page.GetByTestId("kind-public-key").ClickAsync();
        await page.GetByTestId("credential-public-key").FillAsync("ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAITestKey ops@example");
        await page.GetByTestId("save-credential").ClickAsync();

        await page.GetByTestId("tab-tailcat").ClickAsync();
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
    public async Task APasswordIsMaskedUntilTheUserAsksToSeeIt()
    {
        var page = await fixture.NewPageAsync("/?keys");
        await page.GetByTestId("add-credential").ClickAsync();
        await page.GetByTestId("kind-password").ClickAsync();

        var secret = page.GetByTestId("credential-secret");
        await Assertions.Expect(secret).ToHaveAttributeAsync("type", "password");

        await page.GetByTestId("reveal-secret").ClickAsync();
        await Assertions.Expect(secret).ToHaveAttributeAsync("type", "text");
    }

    [Fact]
    public async Task APrivateKeyGetsARoomyFieldRatherThanASingleLine()
    {
        var page = await fixture.NewPageAsync("/?keys");
        await page.GetByTestId("add-credential").ClickAsync();

        // A key pasted into a one-line input is unreviewable, and the default
        // kind is the one people paste keys into.
        var tag = await page.GetByTestId("credential-secret").EvaluateAsync<string>("el => el.tagName");
        Assert.Equal("TEXTAREA", tag);
    }

    [Fact]
    public async Task APastedKeyIsNotUppercasedOrReflowed()
    {
        var page = await fixture.NewPageAsync("/?keys");
        await page.GetByTestId("add-credential").ClickAsync();
        var secret = page.GetByTestId("credential-secret");

        await secret.FillAsync(SampleKey);

        // Base64 is case-sensitive. Uppercasing it in CSS would leave a key
        // that looks right on screen and is silently corrupt.
        var transform = await secret.EvaluateAsync<string>("el => getComputedStyle(el).textTransform");
        Assert.Equal("none", transform);
        await Assertions.Expect(secret).ToHaveValueAsync(SampleKey);
    }

    [Fact]
    public async Task CancellingClearsTheSecretRatherThanKeepingItAround()
    {
        var page = await fixture.NewPageAsync("/?keys");
        await page.GetByTestId("add-credential").ClickAsync();
        await page.GetByTestId("credential-label").FillAsync("half typed");
        await page.GetByTestId("credential-secret").FillAsync(SampleKey);

        await page.GetByTestId("cancel-credential").ClickAsync();
        await page.GetByTestId("add-credential").ClickAsync();

        // A half-typed private key still sitting in the field on the next visit
        // is a secret nobody meant to keep.
        await Assertions.Expect(page.GetByTestId("credential-secret")).ToHaveValueAsync("");
    }

    [Fact]
    public async Task ADeletedKeyLeavesTheList()
    {
        var page = await fixture.NewPageAsync("/?keys");
        await page.GetByTestId("add-credential").ClickAsync();
        await page.GetByTestId("credential-label").FillAsync("deploy key");
        await page.GetByTestId("credential-secret").FillAsync(SampleKey);
        await page.GetByTestId("save-credential").ClickAsync();

        await page.GetByTestId("delete-credential").ClickAsync();

        await Assertions.Expect(page.GetByTestId("keys-empty")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task ASavedKeyCanBeChosenWhenEditingAHost()
    {
        var page = await fixture.NewPageAsync("/?keys");
        await page.GetByTestId("add-credential").ClickAsync();
        await page.GetByTestId("credential-label").FillAsync("deploy key");
        await page.GetByTestId("credential-secret").FillAsync(SampleKey);
        await page.GetByTestId("save-credential").ClickAsync();

        await page.GetByTestId("tab-hosts").ClickAsync();
        await page.GetByTestId("add-host").ClickAsync();

        // The two screens are only useful together: a key nobody can point a
        // host at is a key that does nothing.
        await Assertions.Expect(page.GetByTestId("host-credential")).ToContainTextAsync("deploy key");
    }
}
