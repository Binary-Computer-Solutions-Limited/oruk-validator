using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Newtonsoft.Json.Linq;
using OpenReferralApi.Core.Logging;
using ValidationError = OpenReferralApi.Core.Models.Validation.ValidationError;

namespace OpenReferralApi.Core.Services;

public interface IOpenApiValidationService
{
    Task<OpenApiValidationResult> ValidateOpenApiSpecificationAsync(OpenApiValidationRequest request, CancellationToken cancellationToken = default);
}

public class OpenApiValidationService : IOpenApiValidationService
{
    private const string ValidationMetricsMeterName = "OpenReferralApi.Core.OpenApiValidationService";
    private static readonly ConcurrentDictionary<string, CachedResolvedSpec> FeedResolvedSpecCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, CachedResolvedSpec> ProfileResolvedSpecCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Meter CacheMetricsMeter = new(ValidationMetricsMeterName, "1.0.0");
    private static readonly Counter<long> ResolvedOpenApiCacheHitsCounter = CacheMetricsMeter.CreateCounter<long>(
        "openreferral.openapi.cache.hits",
        description: "Number of resolved OpenAPI cache hits by scope (feed/profile)");
    private static readonly Counter<long> ResolvedOpenApiCacheMissesCounter = CacheMetricsMeter.CreateCounter<long>(
        "openreferral.openapi.cache.misses",
        description: "Number of resolved OpenAPI cache misses by scope (feed/profile)");
    private static readonly ObservableGauge<int> FeedResolvedOpenApiCacheEntriesGauge = CacheMetricsMeter.CreateObservableGauge(
        "openreferral.openapi.cache.entries.feed",
        () => FeedResolvedSpecCache.Count,
        description: "Number of cached resolved feed OpenAPI specifications");
    private static readonly ObservableGauge<int> ProfileResolvedOpenApiCacheEntriesGauge = CacheMetricsMeter.CreateObservableGauge(
        "openreferral.openapi.cache.entries.profile",
        () => ProfileResolvedSpecCache.Count,
        description: "Number of cached resolved profile OpenAPI specifications");
    private static readonly ObservableGauge<int> ResolvedOpenApiExpiredEntriesGauge = CacheMetricsMeter.CreateObservableGauge(
        "openreferral.openapi.cache.entries.expired",
        CountExpiredCacheEntries,
        description: "Number of expired cached resolved OpenAPI specifications (feed + profile)");
    private static readonly Histogram<long> ValidationManagedHeapBytesHistogram = CacheMetricsMeter.CreateHistogram<long>(
        "openreferral.openapi.validation.memory.managed_heap_bytes",
        unit: "By",
        description: "Managed heap size observed at validation memory checkpoints");
    private static readonly Histogram<long> ValidationManagedHeapDeltaBytesHistogram = CacheMetricsMeter.CreateHistogram<long>(
        "openreferral.openapi.validation.memory.managed_heap_delta_bytes",
        unit: "By",
        description: "Managed heap delta between validation memory checkpoints");
    private static readonly Histogram<long> ValidationWorkingSetBytesHistogram = CacheMetricsMeter.CreateHistogram<long>(
        "openreferral.openapi.validation.memory.working_set_bytes",
        unit: "By",
        description: "Process working set observed at validation memory checkpoints");
    private static readonly Histogram<long> ValidationWorkingSetDeltaBytesHistogram = CacheMetricsMeter.CreateHistogram<long>(
        "openreferral.openapi.validation.memory.working_set_delta_bytes",
        unit: "By",
        description: "Process working set delta between validation memory checkpoints");

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
    private readonly OpenApiValidationServerOptions _openApiValidationOptions;
    private readonly CacheOptions _cacheOptions;
    private readonly SpecificationOptions _specificationOptions;

    public OpenApiValidationService(
        ILogger<OpenApiValidationService> logger,
        IHttpClientFactory httpClientFactory,
        IJsonValidatorService jsonValidatorService,
        ISchemaResolverService schemaResolverService,
        IProfileDiscoveryService discoveryService,
        IOpenApiDiscoveryService feedSpecDiscoveryService,
        IOpenApiSpecificationService? openApiSpecificationService = null,
        IHsdsComplianceService? hsdsComplianceService = null,
        IEndpointTestingService? endpointTestingService = null,
        IAuthenticationValidationService? authenticationValidationService = null,
        IOpenApiBootstrapService? openApiBootstrapService = null,
        IOptions<CacheOptions>? cacheOptions = null,
        IOptions<SpecificationOptions>? specificationOptions = null,
        IOptions<OpenApiValidationServerOptions>? openApiValidationServerOptions = null,
        IOptions<SchemaResolutionOptions>? schemaResolutionOptions = null)
    {
        _logger = logger;
        _schemaResolverService = schemaResolverService;
        _profileDiscoveryService = discoveryService;
        _openApiDiscoveryService = feedSpecDiscoveryService;
        _openApiSpecificationService = openApiSpecificationService ?? new OpenApiSpecificationService(NullLogger<OpenApiSpecificationService>.Instance, jsonValidatorService, schemaResolutionOptions);
        _hsdsComplianceService = hsdsComplianceService ?? new HsdsComplianceService(jsonValidatorService, specificationOptions, openApiValidationServerOptions);
        _endpointTestingService = endpointTestingService ?? new EndpointTestingService(NullLogger<EndpointTestingService>.Instance, httpClientFactory, jsonValidatorService, _hsdsComplianceService, openApiValidationServerOptions);
        _authenticationValidationService = authenticationValidationService ?? new AuthenticationValidationService(NullLogger<AuthenticationValidationService>.Instance, openApiValidationServerOptions ?? Options.Create(new OpenApiValidationServerOptions()));
        _openApiValidationOptions = openApiValidationServerOptions?.Value ?? new OpenApiValidationServerOptions();
        _cacheOptions = cacheOptions?.Value ?? new CacheOptions { Enabled = false };
        _specificationOptions = specificationOptions?.Value ?? new SpecificationOptions();
        _specFetcher = new OpenApiSpecFetcher(httpClientFactory, logger, schemaResolverService, allowUserSuppliedAuth: _openApiValidationOptions.AllowUserSuppliedAuth);
        _openApiBootstrapService = openApiBootstrapService ?? new OpenApiBootstrapService(
            _profileDiscoveryService,
            _openApiDiscoveryService,
            NullLogger<OpenApiBootstrapService>.Instance);
    }

