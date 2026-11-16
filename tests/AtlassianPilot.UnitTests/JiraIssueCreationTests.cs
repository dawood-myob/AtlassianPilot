using System.Net;
using System.Text;
using System.Text.Json;
using AtlassianPilot.Core;
using AtlassianPilot.Infrastructure;

namespace AtlassianPilot.UnitTests;

public sealed class JiraIssueCreationTests
{
    private static JiraIssueDraft Draft => new("ABC", "Task", "Migrate remaining alerts", "User story\nAs a developer, I need reliable alerts.\n\nAcceptance criteria\n1. Prefer Teams.\n2. Assess on-call if Teams is unsupported.", "ABC-100");
    private const string TypeMetadata = """
        {"startAt":0,"total":1,"issueTypes":[{"id":"7","name":"Task"}]}
        """;
    private const string FieldMetadata = """
        {"startAt":0,"total":8,"fields":[
          {"fieldId":"project","name":"Project","required":true,"schema":{"type":"project"},"operations":[]},
          {"fieldId":"issuetype","name":"Issue type","required":true,"schema":{"type":"issuetype"},"operations":[]},
          {"fieldId":"summary","name":"Summary","required":true,"schema":{"type":"string"}},
          {"fieldId":"description","name":"Description","required":false,"schema":{"type":"string"}},
          {"fieldId":"parent","name":"Parent","required":false,"schema":{"type":"issuelink"}},
          {"fieldId":"customfield_10","name":"Functional area","required":true,"schema":{"type":"option"},"allowedValues":[{"id":"5","value":"Integration"}]},
          {"fieldId":"assignee","name":"Assignee","required":false,"schema":{"type":"user"}},
          {"fieldId":"labels","name":"Labels","required":false,"schema":{"type":"array","items":"string"}}
        ]}
        """;
    private const string Extra = """{"customfield_10":{"id":"5"},"assignee":{"accountId":"user-1"},"labels":["migration"]}""";

