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
    private Mock<IOptions<SpecificationOptions>> _specificationOptionsMock;
    private HttpClient _httpClient;
    private ProfileDiscoveryService _service;

    [SetUp]
    public void Setup()
    {
        _httpClientFactoryMock = new Mock<IHttpClientFactory>();
        _loggerMock = new Mock<ILogger<ProfileDiscoveryService>>();
        _httpMessageHandlerMock = new Mock<HttpMessageHandler>();
        _specificationOptionsMock = new Mock<IOptions<SpecificationOptions>>();
        _httpClient = TestHttpClientFactory.CreateClient(_httpMessageHandlerMock.Object);
        
        _httpClientFactoryMock
            .Setup(f => f.CreateClient("OpenApiValidationService"))
            .Returns(_httpClient);

        _specificationOptionsMock
            .Setup(o => o.Value)
            .Returns(new SpecificationOptions
            {
                BaseUrl = "https://openreferraluk.org/specifications/"
            });

        _service = new ProfileDiscoveryService(_httpClientFactoryMock.Object, _loggerMock.Object, _specificationOptionsMock.Object);
    }

    [TearDown]
    public void TearDown()
    {
        _httpClient?.Dispose();
    }

    [Test]
    public async Task DiscoverOpenApiUrlAsync_WithNullBaseUrl_ReturnsNull()
    {
        // Act
        var (url, reason) = await _service.DiscoverOpenApiUrlAsync(null!);

        // Assert
        Assert.That(url, Is.Null);
        Assert.That(reason, Is.Null);
    }

    [Test]
    public async Task DiscoverOpenApiUrlAsync_WithEmptyBaseUrl_ReturnsNull()
    {
        // Act
        var (url, reason) = await _service.DiscoverOpenApiUrlAsync("");

        // Assert
        Assert.That(url, Is.Null);
        Assert.That(reason, Is.Null);
    }

    [Test]
    public async Task DiscoverOpenApiUrlAsync_WithExplicitOpenApiUrl_ReturnsDiscoveredUrl()
    {
        // Arrange
        var baseUrl = "https://api.example.com";
        var openApiUrl = "https://api.example.com/openapi.json";
        var responseContent = $@"{{""openapi_url"": ""{openApiUrl}""}}";

        SetupHttpResponse(HttpStatusCode.OK, responseContent);

        // Act
        var (url, reason) = await _service.DiscoverOpenApiUrlAsync(baseUrl);

        // Assert
        Assert.That(url, Is.EqualTo(openApiUrl));
        Assert.That(reason, Does.Contain("openapi_url field"));
    }

    [Test]
    public async Task DiscoverOpenApiUrlAsync_WithOpenapiUrlCamelCase_ReturnsDiscoveredUrl()
    {
        // Arrange
        var baseUrl = "https://api.example.com";
        var openApiUrl = "https://api.example.com/api-docs";
        var responseContent = $@"{{""openapiUrl"": ""{openApiUrl}""}}";

        SetupHttpResponse(HttpStatusCode.OK, responseContent);

        // Act
        var (url, reason) = await _service.DiscoverOpenApiUrlAsync(baseUrl);

        // Assert
        Assert.That(url, Is.EqualTo(openApiUrl));
        Assert.That(reason, Does.Contain("openapi_url field"));
    }

    [Test]
    public async Task DiscoverOpenApiUrlAsync_WithOpenApiUrlUnderscoreCamelCase_ReturnsDiscoveredUrl()
    {
        // Arrange
        var baseUrl = "https://api.example.com";
        var openApiUrl = "https://api.example.com/swagger.json";
        var responseContent = $@"{{""open_api_url"": ""{openApiUrl}""}}";

        SetupHttpResponse(HttpStatusCode.OK, responseContent);

        // Act
        var (url, reason) = await _service.DiscoverOpenApiUrlAsync(baseUrl);

        // Assert
        Assert.That(url, Is.EqualTo(openApiUrl));
        Assert.That(reason, Does.Contain("openapi_url field"));
    }

    [Test]
    public async Task DiscoverOpenApiUrlAsync_WithVersion1_0_ReturnsVersion1_0Spec()
    {
        // Arrange
        var baseUrl = "https://api.example.com";
        var responseContent = @"{""version"": ""1.0""}";

        SetupHttpResponse(HttpStatusCode.OK, responseContent);

        // Act
        var (url, reason) = await _service.DiscoverOpenApiUrlAsync(baseUrl);

        // Assert
        Assert.That(url, Does.Contain("1.0/openapi.json"));
        Assert.That(reason, Does.Contain("Standard version [user: 1.0] read from '/' endpoint"));
    }

    [Test]
    public async Task DiscoverOpenApiUrlAsync_WithVersion3_0_ReturnsVersion3_0Spec()
    {
        // Arrange
        var baseUrl = "https://api.example.com";
        var responseContent = @"{""version"": ""3.0""}";

        SetupHttpResponse(HttpStatusCode.OK, responseContent);

        // Act
        var (url, reason) = await _service.DiscoverOpenApiUrlAsync(baseUrl);

        // Assert
        Assert.That(url, Does.Contain("3.0/openapi.json"));
        Assert.That(reason, Does.Contain("Standard version [user: 3.0] read from '/' endpoint"));
    }

    [Test]
    public async Task DiscoverOpenApiUrlAsync_WithVersionHSDSUK3_0_ReturnsVersion3_0Spec()
    {
        // Arrange
        var baseUrl = "https://api.example.com";
        var responseContent = @"{""version"": ""HSDS-UK-3.0""}";

        SetupHttpResponse(HttpStatusCode.OK, responseContent);

        // Act
        var (url, reason) = await _service.DiscoverOpenApiUrlAsync(baseUrl);

        // Assert
        Assert.That(url, Does.Contain("3.0/openapi.json"));
        Assert.That(reason, Does.Contain("Standard version [user: HSDS-UK-3.0] read from '/' endpoint"));
    }

    [Test]
    public async Task DiscoverOpenApiUrlAsync_WithVersionV3_1_ReturnsVersion3_1Spec()
    {
        // Arrange
        var baseUrl = "https://api.example.com";
        var responseContent = @"{""version"": ""V3.1""}";

        SetupHttpResponse(HttpStatusCode.OK, responseContent);

        // Act
        var (url, reason) = await _service.DiscoverOpenApiUrlAsync(baseUrl);

        // Assert
        Assert.That(url, Does.Contain("3.1/openapi.json"));
        Assert.That(reason, Does.Contain("Standard version [user: V3.1] read from '/' endpoint"));
    }

    [Test]
    public async Task DiscoverOpenApiUrlAsync_WithNoVersionOrOpenApiUrl_ReturnsDefaultSpec()
    {
        // Arrange
        var baseUrl = "https://api.example.com";
        var responseContent = @"{""name"": ""Test API""}";

        SetupHttpResponse(HttpStatusCode.OK, responseContent);

        // Act
        var (url, reason) = await _service.DiscoverOpenApiUrlAsync(baseUrl);

        // Assert
        Assert.That(url, Does.Contain("1.0/openapi.json"));
        Assert.That(reason, Does.Contain("Defaulted to HSDS-UK 1.0"));
        Assert.That(reason, Does.Contain("no version or openapi_url found"));
    }

    [Test]
    public async Task DiscoverOpenApiUrlAsync_WithHttpError_ReturnsDefaultSpec()
    {
        // Arrange
        var baseUrl = "https://api.example.com";

        SetupHttpResponse(HttpStatusCode.NotFound, "Not Found");

        // Act
        var (url, reason) = await _service.DiscoverOpenApiUrlAsync(baseUrl);

        // Assert
        Assert.That(url, Does.Contain("1.0/openapi.json"));
        Assert.That(reason, Does.Contain("Defaulted to HSDS-UK 1.0"));
        Assert.That(reason, Does.Contain("base URL request failed"));
    }

    [Test]
    public async Task DiscoverOpenApiUrlAsync_WithInvalidJson_ReturnsDefaultSpec()
    {
        // Arrange
        var baseUrl = "https://api.example.com";
        var responseContent = "This is not valid JSON";

        SetupHttpResponse(HttpStatusCode.OK, responseContent);

        // Act
        var (url, reason) = await _service.DiscoverOpenApiUrlAsync(baseUrl);

        // Assert
        Assert.That(url, Does.Contain("1.0/openapi.json"));
        Assert.That(reason, Does.Contain("Defaulted to HSDS-UK 1.0"));
        Assert.That(reason, Does.Contain("failed to parse"));
    }

    [Test]
    public async Task DiscoverOpenApiUrlAsync_WithHttpException_ReturnsDefaultSpec()
    {
        // Arrange
        var baseUrl = "https://api.example.com";

        _httpMessageHandlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Connection failed"));

        // Act
        var (url, reason) = await _service.DiscoverOpenApiUrlAsync(baseUrl);

        // Assert
        Assert.That(url, Does.Contain("1.0/openapi.json"));
        Assert.That(reason, Does.Contain("Defaulted to HSDS-UK 1.0"));
        Assert.That(reason, Does.Contain("error requesting base URL"));
    }

    [Test]
    public async Task DiscoverOpenApiUrlAsync_WithInvalidVersionFormat_ReturnsDefaultSpec()
    {
        // Arrange
        var baseUrl = "https://api.example.com";
        var responseContent = @"{""version"": ""invalid-version-string""}";

        SetupHttpResponse(HttpStatusCode.OK, responseContent);

        // Act
        var (url, reason) = await _service.DiscoverOpenApiUrlAsync(baseUrl);

        // Assert
        Assert.That(url, Does.Contain("1.0/openapi.json"));
        Assert.That(reason, Does.Contain("Defaulted to HSDS-UK 1.0"));
    }

    [Test]
    public async Task DiscoverOpenApiUrlAsync_WithBothVersionAndOpenApiUrl_UsesOpenApiUrl()
    {
        // Arrange - openapi_url should be treated as authoritative when provided
        var baseUrl = "https://api.example.com";
        var responseContent = @"{""version"": ""3.0"", ""openapi_url"": ""https://api.example.com/custom.json""}";

        SetupHttpResponse(HttpStatusCode.OK, responseContent);

        // Act
        var (url, reason) = await _service.DiscoverOpenApiUrlAsync(baseUrl);

        // Assert
        Assert.That(url, Is.EqualTo("https://api.example.com/custom.json"));
        Assert.That(reason, Does.Contain("openapi_url field"));
    }

    [Test]
    public async Task DiscoverOpenApiUrlAsync_WithBearerAuthentication_AppliesAuthorizationHeader()
    {
        // Arrange
        var baseUrl = "https://api.example.com";
        var auth = new DataSourceAuthentication
        {
            BearerToken = "token-abc"
        };

        SetupHttpResponse(HttpStatusCode.OK, @"{""openapi_url"": ""https://api.example.com/openapi.json""}");

        // Act
        var (url, _) = await _service.DiscoverOpenApiUrlAsync(baseUrl, auth);

        // Assert
        Assert.That(url, Is.EqualTo("https://api.example.com/openapi.json"));
        _httpMessageHandlerMock
            .Protected()
            .Verify(
                "SendAsync",
                Times.Once(),
                ItExpr.Is<HttpRequestMessage>(request =>
                    request.Headers.Authorization != null
                    && request.Headers.Authorization.Scheme == "Bearer"
                    && request.Headers.Authorization.Parameter == "token-abc"),
                ItExpr.IsAny<CancellationToken>());
    }

    [Test]
    public async Task DiscoverOpenApiUrlAsync_WithApiKeyAuthentication_AppliesApiKeyHeader()
    {
        // Arrange
        var baseUrl = "https://api.example.com";
        var auth = new DataSourceAuthentication
        {
            ApiKey = "api-key-xyz",
            ApiKeyHeader = "X-API-Key"
        };

        SetupHttpResponse(HttpStatusCode.OK, @"{""openapi_url"": ""https://api.example.com/openapi.json""}");

        // Act
        var (url, _) = await _service.DiscoverOpenApiUrlAsync(baseUrl, auth);

        // Assert
        Assert.That(url, Is.EqualTo("https://api.example.com/openapi.json"));
        _httpMessageHandlerMock
            .Protected()
            .Verify(
                "SendAsync",
                Times.Once(),
                ItExpr.Is<HttpRequestMessage>(request =>
                    request.Headers.Contains("X-API-Key")
                    && request.Headers.GetValues("X-API-Key").Contains("api-key-xyz")),
                ItExpr.IsAny<CancellationToken>());
    }

    private void SetupHttpResponse(HttpStatusCode statusCode, string content)
    {
        var mockResponse = new HttpResponseMessage
        {
            StatusCode = statusCode,
            Content = new StringContent(content)
        };

        _httpMessageHandlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(mockResponse);
    }
}
