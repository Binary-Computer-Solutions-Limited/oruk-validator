using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Newtonsoft.Json.Linq;
using OpenReferralApi.Core.Helpers;
using OpenReferralApi.Core.Logging;
using ValidationError = OpenReferralApi.Core.Models.Validation.ValidationError;

namespace OpenReferralApi.Core.Services;

public interface IOpenApiValidationService
{
    Task<OpenApiValidationResult> ValidateOpenApiSpecificationAsync(OpenApiValidationRequest request, CancellationToken cancellationToken = default);
}

public class OpenApiValidationService : OpenApiValidationServiceBase, IOpenApiValidationService
{

    private readonly ILogger<OpenApiValidationService> _logger;
    private readonly ISchemaResolverService _schemaResolverService;
    private readonly IProfileDiscoveryService _profileDiscoveryService;
    private readonly IOpenApiSpecificationService _openApiSpecificationService;
    private readonly IHsdsComplianceService _hsdsComplianceService;
    private readonly IProfileResolverService _profileResolverService;
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
        IOpenApiSpecificationService openApiSpecificationService,
        IHsdsComplianceService hsdsComplianceService,
        IEndpointTestingService endpointTestingService,
        IAuthenticationValidationService authenticationValidationService,
        IProfileDiscoveryService profileDiscoveryService,
        IProfileResolverService? profileResolverService = null,
        IOptions<CacheOptions>? cacheOptions = null,
        IOptions<SpecificationOptions>? specificationOptions = null,
        IOptions<OpenApiValidationServerOptions>? openApiValidationServerOptions = null)
    {
        _logger = logger;
        _schemaResolverService = schemaResolverService;
        _openApiSpecificationService = openApiSpecificationService;
        _hsdsComplianceService = hsdsComplianceService ?? new HsdsComplianceService(jsonValidatorService, specificationOptions, openApiValidationServerOptions);
        _profileResolverService = profileResolverService ?? new ProfileResolverService(_hsdsComplianceService);
        _endpointTestingService = endpointTestingService ?? new EndpointTestingService(NullLogger<EndpointTestingService>.Instance, httpClientFactory, jsonValidatorService, _hsdsComplianceService, openApiValidationServerOptions);
        _authenticationValidationService = authenticationValidationService ?? new AuthenticationValidationService(NullLogger<AuthenticationValidationService>.Instance, openApiValidationServerOptions ?? Options.Create(new OpenApiValidationServerOptions()));
        _openApiValidationOptions = openApiValidationServerOptions?.Value ?? new OpenApiValidationServerOptions();
        _cacheOptions = cacheOptions?.Value ?? new CacheOptions { Enabled = false };
        _specificationOptions = specificationOptions?.Value ?? new SpecificationOptions();
        _specFetcher = new OpenApiSpecFetcher(httpClientFactory, logger, schemaResolverService, allowUserSuppliedAuth: _openApiValidationOptions.AllowUserSuppliedAuth);
        _profileDiscoveryService = profileDiscoveryService;
    }

    public async Task<OpenApiValidationResult> ValidateOpenApiSpecificationAsync(OpenApiValidationRequest request, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var result = new OpenApiValidationResult();
        var schemaResolutionIssues = new List<SchemaResolutionIssue>();
        var memoryCheckpointTracker = new MemoryCheckpointTracker(stopwatch);

        void LogMemoryCheckpoint(string stage)
        {
            if (!_openApiValidationOptions.EnableMemoryCheckpointLogging)
            {
                return;
            }

            var snapshot = memoryCheckpointTracker.Capture();
            var correlationId = GetCurrentCorrelationId();
            var sanitizedBaseUrl = TextSanitizer.SanitizeUrlForLogging(request.BaseUrl ?? string.Empty);
            var profile = ResolveMetadataProfileIdentifier(
                request.ProfileReason,
                _hsdsComplianceService.ExtractClaimedProfileVersion(request.ProfileReason, request.OwnSchemaUrl),
                request.OwnSchemaUrl);

            _logger.OpenApiValidationMemoryCheckpoint(
                stage,
                correlationId,
                sanitizedBaseUrl,
                TextSanitizer.SanitizeStringForLogging(profile ?? string.Empty),
                snapshot.ManagedHeapBytes,
                snapshot.ManagedHeapDeltaBytes,
                snapshot.ProcessWorkingSetBytes,
                snapshot.ProcessWorkingSetDeltaBytes,
                snapshot.GcHeapSizeBytes,
                snapshot.GcHeapSizeDeltaBytes,
                snapshot.GcFragmentedBytes,
                snapshot.GcFragmentedDeltaBytes,
                snapshot.GcTotalCommittedBytes,
                snapshot.GcTotalCommittedDeltaBytes,
                snapshot.GcMemoryLoadBytes,
                snapshot.GcMemoryLoadDeltaBytes,
                snapshot.Gen0CollectionsDelta,
                snapshot.Gen1CollectionsDelta,
                snapshot.Gen2CollectionsDelta,
                snapshot.ElapsedMilliseconds);

            var tags = new TagList { { "stage", stage } };
            ValidationManagedHeapBytesHistogram.Record(snapshot.ManagedHeapBytes, tags);
            ValidationManagedHeapDeltaBytesHistogram.Record(snapshot.ManagedHeapDeltaBytes, tags);
            ValidationWorkingSetBytesHistogram.Record(snapshot.ProcessWorkingSetBytes, tags);
            ValidationWorkingSetDeltaBytesHistogram.Record(snapshot.ProcessWorkingSetDeltaBytes, tags);

            var cacheState = GetResolvedOpenApiCacheState();
            _logger.ResolvedOpenApiCacheState(
                stage,
                cacheState.FeedEntries,
                cacheState.ProfileEntries,
                cacheState.ExpiredEntries,
                cacheState.FeedJsonChars,
                cacheState.ProfileJsonChars);

        }

        try
        {
            _logger.StartingOpenApiTesting();
            LogMemoryCheckpoint("start");

            request.Options ??= new OpenApiValidationOptions();

            var executionOutcome = await ExecuteValidationPipelineAsync(
                request,
                result,
                schemaResolutionIssues,
                LogMemoryCheckpoint,
                cancellationToken);

            result.SpecificationValidation = executionOutcome.SpecificationValidation;
            result.EndpointTests = executionOutcome.EndpointTests;
            result.Summary = BuildTestSummary(result.SpecificationValidation, result.EndpointTests, request.Options);
            var hasFailedEndpoints = result.EndpointTests.Any(e =>
                e.Status == EndpointTestStatus.FailedValidation || e.Status == EndpointTestStatus.Error);
            result.IsValid = result.Summary.FailedTests == 0 && !hasFailedEndpoints;

            result.Metadata = new CommonValidationMetadata
            {
                BaseUrl = request.BaseUrl,
                TestTimestamp = DateTime.UtcNow,
                TestDuration = stopwatch.Elapsed,
                UserAgent = "OpenReferral-Validator/1.0",
                Profile = ResolveMetadataProfileIdentifier(request.ProfileReason, executionOutcome.ClaimedProfileVersion, request.OwnSchemaUrl),
                ProfileReason = request.ProfileReason
            };

            _logger.OpenApiTestingCompleted(result.IsValid, result.EndpointTests.Count);

            ApplyResultShaping(result, request.Options);
            LogMemoryCheckpoint("result-shaping");
        }
        catch (Exception ex)
        {
            _logger.ErrorDuringOpenApiTesting(ex);
            result.IsValid = false;
            result.Summary = new OpenApiValidationSummary();

            if (IsSpecFetchOrResolveFailure(ex))
            {
                var safeSpecUrl = TextSanitizer.SanitizeUrlForLogging(request.OwnSchemaUrl ?? string.Empty);
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

    private async Task<ValidationExecutionOutcome> ExecuteValidationPipelineAsync(
        OpenApiValidationRequest request,
        OpenApiValidationResult result,
        List<SchemaResolutionIssue> schemaResolutionIssues,
        Action<string> logMemoryCheckpoint,
        CancellationToken cancellationToken)
    {
        var discovery = await PrepareValidationDiscoveryAsync(request, result, schemaResolutionIssues, cancellationToken);
        logMemoryCheckpoint("schema-url-discovery");

        var profileState = discovery.ProfileState;

        var specificationStage = await ExecuteSpecificationStageAsync(
            request,
            result,
            discovery,
            profileState,
            discovery.OpenApiSpec,
            schemaResolutionIssues,
            cancellationToken);
        logMemoryCheckpoint("specification-validation");

        // Advance to the finalized profile state produced by the specification stage.
        profileState = specificationStage.FinalProfileState;

        var endpointTests = await ExecuteEndpointTestingAsync(
            request,
            result,
            discovery.DataSourceRequestAuth,
            discovery.OpenApiSpec,
            profileState.ResolvedHsdsProfileSpec,
            profileState.KnownHsdsSchemaUrl,
            specificationStage.SpecValidation,
            specificationStage.SpecValidationErrors,
            cancellationToken);
        logMemoryCheckpoint("endpoint-testing");

        FinalizeSpecificationValidation(result, schemaResolutionIssues, specificationStage);

        await ExecuteFullHsdsRuntimeValidationAsync(
            request,
            result,
            endpointTests,
            discovery.FeedSpecFellBackToHsdsProfile,
            profileState.ResolvedHsdsProfileSpec,
            cancellationToken);
        logMemoryCheckpoint("full-hsds-runtime");

        return new ValidationExecutionOutcome
        {
            SpecificationValidation = result.SpecificationValidation,
            EndpointTests = endpointTests,
            ClaimedProfileVersion = profileState.ClaimedProfileVersion
        };
    }

    private async Task<DiscoveryPreparation> PrepareValidationDiscoveryAsync(
        OpenApiValidationRequest request,
        OpenApiValidationResult result,
        List<SchemaResolutionIssue> schemaResolutionIssues,
        CancellationToken cancellationToken)
    {
        var hasConfiguredDefaultProfile = TryGetDefaultProfileSchemaFallback(
            out var defaultProfileSchemaUrl,
            out var defaultProfileVersion);

        var hasExplicitOwnSchemaUrl = !string.IsNullOrWhiteSpace(request.OwnSchemaUrl);
        var schemaRequestAuth = hasExplicitOwnSchemaUrl
            ? _authenticationValidationService.TryGetValidatedRequestAuthentication("schema", request.DataSourceAuth)
            : null;
        var dataSourceRequestAuth = _authenticationValidationService.TryGetValidatedRequestAuthentication("datasource", request.DataSourceAuth);
        
        bool usedBaseUrlDiscovery = true;
        var bootstrap = await _profileDiscoveryService.DiscoverFromBaseUrlAsync(request.OwnSchemaUrl, request.BaseUrl!, dataSourceRequestAuth, cancellationToken);

        var decision = _profileResolverService.Resolve(
            request.ProfileReason,
            request.OwnSchemaUrl,
            hasConfiguredDefaultProfile,
            defaultProfileSchemaUrl,
            defaultProfileVersion);

        string? knownHsdsSchemaUrl = decision.KnownHsdsSchemaUrl;
        JObject? resolvedHsdsProfileSpec = TryParseJObject(bootstrap.HsdsProfileSchemaContent);

        if (resolvedHsdsProfileSpec == null && !string.IsNullOrWhiteSpace(knownHsdsSchemaUrl))
        {
            resolvedHsdsProfileSpec = await GetCachedResolvedOpenApiSpecAsync(
                knownHsdsSchemaUrl,
                null,
                cancellationToken,
                cacheScope: "profile",
                collectedIssues: schemaResolutionIssues);
        }

        var rawOpenApiContent = bootstrap.OpenApiSchemaContent;
        var openApiSpec = TryParseJObject(rawOpenApiContent);
        var feedSpecFellBackToHsdsProfile = false;
        var effectiveOwnSchemaUrl = request.OwnSchemaUrl;

        if (openApiSpec == null
            && !string.IsNullOrWhiteSpace(rawOpenApiContent)
            && string.IsNullOrWhiteSpace(request.OwnSchemaUrl))
        {
            throw new ArgumentException("Discovered OpenAPI schema content is not valid JSON and no schema URL is available for resolver fallback");
        }

        if (openApiSpec == null)
        {
            try
            {
                openApiSpec = await GetCachedResolvedOpenApiSpecAsync(
                    request.OwnSchemaUrl!,
                    schemaRequestAuth,
                    cancellationToken,
                    cacheScope: "feed",
                    collectedIssues: schemaResolutionIssues);
            }
            catch (Exception ex)
            {
                var fallbackKnownHsdsSchemaUrl = knownHsdsSchemaUrl;
                var fallbackResolvedHsdsProfileSpec = resolvedHsdsProfileSpec;

                if (fallbackResolvedHsdsProfileSpec == null
                    && hasConfiguredDefaultProfile
                    && !string.IsNullOrWhiteSpace(defaultProfileSchemaUrl))
                {
                    try
                    {
                        fallbackKnownHsdsSchemaUrl = defaultProfileSchemaUrl;
                        fallbackResolvedHsdsProfileSpec = await GetCachedResolvedOpenApiSpecAsync(
                            defaultProfileSchemaUrl,
                            null,
                            cancellationToken,
                            cacheScope: "profile",
                            collectedIssues: schemaResolutionIssues);
                    }
                    catch (Exception defaultFallbackEx)
                    {
                        _logger.DefaultProfileFallbackCouldNotBeResolved(
                            defaultFallbackEx,
                            TextSanitizer.SanitizeUrlForLogging(defaultProfileSchemaUrl));
                    }
                }

                if (string.IsNullOrWhiteSpace(fallbackKnownHsdsSchemaUrl) || fallbackResolvedHsdsProfileSpec == null)
                {
                    if (string.IsNullOrWhiteSpace(request.OwnSchemaUrl))
                    {
                        throw new ArgumentException("Failed to discover OpenAPI schema URL or schema content from base URL");
                    }

                    throw;
                }

                _logger.FallingBackToHsdsProfileSchema(
                    ex,
                    TextSanitizer.SanitizeUrlForLogging(request.OwnSchemaUrl ?? string.Empty),
                    TextSanitizer.SanitizeUrlForLogging(fallbackKnownHsdsSchemaUrl));

                openApiSpec = fallbackResolvedHsdsProfileSpec;
                feedSpecFellBackToHsdsProfile = true;
                effectiveOwnSchemaUrl = fallbackKnownHsdsSchemaUrl;
                knownHsdsSchemaUrl = fallbackKnownHsdsSchemaUrl;
                resolvedHsdsProfileSpec = fallbackResolvedHsdsProfileSpec;

                result.Notifications.Add("Unable to fetch OpenAPI specification from the feed URL. Falling back to the HSDS profile OpenAPI specification.");

                if (string.Equals(fallbackKnownHsdsSchemaUrl, defaultProfileSchemaUrl, StringComparison.OrdinalIgnoreCase))
                {
                    result.Notifications.Add("Unable to fetch OpenAPI specification from the feed URL. Falling back to the configured default HSDS profile OpenAPI specification.");
                }

                request.OwnSchemaUrl = effectiveOwnSchemaUrl;
            }
        }

        return new DiscoveryPreparation
        {
            UsedBaseUrlDiscovery = usedBaseUrlDiscovery,
            ProfileState = new ProfileResolutionState
            {
                ClaimedProfileVersion = decision.ClaimedProfileVersion,
                KnownHsdsSchemaUrl = knownHsdsSchemaUrl,
                ResolvedHsdsProfileSpec = resolvedHsdsProfileSpec
            },
            OpenApiSpec = openApiSpec,
            FeedSpecFellBackToHsdsProfile = feedSpecFellBackToHsdsProfile,
            EffectiveOwnSchemaUrl = effectiveOwnSchemaUrl,
            DiscoveredOpenApiSchemaContent = bootstrap.OpenApiSchemaContent,
            DiscoveredHsdsProfileVersion = bootstrap.HsdsProfileVersion,
            DiscoveredHsdsProfileReason = bootstrap.HsdsProfileReason,
            DiscoveredHsdsProfileSchemaContent = bootstrap.HsdsProfileSchemaContent,
            HasConfiguredDefaultProfile = hasConfiguredDefaultProfile,
            DefaultProfileSchemaUrl = defaultProfileSchemaUrl,
            DefaultProfileVersion = defaultProfileVersion,
            SchemaRequestAuth = schemaRequestAuth,
            DataSourceRequestAuth = dataSourceRequestAuth
        };
    }

    private async Task<SpecificationStageResult> ExecuteSpecificationStageAsync(
        OpenApiValidationRequest request,
        OpenApiValidationResult result,
        DiscoveryPreparation discovery,
        ProfileResolutionState profileState,
        JObject openApiSpec,
        List<SchemaResolutionIssue> schemaResolutionIssues,
        CancellationToken cancellationToken)
    {
        var misplacedHsdsVersionWarning = TrySetProfileReasonFromOpenApiSpec(request, openApiSpec, discovery.UsedBaseUrlDiscovery);

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

        var finalProfileState = await ResolveProfileStateWithFallbackAsync(
            request,
            discovery,
            profileState,
            schemaResolutionIssues,
            cancellationToken);

        if (_openApiValidationOptions.ValidateSpecification && specValidation != null)
        {
            ApplySpecificationComparisonAgainstProfile(
                request,
                openApiSpec,
                finalProfileState,
                specValidationErrors!);
        }

        return new SpecificationStageResult
        {
            SpecValidation = specValidation,
            SpecValidationErrors = specValidationErrors,
            FinalProfileState = finalProfileState
        };
    }

    private async Task<ProfileResolutionState> ResolveProfileStateWithFallbackAsync(
        OpenApiValidationRequest request,
        DiscoveryPreparation discovery,
        ProfileResolutionState profileState,
        List<SchemaResolutionIssue> schemaResolutionIssues,
        CancellationToken cancellationToken)
    {
        var decision = _profileResolverService.Resolve(
            request.ProfileReason,
            request.OwnSchemaUrl,
            discovery.HasConfiguredDefaultProfile,
            discovery.DefaultProfileSchemaUrl,
            discovery.DefaultProfileVersion);

        // Only update ProfileReason if no explicit version has already been identified by a prior stage.
        if (!HasExplicitProfileVersionContext(request.ProfileReason, request.OwnSchemaUrl)
            && !string.IsNullOrWhiteSpace(decision.EffectiveProfileReason))
        {
            request.ProfileReason = decision.EffectiveProfileReason;
        }

        var knownHsdsSchemaUrl = !string.IsNullOrWhiteSpace(decision.KnownHsdsSchemaUrl)
            ? decision.KnownHsdsSchemaUrl
            : profileState.KnownHsdsSchemaUrl;

        var resolvedHsdsProfileSpec = profileState.ResolvedHsdsProfileSpec;
        if (resolvedHsdsProfileSpec == null && !string.IsNullOrWhiteSpace(knownHsdsSchemaUrl))
        {
            resolvedHsdsProfileSpec = await GetCachedResolvedOpenApiSpecAsync(
                knownHsdsSchemaUrl,
                null,
                cancellationToken,
                cacheScope: "profile",
                collectedIssues: schemaResolutionIssues);
        }

        return new ProfileResolutionState
        {
            ClaimedProfileVersion = decision.ClaimedProfileVersion ?? profileState.ClaimedProfileVersion,
            KnownHsdsSchemaUrl = knownHsdsSchemaUrl,
            ResolvedHsdsProfileSpec = resolvedHsdsProfileSpec
        };
    }

    private void ApplySpecificationComparisonAgainstProfile(
        OpenApiValidationRequest request,
        JObject openApiSpec,
        ProfileResolutionState profileState,
        List<ValidationError> specValidationErrors)
    {
        if (profileState.ResolvedHsdsProfileSpec != null)
        {
            var profileComplianceFindings = _hsdsComplianceService.CompareFeedSpecAgainstHsdsProfile(openApiSpec, profileState.ResolvedHsdsProfileSpec);
            if (!request.Options!.ReportAdditionalFields)
            {
                profileComplianceFindings = profileComplianceFindings
                    .Where(ShouldIncludeProfileComplianceFinding)
                    .ToList();
            }

            if (profileComplianceFindings.Count > 0)
            {
                specValidationErrors.AddRange(profileComplianceFindings);
            }

            return;
        }

        var hasProfileContext = !string.IsNullOrWhiteSpace(request.ProfileReason)
            || !string.IsNullOrWhiteSpace(request.BaseUrl);

        if (hasProfileContext)
        {
            specValidationErrors.Add(new ValidationError
            {
                Path = "profile",
                Message = "Can only validate against known HSDS schema profiles. The data feed did not identify a recognised HSDS schema version.",
                ErrorCode = "HSDS_PROFILE_UNKNOWN",
                Severity = "Error"
            });
        }
    }

    private async Task<List<EndpointTestResult>> ExecuteEndpointTestingAsync(
        OpenApiValidationRequest request,
        OpenApiValidationResult result,
        DataSourceAuthentication? dataSourceRequestAuth,
        JObject openApiSpec,
        JObject? resolvedHsdsProfileSpec,
        string? knownHsdsSchemaUrl,
        OpenApiSpecificationValidation? specValidation,
        List<ValidationError>? specValidationErrors,
        CancellationToken cancellationToken)
    {
        var endpointTests = new List<EndpointTestResult>();
        if (!_openApiValidationOptions.TestEndpoints || string.IsNullOrEmpty(request.BaseUrl))
        {
            return endpointTests;
        }

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

        endpointValidationSpec = PrepareEndpointValidationSpecForEndpointTesting(endpointValidationSpec, request.BaseUrl, out var pathDeduplicationWarning);
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
        endpointTests = await _endpointTestingService.TestEndpointsAsync(
            endpointValidationSpec,
            request.BaseUrl,
            request.Options!,
            dataSourceRequestAuth,
            endpointValidationSpecUrl,
            cancellationToken);

        return endpointTests;
    }

    private void FinalizeSpecificationValidation(
        OpenApiValidationResult result,
        IEnumerable<SchemaResolutionIssue> schemaResolutionIssues,
        SpecificationStageResult specificationStage)
    {
        if (!_openApiValidationOptions.ValidateSpecification || specificationStage.SpecValidation == null)
        {
            return;
        }

        var validationErrors = specificationStage.SpecValidationErrors ?? new List<ValidationError>();
        AddCircularReferenceValidationIssues(schemaResolutionIssues, validationErrors);
        specificationStage.SpecValidation.Errors = ValidationErrorNormalizer.NormalizeAndDeduplicateByPath(validationErrors);
        specificationStage.SpecValidation.IsValid = !specificationStage.SpecValidation.Errors.Any(e =>
            string.Equals(e.Severity, "Error", StringComparison.OrdinalIgnoreCase));
        result.SpecificationValidation = specificationStage.SpecValidation;
    }

    private async Task ExecuteFullHsdsRuntimeValidationAsync(
        OpenApiValidationRequest request,
        OpenApiValidationResult result,
        List<EndpointTestResult> endpointTests,
        bool feedSpecFellBackToHsdsProfile,
        JObject? resolvedHsdsProfileSpec,
        CancellationToken cancellationToken)
    {
        if (_openApiValidationOptions.HsdsValidationMode != HsdsValidationMode.FullHsdsRuntime)
        {
            return;
        }

        var useOwnSchemaValidation = _openApiValidationOptions.OwnSchemaValidation != OwnSchemaValidationMode.None;
        if (feedSpecFellBackToHsdsProfile || !useOwnSchemaValidation)
        {
            var skipReason = !useOwnSchemaValidation
                ? "OwnSchemaValidation is set to None, so endpoint responses were already validated against the HSDS profile schema during endpoint testing."
                : "the feed's OpenAPI specification could not be fetched, so endpoint responses were already validated against the HSDS profile specification during endpoint testing.";
            result.Notifications.Add(
                $"Full HSDS runtime validation was skipped: {skipReason} No second pass is needed.");
        }
        else if (resolvedHsdsProfileSpec != null)
        {
            await _hsdsComplianceService.ValidateEndpointResponsesAgainstHsdsProfileAsync(
                endpointTests,
                resolvedHsdsProfileSpec,
                request.Options!,
                cancellationToken);
        }
        else
        {
            result.Notifications.Add("Full HSDS runtime mode requested, but no known HSDS profile schema could be resolved.");
        }

        if (!request.Options!.IncludeResponseBody)
        {
            ReleaseEndpointResponseBodies(endpointTests);
        }
    }

    private void ApplyResultShaping(OpenApiValidationResult result, OpenApiValidationOptions options)
    {
        if (!options.IncludeResponseBody && result.EndpointTests != null)
        {
            ReleaseEndpointResponseBodies(result.EndpointTests);
        }
        else if (result.EndpointTests != null)
        {
            ApplyResponseBodyRetentionCap(result.EndpointTests, result.Notifications);
        }

        if (!options.IncludeTestResults && result.EndpointTests != null)
        {
            foreach (var ep in result.EndpointTests)
            {
                ep.RefreshFlattenedFields();
                ep.TestResults.Clear();
            }
        }
    }

    private static void ReleaseEndpointResponseBodies(IEnumerable<EndpointTestResult> endpointTests)
    {
        foreach (var ep in endpointTests)
        {
            if (ep.TestResults == null)
            {
                continue;
            }

            foreach (var tr in ep.TestResults)
            {
                tr.ResponseBody = null;
            }
        }
    }

    private string? TrySetProfileReasonFromOpenApiSpec(
        OpenApiValidationRequest request,
        JObject openApiSpec,
        bool usedBaseUrlDiscovery)
    {
        if (HasExplicitProfileVersionContext(request.ProfileReason, request.OwnSchemaUrl))
        {
            return null;
        }

        var (versionFromSpec, fromOpenapiField) = TryExtractProfileVersionFromOpenApiSpec(openApiSpec);
        if (!string.IsNullOrWhiteSpace(versionFromSpec))
        {
            request.ProfileReason = $"Standard version [user: {versionFromSpec}] read from OpenAPI spec";
            if (fromOpenapiField)
            {
                _logger.HsdsVersionMisplaced(
                    TextSanitizer.SanitizeStringForLogging(openApiSpec.SelectToken("openapi")?.ToString() ?? string.Empty),
                    versionFromSpec);
                return
                    $"Warning: The HSDS schema version was incorrectly defined in the 'openapi' field. " +
                    $"Detected HSDS version {versionFromSpec} from this field as a fallback. " +
                    $"Please use an 'x-hsds-version' field in your OpenAPI spec to declare the HSDS version.";
            }

            return null;
        }

        if (usedBaseUrlDiscovery)
        {
            return null;
        }

        return null;
    }

    private sealed class ValidationExecutionOutcome
    {
        public OpenApiSpecificationValidation? SpecificationValidation { get; init; }
        public List<EndpointTestResult> EndpointTests { get; init; } = new();
        public string? ClaimedProfileVersion { get; init; }
    }

    private sealed class DiscoveryPreparation
    {
        public bool UsedBaseUrlDiscovery { get; init; }
        public required ProfileResolutionState ProfileState { get; init; }
        public required JObject OpenApiSpec { get; init; }
        public bool FeedSpecFellBackToHsdsProfile { get; init; }
        public string? EffectiveOwnSchemaUrl { get; init; }
        public string? DiscoveredOpenApiSchemaContent { get; init; }
        public string? DiscoveredHsdsProfileSchemaContent { get; init; }
        public bool HasConfiguredDefaultProfile { get; init; }
        public string? DefaultProfileSchemaUrl { get; init; }
        public string? DefaultProfileVersion { get; init; }
        public DataSourceAuthentication? SchemaRequestAuth { get; init; }
        public DataSourceAuthentication? DataSourceRequestAuth { get; init; }
        public string? DiscoveredHsdsProfileVersion { get; init; }
        public string? DiscoveredHsdsProfileReason { get; init; }
    }

    private sealed class ProfileResolutionState
    {
        public string? ClaimedProfileVersion { get; init; }
        public string? KnownHsdsSchemaUrl { get; init; }
        public JObject? ResolvedHsdsProfileSpec { get; init; }
    }

    private sealed class SpecificationStageResult
    {
        public OpenApiSpecificationValidation? SpecValidation { get; init; }
        public List<ValidationError>? SpecValidationErrors { get; init; }
        public required ProfileResolutionState FinalProfileState { get; init; }
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

    private static JObject? TryParseJObject(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JObject.Parse(json);
        }
        catch
        {
            return null;
        }
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

    private static JObject PrepareEndpointValidationSpecForEndpointTesting(JObject openApiSpec, string? baseUrl, out string? pathDeduplicationWarning)
    {
        pathDeduplicationWarning = null;

        if (!WouldDuplicateBasePathRequireMutation(openApiSpec, baseUrl))
        {
            return openApiSpec;
        }

        var clonedSpec = (JObject)openApiSpec.DeepClone();
        pathDeduplicationWarning = RemoveDuplicatedBasePathFromOpenApiPaths(clonedSpec, baseUrl);
        return clonedSpec;
    }

    private static bool WouldDuplicateBasePathRequireMutation(JObject openApiSpec, string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl)
            || openApiSpec["paths"] is not JObject pathsObject
            || pathsObject.Count == 0)
        {
            return false;
        }

        var baseUri = TryParseBaseUri(baseUrl);
        if (baseUri == null)
        {
            return false;
        }

        var basePath = NormalizePath(baseUri.AbsolutePath);
        if (string.IsNullOrEmpty(basePath) || basePath == "/")
        {
            return false;
        }

        foreach (var property in pathsObject.Properties())
        {
            var originalPath = NormalizePath(property.Name);
            var deduplicatedPath = TryStripDuplicateBasePath(originalPath, basePath) ?? originalPath;
            if (!string.Equals(deduplicatedPath, originalPath, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
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

    private async Task<JObject> GetCachedResolvedOpenApiSpecAsync(
        string specUrl,
        DataSourceAuthentication? auth,
        CancellationToken cancellationToken,
        string cacheScope,
        ICollection<SchemaResolutionIssue>? collectedIssues = null)
    {
        var lookupStopwatch = Stopwatch.StartNew();
        var lookupStartManagedHeapBytes = GC.GetTotalMemory(forceFullCollection: false);
        var lookupStartWorkingSetBytes = Environment.WorkingSet;
        var sanitizedSpecUrl = TextSanitizer.SanitizeUrlForLogging(specUrl);

        void LogLookupCheckpoint(string outcome)
        {
            var managedHeapBytes = GC.GetTotalMemory(forceFullCollection: false);
            var processWorkingSetBytes = Environment.WorkingSet;

            _logger.ResolvedOpenApiCacheLookupMemoryCheckpoint(
                outcome,
                cacheScope,
                sanitizedSpecUrl,
                managedHeapBytes,
                managedHeapBytes - lookupStartManagedHeapBytes,
                processWorkingSetBytes,
                processWorkingSetBytes - lookupStartWorkingSetBytes,
                lookupStopwatch.Elapsed.TotalMilliseconds);
        }

        var cache = ResolveCacheByScope(cacheScope);
        var cacheKey = $"resolved-openapi:{specUrl}";

        if (_cacheOptions.Enabled)
        {
            if (cache.TryGetValue(cacheKey, out var cachedEntry)
                && cachedEntry.ExpiresAtUtc > DateTime.UtcNow
                && !string.IsNullOrWhiteSpace(cachedEntry.ResolvedSpecJson))
            {
                ResolvedOpenApiCacheHitsCounter.Add(1, new KeyValuePair<string, object?>("scope", cacheScope));
                _logger.ResolvedOpenApiCacheHit(cacheScope, sanitizedSpecUrl);
                LogLookupCheckpoint("cache-hit");
                return cachedEntry.ResolvedSpecDocument;
            }

            ResolvedOpenApiCacheMissesCounter.Add(1, new KeyValuePair<string, object?>("scope", cacheScope));
            _logger.ResolvedOpenApiCacheMiss(cacheScope, sanitizedSpecUrl);
        }

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
                        resolvedFromWarmupObject,
                        DateTime.UtcNow.Add(GetProfileSchemaCacheTtl()));
                }

                _logger.ResolvedProfileViaWarmup(sanitizedSpecUrl);
                LogLookupCheckpoint("cache-miss-warmup-hit");

                return resolvedFromWarmupObject;
            }
            catch (Exception ex)
            {
                _logger.WarmupPathResolutionUnavailable(ex, sanitizedSpecUrl);
            }
        }

        var unresolvedSpec = await _specFetcher.FetchOpenApiSpecFromUrlAsync(
            specUrl,
            auth,
            cancellationToken,
            resolveReferences: false);

        var unresolvedSpecContent = unresolvedSpec.ToString();
        var resolvedSpecContent = await _schemaResolverService.ResolveAsync(unresolvedSpecContent, specUrl, auth);
        if (string.IsNullOrWhiteSpace(resolvedSpecContent))
        {
            // Defensive fallback for misconfigured/mocked resolvers that return empty output.
            resolvedSpecContent = unresolvedSpecContent;
        }
        CollectSchemaResolutionIssues(collectedIssues);

        if (_cacheOptions.Enabled)
        {
            PurgeExpiredCacheEntries();
            var resolvedSpecObject = JObject.Parse(resolvedSpecContent);
            cache[cacheKey] = new CachedResolvedSpec(
                resolvedSpecContent,
                resolvedSpecObject,
                DateTime.UtcNow.Add(GetProfileSchemaCacheTtl()));
            LogLookupCheckpoint("cache-miss-direct");
            return resolvedSpecObject;
        }

        LogLookupCheckpoint("cache-disabled-direct");
        return JObject.Parse(resolvedSpecContent);
    }

    private TimeSpan GetProfileSchemaCacheTtl()
    {
        return _cacheOptions.ExpirationMinutes > 0
            ? TimeSpan.FromMinutes(_cacheOptions.ExpirationMinutes)
            : TimeSpan.FromHours(2);
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
