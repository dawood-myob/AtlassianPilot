using System.Net;
using System.Net.Http.Headers;
using System.Text;
using AtlassianPilot.Core;
using AtlassianPilot.Infrastructure;

namespace AtlassianPilot.UnitTests;

public sealed class AtlassianConnectionTests
{
    [Theory]
    [InlineData(IntegrationKind.Jira, "https://site.atlassian.net/rest/api/3/myself", "{\"accountId\":\"123\"}")]
    [InlineData(IntegrationKind.Confluence, "https://site.atlassian.net/wiki/rest/api/user/current", "{\"accountId\":\"123\",\"type\":\"known\"}")]
    public async Task UsesCorrectReadOnlyEndpointAndSavedCredentials(IntegrationKind kind, string endpoint, string body)
    {
        using var handler = new StubHandler(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal(endpoint, request.RequestUri!.AbsoluteUri);
            Assert.Equal("Basic", request.Headers.Authorization!.Scheme);
            Assert.Equal("person@example.com:test-value", Encoding.UTF8.GetString(Convert.FromBase64String(request.Headers.Authorization.Parameter!)));
            return Json(HttpStatusCode.OK, body);
        });
        using var http = new HttpClient(handler);
        var storage = new MemoryConfiguration(kind);
        var result = await new AtlassianConnectionTester(http, storage, storage).TestAsync(kind);
        Assert.True(result.Success);
        Assert.Equal(ConnectionFailure.None, result.Failure);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData(401, ConnectionFailure.Authentication)]
    [InlineData(403, ConnectionFailure.Permission)]
    [InlineData(404, ConnectionFailure.InvalidResponse)]
    [InlineData(302, ConnectionFailure.InvalidResponse)]
    public async Task HttpFailuresAreNotSuccessfulOrRetried(int status, ConnectionFailure failure)
    {
        using var handler = new StubHandler(_ => Json((HttpStatusCode)status, "{\"error\":\"test-value\"}"));
        using var http = new HttpClient(handler);
        var storage = new MemoryConfiguration(IntegrationKind.Jira);
        var result = await new AtlassianConnectionTester(http, storage, storage).TestAsync(IntegrationKind.Jira);
        Assert.False(result.Success);
        Assert.Equal(failure, result.Failure);
        Assert.DoesNotContain("test-value", result.Message);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("{}", "application/json")]
    [InlineData("[]", "application/json")]
    [InlineData("{broken", "application/json")]
    [InlineData("<html>Login</html>", "text/html")]
    public async Task MalformedResponsesCannotReportConnected(string body, string mediaType)
    {
        using var handler = new StubHandler(_ => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, mediaType) });
        using var http = new HttpClient(handler);
        var storage = new MemoryConfiguration(IntegrationKind.Jira);
        var result = await new AtlassianConnectionTester(http, storage, storage).TestAsync(IntegrationKind.Jira);
        Assert.False(result.Success);
        Assert.Equal(ConnectionFailure.InvalidResponse, result.Failure);
    }

    [Fact]
    public async Task AnonymousConfluenceIsNotAnAuthenticatedConnection()
    {
        using var handler = new StubHandler(_ => Json(HttpStatusCode.OK, "{\"accountId\":\"123\",\"type\":\"anonymous\"}"));
        using var http = new HttpClient(handler);
        var storage = new MemoryConfiguration(IntegrationKind.Confluence);
        var result = await new AtlassianConnectionTester(http, storage, storage).TestAsync(IntegrationKind.Confluence);
        Assert.False(result.Success);
        Assert.Equal(ConnectionFailure.Authentication, result.Failure);
    }

    [Theory]
    [InlineData(429)]
    [InlineData(503)]
    public async Task RetriesTransientReadFailuresAndReturnsOnlyActualSuccess(int status)
    {
        var count = 0;
        using var handler = new StubHandler(_ =>
        {
            if (++count == 3) return Json(HttpStatusCode.OK, "{\"accountId\":\"123\"}");
            var response = Json((HttpStatusCode)status, "{}");
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero);
            return response;
        });
        using var http = new HttpClient(handler);
        var storage = new MemoryConfiguration(IntegrationKind.Jira);
        var result = await new AtlassianConnectionTester(http, storage, storage).TestAsync(IntegrationKind.Jira);
        Assert.True(result.Success);
        Assert.Equal(3, handler.Calls);
    }

    [Fact]
    public async Task LongRetryAfterDoesNotBlockOrRetryEarly()
    {
        using var handler = new StubHandler(_ =>
        {
            var response = Json(HttpStatusCode.TooManyRequests, "{}");
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(120));
            return response;
        });
        using var http = new HttpClient(handler);
        var storage = new MemoryConfiguration(IntegrationKind.Jira);
        var result = await new AtlassianConnectionTester(http, storage, storage).TestAsync(IntegrationKind.Jira);
        Assert.False(result.Success);
        Assert.Equal(ConnectionFailure.RateLimit, result.Failure);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("token:test-value")]
    [InlineData(" test-value ")]
    public async Task MissingOrWrappedTokenIsNeverSent(string? token)
    {
        using var handler = new StubHandler(_ => throw new InvalidOperationException("Must not contact server"));
        using var http = new HttpClient(handler);
        var storage = new MemoryConfiguration(IntegrationKind.Jira) { Token = token };
        var result = await new AtlassianConnectionTester(http, storage, storage).TestAsync(IntegrationKind.Jira);
        Assert.False(result.Success);
        Assert.Equal(ConnectionFailure.Configuration, result.Failure);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task NetworkErrorsAreSafeAndCancellationPropagates()
    {
        using var handler = new StubHandler(_ => throw new HttpRequestException("Sensitive upstream test-value"));
        using var http = new HttpClient(handler);
        var storage = new MemoryConfiguration(IntegrationKind.Jira);
        var tester = new AtlassianConnectionTester(http, storage, storage);
        var result = await tester.TestAsync(IntegrationKind.Jira);
        Assert.Equal(ConnectionFailure.Network, result.Failure);
        Assert.DoesNotContain("test-value", result.Message);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tester.TestAsync(IntegrationKind.Jira, cancelled.Token));
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return Task.FromResult(respond(request));
        }
    }

    private sealed class MemoryConfiguration(IntegrationKind kind) : ISettingsStore, ICredentialStore
    {
        public string? Token { get; set; } = "test-value";
        public Task<IntegrationSettings> LoadAsync(IntegrationKind requested, CancellationToken cancellationToken = default) =>
            Task.FromResult(new IntegrationSettings(kind, kind == IntegrationKind.Jira ? "https://site.atlassian.net" : "https://site.atlassian.net/wiki", "person@example.com"));
        public Task<string?> GetAsync(IntegrationKind requested, CancellationToken cancellationToken = default) => Task.FromResult(Token);
        public Task InitializeAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SaveAsync(IntegrationSettings settings, string? credential = null, bool deleteCredential = false, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string> GetThemeAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetThemeAsync(string theme, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
