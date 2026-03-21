using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ValidationError = OpenReferralApi.Core.Models.Validation.ValidationError;

namespace OpenReferralApi.Core.Services;

public interface IHsdsComplianceService
{
    string? ExtractClaimedProfileVersion(string? profileReason, string? schemaUrl);
    bool TryGetKnownHsdsSchemaUrl(string? profileVersion, out string schemaUrl);
    List<ValidationError> CompareFeedSpecAgainstHsdsProfile(JObject feedSpec, JObject hsdsSpec);
    Task ValidateEndpointResponsesAgainstHsdsProfileAsync(
        List<EndpointTestResult> endpointTests,
        JObject hsdsSpec,
        OpenApiValidationOptions options,
        CancellationToken cancellationToken);
    void ApplyAdditionalFieldPolicy(ValidationResult? validationResult);
}

public class HsdsComplianceService : IHsdsComplianceService
{
    private static readonly Regex ProfileReasonVersionRegex = new(@"Standard version \[user:\s*(?<version>[^\]]+)\]", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex VersionNumberRegex = new(@"(?<major>\d+)(?:\.(?<minor>\d+))?", RegexOptions.Compiled);

    private static readonly HashSet<string> SupportedHttpMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "get", "post", "put", "delete", "patch", "head", "options", "trace"
    };

