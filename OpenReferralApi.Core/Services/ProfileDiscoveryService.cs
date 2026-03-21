using System.Net.Http.Headers;
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
}

public class ProfileDiscoveryService : IProfileDiscoveryService
{
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
}
