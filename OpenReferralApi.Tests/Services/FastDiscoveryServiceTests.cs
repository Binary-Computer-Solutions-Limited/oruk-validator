using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Moq.Protected;
using OpenReferralApi.Core.Services;

namespace OpenReferralApi.Tests.Services;

[TestFixture]
public class FastDiscoveryServiceTests
{
    private Mock<IHttpClientFactory> _httpClientFactoryMock = null!;
    private Mock<ILogger<FastDiscoveryService>> _loggerMock = null!;
    private Mock<HttpMessageHandler> _httpMessageHandlerMock = null!;
    private HttpClient _httpClient = null!;

    [SetUp]
    public void Setup()
    {
        _httpClientFactoryMock = new Mock<IHttpClientFactory>();
        _loggerMock = new Mock<ILogger<FastDiscoveryService>>();
        _httpMessageHandlerMock = new Mock<HttpMessageHandler>();
        _httpClient = TestHttpClientFactory.CreateClient(_httpMessageHandlerMock.Object);

        _httpClientFactoryMock
            .Setup(f => f.CreateClient("OpenApiValidationService"))
            .Returns(_httpClient);
    }

    [TearDown]
    public void TearDown()
    {
        _httpClient?.Dispose();
    }

    [Test]
    public async Task DiscoverAsync_WithEmptyBaseUrl_ReturnsEmptyResult()
    {
        var service = CreateService();

        var result = await service.DiscoverAsync(string.Empty);

        Assert.That(result.Url, Is.Null);
        Assert.That(result.Reason, Is.Null);
    }

    [Test]
    public async Task DiscoverAsync_WithExplicitOpenApiUrlAtRoot_ReturnsDiscoveredUrl()
    {
        SetupHttpResponseMap(new Dictionary<string, (HttpStatusCode statusCode, string content)>
        {
            ["/"] = (HttpStatusCode.OK, "{\"openapi_url\":\"https://api.example.com/openapi.json\"}")
        });

        var service = CreateService();
        var result = await service.DiscoverAsync("https://api.example.com", includeDiscoveredSpecContent: true);

        Assert.That(result.Url, Is.EqualTo("https://api.example.com/openapi.json"));
        Assert.That(result.HasExplicitOpenApiUrl, Is.True);
        Assert.That(result.Reason, Does.Contain("openapi_url field"));
    }

    [Test]
    public async Task DiscoverAsync_WithVersionAtRoot_ResolvesConfiguredProfileUrl()
    {
        SetupHttpResponseMap(new Dictionary<string, (HttpStatusCode statusCode, string content)>
        {
            ["/"] = (HttpStatusCode.OK, "{\"version\":\"HSDS-UK-3.0\"}")
        });

        var service = CreateService();
        var result = await service.DiscoverAsync("https://api.example.com");

        Assert.That(result.Url, Is.EqualTo("https://hsds.example.org/3.0/openapi.json"));
        Assert.That(result.DetectedHsdsProfileVersion, Is.EqualTo("HSDS-UK-3.0"));
    }

    [Test]
    public async Task DiscoverAsync_WhenOwnSchemaValidationNone_ReturnsResolvedProfileUrlFromStandardPath()
    {
        SetupHttpResponseMap(new Dictionary<string, (HttpStatusCode statusCode, string content)>
        {
            ["/"] = (HttpStatusCode.OK, "{}"),
            ["/openapi.json"] = (HttpStatusCode.OK, "{\"x-hsds-version\":\"HSDS-UK-3.0\",\"openapi\":\"3.0.0\"}")
        });

        var service = CreateService(OwnSchemaValidationMode.None);
        var result = await service.DiscoverAsync("https://api.example.com", includeDiscoveredSpecContent: true);

        Assert.That(result.Url, Is.EqualTo("https://hsds.example.org/3.0/openapi.json"));
        Assert.That(result.DetectedHsdsProfileVersion, Is.EqualTo("HSDS-UK-3.0"));
        Assert.That(result.SpecContent, Does.Contain("openapi"));
    }

