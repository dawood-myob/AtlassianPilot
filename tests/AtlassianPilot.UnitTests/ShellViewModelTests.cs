using System.Runtime.Versioning;
using AtlassianPilot.Core;
using AtlassianPilot.Desktop.ViewModels;
using AtlassianPilot.Infrastructure;

namespace AtlassianPilot.UnitTests;

[SupportedOSPlatform("windows")]
public sealed class ShellViewModelTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "AtlassianPilot.Tests", Guid.NewGuid().ToString("N"));
    private SqliteSettingsStore Store => new(Path.Combine(directory, "settings.db"), new DpapiSecretProtector());

    [Fact]
    public async Task StartupDoesNotInventConnectionStatus()
    {
        var viewModel = CreateViewModel();
        await viewModel.InitializeAsync();
        Assert.Equal("Not configured", viewModel.JiraState);
        Assert.Equal("Not configured", viewModel.ConfluenceState);
        Assert.Equal("GitHub Copilot / Not checked", viewModel.CopilotHeader);
        Assert.True(viewModel.IsDashboard);
        Assert.False(viewModel.HasCredential);
        Assert.False(viewModel.HasUnsavedChanges);
    }

    [Fact]
    public void EmptyModelListExplainsHowToLoadModelsAndUpdatesWhenTheyArrive()
    {
        var viewModel = CreateViewModel();
        Assert.False(viewModel.HasAvailableModels);
        Assert.Contains("Sign in with GitHub or test the connection", viewModel.AvailableModelsStatus);
        var changed = new List<string?>();
        viewModel.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        viewModel.AvailableModels.Add(new CopilotModel("test-model", "Test model"));

        Assert.True(viewModel.HasAvailableModels);
        Assert.Contains("1 model(s) available", viewModel.AvailableModelsStatus);
        Assert.Contains(nameof(viewModel.HasAvailableModels), changed);
        Assert.Contains(nameof(viewModel.AvailableModelsStatus), changed);
    }

    [Fact]
    public async Task SavingClearsTokenInputAndNeverReportsConnected()
    {
        var viewModel = CreateViewModel();
        await viewModel.InitializeAsync();
        var cleared = false;
        viewModel.ClearCredentialInput += () => cleared = true;
        viewModel.BaseUrl = "https://example.atlassian.net";
        viewModel.Email = "person@example.com";
        viewModel.DefaultScope = "ABC";
        await viewModel.SaveAsync("test-only-token");
        Assert.True(cleared);
        Assert.True(viewModel.HasCredential);
        Assert.Equal("Configured / untested", viewModel.JiraState);
        Assert.DoesNotContain("test-only-token", viewModel.Notification);
        Assert.False(viewModel.Busy);
        Assert.False(viewModel.HasUnsavedChanges);
    }

    [Fact]
    public async Task InvalidSettingsRemainEditableAndDoNotShowSuccess()
    {
        var viewModel = CreateViewModel();
        await viewModel.InitializeAsync();
        viewModel.BaseUrl = "http://invalid";
        var exception = await Assert.ThrowsAsync<ArgumentException>(() => viewModel.SaveAsync("not-saved"));
        viewModel.ReportError(exception);
        Assert.False(viewModel.Busy);
        Assert.True(viewModel.HasUnsavedChanges);
        Assert.False(viewModel.HasCredential);
        Assert.Contains("HTTPS", viewModel.Notification);
    }

    [Fact]
    public async Task UnsavedSettingsBlockNavigation()
    {
        var viewModel = CreateViewModel();
        await viewModel.InitializeAsync();
        viewModel.NavigateCommand.Execute("Integrations & Settings");
        viewModel.Email = "person@example.com";
        viewModel.NavigateCommand.Execute("Jira Agent");
        Assert.True(viewModel.IsSettings);
        Assert.Contains("Save", viewModel.Notification);
    }

    [Fact]
    public async Task CredentialInputAloneIsAnUnsavedChange()
    {
        var viewModel = CreateViewModel();
        await viewModel.InitializeAsync();
        viewModel.CredentialEdited();
        Assert.True(viewModel.HasUnsavedChanges);
    }

    [Fact]
    public async Task HelpShowsPersonalUseAttributionAndReturnsToDashboard()
    {
        var viewModel = CreateViewModel();
        await viewModel.InitializeAsync();
        viewModel.NavigateCommand.Execute("Help & About");
        Assert.True(viewModel.IsHelp);
        Assert.False(viewModel.IsDashboard);
        Assert.Equal("Developed by Dawood Ali", viewModel.DeveloperAttribution);
        Assert.Contains("2026 Dawood Ali. All rights reserved.", viewModel.CopyrightNotice);
        Assert.Contains("personal use only", viewModel.UsageNotice);
        viewModel.NavigateCommand.Execute("Dashboard");
        Assert.False(viewModel.IsHelp);
        Assert.True(viewModel.IsDashboard);
    }

    [Fact]
    public async Task SelectingConfluenceLoadsItsOwnSavedFieldsWithoutAnotherClick()
    {
        await Store.InitializeAsync();
        await Store.SaveAsync(new(IntegrationKind.Confluence, "https://knowledge.atlassian.net/wiki", "person@example.com", "SPACE"), "test-confluence-token");
        var viewModel = CreateViewModel();
        await viewModel.InitializeAsync();
        await viewModel.ChangeIntegrationAsync(IntegrationKind.Confluence);
        Assert.Equal(IntegrationKind.Confluence, viewModel.SelectedIntegration);
        Assert.Equal(viewModel.SelectedIntegration, viewModel.RequestedIntegration);
        Assert.Equal("https://knowledge.atlassian.net/wiki", viewModel.BaseUrl);
        Assert.Equal("SPACE", viewModel.DefaultScope);
        Assert.Equal("Default space key", viewModel.ScopeLabel);
        Assert.True(viewModel.HasCredential);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SwitchingUnsavedFormRequiresConfirmation(bool confirm)
    {
        var viewModel = CreateViewModel();
        await viewModel.InitializeAsync();
        viewModel.Email = "unsaved@example.com";
        viewModel.ConfirmDiscardChanges += () => confirm;
        await viewModel.ChangeIntegrationAsync(IntegrationKind.Confluence);
        Assert.Equal(confirm ? IntegrationKind.Confluence : IntegrationKind.Jira, viewModel.SelectedIntegration);
        Assert.Equal(confirm ? "" : "unsaved@example.com", viewModel.Email);
        Assert.Equal(!confirm, viewModel.HasUnsavedChanges);
    }

    [Fact]
    public async Task ConnectionTestDoesNotSendUnsavedSettings()
    {
        var fake = new FakeAtlassianConnection();
        var viewModel = new ShellViewModel(Store, fake, new FakeCopilotConnection());
        await viewModel.InitializeAsync();
        viewModel.CredentialEdited();
        viewModel.TestConnectionCommand.Execute(null);
        Assert.Equal(0, fake.Calls);
        Assert.Contains("Save", viewModel.Notification);
    }

    [Fact]
    public async Task JiraRequestUpdatesCopilotStatusWithoutInventingFailureCause()
    {
        var agent = new StatusAgent();
        var jira = new JiraAgentViewModel(agent, new UnusedSearch(), new EmptySavedSearchStore());
        var viewModel = new ShellViewModel(Store, new FakeAtlassianConnection(), new FakeCopilotConnection(), jira);
        await viewModel.InitializeAsync();
        var headerChanges = new List<string>();
        viewModel.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(viewModel.CopilotHeader)) headerChanges.Add(viewModel.CopilotHeader); };
        jira.Prompt = "Find my issues";
        await jira.SendAsync();
        Assert.Equal("GitHub Copilot / Connected (last request)", viewModel.CopilotHeader);
        Assert.Contains("GitHub Copilot / Checking...", headerChanges);
        Assert.Contains(viewModel.CopilotHeader, headerChanges);
        agent.Error = new IntegrationException("Jira search failed.");
        jira.Prompt = "Find more issues";
        await jira.SendAsync();
        Assert.Equal("GitHub Copilot / Status unknown", viewModel.CopilotHeader);
        Assert.Equal("Jira search failed.", jira.Status);
        await viewModel.ChangeIntegrationAsync(IntegrationKind.Copilot);
        viewModel.TestConnectionCommand.Execute(null);
        Assert.Equal("GitHub Copilot / Not signed in", viewModel.CopilotHeader);
        viewModel.SignOutCommand.Execute(null);
        Assert.Equal("GitHub Copilot / Not signed in", viewModel.CopilotHeader);
    }

    private sealed class StatusAgent : IJiraAgent
    {
        public Exception? Error { get; set; }
        public Task<JiraAgentReply> SendAsync(string prompt, IProgress<JiraAgentProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            if (Error is not null) throw Error;
            return Task.FromResult(new JiraAgentReply("Response", null, null));
        }
        public Task NewConversationAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class UnusedSearch : IJiraIssueSearchService
    {
        public Task<JiraIssueSearchResult> SearchAsync(string jql, int maxResults = 50, string? nextPageToken = null, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Unexpected search");
    }

    private sealed class EmptySavedSearchStore : ISavedJiraSearchStore
    {
        public Task<IReadOnlyList<SavedJiraSearch>> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<SavedJiraSearch>>([]);
        public Task SaveAsync(IReadOnlyList<SavedJiraSearch> searches, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Unexpected save");
    }

    private ShellViewModel CreateViewModel() => new(Store, new FakeAtlassianConnection(), new FakeCopilotConnection());

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}
