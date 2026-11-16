using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AtlassianPilot.Core;

namespace AtlassianPilot.Infrastructure;

public sealed class ConfluenceWorkspaceService(HttpClient http, ISettingsStore settings, ICredentialStore credentials) : IConfluenceWorkspaceService
{
    private sealed record PendingSave(string SiteUrl, string Email, string CredentialHash, string Endpoint, string Method,
        string Payload, string Action, string Title, string SpaceKey, string Content, DateTimeOffset ExpiresAt);
    private readonly ConcurrentDictionary<Guid, PendingSave> pending = new();
    private const int MaximumResponseBytes = 1_048_576;
    private const string UnknownOutcome = "Confluence may already have saved this page. Check Confluence before preparing another approval to avoid duplicate or unintended edits. This request will not be retried.";

    public async Task<(IReadOnlyList<ConfluencePageSummary> Pages, bool HasMore, string? NextStart)> SearchAsync(
        string query, string? spaceKey = null, int limit = 20, string? start = null, CancellationToken cancellationToken = default)
    {
        if (query is null || query.Trim().Length is 0 or > 300 || query.Any(char.IsControl) || limit is < 1 or > 50)
            throw new IntegrationException("Enter a Confluence search phrase of 1-300 characters and a page size from 1 to 50.");
        if (!string.IsNullOrWhiteSpace(spaceKey) && !Regex.IsMatch(spaceKey, @"\A[A-Za-z0-9_~-]{1,255}\z", RegexOptions.CultureInvariant))
            throw new IntegrationException("Enter a valid Confluence space key or leave it blank.");
        var offset = 0;
        if (start is not null && (!int.TryParse(start, out offset) || offset < 0 || offset > 10000))
            throw new IntegrationException("The Confluence search continuation is invalid. Start a new search.");
        var cql = "type=page AND text~\"" + EscapeCql(query.Trim()) + "\"";
        if (!string.IsNullOrWhiteSpace(spaceKey)) cql += " AND space=\"" + EscapeCql(spaceKey.Trim()) + "\"";
        var (config, token) = await GetCredentialsAsync(cancellationToken);
        var endpoint = ApiRoot(config.BaseUrl) + "/content/search?cql=" + Uri.EscapeDataString(cql) + $"&start={offset}&limit={limit}&expand=space,version,body.storage";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            using var response = await SendAsync(HttpMethod.Get, endpoint, config.Email, token, null, timeout.Token);
            using var document = JsonDocument.Parse(await ReadBoundedAsync(response.Content, timeout.Token));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
                throw new JsonException();
            var pages = results.EnumerateArray().Select(item => ParseSummary(item, config.BaseUrl)).ToArray();
            var hasMore = root.TryGetProperty("_links", out var links) && links.ValueKind == JsonValueKind.Object &&
                links.TryGetProperty("next", out var next) && next.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(next.GetString());
            return (pages, hasMore, hasMore ? (offset + pages.Length).ToString(System.Globalization.CultureInfo.InvariantCulture) : null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new IntegrationException("Confluence page search timed out. Try again."); }
        catch (JsonException) { throw new IntegrationException("Confluence returned invalid page search results. No page details were displayed."); }
        catch (InvalidDataException) { throw new IntegrationException("Confluence page search exceeded the response size limit."); }
        catch (HttpRequestException) { throw new IntegrationException("Could not search Confluence. Check your connection and permissions."); }
        catch (IOException) { throw new IntegrationException("The Confluence connection failed while searching pages."); }
    }

