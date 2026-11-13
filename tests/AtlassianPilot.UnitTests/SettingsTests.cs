using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using AtlassianPilot.Core;
using AtlassianPilot.Infrastructure;
using Microsoft.Data.Sqlite;

namespace AtlassianPilot.UnitTests;

[SupportedOSPlatform("windows")]
public sealed class SettingsTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "AtlassianPilot.Tests", Guid.NewGuid().ToString("N"));
    private readonly DpapiSecretProtector protector = new();
    private string DatabasePath => Path.Combine(directory, "settings.db");
    private SqliteSettingsStore Store => new(DatabasePath, protector);
    private static IntegrationSettings Jira => new(IntegrationKind.Jira, "https://example.atlassian.net", "person@example.com", "ABC");

    [Fact]
    public async Task FreshDatabaseHasNoCredentialsAndDarkTheme()
    {
        await Store.InitializeAsync();
        Assert.False((await Store.LoadAsync(IntegrationKind.Jira)).HasCredential);
        Assert.Null(await Store.GetAsync(IntegrationKind.Jira));
        Assert.Equal("Dark", await Store.GetThemeAsync());
    }

    [Fact]
    public async Task SettingsAndEncryptedCredentialSurviveReinitialization()
    {
        await Store.InitializeAsync();
        const string token = "test-only-token-not-a-real-secret";
        await Store.SaveAsync(Jira, token);
        await Store.SetThemeAsync("Light");
        await Store.InitializeAsync();
        var loaded = await Store.LoadAsync(IntegrationKind.Jira);
        Assert.Equal(Jira with { HasCredential = true }, loaded);
        Assert.Equal(token, await Store.GetAsync(IntegrationKind.Jira));
        Assert.Equal("Light", await Store.GetThemeAsync());
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DatabasePath, Pooling = false }.ToString());
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT credential FROM integrations WHERE kind='Jira'";
        var encrypted = Assert.IsType<byte[]>(await command.ExecuteScalarAsync());
        Assert.NotEqual(Encoding.UTF8.GetBytes(token), encrypted);
        Assert.Equal(token, protector.Unprotect(encrypted));
        await connection.CloseAsync();
        foreach (var path in Directory.GetFiles(directory))
            Assert.DoesNotContain(token, Encoding.UTF8.GetString(await File.ReadAllBytesAsync(path)));
    }

    [Fact]
    public async Task BlankUiInputPreservesCredentialAndExplicitDeleteRemovesIt()
    {
        await Store.InitializeAsync();
        await Store.SaveAsync(Jira, "original");
        await Store.SaveAsync(Jira with { DefaultScope = "XYZ" });
        Assert.Equal("original", await Store.GetAsync(IntegrationKind.Jira));
        await Store.SaveAsync(Jira, "replacement");
        Assert.Equal("replacement", await Store.GetAsync(IntegrationKind.Jira));
        await Store.SaveAsync(Jira, deleteCredential: true);
        Assert.False((await Store.LoadAsync(IntegrationKind.Jira)).HasCredential);
        Assert.Null(await Store.GetAsync(IntegrationKind.Jira));
    }

    [Fact]
    public async Task JiraAndConfluenceCredentialsRemainIndependent()
    {
        await Store.InitializeAsync();
        await Store.SaveAsync(Jira, "jira-only");
        await Store.SaveAsync(Jira with { Kind = IntegrationKind.Confluence, BaseUrl = "https://example.atlassian.net/wiki" }, "confluence-only");
        await Store.SaveAsync(Jira, deleteCredential: true);
        Assert.Equal("confluence-only", await Store.GetAsync(IntegrationKind.Confluence));
    }

    [Theory]
    [InlineData("http://example.atlassian.net")]
    [InlineData("https://user:secret@example.atlassian.net")]
    [InlineData("https://example.atlassian.net?token=secret")]
    [InlineData("https://example.atlassian.net#fragment")]
    [InlineData("not a url")]
    public async Task InvalidEndpointCannotReplaceExistingConfiguration(string url)
    {
        await Store.InitializeAsync();
        await Store.SaveAsync(Jira, "preserved");
        await Assert.ThrowsAsync<ArgumentException>(() => Store.SaveAsync(Jira with { BaseUrl = url }, "not-saved"));
        Assert.Equal("preserved", await Store.GetAsync(IntegrationKind.Jira));
        Assert.Equal(Jira.BaseUrl, (await Store.LoadAsync(IntegrationKind.Jira)).BaseUrl);
    }

    [Fact]
    public async Task InvalidCredentialOperationsAreRejected()
    {
        await Store.InitializeAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => Store.SaveAsync(Jira, " "));
        await Assert.ThrowsAsync<ArgumentException>(() => Store.SaveAsync(Jira, "value", deleteCredential: true));
        await Assert.ThrowsAsync<ArgumentException>(() => Store.SaveAsync(new(IntegrationKind.Copilot), "github-token"));
        Assert.Null(await Store.GetAsync(IntegrationKind.Jira));
    }

    [Fact]
    public async Task CopilotModelPreferenceRequiresNoToken()
    {
        await Store.InitializeAsync();
        await Store.SaveAsync(new(IntegrationKind.Copilot, Model: "selected-model"));
        Assert.Equal("selected-model", (await Store.LoadAsync(IntegrationKind.Copilot)).Model);
        Assert.Null(await Store.GetAsync(IntegrationKind.Copilot));
    }

    [Fact]
    public async Task InvalidThemeAndEmailAreRejected()
    {
        await Store.InitializeAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => Store.SetThemeAsync("unknown"));
        await Assert.ThrowsAsync<ArgumentException>(() => Store.SaveAsync(Jira with { Email = "not-an-email" }));
    }

    [Fact]
    public void CorruptedCiphertextFailsExplicitly()
    {
        var encrypted = protector.Protect("test");
        encrypted[encrypted.Length / 2] ^= 0xff;
        Assert.Throws<CryptographicException>(() => protector.Unprotect(encrypted));
    }

    [Fact]
    public async Task SavedSearchesPersistWithoutChangingOtherSettings()
    {
        await Store.InitializeAsync();
        await Store.SaveAsync(Jira, "test-token");
        await Store.SetThemeAsync("Light");
        var searches = new SqliteSavedJiraSearchStore(DatabasePath);
        var saved = new SavedJiraSearch(Guid.NewGuid().ToString("N"), "My search", "Find my open issues");
        await searches.SaveAsync([saved]);
        await Store.InitializeAsync();
        Assert.Equal(saved, Assert.Single(await new SqliteSavedJiraSearchStore(DatabasePath).LoadAsync()));
        Assert.Equal("Light", await Store.GetThemeAsync());
        Assert.Equal("test-token", await Store.GetAsync(IntegrationKind.Jira));
        Assert.Equal(Jira.BaseUrl, (await Store.LoadAsync(IntegrationKind.Jira)).BaseUrl);
    }

    [Fact]
    public async Task InvalidSavedSearchesCannotReplaceValidData()
    {
        await Store.InitializeAsync();
        var store = new SqliteSavedJiraSearchStore(DatabasePath);
        var saved = new SavedJiraSearch(Guid.NewGuid().ToString("N"), "Valid", "Find issues");
        await store.SaveAsync([saved]);
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync([saved, new(Guid.NewGuid().ToString("N"), "valid", "Another")]));
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync([saved with { Name = " " }]));
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync([saved with { Request = new string('x', 4001) }]));
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(Enumerable.Range(0, 201).Select(i => new SavedJiraSearch(Guid.NewGuid().ToString("N"), $"Name {i}", "Request")).ToList()));
        Assert.Equal(saved, Assert.Single(await store.LoadAsync()));
    }

    [Fact]
    public async Task MalformedSavedSearchDataFailsWithoutBeingOverwritten()
    {
        await Store.InitializeAsync();
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DatabasePath, Pooling = false }.ToString());
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO preferences (key, value) VALUES ('jira_saved_searches', 'not json')";
        await command.ExecuteNonQueryAsync();
        var error = await Assert.ThrowsAsync<IntegrationException>(() => new SqliteSavedJiraSearchStore(DatabasePath).LoadAsync());
        Assert.Contains("have not been changed", error.Message);
        command.CommandText = "SELECT value FROM preferences WHERE key='jira_saved_searches'";
        Assert.Equal("not json", await command.ExecuteScalarAsync());
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}
