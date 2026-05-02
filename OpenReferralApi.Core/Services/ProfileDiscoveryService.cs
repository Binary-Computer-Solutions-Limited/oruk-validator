using Microsoft.Extensions.Logging;
using System.Text.Json;
using OpenReferralApi.Core.Logging;
using Microsoft.Extensions.Options;
using OpenReferralApi.Core.Helpers;
using OpenReferralApi.Core.Extensions;
using System.Text.RegularExpressions;
using System.Net.Http.Headers;
using System.Text;
namespace OpenReferralApi.Core.Services;

public interface IProfileDiscoveryService
{
    Task<ProfileDiscoveryResult> DiscoverFromBaseUrlAsync(
        string baseUrl,
        DataSourceAuthentication? authentication = null,
        CancellationToken cancellationToken = default);
}

public sealed class ProfileDiscoveryResult
{
    public string? HsdsProfileReason { get; init; }
    public string? OpenApiSchemaContent { get; init; }
    public string? HsdsProfileVersion { get; init; }
    public bool HasExplicitOpenApiUrl { get; init; }
    public string? DiscoveryReason { get; init; }
    public bool UsedDataServiceOpenApi { get; init; }
}

public class ProfileDiscoveryService : IProfileDiscoveryService
{

    private readonly ILogger<ProfileDiscoveryService> _logger;
    private readonly OpenApiValidationServerOptions _openApiValidationOptions;

    private readonly IHttpClientFactory _httpClientFactory;

    private static readonly string[] HSDS_VERSION_candidateTokens = new[]
    {
        "x-hsds-version",
        "version",
        "info.x-hsds-version",
        "info.x-profile-version",
        "info.version"
    };

    public ProfileDiscoveryService(
        ILogger<ProfileDiscoveryService> logger,
        IHttpClientFactory httpClientFactory,
        IOptions<SpecificationOptions> specificationOptions,
        IOptions<OpenApiValidationServerOptions>? openApiValidationOptions = null)
    {
        _logger = logger;
        _openApiValidationOptions = openApiValidationOptions?.Value ?? new OpenApiValidationServerOptions();
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _ = specificationOptions?.Value ?? throw new ArgumentNullException(nameof(specificationOptions));
    }

    private static string? TryExtractProfileVersionFromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            // Only extract from legitimate HSDS version fields.
            // Deliberately do NOT fall back to the "openapi" field — that field specifies the
            // OpenAPI specification version, not the HSDS schema version. If a version can only
            // be inferred from "openapi", leave it unset here so that OpenApiValidationService
            // can detect and report the misplacement with a proper warning.
            foreach (var tokenPath in HSDS_VERSION_candidateTokens)
            {
                var tokenValue = root.TryGetPathString(tokenPath)?.Trim();
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

    private static string? TryExtractProfileVersionFromOpenApiSpec(string specContent)
    {
        if (string.IsNullOrWhiteSpace(specContent))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(specContent);
            var root = document.RootElement;

            // Only extract from legitimate HSDS version fields.
            // Deliberately do NOT fall back to the "openapi" field — that field specifies the
            // OpenAPI specification version, not the HSDS schema version. If a version can only
            // be inferred from "openapi", leave it unset here so that OpenApiValidationService
            // can detect and report the misplacement with a proper warning.
            foreach (var tokenPath in HSDS_VERSION_candidateTokens)
            {
                var tokenValue = root.TryGetPathString(tokenPath)?.Trim();
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

    public async Task<ProfileDiscoveryResult> DiscoverFromBaseUrlAsync(
        string baseUrl,
        DataSourceAuthentication? authentication = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new ArgumentException("Base URL must be provided for OpenAPI discovery", nameof(baseUrl));
        }

        var normalizedBaseUrl = baseUrl.TrimEnd('/');
        var discoverOpenApiSpec = _openApiValidationOptions.OwnSchemaValidation != OwnSchemaValidationMode.None;

        using var client = _httpClientFactory.CreateClient("OpenApiValidationService");
        client.Timeout = TimeSpan.FromSeconds(10);

        var probePaths = BuildDiscoveryProbePaths();
        var firstDiscoveredVersion = (string?)null;

        foreach (var path in probePaths)
        {
            var discoveryUrl = BuildAbsoluteUrl(normalizedBaseUrl, path);
            try
            {
                _logger.ProbingStandardPath(TextSanitizer.SanitizeUrlForLogging(discoveryUrl));
                using var request = new HttpRequestMessage(HttpMethod.Get, discoveryUrl);
                ApplyAuthentication(request, authentication);
                using var response = await client.SendAsync(request, cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.PathReturnedStatusCode(path, (int)response.StatusCode);
                    continue;
                }

                var content = await response.Content.ReadAsStringAsync(cancellationToken);
                var hsdsProfileVersion = TryExtractPotentialHsdsProfileVersion(content);
                if (string.IsNullOrWhiteSpace(firstDiscoveredVersion)
                    && !string.IsNullOrWhiteSpace(hsdsProfileVersion))
                {
                    firstDiscoveredVersion = hsdsProfileVersion;
                }

                if (!discoverOpenApiSpec && !string.IsNullOrWhiteSpace(hsdsProfileVersion))
                {
                    return new ProfileDiscoveryResult
                    {
                        HsdsProfileReason = $"Potential HSDS profile version {TextSanitizer.SanitizeStringForLogging(hsdsProfileVersion ?? string.Empty)} discovered via standard path probing",
                        OpenApiSchemaContent = null,
                        HsdsProfileVersion = hsdsProfileVersion
                    };
                }

                if (LooksLikeOpenApiSpec(content))
                {
                    _logger.DiscoveredSpecViaProbing(path);
                    return new ProfileDiscoveryResult
                    {
                        HsdsProfileReason = $"OpenAPI schema discovered by probing path '{path}'",
                        OpenApiSchemaContent = discoverOpenApiSpec ? content : null,
                        HsdsProfileVersion = hsdsProfileVersion
                    };
                }

                var discoveredFromConfigContent = DiscoverFromSwaggerConfigContent(content, normalizedBaseUrl);
                if (discoveredFromConfigContent.Count > 0)
                {
                    var discoveredSpecUrl = discoveredFromConfigContent[0];
                    _logger.DiscoveredSpecViaConfigEndpoint(TextSanitizer.SanitizeUrlForLogging(discoveredSpecUrl));
                    var openApiSpecContent = discoverOpenApiSpec
                        ? await TryFetchDiscoveredSpecContentAsync(client, discoveredSpecUrl, cancellationToken)
                        : null;

                    if (!string.IsNullOrWhiteSpace(openApiSpecContent))
                    {
                        var fetchedVersion = TryExtractPotentialHsdsProfileVersion(openApiSpecContent) ?? hsdsProfileVersion;
                        if (string.IsNullOrWhiteSpace(firstDiscoveredVersion) && !string.IsNullOrWhiteSpace(fetchedVersion))
                        {
                            firstDiscoveredVersion = fetchedVersion;
                        }

                        return new ProfileDiscoveryResult
                        {
                            HsdsProfileReason = "OpenAPI schema discovered via Swagger config response",
                            OpenApiSchemaContent = openApiSpecContent,
                            HsdsProfileVersion = fetchedVersion
                        };
                    }
                }

                var discoveredFromHtml = await DiscoverFromUiHtmlAsync(client, content, normalizedBaseUrl, cancellationToken);
                if (!string.IsNullOrWhiteSpace(discoveredFromHtml))
                {
                    _logger.DiscoveredSpecViaUiRouteScraping(TextSanitizer.SanitizeUrlForLogging(discoveredFromHtml));
                    var openApiSpecContent = discoverOpenApiSpec
                        ? await TryFetchDiscoveredSpecContentAsync(client, discoveredFromHtml, cancellationToken)
                        : null;

                    if (!string.IsNullOrWhiteSpace(openApiSpecContent))
                    {
                        var fetchedVersion = TryExtractPotentialHsdsProfileVersion(openApiSpecContent) ?? hsdsProfileVersion;
                        if (string.IsNullOrWhiteSpace(firstDiscoveredVersion) && !string.IsNullOrWhiteSpace(fetchedVersion))
                        {
                            firstDiscoveredVersion = fetchedVersion;
                        }

                        return new ProfileDiscoveryResult
                        {
                            HsdsProfileReason = $"OpenAPI schema discovered via HTML/Swagger UI hints at path '{path}'",
                            OpenApiSchemaContent = openApiSpecContent,
                            HsdsProfileVersion = fetchedVersion
                        };
                    }
                }

                if (!string.IsNullOrWhiteSpace(hsdsProfileVersion))
                {
                    return new ProfileDiscoveryResult
                    {
                        HsdsProfileReason = $"Potential HSDS profile version {TextSanitizer.SanitizeStringForLogging(hsdsProfileVersion ?? string.Empty)} discovered via standard path probing",
                        HsdsProfileVersion = hsdsProfileVersion
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
                _logger.ProbeFailed(ex, TextSanitizer.SanitizeUrlForLogging(normalizedBaseUrl), path);
            }
        }

        _logger.UnableToDiscoverOpenApiSpec(TextSanitizer.SanitizeUrlForLogging(normalizedBaseUrl));
        return new ProfileDiscoveryResult
        {
            HsdsProfileVersion = firstDiscoveredVersion,
            HsdsProfileReason = discoverOpenApiSpec == true
                ? "No Hsds Profile Version or OpenAPI specification could be discovered from the base URL"
                : "Unable to discover Hsds Profile Version"
        };
    }

    private static IReadOnlyList<string> BuildDiscoveryProbePaths()
    {
        return ExpandSpecPaths(Constants.OpenApiDocumentProbePaths)
            .Concat(Constants.SwaggerConfigProbePaths)
            .Concat(Constants.DocumentationUiProbePaths)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
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

        return Constants.OpenApiYamlRegex.IsMatch(content);
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

            foreach (var path in HSDS_VERSION_candidateTokens)
            {
                var value = root.TryGetPathString(path);
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
                _logger.DiscoveredSpecUrlReturnedStatusCode(TextSanitizer.SanitizeUrlForLogging(specUrl), (int)response.StatusCode);
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
            _logger.FailedToFetchDiscoveredSpecContent(ex, TextSanitizer.SanitizeUrlForLogging(specUrl));
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
                _logger.DiscoveredCandidateFromUiHtml(TextSanitizer.SanitizeUrlForLogging(discoveredUrl));
            }

            return discoveredUrls[0];
        }

        var configUrls = DiscoverConfigUrls(htmlContent, baseUrl);
        foreach (var configUrl in configUrls)
        {
            _logger.DiscoveredSwaggerConfigCandidate(TextSanitizer.SanitizeUrlForLogging(configUrl));
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
        _logger.RequestingSwaggerConfigEndpoint(TextSanitizer.SanitizeUrlForLogging(configUrl));
        using var response = await client.GetAsync(configUrl, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            _logger.SwaggerConfigEndpointReturnedStatusCode(TextSanitizer.SanitizeUrlForLogging(configUrl), (int)response.StatusCode);
            return new List<string>();
        }

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        var discovered = DiscoverFromSwaggerConfigContent(content, baseUrl);
        foreach (var discoveredUrl in discovered)
        {
            _logger.DiscoveredCandidateFromSwaggerConfig(TextSanitizer.SanitizeUrlForLogging(configUrl), TextSanitizer.SanitizeUrlForLogging(discoveredUrl));
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

        foreach (Match match in Constants.SwaggerUiConfigUrlRegex.Matches(htmlContent))
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

        foreach (Match match in Constants.SwaggerUiDefinitionUrlRegex.Matches(uiHtml))
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

        foreach (Match match in Constants.SwaggerUiApiDocsRegex.Matches(uiHtml))
        {
            AddResolvedUrl(match.Groups[1].Value, baseUri, seen, discoveredUrls);
        }

        foreach (Match match in Constants.SpecUrlAttributeRegex.Matches(uiHtml))
        {
            AddResolvedUrl(match.Groups[1].Value, baseUri, seen, discoveredUrls);
        }

        foreach (Match match in Constants.RedocInitRegex.Matches(uiHtml))
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

}
