using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AtlassianPilot.Core;

namespace AtlassianPilot.Infrastructure;

public sealed class JiraIssueSearchService(HttpClient http, ISettingsStore settings, ICredentialStore credentials)
    : IJiraIssueSearchService, IJiraIssueDetailsService, IJiraCreateMetadataService
{
    private static readonly string[] Fields = ["summary", "status", "assignee", "priority", "issuetype"];
    private const int MaximumResponseBytes = 2 * 1024 * 1024;

    public async Task<JiraIssueSearchResult> SearchAsync(string jql, int maxResults = 50, string? nextPageToken = null,
        CancellationToken cancellationToken = default)
    {
        ValidateSearch(jql, maxResults, nextPageToken);
        var (configuration, token) = await GetCredentialsAsync(cancellationToken);

        var endpoint = new Uri(configuration.BaseUrl.TrimEnd('/') + "/rest/api/3/search/jql");
        var authorization = CreateAuthorization(configuration.Email, token);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));

        for (var attempt = 0; attempt < 3; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", authorization);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Content = new StringContent(JsonSerializer.Serialize(new
            {
                jql,
                maxResults,
                fields = Fields,
                nextPageToken
            }), Encoding.UTF8, "application/json");

            HttpResponseMessage response;
            try
            {
                response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new IntegrationException("Jira search timed out after 60 seconds. Check your network or VPN and try again.");
            }
            catch (HttpRequestException)
            {
                throw new IntegrationException("Could not reach Jira. Check the saved Jira URL, network, VPN, proxy and trusted HTTPS certificates.");
            }

            using (response)
            {
                if (response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500)
                {
                    if (attempt < 2)
                    {
                        var delay = response.Headers.RetryAfter?.Delta ??
                            (response.Headers.RetryAfter?.Date is { } date ? date - DateTimeOffset.UtcNow : TimeSpan.FromSeconds(attempt + 1));
                        if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;
                        if (delay <= TimeSpan.FromSeconds(5))
                        {
                            try { await Task.Delay(delay, timeout.Token); }
                            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                            {
                                throw new IntegrationException("Jira search timed out after 60 seconds. Check your network or VPN and try again.");
                            }
                            continue;
                        }
                    }
                    throw response.StatusCode == HttpStatusCode.TooManyRequests
                        ? new IntegrationException("Jira is rate limiting searches. Wait briefly and try again.")
                        : new IntegrationException("Jira is temporarily unavailable. Try the search again later.");
                }
                if (response.StatusCode == HttpStatusCode.Unauthorized)
                    throw new IntegrationException("Jira authentication failed. Recheck the Atlassian email and API token.");
                if (response.StatusCode == HttpStatusCode.Forbidden)
                    throw new IntegrationException("Jira denied this search. Check your account's project permissions and organization policies.");
                if (response.StatusCode == HttpStatusCode.BadRequest)
                    throw new IntegrationException("Jira rejected the search query. Check the JQL syntax and try again.");
                if (!response.IsSuccessStatusCode)
                    throw new IntegrationException($"Jira search returned HTTP {(int)response.StatusCode}. Check the saved Jira base URL.");
                if (response.Content.Headers.ContentType?.MediaType != "application/json")
                    throw new IntegrationException("Jira did not return JSON for this search. Check the saved Jira base URL.");

                try
                {
                    var body = await ReadBoundedAsync(response.Content, timeout.Token);
                    using var document = JsonDocument.Parse(body);
                    return ParseResult(document.RootElement, configuration.BaseUrl, maxResults);
                }
                catch (JsonException)
                {
                    throw new IntegrationException("Jira returned an invalid issue-search response. No issue results were displayed.");
                }
                catch (InvalidDataException)
                {
                    throw new IntegrationException("Jira's issue-search response exceeded the allowed size. Narrow the query and try again.");
                }
                catch (UriFormatException)
                {
                    throw new IntegrationException("Jira returned an invalid issue link. No issue results were displayed.");
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new IntegrationException("Jira search timed out after 60 seconds. Check your network or VPN and try again.");
                }
                catch (HttpRequestException)
                {
                    throw new IntegrationException("The Jira connection failed while receiving search results. Try again.");
                }
                catch (IOException)
                {
                    throw new IntegrationException("The Jira connection failed while receiving search results. Try again.");
                }
            }
        }

        throw new InvalidOperationException("Jira search retry loop did not terminate.");
    }

    public async Task<JiraIssueDetails> GetAsync(string issueKey, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(issueKey) || issueKey.Length > 255 ||
            !System.Text.RegularExpressions.Regex.IsMatch(issueKey, @"\A[A-Za-z][A-Za-z0-9_]*-[0-9]+\z", System.Text.RegularExpressions.RegexOptions.CultureInvariant))
            throw new IntegrationException("Enter a valid Jira issue key, such as ABC-123.");
        var (configuration, token) = await GetCredentialsAsync(cancellationToken);
        var endpoint = new Uri(configuration.BaseUrl.TrimEnd('/') + "/rest/api/3/issue/" + Uri.EscapeDataString(issueKey) + "?fields=*all");
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", CreateAuthorization(configuration.Email, token));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                throw new IntegrationException("Jira authentication failed. Recheck the Atlassian email and API token.");
            if (response.StatusCode == HttpStatusCode.Forbidden)
                throw new IntegrationException("Jira denied access to this issue. Check your account permissions.");
            if (response.StatusCode == HttpStatusCode.NotFound)
                throw new IntegrationException("Jira could not find this issue, or your account cannot view it.");
            if (!response.IsSuccessStatusCode)
                throw new IntegrationException($"Jira issue details returned HTTP {(int)response.StatusCode}. Try again or check the Jira connection.");
            if (response.Content.Headers.ContentType?.MediaType != "application/json")
                throw new IntegrationException("Jira did not return JSON for this issue. Check the saved Jira base URL.");
            var body = await ReadBoundedAsync(response.Content, timeout.Token);
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !TryGetString(root, "key", out var key) ||
                !root.TryGetProperty("fields", out var fields) || fields.ValueKind != JsonValueKind.Object)
                throw new JsonException();
            var url = new Uri(new Uri(configuration.BaseUrl.TrimEnd('/') + "/"), "browse/" + Uri.EscapeDataString(key));
            var summary = TryGetString(fields, "summary", out var text) ? text : "Summary unavailable";
            var description = fields.TryGetProperty("description", out var value) ? FormatDescription(value) : "Description not returned by Jira.";
            return new(key, url, JsonSerializer.Serialize(fields, new JsonSerializerOptions { WriteIndented = true }), summary,
                NestedName(fields, "status", "Unknown"), NestedName(fields, "assignee", "Unassigned"),
                NestedName(fields, "priority", "Unspecified"), NestedName(fields, "issuetype", "Unknown"), description);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new IntegrationException("Jira issue details timed out after 60 seconds. Check your network or VPN and try again.");
        }
        catch (JsonException) { throw new IntegrationException("Jira returned invalid issue details. No details were displayed."); }
        catch (InvalidDataException) { throw new IntegrationException("Jira issue details exceeded the allowed size. Open the issue in Jira instead."); }
        catch (HttpRequestException) { throw new IntegrationException("Could not retrieve Jira issue details. Check your network and try again."); }
        catch (IOException) { throw new IntegrationException("The Jira connection failed while receiving issue details. Try again."); }
    }

    public async Task<JiraCreateMetadata> GetAsync(string projectKey, string issueType, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(projectKey) || projectKey.Length > 255 ||
            !System.Text.RegularExpressions.Regex.IsMatch(projectKey, @"\A[A-Za-z][A-Za-z0-9_]*\z", System.Text.RegularExpressions.RegexOptions.CultureInvariant) ||
            string.IsNullOrWhiteSpace(issueType) || issueType.Length > 255)
            throw new IntegrationException("Enter a valid project key and issue type to inspect Jira create fields.");
        var (configuration, token) = await GetCredentialsAsync(cancellationToken);
        var prefix = configuration.BaseUrl.TrimEnd('/') + "/rest/api/3/issue/createmeta/" + Uri.EscapeDataString(projectKey) + "/issuetypes";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            var types = await ReadMetadataPagesAsync(prefix, "issueTypes", configuration.Email, token, timeout.Token);
            var matches = types.Where(item => TryGetString(item, "name", out var name) && string.Equals(name, issueType.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count != 1 || !TryGetString(matches[0], "id", out var id) || !id.All(char.IsAsciiDigit))
                throw new IntegrationException("The requested issue type is unavailable or ambiguous for this project. Check Jira create permissions and the issue type name.");
            var values = await ReadMetadataPagesAsync(prefix + "/" + id, "fields", configuration.Email, token, timeout.Token);
            var fields = new List<JiraCreateField>();
            foreach (var field in values)
            {
                if (!TryGetString(field, "fieldId", out var fieldId) || !TryGetString(field, "name", out var name) ||
                    !field.TryGetProperty("required", out var required) || required.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    throw new JsonException();
                var schemaType = field.TryGetProperty("schema", out var schema) && schema.ValueKind == JsonValueKind.Object && TryGetString(schema, "type", out var type) ? type : "unknown";
                fields.Add(new(fieldId, name, schemaType, required.GetBoolean(), field.TryGetProperty("hasDefaultValue", out var def) && def.ValueKind == JsonValueKind.True));
            }
            return new(projectKey, issueType.Trim(), id, fields);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new IntegrationException("Jira create metadata timed out. Check your network and try again."); }
        catch (JsonException) { throw new IntegrationException("Jira returned invalid create metadata. No fields were validated."); }
        catch (InvalidDataException) { throw new IntegrationException("Jira create metadata exceeded the allowed size. No fields were validated."); }
        catch (HttpRequestException) { throw new IntegrationException("Could not retrieve Jira create metadata. Check your network and try again."); }
        catch (IOException) { throw new IntegrationException("The Jira connection failed while receiving create metadata."); }
    }

    private async Task<List<JsonElement>> ReadMetadataPagesAsync(string endpoint, string arrayName, string email, string token, CancellationToken cancellationToken)
    {
        var result = new List<JsonElement>();
        for (var start = 0; start < 1000;)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint + $"?startAt={start}&maxResults=50");
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", CreateAuthorization(email, token));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode) throw new IntegrationException($"Jira create metadata returned HTTP {(int)response.StatusCode}. Check project access and create permissions.");
            if (response.Content.Headers.ContentType?.MediaType != "application/json") throw new IntegrationException("Jira create metadata was not JSON. Check the Jira connection.");
            var bytes = await ReadBoundedAsync(response.Content, cancellationToken);
            using var doc = JsonDocument.Parse(bytes);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(arrayName, out var array) || array.ValueKind != JsonValueKind.Array ||
                !root.TryGetProperty("total", out var totalElement) || !totalElement.TryGetInt32(out var total) || total < 0 || total > 1000 ||
                !root.TryGetProperty("startAt", out var startElement) || !startElement.TryGetInt32(out var actualStart) || actualStart != start ||
                array.GetArrayLength() > 50 || start + array.GetArrayLength() > total)
                throw new JsonException();
            foreach (var item in array.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) throw new JsonException();
                result.Add(item.Clone());
            }
            start += array.GetArrayLength();
            if (start == total) return result;
            if (array.GetArrayLength() == 0) throw new JsonException();
        }
        throw new IntegrationException("Jira returned too many create fields. No fields were validated.");
    }

    private static string FormatDescription(JsonElement description)
    {
        if (description.ValueKind == JsonValueKind.Null) return "No description.";
        if (description.ValueKind == JsonValueKind.String) return description.GetString() ?? "";
        if (description.ValueKind != JsonValueKind.Object) return "Description format not recognized; see raw fields.";
        var text = new StringBuilder();
        AppendDescription(description, text);
        var result = text.ToString().Trim();
        return result.Length == 0 ? "No readable description; see raw fields." : result;
    }

    private static void AppendDescription(JsonElement node, StringBuilder text)
    {
        if (node.ValueKind != JsonValueKind.Object) return;
        var type = node.TryGetProperty("type", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        if (type == "text" && node.TryGetProperty("text", out var content) && content.ValueKind == JsonValueKind.String)
            text.Append(content.GetString());
        else if (type == "hardBreak") text.AppendLine();
        else if (node.TryGetProperty("content", out var children) && children.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in children.EnumerateArray()) AppendDescription(child, text);
            if (type is "paragraph" or "heading" or "listItem" or "codeBlock") text.AppendLine();
        }
        else if (type is not null) text.Append($"[{type}: see raw fields]");
    }

    private async Task<(IntegrationSettings Configuration, string Token)> GetCredentialsAsync(CancellationToken cancellationToken)
    {
        var configuration = await settings.LoadAsync(IntegrationKind.Jira, cancellationToken);
        try { SettingsValidator.Validate(configuration); }
        catch (ArgumentException exception) { throw new IntegrationException(exception.Message); }
        var token = await credentials.GetAsync(IntegrationKind.Jira, cancellationToken);
        if (string.IsNullOrWhiteSpace(token))
            throw new IntegrationException("Save a Jira API token in Integrations & Settings before accessing Jira.");
        if (token.StartsWith("token:", StringComparison.OrdinalIgnoreCase) || token != token.Trim() || token.StartsWith('"'))
            throw new IntegrationException("The saved Jira token has an unsupported prefix or whitespace. Re-enter only the token value.");
        return (configuration, token);
    }

    private static void ValidateSearch(string jql, int maxResults, string? nextPageToken)
    {
        if (string.IsNullOrWhiteSpace(jql) || jql.Length > 4096 || jql.Any(char.IsControl) || jql.Contains(';'))
            throw new IntegrationException("Enter a single JQL search of 1–4096 characters without control characters or semicolons.");
        if (maxResults is < 1 or > 100)
            throw new IntegrationException("A Jira search page must request between 1 and 100 issues.");
        if (nextPageToken is { Length: > 2048 } || nextPageToken?.Any(char.IsControl) == true)
            throw new IntegrationException("The Jira continuation token is invalid. Start a new search.");
    }

    private static string CreateAuthorization(string email, string token)
    {
        var bytes = Encoding.UTF8.GetBytes($"{email}:{token}");
        try { return Convert.ToBase64String(bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static JiraIssueSearchResult ParseResult(JsonElement root, string baseUrl, int maxResults)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("issues", out var issues) ||
            issues.ValueKind != JsonValueKind.Array)
            throw new JsonException("Missing Jira issues array.");

        if (issues.GetArrayLength() > maxResults) throw new JsonException("Jira returned more issues than requested.");
        var output = new List<JiraIssueSummary>(issues.GetArrayLength());
        var jiraBase = new Uri(baseUrl.TrimEnd('/') + "/", UriKind.Absolute);
        foreach (var issue in issues.EnumerateArray())
        {
            if (output.Count == maxResults) break;
            if (issue.ValueKind != JsonValueKind.Object || !TryGetString(issue, "key", out var key) ||
                !issue.TryGetProperty("fields", out var fields) || fields.ValueKind != JsonValueKind.Object ||
                !fields.TryGetProperty("summary", out var summary) || summary.ValueKind != JsonValueKind.String)
                throw new JsonException("Malformed Jira issue record.");

            var issueUrl = new Uri(jiraBase, "browse/" + Uri.EscapeDataString(key));
            output.Add(new(key, summary.GetString() ?? string.Empty,
                NestedName(fields, "status", "Unknown"), NestedName(fields, "assignee", "Unassigned"),
                NestedName(fields, "priority", "Unspecified"), NestedName(fields, "issuetype", "Unknown"), issueUrl));
        }

        var nextToken = root.TryGetProperty("nextPageToken", out var tokenValue) && tokenValue.ValueKind == JsonValueKind.String
            ? tokenValue.GetString()
            : null;
        var isLast = root.TryGetProperty("isLast", out var lastValue) && lastValue.ValueKind == JsonValueKind.True;
        int? total = root.TryGetProperty("total", out var totalValue) && totalValue.ValueKind == JsonValueKind.Number && totalValue.TryGetInt32(out var count) ? count : null;
        return new(output, !isLast && !string.IsNullOrWhiteSpace(nextToken), nextToken, total, maxResults);
    }

    private static string NestedName(JsonElement fields, string property, string fallback) =>
        fields.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Object &&
        TryGetString(value, "displayName", out var displayName) ? displayName :
        fields.TryGetProperty(property, out value) && value.ValueKind == JsonValueKind.Object &&
        TryGetString(value, "name", out var name) ? name : fallback;

    private static bool TryGetString(JsonElement element, string property, out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(property, out var propertyValue) || propertyValue.ValueKind != JsonValueKind.String)
            return false;
        value = propertyValue.GetString() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(value);
    }

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