    [Test]
    public async Task DiscoverAsync_WhenOwnSchemaValidationStrict_ReturnsLocalStandardPathSpec()
    {
        SetupHttpResponseMap(new Dictionary<string, (HttpStatusCode statusCode, string content)>
        {
            ["/"] = (HttpStatusCode.OK, "{}"),
            ["/openapi.json"] = (HttpStatusCode.OK, "{\"x-hsds-version\":\"HSDS-UK-3.0\",\"openapi\":\"3.0.0\"}")
        });

        var service = CreateService();
        var result = await service.DiscoverAsync("https://api.example.com", includeDiscoveredSpecContent: true);

        Assert.That(result.Url, Is.EqualTo("https://api.example.com/openapi.json"));
        Assert.That(result.DetectedHsdsProfileVersion, Is.EqualTo("HSDS-UK-3.0"));
        Assert.That(result.SpecContent, Does.Contain("openapi"));
    }

    [Test]
    public async Task DiscoverAsync_WhenSwaggerConfigEndpointContainsUrl_ReturnsDiscoveredSpecUrl()
    {
        SetupHttpResponseMap(new Dictionary<string, (HttpStatusCode statusCode, string content)>
        {
            ["/"] = (HttpStatusCode.OK, "<html></html>"),
            ["/swagger-config"] = (HttpStatusCode.OK, "{\"url\":\"/swagger/v1/swagger.json\"}"),
            ["/swagger/v1/swagger.json"] = (HttpStatusCode.OK, "{\"openapi\":\"3.0.0\"}")
        });

        var service = CreateService();
        var result = await service.DiscoverAsync("https://api.example.com", includeDiscoveredSpecContent: true);

        Assert.That(result.Url, Is.EqualTo("https://api.example.com/swagger/v1/swagger.json"));
        Assert.That(result.SpecContent, Does.Contain("openapi"));
    }

    [Test]
    public async Task DiscoverAsync_WhenRootHtmlContainsSwaggerUiUrl_ReturnsResolvedSpecUrl()
    {
        var html = @"<!doctype html><html><body>
            <script>
                SwaggerUIBundle({
                    url: '/v3/api-docs',
                    dom_id: '#swagger-ui'
                });
            </script>
            </body></html>";

        SetupHttpResponseMap(new Dictionary<string, (HttpStatusCode statusCode, string content)>
        {
            ["/"] = (HttpStatusCode.OK, html),
            ["/v3/api-docs"] = (HttpStatusCode.OK, "{\"openapi\":\"3.0.1\"}")
        });

        var service = CreateService();
        var result = await service.DiscoverAsync("https://api.example.com", includeDiscoveredSpecContent: true);

        Assert.That(result.Url, Is.EqualTo("https://api.example.com/v3/api-docs"));
        Assert.That(result.SpecContent, Does.Contain("openapi"));
    }

    private FastDiscoveryService CreateService(OwnSchemaValidationMode ownSchemaValidation = OwnSchemaValidationMode.StrictOwnSchemaValidation)
    {
        return new FastDiscoveryService(
            _httpClientFactoryMock.Object,
            _loggerMock.Object,
            Options.Create(new SpecificationOptions
            {
                Urls = new Dictionary<string, string>
                {
                    ["HSDS-UK-3.0"] = "https://hsds.example.org/3.0/openapi.json"
                }
            }),
            Options.Create(new OpenApiValidationServerOptions { OwnSchemaValidation = ownSchemaValidation }));
    }

    private void SetupHttpResponseMap(IDictionary<string, (HttpStatusCode statusCode, string content)> responses)
    {
        _httpMessageHandlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Returns<HttpRequestMessage, CancellationToken>((request, _) =>
            {
                var absolutePath = request.RequestUri?.AbsolutePath ?? "/";
                if (responses.TryGetValue(absolutePath, out var configuredResponse))
                {
                    return Task.FromResult(new HttpResponseMessage
                    {
                        StatusCode = configuredResponse.statusCode,
                        Content = new StringContent(configuredResponse.content)
                    });
                }

                return Task.FromResult(new HttpResponseMessage
                {
                    StatusCode = HttpStatusCode.NotFound,
                    Content = new StringContent(string.Empty)
                });
            });
    }
}
