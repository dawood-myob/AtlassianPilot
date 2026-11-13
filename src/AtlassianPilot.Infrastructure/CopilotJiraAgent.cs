using System.ComponentModel;
using AtlassianPilot.Core;
using GitHub.Copilot;
using Microsoft.Extensions.AI;

namespace AtlassianPilot.Infrastructure;

public sealed class CopilotJiraAgent(ICopilotRuntime runtime, IJiraIssueSearchService jira, ISettingsStore settings)
    : IJiraAgent
{
    private const string SystemMessage = "You are AtlassianPilot's Jira Agent. You may only search Jira using the provided read-only search_jira_issues tool. Never claim to have searched or found issues unless that tool returned them. Never invent issue keys, summaries, statuses, assignees, priorities, counts, or links. Translate the user's request into a single valid JQL query, explain the query briefly, and summarize only the structured results returned by Jira. If a query fails, explain the sanitized error and ask for clarification when needed. Do not attempt to modify Jira.";
    private readonly SemaphoreSlim gate = new(1, 1);
    private CopilotSession? session;
    private string? sessionModel;
    private IDisposable? toolEventSubscription;
    private volatile SearchTurn? activeTurn;
    private bool disposed;

    private sealed class SearchTurn(CancellationToken token, IProgress<JiraAgentProgress>? progress)
    {
        public CancellationToken Token { get; } = token;
        public IProgress<JiraAgentProgress>? Progress { get; } = progress;
        public JiraIssueSearchResult? Result { get; set; }
        public string? Jql { get; set; }
        public string? Error { get; set; }
    }

    public async Task<JiraAgentReply> SendAsync(string prompt, IProgress<JiraAgentProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(prompt) || prompt.Trim().Length > 4000)
            throw new IntegrationException("Enter a Jira request of 1–4000 characters.");

        await gate.WaitAsync(cancellationToken);
        using var turnCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var turn = new SearchTurn(turnCancellation.Token, progress);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            await EnsureSessionAsync(cancellationToken);
            progress?.Report(new("Copilot is considering your Jira request..."));
            activeTurn = turn;
            AssistantMessageEvent? response;
            try
            {
                response = await session!.SendAndWaitAsync(new MessageOptions { Prompt = prompt.Trim() },
                    TimeSpan.FromMinutes(2), cancellationToken);
            }
            catch (Exception exception) when (exception is OperationCanceledException or TimeoutException)
            {
                activeTurn = null;
                turnCancellation.Cancel();
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await session!.AbortAsync(cleanup.Token);
                await DisposeSessionAsync();
                throw;
            }
            if (response is null)
                throw new IntegrationException("Copilot returned no response. Check its connection and try again.");
            if (turn.Result is null && turn.Error is not null)
                throw new IntegrationException(turn.Error);
            var content = turn.Result is null
                ? "No Jira search was performed. Try rephrasing your request as an issue search."
                : $"Jira returned {turn.Result.Issues.Count} issue(s) on this page.";
            return new(content, turn.Result, turn.Jql);
        }
        finally
        {
            activeTurn = null;
            turnCancellation.Cancel();
            gate.Release();
        }
    }

    public async Task NewConversationAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            await DisposeSessionAsync();
        }
        finally { gate.Release(); }
    }

    private async Task EnsureSessionAsync(CancellationToken cancellationToken)
    {
        await runtime.StartAsync(cancellationToken);
        var status = await runtime.GetStatusAsync(cancellationToken);
        if (!status.Authenticated)
        {
            await DisposeSessionAsync();
            throw new IntegrationException("Sign in to GitHub Copilot in AI Configuration before starting the Jira Agent.");
        }
        var preferences = await settings.LoadAsync(IntegrationKind.Copilot, cancellationToken);
        var model = string.IsNullOrWhiteSpace(preferences.Model) ? null : preferences.Model;
        if (session is not null && model == sessionModel) return;
        if (session is not null) await DisposeSessionAsync();
        var models = await runtime.GetModelsAsync(cancellationToken);
        if (model is not null && !models.Any(candidate => candidate.Id == model))
            throw new IntegrationException("The saved Copilot model is not currently available. Choose an available model in AI Configuration or leave the preference blank.");

        var tool = CopilotTool.DefineTool(
            (Func<string, ToolInvocation, int, string?, Task<JiraIssueSearchResult>>)SearchJiraIssuesAsync,
            new CopilotToolOptions { SkipPermission = true, Idempotent = true },
            new AIFunctionFactoryOptions
            {
                Name = "search_jira_issues",
                Description = "Search the connected Jira account using read-only JQL. Returns real issue keys, summaries, statuses, assignees, priorities, issue types, and links, plus a continuation token when more results exist."
            });
        session = await runtime.CreateAgentSessionAsync([tool], model, SystemMessage, cancellationToken);
        sessionModel = model;
        toolEventSubscription = session.On<ToolExecutionStartEvent>(evt =>
            activeTurn?.Progress?.Report(new($"Copilot is running {evt.Data.ToolName}...")));
    }

    private async Task<JiraIssueSearchResult> SearchJiraIssuesAsync(
        [Description("A valid read-only Jira Query Language expression, for example assignee = currentUser() AND status = 'In Progress' ORDER BY updated DESC.")]
        string jql,
        ToolInvocation invocation,
        [Description("Maximum number of issues to return on this page, between 1 and 100.")]
        int maxResults = 50,
        [Description("Opaque continuation token from a previous Jira search page, or null for the first page.")]
        string? nextPageToken = null)
    {
        var turn = activeTurn ?? throw new IntegrationException("This Jira request is no longer active.");
        if (session is null || invocation.SessionId != session.SessionId)
            throw new IntegrationException("This Jira request belongs to an inactive session.");
        turn.Token.ThrowIfCancellationRequested();
        turn.Progress?.Report(new("Searching Jira with a read-only JQL request..."));
        try
        {
            var result = await jira.SearchAsync(jql, maxResults, nextPageToken, turn.Token);
            turn.Result = result;
            turn.Jql = jql;
            turn.Error = null;
            turn.Progress?.Report(new($"Jira returned {result.Issues.Count} actual issue(s).", result, jql));
            return result;
        }
        catch (IntegrationException exception)
        {
            turn.Result = null;
            turn.Jql = null;
            turn.Error = exception.Message;
            throw;
        }
    }

    private async Task DisposeSessionAsync()
    {
        toolEventSubscription?.Dispose();
        toolEventSubscription = null;
        if (session is not null)
        {
            await session.DisposeAsync();
            session = null;
            sessionModel = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync();
        try
        {
            if (disposed) return;
            await DisposeSessionAsync();
            disposed = true;
        }
        finally { gate.Release(); }
    }
}
