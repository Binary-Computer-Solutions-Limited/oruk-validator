using Microsoft.Extensions.Logging;
using System.Text.Json;
using OpenReferralApi.Core.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Caching.Memory;
using OpenReferralApi.Core.Helpers;
using OpenReferralApi.Core.Extensions;
using System.Text.RegularExpressions;
using System.Net.Http.Headers;
using System.Text;
namespace OpenReferralApi.Core.Services;

public interface IProfileDiscoveryService
{
    Task<ProfileDiscoveryResult> DiscoverFromBaseUrlAsync(
        string? ownSchemaUrl,
    string? baseUrl,
    string? profileReason = null,
        DataSourceAuthentication? authentication = null,
        CancellationToken cancellationToken = default);
}

public sealed class ProfileDiscoveryResult
{
    public string? HsdsProfileReason { get; init; }
    public string? HsdsProfileSchemaUrl { get; init; }
    public string? OpenApiSchemaContent { get; init; }
    public string? HsdsProfileSchemaContent { get; init; }
    public string? HsdsProfileVersion { get; init; }
    public bool UsedDefaultProfile { get; init; }
}

public class ProfileDiscoveryService : IProfileDiscoveryService
{

    private readonly ILogger<ProfileDiscoveryService> _logger;
    private readonly OpenApiValidationServerOptions _openApiValidationOptions;

    private readonly IHttpClientFactory _httpClientFactory;

