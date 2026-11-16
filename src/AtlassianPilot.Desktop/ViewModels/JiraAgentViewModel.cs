using System.Collections.ObjectModel;
using System.Windows.Input;
using AtlassianPilot.Core;

namespace AtlassianPilot.Desktop.ViewModels;

public sealed record JiraChatMessage(string Speaker, string Text);
public sealed record JiraSearchChoice(string? Id, string Name, string Request, bool IsExample);

public sealed class JiraAgentViewModel : ObservableObject
{
    private readonly IJiraAgent agent;
    private readonly IJiraIssueSearchService search;
    private readonly ISavedJiraSearchStore savedSearchStore;
    private readonly IConversationHistoryStore? conversationHistory;
    private readonly IActivityLogStore? activityLog;
    private readonly List<SavedJiraSearch> savedSearches = [];
    private string? conversationId;
    private DateTimeOffset conversationCreated;
    private JiraSearchChoice? selectedSearch;
    private JiraIssueDetails? issueDetails;
    private JiraIssueDraft? draft;
    private string searchName = "";
    private string prompt = "";
    private string status = "Ready to search. Jira searches are read-only.";
    private string? jql;
    private string? nextPageToken;
    private int pageSize = 50;
    private bool busy;
    private CancellationTokenSource? operation;

    public JiraAgentViewModel(IJiraAgent agent, IJiraIssueSearchService search, ISavedJiraSearchStore savedSearchStore,
        IConversationHistoryStore? conversationHistory = null, IActivityLogStore? activityLog = null)
    {
        this.agent = agent;
        this.search = search;
        this.savedSearchStore = savedSearchStore;
        this.conversationHistory = conversationHistory;
        this.activityLog = activityLog;
        SendCommand = new AsyncCommand(SendAsync, ReportError);
        NewConversationCommand = new AsyncCommand(NewConversationAsync, ReportError);
        LoadMoreCommand = new AsyncCommand(LoadMoreAsync, ReportError);
        CancelCommand = new RelayCommand(_ => operation?.Cancel());
        UseSearchCommand = new RelayCommand(_ => UseSelectedSearch());
        SaveNewSearchCommand = new AsyncCommand(() => SaveSearchAsync(false), ReportError);
        UpdateSearchCommand = new AsyncCommand(() => SaveSearchAsync(true), ReportError);
        DeleteSearchCommand = new AsyncCommand(DeleteSearchAsync, ReportError);
    }

    public ObservableCollection<JiraChatMessage> Messages { get; } = [];
    public ObservableCollection<JiraIssueSummary> Issues { get; } = [];
    public ObservableCollection<JiraSearchChoice> Searches { get; } = [];
    public JiraIssueDetails? IssueDetails { get => issueDetails; private set { Set(ref issueDetails, value); Changed(nameof(HasIssueDetails)); Changed(nameof(Query)); } }
    public bool HasIssueDetails => IssueDetails is not null;
    public JiraIssueDraft? Draft { get => draft; private set { Set(ref draft, value); Changed(nameof(HasDraft)); } }
    public bool HasDraft => Draft is not null;
    public JiraSearchChoice? SelectedSearch { get => selectedSearch; set { Set(ref selectedSearch, value); Changed(nameof(HasSelectedSearch)); Changed(nameof(HasSelectedSavedSearch)); } }
    public bool HasSelectedSearch => SelectedSearch is not null && CanEdit;
    public bool HasSelectedSavedSearch => SelectedSearch is { IsExample: false } && CanEdit;
    public string SearchName { get => searchName; set => Set(ref searchName, value); }
    public string Prompt { get => prompt; set => Set(ref prompt, value); }
    public string Status { get => status; private set => Set(ref status, value); }
    public string Query => jql is null ? IssueDetails is null ? "No search has been run." : "Issue details" : $"JQL: {jql}";
    public bool Busy { get => busy; private set { Set(ref busy, value); Changed(nameof(CanEdit)); Changed(nameof(CanLoadMore)); Changed(nameof(CanCancel)); Changed(nameof(HasSelectedSearch)); Changed(nameof(HasSelectedSavedSearch)); } }
    public bool CanEdit => !Busy;
    public bool CanCancel => Busy && operation is not null;
    public bool CanLoadMore => !Busy && jql is not null && nextPageToken is not null;
    public event Action? BusyChanged;
    public event Action? CopilotRequestStarted;
    public event Action<bool>? CopilotRequestFinished;

    public ICommand SendCommand { get; }
    public ICommand NewConversationCommand { get; }
    public ICommand LoadMoreCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand UseSearchCommand { get; }
    public ICommand SaveNewSearchCommand { get; }
    public ICommand UpdateSearchCommand { get; }
    public ICommand DeleteSearchCommand { get; }

