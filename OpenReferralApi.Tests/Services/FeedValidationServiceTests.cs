using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using Moq;
using OpenReferralApi.Core.Services;

namespace OpenReferralApi.Tests.Services;

[TestFixture]
public class FeedValidationServiceTests
{
    private Mock<IOpenApiValidationService> _validationServiceMock;
    private Mock<ILogger<FeedValidationService>> _loggerMock;

    [SetUp]
    public void Setup()
    {
        _validationServiceMock = new Mock<IOpenApiValidationService>();
        _loggerMock = new Mock<ILogger<FeedValidationService>>();
    }

    private FeedValidationService CreateService()
    {
        var mongoClientMock = new Mock<IMongoClient>();
        var mongoDatabaseMock = new Mock<IMongoDatabase>();
        var collectionMock = new Mock<IMongoCollection<ServiceFeed>>();
        var databaseOptions = Options.Create(new DatabaseOptions
        {
            DatabaseName = "test-db",
            ServicesCollection = "services"
        });

        mongoClientMock
            .Setup(x => x.GetDatabase("test-db", null))
            .Returns(mongoDatabaseMock.Object);

        mongoDatabaseMock
            .Setup(x => x.GetCollection<ServiceFeed>("services", null))
            .Returns(collectionMock.Object);

        return new FeedValidationService(
            mongoClientMock.Object,
            databaseOptions,
            _validationServiceMock.Object,
            _loggerMock.Object);
    }

    private FeedValidationService CreateService(out Mock<IMongoCollection<ServiceFeed>> collectionMock)
    {
        var mongoClientMock = new Mock<IMongoClient>();
        var mongoDatabaseMock = new Mock<IMongoDatabase>();
        collectionMock = new Mock<IMongoCollection<ServiceFeed>>();
        var databaseOptions = Options.Create(new DatabaseOptions
        {
            DatabaseName = "test-db",
            ServicesCollection = "services"
        });

        mongoClientMock
            .Setup(x => x.GetDatabase("test-db", null))
            .Returns(mongoDatabaseMock.Object);

        mongoDatabaseMock
            .Setup(x => x.GetCollection<ServiceFeed>("services", null))
            .Returns(collectionMock.Object);

        return new FeedValidationService(
            mongoClientMock.Object,
            databaseOptions,
            _validationServiceMock.Object,
            _loggerMock.Object);
    }

