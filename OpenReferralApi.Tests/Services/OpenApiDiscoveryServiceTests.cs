using System.Net;
using Microsoft.Extensions.Logging;
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
        _httpClient = new HttpClient(_httpMessageHandlerMock.Object);

        _httpClientFactoryMock
            .Setup(f => f.CreateClient("OpenApiValidationService"))
            .Returns(_httpClient);

        _service = new OpenApiDiscoveryService(_httpClientFactoryMock.Object, _loggerMock.Object);
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
        SetupHttpResponseSequence(new[]
        {
            (HttpStatusCode.OK, "{\"openapi\":\"3.0.0\"}"),
            (HttpStatusCode.NotFound, ""),
            (HttpStatusCode.NotFound, ""),
            (HttpStatusCode.NotFound, "")
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
        SetupHttpResponseSequence(new[]
        {
            (HttpStatusCode.NotFound, ""),
            (HttpStatusCode.OK, "{\"swagger\":\"2.0\"}"),
            (HttpStatusCode.NotFound, ""),
            (HttpStatusCode.NotFound, "")
        });

        // Act
        var result = await _service.FindOpenApiSpecAsync("https://api.example.com");

        // Assert
        Assert.That(result, Is.EqualTo("https://api.example.com/swagger.json"));
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

        SetupHttpResponseSequence(new[]
        {
            (HttpStatusCode.NotFound, ""),
            (HttpStatusCode.NotFound, ""),
            (HttpStatusCode.NotFound, ""),
            (HttpStatusCode.NotFound, ""),
            (HttpStatusCode.OK, html)
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

        SetupHttpResponseSequence(new[]
        {
            (HttpStatusCode.NotFound, ""),
            (HttpStatusCode.NotFound, ""),
            (HttpStatusCode.NotFound, ""),
            (HttpStatusCode.NotFound, ""),
            (HttpStatusCode.OK, html)
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
        SetupHttpResponseSequence(new[]
        {
            (HttpStatusCode.NotFound, ""),
            (HttpStatusCode.NotFound, ""),
            (HttpStatusCode.NotFound, ""),
            (HttpStatusCode.NotFound, ""),
            (HttpStatusCode.OK, html)
        });

        // Act
        var result = await _service.FindOpenApiSpecAsync("https://api.example.com");

        // Assert
        Assert.That(result, Is.Null);
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
        Assert.That(requestUris, Has.Count.EqualTo(4));
        Assert.That(requestUris, Has.None.EqualTo("https://api.example.com/"));
        Assert.That(requestUris, Has.None.EqualTo("https://api.example.com"));
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

    private void SetupHttpResponseSequence((HttpStatusCode statusCode, string content)[] responses)
    {
        var sequence = _httpMessageHandlerMock
            .Protected()
            .SetupSequence<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>());

        foreach (var (statusCode, content) in responses)
        {
            sequence = sequence.ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = statusCode,
                Content = new StringContent(content)
            });
        }
    }
}