using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Schema;
using OpenReferralApi.Core.Logging;
using ValidationError = OpenReferralApi.Core.Models.Validation.ValidationError;

namespace OpenReferralApi.Core.Services;

public interface IOpenApiSpecificationService
{
    Task<OpenApiSpecificationValidation> ValidateAsync(JObject openApiSpec, CancellationToken cancellationToken = default);
}

public class OpenApiSpecificationService : IOpenApiSpecificationService
{
    private readonly ILogger<OpenApiSpecificationService> _logger;
    private readonly IJsonValidatorService _jsonValidatorService;
    private readonly IOptions<SchemaResolutionOptions> _schemaResolutionOptions;

    public OpenApiSpecificationService(
        ILogger<OpenApiSpecificationService> logger,
        IJsonValidatorService jsonValidatorService,
        IOptions<SchemaResolutionOptions>? schemaResolutionOptions = null)
    {
        _logger = logger;
        _jsonValidatorService = jsonValidatorService;
        _schemaResolutionOptions = schemaResolutionOptions ?? Options.Create(new SchemaResolutionOptions());
    }

    public async Task<OpenApiSpecificationValidation> ValidateAsync(JObject openApiSpec, CancellationToken cancellationToken = default)
    {
        var validation = new OpenApiSpecificationValidation();
        var errors = new List<ValidationError>();

        try
        {
            _logger.ValidatingOpenApiSpecification();

            await ValidateOpenApiSpecObjectAsync(openApiSpec, validation, errors, null, cancellationToken);

            validation.SchemaAnalysis = AnalyzeSchemaStructure(openApiSpec);
            validation.QualityMetrics = AnalyzeQualityMetrics(openApiSpec);
            validation.Recommendations = GenerateRecommendations(openApiSpec, errors);

            return validation;
        }
        catch (Exception ex)
        {
            _logger.ErrorDuringOpenApiValidation(ex);
            errors.Add(new ValidationError
            {
                Path = "",
                Message = $"Validation error: {TextSanitizer.SanitizeExceptionMessage(ex.Message)}",
                ErrorCode = "VALIDATION_ERROR",
                Severity = "Error"
            });

            validation.IsValid = false;
            validation.Errors = errors;
            return validation;
        }
    }

