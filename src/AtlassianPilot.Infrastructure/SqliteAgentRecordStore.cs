using System.Text.Json;
using AtlassianPilot.Core;
using Microsoft.Data.Sqlite;

namespace AtlassianPilot.Infrastructure;

public sealed class SqliteAgentRecordStore(string databasePath) : IConversationHistoryStore, IActivityLogStore
{
    private readonly string connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString();
    private static readonly JsonSerializerOptions JsonOptions = new() { MaxDepth = 16 };
    private static readonly HashSet<string> SafeActions = new(StringComparer.Ordinal)
        { "Search issues", "Read issue", "Create issue", "Update issue", "Add comment", "Assign issue", "Clone issue", "Transition issue", "Link issues", "Search pages", "Read page", "Create page", "Update page" };
    private static readonly HashSet<string> SafeOutcomes = new(StringComparer.Ordinal)
        { "Completed", "Failed", "Cancelled", "Unknown outcome", "Prepared", "Declined", "Rejected" };
    private static readonly TimeSpan ConversationRetention = TimeSpan.FromDays(30);
    private static readonly TimeSpan ActivityRetention = TimeSpan.FromDays(90);

    public async Task SaveAsync(AgentConversation conversation, CancellationToken cancellationToken = default)
    {
        ValidateConversation(conversation);
        var json = JsonSerializer.Serialize(conversation.Messages, JsonOptions);
        if (json.Length > 1_500_000) throw new IntegrationException("The conversation is too large to save locally. Start a new conversation to continue.");
        await using var connection = await OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO conversations (id, agent, title, created_utc, updated_utc, messages_json)
            VALUES ($id, $agent, $title, $created, $updated, $messages)
            ON CONFLICT(id) DO UPDATE SET title=$title, updated_utc=$updated, messages_json=$messages;
            DELETE FROM conversations WHERE updated_utc < $cutoff;
            """;
        command.Parameters.AddWithValue("$id", conversation.Id);
        command.Parameters.AddWithValue("$agent", conversation.Agent.ToString());
        command.Parameters.AddWithValue("$title", conversation.Title);
        command.Parameters.AddWithValue("$created", Format(conversation.CreatedAt));
        command.Parameters.AddWithValue("$updated", Format(conversation.UpdatedAt));
        command.Parameters.AddWithValue("$messages", json);
        command.Parameters.AddWithValue("$cutoff", Format(DateTimeOffset.UtcNow - ConversationRetention));
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AgentConversation>> ListConversationsAsync(IntegrationKind? agent = null, CancellationToken cancellationToken = default)
    {
        if (agent is not null && agent is not (IntegrationKind.Jira or IntegrationKind.Confluence)) throw new ArgumentException("Choose a Jira or Confluence history filter.");
        await using var connection = await OpenAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = agent is null
            ? "SELECT id, agent, title, created_utc, updated_utc, messages_json FROM conversations WHERE updated_utc >= $cutoff ORDER BY updated_utc DESC LIMIT 200"
            : "SELECT id, agent, title, created_utc, updated_utc, messages_json FROM conversations WHERE updated_utc >= $cutoff AND agent=$agent ORDER BY updated_utc DESC LIMIT 200";
        command.Parameters.AddWithValue("$cutoff", Format(DateTimeOffset.UtcNow - ConversationRetention));
        if (agent is not null) command.Parameters.AddWithValue("$agent", agent.Value.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var conversations = new List<AgentConversation>();
        while (await reader.ReadAsync(cancellationToken))
        {
            try
            {
                var kind = Enum.Parse<IntegrationKind>(reader.GetString(1), false);
                var messages = JsonSerializer.Deserialize<List<AgentConversationMessage>>(reader.GetString(5), JsonOptions) ?? throw new JsonException();
                var conversation = new AgentConversation(reader.GetString(0), kind, reader.GetString(2), Parse(reader.GetString(3)), Parse(reader.GetString(4)), messages);
                ValidateConversation(conversation);
                conversations.Add(conversation);
            }
            catch (Exception exception) when (exception is JsonException or ArgumentException or FormatException)
            {
                throw new IntegrationException("Conversation history is invalid and was not changed. Clear the damaged history or check the local workspace.");
            }
        }
        return conversations;
    }

    public async Task DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new ArgumentException("Invalid conversation identifier.");
        await using var connection = await OpenAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM conversations WHERE id=$id";
        command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task DeleteAllAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM conversations";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task RecordAsync(IntegrationKind agent, string action, string outcome, CancellationToken cancellationToken = default)
    {
        if (agent is not (IntegrationKind.Jira or IntegrationKind.Confluence) || !SafeActions.Contains(action) || !SafeOutcomes.Contains(outcome))
            throw new ArgumentException("Activity log entries must use a Jira or Confluence agent and an approved non-sensitive action/outcome label.");
        await using var connection = await OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO activity_log (created_utc, agent, action, outcome) VALUES ($created, $agent, $action, $outcome);
            DELETE FROM activity_log WHERE created_utc < $cutoff;
            DELETE FROM activity_log WHERE id NOT IN (SELECT id FROM activity_log ORDER BY id DESC LIMIT 5000);
            """;
        command.Parameters.AddWithValue("$created", Format(DateTimeOffset.UtcNow));
        command.Parameters.AddWithValue("$agent", agent.ToString());
        command.Parameters.AddWithValue("$action", action);
        command.Parameters.AddWithValue("$outcome", outcome);
        command.Parameters.AddWithValue("$cutoff", Format(DateTimeOffset.UtcNow - ActivityRetention));
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AgentActivity>> ListActivitiesAsync(IntegrationKind? agent = null, string? filter = null, CancellationToken cancellationToken = default)
    {
        if (agent is not null && agent is not (IntegrationKind.Jira or IntegrationKind.Confluence)) throw new ArgumentException("Choose a Jira or Confluence activity filter.");
        if (filter?.Length > 80) throw new ArgumentException("Activity filter must be 80 characters or fewer.");
        await using var connection = await OpenAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, created_utc, agent, action, outcome FROM activity_log
            WHERE created_utc >= $cutoff
              AND ($agent IS NULL OR agent=$agent)
              AND ($filter IS NULL OR instr(lower(action), lower($filter)) > 0 OR instr(lower(outcome), lower($filter)) > 0)
            ORDER BY id DESC LIMIT 1000
            """;
        command.Parameters.AddWithValue("$cutoff", Format(DateTimeOffset.UtcNow - ActivityRetention));
        command.Parameters.AddWithValue("$agent", agent is null ? DBNull.Value : agent.Value.ToString());
        command.Parameters.AddWithValue("$filter", string.IsNullOrWhiteSpace(filter) ? DBNull.Value : filter);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var events = new List<AgentActivity>();
        while (await reader.ReadAsync(cancellationToken))
        {
            if (!Enum.TryParse<IntegrationKind>(reader.GetString(2), false, out var kind) || kind is not (IntegrationKind.Jira or IntegrationKind.Confluence))
                throw new IntegrationException("Activity history contains an invalid agent entry.");
            events.Add(new(reader.GetInt64(0), Parse(reader.GetString(1)), kind, reader.GetString(3), reader.GetString(4)));
        }
        return events;
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM activity_log";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(connectionString);
        try { await connection.OpenAsync(cancellationToken); return connection; }
        catch { await connection.DisposeAsync(); throw; }
    }

    private static void ValidateConversation(AgentConversation conversation)
    {
        if (conversation is null || !Guid.TryParseExact(conversation.Id, "N", out _) ||
            conversation.Agent is not (IntegrationKind.Jira or IntegrationKind.Confluence) ||
            string.IsNullOrWhiteSpace(conversation.Title) || conversation.Title.Length > 120 || conversation.Messages.Count is < 1 or > 200)
            throw new ArgumentException("Conversation history has invalid identifiers, agent, title or message count.");
        var total = 0;
        foreach (var message in conversation.Messages)
        {
            if (message is null || string.IsNullOrWhiteSpace(message.Speaker) || message.Speaker.Length > 40 || message.Text is null || message.Text.Length > 16000)
                throw new ArgumentException("Conversation history contains an invalid message.");
            total += message.Text.Length;
            if (total > 1_000_000) throw new ArgumentException("Conversation history exceeds the supported size.");
        }
    }

    private static string Format(DateTimeOffset date) => date.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture);
    private static DateTimeOffset Parse(string value) => DateTimeOffset.Parse(value, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind);
}
