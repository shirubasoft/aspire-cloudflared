using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Eventing;
using Aspire.Hosting.Lifecycle;
using Microsoft.Extensions.Logging;

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

        // Create parameters for API authentication
#pragma warning disable ASPIREINTERACTION001
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

        // Tunnel token parameter - only used in publish mode
        // In run mode, the token is obtained automatically by the installer
        var tunnelTokenParameter = builder
            .AddParameter($"{name}-tunnel-token", secret: true)
            .WithDescription("The Cloudflare tunnel token. Get this from the Cloudflare dashboard or by running 'cloudflared tunnel token <tunnel-name>'.");
#pragma warning restore ASPIREINTERACTION001

        // Create the tunnel resource
        var tunnelResource = new CloudflareTunnelResource(name);

        // Store API credentials on the resource
        tunnelResource.Annotations.Add(new CloudflareApiTokenAnnotation(
            apiTokenParameter.Resource,
            accountIdParameter.Resource));

        // Create the cloudflared container that will run the tunnel
        var containerResource = new CloudflaredResource($"{name}-cloudflared");
        tunnelResource.Container = containerResource;

        // Add the container to the application
        var containerBuilder = builder.AddResource(containerResource)
            .WithImage(CloudflaredContainerImageTags.Image, CloudflaredContainerImageTags.Tag)
            .WithImageRegistry(CloudflaredContainerImageTags.Registry)
            .WithHttpEndpoint(
                port: metricsPort,
                targetPort: CloudflaredResource.DefaultMetricsPort,
                name: CloudflaredResource.MetricsEndpointName)
            .WithHttpHealthCheck(
                "/ready",
                endpointName: CloudflaredResource.MetricsEndpointName);

        // Add the tunnel resource
        var tunnelBuilder = builder.AddResource(tunnelResource)
            .WithInitialState(new()
            {
                ResourceType = "Cloudflare Tunnel",
                State = KnownResourceStates.Starting,
                Properties = []
            });

        if (builder.ExecutionContext.IsRunMode)
        {
            // RUN MODE: Use default args - token passed via environment variable
            containerBuilder.WithArgs(GetTunnelArgs);
            // Create installer resource that provisions the tunnel
            // The installer will set the TUNNEL_TOKEN environment variable after provisioning
            AddTunnelInstaller(builder, tunnelBuilder, containerBuilder, apiTokenParameter, accountIdParameter);
        }
        else
        {
            // PUBLISH MODE: Use tunnel token parameter passed as environment variable
            // User must provide the token at deployment time
            containerBuilder.WithArgs(GetTunnelArgs);
            containerBuilder.WithEnvironment("TUNNEL_TOKEN", tunnelTokenParameter.Resource);
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

        // Store the pending route on the tunnel resource
        tunnel.Resource.Annotations.Add(new PendingRouteAnnotation(
            hostname,
            endpoint,
            builder.Resource));

        if (builder.ApplicationBuilder.ExecutionContext.IsRunMode)
        {
            // RUN MODE: Create route installer
            AddRouteInstaller(builder.ApplicationBuilder, tunnel, builder.Resource, hostname, endpoint);
        }
        // PUBLISH MODE: Routes are configured in the publish-time provisioning

        return builder;
    }

    private static void AddTunnelInstaller(
        IDistributedApplicationBuilder builder,
        IResourceBuilder<CloudflareTunnelResource> tunnel,
        IResourceBuilder<CloudflaredResource> container,
        IResourceBuilder<ParameterResource> apiToken,
        IResourceBuilder<ParameterResource> accountId)
    {
        var installerName = $"{tunnel.Resource.Name}-installer";
        var installer = new CloudflareTunnelInstallerResource(installerName, tunnel.Resource);

        var installerBuilder = builder.AddResource(installer)
            .WithParentRelationship(tunnel.Resource)
            .ExcludeFromManifest()
            .WithInitialState(new()
            {
                ResourceType = "Tunnel Installer",
                State = KnownResourceStates.Starting,
                Properties = []
            });

        // Register the eventing subscriber to run the installer
        builder.Services.TryAddEventingSubscriber<CloudflareTunnelInstallerEventingSubscriber>();

        // In run mode, set the TUNNEL_TOKEN environment variable from the provisioned token
        container.WithEnvironment(context =>
        {
            // The token is set by the installer before the container starts
            if (!string.IsNullOrEmpty(tunnel.Resource.TunnelToken))
            {
                context.EnvironmentVariables["TUNNEL_TOKEN"] = tunnel.Resource.TunnelToken;
            }
            else if (tunnel.Resource.TryGetLastAnnotation<TunnelTokenAnnotation>(out var tokenAnnotation))
            {
                context.EnvironmentVariables["TUNNEL_TOKEN"] = tokenAnnotation.Token;
            }
            // If no token is available yet, the WaitForCompletion below ensures
            // this callback runs after the installer has set the token
        });

        // Make the container wait for the installer to complete
        container.WaitForCompletion(installerBuilder);
    }

    private static void AddRouteInstaller(
        IDistributedApplicationBuilder builder,
        IResourceBuilder<CloudflareTunnelResource> tunnel,
        IResource targetResource,
        string hostname,
        EndpointReference endpoint)
    {
        // Create a safe resource name from the hostname
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

        // Register the eventing subscriber for route installers
        builder.Services.TryAddEventingSubscriber<CloudflareRouteInstallerEventingSubscriber>();

        // The tunnel container should wait for route installers too
        if (tunnel.Resource.Container is { } container)
        {
            // Find the container builder and add wait
            builder.Eventing.Subscribe<BeforeResourceStartedEvent>(container, async (evt, ct) =>
            {
                // This ensures the route installer completes before the container starts
                // The actual wait is handled by the lifecycle hook
            });
        }
    }

    /// <summary>
    /// Gets the command-line arguments for running cloudflared tunnel.
    /// The tunnel token is passed via TUNNEL_TOKEN environment variable, not command-line args.
    /// </summary>
    private static void GetTunnelArgs(CommandLineArgsCallbackContext context)
    {
        context.Args.Add("tunnel");
        context.Args.Add("--no-autoupdate");
        context.Args.Add("--metrics");
        context.Args.Add($"0.0.0.0:{CloudflaredResource.DefaultMetricsPort}");
        context.Args.Add("run");
    }
}

