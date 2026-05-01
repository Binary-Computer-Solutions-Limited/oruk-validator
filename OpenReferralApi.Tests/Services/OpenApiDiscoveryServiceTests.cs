using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Moq.Protected;
using OpenReferralApi.Core.Services;

namespace OpenReferralApi.Tests.Services;

[TestFixture]
public class OpenApiDiscoveryServiceTests
{
    private Mock<IHttpClientFactory> _httpClientFactoryMock;
    private Mock<ILogger<OpenApiDiscoveryService>> _loggerMock;
    private Mock<HttpMessageHandler> _httpMessageHandlerMock;
    private HttpClient _httpClient;
    private OpenApiDiscoveryService _service;

    [SetUp]
    public void Setup()
    {
        _httpClientFactoryMock = new Mock<IHttpClientFactory>();
        _loggerMock = new Mock<ILogger<OpenApiDiscoveryService>>();
        _httpMessageHandlerMock = new Mock<HttpMessageHandler>();
        _httpClient = TestHttpClientFactory.CreateClient(_httpMessageHandlerMock.Object);

        _httpClientFactoryMock
            .Setup(f => f.CreateClient("OpenApiValidationService"))
            .Returns(_httpClient);

        _service = new OpenApiDiscoveryService(
            _httpClientFactoryMock.Object,
            _loggerMock.Object,
            Options.Create(new OpenApiValidationServerOptions { OwnSchemaValidation = OwnSchemaValidationMode.StrictOwnSchemaValidation }));
    }

    [TearDown]
    public void TearDown()
    {
        _httpClient?.Dispose();
    }

    [Test]
    public async Task FindOpenApiSpecAsync_WhenCommonPathContainsOpenApi_ReturnsPath()
    {
        // Arrange
        SetupHttpResponseMap(new Dictionary<string, (HttpStatusCode statusCode, string content)>
        {
            ["/openapi.json"] = (HttpStatusCode.OK, "{\"openapi\":\"3.0.0\"}")
        });

        // Act
        var result = await _service.FindOpenApiSpecAsync("https://api.example.com");

        // Assert
        Assert.That(result, Is.EqualTo("https://api.example.com/openapi.json"));
    }

    [Test]
    public async Task FindOpenApiSpecAsync_WhenOnlySecondCommonPathContainsSwagger_ReturnsSecondPath()
    {
        // Arrange
        SetupHttpResponseMap(new Dictionary<string, (HttpStatusCode statusCode, string content)>
        {
            ["/swagger.json"] = (HttpStatusCode.OK, "{\"swagger\":\"2.0\"}")
        });

        // Act
        var result = await _service.FindOpenApiSpecAsync("https://api.example.com");

        // Assert
        Assert.That(result, Is.EqualTo("https://api.example.com/swagger.json"));
    }

    [Test]
    public async Task FindOpenApiSpecAsync_WhenYamlSpecExistsAtStandardPath_ReturnsYamlPath()
    {
        // Arrange
        SetupHttpResponseMap(new Dictionary<string, (HttpStatusCode statusCode, string content)>
        {
            ["/openapi.yaml"] = (HttpStatusCode.OK, "openapi: 3.0.0\ninfo:\n  title: API\n  version: 1.0.0\npaths: {}")
        });

        // Act
        var result = await _service.FindOpenApiSpecAsync("https://api.example.com");

        // Assert
        Assert.That(result, Is.EqualTo("https://api.example.com/openapi.yaml"));
    }

    [Test]
    public async Task FindOpenApiSpecAsync_WhenYmlSpecExistsAtWellKnownPath_ReturnsYmlPath()
    {
        // Arrange
        SetupHttpResponseMap(new Dictionary<string, (HttpStatusCode statusCode, string content)>
        {
            ["/.well-known/openapi.yml"] = (HttpStatusCode.OK, "openapi: 3.0.0\ninfo:\n  title: API\n  version: 1.0.0\npaths: {}")
        });

        // Act
        var result = await _service.FindOpenApiSpecAsync("https://api.example.com");

        // Assert
        Assert.That(result, Is.EqualTo("https://api.example.com/.well-known/openapi.yml"));
    }

