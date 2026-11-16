using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AtlassianPilot.Core;

namespace AtlassianPilot.Infrastructure;

public sealed class JiraIssueActionService(HttpClient http, ISettingsStore settings, ICredentialStore credentials,
    IJiraIssueDetailsService details, IJiraCreateMetadataService metadata) : IJiraIssueActionService
{
    private static readonly TimeSpan PreviewLifetime = TimeSpan.FromMinutes(15);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, MaxDepth = 32 };
    private readonly ConcurrentDictionary<Guid, PendingAction> previews = new();
    private readonly SemaphoreSlim writeGate = new(1, 1);

    private sealed record PendingAction(JiraActionPreview Preview, string BaseUrl, string Email, byte[] CredentialFingerprint,
        HttpMethod Method, string Path, string Payload, bool ReturnsCreatedIssue);

    public async Task<IReadOnlyList<JiraWorkflowTransition>> GetTransitionsAsync(string issueKey, CancellationToken cancellationToken = default)
    {
        ValidateIssueKey(issueKey);
        var connection = await GetConnectionAsync(cancellationToken);
        var root = await ReadJsonAsync(connection, HttpMethod.Get, $"/rest/api/3/issue/{EscapeKey(issueKey)}/transitions", null, cancellationToken);
        if (!root.TryGetProperty("transitions", out var array) || array.ValueKind != JsonValueKind.Array || array.GetArrayLength() > 200)
            throw new IntegrationException("Jira returned invalid workflow transitions.");
        var result = new List<JiraWorkflowTransition>();
        foreach (var item in array.EnumerateArray())
        {
            if (!TryString(item, "id", out var id) || !id.All(char.IsAsciiDigit) || !TryString(item, "name", out var name))
                throw new IntegrationException("Jira returned an invalid workflow transition. No transition was offered.");
            result.Add(new(id, name));
        }
        return result;
    }

    public async Task<IReadOnlyList<JiraIssueLinkType>> GetLinkTypesAsync(CancellationToken cancellationToken = default)
    {
        var connection = await GetConnectionAsync(cancellationToken);
        var root = await ReadJsonAsync(connection, HttpMethod.Get, "/rest/api/3/issueLinkType", null, cancellationToken);
        if (!root.TryGetProperty("issueLinkTypes", out var array) || array.ValueKind != JsonValueKind.Array || array.GetArrayLength() > 200)
            throw new IntegrationException("Jira returned invalid issue link types.");
        var result = new List<JiraIssueLinkType>();
        foreach (var item in array.EnumerateArray())
        {
            if (!TryString(item, "name", out var name) || !TryString(item, "inward", out var inward) || !TryString(item, "outward", out var outward))
                throw new IntegrationException("Jira returned an invalid issue link type. No link type was offered.");
            result.Add(new(name, inward, outward));
        }
        return result;
    }

    public async Task<JiraActionPreview> PrepareUpdateAsync(string issueKey, string? summary, string? description, CancellationToken cancellationToken = default)
    {
        ValidateIssueKey(issueKey);
        var fields = new JsonObject();
        if (summary is not null)
        {
            var value = summary.Trim();
            if (value.Length is 0 or > 255 || value.Any(char.IsControl)) throw new IntegrationException("An updated summary must contain 1–255 printable characters.");
            fields["summary"] = value;
        }
        if (description is not null)
        {
            if (description.Length > 32000) throw new IntegrationException("An updated description must be 32,000 characters or fewer.");
            fields["description"] = PlainTextDocument(description);
        }
        if (fields.Count == 0) throw new IntegrationException("Enter a new summary or description to update.");
        var connection = await GetConnectionAsync(cancellationToken);
        return AddPreview(connection, "Update issue", issueKey, "Update the selected issue fields", HttpMethod.Put,
            $"/rest/api/3/issue/{EscapeKey(issueKey)}", new JsonObject { ["fields"] = fields }, false);
    }

    public async Task<JiraActionPreview> PrepareCommentAsync(string issueKey, string comment, CancellationToken cancellationToken = default)
    {
        ValidateIssueKey(issueKey);
        if (string.IsNullOrWhiteSpace(comment) || comment.Trim().Length > 16000) throw new IntegrationException("A comment must contain 1–16,000 characters.");
        var connection = await GetConnectionAsync(cancellationToken);
        return AddPreview(connection, "Add comment", issueKey, "Add a comment to the selected issue", HttpMethod.Post,
            $"/rest/api/3/issue/{EscapeKey(issueKey)}/comment", new JsonObject { ["body"] = PlainTextDocument(comment.Trim()) }, false);
    }

    public async Task<JiraActionPreview> PrepareAssignmentAsync(string issueKey, string accountId, CancellationToken cancellationToken = default)
    {
        ValidateIssueKey(issueKey);
        if (string.IsNullOrWhiteSpace(accountId) || accountId.Length > 255 || accountId.Any(char.IsControl))
            throw new IntegrationException("Enter a valid Jira account ID for the assignee.");
        var connection = await GetConnectionAsync(cancellationToken);
        return AddPreview(connection, "Assign issue", issueKey, "Assign the selected issue to the specified account", HttpMethod.Put,
            $"/rest/api/3/issue/{EscapeKey(issueKey)}/assignee", new JsonObject { ["accountId"] = accountId.Trim() }, false);
    }

    public async Task<JiraActionPreview> PrepareTransitionAsync(string issueKey, string transitionId, CancellationToken cancellationToken = default)
    {
        ValidateIssueKey(issueKey);
        if (string.IsNullOrWhiteSpace(transitionId) || !transitionId.All(char.IsAsciiDigit)) throw new IntegrationException("Choose a workflow transition returned by Jira.");
        var transitions = await GetTransitionsAsync(issueKey, cancellationToken);
        var selected = transitions.SingleOrDefault(item => item.Id == transitionId) ?? throw new IntegrationException("That workflow transition is no longer available. Reload transitions before preparing the action.");
        var connection = await GetConnectionAsync(cancellationToken);
        return AddPreview(connection, "Transition issue", issueKey, $"Transition to {selected.Name}", HttpMethod.Post,
            $"/rest/api/3/issue/{EscapeKey(issueKey)}/transitions", new JsonObject { ["transition"] = new JsonObject { ["id"] = selected.Id } }, false);
    }

    public async Task<JiraActionPreview> PrepareLinkAsync(string issueKey, string linkedIssueKey, string linkType, CancellationToken cancellationToken = default)
    {
        ValidateIssueKey(issueKey);
        ValidateIssueKey(linkedIssueKey);
        if (string.Equals(issueKey, linkedIssueKey, StringComparison.OrdinalIgnoreCase)) throw new IntegrationException("An issue cannot be linked to itself.");
        if (string.IsNullOrWhiteSpace(linkType) || linkType.Length > 255) throw new IntegrationException("Choose an issue link type returned by Jira.");
        var types = await GetLinkTypesAsync(cancellationToken);
        var selected = types.SingleOrDefault(item => string.Equals(item.Name, linkType, StringComparison.Ordinal)) ?? throw new IntegrationException("That issue link type is no longer available. Reload link types before preparing the action.");
        var connection = await GetConnectionAsync(cancellationToken);
        var payload = new JsonObject
        {
            ["type"] = new JsonObject { ["name"] = selected.Name },
            ["outwardIssue"] = new JsonObject { ["key"] = issueKey },
            ["inwardIssue"] = new JsonObject { ["key"] = linkedIssueKey }
        };
        return AddPreview(connection, "Link issues", issueKey, $"{issueKey} {selected.Outward} {linkedIssueKey} ({selected.Inward})", HttpMethod.Post,
            "/rest/api/3/issueLink", payload, false);
    }

    public async Task<JiraActionPreview> PrepareCloneAsync(string sourceIssueKey, string projectKey, string issueType, CancellationToken cancellationToken = default)
    {
        ValidateIssueKey(sourceIssueKey);
        if (string.IsNullOrWhiteSpace(projectKey) || projectKey.Length > 255 || !Regex.IsMatch(projectKey, @"\A[A-Za-z][A-Za-z0-9_]*\z", RegexOptions.CultureInvariant))
            throw new IntegrationException("Enter a valid destination project key.");
        if (string.IsNullOrWhiteSpace(issueType) || issueType.Length > 255 || issueType.Any(char.IsControl)) throw new IntegrationException("Enter a valid destination issue type.");
        var connection = await GetConnectionAsync(cancellationToken);
        var destination = await metadata.GetAsync(projectKey.Trim(), issueType.Trim(), cancellationToken);
        var unsupportedRequired = destination.Fields.FirstOrDefault(field => field.Required && !field.HasDefault &&
            !new[] { "project", "issuetype", "summary", "description" }.Contains(field.Id, StringComparer.Ordinal));
        if (unsupportedRequired is not null)
            throw new IntegrationException($"The destination requires '{unsupportedRequired.Name}', which this clone preview cannot safely infer. Use Create Jira to supply all required fields.");
        var source = await details.GetAsync(sourceIssueKey, cancellationToken);
        using var sourceFields = JsonDocument.Parse(source.FieldsJson);
        var sourceRoot = sourceFields.RootElement;
        var title = source.Summary.Trim();
        var cloneSummary = (title.Length > 246 ? title[..246] : title) + " (clone)";
        JsonNode? description = null;
        if (sourceRoot.TryGetProperty("description", out var sourceDescription)) description = JsonNode.Parse(sourceDescription.GetRawText());
        var fields = new JsonObject
        {
            ["project"] = new JsonObject { ["key"] = projectKey.Trim() },
            ["issuetype"] = new JsonObject { ["name"] = issueType.Trim() },
            ["summary"] = cloneSummary,
            ["description"] = description
        };
        return AddPreview(connection, "Clone issue", sourceIssueKey,
            $"Create a copy in {projectKey.Trim()} as {issueType.Trim()}; comments, attachments, links, history and other fields are not copied",
            HttpMethod.Post, "/rest/api/3/issue", new JsonObject { ["fields"] = fields }, true);
    }

    public async Task<JiraActionResult> ExecuteApprovedAsync(Guid previewId, CancellationToken cancellationToken = default)
    {
        await writeGate.WaitAsync(cancellationToken);
        try
        {
            if (!previews.TryRemove(previewId, out var pending) || pending.Preview.ExpiresAt <= DateTimeOffset.UtcNow)
                throw new IntegrationException("This Jira approval is missing, expired or already used. Prepare a new preview.");
            var connection = await GetConnectionAsync(cancellationToken);
            var currentFingerprint = Fingerprint(connection.Email, connection.Token);
            if (!string.Equals(connection.BaseUrl, pending.BaseUrl, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(connection.Email, pending.Email, StringComparison.OrdinalIgnoreCase) ||
                !CryptographicOperations.FixedTimeEquals(currentFingerprint, pending.CredentialFingerprint))
                throw new IntegrationException("Jira settings or credentials changed after preview. Prepare and review a new approval.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            using var request = new HttpRequestMessage(pending.Method, new Uri(connection.BaseUrl + pending.Path));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", CreateAuthorization(connection.Email, connection.Token));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Content = new StringContent(pending.Payload, Encoding.UTF8, "application/json");
            cancellationToken.ThrowIfCancellationRequested();
            HttpResponseMessage response;
            try { response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token); }
            catch (OperationCanceledException) { throw new IntegrationException("Jira write was cancelled after submission; the outcome is unknown. Check Jira before retrying."); }
            catch (HttpRequestException) { throw new IntegrationException("Jira could not confirm the write; the outcome is unknown. Check Jira before retrying."); }
            using (response)
            {
                if (response.StatusCode == HttpStatusCode.Unauthorized) throw new IntegrationException("Jira authentication failed; the approved action was rejected before completion.");
                if (response.StatusCode == HttpStatusCode.Forbidden) throw new IntegrationException("Jira denied this action. Check project permissions and workflow rules.");
                if (response.StatusCode == HttpStatusCode.NotFound) throw new IntegrationException("Jira could not find the issue or action target. No success was reported.");
                if (!response.IsSuccessStatusCode) throw new IntegrationException($"Jira returned HTTP {(int)response.StatusCode}; the write outcome may be unknown. Check Jira before retrying.");
                if (pending.ReturnsCreatedIssue)
                {
                    var root = await ReadResponseJsonAsync(response, timeout.Token);
                    if (!TryString(root, "key", out var key)) throw new IntegrationException("Jira accepted the clone request but returned no issue key; check Jira before retrying.");
                    var url = new Uri(new Uri(connection.BaseUrl + "/"), "browse/" + Uri.EscapeDataString(key));
                    return new($"Created clone {key}.", key, url);
                }
                return new($"{pending.Preview.Action} completed for {pending.Preview.IssueKey}.");
            }
        }
        finally { writeGate.Release(); }
    }

    public void DiscardPreview(Guid previewId) => previews.TryRemove(previewId, out _);

    private JiraActionPreview AddPreview(Connection connection, string action, string issueKey, string summary, HttpMethod method,
        string path, JsonNode payload, bool returnsCreatedIssue)
    {
        var json = payload.ToJsonString(JsonOptions);
        if (json.Length > 256_000) throw new IntegrationException("The approved Jira payload is too large.");
        var now = DateTimeOffset.UtcNow;
        var preview = new JiraActionPreview(Guid.NewGuid(), action, issueKey, summary, json, now + PreviewLifetime);
        foreach (var expired in previews.Where(pair => pair.Value.Preview.ExpiresAt <= now).Select(pair => pair.Key).ToArray()) previews.TryRemove(expired, out _);
        if (previews.Count >= 100) throw new IntegrationException("Too many pending Jira previews. Discard an unused preview or wait for an approval to expire.");
        previews[preview.Id] = new(preview, connection.BaseUrl, connection.Email, Fingerprint(connection.Email, connection.Token), method, path, json, returnsCreatedIssue);
        return preview;
    }

    private async Task<Connection> GetConnectionAsync(CancellationToken cancellationToken)
    {
        var configuration = await settings.LoadAsync(IntegrationKind.Jira, cancellationToken);
        try { SettingsValidator.Validate(configuration); }
        catch (ArgumentException exception) { throw new IntegrationException(exception.Message); }
        var token = await credentials.GetAsync(IntegrationKind.Jira, cancellationToken);
        if (string.IsNullOrWhiteSpace(token)) throw new IntegrationException("Save a Jira API token in Integrations & Settings before accessing Jira.");
        if (token.StartsWith("token:", StringComparison.OrdinalIgnoreCase) || token != token.Trim() || token.StartsWith('"'))
            throw new IntegrationException("The saved Jira token has an unsupported prefix or whitespace. Re-enter only the token value.");
        return new(configuration.BaseUrl.TrimEnd('/'), configuration.Email, token);
    }

    private async Task<JsonElement> ReadJsonAsync(Connection connection, HttpMethod method, string path, string? payload, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        using var request = CreateRequest(connection, method, path, payload);
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode) throw MapFailure(response.StatusCode);
            return await ReadResponseJsonAsync(response, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new IntegrationException("Jira lookup timed out. Check your network and try again."); }
        catch (HttpRequestException) { throw new IntegrationException("Could not reach Jira. Check the saved Jira URL, network and VPN."); }
        catch (JsonException) { throw new IntegrationException("Jira returned invalid action metadata."); }
    }

    private static HttpRequestMessage CreateRequest(Connection connection, HttpMethod method, string path, string? payload)
    {
        var request = new HttpRequestMessage(method, new Uri(connection.BaseUrl + path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", CreateAuthorization(connection.Email, connection.Token));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (payload is not null) request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        return request;
    }

    private static async Task<JsonElement> ReadResponseJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentType?.MediaType != "application/json") throw new IntegrationException("Jira did not return JSON action metadata.");
        var body = await ReadBoundedAsync(response.Content, cancellationToken);
        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        const int maxBytes = 1_000_000;
        if (content.Headers.ContentLength > maxBytes) throw new IntegrationException("Jira action metadata exceeded the allowed size.");
        await using var input = await content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken)) != 0)
        {
            if (output.Length + read > maxBytes) throw new IntegrationException("Jira action metadata exceeded the allowed size.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private static IntegrationException MapFailure(HttpStatusCode status) => status switch
    {
        HttpStatusCode.Unauthorized => new("Jira authentication failed. Recheck the Atlassian email and API token."),
        HttpStatusCode.Forbidden => new("Jira denied this operation. Check your account permissions."),
        HttpStatusCode.NotFound => new("Jira could not find this issue or action metadata."),
        HttpStatusCode.BadRequest => new("Jira rejected the requested operation. Check the issue, fields and workflow."),
        _ => new($"Jira action metadata returned HTTP {(int)status}.")
    };

    private static JsonObject PlainTextDocument(string text) => new()
    {
        ["type"] = "doc", ["version"] = 1,
        ["content"] = new JsonArray(text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n').Select(line =>
            (JsonNode)new JsonObject { ["type"] = "paragraph", ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = line }) }).ToArray())
    };

    private static void ValidateIssueKey(string issueKey)
    {
        if (string.IsNullOrWhiteSpace(issueKey) || issueKey.Length > 255 || !Regex.IsMatch(issueKey, @"\A[A-Za-z][A-Za-z0-9_]*-[0-9]+\z", RegexOptions.CultureInvariant))
            throw new IntegrationException("Enter a valid Jira issue key, such as ABC-123.");
    }

    private static string EscapeKey(string key) => Uri.EscapeDataString(key);
    private static bool TryString(JsonElement element, string property, out string value)
    {
        value = "";
        if (!element.TryGetProperty(property, out var child) || child.ValueKind != JsonValueKind.String) return false;
        value = child.GetString() ?? "";
        return !string.IsNullOrWhiteSpace(value);
    }
    private static string CreateAuthorization(string email, string token)
    {
        var bytes = Encoding.UTF8.GetBytes($"{email}:{token}");
        try { return Convert.ToBase64String(bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    private static byte[] Fingerprint(string email, string token)
    {
        var bytes = Encoding.UTF8.GetBytes($"{email.ToUpperInvariant()}\0{token}");
        try { return SHA256.HashData(bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    private sealed record Connection(string BaseUrl, string Email, string Token);
}
