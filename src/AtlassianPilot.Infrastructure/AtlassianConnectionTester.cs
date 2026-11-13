using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AtlassianPilot.Core;

namespace AtlassianPilot.Infrastructure;

public sealed class AtlassianConnectionTester(HttpClient http, ISettingsStore settings, ICredentialStore credentials) : IAtlassianConnectionTester
{
    public async Task<ConnectionResult> TestAsync(IntegrationKind kind, CancellationToken cancellationToken = default)
    {
        if (kind is not (IntegrationKind.Jira or IntegrationKind.Confluence))
            return new(false, "Choose Jira or Confluence for this test.", ConnectionFailure.Configuration);
        var configuration = await settings.LoadAsync(kind, cancellationToken);
        try { SettingsValidator.Validate(configuration); }
        catch (ArgumentException exception) { return new(false, exception.Message, ConnectionFailure.Configuration); }
        var token = await credentials.GetAsync(kind, cancellationToken);
        if (string.IsNullOrWhiteSpace(token))
            return new(false, "Save an API token before testing this connection.", ConnectionFailure.Configuration);
        if (token.StartsWith("token:", StringComparison.OrdinalIgnoreCase) || token != token.Trim() || token.StartsWith('"'))
            return new(false, "Paste only the API token value, without token:, quotes or surrounding spaces, and save again.", ConnectionFailure.Configuration);
        var endpoint = new Uri(configuration.BaseUrl.TrimEnd('/') +
            (kind == IntegrationKind.Jira ? "/rest/api/3/myself" : "/rest/api/user/current"));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var bytes = Encoding.UTF8.GetBytes($"{configuration.Email}:{token}");
        string authorization;
        try { authorization = Convert.ToBase64String(bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
        try
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
                request.Headers.Authorization = new AuthenticationHeaderValue("Basic", authorization);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                if (response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500)
                {
                    if (attempt < 2)
                    {
                        var delay = response.Headers.RetryAfter?.Delta ??
                            (response.Headers.RetryAfter?.Date is { } date ? date - DateTimeOffset.UtcNow : TimeSpan.FromSeconds(attempt + 1));
                        if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;
                        if (delay <= TimeSpan.FromSeconds(5))
                        {
                            await Task.Delay(delay, timeout.Token);
                            continue;
                        }
                    }
                    return response.StatusCode == HttpStatusCode.TooManyRequests
                        ? new(false, "Atlassian is rate limiting requests. Wait and try again; no changes were made.", ConnectionFailure.RateLimit)
                        : new(false, "Atlassian is temporarily unavailable. Try again later.", ConnectionFailure.Network);
                }
                if (response.StatusCode == HttpStatusCode.Unauthorized)
                    return new(false, "Authentication failed. Check the account email, token expiry and token type/scopes.", ConnectionFailure.Authentication);
                if (response.StatusCode == HttpStatusCode.Forbidden)
                    return new(false, "Access was denied. Check product access, token scopes and your organization's policies.", ConnectionFailure.Permission);
                if (!response.IsSuccessStatusCode)
                    return new(false, $"Connection test returned HTTP {(int)response.StatusCode}. Check the base URL and product API access.", ConnectionFailure.InvalidResponse);
                if (response.Content.Headers.ContentType?.MediaType is not "application/json")
                    return new(false, "The server did not return JSON. Check that the base URL is the Atlassian API site, not a login page.", ConnectionFailure.InvalidResponse);
                var body = await ReadBoundedAsync(response.Content, timeout.Token);
                using var json = JsonDocument.Parse(body);
                var root = json.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("accountId", out var id) ||
                    id.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(id.GetString()))
                    return new(false, "The API response did not identify an authenticated Atlassian account.", ConnectionFailure.InvalidResponse);
                if (kind == IntegrationKind.Confluence && (!root.TryGetProperty("type", out var type) ||
                    type.ValueKind != JsonValueKind.String || type.GetString() != "known"))
                    return new(false, "Confluence returned an anonymous or unsupported user. Check your credentials.", ConnectionFailure.Authentication);
                return new(true, $"{kind} connection verified at {DateTimeOffset.Now:HH:mm:ss}. Account API access works; specific project/space permissions are not yet tested.");
            }
            throw new InvalidOperationException("Connection retry loop did not terminate.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(false, "Connection test timed out after 30 seconds. Check your network, VPN or proxy.", ConnectionFailure.Timeout);
        }
        catch (HttpRequestException)
        {
            return new(false, "Could not reach Atlassian. Check the hostname, VPN, proxy and trusted HTTPS certificates.", ConnectionFailure.Network);
        }
        catch (JsonException)
        {
            return new(false, "Atlassian returned an invalid account response. Check the API base URL.", ConnectionFailure.InvalidResponse);
        }
        catch (InvalidDataException)
        {
            return new(false, "The account response exceeded the allowed size. Check the API base URL.", ConnectionFailure.InvalidResponse);
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        const int maximum = 64 * 1024;
        if (content.Headers.ContentLength > maximum) throw new InvalidDataException();
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream();
        var buffer = new byte[4096];
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken)) != 0)
        {
            if (output.Length + read > maximum) throw new InvalidDataException();
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }
}
