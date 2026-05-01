using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json.Linq;
using OpenReferralApi.Core.Logging;

namespace OpenReferralApi.Core.Services;

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


public class OpenApiDiscoveryService : IOpenApiDiscoveryService
{
    private static readonly string[] SwaggerConfigPaths =
    {
        "swagger-config",
        "swagger/swagger-config",
        "api-docs/swagger-config",
        "v3/api-docs/swagger-config",
        "v2/api-docs/swagger-config",
        "swagger/v1/swagger-config"
    };

    private static readonly string[] UiPaths =
    {
        "swagger/index.html",
        "swagger",
        "api/swagger",
        "api/swagger/index.html",
        "scalar",
        "scalar/index.html",
        "swagger-ui",
        "swagger-ui/index.html",
        "swagger-ui.html",
        "redoc",
        "api/redoc",
        "docs",
        "api/docs"
    };

    private static readonly Regex OpenApiYamlRegex = new(
        @"(?m)^\s*(openapi|swagger)\s*:\s*",
        RegexOptions.Compiled);

    private static readonly Regex SwaggerUiDefinitionUrlRegex = new(
        @"(?<![a-zA-Z0-9_])url\s*[:=]\s*[""']([^""']+\.(?:json|yaml|yml))[""']",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex SwaggerUiApiDocsRegex = new(
        @"(?<![a-zA-Z0-9_])url\s*[:=]\s*[""']([^""']*api-docs[^""']*)[""']",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex SwaggerUiConfigUrlRegex = new(
        @"configUrl\s*[:=]\s*[""']([^""']+)[""']",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex SpecUrlAttributeRegex = new(
        @"spec-url\s*=\s*[""']([^""']+)[""']",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex RedocInitRegex = new(
        @"Redoc\.init\(\s*[""']([^""']+)[""']",
        RegexOptions.Compiled | RegexOptions.Singleline);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<OpenApiDiscoveryService> _logger;
    private readonly OpenApiValidationServerOptions _openApiValidationOptions;

    public OpenApiDiscoveryService(IHttpClientFactory httpClientFactory, ILogger<OpenApiDiscoveryService> logger, IOptions<OpenApiValidationServerOptions>? openApiValidationOptions = null)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _openApiValidationOptions = openApiValidationOptions?.Value ?? new OpenApiValidationServerOptions();
    }

    public async Task<string?> FindOpenApiSpecAsync(string baseUrl, string? baseUrlContent = null, CancellationToken cancellationToken = default)
    {
        var discovery = await DiscoverOpenApiSpecAsync(baseUrl, baseUrlContent, includeDiscoveredSpecContent: false, cancellationToken);
        return discovery.Url;
    }

    public async Task<OpenApiDiscoveryResult> DiscoverOpenApiSpecAsync(string baseUrl, string? baseUrlContent = null, bool includeDiscoveredSpecContent = false, CancellationToken cancellationToken = default)
    {
        var client = _httpClientFactory.CreateClient("OpenApiValidationService");
        baseUrl = baseUrl.TrimEnd('/');
        var discoverProfileVersionOnly = _openApiValidationOptions.OwnSchemaValidation == OwnSchemaValidationMode.None;

        // 1. Probing strategy — try common well-known OpenAPI spec paths.
        foreach (var path in ExpandSpecPaths(OpenApiDiscoveryStandardPaths.Paths))
        {
            if (!string.IsNullOrWhiteSpace(baseUrlContent) && string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            try
            {
                var specUrl = BuildAbsoluteUrl(baseUrl, path);
                _logger.ProbingStandardPath(SchemaResolverService.SanitizeUrlForLogging(specUrl));
                using var response = await client.GetAsync(specUrl, cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    string content = await response.Content.ReadAsStringAsync(cancellationToken);

                    var detectedHsdsProfileVersion = TryExtractPotentialHsdsProfileVersion(content);
                    if (discoverProfileVersionOnly && !string.IsNullOrWhiteSpace(detectedHsdsProfileVersion))
                    {
                        return new OpenApiDiscoveryResult
                        {
                            Url = null,
                            SpecContent = includeDiscoveredSpecContent ? content : null,
                            DetectedHsdsProfileVersion = detectedHsdsProfileVersion
                        };
                    }

                    if (LooksLikeOpenApiSpec(content))
                    {
                        _logger.DiscoveredSpecViaProbing(path);
                        return new OpenApiDiscoveryResult
                        {
                            Url = specUrl,
                            SpecContent = content,
                            DetectedHsdsProfileVersion = detectedHsdsProfileVersion
                        };
                    }
                    _logger.PathReturnedNonOpenApiContent(path);
                }
                else
                {
                    _logger.PathReturnedStatusCode(path, (int)response.StatusCode);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.ProbeFailed(ex, SchemaResolverService.SanitizeUrlForLogging(baseUrl), path);
            }
        }

        if (discoverProfileVersionOnly)
        {
            return new OpenApiDiscoveryResult();
        }

        // 2. Probe known swagger-config endpoints to discover one or more definitions.
        foreach (var configPath in SwaggerConfigPaths)
        {
            try
            {
                var configUrl = BuildAbsoluteUrl(baseUrl, configPath);
                _logger.ProbingSwaggerConfigPath(SchemaResolverService.SanitizeUrlForLogging(configUrl));
                var discoveredFromConfig = await DiscoverFromSwaggerConfigEndpointAsync(client, baseUrl, configUrl, cancellationToken);
                if (discoveredFromConfig.Count > 0)
                {
                    var specUrl = discoveredFromConfig[0];
                    _logger.DiscoveredSpecViaConfigEndpoint(SchemaResolverService.SanitizeUrlForLogging(specUrl));
                    var discoveredSpecContent = includeDiscoveredSpecContent
                        ? await TryFetchDiscoveredSpecContentAsync(client, specUrl, cancellationToken)
                        : null;
                    return new OpenApiDiscoveryResult
                    {
                        Url = specUrl,
                        SpecContent = discoveredSpecContent
                    };
                }
                _logger.NoDefinitionsAtConfigPath(configPath);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.ConfigProbeFailed(ex, configPath);
            }
        }

        // 3. Scraping strategy — use the already-fetched base response content when available,
        // otherwise fetch the root HTML page and look for a Swagger UI bundle config.
        try
        {
            var html = baseUrlContent;
            if (string.IsNullOrWhiteSpace(html))
            {
                using var response = await client.GetAsync(baseUrl, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    return new OpenApiDiscoveryResult();
                }

                html = await response.Content.ReadAsStringAsync(cancellationToken);
            }

            var discoveredUrl = await DiscoverFromUiHtmlAsync(client, html, baseUrl, cancellationToken);
            if (!string.IsNullOrWhiteSpace(discoveredUrl))
            {
                var specUrl = discoveredUrl;
                _logger.DiscoveredSpecViaHtmlScraping(SchemaResolverService.SanitizeUrlForLogging(specUrl));
                var discoveredSpecContent = includeDiscoveredSpecContent
                    ? await TryFetchDiscoveredSpecContentAsync(client, specUrl, cancellationToken)
                    : null;
                return new OpenApiDiscoveryResult
                {
                    Url = specUrl,
                    SpecContent = discoveredSpecContent
                };
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.HtmlScrapingFailed(ex, SchemaResolverService.SanitizeUrlForLogging(baseUrl));
        }

        // 4. Probe known UI routes and extract OpenAPI URL from HTML if available.
        foreach (var uiPath in UiPaths)
        {
            try
            {
                var uiUrl = BuildAbsoluteUrl(baseUrl, uiPath);
                _logger.ProbingUiRoute(SchemaResolverService.SanitizeUrlForLogging(uiUrl));
                using var response = await client.GetAsync(uiUrl, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.UiRouteReturnedStatusCode(uiPath, (int)response.StatusCode);
                    continue;
                }

                var content = await response.Content.ReadAsStringAsync(cancellationToken);
                var discoveredUrl = await DiscoverFromUiHtmlAsync(client, content, baseUrl, cancellationToken);
                if (!string.IsNullOrWhiteSpace(discoveredUrl))
                {
                    var specUrl = discoveredUrl;
                    _logger.DiscoveredSpecViaUiRouteScraping(SchemaResolverService.SanitizeUrlForLogging(specUrl));
                    var discoveredSpecContent = includeDiscoveredSpecContent
                        ? await TryFetchDiscoveredSpecContentAsync(client, specUrl, cancellationToken)
                        : null;
                    return new OpenApiDiscoveryResult
                    {
                        Url = specUrl,
                        SpecContent = discoveredSpecContent
                    };
                }
                _logger.UiRouteNoSpecUrlFound(uiPath);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.UiProbeFailed(ex, SchemaResolverService.SanitizeUrlForLogging(BuildAbsoluteUrl(baseUrl, uiPath)));
            }
        }

        _logger.UnableToDiscoverOpenApiSpec(SchemaResolverService.SanitizeUrlForLogging(baseUrl));

        return new OpenApiDiscoveryResult();
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

    private async Task<string?> TryFetchDiscoveredSpecContentAsync(HttpClient client, string specUrl, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await client.GetAsync(specUrl, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.DiscoveredSpecUrlReturnedStatusCode(SchemaResolverService.SanitizeUrlForLogging(specUrl), (int)response.StatusCode);
                return null;
            }

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            return LooksLikeOpenApiSpec(content) ? content : null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.FailedToFetchDiscoveredSpecContent(ex, SchemaResolverService.SanitizeUrlForLogging(specUrl));
            return null;
        }
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
            return content.Contains("\"openapi\"", StringComparison.OrdinalIgnoreCase) ||
                   content.Contains("\"swagger\"", StringComparison.OrdinalIgnoreCase);
        }

        return OpenApiYamlRegex.IsMatch(content);
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

    private async Task<string?> DiscoverFromUiHtmlAsync(HttpClient client, string htmlContent, string baseUrl, CancellationToken cancellationToken)
    {
        var discoveredUrls = await DiscoverAllDefinitionsAsync(htmlContent, baseUrl);
        if (discoveredUrls.Count > 0)
        {
            foreach (var discoveredUrl in discoveredUrls)
            {
                _logger.DiscoveredCandidateFromUiHtml(SchemaResolverService.SanitizeUrlForLogging(discoveredUrl));
            }

            return discoveredUrls[0];
        }

        var configUrls = DiscoverConfigUrls(htmlContent, baseUrl);
        foreach (var configUrl in configUrls)
        {
            _logger.DiscoveredSwaggerConfigCandidate(SchemaResolverService.SanitizeUrlForLogging(configUrl));
            var discoveredFromConfig = await DiscoverFromSwaggerConfigEndpointAsync(client, baseUrl, configUrl, cancellationToken);
            if (discoveredFromConfig.Count > 0)
            {
                return discoveredFromConfig[0];
            }
        }

        return null;
    }

    private async Task<List<string>> DiscoverFromSwaggerConfigEndpointAsync(HttpClient client, string baseUrl, string configUrl, CancellationToken cancellationToken)
    {
        _logger.RequestingSwaggerConfigEndpoint(SchemaResolverService.SanitizeUrlForLogging(configUrl));
        using var response = await client.GetAsync(configUrl, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            _logger.SwaggerConfigEndpointReturnedStatusCode(SchemaResolverService.SanitizeUrlForLogging(configUrl), (int)response.StatusCode);
            return new List<string>();
        }

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        var discovered = DiscoverFromSwaggerConfigContent(content, baseUrl);
        foreach (var discoveredUrl in discovered)
        {
            _logger.DiscoveredCandidateFromSwaggerConfig(SchemaResolverService.SanitizeUrlForLogging(configUrl), SchemaResolverService.SanitizeUrlForLogging(discoveredUrl));
        }

        return discovered;
    }

    private static List<string> DiscoverFromSwaggerConfigContent(string configContent, string baseUrl)
    {
        var discoveredUrls = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(configContent))
        {
            return discoveredUrls;
        }

        try
        {
            var root = JToken.Parse(configContent);
            AddResolvedUrl(root["url"]?.ToString(), baseUrl, seen, discoveredUrls);

            if (root["urls"] is JArray urls)
            {
                foreach (var item in urls)
                {
                    AddResolvedUrl(item?["url"]?.ToString(), baseUrl, seen, discoveredUrls);
                }
            }
        }
        catch
        {
            return discoveredUrls;
        }

        return discoveredUrls;
    }

    private static IReadOnlyList<string> DiscoverConfigUrls(string htmlContent, string baseUrl)
    {
        var discoveredUrls = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match match in SwaggerUiConfigUrlRegex.Matches(htmlContent))
        {
            AddResolvedUrl(match.Groups[1].Value, baseUrl, seen, discoveredUrls);
        }

        return discoveredUrls;
    }

    private static Task<List<string>> DiscoverAllDefinitionsAsync(string uiHtml, string baseUri)
    {
        var discoveredUrls = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(uiHtml))
        {
            return Task.FromResult(discoveredUrls);
        }

        foreach (Match match in SwaggerUiDefinitionUrlRegex.Matches(uiHtml))
        {
            var path = match.Groups[1].Value;
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            var absoluteUrl = new Uri(new Uri(baseUri + "/"), path).ToString();
            if (seen.Add(absoluteUrl))
            {
                discoveredUrls.Add(absoluteUrl);
            }
        }

        foreach (Match match in SwaggerUiApiDocsRegex.Matches(uiHtml))
        {
            var path = match.Groups[1].Value;
            AddResolvedUrl(path, baseUri, seen, discoveredUrls);
        }

        foreach (Match match in SpecUrlAttributeRegex.Matches(uiHtml))
        {
            var path = match.Groups[1].Value;
            AddResolvedUrl(path, baseUri, seen, discoveredUrls);
        }

        foreach (Match match in RedocInitRegex.Matches(uiHtml))
        {
            var path = match.Groups[1].Value;
            AddResolvedUrl(path, baseUri, seen, discoveredUrls);
        }

        return Task.FromResult(discoveredUrls);
    }

    private static void AddResolvedUrl(string? path, string baseUri, ISet<string> seen, ICollection<string> output)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var absoluteUrl = new Uri(new Uri(baseUri + "/"), path).ToString();
        if (seen.Add(absoluteUrl))
        {
            output.Add(absoluteUrl);
        }
    }
}