    [Test]
    public async Task FindOpenApiSpecAsync_WhenV3ApiDocsExists_ReturnsV3ApiDocsPath()
    {
        // Arrange
        SetupHttpResponseMap(new Dictionary<string, (HttpStatusCode statusCode, string content)>
        {
            ["/v3/api-docs"] = (HttpStatusCode.OK, "{\"openapi\":\"3.0.1\"}")
        });

        // Act
        var result = await _service.FindOpenApiSpecAsync("https://api.example.com");

        // Assert
        Assert.That(result, Is.EqualTo("https://api.example.com/v3/api-docs"));
    }

    [Test]
    public async Task FindOpenApiSpecAsync_WhenSwaggerConfigEndpointContainsYamlUrl_ReturnsDiscoveredYamlUrl()
    {
        // Arrange
        SetupHttpResponseMap(new Dictionary<string, (HttpStatusCode statusCode, string content)>
        {
            ["/swagger/swagger-config"] = (HttpStatusCode.OK, "{\"urls\":[{\"url\":\"/swagger/v2/swagger.yaml\",\"name\":\"V2\"}]}"),
        });

        // Act
        var result = await _service.FindOpenApiSpecAsync("https://api.example.com");

        // Assert
        Assert.That(result, Is.EqualTo("https://api.example.com/swagger/v2/swagger.yaml"));
    }

    [Test]
    public async Task FindOpenApiSpecAsync_WhenProbingFailsAndSwaggerUiScriptContainsRelativeUrl_ReturnsResolvedAbsoluteUrl()
    {
        // Arrange
        var html = @"<!doctype html><html><body>
            <script>
                const ui = SwaggerUIBundle({
                    url: '/v3/api-docs',
                    dom_id: '#swagger-ui'
                });
            </script>
            </body></html>";

        SetupHttpResponseMap(new Dictionary<string, (HttpStatusCode statusCode, string content)>
        {
            ["/root"] = (HttpStatusCode.OK, html)
        });

        // Act
        var result = await _service.FindOpenApiSpecAsync("https://api.example.com/root");

        // Assert
        Assert.That(result, Is.EqualTo("https://api.example.com/v3/api-docs"));
    }

    [Test]
    public async Task FindOpenApiSpecAsync_WhenProbingFailsAndSwaggerUiScriptContainsAbsoluteUrl_ReturnsAbsoluteUrl()
    {
        // Arrange
        var html = @"<!doctype html><html><body>
            <script>
                SwaggerUIBundle({
                    url: 'https://docs.example.org/openapi.json',
                    dom_id: '#swagger-ui'
                });
            </script>
            </body></html>";

        SetupHttpResponseMap(new Dictionary<string, (HttpStatusCode statusCode, string content)>
        {
            ["/root"] = (HttpStatusCode.OK, html)
        });

        // Act
        var result = await _service.FindOpenApiSpecAsync("https://api.example.com/root");

        // Assert
        Assert.That(result, Is.EqualTo("https://docs.example.org/openapi.json"));
    }

