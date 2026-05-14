using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json.Nodes;
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
        IOptions<CacheOptions>? cacheOptions = null,
        IOptions<SpecificationOptions>? specificationOptions = null,
        IOptions<OpenApiValidationServerOptions>? openApiValidationServerOptions = null)
    {
        _logger = logger;
        _schemaResolverService = schemaResolverService;
        _openApiSpecificationService = openApiSpecificationService;
        _hsdsComplianceService = hsdsComplianceService ?? new HsdsComplianceService(jsonValidatorService, specificationOptions, openApiValidationServerOptions);
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
            var sanitizedBaseUrl = TextSanitizer.SanitizeUrlForLogging(request.BaseUrl ?? string.Empty);
            var profile = ResolveMetadataProfileIdentifier(
                _hsdsComplianceService.ExtractClaimedProfileVersion(null, request.OwnSchemaUrl),
                request.OwnSchemaUrl);
            var cacheState = GetResolvedOpenApiCacheState();

            var payload = CreateMemoryCheckpointPayload(
                service: nameof(OpenApiValidationService),
                stage: stage,
                sanitizedBaseUrl: sanitizedBaseUrl,
                snapshot: snapshot,
                profile: TextSanitizer.SanitizeStringForLogging(profile ?? string.Empty)) with
            {
                FeedCacheEntries = cacheState.FeedEntries,
                ProfileCacheEntries = cacheState.ProfileEntries,
                ExpiredCacheEntries = cacheState.ExpiredEntries,
                FeedJsonChars = cacheState.FeedJsonChars,
                ProfileJsonChars = cacheState.ProfileJsonChars
            };

            _logger.UnifiedMemoryCheckpoint(payload);

            var tags = new TagList { { "stage", stage } };
            ValidationManagedHeapBytesHistogram.Record(snapshot.ManagedHeapBytes, tags);
            ValidationManagedHeapDeltaBytesHistogram.Record(snapshot.ManagedHeapDeltaBytes, tags);
            ValidationWorkingSetBytesHistogram.Record(snapshot.ProcessWorkingSetBytes, tags);
            ValidationWorkingSetDeltaBytesHistogram.Record(snapshot.ProcessWorkingSetDeltaBytes, tags);

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
                Profile = ResolveMetadataProfileIdentifier(executionOutcome.ClaimedProfileVersion, request.OwnSchemaUrl),
                ProfileReason = executionOutcome.ProfileReason
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
        // Step 1: Discovery and preparation of OpenAPI schema content, HSDS profile schemas, and related metadata.
        var discovery = await PrepareValidationDiscoveryAsync(request, result, schemaResolutionIssues, cancellationToken);
        logMemoryCheckpoint("schema-url-discovery");

        // Step 2: Specification validation, including comparison against any discovered HSDS profile schema to produce profile compliance findings.
        var specificationStage = await ExecuteSpecificationStageAsync(
            request,
            result,
            discovery.HsdsProfileVersion,
            discovery.HsdsProfileSchema,
            discovery.OwnSchema,
            discovery.HsdsProfileReason,
            cancellationToken);
        logMemoryCheckpoint("specification-validation");

        // Step 3: Endpoint testing.
        var endpointTests = await ExecuteEndpointTestingAsync(
            request,
            result,
            discovery.DataSourceRequestAuth,
            discovery.OwnSchema,
            discovery.HsdsProfileSchema,
            specificationStage.SpecValidation,
            specificationStage.SpecValidationErrors,
            cancellationToken);
        logMemoryCheckpoint("endpoint-testing");

        // Step 4: Finalize specification validation.
        FinalizeSpecificationValidation(result, schemaResolutionIssues, specificationStage);

        // Step 5: Full HSDS runtime validation.
        await ExecuteFullHsdsRuntimeValidationAsync(
            request,
            result,
            endpointTests,
            discovery.FellBackToHsdsProfile,
            discovery.HsdsProfileSchema,
            cancellationToken);
        logMemoryCheckpoint("full-hsds-runtime");

        // The endpoint test results may have been mutated by the full HSDS runtime validation step, so we use the (potentially) updated results in the final shaping and summary generation steps.
        return new ValidationExecutionOutcome
        {
            SpecificationValidation = result.SpecificationValidation,
            EndpointTests = endpointTests,
            ClaimedProfileVersion = discovery.HsdsProfileVersion,
            ProfileReason = discovery.HsdsProfileReason
        };
    }

    private async Task<DiscoveryPreparation> PrepareValidationDiscoveryAsync(
        OpenApiValidationRequest request,
        OpenApiValidationResult result,
        List<SchemaResolutionIssue> schemaResolutionIssues,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.BaseUrl))
        {
            throw new ArgumentException("Base URL must be provided", nameof(request.BaseUrl));
        }

        var dataSourceRequestAuth =
            _authenticationValidationService.TryGetValidatedRequestAuthentication("datasource", request.DataSourceAuth);

        var bootstrap = await _profileDiscoveryService.DiscoverFromBaseUrlAsync(
            request.OwnSchemaUrl,
            request.BaseUrl,
            dataSourceRequestAuth,
            cancellationToken);

        var ownSchema = TryParseJObject(bootstrap.OpenApiSchemaContent);
        var resolvedHsdsProfileSpec = TryParseJObject(bootstrap.HsdsProfileSchemaContent);

        var feedSpecFellBackToHsdsProfile = false;
        if (ownSchema == null && resolvedHsdsProfileSpec != null)
        {
            feedSpecFellBackToHsdsProfile = true;
            
            if (!string.IsNullOrWhiteSpace(request.OwnSchemaUrl))
            {
                result.Notifications.Add(
                    "Unable to fetch OpenAPI specification from the feed URL. Falling back to the HSDS profile OpenAPI specification.");
            }
            else if (bootstrap.UsedDefaultProfile)
            {
                result.Notifications.Add(
                    "Unable to discover feed OpenAPI specification from the base URL. Falling back to the configured default HSDS profile OpenAPI specification.");
            }
        }

        if (ownSchema == null && bootstrap.HsdsProfileVersion == null)
        {
            throw new ArgumentException("Failed to discover Own OpenAPI schema URL or HSDS profile version from base URL");
        }

        var discoveredProfileVersion = bootstrap.HsdsProfileVersion;

        if (_openApiValidationOptions.OwnSchemaValidation != OwnSchemaValidationMode.None
            && ownSchema == null
            && string.IsNullOrWhiteSpace(request.OwnSchemaUrl)
            && !string.IsNullOrWhiteSpace(request.BaseUrl)
            && !feedSpecFellBackToHsdsProfile)
        {
            result.Notifications.Add("OwnSchemaValidation is enabled, but no feed OpenAPI schema was discovered from the base URL.");
        }

        return new DiscoveryPreparation
        {
            HsdsProfileVersion = discoveredProfileVersion,
            HsdsProfileSchema = resolvedHsdsProfileSpec,
            OwnSchema = ownSchema,
            FellBackToHsdsProfile = feedSpecFellBackToHsdsProfile,
            OwnSchemaUrl = request.OwnSchemaUrl,
            HasConfiguredDefaultProfile = bootstrap.UsedDefaultProfile,
            DataSourceRequestAuth = dataSourceRequestAuth,
            HsdsProfileReason = bootstrap.HsdsProfileReason
        };
    }

    private async Task<SpecificationStageResult> ExecuteSpecificationStageAsync(
        OpenApiValidationRequest request,
        OpenApiValidationResult result,
        string? hsdsProfileVersion,
        JObject? hsdsProfileSchemaContent,
        JObject openApiSchemaContent,
        string? profileReason,
        CancellationToken cancellationToken)
    {
        var isMisplacedVersionWarning = profileReason?.StartsWith("Warning:", StringComparison.Ordinal) == true;
        OpenApiSpecificationValidation? specValidation = null;
        List<ValidationError>? specValidationErrors = null;
        if (_openApiValidationOptions.ValidateSpecification)
        {
            specValidation = await _openApiSpecificationService.ValidateAsync(openApiSchemaContent, cancellationToken);
            specValidationErrors = new List<ValidationError>(specValidation.Errors);

            if (isMisplacedVersionWarning)
            {
                specValidationErrors.Add(new ValidationError
                {
                    Path = "openapi",
                    Message = profileReason!,
                    ErrorCode = "HSDS_SCHEMA_VERSION_MISPLACED",
                    Severity = "Warning"
                });
            }
        }
        else if (isMisplacedVersionWarning)
        {
            result.Notifications.Add(profileReason!);
        }

        if (_openApiValidationOptions.ValidateSpecification && specValidation != null)
        {
            ApplySpecificationComparisonAgainstProfile(
                request,
                openApiSchemaContent,
                hsdsProfileSchemaContent,
                specValidationErrors!,
                hsdsProfileVersion);
        }

        return new SpecificationStageResult
        {
            SpecValidation = specValidation,
            SpecValidationErrors = specValidationErrors
        };
    }

    private void ApplySpecificationComparisonAgainstProfile(
        OpenApiValidationRequest request,
        JObject openApiSchemaContent,
        JObject? hsdsProfileSchemaContent,
        List<ValidationError> specValidationErrors,
        string? profileVersion)
    {
        if (hsdsProfileSchemaContent != null)
        {
            var openApiSchemaNode = ToJsonNode(openApiSchemaContent);
            var hsdsProfileSchemaNode = ToJsonNode(hsdsProfileSchemaContent);
            if (openApiSchemaNode == null || hsdsProfileSchemaNode == null)
            {
                return;
            }

            var profileComplianceFindings = _hsdsComplianceService.CompareFeedSpecAgainstHsdsProfile(openApiSchemaNode, hsdsProfileSchemaNode);
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

        var hasProfileContext = !string.IsNullOrWhiteSpace(profileVersion)
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
        JObject openApiSchemaContent,
        JObject? resolvedHsdsProfileSpec,
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
                endpointValidationSpec = openApiSchemaContent;
                result.Notifications.Add(
                    "OwnSchemaValidation is set to None but no HSDS profile schema could be resolved; falling back to the feed's own schema for endpoint validation.");
            }
        }
        else
        {
            endpointValidationSpec = openApiSchemaContent;
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

        endpointTests = await _endpointTestingService.TestEndpointsAsync(
            endpointValidationSpec,
            request.BaseUrl,
            request.Options!,
            dataSourceRequestAuth,
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
            var resolvedHsdsProfileNode = ToJsonNode(resolvedHsdsProfileSpec);
            if (resolvedHsdsProfileNode == null)
            {
                result.Notifications.Add("Full HSDS runtime mode requested, but the resolved HSDS profile schema could not be parsed.");
                return;
            }

            await _hsdsComplianceService.ValidateEndpointResponsesAgainstHsdsProfileAsync(
                endpointTests,
                resolvedHsdsProfileNode,
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

    private sealed class ValidationExecutionOutcome
    {
        public OpenApiSpecificationValidation? SpecificationValidation { get; init; }
        public List<EndpointTestResult> EndpointTests { get; init; } = new();
        public string? ClaimedProfileVersion { get; init; }
        public string? ProfileReason { get; init; }
    }

    private sealed class DiscoveryPreparation
    {
        public string? HsdsProfileVersion { get; init; }
        public JObject? HsdsProfileSchema { get; init; }
        public required JObject? OwnSchema { get; init; }
        public bool FellBackToHsdsProfile { get; init; }
        public string? OwnSchemaUrl { get; init; }
        public bool HasConfiguredDefaultProfile { get; init; }
        public DataSourceAuthentication? DataSourceRequestAuth { get; init; }
                public string? HsdsProfileReason { get; init; }
    }

    private sealed class SpecificationStageResult
    {
        public OpenApiSpecificationValidation? SpecValidation { get; init; }
        public List<ValidationError>? SpecValidationErrors { get; init; }
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

    private static JsonNode? ToJsonNode(JObject? value)
    {
        return value is null ? null : JsonNode.Parse(value.ToString());
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

    private string? ResolveMetadataProfileIdentifier(string? claimedProfileVersion, string? schemaUrl)
    {
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

        return claimedProfileVersion;
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
