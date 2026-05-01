namespace OpenReferralApi.Core.Services;

public interface IProfileDiscoveryService
{
    Task<ProfileDiscoveryResult> DiscoverAsync(string baseUrl, DataSourceAuthentication? authentication = null, CancellationToken cancellationToken = default);
    Task<(string? url, string? reason)> DiscoverOpenApiUrlAsync(string baseUrl, DataSourceAuthentication? authentication = null, CancellationToken cancellationToken = default);
}

public sealed class ProfileDiscoveryResult
{
    public string? Url { get; init; }
    public string? Reason { get; init; }
    public bool BaseUrlRequestSucceeded { get; init; }
    public string? BaseUrlResponseContent { get; init; }
    public bool HasExplicitOpenApiUrl { get; init; }
    public string? DetectedHsdsProfileVersion { get; init; }
}

public interface IOpenApiDiscoveryService
{
    Task<string?> FindOpenApiSpecAsync(string baseUrl, string? baseUrlContent = null, CancellationToken cancellationToken = default);
    Task<OpenApiDiscoveryResult> DiscoverOpenApiSpecAsync(string baseUrl, string? baseUrlContent = null, bool includeDiscoveredSpecContent = false, CancellationToken cancellationToken = default);
}

public sealed class OpenApiDiscoveryResult
{
    public string? Url { get; init; }
    public string? SpecContent { get; init; }
    public string? DetectedHsdsProfileVersion { get; init; }
}
