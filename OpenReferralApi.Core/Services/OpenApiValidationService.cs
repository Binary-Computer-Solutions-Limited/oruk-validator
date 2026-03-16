using System.Text.RegularExpressions;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json.Linq;
using OpenReferralApi.Core.Models.Configuration;
using OpenReferralApi.Core.Models.Endpoints;
using OpenReferralApi.Core.Models.Feeds;
using OpenReferralApi.Core.Models.Schema;
using OpenReferralApi.Core.Models.Security;
using OpenReferralApi.Core.Models.Validation;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging.Abstractions;
using ValidationError = OpenReferralApi.Core.Models.Validation.ValidationError;

namespace OpenReferralApi.Core.Services;

public interface IOpenApiValidationService
{
    Task<OpenApiValidationResult> ValidateOpenApiSpecificationAsync(OpenApiValidationRequest request, CancellationToken cancellationToken = default);
}

public class OpenApiValidationService : IOpenApiValidationService
{
    private static readonly Regex ArrayIndexRegex = new(@"\[[^\]]*\]", RegexOptions.Compiled);
    private static readonly ConcurrentDictionary<string, CachedResolvedSpec> FeedResolvedSpecCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, CachedResolvedSpec> ProfileResolvedSpecCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Meter CacheMetricsMeter = new("OpenReferralApi.Core.OpenApiValidationService", "1.0.0");
    private static readonly Counter<long> ResolvedOpenApiCacheHitsCounter = CacheMetricsMeter.CreateCounter<long>(
        "openreferral.openapi.cache.hits",
        description: "Number of resolved OpenAPI cache hits by scope (feed/profile)");
    private static readonly Counter<long> ResolvedOpenApiCacheMissesCounter = CacheMetricsMeter.CreateCounter<long>(
        "openreferral.openapi.cache.misses",
        description: "Number of resolved OpenAPI cache misses by scope (feed/profile)");
    private static readonly ObservableGauge<int> FeedResolvedOpenApiCacheEntriesGauge = CacheMetricsMeter.CreateObservableGauge<int>(
        "openreferral.openapi.cache.entries.feed",
        () => FeedResolvedSpecCache.Count,
        description: "Number of cached resolved feed OpenAPI specifications");
    private static readonly ObservableGauge<int> ProfileResolvedOpenApiCacheEntriesGauge = CacheMetricsMeter.CreateObservableGauge<int>(
        "openreferral.openapi.cache.entries.profile",
        () => ProfileResolvedSpecCache.Count,
        description: "Number of cached resolved profile OpenAPI specifications");
    private static readonly ObservableGauge<int> ResolvedOpenApiExpiredEntriesGauge = CacheMetricsMeter.CreateObservableGauge<int>(
        "openreferral.openapi.cache.entries.expired",
        CountExpiredCacheEntries,
        description: "Number of expired cached resolved OpenAPI specifications (feed + profile)");

    private readonly ILogger<OpenApiValidationService> _logger;
    private readonly ISchemaResolverService _schemaResolverService;
    private readonly IProfileDiscoveryService _profileDiscoveryService;
    private readonly IOpenApiDiscoveryService _openApiDiscoveryService;
    private readonly IOpenApiBootstrapService _openApiBootstrapService;
    private readonly IOpenApiSpecificationService _openApiSpecificationService;
    private readonly IHsdsComplianceService _hsdsComplianceService;
    private readonly IEndpointTestingService _endpointTestingService;
    private readonly IAuthenticationValidationService _authenticationValidationService;
    private readonly OpenApiSpecFetcher _specFetcher;
    private readonly bool _allowUserSuppliedAuth;
    private readonly bool _profileSchemaCacheEnabled;
    private readonly TimeSpan _profileSchemaCacheTtl;