    [Fact]
    public async Task PreparationIsReadOnlyAndApprovalPostsTheExactImmutablePayloadOnlyOnce()
    {
        string? submitted = null;
        using var handler = new Handler(async request =>
        {
            submitted = await request.Content!.ReadAsStringAsync();
            Assert.Equal("https://site.atlassian.net/rest/api/3/issue", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Basic", request.Headers.Authorization!.Scheme);
            return Json(HttpStatusCode.Created, "{\"key\":\"ABC-101\",\"self\":\"https://untrusted.example/issue\"}");
        });
        var service = Service(handler);
        var preview = await service.PrepareAsync(Draft, Extra);
        Assert.Equal(0, handler.Posts);
        using var fields = JsonDocument.Parse(preview.FieldsJson);
        Assert.Equal("7", fields.RootElement.GetProperty("issuetype").GetProperty("id").GetString());
        Assert.Equal("doc", fields.RootElement.GetProperty("description").GetProperty("type").GetString());
        Assert.Equal(1, fields.RootElement.GetProperty("description").GetProperty("version").GetInt32());
        Assert.Equal("ABC-100", fields.RootElement.GetProperty("parent").GetProperty("key").GetString());
        Assert.Equal("5", fields.RootElement.GetProperty("customfield_10").GetProperty("id").GetString());
        var result = await service.CreateApprovedAsync(preview.Id);
        Assert.Equal("ABC-101", result.Key);
        Assert.Equal("https://site.atlassian.net/browse/ABC-101", result.Url.AbsoluteUri);
        using var posted = JsonDocument.Parse(submitted!);
        Assert.Equal(JsonSerializer.Serialize(fields.RootElement), JsonSerializer.Serialize(posted.RootElement.GetProperty("fields")));
        Assert.Contains("As a developer, I need reliable alerts.", submitted);
        await Assert.ThrowsAsync<IntegrationException>(() => service.CreateApprovedAsync(preview.Id));
        Assert.Equal(1, handler.Posts);
    }

    [Theory]
    [InlineData("{}", "Functional area")]
    [InlineData("{\"customfield_10\":{\"id\":\"not-allowed\"}}", "available values")]
    [InlineData("{\"customfield_10\":{\"id\":\"5\"},\"status\":{\"id\":\"1\"}}", "cannot be supplied")]
    [InlineData("{\"summary\":\"override\"}", "cannot be supplied")]
    [InlineData("{\"labels\":\"not-an-array\"}", "wrong value format")]
    [InlineData("{\"customfield_10\":{},\"customfield_10\":{}}", "unique")]
    public async Task InvalidFieldsCannotBePreparedOrWritten(string extra, string expected)
    {
        using var handler = new Handler(_ => throw new InvalidOperationException("Unexpected create"));
        var error = await Assert.ThrowsAsync<IntegrationException>(() => Service(handler).PrepareAsync(Draft, extra));
        Assert.Contains(expected, error.Message);
        Assert.Equal(0, handler.Posts);
    }

    [Theory]
    [InlineData("summary")]
    [InlineData("description")]
    public async Task EmptyOperationsOnOrdinaryFieldsStillBlocksPreparation(string fieldId)
    {
        var metadata = System.Text.Json.Nodes.JsonNode.Parse(FieldMetadata)!;
        metadata["fields"]!.AsArray().Single(item => item!["fieldId"]!.GetValue<string>() == fieldId)!["operations"] = new System.Text.Json.Nodes.JsonArray();
        using var handler = new Handler(_ => throw new InvalidOperationException("Unexpected create")) { FieldResponse = metadata.ToJsonString() };
        var error = await Assert.ThrowsAsync<IntegrationException>(() => Service(handler).PrepareAsync(Draft, Extra));
        Assert.Contains("does not allow setting", error.Message);
        Assert.Equal(0, handler.Posts);
    }

    [Fact]
    public async Task ConcurrentApprovalSubmissionsCanSendOnlyOnePost()
    {
        var release = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new Handler(_ => release.Task);
        var service = Service(handler);
        var preview = await service.PrepareAsync(Draft, Extra);
        var first = service.CreateApprovedAsync(preview.Id);
        try
        {
            await Assert.ThrowsAsync<IntegrationException>(() => service.CreateApprovedAsync(preview.Id));
            Assert.Equal(1, handler.Posts);
        }
        finally { release.SetResult(Json(HttpStatusCode.Created, "{\"key\":\"ABC-101\"}")); }
        Assert.Equal("ABC-101", (await first).Key);
    }

    [Fact]
    public async Task CancelledApprovalBeforeSubmissionDoesNotPost()
    {
        using var handler = new Handler(_ => throw new InvalidOperationException("Unexpected create"));
        var service = Service(handler);
        var preview = await service.PrepareAsync(Draft, Extra);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.CreateApprovedAsync(preview.Id, cancelled.Token));
        Assert.Equal(0, handler.Posts);
    }

    [Fact]
    public async Task ChangedSettingsAndDiscardedApprovalsCannotWrite()
    {
        using var handler = new Handler(_ => throw new InvalidOperationException("Unexpected create"));
        var config = new Configuration();
        var service = Service(handler, config);
        var preview = await service.PrepareAsync(Draft, Extra);
        config.Token = "changed-test-token";
        var error = await Assert.ThrowsAsync<IntegrationException>(() => service.CreateApprovedAsync(preview.Id));
        Assert.Contains("changed after preview", error.Message);
        config.Token = "dummy-token";
        var next = await service.PrepareAsync(Draft, Extra);
        service.DiscardPreview(next.Id);
        await Assert.ThrowsAsync<IntegrationException>(() => service.CreateApprovedAsync(next.Id));
        Assert.Equal(0, handler.Posts);
    }

