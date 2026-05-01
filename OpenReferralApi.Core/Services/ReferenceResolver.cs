using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using OpenReferralApi.Core.Helpers;
using OpenReferralApi.Core.Logging;

namespace OpenReferralApi.Core.Services;

/// <summary>
/// Internal helper class for resolving JSON Schema $ref references.
/// Handles both external and internal reference resolution with circular reference detection.
/// </summary>
public class ReferenceResolver
{
    private const string CircularReferenceErrorCode = "CIRCULAR_SCHEMA_REFERENCE";
    private readonly ILogger _logger;
    private readonly RemoteSchemaLoader _remoteSchemaLoader;
    private readonly Dictionary<string, JsonNode?> _refCache = new();
    private readonly List<SchemaResolutionIssue> _resolutionIssues = new();
    private JsonNode? _rootDocument;
    private string? _baseUri;

    public IReadOnlyList<SchemaResolutionIssue> ResolutionIssues => _resolutionIssues;

    public ReferenceResolver(
        ILogger logger,
        RemoteSchemaLoader remoteSchemaLoader)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _remoteSchemaLoader = remoteSchemaLoader ?? throw new ArgumentNullException(nameof(remoteSchemaLoader));
    }

    /// <summary>
    /// Initializes the resolver for a new resolution session.
    /// </summary>
    public void Initialize(JsonNode? rootDocument, string? baseUri)
    {
        _refCache.Clear();
        _resolutionIssues.Clear();
        _rootDocument = rootDocument;
        _baseUri = baseUri;
    }

    /// <summary>
    /// Resolves all $ref references in the provided JSON node recursively.
    /// </summary>
    public async Task<JsonNode?> ResolveAllRefsAsync(JsonNode? obj, HashSet<string> visitedRefs, List<string>? referencePath = null)
    {
        referencePath ??= new List<string>();

        if (obj == null)
        {
            return null;
        }

        if (obj is JsonValue)
        {
            return obj.DeepClone();
        }

        if (obj is JsonArray jsonArray)
        {
            var resultArray = new JsonArray();
            foreach (var item in jsonArray)
            {
                var resolved = await ResolveAllRefsAsync(item, visitedRefs, referencePath);
                resultArray.Add(resolved);
            }
            return resultArray;
        }

        if (obj is JsonObject jsonObject)
        {
            // If this object has a $ref, resolve it
            if (jsonObject.TryGetPropertyValue("$ref", out var refNode) &&
                refNode is JsonValue refValue)
            {
                var refString = refValue.GetValue<string>();
                JsonNode? resolved;

                if (IsExternalSchemaRef(refString))
                {
                    // Resolve external URL reference
                    resolved = await ResolveRefAsync(refString, visitedRefs, referencePath);
                }
                else if (IsInternalRef(refString))
                {
                    // Resolve internal JSON pointer reference
                    resolved = await ResolveInternalRefAsync(refString, visitedRefs, referencePath);

                    // If internal reference resolution failed (returned null), keep the original $ref
                    // This prevents null values from being inserted into schema structures like allOf arrays
                    // where Newtonsoft.Json.Schema cannot handle them
                    if (resolved == null)
                    {
                        _logger.CouldNotResolveInternalReference(refString);
                        return obj.DeepClone();
                    }
                }
                else if (IsLocalSchemaRef(refString))
                {
                    // Resolve local file or relative path reference
                    resolved = await ResolveRefAsync(refString, visitedRefs, referencePath);
                }
                else
                {
                    // Keep the reference as-is if we can't identify it
                    return obj.DeepClone();
                }

                // Merge other properties if they exist (besides $ref)
                var otherProps = jsonObject.Where(kvp => kvp.Key != "$ref").ToList();

                if (otherProps.Any() && resolved is JsonObject resolvedObject)
                {
                    var merged = new JsonObject();

                    // Add resolved properties first
                    foreach (var kvp in resolvedObject)
                    {
                        merged[kvp.Key] = await ResolveAllRefsAsync(kvp.Value, visitedRefs, referencePath);
                    }

                    // Add/override with other properties
                    foreach (var kvp in otherProps)
                    {
                        merged[kvp.Key] = await ResolveAllRefsAsync(kvp.Value, visitedRefs, referencePath);
                    }

                    return merged;
                }

                return resolved;
            }

            // Otherwise, recursively resolve all properties
            var result = new JsonObject();
            foreach (var kvp in jsonObject)
            {
                result[kvp.Key] = await ResolveAllRefsAsync(kvp.Value, visitedRefs, referencePath);
            }

            // Flatten resolved allOf properties to make composite fields discoverable.
            MergeAllOfIntoObject(result);

            return result;
        }

        return obj;
    }

    /// <summary>
    /// Resolves an internal JSON pointer reference (e.g., #/definitions/Person).
    /// </summary>
    private async Task<JsonNode?> ResolveInternalRefAsync(string refPointer, HashSet<string> visitedRefs, List<string> referencePath)
    {
        if (_rootDocument == null)
        {
            _logger.CannotResolveWithoutRootDocument(refPointer);
            return null;
        }

        if (refPointer == "#")
        {
            return await ResolveAllRefsAsync(_rootDocument, visitedRefs, referencePath);
        }

        var resolvedRefKey = CreateInternalReferenceKey(refPointer);

        // Check for circular references
        if (visitedRefs.Contains(resolvedRefKey))
        {
            RecordCircularReference(resolvedRefKey, refPointer, referencePath, isExternal: false);
            return new JsonObject { ["$ref"] = refPointer };
        }
        // Check cache first
        if (_refCache.TryGetValue(resolvedRefKey, out var cached))
        {
            return cached?.DeepClone();
        }

        _ = visitedRefs.Add(resolvedRefKey);
        referencePath.Add(resolvedRefKey);

        try
        {
            JsonNode? current;

            // JSON Pointer (RFC 6901)
            if (refPointer.StartsWith("#/", StringComparison.Ordinal))
            {
                var pointer = refPointer.TrimStart('#', '/');
                var parts = pointer.Split('/');

                current = _rootDocument;
                foreach (var part in parts)
                {
                    if (string.IsNullOrEmpty(part))
                    {
                        continue;
                    }

                    var unescapedPart = UnescapeJsonPointer(part);

                    if (current is JsonObject jsonObj)
                    {
                        if (!jsonObj.TryGetPropertyValue(unescapedPart, out current) || current == null)
                        {
                            _logger.FailedToResolveInternalReferencePath(refPointer, unescapedPart);
                            return null;
                        }
                    }
                    else if (current is JsonArray jsonArr)
                    {
                        if (int.TryParse(unescapedPart, out var index) && index >= 0 && index < jsonArr.Count)
                        {
                            current = jsonArr[index];
                        }
                        else
                        {
                            _logger.InvalidArrayIndexInReference(refPointer, unescapedPart);
                            return null;
                        }
                    }
                    else
                    {
                        _logger.CannotNavigateThroughNonObject(refPointer);
                        return null;
                    }
                }
            }
            else
            {
                // Anchor fragment (e.g. #meta). Supports $anchor and $dynamicAnchor.
                var anchorName = refPointer.TrimStart('#');
                if (string.IsNullOrWhiteSpace(anchorName))
                {
                    return null;
                }

                current = FindAnchorNode(_rootDocument, anchorName);
                if (current == null)
                {
                    _logger.FailedToResolveAnchorReference(refPointer);
                    return null;
                }
            }

            // Recursively resolve the referenced schema
            var resolved = await ResolveAllRefsAsync(current, visitedRefs, referencePath);

            // Cache the resolved value
            _refCache[resolvedRefKey] = resolved;

            return resolved?.DeepClone();
        }
        finally
        {
            _ = visitedRefs.Remove(resolvedRefKey);
            if (referencePath.Count > 0)
            {
                referencePath.RemoveAt(referencePath.Count - 1);
            }
        }
    }

    /// <summary>
    /// Resolves an external URL reference (e.g., https://example.com/schema.json#/definitions/Person).
    /// </summary>
    private async Task<JsonNode?> ResolveRefAsync(string refUrl, HashSet<string> visitedRefs, List<string> referencePath)
    {
        // Split URL and fragment
        var parts = refUrl.Split('#');
        var schemaUrl = parts[0];
        var fragment = parts.Length > 1 ? $"#{parts[1]}" : string.Empty;

        var schemaLocation = ResolveSchemaLocation(schemaUrl);
        var resolvedRefLocation = string.IsNullOrEmpty(fragment)
            ? schemaLocation
            : $"{schemaLocation}{fragment}";
        var resolvedRefKey = CreateExternalReferenceKey(resolvedRefLocation);

        // Check for circular references
        if (visitedRefs.Contains(resolvedRefKey))
        {
            RecordCircularReference(resolvedRefKey, refUrl, referencePath, isExternal: true);
            return new JsonObject { ["$ref"] = refUrl };
        }

        _ = visitedRefs.Add(resolvedRefKey);
        referencePath.Add(resolvedRefKey);

        try
        {
            // Check cache first
            if (_refCache.TryGetValue(resolvedRefKey, out var cached))
            {
                return cached?.DeepClone();
            }

            var schema = await LoadSchemaAsync(schemaLocation);

            if (schema == null)
            {
                _logger.FailedToLoadSchema(TextSanitizer.SanitizeStringForLogging(schemaLocation));
                return null;
            }

            JsonNode? resolved;

            // If there's a fragment, resolve it within the loaded schema
            if (!string.IsNullOrEmpty(fragment))
            {
                var previousRoot = _rootDocument;
                var previousBaseUri = _baseUri;

                try
                {
                    _rootDocument = schema;
                    _baseUri = schemaLocation;
                    resolved = await ResolveInternalRefAsync(fragment, visitedRefs, referencePath);
                }
                finally
                {
                    _rootDocument = previousRoot;
                    _baseUri = previousBaseUri;
                }
            }
            else
            {
                // Recursively resolve references within the loaded schema
                var previousRoot = _rootDocument;
                var previousBaseUri = _baseUri;

                try
                {
                    _rootDocument = schema;
                    _baseUri = schemaLocation;
                    resolved = await ResolveAllRefsAsync(schema, visitedRefs, referencePath);
                }
                finally
                {
                    _rootDocument = previousRoot;
                    _baseUri = previousBaseUri;
                }
            }

            // Cache the resolved value
            _refCache[resolvedRefKey] = resolved;

            return resolved?.DeepClone();
        }
        finally
        {
            _ = visitedRefs.Remove(resolvedRefKey);
            if (referencePath.Count > 0)
            {
                referencePath.RemoveAt(referencePath.Count - 1);
            }
        }
    }

    private string CreateInternalReferenceKey(string refPointer)
    {
        var documentKey = string.IsNullOrWhiteSpace(_baseUri) ? "root" : _baseUri;
        return $"int::{documentKey}{refPointer}";
    }

    private static string CreateExternalReferenceKey(string refLocation)
    {
        return $"ext::{refLocation}";
    }

    private static string ToDisplayReference(string refKey)
    {
        if (refKey.StartsWith("int::", StringComparison.Ordinal) ||
            refKey.StartsWith("ext::", StringComparison.Ordinal))
        {
            return refKey[5..];
        }

        return refKey;
    }

    private void RecordCircularReference(string seenRefKey, string originalReference, IReadOnlyList<string> referencePath, bool isExternal)
    {
        var cyclePath = BuildCyclePath(referencePath, seenRefKey);
        var cyclePathText = string.Join(" -> ", cyclePath.Select(ToDisplayReference).Select(TextSanitizer.SanitizeStringForLogging));
        var safeReference = TextSanitizer.SanitizeStringForLogging(originalReference);

        if (isExternal)
        {
            _logger.CircularExternalReferenceDetectedWithPath(safeReference, cyclePathText);
        }
        else
        {
            _logger.CircularReferenceDetectedWithPath(safeReference, cyclePathText);
        }

        _resolutionIssues.Add(new SchemaResolutionIssue
        {
            ErrorCode = CircularReferenceErrorCode,
            Message = "Circular schema reference detected; nested dependency resolution stopped at the repeated reference.",
            Reference = originalReference,
            ReferencePath = string.Join(" -> ", cyclePath.Select(ToDisplayReference))
        });
    }

    private static IReadOnlyList<string> BuildCyclePath(IReadOnlyList<string> referencePath, string repeatedRef)
    {
        var startIndex = -1;
        for (var i = 0; i < referencePath.Count; i++)
        {
            if (string.Equals(referencePath[i], repeatedRef, StringComparison.Ordinal))
            {
                startIndex = i;
                break;
            }
        }

        if (startIndex < 0)
        {
            return new List<string> { repeatedRef, repeatedRef };
        }

        var cycle = new List<string>();
        for (var i = startIndex; i < referencePath.Count; i++)
        {
            cycle.Add(referencePath[i]);
        }

        cycle.Add(repeatedRef);
        return cycle;
    }

    /// <summary>
    /// Checks if a reference string points to an external schema URL.
    /// </summary>
    private static bool IsExternalSchemaRef(string refString)
    {
        return refString.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
               refString.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Checks if a reference string points to a local schema file path.
    /// </summary>
    private static bool IsLocalSchemaRef(string refString)
    {
        if (string.IsNullOrWhiteSpace(refString))
        {
            return false;
        }

        var schemaPart = refString.Split('#')[0];
        if (string.IsNullOrWhiteSpace(schemaPart))
        {
            return false;
        }

        if (Uri.TryCreate(schemaPart, UriKind.Absolute, out var absoluteUri))
        {
            return absoluteUri.Scheme == Uri.UriSchemeFile;
        }

        return Path.IsPathRooted(schemaPart) ||
               !schemaPart.Contains("://", StringComparison.Ordinal);
    }

    /// <summary>
    /// Checks if a reference string is an internal JSON pointer.
    /// </summary>
    private static bool IsInternalRef(string refString)
    {
        return refString.StartsWith("#", StringComparison.Ordinal);
    }

    /// <summary>
    /// Finds a node by JSON Schema anchor name ($anchor or $dynamicAnchor).
    /// </summary>
    private static JsonNode? FindAnchorNode(JsonNode? node, string anchorName)
    {
        if (node == null)
        {
            return null;
        }

        if (node is JsonObject jsonObject)
        {
            if (HasMatchingAnchor(jsonObject, "$anchor", anchorName) ||
                HasMatchingAnchor(jsonObject, "$dynamicAnchor", anchorName))
            {
                return jsonObject;
            }

            foreach (var kvp in jsonObject)
            {
                var found = FindAnchorNode(kvp.Value, anchorName);
                if (found != null)
                {
                    return found;
                }
            }
        }
        else if (node is JsonArray jsonArray)
        {
            foreach (var item in jsonArray)
            {
                var found = FindAnchorNode(item, anchorName);
                if (found != null)
                {
                    return found;
                }
            }
        }

        return null;
    }

    private static bool HasMatchingAnchor(JsonObject node, string propertyName, string anchorName)
    {
        if (!node.TryGetPropertyValue(propertyName, out var anchorNode) ||
            anchorNode is not JsonValue anchorValue)
        {
            return false;
        }

        return string.Equals(anchorValue.GetValue<string>(), anchorName, StringComparison.Ordinal);
    }

    /// <summary>
    /// Resolves a schema location against the current base URI/path.
    /// </summary>
    private string ResolveSchemaLocation(string schemaRef)
    {
        if (string.IsNullOrWhiteSpace(schemaRef))
        {
            return _baseUri ?? string.Empty;
        }

        if (Uri.TryCreate(schemaRef, UriKind.Absolute, out var absoluteUri))
        {
            return absoluteUri.Scheme == Uri.UriSchemeFile
                ? absoluteUri.LocalPath
                : schemaRef;
        }

        if (string.IsNullOrWhiteSpace(_baseUri))
        {
            return Path.GetFullPath(schemaRef);
        }

        if (Uri.TryCreate(_baseUri, UriKind.Absolute, out var baseUri))
        {
            if (Uri.TryCreate(baseUri, schemaRef, out var resolvedUri))
            {
                return resolvedUri.Scheme == Uri.UriSchemeFile
                    ? resolvedUri.LocalPath
                    : resolvedUri.ToString();
            }
        }

        var basePath = _baseUri;
        if (!string.IsNullOrEmpty(Path.GetExtension(basePath)))
        {
            basePath = Path.GetDirectoryName(basePath) ?? basePath;
        }

        return Path.GetFullPath(Path.Combine(basePath, schemaRef));
    }

    /// <summary>
    /// Loads a schema from HTTP(S) or a local file path.
    /// </summary>
    private async Task<JsonNode?> LoadSchemaAsync(string schemaLocation)
    {
        if (Uri.TryCreate(schemaLocation, UriKind.Absolute, out var schemaUri) &&
            (schemaUri.Scheme == Uri.UriSchemeHttp || schemaUri.Scheme == Uri.UriSchemeHttps))
        {
            return await _remoteSchemaLoader.LoadRemoteSchemaAsync(schemaLocation);
        }

        var localPath = schemaLocation;
        if (Uri.TryCreate(schemaLocation, UriKind.Absolute, out var fileUri) &&
            fileUri.Scheme == Uri.UriSchemeFile)
        {
            localPath = fileUri.LocalPath;
        }

        if (!File.Exists(localPath))
        {
            _logger.SchemaFileNotFound(TextSanitizer.SanitizeStringForLogging(localPath));
            return null;
        }

        try
        {
            var content = await File.ReadAllTextAsync(localPath);
            return JsonNode.Parse(content);
        }
        catch (Exception ex)
        {
            _logger.FailedToLoadLocalSchemaFile(ex, TextSanitizer.SanitizeStringForLogging(localPath));
            throw;
        }
    }

    /// <summary>
    /// Unescapes a JSON pointer token according to RFC 6901.
    /// </summary>
    private static string UnescapeJsonPointer(string token)
    {
        return token.Replace("~1", "/").Replace("~0", "~");
    }

    /// <summary>
    /// Merges allOf properties into the target object to make composite fields discoverable.
    /// </summary>
    private static void MergeAllOfIntoObject(JsonObject target)
    {
        if (!target.TryGetPropertyValue("allOf", out var allOfNode) || allOfNode is not JsonArray allOfArray)
        {
            return;
        }

        JsonObject? targetProperties = null;
        if (target.TryGetPropertyValue("properties", out var propsNode) && propsNode is JsonObject propsObject)
        {
            targetProperties = propsObject;
        }

        JsonArray? targetRequired = null;
        if (target.TryGetPropertyValue("required", out var requiredNode) && requiredNode is JsonArray requiredArray)
        {
            targetRequired = requiredArray;
        }

        foreach (var item in allOfArray)
        {
            if (item is not JsonObject itemObject)
            {
                continue;
            }

            if (itemObject.TryGetPropertyValue("properties", out var itemPropsNode) && itemPropsNode is JsonObject itemProps)
            {
                targetProperties ??= new JsonObject();

                foreach (var kvp in itemProps)
                {
                    if (!targetProperties.ContainsKey(kvp.Key))
                    {
                        targetProperties[kvp.Key] = kvp.Value?.DeepClone();
                    }
                }
            }

            if (itemObject.TryGetPropertyValue("required", out var itemRequiredNode) && itemRequiredNode is JsonArray itemRequired)
            {
                targetRequired ??= new JsonArray();

                foreach (var requiredItem in itemRequired)
                {
                    if (requiredItem is not JsonValue requiredValue)
                    {
                        continue;
                    }

                    var requiredName = requiredValue.GetValue<string>();
                    if (!targetRequired.Any(existing => existing?.GetValue<string>() == requiredName))
                    {
                        targetRequired.Add(requiredName);
                    }
                }
            }

            if (!target.TryGetPropertyValue("type", out _) &&
                itemObject.TryGetPropertyValue("type", out var itemTypeNode))
            {
                target["type"] = itemTypeNode?.DeepClone();
            }
        }

        if (targetProperties != null)
        {
            target["properties"] = targetProperties;
        }

        if (targetRequired != null)
        {
            target["required"] = targetRequired;
        }
    }
}
