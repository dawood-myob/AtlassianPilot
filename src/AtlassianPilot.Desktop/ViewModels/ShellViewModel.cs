using System.Collections.ObjectModel;
using System.Windows.Input;
using AtlassianPilot.Core;

namespace AtlassianPilot.Desktop.ViewModels;

public sealed class ShellViewModel : ObservableObject
{
    private readonly ISettingsStore settings;
    private string selectedSection = "Dashboard";
    private string theme = "Dark";
    private string notification = "Welcome. Configure your integrations to get started.";
    private bool busy;
    private bool loading;
    private bool dirty;
    private string jiraState = "Not configured";
    private string confluenceState = "Not configured";
    private string copilotModel = "Default model";
    private IntegrationKind selectedIntegration;
    private string baseUrl = "";
    private string email = "";
    private string defaultScope = "";
    private string model = "";
    private bool hasCredential;
    private IntegrationKind requestedIntegration;

    public ShellViewModel(ISettingsStore settings)
    {
        this.settings = settings;
        NavigateCommand = new RelayCommand(value => Navigate(value as string ?? "Dashboard"));
        SaveCommand = new AsyncCommand(() => SaveAsync(null), ReportError);
        DeleteCredentialCommand = new AsyncCommand(DeleteCredentialAsync, ReportError);
        ToggleThemeCommand = new AsyncCommand(ToggleThemeAsync, ReportError);
        SelectIntegrationCommand = new AsyncCommand(SwitchIntegrationAsync, ReportError);
    }

    public ObservableCollection<string> Sections { get; } =
        ["Dashboard", "Jira Agent", "Confluence Agent", "Agent History", "Integrations & Settings", "AI Configuration", "Activity Logs"];
    public ICommand NavigateCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand DeleteCredentialCommand { get; }
    public ICommand ToggleThemeCommand { get; }
    public ICommand SelectIntegrationCommand { get; }
    public event Action<string>? ThemeChanged;
    public event Action? ClearCredentialInput;
    public string DeveloperAttribution => "Developed by Dawood Ali";
    public string CopyrightNotice => "Copyright (c) 2026 Dawood Ali. All rights reserved.";
    public string UsageNotice => "Developed for personal use only. Third-party components remain subject to their respective licenses. Not affiliated with or endorsed by Atlassian or GitHub.";
    public bool IsHelp => SelectedSection == "Help & About";
    public string SelectedSection { get => selectedSection; private set { Set(ref selectedSection, value); Changed(nameof(IsDashboard)); Changed(nameof(IsSettings)); Changed(nameof(IsAgent)); Changed(nameof(IsHistory)); Changed(nameof(IsLogs)); Changed(nameof(IsHelp)); Changed(nameof(AgentDescription)); } }
    public bool IsDashboard => SelectedSection == "Dashboard";
    public bool IsSettings => SelectedSection is "Integrations & Settings" or "AI Configuration";
    public bool IsAgent => SelectedSection is "Jira Agent" or "Confluence Agent";
    public bool IsHistory => SelectedSection == "Agent History";
    public bool IsLogs => SelectedSection == "Activity Logs";
    public string AgentDescription => SelectedSection == "Jira Agent"
        ? "Your future Jira workspace for searching issues and reviewing changes before execution."
        : "Your future Confluence workspace for finding, improving and publishing pages with your approval.";
    public string Notification { get => notification; private set => Set(ref notification, value); }
    public bool Busy { get => busy; private set { Set(ref busy, value); Changed(nameof(CanEdit)); } }
    public bool CanEdit => !Busy;
    public string JiraState { get => jiraState; private set => Set(ref jiraState, value); }
    public string ConfluenceState { get => confluenceState; private set => Set(ref confluenceState, value); }
    public string CopilotModel { get => copilotModel; private set => Set(ref copilotModel, value); }
    public IntegrationKind[] IntegrationKinds { get; } = Enum.GetValues<IntegrationKind>();
    public IntegrationKind SelectedIntegration { get => selectedIntegration; set { if (selectedIntegration == value) return; Set(ref selectedIntegration, value); Changed(nameof(IsAtlassian)); Changed(nameof(IsCopilot)); Changed(nameof(ScopeLabel)); } }
    public bool IsAtlassian => SelectedIntegration != IntegrationKind.Copilot;
    public bool IsCopilot => !IsAtlassian;
    public string ScopeLabel => SelectedIntegration == IntegrationKind.Jira ? "Default project key" : "Default space key";
    public string BaseUrl { get => baseUrl; set { Set(ref baseUrl, value); MarkDirty(); } }
    public string Email { get => email; set { Set(ref email, value); MarkDirty(); } }
    public string DefaultScope { get => defaultScope; set { Set(ref defaultScope, value); MarkDirty(); } }
    public string Model { get => model; set { Set(ref model, value); MarkDirty(); } }
    public bool HasCredential { get => hasCredential; private set { Set(ref hasCredential, value); Changed(nameof(CredentialStatus)); } }
    public string CredentialStatus => HasCredential ? "A token is securely stored. Leave blank to keep it." : "No API token stored.";
    public IntegrationKind RequestedIntegration { get => requestedIntegration; set => Set(ref requestedIntegration, value); }
    public bool HasUnsavedChanges => dirty;

