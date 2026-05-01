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
    private Mock<IHttpClientFactory> _httpClientFactoryMock;
    private Mock<ILogger<ProfileDiscoveryService>> _loggerMock;
    private Mock<HttpMessageHandler> _httpMessageHandlerMock;
    private HttpClient _httpClient;

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
    public async Task DiscoverAsync_WhenOwnSchemaValidationNone_StopsAtFirstPotentialHsdsVersionFromStandardPath()
    {
        // Arrange
        var requestUris = new List<string>();
        _httpMessageHandlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Returns<HttpRequestMessage, CancellationToken>((request, _) =>
            {
                var absolutePath = request.RequestUri?.AbsolutePath ?? "/";
                requestUris.Add(absolutePath);

                if (absolutePath == "/")
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("{}")
                    });
                }

                if (absolutePath == "/openapi.json")
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("{\"x-hsds-version\":\"HSDS-UK-3.0\",\"openapi\":\"3.0.0\"}")
                    });
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            });

        var service = new ProfileDiscoveryService(
            _httpClientFactoryMock.Object,
            _loggerMock.Object,
            Options.Create(new SpecificationOptions
            {
                Urls = new Dictionary<string, string>
                {
                    ["HSDS-UK-3.0"] = "https://hsds.example.org/HSDS-UK-3.0/openapi.json"
                }
            }),
            Options.Create(new OpenApiValidationServerOptions { OwnSchemaValidation = OwnSchemaValidationMode.None }));

        // Act
        var result = await service.DiscoverAsync("https://api.example.com");

        // Assert
        Assert.That(result.DetectedHsdsProfileVersion, Is.EqualTo("HSDS-UK-3.0"));
        Assert.That(result.Url, Is.EqualTo("https://hsds.example.org/HSDS-UK-3.0/openapi.json"));
        Assert.That(requestUris, Does.Contain("/openapi.json"));
    }

    [Test]
    public async Task DiscoverAsync_WhenOwnSchemaValidationStrict_DiscoversLocalOpenApiSpecFromStandardPath()
    {
        // Arrange
        _httpMessageHandlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Returns<HttpRequestMessage, CancellationToken>((request, _) =>
            {
                var absolutePath = request.RequestUri?.AbsolutePath ?? "/";

                if (absolutePath == "/")
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("{}")
                    });
                }

                if (absolutePath == "/openapi.json")
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("{\"x-hsds-version\":\"HSDS-UK-3.0\",\"openapi\":\"3.0.0\"}")
                    });
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            });

        var service = new ProfileDiscoveryService(
            _httpClientFactoryMock.Object,
            _loggerMock.Object,
            Options.Create(new SpecificationOptions
            {
                Urls = new Dictionary<string, string>()
            }),
            Options.Create(new OpenApiValidationServerOptions { OwnSchemaValidation = OwnSchemaValidationMode.StrictOwnSchemaValidation }));

        // Act
        var result = await service.DiscoverAsync("https://api.example.com");

        // Assert
        Assert.That(result.Url, Is.EqualTo("https://api.example.com/openapi.json"));
        Assert.That(result.DetectedHsdsProfileVersion, Is.EqualTo("HSDS-UK-3.0"));
    }
}
