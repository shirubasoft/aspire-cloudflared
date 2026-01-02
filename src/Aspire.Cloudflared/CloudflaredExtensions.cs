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
        CloudflareTunnelResource resource = new(name);

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

        // Store the token parameter reference on the resource for later use
        resource.Annotations.Add(new TunnelTokenParameterAnnotation(tokenParameter.Resource));

        return builder.AddResource(resource)
            .WithImage(CloudflaredContainerImageTags.Image, CloudflaredContainerImageTags.Tag)
            .WithImageRegistry(CloudflaredContainerImageTags.Registry)
            .WithHttpEndpoint(
                port: metricsPort,
                targetPort: CloudflareTunnelResource.DefaultMetricsPort,
                name: CloudflareTunnelResource.MetricsEndpointName
            )
            .WithHttpHealthCheck(
                "/diag/tunnel",
                endpointName: CloudflareTunnelResource.MetricsEndpointName
            )
            .WithArgs(context => GetTunnelArgsAsync(context, resource));
    }

    /// <summary>
    /// Enables automatic tunnel creation and configuration via the Cloudflare API.
    /// When enabled, the tunnel will be created if it doesn't exist, and ingress routes
    /// will be configured based on <see cref="WithCloudflareTunnel{T}"/> calls.
    /// </summary>
    /// <param name="builder">The tunnel resource builder.</param>
    /// <returns>The resource builder for chaining.</returns>
    public static IResourceBuilder<CloudflareTunnelResource> WithAutoCreate(
        this IResourceBuilder<CloudflareTunnelResource> builder)
    {
        var appBuilder = builder.ApplicationBuilder;
        var tunnelResource = builder.Resource;

#pragma warning disable ASPIREINTERACTION001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
        var apiTokenParameter = appBuilder
            .AddParameter($"{tunnelResource.Name}-api-token", secret: true)
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

        var accountIdParameter = appBuilder
            .AddParameter($"{tunnelResource.Name}-account-id", secret: false)
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

        // Create the initializer resource
        var initializerName = $"{tunnelResource.Name}-initializer";
        var initializerResource = new CloudflareTunnelInitializerResource(initializerName, tunnelResource)
        {
            ApiTokenParameter = apiTokenParameter.Resource,
            AccountIdParameter = accountIdParameter.Resource
        };

        // Add the initializer resource and set up its lifecycle
        var initializerBuilder = appBuilder.AddResource(initializerResource);

        // Subscribe to the initializer's initialization event to provision the tunnel
        initializerBuilder.OnInitializeResource(async (resource, evt, ct) =>
        {
            var logger = evt.Services.GetRequiredService<ILogger<CloudflareTunnelInitializerResource>>();
            var notificationService = evt.Services.GetRequiredService<ResourceNotificationService>();

            await notificationService.PublishUpdateAsync(resource, state => state with
            {
                State = new ResourceStateSnapshot("Initializing", KnownResourceStateStyles.Info)
            });

            try
            {
                await ProvisionTunnelAsync(resource, appBuilder.Resources, logger, notificationService, ct);

                await notificationService.PublishUpdateAsync(resource, state => state with
                {
                    State = new ResourceStateSnapshot("Running", KnownResourceStateStyles.Success)
                });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to provision tunnel");
                await notificationService.PublishUpdateAsync(resource, state => state with
                {
                    State = new ResourceStateSnapshot("Failed", KnownResourceStateStyles.Error)
                });
                throw;
            }
        });

        // Store the initializer reference on the tunnel resource
        tunnelResource.Annotations.Add(new TunnelInitializerAnnotation(initializerResource));

        // Make the tunnel container wait for the initializer
        //builder.WaitFor(initializerBuilder);

        return builder;
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
        var endpoint = builder.GetEndpoint(endpointName);
        var sanitizedHostname = hostname.Replace(".", "-").Replace(":", "-");
        var routeName = $"{builder.Resource.Name}-{sanitizedHostname}-route";

        // Create the published route resource (child of tunnel)
        var routeResource = new PublishedRouteResource(
            routeName,
            hostname,
            endpoint,
            tunnel.Resource);

        // Add the route resource to the application model
        builder.ApplicationBuilder.AddResource(routeResource);

        // Add a URL annotation to show the public Cloudflare URL in the dashboard
        builder.WithAnnotation(new CloudflarePublicUrlAnnotation(hostname));

        return builder;
    }

    private static async Task GetTunnelArgsAsync(CommandLineArgsCallbackContext context, CloudflareTunnelResource resource)
    {
        // Add base args
        context.Args.Add("tunnel");
        context.Args.Add("--no-autoupdate");
        context.Args.Add("--metrics");
        context.Args.Add($"0.0.0.0:{CloudflareTunnelResource.DefaultMetricsPort}");
        context.Args.Add("run");
        context.Args.Add("--token");

        // Check if we have an initializer with a token (auto-create mode)
        if (resource.TryGetLastAnnotation<TunnelInitializerAnnotation>(out var initializerAnnotation) &&
            !string.IsNullOrEmpty(initializerAnnotation.Initializer.TunnelToken))
        {
            // Use the token from the initializer
            context.Args.Add(initializerAnnotation.Initializer.TunnelToken);
        }
        else if (resource.TryGetLastAnnotation<TunnelTokenParameterAnnotation>(out var tokenAnnotation))
        {
            // Use the manual token parameter
            var tokenValue = await tokenAnnotation.TokenParameter.GetValueAsync(context.CancellationToken);
            context.Args.Add(tokenValue ?? string.Empty);
        }
    }

    private static async Task ProvisionTunnelAsync(
        CloudflareTunnelInitializerResource initializer,
        IResourceCollection resources,
        ILogger logger,
        ResourceNotificationService notificationService,
        CancellationToken cancellationToken)
    {
        var tunnel = initializer.Parent;

        // Get the routes for this tunnel
        var tunnelRoutes = resources
            .OfType<PublishedRouteResource>()
            .Where(r => r.Parent == tunnel)
            .ToList();

        logger.LogInformation("Provisioning Cloudflare tunnel '{TunnelName}' with {RouteCount} routes",
            tunnel.Name, tunnelRoutes.Count);

        // Get API credentials from parameters
        if (initializer.ApiTokenParameter is null || initializer.AccountIdParameter is null)
        {
            logger.LogWarning("API token or account ID parameter not configured for tunnel '{TunnelName}'",
                tunnel.Name);
            return;
        }

        var apiToken = await initializer.ApiTokenParameter.GetValueAsync(cancellationToken);
        var accountId = await initializer.AccountIdParameter.GetValueAsync(cancellationToken);

        if (string.IsNullOrEmpty(apiToken) || string.IsNullOrEmpty(accountId))
        {
            logger.LogWarning("API token or account ID not provided for tunnel '{TunnelName}'. Skipping auto-creation.",
                tunnel.Name);
            return;
        }

        using var apiClient = new CloudflareApiClient(apiToken, accountId);

        // Find or create the tunnel
        var tunnelInfo = await FindOrCreateTunnelAsync(apiClient, tunnel.Name, logger, cancellationToken);
        initializer.TunnelId = tunnelInfo.Id;

        logger.LogInformation("Tunnel '{TunnelName}' has ID: {TunnelId}", tunnel.Name, tunnelInfo.Id);

        // Get the tunnel token
        var tunnelToken = await apiClient.GetTunnelTokenAsync(tunnelInfo.Id, cancellationToken);
        initializer.TunnelToken = tunnelToken;

        logger.LogInformation("Retrieved tunnel token for '{TunnelName}'", tunnel.Name);

        // Update the tunnel configuration with ingress rules
        if (tunnelRoutes.Count > 0)
        {
            await UpdateTunnelConfigurationAsync(apiClient, tunnelInfo.Id, tunnelRoutes, logger, cancellationToken);

            // Update route resource states
            foreach (var route in tunnelRoutes)
            {
                await notificationService.PublishUpdateAsync(route, state => state with
                {
                    State = new ResourceStateSnapshot("Configured", KnownResourceStateStyles.Success)
                });
            }
        }
    }

    private static async Task<CloudflareTunnelInfo> FindOrCreateTunnelAsync(
        CloudflareApiClient apiClient,
        string tunnelName,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        // Try to find existing tunnel
        var existingTunnel = await apiClient.FindTunnelByNameAsync(tunnelName, cancellationToken);

        if (existingTunnel != null)
        {
            logger.LogInformation("Found existing tunnel '{TunnelName}' with ID: {TunnelId}",
                tunnelName, existingTunnel.Id);
            return existingTunnel;
        }

        // Create new tunnel
        logger.LogInformation("Creating new tunnel '{TunnelName}'", tunnelName);
        var newTunnel = await apiClient.CreateTunnelAsync(tunnelName, cancellationToken);
        logger.LogInformation("Created tunnel '{TunnelName}' with ID: {TunnelId}",
            tunnelName, newTunnel.Id);

        return newTunnel;
    }

    private static async Task UpdateTunnelConfigurationAsync(
        CloudflareApiClient apiClient,
        string tunnelId,
        List<PublishedRouteResource> routes,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        logger.LogInformation("Updating tunnel configuration with {RouteCount} ingress rules", routes.Count);

        var config = new TunnelConfiguration();

        foreach (var route in routes)
        {
            // Build the service URL using container name and target port
            var serviceUrl = await BuildServiceUrlAsync(route.TargetEndpoint, cancellationToken);

            config.Ingress.Add(new IngressRule
            {
                Hostname = route.Hostname,
                Service = serviceUrl
            });

            logger.LogInformation("Added ingress rule: {Hostname} -> {ServiceUrl}",
                route.Hostname, serviceUrl);
        }

        // Add the catch-all rule (required by Cloudflare)
        config.Ingress.Add(new IngressRule
        {
            Service = "http_status:404"
        });

        await apiClient.UpdateTunnelConfigurationAsync(tunnelId, config, cancellationToken);

        logger.LogInformation("Tunnel configuration updated successfully");
    }

    private static async Task<string> BuildServiceUrlAsync(EndpointReference endpoint, CancellationToken cancellationToken)
    {
        // Get the resource name (container name) and target port
        var resourceName = endpoint.Resource.Name;

        // Try to get the endpoint annotation to find the target port
        if (endpoint.Resource.TryGetEndpoints(out var endpoints))
        {
            var endpointAnnotation = endpoints.FirstOrDefault(e => e.Name == endpoint.EndpointName);
            if (endpointAnnotation != null)
            {
                var targetPort = endpointAnnotation.TargetPort ?? endpointAnnotation.Port;
                var scheme = endpointAnnotation.UriScheme ?? "http";

                return $"{scheme}://{resourceName}:{targetPort}";
            }
        }

        // Fallback: use the endpoint expression
        var expression = endpoint.Property(EndpointProperty.HostAndPort);
        var hostAndPort = await expression.GetValueAsync(cancellationToken);

        return $"http://{hostAndPort}";
    }
}

/// <summary>
/// Internal annotation to store the tunnel token parameter reference.
/// </summary>
internal sealed class TunnelTokenParameterAnnotation(ParameterResource tokenParameter) : IResourceAnnotation
{
    public ParameterResource TokenParameter { get; } = tokenParameter;
}

/// <summary>
/// Internal annotation to store the initializer resource reference.
/// </summary>
internal sealed class TunnelInitializerAnnotation(CloudflareTunnelInitializerResource initializer) : IResourceAnnotation
{
    public CloudflareTunnelInitializerResource Initializer { get; } = initializer;
}

/// <summary>
/// Annotation that stores the public Cloudflare URL for a resource.
/// </summary>
public sealed class CloudflarePublicUrlAnnotation(string hostname) : IResourceAnnotation
{
    /// <summary>
    /// Gets the public hostname.
    /// </summary>
    public string Hostname { get; } = hostname;

    /// <summary>
    /// Gets the full public URL.
    /// </summary>
    public string Url => $"https://{Hostname}";
}