    private async Task ValidateOpenApiSpecObjectAsync(
        JObject specObject,
        OpenApiSpecificationValidation validation,
        List<ValidationError> errors,
        JSchema? originalSchema = null,
        CancellationToken cancellationToken = default)
    {
        if (!specObject.ContainsKey("openapi") && !specObject.ContainsKey("swagger"))
        {
            errors.Add(new ValidationError
            {
                Path = "",
                Message = "OpenAPI specification must contain 'openapi' or 'swagger' field",
                ErrorCode = "MISSING_OPENAPI_VERSION",
                Severity = "Error"
            });
        }

        if (specObject.ContainsKey("openapi"))
        {
            validation.OpenApiVersion = specObject["openapi"]?.ToString();
        }
        else if (specObject.ContainsKey("swagger"))
        {
            validation.OpenApiVersion = specObject["swagger"]?.ToString();
        }

        if (!specObject.ContainsKey("info"))
        {
            errors.Add(new ValidationError
            {
                Path = "info",
                Message = "OpenAPI specification must contain 'info' section",
                ErrorCode = "MISSING_INFO",
                Severity = "Error"
            });
        }
        else
        {
            var info = specObject["info"];
            validation.Title = info?["title"]?.ToString();
            validation.Version = info?["version"]?.ToString();

            if (string.IsNullOrEmpty(validation.Title))
            {
                errors.Add(new ValidationError
                {
                    Path = "info.title",
                    Message = "API title is recommended",
                    ErrorCode = "MISSING_TITLE",
                    Severity = "Warning"
                });
            }

            if (string.IsNullOrEmpty(validation.Version))
            {
                errors.Add(new ValidationError
                {
                    Path = "info.version",
                    Message = "API version is recommended",
                    ErrorCode = "MISSING_VERSION",
                    Severity = "Warning"
                });
            }
        }

        if (!specObject.ContainsKey("paths"))
        {
            errors.Add(new ValidationError
            {
                Path = "paths",
                Message = "OpenAPI specification must contain 'paths' section",
                ErrorCode = "MISSING_PATHS",
                Severity = "Error"
            });
        }
        else
        {
            var paths = specObject["paths"];
            if (paths is JObject pathsObject)
            {
                validation.EndpointCount = pathsObject.Count;

                if (validation.EndpointCount == 0)
                {
                    errors.Add(new ValidationError
                    {
                        Path = "paths",
                        Message = "No endpoints defined in paths section",
                        ErrorCode = "NO_ENDPOINTS",
                        Severity = "Warning"
                    });
                }
            }
        }

        try
        {
            var schemaUri = this.GetOpenApiSchemaUri(specObject, validation.OpenApiVersion);
            if (!string.IsNullOrEmpty(schemaUri))
            {
                object dataForValidation = originalSchema != null ? originalSchema : specObject;
                var validationRequest = new ValidationRequest
                {
                    JsonData = dataForValidation,
                    SchemaUri = schemaUri
                };

                var schemaValidation = await _jsonValidatorService.ValidateAsync(validationRequest, cancellationToken);
                if (schemaValidation.Errors.Any())
                {
                    errors.AddRange(schemaValidation.Errors);
                }

                var dialectInfo = specObject.ContainsKey("jsonSchemaDialect")
                    ? $"using jsonSchemaDialect: {SchemaResolverService.SanitizeStringForLogging(specObject["jsonSchemaDialect"]?.ToString() ?? string.Empty)}"
                    : $"using version-based schema for OpenAPI {validation.OpenApiVersion}";
                _logger.ValidatedOpenApiSpecification(dialectInfo, schemaUri);
            }
            else
            {
                var dialectInfo = specObject.ContainsKey("jsonSchemaDialect")
                    ? $"jsonSchemaDialect '{specObject["jsonSchemaDialect"]}' is not supported"
                    : $"version '{validation.OpenApiVersion}' is not supported";

                errors.Add(new ValidationError
                {
                    Path = "",
                    Message = $"No schema validation available: {dialectInfo}. Supported versions: OpenAPI 3.0.x, 3.1.x, Swagger 2.0, and common JSON Schema dialects (2020-12, 2019-09, draft-07, draft-06, draft-04)",
                    ErrorCode = "UNSUPPORTED_SCHEMA_VERSION",
                    Severity = "Error"
                });
            }
        }
        catch (Exception ex)
        {
            _logger.CouldNotValidateAgainstSchema(ex);
            errors.Add(new ValidationError
            {
                Path = "",
                Message = $"Could not validate against OpenAPI schema: {TextSanitizer.SanitizeExceptionMessage(ex.Message)}",
                ErrorCode = "SCHEMA_VALIDATION_FAILED",
                Severity = "Error"
            });
        }

        validation.Errors = ValidationErrorNormalizer.NormalizeAndDeduplicateByPath(errors);
        validation.IsValid = !validation.Errors.Any(e => string.Equals(e.Severity, "Error", StringComparison.OrdinalIgnoreCase));

        _logger.OpenApiValidationCompleted(validation.IsValid, validation.Errors.Count);
    }

    private string? GetOpenApiSchemaUri(JObject specObject, string? version)
    {
        if (specObject.ContainsKey("jsonSchemaDialect"))
        {
            var dialect = specObject["jsonSchemaDialect"]?.ToString();
            if (!string.IsNullOrEmpty(dialect))
            {
                if (this.IsKnownJsonSchemaDialect(dialect))
                {
                    return dialect;
                }

                return null;
            }
        }

        if (!string.IsNullOrWhiteSpace(version))
        {
            // Use the root schema from configured KnownJsonSchemaUrls
            var knownUrls = _schemaResolutionOptions.Value.KnownJsonSchemaUrls;
            if (knownUrls?.Count > 0)
            {
                return knownUrls[0];
            }
        }

        return null;
    }

    private bool IsKnownJsonSchemaDialect(string dialect)
    {
        var knownUrls = _schemaResolutionOptions.Value.KnownJsonSchemaUrls;
        return knownUrls?.Contains(dialect, StringComparer.OrdinalIgnoreCase) == true;
    }

