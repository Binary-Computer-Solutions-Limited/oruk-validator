using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Schema;
using OpenReferralApi.Core.Logging;
using ValidationError = OpenReferralApi.Core.Models.Validation.ValidationError;

namespace OpenReferralApi.Core.Services;

public interface IJsonValidatorService
{
    Task<ValidationResult> ValidateAsync(ValidationRequest request, CancellationToken cancellationToken = default);
    Task<ValidationResult> ValidateWithSchemaUriAsync(object jsonData, string schemaUri, ValidationOptions? options = null, CancellationToken cancellationToken = default);
    Task<bool> IsValidAsync(ValidationRequest request, CancellationToken cancellationToken = default);
    Task<ValidationResult> ValidateSchemaAsync(object schema, CancellationToken cancellationToken = default);
}

public class JsonValidatorService : IJsonValidatorService
{
    private const int MaxAllowedJsonDepth = 64;
    private static readonly ConcurrentDictionary<string, CachedExternalSchemaDocument> ExternalSchemaUriCache = new(StringComparer.OrdinalIgnoreCase);

    private readonly ILogger<JsonValidatorService> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IPathParsingService _pathParsingService;
    private readonly IRequestProcessingService _requestProcessingService;
    private readonly ISchemaResolverService _schemaResolverService;
    private readonly bool _externalSchemaUriCacheEnabled;
    private readonly TimeSpan _externalSchemaUriCacheTtl;

    public JsonValidatorService(
        ILogger<JsonValidatorService> logger,
        IHttpClientFactory httpClientFactory,
        IPathParsingService pathParsingService,
        IRequestProcessingService requestProcessingService,
        ISchemaResolverService schemaResolverService,
        IOptions<CacheOptions>? cacheOptions = null)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _pathParsingService = pathParsingService;
        _requestProcessingService = requestProcessingService;
        _schemaResolverService = schemaResolverService;