    public OpenApiValidationService(
        ILogger<OpenApiValidationService> logger,
        HttpClient httpClient,
        IJsonValidatorService jsonValidatorService,
        ISchemaResolverService schemaResolverService,
        IProfileDiscoveryService discoveryService,
        IOpenApiDiscoveryService feedSpecDiscoveryService,
        IOptions<AuthenticationOptions> authOptions,
        IOpenApiSpecificationService? openApiSpecificationService = null,
        IHsdsComplianceService? hsdsComplianceService = null,
        IEndpointTestingService? endpointTestingService = null,
        IAuthenticationValidationService? authenticationValidationService = null,
        IOpenApiBootstrapService? openApiBootstrapService = null,
        IOptions<CacheOptions>? cacheOptions = null,
        IOptions<SpecificationOptions>? specificationOptions = null)
    {
        _logger = logger;
        _schemaResolverService = schemaResolverService;
        _profileDiscoveryService = discoveryService;
        _openApiDiscoveryService = feedSpecDiscoveryService;
        _openApiSpecificationService = openApiSpecificationService ?? new OpenApiSpecificationService(NullLogger<OpenApiSpecificationService>.Instance, jsonValidatorService);
        _hsdsComplianceService = hsdsComplianceService ?? new HsdsComplianceService(jsonValidatorService, specificationOptions);
        _endpointTestingService = endpointTestingService ?? new EndpointTestingService(NullLogger<EndpointTestingService>.Instance, httpClient, jsonValidatorService, _hsdsComplianceService, specificationOptions);
        _authenticationValidationService = authenticationValidationService ?? new AuthenticationValidationService(NullLogger<AuthenticationValidationService>.Instance, authOptions);
        _allowUserSuppliedAuth = authOptions.Value.AllowUserSuppliedAuth;
        var effectiveCacheOptions = cacheOptions?.Value;
        _profileSchemaCacheEnabled = effectiveCacheOptions?.Enabled == true;
        _profileSchemaCacheTtl = effectiveCacheOptions != null && effectiveCacheOptions.ExpirationMinutes > 0
            ? TimeSpan.FromMinutes(effectiveCacheOptions.ExpirationMinutes)
            : TimeSpan.FromHours(2);
        _specFetcher = new OpenApiSpecFetcher(httpClient, logger, schemaResolverService, allowUserSuppliedAuth: _allowUserSuppliedAuth);
        _openApiBootstrapService = openApiBootstrapService ?? new OpenApiBootstrapService(
            _profileDiscoveryService,
            _openApiDiscoveryService,
            NullLogger<OpenApiBootstrapService>.Instance);
    }