    [Theory]
    [InlineData(500, "{}")]
    [InlineData(201, "{broken")]
    [InlineData(201, "{}")]
    public async Task AmbiguousCreateResponsesNeverRetryOrPermitReusingApproval(int code, string body)
    {
        using var handler = new Handler(_ => Task.FromResult(Json((HttpStatusCode)code, body)));
        var service = Service(handler);
        var preview = await service.PrepareAsync(Draft, Extra);
        var error = await Assert.ThrowsAsync<IntegrationException>(() => service.CreateApprovedAsync(preview.Id));
        Assert.Contains("outcome is unknown", error.Message);
        await Assert.ThrowsAsync<IntegrationException>(() => service.CreateApprovedAsync(preview.Id));
        Assert.Equal(1, handler.Posts);
    }

    [Fact]
    public async Task TransportFailureAfterSubmissionHasUnknownOutcomeAndNoRetry()
    {
        using var handler = new Handler(_ => throw new HttpRequestException("private error"));
        var service = Service(handler);
        var preview = await service.PrepareAsync(Draft, Extra);
        var error = await Assert.ThrowsAsync<IntegrationException>(() => service.CreateApprovedAsync(preview.Id));
        Assert.Contains("Check Jira", error.Message);
        Assert.DoesNotContain("private error", error.Message);
        Assert.Equal(1, handler.Posts);
    }

    [Fact]
    public async Task ReferenceCopiesCompatibleDefaultsButNotHistoryOrContent()
    {
        using var handler = new Handler(_ => throw new InvalidOperationException("Unexpected create"));
        var service = Service(handler);
        var template = await service.LoadTemplateAsync("ABC", "Task", "ABC-99");
        using var copied = JsonDocument.Parse(template.InitialFieldsJson);
        Assert.Equal("5", copied.RootElement.GetProperty("customfield_10").GetProperty("id").GetString());
        Assert.Equal("user-1", copied.RootElement.GetProperty("assignee").GetProperty("accountId").GetString());
        Assert.False(copied.RootElement.TryGetProperty("summary", out _));
        Assert.False(copied.RootElement.TryGetProperty("parent", out _));
        Assert.False(copied.RootElement.TryGetProperty("status", out _));
        Assert.False(copied.RootElement.TryGetProperty("reporter", out _));
        Assert.Contains("not copied", template.ReferenceNotice);
        Assert.Equal(0, handler.Posts);
    }

    private static JiraIssueSearchService Service(Handler handler, Configuration? configuration = null)
    {
        var config = configuration ?? new();
        return new(new HttpClient(handler), config, config);
    }

    private static HttpResponseMessage Json(HttpStatusCode code, string body) => new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> create) : HttpMessageHandler
    {
        public int Posts { get; private set; }
        public string FieldResponse { get; init; } = FieldMetadata;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request.Method == HttpMethod.Post) { Posts++; return create(request); }
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/issuetypes")) return Task.FromResult(Json(HttpStatusCode.OK, TypeMetadata));
            if (path.EndsWith("/issuetypes/7")) return Task.FromResult(Json(HttpStatusCode.OK, FieldResponse));
            if (path.EndsWith("/issue/ABC-99")) return Task.FromResult(Json(HttpStatusCode.OK, """
                {"key":"ABC-99","fields":{"project":{"key":"ABC"},"issuetype":{"id":"7","name":"Task"},"summary":"Do not copy this","parent":{"key":"ABC-1"},"status":{"id":"done"},"reporter":{"accountId":"other"},"customfield_10":{"id":"5","value":"Integration"},"assignee":{"accountId":"user-1","displayName":"Example User"}}}
                """));
            throw new InvalidOperationException("Unexpected read endpoint");
        }
    }

    private sealed class Configuration : ISettingsStore, ICredentialStore
    {
        public string Token { get; set; } = "dummy-token";
        public Task<IntegrationSettings> LoadAsync(IntegrationKind kind, CancellationToken cancellationToken = default) => Task.FromResult(new IntegrationSettings(kind, "https://site.atlassian.net", "person@example.com"));
        public Task<string?> GetAsync(IntegrationKind kind, CancellationToken cancellationToken = default) => Task.FromResult<string?>(Token);
        public Task InitializeAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SaveAsync(IntegrationSettings settings, string? credential = null, bool deleteCredential = false, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string> GetThemeAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetThemeAsync(string theme, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
