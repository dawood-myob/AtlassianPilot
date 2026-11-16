using System.Collections.ObjectModel;
using System.Windows.Input;
using AtlassianPilot.Core;

namespace AtlassianPilot.Desktop.ViewModels;

public sealed class ActivityLogViewModel : ObservableObject
{
    private readonly IActivityLogStore store;
    private string selectedAgent = "All agents";
    private string filter = "";
    private string status = "Activity records store only approved action and outcome labels, never prompts or page text.";
    private bool busy;

    public ActivityLogViewModel(IActivityLogStore store)
    {
        this.store = store;
        RefreshCommand = new AsyncCommand(LoadAsync, ReportError);
        ClearCommand = new AsyncCommand(ClearAsync, ReportError);
    }

    public ObservableCollection<string> AgentFilters { get; } = ["All agents", "Jira", "Confluence"];
    public ObservableCollection<AgentActivity> Entries { get; } = [];
    public ICommand RefreshCommand { get; }
    public ICommand ClearCommand { get; }
    public event Func<bool>? ConfirmClear;
    public event Action? BusyChanged;
    public string SelectedAgent { get => selectedAgent; set { if (selectedAgent == value) return; Set(ref selectedAgent, value); _ = LoadAsync(); } }
    public string Filter { get => filter; set => Set(ref filter, value); }
    public string Status { get => status; private set => Set(ref status, value); }
    public bool Busy { get => busy; private set { Set(ref busy, value); Changed(nameof(CanEdit)); BusyChanged?.Invoke(); } }
    public bool CanEdit => !Busy;

    public async Task InitializeAsync() => await LoadAsync();

    public async Task LoadAsync()
    {
        if (Busy) return;
        Busy = true;
        try
        {
            var agent = SelectedAgent switch { "Jira" => IntegrationKind.Jira, "Confluence" => IntegrationKind.Confluence, _ => (IntegrationKind?)null };
            var entries = await store.ListActivitiesAsync(agent, Filter.Trim());
            Entries.Clear();
            foreach (var entry in entries) Entries.Add(entry);
            Status = $"Showing {Entries.Count} recent activity record(s). Records expire after 90 days; at most 5,000 are retained.";
        }
        catch (Exception exception) { ReportError(exception); }
        finally { Busy = false; }
    }

    public async Task ClearAsync()
    {
        if (Busy || ConfirmClear?.Invoke() != true) return;
        Busy = true;
        try
        {
            await store.ClearAsync();
            Entries.Clear();
            Status = "Activity history cleared.";
        }
        catch (Exception exception) { ReportError(exception); }
        finally { Busy = false; }
    }

    private void ReportError(Exception exception) => Status = exception is IntegrationException or ArgumentException
        ? exception.Message : "Activity history could not be read or changed. Check the local workspace.";
}
