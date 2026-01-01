using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aspire.Cloudflared;

/// <summary>
/// HTTP client wrapper for interacting with the Cloudflare Tunnel API.
/// </summary>
public sealed class CloudflareApiClient : IDisposable
{
    private const string BaseUrl = "https://api.cloudflare.com/client/v4";
    private readonly HttpClient _httpClient;
    private readonly string _accountId;
    private bool _disposed;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public CloudflareApiClient(string apiToken, string accountId)
    {
        _accountId = accountId;
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(BaseUrl)
        };
        _httpClient.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiToken);
    }

    /// <summary>
    /// Lists all Cloudflare tunnels, optionally filtered by name.
    /// </summary>
    public async Task<CloudflareTunnelInfo?> FindTunnelByNameAsync(string name, CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.GetAsync(
            $"/client/v4/accounts/{_accountId}/cfd_tunnel?name={Uri.EscapeDataString(name)}&is_deleted=false",
            cancellationToken);

        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<CloudflareApiResponse<CloudflareTunnelInfo[]>>(JsonOptions, cancellationToken);

        return result?.Result?.FirstOrDefault();
    }

    /// <summary>
    /// Creates a new Cloudflare tunnel.
    /// </summary>
    public async Task<CloudflareTunnelInfo> CreateTunnelAsync(string name, CancellationToken cancellationToken = default)
    {
        // Generate a random tunnel secret (32 bytes, base64 encoded)
        var secretBytes = RandomNumberGenerator.GetBytes(32);
        var tunnelSecret = Convert.ToBase64String(secretBytes);

        var request = new CreateTunnelRequest(name, tunnelSecret);

        var response = await _httpClient.PostAsJsonAsync(
            $"/client/v4/accounts/{_accountId}/cfd_tunnel",
            request,
            JsonOptions,
            cancellationToken);

        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<CloudflareApiResponse<CloudflareTunnelInfo>>(JsonOptions, cancellationToken);

        return result?.Result ?? throw new InvalidOperationException("Failed to create tunnel: no result returned");
    }

    /// <summary>
    /// Gets the tunnel token for connecting cloudflared to the tunnel.
    /// </summary>
    public async Task<string> GetTunnelTokenAsync(string tunnelId, CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.GetAsync(
            $"/client/v4/accounts/{_accountId}/cfd_tunnel/{tunnelId}/token",
            cancellationToken);

        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<CloudflareApiResponse<string>>(JsonOptions, cancellationToken);

        return result?.Result ?? throw new InvalidOperationException("Failed to get tunnel token: no result returned");
    }

    /// <summary>
    /// Gets the current tunnel configuration.
    /// </summary>
    public async Task<TunnelConfiguration?> GetTunnelConfigurationAsync(string tunnelId, CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.GetAsync(
            $"/client/v4/accounts/{_accountId}/cfd_tunnel/{tunnelId}/configurations",
            cancellationToken);

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<CloudflareApiResponse<TunnelConfigurationWrapper>>(JsonOptions, cancellationToken);

        return result?.Result?.Config;
    }

    /// <summary>
    /// Updates the tunnel configuration with new ingress rules.
    /// </summary>
    public async Task UpdateTunnelConfigurationAsync(string tunnelId, TunnelConfiguration config, CancellationToken cancellationToken = default)
    {
        var wrapper = new TunnelConfigurationWrapper(config);

        var response = await _httpClient.PutAsJsonAsync(
            $"/client/v4/accounts/{_accountId}/cfd_tunnel/{tunnelId}/configurations",
            wrapper,
            JsonOptions,
            cancellationToken);

        response.EnsureSuccessStatusCode();
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _httpClient.Dispose();
            _disposed = true;
        }
    }
}

#region API DTOs

internal sealed record CloudflareApiResponse<T>(
    bool Success,
    T? Result,
    CloudflareApiError[]? Errors,
    CloudflareApiMessage[]? Messages);

internal sealed record CloudflareApiError(int Code, string Message);

internal sealed record CloudflareApiMessage(int Code, string Message);

internal sealed record CreateTunnelRequest(string Name, string TunnelSecret);

/// <summary>
/// Represents a Cloudflare tunnel.
/// </summary>
public sealed record CloudflareTunnelInfo(
    string Id,
    string Name,
    string Status,
    string? CreatedAt,
    string? DeletedAt);

internal sealed record TunnelConfigurationWrapper(TunnelConfiguration Config);

/// <summary>
/// Represents the tunnel configuration containing ingress rules.
/// </summary>
public sealed class TunnelConfiguration
{
    [JsonPropertyName("ingress")]
    public List<IngressRule> Ingress { get; set; } = [];
}

/// <summary>
/// Represents an ingress rule that routes traffic to a service.
/// </summary>
public sealed class IngressRule
{
    /// <summary>
    /// The hostname to match (null for catch-all rule).
    /// </summary>
    [JsonPropertyName("hostname")]
    public string? Hostname { get; set; }

    /// <summary>
    /// The service URL to route to (e.g., "http://container:port" or "http_status:404").
    /// </summary>
    [JsonPropertyName("service")]
    public string Service { get; set; } = string.Empty;

    /// <summary>
    /// Optional path matching.
    /// </summary>
    [JsonPropertyName("path")]
    public string? Path { get; set; }
}

#endregion
