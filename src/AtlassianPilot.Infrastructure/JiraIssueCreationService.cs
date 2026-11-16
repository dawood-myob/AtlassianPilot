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

public sealed partial class JiraIssueSearchService
{
    private sealed record PendingCreation(string SiteUrl, string Email, string CredentialHash, string Payload, DateTimeOffset ExpiresAt);
    private readonly ConcurrentDictionary<Guid, PendingCreation> pendingCreations = new();
    private static readonly HashSet<string> ManagedFields = new(StringComparer.Ordinal) { "project", "issuetype", "summary", "description", "parent" };
    private static readonly HashSet<string> ExcludedFields = new(StringComparer.Ordinal)
        { "attachment", "issuelinks", "issuerestriction", "reporter", "status", "resolution", "created", "updated", "creator", "comment", "worklog" };
    private const string UncertainOutcome = "The creation outcome is unknown. Jira may already have created the issue. Check Jira before preparing another approval to avoid duplicates. This request will not be retried.";

    public async Task<IReadOnlyList<JiraIssueType>> GetIssueTypesAsync(string projectKey, CancellationToken cancellationToken = default)
    {
        ValidateProjectKey(projectKey);
        var (config, token) = await GetCredentialsAsync(cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            var types = await ReadMetadataPagesAsync(config.BaseUrl.TrimEnd('/') + "/rest/api/3/issue/createmeta/" + Uri.EscapeDataString(projectKey) + "/issuetypes",
                "issueTypes", config.Email, token, timeout.Token);
            return types.Select(item => TryGetString(item, "id", out var id) && TryGetString(item, "name", out var name)
                ? new JiraIssueType(id, name) : throw new JsonException()).ToArray();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new IntegrationException("Loading Jira issue types timed out. Try again."); }
        catch (JsonException) { throw new IntegrationException("Jira returned invalid issue types. No type was selected."); }
        catch (InvalidDataException) { throw new IntegrationException("The Jira issue-type response exceeded the size limit."); }
        catch (HttpRequestException) { throw new IntegrationException("Could not load Jira issue types. Check the Jira connection."); }
        catch (IOException) { throw new IntegrationException("The Jira connection failed while loading issue types."); }
    }

    public async Task<JiraCreationTemplate> LoadTemplateAsync(string projectKey, string issueType, string? referenceKey, CancellationToken cancellationToken = default)
    {
        var metadata = await GetAsync(projectKey, issueType, cancellationToken);
        var initial = new JsonObject();
        var notice = "No reference values loaded. Omitted optional fields use Jira defaults.";
        if (!string.IsNullOrWhiteSpace(referenceKey))
        {
            var reference = await GetAsync(referenceKey.Trim(), cancellationToken);
            using var json = JsonDocument.Parse(reference.FieldsJson);
            var fields = json.RootElement;
            if (!fields.TryGetProperty("project", out var project) || !TryGetString(project, "key", out var key) || !string.Equals(key, projectKey, StringComparison.OrdinalIgnoreCase) ||
                !fields.TryGetProperty("issuetype", out var type) || !TryGetString(type, "id", out var id) || id != metadata.IssueTypeId)
                throw new IntegrationException("The reference issue must have the same project and issue type. Choose its type or remove the reference.");
            var copied = new List<string>();
            foreach (var field in metadata.Fields.Where(field => !ManagedFields.Contains(field.Id) && !IsExcluded(field) && field.CanSet))
            {
                if (!fields.TryGetProperty(field.Id, out var value) || value.ValueKind == JsonValueKind.Null) continue;
                var normalized = NormalizeReference(field, value);
                if (normalized is null) continue;
                initial[field.Id] = normalized;
                copied.Add(field.Name);
            }
            // A parent is a deliberate hierarchy choice, never inherited from the reference.
            notice = copied.Count == 0 ? "No compatible reference defaults were copied." : "Reference defaults loaded for review: " + string.Join(", ", copied) + ".";
            notice += " Summary, description, parent, reporter, history, status, attachments, links, restrictions and unsupported/computed fields are not copied. Check assignee, dates, teams and options before approval.";
        }
        return new(metadata, initial.ToJsonString(), notice);
    }

