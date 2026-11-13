namespace AtlassianPilot.Core;

public enum ConnectionFailure { None, Configuration, Authentication, Permission, RateLimit, Network, Timeout, InvalidResponse, Provider }

public sealed record ConnectionResult(bool Success, string Message, ConnectionFailure Failure = ConnectionFailure.None);

/// <summary>Tests a saved Atlassian connection using a read-only authenticated API request.</summary>
public interface IAtlassianConnectionTester
{
    Task<ConnectionResult> TestAsync(IntegrationKind kind, CancellationToken cancellationToken = default);
}

public sealed record CopilotModel(string Id, string Name)
{
    public string DisplayName => $"{Name} ({Id})";
}

public sealed record CopilotConnectionResult(ConnectionResult Connection, IReadOnlyList<CopilotModel> Models, string? Login = null);
public sealed record DeviceLoginPrompt(string UserCode, Uri VerificationUri, int ExpiresInSeconds);

/// <summary>Authenticates with GitHub Copilot without receiving or exposing Atlassian credentials.</summary>
public interface ICopilotConnection
{
    Task<CopilotConnectionResult> TestAsync(CancellationToken cancellationToken = default);
    Task<CopilotConnectionResult> SignInAsync(IProgress<DeviceLoginPrompt> progress, CancellationToken cancellationToken = default);
    Task SignOutAsync(CancellationToken cancellationToken = default);
}

public sealed class IntegrationException(string message) : Exception(message);
