using System.Net;
using System.Text;
using System.Text.Json;
using AtlassianPilot.Core;
using AtlassianPilot.Infrastructure;

namespace AtlassianPilot.UnitTests;

public sealed class JiraIssueActionTests
{
    [Fact]
    public async Task UpdateAndCommentPreviewExactPayloadAndExecuteOnlyOnce()
    {
        using var handler = new Handler(async request =>
        {
            Assert.Equal("Basic", request.Headers.Authorization!.Scheme);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            if (request.Method == HttpMethod.Put)
            {
                Assert.Equal("/rest/api/3/issue/ABC-1", request.RequestUri!.AbsolutePath);
                Assert.Equal("Changed", body.RootElement.GetProperty("fields").GetProperty("summary").GetString());
                Assert.Equal("doc", body.RootElement.GetProperty("fields").GetProperty("description").GetProperty("type").GetString());
            }
            else
            {
                Assert.Equal("/rest/api/3/issue/ABC-1/comment", request.RequestUri!.AbsolutePath);
                Assert.Equal("Actual comment", body.RootElement.GetProperty("body").GetProperty("content")[0].GetProperty("content")[0].GetProperty("text").GetString());
            }
            return new(HttpStatusCode.Created) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
        });
        var service = Service(handler);
        var update = await service.PrepareUpdateAsync("ABC-1", "Changed", "New description");
        Assert.Equal(0, handler.Calls);
        Assert.Contains("Changed", update.PayloadJson);
        Assert.Contains("New description", update.PayloadJson);
        await service.ExecuteApprovedAsync(update.Id);
        await Assert.ThrowsAsync<IntegrationException>(() => service.ExecuteApprovedAsync(update.Id));
        var comment = await service.PrepareCommentAsync("ABC-1", "Actual comment");
        await service.ExecuteApprovedAsync(comment.Id);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task AssignmentTransitionAndLinksUseValuesReturnedByJira()
    {
        using var handler = new Handler(async request =>
        {
            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath.EndsWith("/transitions", StringComparison.Ordinal))
                return Json(HttpStatusCode.OK, "{\"transitions\":[{\"id\":\"31\",\"name\":\"Start Progress\"}]}" );
            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath.EndsWith("/issueLinkType", StringComparison.Ordinal))
                return Json(HttpStatusCode.OK, "{\"issueLinkTypes\":[{\"name\":\"Blocks\",\"inward\":\"is blocked by\",\"outward\":\"blocks\"}]}" );
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            if (request.RequestUri!.AbsolutePath.EndsWith("/assignee", StringComparison.Ordinal))
                Assert.Equal("account-123", body.RootElement.GetProperty("accountId").GetString());
            else if (request.RequestUri.AbsolutePath.EndsWith("/transitions", StringComparison.Ordinal))
                Assert.Equal("31", body.RootElement.GetProperty("transition").GetProperty("id").GetString());
            else
            {
                Assert.Equal("Blocks", body.RootElement.GetProperty("type").GetProperty("name").GetString());
                Assert.Equal("ABC-1", body.RootElement.GetProperty("outwardIssue").GetProperty("key").GetString());
                Assert.Equal("ABC-2", body.RootElement.GetProperty("inwardIssue").GetProperty("key").GetString());
            }
            return new(HttpStatusCode.NoContent);
        });
        var service = Service(handler);
        var assignment = await service.PrepareAssignmentAsync("ABC-1", "account-123");
        await service.ExecuteApprovedAsync(assignment.Id);
        Assert.Equal("Start Progress", Assert.Single(await service.GetTransitionsAsync("ABC-1")).Name);
        var transition = await service.PrepareTransitionAsync("ABC-1", "31");
        await service.ExecuteApprovedAsync(transition.Id);
        Assert.Equal("Blocks", Assert.Single(await service.GetLinkTypesAsync()).Name);
        var link = await service.PrepareLinkAsync("ABC-1", "ABC-2", "Blocks");
        Assert.Contains("ABC-1 blocks ABC-2", link.Summary);
        await service.ExecuteApprovedAsync(link.Id);
        Assert.Equal(7, handler.Calls);
    }

    [Fact]
    public async Task CloneRequiresDestinationFieldsAndCreatesOnlyReviewedSubset()
    {
        using var handler = new Handler(async request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://site.atlassian.net/rest/api/3/issue", request.RequestUri!.AbsoluteUri);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var fields = body.RootElement.GetProperty("fields");
            Assert.Equal("XYZ", fields.GetProperty("project").GetProperty("key").GetString());
            Assert.Equal("Bug", fields.GetProperty("issuetype").GetProperty("name").GetString());
            Assert.Equal("Original summary (clone)", fields.GetProperty("summary").GetString());
            Assert.Equal("doc", fields.GetProperty("description").GetProperty("type").GetString());
            Assert.False(fields.TryGetProperty("assignee", out _));
            return Json(HttpStatusCode.Created, "{\"key\":\"XYZ-44\"}");
        });
        var service = Service(handler);
        var preview = await service.PrepareCloneAsync("ABC-1", "XYZ", "Bug");
        Assert.Equal(0, handler.Calls);
        Assert.Contains("comments, attachments, links", preview.Summary);
        var result = await service.ExecuteApprovedAsync(preview.Id);
        Assert.Equal("XYZ-44", result.IssueKey);
        Assert.Equal("https://site.atlassian.net/browse/XYZ-44", result.IssueUrl!.AbsoluteUri);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task ChangedJiraCredentialsConsumePreparedApprovalWithoutWriting()
    {
        using var handler = new Handler(_ => throw new InvalidOperationException("Credentials changed; no request should be sent."));
        var configuration = new Configuration();
        var service = Service(handler, configuration: configuration);
        var preview = await service.PrepareCommentAsync("ABC-1", "Reviewed comment");
        configuration.Token = "replacement-token";
        var error = await Assert.ThrowsAsync<IntegrationException>(() => service.ExecuteApprovedAsync(preview.Id));
        Assert.Contains("credentials changed", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, handler.Calls);
        await Assert.ThrowsAsync<IntegrationException>(() => service.ExecuteApprovedAsync(preview.Id));
    }

    [Fact]
    public async Task UnsupportedRequiredCloneFieldBlocksPreparationAndInvalidKeysNeverReachNetwork()
    {
        using var handler = new Handler(_ => throw new InvalidOperationException("Unexpected request"));
        var metadata = new Metadata([new("customfield_1", "Release train", "string", true, false)]);
        var service = Service(handler, metadata);
        var error = await Assert.ThrowsAsync<IntegrationException>(() => service.PrepareCloneAsync("ABC-1", "XYZ", "Task"));
        Assert.Contains("Release train", error.Message);
        await Assert.ThrowsAsync<IntegrationException>(() => service.PrepareCommentAsync("../ABC-1", "hello"));
        Assert.Equal(0, handler.Calls);
    }

    private static JiraIssueActionService Service(Handler handler, Metadata? metadata = null, Configuration? configuration = null)
    {
        configuration ??= new Configuration();
        return new(new HttpClient(handler), configuration, configuration, new Details(), metadata ?? new Metadata([]));
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return send(request);
        }
    }

    private sealed class Details : IJiraIssueDetailsService
    {
        public Task<JiraIssueDetails> GetAsync(string issueKey, CancellationToken cancellationToken = default) =>
            Task.FromResult(new JiraIssueDetails(issueKey, new Uri("https://site.atlassian.net/browse/ABC-1"),
                "{\"summary\":\"Original summary\",\"description\":{\"type\":\"doc\",\"version\":1,\"content\":[]},\"assignee\":{\"accountId\":\"old-user\"}}", "Original summary"));
    }

    private sealed class Metadata(IReadOnlyList<JiraCreateField> fields) : IJiraCreateMetadataService
    {
        public Task<JiraCreateMetadata> GetAsync(string projectKey, string issueType, CancellationToken cancellationToken = default) =>
            Task.FromResult(new JiraCreateMetadata(projectKey, issueType, "2", fields));
    }

    private sealed class Configuration : ISettingsStore, ICredentialStore
    {
        public string Token { get; set; } = "test-token";
        public Task<IntegrationSettings> LoadAsync(IntegrationKind kind, CancellationToken cancellationToken = default) =>
            Task.FromResult(new IntegrationSettings(kind, "https://site.atlassian.net", "person@example.com"));
        public Task<string?> GetAsync(IntegrationKind kind, CancellationToken cancellationToken = default) => Task.FromResult<string?>(Token);
        public Task InitializeAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SaveAsync(IntegrationSettings settings, string? credential = null, bool deleteCredential = false, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string> GetThemeAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetThemeAsync(string theme, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
