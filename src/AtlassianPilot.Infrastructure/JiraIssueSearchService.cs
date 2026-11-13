using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AtlassianPilot.Core;

namespace AtlassianPilot.Infrastructure;

public sealed class JiraIssueSearchService(HttpClient http, ISettingsStore settings, ICredentialStore credentials)
    : IJiraIssueSearchService
{
    private static readonly string[] Fields = ["summary", "status", "assignee", "priority", "issuetype"];
    private const int MaximumResponseBytes = 2 * 1024 * 1024;

    public async Task<JiraIssueSearchResult> SearchAsync(string jql, int maxResults = 50, string? nextPageToken = null,
        CancellationToken cancellationToken = default)
    {
        ValidateSearch(jql, maxResults, nextPageToken);
        var configuration = await settings.LoadAsync(IntegrationKind.Jira, cancellationToken);
        try { SettingsValidator.Validate(configuration); }
        catch (ArgumentException exception) { throw new IntegrationException(exception.Message); }

        var token = await credentials.GetAsync(IntegrationKind.Jira, cancellationToken);
        if (string.IsNullOrWhiteSpace(token))
            throw new IntegrationException("Save a Jira API token in Integrations & Settings before searching.");
        if (token.StartsWith("token:", StringComparison.OrdinalIgnoreCase) || token != token.Trim() || token.StartsWith('"'))
            throw new IntegrationException("The saved Jira token has an unsupported prefix or whitespace. Re-enter only the token value.");

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