    public async Task InitializeAsync()
    {
        savedSearches.Clear();
        savedSearches.AddRange(await savedSearchStore.LoadAsync());
        RefreshSearches();
    }

    public void UseSelectedSearch()
    {
        if (Busy || SelectedSearch is null) return;
        Prompt = SelectedSearch.Request;
        SearchName = SelectedSearch.IsExample ? "" : SelectedSearch.Name;
        Status = "Search copied into your request. Review or edit it before searching.";
    }

    public async Task SaveSearchAsync(bool update)
    {
        if (Busy) return;
        var name = SearchName.Trim();
        var request = Prompt.Trim();
        if (name.Length is 0 or > 80 || request.Length is 0 or > 4000)
        {
            Status = "Enter a search name (up to 80 characters) and a request (up to 4000 characters).";
            return;
        }
        if (update && (SelectedSearch is null || SelectedSearch.IsExample))
        {
            Status = "Select a saved search to update. Examples cannot be changed; save a copy instead.";
            return;
        }
        var id = update ? SelectedSearch!.Id! : Guid.NewGuid().ToString("N");
        if (savedSearches.Any(item => item.Id != id && string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            Status = "A saved search already has that name. Choose a different name.";
            return;
        }
        var changed = savedSearches.Where(item => item.Id != id).ToList();
        changed.Add(new(id, name, request));
        StartOperation();
        try
        {
            await savedSearchStore.SaveAsync(changed, operation!.Token);
            savedSearches.Clear();
            savedSearches.AddRange(changed);
            RefreshSearches(id);
            Status = update ? "Saved search updated." : "Search saved locally.";
        }
        catch (OperationCanceledException) { Status = "Saving the search was cancelled."; }
        catch (Exception exception) { ReportStorageError(exception); }
        finally { FinishOperation(); }
    }

    public async Task DeleteSearchAsync()
    {
        if (Busy || SelectedSearch is null || SelectedSearch.IsExample) return;
        var id = SelectedSearch.Id;
        var changed = savedSearches.Where(item => item.Id != id).ToList();
        StartOperation();
        try
        {
            await savedSearchStore.SaveAsync(changed, operation!.Token);
            savedSearches.Clear();
            savedSearches.AddRange(changed);
            RefreshSearches();
            SearchName = "";
            Status = "Saved search deleted. The request box was not changed.";
        }
        catch (OperationCanceledException) { Status = "Deleting the search was cancelled."; }
        catch (Exception exception) { ReportStorageError(exception); }
        finally { FinishOperation(); }
    }

    private void RefreshSearches(string? selectedId = null)
    {
        Searches.Clear();
        Searches.Add(new(null, "Example: My In Progress issues", "Find my Jira issues that are In Progress", true));
        Searches.Add(new(null, "Example: My unresolved issues", "Find my unresolved Jira issues", true));
        Searches.Add(new(null, "Example: Bugs in a project", "Show bugs in project [project key]", true));
        Searches.Add(new(null, "Example: Recently updated", "Find issues updated in the last 30 days", true));
        Searches.Add(new(null, "Example: Assigned to a person", "Find Jira issues assigned to [person name]", true));
        foreach (var item in savedSearches.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
            Searches.Add(new(item.Id, item.Name, item.Request, false));
        SelectedSearch = Searches.FirstOrDefault(item => item.Id == selectedId && !item.IsExample);
    }

    private void ReportStorageError(Exception exception)
    {
        Status = exception is ArgumentException or IntegrationException ? exception.Message :
            "Could not save or delete the search locally. Check access to the AtlassianPilot workspace and try again.";
    }

    public async Task SendAsync()
    {
        if (Busy) return;
        var input = Prompt.Trim();
        if (input.Length is 0 or > 4000)
        {
            Status = "Enter a Jira request of 1–4000 characters.";
            return;
        }
        StartOperation();
        string? activityAction = "Search issues";
        var activityOutcome = "Failed";
        try
        {
            Messages.Add(new("You", input));
            Prompt = "";
            Issues.Clear();
            IssueDetails = null;
            Draft = null;
            SetPage(null, null);
            var currentOperation = operation!;
            var progress = new Progress<JiraAgentProgress>(value => { if (operation == currentOperation && Busy) Status = value.Message; });
            CopilotRequestStarted?.Invoke();
            JiraAgentReply result;
            try
            {
                result = await agent.SendAsync(input, progress, currentOperation.Token);
            }
            catch
            {
                CopilotRequestFinished?.Invoke(false);
                throw;
            }
            CopilotRequestFinished?.Invoke(true);
            Messages.Add(new("Jira Agent", result.Content));
            activityAction = result.Draft is not null ? null : result.IssueDetails is not null ? "Read issue" : result.SearchResult is not null ? "Search issues" : null;
            activityOutcome = "Completed";
            IssueDetails = result.IssueDetails;
            Draft = result.Draft;
            if (result.Draft is not null)
                Status = "Draft only: no Jira issue was created or validated here. Open Create Jira in the sidebar for refinement and approved creation.";
            else if (result.IssueDetails is not null)
                Status = "Issue fields retrieved. Some nested collections may be paginated; history is not included.";
            else if (result.SearchResult is not null && result.Jql is not null)
            {
                foreach (var issue in result.SearchResult.Issues) Issues.Add(issue);
                pageSize = result.SearchResult.PageSize;
                SetPage(result.Jql, result.SearchResult.HasMore ? result.SearchResult.NextPageToken : null);
                Status = result.SearchResult.HasMore
                    ? "More results are available. Select Load more to retrieve the next page."
                    : "Search complete. Only results returned by Jira are shown.";
            }
            else Status = "No Jira issue was created or modified.";
        }
        catch (OperationCanceledException) { activityOutcome = "Cancelled"; Status = "Jira request cancelled."; }
        catch (Exception exception) { ReportError(exception); }
        finally
        {
            await PersistConversationAsync();
            if (activityAction is not null) await RecordActivityAsync(activityAction, activityOutcome);
            FinishOperation();
        }
    }

    public async Task LoadMoreAsync()
    {
        if (!CanLoadMore) return;
        var query = jql!;
        var token = nextPageToken!;
        StartOperation();
        try
        {
            Status = "Loading the next page from Jira...";
            var result = await search.SearchAsync(query, pageSize, token, operation!.Token);
            foreach (var issue in result.Issues) Issues.Add(issue);
            SetPage(query, result.HasMore ? result.NextPageToken : null);
            Status = result.HasMore ? "More Jira results are available." : "All available pages have been loaded.";
            await RecordActivityAsync("Search issues", "Completed");
        }
        catch (OperationCanceledException) { Status = "Loading more results was cancelled."; await RecordActivityAsync("Search issues", "Cancelled"); }
        catch (Exception exception) { ReportError(exception); await RecordActivityAsync("Search issues", "Failed"); }
        finally { FinishOperation(); }
    }

    public async Task NewConversationAsync()
    {
        if (Busy) return;
        StartOperation();
        try
        {
            await agent.NewConversationAsync(operation!.Token);
            Messages.Clear();
            Issues.Clear();
            IssueDetails = null;
            Draft = null;
            Prompt = "";
            conversationId = null;
            conversationCreated = default;
            SetPage(null, null);
            Status = "New conversation started.";
        }
        catch (OperationCanceledException) { Status = "Starting a new conversation was cancelled."; }
        catch (Exception exception) { ReportError(exception); }
        finally { FinishOperation(); }
    }

    private void StartOperation()
    {
        operation = new CancellationTokenSource();
        Busy = true;
        BusyChanged?.Invoke();
    }

    private void FinishOperation()
    {
        operation?.Dispose();
        operation = null;
        Busy = false;
        BusyChanged?.Invoke();
    }

    private async Task PersistConversationAsync()
    {
        if (conversationHistory is null || Messages.Count == 0) return;
        var now = DateTimeOffset.UtcNow;
        conversationId ??= Guid.NewGuid().ToString("N");
        if (conversationCreated == default) conversationCreated = now;
        var title = Messages.FirstOrDefault(message => message.Speaker == "You")?.Text ?? "Jira conversation";
        if (title.Length > 120) title = title[..120];
        try
        {
            var messages = Messages.Select(message => new AgentConversationMessage(message.Speaker, message.Text, now)).ToArray();
            await conversationHistory.SaveAsync(new(conversationId, IntegrationKind.Jira, title, conversationCreated, now, messages));
        }
        catch (Exception)
        {
            Status += " Conversation is visible now but could not be saved to local history.";
        }
    }

    private async Task RecordActivityAsync(string action, string outcome)
    {
        if (activityLog is null) return;
        try { await activityLog.RecordAsync(IntegrationKind.Jira, action, outcome); }
        catch (Exception) { Status += " Activity log could not be saved locally."; }
    }

    private void SetPage(string? query, string? token)
    {
        jql = query;
        nextPageToken = token;
        Changed(nameof(Query));
        Changed(nameof(CanLoadMore));
    }

    private void ReportError(Exception exception)
    {
        Status = exception is IntegrationException or ArgumentException
            ? exception.Message
            : "Jira Agent failed. Check Copilot sign-in, Jira access, network and workplace policy, then try again. Provider details are hidden to protect credentials.";
    }
}
