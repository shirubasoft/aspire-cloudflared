using Aspire.Hosting.ApplicationModel;

namespace Aspire.Cloudflared;

/// <summary>
/// A resource that initializes a Cloudflare tunnel by calling the Cloudflare API.
/// This resource runs before the actual tunnel container and handles:
/// - Finding or creating the tunnel
/// - Retrieving the tunnel token
/// - Configuring ingress routes
/// </summary>
public sealed class CloudflareTunnelInitializerResource(
    [ResourceName] string name,
    CloudflareTunnelResource tunnel) : Resource(name), IResourceWithParent<CloudflareTunnelResource>
{
    /// <summary>
    /// Gets the parent tunnel resource.
    /// </summary>
    public CloudflareTunnelResource Parent { get; } = tunnel;

    /// <summary>
    /// Gets or sets the tunnel ID after it has been created or found.
    /// </summary>
    public string? TunnelId { get; set; }

    /// <summary>
    /// Gets or sets the tunnel token after retrieval.
    /// </summary>
    public string? TunnelToken { get; set; }

    /// <summary>
    /// Gets or sets the API token parameter resource.
    /// </summary>
    public ParameterResource? ApiTokenParameter { get; set; }

    /// <summary>
    /// Gets or sets the account ID parameter resource.
    /// </summary>
    public ParameterResource? AccountIdParameter { get; set; }
}