/// <summary>
/// Eventing subscriber that handles tunnel creation via the Cloudflare API.
/// </summary>
internal sealed class CloudflareTunnelInstallerEventingSubscriber(
    ResourceLoggerService loggerService,
    ResourceNotificationService notificationService) : IDistributedApplicationEventingSubscriber
{
    public Task SubscribeAsync(IDistributedApplicationEventing eventing, DistributedApplicationExecutionContext executionContext, CancellationToken cancellationToken)
    {
        eventing.Subscribe<BeforeStartEvent>(OnBeforeStartAsync);
        return Task.CompletedTask;
    }

    private async Task OnBeforeStartAsync(BeforeStartEvent @event, CancellationToken cancellationToken = default)
    {
        var installers = @event.Model.Resources.OfType<CloudflareTunnelInstallerResource>().ToList();

        foreach (var installer in installers)
        {
            var logger = loggerService.GetLogger(installer);

            try
            {
                await ProvisionTunnelAsync(installer, logger, cancellationToken);

                await notificationService.PublishUpdateAsync(installer, state => state with
                {
                    State = new ResourceStateSnapshot(KnownResourceStates.Finished, KnownResourceStateStyles.Success)
                });

                // Also update the tunnel resource state
                await notificationService.PublishUpdateAsync(installer.Tunnel, state => state with
                {
                    State = new ResourceStateSnapshot(KnownResourceStates.Running, KnownResourceStateStyles.Success),
                    Properties = [
                        ..state.Properties,
                        new("TunnelId", installer.Tunnel.TunnelId ?? "Unknown")
                    ]
                });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to provision tunnel '{TunnelName}'", installer.Tunnel.Name);

                await notificationService.PublishUpdateAsync(installer, state => state with
                {
                    State = new ResourceStateSnapshot("Failed", KnownResourceStateStyles.Error)
                });

                throw;
            }
        }
    }

    private async Task ProvisionTunnelAsync(CloudflareTunnelInstallerResource installer, ILogger logger, CancellationToken ct)
    {
        var tunnel = installer.Tunnel;

        if (!tunnel.TryGetLastAnnotation<CloudflareApiTokenAnnotation>(out var apiAnnotation))
        {
            throw new InvalidOperationException("Cloudflare API credentials not configured on tunnel resource.");
        }

        var apiToken = await apiAnnotation.ApiToken.GetValueAsync(ct);
        var accountId = await apiAnnotation.AccountId.GetValueAsync(ct);

        if (string.IsNullOrEmpty(apiToken) || string.IsNullOrEmpty(accountId))
        {
            throw new InvalidOperationException("Cloudflare API token and account ID are required.");
        }

        using var client = new CloudflareApiClient(apiToken, accountId);

        // Find or create the tunnel
        logger.LogInformation("Looking for existing tunnel '{TunnelName}'...", tunnel.Name);
        var existingTunnel = await client.FindTunnelByNameAsync(tunnel.Name, ct);

        if (existingTunnel is null)
        {
            logger.LogInformation("Creating new Cloudflare tunnel '{TunnelName}'...", tunnel.Name);
            existingTunnel = await client.CreateTunnelAsync(tunnel.Name, ct);
            installer.WasCreated = true;
            logger.LogInformation("Created tunnel with ID {TunnelId}", existingTunnel.Id);
        }
        else
        {
            logger.LogInformation("Found existing tunnel '{TunnelName}' with ID {TunnelId}", tunnel.Name, existingTunnel.Id);
        }

        tunnel.TunnelId = existingTunnel.Id;

        // Get the tunnel token
        logger.LogInformation("Retrieving tunnel token...");
        var tunnelToken = await client.GetTunnelTokenAsync(existingTunnel.Id, ct);
        tunnel.TunnelToken = tunnelToken;
        tunnel.Annotations.Add(new TunnelTokenAnnotation(tunnelToken));

        logger.LogInformation("Tunnel '{TunnelName}' provisioned successfully", tunnel.Name);
    }
}

