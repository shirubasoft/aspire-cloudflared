using Aspire.Hosting.ApplicationModel;

namespace Aspire.Cloudflared;

public sealed class CloudflaredResource([ResourceName] string name) : ContainerResource(name)
{
    public const string MetricsEndpointName = "metrics";

    public const int DefaultMetricsPort = 60123;

    public EndpointReference MetricsEndpoint => field ??= new(this, MetricsEndpointName);
}