    private readonly SpecificationOptions _specificationOptions;
    private readonly IMemoryCache? _memoryCache;

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
        IOptions<OpenApiValidationServerOptions>? openApiValidationOptions = null,
        IMemoryCache? memoryCache = null)
    {
        _logger = logger;
        _openApiValidationOptions = openApiValidationOptions?.Value ?? new OpenApiValidationServerOptions();
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _specificationOptions = specificationOptions?.Value ?? throw new ArgumentNullException(nameof(specificationOptions));
        _memoryCache = memoryCache;
    }

    public async Task<ProfileDiscoveryResult> DiscoverFromBaseUrlAsync(
        string? ownSchemaUrl,
        string? baseUrl,
        string? profileReason = null,
        DataSourceAuthentication? authentication = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new ArgumentException("Base URL must be provided", nameof(baseUrl));
        }

        var normalizedBaseUrl = baseUrl?.TrimEnd('/');
        var needsSchema = _openApiValidationOptions.OwnSchemaValidation != OwnSchemaValidationMode.None;

        string? discoveredVersion = null;
        string? discoveredSchema = null;
        string? discoveryReason = null;
        bool usedDefaultProfile = false;
        
        if (!string.IsNullOrWhiteSpace(normalizedBaseUrl))
        {
            using var client = _httpClientFactory.CreateClient("OpenApiValidationService");
            var probePaths = BuildDiscoveryProbePaths(ownSchemaUrl);

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
                            discoveryReason = $"HSDS version {discoveredVersion} discovered from base URL at {path}";
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
        }

        if (string.IsNullOrWhiteSpace(discoveredVersion))
        {
            discoveredVersion = TryExtractProfileVersionFromProfileReason(profileReason);
            if (!string.IsNullOrWhiteSpace(discoveredVersion))
            {
                discoveryReason = $"HSDS version {discoveredVersion} extracted from profile reason";
            }
        }

        if (string.IsNullOrWhiteSpace(discoveredVersion))
        {
            discoveredVersion = TryExtractProfileVersionFromSchemaUrl(ownSchemaUrl);
            if (!string.IsNullOrWhiteSpace(discoveredVersion))
            {
                discoveryReason = $"HSDS version {discoveredVersion} extracted from schema URL";
            }
        }

        // if we get to here with no profile version, use the default if one is configured.
        if (string.IsNullOrWhiteSpace(discoveredVersion))
        {
            var hasConfiguredDefaultProfile = TryGetDefaultProfileSchemaFallback(
                out _,
                out var defaultProfileVersion);

            if (hasConfiguredDefaultProfile)
            {
                usedDefaultProfile = true;
                discoveredVersion = defaultProfileVersion;
                discoveryReason = $"Using configured default HSDS profile version: {defaultProfileVersion}";
            }
            else
            {
                throw new ArgumentException(
                    "Can only validate against known profile versions. No HSDS profile version was discovered from the base URL and no default profile is configured.");
            }
        }

        if (!TryGetSchemaUrlForProfileVersion(discoveredVersion!, out var hsdsProfileSchemaUrl))
        {
            throw new ArgumentException(
                $"Can only validate against known profile versions. Discovered profile '{discoveredVersion}' is not supported.");
        }

        var hsdsProfileSchemaContent = TryGetHsdsProfileSchemaContentFromCache(discoveredVersion);
        if (string.IsNullOrWhiteSpace(hsdsProfileSchemaContent))
        {
            throw new ArgumentException(
                $"Can only validate against known profile versions. Schema for profile '{discoveredVersion}' is not available in cache.");
        }

        return new ProfileDiscoveryResult
        {
            HsdsProfileVersion = discoveredVersion,
            HsdsProfileSchemaUrl = hsdsProfileSchemaUrl,
            OpenApiSchemaContent = discoveredSchema,
            HsdsProfileSchemaContent = hsdsProfileSchemaContent,
            HsdsProfileReason = discoveryReason ?? "Discovery completed with available information.",
            UsedDefaultProfile = usedDefaultProfile
        };
    }

    private string? TryGetHsdsProfileSchemaContentFromCache(string? hsdsProfileVersion)
    {
        if (_memoryCache == null)
        {
            return null;
        }

        var profileVersion = hsdsProfileVersion?.Trim();
        if (string.IsNullOrWhiteSpace(profileVersion))
        {
            return null;
        }

        if (!TryGetSchemaUrlForProfileVersion(profileVersion, out var schemaUrl)
            || string.IsNullOrWhiteSpace(schemaUrl))
        {
            return null;
        }

        foreach (var cacheKey in GetSchemaCacheKeyCandidates(schemaUrl))
        {
            if (_memoryCache.TryGetValue<string>(cacheKey, out var schemaContent)
                && !string.IsNullOrWhiteSpace(schemaContent))
            {
                return schemaContent;
            }
        }

        return null;
    }

    private bool TryGetSchemaUrlForProfileVersion(string hsdsProfileVersion, out string schemaUrl)
    {
        schemaUrl = string.Empty;

        if (_specificationOptions.Urls.TryGetValue(hsdsProfileVersion, out var directUrl)
            && !string.IsNullOrWhiteSpace(directUrl))
        {
            schemaUrl = directUrl;
            return true;
        }

        foreach (var mapping in _specificationOptions.Urls)
        {
            if (string.Equals(mapping.Key, hsdsProfileVersion, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(mapping.Value))
            {
                schemaUrl = mapping.Value;
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<string> GetSchemaCacheKeyCandidates(string schemaUrl)
    {
        yield return $"schema:{schemaUrl}";

        if (Uri.TryCreate(schemaUrl, UriKind.Absolute, out var schemaUri))
        {
            var normalizedUrl = schemaUri.GetLeftPart(UriPartial.Path).TrimEnd('/');
            if (!string.Equals(normalizedUrl, schemaUrl, StringComparison.Ordinal))
            {
                yield return $"schema:{normalizedUrl}";
            }
        }
    }

    private static IReadOnlyList<string> BuildDiscoveryProbePaths(string? ownSchemaUrl = null)
    {
        var paths = ExpandSpecPaths(Constants.OpenApiDocumentProbePaths)
            .Concat(Constants.SwaggerConfigProbePaths)
            .Concat(Constants.DocumentationUiProbePaths)
            .Distinct(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(ownSchemaUrl))
        {
            paths = new[] { ownSchemaUrl }.Concat(paths).Distinct(StringComparer.OrdinalIgnoreCase);
        }

        return paths.ToArray();
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
        if (string.IsNullOrWhiteSpace(relativePath))
            return baseUrl;

        if (Uri.IsWellFormedUriString(relativePath, UriKind.Absolute))
            return relativePath;

        return $"{baseUrl}/{relativePath.TrimStart('/')}";
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

    private static string? TryExtractProfileVersionFromProfileReason(string? profileReason)
    {
        if (string.IsNullOrWhiteSpace(profileReason))
        {
            return null;
        }

        var match = Regex.Match(
            profileReason,
            @"Standard version \[user:\s*(?<profile>[^\]]+)\]",
            RegexOptions.IgnoreCase);

        if (!match.Success)
        {
            return null;
        }

        var extracted = match.Groups["profile"].Value.Trim();
        return string.IsNullOrWhiteSpace(extracted) ? null : extracted;
    }

    private static string? TryExtractProfileVersionFromSchemaUrl(string? schemaUrl)
    {
        if (string.IsNullOrWhiteSpace(schemaUrl))
        {
            return null;
        }

        var match = Regex.Match(
            schemaUrl,
            @"/specifications/(?<version>[^/]+)/openapi\.json",
            RegexOptions.IgnoreCase);

        if (!match.Success)
        {
            return null;
        }

        var extracted = match.Groups["version"].Value.Trim();
        return string.IsNullOrWhiteSpace(extracted) ? null : extracted;
    }

    private bool TryGetDefaultProfileSchemaFallback(out string schemaUrl, out string? profileVersion)
    {
        schemaUrl = string.Empty;
        profileVersion = null;

        if (string.IsNullOrWhiteSpace(_specificationOptions.DefaultProfileVersion) || _specificationOptions.Urls.Count == 0)
        {
            return false;
        }

        var configuredDefaultKey = _specificationOptions.DefaultProfileVersion.Trim();
        if (!_specificationOptions.Urls.TryGetValue(configuredDefaultKey, out var configuredDefaultSchemaUrl)
            || string.IsNullOrWhiteSpace(configuredDefaultSchemaUrl)
            || !Uri.IsWellFormedUriString(configuredDefaultSchemaUrl, UriKind.Absolute))
        {
            _logger.InvalidDefaultProfileVersion(TextSanitizer.SanitizeStringForLogging(configuredDefaultKey));
            return false;
        }

        schemaUrl = configuredDefaultSchemaUrl;
        profileVersion = configuredDefaultKey;

        return true;
    }


}