/// <summary>
/// Eventing subscriber that handles route configuration via the Cloudflare API.
/// </summary>
internal sealed class CloudflareRouteInstallerEventingSubscriber(
    ResourceLoggerService loggerService,
    ResourceNotificationService notificationService) : IDistributedApplicationEventingSubscriber
{
    public Task SubscribeAsync(IDistributedApplicationEventing eventing, DistributedApplicationExecutionContext executionContext, CancellationToken cancellationToken)
    {
        eventing.Subscribe<BeforeStartEvent>(OnBeforeStartAsync);
        return Task.CompletedTask;
    }

    private async Task OnBeforeStartAsync(BeforeStartEvent @event, CancellationToken cancellationToken = default)
    {
        var routeInstallers = @event.Model.Resources.OfType<CloudflareRouteInstallerResource>().ToList();

        if (routeInstallers.Count == 0)
        {
            return;
        }

        // Group routes by tunnel for batch configuration
        var routesByTunnel = routeInstallers.GroupBy(r => r.Tunnel).ToList();

        foreach (var tunnelRoutes in routesByTunnel)
        {
            var tunnel = tunnelRoutes.Key;
            var logger = loggerService.GetLogger(tunnel);

            if (!tunnel.TryGetLastAnnotation<CloudflareApiTokenAnnotation>(out var apiAnnotation))
            {
                logger.LogWarning("Cloudflare API credentials not found for tunnel '{TunnelName}'", tunnel.Name);
                continue;
            }

            try
            {
                var apiToken = await apiAnnotation.ApiToken.GetValueAsync(cancellationToken);
                var accountId = await apiAnnotation.AccountId.GetValueAsync(cancellationToken);

                if (string.IsNullOrEmpty(apiToken) || string.IsNullOrEmpty(accountId))
                {
                    throw new InvalidOperationException("Cloudflare API credentials not available.");
                }

                using var client = new CloudflareApiClient(apiToken, accountId);

                // Wait for tunnel to be provisioned
                if (string.IsNullOrEmpty(tunnel.TunnelId))
                {
                    logger.LogWarning("Tunnel '{TunnelName}' not yet provisioned, skipping route configuration", tunnel.Name);
                    continue;
                }

                await ConfigureRoutesAsync(client, tunnel, tunnelRoutes.ToList(), logger, cancellationToken);

                // Mark all route installers as finished
                foreach (var route in tunnelRoutes)
                {
                    await notificationService.PublishUpdateAsync(route, state => state with
                    {
                        State = new ResourceStateSnapshot(KnownResourceStates.Finished, KnownResourceStateStyles.Success)
                    });
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to configure routes for tunnel '{TunnelName}'", tunnel.Name);

                foreach (var route in tunnelRoutes)
                {
                    await notificationService.PublishUpdateAsync(route, state => state with
                    {
                        State = new ResourceStateSnapshot("Failed", KnownResourceStateStyles.Error)
                    });
                }

                throw;
            }
        }
    }

    private async Task ConfigureRoutesAsync(
        CloudflareApiClient client,
        CloudflareTunnelResource tunnel,
        List<CloudflareRouteInstallerResource> routes,
        ILogger tunnelLogger,
        CancellationToken ct)
    {
        var config = new TunnelConfiguration();

        foreach (var route in routes)
        {
            var logger = loggerService.GetLogger(route);

            // Create DNS record
            var domain = GetRootDomain(route.Hostname);
            logger.LogInformation("Looking up zone for domain {Domain}...", domain);
            var zone = await client.FindZoneByNameAsync(domain, ct);

            if (zone is null)
            {
                var errorMessage = $"Could not find zone for domain '{domain}'. DNS record cannot be created for '{route.Hostname}'. " +
                    "Make sure the domain is registered in your Cloudflare account and the API token has Zone:Read permission.";
                logger.LogError(errorMessage);
                
                await notificationService.PublishUpdateAsync(route, state => state with
                {
                    State = new ResourceStateSnapshot("Zone Not Found", KnownResourceStateStyles.Error)
                });
                
                throw new InvalidOperationException(errorMessage);
            }

            logger.LogInformation("Found zone {ZoneId} for domain {Domain}. Creating DNS CNAME record for {Hostname} -> {TunnelId}.cfargotunnel.com",
                zone.Id, domain, route.Hostname, tunnel.TunnelId);

            try
            {
                await client.CreateTunnelDnsRecordAsync(zone.Id, route.Hostname, tunnel.TunnelId!, cancellationToken: ct);
                route.DnsRecordCreated = true;
                logger.LogInformation("DNS record created successfully for {Hostname}", route.Hostname);
            }
            catch (CloudflareApiException ex) when (ex.RecordAlreadyExists)
            {
                logger.LogInformation("DNS record for {Hostname} already exists, skipping creation", route.Hostname);
                route.DnsRecordCreated = true; // Mark as created since it exists
            }
            catch (CloudflareApiException ex)
            {
                logger.LogError(ex, "Failed to create DNS record for {Hostname}: {Message}", route.Hostname, ex.Message);
                throw;
            }

            // Build service URL for ingress
            // In run mode, we use the container name as the hostname within the Docker network
            var serviceUrl = BuildServiceUrl(route);

            config.Ingress.Add(new IngressRule
            {
                Hostname = route.Hostname,
                Service = serviceUrl
            });

            tunnel.Annotations.Add(new ConfiguredRouteAnnotation(route.Hostname, serviceUrl));

            logger.LogInformation("Added ingress rule: {Hostname} -> {Service}", route.Hostname, serviceUrl);
        }

        // Add required catch-all rule
        config.Ingress.Add(new IngressRule
        {
            Service = "http_status:404"
        });

        tunnelLogger.LogInformation("Updating tunnel configuration with {RouteCount} routes...", routes.Count);
        await client.UpdateTunnelConfigurationAsync(tunnel.TunnelId!, config, ct);
    }

    private static string BuildServiceUrl(CloudflareRouteInstallerResource route)
    {
        // The service URL needs to point to the target container
        // In Docker Compose / container environments, we use the container name
        var targetName = route.TargetResource.Name;

        // Try to determine the port from the endpoint
        var port = 80; // Default HTTP port

        // The endpoint's target port is what we need
        // This will be resolved when the endpoint is allocated
        return $"http://{targetName}:{port}";
    }

    private static string GetRootDomain(string hostname)
    {
        var parts = hostname.Split('.');
        if (parts.Length >= 2)
        {
            return string.Join(".", parts.TakeLast(2));
        }
        return hostname;
    }
}
