using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aspire.Cloudflared;

public static class CloudflaredExtensions
{
    /// <summary>
    /// Adds a Cloudflare tunnel resource to the application with automatic tunnel creation.
    /// The tunnel will be created via the Cloudflare API if it doesn't exist.
    /// </summary>
    /// <param name="builder">The distributed application builder.</param>
    /// <param name="name">The name of the tunnel resource. This will also be used as the tunnel name in Cloudflare.</param>
    /// <param name="metricsPort">Optional port for the metrics endpoint.</param>
    /// <returns>A resource builder for the tunnel.</returns>
    /// <remarks>
    /// This method requires the following parameters to be configured:
    /// - <c>{name}-api-token</c>: A Cloudflare API token with tunnel permissions
    /// - <c>{name}-account-id</c>: Your Cloudflare account ID
    /// 
    /// In run mode, an installer resource will create/find the tunnel before starting.
    /// In publish mode, the tunnel is provisioned during the publish operation.
    /// </remarks>
    public static IResourceBuilder<CloudflareTunnelResource> AddCloudflareTunnel(
        this IDistributedApplicationBuilder builder,
        [ResourceName] string name,
        int? metricsPort = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

#pragma warning disable ASPIREINTERACTION001
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
#pragma warning restore ASPIREINTERACTION001

        // Create the tunnel resource
        var tunnelResource = new CloudflareTunnelResource(name);

        // Store API credentials on the resource
        tunnelResource.Annotations.Add(new CloudflareApiTokenAnnotation(
            apiTokenParameter.Resource,
            accountIdParameter.Resource));

        // Add the container to the application
        var tunnelBuilder = builder.AddResource(tunnelResource)
            .WithImage(CloudflaredContainerImageTags.Image, CloudflaredContainerImageTags.Tag)
            .WithImageRegistry(CloudflaredContainerImageTags.Registry)
            .WithHttpEndpoint(
                port: metricsPort,
                targetPort: CloudflaredResource.DefaultMetricsPort,
                name: CloudflaredResource.MetricsEndpointName)
            .WithHttpHealthCheck(
                "/ready",
                endpointName: CloudflaredResource.MetricsEndpointName)
            .WithArgs(
            [
                "tunnel",
                "--no-autoupdate",
                "--metrics",
                $"0.0.0.0:{CloudflaredResource.DefaultMetricsPort}",
                "run"
            ])
            .WithInitialState(new()
            {
                ResourceType = "Cloudflare Tunnel",
                State = KnownResourceStates.Starting,
                Properties = []
            });
;

        if (builder.ExecutionContext.IsRunMode)
        {
            // Create installer resource that provisions the tunnel
            var installerBuilder = AddTunnelInstaller(builder, tunnelBuilder);

            // In run mode, set the TUNNEL_TOKEN environment variable from the provisioned token
            tunnelBuilder.WithEnvironment(context =>
            {
                // The token is set by the installer before the container starts
                if (!string.IsNullOrEmpty(tunnelResource.TunnelToken))
                {
                    context.EnvironmentVariables["TUNNEL_TOKEN"] = tunnelResource.TunnelToken;
                }
                else 
                {
                    throw new InvalidOperationException("Cloudflare tunnel token not available yet.");
                }
            });

            tunnelBuilder.WaitForCompletion(installerBuilder);
        }
        else
        {
            var tunnelTokenParameter = builder
                .AddParameter($"{name}-tunnel-token", secret: true)
                .WithDescription("The Cloudflare tunnel token. Get this from the Cloudflare dashboard or by running 'cloudflared tunnel token <tunnel-name>'.");

            tunnelBuilder.WithEnvironment("TUNNEL_TOKEN", tunnelTokenParameter.Resource);
        }

        return tunnelBuilder;
    }

    /// <summary>
    /// Exposes a resource's endpoint through a Cloudflare tunnel with the specified hostname.
    /// Creates DNS record and configures tunnel ingress routing.
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
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(tunnel);
        ArgumentException.ThrowIfNullOrEmpty(hostname);

        var endpoint = builder.Resource.GetEndpoint(endpointName);

        if (builder.ApplicationBuilder.ExecutionContext.IsRunMode)
        {
            // RUN MODE: Create route installer
            AddRouteInstaller(builder.ApplicationBuilder, tunnel, builder.Resource, hostname, endpoint);
        }

        return builder;
    }

    private static IResourceBuilder<CloudflareTunnelInstallerResource> AddTunnelInstaller(
        IDistributedApplicationBuilder builder,
        IResourceBuilder<CloudflareTunnelResource> tunnel)
    {
        var installerName = $"{tunnel.Resource.Name}-installer";
        var installer = new CloudflareTunnelInstallerResource(installerName, tunnel.Resource);

        // Register the provisioner services
        builder.Services.TryAddSingleton<CloudflareTunnelProvisioner>();
        builder.Services.TryAddSingleton<CloudflareRouteProvisioner>();

        var installerBuilder = builder.AddResource(installer)
            .WithParentRelationship(tunnel.Resource)
            .ExcludeFromManifest()
            .WithInitialState(new()
            {
                ResourceType = "Tunnel Installer",
                State = KnownResourceStates.Starting,
                Properties = []
            });

        builder.Eventing.Subscribe<InitializeResourceEvent>(installer, async (@event, ct) =>
        {
            var services = @event.Services;
            var provisioner = services.GetRequiredService<CloudflareTunnelProvisioner>();
            await provisioner.ProvisionAsync(installer, ct);
        });

        return installerBuilder;
    }

    private static void AddRouteInstaller(
        IDistributedApplicationBuilder builder,
        IResourceBuilder<CloudflareTunnelResource> tunnel,
        IResource targetResource,
        string hostname,
        EndpointReference endpoint)
    {
        var safeName = hostname.Replace(".", "-").Replace(":", "-");
        var installerName = $"{tunnel.Resource.Name}-route-{safeName}";

        var installer = new CloudflareRouteInstallerResource(
            installerName,
            tunnel.Resource,
            hostname,
            endpoint,
            targetResource);

        var installerBuilder = builder.AddResource(installer)
            .WithParentRelationship(tunnel.Resource)
            .ExcludeFromManifest()
            .WithInitialState(new()
            {
                ResourceType = "Route Installer",
                State = KnownResourceStates.Starting,
                Properties = [new("Hostname", hostname)]
            });

        builder.Eventing.Subscribe<ResourceReadyEvent>(tunnel.Resource, async (@event, ct) =>
        {
            var services = @event.Services;

            var provisioner = services.GetRequiredService<CloudflareRouteProvisioner>();
            await provisioner.ConfigureRouteAsync(installer, ct);
        });
    }
}
