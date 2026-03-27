using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json.Linq;

namespace OpenReferralApi.Core.Services;

public interface IProfileDiscoveryService
{
    Task<ProfileDiscoveryResult> DiscoverAsync(string baseUrl, DataSourceAuthentication? authentication = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Attempts to discover an OpenAPI schema URL from the provided base URL.
    /// Returns the discovered URL and the reason for how it was discovered, or null if none found.
    /// </summary>
    Task<(string? url, string? reason)> DiscoverOpenApiUrlAsync(string baseUrl, DataSourceAuthentication? authentication = null, CancellationToken cancellationToken = default);
}

public class ProfileDiscoveryResult
{
    public string? Url { get; init; }
    public string? Reason { get; init; }
    public bool BaseUrlRequestSucceeded { get; init; }
    public string? BaseUrlResponseContent { get; init; }
    public bool HasExplicitOpenApiUrl { get; init; }
    /// <summary>
    /// If detected during discovery, contains the HSDS profile version (e.g., "3.0")
    /// extracted from the discovered OpenAPI spec's "openapi" field.
    /// </summary>
    public string? DetectedHsdsProfileVersion { get; init; }
}

public class ProfileDiscoveryService : IProfileDiscoveryService
{
    private static readonly string[] FallbackSpecPaths =
    {
        "openapi.json",
        "swagger.json",
        ".well-known/openapi.json",
        "api-docs/openapi.json",
        "api-docs",
        "v3/api-docs",
        "swagger/v1/swagger.json"
    };

