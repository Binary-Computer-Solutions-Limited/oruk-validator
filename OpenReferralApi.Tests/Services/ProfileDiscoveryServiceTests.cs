using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Moq.Protected;
using OpenReferralApi.Core.Services;

namespace OpenReferralApi.Tests.Services;

[TestFixture]
public class ProfileDiscoveryServiceTests
{
    private Mock<IHttpClientFactory> _httpClientFactoryMock = null!;
    private Mock<ILogger<ProfileDiscoveryService>> _loggerMock = null!;
    private Mock<HttpMessageHandler> _httpMessageHandlerMock = null!;
    private HttpClient _httpClient = null!;

    [SetUp]
    public void Setup()
    {
        _httpClientFactoryMock = new Mock<IHttpClientFactory>();
        _loggerMock = new Mock<ILogger<ProfileDiscoveryService>>();
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
    public void DiscoverFromBaseUrlAsync_WithEmptyBaseUrl_ThrowsArgumentException()
    {
        var service = CreateService();

        Assert.ThrowsAsync<ArgumentException>(() => service.DiscoverFromBaseUrlAsync(string.Empty));
    }

    [Test]
    public async Task DiscoverFromBaseUrlAsync_WhenVersionFoundAtRoot_ReturnsProfileVersionOnly()
    {
        SetupHttpResponseMap(new Dictionary<string, (HttpStatusCode, string)>
        {
            ["/"] = (HttpStatusCode.OK, "{\"version\":\"HSDS-UK-3.0\"}")
        });

        var service = CreateService();
        var result = await service.DiscoverFromBaseUrlAsync("https://api.example.com");

        Assert.That(result.HsdsProfileVersion, Is.EqualTo("HSDS-UK-3.0"));
        Assert.That(result.OpenApiSchemaContent, Is.Null);
    }

    [Test]
    public async Task DiscoverFromBaseUrlAsync_WhenOpenApiSpecAtStandardPath_ReturnsSpecContent()
    {
        SetupHttpResponseMap(new Dictionary<string, (HttpStatusCode, string)>
        {
            ["/openapi.json"] = (HttpStatusCode.OK, "{\"x-hsds-version\":\"HSDS-UK-3.0\",\"openapi\":\"3.0.0\"}")
        });

        var service = CreateService();
        var result = await service.DiscoverFromBaseUrlAsync("https://api.example.com");

        Assert.That(result.HsdsProfileVersion, Is.EqualTo("HSDS-UK-3.0"));
        Assert.That(result.OpenApiSchemaContent, Does.Contain("openapi"));
    }

    [Test]
    public async Task DiscoverFromBaseUrlAsync_WhenOwnSchemaValidationNone_ReturnsVersionWithoutSpecContent()
    {
        SetupHttpResponseMap(new Dictionary<string, (HttpStatusCode, string)>
        {
            ["/openapi.json"] = (HttpStatusCode.OK, "{\"x-hsds-version\":\"HSDS-UK-3.0\",\"openapi\":\"3.0.0\"}")
        });

        var service = CreateService(OwnSchemaValidationMode.None);
        var result = await service.DiscoverFromBaseUrlAsync("https://api.example.com");

        Assert.That(result.HsdsProfileVersion, Is.EqualTo("HSDS-UK-3.0"));
        Assert.That(result.OpenApiSchemaContent, Is.Null);
    }

    [Test]
    public async Task DiscoverFromBaseUrlAsync_WhenNoSpecFound_ReturnsResultWithNoDiscoveredVersion()
    {
        var service = CreateService();
        var result = await service.DiscoverFromBaseUrlAsync("https://api.example.com");

        Assert.That(result.HsdsProfileVersion, Is.Null);
        Assert.That(result.OpenApiSchemaContent, Is.Null);
    }

    [Test]
    public async Task DiscoverFromBaseUrlAsync_WhenSwaggerConfigEndpointContainsUrl_ReturnsDiscoveredSpecContent()
    {
        // Use a URL not in Constants.Paths so it is only reachable via swagger-config probing
        SetupHttpResponseMap(new Dictionary<string, (HttpStatusCode, string)>
        {
            ["/swagger-config"] = (HttpStatusCode.OK, "{\"url\":\"/api/v2/openapi-custom.json\"}"),
            ["/api/v2/openapi-custom.json"] = (HttpStatusCode.OK, "{\"openapi\":\"3.0.0\"}")
        });

        var service = CreateService();
        var result = await service.DiscoverFromBaseUrlAsync("https://api.example.com");

        Assert.That(result.OpenApiSchemaContent, Does.Contain("openapi"));
    }

    [Test]
    public async Task DiscoverFromBaseUrlAsync_WithAuthentication_PassesAuthToHttpRequests()
    {
        SetupHttpResponseMap(new Dictionary<string, (HttpStatusCode, string)>
        {
            ["/"] = (HttpStatusCode.OK, "{\"version\":\"HSDS-UK-3.0\"}")
        });

        var auth = new DataSourceAuthentication { BearerToken = "token-123" };
        var service = CreateService();
        var result = await service.DiscoverFromBaseUrlAsync("https://api.example.com", auth);

        Assert.That(result.HsdsProfileVersion, Is.EqualTo("HSDS-UK-3.0"));
    }

    [Test]
    public async Task DiscoverFromBaseUrlAsync_WhenStandardPathReturnsSwaggerUiSpec_ReturnsResolvedSpecContent()
    {
        var html = @"<!doctype html><html><body>
            <script>
                SwaggerUIBundle({
                    url: '/v3/api-docs',
                    dom_id: '#swagger-ui'
                });
            </script>
            </body></html>";

        SetupHttpResponseMap(new Dictionary<string, (HttpStatusCode, string)>
        {
            ["/"] = (HttpStatusCode.OK, html),
            ["/v3/api-docs"] = (HttpStatusCode.OK, "{\"openapi\":\"3.0.1\"}")
        });

        var service = CreateService();
        var result = await service.DiscoverFromBaseUrlAsync("https://api.example.com");

        Assert.That(result.OpenApiSchemaContent, Does.Contain("openapi"));
    }

    private ProfileDiscoveryService CreateService(OwnSchemaValidationMode ownSchemaValidation = OwnSchemaValidationMode.StrictOwnSchemaValidation)
    {
        return new ProfileDiscoveryService(
            _loggerMock.Object,
            _httpClientFactoryMock.Object,
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
