using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Logging;

namespace Aspire.Cloudflared;

/// <summary>
/// Handles the configuration of routes for Cloudflare tunnels via the Cloudflare API.
/// This class is responsible for creating DNS records and updating tunnel ingress configuration.
/// </summary>
internal sealed class CloudflareRouteProvisioner(
    ResourceNotificationService notificationService,
    ResourceLoggerService loggerService)
{
    /// <summary>
    /// Configures routes for all route installers associated with a tunnel.
    /// Creates DNS records and updates the tunnel's ingress configuration.
    /// </summary>
    public async Task ConfigureRoutesAsync(
        CloudflareTunnelResource tunnel,
        IReadOnlyList<CloudflareRouteInstallerResource> routes,
        CancellationToken cancellationToken)
    {
        var logger = loggerService.GetLogger(tunnel);

        try
        {
            if (!tunnel.TryGetLastAnnotation<CloudflareApiTokenAnnotation>(out var apiAnnotation))
            {
                logger.LogWarning("Cloudflare API credentials not found for tunnel '{TunnelName}'", tunnel.Name);
                throw new InvalidOperationException("Cloudflare API credentials not available.");
            }

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
                
                throw new InvalidOperationException("Tunnel not yet provisioned.");
            }

            await DoConfigureRoutesAsync(client, tunnel, routes, logger, cancellationToken);

            // Mark all route installers as finished
            foreach (var route in routes)
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

            foreach (var route in routes)
            {
                await notificationService.PublishUpdateAsync(route, state => state with
                {
                    State = new ResourceStateSnapshot("Failed", KnownResourceStateStyles.Error)
                });
            }

            throw;
        }
    }

    /// <summary>
    /// Configures a single route for a tunnel.
    /// </summary>
    public async Task ConfigureRouteAsync(
        CloudflareRouteInstallerResource route,
        CancellationToken cancellationToken)
    {
        await ConfigureRoutesAsync(route.Tunnel, [route], cancellationToken);
    }

    private async Task DoConfigureRoutesAsync(
        CloudflareApiClient client,
        CloudflareTunnelResource tunnel,
        IReadOnlyList<CloudflareRouteInstallerResource> routes,
        ILogger tunnelLogger,
        CancellationToken cancellationToken)
    {
        var config = new TunnelConfiguration();

        foreach (var route in routes)
        {
            var logger = loggerService.GetLogger(route);

            // Create DNS record
            var domain = GetRootDomain(route.Hostname);
            logger.LogInformation("Looking up zone for domain {Domain}...", domain);
            var zone = await client.FindZoneByNameAsync(domain, cancellationToken);

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
                await client.CreateTunnelDnsRecordAsync(zone.Id, route.Hostname, tunnel.TunnelId!, cancellationToken: cancellationToken);
                route.DnsRecordCreated = true;
                logger.LogInformation("DNS record created successfully for {Hostname}", route.Hostname);
            }
            catch (CloudflareApiException ex) when (ex.RecordAlreadyExists)
            {
                logger.LogInformation("DNS record for {Hostname} already exists, skipping creation", route.Hostname);
                route.DnsRecordCreated = true;
            }
            catch (CloudflareApiException ex)
            {
                logger.LogError(ex, "Failed to create DNS record for {Hostname}: {Message}", route.Hostname, ex.Message);
                throw;
            }

            var serviceUrl = BuildServiceUrl(route);

            config.Ingress.Add(new IngressRule
            {
                Hostname = route.Hostname,
                Service = serviceUrl
            });

            logger.LogInformation("Added ingress rule: {Hostname} -> {Service}", route.Hostname, serviceUrl);
        }

        // Add required catch-all rule
        config.Ingress.Add(new IngressRule
        {
            Service = "http_status:404"
        });

        tunnelLogger.LogInformation("Updating tunnel configuration with {RouteCount} routes...", routes.Count);
        await client.UpdateTunnelConfigurationAsync(tunnel.TunnelId!, config, cancellationToken);
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