    private static readonly Regex SwaggerUiDefinitionUrlRegex = new(
        @"(?<![a-zA-Z0-9_])url\s*[:=]\s*[""']([^""']+(?:openapi|swagger|api-docs)[^""']*)[""']",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex SwaggerUiConfigUrlRegex = new(
        @"configUrl\s*[:=]\s*[""']([^""']+)[""']",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ProfileDiscoveryService> _logger;
    private readonly string _baseSpecificationUrl;

    public ProfileDiscoveryService(IHttpClientFactory httpClientFactory, ILogger<ProfileDiscoveryService> logger, IOptions<SpecificationOptions> specificationOptions)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _baseSpecificationUrl = specificationOptions?.Value.BaseUrl ?? throw new ArgumentNullException(nameof(specificationOptions));
    }

    public async Task<ProfileDiscoveryResult> DiscoverAsync(string baseUrl, DataSourceAuthentication? authentication = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return new ProfileDiscoveryResult();
        }

        try
        {
            using var httpClient = _httpClientFactory.CreateClient("OpenApiValidationService");
            httpClient.Timeout = TimeSpan.FromSeconds(10);
            _logger.LogInformation("Requesting BaseUrl to discover openapi_url: {BaseUrl}", SchemaResolverService.SanitizeUrlForLogging(baseUrl));
            using var request = new HttpRequestMessage(HttpMethod.Get, baseUrl);
            ApplyAuthentication(request, authentication);

            var resp = await httpClient.SendAsync(request, cancellationToken);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogInformation("BaseUrl request returned {Status}; unable to determine HSDS schema version", resp.StatusCode);
                return new ProfileDiscoveryResult
                {
                    Url = null,
                    Reason = "Base URL request failed",
                    BaseUrlRequestSucceeded = false
                };
            }

            var content = await resp.Content.ReadAsStringAsync(cancellationToken);
            try
            {
                var j = JObject.Parse(content);

                // openapi_url explicitly points to the schema to validate against.
                var openapiUrlToken = j.SelectToken("openapi_url") ?? j.SelectToken("openapiUrl") ?? j.SelectToken("open_api_url");
                var openapiUrl = openapiUrlToken?.ToString();
                if (!string.IsNullOrEmpty(openapiUrl))
                {
                    _logger.LogInformation("Discovered openapi_url: {OpenApiUrl}", SchemaResolverService.SanitizeUrlForLogging(openapiUrl));
                    return new ProfileDiscoveryResult
                    {
                        Url = openapiUrl,
                        Reason = "OpenAPI URL read from '/' endpoint (openapi_url field)",
                        BaseUrlRequestSucceeded = true,
                        BaseUrlResponseContent = content,
                        HasExplicitOpenApiUrl = true
                    };
                }

                // Check for version field and construct URL
                var versionToken = j.SelectToken("version");
                var version = versionToken?.ToString();
                if (!string.IsNullOrEmpty(version))
                {
                    var extractedVersion = ExtractVersionNumber(version);
                    if (extractedVersion.HasValue)
                    {
                        var versionedSpec = $"{_baseSpecificationUrl}{extractedVersion.Value:0.0}/openapi.json";
                        _logger.LogInformation("Detected version '{Version}'; HSDS-UK {ExtractedVersion:0.0} spec: {OpenApiUrl}", SchemaResolverService.SanitizeStringForLogging(version), extractedVersion.Value, versionedSpec);
                        return new ProfileDiscoveryResult
                        {
                            Url = versionedSpec,
                            Reason = $"Standard version {SchemaResolverService.SanitizeStringForLogging(version)} read from '/' endpoint",
                            BaseUrlRequestSucceeded = true,
                            BaseUrlResponseContent = content
                        };
                    }
                }

                _logger.LogInformation("No openapi_url or version in BaseUrl response; unable to determine HSDS schema version");
                var fallback = await DiscoverOpenApiFromFallbacksAsync(httpClient, baseUrl, content, cancellationToken);
                if (!string.IsNullOrWhiteSpace(fallback.url))
                {
                    return new ProfileDiscoveryResult
                    {
                        Url = fallback.url,
                        Reason = fallback.reason,
                        BaseUrlRequestSucceeded = true,
                        BaseUrlResponseContent = content,
                        DetectedHsdsProfileVersion = fallback.detectedVersion
                    };
                }

                return new ProfileDiscoveryResult
                {
                    Url = null,
                    Reason = "No version or openapi_url found in '/' response",
                    BaseUrlRequestSucceeded = true,
                    BaseUrlResponseContent = content
                };
            }
            catch (Exception jex)
            {
                _logger.LogWarning(jex, "Failed to parse JSON from BaseUrl response; unable to determine HSDS schema version");

                var fallback = await DiscoverOpenApiFromFallbacksAsync(httpClient, baseUrl, content, cancellationToken);
                if (!string.IsNullOrWhiteSpace(fallback.url))
                {
                    return new ProfileDiscoveryResult
                    {
                        Url = fallback.url,
                        Reason = fallback.reason,
                        BaseUrlRequestSucceeded = true,
                        BaseUrlResponseContent = content,
                        DetectedHsdsProfileVersion = fallback.detectedVersion
                    };
                }

                return new ProfileDiscoveryResult
                {
                    Url = null,
                    Reason = "Failed to parse '/' response",
                    BaseUrlRequestSucceeded = true,
                    BaseUrlResponseContent = content
                };
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error requesting BaseUrl to discover openapi_url; unable to determine HSDS schema version");
            return new ProfileDiscoveryResult
            {
                Url = null,
                Reason = "Error requesting base URL",
                BaseUrlRequestSucceeded = false
            };
        }
    }

    public async Task<(string? url, string? reason)> DiscoverOpenApiUrlAsync(string baseUrl, DataSourceAuthentication? authentication = null, CancellationToken cancellationToken = default)
    {
        var discovery = await DiscoverAsync(baseUrl, authentication, cancellationToken);
        return (discovery.Url, discovery.Reason);
    }

    private static void ApplyAuthentication(HttpRequestMessage request, IAuthenticationConfig? auth)
    {
        if (auth == null)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(auth.ApiKey) && !string.IsNullOrWhiteSpace(auth.ApiKeyHeader) && IsValidHeaderName(auth.ApiKeyHeader))
        {
            request.Headers.TryAddWithoutValidation(auth.ApiKeyHeader, auth.ApiKey);
        }

