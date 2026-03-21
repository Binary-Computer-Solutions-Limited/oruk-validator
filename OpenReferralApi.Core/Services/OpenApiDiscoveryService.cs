using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace OpenReferralApi.Core.Services;

public interface IOpenApiDiscoveryService
{
    Task<string?> FindOpenApiSpecAsync(string baseUrl, string? baseUrlContent = null, CancellationToken cancellationToken = default);
}


public class OpenApiDiscoveryService : IOpenApiDiscoveryService
{
    private static readonly string[] StandardPaths =
    {
        "openapi.json",
        "swagger.json",
        ".well-known/openapi.json",  // RFC standard
        "api-docs"
    };

    private static readonly string[] SwaggerConfigPaths =
    {
        "swagger-config",
        "swagger/swagger-config",
        "api-docs/swagger-config"
    };

    private static readonly string[] UiPaths =
    {
        "swagger/index.html",
        "swagger",
        "scalar",
        "scalar/index.html",
        "swagger-ui",
        "swagger-ui/index.html",
        "swagger-ui.html",
        "redoc",
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

    public OpenApiDiscoveryService(IHttpClientFactory httpClientFactory, ILogger<OpenApiDiscoveryService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<string?> FindOpenApiSpecAsync(string baseUrl, string? baseUrlContent = null, CancellationToken cancellationToken = default)
    {
        var client = _httpClientFactory.CreateClient("OpenApiValidationService");
        baseUrl = baseUrl.TrimEnd('/');

        // 1. Probing strategy — try common well-known OpenAPI spec paths.
        foreach (var path in ExpandSpecPaths(StandardPaths))
        {
            try
            {
                var specUrl = BuildAbsoluteUrl(baseUrl, path);
                var response = await client.GetAsync(specUrl, cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    string content = await response.Content.ReadAsStringAsync(cancellationToken);
                    if (LooksLikeOpenApiSpec(content))
                    {
                        _logger.LogInformation("Discovered feed OpenAPI spec via probing at {Path}", path);
                        return specUrl;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Probe failed for {BaseUrl}{Path}", SchemaResolverService.SanitizeUrlForLogging(baseUrl), path);
            }
        }

        // 2. Probe known swagger-config endpoints to discover one or more definitions.
        foreach (var configPath in SwaggerConfigPaths)
        {
            try
            {
                var configUrl = BuildAbsoluteUrl(baseUrl, configPath);
                var discoveredFromConfig = await DiscoverFromSwaggerConfigEndpointAsync(client, baseUrl, configUrl, cancellationToken);
                if (discoveredFromConfig.Count > 0)
                {
                    var specUrl = discoveredFromConfig[0];
                    _logger.LogInformation("Discovered feed OpenAPI spec via config endpoint: {SpecUrl}", SchemaResolverService.SanitizeUrlForLogging(specUrl));
                    return specUrl;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Config probe failed for {ConfigPath}", configPath);
            }
        }

        // 3. Scraping strategy — use the already-fetched base response content when available,
        // otherwise fetch the root HTML page and look for a Swagger UI bundle config.
        try
        {
            var html = baseUrlContent;
            if (string.IsNullOrWhiteSpace(html))
            {
                var response = await client.GetAsync(baseUrl, cancellationToken);
                if (!response.IsSuccessStatusCode) return null;

                html = await response.Content.ReadAsStringAsync(cancellationToken);
            }

            var discoveredUrl = await DiscoverFromUiHtmlAsync(client, html, baseUrl, cancellationToken);
            if (!string.IsNullOrWhiteSpace(discoveredUrl))
            {
                var specUrl = discoveredUrl;
                _logger.LogInformation("Discovered feed OpenAPI spec via HTML scraping: {SpecUrl}", SchemaResolverService.SanitizeUrlForLogging(specUrl));
                return specUrl;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "HTML scraping failed for base URL {BaseUrl}", SchemaResolverService.SanitizeUrlForLogging(baseUrl));
        }

        // 4. Probe known UI routes and extract OpenAPI URL from HTML if available.
        foreach (var uiPath in UiPaths)
        {
            try
            {
                var uiUrl = BuildAbsoluteUrl(baseUrl, uiPath);
                var response = await client.GetAsync(uiUrl, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    continue;
                }

                var content = await response.Content.ReadAsStringAsync(cancellationToken);
                var discoveredUrl = await DiscoverFromUiHtmlAsync(client, content, baseUrl, cancellationToken);
                if (!string.IsNullOrWhiteSpace(discoveredUrl))
                {
                    var specUrl = discoveredUrl;
                    _logger.LogInformation("Discovered feed OpenAPI spec via UI route scraping: {SpecUrl}", SchemaResolverService.SanitizeUrlForLogging(specUrl));
                    return specUrl;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "UI probe failed for {UiUrl}", SchemaResolverService.SanitizeUrlForLogging(BuildAbsoluteUrl(baseUrl, uiPath)));
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
            return discoveredUrls[0];
        }

        var configUrls = DiscoverConfigUrls(htmlContent, baseUrl);
        foreach (var configUrl in configUrls)
        {
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
        var response = await client.GetAsync(configUrl, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return new List<string>();
        }

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        return DiscoverFromSwaggerConfigContent(content, baseUrl);
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