    public async Task<OpenApiValidationResult> ValidateOpenApiSpecificationAsync(OpenApiValidationRequest request, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var result = new OpenApiValidationResult();

        try
        {
            _logger.LogInformation("Starting OpenAPI specification testing");

            // Ensure options has default values if not provided
            request.Options ??= new OpenApiValidationOptions();

            // User-supplied authentication for schema and datasource requests is feature-gated
            // and must pass strict validation before it can be applied.
            var schemaRequestAuth = _authenticationValidationService.TryGetValidatedRequestAuthentication("schema", request.OpenApiSchema?.Authentication);
            var dataSourceRequestAuth = _authenticationValidationService.TryGetValidatedRequestAuthentication("datasource", request.DataSourceAuth);

            // Discover OpenAPI schema URL if not provided
            var usedBaseUrlDiscovery = false;
            if (request.OpenApiSchema == null || string.IsNullOrEmpty(request.OpenApiSchema.Url))
            {
                if (!string.IsNullOrEmpty(request.BaseUrl))
                {
                    usedBaseUrlDiscovery = true;
                    var bootstrap = await _openApiBootstrapService.ResolveFromBaseUrlAsync(request.BaseUrl, dataSourceRequestAuth, cancellationToken);
                    var discoveredUrl = bootstrap.OpenApiSchemaUrl;
                    var reason = bootstrap.DiscoveryReason;

                    if (!string.IsNullOrEmpty(discoveredUrl))
                    {
                        if (!bootstrap.UsedDataServiceOpenApi
                            && !string.IsNullOrWhiteSpace(bootstrap.ProfileVersion)
                            && _hsdsComplianceService.TryGetKnownHsdsSchemaUrl(bootstrap.ProfileVersion, out var bootstrapMappedProfileSchemaUrl))
                        {
                            discoveredUrl = bootstrapMappedProfileSchemaUrl;
                            _logger.LogInformation(
                                "No OpenAPI spec found on data service; using profile schema URL {ProfileSchemaUrl} for profile version {ProfileVersion}",
                                SchemaResolverService.SanitizeUrlForLogging(bootstrapMappedProfileSchemaUrl),
                                bootstrap.ProfileVersion);
                        }

                        _logger.LogInformation("Discovered OpenAPI schema URL: {Url} (Reason: {Reason})", SchemaResolverService.SanitizeUrlForLogging(discoveredUrl), reason);
                        request.OpenApiSchema ??= new OpenApiSchema();
                        request.OpenApiSchema.Url = discoveredUrl;
                        request.ProfileReason = bootstrap.ProfileReason;
                    }
                    else
                    {
                        throw new ArgumentException("Failed to discover OpenAPI schema URL from base URL");
                    }
                }
                else
                {
                    throw new ArgumentException("OpenAPI schema URL must be provided or BaseUrl must allow discovery");
                }
            }

            if (string.IsNullOrEmpty(request.OpenApiSchema?.Url))
            {
                throw new ArgumentException("OpenAPI schema URL must be provided or BaseUrl must allow discovery");
            }

            var claimedProfileVersion = _hsdsComplianceService.ExtractClaimedProfileVersion(request.ProfileReason, request.OpenApiSchema.Url);
            JObject? resolvedHsdsProfileSpec = null;
            string? knownHsdsSchemaUrl = null;

            if (_hsdsComplianceService.TryGetKnownHsdsSchemaUrl(claimedProfileVersion, out var mappedProfileSchemaUrl))
            {
                knownHsdsSchemaUrl = mappedProfileSchemaUrl;
                resolvedHsdsProfileSpec = await GetCachedResolvedOpenApiSpecAsync(mappedProfileSchemaUrl, null, cancellationToken, cacheScope: "profile");
            }

            // Always resolve and cache the feed OpenAPI specification before validation/testing.
            JObject openApiSpec;
            try
            {
                openApiSpec = await GetCachedResolvedOpenApiSpecAsync(request.OpenApiSchema.Url, schemaRequestAuth, cancellationToken, cacheScope: "feed");
            }
            catch (Exception ex) when (!string.IsNullOrWhiteSpace(knownHsdsSchemaUrl) && resolvedHsdsProfileSpec != null)
            {
                _logger.LogWarning(
                    ex,
                    "Failed to fetch/resolve OpenAPI from feed URL {FeedSpecUrl}; falling back to HSDS profile schema {ProfileSchemaUrl}",
                    SchemaResolverService.SanitizeUrlForLogging(request.OpenApiSchema.Url),
                    SchemaResolverService.SanitizeUrlForLogging(knownHsdsSchemaUrl));

                openApiSpec = (JObject)resolvedHsdsProfileSpec.DeepClone();
                request.OpenApiSchema.Url = knownHsdsSchemaUrl;
                result.Notifications.Add("Unable to fetch OpenAPI specification from the feed URL. Falling back to the HSDS profile OpenAPI specification.");
            }

            // If discovery could not infer an HSDS profile version, try extracting it from the OpenAPI document itself.
            if (!HasExplicitProfileVersionContext(request.ProfileReason, request.OpenApiSchema?.Url))
            {
                var versionFromSpec = TryExtractProfileVersionFromOpenApiSpec(openApiSpec);
                if (!string.IsNullOrWhiteSpace(versionFromSpec))
                {
                    request.ProfileReason = $"Standard version [user: {versionFromSpec}] read from OpenAPI spec";
                }
                else if (usedBaseUrlDiscovery)
                {
                    request.ProfileReason = "Standard version [user: HSDS-UK-1.0] defaulted (version not found in '/' response or OpenAPI spec)";
                }
            }

            // Validate the OpenAPI specification
            OpenApiSpecificationValidation? specValidation = null;
            if (request.Options.ValidateSpecification)
            {
                specValidation = await _openApiSpecificationService.ValidateAsync(openApiSpec, cancellationToken);
                result.SpecificationValidation = specValidation;
            }

            claimedProfileVersion = _hsdsComplianceService.ExtractClaimedProfileVersion(request.ProfileReason, request.OpenApiSchema?.Url);
            if (resolvedHsdsProfileSpec == null && _hsdsComplianceService.TryGetKnownHsdsSchemaUrl(claimedProfileVersion, out var knownHsdsSchemaUrlAfterDiscovery))
            {
                knownHsdsSchemaUrl = knownHsdsSchemaUrlAfterDiscovery;
                resolvedHsdsProfileSpec = await GetCachedResolvedOpenApiSpecAsync(knownHsdsSchemaUrlAfterDiscovery, null, cancellationToken, cacheScope: "profile");
            }

            // Compare feed specification against the known HSDS baseline profile, when discoverable.
            if (request.Options.ValidateSpecification && specValidation != null)
            {
                if (resolvedHsdsProfileSpec != null)
                {
                    var profileComplianceFindings = _hsdsComplianceService.CompareFeedSpecAgainstHsdsProfile(openApiSpec, resolvedHsdsProfileSpec);
                    if (profileComplianceFindings.Count > 0)
                    {
                        specValidation.Errors = NormalizeAndDeduplicateValidationErrors(
                            specValidation.Errors.Concat(profileComplianceFindings));
                        specValidation.IsValid = !specValidation.Errors.Any(e =>
                            string.Equals(e.Severity, "Error", StringComparison.OrdinalIgnoreCase));
                    }
                }
                else
                {
                    var hasProfileContext = !string.IsNullOrWhiteSpace(request.ProfileReason)
                        || !string.IsNullOrWhiteSpace(request.BaseUrl);

                    if (hasProfileContext)
                    {
                        specValidation.Errors = NormalizeAndDeduplicateValidationErrors(
                            specValidation.Errors.Concat(new[]
                            {
                                new ValidationError
                                {
                                    Path = "profile",
                                    Message = "Unable to map feed profile version to a known HSDS schema for baseline comparison.",
                                    ErrorCode = "HSDS_PROFILE_UNKNOWN",
                                    Severity = "Warning"
                                }
                            }));
                    }
                }
            }

            // Test endpoints if requested
            List<EndpointTestResult> endpointTests = new();
            if (request.Options.TestEndpoints && !string.IsNullOrEmpty(request.BaseUrl))
            {
                endpointTests = await _endpointTestingService.TestEndpointsAsync(openApiSpec, request.BaseUrl, request.Options, dataSourceRequestAuth, request.OpenApiSchema?.Url, cancellationToken);
                result.EndpointTests = endpointTests;

                if (request.Options.HsdsValidationMode == HsdsValidationMode.FullHsdsRuntime)
                {
                    if (resolvedHsdsProfileSpec != null)
                    {
                        await _hsdsComplianceService.ValidateEndpointResponsesAgainstHsdsProfileAsync(endpointTests, resolvedHsdsProfileSpec, request.Options, cancellationToken);
                    }
                    else
                    {
                        result.Notifications.Add("Full HSDS runtime mode requested, but no known HSDS profile schema could be resolved.");
                    }
                }
            }

            // Build summary
            result.Summary = BuildTestSummary(specValidation, endpointTests, request.Options);
            result.IsValid = result.Summary.FailedTests == 0;

            // Set metadata
            result.Metadata = new CommonValidationMetadata
            {
                BaseUrl = request.BaseUrl,
                TestTimestamp = DateTime.UtcNow,
                TestDuration = stopwatch.Elapsed,
                UserAgent = "OpenReferral-Validator/1.0",
                Profile = claimedProfileVersion,
                ProfileReason = request.ProfileReason
            };

            _logger.LogInformation("OpenAPI testing completed. IsValid: {IsValid}, Endpoints: {EndpointCount}",
                result.IsValid, result.EndpointTests.Count);

            // Honor option to exclude response bodies from the produced result (does not affect testing)
            if (!request.Options.IncludeResponseBody && result.EndpointTests != null)
            {
                foreach (var ep in result.EndpointTests)
                {
                    if (ep.TestResults == null) continue;
                    foreach (var tr in ep.TestResults)
                    {
                        tr.ResponseBody = null;
                    }
                }
            }

            // Honor option to exclude test results array from the produced result (does not affect testing)
            if (!request.Options.IncludeTestResults && result.EndpointTests != null)
            {
                foreach (var ep in result.EndpointTests)
                {
                    ep.RefreshFlattenedFields();
                    ep.TestResults.Clear();
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during OpenAPI testing");
            result.IsValid = false;
            result.Summary = new OpenApiValidationSummary();

            if (IsSpecFetchOrResolveFailure(ex))
            {
                var safeSpecUrl = SchemaResolverService.SanitizeUrlForLogging(request.OpenApiSchema?.Url ?? string.Empty);
                var rootMessage = SanitizeExceptionMessage(GetInnermostException(ex).Message);
                var notification = string.IsNullOrEmpty(safeSpecUrl)
                    ? $"Unable to get or resolve the OpenAPI specification. {rootMessage}"
                    : $"Unable to get or resolve the OpenAPI specification from {safeSpecUrl}. {rootMessage}";

                result.Notifications.Add(notification);
            }
        }
        finally
        {
            stopwatch.Stop();
            result.Duration = stopwatch.Elapsed;
        }

        return result;
    }

    private static List<ValidationError> NormalizeAndDeduplicateValidationErrors(IEnumerable<ValidationError> errors)
    {
        var capacity = errors is ICollection<ValidationError> collection ? collection.Count : 0;
        var seenPaths = capacity > 0
            ? new HashSet<string>(capacity, StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
        var deduplicatedErrors = capacity > 0
            ? new List<ValidationError>(capacity)
            : new List<ValidationError>();

        foreach (var error in errors)
        {
            var normalizedPath = NormalizeValidationErrorText(error.Path);

            // Keep the first validation error encountered for each normalized path.
            if (!seenPaths.Add(normalizedPath))
            {
                continue;
            }

            // Normalize message only for kept entries to avoid work for discarded duplicates.
            deduplicatedErrors.Add(new ValidationError
            {
                Path = normalizedPath,
                Message = NormalizeValidationErrorText(error.Message),
                ErrorCode = error.ErrorCode,
                Severity = error.Severity,
                LineNumber = error.LineNumber,
                ColumnNumber = error.ColumnNumber
            });
        }

        return deduplicatedErrors;
    }

    private async Task<JObject> GetCachedResolvedOpenApiSpecAsync(
        string specUrl,
        DataSourceAuthentication? auth,
        CancellationToken cancellationToken,
        string cacheScope)
    {
        var cache = ResolveCacheByScope(cacheScope);
        var cacheKey = $"resolved-openapi:{specUrl}";

        if (cache.TryGetValue(cacheKey, out var cachedEntry)
            && cachedEntry.ExpiresAtUtc > DateTime.UtcNow
            && !string.IsNullOrWhiteSpace(cachedEntry.ResolvedSpecJson))
        {
            ResolvedOpenApiCacheHitsCounter.Add(1, new KeyValuePair<string, object?>("scope", cacheScope));
            _logger.LogDebug(
                "Resolved OpenAPI cache hit (scope: {CacheScope}) for URL {SpecUrl}",
                cacheScope,
                SchemaResolverService.SanitizeUrlForLogging(specUrl));
            return JObject.Parse(cachedEntry.ResolvedSpecJson);
        }

        ResolvedOpenApiCacheMissesCounter.Add(1, new KeyValuePair<string, object?>("scope", cacheScope));
        _logger.LogDebug(
            "Resolved OpenAPI cache miss (scope: {CacheScope}) for URL {SpecUrl}",
            cacheScope,
            SchemaResolverService.SanitizeUrlForLogging(specUrl));

        if (string.Equals(cacheScope, "profile", StringComparison.Ordinal))
        {
            var warmupSchemaRef = $$"""
            {
              "$ref": "{{specUrl}}"
            }
            """;

            try
            {
                var resolvedFromWarmup = await _schemaResolverService.ResolveAsync(warmupSchemaRef, specUrl, auth: null);
                var resolvedFromWarmupObject = JObject.Parse(resolvedFromWarmup);

                if (!IsLikelyOpenApiDocument(resolvedFromWarmupObject))
                {
                    throw new InvalidOperationException("Warmup-path resolution did not produce an OpenAPI document.");
                }

                if (_profileSchemaCacheEnabled)
                {
                    cache[cacheKey] = new CachedResolvedSpec(
                        resolvedFromWarmup,
                        DateTime.UtcNow.Add(_profileSchemaCacheTtl));
                }

                _logger.LogDebug(
                    "Resolved HSDS profile OpenAPI via schema resolver warmup path for URL {SpecUrl}",
                    SchemaResolverService.SanitizeUrlForLogging(specUrl));

                return resolvedFromWarmupObject;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(
                    ex,
                    "Warmup-path resolution unavailable for HSDS profile URL {SpecUrl}; falling back to direct fetch",
                    SchemaResolverService.SanitizeUrlForLogging(specUrl));
            }
        }

        var unresolvedSpec = await _specFetcher.FetchOpenApiSpecFromUrlAsync(
            specUrl,
            auth,
            cancellationToken,
            resolveReferences: false);

        var resolvedSpecContent = await _schemaResolverService.ResolveAsync(unresolvedSpec.ToString(), specUrl, auth);

        if (_profileSchemaCacheEnabled)
        {
            cache[cacheKey] = new CachedResolvedSpec(
                resolvedSpecContent,
                DateTime.UtcNow.Add(_profileSchemaCacheTtl));
        }

        return JObject.Parse(resolvedSpecContent);
    }

    private static ConcurrentDictionary<string, CachedResolvedSpec> ResolveCacheByScope(string cacheScope)
    {
        return cacheScope switch
        {
            "feed" => FeedResolvedSpecCache,
            "profile" => ProfileResolvedSpecCache,
            _ => throw new ArgumentOutOfRangeException(nameof(cacheScope), cacheScope, "Cache scope must be either 'feed' or 'profile'.")
        };
    }

    private sealed record CachedResolvedSpec(string ResolvedSpecJson, DateTime ExpiresAtUtc);

    private static bool IsLikelyOpenApiDocument(JObject candidate)
    {
        return candidate.ContainsKey("openapi")
            || candidate.ContainsKey("swagger")
            || candidate.ContainsKey("paths");
    }

    private static int CountExpiredCacheEntries()
    {
        var now = DateTime.UtcNow;
        var expiredFeedEntries = FeedResolvedSpecCache.Values.Count(entry => entry.ExpiresAtUtc <= now);
        var expiredProfileEntries = ProfileResolvedSpecCache.Values.Count(entry => entry.ExpiresAtUtc <= now);
        return expiredFeedEntries + expiredProfileEntries;
    }

    private bool HasExplicitProfileVersionContext(string? profileReason, string? schemaUrl)
    {
        return !string.IsNullOrWhiteSpace(_hsdsComplianceService.ExtractClaimedProfileVersion(profileReason, schemaUrl));
    }

    private static string? TryExtractProfileVersionFromOpenApiSpec(JObject openApiSpec)
    {
        var candidateTokens = new[]
        {
            "x-hsds-version",
            "version",
            "info.x-hsds-version",
            "info.x-profile-version"
        };

        foreach (var tokenPath in candidateTokens)
        {
            var tokenValue = openApiSpec.SelectToken(tokenPath)?.ToString();
            var normalized = NormalizeVersionToken(tokenValue);
            if (!string.IsNullOrWhiteSpace(normalized))
            {
                return normalized;
            }
        }

        return null;
    }

    private static string? NormalizeVersionToken(string? rawVersion)
    {
        return ProfileVersionNormalizer.NormalizeVersionNumber(rawVersion);
    }
    private static string NormalizeValidationErrorText(string? input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return string.Empty;
        }

        if (input.IndexOf('[') < 0)
        {
            return input;
        }

        return ArrayIndexRegex.Replace(input, string.Empty);
    }

    /// <summary>
    /// Sanitizes exception messages to prevent log injection attacks by removing control characters.
    /// </summary>
    private static string SanitizeExceptionMessage(string message)
    {
        if (string.IsNullOrEmpty(message))
            return string.Empty;

        // Remove control characters (including CR/LF) to prevent log forging
        var sanitized = new string(message.Where(c => !char.IsControl(c)).ToArray());

        // Limit length to prevent log flooding
        const int maxLength = 500;
        if (sanitized.Length > maxLength)
        {
            sanitized = sanitized.Substring(0, maxLength) + "...(truncated)";
        }

        return sanitized;
    }

    private static Exception GetInnermostException(Exception exception)
    {
        var current = exception;
        while (current.InnerException != null)
        {
            current = current.InnerException;
        }

        return current;
    }

    private static bool IsSpecFetchOrResolveFailure(Exception ex)
    {
        var current = ex;
        while (current != null)
        {
            if (current is HttpRequestException)
            {
                return true;
            }

            var message = current.Message ?? string.Empty;
            if (message.Contains("Failed to fetch OpenAPI specification", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("resolve", StringComparison.OrdinalIgnoreCase) &&
                message.Contains("reference", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            current = current.InnerException;
        }

        return false;
    }

    private OpenApiValidationSummary BuildTestSummary(OpenApiSpecificationValidation? specValidation, List<EndpointTestResult> endpointTests, OpenApiValidationOptions options)
    {
        var shouldIgnoreOptionalFailures = options.TestOptionalEndpoints && options.TreatOptionalEndpointsAsWarnings;
        var failedTests = endpointTests.Count(e =>
            (e.Status == EndpointTestStatus.FailedValidation || e.Status == EndpointTestStatus.Error) &&
            !(shouldIgnoreOptionalFailures && e.IsOptional));
        var specificationFailures = specValidation?.Errors.Count(e =>
            string.Equals(e.Severity, "Error", StringComparison.OrdinalIgnoreCase)) ?? 0;

        var summary = new OpenApiValidationSummary
        {
            TotalEndpoints = endpointTests.Count,
            TestedEndpoints = endpointTests.Count(e => e.IsTested),
            SuccessfulTests = endpointTests.Count(e => e.Status == EndpointTestStatus.PassedValidation),
            FailedTests = failedTests + specificationFailures,
            SkippedTests = endpointTests.Count(e => e.Status == EndpointTestStatus.NotTested || e.Status == EndpointTestStatus.Skipped),
            TotalRequests = endpointTests.Sum(e => e.TestResults.Count),
            SpecificationValid = specValidation?.IsValid ?? true
        };

        var responseTimes = endpointTests
            .SelectMany(e => e.TestResults)
            .Where(r => r.ResponseTime > TimeSpan.Zero)
            .Select(r => r.ResponseTime);

        if (responseTimes.Any())
        {
            summary.AverageResponseTime = TimeSpan.FromMilliseconds(responseTimes.Average(rt => rt.TotalMilliseconds));
        }

        return summary;
    }

}
