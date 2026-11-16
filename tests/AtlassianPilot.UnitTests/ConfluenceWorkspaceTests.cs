using System.Net;
using System.Text;
using System.Text.Json;
using AtlassianPilot.Core;
using AtlassianPilot.Infrastructure;

namespace AtlassianPilot.UnitTests;

public sealed class ConfluenceWorkspaceTests
{
    private static Store Config() => new();
    private const string Page = """
        {"id":"12345","type":"page","title":"Incident alerts","space":{"key":"ENG"},"version":{"number":7},"body":{"storage":{"value":"<p>Current &amp; trusted text</p>"}},"_links":{"webui":"/spaces/ENG/pages/12345/Incident+alerts"}}
        """;

    [Fact]
    public async Task SearchEscapesCqlAndReturnsOnlyActualPagesAndSafeLinks()
    {
        using var handler = new Handler(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.StartsWith("https://site.atlassian.net/wiki/rest/api/content/search?", request.RequestUri!.AbsoluteUri);
            var query = Uri.UnescapeDataString(request.RequestUri.Query);
            Assert.Contains("text~\"alerts \\\"AWS\\\"\"", query);
            Assert.Contains("space=\"ENG\"", query);
            return Task.FromResult(Json(HttpStatusCode.OK, """
                {"size":1,"results":[{"id":"12345","title":"Incident alerts","space":{"key":"ENG"},"body":{"storage":{"value":"<p>Route &amp; review</p>"}},"_links":{"webui":"/spaces/ENG/pages/12345/Incident+alerts"}}],"_links":{"next":"/rest/api/content/search?start=1"}}
                """));
        });
        var result = await Service(handler).SearchAsync("alerts \"AWS\"", "ENG");
        var page = Assert.Single(result.Pages);
        Assert.Equal("12345", page.Id);
        Assert.Equal("Incident alerts", page.Title);
        Assert.Equal("ENG", page.SpaceKey);
        Assert.Equal("Route & review", page.Excerpt);
        Assert.Equal("https://site.atlassian.net/wiki/spaces/ENG/pages/12345/Incident+alerts", page.Url.AbsoluteUri);
        Assert.True(result.HasMore);
        Assert.Equal("1", result.NextStart);
        Assert.Equal(0, handler.Writes);
    }

    [Theory]
    [InlineData("", null, 20, null)]
    [InlineData("content", "invalid space", 20, null)]
    [InlineData("content", null, 51, null)]
    [InlineData("content", null, 20, "not-a-number")]
    public async Task InvalidSearchParametersNeverReachConfluence(string query, string? space, int limit, string? start)
    {
        using var handler = new Handler(_ => throw new InvalidOperationException("Unexpected API call"));
        await Assert.ThrowsAsync<IntegrationException>(() => Service(handler).SearchAsync(query, space, limit, start));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task PageReadUsesBoundedStorageContentAndFallsBackToConfiguredWikiPath()
    {
        using var handler = new Handler(_ => Task.FromResult(Json(HttpStatusCode.OK, Page)));
        var page = await Service(handler).GetAsync("12345");
        Assert.Equal("12345", page.Id);
        Assert.Equal("Incident alerts", page.Title);
        Assert.Equal("<p>Current &amp; trusted text</p>", page.StorageContent);
        Assert.Equal(7, page.Version);
        Assert.Equal("https://site.atlassian.net/wiki/spaces/ENG/pages/12345/Incident+alerts", page.Url.AbsoluteUri);
    }

    [Theory]
    [InlineData("../12345")]
    [InlineData("123/45")]
    [InlineData("")]
    public async Task InvalidPageIdsAreRejectedBeforeNetwork(string id)
    {
        using var handler = new Handler(_ => throw new InvalidOperationException("Unexpected API call"));
        await Assert.ThrowsAsync<IntegrationException>(() => Service(handler).GetAsync(id));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task PrepareCreateIsReadOnlyAndEscapesHtmlThenApprovalPostsExactPayloadOnce()
    {
        string? submitted = null;
        using var handler = new Handler(async request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://site.atlassian.net/wiki/rest/api/content", request.RequestUri!.AbsoluteUri);
            submitted = await request.Content!.ReadAsStringAsync();
            return Json(HttpStatusCode.Created, """
                {"id":"9001","title":"Improved alert runbook","space":{"key":"ENG"},"_links":{"webui":"/spaces/ENG/pages/9001/Improved+alert+runbook"}}
                """);
        });
        var service = Service(handler);
        var preview = await service.PrepareAsync(new("ENG", "Improved alert runbook", "Safe <script>alert('x')</script> & reviewed"));
        Assert.Equal("Create", preview.Action);
        Assert.Equal(0, handler.Writes);
        using var payload = JsonDocument.Parse(preview.PayloadJson);
        var storage = payload.RootElement.GetProperty("body").GetProperty("storage");
        Assert.Equal("storage", storage.GetProperty("representation").GetString());
        Assert.Contains("&lt;script&gt;", storage.GetProperty("value").GetString());
        Assert.DoesNotContain("<script>", storage.GetProperty("value").GetString());
        var saved = await service.SaveApprovedAsync(preview.Id);
        Assert.Equal("9001", saved.Id);
        Assert.Equal("https://site.atlassian.net/wiki/spaces/ENG/pages/9001/Improved+alert+runbook", saved.Url.AbsoluteUri);
        Assert.False(saved.Updated);
        Assert.Equal(preview.PayloadJson, JsonSerializer.Serialize(JsonDocument.Parse(submitted!).RootElement, new JsonSerializerOptions { WriteIndented = true }));
        await Assert.ThrowsAsync<IntegrationException>(() => service.SaveApprovedAsync(preview.Id));
        Assert.Equal(1, handler.Writes);
    }

    [Fact]
    public async Task UpdateRequiresSameSpaceAndCapturesLatestVersionInPreview()
    {
        string? submitted = null;
        using var handler = new Handler(async request =>
        {
            if (request.Method == HttpMethod.Get) return Json(HttpStatusCode.OK, Page);
            Assert.Equal(HttpMethod.Put, request.Method);
            submitted = await request.Content!.ReadAsStringAsync();
            return Json(HttpStatusCode.OK, """
                {"id":"12345","title":"Improved runbook","space":{"key":"ENG"},"_links":{"webui":"/spaces/ENG/pages/12345/Improved+runbook"}}
                """);
        });
        var service = Service(handler);
        var preview = await service.PrepareAsync(new("ENG", "Improved runbook", "New reviewed body", "12345"));
        using (var json = JsonDocument.Parse(preview.PayloadJson))
        {
            Assert.Equal(8, json.RootElement.GetProperty("version").GetProperty("number").GetInt32());
            Assert.Equal("12345", json.RootElement.GetProperty("id").GetString());
        }
        Assert.Equal(0, handler.Writes);
        var saved = await service.SaveApprovedAsync(preview.Id);
        Assert.True(saved.Updated);
        Assert.Equal("12345", saved.Id);
        using var posted = JsonDocument.Parse(submitted!);
        Assert.Equal("New reviewed body", posted.RootElement.GetProperty("body").GetProperty("storage").GetProperty("value").GetString()!.Replace("<p>", "").Replace("</p>", ""));
        Assert.Equal(1, handler.Writes);
    }

    [Fact]
    public async Task UpdateCannotTargetAnotherSpaceAndPreparationDoesNotWrite()
    {
        using var handler = new Handler(_ => Task.FromResult(Json(HttpStatusCode.OK, Page)));
        var error = await Assert.ThrowsAsync<IntegrationException>(() => Service(handler).PrepareAsync(new("OPS", "Title", "Body", "12345")));
        Assert.Contains("different space", error.Message);
        Assert.Equal(0, handler.Writes);
    }

    [Theory]
    [InlineData(500, "{}")]
    [InlineData(201, "{broken")]
    [InlineData(201, "{}")]
    public async Task UnknownCreateOutcomeIsNotRetriedAndPreviewIsSingleUse(int status, string body)
    {
        using var handler = new Handler(_ => Task.FromResult(Json((HttpStatusCode)status, body)));
        var service = Service(handler);
        var preview = await service.PrepareAsync(new("ENG", "Title", "Body"));
        var error = await Assert.ThrowsAsync<IntegrationException>(() => service.SaveApprovedAsync(preview.Id));
        Assert.Contains("may already have saved", error.Message);
        await Assert.ThrowsAsync<IntegrationException>(() => service.SaveApprovedAsync(preview.Id));
        Assert.Equal(1, handler.Writes);
    }

    [Fact]
    public async Task ForeignPageLinkCannotEscapeConfiguredAtlassianHost()
    {
        using var handler = new Handler(_ => Task.FromResult(Json(HttpStatusCode.OK, Page.Replace("/spaces/ENG/pages/12345/Incident+alerts", "https://attacker.example/steal"))));
        var page = await Service(handler).GetAsync("12345");
        Assert.Equal("https://site.atlassian.net/wiki/pages/viewpage.action?pageId=12345", page.Url.AbsoluteUri);
    }

    private static ConfluenceWorkspaceService Service(Handler handler, Store? store = null) => new(new HttpClient(handler), store ?? Config(), store ?? Config());
    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public int Writes { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            if (request.Method != HttpMethod.Get) Writes++;
            Assert.Equal("Basic", request.Headers.Authorization!.Scheme);
            return await respond(request);
        }
    }

    private sealed class Store : ISettingsStore, ICredentialStore
    {
        public string Token { get; set; } = "dummy-token";
        public Task<IntegrationSettings> LoadAsync(IntegrationKind kind, CancellationToken cancellationToken = default) =>
            Task.FromResult(new IntegrationSettings(kind, "https://site.atlassian.net/wiki", "user@example.com", "ENG"));
        public Task<string?> GetAsync(IntegrationKind kind, CancellationToken cancellationToken = default) => Task.FromResult<string?>(Token);
        public Task InitializeAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SaveAsync(IntegrationSettings settings, string? credential = null, bool deleteCredential = false, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string> GetThemeAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetThemeAsync(string theme, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
