using System.Collections.Concurrent;
using System.Reflection;
using Json.Schema;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenReferralApi.Core.Helpers;
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
            using var ownedJsonDataDoc = request.JsonData is not System.Text.Json.JsonDocument
                ? jsonDataDoc
                : null;
            var schema = await schemaTask;

            // Fail fast: check for required root properties (example: "type")
            var requiredProperties = GetRequiredRootProperties(schema.SchemaNode);
            if (requiredProperties.Count > 0)
            {
                foreach (var requiredProp in requiredProperties)
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

            var dataSize = GetJsonDocumentUtf8Size(jsonDataDoc);

            // Selective parsing: only validate properties present in schema
            var validationErrors = await ValidateJsonAgainstSchemaAsync(jsonDataDoc, schema, request.Options);

            // Report additional fields if requested
            if (request.Options?.ReportAdditionalFields == true)
            {
                var additionalFieldWarnings = DetectAdditionalFields(jsonDataDoc.RootElement, schema.SchemaNode);
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
                DataSize = dataSize,
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
            _ = await _schemaResolverService.CreateSchemaFromJsonAsync(schemaJson, cancellationToken);
            var resolvedSchemaJson = await _schemaResolverService.ResolveAsync(schemaJson);
            var effectiveSchemaJson = string.IsNullOrWhiteSpace(resolvedSchemaJson) ? schemaJson : resolvedSchemaJson;
            var schemaDetails = BuildSchemaDetails(effectiveSchemaJson);

            // Basic schema validation
            var schemaValidationErrors = new List<ValidationError>();

            if (!HasRootTypeKeyword(schemaDetails.SchemaNode))
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
                SchemaTitle = GetSchemaTitleFromObject(schema) ?? schemaDetails.Title,
                SchemaDescription = GetSchemaDescriptionFromObject(schema) ?? schemaDetails.Description,
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
                const string sourceIdentifier = "request.jsonData (string)";
                _logger.UserJsonCycleOrDepthLimitExceeded(ex, MaxAllowedJsonDepth, sourceIdentifier);
                throw new JsonStructureViolationException(JsonStructureViolationSource.UserProvidedJson, sourceIdentifier, ex);
            }
        }
        else if (request.JsonData is System.Text.Json.JsonDocument doc)
        {
            return doc;
        }
        else if (request.JsonData is System.Text.Json.Nodes.JsonNode jsonNode)
        {
            // JsonNode maintains parent links, so serializing it as a plain object can trigger
            // false cycle detection; parse from its JSON representation instead.
            return System.Text.Json.JsonDocument.Parse(jsonNode.ToJsonString());
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
                const string sourceIdentifier = "request.jsonData (object)";
                _logger.UserJsonCycleOrDepthLimitExceeded(ex, MaxAllowedJsonDepth, sourceIdentifier);
                var detectedCyclePath = TryFindCyclePath(request.JsonData);
                throw new JsonStructureViolationException(JsonStructureViolationSource.UserProvidedJson, sourceIdentifier, ex, detectedCyclePath);
            }
        }
        else
        {
            throw new ArgumentException("Either JsonData or DataUrl must be provided");
        }
    }

    private async Task<ResolvedSchemaDetails> GetSchemaAsync(ValidationRequest request, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(request.SchemaUri))
        {
            return await LoadSchemaFromUriAsync(request.SchemaUri, request.Options, cancellationToken);
        }
        else if (request.Schema is JsonSchema compiledSchema)
        {
            return BuildSchemaDetails(compiledSchema.ToString() ?? "{}");
        }
        else if (request.Schema is System.Text.Json.Nodes.JsonNode schemaNode)
        {
            return await CreateSchemaFromJsonAsync(schemaNode.ToJsonString());
        }
        else if (request.Schema is System.Text.Json.JsonDocument schemaDocument)
        {
            return await CreateSchemaFromJsonAsync(schemaDocument.RootElement.GetRawText());
        }
        else if (request.Schema is System.Text.Json.JsonElement schemaElement)
        {
            return await CreateSchemaFromJsonAsync(schemaElement.GetRawText());
        }
        else if (request.Schema is string schemaString)
        {
            return await CreateSchemaFromJsonAsync(schemaString);
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

    private async Task<ResolvedSchemaDetails> LoadSchemaFromUriAsync(string schemaUri, ValidationOptions? options, CancellationToken cancellationToken)
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
                var resolvedSchemaJson = await _schemaResolverService.ResolveAsync(cachedSchemaJson, normalizedSchemaUri, null);
                var effectiveSchemaJson = string.IsNullOrWhiteSpace(resolvedSchemaJson) ? cachedSchemaJson : resolvedSchemaJson;
                return BuildSchemaDetails(effectiveSchemaJson);
            }
            catch (System.Text.Json.JsonException ex) when (IsCycleOrDepthViolation(ex))
            {
                _logger.CachedSchemaCycleOrDepthLimitExceeded(ex, normalizedSchemaUri, MaxAllowedJsonDepth);
                throw new JsonStructureViolationException(JsonStructureViolationSource.CachedSchema, normalizedSchemaUri, ex);
            }
        }

        var retryResult = await _requestProcessingService.ExecuteWithRetryAsync(async (ct) =>
        {
            try
            {
                _logger.LoadingSchemaFromUri(normalizedSchemaUri);

                var httpClient = _httpClientFactory.CreateClient();
                using var response = await httpClient.GetAsync(validatedUri, ct);
                _ = response.EnsureSuccessStatusCode();
                var schemaJson = await response.Content.ReadAsStringAsync(ct);

                if (_externalSchemaUriCacheEnabled)
                {
                    PurgeExpiredExternalSchemaEntries();
                    ExternalSchemaUriCache[normalizedSchemaUri] = new CachedExternalSchemaDocument(
                        schemaJson,
                        DateTime.UtcNow.Add(_externalSchemaUriCacheTtl));
                }

                var resolvedSchemaJson = await _schemaResolverService.ResolveAsync(schemaJson, normalizedSchemaUri, null);
                var effectiveSchemaJson = string.IsNullOrWhiteSpace(resolvedSchemaJson) ? schemaJson : resolvedSchemaJson;
                return (object)BuildSchemaDetails(effectiveSchemaJson);
            }
            catch (Exception ex)
            {
                _logger.FailedToLoadSchemaFromUriRetry(ex, normalizedSchemaUri);
                throw new InvalidOperationException($"Failed to load schema from URI: {normalizedSchemaUri}", ex);
            }
        }, options, cancellationToken);

        return (ResolvedSchemaDetails)retryResult;
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

    private static void PurgeExpiredExternalSchemaEntries()
    {
        var now = DateTime.UtcNow;
        foreach (var key in ExternalSchemaUriCache.Keys.ToList())
        {
            if (ExternalSchemaUriCache.TryGetValue(key, out var entry) && entry.ExpiresAtUtc <= now)
            {
                _ = ExternalSchemaUriCache.TryRemove(key, out _);
            }
        }
    }

    private async Task<ResolvedSchemaDetails> CreateSchemaFromObjectAsync(object schema)
    {
        try
        {
            var schemaJson = schema switch
            {
                string schemaString => schemaString,
                System.Text.Json.Nodes.JsonNode schemaNode => schemaNode.ToJsonString(),
                System.Text.Json.JsonDocument schemaDocument => schemaDocument.RootElement.GetRawText(),
                System.Text.Json.JsonElement schemaElement => schemaElement.GetRawText(),
                _ => System.Text.Json.JsonSerializer.Serialize(schema, new System.Text.Json.JsonSerializerOptions
                {
                    MaxDepth = MaxAllowedJsonDepth
                })
            };

            return await CreateSchemaFromJsonAsync(schemaJson);
        }
        catch (System.Text.Json.JsonException ex) when (IsCycleOrDepthViolation(ex))
        {
            const string sourceIdentifier = "request.schema";
            _logger.UserSchemaCycleOrDepthLimitExceeded(ex, MaxAllowedJsonDepth, sourceIdentifier);
            var detectedCyclePath = TryFindCyclePath(schema);
            throw new JsonStructureViolationException(JsonStructureViolationSource.UserProvidedSchema, sourceIdentifier, ex, detectedCyclePath);
        }
        catch (Exception ex)
        {
            _logger.FailedToCreateSchemaFromObject(ex);
            throw new InvalidOperationException("Failed to create schema from object", ex);
        }
    }

    private async Task<ResolvedSchemaDetails> CreateSchemaFromJsonAsync(string schemaJson, string? documentUri = null)
    {
        var resolvedSchemaJson = await _schemaResolverService.ResolveAsync(schemaJson, documentUri, auth: null);
        var effectiveSchemaJson = string.IsNullOrWhiteSpace(resolvedSchemaJson) ? schemaJson : resolvedSchemaJson;
        return BuildSchemaDetails(effectiveSchemaJson);
    }

    private static ResolvedSchemaDetails BuildSchemaDetails(string schemaJson)
    {
        var schemaNode = System.Text.Json.Nodes.JsonNode.Parse(schemaJson);
        if (schemaNode is null)
        {
            throw new InvalidOperationException("Schema JSON could not be parsed");
        }

        // Accept common OpenAPI-style schema keywords by normalizing them to JSON Schema.
        NormalizeSchemaNodeForDialect(schemaNode);

        var normalizedSchemaJson = schemaNode.ToJsonString();
        var builtSchema = JsonSchemaBuild.FromText(normalizedSchemaJson);
        var title = TryReadSchemaStringField(schemaNode, "title");
        var description = TryReadSchemaStringField(schemaNode, "description");
        return new ResolvedSchemaDetails(builtSchema, schemaNode, title, description);
    }

    private static void NormalizeSchemaNodeForDialect(System.Text.Json.Nodes.JsonNode node)
    {
        if (node is System.Text.Json.Nodes.JsonObject obj)
        {
            if (obj.TryGetPropertyValue("example", out var exampleValue))
            {
                if (!obj.ContainsKey("examples"))
                {
                    var examples = new System.Text.Json.Nodes.JsonArray();
                    if (exampleValue != null)
                    {
                        examples.Add(exampleValue.DeepClone());
                    }

                    obj["examples"] = examples;
                }

                _ = obj.Remove("example");
            }

            _ = obj.Remove("name");

            var properties = obj.ToList();
            foreach (var (key, value) in properties)
            {
                if (value == null)
                {
                    continue;
                }

                if (IsSchemaMapKeyword(key) && value is System.Text.Json.Nodes.JsonObject schemaMap)
                {
                    foreach (var (_, mappedSchema) in schemaMap.ToList())
                    {
                        if (mappedSchema != null)
                        {
                            NormalizeSchemaNodeForDialect(mappedSchema);
                        }
                    }

                    continue;
                }

                if (IsSchemaArrayKeyword(key) && value is System.Text.Json.Nodes.JsonArray schemaArray)
                {
                    foreach (var item in schemaArray)
                    {
                        if (item != null)
                        {
                            NormalizeSchemaNodeForDialect(item);
                        }
                    }

                    continue;
                }

                if (IsSchemaObjectKeyword(key) && value is System.Text.Json.Nodes.JsonObject nestedSchema)
                {
                    NormalizeSchemaNodeForDialect(nestedSchema);
                }
            }

            return;
        }

        if (node is System.Text.Json.Nodes.JsonArray array)
        {
            foreach (var item in array)
            {
                if (item != null)
                {
                    NormalizeSchemaNodeForDialect(item);
                }
            }
        }
    }

    private static bool IsSchemaMapKeyword(string keyword)
    {
        return keyword is "properties"
            or "patternProperties"
            or "$defs"
            or "definitions"
            or "dependentSchemas";
    }

    private static bool IsSchemaArrayKeyword(string keyword)
    {
        return keyword is "allOf"
            or "anyOf"
            or "oneOf"
            or "prefixItems";
    }

    private static bool IsSchemaObjectKeyword(string keyword)
    {
        return keyword is "items"
            or "contains"
            or "if"
            or "then"
            or "else"
            or "not"
            or "propertyNames"
            or "additionalProperties"
            or "unevaluatedItems"
            or "unevaluatedProperties"
            or "contentSchema";
    }

    private static string? TryReadSchemaStringField(System.Text.Json.Nodes.JsonNode? node, string fieldName)
    {
        if (node is not System.Text.Json.Nodes.JsonObject obj)
        {
            return null;
        }

        return obj[fieldName]?.GetValue<string>();
    }

    private static int GetJsonDocumentUtf8Size(System.Text.Json.JsonDocument doc)
    {
        using var ms = new MemoryStream();
        using (var writer = new System.Text.Json.Utf8JsonWriter(ms))
        {
            doc.RootElement.WriteTo(writer);
        }
        return (int)ms.Length;
    }

    private static bool IsCycleOrDepthViolation(System.Text.Json.JsonException exception)
    {
        var message = exception.Message;
        return message.Contains("possible object cycle", StringComparison.OrdinalIgnoreCase)
            || message.Contains("maximum allowed depth", StringComparison.OrdinalIgnoreCase)
            || message.Contains("depth", StringComparison.OrdinalIgnoreCase);
    }

    private static string? TryFindCyclePath(object? root)
    {
        if (root == null || IsLeafValue(root.GetType()))
        {
            return null;
        }

        var stack = new Dictionary<object, string>(ReferenceEqualityComparer.Instance)
        {
            [root] = "$"
        };

        return TryFindCyclePathRecursive(root, "$", stack, depth: 0, visitedCount: 1);
    }

    private static string? TryFindCyclePathRecursive(object current, string currentPath, Dictionary<object, string> stack, int depth, int visitedCount)
    {
        const int maxTraversalDepth = 256;
        const int maxVisitedNodes = 100_000;

        if (depth >= maxTraversalDepth || visitedCount >= maxVisitedNodes)
        {
            return null;
        }

        foreach (var (segment, child) in EnumerateObjectChildren(current))
        {
            if (child == null)
            {
                continue;
            }

            var childType = child.GetType();
            if (IsLeafValue(childType))
            {
                continue;
            }

            var childPath = BuildChildPath(currentPath, segment, childType);

            if (stack.TryGetValue(child, out var seenPath))
            {
                return $"{childPath} (references {seenPath})";
            }

            stack[child] = childPath;
            var nested = TryFindCyclePathRecursive(child, childPath, stack, depth + 1, visitedCount + 1);
            if (nested != null)
            {
                return nested;
            }

            _ = stack.Remove(child);
        }

        return null;
    }

    private static IEnumerable<(string Segment, object? Value)> EnumerateObjectChildren(object value)
    {
        if (value is System.Collections.IDictionary dictionary)
        {
            foreach (System.Collections.DictionaryEntry entry in dictionary)
            {
                var key = entry.Key?.ToString() ?? "?";
                yield return (key, entry.Value);
            }

            yield break;
        }

        if (value is System.Collections.IEnumerable enumerable && value is not string)
        {
            var i = 0;
            foreach (var item in enumerable)
            {
                yield return ($"[{i}]", item);
                i++;
            }

            yield break;
        }

        foreach (var property in value.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (!property.CanRead || property.GetIndexParameters().Length > 0)
            {
                continue;
            }

            object? propertyValue;
            try
            {
                propertyValue = property.GetValue(value);
            }
            catch
            {
                continue;
            }

            yield return (property.Name, propertyValue);
        }
    }

    private static string BuildChildPath(string parentPath, string segment, Type childType)
    {
        var isArrayIndex = segment.StartsWith("[", StringComparison.Ordinal);
        if (isArrayIndex)
        {
            return $"{parentPath}{segment}";
        }

        if (childType.IsGenericType && childType.GetGenericTypeDefinition() == typeof(KeyValuePair<,>))
        {
            return $"{parentPath}.{segment}";
        }

        return $"{parentPath}.{segment}";
    }

    private static bool IsLeafValue(Type type)
    {
        if (type.IsPrimitive || type.IsEnum)
        {
            return true;
        }

        return type == typeof(string)
            || type == typeof(decimal)
            || type == typeof(DateTime)
            || type == typeof(DateTimeOffset)
            || type == typeof(TimeSpan)
            || type == typeof(Guid)
            || type == typeof(Uri);
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

        var line = ToNullableInt(exception.LineNumber);
        var column = ToNullableInt(exception.BytePositionInLine);
        var location = BuildLocationSuffix(line, column);
        var path = string.IsNullOrWhiteSpace(exception.JsonPath) ? "$" : exception.JsonPath!;
        var sourceIdentifier = string.IsNullOrWhiteSpace(exception.SourceIdentifier)
            ? "unknown"
            : exception.SourceIdentifier;

        var details = exception.ViolationKind switch
        {
            JsonStructureViolationKind.Cycle => "contains a circular reference",
            JsonStructureViolationKind.Depth => $"exceeds maximum depth of {MaxAllowedJsonDepth}",
            _ => $"contains circular references or exceeds maximum depth of {MaxAllowedJsonDepth}"
        };

        return new ValidationError
        {
            Path = path,
            Message = $"Validation failed: {source} {details}. Source: {sourceIdentifier}. JSON path: {path}{location}.",
            ErrorCode = code,
            Severity = "Error",
            LineNumber = line,
            ColumnNumber = column,
            SourceIdentifier = sourceIdentifier
        };
    }

    private static int? ToNullableInt(long? value)
    {
        if (!value.HasValue)
        {
            return null;
        }

        if (value.Value < 0)
        {
            return null;
        }

        if (value.Value > int.MaxValue)
        {
            return int.MaxValue;
        }

        return (int)value.Value;
    }

    private static string BuildLocationSuffix(int? line, int? column)
    {
        if (!line.HasValue && !column.HasValue)
        {
            return string.Empty;
        }

        if (line.HasValue && column.HasValue)
        {
            return $", line {line.Value}, column {column.Value}";
        }

        if (line.HasValue)
        {
            return $", line {line.Value}";
        }

        return $", column {column!.Value}";
    }

    private static JsonStructureViolationKind GetViolationKind(System.Text.Json.JsonException exception)
    {
        var message = exception.Message;

        if (message.Contains("possible object cycle", StringComparison.OrdinalIgnoreCase))
        {
            return JsonStructureViolationKind.Cycle;
        }

        if (message.Contains("maximum allowed depth", StringComparison.OrdinalIgnoreCase)
            || message.Contains("depth", StringComparison.OrdinalIgnoreCase))
        {
            return JsonStructureViolationKind.Depth;
        }

        return JsonStructureViolationKind.Unknown;
    }

    private enum JsonStructureViolationSource
    {
        UserProvidedJson,
        CachedSchema,
        UserProvidedSchema
    }

    private enum JsonStructureViolationKind
    {
        Unknown,
        Cycle,
        Depth
    }

    private sealed class JsonStructureViolationException : Exception
    {
        public JsonStructureViolationException(JsonStructureViolationSource sourceType, string sourceIdentifier, System.Text.Json.JsonException innerException, string? detectedPath = null)
            : base("JSON structure violates cycle/depth constraints.", innerException)
        {
            SourceType = sourceType;
            SourceIdentifier = sourceIdentifier;
            JsonPath = !string.IsNullOrWhiteSpace(detectedPath) ? detectedPath : innerException.Path;
            LineNumber = innerException.LineNumber;
            BytePositionInLine = innerException.BytePositionInLine;
            ViolationKind = GetViolationKind(innerException);
        }

        public JsonStructureViolationSource SourceType { get; }
        public string SourceIdentifier { get; }
        public string? JsonPath { get; }
        public long? LineNumber { get; }
        public long? BytePositionInLine { get; }
        public JsonStructureViolationKind ViolationKind { get; }
    }

    private Task<List<ValidationError>> ValidateJsonAgainstSchemaAsync(System.Text.Json.JsonDocument dataDocument, ResolvedSchemaDetails schema, ValidationOptions? options)
    {
        var errors = new List<ValidationError>();
        var maxErrors = options?.MaxErrors ?? 100;

        try
        {
            var evaluationOptions = new EvaluationOptions
            {
                OutputFormat = OutputFormat.List,
                RequireFormatValidation = options?.ValidateFormat ?? true
            };

            var evaluation = schema.Schema.Evaluate(dataDocument.RootElement, evaluationOptions);
            if (evaluation.IsValid)
            {
                return Task.FromResult(errors);
            }

            errors.AddRange(FlattenEvaluationErrors(evaluation).Take(maxErrors));
        }
        catch (System.Text.Json.JsonException ex)
        {
            errors.Add(new ValidationError
            {
                Path = "",
                Message = $"Invalid JSON format: {TextSanitizer.SanitizeExceptionMessage(ex.Message)}",
                ErrorCode = "INVALID_JSON",
                Severity = "Error"
            });
        }
        catch (RefResolutionException ex)
        {
            errors.Add(new ValidationError
            {
                Path = "",
                Message = $"Schema reference could not be resolved: {TextSanitizer.SanitizeExceptionMessage(ex.Message)}",
                ErrorCode = "SCHEMA_REFERENCE_UNRESOLVED",
                Severity = "Error"
            });
        }
        catch (Exception ex)
        {
            errors.Add(new ValidationError
            {
                Path = "",
                Message = $"Validation failed: {TextSanitizer.SanitizeExceptionMessage(ex.Message)}",
                ErrorCode = "VALIDATION_ERROR",
                Severity = "Error"
            });
        }

        return Task.FromResult(errors);
    }

    private static IEnumerable<ValidationError> FlattenEvaluationErrors(EvaluationResults root)
    {
        var stack = new Stack<EvaluationResults>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            var current = stack.Pop();

            if (current.Errors != null)
            {
                foreach (var error in current.Errors)
                {
                    var evaluationPath = current.EvaluationPath.ToString();
                    var isAdditionalProperty = error.Key.Contains("additionalProperties", StringComparison.OrdinalIgnoreCase)
                        || error.Value.Contains("additional properties", StringComparison.OrdinalIgnoreCase)
                        || evaluationPath.Contains("additionalProperties", StringComparison.OrdinalIgnoreCase);

                    yield return new ValidationError
                    {
                        Path = ConvertJsonPointerToPath(current.InstanceLocation.ToString()),
                        Message = error.Value,
                        ErrorCode = isAdditionalProperty ? "ADDITIONAL_FIELD" : "VALIDATION_ERROR",
                        Severity = isAdditionalProperty ? "Info" : "Error"
                    };
                }
            }

            if (current.Details == null)
            {
                continue;
            }

            for (var i = current.Details.Count - 1; i >= 0; i--)
            {
                stack.Push(current.Details[i]);
            }
        }
    }

    private static string ConvertJsonPointerToPath(string jsonPointer)
    {
        if (string.IsNullOrWhiteSpace(jsonPointer) || jsonPointer == "/")
        {
            return string.Empty;
        }

        var segments = jsonPointer
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(segment => segment.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal));

        var pathBuilder = new System.Text.StringBuilder();
        foreach (var segment in segments)
        {
            if (int.TryParse(segment, out _))
            {
                pathBuilder.Append('[').Append(segment).Append(']');
                continue;
            }

            if (pathBuilder.Length > 0)
            {
                pathBuilder.Append('.');
            }

            pathBuilder.Append(segment);
        }

        return pathBuilder.ToString();
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
                using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                _ = response.EnsureSuccessStatusCode();
                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                return await System.Text.Json.JsonDocument.ParseAsync(stream, cancellationToken: ct);
            }
            catch (HttpRequestException ex)
            {
                _logger.HttpRequestFailedFetchingData(ex, dataUrl);
                throw new InvalidOperationException($"Failed to fetch data from URL: {dataUrl}", ex);
            }
            catch (System.Text.Json.JsonException ex)
            {
                if (IsCycleOrDepthViolation(ex))
                {
                    _logger.UserJsonCycleOrDepthLimitExceeded(ex, MaxAllowedJsonDepth, dataUrl);
                    throw new JsonStructureViolationException(JsonStructureViolationSource.UserProvidedJson, dataUrl, ex);
                }

                _logger.InvalidJsonReceived(ex, dataUrl);
                throw new InvalidOperationException($"Invalid JSON received from URL: {dataUrl}", ex);
            }
        }, options, cancellationToken);
    }

    private string? GetSchemaTitle(ValidationRequest request, ResolvedSchemaDetails schema)
    {
        return TryGetSchemaStringFieldFromObject(request.Schema, "title") ?? schema.Title;
    }

    private string? GetSchemaDescription(ValidationRequest request, ResolvedSchemaDetails schema)
    {
        return TryGetSchemaStringFieldFromObject(request.Schema, "description") ?? schema.Description;
    }

    private string? GetSchemaTitleFromObject(object? schemaObject)
    {
        return TryGetSchemaStringFieldFromObject(schemaObject, "title");
    }

    private string? GetSchemaDescriptionFromObject(object? schemaObject)
    {
        return TryGetSchemaStringFieldFromObject(schemaObject, "description");
    }

    private string? TryGetSchemaStringFieldFromObject(object? schemaObject, string fieldName)
    {
        if (schemaObject == null)
        {
            return null;
        }

        try
        {
            var schemaJson = schemaObject switch
            {
                string schemaString => schemaString,
                System.Text.Json.Nodes.JsonNode schemaNode => schemaNode.ToJsonString(),
                System.Text.Json.JsonDocument schemaDocument => schemaDocument.RootElement.GetRawText(),
                System.Text.Json.JsonElement schemaElement => schemaElement.GetRawText(),
                _ => System.Text.Json.JsonSerializer.Serialize(schemaObject, new System.Text.Json.JsonSerializerOptions
                {
                    MaxDepth = MaxAllowedJsonDepth
                })
            };

            var schemaNodeText = System.Text.Json.Nodes.JsonNode.Parse(schemaJson);
            return TryReadSchemaStringField(schemaNodeText, fieldName);
        }
        catch (Exception ex)
        {
            if (fieldName == "title")
            {
                _logger.FailedToExtractTitleFromSchema(ex);
            }
            else if (fieldName == "description")
            {
                _logger.FailedToExtractDescriptionFromSchema(ex);
            }

            return null;
        }
    }

    /// <summary>
    /// Detects fields in the JSON data that are not defined in the schema.
    /// Returns a list of validation warnings for each additional field found.
    /// Normalizes array index segments (for example "items[0]" -> "items") and returns only unique results.
    /// </summary>
    private List<ValidationError> DetectAdditionalFields(System.Text.Json.JsonElement dataElement, System.Text.Json.Nodes.JsonNode? schemaNode)
    {
        var warnings = new List<ValidationError>();

        try
        {
            DetectAdditionalFieldsRecursive(dataElement, schemaNode, "", warnings);

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
    private void DetectAdditionalFieldsRecursive(System.Text.Json.JsonElement jsonElement, System.Text.Json.Nodes.JsonNode? schemaNode, string currentPath, List<ValidationError> warnings)
    {
        if (jsonElement.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            var schemaObject = schemaNode as System.Text.Json.Nodes.JsonObject;
            var schemaProperties = schemaObject?["properties"] as System.Text.Json.Nodes.JsonObject;
            var additionalPropertiesNode = schemaObject?["additionalProperties"];

            foreach (var property in jsonElement.EnumerateObject())
            {
                var propertyPath = string.IsNullOrEmpty(currentPath) ? property.Name : $"{currentPath}.{property.Name}";
                var hasSchemaProperty = schemaProperties?.ContainsKey(property.Name) == true;

                if (!hasSchemaProperty)
                {
                    warnings.Add(new ValidationError
                    {
                        Path = propertyPath,
                        Message = BuildAdditionalFieldMessage(propertyPath),
                        ErrorCode = "ADDITIONAL_FIELD",
                        Severity = "Info"
                    });
                }

                if (hasSchemaProperty)
                {
                    DetectAdditionalFieldsRecursive(property.Value, schemaProperties![property.Name], propertyPath, warnings);
                }
                else if (additionalPropertiesNode is System.Text.Json.Nodes.JsonObject additionalPropertiesSchema)
                {
                    DetectAdditionalFieldsRecursive(property.Value, additionalPropertiesSchema, propertyPath, warnings);
                }
            }
        }
        else if (jsonElement.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            var schemaObject = schemaNode as System.Text.Json.Nodes.JsonObject;
            var itemSchema = schemaObject?["items"];

            if (itemSchema != null)
            {
                var index = 0;
                foreach (var item in jsonElement.EnumerateArray())
                {
                    var itemPath = $"{currentPath}[{index}]";
                    DetectAdditionalFieldsRecursive(item, itemSchema, itemPath, warnings);
                    index++;
                }
            }
        }
    }

    private static List<string> GetRequiredRootProperties(System.Text.Json.Nodes.JsonNode? schemaNode)
    {
        if (schemaNode is not System.Text.Json.Nodes.JsonObject schemaObject
            || schemaObject["required"] is not System.Text.Json.Nodes.JsonArray requiredArray)
        {
            return [];
        }

        return requiredArray
            .Select(static item => item?.GetValue<string>())
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Cast<string>()
            .ToList();
    }

    private static bool HasRootTypeKeyword(System.Text.Json.Nodes.JsonNode? schemaNode)
    {
        return schemaNode is System.Text.Json.Nodes.JsonObject schemaObject
            && schemaObject.ContainsKey("type");
    }

    private static string BuildAdditionalFieldMessage(string path)
    {
        return $"Field '{path}' is not defined in the schema";
    }

    private sealed record ResolvedSchemaDetails(
        JsonSchema Schema,
        System.Text.Json.Nodes.JsonNode? SchemaNode,
        string? Title,
        string? Description);

}

