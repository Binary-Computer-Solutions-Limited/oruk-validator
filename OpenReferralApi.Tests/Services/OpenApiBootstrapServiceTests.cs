using Microsoft.Extensions.Logging;
using Moq;
using OpenReferralApi.Core.Services;

namespace OpenReferralApi.Tests.Services;

[TestFixture]
public class OpenApiBootstrapServiceTests
{
    private Mock<IFastDiscoveryService> _fastDiscoveryMock = null!;
    private Mock<ILogger<OpenApiBootstrapService>> _loggerMock = null!;
    private OpenApiBootstrapService _service = null!;

    [SetUp]
    public void Setup()
    {
        _fastDiscoveryMock = new Mock<IFastDiscoveryService>();
        _loggerMock = new Mock<ILogger<OpenApiBootstrapService>>();

        _service = new OpenApiBootstrapService(
            _fastDiscoveryMock.Object,
            _loggerMock.Object);
    }

    [Test]
    public async Task ResolveFromBaseUrlAsync_WithExplicitOpenApiUrl_UsesRootUrlAndVersion()
    {
        // Arrange
        _fastDiscoveryMock
            .Setup(x => x.DiscoverAsync("https://api.example.com", It.IsAny<DataSourceAuthentication?>(), null, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UnifiedDiscoveryResult
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
        Assert.That(result.ProfileVersion, Is.EqualTo("HSDS-UK-3.0"));
        Assert.That(result.ProfileReason, Does.Contain("HSDS-UK-3.0"));
    }

    [Test]
    public async Task ResolveFromBaseUrlAsync_WithoutExplicitOpenApiUrl_UsesFeedDiscoveryAndReturnsNullProfileVersion()
    {
        // Arrange
        _fastDiscoveryMock
            .Setup(x => x.DiscoverAsync("https://api.example.com", It.IsAny<DataSourceAuthentication?>(), null, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UnifiedDiscoveryResult
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
        _fastDiscoveryMock
            .Setup(x => x.DiscoverAsync("https://api.example.com", It.IsAny<DataSourceAuthentication?>(), null, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UnifiedDiscoveryResult
            {
                Url = null,
                Reason = "Base URL request failed",
                BaseUrlResponseContent = null,
                HasExplicitOpenApiUrl = false
            });

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

        _fastDiscoveryMock
            .Setup(x => x.DiscoverAsync("https://api.example.com", auth, null, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UnifiedDiscoveryResult
            {
                Url = "https://api.example.com/custom-openapi.json",
                BaseUrlResponseContent = "{}",
                HasExplicitOpenApiUrl = true
            });

        // Act
        var result = await _service.ResolveFromBaseUrlAsync("https://api.example.com", auth);

        // Assert
        Assert.That(result.OpenApiSchemaUrl, Is.EqualTo("https://api.example.com/custom-openapi.json"));
        _fastDiscoveryMock.Verify(x => x.DiscoverAsync("https://api.example.com", auth, null, true, It.IsAny<CancellationToken>()), Times.Once);
    }
}
