using Aspire.Hosting.ApplicationModel;

namespace Aspire.Cloudflared;

/// <summary>
/// Annotation that stores the API token parameter reference for a tunnel resource.
/// </summary>
internal sealed class CloudflareApiTokenAnnotation(ParameterResource apiToken, ParameterResource accountId) : IResourceAnnotation
{
    public ParameterResource ApiToken { get; } = apiToken;
    public ParameterResource AccountId { get; } = accountId;
}