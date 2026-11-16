using AtlassianPilot.Core;
using AtlassianPilot.Desktop.ViewModels;

namespace AtlassianPilot.UnitTests;

public sealed class JiraActionsViewModelTests
{
    [Fact]
    public async Task JiraWriteRequiresConfirmationAndAnApprovalCanOnlyExecuteOnce()
    {
        var service = new Actions();
        var log = new Activity();
        var vm = new JiraActionsViewModel(service, log) { IssueKey = "ABC-1", Comment = "Approved comment" };
        await vm.PrepareCommentAsync();
        Assert.True(vm.HasPreview);
        Assert.Equal(0, service.Executions);

        Func<JiraActionPreview, bool> decline = _ => false;
        vm.ConfirmAction += decline;
        await vm.ApproveAsync();
        Assert.Equal(0, service.Executions);
        Assert.True(vm.HasPreview);
        Assert.Equal("Declined", Assert.Single(log.Outcomes).Outcome);

        vm.ConfirmAction -= decline;
        vm.ConfirmAction += _ => true;
        await vm.ApproveAsync();
        await vm.ApproveAsync();
        Assert.Equal(1, service.Executions);
        Assert.False(vm.HasPreview);
        Assert.Equal("Completed", log.Outcomes[^1].Outcome);
    }

    [Fact]
    public async Task ClonedIssueLinkVisibilityTracksTheExecutionResult()
    {
        var service = new Actions { ExecutionResult = new JiraActionResult("Cloned", "ABC-2", new Uri("https://site.atlassian.net/browse/ABC-2")) };
        var vm = new JiraActionsViewModel(service, new Activity()) { IssueKey = "ABC-1", CloneProjectKey = "ABC", CloneIssueType = "Task" };
        await vm.PrepareCloneAsync();
        vm.ConfirmAction += _ => true;
        await vm.ApproveAsync();
        Assert.True(vm.HasIssueUrl);
        Assert.Equal("https://site.atlassian.net/browse/ABC-2", vm.Result!.IssueUrl!.AbsoluteUri);
        vm.IssueKey = "ABC-3";
        Assert.False(vm.HasIssueUrl);
    }

    [Fact]
    public async Task EditingAnActionDiscardsItsPreparedApproval()
    {
        var service = new Actions();
        var vm = new JiraActionsViewModel(service, new Activity()) { IssueKey = "ABC-1", Comment = "First comment" };
        await vm.PrepareCommentAsync();
        vm.Comment = "Changed after preview";
        Assert.False(vm.HasPreview);
        Assert.Equal(1, service.Discards);
        await vm.ApproveAsync();
        Assert.Equal(0, service.Executions);
    }

    private sealed class Actions : IJiraIssueActionService
    {
        private JiraActionPreview? preview;
        public int Executions { get; private set; }
        public int Discards { get; private set; }
        public JiraActionResult ExecutionResult { get; set; } = new("Completed.");
        public Task<IReadOnlyList<JiraWorkflowTransition>> GetTransitionsAsync(string issueKey, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<JiraWorkflowTransition>>([]);
        public Task<IReadOnlyList<JiraIssueLinkType>> GetLinkTypesAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<JiraIssueLinkType>>([]);
        public Task<JiraActionPreview> PrepareUpdateAsync(string issueKey, string? summary, string? description, CancellationToken cancellationToken = default) => Prepare("Update issue", issueKey);
        public Task<JiraActionPreview> PrepareCommentAsync(string issueKey, string comment, CancellationToken cancellationToken = default) => Prepare("Add comment", issueKey);
        public Task<JiraActionPreview> PrepareAssignmentAsync(string issueKey, string accountId, CancellationToken cancellationToken = default) => Prepare("Assign issue", issueKey);
        public Task<JiraActionPreview> PrepareTransitionAsync(string issueKey, string transitionId, CancellationToken cancellationToken = default) => Prepare("Transition issue", issueKey);
        public Task<JiraActionPreview> PrepareLinkAsync(string issueKey, string linkedIssueKey, string linkType, CancellationToken cancellationToken = default) => Prepare("Link issues", issueKey);
        public Task<JiraActionPreview> PrepareCloneAsync(string sourceIssueKey, string projectKey, string issueType, CancellationToken cancellationToken = default) => Prepare("Clone issue", sourceIssueKey);
        public Task<JiraActionResult> ExecuteApprovedAsync(Guid previewId, CancellationToken cancellationToken = default)
        {
            if (preview is null || preview.Id != previewId) throw new IntegrationException("Invalid preview.");
            preview = null;
            Executions++;
            return Task.FromResult(ExecutionResult);
        }
        public void DiscardPreview(Guid previewId) { if (preview?.Id == previewId) preview = null; Discards++; }
        private Task<JiraActionPreview> Prepare(string action, string key)
        {
            preview = new(Guid.NewGuid(), action, key, "Exact reviewed payload", "{}", DateTimeOffset.UtcNow.AddMinutes(15));
            return Task.FromResult(preview);
        }
    }

    private sealed class Activity : IActivityLogStore
    {
        public List<(string Action, string Outcome)> Outcomes { get; } = [];
        public Task RecordAsync(IntegrationKind agent, string action, string outcome, CancellationToken cancellationToken = default)
        {
            Outcomes.Add((action, outcome));
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<AgentActivity>> ListActivitiesAsync(IntegrationKind? agent = null, string? filter = null, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<AgentActivity>>([]);
        public Task ClearAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
