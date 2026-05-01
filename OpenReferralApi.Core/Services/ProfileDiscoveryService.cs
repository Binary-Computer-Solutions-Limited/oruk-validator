using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json.Linq;
using OpenReferralApi.Core.Logging;

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
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ProfileDiscoveryService> _logger;
    private readonly SpecificationOptions _specificationOptions;
    private readonly OpenApiValidationServerOptions _openApiValidationOptions;

    public ProfileDiscoveryService(
        IHttpClientFactory httpClientFactory,
        ILogger<ProfileDiscoveryService> logger,
        IOptions<SpecificationOptions> specificationOptions,
        IOptions<OpenApiValidationServerOptions>? openApiValidationOptions = null)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _specificationOptions = specificationOptions?.Value ?? throw new ArgumentNullException(nameof(specificationOptions));
        _openApiValidationOptions = openApiValidationOptions?.Value ?? new OpenApiValidationServerOptions();
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
            _logger.RequestingBaseUrl(SchemaResolverService.SanitizeUrlForLogging(baseUrl));
            using var request = new HttpRequestMessage(HttpMethod.Get, baseUrl);
            ApplyAuthentication(request, authentication);
            var discoverProfileVersionOnly = _openApiValidationOptions.OwnSchemaValidation == OwnSchemaValidationMode.None;

            using var resp = await httpClient.SendAsync(request, cancellationToken);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.BaseUrlRequestFailed((int)resp.StatusCode);
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
                    _logger.DiscoveredOpenApiUrl(SchemaResolverService.SanitizeUrlForLogging(openapiUrl));
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
                    var versionedSpec = ResolveSpecificationUrl(version);
                    if (!string.IsNullOrWhiteSpace(versionedSpec))
                    {
                        _logger.DetectedVersionResolvedSpec(SchemaResolverService.SanitizeStringForLogging(version), versionedSpec);
                        return new ProfileDiscoveryResult
                        {
                            Url = versionedSpec,
                            Reason = $"Standard version {SchemaResolverService.SanitizeStringForLogging(version)} read from '/' endpoint",
                            BaseUrlRequestSucceeded = true,
                            BaseUrlResponseContent = content,
                            DetectedHsdsProfileVersion = version
                        };
                    }
                }

                var discoveredFromStandardPaths = await DiscoverFromStandardPathsAsync(httpClient, baseUrl, content, discoverProfileVersionOnly, cancellationToken);
                if (discoveredFromStandardPaths != null)
                {
                    return discoveredFromStandardPaths;
                }

                _logger.NoOpenApiUrlOrVersionFound();
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
                _logger.FailedToParseBaseUrlJson(jex);

                var discoveredFromStandardPaths = await DiscoverFromStandardPathsAsync(httpClient, baseUrl, content, discoverProfileVersionOnly, cancellationToken);
                if (discoveredFromStandardPaths != null)
                {
                    return discoveredFromStandardPaths;
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
            _logger.ErrorRequestingBaseUrl(ex);
            return new ProfileDiscoveryResult
            {
                Url = null,
                Reason = "Error requesting base URL",
                BaseUrlRequestSucceeded = false
            };
        }
    }

    private async Task<ProfileDiscoveryResult?> DiscoverFromStandardPathsAsync(
        HttpClient httpClient,
        string baseUrl,
        string baseUrlContent,
        bool discoverProfileVersionOnly,
        CancellationToken cancellationToken)
    {
        var normalizedBaseUrl = baseUrl.TrimEnd('/');

        foreach (var path in ExpandSpecPaths(OpenApiDiscoveryStandardPaths.Paths))
        {
            // if (string.IsNullOrWhiteSpace(path))
            // {
            //     continue;
            // }

            var specUrl = BuildAbsoluteUrl(normalizedBaseUrl, path);
            try
            {
                using var response = await httpClient.GetAsync(specUrl, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    continue;
                }

                var content = await response.Content.ReadAsStringAsync(cancellationToken);
                var detectedHsdsProfileVersion = TryExtractPotentialHsdsProfileVersion(content);

                if (!string.IsNullOrWhiteSpace(detectedHsdsProfileVersion))
                {
                    var resolvedProfileSpecUrl = ResolveSpecificationUrl(detectedHsdsProfileVersion);
                    if (discoverProfileVersionOnly)
                    {
                        return new ProfileDiscoveryResult
                        {
                            Url = resolvedProfileSpecUrl,
                            Reason = $"Potential HSDS profile version {SchemaResolverService.SanitizeStringForLogging(detectedHsdsProfileVersion)} discovered via standard path probing",
                            BaseUrlRequestSucceeded = true,
                            BaseUrlResponseContent = baseUrlContent,
                            DetectedHsdsProfileVersion = detectedHsdsProfileVersion
                        };
                    }

                    if (LooksLikeOpenApiSpec(content))
                    {
                        return new ProfileDiscoveryResult
                        {
                            Url = specUrl,
                            Reason = $"OpenAPI URL discovered by probing standard path '{path}'",
                            BaseUrlRequestSucceeded = true,
                            BaseUrlResponseContent = baseUrlContent,
                            DetectedHsdsProfileVersion = detectedHsdsProfileVersion
                        };
                    }

                    if (!string.IsNullOrWhiteSpace(resolvedProfileSpecUrl))
                    {
                        return new ProfileDiscoveryResult
                        {
                            Url = resolvedProfileSpecUrl,
                            Reason = $"Potential HSDS profile version {SchemaResolverService.SanitizeStringForLogging(detectedHsdsProfileVersion)} discovered via standard path probing",
                            BaseUrlRequestSucceeded = true,
                            BaseUrlResponseContent = baseUrlContent,
                            DetectedHsdsProfileVersion = detectedHsdsProfileVersion
                        };
                    }
                }

                if (!discoverProfileVersionOnly && LooksLikeOpenApiSpec(content))
                {
                    return new ProfileDiscoveryResult
                    {
                        Url = specUrl,
                        Reason = $"OpenAPI URL discovered by probing standard path '{path}'",
                        BaseUrlRequestSucceeded = true,
                        BaseUrlResponseContent = baseUrlContent
                    };
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Ignore per-path failures and continue probing.
            }
        }

        return null;
    }

    private static bool LooksLikeOpenApiSpec(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return false;
        }

        var trimmed = content.TrimStart();
        if (trimmed.StartsWith("{", StringComparison.Ordinal) || trimmed.StartsWith("[", StringComparison.Ordinal))
        {
            return content.Contains("\"openapi\"", StringComparison.OrdinalIgnoreCase)
                   || content.Contains("\"swagger\"", StringComparison.OrdinalIgnoreCase);
        }

        return content.Contains("openapi:", StringComparison.OrdinalIgnoreCase)
               || content.Contains("swagger:", StringComparison.OrdinalIgnoreCase);
    }

    private static string? TryExtractPotentialHsdsProfileVersion(string specContent)
    {
        if (string.IsNullOrWhiteSpace(specContent))
        {
            return null;
        }

        try
        {
            var parsed = JToken.Parse(specContent);
            var candidatePaths = new[]
            {
                "x-hsds-version",
                "version",
                "info.x-hsds-version",
                "info.x-profile-version",
                "info.version",
                "openapi"
            };

            foreach (var path in candidatePaths)
            {
                var value = parsed.SelectToken(path)?.ToString()?.Trim();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    private static IEnumerable<string> ExpandSpecPaths(IEnumerable<string> basePaths)
    {
        foreach (var path in basePaths)
        {
            yield return path;

            if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                var withoutJsonExt = path[..^".json".Length];
                yield return withoutJsonExt + ".yaml";
                yield return withoutJsonExt + ".yml";
            }
        }
    }

    private static string BuildAbsoluteUrl(string baseUrl, string relativePath)
    {
        return $"{baseUrl}/{relativePath.TrimStart('/')}";
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
            _ = request.Headers.TryAddWithoutValidation(auth.ApiKeyHeader, auth.ApiKey);
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

            _ = request.Headers.TryAddWithoutValidation(header.Key, header.Value);
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

    private string? ResolveSpecificationUrl(string? rawVersion)
    {
        if (string.IsNullOrWhiteSpace(rawVersion))
        {
            return null;
        }

        // Prefer an exact key match first (e.g. data supplies "HSDS-UK-3.0" which is already a configured key).
        if (_specificationOptions.Urls.TryGetValue(rawVersion, out var exactUrl)
            && !string.IsNullOrWhiteSpace(exactUrl))
        {
            return exactUrl.Trim();
        }

        // Fall back to the first configured key whose normalised version matches (e.g. "3.0" → "HSDS-UK-3.0").
        var versionNumber = ProfileVersionNormalizer.NormalizeVersionNumber(rawVersion);
        if (string.IsNullOrWhiteSpace(versionNumber))
        {
            return null;
        }

        var firstMatch = _specificationOptions.Urls
            .FirstOrDefault(entry => !string.IsNullOrWhiteSpace(entry.Value)
                && string.Equals(
                    ProfileVersionNormalizer.NormalizeVersionNumber(entry.Key),
                    versionNumber,
                    StringComparison.OrdinalIgnoreCase));

        return string.IsNullOrWhiteSpace(firstMatch.Value) ? null : firstMatch.Value.Trim();
    }
}
