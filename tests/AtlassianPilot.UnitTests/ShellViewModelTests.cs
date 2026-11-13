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
        var viewModel = new ShellViewModel(Store);
        await viewModel.InitializeAsync();
        Assert.Equal("Not configured", viewModel.JiraState);
        Assert.Equal("Not configured", viewModel.ConfluenceState);
        Assert.True(viewModel.IsDashboard);
        Assert.False(viewModel.HasCredential);
        Assert.False(viewModel.HasUnsavedChanges);
    }

    [Fact]
    public async Task SavingClearsTokenInputAndNeverReportsConnected()
    {
        var viewModel = new ShellViewModel(Store);
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
        var viewModel = new ShellViewModel(Store);
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
        var viewModel = new ShellViewModel(Store);
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
        var viewModel = new ShellViewModel(Store);
        await viewModel.InitializeAsync();
        viewModel.CredentialEdited();
        Assert.True(viewModel.HasUnsavedChanges);
    }

    [Fact]
    public async Task HelpShowsPersonalUseAttributionAndReturnsToDashboard()
    {
        var viewModel = new ShellViewModel(Store);
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

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}
