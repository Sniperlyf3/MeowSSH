using System.Net;
using System.Text.Json;
using MeowSSH.Core.Diagnostics;
using MeowSSH.Core.Licensing;

namespace MeowSSH.Core.Tests;

public sealed class DiagnosticReportUploadServiceTests
{
    private static readonly DiagnosticReportSnapshot Report = new(
        1,
        DateTimeOffset.Parse("2026-09-21T09:00:00Z"),
        "2.0.0",
        "Android",
        [new DiagnosticExceptionSnapshot("System.InvalidOperationException", "boom", "at Foo.Bar()")],
        [new DiagnosticBreadcrumb(DiagnosticBreadcrumbKind.OpenedSftp, DateTimeOffset.Parse("2026-09-21T08:59:00Z"))]);

    [Fact]
    public async Task UploadPostsTheReportToTheDiagnosticsEndpointAndReturnsTrueOnSuccess()
    {
        var handler = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.OK));
        using var http = new HttpClient(handler);
        var service = CreateService(http);

        var accepted = await service.TryUploadAsync(Report, "install-a");

        Assert.True(accepted);
        Assert.Equal("https://licensing.example/v1/diagnostics/reports", handler.RequestUri?.ToString());
        using var body = JsonDocument.Parse(handler.RequestBody!);
        var root = body.RootElement;
        Assert.Equal(1, root.GetProperty("formatVersion").GetInt32());
        Assert.Equal("2.0.0", root.GetProperty("appVersion").GetString());
        Assert.Equal("install-a", root.GetProperty("installId").GetString());
        var exception = Assert.Single(root.GetProperty("exceptions").EnumerateArray());
        Assert.Equal("System.InvalidOperationException", exception.GetProperty("type").GetString());
        // DiagnosticBreadcrumbKind.OpenedSftp's ordinal -- see
        // DiagnosticReportUploadRequest's own doc comment for why this must
        // stay numeric, not the string "OpenedSftp".
        var breadcrumb = Assert.Single(root.GetProperty("breadcrumbs").EnumerateArray());
        Assert.Equal(3, breadcrumb.GetProperty("kind").GetInt32());
    }

    [Fact]
    public async Task ANullInstallIdIsSentAsNullRatherThanOmitted()
    {
        var handler = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.OK));
        using var http = new HttpClient(handler);
        var service = CreateService(http);

        await service.TryUploadAsync(Report, installId: null);

        using var body = JsonDocument.Parse(handler.RequestBody!);
        Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("installId").ValueKind);
    }

    [Fact]
    public async Task ANonSuccessResponseIsReportedAsFalseNotAThrow()
    {
        var handler = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.TooManyRequests));
        using var http = new HttpClient(handler);
        var service = CreateService(http);

        var accepted = await service.TryUploadAsync(Report, "install-a");

        Assert.False(accepted);
    }

    [Fact]
    public async Task ANetworkFailureIsReportedAsFalseNotAThrow()
    {
        using var http = new HttpClient(new ThrowingHandler());
        var service = CreateService(http);

        var accepted = await service.TryUploadAsync(Report, "install-a");

        Assert.False(accepted);
    }

    [Fact]
    public async Task AnUnconfiguredLicensingApiIsReportedAsFalseWithoutAttemptingARequest()
    {
        var handler = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.OK));
        using var http = new HttpClient(handler);
        var service = new DiagnosticReportUploadService(
            http,
            new LicensingApiOptions(null, "public-key", "dev.sniperlyf3.meowssh"));

        var accepted = await service.TryUploadAsync(Report, "install-a");

        Assert.False(accepted);
        Assert.Null(handler.RequestUri);
    }

    private static DiagnosticReportUploadService CreateService(HttpClient http) =>
        new(
            http,
            new LicensingApiOptions(
                new Uri("https://licensing.example/"),
                "public-key",
                "dev.sniperlyf3.meowssh"));

    private sealed class RecordingHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }
        public string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            RequestBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return response;
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new HttpRequestException("simulated network failure");
    }
}
