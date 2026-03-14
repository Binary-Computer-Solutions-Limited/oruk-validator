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
            .Setup(x => x.DiscoverAsync("https://api.example.com", It.IsAny<CancellationToken>()))
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
        Assert.That(result.ProfileVersion, Is.EqualTo("HSDS-UK-3.0"));
        Assert.That(result.ProfileReason, Does.Contain("HSDS-UK-3.0"));

        _openApiDiscoveryMock.Verify(x => x.FindOpenApiSpecAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task ResolveFromBaseUrlAsync_WithoutExplicitOpenApiUrl_UsesFeedDiscoveryAndDefaultsProfileVersion()
    {
        // Arrange
        _profileDiscoveryMock
            .Setup(x => x.DiscoverAsync("https://api.example.com", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProfileDiscoveryResult
            {
                Url = "https://openreferraluk.org/specifications/1.0/openapi.json",
                Reason = "Defaulted to HSDS-UK 1.0 (no version or openapi_url found)",
                BaseUrlResponseContent = "{}",
                HasExplicitOpenApiUrl = false
            });

        _openApiDiscoveryMock
            .Setup(x => x.FindOpenApiSpecAsync("https://api.example.com", "{}", It.IsAny<CancellationToken>()))
            .ReturnsAsync("https://api.example.com/openapi.json");

        // Act
        var result = await _service.ResolveFromBaseUrlAsync("https://api.example.com");

        // Assert
        Assert.That(result.OpenApiSchemaUrl, Is.EqualTo("https://api.example.com/openapi.json"));
        Assert.That(result.ProfileVersion, Is.EqualTo("HSDS-UK-1.0"));
        Assert.That(result.ProfileReason, Does.Contain("HSDS-UK-1.0"));
        Assert.That(result.DiscoveryReason, Does.Contain("Feed spec discovered"));
    }

    [Test]
    public async Task ResolveFromBaseUrlAsync_WhenNoSchemaUrlFound_ReturnsDefaultProfileVersion()
    {
        // Arrange
        _profileDiscoveryMock
            .Setup(x => x.DiscoverAsync("https://api.example.com", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProfileDiscoveryResult
            {
                Url = null,
                Reason = "Defaulted to HSDS-UK 1.0 (base URL request failed)",
                BaseUrlResponseContent = null,
                HasExplicitOpenApiUrl = false
            });

        _openApiDiscoveryMock
            .Setup(x => x.FindOpenApiSpecAsync("https://api.example.com", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);

        // Act
        var result = await _service.ResolveFromBaseUrlAsync("https://api.example.com");

        // Assert
        Assert.That(result.OpenApiSchemaUrl, Is.Null);
        Assert.That(result.ProfileVersion, Is.EqualTo("HSDS-UK-1.0"));
        Assert.That(result.ProfileReason, Does.Contain("HSDS-UK-1.0"));
    }
}
