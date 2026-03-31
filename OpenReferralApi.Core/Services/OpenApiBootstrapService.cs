using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using OpenReferralApi.Core.Logging;

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

        // Try detected version from discovered OpenAPI spec first, then fall back to root endpoint
        var rootProfileVersion = !string.IsNullOrWhiteSpace(profileDiscovery.DetectedHsdsProfileVersion)
            ? profileDiscovery.DetectedHsdsProfileVersion
            : TryExtractProfileVersionFromJson(profileDiscovery.BaseUrlResponseContent);

        OpenApiDiscoveryResult? feedSpecDiscovery = null;
        string? feedSpecUrl = null;
        if (!profileDiscovery.HasExplicitOpenApiUrl)
        {
            feedSpecDiscovery = await _openApiDiscoveryService.DiscoverOpenApiSpecAsync(
                baseUrl,
                profileDiscovery.BaseUrlResponseContent,
                includeDiscoveredSpecContent: true,
                cancellationToken)
                ?? new OpenApiDiscoveryResult();
            feedSpecUrl = feedSpecDiscovery.Url;
        }

        if (string.IsNullOrWhiteSpace(rootProfileVersion) && !string.IsNullOrWhiteSpace(feedSpecDiscovery?.SpecContent))
        {
            rootProfileVersion = TryExtractProfileVersionFromOpenApiSpec(feedSpecDiscovery.SpecContent);
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
                ProfileVersion = rootProfileVersion,
                ProfileReason = rootProfileVersion != null
                    ? $"Standard version [user: {rootProfileVersion}] detected"
                    : "Version not found in '/' response",
                DiscoveryReason = profileDiscovery.Reason,
                UsedDataServiceOpenApi = false
            };
        }

        var discoveryReason = !string.IsNullOrWhiteSpace(feedSpecUrl)
            ? $"Feed spec discovered at {SchemaResolverService.SanitizeUrlForLogging(feedSpecUrl)}"
              + (!string.IsNullOrWhiteSpace(profileDiscovery.Reason) ? $"; {profileDiscovery.Reason}" : string.Empty)
            : profileDiscovery.Reason;

        var profileReason = rootProfileVersion != null
            ? $"Standard version [user: {rootProfileVersion}] detected"
            : "Version not found in '/' response";

        _logger.BootstrapDiscoveryResolved(SchemaResolverService.SanitizeUrlForLogging(discoveredUrl), profileReason);

        return new OpenApiBootstrapResult
        {
            OpenApiSchemaUrl = discoveredUrl,
            ProfileVersion = rootProfileVersion,
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
            var rawVersion = parsed.SelectToken("version")?.ToString()?.Trim();
            return string.IsNullOrWhiteSpace(rawVersion) ? null : rawVersion;
        }
        catch
        {
            return null;
        }
    }

    private static string? TryExtractProfileVersionFromOpenApiSpec(string specContent)
    {
        if (string.IsNullOrWhiteSpace(specContent))
        {
            return null;
        }

        try
        {
            var parsed = JObject.Parse(specContent);

            // Only extract from legitimate HSDS version fields.
            // Deliberately do NOT fall back to the "openapi" field — that field specifies the
            // OpenAPI specification version, not the HSDS schema version. If a version can only
            // be inferred from "openapi", leave it unset here so that OpenApiValidationService
            // can detect and report the misplacement with a proper warning.
            var candidateTokens = new[] { "x-hsds-version", "version", "info.x-hsds-version", "info.x-profile-version" };
            foreach (var tokenPath in candidateTokens)
            {
                var tokenValue = parsed.SelectToken(tokenPath)?.ToString()?.Trim();
                if (!string.IsNullOrWhiteSpace(tokenValue))
                {
                    return tokenValue;
                }
            }

            return null;
        }
        catch
        {
            return null;
        }
    }
}
