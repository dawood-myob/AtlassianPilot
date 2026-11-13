using System.Collections.ObjectModel;
using System.Windows.Input;
using AtlassianPilot.Core;

namespace AtlassianPilot.Desktop.ViewModels;

public sealed class ShellViewModel : ObservableObject
{
    private readonly ISettingsStore settings;
    private readonly IAtlassianConnectionTester atlassian;
    private readonly ICopilotConnection copilot;
    private readonly Dictionary<IntegrationKind, ConnectionResult> connectionResults = [];
    private CancellationTokenSource? operation;
    private string copilotState = "Not checked";
    private string connectionDetails = "Connection has not been tested in this session.";
    private DeviceLoginPrompt? loginPrompt;
    private string selectedSection = "Dashboard";
    private string theme = "Dark";
    private string notification = "Configure integrations, then test your saved connections.";
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

    public ShellViewModel(ISettingsStore settings, IAtlassianConnectionTester atlassian, ICopilotConnection copilot, JiraAgentViewModel? jiraAgent = null)
    {
        this.settings = settings;
        this.atlassian = atlassian;
        this.copilot = copilot;
        JiraAgent = jiraAgent;
        if (JiraAgent is not null)
        {
            JiraAgent.BusyChanged += () => { Changed(nameof(CanEdit)); Changed(nameof(AnyBusy)); };
            JiraAgent.CopilotRequestStarted += () => CopilotState = "Checking...";
            JiraAgent.CopilotRequestFinished += success => CopilotState = success ? "Connected (last request)" : "Status unknown";
        }
        NavigateCommand = new RelayCommand(value => Navigate(value as string ?? "Dashboard"));
        SaveCommand = new AsyncCommand(() => SaveAsync(null), ReportError);
        DeleteCredentialCommand = new AsyncCommand(DeleteCredentialAsync, ReportError);
        ToggleThemeCommand = new AsyncCommand(ToggleThemeAsync, ReportError);
        SelectIntegrationCommand = new AsyncCommand(ReloadAsync, ReportError);
        TestConnectionCommand = new AsyncCommand(() => RunConnectionAsync(false), ReportError);
        SignInCommand = new AsyncCommand(() => RunConnectionAsync(true), ReportError);
        SignOutCommand = new AsyncCommand(SignOutAsync, ReportError);
        CancelCommand = new RelayCommand(_ => operation?.Cancel());
        UseModelCommand = new RelayCommand(value => { if (!Busy && value is CopilotModel choice) Model = choice.Id; });
    }

