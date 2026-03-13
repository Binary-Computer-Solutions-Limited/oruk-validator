using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json.Linq;
using OpenReferralApi.Core.Models;
using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using ValidationError = OpenReferralApi.Core.Models.ValidationError;

namespace OpenReferralApi.Core.Services;

public interface IOpenApiValidationService
{
    Task<OpenApiValidationResult> ValidateOpenApiSpecificationAsync(OpenApiValidationRequest request, CancellationToken cancellationToken = default);
}

public class OpenApiValidationService : IOpenApiValidationService
{
    private static readonly Regex ArrayIndexRegex = new(@"\[[^\]]*\]", RegexOptions.Compiled);

    private readonly ILogger<OpenApiValidationService> _logger;
    private readonly ISchemaResolverService _schemaResolverService;
    private readonly IOpenApiDiscoveryService _discoveryService;
    private readonly IOpenApiSpecificationService _openApiSpecificationService;
    private readonly IHsdsComplianceService _hsdsComplianceService;
    private readonly IEndpointTestingService _endpointTestingService;
    private readonly OpenApiSpecFetcher _specFetcher;
    private readonly bool _allowUserSuppliedAuth;

    public OpenApiValidationService(
        ILogger<OpenApiValidationService> logger,
        HttpClient httpClient,
        IJsonValidatorService jsonValidatorService,
        ISchemaResolverService schemaResolverService,
        IOpenApiDiscoveryService discoveryService,
        IOptions<AuthenticationOptions> authOptions,
        IOpenApiSpecificationService? openApiSpecificationService = null,
        IHsdsComplianceService? hsdsComplianceService = null,
        IEndpointTestingService? endpointTestingService = null)
    {
        _logger = logger;
        _schemaResolverService = schemaResolverService;
        _discoveryService = discoveryService;
        _openApiSpecificationService = openApiSpecificationService ?? new OpenApiSpecificationService(NullLogger<OpenApiSpecificationService>.Instance, jsonValidatorService);
        _hsdsComplianceService = hsdsComplianceService ?? new HsdsComplianceService(jsonValidatorService);
        _endpointTestingService = endpointTestingService ?? new EndpointTestingService(NullLogger<EndpointTestingService>.Instance, httpClient, jsonValidatorService, _hsdsComplianceService);
        _allowUserSuppliedAuth = authOptions.Value.AllowUserSuppliedAuth;
        _specFetcher = new OpenApiSpecFetcher(httpClient, logger, schemaResolverService, allowUserSuppliedAuth: _allowUserSuppliedAuth);
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

            // Discover OpenAPI schema URL if not provided
            if (request.OpenApiSchema == null || string.IsNullOrEmpty(request.OpenApiSchema.Url))
            {
                if (!string.IsNullOrEmpty(request.BaseUrl))
                {
                    var (discoveredUrl, reason) = await _discoveryService.DiscoverOpenApiUrlAsync(request.BaseUrl, cancellationToken);
                    if (!string.IsNullOrEmpty(discoveredUrl))
                    {
                        _logger.LogInformation("Discovered OpenAPI schema URL: {Url} (Reason: {Reason})", SchemaResolverService.SanitizeUrlForLogging(discoveredUrl), reason);
                        request.OpenApiSchema ??= new OpenApiSchema();
                        request.OpenApiSchema.Url = discoveredUrl;
                        request.ProfileReason = reason;
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

            // Get OpenAPI specification
            JObject openApiSpec;
            bool isResolved = false;

            // User-supplied authentication for schema and datasource requests is feature-gated
            // and must pass strict validation before it can be applied.
            var schemaRequestAuth = TryGetValidatedRequestAuthentication("schema", request.OpenApiSchema?.Authentication);
            var dataSourceRequestAuth = TryGetValidatedRequestAuthentication("datasource", request.DataSourceAuth);

            if (!string.IsNullOrEmpty(request.OpenApiSchema?.Url))
            {
                // Fetch OpenAPI spec but defer resolution until we know we need it
                // This avoids expensive resolution when we're only validating spec structure
                // or when most endpoints won't be tested
                openApiSpec = await _specFetcher.FetchOpenApiSpecFromUrlAsync(
                    request.OpenApiSchema.Url,
                    schemaRequestAuth,
                    cancellationToken,
                    resolveReferences: false);
            }
            else
            {
                throw new ArgumentException("OpenAPI schema URL must be provided or BaseUrl must allow discovery");
            }

            // Validate the OpenAPI specification
            OpenApiSpecificationValidation? specValidation = null;
            if (request.Options.ValidateSpecification)
            {
                specValidation = await _openApiSpecificationService.ValidateAsync(openApiSpec, cancellationToken);
                result.SpecificationValidation = specValidation;
            }

            var claimedProfileVersion = _hsdsComplianceService.ExtractClaimedProfileVersion(request.ProfileReason, request.OpenApiSchema?.Url);
            JObject? resolvedHsdsProfileSpec = null;

            // Compare feed specification against the known HSDS baseline profile, when discoverable.
            if (request.Options.ValidateSpecification && specValidation != null)
            {
                if (_hsdsComplianceService.TryGetKnownHsdsSchemaUrl(claimedProfileVersion, out var knownHsdsSchemaUrl))
                {
                    if (!isResolved)
                    {
                        var resolvedFeedSpec = await _schemaResolverService.ResolveAsync(openApiSpec.ToString(), request.OpenApiSchema?.Url, schemaRequestAuth);
                        openApiSpec = JObject.Parse(resolvedFeedSpec);
                        isResolved = true;
                    }

                    var hsdsSpec = await _specFetcher.FetchOpenApiSpecFromUrlAsync(
                        knownHsdsSchemaUrl,
                        null,
                        cancellationToken,
                        resolveReferences: false);

                    var resolvedHsdsSpec = await _schemaResolverService.ResolveAsync(hsdsSpec.ToString(), knownHsdsSchemaUrl, null);
                    hsdsSpec = JObject.Parse(resolvedHsdsSpec);
                    resolvedHsdsProfileSpec = hsdsSpec;

                    var profileComplianceFindings = _hsdsComplianceService.CompareFeedSpecAgainstHsdsProfile(openApiSpec, hsdsSpec);
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
                // Resolve references now that we know we're actually testing endpoints
                // This lazy approach avoids wasting resolution work when endpoints aren't tested
                if (!isResolved)
                {
                    _logger.LogDebug("Resolving OpenAPI document references for endpoint testing");
                    var resolvedContent = await _schemaResolverService.ResolveAsync(openApiSpec.ToString(), request.OpenApiSchema?.Url, schemaRequestAuth);
                    openApiSpec = JObject.Parse(resolvedContent);
                    isResolved = true;
                }

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

    private DataSourceAuthentication? TryGetValidatedRequestAuthentication(string context, DataSourceAuthentication? auth)
    {
        // Evaluate the server-side feature gate first so user-controlled request content
        // cannot influence whether the authorization policy check is reached.
        if (!_allowUserSuppliedAuth)
        {
            if (auth != null)
            {
                _logger.LogWarning(
                    "User-supplied authentication was provided for {Context} but is disabled by server configuration",
                    SanitizeForLogging(context));
            }

            return null;
        }

        var validated = ValidateAuthentication(auth);
        if (validated == null && auth != null)
        {
            _logger.LogWarning(
                "Rejected invalid user-supplied authentication for {Context}",
                SanitizeForLogging(context));
        }

        return validated;
    }

    private static DataSourceAuthentication? ValidateAuthentication(DataSourceAuthentication? auth)
    {
        if (auth == null)
        {
            return null;
        }

        const int maxTokenLength = 4096;

        static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        static bool IsTooLong(string? value, int maxLength) => !string.IsNullOrEmpty(value) && value.Length > maxLength;

        // Perform stricter validation on the authentication configuration.
        // If it fails validation, treat it as if no authentication was provided.
        var apiKey = Normalize(auth.ApiKey);
        var apiKeyHeader = Normalize(auth.ApiKeyHeader) ?? "X-API-Key";
        var bearerToken = Normalize(auth.BearerToken);
        var basicUsername = Normalize(auth.BasicAuth?.Username);
        var basicPassword = Normalize(auth.BasicAuth?.Password);

        if (IsTooLong(apiKey, maxTokenLength) ||
            IsTooLong(bearerToken, maxTokenLength) ||
            IsTooLong(basicUsername, maxTokenLength) ||
            IsTooLong(basicPassword, maxTokenLength))
        {
            return null;
        }

        var hasApiKey = !string.IsNullOrEmpty(apiKey);
        if (hasApiKey && !IsValidHttpHeaderName(apiKeyHeader))
        {
            return null;
        }

        var hasBearer = !string.IsNullOrEmpty(bearerToken);
        var hasBasic = auth.BasicAuth != null
                       && !string.IsNullOrEmpty(basicUsername)
                       && !string.IsNullOrEmpty(basicPassword);
        var hasCustomHeaders = auth.CustomHeaders != null && auth.CustomHeaders.Count > 0;

        var mechanismsCount = 0;
        if (hasApiKey) mechanismsCount++;
        if (hasBearer) mechanismsCount++;
        if (hasBasic) mechanismsCount++;
        if (hasCustomHeaders) mechanismsCount++;

        // Require at least one and at most one primary authentication mechanism.
        if (mechanismsCount != 1)
        {
            return null;
        }

        // Return a sanitized copy so that downstream code does not operate on the original user object.
        var validated = new DataSourceAuthentication();

        if (hasApiKey)
        {
            validated.ApiKey = apiKey;
            validated.ApiKeyHeader = apiKeyHeader;
        }
        else if (hasBearer)
        {
            validated.BearerToken = bearerToken;
        }
        else if (hasBasic)
        {
            validated.BasicAuth = new BasicAuthentication
            {
                Username = basicUsername!,
                Password = basicPassword!
            };
        }
        else if (hasCustomHeaders)
        {
            // Copy only non-empty header names and values that pass header safety checks.
            validated.CustomHeaders = new Dictionary<string, string>();
            foreach (var kvp in auth.CustomHeaders!)
            {
                var headerName = Normalize(kvp.Key);
                var headerValue = Normalize(kvp.Value);

                if (string.IsNullOrEmpty(headerName) ||
                    string.IsNullOrEmpty(headerValue) ||
                    !IsValidHttpHeaderName(headerName) ||
                    !IsSafeHeaderValue(headerValue) ||
                    IsTooLong(headerValue, maxTokenLength))
                {
                    return null;
                }

                validated.CustomHeaders[headerName] = headerValue;
            }

            if (validated.CustomHeaders.Count == 0)
            {
                return null;
            }

            if (validated.CustomHeaders.Count > 20)
            {
                return null;
            }
        }

        // Ensure all outgoing header values are safe against CRLF/control character injection.
        if ((validated.ApiKey != null && !IsSafeHeaderValue(validated.ApiKey)) ||
            (validated.BearerToken != null && !IsSafeHeaderValue(validated.BearerToken)) ||
            (validated.BasicAuth?.Username != null && !IsSafeHeaderValue(validated.BasicAuth.Username)) ||
            (validated.BasicAuth?.Password != null && !IsSafeHeaderValue(validated.BasicAuth.Password)))
        {
            return null;
        }

        return validated;
    }

    private static bool IsValidHttpHeaderName(string headerName)
    {
        if (string.IsNullOrWhiteSpace(headerName))
        {
            return false;
        }

        const string allowedHeaderTokenSymbols = "!#$%&'*+-.^_`|~";

        foreach (var c in headerName)
        {
            if (char.IsLetterOrDigit(c))
            {
                continue;
            }

            if (allowedHeaderTokenSymbols.IndexOf(c) >= 0)
            {
                continue;
            }

            return false;
        }

        return true;
    }

    private static bool IsSafeHeaderValue(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        foreach (var c in value)
        {
            if (c == '\r' || c == '\n' || char.IsControl(c))
            {
                return false;
            }
        }

        return true;
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

    /// <summary>
    /// Sanitizes a string for safe inclusion in log messages by removing control characters.
    /// </summary>
    /// <param name="value">The value to sanitize.</param>
    /// <returns>A sanitized string safe for logging.</returns>
    private static string SanitizeForLogging(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        // Remove carriage returns and newlines to prevent log forging
        var sanitized = value.Replace("\r", string.Empty)
                             .Replace("\n", string.Empty);

        return sanitized;
    }

}
