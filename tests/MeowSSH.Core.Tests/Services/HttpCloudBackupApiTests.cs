using System.Net;
using System.Text;
using MeowSSH.Core.Licensing;
using MeowSSH.Core.Security;
using MeowSSH.Core.Services;

namespace MeowSSH.Core.Tests.Services;

public sealed class HttpCloudBackupApiTests
{
    private static readonly LicensingApiOptions Options = new(
        new Uri("https://api.meowssh.test/"), "cHVibGljLWtleQ==", "dev.sniperlyf3.meowssh");

    private static readonly CloudBackupCredential Credential =
        CloudBackupCredential.FromRecoveryCode("000G-40R4-0M30-E209-185G-R38E-1W81-24GK");

    [Fact]
    public async Task UploadSendsTheWireShapeMeowSshApiExpects()
    {
        // Mirrors MeowSSHAPI's PUT /v1/cloud-backups/{locator}/versions:
        // bearer secret, "<payload>.<signature>" grant header, raw body.
        var handler = new RecordingHandler(_ => Json(HttpStatusCode.OK,
            """{"id":"1790000000000-00000000000000aa","createdAtUtc":"2026-09-24T12:00:00Z","sizeBytes":3,"sha256Hex":"ab"}"""));
        var api = new HttpCloudBackupApi(new HttpClient(handler), Options);

        var version = await api.UploadAsync(Credential, new SignedEntitlementGrant("cGF5bG9hZA==", "c2ln"), new byte[] { 1, 2, 3 });

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Put, request.Method);
        Assert.Equal($"https://api.meowssh.test/v1/cloud-backups/{Credential.Locator}/versions", request.Uri);
        Assert.Equal("Bearer " + Credential.SecretBase64Url, request.Authorization);
        Assert.Equal("cGF5bG9hZA==.c2ln", request.Grant);
        Assert.Equal(new byte[] { 1, 2, 3 }, request.Body);
        Assert.Equal(3, version.SizeBytes);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "pro_cloud_required", "requires MeowSSH Pro Cloud")]
    [InlineData(HttpStatusCode.Unauthorized, "unauthorized", "No cloud backup matches")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "cloud_backup_unavailable", "not available")]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, "backup_too_large", "larger than the cloud backup limit")]
    public async Task ServerErrorCodesBecomeSentencesForTheUser(HttpStatusCode status, string code, string expected)
    {
        var api = new HttpCloudBackupApi(new HttpClient(new RecordingHandler(_ =>
            Json(status, $$"""{"error":"{{code}}","message":"server detail"}"""))), Options);

        var error = await Assert.ThrowsAsync<CloudBackupException>(() => api.ListAsync(Credential));

        Assert.Equal(code, error.Code);
        Assert.Contains(expected, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnHttpClientTimeoutBecomesAMessageNotACancellation()
    {
        // HttpClient reports its own timeout as TaskCanceledException; that
        // escaping a click handler would freeze the Blazor renderer.
        var api = new HttpCloudBackupApi(new HttpClient(new RecordingHandler(_ =>
            throw new TaskCanceledException("timeout"))), Options);

        var error = await Assert.ThrowsAsync<CloudBackupException>(() => api.ListAsync(Credential));

        Assert.Equal("timeout", error.Code);
    }

    [Fact]
    public async Task AnUnconfiguredBuildNeverSendsTheSecretAnywhere()
    {
        var handler = new RecordingHandler(_ => Json(HttpStatusCode.OK, "{}"));
        var api = new HttpCloudBackupApi(new HttpClient(handler), new LicensingApiOptions(null, "", "dev.sniperlyf3.meowssh"));

        var error = await Assert.ThrowsAsync<CloudBackupException>(() => api.ListAsync(Credential));

        Assert.Equal("not_configured", error.Code);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task AFirstSyncPushAsksTheServerToCreateNotOverwrite()
    {
        var handler = new RecordingHandler(_ => WithETag(Json(HttpStatusCode.OK, "{}"), "abc"));
        var api = new HttpCloudBackupApi(new HttpClient(handler), Options);

        var etag = await api.PushSyncAsync(Credential, new SignedEntitlementGrant("cA==", "cw=="), new byte[] { 7 }, expectedETag: null);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Put, request.Method);
        Assert.Equal($"https://api.meowssh.test/v1/cloud-backups/{Credential.Locator}/sync", request.Uri);
        Assert.Equal("*", request.IfNoneMatch);
        Assert.Null(request.IfMatch);
        Assert.Equal("cA==.cw==", request.Grant);
        Assert.Equal("abc", etag);
    }

    [Fact]
    public async Task ALaterSyncPushNamesTheVersionItMerged()
    {
        var handler = new RecordingHandler(_ => WithETag(Json(HttpStatusCode.OK, "{}"), "def"));
        var api = new HttpCloudBackupApi(new HttpClient(handler), Options);

        await api.PushSyncAsync(Credential, new SignedEntitlementGrant("cA==", "cw=="), new byte[] { 7 }, expectedETag: "abc");

        Assert.Equal("\"abc\"", Assert.Single(handler.Requests).IfMatch);
    }

    [Fact]
    public async Task ALostSyncRaceSurfacesAsAConflictTheSyncServiceCanRetry()
    {
        var api = new HttpCloudBackupApi(new HttpClient(new RecordingHandler(_ =>
            Json(HttpStatusCode.PreconditionFailed, """{"error":"sync_conflict","message":"x"}"""))), Options);

        var error = await Assert.ThrowsAsync<CloudBackupException>(() =>
            api.PushSyncAsync(Credential, new SignedEntitlementGrant("cA==", "cw=="), new byte[] { 7 }, "abc"));

        Assert.Equal("sync_conflict", error.Code);
    }

    [Fact]
    public async Task FetchingAnUnchangedSlotSendsTheETagAndReadsNotModified()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.NotModified));
        var api = new HttpCloudBackupApi(new HttpClient(handler), Options);

        var fetched = await api.FetchSyncAsync(Credential, ifNoneMatch: "abc");

        Assert.Equal("\"abc\"", Assert.Single(handler.Requests).IfNoneMatch);
        Assert.Equal(CloudSyncFetchKind.NotModified, fetched.Kind);
    }

    [Fact]
    public async Task FetchingANewerSlotReturnsItsBytesAndUnquotedETag()
    {
        var api = new HttpCloudBackupApi(new HttpClient(new RecordingHandler(_ =>
            WithETag(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) }, "fresh"))), Options);

        var fetched = await api.FetchSyncAsync(Credential, ifNoneMatch: "stale");

        Assert.Equal(CloudSyncFetchKind.Changed, fetched.Kind);
        Assert.Equal("fresh", fetched.ETag);
        Assert.Equal(new byte[] { 1, 2, 3 }, fetched.Content);
    }

    [Fact]
    public async Task FetchingAnEmptySlotIsEmptyNotAnError()
    {
        var api = new HttpCloudBackupApi(new HttpClient(new RecordingHandler(_ =>
            Json(HttpStatusCode.NotFound, """{"error":"not_found","message":"x"}"""))), Options);

        Assert.Equal(CloudSyncFetchKind.Empty, (await api.FetchSyncAsync(Credential, ifNoneMatch: null)).Kind);
    }

    private static HttpResponseMessage WithETag(HttpResponseMessage response, string etag)
    {
        response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"" + etag + "\"");
        return response;
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed record Sent(HttpMethod Method, string Uri, string? Authorization, string? Grant, byte[]? Body, string? IfMatch, string? IfNoneMatch);

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Sent> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            Requests.Add(new Sent(
                request.Method,
                request.RequestUri!.ToString(),
                request.Headers.Authorization?.ToString(),
                request.Headers.TryGetValues("X-MeowSSH-Entitlement-Grant", out var grant) ? grant.Single() : null,
                body,
                request.Headers.IfMatch.Count == 0 ? null : request.Headers.IfMatch.ToString(),
                request.Headers.IfNoneMatch.Count == 0 ? null : request.Headers.IfNoneMatch.ToString()));
            return respond(request);
        }
    }
}
