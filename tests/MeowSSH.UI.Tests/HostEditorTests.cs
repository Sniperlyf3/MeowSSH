using Microsoft.Playwright;

namespace MeowSSH.UI.Tests;

[Collection(nameof(TestHostCollection))]
public class HostEditorTests(TestHostFixture fixture)
{
    [Fact]
    public async Task TheAddButtonOpensAnEmptyEditor()
    {
        var page = await fixture.NewPageAsync("/");

        await page.GetByTestId("add-host").ClickAsync();

        await Assertions.Expect(page.GetByTestId("host-editor")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("host-label")).ToHaveValueAsync("");
        // Deleting a host that does not exist yet is not an offer worth making.
        await Assertions.Expect(page.GetByTestId("delete-host")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task SavingIsRefusedUntilTheHostCanActuallyBeReached()
    {
        var page = await fixture.NewPageAsync("/?newhost");

        var save = page.GetByTestId("save-host");
        await Assertions.Expect(save).ToBeDisabledAsync();

        await page.GetByTestId("host-label").FillAsync("build-01");
        // A name with no address is a row in the list that cannot be connected to.
        await Assertions.Expect(save).ToBeDisabledAsync();

        await page.GetByTestId("host-address").FillAsync("build.example.com");
        await Assertions.Expect(save).ToBeEnabledAsync();
    }

    [Fact]
    public async Task ASavedHostAppearsInTheList()
    {
        var page = await fixture.NewPageAsync("/?newhost");
        await page.GetByTestId("host-label").FillAsync("new-machine");
        await page.GetByTestId("host-address").FillAsync("10.9.9.9");

        await page.GetByTestId("save-host").ClickAsync();

        await Assertions.Expect(page.GetByTestId("host-editor")).ToHaveCountAsync(0);
        await Assertions.Expect(page.GetByText("new-machine")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task EditingAHostArrivesWithItsFieldsFilledIn()
    {
        var page = await fixture.NewPageAsync("/");

        await page.GetByTestId("edit-host").First.ClickAsync();

        await Assertions.Expect(page.GetByTestId("host-editor")).ToBeVisibleAsync();
        // Blank fields on an edit screen read as "this host has no username",
        // and saving would then quietly erase one.
        await Assertions.Expect(page.GetByTestId("host-label")).Not.ToHaveValueAsync("");
        await Assertions.Expect(page.GetByTestId("host-address")).Not.ToHaveValueAsync("");
        await Assertions.Expect(page.GetByTestId("delete-host")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task ChoosingTailcatHidesTheFieldsItHasNoUseFor()
    {
        var page = await fixture.NewPageAsync("/?newhost");

        await page.GetByTestId("transport-Tailcat").ClickAsync();

        // The address is the credential, so a username and port are not merely
        // unnecessary -- filling them in would look like a setting that works.
        await Assertions.Expect(page.GetByTestId("host-username")).ToHaveCountAsync(0);
        await Assertions.Expect(page.GetByTestId("host-port")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task ChoosingTailscaleExplainsWhyThereIsNoKeyToPick()
    {
        var page = await fixture.NewPageAsync("/?newhost");

        await page.GetByTestId("transport-TailscaleSsh").ClickAsync();

        await Assertions.Expect(page.GetByTestId("tailscale-note")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("host-credential")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task TheTransportChoiceIsAnnouncedToAssistiveTechnology()
    {
        var page = await fixture.NewPageAsync("/?newhost");

        await page.GetByTestId("transport-Tailcat").ClickAsync();

        // aria-checked has to be the literal string, not a bare attribute:
        // a boolean attribute here reads as "checked" whatever its value.
        await Assertions.Expect(page.GetByTestId("transport-Tailcat")).ToHaveAttributeAsync("aria-checked", "true");
        await Assertions.Expect(page.GetByTestId("transport-Tcp")).ToHaveAttributeAsync("aria-checked", "false");
    }

    [Fact]
    public async Task CancellingLeavesTheListUnchanged()
    {
        var page = await fixture.NewPageAsync("/?newhost");
        await page.GetByTestId("host-label").FillAsync("never-saved");

        await page.GetByTestId("cancel-host").ClickAsync();

        await Assertions.Expect(page.GetByTestId("host-list").First).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByText("never-saved")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task DeletingAHostRemovesItFromTheList()
    {
        var page = await fixture.NewPageAsync("/");
        await page.GetByTestId("edit-host").First.ClickAsync();
        var label = await page.GetByTestId("host-label").InputValueAsync();

        await page.GetByTestId("delete-host").ClickAsync();

        await Assertions.Expect(page.GetByTestId("host-list").First).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByText(label, new() { Exact = true })).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task AHostnameIsNotUppercasedAsItIsTyped()
    {
        var page = await fixture.NewPageAsync("/?newhost");
        var address = page.GetByTestId("host-address");

        await address.FillAsync("Build.Example.COM");

        // The recovery-code field uppercases on purpose; that styling must not
        // reach a field holding a case-sensitive value.
        var transform = await address.EvaluateAsync<string>("el => getComputedStyle(el).textTransform");
        Assert.Equal("none", transform);
        await Assertions.Expect(address).ToHaveValueAsync("Build.Example.COM");
    }
}