    public async Task<ConfluencePage> GetAsync(string pageId, CancellationToken cancellationToken = default)
    {
        ValidatePageId(pageId);
        var (config, token) = await GetCredentialsAsync(cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            using var response = await SendAsync(HttpMethod.Get,
                ApiRoot(config.BaseUrl) + "/content/" + Uri.EscapeDataString(pageId) + "?expand=space,version,body.storage", config.Email, token, null, timeout.Token);
            using var document = JsonDocument.Parse(await ReadBoundedAsync(response.Content, timeout.Token));
            var root = document.RootElement;
            if (!TryString(root, "id", out var id) || id != pageId || !TryString(root, "title", out var title) ||
                !root.TryGetProperty("space", out var space) || !TryString(space, "key", out var spaceKey) ||
                !root.TryGetProperty("version", out var version) || !version.TryGetProperty("number", out var versionNumber) || !versionNumber.TryGetInt32(out var number) || number < 1 ||
                !root.TryGetProperty("body", out var body) || !body.TryGetProperty("storage", out var storage) || !TryString(storage, "value", out var content))
                throw new JsonException();
            return new(id, title, spaceKey, content, number, BuildPageUrl(config.BaseUrl, id, ReadWebPath(root)));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new IntegrationException("Loading the Confluence page timed out. Try again."); }
        catch (JsonException) { throw new IntegrationException("Confluence returned invalid page details. No page changes were made."); }
        catch (InvalidDataException) { throw new IntegrationException("Confluence page content exceeded the response size limit."); }
        catch (HttpRequestException) { throw new IntegrationException("Could not load the Confluence page. Check its ID and your page permissions."); }
        catch (IOException) { throw new IntegrationException("The Confluence connection failed while loading the page."); }
    }

