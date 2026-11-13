using AtlassianPilot.Core;
using Microsoft.Data.Sqlite;

namespace AtlassianPilot.Infrastructure;

public sealed class SqliteSettingsStore(string databasePath, ISecretProtector protector) : ISettingsStore, ICredentialStore
{
    private readonly string connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = databasePath,
        Pooling = false
    }.ToString();

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);
        await using var connection = await OpenAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version";
        var version = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
        if (version > 1) throw new InvalidOperationException("This workspace requires a newer version of AtlassianPilot.");
        command.CommandText = "PRAGMA journal_mode=WAL";
        await command.ExecuteNonQueryAsync(cancellationToken);
        if (version == 1) return;
        using var transaction = connection.BeginTransaction();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS integrations (
                kind TEXT PRIMARY KEY, base_url TEXT NOT NULL, email TEXT NOT NULL,
                default_scope TEXT NOT NULL, model TEXT NOT NULL, credential BLOB);
            CREATE TABLE IF NOT EXISTS preferences (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            PRAGMA user_version=1;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IntegrationSettings> LoadAsync(IntegrationKind kind, CancellationToken cancellationToken = default)
    {
        EnsureKind(kind);
        await using var connection = await OpenAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT base_url, email, default_scope, model, credential IS NOT NULL FROM integrations WHERE kind=$kind";
        command.Parameters.AddWithValue("$kind", kind.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new(kind, reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetBoolean(4))
            : new(kind);
    }

    public async Task SaveAsync(IntegrationSettings settings, string? credential = null, bool deleteCredential = false,
        CancellationToken cancellationToken = default)
    {
        SettingsValidator.Validate(settings);
        if (credential is not null && (string.IsNullOrWhiteSpace(credential) || credential.Length > 8192))
            throw new ArgumentException("Credential must be non-empty and no more than 8192 characters.");
        if (deleteCredential && credential is not null)
            throw new ArgumentException("Cannot replace and delete a credential in the same operation.");
        if (settings.Kind == IntegrationKind.Copilot && credential is not null)
            throw new ArgumentException("Use GitHub sign-in for Copilot; do not store a token here.");
        var encrypted = credential is null ? null : protector.Protect(credential);
        await using var connection = await OpenAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO integrations (kind, base_url, email, default_scope, model, credential)
            VALUES ($kind, $url, $email, $scope, $model, $credential)
            ON CONFLICT(kind) DO UPDATE SET base_url=$url, email=$email, default_scope=$scope, model=$model,
                credential=CASE WHEN $delete THEN NULL WHEN $replace THEN $credential ELSE integrations.credential END;
            """;
        command.Parameters.AddWithValue("$kind", settings.Kind.ToString());
        command.Parameters.AddWithValue("$url", settings.BaseUrl.TrimEnd('/'));
        command.Parameters.AddWithValue("$email", settings.Email);
        command.Parameters.AddWithValue("$scope", settings.DefaultScope);
        command.Parameters.AddWithValue("$model", settings.Model);
        command.Parameters.Add("$credential", SqliteType.Blob).Value = encrypted is null ? DBNull.Value : encrypted;
        command.Parameters.AddWithValue("$delete", deleteCredential);
        command.Parameters.AddWithValue("$replace", credential is not null);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<string?> GetAsync(IntegrationKind kind, CancellationToken cancellationToken = default)
    {
        EnsureKind(kind);
        await using var connection = await OpenAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT credential FROM integrations WHERE kind=$kind";
        command.Parameters.AddWithValue("$kind", kind.ToString());
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is byte[] bytes ? protector.Unprotect(bytes) : null;
    }

    public async Task<string> GetThemeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM preferences WHERE key='theme'";
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is string theme ? theme : "Dark";
    }

    public async Task SetThemeAsync(string theme, CancellationToken cancellationToken = default)
    {
        if (theme is not ("Dark" or "Light"))
            throw new ArgumentException("Theme must be Dark or Light.");
        await using var connection = await OpenAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO preferences VALUES ('theme', $theme) ON CONFLICT(key) DO UPDATE SET value=$theme";
        command.Parameters.AddWithValue("$theme", theme);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private static void EnsureKind(IntegrationKind kind)
    {
        if (!Enum.IsDefined(kind)) throw new ArgumentException("Unknown integration.");
    }
}
