using Microsoft.Extensions.Logging;
using Moq;
using OpenReferralApi.Core.Services;

namespace OpenReferralApi.Tests.Services;

[TestFixture]
public class OpenApiBootstrapServiceTests
{
    private Mock<IProfileDiscoveryService> _profileDiscoveryMock = null!;
    private Mock<IOpenApiDiscoveryService> _openApiDiscoveryMock = null!;
    private Mock<ILogger<OpenApiBootstrapService>> _loggerMock = null!;
    private OpenApiBootstrapService _service = null!;

    [SetUp]
    public void Setup()
    {
        _profileDiscoveryMock = new Mock<IProfileDiscoveryService>();
        _openApiDiscoveryMock = new Mock<IOpenApiDiscoveryService>();
        _loggerMock = new Mock<ILogger<OpenApiBootstrapService>>();

        _service = new OpenApiBootstrapService(
            _profileDiscoveryMock.Object,
            _openApiDiscoveryMock.Object,
            _loggerMock.Object);
    }

    [Test]
    public async Task ResolveFromBaseUrlAsync_WithExplicitOpenApiUrl_UsesRootUrlAndVersion()
    {
        // Arrange
        _profileDiscoveryMock
            .Setup(x => x.DiscoverAsync("https://api.example.com", It.IsAny<DataSourceAuthentication?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProfileDiscoveryResult
            {
                Url = "https://api.example.com/custom-openapi.json",
                Reason = "OpenAPI URL read from '/' endpoint (openapi_url field)",
                BaseUrlResponseContent = "{\"version\":\"HSDS-UK-3.0\",\"openapi_url\":\"https://api.example.com/custom-openapi.json\"}",
                HasExplicitOpenApiUrl = true
            });

        // Act
        var result = await _service.ResolveFromBaseUrlAsync("https://api.example.com");

        // Assert
        Assert.That(result.OpenApiSchemaUrl, Is.EqualTo("https://api.example.com/custom-openapi.json"));
        Assert.That(result.ProfileVersion, Is.EqualTo("3.0"));
        Assert.That(result.ProfileReason, Does.Contain("3.0"));

        _openApiDiscoveryMock.Verify(x => x.DiscoverOpenApiSpecAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task ResolveFromBaseUrlAsync_WithoutExplicitOpenApiUrl_UsesFeedDiscoveryAndReturnsNullProfileVersion()
    {
        // Arrange
        _profileDiscoveryMock
            .Setup(x => x.DiscoverAsync("https://api.example.com", It.IsAny<DataSourceAuthentication?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProfileDiscoveryResult
            {
                Url = null,
                Reason = "No version or openapi_url found in '/' response",
                BaseUrlResponseContent = "{}",
                HasExplicitOpenApiUrl = false
            });

        _openApiDiscoveryMock
            .Setup(x => x.DiscoverOpenApiSpecAsync("https://api.example.com", "{}", true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OpenApiDiscoveryResult
            {
                Url = "https://api.example.com/openapi.json"
            });

        // Act
        var result = await _service.ResolveFromBaseUrlAsync("https://api.example.com");

        // Assert
        Assert.That(result.OpenApiSchemaUrl, Is.EqualTo("https://api.example.com/openapi.json"));
        Assert.That(result.ProfileVersion, Is.Null);
        Assert.That(result.DiscoveryReason, Does.Contain("Feed spec discovered"));
    }

    [Test]
    public async Task ResolveFromBaseUrlAsync_WhenNoSchemaUrlFound_ReturnsNullProfileVersion()
    {
        // Arrange
        _profileDiscoveryMock
            .Setup(x => x.DiscoverAsync("https://api.example.com", It.IsAny<DataSourceAuthentication?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProfileDiscoveryResult
            {
                Url = null,
                Reason = "Base URL request failed",
                BaseUrlResponseContent = null,
                HasExplicitOpenApiUrl = false
            });

        _openApiDiscoveryMock
            .Setup(x => x.DiscoverOpenApiSpecAsync("https://api.example.com", null, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OpenApiDiscoveryResult());

        // Act
        var result = await _service.ResolveFromBaseUrlAsync("https://api.example.com");

        // Assert
        Assert.That(result.OpenApiSchemaUrl, Is.Null);
        Assert.That(result.ProfileVersion, Is.Null);
    }

    [Test]
    public async Task ResolveFromBaseUrlAsync_WithAuthentication_PassesAuthenticationToProfileDiscovery()
    {
        // Arrange
        var auth = new DataSourceAuthentication { BearerToken = "token-123" };

        _profileDiscoveryMock
            .Setup(x => x.DiscoverAsync("https://api.example.com", auth, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProfileDiscoveryResult
            {
                Url = "https://api.example.com/custom-openapi.json",
                BaseUrlResponseContent = "{}",
                HasExplicitOpenApiUrl = true
            });

        // Act
        var result = await _service.ResolveFromBaseUrlAsync("https://api.example.com", auth);

        // Assert
        Assert.That(result.OpenApiSchemaUrl, Is.EqualTo("https://api.example.com/custom-openapi.json"));
        _profileDiscoveryMock.Verify(x => x.DiscoverAsync("https://api.example.com", auth, It.IsAny<CancellationToken>()), Times.Once);
    }
}