    private SchemaAnalysis AnalyzeSchemaStructure(JObject specObject)
    {
        var analysis = new SchemaAnalysis();

        try
        {
            if (specObject.ContainsKey("components"))
            {
                var components = specObject["components"];
                if (components is JObject componentsObject)
                {
                    analysis.ComponentCount = 1;

                    if (componentsObject.ContainsKey("schemas"))
                    {
                        var schemas = componentsObject["schemas"];
                        if (schemas is JObject schemasObject)
                        {
                            analysis.SchemaCount = schemasObject.Count;
                        }
                    }

                    if (componentsObject.ContainsKey("responses"))
                    {
                        var responses = componentsObject["responses"];
                        if (responses is JObject responsesObject)
                        {
                            analysis.ResponseCount = responsesObject.Count;
                        }
                    }

                    if (componentsObject.ContainsKey("parameters"))
                    {
                        var parameters = componentsObject["parameters"];
                        if (parameters is JObject parametersObject)
                        {
                            analysis.ParameterCount = parametersObject.Count;
                        }
                    }

                    if (componentsObject.ContainsKey("requestBodies"))
                    {
                        var requestBodies = componentsObject["requestBodies"];
                        if (requestBodies is JObject requestBodiesObject)
                        {
                            analysis.RequestBodyCount = requestBodiesObject.Count;
                        }
                    }

                    if (componentsObject.ContainsKey("headers"))
                    {
                        var headers = componentsObject["headers"];
                        if (headers is JObject headersObject)
                        {
                            analysis.HeaderCount = headersObject.Count;
                        }
                    }

                    if (componentsObject.ContainsKey("links"))
                    {
                        var links = componentsObject["links"];
                        if (links is JObject linksObject)
                        {
                            analysis.LinkCount = linksObject.Count;
                        }
                    }

                    if (componentsObject.ContainsKey("callbacks"))
                    {
                        var callbacks = componentsObject["callbacks"];
                        if (callbacks is JObject callbacksObject)
                        {
                            analysis.CallbackCount = callbacksObject.Count;
                        }
                    }
                }
            }

            if (specObject.ContainsKey("definitions"))
            {
                var definitions = specObject["definitions"];
                if (definitions is JObject definitionsObject)
                {
                    analysis.SchemaCount = definitionsObject.Count;
                }
            }

            analysis.ExampleCount = CountExamplesInSpec(specObject);

            var specJson = specObject.ToString();
            var refMatches = Regex.Matches(specJson, "\\$ref");
            analysis.ReferencesResolved = refMatches.Count;
        }
        catch (Exception ex)
        {
            _logger.ErrorAnalyzingSchemaStructure(ex);
        }

        return analysis;
    }