    public async Task InitializeAsync()
    {
        await settings.InitializeAsync();
        theme = await settings.GetThemeAsync();
        ThemeChanged?.Invoke(theme);
        await LoadIntegrationAsync(IntegrationKind.Jira);
        await RefreshStatusAsync();
    }

    public async Task SaveAsync(string? credential)
    {
        if (Busy) return;
        Busy = true;
        try
        {
            var value = new IntegrationSettings(SelectedIntegration, BaseUrl.Trim(), Email.Trim(), DefaultScope.Trim(), Model.Trim());
            await settings.SaveAsync(value, credential);
            dirty = false;
            HasCredential = (await settings.LoadAsync(SelectedIntegration)).HasCredential;
            ClearCredentialInput?.Invoke();
            await RefreshStatusAsync();
            Notification = "Settings saved locally. Connection has not yet been tested.";
        }
        finally { Busy = false; }
    }

    public void CredentialEdited() => MarkDirty();

    public void ReportError(Exception exception)
    {
        Notification = exception is ArgumentException
            ? exception.Message
            : "The operation failed. Check access to the local data folder. Credentials may need to be re-entered if your Windows profile changed.";
    }

    private void Navigate(string section)
    {
        if (Busy) return;
        if (dirty && IsSettings && section != SelectedSection)
        {
            Notification = "Save your changes before leaving settings, or reload this integration to discard them.";
            return;
        }
        SelectedSection = section;
        if (section == "AI Configuration")
        {
            RequestedIntegration = IntegrationKind.Copilot;
            SelectIntegrationCommand.Execute(null);
        }
    }

    private async Task SwitchIntegrationAsync()
    {
        if (Busy) return;
        Busy = true;
        try
        {
            await LoadIntegrationAsync(RequestedIntegration);
            Notification = "Settings loaded. Unsaved edits were discarded.";
        }
        finally { Busy = false; }
    }

    private async Task LoadIntegrationAsync(IntegrationKind kind)
    {
        var value = await settings.LoadAsync(kind);
        loading = true;
        try
        {
            SelectedIntegration = kind;
            BaseUrl = value.BaseUrl;
            Email = value.Email;
            DefaultScope = value.DefaultScope;
            Model = value.Model;
            HasCredential = value.HasCredential;
            ClearCredentialInput?.Invoke();
            dirty = false;
        }
        finally { loading = false; }
    }

    private async Task DeleteCredentialAsync()
    {
        if (Busy) return;
        Busy = true;
        try
        {
            var saved = await settings.LoadAsync(SelectedIntegration);
            await settings.SaveAsync(saved, deleteCredential: true);
            HasCredential = false;
            ClearCredentialInput?.Invoke();
            await RefreshStatusAsync();
            Notification = "Stored token deleted. Other unsaved settings have not been saved.";
        }
        finally { Busy = false; }
    }

    private async Task ToggleThemeAsync()
    {
        var next = theme == "Dark" ? "Light" : "Dark";
        await settings.SetThemeAsync(next);
        theme = next;
        ThemeChanged?.Invoke(theme);
    }

    private async Task RefreshStatusAsync()
    {
        JiraState = (await settings.LoadAsync(IntegrationKind.Jira)).HasCredential ? "Configured / untested" : "Not configured";
        ConfluenceState = (await settings.LoadAsync(IntegrationKind.Confluence)).HasCredential ? "Configured / untested" : "Not configured";
        var ai = await settings.LoadAsync(IntegrationKind.Copilot);
        CopilotModel = string.IsNullOrWhiteSpace(ai.Model) ? "Default model" : ai.Model;
    }

    private void MarkDirty() { if (!loading) dirty = true; }
}