    [Test]
    public async Task FindOpenApiSpecAsync_WhenProbingFailsAndNoSwaggerUiConfig_ReturnsNull()
    {
        // Arrange
        var html = @"<html><body><script>const config = { url: '/not-openapi' };</script></body></html>";
        SetupHttpResponseMap(new Dictionary<string, (HttpStatusCode statusCode, string content)>
        {
            ["/"] = (HttpStatusCode.OK, html)
        });

        // Act
        var result = await _service.FindOpenApiSpecAsync("https://api.example.com");

        // Assert
        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task FindOpenApiSpecAsync_WhenUiHtmlContainsConfigUrl_ReturnsSpecFromConfigEndpoint()
    {
        // Arrange
        var html = @"<!doctype html><html><body>
            <script>
                SwaggerUIBundle({
                    configUrl: '/swagger/swagger-config',
                    dom_id: '#swagger-ui'
                });
            </script>
            </body></html>";

        SetupHttpResponseMap(new Dictionary<string, (HttpStatusCode statusCode, string content)>
        {
            ["/"] = (HttpStatusCode.OK, html),
            ["/swagger/swagger-config"] = (HttpStatusCode.OK, "{\"url\":\"/swagger/v1/swagger.yaml\"}")
        });

        // Act
        var result = await _service.FindOpenApiSpecAsync("https://api.example.com");

        // Assert
        Assert.That(result, Is.EqualTo("https://api.example.com/swagger/v1/swagger.yaml"));
    }

    [Test]
    public async Task FindOpenApiSpecAsync_WhenUiHtmlContainsRedocInit_ReturnsSpecUrl()
    {
        // Arrange
        var html = @"<!doctype html><html><body>
            <script>
                Redoc.init('/openapi.yaml', {});
            </script>
            </body></html>";

        SetupHttpResponseMap(new Dictionary<string, (HttpStatusCode statusCode, string content)>
        {
            ["/"] = (HttpStatusCode.OK, html)
        });

        // Act
        var result = await _service.FindOpenApiSpecAsync("https://api.example.com");

        // Assert
        Assert.That(result, Is.EqualTo("https://api.example.com/openapi.yaml"));
    }

    [Test]
    public async Task FindOpenApiSpecAsync_WhenUiHtmlContainsSpecUrlAttribute_ReturnsSpecUrl()
    {
        // Arrange
        var html = @"<!doctype html><html><body>
            <rapi-doc spec-url='/openapi.yml'></rapi-doc>
            </body></html>";

        SetupHttpResponseMap(new Dictionary<string, (HttpStatusCode statusCode, string content)>
        {
            ["/"] = (HttpStatusCode.OK, html)
        });

        // Act
        var result = await _service.FindOpenApiSpecAsync("https://api.example.com");

        // Assert
        Assert.That(result, Is.EqualTo("https://api.example.com/openapi.yml"));
    }

    [Test]
    public async Task FindOpenApiSpecAsync_WhenSwaggerConfigEndpointIsInvalidJson_FallsBackToUiDiscovery()
    {
        // Arrange
        var html = @"<!doctype html><html><body>
            <script>
                SwaggerUIBundle({
                    url: '/openapi.json',
                    dom_id: '#swagger-ui'
                });
            </script>
            </body></html>";

        SetupHttpResponseMap(new Dictionary<string, (HttpStatusCode statusCode, string content)>
        {
            ["/swagger-config"] = (HttpStatusCode.OK, "not-json"),
            ["/"] = (HttpStatusCode.OK, html)
        });

        // Act
        var result = await _service.FindOpenApiSpecAsync("https://api.example.com");

        // Assert
        Assert.That(result, Is.EqualTo("https://api.example.com/openapi.json"));
    }

    [Test]
    public async Task FindOpenApiSpecAsync_WhenSwaggerConfigContainsDuplicateUrls_ReturnsFirstUrl()
    {
        // Arrange
        SetupHttpResponseMap(new Dictionary<string, (HttpStatusCode statusCode, string content)>
        {
            ["/swagger-config"] = (HttpStatusCode.OK,
                "{\"urls\":[{\"url\":\"/swagger/v1/swagger.json\"},{\"url\":\"/swagger/v1/swagger.json\"},{\"url\":\"/swagger/v2/swagger.json\"}]}")
        });

        // Act
        var result = await _service.FindOpenApiSpecAsync("https://api.example.com");

        // Assert
        Assert.That(result, Is.EqualTo("https://api.example.com/swagger/v1/swagger.json"));
    }

    [Test]
    public void FindOpenApiSpecAsync_WhenCancellationRequested_DuringProbe_ThrowsOperationCanceledException()
    {
        // Arrange
        _httpMessageHandlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Returns<HttpRequestMessage, CancellationToken>((_, token) =>
            {
                token.ThrowIfCancellationRequested();
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"openapi\":\"3.0.0\"}")
                });
            });

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act + Assert
        Assert.That(async () => await _service.FindOpenApiSpecAsync("https://api.example.com", null, cts.Token),
            Throws.InstanceOf<OperationCanceledException>());
    }

    [Test]
    public async Task FindOpenApiSpecAsync_WhenBaseContentProvided_DoesNotRefetchRootPage()
    {
        // Arrange
        var html = @"<!doctype html><html><body>
            <script>
                SwaggerUIBundle({
                    url: '/openapi.json',
                    dom_id: '#swagger-ui'
                });
            </script>
            </body></html>";

        var requestUris = new List<string>();
        _httpMessageHandlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Returns<HttpRequestMessage, CancellationToken>((request, _) =>
            {
                requestUris.Add(request.RequestUri!.ToString());
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            });

        // Act
        var result = await _service.FindOpenApiSpecAsync("https://api.example.com", html);

        // Assert
        Assert.That(result, Is.EqualTo("https://api.example.com/openapi.json"));
        Assert.That(requestUris.Count, Is.GreaterThanOrEqualTo(25));
        Assert.That(requestUris, Has.None.EqualTo("https://api.example.com/"));
        Assert.That(requestUris, Has.None.EqualTo("https://api.example.com"));
    }

    [Test]
    public async Task FindOpenApiSpecAsync_WhenUiHtmlContainsMultipleDefinitions_ReturnsFirstDiscoveredDefinition()
    {
        // Arrange
        var html = @"<!doctype html><html><body>
            <script>
                SwaggerUIBundle({
                    urls: [
                        { url: '/swagger/v1/swagger.json', name: 'V1' },
                        { url: '/swagger/v2/swagger.yaml', name: 'V2' }
                    ]
                });
            </script>
            </body></html>";

        SetupHttpResponseMap(new Dictionary<string, (HttpStatusCode statusCode, string content)>
        {
            ["/"] = (HttpStatusCode.OK, html)
        });

        // Act
        var result = await _service.FindOpenApiSpecAsync("https://api.example.com");

        // Assert
        Assert.That(result, Is.EqualTo("https://api.example.com/swagger/v1/swagger.json"));
    }

    [Test]
    public async Task FindOpenApiSpecAsync_WhenProbeThrowsNonCancellationException_ContinuesToNextPath()
    {
        // Arrange
        var callCount = 0;
        _httpMessageHandlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Returns<HttpRequestMessage, CancellationToken>((request, _) =>
            {
                callCount++;
                if (callCount == 1)
                {
                    throw new HttpRequestException("network error");
                }

                if (request.RequestUri!.AbsolutePath.EndsWith("/swagger.json", StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("{\"swagger\":\"2.0\"}")
                    });
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            });

        // Act
        var result = await _service.FindOpenApiSpecAsync("https://api.example.com");

        // Assert
        Assert.That(result, Is.EqualTo("https://api.example.com/swagger.json"));
        Assert.That(callCount, Is.GreaterThanOrEqualTo(2));
    }

    [Test]
    public async Task DiscoverOpenApiSpecAsync_WhenOwnSchemaValidationNone_StopsAtFirstPotentialHsdsVersion()
    {
        // Arrange
        var service = new OpenApiDiscoveryService(
            _httpClientFactoryMock.Object,
            _loggerMock.Object,
            Options.Create(new OpenApiValidationServerOptions { OwnSchemaValidation = OwnSchemaValidationMode.None }));

        SetupHttpResponseMap(new Dictionary<string, (HttpStatusCode statusCode, string content)>
        {
            ["/openapi.json"] = (HttpStatusCode.OK, "{\"x-hsds-version\":\"HSDS-UK-3.0\",\"openapi\":\"3.0.0\"}")
        });

        // Act
        var result = await service.DiscoverOpenApiSpecAsync("https://api.example.com");

        // Assert
        Assert.That(result.Url, Is.Null);
        Assert.That(result.DetectedHsdsProfileVersion, Is.EqualTo("HSDS-UK-3.0"));
    }

    [Test]
    public async Task DiscoverOpenApiSpecAsync_WhenOwnSchemaValidationStrict_ReturnsDiscoveredOpenApiUrl()
    {
        // Arrange
        SetupHttpResponseMap(new Dictionary<string, (HttpStatusCode statusCode, string content)>
        {
            ["/openapi.json"] = (HttpStatusCode.OK, "{\"x-hsds-version\":\"HSDS-UK-3.0\",\"openapi\":\"3.0.0\"}")
        });

        // Act
        var result = await _service.DiscoverOpenApiSpecAsync("https://api.example.com");

        // Assert
        Assert.That(result.Url, Is.EqualTo("https://api.example.com/openapi.json"));
        Assert.That(result.DetectedHsdsProfileVersion, Is.EqualTo("HSDS-UK-3.0"));
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