namespace OpenReferralApi.Core.Services;

/// <summary>
/// Maps OpenAPI validation results to the standard ValidationResponse format
/// </summary>
public interface IOpenReferralUKValidationResponseMapper
{
    OpenReferralUKValidationResponse MapToOpenReferralUKValidationResponse(OpenApiValidationResult openApiResult);
}

public class OpenReferralUKValidationResponseMapper : IOpenReferralUKValidationResponseMapper
{
    public OpenReferralUKValidationResponse MapToOpenReferralUKValidationResponse(OpenApiValidationResult openApiResult)
    {
        var testSuites = new List<object>();

        // Map endpoint tests to test groups - separate required and optional endpoints
        if (openApiResult.EndpointTests != null && openApiResult.EndpointTests.Any())
        {
            var requiredEndpoints = openApiResult.EndpointTests.Where(e => !e.IsOptional).ToList();
            var optionalEndpoints = openApiResult.EndpointTests.Where(e => e.IsOptional).ToList();

            if (requiredEndpoints.Any())
            {
                testSuites.Add(MapEndpointTests(requiredEndpoints, openApiResult?.Metadata?.BaseUrl ?? "",
                    "Level 1 Compliance - Basic checks",
                    "Will validate the required basic endpoints. Validation will fail if it does not pass all these checks.",
                    true));
            }

            if (optionalEndpoints.Any())
            {
                testSuites.Add(MapEndpointTests(optionalEndpoints, openApiResult?.Metadata?.BaseUrl ?? "",
                    "Level 2 Compliance - Extended checks",
                    "Will validate all other endpoints. Validation will not fail if it does not pass all these checks.",
                    false));
            }
        }

        // Determine overall validity from the fully computed result on the validation result object,
        // which accounts for endpoint failures (FailedValidation and Error), specification validation errors,
        // and optional-endpoint treatment rules.
        bool isValid = openApiResult?.IsValid ?? false;

        var specificationValidation = openApiResult?.SpecificationValidation == null
            ? null
            : new
            {
                isValid = openApiResult.SpecificationValidation.IsValid,
                version = openApiResult.SpecificationValidation.Version,
                errors = openApiResult.SpecificationValidation.Errors.Select(error => new
                {
                    name = error.ErrorCode,
                    description = error.Severity,
                    message = error.Message,
                    errorIn = BuildErrorIn(error),
                    errorAt = BuildErrorAt(error)
                }).ToList()
            };

        return new OpenReferralUKValidationResponse
        {
            Service = new ServiceInfo
            {
                Url = openApiResult?.Metadata?.BaseUrl ?? "",
                IsValid = isValid,
                Profile = openApiResult?.Metadata?.Profile
                    ?? $"{openApiResult?.SpecificationValidation?.Version ?? "Unknown"}",
                ProfileReason = openApiResult?.Metadata?.ProfileReason ?? "Unknown"
            },
            TestSuites = testSuites,
            SpecificationValidation = specificationValidation,
            Notifications = openApiResult?.Notifications?.ToList() ?? new List<string>()
        };
    }

    private static string BuildErrorAt(ValidationError error)
    {
        if (error.LineNumber.HasValue && error.ColumnNumber.HasValue)
        {
            return $"line {error.LineNumber.Value}, column {error.ColumnNumber.Value}";
        }

        if (error.LineNumber.HasValue)
        {
            return $"line {error.LineNumber.Value}";
        }

        if (error.ColumnNumber.HasValue)
        {
            return $"column {error.ColumnNumber.Value}";
        }

        return string.Empty;
    }

    private static string BuildErrorIn(ValidationError error)
    {
        if (IsJsonStructureViolation(error.ErrorCode) && !string.IsNullOrWhiteSpace(error.SourceIdentifier))
        {
            return $"source={error.SourceIdentifier} | path={error.Path}";
        }

        return error.Path;
    }