    public async Task<JiraCreationPreview> PrepareAsync(JiraIssueDraft draft, string additionalFieldsJson, CancellationToken cancellationToken = default)
    {
        ValidateProjectKey(draft.ProjectKey);
        if (string.IsNullOrWhiteSpace(draft.Summary) || draft.Summary.Length > 255 || string.IsNullOrWhiteSpace(draft.Description) || draft.Description.Length > 32000)
            throw new IntegrationException("Review a summary of 1-255 characters and a description of 1-32000 characters before preparing approval.");
        if (draft.ParentKey is not null && !Regex.IsMatch(draft.ParentKey, @"\A[A-Za-z][A-Za-z0-9_]*-[0-9]+\z", RegexOptions.CultureInvariant))
            throw new IntegrationException("Enter a parent issue key such as ABC-123, or leave Parent blank.");
        if (additionalFieldsJson.Length > 128000) throw new IntegrationException("Additional fields exceed the supported size.");
        var (configuration, token) = await GetCredentialsAsync(cancellationToken);
        var metadata = await GetAsync(draft.ProjectKey, draft.IssueType, cancellationToken);
        JsonObject extra;
        try
        {
            using var document = JsonDocument.Parse(additionalFieldsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object || document.RootElement.EnumerateObject().Select(p => p.Name).Distinct().Count() != document.RootElement.EnumerateObject().Count())
                throw new JsonException();
            extra = JsonNode.Parse(additionalFieldsJson)!.AsObject();
        }
        catch (JsonException) { throw new IntegrationException("Additional field values must form a JSON object with unique field IDs."); }
        var payloadFields = new JsonObject
        {
            ["project"] = new JsonObject { ["key"] = draft.ProjectKey },
            ["issuetype"] = new JsonObject { ["id"] = metadata.IssueTypeId },
            ["summary"] = draft.Summary,
            ["description"] = ToDocument(draft.Description)
        };
        if (draft.ParentKey is not null) payloadFields["parent"] = new JsonObject { ["key"] = draft.ParentKey };
        foreach (var entry in extra)
        {
            var field = metadata.Fields.SingleOrDefault(field => field.Id == entry.Key);
            if (field is null || ManagedFields.Contains(entry.Key) || IsExcluded(field) || !field.CanSet)
                throw new IntegrationException($"Field '{entry.Key}' cannot be supplied by this creation workflow. Reload project fields.");
            if (entry.Value is null) continue;
            ValidateFieldValue(field, entry.Value);
            payloadFields[entry.Key] = entry.Value.DeepClone();
        }
        foreach (var field in metadata.Fields)
        {
            if (payloadFields.ContainsKey(field.Id) && !field.CanSet)
                throw new IntegrationException($"Jira does not allow setting '{field.Name}' on this create screen.");
            if (field.Required && !field.HasDefault && (!payloadFields.TryGetPropertyValue(field.Id, out var value) || IsEmpty(value)))
                throw new IntegrationException($"Jira requires '{field.Name}' ({field.Id}). Supply it before preparing approval.");
        }
        foreach (var name in payloadFields.Select(pair => pair.Key))
            if (!metadata.Fields.Any(field => field.Id == name && field.CanSet))
                throw new IntegrationException($"The Jira create screen does not support '{name}'. Nothing was created.");
        var (currentConfig, currentToken) = await GetCredentialsAsync(cancellationToken);
        if (configuration.BaseUrl != currentConfig.BaseUrl || configuration.Email != currentConfig.Email || token != currentToken)
            throw new IntegrationException("Jira settings changed during preparation. Reload fields and prepare again.");
        foreach (var stale in pendingCreations.Where(item => item.Value.ExpiresAt <= DateTimeOffset.UtcNow).Select(item => item.Key)) pendingCreations.TryRemove(stale, out _);
        var id = Guid.NewGuid();
        var payload = new JsonObject { ["fields"] = payloadFields }.ToJsonString();
        pendingCreations[id] = new(configuration.BaseUrl, configuration.Email, HashCredential(token), payload, DateTimeOffset.UtcNow.AddMinutes(15));
        return new(id, configuration.BaseUrl, draft.Summary, payloadFields.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    public void DiscardPreview(Guid previewId) => pendingCreations.TryRemove(previewId, out _);

    public async Task<JiraCreatedIssue> CreateApprovedAsync(Guid previewId, CancellationToken cancellationToken = default)
    {
        if (!pendingCreations.TryRemove(previewId, out var approved) || approved.ExpiresAt <= DateTimeOffset.UtcNow)
            throw new IntegrationException("This approval has expired or was already used. Prepare a new preview; do not retry an uncertain creation without checking Jira.");
        var (config, token) = await GetCredentialsAsync(cancellationToken);
        if (config.BaseUrl != approved.SiteUrl || config.Email != approved.Email || HashCredential(token) != approved.CredentialHash)
            throw new IntegrationException("Jira settings or credentials changed after preview. Prepare and review a new approval.");
        using var request = new HttpRequestMessage(HttpMethod.Post, config.BaseUrl.TrimEnd('/') + "/rest/api/3/issue");
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", CreateAuthorization(config.Email, token));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Content = new StringContent(approved.Payload, Encoding.UTF8, "application/json");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            // Never retry a non-idempotent create, including after a lost response.
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity)
                throw new IntegrationException("Jira rejected the issue fields. Recheck required fields, option IDs, parent hierarchy and project rules, then prepare a new preview. No automatic retry was made.");
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new IntegrationException("Jira denied issue creation. Check credentials and Create issues permissions. No automatic retry was made.");
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                throw new IntegrationException("Jira rate limited creation. No automatic retry was made; wait before preparing another approval.");
            if (response.StatusCode != HttpStatusCode.Created || response.Content.Headers.ContentType?.MediaType != "application/json")
                throw new IntegrationException(UncertainOutcome);
            using var json = JsonDocument.Parse(await ReadBoundedAsync(response.Content, timeout.Token));
            if (json.RootElement.ValueKind != JsonValueKind.Object || !TryGetString(json.RootElement, "key", out var key) ||
                !Regex.IsMatch(key, @"\A[A-Za-z][A-Za-z0-9_]*-[0-9]+\z", RegexOptions.CultureInvariant)) throw new JsonException();
            return new(key, new Uri(config.BaseUrl.TrimEnd('/') + "/browse/" + Uri.EscapeDataString(key)));
        }
        catch (OperationCanceledException) { throw new IntegrationException(UncertainOutcome); }
        catch (HttpRequestException) { throw new IntegrationException(UncertainOutcome); }
        catch (IOException) { throw new IntegrationException(UncertainOutcome); }
        catch (JsonException) { throw new IntegrationException(UncertainOutcome); }
        catch (InvalidDataException) { throw new IntegrationException(UncertainOutcome); }
    }

