using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json.Linq;
using OpenReferralApi.Core.Models;

namespace OpenReferralApi.Core.Services;

public interface IOpenApiDiscoveryService
{
    /// <summary>
    /// Attempts to discover an OpenAPI schema URL from the provided base URL.
    /// Returns the discovered URL and the reason for how it was discovered, or null if none found.
    /// </summary>
    Task<(string? url, string? reason)> DiscoverOpenApiUrlAsync(string baseUrl, CancellationToken cancellationToken = default);
}

public partial class OpenApiDiscoveryService : IOpenApiDiscoveryService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<OpenApiDiscoveryService> _logger;
    private readonly string _baseSpecificationUrl;

    public OpenApiDiscoveryService(IHttpClientFactory httpClientFactory, ILogger<OpenApiDiscoveryService> logger, IOptions<SpecificationOptions> specificationOptions)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _baseSpecificationUrl = specificationOptions.Value.BaseUrl;
    }

    public async Task<(string? url, string? reason)> DiscoverOpenApiUrlAsync(string baseUrl, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(baseUrl)) return (null, null);

        const float defaultSpecificationVersion = 1.0f;
        var defaultSpec = $"{_baseSpecificationUrl}{defaultSpecificationVersion:0.0}/openapi.json";
        try
        {
            using var httpClient = _httpClientFactory?.CreateClient("OpenApiValidationService") ?? new HttpClient();
            httpClient.Timeout = TimeSpan.FromSeconds(10);
            LogRequestingBaseUrl(SchemaResolverService.SanitizeUrlForLogging(baseUrl));
            var resp = await httpClient.GetAsync(baseUrl, cancellationToken);
            if (!resp.IsSuccessStatusCode)
            {
                LogBaseUrlRequestFailed(resp.StatusCode, defaultSpec);
                return (defaultSpec, "Defaulted to HSDS-UK 1.0 (base URL request failed)");
            }

            var content = await resp.Content.ReadAsStringAsync(cancellationToken);
            try
            {
                var j = JObject.Parse(content);

                // Check for version field and construct URL
                var versionToken = j.SelectToken("version");
                var version = versionToken?.ToString();
                if (!string.IsNullOrEmpty(version))
                {
                    var extractedVersion = ExtractVersionNumber(version);
                    if (extractedVersion.HasValue)
                    {
                        var versionedSpec = $"{_baseSpecificationUrl}{extractedVersion.Value:0.0}/openapi.json";
                        LogDetectedVersion(SchemaResolverService.SanitizeStringForLogging(version), extractedVersion.Value, versionedSpec);
                        return (versionedSpec, $"Standard version {SchemaResolverService.SanitizeStringForLogging(version)} read from '/' endpoint");
                    }
                }

                // Check for explicit openapi_url field first
                var openapiUrlToken = j.SelectToken("openapi_url") ?? j.SelectToken("openapiUrl") ?? j.SelectToken("open_api_url");
                var openapiUrl = openapiUrlToken?.ToString();
                if (!string.IsNullOrEmpty(openapiUrl))
                {
                    LogDiscoveredOpenApiUrl(SchemaResolverService.SanitizeUrlForLogging(openapiUrl));
                    return (openapiUrl, "OpenAPI URL read from '/' endpoint (openapi_url field)");
                }

                LogNoOpenApiUrlOrVersion(defaultSpec);
                return (defaultSpec, "Defaulted to HSDS-UK 1.0 (no version or openapi_url found)");
            }
            catch (Exception jex)
            {
                LogJsonParseFailed(jex, defaultSpec);
                return (defaultSpec, "Defaulted to HSDS-UK 1.0 (failed to parse base URL response)");
            }
        }
        catch (Exception ex)
        {
            LogBaseUrlRequestError(ex, defaultSpec);
            return (defaultSpec, "Defaulted to HSDS-UK 1.0 (error requesting base URL)");
        }
    }

    private static float? ExtractVersionNumber(string version)    {
        // Try to extract version number from formats like "HSDS-UK-3.0", "V3", "3.0", "3.1", etc.
        var versionString = version
            .Replace("HSDS-UK-", "", StringComparison.OrdinalIgnoreCase)
            .Replace("V", "", StringComparison.OrdinalIgnoreCase)
            .Replace("v", "")
            .Trim();

        if (float.TryParse(versionString, out var versionNumber))
        {
            return versionNumber;
        }

        return null;
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Requesting BaseUrl to discover openapi_url: {BaseUrl}")]
    private partial void LogRequestingBaseUrl(string baseUrl);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "BaseUrl request returned {Status}; defaulting to HSDS-UK 1.0 spec: {DefaultSpec}")]
    private partial void LogBaseUrlRequestFailed(HttpStatusCode status, string defaultSpec);

    [LoggerMessage(EventId = 3, Level = LogLevel.Information, Message = "Detected version '{Version}'; using HSDS-UK {ExtractedVersion:0.0} spec: {OpenApiUrl}")]
    private partial void LogDetectedVersion(string version, float extractedVersion, string openApiUrl);

    [LoggerMessage(EventId = 4, Level = LogLevel.Information, Message = "Discovered openapi_url: {OpenApiUrl}")]
    private partial void LogDiscoveredOpenApiUrl(string openApiUrl);

    [LoggerMessage(EventId = 5, Level = LogLevel.Information, Message = "No openapi_url or version in BaseUrl response; defaulting to HSDS-UK 1.0 spec: {DefaultSpec}")]
    private partial void LogNoOpenApiUrlOrVersion(string defaultSpec);

    [LoggerMessage(EventId = 6, Level = LogLevel.Warning, Message = "Failed to parse JSON from BaseUrl response; defaulting to HSDS-UK 1.0 spec: {DefaultSpec}")]
    private partial void LogJsonParseFailed(Exception jex, string defaultSpec);

    [LoggerMessage(EventId = 7, Level = LogLevel.Warning, Message = "Error requesting BaseUrl to discover openapi_url; defaulting to HSDS-UK 1.0 spec: {DefaultSpec}")]
    private partial void LogBaseUrlRequestError(Exception ex, string defaultSpec);
}
