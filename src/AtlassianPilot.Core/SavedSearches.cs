namespace AtlassianPilot.Core;

public sealed record SavedJiraSearch(string Id, string Name, string Request);

/// <summary>Persists user-defined Jira search prompts in the local workspace.</summary>
public interface ISavedJiraSearchStore
{
    Task<IReadOnlyList<SavedJiraSearch>> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(IReadOnlyList<SavedJiraSearch> searches, CancellationToken cancellationToken = default);
}