    private static void ValidateProjectKey(string projectKey)
    {
        if (projectKey.Length is 0 or > 255 || !Regex.IsMatch(projectKey, @"\A[A-Za-z][A-Za-z0-9_]*\z", RegexOptions.CultureInvariant))
            throw new IntegrationException("Enter a valid Jira project key, such as WOR.");
    }

    private static bool IsExcluded(JiraCreateField field) => ExcludedFields.Contains(field.Id) || field.SchemaType is "any" or "unknown" ||
        field.SchemaCustom?.Contains(":rank", StringComparison.OrdinalIgnoreCase) == true || field.SchemaCustom?.Contains(":development", StringComparison.OrdinalIgnoreCase) == true;

    private static bool IsEmpty(JsonNode? value) => value is null || value is JsonArray { Count: 0 } || value is JsonObject { Count: 0 } ||
        value is JsonValue scalar && scalar.TryGetValue<string>(out var text) && string.IsNullOrWhiteSpace(text);

    private static void ValidateFieldValue(JiraCreateField field, JsonNode value)
    {
        if (IsEmpty(value)) throw new IntegrationException($"'{field.Name}' cannot be empty when supplied. Clear it to use Jira defaults.");
        var valid = field.SchemaType switch
        {
            "array" => value is JsonArray,
            "number" => value is JsonValue number && number.TryGetValue<double>(out _),
            "string" when field.SchemaCustom?.EndsWith(":textarea", StringComparison.Ordinal) == true => value is JsonObject,
            "string" or "date" or "datetime" or "team" => value is JsonValue text && text.TryGetValue<string>(out _),
            "boolean" => value is JsonValue boolean && boolean.TryGetValue<bool>(out _),
            _ => value is JsonObject
        };
        if (!valid) throw new IntegrationException($"'{field.Name}' has the wrong value format for Jira type {field.SchemaType}.");
        if (field.SchemaType == "date" && (!DateOnly.TryParseExact(value.GetValue<string>(), "yyyy-MM-dd", out _)))
            throw new IntegrationException($"'{field.Name}' must use yyyy-MM-dd.");
        if (field.AllowedValuesJson is not null)
        {
            var allowed = JsonNode.Parse(field.AllowedValuesJson)!.AsArray();
            if (allowed.Count > 0)
            {
                var selected = value is JsonArray array ? array.ToArray() : new[] { value };
                foreach (var item in selected)
                    if (!allowed.Any(option => OptionMatches(option, item)))
                        throw new IntegrationException($"'{field.Name}' is not one of Jira's available values. Choose an available option or clear it.");
            }
        }
    }

