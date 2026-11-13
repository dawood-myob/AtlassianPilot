using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AtlassianPilot.Core;
using AtlassianPilot.Infrastructure;

namespace AtlassianPilot.UnitTests;

public sealed class JiraIssueSearchTests
{
    [Fact]
    public async Task SendsReadOnlySearchAndUsesJiraFieldsAndContinuationToken()
    {
        using var handler = new Handler(async request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://site.atlassian.net/rest/api/3/search/jql", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Basic", request.Headers.Authorization!.Scheme);
            Assert.Equal("person@example.com:test-token", Encoding.UTF8.GetString(Convert.FromBase64String(request.Headers.Authorization.Parameter!)));
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.Equal("assignee = currentUser()", body.RootElement.GetProperty("jql").GetString());
            Assert.Equal(2, body.RootElement.GetProperty("maxResults").GetInt32());
            Assert.Equal("page-token", body.RootElement.GetProperty("nextPageToken").GetString());
            Assert.Contains(body.RootElement.GetProperty("fields").EnumerateArray(), value => value.GetString() == "summary");
            return Json(HttpStatusCode.OK, """
                {"issues":[{"key":"ABC-1","fields":{"summary":"Real issue","status":{"name":"In Progress"},"assignee":{"displayName":"Example User"},"priority":{"name":"High"},"issuetype":{"name":"Story"}}}],"isLast":false,"nextPageToken":"next-page"}
                """);
        });
        var result = await Service(handler).SearchAsync("assignee = currentUser()", 2, "page-token");
        var issue = Assert.Single(result.Issues);
        Assert.Equal("ABC-1", issue.Key);
        Assert.Equal("Real issue", issue.Summary);
        Assert.Equal("In Progress", issue.Status);
        Assert.Equal("Example User", issue.Assignee);
        Assert.Equal("High", issue.Priority);
        Assert.Equal("Story", issue.IssueType);
        Assert.Equal("https://site.atlassian.net/browse/ABC-1", issue.Url.AbsoluteUri);
        Assert.True(result.HasMore);
        Assert.Equal("next-page", result.NextPageToken);
        Assert.Null(result.Total);
    }

    [Theory]
    [InlineData("", 50)]
    [InlineData("project = ABC; delete", 50)]
    [InlineData("project = ABC", 0)]
    [InlineData("project = ABC", 101)]
    public async Task InvalidArgumentsNeverContactJira(string query, int maxResults)
    {
        using var handler = new Handler(_ => throw new InvalidOperationException("Unexpected request"));
        await Assert.ThrowsAsync<IntegrationException>(() => Service(handler).SearchAsync(query, maxResults));
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData(400, "query")]
    [InlineData(401, "authentication")]
    [InlineData(403, "denied")]
    [InlineData(302, "HTTP 302")]
    [InlineData(429, "rate limiting")]
    public async Task ErrorsAreSanitizedAndNeverLookLikeResults(int code, string expected)
    {
        using var handler = new Handler(_ =>
        {
            var response = Json((HttpStatusCode)code, "{\"error\":\"secret-provider-output\"}");
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(120));
            return Task.FromResult(response);
        });
        var error = await Assert.ThrowsAsync<IntegrationException>(() => Service(handler).SearchAsync("project = ABC"));
        Assert.Contains(expected, error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret-provider-output", error.Message);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"issues\":[{}]}")]
    [InlineData("{broken")]
    public async Task InvalidResponsesNeverProduceResults(string responseBody)
    {
        using var handler = new Handler(_ => Task.FromResult(Json(HttpStatusCode.OK, responseBody)));
        var error = await Assert.ThrowsAsync<IntegrationException>(() => Service(handler).SearchAsync("project = ABC"));
        Assert.Contains("invalid", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NullFieldsHaveExplicitFallbacksAndLastPageIsNotContinued()
    {
        using var handler = new Handler(_ => Task.FromResult(Json(HttpStatusCode.OK,
            "{\"issues\":[{\"key\":\"ABC-2\",\"fields\":{\"summary\":\"Second\",\"assignee\":null,\"priority\":null}}],\"isLast\":true}")));
        var result = await Service(handler).SearchAsync("project = ABC");
        Assert.Equal("Unassigned", Assert.Single(result.Issues).Assignee);
        Assert.Equal("Unspecified", result.Issues[0].Priority);
        Assert.False(result.HasMore);
    }

    [Fact]
    public async Task RetryableReadIsRetriedButCancellationPropagates()
    {
        using var handler = new Handler(_ =>
        {
            if (handlerCalls++ == 0)
            {
                var unavailable = Json(HttpStatusCode.ServiceUnavailable, "{}");
                unavailable.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero);
                return Task.FromResult(unavailable);
            }
            return Task.FromResult(Json(HttpStatusCode.OK, "{\"issues\":[],\"isLast\":true}"));
        });
        var result = await Service(handler).SearchAsync("project = ABC");
        Assert.Empty(result.Issues);
        Assert.Equal(2, handler.Calls);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service(handler).SearchAsync("project = ABC", cancellationToken: cancellation.Token));
    }

    private int handlerCalls;

    private static JiraIssueSearchService Service(Handler handler)
    {
        var configuration = new Configuration();
        return new JiraIssueSearchService(new HttpClient(handler), configuration, configuration);
    }

    private static HttpResponseMessage Json(HttpStatusCode code, string body) =>
        new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

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

    private sealed class Configuration : ISettingsStore, ICredentialStore
    {
        public Task<IntegrationSettings> LoadAsync(IntegrationKind kind, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new IntegrationSettings(kind, "https://site.atlassian.net", "person@example.com"));
        }
        public Task<string?> GetAsync(IntegrationKind kind, CancellationToken cancellationToken = default) => Task.FromResult<string?>("test-token");
        public Task InitializeAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SaveAsync(IntegrationSettings settings, string? credential = null, bool deleteCredential = false, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string> GetThemeAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetThemeAsync(string theme, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
