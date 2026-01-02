using Aspire.Hosting.ApplicationModel;

namespace Aspire.Cloudflared;

/// <summary>
/// Represents a logical Cloudflare tunnel configuration.
/// This resource tracks the tunnel name and associated routes, and is linked to a
/// <see cref="CloudflaredResource"/> container that runs the actual tunnel connection.
/// </summary>
public sealed class CloudflareTunnelResource([ResourceName] string name) : Resource(name)
{
    /// <summary>
    /// Gets or sets the Cloudflare tunnel ID (UUID) after creation/discovery.
    /// </summary>
    public string? TunnelId { get; internal set; }

    /// <summary>
    /// Gets or sets the tunnel token used to authenticate the cloudflared connection.
    /// This is populated at runtime in run mode.
    /// </summary>
    public string? TunnelToken { get; internal set; }

    /// <summary>
    /// Gets the cloudflared container resource that runs this tunnel.
    /// </summary>
    public CloudflaredResource? Container { get; internal set; }
}
