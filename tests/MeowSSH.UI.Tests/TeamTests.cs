using Microsoft.Playwright;

namespace MeowSSH.UI.Tests;

[Collection(nameof(TestHostCollection))]
public sealed class TeamTests(TestHostFixture fixture)
{
    private static readonly string[] OfferedForSharing =
    [
        "Choose a host",
        "bastion (jump@bastion.example.com)",
        "prod-web-01 (deploy@10.4.2.11)",
        "staging-db-replica-eu-west (postgres@db-replica.staging.example.com)",
    ];

    private async Task<IPage> OpenAsync(string query)
    {
        var page = await fixture.NewPageAsync("/?" + query);
        await page.GetByTestId("tab-tools").ClickAsync();
        await page.GetByTestId("open-tool-team").ClickAsync();
        await Assertions.Expect(page.GetByTestId("team-page")).ToBeVisibleAsync();
        return page;
    }

    [Fact]
    public async Task WithProYouCanJoinATeamButNotStartOne()
    {
        var page = await OpenAsync("");

        await Assertions.Expect(page.GetByTestId("team-join")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("team-create-upsell")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("team-create")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task OnFreeYouCanDoNeither()
    {
        var page = await OpenAsync("free");

        await Assertions.Expect(page.GetByTestId("team-join-upsell")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("team-create-upsell")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("team-join")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task JoiningWithACodeShowsTheTeamsHosts()
    {
        var page = await OpenAsync("");
        await Assertions.Expect(page.GetByTestId("team-join")).ToBeDisabledAsync();

        await page.GetByTestId("team-join-code").FillAsync("7k2qm abcde fghjk mnpqr");
        await page.GetByTestId("team-join-name").FillAsync("Alice");
        await page.GetByTestId("team-join").ClickAsync();

        await Assertions.Expect(page.GetByTestId("team-name")).ToHaveTextAsync("Ops");
        await Assertions.Expect(page.GetByTestId("team-role")).ToHaveTextAsync("You are a member of this team.");
        await Assertions.Expect(page.GetByTestId("team-host-address")).ToHaveTextAsync("deploy@web.internal");
    }

    [Fact]
    public async Task AWrongCodeSaysWhyAndLeavesTheFormThere()
    {
        var page = await OpenAsync("");

        await page.GetByTestId("team-join-code").FillAsync("AAAAA-BBBBB-CCCCC-DDDDD");
        await page.GetByTestId("team-join-name").FillAsync("Alice");
        await page.GetByTestId("team-join").ClickAsync();

        await Assertions.Expect(page.GetByTestId("team-message")).ToContainTextAsync("not valid");
        await Assertions.Expect(page.GetByTestId("team-join")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task ASharedHostIsAddedToMyHostsOnceAndKnownAfterwards()
    {
        var page = await OpenAsync("teammember");
        var hosts = page.GetByTestId("team-host");
        await Assertions.Expect(hosts).ToHaveCountAsync(2);
        // build-runner is already in the sample vault at the same address.
        await Assertions.Expect(hosts.Nth(0).GetByTestId("team-host-added")).ToBeVisibleAsync();
        await Assertions.Expect(hosts.Nth(0).GetByTestId("team-host-add")).ToHaveCountAsync(0);

        await hosts.Nth(1).GetByTestId("team-host-add").ClickAsync();

        await Assertions.Expect(hosts.Nth(1).GetByTestId("team-host-added")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("team-message")).ToContainTextAsync("choose your key or password");
        await page.GetByTestId("tab-hosts").ClickAsync();
        await Assertions.Expect(page.GetByTestId("host-name").Filter(new() { HasText = "Shared web" })).ToHaveCountAsync(1);
    }

    [Fact]
    public async Task MembersSeeNoInvitesActivityOrRemoveButtonsAndCanLeave()
    {
        var page = await OpenAsync("teammember&teamtier");

        await Assertions.Expect(page.GetByTestId("team-member")).ToHaveCountAsync(2);
        await Assertions.Expect(page.GetByTestId("team-invites")).ToHaveCountAsync(0);
        await Assertions.Expect(page.GetByTestId("team-audit")).ToHaveCountAsync(0);
        await Assertions.Expect(page.GetByTestId("team-member-remove")).ToHaveCountAsync(0);
        await Assertions.Expect(page.GetByTestId("team-host-remove")).ToHaveCountAsync(0);
        await Assertions.Expect(page.GetByTestId("team-share-select")).ToHaveCountAsync(0);

        await page.GetByTestId("team-leave").ClickAsync();

        await Assertions.Expect(page.GetByTestId("team-join-form")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task StartingATeamThenInvitingShowsTheCodeOnce()
    {
        var page = await OpenAsync("teamtier");

        await page.GetByTestId("team-create-name").FillAsync("Ops");
        await page.GetByTestId("team-create-owner").FillAsync("Sam");
        await page.GetByTestId("team-create").ClickAsync();
        await Assertions.Expect(page.GetByTestId("team-role")).ToHaveTextAsync("You own this team.");

        await page.GetByTestId("team-invite-create").ClickAsync();

        await Assertions.Expect(page.GetByTestId("team-invite-code")).ToHaveTextAsync(new System.Text.RegularExpressions.Regex(@"^[A-Z0-9]{5}(-[A-Z0-9]{5}){3}$"));
        await Assertions.Expect(page.GetByTestId("team-invite")).ToHaveCountAsync(1);
    }

    [Fact]
    public async Task OnlyPlainSshHostsNotAlreadySharedAreOfferedForSharing()
    {
        var page = await OpenAsync("teamowned&teamtier");
        var options = page.GetByTestId("team-share-select").Locator("option");

        // Sample vault: home-nas is a tailcat address, pi-garage is Tailscale
        // SSH, and build-runner is already shared.
        await Assertions.Expect(options).ToHaveTextAsync(OfferedForSharing);

        await page.GetByTestId("team-share-select").SelectOptionAsync(new SelectOptionValue { Label = "prod-web-01 (deploy@10.4.2.11)" });
        await page.GetByTestId("team-share").ClickAsync();

        await Assertions.Expect(page.GetByTestId("team-host")).ToHaveCountAsync(3);
        await Assertions.Expect(options).ToHaveCountAsync(3);
    }

    [Fact]
    public async Task RemovingAMemberShowsUpInTheActivityLog()
    {
        var page = await OpenAsync("teamowned&teamtier");

        await page.GetByTestId("team-member-remove").ClickAsync();
        await Assertions.Expect(page.GetByTestId("team-member")).ToHaveCountAsync(1);

        await page.GetByTestId("team-audit-load").ClickAsync();
        var entries = page.GetByTestId("team-audit-entry");
        await Assertions.Expect(entries.First).ToContainTextAsync("Sam removed Alice");
        await Assertions.Expect(entries.Last).ToContainTextAsync("Sam started the team");
    }

    [Fact]
    public async Task ALapsedOwnerCanStillTidyUpButNotAdd()
    {
        var page = await OpenAsync("teamowned");

        await Assertions.Expect(page.GetByTestId("team-owner-lapsed")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("team-invite-create")).ToHaveCountAsync(0);
        await Assertions.Expect(page.GetByTestId("team-share-select")).ToHaveCountAsync(0);

        await page.GetByTestId("team-host-remove").First.ClickAsync();
        await Assertions.Expect(page.GetByTestId("team-host")).ToHaveCountAsync(1);
        await page.GetByTestId("team-invite-revoke").ClickAsync();
        await Assertions.Expect(page.GetByTestId("team-invite")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task DeletingTheTeamTakesTwoTaps()
    {
        var page = await OpenAsync("teamowned&teamtier");

        await page.GetByTestId("team-delete").ClickAsync();
        await Assertions.Expect(page.GetByTestId("team-delete")).ToContainTextAsync("Tap again");
        await Assertions.Expect(page.GetByTestId("team-name")).ToBeVisibleAsync();

        await page.GetByTestId("team-delete").ClickAsync();

        await Assertions.Expect(page.GetByTestId("team-create-form")).ToBeVisibleAsync();
    }
}
