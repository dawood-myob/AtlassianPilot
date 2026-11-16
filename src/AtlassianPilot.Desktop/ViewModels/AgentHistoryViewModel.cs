using System.Collections.ObjectModel;
using System.Windows.Input;
using AtlassianPilot.Core;

namespace AtlassianPilot.Desktop.ViewModels;

public sealed class AgentHistoryViewModel : ObservableObject
{
    private readonly IConversationHistoryStore store;
    private string selectedAgent = "All agents";
    private AgentConversation? selectedConversation;
    private bool busy;
    private string status = "Conversation history is stored locally for 30 days.";

    public ObservableCollection<string> AgentFilters { get; } = ["All agents", "Jira", "Confluence"];
    public ObservableCollection<AgentConversation> Conversations { get; } = [];
    public ICommand RefreshCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand DeleteAllCommand { get; }
    public event Func<AgentConversation, bool>? ConfirmDelete;
    public event Func<bool>? ConfirmDeleteAll;
    public event Action? BusyChanged;
    public string SelectedAgent { get => selectedAgent; set { if (selectedAgent == value) return; Set(ref selectedAgent, value); _ = LoadAsync(); } }
    public AgentConversation? SelectedConversation { get => selectedConversation; set { if (selectedConversation == value) return; Set(ref selectedConversation, value); Changed(nameof(HasSelectedConversation)); } }
    public bool HasSelectedConversation => SelectedConversation is not null;
    public string Status { get => status; private set => Set(ref status, value); }
    public bool Busy { get => busy; private set { Set(ref busy, value); Changed(nameof(CanEdit)); BusyChanged?.Invoke(); } }
    public bool CanEdit => !Busy;

    public AgentHistoryViewModel(IConversationHistoryStore store)
    {
        this.store = store;
        RefreshCommand = new AsyncCommand(LoadAsync, ReportError);
        DeleteCommand = new AsyncCommand(DeleteSelectedAsync, ReportError);
        DeleteAllCommand = new AsyncCommand(DeleteAllAsync, ReportError);
    }

    public async Task InitializeAsync() => await LoadAsync();

    public async Task LoadAsync()
    {
        if (Busy) return;
        Busy = true;
        try
        {
            var filter = SelectedAgent switch { "Jira" => IntegrationKind.Jira, "Confluence" => IntegrationKind.Confluence, _ => (IntegrationKind?)null };
            var selectedId = SelectedConversation?.Id;
            var items = await store.ListConversationsAsync(filter);
            Conversations.Clear();
            foreach (var item in items) Conversations.Add(item);
            SelectedConversation = Conversations.FirstOrDefault(item => item.Id == selectedId) ?? Conversations.FirstOrDefault();
            Status = $"Showing {Conversations.Count} conversation(s). Histories older than 30 days are removed automatically.";
        }
        catch (Exception exception) { ReportError(exception); }
        finally { Busy = false; }
    }

    public async Task DeleteSelectedAsync()
    {
        if (Busy || SelectedConversation is not { } conversation || ConfirmDelete?.Invoke(conversation) != true) return;
        Busy = true;
        try
        {
            await store.DeleteAsync(conversation.Id);
            Status = "Conversation deleted from local history.";
            await LoadAfterOperationAsync();
        }
        catch (Exception exception) { ReportError(exception); }
        finally { Busy = false; }
    }

    public async Task DeleteAllAsync()
    {
        if (Busy || ConfirmDeleteAll?.Invoke() != true) return;
        Busy = true;
        try
        {
            await store.DeleteAllAsync();
            Conversations.Clear();
            SelectedConversation = null;
            Status = "All conversation history deleted.";
        }
        catch (Exception exception) { ReportError(exception); }
        finally { Busy = false; }
    }

    private async Task LoadAfterOperationAsync()
    {
        var filter = SelectedAgent switch { "Jira" => IntegrationKind.Jira, "Confluence" => IntegrationKind.Confluence, _ => (IntegrationKind?)null };
        var items = await store.ListConversationsAsync(filter);
        Conversations.Clear();
        foreach (var item in items) Conversations.Add(item);
        SelectedConversation = Conversations.FirstOrDefault();
    }

    private void ReportError(Exception exception) => Status = exception is IntegrationException or ArgumentException
        ? exception.Message : "Conversation history could not be read or changed. Check the local workspace.";
}
