using System.Text.Json.Nodes;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using OpenReferralApi.Core.Logging;
using ValidationError = OpenReferralApi.Core.Models.Validation.ValidationError;

namespace OpenReferralApi.Core.Services;

public interface IEndpointTestingService
{
    Task<List<EndpointTestResult>> TestEndpointsAsync(
        JObject openApiSpec,
        string baseUrl,
        OpenApiValidationOptions options,
        DataSourceAuthentication? authentication,
        string? documentUri,
        CancellationToken cancellationToken = default);
}

public class EndpointTestingService : IEndpointTestingService
{
    private const string EndpointTestingMetricsMeterName = "OpenReferralApi.Core.EndpointTestingService";
    private static readonly Meter EndpointTestingMetricsMeter = new(EndpointTestingMetricsMeterName, "1.0.0");
    private static readonly Histogram<long> EndpointTestingManagedHeapBytesHistogram = EndpointTestingMetricsMeter.CreateHistogram<long>(
        "openreferral.openapi.endpoint_testing.memory.managed_heap_bytes",
        unit: "By",
        description: "Managed heap size observed at endpoint testing memory checkpoints");
    private static readonly Histogram<long> EndpointTestingManagedHeapDeltaBytesHistogram = EndpointTestingMetricsMeter.CreateHistogram<long>(
        "openreferral.openapi.endpoint_testing.memory.managed_heap_delta_bytes",
        unit: "By",
        description: "Managed heap delta between endpoint testing memory checkpoints");
    private static readonly Histogram<long> EndpointTestingWorkingSetBytesHistogram = EndpointTestingMetricsMeter.CreateHistogram<long>(
        "openreferral.openapi.endpoint_testing.memory.working_set_bytes",
        unit: "By",
        description: "Process working set observed at endpoint testing memory checkpoints");
    private static readonly Histogram<long> EndpointTestingWorkingSetDeltaBytesHistogram = EndpointTestingMetricsMeter.CreateHistogram<long>(
        "openreferral.openapi.endpoint_testing.memory.working_set_delta_bytes",
        unit: "By",
        description: "Process working set delta between endpoint testing memory checkpoints");
    private readonly ILogger<EndpointTestingService> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IJsonValidatorService _jsonValidatorService;
    private readonly IHsdsComplianceService _hsdsComplianceService;
    private readonly OpenApiValidationServerOptions? _openApiValidationOptions;
    private readonly ConcurrentDictionary<string, JToken> _validationSchemaCache = new(StringComparer.Ordinal);