    // In-memory lookup table for known HSDS baseline schemas by profile version.
    private static readonly IReadOnlyDictionary<string, string> DefaultHsdsSchemaByVersion =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["1.0"] = "https://openreferraluk.org/specifications/1.0/openapi.json",
            ["2.0"] = "https://openreferraluk.org/specifications/2.0/openapi.json",
            ["3.0"] = "https://openreferraluk.org/specifications/3.0/openapi.json",
            ["3.1"] = "https://openreferraluk.org/specifications/3.1/openapi.json"
        };

    private readonly IJsonValidatorService _jsonValidatorService;
    private readonly IReadOnlyDictionary<string, string> _profileSchemaByVersion;
    private readonly SpecificationOptions? _specificationOptions;

    public HsdsComplianceService(
        IJsonValidatorService jsonValidatorService,
        IOptions<SpecificationOptions>? specificationOptions = null)
    {
        _jsonValidatorService = jsonValidatorService;
        _specificationOptions = specificationOptions?.Value;
        _profileSchemaByVersion = BuildProfileSchemaLookup(_specificationOptions);
    }

    public string? ExtractClaimedProfileVersion(string? profileReason, string? schemaUrl)
    {
        if (!string.IsNullOrWhiteSpace(profileReason))
        {
            var profileReasonMatch = ProfileReasonVersionRegex.Match(profileReason);
            if (profileReasonMatch.Success)
            {
                var extracted = NormalizeVersion(profileReasonMatch.Groups["version"].Value);
                if (!string.IsNullOrWhiteSpace(extracted))
                {
                    return extracted;
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(schemaUrl))
        {
            var urlMatch = Regex.Match(schemaUrl, @"/specifications/(?<version>[^/]+)/openapi\.json", RegexOptions.IgnoreCase);
            if (urlMatch.Success)
            {
                return NormalizeVersion(urlMatch.Groups["version"].Value);
            }
        }

        return null;
    }

    public bool TryGetKnownHsdsSchemaUrl(string? profileVersion, out string schemaUrl)
    {
        schemaUrl = string.Empty;
        var normalizedVersion = NormalizeVersion(profileVersion);
        if (string.IsNullOrWhiteSpace(normalizedVersion))
        {
            return false;
        }

        if (!_profileSchemaByVersion.TryGetValue(normalizedVersion, out var resolvedSchemaUrl))
        {
            return false;
        }

        schemaUrl = resolvedSchemaUrl;
        return true;
    }

    public List<ValidationError> CompareFeedSpecAgainstHsdsProfile(JObject feedSpec, JObject hsdsSpec)
    {
        var findings = new List<ValidationError>();

        var feedOperations = GetOperationMap(feedSpec, includeOptionalOperations: true);
        var hsdsAllOperations = GetOperationMap(hsdsSpec, includeOptionalOperations: true);
        var hsdsRequiredOperations = GetOperationMap(hsdsSpec, includeOptionalOperations: false);

        foreach (var requiredOperation in hsdsRequiredOperations.Keys)
        {
            if (!feedOperations.ContainsKey(requiredOperation))
            {
                findings.Add(new ValidationError
                {
                    Path = $"paths.{requiredOperation}",
                    Message = $"Missing required HSDS endpoint: {requiredOperation}",
                    ErrorCode = "HSDS_MISSING_ENDPOINT",
                    Severity = "Error"
                });
            }
        }

        foreach (var feedOperation in feedOperations.Keys)
        {
            if (!hsdsAllOperations.ContainsKey(feedOperation))
            {
                findings.Add(new ValidationError
                {
                    Path = $"paths.{feedOperation}",
                    Message = $"Additional endpoint not defined by HSDS profile: {feedOperation}",
                    ErrorCode = "HSDS_ADDITIONAL_ENDPOINT",
                    Severity = "Info"
                });
            }
        }

        var commonOperations = feedOperations.Keys.Intersect(hsdsRequiredOperations.Keys, StringComparer.OrdinalIgnoreCase);
        foreach (var operationKey in commonOperations)
        {
            var feedOperation = feedOperations[operationKey];
            var hsdsOperation = hsdsRequiredOperations[operationKey];

            var feedResponseSchema = GetPrimarySuccessResponseSchema(feedOperation);
            var hsdsResponseSchema = GetPrimarySuccessResponseSchema(hsdsOperation);
            CompareSchemaFields(
                findings,
                operationKey,
                scope: "response",
                feedSchema: feedResponseSchema,
                hsdsSchema: hsdsResponseSchema,
                missingFieldCode: "HSDS_MISSING_REQUIRED_FIELD",
                additionalFieldCode: "HSDS_ADDITIONAL_FIELD",
                missingFieldMessagePrefix: "Missing required HSDS field",
                additionalFieldMessagePrefix: "Additional field",
                missingSchemaCode: null,
                missingSchemaMessage: null);

            var feedRequestSchema = GetRequestBodySchema(feedOperation);
            var hsdsRequestSchema = GetRequestBodySchema(hsdsOperation);
            CompareSchemaFields(
                findings,
                operationKey,
                scope: "requestBody",
                feedSchema: feedRequestSchema,
                hsdsSchema: hsdsRequestSchema,
                missingFieldCode: "HSDS_MISSING_REQUIRED_REQUEST_FIELD",
                additionalFieldCode: "HSDS_ADDITIONAL_REQUEST_FIELD",
                missingFieldMessagePrefix: "Missing required HSDS request-body field",
                additionalFieldMessagePrefix: "Additional request-body field",
                missingSchemaCode: "HSDS_MISSING_REQUEST_BODY",
                missingSchemaMessage: "Missing request body schema required by HSDS profile");
        }

        return findings;
    }

    public async Task ValidateEndpointResponsesAgainstHsdsProfileAsync(
        List<EndpointTestResult> endpointTests,
        JObject hsdsSpec,
        OpenApiValidationOptions options,
        CancellationToken cancellationToken)
    {
        var hsdsOperations = GetOperationMap(hsdsSpec, includeOptionalOperations: false);

        foreach (var endpoint in endpointTests)
        {
            var operationKey = $"{endpoint.Method?.ToUpperInvariant()} {endpoint.Path}";
            if (!hsdsOperations.TryGetValue(operationKey, out var hsdsOperation))
            {
                continue;
            }

            var hsdsResponseSchema = GetPrimarySuccessResponseSchema(hsdsOperation);
            if (hsdsResponseSchema == null)
            {
                continue;
            }

            foreach (var testResult in endpoint.TestResults.Where(t => t.IsSuccessStatusCode && !string.IsNullOrWhiteSpace(t.ResponseBody)))
            {
                var validationRequest = new ValidationRequest
                {
                    JsonData = JsonConvert.DeserializeObject(testResult.ResponseBody ?? "{}"),
                    Schema = hsdsResponseSchema,
                    Options = new ValidationOptions
                    {
                        ReportAdditionalFields = true
                    }
                };

                var hsdsValidationResult = await _jsonValidatorService.ValidateAsync(validationRequest, cancellationToken);
                ApplyAdditionalFieldPolicy(hsdsValidationResult);

                if (hsdsValidationResult.Errors.Count == 0)
                {
                    continue;
                }

                var mappedErrors = hsdsValidationResult.Errors.Select(error => new ValidationError
                {
                    Path = error.Path,
                    Message = $"HSDS runtime validation: {error.Message}",
                    ErrorCode = error.ErrorCode.Equals("ADDITIONAL_FIELD", StringComparison.OrdinalIgnoreCase)
                        ? "HSDS_RUNTIME_ADDITIONAL_FIELD"
                        : "HSDS_RUNTIME_VALIDATION_ERROR",
                    Severity = error.Severity,
                    LineNumber = error.LineNumber,
                    ColumnNumber = error.ColumnNumber
                }).ToList();

                if (testResult.ValidationResult == null)
                {
                    testResult.ValidationResult = new ValidationResult
                    {
                        IsValid = false,
                        Errors = mappedErrors,
                        SchemaVersion = hsdsValidationResult.SchemaVersion,
                        Duration = hsdsValidationResult.Duration
                    };
                }
                else
                {
                    testResult.ValidationResult.Errors.AddRange(mappedErrors);
                }

                if (mappedErrors.Any(e => string.Equals(e.Severity, "Error", StringComparison.OrdinalIgnoreCase)))
                {
                    endpoint.Status = EndpointTestStatus.FailedValidation;
                }
                else if (endpoint.Status != EndpointTestStatus.FailedValidation)
                {
                    endpoint.Status = EndpointTestStatus.PassedWithWarnings;
                }
            }

            endpoint.RefreshFlattenedFields();
        }
    }

    public void ApplyAdditionalFieldPolicy(ValidationResult? validationResult)
    {
        if (validationResult?.Errors == null || validationResult.Errors.Count == 0)
        {
            return;
        }

        var additionalFieldErrors = validationResult.Errors
            .Where(e => string.Equals(e.ErrorCode, "ADDITIONAL_FIELD", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (additionalFieldErrors.Count == 0)
        {
            return;
        }

        var strictOwnSchemaValidation = _specificationOptions?.StrictOwnSchemaValidation ?? true;
        var additionalFieldSeverity = strictOwnSchemaValidation ? "Error" : "Warning";
        foreach (var error in additionalFieldErrors)
        {
            error.Severity = additionalFieldSeverity;
        }

        validationResult.IsValid = !validationResult.Errors.Any(e =>
            string.Equals(e.Severity, "Error", StringComparison.OrdinalIgnoreCase));
    }

    private static string? NormalizeVersion(string? rawVersion)
    {
        if (string.IsNullOrWhiteSpace(rawVersion))
        {
            return null;
        }

        var cleaned = rawVersion
            .Replace("HSDS-UK-", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("V", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Trim();

        var match = VersionNumberRegex.Match(cleaned);
        if (!match.Success)
        {
            return null;
        }

        var major = match.Groups["major"].Value;
        var minor = match.Groups["minor"].Success ? match.Groups["minor"].Value : "0";
        if (!int.TryParse(major, NumberStyles.None, CultureInfo.InvariantCulture, out var majorNumber) ||
            !int.TryParse(minor, NumberStyles.None, CultureInfo.InvariantCulture, out var minorNumber))
        {
            return null;
        }

        return $"{majorNumber}.{minorNumber}";
    }

    private static IReadOnlyDictionary<string, string> BuildProfileSchemaLookup(SpecificationOptions? options)
    {
        var lookup = new Dictionary<string, string>(DefaultHsdsSchemaByVersion, StringComparer.OrdinalIgnoreCase);

        if (options?.Urls != null)
        {
            MergeMappings(lookup, options.Urls);
        }

        return lookup;
    }

    private static void MergeMappings(Dictionary<string, string> destination, IReadOnlyDictionary<string, string>? source)
    {
        if (source == null)
        {
            return;
        }

        foreach (var pair in source)
        {
            var normalizedVersion = NormalizeVersion(pair.Key);
            if (string.IsNullOrWhiteSpace(normalizedVersion) || string.IsNullOrWhiteSpace(pair.Value))
            {
                continue;
            }

            var schemaUrl = pair.Value.Trim();
            if (!Uri.IsWellFormedUriString(schemaUrl, UriKind.Absolute))
            {
                continue;
            }

            destination[normalizedVersion] = schemaUrl;
        }
    }
    private static Dictionary<string, JObject> GetOperationMap(JObject spec, bool includeOptionalOperations)
    {
        var operationMap = new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);
        if (spec["paths"] is not JObject paths)
        {
            return operationMap;
        }

        foreach (var pathProperty in paths.Properties())
        {
            if (pathProperty.Value is not JObject pathItem)
            {
                continue;
            }

            foreach (var methodProperty in pathItem.Properties())
            {
                if (!SupportedHttpMethods.Contains(methodProperty.Name) || methodProperty.Value is not JObject operation)
                {
                    continue;
                }

                if (!includeOptionalOperations && operation.IsOptionalEndpoint())
                {
                    continue;
                }

                var operationKey = $"{methodProperty.Name.ToUpperInvariant()} {pathProperty.Name}";
                operationMap[operationKey] = operation;
            }
        }

        return operationMap;
    }

    private static JToken? GetPrimarySuccessResponseSchema(JObject operation)
    {
        if (operation["responses"] is not JObject responses)
        {
            return null;
        }

        var statusCodeKey = responses.Properties()
            .Select(p => p.Name)
            .FirstOrDefault(name => name.StartsWith("2", StringComparison.Ordinal));

        if (statusCodeKey == null)
        {
            return null;
        }

        if (responses[statusCodeKey] is not JObject responseObject ||
            responseObject["content"] is not JObject contentObject)
        {
            return null;
        }

        var jsonContent = contentObject.Properties()
            .FirstOrDefault(p => p.Name.Contains("application/json", StringComparison.OrdinalIgnoreCase));

        return jsonContent?.Value?["schema"];
    }

    private static JToken? GetRequestBodySchema(JObject operation)
    {
        if (operation["requestBody"] is not JObject requestBodyObject ||
            requestBodyObject["content"] is not JObject contentObject)
        {
            return null;
        }

        var jsonContent = contentObject.Properties()
            .FirstOrDefault(p => p.Name.Contains("application/json", StringComparison.OrdinalIgnoreCase));

        return jsonContent?.Value?["schema"];
    }

    private static void CompareSchemaFields(
        List<ValidationError> findings,
        string operationKey,
        string scope,
        JToken? feedSchema,
        JToken? hsdsSchema,
        string missingFieldCode,
        string additionalFieldCode,
        string missingFieldMessagePrefix,
        string additionalFieldMessagePrefix,
        string? missingSchemaCode,
        string? missingSchemaMessage)
    {
        if (hsdsSchema == null)
        {
            return;
        }

        if (feedSchema == null)
        {
            if (!string.IsNullOrWhiteSpace(missingSchemaCode) && !string.IsNullOrWhiteSpace(missingSchemaMessage))
            {
                findings.Add(new ValidationError
                {
                    Path = $"paths.{operationKey}.{scope}",
                    Message = $"{missingSchemaMessage} for endpoint {operationKey}",
                    ErrorCode = missingSchemaCode,
                    Severity = "Error"
                });
            }

            return;
            }

        var hsdsRequiredFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ExtractRequiredFieldPaths(hsdsSchema, string.Empty, hsdsRequiredFields);

        var feedFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ExtractAllFieldPaths(feedSchema, string.Empty, feedFields);

        foreach (var requiredField in hsdsRequiredFields)
        {
            if (!feedFields.Contains(requiredField))
            {
                findings.Add(new ValidationError
                {
                    Path = $"paths.{operationKey}.{scope}.{requiredField}",
                    Message = $"{missingFieldMessagePrefix} '{requiredField}' for endpoint {operationKey}",
                    ErrorCode = missingFieldCode,
                    Severity = "Error"
                });
            }
        }

        var hsdsFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ExtractAllFieldPaths(hsdsSchema, string.Empty, hsdsFields);

        foreach (var feedField in feedFields)
        {
            if (!hsdsFields.Contains(feedField))
            {
                findings.Add(new ValidationError
                {
                    Path = $"paths.{operationKey}.{scope}.{feedField}",
                    Message = $"{additionalFieldMessagePrefix} '{feedField}' is not defined in HSDS profile for endpoint {operationKey}",
                    ErrorCode = additionalFieldCode,
                    Severity = "Info"
                });
            }
        }
    }

    private static void ExtractRequiredFieldPaths(JToken schemaToken, string prefix, ISet<string> result)
    {
        if (schemaToken is not JObject schemaObject)
        {
            return;
        }

        if (schemaObject["required"] is JArray requiredArray && schemaObject["properties"] is JObject properties)
        {
            foreach (var requiredToken in requiredArray)
            {
                var requiredName = requiredToken?.ToString();
                if (string.IsNullOrWhiteSpace(requiredName))
                {
                    continue;
                }

                var fullPath = string.IsNullOrEmpty(prefix) ? requiredName : $"{prefix}.{requiredName}";
                result.Add(fullPath);

                if (properties[requiredName] != null)
                {
                    ExtractRequiredFieldPaths(properties[requiredName]!, fullPath, result);
                }
            }
        }

        if (schemaObject["type"]?.ToString() == "array" && schemaObject["items"] != null)
        {
            var arrayPrefix = string.IsNullOrEmpty(prefix) ? "[]" : $"{prefix}[]";
            ExtractRequiredFieldPaths(schemaObject["items"]!, arrayPrefix, result);
        }

        if (schemaObject["allOf"] is JArray allOf)
        {
            foreach (var subSchema in allOf)
            {
                ExtractRequiredFieldPaths(subSchema, prefix, result);
            }
        }
    }

    private static void ExtractAllFieldPaths(JToken schemaToken, string prefix, ISet<string> result)
    {
        if (schemaToken is not JObject schemaObject)
        {
            return;
        }

        if (schemaObject["properties"] is JObject properties)
        {
            foreach (var property in properties.Properties())
            {
                var fullPath = string.IsNullOrEmpty(prefix) ? property.Name : $"{prefix}.{property.Name}";
                result.Add(fullPath);
                ExtractAllFieldPaths(property.Value, fullPath, result);
            }
        }

        if (schemaObject["type"]?.ToString() == "array" && schemaObject["items"] != null)
        {
            var arrayPrefix = string.IsNullOrEmpty(prefix) ? "[]" : $"{prefix}[]";
            ExtractAllFieldPaths(schemaObject["items"]!, arrayPrefix, result);
        }

        if (schemaObject["allOf"] is JArray allOf)
        {
            foreach (var subSchema in allOf)
            {
                ExtractAllFieldPaths(subSchema, prefix, result);
            }
        }
    }
}
