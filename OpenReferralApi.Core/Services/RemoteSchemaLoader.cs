using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenReferralApi.Core.Models;

namespace OpenReferralApi.Core.Services;

/// <summary>
/// Internal helper class for loading remote JSON schemas with caching and authentication support.
/// </summary>
internal partial class RemoteSchemaLoader
{
    private readonly HashSet<string> _knownJsonSchemaUrls;
    private readonly HashSet<string> _unknownDraftWarnings = new(StringComparer.OrdinalIgnoreCase);
    private readonly bool _warnOnUnknownJsonSchemaDraft;

    private readonly HttpClient _httpClient;
    private readonly ILogger _logger;
    private readonly IMemoryCache _memoryCache;
    private readonly CacheOptions _cacheOptions;
    private readonly string? _localSpecificationBaseUrl;
    private IAuthenticationConfig? _auth;

    public RemoteSchemaLoader(
        HttpClient httpClient,
        ILogger logger,
        IMemoryCache memoryCache,
        IOptions<CacheOptions> cacheOptions,
        string? localSpecificationBaseUrl = null,
        IEnumerable<string>? knownJsonSchemaUrls = null,
        bool warnOnUnknownJsonSchemaDraft = true)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _memoryCache = memoryCache ?? throw new ArgumentNullException(nameof(memoryCache));
        _cacheOptions = cacheOptions?.Value ?? throw new ArgumentNullException(nameof(cacheOptions));
        _localSpecificationBaseUrl = localSpecificationBaseUrl;
        _warnOnUnknownJsonSchemaDraft = warnOnUnknownJsonSchemaDraft;
        _knownJsonSchemaUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var configuredUrls = knownJsonSchemaUrls ?? SchemaResolutionOptionsDefaults.KnownJsonSchemaUrls;
        foreach (var url in configuredUrls)
        {
            var normalized = NormalizeAbsoluteUrl(url);
            if (!string.IsNullOrWhiteSpace(normalized))
            {
                _knownJsonSchemaUrls.Add(normalized);
            }
        }
    }

    /// <summary>
    /// Sets the authentication configuration for this loader instance.
    /// </summary>
    public void SetAuthentication(IAuthenticationConfig? auth)
    {
        _auth = auth;
    }

    /// <summary>
    /// Loads a remote JSON schema from a URL with caching support.
    /// </summary>
    public async Task<JsonNode?> LoadRemoteSchemaAsync(string schemaUrl)
    {
        var normalizedKnownSchemaUrl = NormalizeKnownSchemaUrl(schemaUrl);

        // Rewrite URL if needed (e.g., redirect openreferraluk.org URLs to local server)
        var rewrittenUrl = normalizedKnownSchemaUrl ?? RewriteSchemaUrl(schemaUrl);
        
        // Check persistent cache first if caching is enabled
        if (_cacheOptions.Enabled)
        {
            var cacheKey = GenerateCacheKey(rewrittenUrl);
            if (_memoryCache.TryGetValue<string>(cacheKey, out var cachedContent) && cachedContent != null)
            {
                LogSchemaFromCache(_logger, SchemaResolverService.SanitizeUrlForLogging(rewrittenUrl));
                return JsonNode.Parse(cachedContent);
            }
        }

        try
        {
            // Validate URL before making HTTP request to prevent SSRF attacks
            if (!Uri.TryCreate(rewrittenUrl, UriKind.Absolute, out var schemaUri) ||
                (schemaUri.Scheme != Uri.UriSchemeHttp && schemaUri.Scheme != Uri.UriSchemeHttps))
            {
                throw new ArgumentException($"Invalid schema URL: Only HTTP and HTTPS URLs are allowed", nameof(schemaUrl));
            }

            if (rewrittenUrl != schemaUrl)
            {
                LogRewrittenSchemaUrl(_logger,
                    SchemaResolverService.SanitizeUrlForLogging(schemaUrl),
                    SchemaResolverService.SanitizeUrlForLogging(rewrittenUrl));
            }

            LogFetchingRemoteSchema(_logger, SchemaResolverService.SanitizeUrlForLogging(rewrittenUrl));

            using var request = new HttpRequestMessage(HttpMethod.Get, rewrittenUrl);

            // Apply authentication only if the configuration is considered valid
            if (_auth != null && IsValidAuthentication(_auth))
            {
                ApplyAuthentication(request, _auth);
            }

            var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();
            var content = await response.Content.ReadAsStringAsync();

            // Store in persistent cache if caching is enabled
            if (_cacheOptions.Enabled)
            {
                var cacheKey = GenerateCacheKey(rewrittenUrl);
                var cacheEntryOptions = new MemoryCacheEntryOptions
                {
                    Size = content.Length,
                    Priority = CacheItemPriority.Normal
                };

                // Configure expiration
                if (_cacheOptions.ExpirationMinutes > 0)
                {
                    if (_cacheOptions.UseSlidingExpiration)
                    {
                        cacheEntryOptions.SlidingExpiration = TimeSpan.FromMinutes(_cacheOptions.SlidingExpirationMinutes);
                        cacheEntryOptions.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(_cacheOptions.ExpirationMinutes);
                    }
                    else
                    {
                        cacheEntryOptions.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(_cacheOptions.ExpirationMinutes);
                    }
                }

                _memoryCache.Set(cacheKey, content, cacheEntryOptions);
                LogCachedSchema(_logger, SchemaResolverService.SanitizeUrlForLogging(rewrittenUrl), _cacheOptions.ExpirationMinutes);
            }

            return JsonNode.Parse(content);
        }
        catch (Exception ex)
        {
            LogFetchRemoteSchemaFailed(_logger, ex, SchemaResolverService.SanitizeUrlForLogging(schemaUrl));
            throw;
        }
    }

    /// <summary>
    /// Applies authentication credentials to an HTTP request.
    /// </summary>
    private void ApplyAuthentication(HttpRequestMessage request, IAuthenticationConfig auth)
    {
        // Validate authentication data early to prevent propagation of tainted values
        if (auth == null)
            return;

        // Apply API Key authentication
        if (!string.IsNullOrEmpty(auth.ApiKey) && !string.IsNullOrEmpty(auth.ApiKeyHeader))
        {
            // Validate header name before using it to prevent header injection
            if (!IsValidHeaderName(auth.ApiKeyHeader))
            {
                LogInvalidApiKeyHeader(_logger);
                return;
            }
            request.Headers.Add(auth.ApiKeyHeader, auth.ApiKey);
            LogAppliedApiKeyAuth(_logger);
        }

        // Apply Bearer Token authentication
        if (!string.IsNullOrEmpty(auth.BearerToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", auth.BearerToken);
            LogAppliedBearerTokenAuth(_logger);
        }

        // Apply Basic authentication
        if (auth.BasicAuth != null && !string.IsNullOrEmpty(auth.BasicAuth.Username) && !string.IsNullOrEmpty(auth.BasicAuth.Password))
        {
            var credentials = Convert.ToBase64String(
                Encoding.ASCII.GetBytes($"{auth.BasicAuth.Username}:{auth.BasicAuth.Password}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
            LogAppliedBasicAuth(_logger);
        }

        // Apply custom headers
        if (auth.CustomHeaders != null)
        {
            foreach (var header in auth.CustomHeaders)
            {
                // Validate header name
                if (!IsValidHeaderName(header.Key))
                {
                    LogInvalidCustomHeaderName(_logger, header.Key);
                    continue;
                }
                request.Headers.Add(header.Key, header.Value);
                LogAppliedCustomHeader(_logger, header.Key);
            }
        }
    }

    /// <summary>
    /// Validates that authentication configuration is present and properly formed.
    /// </summary>
    private static bool IsValidAuthentication(IAuthenticationConfig? auth)
    {
        if (auth == null)
        {
            return false;
        }

        // Validate that at least one authentication method is configured
        var hasApiKey = !string.IsNullOrEmpty(auth.ApiKey) && !string.IsNullOrEmpty(auth.ApiKeyHeader);
        var hasBearerToken = !string.IsNullOrEmpty(auth.BearerToken);
        var hasBasicAuth = auth.BasicAuth != null && !string.IsNullOrEmpty(auth.BasicAuth.Username);
        var hasCustomHeaders = auth.CustomHeaders != null && auth.CustomHeaders.Count > 0;

        return hasApiKey || hasBearerToken || hasBasicAuth || hasCustomHeaders;
    }

    /// <summary>
    /// Validates HTTP header names to prevent header injection attacks.
    /// </summary>
    private static bool IsValidHeaderName(string headerName)
    {
        if (string.IsNullOrWhiteSpace(headerName))
        {
            return false;
        }

        // Header names must not contain control characters or colons
        // RFC 7230 section 3.2: header-field = field-name ":" OWS field-value OWS
        return !headerName.Any(c => char.IsControl(c) || c == ':' || c == '\r' || c == '\n');
    }

    /// <summary>
    /// Generates a cache key for a schema URL.
    /// </summary>
    private static string GenerateCacheKey(string schemaUrl)
    {
        return $"schema:{schemaUrl}";
    }

    /// <summary>
    /// Rewrites a schema URL if it points to a known remote specification server
    /// and a local specification base URL is configured.
    /// This allows development environments to use local schema files instead of remote ones.
    /// </summary>
    /// <param name="schemaUrl">The original schema URL.</param>
    /// <returns>The rewritten URL or the original URL if no rewriting is needed.</returns>
    private string RewriteSchemaUrl(string schemaUrl)
    {
        if (string.IsNullOrWhiteSpace(_localSpecificationBaseUrl))
        {
            return schemaUrl;
        }

        // Rewrite openreferraluk.org URLs to use the local specification server
        const string remoteSpecificationBase = "https://openreferraluk.org/specifications/";
        
        if (schemaUrl.StartsWith(remoteSpecificationBase, StringComparison.OrdinalIgnoreCase))
        {
            var relativePath = schemaUrl.Substring(remoteSpecificationBase.Length);
            var localUrl = $"{_localSpecificationBaseUrl.TrimEnd('/')}/{relativePath}";
            return localUrl;
        }

        return schemaUrl;
    }

    private string? NormalizeKnownSchemaUrl(string schemaUrl)
    {
        var normalized = NormalizeAbsoluteUrl(schemaUrl);
        if (normalized == null)
        {
            return null;
        }

        if (_knownJsonSchemaUrls.Contains(normalized))
        {
            return normalized;
        }

        if (_warnOnUnknownJsonSchemaDraft &&
            IsJsonSchemaDraftUrl(normalized) &&
            _unknownDraftWarnings.Add(normalized))
        {
            LogUnknownJsonSchemaDraft(_logger, SchemaResolverService.SanitizeUrlForLogging(normalized));
        }

        return null;
    }

    private static string? NormalizeAbsoluteUrl(string schemaUrl)
    {
        if (!Uri.TryCreate(schemaUrl, UriKind.Absolute, out var uri))
        {
            return null;
        }

        return uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
    }

    private static bool IsJsonSchemaDraftUrl(string absoluteUrl)
    {
        if (!Uri.TryCreate(absoluteUrl, UriKind.Absolute, out var uri))
        {
            return false;
        }

        return string.Equals(uri.Host, "json-schema.org", StringComparison.OrdinalIgnoreCase) &&
               uri.AbsolutePath.StartsWith("/draft/", StringComparison.OrdinalIgnoreCase);
    }

    private static class SchemaResolutionOptionsDefaults
    {
        public static readonly string[] KnownJsonSchemaUrls =
        [
            "https://json-schema.org/draft/2020-12/schema",
            "https://json-schema.org/draft/2020-12/meta/core",
            "https://json-schema.org/draft/2020-12/meta/applicator",
            "https://json-schema.org/draft/2020-12/meta/unevaluated",
            "https://json-schema.org/draft/2020-12/meta/validation",
            "https://json-schema.org/draft/2020-12/meta/meta-data",
            "https://json-schema.org/draft/2020-12/meta/format-annotation",
            "https://json-schema.org/draft/2020-12/meta/content"
        ];
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Debug, Message = "Retrieved schema from cache: {SchemaUrl}")]
    private static partial void LogSchemaFromCache(ILogger logger, string schemaUrl);

    [LoggerMessage(EventId = 2, Level = LogLevel.Debug, Message = "Rewritten schema URL from {OriginalUrl} to {RewrittenUrl}")]
    private static partial void LogRewrittenSchemaUrl(ILogger logger, string originalUrl, string rewrittenUrl);

    [LoggerMessage(EventId = 3, Level = LogLevel.Debug, Message = "Fetching remote schema: {SchemaUrl}")]
    private static partial void LogFetchingRemoteSchema(ILogger logger, string schemaUrl);

    [LoggerMessage(EventId = 4, Level = LogLevel.Debug, Message = "Cached schema: {SchemaUrl} (expires in {Minutes} minutes)")]
    private static partial void LogCachedSchema(ILogger logger, string schemaUrl, int minutes);

    [LoggerMessage(EventId = 5, Level = LogLevel.Error, Message = "Failed to fetch remote schema: {SchemaUrl}")]
    private static partial void LogFetchRemoteSchemaFailed(ILogger logger, Exception ex, string schemaUrl);

    [LoggerMessage(EventId = 6, Level = LogLevel.Warning, Message = "Invalid API key header name provided, skipping API key authentication")]
    private static partial void LogInvalidApiKeyHeader(ILogger logger);

    [LoggerMessage(EventId = 7, Level = LogLevel.Debug, Message = "Applied API Key authentication")]
    private static partial void LogAppliedApiKeyAuth(ILogger logger);

    [LoggerMessage(EventId = 8, Level = LogLevel.Debug, Message = "Applied Bearer Token authentication")]
    private static partial void LogAppliedBearerTokenAuth(ILogger logger);

    [LoggerMessage(EventId = 9, Level = LogLevel.Debug, Message = "Applied Basic authentication")]
    private static partial void LogAppliedBasicAuth(ILogger logger);

    [LoggerMessage(EventId = 10, Level = LogLevel.Warning, Message = "Invalid custom header name provided: {HeaderName}")]
    private static partial void LogInvalidCustomHeaderName(ILogger logger, string headerName);

    [LoggerMessage(EventId = 11, Level = LogLevel.Debug, Message = "Applied custom header: {HeaderName}")]
    private static partial void LogAppliedCustomHeader(ILogger logger, string headerName);

    [LoggerMessage(EventId = 12, Level = LogLevel.Warning, Message = "Encountered json-schema.org draft URL not present in configured known schema list: {SchemaUrl}")]
    private static partial void LogUnknownJsonSchemaDraft(ILogger logger, string schemaUrl);
}
