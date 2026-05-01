using Microsoft.Extensions.Logging;
using System.Text.Json;
using OpenReferralApi.Core.Logging;
using Microsoft.Extensions.Options;

namespace OpenReferralApi.Core.Services;

public interface IOpenApiBootstrapService
{
    Task<OpenApiBootstrapResult> ResolveFromBaseUrlAsync(string baseUrl, DataSourceAuthentication? authentication = null, CancellationToken cancellationToken = default);
}

public class OpenApiBootstrapResult
{
    public string? OpenApiSchemaUrl { get; init; }
    public string? ProfileVersion { get; init; }
    public string? ProfileReason { get; init; }
    public string? DiscoveryReason { get; init; }
    public bool UsedDataServiceOpenApi { get; init; }
}

public class OpenApiBootstrapService : IOpenApiBootstrapService
{

    private readonly IFastDiscoveryService _fastDiscoveryService;
    private readonly ILogger<OpenApiBootstrapService> _logger;
    private readonly OpenApiValidationServerOptions _openApiValidationOptions;


    public OpenApiBootstrapService(
        IFastDiscoveryService fastDiscoveryService,
        ILogger<OpenApiBootstrapService> logger,
        IOptions<OpenApiValidationServerOptions>? openApiValidationOptions = null)
    {
        _fastDiscoveryService = fastDiscoveryService;
        _logger = logger;
       _openApiValidationOptions = openApiValidationOptions?.Value ?? new OpenApiValidationServerOptions();
    }

    public async Task<OpenApiBootstrapResult> ResolveFromBaseUrlAsync(string baseUrl, DataSourceAuthentication? authentication = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new ArgumentException("Base URL must be provided for OpenAPI bootstrap discovery", nameof(baseUrl));
        }

        var discovery = await _fastDiscoveryService.DiscoverAsync(
            baseUrl,
            authentication,
            baseUrlContent: null,
            includeDiscoveredSpecContent: true,
            cancellationToken);

        // Try detected version from discovered OpenAPI spec first, then fall back to root endpoint.
        var rootProfileVersion = !string.IsNullOrWhiteSpace(discovery.DetectedHsdsProfileVersion)
            ? discovery.DetectedHsdsProfileVersion
            : TryExtractProfileVersionFromJson(discovery.BaseUrlResponseContent);

        if (string.IsNullOrWhiteSpace(rootProfileVersion) && !string.IsNullOrWhiteSpace(discovery.SpecContent))
        {
            rootProfileVersion = TryExtractProfileVersionFromOpenApiSpec(discovery.SpecContent);
        }

        var discoveredUrl = discovery.Url;
        var discoverProfileVersionOnly = _openApiValidationOptions.OwnSchemaValidation == OwnSchemaValidationMode.None;

        if (string.IsNullOrWhiteSpace(discoveredUrl) || discoverProfileVersionOnly)
        {
            return new OpenApiBootstrapResult
            {
                OpenApiSchemaUrl = null,
                ProfileVersion = rootProfileVersion,
                ProfileReason = rootProfileVersion != null
                    ? $"Standard version [user: {rootProfileVersion}] detected"
                    : "Version not found in '/' response",
                DiscoveryReason = discovery.Reason,
                UsedDataServiceOpenApi = false
            };
        }

        var discoveryReason = !string.IsNullOrWhiteSpace(discovery.Url)
            ? $"Feed spec discovered at {SchemaResolverService.SanitizeUrlForLogging(discovery.Url)}"
              + (!string.IsNullOrWhiteSpace(discovery.Reason) ? $"; {discovery.Reason}" : string.Empty)
            : discovery.Reason;

        var profileReason = rootProfileVersion != null
            ? $"Standard version [user: {rootProfileVersion}] detected"
            : "Version not found in '/' response";

        _logger.BootstrapDiscoveryResolved(SchemaResolverService.SanitizeUrlForLogging(discoveredUrl), profileReason);

        return new OpenApiBootstrapResult
        {
            OpenApiSchemaUrl = discoveredUrl,
            ProfileVersion = rootProfileVersion,
            ProfileReason = profileReason,
            DiscoveryReason = discoveryReason,
            UsedDataServiceOpenApi = true
        };
    }

    private static string? TryExtractProfileVersionFromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            // Only extract from legitimate HSDS version fields.
            // Deliberately do NOT fall back to the "openapi" field — that field specifies the
            // OpenAPI specification version, not the HSDS schema version. If a version can only
            // be inferred from "openapi", leave it unset here so that OpenApiValidationService
            // can detect and report the misplacement with a proper warning.
            var candidateTokens = new[] { "x-hsds-version", "version", "info.x-hsds-version", "info.x-profile-version" };
            foreach (var tokenPath in candidateTokens)
            {
                var tokenValue = TryGetPathString(root, tokenPath)?.Trim();
                if (!string.IsNullOrWhiteSpace(tokenValue))
                {
                    return tokenValue;
                }
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    private static string? TryExtractProfileVersionFromOpenApiSpec(string specContent)
    {
        if (string.IsNullOrWhiteSpace(specContent))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(specContent);
            var root = document.RootElement;

            // Only extract from legitimate HSDS version fields.
            // Deliberately do NOT fall back to the "openapi" field — that field specifies the
            // OpenAPI specification version, not the HSDS schema version. If a version can only
            // be inferred from "openapi", leave it unset here so that OpenApiValidationService
            // can detect and report the misplacement with a proper warning.
            var candidateTokens = new[] { "x-hsds-version", "version", "info.x-hsds-version", "info.x-profile-version" };
            foreach (var tokenPath in candidateTokens)
            {
                var tokenValue = TryGetPathString(root, tokenPath)?.Trim();
                if (!string.IsNullOrWhiteSpace(tokenValue))
                {
                    return tokenValue;
                }
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    private static string? TryGetPathString(JsonElement root, string path)
    {
        var current = root;
        foreach (var segment in path.Split('.'))
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current))
            {
                return null;
            }
        }

        return current.ValueKind switch
        {
            JsonValueKind.String => current.GetString(),
            JsonValueKind.Number => current.GetRawText(),
            JsonValueKind.True => bool.TrueString,
            JsonValueKind.False => bool.FalseString,
            _ => null
        };
    }
}
