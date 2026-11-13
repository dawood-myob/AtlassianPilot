namespace AtlassianPilot.Core;

public enum IntegrationKind { Jira, Confluence, Copilot }

public sealed record IntegrationSettings(
    IntegrationKind Kind,
    string BaseUrl = "",
    string Email = "",
    string DefaultScope = "",
    string Model = "",
    bool HasCredential = false);

/// <summary>Stores non-secret configuration and encrypted credential updates atomically.</summary>
public interface ISettingsStore
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task<IntegrationSettings> LoadAsync(IntegrationKind kind, CancellationToken cancellationToken = default);
    /// <summary>A null credential preserves it. Deletion must be requested explicitly.</summary>
    Task SaveAsync(IntegrationSettings settings, string? credential = null, bool deleteCredential = false,
        CancellationToken cancellationToken = default);
    Task<string> GetThemeAsync(CancellationToken cancellationToken = default);
    Task SetThemeAsync(string theme, CancellationToken cancellationToken = default);
}

/// <summary>Provides credentials only to trusted integration services, never to agent prompts.</summary>
public interface ICredentialStore
{
    Task<string?> GetAsync(IntegrationKind kind, CancellationToken cancellationToken = default);
}

/// <summary>Protects secrets using the operating system's current-user credential protection.</summary>
public interface ISecretProtector
{
    byte[] Protect(string secret);
    string Unprotect(byte[] encryptedSecret);
}

public static class SettingsValidator
{
    public static void Validate(IntegrationSettings settings)
    {
        if (!Enum.IsDefined(settings.Kind))
            throw new ArgumentException("Unknown integration.");
        if (settings.Kind == IntegrationKind.Copilot)
        {
            if (settings.BaseUrl.Length != 0 || settings.Email.Length != 0 || settings.DefaultScope.Length != 0)
                throw new ArgumentException("Copilot uses GitHub sign-in, not Atlassian connection fields.");
            if (settings.Model.Length > 120)
                throw new ArgumentException("Model name must be 120 characters or fewer.");
            return;
        }
        if (!Uri.TryCreate(settings.BaseUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || string.IsNullOrEmpty(uri.Host) ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new ArgumentException("Enter an HTTPS base URL without credentials, query parameters or fragments.");
        if (settings.BaseUrl.Length > 2048)
            throw new ArgumentException("Base URL is too long.");
        if (!System.Net.Mail.MailAddress.TryCreate(settings.Email, out var address) || address.Address != settings.Email)
            throw new ArgumentException("Enter a valid Atlassian email address.");
        if (settings.DefaultScope.Length > 120)
            throw new ArgumentException("Default project or space must be 120 characters or fewer.");
    }
}
