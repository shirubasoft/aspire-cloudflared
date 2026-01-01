using Aspire.Hosting.ApplicationModel;

namespace Aspire.Cloudflared;

/// <summary>
/// Represents a published route through a Cloudflare tunnel.
/// This resource is a child of <see cref="CloudflareTunnelResource"/> and tracks
/// a specific hostname-to-endpoint mapping.
/// </summary>
public sealed class PublishedRouteResource(
    [ResourceName] string name,
    string hostname,
    EndpointReference targetEndpoint,
    CloudflareTunnelResource tunnel)
    : Resource(name), IResourceWithParent<CloudflareTunnelResource>
{
    /// <summary>
    /// Gets the public hostname for this route (e.g., "api.example.com").
    /// </summary>
    public string Hostname { get; } = hostname;

    /// <summary>
    /// Gets the endpoint reference to the target service that will receive traffic.
    /// </summary>
    public EndpointReference TargetEndpoint { get; } = targetEndpoint;

    /// <summary>
    /// Gets the parent Cloudflare tunnel resource.
    /// </summary>
    public CloudflareTunnelResource Parent { get; } = tunnel;
}
