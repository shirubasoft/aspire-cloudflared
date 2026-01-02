using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Eventing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aspire.Cloudflared;

public static class CloudflaredExtensions
{
    /// <summary>
    /// Adds a Cloudflare tunnel resource to the application.
    /// By default, requires a tunnel token parameter. Use <see cref="WithAutoCreate"/> to automatically
    /// create the tunnel via the Cloudflare API.
    /// </summary>
    /// <param name="builder">The distributed application builder.</param>
    /// <param name="name">The name of the tunnel resource.</param>
    /// <param name="metricsPort">Optional port for the metrics endpoint.</param>
    /// <returns>A resource builder for the tunnel.</returns>
    public static IResourceBuilder<CloudflareTunnelResource> AddCloudflareTunnel(
        this IDistributedApplicationBuilder builder,
        [ResourceName] string name,
        int? metricsPort = null)
    {
        CloudflaredResource resource = new(name);

#pragma warning disable ASPIREINTERACTION001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
        var apiTokenParameter = builder
            .AddParameter($"{name}-api-token", secret: true)
            .WithDescription("The Cloudflare API token with tunnel permissions.")
            .WithCustomInput(p => new()
            {
                InputType = InputType.Text,
                Value = null,
                Name = p.Name,
                Placeholder = "Enter your Cloudflare API token",
                Description = p.Description,
                Required = true
            });

        var accountIdParameter = builder
            .AddParameter($"{name}-account-id", secret: false)
            .WithDescription("The Cloudflare account ID.")
            .WithCustomInput(p => new()
            {
                InputType = InputType.Text,
                Value = null,
                Name = p.Name,
                Placeholder = "Enter your Cloudflare account ID",
                Description = p.Description,
                Required = true
            });
#pragma warning restore ASPIREINTERACTION001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

        // Store the token parameter reference on the resource for later use
        resource.Annotations.Add(new TunnelTokenParameterAnnotation(tokenParameter.Resource));

        return builder.AddResource(resource)
            .WithImage(CloudflaredContainerImageTags.Image, CloudflaredContainerImageTags.Tag)
            .WithImageRegistry(CloudflaredContainerImageTags.Registry)
            .WithHttpEndpoint(
                port: metricsPort,
                targetPort: CloudflaredResource.DefaultMetricsPort,
                name: CloudflaredResource.MetricsEndpointName
            )
            .WithHttpHealthCheck(
                "/diag/tunnel",
                endpointName: CloudflaredResource.MetricsEndpointName
            )
            .WithArgs(context => GetTunnelArgsAsync(context, resource));
    }

    /// <summary>
    /// Exposes a resource's endpoint through a Cloudflare tunnel with the specified hostname.
    /// Creates a <see cref="PublishedRouteResource"/> as a child of the tunnel and adds
    /// a public endpoint to the target resource for visibility in the dashboard.
    /// </summary>
    /// <typeparam name="T">The type of resource with endpoints.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="tunnel">The Cloudflare tunnel to route through.</param>
    /// <param name="hostname">The public hostname for this route (e.g., "api.example.com").</param>
    /// <param name="endpointName">The name of the endpoint to expose. Defaults to "http".</param>
    /// <returns>The resource builder for chaining.</returns>
    public static IResourceBuilder<T> WithCloudflareTunnel<T>(
        this IResourceBuilder<T> builder,
        IResourceBuilder<CloudflareTunnelResource> tunnel,
        string hostname,
        string endpointName = "http")
        where T : IResourceWithEndpoints
    {
        return builder;
    }

    private static async Task GetTunnelArgsAsync(CommandLineArgsCallbackContext context, CloudflareTunnelResource resource)
    {
        // Add base args
        context.Args.Add("tunnel");
        context.Args.Add("--no-autoupdate");
        context.Args.Add("--metrics");
        context.Args.Add($"0.0.0.0:{CloudflaredResource.DefaultMetricsPort}");
        context.Args.Add("run");
        context.Args.Add("--token");
    }
}