    private static bool IsJsonStructureViolation(string? errorCode)
    {
        return string.Equals(errorCode, "JSON_STRUCTURE_VIOLATION", StringComparison.Ordinal)
            || string.Equals(errorCode, "CACHED_SCHEMA_STRUCTURE_VIOLATION", StringComparison.Ordinal)
            || string.Equals(errorCode, "SCHEMA_STRUCTURE_VIOLATION", StringComparison.Ordinal);
    }

    private object MapEndpointTests(List<EndpointTestResult> endpointTests, string baseUrl,
        string name, string description, bool required)
    {
        var tests = endpointTests.Select(endpoint =>
        {
            var testToUse = endpoint.PrimaryTestResult;

            return new
            {
                name = endpoint.Name ?? $"{endpoint.Method} {endpoint.Path}",
                endpoint = $"{baseUrl}{endpoint.Path}",
                description = endpoint.Summary ?? endpoint.OperationId ?? "Endpoint test",
                id = testToUse?.TestedId,
                success = endpoint.PrimaryTestResult?.ValidationResult?.IsValid ?? endpoint.TestResults.Any(tr => tr.ValidationResult != null && tr.ValidationResult.IsValid),
                messages = MapEndpointMessages(endpoint, testToUse)
            };
        }).ToList();

        return new
        {
            name,
            description,
            messageLevel = required ? "error" : "warning",
            required,
            success = endpointTests.All(e => e.Status == EndpointTestStatus.PassedValidation || e.Status == EndpointTestStatus.PassedWithWarnings),
            tests
        };
    }

    private List<object> MapEndpointMessages(EndpointTestResult endpoint, HttpTestResult? specificTest = null)
    {
        var messages = new List<object>();

        // Add schema validation issues from test results
        // If a specific test is provided (first failed), use only that one
        var endpointErrors = endpoint.ValidationErrors;

        var testsToProcess = specificTest != null
            ? [specificTest]
            : endpoint.TestResults.Where(tr => tr.ValidationResult != null && !tr.ValidationResult.IsValid);

        // Track seen error paths to deduplicate errors across multiple test results
        var seenErrorPaths = new HashSet<string>(StringComparer.Ordinal);

        if (specificTest == null && endpointErrors.Any())
        {
            foreach (var validationError in endpointErrors)
            {
                if (seenErrorPaths.Add(validationError.Path))
                {
                    messages.Add(new
                    {
                        name = validationError.ErrorCode,
                        description = validationError.Severity,
                        message = validationError.Message,
                        errorIn = validationError.Path,
                        errorAt = ""
                    });
                }
            }

            return messages;
        }

        foreach (var testResult in testsToProcess)
        {
            if (testResult.ValidationResult != null && !testResult.ValidationResult.IsValid)
            {
                foreach (var validationError in testResult.ValidationResult.Errors)
                {
                    // Only add error if we haven't seen this path before
                    if (seenErrorPaths.Add(validationError.Path))
                    {
                        messages.Add(new
                        {
                            name = validationError.ErrorCode,
                            description = validationError.Severity,
                            message = validationError.Message,
                            errorIn = validationError.Path,
                            errorAt = ""
                        });
                    }
                }
            }
        }

        // If endpoint succeeded but had performance issues, add info messages
        if (endpoint.Status == EndpointTestStatus.PassedValidation && endpoint.TestResults.Any())
        {
            var avgResponseTime = endpoint.TestResults
                .Where(tr => tr.ResponseTime > TimeSpan.Zero)
                .Average(tr => tr.ResponseTime.TotalMilliseconds);

            if (avgResponseTime > 5000) // Slow response warning
            {
                messages.Add(new
                {
                    name = "Performance",
                    description = "Warning",
                    message = $"Average response time is {avgResponseTime:F0}ms, which may be slow",
                    errorIn = endpoint.Path,
                    errorAt = ""
                });
            }
        }

        return messages;
    }
}
