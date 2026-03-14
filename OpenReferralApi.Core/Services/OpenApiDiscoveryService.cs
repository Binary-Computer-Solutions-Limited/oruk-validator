using System.Net.Http;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using Microsoft.Extensions.Logging;

namespace OpenReferralApi.Core.Services;

public interface IOpenApiDiscoveryService
{
    Task<string?> FindOpenApiSpecAsync(string baseUrl, CancellationToken cancellationToken = default);
}

public class OpenApiDiscoveryService : IOpenApiDiscoveryService
{
    // Matches a SwaggerUIBundle/SwaggerUI initialisation block containing a url: property,
    // anchored to a known Swagger UI function call to reduce false positives from unrelated JS.
    private static readonly Regex SwaggerUiUrlRegex = new(
        @"SwaggerUI(?:Bundle)?\s*\([^)]*url\s*:\s*[""']([^""']+)[""']",
        RegexOptions.Compiled | RegexOptions.Singleline);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<OpenApiDiscoveryService> _logger;

    public OpenApiDiscoveryService(IHttpClientFactory httpClientFactory, ILogger<OpenApiDiscoveryService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<string?> FindOpenApiSpecAsync(string baseUrl, CancellationToken cancellationToken = default)
    {
        var client = _httpClientFactory.CreateClient("OpenApiValidationService");
        baseUrl = baseUrl.TrimEnd('/');

        // 1. Probing strategy — try common well-known OpenAPI spec paths.
        string[] commonPaths = { "/openapi.json", "/swagger.json", "/api-docs", "/v3/api-docs" };
        foreach (var path in commonPaths)
        {
            try
            {
                var response = await client.GetAsync($"{baseUrl}{path}", cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    string content = await response.Content.ReadAsStringAsync(cancellationToken);
                    if (content.Contains("\"openapi\"") || content.Contains("\"swagger\""))
                    {
                        _logger.LogInformation("Discovered feed OpenAPI spec via probing at {Path}", path);
                        return $"{baseUrl}{path}";
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Probe failed for {BaseUrl}{Path}", SchemaResolverService.SanitizeUrlForLogging(baseUrl), path);
            }
        }

        // 2. Scraping strategy — fetch the root HTML page and look for a Swagger UI bundle config.
        try
        {
            var response = await client.GetAsync(baseUrl, cancellationToken);
            if (!response.IsSuccessStatusCode) return null;

            var html = await response.Content.ReadAsStringAsync(cancellationToken);
            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            var scriptNodes = doc.DocumentNode.SelectNodes("//script");
            if (scriptNodes != null)
            {
                foreach (var script in scriptNodes)
                {
                    var match = SwaggerUiUrlRegex.Match(script.InnerHtml);
                    if (match.Success)
                    {
                        string foundPath = match.Groups[1].Value;
                        var specUrl = new Uri(new Uri(baseUrl + "/"), foundPath).ToString();
                        _logger.LogInformation("Discovered feed OpenAPI spec via HTML scraping: {SpecUrl}", SchemaResolverService.SanitizeUrlForLogging(specUrl));
                        return specUrl;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "HTML scraping failed for base URL {BaseUrl}", SchemaResolverService.SanitizeUrlForLogging(baseUrl));
        }

        return null;
    }
}