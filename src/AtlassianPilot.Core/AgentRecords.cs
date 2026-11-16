namespace AtlassianPilot.Core;

public sealed record AgentConversationMessage(string Speaker, string Text, DateTimeOffset CreatedAt);
public sealed record AgentConversation(string Id, IntegrationKind Agent, string Title, DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt, IReadOnlyList<AgentConversationMessage> Messages);

public interface IConversationHistoryStore
{
    Task SaveAsync(AgentConversation conversation, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AgentConversation>> ListConversationsAsync(IntegrationKind? agent = null, CancellationToken cancellationToken = default);
    Task DeleteAsync(string id, CancellationToken cancellationToken = default);
    Task DeleteAllAsync(CancellationToken cancellationToken = default);
}

public sealed record AgentActivity(long Id, DateTimeOffset CreatedAt, IntegrationKind Agent, string Action, string Outcome);

public interface IActivityLogStore
{
    Task RecordAsync(IntegrationKind agent, string action, string outcome, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AgentActivity>> ListActivitiesAsync(IntegrationKind? agent = null, string? filter = null, CancellationToken cancellationToken = default);
    Task ClearAsync(CancellationToken cancellationToken = default);
}