    private int CountExamplesInSpec(JObject specObject)
    {
        int exampleCount = 0;

        try
        {
            if (specObject.ContainsKey("components"))
            {
                var components = specObject["components"];
                if (components is JObject componentsObject && componentsObject.ContainsKey("examples"))
                {
                    var examples = componentsObject["examples"];
                    if (examples is JObject examplesObject)
                    {
                        exampleCount += examplesObject.Count;
                    }
                }
            }

            if (specObject.ContainsKey("paths"))
            {
                var paths = specObject["paths"];
                if (paths is JObject pathsObject)
                {
                    foreach (var path in pathsObject.Properties())
                    {
                        if (path.Value is JObject pathObject)
                        {
                            foreach (var operation in pathObject.Properties())
                            {
                                if (operation.Value is JObject operationObject)
                                {
                                    if (operationObject.ContainsKey("requestBody"))
                                    {
                                        var requestBody = operationObject["requestBody"];
                                        if (requestBody is JObject requestBodyObject && requestBodyObject.ContainsKey("content"))
                                        {
                                            var content = requestBodyObject["content"];
                                            if (content is JObject contentObject)
                                            {
                                                foreach (var mediaType in contentObject.Properties())
                                                {
                                                    if (mediaType.Value is JObject mediaTypeObject)
                                                    {
                                                        if (mediaTypeObject.ContainsKey("example"))
                                                        {
                                                            exampleCount++;
                                                        }
                                                        if (mediaTypeObject.ContainsKey("examples"))
                                                        {
                                                            var examples = mediaTypeObject["examples"];
                                                            if (examples is JObject examplesObject)
                                                            {
                                                                exampleCount += examplesObject.Count;
                                                            }
                                                        }
                                                    }
                                                }
                                            }
                                        }
                                    }

                                    if (operationObject.ContainsKey("responses"))
                                    {
                                        var responses = operationObject["responses"];
                                        if (responses is JObject responsesObject)
                                        {
                                            foreach (var response in responsesObject.Properties())
                                            {
                                                if (response.Value is JObject responseObject && responseObject.ContainsKey("content"))
                                                {
                                                    var content = responseObject["content"];
                                                    if (content is JObject contentObject)
                                                    {
                                                        foreach (var mediaType in contentObject.Properties())
                                                        {
                                                            if (mediaType.Value is JObject mediaTypeObject)
                                                            {
                                                                if (mediaTypeObject.ContainsKey("example"))
                                                                {
                                                                    exampleCount++;
                                                                }
                                                                if (mediaTypeObject.ContainsKey("examples"))
                                                                {
                                                                    var examples = mediaTypeObject["examples"];
                                                                    if (examples is JObject examplesObject)
                                                                    {
                                                                        exampleCount += examplesObject.Count;
                                                                    }
                                                                }
                                                            }
                                                        }
                                                    }
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.ErrorCountingExamples(ex);
        }

        return exampleCount;
    }

    private QualityMetrics AnalyzeQualityMetrics(JObject specObject)
    {
        var metrics = new QualityMetrics();

        try
        {
            if (specObject.ContainsKey("paths"))
            {
                var paths = specObject["paths"];
                if (paths is JObject pathsObject)
                {
                    int totalEndpoints = 0;
                    int endpointsWithDescription = 0;
                    int endpointsWithSummary = 0;
                    int endpointsWithExamples = 0;
                    int totalParameters = 0;
                    int parametersWithDescription = 0;
                    int totalResponseCodes = 0;
                    int responseCodesDocumented = 0;

                    foreach (var path in pathsObject.Properties())
                    {
                        if (path.Value is JObject pathObject)
                        {
                            foreach (var method in pathObject.Properties())
                            {
                                if (method.Value is JObject operationObject)
                                {
                                    totalEndpoints++;

                                    if (operationObject.ContainsKey("description") &&
                                        !string.IsNullOrWhiteSpace(operationObject["description"]?.ToString()))
                                    {
                                        endpointsWithDescription++;
                                    }

                                    if (operationObject.ContainsKey("summary") &&
                                        !string.IsNullOrWhiteSpace(operationObject["summary"]?.ToString()))
                                    {
                                        endpointsWithSummary++;
                                    }

                                    if (HasExamples(operationObject))
                                    {
                                        endpointsWithExamples++;
                                    }

                                    if (operationObject.ContainsKey("parameters"))
                                    {
                                        var parameters = operationObject["parameters"];
                                        if (parameters is JArray parametersArray)
                                        {
                                            totalParameters += parametersArray.Count;
                                            parametersWithDescription += parametersArray
                                                .Where(p => p is JObject pObj &&
                                                       pObj.ContainsKey("description") &&
                                                       !string.IsNullOrWhiteSpace(pObj["description"]?.ToString()))
                                                .Count();
                                        }
                                    }

                                    if (operationObject.ContainsKey("responses"))
                                    {
                                        var responses = operationObject["responses"];
                                        if (responses is JObject responsesObject)
                                        {
                                            totalResponseCodes += responsesObject.Count;
                                            responseCodesDocumented += responsesObject.Properties()
                                                .Where(r => r.Value is JObject rObj &&
                                                       rObj.ContainsKey("description") &&
                                                       !string.IsNullOrWhiteSpace(rObj["description"]?.ToString()))
                                                .Count();
                                        }
                                    }
                                }
                            }
                        }
                    }

                    metrics.EndpointsWithDescription = endpointsWithDescription;
                    metrics.EndpointsWithSummary = endpointsWithSummary;
                    metrics.EndpointsWithExamples = endpointsWithExamples;
                    metrics.ParametersWithDescription = parametersWithDescription;
                    metrics.TotalParameters = totalParameters;
                    metrics.ResponseCodesDocumented = responseCodesDocumented;
                    metrics.TotalResponseCodes = totalResponseCodes;

                    if (totalEndpoints > 0)
                    {
                        metrics.DocumentationCoverage = (double)endpointsWithDescription / totalEndpoints * 100;
                    }
                }
            }

            CountSchemaDescriptions(specObject, metrics);
            CalculateQualityScore(metrics);
        }
        catch (Exception ex)
        {
            _logger.ErrorAnalyzingQualityMetrics(ex);
        }

        return metrics;
    }

    private bool HasExamples(JObject operationObject)
    {
        if (operationObject.ContainsKey("requestBody"))
        {
            var requestBody = operationObject["requestBody"];
            if (requestBody is JObject requestBodyObject && HasContentExamples(requestBodyObject))
            {
                return true;
            }
        }

        if (operationObject.ContainsKey("responses"))
        {
            var responses = operationObject["responses"];
            if (responses is JObject responsesObject)
            {
                foreach (var response in responsesObject.Properties())
                {
                    if (response.Value is JObject responseObject && HasContentExamples(responseObject))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    private bool HasContentExamples(JObject contentContainer)
    {
        if (contentContainer.ContainsKey("content"))
        {
            var content = contentContainer["content"];
            if (content is JObject contentObject)
            {
                foreach (var mediaType in contentObject.Properties())
                {
                    if (mediaType.Value is JObject mediaTypeObject)
                    {
                        if (mediaTypeObject.ContainsKey("example") || mediaTypeObject.ContainsKey("examples"))
                        {
                            return true;
                        }
                    }
                }
            }
        }
        return false;
    }

    private void CountSchemaDescriptions(JObject specObject, QualityMetrics metrics)
    {
        if (specObject.ContainsKey("components"))
        {
            var components = specObject["components"];
            if (components is JObject componentsObject && componentsObject.ContainsKey("schemas"))
            {
                var schemas = componentsObject["schemas"];
                if (schemas is JObject schemasObject)
                {
                    metrics.TotalSchemas = schemasObject.Count;
                    metrics.SchemasWithDescription = schemasObject.Properties()
                        .Where(s => s.Value is JObject sObj &&
                               sObj.ContainsKey("description") &&
                               !string.IsNullOrWhiteSpace(sObj["description"]?.ToString()))
                        .Count();
                }
            }
        }

        if (specObject.ContainsKey("definitions"))
        {
            var definitions = specObject["definitions"];
            if (definitions is JObject definitionsObject)
            {
                metrics.TotalSchemas = definitionsObject.Count;
                metrics.SchemasWithDescription = definitionsObject.Properties()
                    .Where(d => d.Value is JObject dObj &&
                           dObj.ContainsKey("description") &&
                           !string.IsNullOrWhiteSpace(dObj["description"]?.ToString()))
                    .Count();
            }
        }
    }

    private void CalculateQualityScore(QualityMetrics metrics)
    {
        double score = 0;
        int factors = 0;

        if (metrics.DocumentationCoverage > 0)
        {
            score += metrics.DocumentationCoverage * 0.3;
            factors++;
        }

        if (metrics.TotalParameters > 0)
        {
            double parameterScore = (double)metrics.ParametersWithDescription / metrics.TotalParameters * 100;
            score += parameterScore * 0.25;
            factors++;
        }

        if (metrics.TotalSchemas > 0)
        {
            double schemaScore = (double)metrics.SchemasWithDescription / metrics.TotalSchemas * 100;
            score += schemaScore * 0.25;
            factors++;
        }

        if (metrics.TotalResponseCodes > 0)
        {
            double responseScore = (double)metrics.ResponseCodesDocumented / metrics.TotalResponseCodes * 100;
            score += responseScore * 0.20;
            factors++;
        }

        metrics.QualityScore = factors > 0 ? score / factors : 0;
    }

    private List<Recommendation> GenerateRecommendations(JObject specObject, List<ValidationError> errors)
    {
        var recommendations = new List<Recommendation>();

        try
        {
            foreach (var error in errors.Where(e => e.Severity == "Error"))
            {
                recommendations.Add(new Recommendation
                {
                    Type = "Error",
                    Category = "Validation",
                    Priority = "High",
                    Message = error.Message,
                    Path = error.Path,
                    ActionRequired = "Fix this validation error to ensure spec compliance",
                    Impact = "API consumers may not be able to use the specification correctly"
                });
            }

            foreach (var error in errors.Where(e => e.Severity == "Warning"))
            {
                recommendations.Add(new Recommendation
                {
                    Type = "Warning",
                    Category = "Best Practice",
                    Priority = "Medium",
                    Message = error.Message,
                    Path = error.Path,
                    ActionRequired = "Consider addressing this warning to improve spec quality",
                    Impact = "May affect usability or developer experience"
                });
            }

            AddQualityRecommendations(specObject, recommendations);
        }
        catch (Exception ex)
        {
            _logger.ErrorGeneratingRecommendations(ex);
        }

        return recommendations;
    }

    private void AddQualityRecommendations(JObject specObject, List<Recommendation> recommendations)
    {
        if (!specObject.ContainsKey("info") || specObject["info"] is not JObject infoObject)
        {
            return;
        }

        if (!infoObject.ContainsKey("description") || string.IsNullOrWhiteSpace(infoObject["description"]?.ToString()))
        {
            recommendations.Add(new Recommendation
            {
                Type = "Improvement",
                Category = "Documentation",
                Priority = "Medium",
                Message = "API description is missing or empty",
                Path = "info.description",
                ActionRequired = "Add a comprehensive description of your API's purpose and functionality",
                Impact = "Helps developers understand the API's capabilities and use cases"
            });
        }

        if (!infoObject.ContainsKey("contact"))
        {
            recommendations.Add(new Recommendation
            {
                Type = "Improvement",
                Category = "Documentation",
                Priority = "Low",
                Message = "Contact information is missing",
                Path = "info.contact",
                ActionRequired = "Add contact information for API support",
                Impact = "Helps users get support when needed"
            });
        }

        if (!infoObject.ContainsKey("license"))
        {
            recommendations.Add(new Recommendation
            {
                Type = "Improvement",
                Category = "Legal",
                Priority = "Low",
                Message = "License information is missing",
                Path = "info.license",
                ActionRequired = "Add license information for your API",
                Impact = "Clarifies usage rights and restrictions"
            });
        }

        AddEndpointQualityRecommendations(specObject, recommendations);
    }

    private void AddEndpointQualityRecommendations(JObject specObject, List<Recommendation> recommendations)
    {
        if (!HasServerMetadata(specObject))
        {
            recommendations.Add(new Recommendation
            {
                Type = "Improvement",
                Category = "Documentation",
                Priority = "Medium",
                Message = "Server/base URL metadata is missing",
                Path = "servers",
                ActionRequired = "Add 'servers' (OpenAPI 3.x) or host/basePath/schemes (Swagger 2.0) so client tooling can resolve endpoint URLs consistently",
                Impact = "Improves endpoint testing reliability and machine-readability for client generators"
            });
        }

        if (!specObject.ContainsKey("paths") || specObject["paths"] is not JObject pathsObject)
        {
            return;
        }

        foreach (var path in pathsObject.Properties())
        {
            if (path.Value is not JObject pathObject)
            {
                continue;
            }

            foreach (var method in pathObject.Properties())
            {
                if (!IsOperationMethod(method.Name) || method.Value is not JObject operationObject)
                {
                    continue;
                }

                var operationPath = $"paths.{path.Name}.{method.Name}";

                if (string.IsNullOrWhiteSpace(operationObject["operationId"]?.ToString()))
                {
                    recommendations.Add(new Recommendation
                    {
                        Type = "Improvement",
                        Category = "Best Practice",
                        Priority = "Medium",
                        Message = "Operation ID is missing",
                        Path = $"{operationPath}.operationId",
                        ActionRequired = "Add a stable, unique operationId for this endpoint",
                        Impact = "Improves machine-readable integrations, SDK generation, and traceability in validation reports"
                    });
                }

                if (!TryGetResponsesObject(operationObject, out var responsesObject))
                {
                    recommendations.Add(new Recommendation
                    {
                        Type = "Improvement",
                        Category = "Testing",
                        Priority = "High",
                        Message = "Responses object is missing for this operation",
                        Path = $"{operationPath}.responses",
                        ActionRequired = "Define response status codes and payload structures for this operation",
                        Impact = "Improves endpoint validation quality and ensures consumers can handle expected outcomes"
                    });

                    continue;
                }

                var hasErrorResponse = responsesObject.Properties().Any(p => IsErrorStatusCode(p.Name));
                if (!hasErrorResponse)
                {
                    recommendations.Add(new Recommendation
                    {
                        Type = "Improvement",
                        Category = "Testing",
                        Priority = "High",
                        Message = "No error response codes are documented",
                        Path = $"{operationPath}.responses",
                        ActionRequired = "Document at least one 4xx and/or 5xx response for this operation",
                        Impact = "Improves feed validation accuracy by allowing negative-path behavior to be tested consistently"
                    });
                }

                var successResponseWithoutSchema = responsesObject.Properties()
                    .Where(p => IsSuccessStatusCode(p.Name) && p.Value is JObject)
                    .Any(p => !ResponseHasSchema((JObject)p.Value));

                if (successResponseWithoutSchema)
                {
                    recommendations.Add(new Recommendation
                    {
                        Type = "Improvement",
                        Category = "Data Quality",
                        Priority = "High",
                        Message = "One or more success responses are missing a response schema",
                        Path = $"{operationPath}.responses",
                        ActionRequired = "Add explicit schemas for 2xx responses (response.content.*.schema in OpenAPI 3.x or response.schema in Swagger 2.0)",
                        Impact = "Enables stronger runtime payload validation and improves feed quality checks"
                    });
                }
            }
        }
    }

    private static bool HasServerMetadata(JObject specObject)
    {
        if (specObject["servers"] is JArray servers && servers.Count > 0)
        {
            return true;
        }

        var host = specObject["host"]?.ToString();
        var basePath = specObject["basePath"]?.ToString();
        var schemes = specObject["schemes"] as JArray;

        return !string.IsNullOrWhiteSpace(host)
               || !string.IsNullOrWhiteSpace(basePath)
               || (schemes != null && schemes.Count > 0);
    }

    private static bool IsOperationMethod(string methodName)
    {
        return methodName.Equals("get", StringComparison.OrdinalIgnoreCase)
               || methodName.Equals("post", StringComparison.OrdinalIgnoreCase)
               || methodName.Equals("put", StringComparison.OrdinalIgnoreCase)
               || methodName.Equals("patch", StringComparison.OrdinalIgnoreCase)
               || methodName.Equals("delete", StringComparison.OrdinalIgnoreCase)
               || methodName.Equals("head", StringComparison.OrdinalIgnoreCase)
               || methodName.Equals("options", StringComparison.OrdinalIgnoreCase)
               || methodName.Equals("trace", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryGetResponsesObject(JObject operationObject, out JObject responsesObject)
    {
        responsesObject = null!;
        if (operationObject["responses"] is not JObject responses)
        {
            return false;
        }

        responsesObject = responses;
        return true;
    }

    private static bool IsSuccessStatusCode(string responseCode)
    {
        return responseCode.Length == 3
               && responseCode[0] == '2'
               && responseCode.All(char.IsDigit);
    }

    private static bool IsErrorStatusCode(string responseCode)
    {
        if (responseCode.Equals("default", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return responseCode.Length == 3
               && (responseCode[0] == '4' || responseCode[0] == '5')
               && responseCode.All(char.IsDigit);
    }

    private static bool ResponseHasSchema(JObject responseObject)
    {
        // OpenAPI 3.x: responses.<code>.content.<mediaType>.schema
        if (responseObject["content"] is JObject contentObject)
        {
            foreach (var mediaType in contentObject.Properties())
            {
                if (mediaType.Value is JObject mediaTypeObject && mediaTypeObject["schema"] != null)
                {
                    return true;
                }
            }
        }

        // Swagger 2.0: responses.<code>.schema
        return responseObject["schema"] != null;
    }

}