    public async Task<OpenApiValidationResult> ValidateOpenApiSpecificationAsync(OpenApiValidationRequest request, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var result = new OpenApiValidationResult();
        var schemaResolutionIssues = new List<SchemaResolutionIssue>();
        var lastManagedHeapBytes = GC.GetTotalMemory(forceFullCollection: false);
        var lastWorkingSetBytes = Environment.WorkingSet;

        void LogMemoryCheckpoint(string stage)
        {
            var managedHeapBytes = GC.GetTotalMemory(forceFullCollection: false);
            var managedHeapDeltaBytes = managedHeapBytes - lastManagedHeapBytes;
            var processWorkingSetBytes = Environment.WorkingSet;
            var processWorkingSetDeltaBytes = processWorkingSetBytes - lastWorkingSetBytes;
            var correlationId = GetCurrentCorrelationId();
            var sanitizedBaseUrl = SchemaResolverService.SanitizeUrlForLogging(request.BaseUrl ?? string.Empty);
            var profile = ResolveMetadataProfileIdentifier(
                request.ProfileReason,
                _hsdsComplianceService.ExtractClaimedProfileVersion(request.ProfileReason, request.OwnSchemaUrl),
                request.OwnSchemaUrl);

            _logger.OpenApiValidationMemoryCheckpoint(
                stage,
                correlationId,
                sanitizedBaseUrl,
                SchemaResolverService.SanitizeStringForLogging(profile ?? string.Empty),
                managedHeapBytes,
                managedHeapDeltaBytes,
                processWorkingSetBytes,
                processWorkingSetDeltaBytes,
                stopwatch.Elapsed.TotalMilliseconds);

            var tags = new TagList
            {
                { "stage", stage }
            };
            ValidationManagedHeapBytesHistogram.Record(managedHeapBytes, tags);
            ValidationManagedHeapDeltaBytesHistogram.Record(managedHeapDeltaBytes, tags);
            ValidationWorkingSetBytesHistogram.Record(processWorkingSetBytes, tags);
            ValidationWorkingSetDeltaBytesHistogram.Record(processWorkingSetDeltaBytes, tags);

            lastManagedHeapBytes = managedHeapBytes;
            lastWorkingSetBytes = processWorkingSetBytes;
        }

        try
        {
            _logger.StartingOpenApiTesting();
            LogMemoryCheckpoint("start");

            // Ensure options has default values if not provided
            request.Options ??= new OpenApiValidationOptions();

            var hasConfiguredDefaultProfile = TryGetDefaultProfileSchemaFallback(
                out var defaultProfileSchemaUrl,
                out var defaultProfileVersion);

            // User-supplied authentication for schema requests should only apply when the caller
            // explicitly supplied ownSchemaUrl. Discovered or fallback schema URLs must not reuse
            // schema-scoped request authentication.
            var hasExplicitOwnSchemaUrl = !string.IsNullOrWhiteSpace(request.OwnSchemaUrl);
            var schemaRequestAuth = hasExplicitOwnSchemaUrl
                ? _authenticationValidationService.TryGetValidatedRequestAuthentication("schema", request.DataSourceAuth)
                : null;

            // Datasource authentication can still be used for bootstrap discovery and endpoint testing.
            var dataSourceRequestAuth = _authenticationValidationService.TryGetValidatedRequestAuthentication("datasource", request.DataSourceAuth);

            // Discover OpenAPI schema URL if not provided
            var usedBaseUrlDiscovery = false;
            if (string.IsNullOrEmpty(request.OwnSchemaUrl))
            {
                if (!string.IsNullOrEmpty(request.BaseUrl))
                {
                    usedBaseUrlDiscovery = true;
                    var bootstrap = await _openApiBootstrapService.ResolveFromBaseUrlAsync(request.BaseUrl, dataSourceRequestAuth, cancellationToken);
                    var discoveredUrl = bootstrap.OpenApiSchemaUrl;
                    var reason = bootstrap.DiscoveryReason ?? "unknown";

                    if (!string.IsNullOrEmpty(discoveredUrl))
                    {
                        if (!bootstrap.UsedDataServiceOpenApi
                            && !string.IsNullOrWhiteSpace(bootstrap.ProfileVersion)
                            && _hsdsComplianceService.TryGetKnownHsdsSchemaUrl(bootstrap.ProfileVersion, out var bootstrapMappedProfileSchemaUrl))
                        {
                            discoveredUrl = bootstrapMappedProfileSchemaUrl;
                            _logger.UsingProfileSchemaUrl(
                                SchemaResolverService.SanitizeUrlForLogging(bootstrapMappedProfileSchemaUrl),
                                bootstrap.ProfileVersion);
                        }

                        _logger.DiscoveredOpenApiSchemaUrl(SchemaResolverService.SanitizeUrlForLogging(discoveredUrl), reason);
                        request.OwnSchemaUrl = discoveredUrl;
                        request.ProfileReason = bootstrap.ProfileReason;
                    }
                    else
                    {
                        if (hasConfiguredDefaultProfile)
                        {
                            request.OwnSchemaUrl = defaultProfileSchemaUrl;
                            if (!string.IsNullOrWhiteSpace(defaultProfileVersion))
                            {
                                request.ProfileReason = $"Standard version [user: {defaultProfileVersion}] configured default profile fallback";
                            }

                            result.Notifications.Add("OpenAPI schema URL could not be discovered from the base URL. Falling back to the configured default HSDS profile OpenAPI specification.");
                            _logger.UsingDefaultProfileSchemaUrl(
                                SchemaResolverService.SanitizeUrlForLogging(request.BaseUrl ?? string.Empty),
                                SchemaResolverService.SanitizeUrlForLogging(defaultProfileSchemaUrl));
                        }
                        else
                        {
                            throw new ArgumentException("Failed to discover OpenAPI schema URL from base URL");
                        }
                    }
                }
                else
                {
                    throw new ArgumentException("OpenAPI schema URL must be provided or BaseUrl must allow discovery");
                }
            }

            if (string.IsNullOrEmpty(request.OwnSchemaUrl))
            {
                throw new ArgumentException("OpenAPI schema URL must be provided or BaseUrl must allow discovery");
            }

            LogMemoryCheckpoint("schema-url-discovery");

            var claimedProfileVersion = _hsdsComplianceService.ExtractClaimedProfileVersion(request.ProfileReason, request.OwnSchemaUrl);
            JObject? resolvedHsdsProfileSpec = null;
            string? knownHsdsSchemaUrl = null;

            if (_hsdsComplianceService.TryGetKnownHsdsSchemaUrl(claimedProfileVersion, out var resolvedKnownHsdsSchemaUrl))
            {
                knownHsdsSchemaUrl = resolvedKnownHsdsSchemaUrl;
                if (!string.IsNullOrWhiteSpace(knownHsdsSchemaUrl))
                {
                    resolvedHsdsProfileSpec = await GetCachedResolvedOpenApiSpecAsync(knownHsdsSchemaUrl, null, cancellationToken, cacheScope: "profile", collectedIssues: schemaResolutionIssues);
                }
            }

            LogMemoryCheckpoint("hsds-profile-resolution");

            // Always resolve and cache the feed OpenAPI specification before validation/testing.
            // Track whether the feed spec fell back to the HSDS profile spec so that we can avoid
            // redundant double-validation in FullHsdsRuntime mode (the endpoint tests would already
            // be running against the HSDS profile schema in that case).
            var feedSpecFellBackToHsdsProfile = false;
            JObject openApiSpec;
            try
            {
                openApiSpec = await GetCachedResolvedOpenApiSpecAsync(request.OwnSchemaUrl, schemaRequestAuth, cancellationToken, cacheScope: "feed", collectedIssues: schemaResolutionIssues);
            }
            catch (Exception ex)
            {
                if (resolvedHsdsProfileSpec == null && hasConfiguredDefaultProfile)
                {
                    try
                    {
                        resolvedHsdsProfileSpec = await GetCachedResolvedOpenApiSpecAsync(defaultProfileSchemaUrl, null, cancellationToken, cacheScope: "profile", collectedIssues: schemaResolutionIssues);
                        knownHsdsSchemaUrl = defaultProfileSchemaUrl;
                    }
                    catch (Exception defaultFallbackEx)
                    {
                        _logger.DefaultProfileFallbackCouldNotBeResolved(
                            defaultFallbackEx,
                            SchemaResolverService.SanitizeUrlForLogging(defaultProfileSchemaUrl));
                    }
                }

                if (string.IsNullOrWhiteSpace(knownHsdsSchemaUrl) || resolvedHsdsProfileSpec == null)
                {
                    throw;
                }

                _logger.FallingBackToHsdsProfileSchema(
                    ex,
                    SchemaResolverService.SanitizeUrlForLogging(request.OwnSchemaUrl),
                    SchemaResolverService.SanitizeUrlForLogging(knownHsdsSchemaUrl));

                openApiSpec = (JObject)resolvedHsdsProfileSpec.DeepClone();
                request.OwnSchemaUrl = knownHsdsSchemaUrl;
                feedSpecFellBackToHsdsProfile = true;
                result.Notifications.Add("Unable to fetch OpenAPI specification from the feed URL. Falling back to the HSDS profile OpenAPI specification.");
            }

            LogMemoryCheckpoint("feed-openapi-resolution");

            // If discovery could not infer an HSDS profile version, try extracting it from the OpenAPI document itself.
            string? misplacedHsdsVersionWarning = null;
            if (!HasExplicitProfileVersionContext(request.ProfileReason, request.OwnSchemaUrl))
            {
                var (versionFromSpec, fromOpenapiField) = TryExtractProfileVersionFromOpenApiSpec(openApiSpec);
                if (!string.IsNullOrWhiteSpace(versionFromSpec))
                {
                    request.ProfileReason = $"Standard version [user: {versionFromSpec}] read from OpenAPI spec";
                    if (fromOpenapiField)
                    {
                        _logger.HsdsVersionMisplaced(
                            SchemaResolverService.SanitizeStringForLogging(openApiSpec.SelectToken("openapi")?.ToString() ?? string.Empty),
                            versionFromSpec);
                        misplacedHsdsVersionWarning =
                            $"Warning: The HSDS schema version was incorrectly defined in the 'openapi' field. " +
                            $"Detected HSDS version {versionFromSpec} from this field as a fallback. " +
                            $"Please use an 'x-hsds-version' field in your OpenAPI spec to declare the HSDS version.";
                    }
                }
                else if (usedBaseUrlDiscovery)
                {
                    // No version found; HSDS profile validation will flag the unknown version error.
                }
            }

            // Validate the OpenAPI specification
            OpenApiSpecificationValidation? specValidation = null;
            List<ValidationError>? specValidationErrors = null;
            if (_openApiValidationOptions.ValidateSpecification)
            {
                specValidation = await _openApiSpecificationService.ValidateAsync(openApiSpec, cancellationToken);
                specValidationErrors = new List<ValidationError>(specValidation.Errors);

                if (!string.IsNullOrWhiteSpace(misplacedHsdsVersionWarning))
                {
                    specValidationErrors.Add(new ValidationError
                    {
                        Path = "openapi",
                        Message = misplacedHsdsVersionWarning,
                        ErrorCode = "HSDS_SCHEMA_VERSION_MISPLACED",
                        Severity = "Warning"
                    });
                }
            }
            else if (!string.IsNullOrWhiteSpace(misplacedHsdsVersionWarning))
            {
                result.Notifications.Add(misplacedHsdsVersionWarning);
            }

            claimedProfileVersion = _hsdsComplianceService.ExtractClaimedProfileVersion(request.ProfileReason, request.OwnSchemaUrl);

            if (string.IsNullOrWhiteSpace(claimedProfileVersion) && hasConfiguredDefaultProfile)
            {
                knownHsdsSchemaUrl = defaultProfileSchemaUrl;
                if (resolvedHsdsProfileSpec == null)
                {
                    resolvedHsdsProfileSpec = await GetCachedResolvedOpenApiSpecAsync(knownHsdsSchemaUrl, null, cancellationToken, cacheScope: "profile", collectedIssues: schemaResolutionIssues);
                }

                if (!string.IsNullOrWhiteSpace(defaultProfileVersion))
                {
                    request.ProfileReason = $"Standard version [user: {defaultProfileVersion}] configured default profile fallback";
                }
            }

            if (resolvedHsdsProfileSpec == null && _hsdsComplianceService.TryGetKnownHsdsSchemaUrl(claimedProfileVersion, out resolvedKnownHsdsSchemaUrl))
            {
                knownHsdsSchemaUrl = resolvedKnownHsdsSchemaUrl;
                if (!string.IsNullOrWhiteSpace(knownHsdsSchemaUrl))
                {
                    resolvedHsdsProfileSpec = await GetCachedResolvedOpenApiSpecAsync(knownHsdsSchemaUrl, null, cancellationToken, cacheScope: "profile", collectedIssues: schemaResolutionIssues);
                }
            }

            // Compare feed specification against the known HSDS baseline profile before endpoint testing.
            if (_openApiValidationOptions.ValidateSpecification && specValidation != null)
            {
                if (resolvedHsdsProfileSpec != null)
                {
                    var profileComplianceFindings = _hsdsComplianceService.CompareFeedSpecAgainstHsdsProfile(openApiSpec, resolvedHsdsProfileSpec);
                    if (!request.Options.ReportAdditionalFields)
                    {
                        profileComplianceFindings = profileComplianceFindings
                            .Where(ShouldIncludeProfileComplianceFinding)
                            .ToList();
                    }

                    if (profileComplianceFindings.Count > 0)
                    {
                        specValidationErrors!.AddRange(profileComplianceFindings);
                    }
                }
                else
                {
                    var hasProfileContext = !string.IsNullOrWhiteSpace(request.ProfileReason)
                        || !string.IsNullOrWhiteSpace(request.BaseUrl);

                    if (hasProfileContext)
                    {
                        specValidationErrors!.Add(new ValidationError
                        {
                            Path = "profile",
                            Message = "Can only validate against known HSDS schema profiles. The data feed did not identify a recognised HSDS schema version.",
                            ErrorCode = "HSDS_PROFILE_UNKNOWN",
                            Severity = "Error"
                        });
                    }
                }
            }

            LogMemoryCheckpoint("specification-validation");

            // Test endpoints after specification and HSDS profile checks.
            // When OwnSchemaValidation is None, validate responses against the HSDS profile schema
            // instead of the feed's own schema.
            List<EndpointTestResult> endpointTests = new();
            if (_openApiValidationOptions.TestEndpoints && !string.IsNullOrEmpty(request.BaseUrl))
            {
                var useOwnSchemaValidation = _openApiValidationOptions.OwnSchemaValidation != OwnSchemaValidationMode.None;
                JObject endpointValidationSpec;
                if (!useOwnSchemaValidation)
                {
                    if (resolvedHsdsProfileSpec != null)
                    {
                        endpointValidationSpec = resolvedHsdsProfileSpec;
                        result.Notifications.Add(
                            "OwnSchemaValidation is set to None: endpoint responses are validated against the HSDS profile schema instead of the feed's own schema.");
                    }
                    else
                    {
                        endpointValidationSpec = openApiSpec;
                        result.Notifications.Add(
                            "OwnSchemaValidation is set to None but no HSDS profile schema could be resolved; falling back to the feed's own schema for endpoint validation.");
                    }
                }
                else
                {
                    endpointValidationSpec = openApiSpec;
                }

                var pathDeduplicationWarning = RemoveDuplicatedBasePathFromOpenApiPaths(endpointValidationSpec, request.BaseUrl);
                if (!string.IsNullOrWhiteSpace(pathDeduplicationWarning))
                {
                    if (_openApiValidationOptions.ValidateSpecification && specValidation != null)
                    {
                        specValidationErrors!.Add(new ValidationError
                        {
                            Path = "paths",
                            Message = pathDeduplicationWarning,
                            ErrorCode = "OPENAPI_BASE_PATH_DEDUPLICATED",
                            Severity = "Warning"
                        });
                    }
                    else
                    {
                        result.Notifications.Add(pathDeduplicationWarning);
                    }
                }

                var endpointValidationSpecUrl = useOwnSchemaValidation
                    ? request.OwnSchemaUrl
                    : knownHsdsSchemaUrl ?? request.OwnSchemaUrl;
                endpointTests = await _endpointTestingService.TestEndpointsAsync(endpointValidationSpec, request.BaseUrl, request.Options, dataSourceRequestAuth, endpointValidationSpecUrl, cancellationToken);
                result.EndpointTests = endpointTests;
            }

            LogMemoryCheckpoint("endpoint-testing");

            if (_openApiValidationOptions.ValidateSpecification && specValidation != null)
            {
                AddCircularReferenceValidationIssues(schemaResolutionIssues, specValidationErrors!);
                specValidation.Errors = ValidationErrorNormalizer.NormalizeAndDeduplicateByPath(specValidationErrors!);
                specValidation.IsValid = !specValidation.Errors.Any(e =>
                    string.Equals(e.Severity, "Error", StringComparison.OrdinalIgnoreCase));
                result.SpecificationValidation = specValidation;
            }

            if (_openApiValidationOptions.HsdsValidationMode == HsdsValidationMode.FullHsdsRuntime)
            {
                var useOwnSchemaValidation = _openApiValidationOptions.OwnSchemaValidation != OwnSchemaValidationMode.None;
                if (feedSpecFellBackToHsdsProfile || !useOwnSchemaValidation)
                {
                    // Endpoint responses were already validated against the HSDS profile schema above.
                    // A second pass against the same schema would produce duplicate errors, so skip it.
                    var skipReason = !useOwnSchemaValidation
                        ? "OwnSchemaValidation is set to None, so endpoint responses were already validated against the HSDS profile schema during endpoint testing."
                        : "the feed's OpenAPI specification could not be fetched, so endpoint responses were already validated against the HSDS profile specification during endpoint testing.";
                    result.Notifications.Add(
                        $"Full HSDS runtime validation was skipped: {skipReason} No second pass is needed.");
                }
                else if (resolvedHsdsProfileSpec != null)
                {
                    await _hsdsComplianceService.ValidateEndpointResponsesAgainstHsdsProfileAsync(endpointTests, resolvedHsdsProfileSpec, request.Options, cancellationToken);
                }
                else
                {
                    result.Notifications.Add("Full HSDS runtime mode requested, but no known HSDS profile schema could be resolved.");
                }

                // Response bodies were retained solely for the HSDS runtime validation pass above.
                // If the caller did not request them, release them immediately now that the pass is
                // complete to reduce peak memory usage.
                if (!request.Options.IncludeResponseBody)
                {
                    foreach (var ep in endpointTests)
                    {
                        if (ep.TestResults == null) continue;
                        foreach (var tr in ep.TestResults)
                        {
                            tr.ResponseBody = null;
                        }
                    }
                }
            }

            LogMemoryCheckpoint("full-hsds-runtime");

            // Build summary after all validation stages have had a chance to update endpoint results.
            result.Summary = BuildTestSummary(specValidation, endpointTests, request.Options);
            var hasFailedEndpoints = endpointTests.Any(e =>
                e.Status == EndpointTestStatus.FailedValidation || e.Status == EndpointTestStatus.Error);
            result.IsValid = result.Summary.FailedTests == 0 && !hasFailedEndpoints;

            // Set metadata
            result.Metadata = new CommonValidationMetadata
            {
                BaseUrl = request.BaseUrl,
                TestTimestamp = DateTime.UtcNow,
                TestDuration = stopwatch.Elapsed,
                UserAgent = "OpenReferral-Validator/1.0",
                Profile = ResolveMetadataProfileIdentifier(request.ProfileReason, claimedProfileVersion, request.OwnSchemaUrl),
                ProfileReason = request.ProfileReason
            };

            _logger.OpenApiTestingCompleted(result.IsValid, result.EndpointTests.Count);

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
            else if (result.EndpointTests != null)
            {
                ApplyResponseBodyRetentionCap(result.EndpointTests, result.Notifications);
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

            LogMemoryCheckpoint("result-shaping");
        }
        catch (Exception ex)
        {
            _logger.ErrorDuringOpenApiTesting(ex);
            result.IsValid = false;
            result.Summary = new OpenApiValidationSummary();

            if (IsSpecFetchOrResolveFailure(ex))
            {
                var safeSpecUrl = SchemaResolverService.SanitizeUrlForLogging(request.OwnSchemaUrl ?? string.Empty);
                var rootMessage = TextSanitizer.SanitizeExceptionMessage(GetInnermostException(ex).Message);
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

            var managedHeapBytes = GC.GetTotalMemory(forceFullCollection: false);
            var processWorkingSetBytes = Environment.WorkingSet;
            _logger.OpenApiValidationMemoryUsageAtCompletion(
                managedHeapBytes,
                processWorkingSetBytes,
                result.Duration.TotalMilliseconds);
        }

        return result;
    }

    private void CollectSchemaResolutionIssues(ICollection<SchemaResolutionIssue>? collectedIssues)
    {
        if (collectedIssues == null)
        {
            return;
        }

        var latestIssues = _schemaResolverService.GetResolutionIssues() ?? Array.Empty<SchemaResolutionIssue>();
        foreach (var issue in latestIssues)
        {
            if (string.Equals(issue.ErrorCode, "CIRCULAR_SCHEMA_REFERENCE", StringComparison.Ordinal))
            {
                collectedIssues.Add(issue);
            }
        }
    }

    private static string GetCurrentCorrelationId()
    {
        return Activity.Current?.TraceId.ToString()
            ?? Activity.Current?.Id
            ?? "n/a";
    }

    private static void AddCircularReferenceValidationIssues(
        IEnumerable<SchemaResolutionIssue> schemaResolutionIssues,
        ICollection<ValidationError> targetValidationErrors)
    {
        foreach (var issue in DistinctCircularReferenceIssues(schemaResolutionIssues))
        {
            targetValidationErrors.Add(new ValidationError
            {
                Path = issue.Reference,
                ErrorCode = issue.ErrorCode,
                Severity = "Error",
                Message = BuildCircularReferenceMessage(issue)
            });
        }
    }

    private static IEnumerable<SchemaResolutionIssue> DistinctCircularReferenceIssues(
        IEnumerable<SchemaResolutionIssue> schemaResolutionIssues)
    {
        return schemaResolutionIssues
            .Where(issue => string.Equals(issue.ErrorCode, "CIRCULAR_SCHEMA_REFERENCE", StringComparison.Ordinal))
            .GroupBy(issue => $"{issue.Reference}|{issue.ReferencePath}", StringComparer.Ordinal)
            .Select(group => group.First());
    }

    private static string BuildCircularReferenceMessage(SchemaResolutionIssue issue)
    {
        return $"Circular schema reference detected at '{issue.Reference}'. Resolution path: {issue.ReferencePath}. Nested reference resolution was stopped at the repeated reference.";
    }

    private static bool ShouldIncludeProfileComplianceFinding(ValidationError error)
    {
        return !error.ErrorCode.StartsWith("HSDS_ADDITIONAL_", StringComparison.OrdinalIgnoreCase);
    }

    private static string? RemoveDuplicatedBasePathFromOpenApiPaths(JObject openApiSpec, string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl)
            || openApiSpec["paths"] is not JObject pathsObject
            || pathsObject.Count == 0)
        {
            return null;
        }

        var baseUri = TryParseBaseUri(baseUrl);
        if (baseUri == null)
        {
            return null;
        }

        var basePath = NormalizePath(baseUri.AbsolutePath);
        if (string.IsNullOrEmpty(basePath) || basePath == "/")
        {
            return null;
        }

        var updatedPaths = new JObject();
        var duplicatedEntries = new List<string>();
        var duplicateCollisions = new List<string>();

        foreach (var property in pathsObject.Properties())
        {
            var originalPath = NormalizePath(property.Name);
            var deduplicatedPath = TryStripDuplicateBasePath(originalPath, basePath) ?? originalPath;

            if (!string.Equals(deduplicatedPath, originalPath, StringComparison.Ordinal))
            {
                duplicatedEntries.Add(originalPath);
            }

            if (updatedPaths.TryGetValue(deduplicatedPath, out _))
            {
                duplicateCollisions.Add(deduplicatedPath);
                if (!updatedPaths.TryGetValue(originalPath, out _))
                {
                    updatedPaths[originalPath] = property.Value;
                }

                continue;
            }

            updatedPaths[deduplicatedPath] = property.Value;
        }

        if (duplicatedEntries.Count == 0)
        {
            return null;
        }

        if (duplicateCollisions.Count > 0)
        {
            var uniqueCollisions = string.Join(", ", duplicateCollisions.Distinct(StringComparer.Ordinal));
            return $"Warning: OpenAPI endpoint paths duplicate the base URL prefix '{basePath}', but automatic de-duplication was skipped for colliding paths: {uniqueCollisions}.";
        }

        pathsObject.RemoveAll();
        foreach (var updatedProperty in updatedPaths.Properties())
        {
            pathsObject.Add(updatedProperty.Name, updatedProperty.Value);
        }

        return $"Warning: Removed duplicated base URL prefix '{basePath}' from {duplicatedEntries.Count} OpenAPI endpoint path(s) before endpoint testing.";
    }

