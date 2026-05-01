using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenReferralApi.Core.Logging;

namespace OpenReferralApi.Core.Services;

public sealed class UnifiedDiscoveryResult
{
    public string? Url { get; init; }
    public string? Reason { get; init; }
    public string? SpecContent { get; init; }
    public string? DetectedHsdsProfileVersion { get; init; }
}

public interface IFastDiscoveryService
{
  Task<UnifiedDiscoveryResult?> DiscoverAsync(string baseUrl, IEnumerable<string> candidatePaths, DataSourceAuthentication? authentication = null, CancellationToken ct = default);
}

public class FastDiscoveryService(
    IHttpClientFactory httpClientFactory,
    ILogger<FastDiscoveryService> logger,
    IOptions<OpenApiValidationServerOptions> options) : IFastDiscoveryService
{
  private readonly IHttpClientFactory _httpClientFactory = httpClientFactory;
  private readonly ILogger<FastDiscoveryService> _logger = logger;
  private readonly OpenApiValidationServerOptions _options = options.Value;

  // Tokens used to avoid hardcoded property names
  private static readonly string[] HSDSProfilePropertyNames =
  {
        "x-hsds-version", "version", "info.x-hsds-version", "info.x-profile-version", "info.version", "openapi"
    };

  private static readonly string[] OpenApiUrlPropertyNames =
  {
        "openapi_url", "openapiUrl", "open_api_url"
    };

  private static readonly Regex OpenApiYamlRegex = new(@"(?m)^\s*(openapi|swagger)\s*:\s*", RegexOptions.Compiled);

  public async Task<UnifiedDiscoveryResult?> DiscoverAsync(
        string baseUrl,
        IEnumerable<string> candidatePaths,
        DataSourceAuthentication? authentication = null,
        CancellationToken ct = default)
  {
    using var client = _httpClientFactory.CreateClient("OpenApiValidationService");
    var needsSchema = _options.OwnSchemaValidation != OwnSchemaValidationMode.None;

    foreach (var path in candidatePaths)
    {
      var url = BuildAbsoluteUrl(baseUrl, path);

      try
      {
        _logger.RequestingBaseUrl(SchemaResolverService.SanitizeUrlForLogging(url));

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        ApplyAuthentication(request, authentication);

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

        if (!response.IsSuccessStatusCode)
        {
          _logger.PathReturnedStatusCode(path, (int)response.StatusCode);
          continue;
        }

        var contentType = response.Content.Headers.ContentType?.MediaType;

        if (contentType != null && contentType.Contains("json"))
        {
          using var stream = await response.Content.ReadAsStreamAsync(ct);
          using var doc = await JsonDocument.ParseAsync(stream, default, ct);
          var root = doc.RootElement;

          var version = TryGetProperty(root, HSDSProfilePropertyNames);
          var schemaUrl = TryGetProperty(root, OpenApiUrlPropertyNames);
          bool isSpec = root.TryGetProperty("openapi", out _) || root.TryGetProperty("swagger", out _);

          if (!string.IsNullOrEmpty(version) || (needsSchema && (schemaUrl != null || isSpec)))
          {
            var reason = !string.IsNullOrEmpty(version)
                ? $"HSDS version '{version}' discovered via JSON property at {path}"
                : $"OpenAPI spec discovered at {path}";

            _logger.DiscoveredOpenApiUrl(SchemaResolverService.SanitizeUrlForLogging(schemaUrl ?? url));

            return new UnifiedDiscoveryResult
            {
              DetectedHsdsProfileVersion = version,
              Url = schemaUrl ?? (isSpec ? url : null),
              SpecContent = isSpec ? root.GetRawText() : null, // Returns whole spec[cite: 4]
              Reason = reason
            };
          }
        }
        else
        {
          var content = await response.Content.ReadAsStringAsync(ct);
          if (OpenApiYamlRegex.IsMatch(content))
          {
            return new UnifiedDiscoveryResult
            {
              Url = url,
              SpecContent = content,
              Reason = $"OpenAPI YAML spec discovered at {path}"
            };
          }
        }
      }
      catch (Exception ex)
      {
        _logger.ProbeFailed(ex, SchemaResolverService.SanitizeUrlForLogging(baseUrl), path);
      }
    }
    return null;
  }

  private static string? TryGetProperty(JsonElement root, string[] names)
  {
    foreach (var name in names)
    {
      // Simple support for nested paths like info.version
      if (name.Contains('.'))
      {
        var parts = name.Split('.');
        if (root.TryGetProperty(parts[0], out var parent) && parent.TryGetProperty(parts[1], out var child))
          return child.GetString();
      }
      else if (root.TryGetProperty(name, out var val))
      {
        return val.GetString();
      }
    }
    return null;
  }

  private static void ApplyAuthentication(HttpRequestMessage request, DataSourceAuthentication? auth)
  {
    if (auth == null) return;

    if (!string.IsNullOrWhiteSpace(auth.ApiKey) && !string.IsNullOrWhiteSpace(auth.ApiKeyHeader))
    {
      request.Headers.TryAddWithoutValidation(auth.ApiKeyHeader, auth.ApiKey);
    }

    if (!string.IsNullOrWhiteSpace(auth.BearerToken))
    {
      request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", auth.BearerToken);
    }

    if (auth.BasicAuth != null && !string.IsNullOrWhiteSpace(auth.BasicAuth.Username))
    {
      var credentials = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{auth.BasicAuth.Username}:{auth.BasicAuth.Password}"));
      request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
    }
  }

  private static string BuildAbsoluteUrl(string baseU, string path) => $"{baseU.TrimEnd('/')}/{path.TrimStart('/')}";
}