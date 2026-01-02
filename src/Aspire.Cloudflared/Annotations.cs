using Aspire.Hosting.ApplicationModel;

namespace Aspire.Cloudflared;

/// <summary>
/// Annotation that stores the API token parameter reference for a tunnel resource.
/// </summary>
internal sealed class CloudflareApiTokenAnnotation(ParameterResource apiToken, ParameterResource accountId) : IResourceAnnotation
{
    public ParameterResource ApiToken { get; } = apiToken;
    public ParameterResource AccountId { get; } = accountId;
}

/// <summary>
/// Annotation that stores the tunnel token value after it has been retrieved/created.
/// </summary>
internal sealed class TunnelTokenAnnotation(string token) : IResourceAnnotation
{
    public string Token { get; } = token;
}

/// <summary>
/// Annotation that stores the tunnel ID after creation.
/// </summary>
internal sealed class TunnelIdAnnotation(string tunnelId) : IResourceAnnotation
{
    public string TunnelId { get; } = tunnelId;
}

/// <summary>
/// Annotation that tracks pending route configurations for a tunnel.
/// </summary>
internal sealed class PendingRouteAnnotation(
    string hostname,
    EndpointReference targetEndpoint,
    IResource targetResource) : IResourceAnnotation
{
    public string Hostname { get; } = hostname;
    public EndpointReference TargetEndpoint { get; } = targetEndpoint;
    public IResource TargetResource { get; } = targetResource;
}

/// <summary>
/// Annotation that marks a route as configured in the tunnel.
/// </summary>
internal sealed class ConfiguredRouteAnnotation(string hostname, string service) : IResourceAnnotation
{
    public string Hostname { get; } = hostname;
    public string Service { get; } = service;
}
