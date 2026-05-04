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
    public string? HsdsProfileVersion { get; set; }
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
        var normalizedBaseUrl = baseUrl.TrimEnd('/');
        var needsSchema = _openApiValidationOptions.OwnSchemaValidation != OwnSchemaValidationMode.None;

        string? discoveredVersion = null;
        string? discoveredSchema = null;
        string? discoveryReason = null;

        using var client = _httpClientFactory.CreateClient("OpenApiValidationService");
        var probePaths = BuildDiscoveryProbePaths();

        foreach (var path in probePaths)
        {
            // 1. Check if we already have everything we need to stop
            if (discoveredVersion != null && (!needsSchema || discoveredSchema != null))
            {
                break;
            }

            var discoveryUrl = BuildAbsoluteUrl(normalizedBaseUrl, path);
            try
            {
                _logger.ProbingStandardPath(TextSanitizer.SanitizeUrlForLogging(discoveryUrl));
                using var request = new HttpRequestMessage(HttpMethod.Get, discoveryUrl);
                ApplyAuthentication(request, authentication);
                using var response = await client.SendAsync(request, cancellationToken);

                if (!response.IsSuccessStatusCode) continue;

                var content = await response.Content.ReadAsStringAsync(cancellationToken);

                // 2. Extract version only if we don't have one yet
                if (discoveredVersion == null)
                {
                    discoveredVersion = TryExtractPotentialHsdsProfileVersion(content);
                    if (discoveredVersion != null)
                    {
                        discoveryReason = $"HSDS version {discoveredVersion} found at {path}";
                    }
                }

                // 3. Extract schema if required and not yet found
                if (needsSchema && discoveredSchema == null)
                {
                    if (LooksLikeOpenApiSpec(content))
                    {
                        discoveredSchema = content;
                    }
                    else
                    {
                        // Attempt scraping for indirect schema (UI/Config)
                        discoveredSchema = await TryFetchIndirectSchemaAsync(client, content, normalizedBaseUrl, cancellationToken);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.ProbeFailed(ex, TextSanitizer.SanitizeUrlForLogging(normalizedBaseUrl), path);
            }
        }

        // if we get to here with no profile version, use the defaukt.
        if (String.IsNullOrEmpty(discoveredVersion)) throw new InvalidOperationException($"Failed to discover any profile version information from base URL: {baseUrl}");

        return new ProfileDiscoveryResult
        {
            HsdsProfileVersion = discoveredVersion,
            OpenApiSchemaContent = discoveredSchema,
            HsdsProfileReason = discoveryReason ?? "Discovery completed with available information."
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
    private async Task<string?> TryFetchIndirectSchemaAsync(
    HttpClient client,
    string content,
    string baseUrl,
    CancellationToken cancellationToken)
    {
        // 1. Check if the content is a Swagger Config JSON
        var configUrls = DiscoverFromSwaggerConfigContent(content, baseUrl);
        if (configUrls.Count > 0)
        {
            // Try the first URL found in the config
            var spec = await TryFetchDiscoveredSpecContentAsync(client, configUrls[0], cancellationToken);
            if (spec != null) return spec;
        }

        // 2. Check if the content is HTML (Swagger UI / Redoc)
        var htmlSpecUrl = await DiscoverFromUiHtmlAsync(client, content, baseUrl, cancellationToken);
        if (!string.IsNullOrWhiteSpace(htmlSpecUrl))
        {
            return await TryFetchDiscoveredSpecContentAsync(client, htmlSpecUrl, cancellationToken);
        }

        return null;
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