    public EndpointTestingService(
        ILogger<EndpointTestingService> logger,
        IHttpClientFactory httpClientFactory,
        IJsonValidatorService jsonValidatorService,
        IHsdsComplianceService hsdsComplianceService,
        IOptions<OpenApiValidationServerOptions>? openApiValidationOptions = null)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _jsonValidatorService = jsonValidatorService;
        _hsdsComplianceService = hsdsComplianceService;
        _openApiValidationOptions = openApiValidationOptions?.Value;
    }
    public async Task<List<EndpointTestResult>> TestEndpointsAsync(JObject openApiSpec, string baseUrl, OpenApiValidationOptions options, DataSourceAuthentication? authentication, string? documentUri, CancellationToken cancellationToken = default)
    {
        var results = new List<EndpointTestResult>();
        var stopwatch = Stopwatch.StartNew();
        var lastManagedHeapBytes = GC.GetTotalMemory(forceFullCollection: false);
        var lastWorkingSetBytes = Environment.WorkingSet;

        void LogMemoryCheckpoint(string stage, string groupName)
        {
            var managedHeapBytes = GC.GetTotalMemory(forceFullCollection: false);
            var managedHeapDeltaBytes = managedHeapBytes - lastManagedHeapBytes;
            var processWorkingSetBytes = Environment.WorkingSet;
            var processWorkingSetDeltaBytes = processWorkingSetBytes - lastWorkingSetBytes;

            _logger.EndpointTestingMemoryCheckpoint(
                stage,
                TextSanitizer.SanitizeForLogging(groupName),
                Activity.Current?.TraceId.ToString() ?? Activity.Current?.Id ?? "n/a",
                SchemaResolverService.SanitizeUrlForLogging(baseUrl),
                managedHeapBytes,
                managedHeapDeltaBytes,
                processWorkingSetBytes,
                processWorkingSetDeltaBytes,
                stopwatch.Elapsed.TotalMilliseconds,
                results.Count);

            var tags = new TagList
            {
                { "stage", stage }
            };
            EndpointTestingManagedHeapBytesHistogram.Record(managedHeapBytes, tags);
            EndpointTestingManagedHeapDeltaBytesHistogram.Record(managedHeapDeltaBytes, tags);
            EndpointTestingWorkingSetBytesHistogram.Record(processWorkingSetBytes, tags);
            EndpointTestingWorkingSetDeltaBytesHistogram.Record(processWorkingSetDeltaBytes, tags);

            lastManagedHeapBytes = managedHeapBytes;
            lastWorkingSetBytes = processWorkingSetBytes;
        }

        try
        {
            _logger.TestingEndpointsWithDependencyOrdering();
            LogMemoryCheckpoint("start", "all");

            // We already have a JObject, so use it directly
            if (!openApiSpec.ContainsKey("paths"))
            {
                _logger.NoPathsFound();
                return results;
            }

            var paths = openApiSpec["paths"];
            if (paths is not JObject pathsObject)
            {
                return results;
            }

            // Group and order endpoints with intelligent dependency handling
            var endpointGroups = GroupEndpointsByDependencies(pathsObject, options);

            // Shared dictionary for ID extraction and usage across dependent endpoints
            // This dictionary is populated by collection endpoints and consumed by parameterized endpoints
            var extractedIds = new ConcurrentDictionary<string, List<string>>();

            _logger.FoundEndpointGroups(endpointGroups.Count);
            LogMemoryCheckpoint("grouping-complete", "all");

            // Test endpoints in dependency order - collection endpoints first, then parameterized
            foreach (var group in endpointGroups)
            {
                _logger.TestingEndpointGroup(TextSanitizer.SanitizeForLogging(group.RootPath), group.Endpoints.Count);
                LogMemoryCheckpoint("group-start", group.RootPath);

                var semaphore = new SemaphoreSlim(options.MaxConcurrentRequests, options.MaxConcurrentRequests);

                // PHASE 1: Test collection endpoints sequentially to extract IDs
                // These endpoints (e.g., GET /users) return collections with IDs that are stored in extractedIds
                foreach (var endpoint in group.CollectionEndpoints)
                {
                    var result = await TestSingleEndpointWithIdExtractionAsync(endpoint.Path, endpoint.Method, endpoint.Operation,
                        baseUrl, options, authentication, extractedIds, semaphore, openApiSpec, documentUri, endpoint.PathItem, cancellationToken);
                    results.Add(result);
                }

                LogMemoryCheckpoint("group-collections-complete", group.RootPath);

                // PHASE 2: Test parameterized endpoints concurrently using extracted IDs
                // These endpoints (e.g., GET /users/{id}) use IDs from the extractedIds dictionary
                var parameterizedTasks = new List<Task<EndpointTestResult>>();
                foreach (var endpoint in group.ParameterizedEndpoints)
                {
                    var task = TestSingleEndpointWithIdSubstitutionAsync(endpoint.Path, endpoint.Method, endpoint.Operation,
                        baseUrl, options, authentication, extractedIds, semaphore, openApiSpec, documentUri, endpoint.PathItem, cancellationToken);
                    parameterizedTasks.Add(task);
                }

                var parameterizedResults = await Task.WhenAll(parameterizedTasks);
                results.AddRange(parameterizedResults);

                semaphore.Dispose();

                _logger.CompletedEndpointGroup(TextSanitizer.SanitizeForLogging(group.RootPath), group.CollectionEndpoints.Count, group.ParameterizedEndpoints.Count);
                LogMemoryCheckpoint("group-complete", group.RootPath);
            }

            _logger.CompletedTestingEndpoints(results.Count);
            LogMemoryCheckpoint("complete", "all");
        }
        catch (Exception ex)
        {
            _logger.ErrorDuringEndpointTesting(ex);
        }

        return results;
    }

    private async Task<EndpointTestResult> TestSingleEndpointAsync(string path, string method, JObject operation, string baseUrl, OpenApiValidationOptions options, DataSourceAuthentication? authentication, SemaphoreSlim semaphore, JObject openApiDocument, string? documentUri, JObject pathItem, CancellationToken cancellationToken, string? testedId = null)
    {
        await semaphore.WaitAsync(cancellationToken);

        // Resolve all parameter references upfront (includes path-level and operation-level params)
        var resolvedParams = ResolveOperationParameters(operation, pathItem, openApiDocument);

        var result = new EndpointTestResult
        {
            Path = path,
            Method = method,
            Name = operation["name"]?.ToString(),
            OperationId = operation["operationId"]?.ToString(),
            Summary = operation["summary"]?.ToString(),
            IsOptional = operation.IsOptionalEndpoint(),
            Status = EndpointTestStatus.NotTested
        };

        try
        {
            bool isOptional = operation.IsOptionalEndpoint();
            bool skipOptional = !(_openApiValidationOptions?.TestOptionalEndpoints ?? true) && isOptional;
            if (skipOptional)
            {
                result.Status = EndpointTestStatus.Skipped;
                return result;
            }

            // Check if this endpoint has pagination support
            _logger.CheckingPaginationSupport(SchemaResolverService.SanitizeStringForLogging(method), SchemaResolverService.SanitizeStringForLogging(path));
            bool hasPagination = method == "GET" && HasPageParameter(resolvedParams);
            _logger.PaginationCheckResult(SchemaResolverService.SanitizeStringForLogging(method), SchemaResolverService.SanitizeStringForLogging(path), hasPagination);

            if (hasPagination)
            {
                // Test pagination: first page, middle page(s), last page
                await TestPaginatedEndpointAsync(result, path, method, operation, baseUrl, options, authentication, resolvedParams, openApiDocument, documentUri, pathItem, cancellationToken);
            }
            else
            {
                // Standard single-request testing
                var fullUrl = BuildFullUrl(baseUrl, path, resolvedParams, options);
                var testResult = await ExecuteHttpRequestAsync(fullUrl, method, operation, options, authentication, cancellationToken, testedId);

                result.TestResults.Add(testResult);
                result.IsTested = true;

                // Check for non-success status codes and handle based on endpoint requirements
                if (!testResult.IsSuccessStatusCode)
                {
                    var isOptionalEndpoint = pathItem.IsOptionalEndpoint();
                    var statusCode = testResult.ResponseStatusCode ?? 0;
                    var errorMessage = $"Endpoint returned {statusCode} status code";

                    if (isOptionalEndpoint)
                    {
                        // For optional endpoints, add validation warning instead of error
                        if (testResult.ValidationResult == null)
                        {
                            testResult.ValidationResult = new ValidationResult
                            {
                                IsValid = false,
                                Errors = new List<ValidationError>(),
                                SchemaVersion = string.Empty,
                                Duration = TimeSpan.Zero
                            };
                        }
                        testResult.ValidationResult.Errors.Add(new ValidationError
                        {
                            Path = path,
                            Message = $"Optional endpoint {method} {path} returned non-success status {statusCode}. This may indicate the endpoint is not implemented, which is acceptable for optional endpoints.",
                            ErrorCode = "OPTIONAL_ENDPOINT_NON_SUCCESS",
                            Severity = "Warning"
                        });
                        result.Status = EndpointTestStatus.PassedWithWarnings;
                    }
                    else
                    {
                        // For required endpoints, add validation error
                        if (testResult.ValidationResult == null)
                        {
                            testResult.ValidationResult = new ValidationResult
                            {
                                IsValid = false,
                                Errors = new List<ValidationError>(),
                                SchemaVersion = string.Empty,
                                Duration = TimeSpan.Zero
                            };
                        }
                        testResult.ValidationResult.Errors.Add(new ValidationError
                        {
                            Path = path,
                            Message = $"Required endpoint {method} {path} returned non-success status {statusCode}. Expected 2xx status code.",
                            ErrorCode = "REQUIRED_ENDPOINT_FAILED",
                            Severity = "Error"
                        });
                        result.Status = EndpointTestStatus.FailedValidation;
                    }
                }

                // Validate response if schema is defined
                if (testResult.IsSuccessStatusCode && testResult.ResponseBody != null)
                {
                    await ValidateResponseAsync(testResult, operation, openApiDocument, documentUri, options, cancellationToken);

                    var validationResult = testResult.ValidationResult;
                    if (validationResult == null || (validationResult.Errors.Count == 0 && !validationResult.IsValid))
                    {
                        // No schema was available for this response status, treat as pass.
                        result.Status = EndpointTestStatus.PassedValidation;
                    }
                    else if (validationResult.Errors.Any(e => string.Equals(e.Severity, "Warning", StringComparison.OrdinalIgnoreCase)) &&
                             !validationResult.Errors.Any(e => string.Equals(e.Severity, "Error", StringComparison.OrdinalIgnoreCase)))
                    {
                        result.Status = EndpointTestStatus.PassedWithWarnings;
                    }
                    else if (validationResult.IsValid)
                    {
                        result.Status = EndpointTestStatus.PassedValidation;
                    }
                    else
                    {
                        result.Status = EndpointTestStatus.FailedValidation;
                    }
                }


                // Optional endpoint warning logic (only apply if status wasn't already set by non-success handling)
                if (result.Status == EndpointTestStatus.NotTested || result.Status == EndpointTestStatus.PassedValidation || result.Status == EndpointTestStatus.FailedValidation)
                {
                    if (isOptional && (_openApiValidationOptions?.TestOptionalEndpoints ?? true) && (_openApiValidationOptions?.TreatOptionalEndpointsAsWarnings ?? true))
                    {
                        // If there are validation errors, report as warnings
                        if (testResult.ValidationResult != null && !testResult.ValidationResult.IsValid)
                        {
                            result.Status = EndpointTestStatus.PassedWithWarnings;
                        }
                        else if (result.Status != EndpointTestStatus.PassedWithWarnings)
                        {
                            result.Status = testResult.IsSuccessStatusCode
                                ? EndpointTestStatus.PassedValidation
                                : EndpointTestStatus.PassedWithWarnings;
                        }
                    }
                    else if (result.Status != EndpointTestStatus.PassedWithWarnings && result.Status != EndpointTestStatus.FailedValidation)
                    {
                        result.Status = testResult.IsSuccessStatusCode
                            ? EndpointTestStatus.PassedValidation
                            : EndpointTestStatus.FailedValidation;
                    }
                }

                NormalizeValidationResultErrors(testResult.ValidationResult);
            }
        }
        catch (Exception ex)
        {
            _logger.ErrorTestingEndpoint(ex, SchemaResolverService.SanitizeStringForLogging(method), SchemaResolverService.SanitizeStringForLogging(path));
            result.TestResults.Add(new HttpTestResult
            {
                RequestUrl = $"{baseUrl}{path}",
                RequestMethod = method,
                IsSuccessStatusCode = false,
                ErrorMessage = TextSanitizer.SanitizeExceptionMessage(ex.Message),
                ResponseTime = TimeSpan.Zero
            });
            result.Status = EndpointTestStatus.Error;
        }
        finally
        {
      _ = semaphore.Release();
        }

        return result;
    }

    /// <summary>
    /// Tests a paginated endpoint by requesting the first page, last page, and a page in the middle.
    /// Validates pagination metadata and warns if the feed contains no data.
    /// </summary>
    private async Task TestPaginatedEndpointAsync(
        EndpointTestResult result,
        string path,
        string method,
        JObject operation,
        string baseUrl,
        OpenApiValidationOptions options,
        DataSourceAuthentication? auth,
        JArray resolvedParams,
        JObject openApiDocument,
        string? documentUri,
        JObject pathItem,
        CancellationToken cancellationToken)
    {
        _logger.TestingPaginatedEndpoint(SchemaResolverService.SanitizeStringForLogging(method), SchemaResolverService.SanitizeStringForLogging(path));

        result.IsTested = true;

        // Test first page (page=1)
        _logger.TestingFirstPage(TextSanitizer.SanitizeForLogging(path));
        var firstPageUrl = BuildFullUrl(baseUrl, path, resolvedParams, options, pageNumber: 1);
        var firstPageResult = await ExecuteHttpRequestAsync(firstPageUrl, method, operation, options, auth, cancellationToken);
        result.TestResults.Add(firstPageResult);

        if (!firstPageResult.IsSuccessStatusCode)
        {
            var isOptionalEndpoint = pathItem.IsOptionalEndpoint();
            var statusCode = firstPageResult.ResponseStatusCode ?? 0;

            if (isOptionalEndpoint)
            {
                firstPageResult.ValidationResult!.Errors.Add(new ValidationError
                {
                    Path = path,
                    Message = $"Optional endpoint {method} {path} returned non-success status {statusCode}. This may indicate the endpoint is not implemented, which is acceptable for optional endpoints.",
                    ErrorCode = "OPTIONAL_ENDPOINT_NON_SUCCESS",
                    Severity = "Warning"
                });
                NormalizeValidationResultErrors(firstPageResult.ValidationResult);
                result.Status = EndpointTestStatus.PassedWithWarnings;
            }
            else
            {
                firstPageResult.ValidationResult!.Errors.Add(new ValidationError
                {
                    Path = path,
                    Message = $"Required endpoint {method} {path} returned non-success status {statusCode}. Expected 2xx status code.",
                    ErrorCode = "REQUIRED_ENDPOINT_FAILED",
                    Severity = "Error"
                });
                NormalizeValidationResultErrors(firstPageResult.ValidationResult);
                result.Status = EndpointTestStatus.FailedValidation;
            }
            return;
        }

        // Validate first page response schema
        if (firstPageResult.ResponseBody != null)
        {
            await ValidateResponseAsync(firstPageResult, operation, openApiDocument, documentUri, options, cancellationToken);
        }

        // Try to determine total pages and check for empty feed
        var paginationInfo = ExtractPaginationInfo(firstPageResult.ResponseBody);

        // Warn if feed returns no rows
        if (paginationInfo.ItemCount == 0)
        {
            firstPageResult.ValidationResult!.Errors.Add(new ValidationError
            {
                Path = path,
                Message = $"Paginated endpoint {method} {path} returned 0 items. Consider verifying if this is expected or if the feed should contain data.",
                ErrorCode = "EMPTY_FEED_WARNING",
                Severity = "Warning"
            });
            NormalizeValidationResultErrors(firstPageResult.ValidationResult);
            firstPageResult.ValidationResult.IsValid = false;
            result.Status = EndpointTestStatus.PassedWithWarnings;
            _logger.PaginatedEndpointReturnedEmpty(TextSanitizer.SanitizeForLogging(path));
            return; // No further pagination testing needed for empty feeds
        }

        if (paginationInfo.TotalPages.HasValue && paginationInfo.TotalPages.Value > 1)
        {
            var totalPages = paginationInfo.TotalPages.Value;
            _logger.TestingPaginationPages(TextSanitizer.SanitizeForLogging(path), totalPages);

            // Test middle page if there are more than 2 pages
            if (totalPages > 2)
            {
                var middlePage = totalPages / 2;
                _logger.TestingMiddlePage(middlePage, TextSanitizer.SanitizeForLogging(path));
                var middlePageUrl = BuildFullUrl(baseUrl, path, resolvedParams, options, pageNumber: middlePage);
                var middlePageResult = await ExecuteHttpRequestAsync(middlePageUrl, method, operation, options, auth, cancellationToken);
                result.TestResults.Add(middlePageResult);

                if (middlePageResult.IsSuccessStatusCode && middlePageResult.ResponseBody != null)
                {
                    await ValidateResponseAsync(middlePageResult, operation, openApiDocument, documentUri, options, cancellationToken);
                }
            }

            // Test last page
            _logger.TestingLastPage(totalPages, TextSanitizer.SanitizeForLogging(path));
            var lastPageUrl = BuildFullUrl(baseUrl, path, resolvedParams, options, pageNumber: totalPages);
            var lastPageResult = await ExecuteHttpRequestAsync(lastPageUrl, method, operation, options, auth, cancellationToken);
            result.TestResults.Add(lastPageResult);

            if (lastPageResult.IsSuccessStatusCode && lastPageResult.ResponseBody != null)
            {
                await ValidateResponseAsync(lastPageResult, operation, openApiDocument, documentUri, options, cancellationToken);
            }
        }
        else
        {
            _logger.SkippingAdditionalPageTests(TextSanitizer.SanitizeForLogging(path));
        }

        foreach (var testResult in result.TestResults)
        {
            NormalizeValidationResultErrors(testResult.ValidationResult);
        }

        result.Status = DeterminePaginatedEndpointStatus(result);

        if (!ShouldRetainResponseBodies(options))
        {
            foreach (var tr in result.TestResults)
            {
                tr.ResponseBody = null;
            }
        }
    }

    private static EndpointTestStatus DeterminePaginatedEndpointStatus(EndpointTestResult result)
    {
        if (!result.TestResults.Any())
        {
            return EndpointTestStatus.NotTested;
        }

        if (result.TestResults.Any(tr => !tr.IsSuccessStatusCode))
        {
            return result.IsOptional
                ? EndpointTestStatus.PassedWithWarnings
                : EndpointTestStatus.FailedValidation;
        }

        var validationErrors = result.TestResults
            .Where(tr => tr.ValidationResult != null)
            .SelectMany(tr => tr.ValidationResult!.Errors);

        if (validationErrors.Any(e => string.Equals(e.Severity, "Error", StringComparison.OrdinalIgnoreCase)))
        {
            return EndpointTestStatus.FailedValidation;
        }

        if (validationErrors.Any(e => string.Equals(e.Severity, "Warning", StringComparison.OrdinalIgnoreCase)))
        {
            return EndpointTestStatus.PassedWithWarnings;
        }

        if (result.TestResults.Any(tr => tr.ValidationResult != null && !tr.ValidationResult.IsValid))
        {
            return EndpointTestStatus.FailedValidation;
        }

        return EndpointTestStatus.PassedValidation;
    }

    /// <summary>
    /// Extracts pagination information from a response body to determine total pages and item count
    /// </summary>
    private (int? TotalPages, int ItemCount) ExtractPaginationInfo(string? responseBody)
    {
        if (string.IsNullOrEmpty(responseBody))
        {
            return (null, 0);
        }

        try
        {
            var json = JToken.Parse(responseBody);
            int? totalPages = null;
            int itemCount = 0;

            // Try to find total_pages field (common in paginated APIs)
            var totalPagesToken = json.SelectToken("$.total_pages") ??
                                  json.SelectToken("$.totalPages") ??
                                  json.SelectToken("$.pagination.total_pages") ??
                                  json.SelectToken("$.pagination.totalPages") ??
                                  json.SelectToken("$.meta.total_pages") ??
                                  json.SelectToken("$.meta.totalPages");

            if (totalPagesToken != null && int.TryParse(totalPagesToken.ToString(), out var pages))
            {
                totalPages = pages;
            }

            // Count items in common collection properties
            if (json is JArray array)
            {
                itemCount = array.Count;
            }
            else if (json is JObject obj)
            {
                // Check common collection property names
                foreach (var propName in new[] { "data", "items", "results", "content", "contents" })
                {
                    if (obj[propName] is JArray items)
                    {
                        itemCount = items.Count;
                        break;
                    }
                }

                // Also check for size/count fields
                if (itemCount == 0)
                {
                    var sizeToken = obj["size"] ?? obj["count"] ?? obj["length"];
                    if (sizeToken != null && int.TryParse(sizeToken.ToString(), out var size))
                    {
                        itemCount = size;
                    }
                }
            }

            return (totalPages, itemCount);
        }
        catch (Exception ex)
        {
            _logger.FailedToExtractPaginationInfo(ex);
            return (null, 0);
        }
    }

    private string BuildFullUrl(string baseUrl, string path, JArray resolvedParams, OpenApiValidationOptions options, int? pageNumber = null)
    {
        var url = $"{baseUrl.TrimEnd('/')}{path}";

        // Add page parameter if specified
        if (pageNumber.HasValue && HasPageParameter(resolvedParams))
        {
            var separator = url.Contains('?') ? "&" : "?";
            url += $"{separator}page={pageNumber.Value}";
        }

        return url;
    }

    /// <summary>
    /// Checks if the resolved parameters array contains a 'page' query parameter.
    /// Parameters should already be resolved (references expanded, path and operation params merged).
    /// </summary>
    private bool HasPageParameter(JArray resolvedParams)
    {
        _logger.CheckingPageParameter(resolvedParams.Count);
        foreach (var param in resolvedParams)
        {
            if (param is JObject paramObj)
            {
                var name = paramObj["name"]?.ToString();
                var inLocation = paramObj["in"]?.ToString();
                _logger.CheckingParam(SchemaResolverService.SanitizeStringForLogging(name ?? string.Empty), SchemaResolverService.SanitizeStringForLogging(inLocation ?? string.Empty));

                if (name?.Equals("page", StringComparison.OrdinalIgnoreCase) == true &&
                    inLocation?.Equals("query", StringComparison.OrdinalIgnoreCase) == true)
                {
                    _logger.FoundPageQueryParameter();
                    return true;
                }
            }
        }
        _logger.NoPageParameterFound();
        return false;
    }

    /// <summary>
    /// Merges path-level and operation-level parameters.
    /// Returns a JArray of parameter objects (references already resolved upstream).
    /// </summary>
    private JArray ResolveOperationParameters(JObject operation, JObject pathItem, JObject openApiDocument)
    {
        var resolvedParams = new JArray();

        // Add path-level parameters first (these are inherited by all operations)
        if (pathItem["parameters"] is JArray pathParams)
        {
            _logger.FoundPathLevelParameters(pathParams.Count);
            foreach (var param in pathParams)
            {
                resolvedParams.Add(param);
                if (param is JObject paramObj)
                {
                    var paramName = paramObj["name"]?.ToString();
                    _logger.PathLevelParam(SchemaResolverService.SanitizeStringForLogging(paramName ?? string.Empty));
                }
            }
        }

        // Add operation-level parameters (these can override path-level params)
        if (operation["parameters"] is JArray operationParams)
        {
            _logger.FoundOperationLevelParameters(operationParams.Count);
            foreach (var param in operationParams)
            {
                resolvedParams.Add(param);
                if (param is JObject paramObj)
                {
                    var paramName = paramObj["name"]?.ToString();
                    _logger.OperationLevelParam(SchemaResolverService.SanitizeStringForLogging(paramName ?? string.Empty));
                }
            }
        }

        _logger.TotalResolvedParameters(resolvedParams.Count);
        return resolvedParams;
    }

    private async Task<HttpTestResult> ExecuteHttpRequestAsync(string url, string method, JObject operation, OpenApiValidationOptions options, DataSourceAuthentication? authentication, CancellationToken cancellationToken, string? testedId = null)
    {
        var testResult = new HttpTestResult
        {
            RequestUrl = url,
            RequestMethod = method,
            TestedId = testedId,
            ValidationResult = new ValidationResult()
        };

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), url);

            // Add User-Agent header to match browser behavior
            // Many servers reject requests without a User-Agent header
            if (!request.Headers.Contains("User-Agent"))
            {
                request.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/91.0.4472.124 Safari/537.36");
            }

            // Apply request-supplied authentication only for HTTPS endpoints.
            // Never send user-provided credentials over plain HTTP.
            if (authentication != null &&
                Uri.TryCreate(url, UriKind.Absolute, out var requestUri) &&
                string.Equals(requestUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                ApplyAuthenticationHeaders(request, authentication);
            }
            else if (authentication != null)
            {
                _logger.SkippedAuthForNonHttpsEndpoint(TextSanitizer.SanitizeForLogging(url));
            }

            // Set timeout
            var timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeout);

            // Use the injected HttpClient so test HttpMessageHandler mocks are respected.
            // Do NOT dispose - IHttpClientFactory manages the lifetime of pooled handlers.
            TimeSpan dnsLookup = TimeSpan.Zero, tcpConnection = TimeSpan.Zero, tlsHandshake = TimeSpan.Zero;
            var sendStart = Stopwatch.StartNew();
            var httpClient = _httpClientFactory.CreateClient(nameof(EndpointTestingService));
            var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            var timeToHeaders = sendStart.Elapsed;

            // Read response content without an intermediate MemoryStream to reduce peak allocations.
            string responseBody = string.Empty;
            var contentTransferStopwatch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                responseBody = await response.Content.ReadAsStringAsync(cts.Token);
                contentTransferStopwatch.Stop();
            }
            catch (OperationCanceledException)
            {
                contentTransferStopwatch.Stop();
                responseBody = string.Empty;
            }

            // Stop the overall timers
            sendStart.Stop();

            // Populate basic result fields
            testResult.ResponseTime = timeToHeaders + contentTransferStopwatch.Elapsed;
            testResult.ResponseStatusCode = (int)response.StatusCode;
            testResult.IsSuccessStatusCode = response.IsSuccessStatusCode;
            testResult.ResponseBody = responseBody;

            // Populate performance metrics (include best-effort DNS/TCP/TLS measurements if available)
            testResult.PerformanceMetrics = new EndpointPerformanceMetrics
            {
                DnsLookup = dnsLookup,
                TcpConnection = tcpConnection,
                TlsHandshake = tlsHandshake,
                ServerProcessing = timeToHeaders,
                ContentTransfer = contentTransferStopwatch.Elapsed
            };
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            testResult.ResponseTime = stopwatch.Elapsed;
            testResult.IsSuccessStatusCode = false;
            testResult.ErrorMessage = TextSanitizer.SanitizeExceptionMessage(ex.Message);
        }

        return testResult;
    }

    private async Task ValidateResponseAsync(HttpTestResult testResult, JObject operation, JObject openApiDocument, string? documentUri, OpenApiValidationOptions options, CancellationToken cancellationToken)
    {
        try
        {
            if (operation.ContainsKey("responses"))
            {
                var responses = operation["responses"];
                if (responses is JObject responsesObject)
                {
                    var statusCode = testResult.ResponseStatusCode?.ToString() ?? "default";
                    var responseSchema = responsesObject[statusCode] ?? responsesObject["default"];

                    if (responseSchema is JObject responseSchemaObject && responseSchemaObject.ContainsKey("content"))
                    {
                        var content = responseSchemaObject["content"];
                        if (content is JObject contentObject)
                        {
                            // Find JSON content type
                            var jsonContent = contentObject.Properties()
                                .FirstOrDefault(p => p.Name.Contains("application/json"));

                            if (jsonContent?.Value is JObject jsonContentObject && jsonContentObject.ContainsKey("schema"))
                            {
                                var schema = jsonContentObject["schema"];
                                if (schema != null)
                                {
                                    var schemaForValidation = GetValidationSchemaForResponse(schema, openApiDocument);
                                    // Build schema in full OpenAPI context so internal refs like
                                    // #/components/schemas/* can be pre-resolved before JSchema creation.
                                    var validationRequest = new ValidationRequest
                                    {
                                        JsonData = JsonNode.Parse(testResult.ResponseBody ?? "{}"),
                                        Schema = schemaForValidation,
                                        Options = new ValidationOptions
                                        {
                                            ReportAdditionalFields = (options?.ReportAdditionalFields ?? false)
                                                || ((_openApiValidationOptions?.OwnSchemaValidation
                                                     ?? OwnSchemaValidationMode.StrictOwnSchemaValidation)
                                                    == OwnSchemaValidationMode.StrictOwnSchemaValidation)
                                        }
                                    };
                                    var validationResult = await _jsonValidatorService.ValidateAsync(validationRequest, cancellationToken);
                                    _hsdsComplianceService.ApplyAdditionalFieldPolicy(validationResult, options?.ReportAdditionalFields ?? false);
                                    testResult.ValidationResult = validationResult;
                                    NormalizeValidationResultErrors(testResult.ValidationResult);
                                }
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.CouldNotValidateResponse(ex, SchemaResolverService.SanitizeUrlForLogging(testResult.RequestUrl ?? string.Empty));
        }
    }

    private JToken GetValidationSchemaForResponse(JToken schema, JObject openApiDocument)
    {
        if (string.IsNullOrWhiteSpace(schema.Path))
        {
            return BuildValidationSchemaWithComponentsContext(schema, openApiDocument);
        }

        return _validationSchemaCache.GetOrAdd(
            schema.Path,
            _ => BuildValidationSchemaWithComponentsContext(schema, openApiDocument));
    }

    private static JToken BuildValidationSchemaWithComponentsContext(JToken schema, JObject openApiDocument)
    {
        if (!RequiresComponentsContext(schema)
            || openApiDocument["components"] is not JObject components)
        {
            return schema.DeepClone();
        }

        // Keep the response schema at document root and attach components so refs like
        // #/components/schemas/* remain resolvable without introducing synthetic wrapper refs.
        if (schema is JObject schemaObject)
        {
            var schemaWithComponents = (JObject)schemaObject.DeepClone();
            if (!schemaWithComponents.ContainsKey("components"))
            {
                schemaWithComponents["components"] = components.DeepClone();
            }

            return schemaWithComponents;
        }

        return schema.DeepClone();
    }

    private static bool RequiresComponentsContext(JToken schema)
    {
        if (schema is JObject schemaObject
            && schemaObject.TryGetValue("$ref", out var refToken)
            && refToken.Type == JTokenType.String
            && refToken.ToString().StartsWith("#/components/", StringComparison.Ordinal))
        {
            return true;
        }

        foreach (var child in schema.Children())
        {
            if (RequiresComponentsContext(child))
            {
                return true;
            }
        }

        return false;
    }

    private static void NormalizeValidationResultErrors(ValidationResult? validationResult)
    {
        if (validationResult?.Errors == null || validationResult.Errors.Count == 0)
        {
            return;
        }

        validationResult.Errors = ValidationErrorNormalizer.NormalizeAndDeduplicateByPath(validationResult.Errors);
    }

    private bool ShouldRetainResponseBodies(OpenApiValidationOptions options)
    {
        return options.IncludeResponseBody
            || (_openApiValidationOptions?.HsdsValidationMode == HsdsValidationMode.FullHsdsRuntime);
    }

    private List<EndpointGroup> GroupEndpointsByDependencies(JObject pathsObject, OpenApiValidationOptions options)
    {
        var endpoints = new List<EndpointInfo>();
        var validHttpMethods = new HashSet<string> { "GET", "POST", "PUT", "DELETE", "PATCH", "HEAD", "OPTIONS", "TRACE" };

        // Extract all endpoints
        foreach (var pathProperty in pathsObject.Properties())
        {
            var path = pathProperty.Name;
            var pathItem = pathProperty.Value;

            if (pathItem is JObject pathItemObject)
            {
                foreach (var methodProperty in pathItemObject.Properties())
                {
                    var method = methodProperty.Name.ToUpperInvariant();

                    // Skip non-HTTP method properties like "parameters", "summary", "$ref", "servers", etc.
                    if (!validHttpMethods.Contains(method))
                    {
                        continue;
                    }

                    var operation = methodProperty.Value;
                    if (operation is JObject operationObject)
                    {
                        endpoints.Add(new EndpointInfo
                        {
                            Path = path,
                            Method = method,
                            Operation = operationObject,
                            PathItem = pathItemObject  // Add path item for optional endpoint checking
                        });
                    }
                }
            }
        }

        // Group by root path and separate collection from parameterized
        var groups = endpoints
            .GroupBy(e => e.RootPath)
            .Select(g => new EndpointGroup
            {
                RootPath = g.Key,
                CollectionEndpoints = g.Where(e => !e.IsParameterized && e.Method == "GET").ToList(),
                ParameterizedEndpoints = g.Where(e => e.IsParameterized).ToList()
            })
            .Where(g => g.Endpoints.Any())
            .ToList();

        return groups;
    }

    /// <summary>
    /// Tests an endpoint and extracts IDs from the response for use by dependent endpoints.
    /// The extractedIds dictionary is updated with any IDs found in the response.
    /// </summary>
    /// <param name="path">The endpoint path to test</param>
    /// <param name="method">The HTTP method to use</param>
    /// <param name="operation">The OpenAPI operation definition</param>
    /// <param name="baseUrl">The base URL for the API</param>
    /// <param name="authentication">Authentication configuration</param>
    /// <param name="options">Validation options</param>
    /// <param name="extractedIds">Dictionary to store extracted IDs (passed by reference, modifications persist)</param>
    /// <param name="semaphore">Semaphore for concurrency control</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The endpoint test result with extracted IDs stored in the shared dictionary</returns>
    private async Task<EndpointTestResult> TestSingleEndpointWithIdExtractionAsync(
        string path, string method, JObject operation, string baseUrl,
        OpenApiValidationOptions options, DataSourceAuthentication? authentication,
        ConcurrentDictionary<string, List<string>> extractedIds, SemaphoreSlim semaphore,
        JObject openApiDocument, string? documentUri, JObject pathItem, CancellationToken cancellationToken)
    {
        var result = await TestSingleEndpointAsync(path, method, operation, baseUrl, options, authentication, semaphore, openApiDocument, documentUri, pathItem, cancellationToken);

        // Extract IDs from successful GET responses for dependency testing
        if (method == "GET" && result.TestResults.Any(r => r.IsSuccessStatusCode && !string.IsNullOrEmpty(r.ResponseBody)))
        {
            var rootPath = EndpointInfo.GetRootPath(path);
            var successfulResponse = result.TestResults.First(r => r.IsSuccessStatusCode);

            _logger.ProcessingHttpResponse(
                SchemaResolverService.SanitizeUrlForLogging(successfulResponse.RequestUrl ?? string.Empty),
                successfulResponse.ResponseStatusCode ?? 0,
                successfulResponse.ResponseBody?.Length ?? 0);

            // Log first 500 characters of response for debugging
            var responsePreview = successfulResponse.ResponseBody!.Length > 500
                ? successfulResponse.ResponseBody[..500] + "..."
                : successfulResponse.ResponseBody;

            _logger.ResponseContentLength(successfulResponse.ResponseBody?.Length ?? 0);

            var ids = ExtractIdsFromResponse(successfulResponse.ResponseBody!, rootPath, operation, openApiDocument);

            if (ids.Any())
            {
                // Store extracted IDs in the shared dictionary for use by dependent endpoints
                // Note: ConcurrentDictionary is a reference type, so this modification persists to the caller
                extractedIds[rootPath] = ids;
                _logger.SuccessfullyExtractedIds(ids.Count, TextSanitizer.SanitizeForLogging(path), TextSanitizer.SanitizeForLogging(rootPath));

                // Verify the IDs were stored correctly
                if (extractedIds.TryGetValue(rootPath, out var storedIds))
                {
                    _logger.VerifiedIdsStored(storedIds.Count, TextSanitizer.SanitizeForLogging(rootPath));
                }
                else
                {
                    _logger.IdsVerificationFailed(TextSanitizer.SanitizeForLogging(rootPath));
                }
            }
            else
            {
                _logger.NoIdsExtracted(TextSanitizer.SanitizeForLogging(path), TextSanitizer.SanitizeForLogging(rootPath));
            }
        }

        if (!ShouldRetainResponseBodies(options))
        {
            foreach (var tr in result.TestResults)
            {
                tr.ResponseBody = null;
            }
        }

        return result;
    }

    /// <summary>
    /// Tests an endpoint with parameter substitution using extracted IDs from the shared dictionary.
    /// This method retrieves IDs extracted by TestSingleEndpointWithIdExtractionAsync and uses them
    /// to test parameterized endpoints with realistic data.
    /// </summary>
    /// <param name="path">The parameterized endpoint path to test</param>
    /// <param name="method">The HTTP method to use</param>
    /// <param name="operation">The OpenAPI operation definition</param>
    /// <param name="baseUrl">The base URL for the API</param>
    /// <param name="authentication">Authentication configuration</param>
    /// <param name="options">Validation options</param>
    /// <param name="extractedIds">Dictionary containing extracted IDs from collection endpoints</param>
    /// <param name="semaphore">Semaphore for concurrency control</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The endpoint test result using extracted IDs for parameters</returns>
    private async Task<EndpointTestResult> TestSingleEndpointWithIdSubstitutionAsync(
        string path, string method, JObject operation, string baseUrl,
        OpenApiValidationOptions options, DataSourceAuthentication? authentication,
        ConcurrentDictionary<string, List<string>> extractedIds, SemaphoreSlim semaphore,
        JObject openApiDocument, string? documentUri, JObject pathItem, CancellationToken cancellationToken)
    {
        var rootPath = EndpointInfo.GetRootPath(path);

        _logger.LookingForExtractedIds(TextSanitizer.SanitizeForLogging(rootPath), extractedIds.Keys.Count);

        // Try to retrieve extracted IDs from the shared dictionary populated by collection endpoint tests
        if (extractedIds.TryGetValue(rootPath, out var availableIds) && availableIds.Any())
        {
            _logger.FoundExtractedIds(availableIds.Count, TextSanitizer.SanitizeForLogging(rootPath));

            // Test up to 10 random IDs from the available IDs
            var maxIdsToTest = Math.Min(10, availableIds.Count);
            var random = new Random();
            var idsToTest = availableIds.Count <= 10
                ? availableIds.ToList()
                : availableIds.OrderBy(_ => random.Next()).Take(10).ToList();

            _logger.TestingRandomIds(idsToTest.Count, TextSanitizer.SanitizeForLogging(path));

            // Create a composite result that combines all test results
            var compositeResult = new EndpointTestResult
            {
                Path = path,
                Method = method,
                Name = operation["name"]?.ToString(),
                OperationId = operation["operationId"]?.ToString(),
                Summary = operation["summary"]?.ToString(),
                IsOptional = operation.IsOptionalEndpoint(),
                Status = EndpointTestStatus.NotTested,
                IsTested = false
            };

            // Test each ID
            var allTestsSuccessful = true;
            var hasSkippedResult = false;
            foreach (var id in idsToTest)
            {
                var substitutedPath = SubstitutePathParametersWithSpecificId(path, id);
                _logger.TestingEndpointWithExtractedId();

                var singleResult = await TestSingleEndpointAsync(substitutedPath, method, operation, baseUrl, options, authentication, semaphore, openApiDocument, documentUri, pathItem, cancellationToken, testedId: id);

                // Aggregate the results
                compositeResult.TestResults.AddRange(singleResult.TestResults);
                //compositeResult.ValidationErrors.AddRange(singleResult.ValidationErrors);
                //compositeResult.SchemaValidationDetails.AddRange(singleResult.SchemaValidationDetails);

                if (singleResult.Status == EndpointTestStatus.Skipped)
                {
                    hasSkippedResult = true;
                }

                if (singleResult.Status == EndpointTestStatus.FailedValidation || singleResult.Status == EndpointTestStatus.Error)
                {
                    allTestsSuccessful = false;
                }
            }

            compositeResult.IsTested = compositeResult.TestResults.Any();

            // Set the composite status based on all test results
            if (compositeResult.TestResults.Any())
            {
                if (allTestsSuccessful)
                {
                    compositeResult.Status = EndpointTestStatus.PassedValidation;
                }
                else if (compositeResult.TestResults.Any(tr => tr.ValidationResult != null && tr.ValidationResult.Errors.Any(e => e.Severity == "Warning")) && !compositeResult.TestResults.Any(tr => tr.ValidationResult != null && tr.ValidationResult.Errors.Any(e => e.Severity == "Error")))
                {
                    compositeResult.Status = EndpointTestStatus.PassedWithWarnings;
                }
                else
                {
                    compositeResult.Status = EndpointTestStatus.FailedValidation;
                }
            }
            else if (hasSkippedResult)
            {
                compositeResult.Status = EndpointTestStatus.Skipped;
            }

            if (!ShouldRetainResponseBodies(options))
            {
                foreach (var tr in compositeResult.TestResults)
                {
                    tr.ResponseBody = null;
                }
            }

            return compositeResult;
        }
        else
        {
            _logger.NoExtractedIdsAvailable(TextSanitizer.SanitizeForLogging(rootPath), extractedIds.Count, TextSanitizer.SanitizeForLogging(path));

            // Log available keys for debugging
            if (extractedIds.Any())
            {
                _logger.AvailableIdKeysCount(extractedIds.Keys.Count);
            }

            // Return a NotTested result instead of falling back to default values
            var notTestedResult = new EndpointTestResult
            {
                Path = path,
                Method = method,
                Name = operation["name"]?.ToString(),
                OperationId = operation["operationId"]?.ToString(),
                Summary = operation["summary"]?.ToString(),
                IsOptional = operation.IsOptionalEndpoint(),
                Status = EndpointTestStatus.NotTested,
                IsTested = false,
                TestResults = new List<HttpTestResult>(){
                    new() {
                        IsSuccessStatusCode = false,
                        RequestUrl = $"{baseUrl}{path}",
                        ErrorMessage = "No extracted IDs available for parameter substitution. Endpoint was not tested.",
                        ValidationResult= new ValidationResult
                        {
                            IsValid = false,
                            Errors = new List<ValidationError>
                            {
                                new ValidationError
                                {
                                    Path = path,
                                    Message = "No extracted IDs available for parameter substitution. Endpoint was not tested.",
                                    ErrorCode = "NO_IDS_AVAILABLE",
                                    Severity = "Warning"
                                }
                            }
                        }
                    }
                }
            };

            NormalizeValidationResultErrors(notTestedResult.TestResults.FirstOrDefault()?.ValidationResult);
            return notTestedResult;
        }
    }

    /// <summary>
    /// Extracts IDs from a JSON response using OpenAPI schema information to identify ID field locations
    /// </summary>
    private List<string> ExtractIdsFromResponse(string responseBody, string rootPath, JObject operation, JObject openApiDocument)
    {
        var ids = new List<string>();

        _logger.StartingIdExtraction(TextSanitizer.SanitizeForLogging(rootPath));

        // First, try to extract ID field names from the OpenAPI schema
        var schemaIdFields = ExtractIdFieldsFromSchema(operation, openApiDocument);
        if (schemaIdFields.Any())
        {
            _logger.FoundIdFieldsFromSchema(schemaIdFields.Count);
        }
        else
        {
            _logger.FallingBackToCommonFieldNames();
        }

        try
        {
            var json = JToken.Parse(responseBody);
            _logger.ParsedJsonType(json.Type.ToString());

            // Handle array responses (most common for collections)
            if (json is JArray array)
            {
                _logger.FoundJsonArray(array.Count);

                foreach (var item in array)
                {
                    var id = ExtractIdFromObject(item, schemaIdFields);
                    if (!string.IsNullOrEmpty(id))
                    {
                        _logger.FoundIdInArrayItem();
                        ids.Add(id);
                    }
                }
            }
            // Handle object responses with data/items property
            else if (json is JObject obj)
            {
                // First try to identify collection properties from the schema
                var collectionProps = ExtractCollectionPropertiesFromSchema(operation, openApiDocument);
                if (collectionProps.Any())
                {
                    var sanitizedProps = string.Join(", ", collectionProps.Select(p => TextSanitizer.SanitizeForLogging(p)));
                    _logger.FoundCollectionProperties(sanitizedProps);

                    foreach (var propName in collectionProps)
                    {
                        if (obj[propName] is JArray items)
                        {
                            _logger.ProcessingCollectionProperty(TextSanitizer.SanitizeForLogging(propName), items.Count);
                            foreach (var item in items)
                            {
                                var id = ExtractIdFromObject(item, schemaIdFields);
                                if (!string.IsNullOrEmpty(id))
                                    ids.Add(id);
                            }
                            break;
                        }
                    }
                }

                // If no schema-based collection found, try common collection property names
                if (!ids.Any())
                {
                    foreach (var propName in new[] { "data", "items", "results", "content", "contents" })
                    {
                        if (obj[propName] is JArray items)
                        {
                            _logger.ProcessingFallbackCollectionProperty(TextSanitizer.SanitizeForLogging(propName), items.Count);
                            foreach (var item in items)
                            {
                                var id = ExtractIdFromObject(item, schemaIdFields);
                                if (!string.IsNullOrEmpty(id))
                                    ids.Add(id);
                            }
                            break;
                        }
                    }
                }

                // If no collection found, try to extract ID from the object itself
                if (!ids.Any())
                {
                    var id = ExtractIdFromObject(json, schemaIdFields);
                    if (!string.IsNullOrEmpty(id))
                        ids.Add(id);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.FailedToExtractIds(ex, TextSanitizer.SanitizeForLogging(rootPath));
        }

        return ids.Distinct().ToList();
    }

    /// <summary>
    /// Extracts an ID from a JSON object using OpenAPI schema-identified ID fields, with fallback to common names
    /// </summary>
    private static string? ExtractIdFromObject(JToken item, List<string> schemaIdFields)
    {
        if (item is not JObject obj)
            return null;

        // First try fields identified from the OpenAPI schema
        foreach (var idField in schemaIdFields)
        {
            var idValue = obj[idField]?.ToString();
            if (!string.IsNullOrWhiteSpace(idValue))
                return idValue;
        }

        // Fallback to common ID field names if schema-based extraction failed
        foreach (var idField in new[] { "id", "_id", "uid", "uuid", "identifier", "key" })
        {
            var idValue = obj[idField]?.ToString();
            if (!string.IsNullOrWhiteSpace(idValue))
                return idValue;
        }

        return null;
    }

    /// <summary>
    /// Extracts ID field names from the OpenAPI response schema
    /// </summary>
    private List<string> ExtractIdFieldsFromSchema(JObject operation, JObject openApiDocument)
    {
        var idFields = new List<string>();

        try
        {
            // Get the 200 response schema
            var responseSchema = operation["responses"]?["200"]?["content"]?["application/json"]?["schema"];
            if (responseSchema != null)
            {
                ExtractIdFieldsFromSchemaRecursive(responseSchema, idFields);
            }
        }
        catch (Exception ex)
        {
            _logger.FailedToExtractIdFieldsFromSchema(ex);
        }

        return idFields.Distinct().ToList();
    }

    /// <summary>
    /// Extracts collection property names from the OpenAPI response schema
    /// </summary>
    private List<string> ExtractCollectionPropertiesFromSchema(JObject operation, JObject openApiDocument)
    {
        var collectionProps = new List<string>();

        try
        {
            // Get the 200 response schema
            var responseSchema = operation["responses"]?["200"]?["content"]?["application/json"]?["schema"];
            if (responseSchema != null)
            {
                ExtractCollectionPropertiesFromSchemaRecursive(responseSchema, collectionProps);
            }
        }
        catch (Exception ex)
        {
            _logger.FailedToExtractCollectionProperties(ex);
        }

        return collectionProps.Distinct().ToList();
    }

    /// <summary>
    /// Recursively extracts ID field names from a schema structure
    /// </summary>
    private static void ExtractIdFieldsFromSchemaRecursive(JToken schema, List<string> idFields)
    {
        if (schema is JObject schemaObj)
        {
            // Check if this schema has properties
            if (schemaObj["properties"] is JObject properties)
            {
                foreach (var prop in properties.Properties())
                {
                    var propName = prop.Name;
                    var propSchema = prop.Value;

                    // Check if this looks like an ID field
                    if (IsIdField(propName, propSchema))
                    {
                        idFields.Add(propName);
                    }

                    // Recursively check nested properties
                    ExtractIdFieldsFromSchemaRecursive(propSchema, idFields);
                }
            }

            // Check array items
            if (schemaObj["items"] is JToken itemsSchema)
            {
                ExtractIdFieldsFromSchemaRecursive(itemsSchema, idFields);
            }

            // Check allOf, anyOf, oneOf
            foreach (var combiner in new[] { "allOf", "anyOf", "oneOf" })
            {
                if (schemaObj[combiner] is JArray combinerArray)
                {
                    foreach (var item in combinerArray)
                    {
                        ExtractIdFieldsFromSchemaRecursive(item, idFields);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Recursively extracts collection property names from a schema structure
    /// </summary>
    private static void ExtractCollectionPropertiesFromSchemaRecursive(JToken schema, List<string> collectionProps)
    {
        if (schema is JObject schemaObj)
        {
            // Check if this schema has properties
            if (schemaObj["properties"] is JObject properties)
            {
                foreach (var prop in properties.Properties())
                {
                    var propName = prop.Name;
                    var propSchema = prop.Value;

                    // Check if this property is an array (collection)
                    if (propSchema is JObject propObj && propObj["type"]?.ToString() == "array")
                    {
                        collectionProps.Add(propName);
                    }

                    // Recursively check nested properties
                    ExtractCollectionPropertiesFromSchemaRecursive(propSchema, collectionProps);
                }
            }

            // Check allOf, anyOf, oneOf
            foreach (var combiner in new[] { "allOf", "anyOf", "oneOf" })
            {
                if (schemaObj[combiner] is JArray combinerArray)
                {
                    foreach (var item in combinerArray)
                    {
                        ExtractCollectionPropertiesFromSchemaRecursive(item, collectionProps);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Determines if a property name and schema indicate an ID field
    /// </summary>
    private static bool IsIdField(string propName, JToken? propSchema)
    {
        // Check property name patterns
        var nameLower = propName.ToLowerInvariant();
        if (nameLower == "id" || nameLower == "_id" || nameLower == "uid" ||
            nameLower == "uuid" || nameLower == "identifier" || nameLower == "key" ||
            nameLower.EndsWith("id") || nameLower.EndsWith("_id"))
        {
            return true;
        }

        // Check schema properties for ID indicators
        if (propSchema is JObject schemaObj)
        {
            var description = schemaObj["description"]?.ToString().ToLowerInvariant();
            if (!string.IsNullOrEmpty(description) &&
                (description.Contains("identifier") || description.Contains("unique id") || description.Contains(" id ")))
            {
                return true;
            }

            var format = schemaObj["format"]?.ToString().ToLowerInvariant();
            if (format == "uuid" || format == "guid")
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Helper method to extract a schema from a given path in the OpenAPI document
    /// This is used by parameter resolution to resolve parameter references
    /// </summary>
    private static JToken? GetSchemaFromPath(JObject document, string path)
    {
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        JToken? current = document;

        foreach (var part in parts)
        {
            if (current is JObject obj && obj.ContainsKey(part))
            {
                current = obj[part];
            }
            else if (current is JArray array && int.TryParse(part, out var index) && index >= 0 && index < array.Count)
            {
                current = array[index];
            }
            else
            {
                return null; // Path not found
            }
        }

        return current;
    }

    /// <summary>
    /// Substitutes path parameters with a specific ID value
    /// </summary>
    private string SubstitutePathParametersWithSpecificId(string path, string id)
    {
        var substitutedPath = path;

        // Find all path parameters and replace with the specific ID
        var matches = Regex.Matches(path, @"\{([^}]+)\}");

        foreach (Match match in matches)
        {
            var paramPlaceholder = match.Value;
            substitutedPath = substitutedPath.Replace(paramPlaceholder, id);
        }

        return substitutedPath;
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

    /// <summary>
    /// Applies authentication to an HTTP request based on the provided authentication configuration
    /// Supports API key, bearer token, basic authentication, and custom headers
    /// </summary>
    /// <param name="request">The HTTP request message to apply authentication to</param>
    /// <param name="authentication">The authentication configuration containing credentials and auth type</param>
    private void ApplyAuthenticationHeaders(HttpRequestMessage request, IAuthenticationConfig authentication)
    {
        // Apply API Key authentication
        if (!string.IsNullOrEmpty(authentication.ApiKey))
        {
            var headerName = string.IsNullOrEmpty(authentication.ApiKeyHeader) ? "X-API-Key" : authentication.ApiKeyHeader;
            if (IsValidHttpHeaderName(headerName) && IsSafeHeaderValue(authentication.ApiKey))
            {
                request.Headers.Add(headerName, authentication.ApiKey);
                _logger.AppliedApiKeyAuthenticationWithHeader(TextSanitizer.SanitizeForLogging(headerName));
            }
            else
            {
                _logger.SkippedApiKeyAuthentication();
            }
        }

        // Apply Bearer Token authentication
        if (!string.IsNullOrEmpty(authentication.BearerToken))
        {
            if (IsSafeHeaderValue(authentication.BearerToken))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", authentication.BearerToken);
                EndpointTestingLog.AppliedBearerTokenAuthentication(_logger);
            }
            else
            {
                _logger.SkippedBearerTokenAuthentication();
            }
        }

        // Apply Basic Authentication
        if (authentication.BasicAuth != null &&
            !string.IsNullOrEmpty(authentication.BasicAuth.Username))
        {
            var credentials = Convert.ToBase64String(
                Encoding.ASCII.GetBytes($"{authentication.BasicAuth.Username}:{authentication.BasicAuth.Password ?? string.Empty}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
            _logger.AppliedBasicAuthentication(TextSanitizer.SanitizeForLogging(authentication.BasicAuth.Username));
        }

        // Apply Custom Headers
        if (authentication.CustomHeaders != null && authentication.CustomHeaders.Any())
        {
            foreach (var header in authentication.CustomHeaders)
            {
                if (!string.IsNullOrEmpty(header.Key) &&
                    !string.IsNullOrEmpty(header.Value) &&
                    IsValidHttpHeaderName(header.Key) &&
                    IsSafeHeaderValue(header.Value))
                {
                    request.Headers.Add(header.Key, header.Value);
                    EndpointTestingLog.AppliedCustomHeader(_logger, TextSanitizer.SanitizeForLogging(header.Key));
                }
                else
                {
                    _logger.SkippedInvalidCustomHeader();
                }
            }
        }
    }
}
