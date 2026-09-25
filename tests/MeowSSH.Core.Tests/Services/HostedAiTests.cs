using System.Net;
using System.Text;
using System.Text.Json;
using MeowSSH.Core.Licensing;
using MeowSSH.Core.Services;
using MeowSSH.Core.Tests.Fakes;

namespace MeowSSH.Core.Tests.Services;

public sealed class HostedAiTests
{
    private static readonly LicensingApiOptions Options = new(
        new Uri("https://api.meowssh.test/"), "cHVibGljLWtleQ==", "dev.sniperlyf3.meowssh");

    private sealed class RecordingApi : IHostedAiApi
    {
        public List<AiAssistRequest> Sent { get; } = [];

        public Task<AiAssistAnswer> AssistAsync(SignedEntitlementGrant grant, AiAssistRequest request, CancellationToken cancellationToken = default)
        {
            Sent.Add(request);
            return Task.FromResult(new AiAssistAnswer("ok", null));
        }
    }

    private static (HostedAiService Service, RecordingApi Api) Create(EntitlementTier tier)
    {
        var api = new RecordingApi();
        return (new HostedAiService(api, new FakeCloudGrants(tier), new FakeTier(tier)), api);
    }

    [Theory]
    [InlineData(EntitlementTier.Free)]
    [InlineData(EntitlementTier.Pro)]
    public async Task BelowProCloudNothingIsSent(EntitlementTier tier)
    {
        var (service, api) = Create(tier);

        Assert.False(service.CanUse);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.AskAsync(new AiAssistRequest(AiAssistMode.Explain, "x", null)));
        Assert.Empty(api.Sent);
    }

    [Fact]
    public async Task OverlongOutputIsRefusedWholeNotCutShort()
    {
        var (service, api) = Create(EntitlementTier.ProCloud);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.AskAsync(new AiAssistRequest(AiAssistMode.Explain, new string('x', HostedAiService.MaxInputCharacters + 1), null)));

        Assert.Contains("Select less", error.Message, StringComparison.Ordinal);
        Assert.Empty(api.Sent);
    }

    [Theory]
    [InlineData(AiAssistMode.Explain, "   ", "what is this")]
    [InlineData(AiAssistMode.Command, "some output", "  ")]
    public async Task EachModeNeedsItsOwnInput(AiAssistMode mode, string input, string question)
    {
        var (service, api) = Create(EntitlementTier.ProCloud);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.AskAsync(new AiAssistRequest(mode, input, question)));

        Assert.Empty(api.Sent);
    }

    [Fact]
    public async Task TheReviewedTextIsSentTrimmedAndOtherwiseUntouched()
    {
        var (service, api) = Create(EntitlementTier.ProCloud);

        await service.AskAsync(new AiAssistRequest(AiAssistMode.Command, "\n  Permission denied (publickey).  \n", "  fix this  "));

        var sent = Assert.Single(api.Sent);
        Assert.Equal("Permission denied (publickey).", sent.Input);
        Assert.Equal("fix this", sent.Question);
    }

    [Fact]
    public async Task AGrantThatCannotBeFetchedBecomesAMessageNotACrash()
    {
        var api = new RecordingApi();
        var service = new HostedAiService(api, new FakeCloudGrants(EntitlementTier.Pro), new FakeTier(EntitlementTier.ProCloud));

        var error = await Assert.ThrowsAsync<HostedAiException>(() => service.AskAsync(new AiAssistRequest(AiAssistMode.Explain, "x", null)));

        Assert.Equal("entitlement_unavailable", error.Code);
    }

    [Fact]
    public async Task TheWireShapeIsWhatMeowSshApiExpects()
    {
        var handler = new RecordingHandler(_ => Json(HttpStatusCode.OK, """{"answer":"Use df.","command":"df -h","model":"claude-opus-5"}"""));
        var api = new HttpHostedAiApi(new HttpClient(handler), Options);

        var answer = await api.AssistAsync(new SignedEntitlementGrant("cA==", "cw=="), new AiAssistRequest(AiAssistMode.Command, "", "disk"));

        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://api.meowssh.test/v1/ai/assist", request.Uri);
        Assert.Equal("cA==.cw==", request.Grant);
        using var body = JsonDocument.Parse(request.Body);
        Assert.Equal("command", body.RootElement.GetProperty("mode").GetString());
        Assert.Equal("disk", body.RootElement.GetProperty("question").GetString());
        Assert.Equal("df -h", answer.Command);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, "ai_quota_exceeded", "today's Ask AI allowance")]
    [InlineData(HttpStatusCode.Forbidden, "pro_cloud_required", "requires MeowSSH Pro Cloud")]
    [InlineData(HttpStatusCode.UnprocessableEntity, "ai_refused", "could not help")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "ai_unavailable", "not available")]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, "input_too_long", "Select less")]
    public async Task ServerCodesBecomeSentences(HttpStatusCode status, string code, string expected)
    {
        var api = new HttpHostedAiApi(new HttpClient(new RecordingHandler(_ =>
            Json(status, $$"""{"error":"{{code}}","message":"server detail"}"""))), Options);

        var error = await Assert.ThrowsAsync<HostedAiException>(() =>
            api.AssistAsync(new SignedEntitlementGrant("cA==", "cw=="), new AiAssistRequest(AiAssistMode.Explain, "x", null)));

        Assert.Equal(code, error.Code);
        Assert.Contains(expected, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnHttpClientTimeoutBecomesAMessage()
    {
        var api = new HttpHostedAiApi(new HttpClient(new RecordingHandler(_ => throw new TaskCanceledException("timeout"))), Options);

        var error = await Assert.ThrowsAsync<HostedAiException>(() =>
            api.AssistAsync(new SignedEntitlementGrant("cA==", "cw=="), new AiAssistRequest(AiAssistMode.Explain, "x", null)));

        Assert.Equal("timeout", error.Code);
    }

    [Fact]
    public async Task AnUnconfiguredBuildSendsNothing()
    {
        var handler = new RecordingHandler(_ => Json(HttpStatusCode.OK, "{}"));
        var api = new HttpHostedAiApi(new HttpClient(handler), new LicensingApiOptions(null, "", "dev.sniperlyf3.meowssh"));

        var error = await Assert.ThrowsAsync<HostedAiException>(() =>
            api.AssistAsync(new SignedEntitlementGrant("cA==", "cw=="), new AiAssistRequest(AiAssistMode.Explain, "x", null)));

        Assert.Equal("not_configured", error.Code);
        Assert.Empty(handler.Requests);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed record Sent(string Uri, string? Grant, string Body);

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Sent> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new Sent(
                request.RequestUri!.ToString(),
                request.Headers.TryGetValues("X-MeowSSH-Entitlement-Grant", out var grant) ? grant.Single() : null,
                request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken)));
            return respond(request);
        }
    }
}