    private static Uri? TryParseBaseUri(string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return null;
        }

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri)
            || !string.Equals(baseUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(baseUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return baseUri;
    }

    private static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "/";
        }

        var normalized = path.Replace('\\', '/').Trim();
        if (!normalized.StartsWith('/'))
        {
            normalized = $"/{normalized}";
        }

        while (normalized.Contains("//", StringComparison.Ordinal))
        {
            normalized = normalized.Replace("//", "/", StringComparison.Ordinal);
        }

        if (normalized.Length > 1 && normalized.EndsWith('/'))
        {
            normalized = normalized.TrimEnd('/');
        }

        return normalized;
    }

    private static string? TryStripDuplicateBasePath(string endpointPath, string basePath)
    {
        if (!endpointPath.StartsWith(basePath, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (endpointPath.Length == basePath.Length)
        {
            return "/";
        }

        if (endpointPath[basePath.Length] != '/')
        {
            return null;
        }

        var stripped = endpointPath[basePath.Length..];
        return NormalizePath(stripped);
    }

    private void ApplyResponseBodyRetentionCap(
        IEnumerable<EndpointTestResult> endpointTests,
        ICollection<string> notifications)
    {
        if (_openApiValidationOptions.MaxRetainedResponseBodyCharacters <= 0)
        {
            return;
        }

        var cap = _openApiValidationOptions.MaxRetainedResponseBodyCharacters;
        var truncatedCount = 0;

        foreach (var endpoint in endpointTests)
        {
            if (endpoint.TestResults == null)
            {
                continue;
            }

            foreach (var testResult in endpoint.TestResults)
            {
                if (string.IsNullOrEmpty(testResult.ResponseBody)
                    || testResult.ResponseBody.Length <= cap)
                {
                    continue;
                }

                testResult.ResponseBody = testResult.ResponseBody[..cap];
                truncatedCount++;
            }
        }

        if (truncatedCount > 0)
        {
            notifications.Add(
                $"Response bodies were truncated to {_openApiValidationOptions.MaxRetainedResponseBodyCharacters} characters for {truncatedCount} test result(s) by server configuration.");
        }
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
            _logger.InvalidDefaultProfileVersion(SchemaResolverService.SanitizeStringForLogging(configuredDefaultKey));
            return false;
        }

        schemaUrl = configuredDefaultSchemaUrl;
        profileVersion = configuredDefaultKey;

        return true;
    }

    private async Task<JObject> GetCachedResolvedOpenApiSpecAsync(
        string specUrl,
        DataSourceAuthentication? auth,
        CancellationToken cancellationToken,
        string cacheScope,
        ICollection<SchemaResolutionIssue>? collectedIssues = null)
    {
        var cache = ResolveCacheByScope(cacheScope);
        var cacheKey = $"resolved-openapi:{specUrl}";

        if (cache.TryGetValue(cacheKey, out var cachedEntry)
            && cachedEntry.ExpiresAtUtc > DateTime.UtcNow
            && !string.IsNullOrWhiteSpace(cachedEntry.ResolvedSpecJson))
        {
            ResolvedOpenApiCacheHitsCounter.Add(1, new KeyValuePair<string, object?>("scope", cacheScope));
            _logger.ResolvedOpenApiCacheHit(cacheScope, SchemaResolverService.SanitizeUrlForLogging(specUrl));
            return JObject.Parse(cachedEntry.ResolvedSpecJson);
        }

        ResolvedOpenApiCacheMissesCounter.Add(1, new KeyValuePair<string, object?>("scope", cacheScope));
        _logger.ResolvedOpenApiCacheMiss(cacheScope, SchemaResolverService.SanitizeUrlForLogging(specUrl));

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
                CollectSchemaResolutionIssues(collectedIssues);
                var resolvedFromWarmupObject = JObject.Parse(resolvedFromWarmup);

                if (!IsLikelyOpenApiDocument(resolvedFromWarmupObject))
                {
                    throw new InvalidOperationException("Warmup-path resolution did not produce an OpenAPI document.");
                }

                if (_cacheOptions.Enabled)
                {
                    PurgeExpiredCacheEntries();
                    cache[cacheKey] = new CachedResolvedSpec(
                        resolvedFromWarmup,
                    DateTime.UtcNow.Add(GetProfileSchemaCacheTtl()));
                }

                _logger.ResolvedProfileViaWarmup(SchemaResolverService.SanitizeUrlForLogging(specUrl));

                return resolvedFromWarmupObject;
            }
            catch (Exception ex)
            {
                _logger.WarmupPathResolutionUnavailable(ex, SchemaResolverService.SanitizeUrlForLogging(specUrl));
            }
        }

        var unresolvedSpec = await _specFetcher.FetchOpenApiSpecFromUrlAsync(
            specUrl,
            auth,
            cancellationToken,
            resolveReferences: false);

        var resolvedSpecContent = await _schemaResolverService.ResolveAsync(unresolvedSpec.ToString(), specUrl, auth);
        CollectSchemaResolutionIssues(collectedIssues);

        if (_cacheOptions.Enabled)
        {
            PurgeExpiredCacheEntries();
            cache[cacheKey] = new CachedResolvedSpec(
                resolvedSpecContent,
                DateTime.UtcNow.Add(GetProfileSchemaCacheTtl()));
        }

        return JObject.Parse(resolvedSpecContent);
    }

    private TimeSpan GetProfileSchemaCacheTtl()
    {
        return _cacheOptions.ExpirationMinutes > 0
            ? TimeSpan.FromMinutes(_cacheOptions.ExpirationMinutes)
            : TimeSpan.FromHours(2);
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

    private static void PurgeExpiredCacheEntries()
    {
        var now = DateTime.UtcNow;
        foreach (var key in FeedResolvedSpecCache.Keys.ToList())
        {
            if (FeedResolvedSpecCache.TryGetValue(key, out var entry) && entry.ExpiresAtUtc <= now)
            {
                _ = FeedResolvedSpecCache.TryRemove(key, out _);
            }
        }

        foreach (var key in ProfileResolvedSpecCache.Keys.ToList())
        {
            if (ProfileResolvedSpecCache.TryGetValue(key, out var entry) && entry.ExpiresAtUtc <= now)
            {
                _ = ProfileResolvedSpecCache.TryRemove(key, out _);
            }
        }
    }

    private bool HasExplicitProfileVersionContext(string? profileReason, string? schemaUrl)
    {
        return !string.IsNullOrWhiteSpace(_hsdsComplianceService.ExtractClaimedProfileVersion(profileReason, schemaUrl));
    }

    private string? ResolveMetadataProfileIdentifier(string? profileReason, string? claimedProfileVersion, string? schemaUrl)
    {
        var explicitProfileFromReason = TryExtractProfileIdentifierFromProfileReason(profileReason);

        // Prefer exact match against configured profile keys
        if (!string.IsNullOrWhiteSpace(explicitProfileFromReason))
        {
            // Exact key match
            if (_specificationOptions.Urls.ContainsKey(explicitProfileFromReason))
            {
                return explicitProfileFromReason;
            }

            // Numeric major.minor fallback to find the configured key
            var configuredFromVersion = TryResolveConfiguredProfileKeyFromVersion(
                ProfileVersionNormalizer.ExtractMajorMinor(explicitProfileFromReason) ?? explicitProfileFromReason);
            if (!string.IsNullOrWhiteSpace(configuredFromVersion))
            {
                return configuredFromVersion;
            }
        }

        var configuredProfileFromSchemaUrl = TryResolveConfiguredProfileKeyFromSchemaUrl(schemaUrl);
        if (!string.IsNullOrWhiteSpace(configuredProfileFromSchemaUrl))
        {
            return configuredProfileFromSchemaUrl;
        }

        if (!string.IsNullOrWhiteSpace(claimedProfileVersion))
        {
            // Exact key match
            if (_specificationOptions.Urls.ContainsKey(claimedProfileVersion))
            {
                return claimedProfileVersion;
            }

            var configuredFromVersion = TryResolveConfiguredProfileKeyFromVersion(
                ProfileVersionNormalizer.ExtractMajorMinor(claimedProfileVersion) ?? claimedProfileVersion);
            if (!string.IsNullOrWhiteSpace(configuredFromVersion))
            {
                return configuredFromVersion;
            }

            return claimedProfileVersion;
        }

        return explicitProfileFromReason;
    }

    private static string? TryExtractProfileIdentifierFromProfileReason(string? profileReason)
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

    private string? TryResolveConfiguredProfileKeyFromSchemaUrl(string? schemaUrl)
    {
        if (string.IsNullOrWhiteSpace(schemaUrl) || _specificationOptions.Urls.Count == 0)
        {
            return null;
        }

        if (!Uri.TryCreate(schemaUrl, UriKind.Absolute, out var requestedUri))
        {
            return null;
        }

        var requestedAbsoluteUrl = requestedUri.AbsoluteUri.TrimEnd('/');

        foreach (var configuredEntry in _specificationOptions.Urls)
        {
            if (string.IsNullOrWhiteSpace(configuredEntry.Value)
                || !Uri.TryCreate(configuredEntry.Value, UriKind.Absolute, out var configuredUri))
            {
                continue;
            }

            if (string.Equals(configuredUri.AbsoluteUri.TrimEnd('/'), requestedAbsoluteUrl, StringComparison.OrdinalIgnoreCase))
            {
                return configuredEntry.Key;
            }
        }

        return null;
    }

    private string? TryResolveConfiguredProfileKeyFromVersion(string versionNumber)
    {
        if (string.IsNullOrWhiteSpace(versionNumber) || _specificationOptions.Urls.Count == 0)
        {
            return null;
        }

        return _specificationOptions.Urls.Keys
            .FirstOrDefault(key => string.Equals(
                ProfileVersionNormalizer.NormalizeVersionNumber(key),
                versionNumber,
                StringComparison.OrdinalIgnoreCase));
    }

    private static (string? version, bool fromOpenapiField) TryExtractProfileVersionFromOpenApiSpec(JObject openApiSpec)
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
            var tokenValue = openApiSpec.SelectToken(tokenPath)?.ToString()?.Trim();
            if (!string.IsNullOrWhiteSpace(tokenValue))
            {
                return (tokenValue, false);
            }
        }

        // Last resort: use the "openapi" field (e.g. "3.0.3" -> "3.0").
        // This is incorrect usage — the "openapi" field specifies the OpenAPI spec version,
        // not the HSDS schema version — so we flag it as incorrectly defined.
        var openapiValue = openApiSpec.SelectToken("openapi")?.ToString();
        if (!string.IsNullOrWhiteSpace(openapiValue))
        {
            var parts = openapiValue.Split('.');
            if (parts.Length >= 2)
            {
                var majorMinor = $"{parts[0]}.{parts[1]}";
                if (!string.IsNullOrWhiteSpace(majorMinor))
                {
                    return (majorMinor, true);
                }
            }
        }

        return (null, false);
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
                message.Contains("Failed to discover OpenAPI schema URL", StringComparison.OrdinalIgnoreCase) ||
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
        var shouldIgnoreOptionalFailures = _openApiValidationOptions.TestOptionalEndpoints && _openApiValidationOptions.TreatOptionalEndpointsAsWarnings;
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
