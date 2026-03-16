using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using OpenReferralApi.Core.Models.Configuration;
using OpenReferralApi.Core.Models.Endpoints;
using OpenReferralApi.Core.Models.Feeds;
using OpenReferralApi.Core.Models.Schema;
using OpenReferralApi.Core.Models.Security;
using OpenReferralApi.Core.Models.Validation;

namespace OpenReferralApi.Core.Services;

public interface IOpenApiBootstrapService
{
    Task<OpenApiBootstrapResult> ResolveFromBaseUrlAsync(string baseUrl, DataSourceAuthentication? authentication = null, CancellationToken cancellationToken = default);
}

public class OpenApiBootstrapResult
{
    public string? OpenApiSchemaUrl { get; init; }
    public string? ProfileVersion { get; init; }
    public string? ProfileReason { get; init; }
    public string? DiscoveryReason { get; init; }
    public bool UsedDataServiceOpenApi { get; init; }
}

public class OpenApiBootstrapService : IOpenApiBootstrapService
{
    private const string DefaultProfileVersion = "HSDS-UK-1.0";

    private readonly IProfileDiscoveryService _profileDiscoveryService;
    private readonly IOpenApiDiscoveryService _openApiDiscoveryService;
    private readonly ILogger<OpenApiBootstrapService> _logger;

    public OpenApiBootstrapService(
        IProfileDiscoveryService profileDiscoveryService,
        IOpenApiDiscoveryService openApiDiscoveryService,
        ILogger<OpenApiBootstrapService> logger)
    {
        _profileDiscoveryService = profileDiscoveryService;
        _openApiDiscoveryService = openApiDiscoveryService;
        _logger = logger;
    }

    public async Task<OpenApiBootstrapResult> ResolveFromBaseUrlAsync(string baseUrl, DataSourceAuthentication? authentication = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new ArgumentException("Base URL must be provided for OpenAPI bootstrap discovery", nameof(baseUrl));
        }

        var profileDiscovery = await _profileDiscoveryService.DiscoverAsync(baseUrl, authentication, cancellationToken);

        var rootProfileVersion = TryExtractProfileVersionFromJson(profileDiscovery.BaseUrlResponseContent);

        string? feedSpecUrl = null;
        if (!profileDiscovery.HasExplicitOpenApiUrl)
        {
            feedSpecUrl = await _openApiDiscoveryService.FindOpenApiSpecAsync(
                baseUrl,
                profileDiscovery.BaseUrlResponseContent,
                cancellationToken);
        }

        var discoveredUrl = profileDiscovery.HasExplicitOpenApiUrl
            ? profileDiscovery.Url
            : !string.IsNullOrWhiteSpace(feedSpecUrl)
                ? feedSpecUrl
                : profileDiscovery.Url;

        if (string.IsNullOrWhiteSpace(discoveredUrl))
        {
            return new OpenApiBootstrapResult
            {
                OpenApiSchemaUrl = null,
                ProfileVersion = rootProfileVersion ?? DefaultProfileVersion,
                ProfileReason = rootProfileVersion != null
                    ? $"Standard version [user: {rootProfileVersion}] read from '/' endpoint"
                    : $"Standard version [user: {DefaultProfileVersion}] defaulted (version not found in '/' response)",
                DiscoveryReason = profileDiscovery.Reason,
                UsedDataServiceOpenApi = false
            };
        }

        var discoveryReason = !string.IsNullOrWhiteSpace(feedSpecUrl)
            ? $"Feed spec discovered at {SchemaResolverService.SanitizeUrlForLogging(feedSpecUrl)}"
              + (!string.IsNullOrWhiteSpace(profileDiscovery.Reason) ? $"; {profileDiscovery.Reason}" : string.Empty)
            : profileDiscovery.Reason;

        var profileReason = rootProfileVersion != null
            ? $"Standard version [user: {rootProfileVersion}] read from '/' endpoint"
            : $"Standard version [user: {DefaultProfileVersion}] defaulted (version not found in '/' response)";

        _logger.LogInformation(
            "Bootstrap discovery resolved schema URL {SchemaUrl} with profile context {ProfileReason}",
            SchemaResolverService.SanitizeUrlForLogging(discoveredUrl),
            profileReason);

        return new OpenApiBootstrapResult
        {
            OpenApiSchemaUrl = discoveredUrl,
            ProfileVersion = rootProfileVersion ?? DefaultProfileVersion,
            ProfileReason = profileReason,
            DiscoveryReason = discoveryReason,
            UsedDataServiceOpenApi = profileDiscovery.HasExplicitOpenApiUrl || !string.IsNullOrWhiteSpace(feedSpecUrl)
        };
    }

    private static string? TryExtractProfileVersionFromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var parsed = JObject.Parse(json);
            var rawVersion = parsed.SelectToken("version")?.ToString();
            return NormalizeProfileVersion(rawVersion);
        }
        catch
        {
            return null;
        }
    }

    private static string? NormalizeProfileVersion(string? rawVersion)
    {
        return ProfileVersionNormalizer.NormalizeHsdsProfileVersion(rawVersion);
    }
}
