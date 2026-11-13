using System.Text.Json;
using AtlassianPilot.Core;
using Microsoft.Data.Sqlite;

namespace AtlassianPilot.Infrastructure;

public sealed class SqliteSavedJiraSearchStore(string databasePath) : ISavedJiraSearchStore
{
    private readonly string connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString();
    private const string PreferenceKey = "jira_saved_searches";

    public async Task<IReadOnlyList<SavedJiraSearch>> LoadAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM preferences WHERE key=$key";
        command.Parameters.AddWithValue("$key", PreferenceKey);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        if (value is null) return [];
        try
        {
            var searches = JsonSerializer.Deserialize<List<SavedJiraSearch>>((string)value)
                ?? throw new JsonException("Saved searches are null.");
            Validate(searches);
            return searches;
        }
        catch (JsonException)
        {
            throw new IntegrationException("Saved Jira searches could not be read. They have not been changed; check the local workspace.");
        }
        catch (ArgumentException)
        {
            throw new IntegrationException("Saved Jira searches are invalid. They have not been changed; check the local workspace.");
        }
    }

    public async Task SaveAsync(IReadOnlyList<SavedJiraSearch> searches, CancellationToken cancellationToken = default)
    {
        Validate(searches);
        var json = JsonSerializer.Serialize(searches);
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO preferences (key, value) VALUES ($key, $value) ON CONFLICT(key) DO UPDATE SET value=$value";
        command.Parameters.AddWithValue("$key", PreferenceKey);
        command.Parameters.AddWithValue("$value", json);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void Validate(IReadOnlyList<SavedJiraSearch> searches)
    {
        if (searches.Count > 200) throw new ArgumentException("You can save up to 200 Jira searches.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var search in searches)
        {
            if (search is null || !Guid.TryParseExact(search.Id, "N", out _) || !ids.Add(search.Id) ||
                string.IsNullOrWhiteSpace(search.Name) || search.Name != search.Name.Trim() || search.Name.Length > 80 ||
                !names.Add(search.Name) || string.IsNullOrWhiteSpace(search.Request) || search.Request.Length > 4000)
                throw new ArgumentException("Each saved search needs a unique name (up to 80 characters) and a request (up to 4000 characters).");
        }
    }
}