    [Test]
    public async Task ValidateSingleFeedAsync_WithValidFeed_ReturnsSuccessResult()
    {
        // Arrange
        var service = CreateService();
        var feed = new ServiceFeed { Id = "1", UrlField = "https://example.com", Service = null };

        var validationResult = new OpenApiValidationResult
        {
            IsValid = true,
            Duration = TimeSpan.FromSeconds(2),
            SpecificationValidation = new OpenApiSpecificationValidation { Errors = new List<ValidationError>() },
            EndpointTests = new List<EndpointTestResult>
            {
                new EndpointTestResult
                {
                    Path = "/services",
                    Method = "GET",
                    TestResults = new List<HttpTestResult>
                    {
                        new HttpTestResult { IsSuccessStatusCode = true }
                    }
                }
            }
        };

        _validationServiceMock
            .Setup(x => x.ValidateOpenApiSpecificationAsync(It.IsAny<OpenApiValidationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(validationResult);

        // Act
        var result = await service.ValidateSingleFeedAsync(feed);

        // Assert
        Assert.That(result.IsUp, Is.True);
        Assert.That(result.IsValid, Is.True);
        Assert.That(result.FeedId, Is.EqualTo("1"));
        Assert.That(result.ErrorMessage, Is.Null.Or.Empty);
        Assert.That(result.ResponseTimeMs, Is.GreaterThan(1900).And.LessThan(2100));
    }

    [Test]
    public async Task ValidateSingleFeedAsync_WithInvalidFeed_ReturnsInvalidResult()
    {
        // Arrange
        var service = CreateService();
        var feed = new ServiceFeed { Id = "1", UrlField = "https://example.com" };

        var validationResult = new OpenApiValidationResult
        {
            IsValid = false,
            Duration = TimeSpan.FromSeconds(1),
            SpecificationValidation = new OpenApiSpecificationValidation
            {
                Errors = new List<ValidationError>
                {
                    new ValidationError { Path = "/paths", Message = "Invalid path" },
                    new ValidationError { Path = "/definitions", Message = "Invalid schema" }
                }
            },
            EndpointTests = new List<EndpointTestResult>
            {
                new EndpointTestResult
                {
                    Path = "/services",
                    Method = "GET",
                    TestResults = new List<HttpTestResult>
                    {
                        new HttpTestResult
                        {
                            IsSuccessStatusCode = true,
                            ValidationResult = new ValidationResult
                            {
                                IsValid = false,
                                Errors = new List<ValidationError>
                                {
                                    new ValidationError { Path = "/paths", Message = "Invalid path" },
                                    new ValidationError { Path = "/definitions", Message = "Invalid schema" }
                                }
                            }
                        }
                    }
                }
            }
        };

        _validationServiceMock
            .Setup(x => x.ValidateOpenApiSpecificationAsync(It.IsAny<OpenApiValidationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(validationResult);

        // Act
        var result = await service.ValidateSingleFeedAsync(feed);

        // Assert
        Assert.That(result.IsUp, Is.True);
        Assert.That(result.IsValid, Is.False);
        Assert.That(result.ValidationErrorCount, Is.EqualTo(2));
        Assert.That(result.ErrorMessage, Does.Contain("Invalid path"));
    }

    [Test]
    public async Task ValidateSingleFeedAsync_WithInvalidFeed_UsesFlattenedEndpointValidationErrors()
    {
        // Arrange
        var service = CreateService();
        var feed = new ServiceFeed { Id = "1", UrlField = "https://example.com" };

        var endpoint = new EndpointTestResult
        {
            Path = "/services",
            Method = "GET",
            TestResults = new List<HttpTestResult>
            {
                new HttpTestResult { IsSuccessStatusCode = true }
            }
        };
        endpoint.ValidationErrors = new List<ValidationError>
        {
            new() { Path = "/services/name", Message = "missing name", ErrorCode = "E1", Severity = "Error" },
            new() { Path = "/services/id", Message = "missing id", ErrorCode = "E2", Severity = "Error" }
        };

        var validationResult = new OpenApiValidationResult
        {
            IsValid = false,
            Duration = TimeSpan.FromSeconds(1),
            EndpointTests = new List<EndpointTestResult> { endpoint }
        };

        _validationServiceMock
            .Setup(x => x.ValidateOpenApiSpecificationAsync(It.IsAny<OpenApiValidationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(validationResult);

        // Act
        var result = await service.ValidateSingleFeedAsync(feed);

        // Assert
        Assert.That(result.ValidationErrorCount, Is.EqualTo(2));
        Assert.That(result.ErrorMessage, Does.Contain("missing name"));
    }

    [Test]
    public async Task ValidateSingleFeedAsync_WithHttpError_ReturnsDownFeed()
    {
        // Arrange
        var service = CreateService();
        var feed = new ServiceFeed { Id = "1", UrlField = "https://invalid.example.com" };

        _validationServiceMock
            .Setup(x => x.ValidateOpenApiSpecificationAsync(It.IsAny<OpenApiValidationRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Connection timeout"));

        // Act
        var result = await service.ValidateSingleFeedAsync(feed);

        // Assert
        Assert.That(result.IsUp, Is.False);
        Assert.That(result.IsValid, Is.False);
        Assert.That(result.ErrorMessage, Does.Contain("HTTP error"));
    }

    [Test]
    public async Task ValidateSingleFeedAsync_WithTimeout_ReturnsDownFeed()
    {
        // Arrange
        var service = CreateService();
        var feed = new ServiceFeed { Id = "1", UrlField = "https://slow.example.com" };

        _validationServiceMock
            .Setup(x => x.ValidateOpenApiSpecificationAsync(It.IsAny<OpenApiValidationRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TaskCanceledException("Request timed out"));

        // Act
        var result = await service.ValidateSingleFeedAsync(feed);

        // Assert
        Assert.That(result.IsUp, Is.False);
        Assert.That(result.IsValid, Is.False);
        Assert.That(result.ErrorMessage, Does.Contain("timed out"));
    }

    [Test]
    public async Task ValidateSingleFeedAsync_WithUnexpectedError_ReturnsDownFeed()
    {
        // Arrange
        var service = CreateService();
        var feed = new ServiceFeed { Id = "1", UrlField = "https://example.com" };

        _validationServiceMock
            .Setup(x => x.ValidateOpenApiSpecificationAsync(It.IsAny<OpenApiValidationRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("Unexpected failure"));

        // Act
        var result = await service.ValidateSingleFeedAsync(feed);

        // Assert
        Assert.That(result.IsUp, Is.False);
        Assert.That(result.IsValid, Is.False);
        Assert.That(result.ErrorMessage, Does.Contain("Unexpected error"));
    }

    [Test]
    public async Task ValidateAndUpdateFeedAsync_WithMongoPipeline_ValidatesAndPersistsStatus()
    {
        // Arrange
        var service = CreateService(out var collectionMock);
        var feed = new ServiceFeed
        {
            Id = "507f1f77bcf86cd799439011",
            UrlField = "https://example.com/openapi",
            ActiveField = true
        };

        var existingFeed = new ServiceFeed
        {
            Id = feed.Id,
            UrlField = feed.UrlField,
            ActiveField = true,
            StatusIsUp = false,
            StatusIsValid = false,
            StatusOverall = false
        };

        var cursorMock = new Mock<IAsyncCursor<ServiceFeed>>();
        cursorMock.SetupSequence(c => c.MoveNextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(true)
            .ReturnsAsync(false);
        cursorMock.SetupSequence(c => c.MoveNext(It.IsAny<CancellationToken>()))
            .Returns(true)
            .Returns(false);
        cursorMock.SetupGet(c => c.Current)
            .Returns(new List<ServiceFeed> { existingFeed });

        collectionMock
            .Setup(c => c.FindAsync(
                It.IsAny<FilterDefinition<ServiceFeed>>(),
                It.IsAny<FindOptions<ServiceFeed, ServiceFeed>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(cursorMock.Object);

        collectionMock
            .Setup(c => c.UpdateOneAsync(
                It.IsAny<FilterDefinition<ServiceFeed>>(),
                It.IsAny<UpdateDefinition<ServiceFeed>>(),
                It.IsAny<UpdateOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<UpdateResult>());

        _validationServiceMock
            .Setup(x => x.ValidateOpenApiSpecificationAsync(It.IsAny<OpenApiValidationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OpenApiValidationResult
            {
                IsValid = true,
                Duration = TimeSpan.FromMilliseconds(250),
                SpecificationValidation = new OpenApiSpecificationValidation { Errors = new List<ValidationError>() },
                EndpointTests = new List<EndpointTestResult>
                {
                    new()
                    {
                        Path = "/services",
                        Method = "GET",
                        ValidationErrors = new List<ValidationError>(),
                        TestResults = new List<HttpTestResult>
                        {
                            new() { IsSuccessStatusCode = true }
                        }
                    }
                }
            });

        // Act
        var result = await service.ValidateAndUpdateFeedAsync(feed, CancellationToken.None);

        // Assert
        Assert.That(result.FeedId, Is.EqualTo(feed.Id));
        Assert.That(result.IsUp, Is.True);
        Assert.That(result.IsValid, Is.True);
        Assert.That(result.ResponseTimeMs, Is.GreaterThan(0));

        _validationServiceMock.Verify(
            x => x.ValidateOpenApiSpecificationAsync(It.IsAny<OpenApiValidationRequest>(), It.IsAny<CancellationToken>()),
            Times.Once);

        collectionMock.Verify(
            c => c.FindAsync(
                It.IsAny<FilterDefinition<ServiceFeed>>(),
                It.IsAny<FindOptions<ServiceFeed, ServiceFeed>>(),
                It.IsAny<CancellationToken>()),
            Times.Once);

        collectionMock.Verify(
            c => c.UpdateOneAsync(
                It.IsAny<FilterDefinition<ServiceFeed>>(),
                It.IsAny<UpdateDefinition<ServiceFeed>>(),
                It.IsAny<UpdateOptions>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }
}