    public async Task<ConfluencePagePreview> PrepareAsync(ConfluencePageDraft draft, CancellationToken cancellationToken = default)
    {
        ValidateSpaceKey(draft.SpaceKey);
        if (string.IsNullOrWhiteSpace(draft.Title) || draft.Title.Trim().Length > 255 || string.IsNullOrWhiteSpace(draft.Content) || draft.Content.Length > 32000)
            throw new IntegrationException("Review a page title of 1-255 characters and page content of 1-32000 characters.");
        var (config, token) = await GetCredentialsAsync(cancellationToken);
        var action = string.IsNullOrWhiteSpace(draft.PageId) ? "Create" : "Update";
        string endpoint;
        HttpMethod method;
        var payload = new JsonObject
        {
            ["type"] = "page",
            ["title"] = draft.Title.Trim(),
            ["space"] = new JsonObject { ["key"] = draft.SpaceKey.Trim() },
            ["body"] = new JsonObject { ["storage"] = new JsonObject { ["value"] = ToStorage(draft.Content), ["representation"] = "storage" } }
        };
        if (action == "Create")
        {
            endpoint = ApiRoot(config.BaseUrl) + "/content";
            method = HttpMethod.Post;
        }
        else
        {
            ValidatePageId(draft.PageId!);
            var page = await GetAsync(draft.PageId!, cancellationToken);
            if (!string.Equals(page.SpaceKey, draft.SpaceKey.Trim(), StringComparison.OrdinalIgnoreCase))
                throw new IntegrationException("The target page belongs to a different space. Choose its actual space before preparing an update.");
            payload["id"] = page.Id;
            payload["version"] = new JsonObject { ["number"] = checked(page.Version + 1), ["message"] = "Updated with AtlassianPilot" };
            endpoint = ApiRoot(config.BaseUrl) + "/content/" + Uri.EscapeDataString(page.Id);
            method = HttpMethod.Put;
        }
        var (latestConfig, latestToken) = await GetCredentialsAsync(cancellationToken);
        if (token != latestToken || config.BaseUrl != latestConfig.BaseUrl || config.Email != latestConfig.Email)
            throw new IntegrationException("Confluence settings or credentials changed during preparation. Prepare the preview again.");
        foreach (var stale in pending.Where(item => item.Value.ExpiresAt <= DateTimeOffset.UtcNow).Select(item => item.Key)) pending.TryRemove(stale, out _);
        var id = Guid.NewGuid();
        var json = payload.ToJsonString();
        pending[id] = new(config.BaseUrl, config.Email, Hash(token), endpoint, method.Method, json, action,
            draft.Title.Trim(), draft.SpaceKey.Trim(), draft.Content, DateTimeOffset.UtcNow.AddMinutes(15));
        return new(id, config.BaseUrl, action, draft.Title.Trim(), draft.SpaceKey.Trim(), draft.Content, payload.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    public void DiscardPreview(Guid previewId) => pending.TryRemove(previewId, out _);

    public async Task<ConfluencePageSaved> SaveApprovedAsync(Guid previewId, CancellationToken cancellationToken = default)
    {
        if (!pending.TryRemove(previewId, out var item) || item.ExpiresAt <= DateTimeOffset.UtcNow)
            throw new IntegrationException("This Confluence approval expired or was already used. Prepare a new preview; check the page first if a previous save outcome is uncertain.");
        var (config, token) = await GetCredentialsAsync(cancellationToken);
        if (config.BaseUrl != item.SiteUrl || config.Email != item.Email || Hash(token) != item.CredentialHash)
            throw new IntegrationException("Confluence settings or credentials changed after preview. Prepare and review a new approval.");
        cancellationToken.ThrowIfCancellationRequested();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            using var response = await SendAsync(new HttpMethod(item.Method), item.Endpoint, config.Email, token, item.Payload, timeout.Token);
            using var document = JsonDocument.Parse(await ReadBoundedAsync(response.Content, timeout.Token));
            var root = document.RootElement;
            if (!TryString(root, "id", out var id) || !TryString(root, "title", out var title)) throw new JsonException();
            var spaceKey = root.TryGetProperty("space", out var space) && TryString(space, "key", out var key) ? key : item.SpaceKey;
            return new(id, title, spaceKey, BuildPageUrl(config.BaseUrl, id, ReadWebPath(root)), item.Method == HttpMethod.Put.Method);
        }
        catch (OperationCanceledException) { throw new IntegrationException(UnknownOutcome); }
        catch (HttpRequestException) { throw new IntegrationException(UnknownOutcome); }
        catch (IOException) { throw new IntegrationException(UnknownOutcome); }
        catch (JsonException) { throw new IntegrationException(UnknownOutcome); }
        catch (InvalidDataException) { throw new IntegrationException(UnknownOutcome); }
    }

    private async Task<(IntegrationSettings Config, string Token)> GetCredentialsAsync(CancellationToken cancellationToken)
    {
        var config = await GetConfigurationAsync(cancellationToken);
        var token = await credentials.GetAsync(IntegrationKind.Confluence, cancellationToken);
        if (string.IsNullOrWhiteSpace(token) || token.StartsWith("token:", StringComparison.OrdinalIgnoreCase) || token != token.Trim() || token.StartsWith('"'))
            throw new IntegrationException("Save a valid Confluence API token in Integrations & Settings before using the Confluence Agent.");
        return (config, token);
    }

    private async Task<IntegrationSettings> GetConfigurationAsync(CancellationToken cancellationToken)
    {
        var config = await settings.LoadAsync(IntegrationKind.Confluence, cancellationToken);
        try { SettingsValidator.Validate(config); }
        catch (ArgumentException exception) { throw new IntegrationException(exception.Message); }
        return config;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string endpoint, string email, string token,
        string? payload, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, endpoint);
        var bytes = Encoding.UTF8.GetBytes(email + ":" + token);
        try { request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(bytes)); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (payload is not null) request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Unauthorized) { response.Dispose(); throw new IntegrationException("Confluence authentication failed. Check the saved account email and token."); }
        if (response.StatusCode == HttpStatusCode.Forbidden) { response.Dispose(); throw new IntegrationException("Confluence denied access. Check space permissions, token scopes and organization policy."); }
        if (response.StatusCode == HttpStatusCode.NotFound) { response.Dispose(); throw new IntegrationException("The Confluence page or space was not found, or your account cannot access it."); }
        if (response.StatusCode == HttpStatusCode.TooManyRequests) { response.Dispose(); throw new IntegrationException("Confluence rate limited this request. Wait before preparing another approval; no automatic retry was made."); }
        if (method != HttpMethod.Get && response.StatusCode == HttpStatusCode.BadRequest) { response.Dispose(); throw new IntegrationException("Confluence rejected the page content or fields. Review the title, space and storage-format content, then prepare a new preview."); }
        if (method == HttpMethod.Put && response.StatusCode == HttpStatusCode.Conflict) { response.Dispose(); throw new IntegrationException("The page changed after preview. Reload its latest content and prepare a new update approval."); }
        var expected = method == HttpMethod.Get ? HttpStatusCode.OK : method == HttpMethod.Post ? HttpStatusCode.Created : HttpStatusCode.OK;
        if (response.StatusCode != expected || response.Content.Headers.ContentType?.MediaType != "application/json")
        {
            var status = (int)response.StatusCode;
            response.Dispose();
            if (method != HttpMethod.Get) throw new IntegrationException(UnknownOutcome);
            throw new IntegrationException($"Confluence returned HTTP {status}. Check the page details and permissions.");
        }
        return response;
    }

    private static string ApiRoot(string baseUrl)
    {
        var uri = new Uri(baseUrl);
        var path = uri.AbsolutePath.TrimEnd('/');
        var wikiBase = path.EndsWith("/wiki", StringComparison.OrdinalIgnoreCase) ? path : path + "/wiki";
        return uri.GetLeftPart(UriPartial.Authority) + wikiBase + "/rest/api";
    }

    private static ConfluencePageSummary ParseSummary(JsonElement item, string baseUrl)
    {
        if (!TryString(item, "id", out var id) || !Regex.IsMatch(id, @"\A[0-9]+\z", RegexOptions.CultureInvariant) ||
            !TryString(item, "title", out var title) || !item.TryGetProperty("space", out var space) || !TryString(space, "key", out var key))
            throw new JsonException();
        var excerpt = item.TryGetProperty("body", out var body) && body.TryGetProperty("storage", out var storage) && TryString(storage, "value", out var html)
            ? HtmlToText(html, 500) : "";
        return new(id, title, key, excerpt, BuildPageUrl(baseUrl, id, ReadWebPath(item)));
    }

    private static string? ReadWebPath(JsonElement item) => item.TryGetProperty("_links", out var links) && TryString(links, "webui", out var value) ? value : null;

    private static Uri BuildPageUrl(string baseUrl, string id, string? webPath)
    {
        var settings = new Uri(baseUrl);
        var origin = settings.GetLeftPart(UriPartial.Authority);
        if (webPath is not null)
        {
            var candidate = webPath.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                ? Uri.TryCreate(webPath, UriKind.Absolute, out var absolute) ? absolute : null
                : BuildRelativePageUri(origin, webPath);
            if (candidate is not null && candidate.Scheme == Uri.UriSchemeHttps && string.Equals(candidate.Host, settings.Host, StringComparison.OrdinalIgnoreCase) && candidate.UserInfo.Length == 0)
                return candidate;
        }
        return new Uri(origin + "/wiki/pages/viewpage.action?pageId=" + Uri.EscapeDataString(id));
    }

    private static Uri? BuildRelativePageUri(string origin, string path)
    {
        var relativePath = path.StartsWith('/') ? path : "/" + path;
        if (!relativePath.StartsWith("/wiki/", StringComparison.OrdinalIgnoreCase) && !string.Equals(relativePath, "/wiki", StringComparison.OrdinalIgnoreCase))
            relativePath = "/wiki" + relativePath;
        return Uri.TryCreate(origin + relativePath, UriKind.Absolute, out var uri) ? uri : null;
    }

    private static string EscapeCql(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
    private static void ValidateSpaceKey(string spaceKey)
    {
        if (string.IsNullOrWhiteSpace(spaceKey) || !Regex.IsMatch(spaceKey, @"\A[A-Za-z0-9_~-]{1,255}\z", RegexOptions.CultureInvariant))
            throw new IntegrationException("Enter a valid Confluence space key, such as ENG.");
    }
    private static void ValidatePageId(string pageId)
    {
        if (string.IsNullOrWhiteSpace(pageId) || !Regex.IsMatch(pageId, @"\A[0-9]{1,32}\z", RegexOptions.CultureInvariant))
            throw new IntegrationException("Enter a valid numeric Confluence page ID.");
    }
    private static string ToStorage(string content) => string.Join("", content.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n')
        .Select(line => "<p>" + SecurityElement.Escape(line) + "</p>"));
    private static string HtmlToText(string html, int maximum)
    {
        var text = Regex.Replace(html, "<[^>]*>", " ", RegexOptions.CultureInvariant);
        text = System.Net.WebUtility.HtmlDecode(Regex.Replace(text, @"\s+", " ", RegexOptions.CultureInvariant)).Trim();
        return text.Length <= maximum ? text : text[..maximum] + "…";
    }
    private static bool TryString(JsonElement element, string property, out string value)
    {
        value = "";
        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var candidate) &&
            candidate.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value = candidate.GetString()!);
    }
    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > MaximumResponseBytes) throw new InvalidDataException();
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken)) != 0)
        {
            if (output.Length + read > MaximumResponseBytes) throw new InvalidDataException();
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }
}