        var effectiveCacheOptions = cacheOptions?.Value;
        _externalSchemaUriCacheEnabled = effectiveCacheOptions?.Enabled ?? true;
        _externalSchemaUriCacheTtl = effectiveCacheOptions != null && effectiveCacheOptions.ExpirationMinutes > 0
            ? TimeSpan.FromMinutes(effectiveCacheOptions.ExpirationMinutes)
            : TimeSpan.FromHours(2);
    }

    public async Task<ValidationResult> ValidateAsync(ValidationRequest request, CancellationToken cancellationToken = default)
    {
        return await _requestProcessingService.ExecuteWithConcurrencyControlAsync(
            ct => ValidateCoreAsync(request, ct),
            request.Options,
            cancellationToken);
    }

    public async Task<ValidationResult> ValidateWithSchemaUriAsync(object jsonData, string schemaUri, ValidationOptions? options = null, CancellationToken cancellationToken = default)
    {
        var request = new ValidationRequest
        {
            JsonData = jsonData,
            SchemaUri = schemaUri,
            Options = options
        };

        return await _requestProcessingService.ExecuteWithConcurrencyControlAsync(
            ct => ValidateCoreAsync(request, ct),
            options,
            cancellationToken);
    }

    public async Task<bool> IsValidAsync(ValidationRequest request, CancellationToken cancellationToken = default)
    {
        var result = await _requestProcessingService.ExecuteWithConcurrencyControlAsync(
            ct => ValidateCoreAsync(request, ct),
            request.Options,
            cancellationToken);
        return result.IsValid;
    }

    private async Task<ValidationResult> ValidateCoreAsync(ValidationRequest request, CancellationToken cancellationToken)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var result = new ValidationResult();

        try
        {
            _logger.StartingJsonValidation();

            // Create timeout token
            using var timeoutCts = _requestProcessingService.CreateTimeoutToken(request.Options, cancellationToken);
            var effectiveToken = timeoutCts.Token;

            // Get JSON data and schema concurrently if possible
            var dataTask = GetJsonDataAsync(request, effectiveToken);
            var schemaTask = GetSchemaAsync(request, effectiveToken);

            var jsonDataDoc = await dataTask;
            var schema = await schemaTask;

            // Fail fast: check for required root properties (example: "type")
            if (schema.Required != null && schema.Required.Count > 0)
            {
                foreach (var requiredProp in schema.Required)
                {
                    if (!jsonDataDoc.RootElement.TryGetProperty(requiredProp, out _))
                    {
                        result.IsValid = false;
                        result.Errors.Add(new ValidationError
                        {
                            Path = requiredProp,
                            Message = $"Missing required property: {requiredProp}",
                            ErrorCode = "MISSING_REQUIRED_PROPERTY",
                            Severity = "Error"
                        });
                        // Fail fast: stop further validation
                        return result;
                    }
                }
            }

            // Selective parsing: only validate properties present in schema
            var validationErrors = await ValidateJsonAgainstSchemaAsync(jsonDataDoc, schema, request.Options);

            // Report additional fields if requested
            if (request.Options?.ReportAdditionalFields == true)
            {
                var jsonDataString = jsonDataDoc.RootElement.GetRawText();
                var additionalFieldWarnings = DetectAdditionalFields(jsonDataString, schema);
                validationErrors.AddRange(additionalFieldWarnings);
            }

            // Build result - only count Error severity as validation failures
            result.IsValid = !validationErrors.Any(e => e.Severity == "Error");
            result.Errors = validationErrors;
            result.SchemaVersion = "2020-12";
            result.Metadata = new CommonValidationMetadata
            {
                SchemaTitle = GetSchemaTitle(request, schema),
                SchemaDescription = GetSchemaDescription(request, schema),
                DataSize = jsonDataDoc.RootElement.GetRawText().Length,
                ValidationTimestamp = DateTime.UtcNow,
                DataSource = !string.IsNullOrEmpty(request.DataUrl) ? request.DataUrl : "direct"
            };

            _logger.JsonValidationCompleted(result.IsValid, result.Errors.Count);
        }
        catch (JsonStructureViolationException ex)
        {
            result.IsValid = false;
            result.Errors.Add(MapJsonStructureViolationToValidationError(ex));
        }
        catch (ArgumentException ex)
        {
            _logger.InvalidArgumentDuringJsonValidation(ex);
            throw;
        }
        catch (InvalidOperationException ex)
        {
            _logger.InvalidOperationDuringJsonValidation(ex);
            throw;
        }
        catch (Exception ex)
        {
            _logger.UnexpectedErrorDuringJsonValidation(ex);
            result.IsValid = false;
            result.Errors.Add(new ValidationError
            {
                Path = "",
                Message = $"Validation failed: {TextSanitizer.SanitizeExceptionMessage(ex.Message)}",
                ErrorCode = "VALIDATION_ERROR",
                Severity = "Error"
            });
        }
        finally
        {
            stopwatch.Stop();
            result.Duration = stopwatch.Elapsed;
        }

        return result;
    }

    public async Task<ValidationResult> ValidateSchemaAsync(object schema, CancellationToken cancellationToken = default)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var result = new ValidationResult();

        try
        {
            _logger.StartingSchemaValidation();

            var schemaJson = System.Text.Json.JsonSerializer.Serialize(schema);
            var jsonSchema = await _schemaResolverService.CreateSchemaFromJsonAsync(schemaJson, cancellationToken);

            // Basic schema validation
            var schemaValidationErrors = new List<ValidationError>();

            if (jsonSchema.Type == null)
            {
                schemaValidationErrors.Add(new ValidationError
                {
                    Path = "",
                    Message = "Schema should specify a type",
                    ErrorCode = "MISSING_TYPE",
                    Severity = "Warning"
                });
            }

            result.IsValid = !schemaValidationErrors.Any();
            result.Errors = schemaValidationErrors;
            result.SchemaVersion = "2020-12";
            result.Metadata = new CommonValidationMetadata
            {
                SchemaTitle = GetSchemaTitleFromObject(schema) ?? jsonSchema.Title,
                SchemaDescription = GetSchemaDescriptionFromObject(schema) ?? jsonSchema.Description,
                ValidationTimestamp = DateTime.UtcNow
            };

            _logger.SchemaValidationCompleted(result.IsValid);
        }
        catch (Exception ex)
        {
            _logger.ErrorDuringSchemaValidation(ex);
            result.IsValid = false;
            result.Errors.Add(new ValidationError
            {
                Path = "",
                Message = $"Schema validation failed: {TextSanitizer.SanitizeExceptionMessage(ex.Message)}",
                ErrorCode = "SCHEMA_VALIDATION_ERROR",
                Severity = "Error"
            });
        }
        finally
        {
            stopwatch.Stop();
            result.Duration = stopwatch.Elapsed;
        }

        return result;
    }

    private async Task<System.Text.Json.JsonDocument> GetJsonDataAsync(ValidationRequest request, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(request.DataUrl))
        {
            return await FetchJsonDataFromUrlAsync(request.DataUrl, request.Options, cancellationToken);
        }
        else if (request.JsonData is string jsonString)
        {
            try
            {
                return System.Text.Json.JsonDocument.Parse(jsonString);
            }
            catch (System.Text.Json.JsonException ex) when (IsCycleOrDepthViolation(ex))
            {
                _logger.UserJsonCycleOrDepthLimitExceeded(ex, MaxAllowedJsonDepth);
                throw new JsonStructureViolationException(JsonStructureViolationSource.UserProvidedJson, ex);
            }
        }
        else if (request.JsonData is System.Text.Json.JsonDocument doc)
        {
            return doc;
        }
        else if (request.JsonData != null)
        {
            var options = new System.Text.Json.JsonSerializerOptions
            {
                MaxDepth = MaxAllowedJsonDepth
            };

            try
            {
                var json = System.Text.Json.JsonSerializer.Serialize(request.JsonData, options);
                return System.Text.Json.JsonDocument.Parse(json);
            }
            catch (System.Text.Json.JsonException ex) when (IsCycleOrDepthViolation(ex))
            {
                _logger.UserJsonCycleOrDepthLimitExceeded(ex, MaxAllowedJsonDepth);
                throw new JsonStructureViolationException(JsonStructureViolationSource.UserProvidedJson, ex);
            }
        }
        else
        {
            throw new ArgumentException("Either JsonData or DataUrl must be provided");
        }
    }

    private async Task<JSchema> GetSchemaAsync(ValidationRequest request, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(request.SchemaUri))
        {
            return await LoadSchemaFromUriAsync(request.SchemaUri, request.Options, cancellationToken);
        }
        else if (request.Schema != null)
        {
            return await CreateSchemaFromObjectAsync(request.Schema);
        }
        else
        {
            throw new ArgumentException("Either Schema, SchemaUri, or SchemaId must be provided");
        }
    }

    private async Task<JSchema> LoadSchemaFromUriAsync(string schemaUri, ValidationOptions? options, CancellationToken cancellationToken)
    {
        Uri validatedUri;
        try
        {
            validatedUri = await _pathParsingService.ValidateAndParseSchemaUriAsync(schemaUri, options);
        }
        catch (Exception ex)
        {
            _logger.FailedToLoadSchemaFromUri(ex, schemaUri);
            throw new InvalidOperationException($"Failed to load schema from URI: {schemaUri}", ex);
        }

        var normalizedSchemaUri = validatedUri.ToString();

        if (TryGetCachedSchemaJson(normalizedSchemaUri, out var cachedSchemaJson))
        {
            _logger.UsingCachedSchemaDocument(normalizedSchemaUri);
            try
            {
                return await _schemaResolverService.CreateSchemaFromJsonAsync(cachedSchemaJson, normalizedSchemaUri, null, cancellationToken);
            }
            catch (System.Text.Json.JsonException ex) when (IsCycleOrDepthViolation(ex))
            {
                _logger.CachedSchemaCycleOrDepthLimitExceeded(ex, normalizedSchemaUri, MaxAllowedJsonDepth);
                throw new JsonStructureViolationException(JsonStructureViolationSource.CachedSchema, ex);
            }
        }

        return await _requestProcessingService.ExecuteWithRetryAsync(async (ct) =>
        {
            try
            {
                _logger.LoadingSchemaFromUri(normalizedSchemaUri);

                var httpClient = _httpClientFactory.CreateClient();
                var response = await httpClient.GetAsync(validatedUri, ct);
            _ = response.EnsureSuccessStatusCode();
                var schemaJson = await response.Content.ReadAsStringAsync(ct);

                if (_externalSchemaUriCacheEnabled)
                {
                    ExternalSchemaUriCache[normalizedSchemaUri] = new CachedExternalSchemaDocument(
                        schemaJson,
                        DateTime.UtcNow.Add(_externalSchemaUriCacheTtl));
                }

                // Pass the validated URI as documentUri so JSchemaUrlResolver can resolve relative references
                return await _schemaResolverService.CreateSchemaFromJsonAsync(schemaJson, normalizedSchemaUri, null, ct);
            }
            catch (Exception ex)
            {
                _logger.FailedToLoadSchemaFromUriRetry(ex, normalizedSchemaUri);
                throw new InvalidOperationException($"Failed to load schema from URI: {normalizedSchemaUri}", ex);
            }
        }, options, cancellationToken);
    }

    private bool TryGetCachedSchemaJson(string schemaUri, out string schemaJson)
    {
        schemaJson = string.Empty;

        if (!_externalSchemaUriCacheEnabled)
        {
            return false;
        }

        if (!ExternalSchemaUriCache.TryGetValue(schemaUri, out var cachedEntry))
        {
            return false;
        }

        if (cachedEntry.ExpiresAtUtc <= DateTime.UtcNow || string.IsNullOrWhiteSpace(cachedEntry.SchemaJson))
        {
      _ = ExternalSchemaUriCache.TryRemove(schemaUri, out _);
            return false;
        }

        schemaJson = cachedEntry.SchemaJson;
        return true;
    }

    private sealed record CachedExternalSchemaDocument(string SchemaJson, DateTime ExpiresAtUtc);

    private async Task<JSchema> CreateSchemaFromObjectAsync(object schema)
    {
        try
        {
            var schemaJson = System.Text.Json.JsonSerializer.Serialize(schema, new System.Text.Json.JsonSerializerOptions
            {
                MaxDepth = MaxAllowedJsonDepth
            });
            return await _schemaResolverService.CreateSchemaFromJsonAsync(schemaJson);
        }
        catch (System.Text.Json.JsonException ex) when (IsCycleOrDepthViolation(ex))
        {
            _logger.UserSchemaCycleOrDepthLimitExceeded(ex, MaxAllowedJsonDepth);
            throw new JsonStructureViolationException(JsonStructureViolationSource.UserProvidedSchema, ex);
        }
        catch (Exception ex)
        {
            _logger.FailedToCreateSchemaFromObject(ex);
            throw new InvalidOperationException("Failed to create schema from object", ex);
        }
    }

    private static bool IsCycleOrDepthViolation(System.Text.Json.JsonException exception)
    {
        var message = exception.Message;
        return message.Contains("possible object cycle", StringComparison.OrdinalIgnoreCase)
            || message.Contains("maximum allowed depth", StringComparison.OrdinalIgnoreCase)
            || message.Contains("depth", StringComparison.OrdinalIgnoreCase);
    }

    private static ValidationError MapJsonStructureViolationToValidationError(JsonStructureViolationException exception)
    {
        var source = exception.SourceType switch
        {
            JsonStructureViolationSource.UserProvidedJson => "user provided JSON",
            JsonStructureViolationSource.CachedSchema => "cached schema",
            JsonStructureViolationSource.UserProvidedSchema => "user provided schema",
            _ => "JSON payload"
        };

        var code = exception.SourceType switch
        {
            JsonStructureViolationSource.UserProvidedJson => "JSON_STRUCTURE_VIOLATION",
            JsonStructureViolationSource.CachedSchema => "CACHED_SCHEMA_STRUCTURE_VIOLATION",
            JsonStructureViolationSource.UserProvidedSchema => "SCHEMA_STRUCTURE_VIOLATION",
            _ => "JSON_STRUCTURE_VIOLATION"
        };

        return new ValidationError
        {
            Path = "$",
            Message = $"Validation failed: {source} contains circular references or exceeds maximum depth of {MaxAllowedJsonDepth}.",
            ErrorCode = code,
            Severity = "Error"
        };
    }

    private enum JsonStructureViolationSource
    {
        UserProvidedJson,
        CachedSchema,
        UserProvidedSchema
    }

    private sealed class JsonStructureViolationException : Exception
    {
        public JsonStructureViolationException(JsonStructureViolationSource sourceType, Exception innerException)
            : base("JSON structure violates cycle/depth constraints.", innerException)
        {
            SourceType = sourceType;
        }

        public JsonStructureViolationSource SourceType { get; }
    }

    private Task<List<ValidationError>> ValidateJsonAgainstSchemaAsync(System.Text.Json.JsonDocument jsonDataDoc, JSchema schema, ValidationOptions? options)
    {
        var errors = new List<ValidationError>();
        var maxErrors = options?.MaxErrors ?? 100;

        try
        {
            // Convert JsonDocument to JObject for schema validation (Newtonsoft)
            var jsonString = jsonDataDoc.RootElement.GetRawText();
            var jsonToken = JToken.Parse(jsonString);

            // Only validate properties present in schema (selective parsing)
            bool isValid = jsonToken.IsValid(schema, out IList<string> errorMessages);

            if (!isValid)
            {
                // Use JSchema.Validate to get detailed validation errors
                var validationErrors = new List<ValidationError>();
                jsonToken.Validate(schema, (sender, args) =>
                {
                    var isAdditionalProp = args.ValidationError?.ErrorType == ErrorType.AdditionalProperties;
                    validationErrors.Add(new ValidationError
                    {
                        Path = args.Path ?? "",
                        Message = args.Message,
                        ErrorCode = isAdditionalProp ? "ADDITIONAL_FIELD" : "VALIDATION_ERROR",
                        Severity = isAdditionalProp ? "Info" : "Error"
                    });
                });
                errors.AddRange(validationErrors.Take(maxErrors));
            }
        }
        catch (JsonReaderException ex)
        {
            errors.Add(new ValidationError
            {
                Path = "",
                Message = $"Invalid JSON format: {TextSanitizer.SanitizeExceptionMessage(ex.Message)}",
                ErrorCode = "INVALID_JSON",
                Severity = "Error"
            });
        }

        return Task.FromResult(errors);
    }

    private async Task<System.Text.Json.JsonDocument> FetchJsonDataFromUrlAsync(string dataUrl, ValidationOptions? options, CancellationToken cancellationToken)
    {
        return await _requestProcessingService.ExecuteWithRetryAsync(async (ct) =>
        {
            try
            {
                _logger.FetchingJsonDataFromUrl(dataUrl);
                var validatedUri = await _pathParsingService.ValidateAndParseDataUrlAsync(dataUrl, options);
                var httpClient = _httpClientFactory.CreateClient();
                using var request = new HttpRequestMessage(HttpMethod.Get, validatedUri);
                var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                _ = response.EnsureSuccessStatusCode();
                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                return await System.Text.Json.JsonDocument.ParseAsync(stream, cancellationToken: ct);
            }
            catch (HttpRequestException ex)
            {
                _logger.HttpRequestFailedFetchingData(ex, dataUrl);
                throw new InvalidOperationException($"Failed to fetch data from URL: {dataUrl}", ex);
            }
            catch (JsonException ex)
            {
                _logger.InvalidJsonReceived(ex, dataUrl);
                throw new InvalidOperationException($"Invalid JSON received from URL: {dataUrl}", ex);
            }
        }, options, cancellationToken);
    }

    private string? GetSchemaTitle(ValidationRequest request, JSchema schema)
    {
        // First try to get the title from the original schema object
        if (request.Schema != null)
        {
            try
            {
                var jObject = JObject.FromObject(request.Schema);
                var title = jObject["title"]?.ToString();
                if (!string.IsNullOrEmpty(title))
                {
                    return title;
                }
            }
            catch (Exception ex)
            {
                _logger.FailedToExtractTitleFromOriginalSchema(ex);
            }
        }

        // Fall back to JSchema title
        return schema.Title;
    }

    private string? GetSchemaDescription(ValidationRequest request, JSchema schema)
    {
        // First try to get the description from the original schema object
        if (request.Schema != null)
        {
            try
            {
                var jObject = JObject.FromObject(request.Schema);
                var description = jObject["description"]?.ToString();
                if (!string.IsNullOrEmpty(description))
                {
                    return description;
                }
            }
            catch (Exception ex)
            {
                _logger.FailedToExtractDescriptionFromOriginalSchema(ex);
            }
        }

        // Fall back to JSchema description
        return schema.Description;
    }

    private string? GetSchemaTitleFromObject(object? schemaObject)
    {
        if (schemaObject == null) return null;

        try
        {
            var jObject = JObject.FromObject(schemaObject);
            return jObject["title"]?.ToString();
        }
        catch (Exception ex)
        {
            _logger.FailedToExtractTitleFromSchema(ex);
            return null;
        }
    }

    private string? GetSchemaDescriptionFromObject(object? schemaObject)
    {
        if (schemaObject == null) return null;

        try
        {
            var jObject = JObject.FromObject(schemaObject);
            return jObject["description"]?.ToString();
        }
        catch (Exception ex)
        {
            _logger.FailedToExtractDescriptionFromSchema(ex);
            return null;
        }
    }

    /// <summary>
    /// Detects fields in the JSON data that are not defined in the schema.
    /// Returns a list of validation warnings for each additional field found.
    /// Normalizes array index segments (for example "items[0]" -> "items") and returns only unique results.
    /// </summary>
    private List<ValidationError> DetectAdditionalFields(string jsonData, JSchema schema)
    {
        var warnings = new List<ValidationError>();

        try
        {
            var jsonToken = JToken.Parse(jsonData);
            DetectAdditionalFieldsRecursive(jsonToken, schema, "", warnings);

            // Normalize paths and keep only unique warnings by normalized path
            var uniqueWarnings = new Dictionary<string, ValidationError>();

            foreach (var warning in warnings)
            {
                var normalizedPath = ValidationPathNormalizer.NormalizeArrayIndexes(warning.Path);
                if (!uniqueWarnings.ContainsKey(normalizedPath))
                {
                    warning.Path = normalizedPath;
                    warning.Message = BuildAdditionalFieldMessage(normalizedPath);
                    uniqueWarnings[normalizedPath] = warning;
                }
            }

            return uniqueWarnings.Values.ToList();
        }
        catch (Exception ex)
        {
            _logger.ErrorDetectingAdditionalFields(ex);
        }

        return warnings;
    }

    /// <summary>
    /// Recursively traverses the JSON data and schema to detect fields not defined in the schema.
    /// </summary>
    private void DetectAdditionalFieldsRecursive(JToken jsonToken, JSchema schema, string currentPath, List<ValidationError> warnings)
    {
        // Handle objects
        if (jsonToken.Type == JTokenType.Object && jsonToken is JObject jObject)
        {
            // Get the properties defined in the schema
            var schemaProperties = schema.Properties ?? new Dictionary<string, JSchema>();
            var additionalPropertiesAllowed = schema.AllowAdditionalProperties;
            var additionalPropertiesSchema = schema.AdditionalProperties;

            foreach (var property in jObject.Properties())
            {
                var propertyPath = string.IsNullOrEmpty(currentPath) ? property.Name : $"{currentPath}.{property.Name}";

                // Check if this property is defined in the schema
                if (!schemaProperties.ContainsKey(property.Name))
                {
                    // Property not defined in schema - report it
                    warnings.Add(new ValidationError
                    {
                        Path = propertyPath,
                        Message = BuildAdditionalFieldMessage(propertyPath),
                        ErrorCode = "ADDITIONAL_FIELD",
                        Severity = "Info"
                    });
                }

                // Recursively check nested properties if there's a schema definition
                if (schemaProperties.TryGetValue(property.Name, out var propertySchema))
                {
                    DetectAdditionalFieldsRecursive(property.Value, propertySchema, propertyPath, warnings);
                }
                else if (additionalPropertiesSchema != null)
                {
                    // If there's an additionalProperties schema, use it for validation
                    DetectAdditionalFieldsRecursive(property.Value, additionalPropertiesSchema, propertyPath, warnings);
                }
            }
        }
        // Handle arrays
        else if (jsonToken.Type == JTokenType.Array && jsonToken is JArray jArray)
        {
            var itemsSchema = schema.Items?.FirstOrDefault();
            if (itemsSchema != null)
            {
                for (int i = 0; i < jArray.Count; i++)
                {
                    var itemPath = $"{currentPath}[{i}]";
                    DetectAdditionalFieldsRecursive(jArray[i], itemsSchema, itemPath, warnings);
                }
            }
        }
    }

    private static string BuildAdditionalFieldMessage(string path)
    {
        return $"Field '{path}' is not defined in the schema";
    }

}