    private static bool OptionMatches(JsonNode? option, JsonNode? value)
    {
        if (option is JsonObject obj && value is JsonObject selected)
            return new[] { "id", "accountId", "value" }.Any(key => obj[key] is not null && selected[key] is not null && JsonNode.DeepEquals(obj[key], selected[key]));
        return JsonNode.DeepEquals(option, value);
    }

    private static JsonNode? NormalizeReference(JiraCreateField field, JsonElement value)
    {
        if (value.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
            return JsonNode.Parse(value.GetRawText());
        if (value.ValueKind == JsonValueKind.Array)
        {
            var array = new JsonArray();
            foreach (var item in value.EnumerateArray())
            {
                var normalized = NormalizeReference(field with { SchemaType = field.SchemaItems ?? "unknown" }, item);
                if (normalized is null) return null;
                array.Add(normalized);
            }
            return array.Count == 0 ? null : array;
        }
        if (value.ValueKind != JsonValueKind.Object) return null;
        if (field.SchemaType == "team" && TryGetString(value, "id", out var teamId)) return JsonValue.Create(teamId);
        if (field.SchemaType == "user" && TryGetString(value, "accountId", out var accountId)) return new JsonObject { ["accountId"] = accountId };
        if (field.SchemaType is "option" or "priority" or "component" or "version" && TryGetString(value, "id", out var id)) return new JsonObject { ["id"] = id };
        return null;
    }

    private static JsonObject ToDocument(string description)
    {
        var paragraphs = new JsonArray();
        foreach (var line in description.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            paragraphs.Add(new JsonObject { ["type"] = "paragraph", ["content"] = line.Length == 0 ? new JsonArray() : new JsonArray(new JsonObject { ["type"] = "text", ["text"] = line }) });
        return new JsonObject { ["type"] = "doc", ["version"] = 1, ["content"] = paragraphs };
    }

    private static string HashCredential(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
