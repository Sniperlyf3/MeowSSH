using Microsoft.Playwright;

namespace MeowSSH.UI.Tests;

/// <summary>
/// Free-tier coverage for the Pro features that render a lock card:
/// transfer queue, host health dashboard, Tailcat workspaces and Tailcat
/// temporary sharing. <see cref="MeowSSH.TestHost.Fakes.FakeEntitlementService"/>
/// grants Pro to every page except one opened with "free" in the query
/// string (<c>NewPageAsync("/?free")</c>) -- every other test in this
/// project relies on the always-Pro default, so this is the only page in
/// the whole suite that ever sees the Free path.
/// </summary>
/// <remarks>
/// Each test asserts both halves of the gate: the lock card is there with
/// its explanation, and the Pro control is not merely dimmed but gone from
/// the render tree entirely (a real <c>ToHaveCountAsync(0)</c>, not a
/// visibility check) -- or, where the control is deliberately left clickable
/// (queue-upload from Files; see FilesPage.razor's comment), that clicking
/// it hits the Core-level throw and queues nothing. A regression that made
/// any of these render its Pro half regardless of tier is exactly what each
/// test was mutated against -- see the commit message for the mutation
/// checks run on this file.
/// </remarks>
[Collection(nameof(TestHostCollection))]
public sealed class FreeTierProGateTests(TestHostFixture fixture)
{
    [Fact]
    public async Task TransferQueueToolsPageShowsLockCardWithNoQueueOrHistoryControls()
    {
        var page = await fixture.NewPageAsync("/?free");
        await page.GetByTestId("tab-tools").ClickAsync();
        await page.GetByTestId("open-tool-transferqueue").ClickAsync();
        await Assertions.Expect(page.GetByTestId("transfer-queue-page")).ToBeVisibleAsync();

        var gate = page.GetByTestId("transfer-queue-pro-gate");
        await Assertions.Expect(gate).ToBeVisibleAsync();
        await Assertions.Expect(gate).ToContainTextAsync("MeowSSH Pro required");
        await Assertions.Expect(gate).ToContainTextAsync("local record of what");

        // Both real sections render their heading even with nothing queued or
        // in history (see HostHealthDashboardTests's Pro counterpart and
        // TransferQueueTests), so their absence here -- not just their
        // buttons' -- proves the whole Pro half never rendered rather than an
        // empty state that happens to look similar.
        await Assertions.Expect(page.Locator("#transfer-queue-active-title")).ToHaveCountAsync(0);
        await Assertions.Expect(page.Locator("#transfer-history-title")).ToHaveCountAsync(0);
        await Assertions.Expect(page.GetByTestId("transfer-queue-empty")).ToHaveCountAsync(0);
        await Assertions.Expect(page.GetByTestId("transfer-history-empty")).ToHaveCountAsync(0);
        await Assertions.Expect(page.GetByTestId("clear-finished-transfers")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task QueueDownloadIsGenuinelyDisabledForFreeTierWithHint()
    {
        var page = await fixture.NewPageAsync("/?files&free");
        await Assertions.Expect(page.GetByTestId("filelist")).ToBeVisibleAsync();

        await page.Locator("[data-testid=file-actions][data-file-name='start.sh']").ClickAsync();
        var queueDownload = page.GetByTestId("queue-download");
        var hint = page.GetByTestId("queue-download-hint");

        // A real disabled attribute, not a class: Playwright's own actionability
        // checks refuse to click it, which a CSS-only "looks disabled" control
        // would not stop.
        await Assertions.Expect(queueDownload).ToBeDisabledAsync();
        await Assertions.Expect(hint).ToBeVisibleAsync();
        await Assertions.Expect(hint).ToContainTextAsync("requires MeowSSH Pro");
        await Assertions.Expect(queueDownload).ToHaveAttributeAsync("aria-describedby", "queue-download-hint");
    }

    [Fact]
    public async Task QueueUploadIsLeftClickableButCoreRefusesItAndQueuesNothingForFreeTier()
    {
        // FilesPage.razor deliberately does not disable queue-upload (a control
        // that vanishes below Pro would hide the feature entirely) and instead
        // relies on TransferQueueService.EnqueueAsync throwing. This is the test
        // that would catch it if that Core-level throw were ever removed while
        // the UI-side leniency stayed -- a real entitlement bypass, not just a
        // cosmetic gap.
        var page = await fixture.NewPageAsync("/?files&free");
        await Assertions.Expect(page.GetByTestId("filelist")).ToBeVisibleAsync();

        await page.GetByTestId("queue-upload").ClickAsync();
        await Assertions.Expect(page.GetByTestId("upload-conflict")).ToBeVisibleAsync();
        await page.GetByTestId("replace-upload").ClickAsync();

        var error = page.GetByTestId("files-error");
        await Assertions.Expect(error).ToBeVisibleAsync();
        await Assertions.Expect(error).ToContainTextAsync("Queuing transfers requires MeowSSH Pro.");

        // The queue panel is itself gated (TransferQueueContents), so the
        // strongest available proof that nothing was queued is that opening it
        // shows the same lock card rather than an item for start.sh.
        await page.GetByTestId("open-transfer-queue").ClickAsync();
        await Assertions.Expect(page.GetByTestId("transfer-queue-pro-gate")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("transfer-queue-item")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task HostHealthDashboardShowsLockCardWithNoRunButton()
    {
        var page = await fixture.NewPageAsync("/?free");
        await page.GetByTestId("tab-tools").ClickAsync();
        await page.GetByTestId("open-tool-health").ClickAsync();
        await Assertions.Expect(page.GetByTestId("host-health-dashboard")).ToBeVisibleAsync();

        var gate = page.GetByTestId("host-health-pro-gate");
        await Assertions.Expect(gate).ToBeVisibleAsync();
        await Assertions.Expect(gate).ToContainTextAsync("MeowSSH Pro required");
        await Assertions.Expect(gate).ToContainTextAsync("Fleet health checks");

        await Assertions.Expect(page.GetByTestId("refresh-host-health")).ToHaveCountAsync(0);
        await Assertions.Expect(page.GetByTestId("host-health-results")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task TailcatWorkspacesShowsLockCardWithNoWorkspaceControls()
    {
        var page = await fixture.NewPageAsync("/?free");
        await page.GetByTestId("tab-tools").ClickAsync();
        await page.GetByTestId("open-tool-tailcat").ClickAsync();
        await Assertions.Expect(page.GetByTestId("tailcat-page")).ToBeVisibleAsync();
        await page.GetByTestId("tailcat-section-workspaces").ClickAsync();
        await Assertions.Expect(page.GetByTestId("tailcat-workspaces")).ToBeVisibleAsync();

        var gate = page.GetByTestId("tailcat-workspaces-locked");
        await Assertions.Expect(gate).ToBeVisibleAsync();
        await Assertions.Expect(gate).ToContainTextAsync("Tailcat Workspaces");
        await Assertions.Expect(gate).ToContainTextAsync("Upgrade to MeowSSH Pro to create or start workspaces.");

        await Assertions.Expect(page.GetByTestId("new-tailcat-workspace")).ToHaveCountAsync(0);
        await Assertions.Expect(page.GetByTestId("tailcat-workspaces-empty")).ToHaveCountAsync(0);
        await Assertions.Expect(page.GetByTestId("tailcat-workspace-row")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task TailcatTemporaryShareShowsLockCardWithNoShareForm()
    {
        var page = await fixture.NewPageAsync("/?free");
        await page.GetByTestId("tab-tools").ClickAsync();
        await page.GetByTestId("open-tool-tailcat").ClickAsync();
        await Assertions.Expect(page.GetByTestId("tailcat-page")).ToBeVisibleAsync();
        await page.GetByTestId("tailcat-section-share").ClickAsync();
        await Assertions.Expect(page.GetByTestId("tailcat-temporary-share")).ToBeVisibleAsync();

        var gate = page.GetByTestId("tailcat-temporary-share-locked");
        await Assertions.Expect(gate).ToBeVisibleAsync();
        await Assertions.Expect(gate).ToContainTextAsync("Temporary sharing");
        await Assertions.Expect(gate).ToContainTextAsync("Upgrade to MeowSSH Pro to create a temporary share.");

        await Assertions.Expect(page.GetByTestId("start-temporary-share")).ToHaveCountAsync(0);
        await Assertions.Expect(page.GetByTestId("tailcat-temporary-share-targets")).ToHaveCountAsync(0);
        await Assertions.Expect(page.GetByTestId("tailcat-temporary-share-clients")).ToHaveCountAsync(0);
        await Assertions.Expect(page.GetByTestId("tailcat-temporary-share-history")).ToHaveCountAsync(0);
    }
}
