using Aspire.Hosting.ApplicationModel;

namespace Aspire.Cloudflared;

/// <summary>
/// Resource that handles configuring a route (DNS + ingress) for a Cloudflare tunnel.
/// This is an installer resource that configures the route before the tunnel container starts.
/// </summary>
/// <remarks>
/// This resource uses the Cloudflare API to:
/// 1. Create/update a DNS CNAME record pointing to the tunnel
/// 2. Update the tunnel's ingress configuration to route the hostname to the target service
/// 
/// This resource uses <see cref="Aspire.Hosting.ResourceBuilderExtensions.WithParentRelationship{T}"/>
/// to appear as a child of the tunnel in the dashboard.
/// </remarks>
public sealed class CloudflareRouteInstallerResource(
    [ResourceName] string name,
    CloudflareTunnelResource tunnel,
    string hostname,
    EndpointReference targetEndpoint,
    IResource targetResource) : Resource(name), IResourceWithWaitSupport
{
    /// <summary>
    /// Gets the tunnel resource that this route is being added to.
    /// </summary>
    public CloudflareTunnelResource Tunnel { get; } = tunnel;

    /// <summary>
    /// Gets the public hostname for this route (e.g., "api.example.com").
    /// </summary>
    public string Hostname { get; } = hostname;

    /// <summary>
    /// Gets the endpoint reference to the target service.
    /// </summary>
    public EndpointReference TargetEndpoint { get; } = targetEndpoint;

    /// <summary>
    /// Gets the target resource that will receive traffic.
    /// </summary>
    public IResource TargetResource { get; } = targetResource;

    /// <summary>
    /// Gets or sets whether the DNS record was created (vs updated).
    /// </summary>
    public bool DnsRecordCreated { get; internal set; }
}