    public ObservableCollection<string> Sections { get; } =
        ["Dashboard", "Jira Agent", "Confluence Agent", "Agent History", "Integrations & Settings", "AI Configuration", "Activity Logs"];
    public ICommand NavigateCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand DeleteCredentialCommand { get; }
    public ICommand ToggleThemeCommand { get; }
    public ICommand SelectIntegrationCommand { get; }
    public ICommand TestConnectionCommand { get; }
    public ICommand SignInCommand { get; }
    public ICommand SignOutCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand UseModelCommand { get; }
    public ObservableCollection<CopilotModel> AvailableModels { get; } = [];
    public JiraAgentViewModel? JiraAgent { get; }
    public event Action<string>? ThemeChanged;
    public event Action? ClearCredentialInput;
    public event Func<bool>? ConfirmDiscardChanges;
    public string CopilotState { get => copilotState; private set { Set(ref copilotState, value); Changed(nameof(CopilotHeader)); } }
    public string CopilotHeader => $"GitHub Copilot / {CopilotState}";
    public string ConnectionDetails { get => connectionDetails; private set => Set(ref connectionDetails, value); }
    public string DeviceCode => loginPrompt?.UserCode ?? "";
    public string VerificationUrl => loginPrompt?.VerificationUri.AbsoluteUri ?? "";
    public bool HasDeviceCode => loginPrompt is not null;
    public bool CanCancel => operation is not null;
    public string DeveloperAttribution => "Developed by Dawood Ali";
    public string CopyrightNotice => "Copyright (c) 2026 Dawood Ali. All rights reserved.";
    public string UsageNotice => "Developed for personal use only. Third-party components remain subject to their respective licenses. Not affiliated with or endorsed by Atlassian or GitHub.";
    public bool IsHelp => SelectedSection == "Help & About";
    public string SelectedSection { get => selectedSection; private set { Set(ref selectedSection, value); Changed(nameof(IsDashboard)); Changed(nameof(IsSettings)); Changed(nameof(IsAgent)); Changed(nameof(IsJiraAgent)); Changed(nameof(IsHistory)); Changed(nameof(IsLogs)); Changed(nameof(IsHelp)); Changed(nameof(AgentDescription)); } }
    public bool IsDashboard => SelectedSection == "Dashboard";
    public bool IsSettings => SelectedSection is "Integrations & Settings" or "AI Configuration";
    public bool IsAgent => SelectedSection == "Confluence Agent";
    public bool IsJiraAgent => SelectedSection == "Jira Agent";
    public bool IsHistory => SelectedSection == "Agent History";
    public bool IsLogs => SelectedSection == "Activity Logs";
    public string AgentDescription => SelectedSection == "Jira Agent"
        ? "Your future Jira workspace for searching issues and reviewing changes before execution."
        : "Your future Confluence workspace for finding, improving and publishing pages with your approval.";
    public string Notification { get => notification; private set => Set(ref notification, value); }
    public bool Busy { get => busy; private set { Set(ref busy, value); Changed(nameof(CanEdit)); Changed(nameof(AnyBusy)); } }
    public bool AnyBusy => Busy || JiraAgent?.Busy == true;
    public bool CanEdit => !AnyBusy;
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
        if (JiraAgent is not null) await JiraAgent.InitializeAsync();
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
            if (SelectedIntegration == IntegrationKind.Copilot && value.Model.Length != 0 &&
                AvailableModels.Count > 0 && !AvailableModels.Any(candidate => candidate.Id == value.Model))
                throw new ArgumentException("Choose a model returned by Copilot or leave the preference blank.");
            await settings.SaveAsync(value, credential);
            connectionResults.Remove(SelectedIntegration);
            dirty = false;
            HasCredential = (await settings.LoadAsync(SelectedIntegration)).HasCredential;
            ClearCredentialInput?.Invoke();
            await RefreshStatusAsync();
            ConnectionDetails = "Settings changed. Test the saved connection again.";
            Notification = "Settings saved locally. Connection has not yet been tested.";
        }
        finally { Busy = false; }
    }

    public void CredentialEdited() => MarkDirty();

    public void ReportError(Exception exception)
    {
        Notification = exception is ArgumentException or IntegrationException
            ? exception.Message
            : exception is OperationCanceledException
                ? "Operation cancelled. If a sign-in code was displayed, it is no longer active in this app."
                : "Operation failed. For Copilot, check network/VPN, GitHub access and workplace policy. For local settings, check Windows data-folder and Credential Manager access. Raw provider errors are not displayed to protect credentials.";
    }

    private async void Navigate(string section)
    {
        if (AnyBusy) return;
        if (dirty && IsSettings && section != SelectedSection)
        {
            Notification = "Save your changes before leaving settings, or reload this integration to discard them.";
            return;
        }
        SelectedSection = section;
        if (section == "AI Configuration")
        {
            try { await ChangeIntegrationAsync(IntegrationKind.Copilot); }
            catch (Exception exception) { ReportError(exception); }
        }
    }

    private Task ReloadAsync() => ChangeIntegrationAsync(SelectedIntegration, reload: true);

    public async Task ChangeIntegrationAsync(IntegrationKind kind, bool reload = false)
    {
        if (loading || Busy || (!reload && kind == SelectedIntegration)) return;
        Busy = true;
        try
        {
            if (dirty && ConfirmDiscardChanges?.Invoke() != true)
            {
                RequestedIntegration = SelectedIntegration;
                Changed(nameof(RequestedIntegration));
                Notification = "Unsaved edits kept. Save them before switching, or confirm discarding them.";
                return;
            }
            await LoadIntegrationAsync(kind);
            Notification = "Saved settings loaded. Test connection to verify access.";
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
            RequestedIntegration = kind;
            BaseUrl = value.BaseUrl;
            Email = value.Email;
            DefaultScope = value.DefaultScope;
            Model = value.Model;
            HasCredential = value.HasCredential;
            ConnectionDetails = connectionResults.TryGetValue(kind, out var result) ? result.Message : "Connection has not been tested in this session.";
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
            connectionResults.Remove(SelectedIntegration);
            HasCredential = false;
            ClearCredentialInput?.Invoke();
            await RefreshStatusAsync();
            ConnectionDetails = "Stored token deleted. Save a token before testing again.";
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
        JiraState = ConnectionState(IntegrationKind.Jira, (await settings.LoadAsync(IntegrationKind.Jira)).HasCredential);
        ConfluenceState = ConnectionState(IntegrationKind.Confluence, (await settings.LoadAsync(IntegrationKind.Confluence)).HasCredential);
        var ai = await settings.LoadAsync(IntegrationKind.Copilot);
        CopilotModel = string.IsNullOrWhiteSpace(ai.Model) ? "Default model" : ai.Model;
        Changed(nameof(CopilotHeader));
    }

    private string ConnectionState(IntegrationKind kind, bool configured) =>
        connectionResults.TryGetValue(kind, out var result) ? result.Success ? "Connection verified" : "Test failed" : configured ? "Configured / untested" : "Not configured";

    private async Task RunConnectionAsync(bool signIn)
    {
        if (Busy) return;
        if (dirty)
        {
            Notification = "Save your settings and token before testing or signing in. The test uses only saved configuration.";
            return;
        }
        Busy = true;
        operation = new CancellationTokenSource(signIn ? TimeSpan.FromMinutes(16) : TimeSpan.FromSeconds(60));
        Changed(nameof(CanCancel));
        var kind = SelectedIntegration;
        try
        {
            Notification = signIn ? "Starting GitHub device authorization..." : $"Testing {kind} connection...";
            ConnectionResult result;
            if (kind == IntegrationKind.Copilot)
            {
                AvailableModels.Clear();
                CopilotState = "Checking...";
                Changed(nameof(CopilotHeader));
                var activeOperation = operation;
                var progress = new Progress<DeviceLoginPrompt>(prompt =>
                {
                    if (operation != activeOperation || activeOperation.IsCancellationRequested) return;
                    loginPrompt = prompt;
                    Changed(nameof(DeviceCode)); Changed(nameof(VerificationUrl)); Changed(nameof(HasDeviceCode));
                    Notification = "Open the GitHub verification page and enter the displayed one-time code. Waiting for authorization...";
                });
                var response = signIn ? await copilot.SignInAsync(progress, operation.Token) : await copilot.TestAsync(operation.Token);
                result = response.Connection;
                foreach (var candidate in response.Models) AvailableModels.Add(candidate);
                CopilotState = result.Success ? $"Connected{(response.Login is null ? "" : $" as {response.Login}")}" :
                    result.Failure == ConnectionFailure.Authentication ? "Not signed in" : "Check failed";
            }
            else result = await atlassian.TestAsync(kind, operation.Token);
            connectionResults[kind] = result;
            ConnectionDetails = result.Message;
            Notification = result.Message;
        }
        catch (Exception exception)
        {
            ReportError(exception);
            connectionResults[kind] = new(false, Notification, ConnectionFailure.Provider);
            ConnectionDetails = Notification;
            if (kind == IntegrationKind.Copilot) CopilotState = "Status unknown";
        }
        finally
        {
            operation.Dispose();
            operation = null;
            loginPrompt = null;
            Changed(nameof(CanCancel)); Changed(nameof(DeviceCode)); Changed(nameof(VerificationUrl)); Changed(nameof(HasDeviceCode));
            try { await RefreshStatusAsync(); }
            finally { Busy = false; }
        }
    }

    private async Task SignOutAsync()
    {
        if (Busy) return;
        Busy = true;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await copilot.SignOutAsync(timeout.Token);
            AvailableModels.Clear();
            connectionResults.Remove(IntegrationKind.Copilot);
            CopilotState = "Not signed in";
            Changed(nameof(CopilotHeader));
            ConnectionDetails = "Signed out of this app's Copilot runtime. Atlassian credentials were not changed.";
            Notification = ConnectionDetails;
        }
        finally { Busy = false; }
    }

    private void MarkDirty() { if (!loading) dirty = true; }
}