        if (!string.IsNullOrWhiteSpace(auth.BearerToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", auth.BearerToken);
        }

        if (auth.BasicAuth != null && !string.IsNullOrWhiteSpace(auth.BasicAuth.Username) && !string.IsNullOrWhiteSpace(auth.BasicAuth.Password))
        {
            var credentials = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{auth.BasicAuth.Username}:{auth.BasicAuth.Password}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
        }

        if (auth.CustomHeaders == null)
        {
            return;
        }

        foreach (var header in auth.CustomHeaders)
        {
            if (string.IsNullOrWhiteSpace(header.Value) || !IsValidHeaderName(header.Key))
            {
                continue;
            }

            request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
    }

    private static bool IsValidHeaderName(string headerName)
    {
        if (string.IsNullOrWhiteSpace(headerName))
        {
            return false;
        }

        return !headerName.Any(c => char.IsControl(c) || c == ':' || c == '\r' || c == '\n');
    }

    private static float? ExtractVersionNumber(string version)
    {
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

    /// <summary>
    /// Attempts to extract HSDS version from an OpenAPI spec's "openapi" field.
    /// Returns the extracted major.minor version and a flag indicating if it was incorrectly defined.
    /// For example, "3.0.3" would extract to 3.0 and flag isIncorrect=true.
    /// </summary>
    private (float? version, string? openapiValue) TryExtractHsdsVersionFromOpenApiSpec(string specContent)
    {
        if (string.IsNullOrWhiteSpace(specContent))
        {
            return (null, null);
        }

        try
        {
            var spec = JObject.Parse(specContent);
            var openapiVersion = spec.SelectToken("openapi")?.ToString();

            if (!string.IsNullOrEmpty(openapiVersion))
            {
                // Extract major.minor version (e.g., "3.0" from "3.0.3")
                var versionParts = openapiVersion.Split('.');
                if (versionParts.Length >= 2)
                {
                    var majorMinor = $"{versionParts[0]}.{versionParts[1]}";
                    if (float.TryParse(majorMinor, out var version))
                    {
                        return (version, openapiVersion);
                    }
                }
            }
        }
        catch
        {
            // Ignore parsing errors
        }

        return (null, null);
    }

    private async Task<(string? url, string? reason, string? detectedVersion)> DiscoverOpenApiFromFallbacksAsync(HttpClient client, string baseUrl, string? baseUrlContent, CancellationToken cancellationToken)
    {
        var normalizedBaseUrl = baseUrl.TrimEnd('/');

        foreach (var path in FallbackSpecPaths)
        {
            try
            {
                var specUrl = BuildAbsoluteUrl(normalizedBaseUrl, path);
                _logger.LogDebug("Probing fallback path: {SpecUrl}", SchemaResolverService.SanitizeUrlForLogging(specUrl));
                using var request = new HttpRequestMessage(HttpMethod.Get, specUrl);
                var response = await client.SendAsync(request, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogDebug("Fallback path {Path} returned {StatusCode}", path, (int)response.StatusCode);
                    continue;
                }

                var content = await response.Content.ReadAsStringAsync(cancellationToken);
                if (LooksLikeOpenApiDocument(content))
                {
                    // Try to extract HSDS version from the "openapi" field if no proper version field exists
                    var (extractedVersion, openapiValue) = TryExtractHsdsVersionFromOpenApiSpec(content);
                    if (extractedVersion.HasValue)
                    {
                        var detectedVersion = $"{extractedVersion.Value:0.0}";
                        _logger.LogWarning(
                            "Discovered OpenAPI spec at {SpecUrl} incorrectly defines HSDS schema version in 'openapi' field (value: {OpenapiValue}). " +
                            "This should be defined in a proper HSDS version field. Detected HSDS profile version: {ProfileVersion}",
                            SchemaResolverService.SanitizeUrlForLogging(specUrl),
                            SchemaResolverService.SanitizeStringForLogging(openapiValue ?? "unknown"),
                            detectedVersion);
                        return (specUrl, $"OpenAPI URL discovered by probing '{path}' with detected HSDS version {detectedVersion} from 'openapi' field", detectedVersion);
                    }

                    _logger.LogInformation("Discovered OpenAPI spec via fallback probing at {SpecUrl}", SchemaResolverService.SanitizeUrlForLogging(specUrl));
                    return (specUrl, $"OpenAPI URL discovered by probing '{path}'", null);
                }
                _logger.LogDebug("Fallback path {Path} returned 200 but content does not look like an OpenAPI document", path);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Fallback probe failed for path {Path}", path);
            }
        }

        if (!string.IsNullOrWhiteSpace(baseUrlContent) && baseUrlContent.Contains("<html", StringComparison.OrdinalIgnoreCase))
        {
            var discoveredUrl = await DiscoverFromSwaggerUiHtmlAsync(client, normalizedBaseUrl, baseUrlContent, cancellationToken);
            if (!string.IsNullOrWhiteSpace(discoveredUrl))
            {
                return (discoveredUrl, "OpenAPI URL discovered from Swagger UI HTML", null);
            }
        }

        return (null, null, null);
    }

    private async Task<string?> DiscoverFromSwaggerUiHtmlAsync(HttpClient client, string baseUrl, string html, CancellationToken cancellationToken)
    {
        foreach (Match match in SwaggerUiDefinitionUrlRegex.Matches(html))
        {
            var candidate = match.Groups[1].Value;
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            var resolved = ResolveUrl(baseUrl, candidate);
            if (!string.IsNullOrWhiteSpace(resolved))
            {
                _logger.LogDebug("Discovered OpenAPI candidate from Swagger UI definition: {SpecUrl}", SchemaResolverService.SanitizeUrlForLogging(resolved));
                _logger.LogInformation("Discovered OpenAPI URL from Swagger UI definition: {SpecUrl}", SchemaResolverService.SanitizeUrlForLogging(resolved));
                return resolved;
            }
        }

        foreach (Match match in SwaggerUiConfigUrlRegex.Matches(html))
        {
            var configPath = match.Groups[1].Value;
            if (string.IsNullOrWhiteSpace(configPath))
            {
                continue;
            }

            var configUrl = ResolveUrl(baseUrl, configPath);
            if (string.IsNullOrWhiteSpace(configUrl))
            {
                continue;
            }

            try
            {
                _logger.LogDebug("Requesting swagger-config endpoint discovered from Swagger UI HTML: {ConfigUrl}", SchemaResolverService.SanitizeUrlForLogging(configUrl));
                using var configRequest = new HttpRequestMessage(HttpMethod.Get, configUrl);
                var configResp = await client.SendAsync(configRequest, cancellationToken);
                if (!configResp.IsSuccessStatusCode)
                {
                    _logger.LogDebug("Swagger-config endpoint {ConfigUrl} returned {StatusCode}", SchemaResolverService.SanitizeUrlForLogging(configUrl), (int)configResp.StatusCode);
                    continue;
                }

                var configContent = await configResp.Content.ReadAsStringAsync(cancellationToken);
                var token = JToken.Parse(configContent);
                var discovered = token["url"]?.ToString();
                if (!string.IsNullOrWhiteSpace(discovered))
                {
                    var resolved = ResolveUrl(baseUrl, discovered);
                    if (!string.IsNullOrWhiteSpace(resolved))
                    {
                        _logger.LogDebug("Discovered OpenAPI candidate from swagger-config endpoint {ConfigUrl}: {SpecUrl}", SchemaResolverService.SanitizeUrlForLogging(configUrl), SchemaResolverService.SanitizeUrlForLogging(resolved));
                        _logger.LogInformation("Discovered OpenAPI URL from Swagger config: {SpecUrl}", SchemaResolverService.SanitizeUrlForLogging(resolved));
                        return resolved;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to read swagger config at {ConfigUrl}", SchemaResolverService.SanitizeUrlForLogging(configUrl));
            }
        }

        return null;
    }

    private static bool LooksLikeOpenApiDocument(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return false;
        }

        return content.Contains("\"openapi\"", StringComparison.OrdinalIgnoreCase)
            || content.Contains("\"swagger\"", StringComparison.OrdinalIgnoreCase)
            || content.Contains("openapi:", StringComparison.OrdinalIgnoreCase)
            || content.Contains("swagger:", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildAbsoluteUrl(string baseUrl, string relativePath)
    {
        return $"{baseUrl}/{relativePath.TrimStart('/')}";
    }

    private static string? ResolveUrl(string baseUrl, string path)
    {
        try
        {
            return new Uri(new Uri(baseUrl + "/"), path).ToString();
        }
        catch
        {
            return null;
        }
    }
}
