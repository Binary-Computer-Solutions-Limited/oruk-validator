using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenReferralApi.Core.Logging;

namespace OpenReferralApi.Core.Services;

public sealed class UnifiedDiscoveryResult
{
    public string? Url { get; init; }
    public string? Reason { get; init; }
    public string? SpecContent { get; init; }
    public string? DetectedHsdsProfileVersion { get; init; }
    public bool BaseUrlRequestSucceeded { get; init; }
    public string? BaseUrlResponseContent { get; init; }
    public bool HasExplicitOpenApiUrl { get; init; }
}

public interface IFastDiscoveryService
{
    Task<UnifiedDiscoveryResult> DiscoverAsync(
        string baseUrl,
        DataSourceAuthentication? authentication = null,
        string? baseUrlContent = null,
        bool includeDiscoveredSpecContent = false,
        CancellationToken cancellationToken = default);
}

public sealed class FastDiscoveryService : IFastDiscoveryService
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
    private readonly ILogger<FastDiscoveryService> _logger;
    private readonly SpecificationOptions _specificationOptions;
    private readonly OpenApiValidationServerOptions _openApiValidationOptions;

    public FastDiscoveryService(
        IHttpClientFactory httpClientFactory,
        ILogger<FastDiscoveryService> logger,
        IOptions<SpecificationOptions> specificationOptions,
        IOptions<OpenApiValidationServerOptions>? openApiValidationOptions = null)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _specificationOptions = specificationOptions?.Value ?? throw new ArgumentNullException(nameof(specificationOptions));
        _openApiValidationOptions = openApiValidationOptions?.Value ?? new OpenApiValidationServerOptions();
    }

    public async Task<UnifiedDiscoveryResult> DiscoverAsync(
        string baseUrl,
        DataSourceAuthentication? authentication = null,
        string? baseUrlContent = null,
        bool includeDiscoveredSpecContent = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return new UnifiedDiscoveryResult();
        }

        var normalizedBaseUrl = baseUrl.TrimEnd('/');
        var discoverProfileVersionOnly = _openApiValidationOptions.OwnSchemaValidation == OwnSchemaValidationMode.None;

        using var client = _httpClientFactory.CreateClient("OpenApiValidationService");
        client.Timeout = TimeSpan.FromSeconds(10);

        var rootContent = baseUrlContent;
        var baseUrlRequestSucceeded = !string.IsNullOrWhiteSpace(baseUrlContent);

        if (rootContent == null)
        {
            try
            {
                _logger.RequestingBaseUrl(SchemaResolverService.SanitizeUrlForLogging(normalizedBaseUrl));
                using var request = new HttpRequestMessage(HttpMethod.Get, normalizedBaseUrl);
                ApplyAuthentication(request, authentication);
                using var response = await client.SendAsync(request, cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    rootContent = await response.Content.ReadAsStringAsync(cancellationToken);
                    baseUrlRequestSucceeded = true;
                }
                else
                {
                    _logger.BaseUrlRequestFailed((int)response.StatusCode);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.ErrorRequestingBaseUrl(ex);
            }
        }

        if (!string.IsNullOrWhiteSpace(rootContent))
        {
            var rootResult = HandleRootResponse(rootContent, baseUrlRequestSucceeded);
            if (rootResult != null)
            {
                return rootResult;
            }
        }

        foreach (var path in ExpandSpecPaths(OpenApiDiscoveryStandardPaths.Paths))
        {
            if (!string.IsNullOrWhiteSpace(rootContent) && string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            var specUrl = BuildAbsoluteUrl(normalizedBaseUrl, path);
            try
            {
                _logger.ProbingStandardPath(SchemaResolverService.SanitizeUrlForLogging(specUrl));
                using var request = new HttpRequestMessage(HttpMethod.Get, specUrl);
                ApplyAuthentication(request, authentication);
                using var response = await client.SendAsync(request, cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.PathReturnedStatusCode(path, (int)response.StatusCode);
                    continue;
                }

                var content = await response.Content.ReadAsStringAsync(cancellationToken);
                var detectedVersion = TryExtractPotentialHsdsProfileVersion(content);
                var resolvedProfileSpecUrl = ResolveSpecificationUrl(detectedVersion);

                if (discoverProfileVersionOnly && !string.IsNullOrWhiteSpace(detectedVersion))
                {
                    return new UnifiedDiscoveryResult
                    {
                        Url = resolvedProfileSpecUrl,
                        Reason = $"Potential HSDS profile version {SchemaResolverService.SanitizeStringForLogging(detectedVersion ?? string.Empty)} discovered via standard path probing",
                        SpecContent = includeDiscoveredSpecContent && LooksLikeOpenApiSpec(content) ? content : null,
                        DetectedHsdsProfileVersion = detectedVersion,
                        BaseUrlRequestSucceeded = baseUrlRequestSucceeded,
                        BaseUrlResponseContent = rootContent
                    };
                }

                if (LooksLikeOpenApiSpec(content))
                {
                    _logger.DiscoveredSpecViaProbing(path);
                    return new UnifiedDiscoveryResult
                    {
                        Url = specUrl,
                        Reason = $"OpenAPI URL discovered by probing standard path '{path}'",
                        SpecContent = includeDiscoveredSpecContent ? content : null,
                        DetectedHsdsProfileVersion = detectedVersion,
                        BaseUrlRequestSucceeded = baseUrlRequestSucceeded,
                        BaseUrlResponseContent = rootContent
                    };
                }

                if (!string.IsNullOrWhiteSpace(resolvedProfileSpecUrl))
                {
                    return new UnifiedDiscoveryResult
                    {
                        Url = resolvedProfileSpecUrl,
                        Reason = $"Potential HSDS profile version {SchemaResolverService.SanitizeStringForLogging(detectedVersion ?? string.Empty)} discovered via standard path probing",
                        DetectedHsdsProfileVersion = detectedVersion,
                        BaseUrlRequestSucceeded = baseUrlRequestSucceeded,
                        BaseUrlResponseContent = rootContent
                    };
                }

                _logger.PathReturnedNonOpenApiContent(path);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.ProbeFailed(ex, SchemaResolverService.SanitizeUrlForLogging(normalizedBaseUrl), path);
            }
        }

        if (!discoverProfileVersionOnly)
        {
            foreach (var configPath in SwaggerConfigPaths)
            {
                try
                {
                    var configUrl = BuildAbsoluteUrl(normalizedBaseUrl, configPath);
                    _logger.ProbingSwaggerConfigPath(SchemaResolverService.SanitizeUrlForLogging(configUrl));
                    var discoveredFromConfig = await DiscoverFromSwaggerConfigEndpointAsync(client, normalizedBaseUrl, configUrl, cancellationToken);
                    if (discoveredFromConfig.Count > 0)
                    {
                        var discoveredSpecUrl = discoveredFromConfig[0];
                        _logger.DiscoveredSpecViaConfigEndpoint(SchemaResolverService.SanitizeUrlForLogging(discoveredSpecUrl));
                        var discoveredSpecContent = includeDiscoveredSpecContent
                            ? await TryFetchDiscoveredSpecContentAsync(client, discoveredSpecUrl, cancellationToken)
                            : null;
                        return new UnifiedDiscoveryResult
                        {
                            Url = discoveredSpecUrl,
                            Reason = "OpenAPI URL discovered via Swagger config endpoint",
                            SpecContent = discoveredSpecContent,
                            BaseUrlRequestSucceeded = baseUrlRequestSucceeded,
                            BaseUrlResponseContent = rootContent
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

            try
            {
                var htmlContent = rootContent;
                if (string.IsNullOrWhiteSpace(htmlContent))
                {
                    using var response = await client.GetAsync(normalizedBaseUrl, cancellationToken);
                    if (response.IsSuccessStatusCode)
                    {
                        htmlContent = await response.Content.ReadAsStringAsync(cancellationToken);
                    }
                }

                if (!string.IsNullOrWhiteSpace(htmlContent))
                {
                    var discoveredFromHtml = await DiscoverFromUiHtmlAsync(client, htmlContent, normalizedBaseUrl, cancellationToken);
                    if (!string.IsNullOrWhiteSpace(discoveredFromHtml))
                    {
                        _logger.DiscoveredSpecViaHtmlScraping(SchemaResolverService.SanitizeUrlForLogging(discoveredFromHtml));
                        var discoveredSpecContent = includeDiscoveredSpecContent
                            ? await TryFetchDiscoveredSpecContentAsync(client, discoveredFromHtml, cancellationToken)
                            : null;
                        return new UnifiedDiscoveryResult
                        {
                            Url = discoveredFromHtml,
                            Reason = "OpenAPI URL discovered by scraping HTML",
                            SpecContent = discoveredSpecContent,
                            BaseUrlRequestSucceeded = baseUrlRequestSucceeded,
                            BaseUrlResponseContent = rootContent
                        };
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.HtmlScrapingFailed(ex, SchemaResolverService.SanitizeUrlForLogging(normalizedBaseUrl));
            }

            foreach (var uiPath in UiPaths)
            {
                try
                {
                    var uiUrl = BuildAbsoluteUrl(normalizedBaseUrl, uiPath);
                    _logger.ProbingUiRoute(SchemaResolverService.SanitizeUrlForLogging(uiUrl));
                    using var response = await client.GetAsync(uiUrl, cancellationToken);
                    if (!response.IsSuccessStatusCode)
                    {
                        _logger.UiRouteReturnedStatusCode(uiPath, (int)response.StatusCode);
                        continue;
                    }

                    var content = await response.Content.ReadAsStringAsync(cancellationToken);
                    var discoveredFromHtml = await DiscoverFromUiHtmlAsync(client, content, normalizedBaseUrl, cancellationToken);
                    if (!string.IsNullOrWhiteSpace(discoveredFromHtml))
                    {
                        _logger.DiscoveredSpecViaUiRouteScraping(SchemaResolverService.SanitizeUrlForLogging(discoveredFromHtml));
                        var discoveredSpecContent = includeDiscoveredSpecContent
                            ? await TryFetchDiscoveredSpecContentAsync(client, discoveredFromHtml, cancellationToken)
                            : null;
                        return new UnifiedDiscoveryResult
                        {
                            Url = discoveredFromHtml,
                            Reason = $"OpenAPI URL discovered by probing UI path '{uiPath}'",
                            SpecContent = discoveredSpecContent,
                            BaseUrlRequestSucceeded = baseUrlRequestSucceeded,
                            BaseUrlResponseContent = rootContent
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
                    _logger.UiProbeFailed(ex, SchemaResolverService.SanitizeUrlForLogging(BuildAbsoluteUrl(normalizedBaseUrl, uiPath)));
                }
            }
        }

        if (!baseUrlRequestSucceeded && baseUrlContent == null)
        {
            return new UnifiedDiscoveryResult
            {
                Reason = "Base URL request failed",
                BaseUrlRequestSucceeded = false
            };
        }

        _logger.UnableToDiscoverOpenApiSpec(SchemaResolverService.SanitizeUrlForLogging(normalizedBaseUrl));
        return new UnifiedDiscoveryResult
        {
            Reason = !string.IsNullOrWhiteSpace(rootContent)
                ? "No version or openapi_url found in '/' response"
                : "Unable to discover OpenAPI schema URL",
            BaseUrlRequestSucceeded = baseUrlRequestSucceeded,
            BaseUrlResponseContent = rootContent
        };
    }

    private UnifiedDiscoveryResult? HandleRootResponse(string rootContent, bool baseUrlRequestSucceeded)
    {
        try
        {
            using var document = JsonDocument.Parse(rootContent);
            var root = document.RootElement;

            var openapiUrl = TryGetString(root, "openapi_url")
                ?? TryGetString(root, "openapiUrl")
                ?? TryGetString(root, "open_api_url");
            if (!string.IsNullOrWhiteSpace(openapiUrl))
            {
                _logger.DiscoveredOpenApiUrl(SchemaResolverService.SanitizeUrlForLogging(openapiUrl));
                return new UnifiedDiscoveryResult
                {
                    Url = openapiUrl,
                    Reason = "OpenAPI URL read from '/' endpoint (openapi_url field)",
                    BaseUrlRequestSucceeded = baseUrlRequestSucceeded,
                    BaseUrlResponseContent = rootContent,
                    HasExplicitOpenApiUrl = true
                };
            }

            var version = TryGetString(root, "version");
            if (!string.IsNullOrWhiteSpace(version))
            {
                var versionedSpec = ResolveSpecificationUrl(version);
                if (!string.IsNullOrWhiteSpace(versionedSpec))
                {
                    _logger.DetectedVersionResolvedSpec(SchemaResolverService.SanitizeStringForLogging(version), versionedSpec);
                    return new UnifiedDiscoveryResult
                    {
                        Url = versionedSpec,
                        Reason = $"Standard version {SchemaResolverService.SanitizeStringForLogging(version)} read from '/' endpoint",
                        BaseUrlRequestSucceeded = baseUrlRequestSucceeded,
                        BaseUrlResponseContent = rootContent,
                        DetectedHsdsProfileVersion = version
                    };
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.FailedToParseBaseUrlJson(ex);
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

        return OpenApiYamlRegex.IsMatch(content);
    }

    private static string? TryExtractPotentialHsdsProfileVersion(string specContent)
    {
        if (string.IsNullOrWhiteSpace(specContent))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(specContent);
            var root = document.RootElement;

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
                var value = TryGetPathString(root, path);
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
            using var document = JsonDocument.Parse(configContent);
            var root = document.RootElement;

            AddResolvedUrl(TryGetString(root, "url"), baseUrl, seen, discoveredUrls);

            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("urls", out var urls)
                && urls.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in urls.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    AddResolvedUrl(TryGetString(item, "url"), baseUrl, seen, discoveredUrls);
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
            AddResolvedUrl(match.Groups[1].Value, baseUri, seen, discoveredUrls);
        }

        foreach (Match match in SpecUrlAttributeRegex.Matches(uiHtml))
        {
            AddResolvedUrl(match.Groups[1].Value, baseUri, seen, discoveredUrls);
        }

        foreach (Match match in RedocInitRegex.Matches(uiHtml))
        {
            AddResolvedUrl(match.Groups[1].Value, baseUri, seen, discoveredUrls);
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

    private static string BuildAbsoluteUrl(string baseUrl, string relativePath)
    {
        return string.IsNullOrWhiteSpace(relativePath)
            ? baseUrl
            : $"{baseUrl}/{relativePath.TrimStart('/')}";
    }

    private static void ApplyAuthentication(HttpRequestMessage request, IAuthenticationConfig? auth)
    {
        if (auth == null)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(auth.ApiKey)
            && !string.IsNullOrWhiteSpace(auth.ApiKeyHeader)
            && IsValidHeaderName(auth.ApiKeyHeader))
        {
            _ = request.Headers.TryAddWithoutValidation(auth.ApiKeyHeader, auth.ApiKey);
        }

        if (!string.IsNullOrWhiteSpace(auth.BearerToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", auth.BearerToken);
        }

        if (auth.BasicAuth != null
            && !string.IsNullOrWhiteSpace(auth.BasicAuth.Username)
            && !string.IsNullOrWhiteSpace(auth.BasicAuth.Password))
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

    private static string? TryGetString(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString(),
            JsonValueKind.Number => property.GetRawText(),
            JsonValueKind.True => bool.TrueString,
            JsonValueKind.False => bool.FalseString,
            _ => null
        };
    }

    private static string? TryGetPathString(JsonElement root, string path)
    {
        var current = root;
        foreach (var segment in path.Split('.'))
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current))
            {
                return null;
            }
        }

        return current.ValueKind switch
        {
            JsonValueKind.String => current.GetString(),
            JsonValueKind.Number => current.GetRawText(),
            JsonValueKind.True => bool.TrueString,
            JsonValueKind.False => bool.FalseString,
            _ => null
        };
    }

    private string? ResolveSpecificationUrl(string? rawVersion)
    {
        if (string.IsNullOrWhiteSpace(rawVersion))
        {
            return null;
        }

        if (_specificationOptions.Urls.TryGetValue(rawVersion, out var exactUrl)
            && !string.IsNullOrWhiteSpace(exactUrl))
        {
            return exactUrl.Trim();
        }

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
