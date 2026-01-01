using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Cloudflared;

public static class CloudflaredResourceExtensions
{
    public static IResourceBuilder<CloudflaredResource> AddCloudflared(this IDistributedApplicationBuilder builder, 
        [ResourceName] string name,
        int? metricsPort = null)
    {
        CloudflaredResource resource = new(name);

#pragma warning disable ASPIREINTERACTION001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
        var tokenParameter = builder
            .AddParameter($"{name}-tunnel-token", secret: true)
            .WithDescription("The Cloudflared tunnel token.")
            .WithCustomInput(p => new()
            {
                InputType = InputType.Text,
                Value = null,
                Name = p.Name,
                Placeholder = $"Enter value for {p.Name}",
                Description = p.Description,
                Required = true
            });
#pragma warning restore ASPIREINTERACTION001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

        return builder.AddResource(resource)
            .WithImage(CloudflaredContainerImageTags.Image, CloudflaredContainerImageTags.Tag)
            .WithImageRegistry(CloudflaredContainerImageTags.Registry)
            .WithHttpEndpoint(
                port: metricsPort,
                targetPort: CloudflaredResource.DefaultMetricsPort,
                name: CloudflaredResource.MetricsEndpointName
            )
            .WithArgs(
            [
                "tunnel",
                "--no-autoupdate",
                "--metrics",
                $"0.0.0.0:{CloudflaredResource.DefaultMetricsPort}",
                "run",
                "--token",
                ReferenceExpression.Create($"{tokenParameter}")
            ]);
    }
}
