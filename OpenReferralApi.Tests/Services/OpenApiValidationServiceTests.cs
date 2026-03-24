using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Newtonsoft.Json.Schema;
using OpenReferralApi.Core.Services;

namespace OpenReferralApi.Tests.Services;

[TestFixture]
public class OpenApiValidationServiceTests
{
    private Mock<ILogger<OpenApiValidationService>> _loggerMock;
    private Mock<IJsonValidatorService> _jsonValidatorServiceMock;
    private Mock<ISchemaResolverService> _schemaResolverServiceMock;
    private Mock<IProfileDiscoveryService> _profileDiscoveryServiceMock;
    private Mock<IOpenApiDiscoveryService> _feedSpecDiscoveryMock;
    private IOptions<AuthenticationOptions> _authOptions;
    private IOptions<OpenApiValidationServerOptions> _openApiValidationServerOptions;
    private HttpClient _httpClient;
    private OpenApiValidationService _service;

    [SetUp]
    public void Setup()
    {
        _loggerMock = new Mock<ILogger<OpenApiValidationService>>();
        _jsonValidatorServiceMock = new Mock<IJsonValidatorService>();
        _schemaResolverServiceMock = new Mock<ISchemaResolverService>();
        _profileDiscoveryServiceMock = new Mock<IProfileDiscoveryService>();
        _feedSpecDiscoveryMock = new Mock<IOpenApiDiscoveryService>();
        _profileDiscoveryServiceMock
            .Setup(s => s.DiscoverAsync(It.IsAny<string>(), It.IsAny<DataSourceAuthentication?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProfileDiscoveryResult
            {
                Url = null,
                Reason = "No version or openapi_url found in '/' response"
            });
        _feedSpecDiscoveryMock
            .Setup(s => s.FindOpenApiSpecAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);

        _jsonValidatorServiceMock
            .Setup(service => service.ValidateAsync(It.IsAny<ValidationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ValidationResult
            {
                IsValid = true,
                Errors = new List<OpenReferralApi.Core.Models.Validation.ValidationError>(),
                SchemaVersion = "test",
                Duration = TimeSpan.Zero
            });

        _schemaResolverServiceMock
            .Setup(service => service.CreateSchemaFromJsonAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DataSourceAuthentication>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string schemaJson, string documentUri, DataSourceAuthentication auth, CancellationToken ct) => JSchema.Parse(schemaJson));

        _schemaResolverServiceMock
            .Setup(service => service.CreateSchemaFromJsonAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string schemaJson, CancellationToken ct) => JSchema.Parse(schemaJson));

        // Mock ResolveAsync method for OpenAPI document resolution
        _schemaResolverServiceMock
            .Setup(service => service.ResolveAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DataSourceAuthentication>()))
            .ReturnsAsync((string schema, string baseUri, DataSourceAuthentication auth) => schema);

        var mockHandler = new MockHttpMessageHandler();
        _httpClient = TestHttpClientFactory.CreateClient(mockHandler);

        _authOptions = Options.Create(new AuthenticationOptions { AllowUserSuppliedAuth = true });
        _openApiValidationServerOptions = Options.Create(new OpenApiValidationServerOptions
        {
            HsdsValidationMode = HsdsValidationMode.SpecAndFeedRuntimeFast
        });

        _service = new OpenApiValidationService(
            _loggerMock.Object,
            CreateFactory(_httpClient),
            _jsonValidatorServiceMock.Object,
            _schemaResolverServiceMock.Object,
            _profileDiscoveryServiceMock.Object,
            _feedSpecDiscoveryMock.Object,
            _authOptions,
            specificationOptions: Options.Create(new SpecificationOptions
            {
                Urls = new Dictionary<string, string>
                {
                    ["HSDS-UK-1.0"] = "https://openreferraluk.org/specifications/1.0/openapi.json",
                    ["HSDS-UK-3.0"] = "https://openreferraluk.org/specifications/3.0/openapi.json"
                }
            }),
            openApiValidationServerOptions: _openApiValidationServerOptions);
    }

    [TearDown]
    public void TearDown()
    {
        _httpClient?.Dispose();
    }

    #region Basic Response Handling

    [Test]
    public async Task ValidateOpenApiSpecificationAsync_ReturnsValidationResult()
    {
        // Arrange
        var json = CreateOpenApi30Spec();
        var request = new OpenApiValidationRequest
        {
            OpenApiSchema = new OpenApiSchema
            {
                Url = "https://example.com/openapi.json"
            }
        };
        SetupHttpMock(json);

        // Act
        var result = await _service.ValidateOpenApiSpecificationAsync(request);

        // Assert
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task ValidateOpenApiSpecificationAsync_IncludesMetadata()
    {
        // Arrange
        var json = CreateOpenApi30Spec();
        var request = new OpenApiValidationRequest
        {
            OpenApiSchema = new OpenApiSchema
            {
                Url = "https://api.example.com/openapi.json"
            },
            BaseUrl = "https://api.example.com"
        };
        SetupHttpMock(json);

        // Act
        var result = await _service.ValidateOpenApiSpecificationAsync(request);

        // Assert
        Assert.That(result.Metadata, Is.Not.Null);
        Assert.That(result.Metadata!.BaseUrl, Is.EqualTo("https://api.example.com"));
    }

    [Test]
    public async Task ValidateOpenApiSpecificationAsync_MeasuresDuration()
    {
        // Arrange
        var json = CreateOpenApi30Spec();
        var request = new OpenApiValidationRequest
        {
            OpenApiSchema = new OpenApiSchema
            {
                Url = "https://example.com/openapi.json"
            }
        };
        SetupHttpMock(json);

        // Act
        var result = await _service.ValidateOpenApiSpecificationAsync(request);

        // Assert
        Assert.That(result.Duration, Is.GreaterThan(TimeSpan.Zero));
    }

    [Test]
    public async Task ValidateOpenApiSpecificationAsync_IncludesSummary()
    {
        // Arrange
        var json = CreateOpenApi30Spec();
        var request = new OpenApiValidationRequest
        {
            OpenApiSchema = new OpenApiSchema
            {
                Url = "https://example.com/openapi.json"
            }
        };
        SetupHttpMock(json);

        // Act
        var result = await _service.ValidateOpenApiSpecificationAsync(request);

        // Assert
        Assert.That(result.Summary, Is.Not.Null);
    }

    #endregion

    #region OpenAPI Version Detection

    [Test]
    public async Task ValidateOpenApiSpecificationAsync_DetectsOpenApi30Version()
    {
        // Arrange
        var json = CreateOpenApi30Spec();
        var request = new OpenApiValidationRequest
        {
            OpenApiSchema = new OpenApiSchema
            {
                Url = "https://example.com/openapi.json"
            },
            Options = new OpenApiValidationOptions { ValidateSpecification = true }
        };
        SetupHttpMock(json);

        // Act
        var result = await _service.ValidateOpenApiSpecificationAsync(request);

        // Assert
        Assert.That(result.SpecificationValidation, Is.Not.Null);
        Assert.That(result.SpecificationValidation!.OpenApiVersion, Does.Contain("3.0"));
    }

    [Test]
    public async Task ValidateOpenApiSpecificationAsync_DetectsSwagger20Version()
    {
        // Arrange
        var json = CreateSwagger20Spec();
        var request = new OpenApiValidationRequest
        {
            OpenApiSchema = new OpenApiSchema
            {
                Url = "https://example.com/swagger.json"
            },
            Options = new OpenApiValidationOptions { ValidateSpecification = true }
        };
        SetupHttpMock(json);

        // Act
        var result = await _service.ValidateOpenApiSpecificationAsync(request);

        // Assert
        Assert.That(result.SpecificationValidation, Is.Not.Null);
        Assert.That(result.SpecificationValidation!.Errors, Is.Not.Null);
    }

    #endregion

    #region Validation Options

    [Test]
    public async Task ValidateOpenApiSpecificationAsync_SkipsValidationWhenDisabled()
    {
        // Arrange
        var json = CreateOpenApi30Spec();
        var request = new OpenApiValidationRequest
        {
            OpenApiSchema = new OpenApiSchema
            {
                Url = "https://example.com/openapi.json"
            },
            Options = new OpenApiValidationOptions { ValidateSpecification = false }
        };
        SetupHttpMock(json);

        // Act
        var result = await _service.ValidateOpenApiSpecificationAsync(request);

        // Assert
        Assert.That(result.SpecificationValidation, Is.Null);
    }

    [Test]
    public async Task ValidateOpenApiSpecificationAsync_PerformsValidationWhenEnabled()
    {
        // Arrange
        var json = CreateOpenApi30Spec();
        var request = new OpenApiValidationRequest
        {
            OpenApiSchema = new OpenApiSchema
            {
                Url = "https://example.com/openapi.json"
            },
            Options = new OpenApiValidationOptions { ValidateSpecification = true }
        };
        SetupHttpMock(json);

        // Act
        var result = await _service.ValidateOpenApiSpecificationAsync(request);

        // Assert
        Assert.That(result.SpecificationValidation, Is.Not.Null);
    }

    [Test]
    public async Task ValidateOpenApiSpecificationAsync_NormalizesAndDeduplicatesSpecificationValidationErrorsAndWarnings()
    {
        // Arrange
        var json = CreateOpenApi30Spec();
        var request = new OpenApiValidationRequest
        {
            OpenApiSchema = new OpenApiSchema
            {
                Url = "https://example.com/openapi.json"
            },
            Options = new OpenApiValidationOptions { ValidateSpecification = true, TestEndpoints = false }
        };

        _jsonValidatorServiceMock
            .Setup(service => service.ValidateAsync(It.IsAny<ValidationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ValidationResult
            {
                IsValid = false,
                Errors = new List<OpenReferralApi.Core.Models.Validation.ValidationError>
                {
                    new()
                    {
                        Path = "paths./items[0].name",
                        Message = "paths./items[0].name is required",
                        ErrorCode = "VALIDATION_ERROR",
                        Severity = "Error"
                    },
                    new()
                    {
                        Path = "paths./items[1].name",
                        Message = "paths./items[1].name is required",
                        ErrorCode = "VALIDATION_ERROR",
                        Severity = "Error"
                    },
                    new()
                    {
                        Path = "paths./items[0].metadata",
                        Message = "paths./items[0].metadata is not expected",
                        ErrorCode = "VALIDATION_WARNING",
                        Severity = "Warning"
                    },
                    new()
                    {
                        Path = "paths./items[2].metadata",
                        Message = "paths./items[2].metadata is not expected",
                        ErrorCode = "VALIDATION_WARNING",
                        Severity = "Warning"
                    }
                }
            });

        SetupHttpMock(json);

        // Act
        var result = await _service.ValidateOpenApiSpecificationAsync(request);
        var errors = result.SpecificationValidation!.Errors;

        // Assert
        Assert.That(errors, Has.Count.EqualTo(2));
        Assert.That(errors.All(e => !e.Path.Contains("[")), Is.True);
        Assert.That(errors.All(e => !e.Message.Contains("[")), Is.True);
    }

    [Test]
    public async Task ValidateOpenApiSpecificationAsync_DeduplicationUsesPathOnlyAndKeepsFirstError()
    {
        // Arrange
        var json = CreateOpenApi30Spec();
        var request = new OpenApiValidationRequest
        {
            OpenApiSchema = new OpenApiSchema
            {
                Url = "https://example.com/openapi.json"
            },
            Options = new OpenApiValidationOptions { ValidateSpecification = true, TestEndpoints = false }
        };

        _jsonValidatorServiceMock
            .Setup(service => service.ValidateAsync(It.IsAny<ValidationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ValidationResult
            {
                IsValid = false,
                Errors = new List<OpenReferralApi.Core.Models.Validation.ValidationError>
                {
                    new()
                    {
                        Path = "items[0].name",
                        Message = "items[0].name is required",
                        ErrorCode = "VALIDATION_ERROR",
                        Severity = "Error"
                    },
                    new()
                    {
                        Path = "items[1].name",
                        Message = "items[1].name is required",
                        ErrorCode = "VALIDATION_WARNING",
                        Severity = "Warning"
                    }
                }
            });

        SetupHttpMock(json);

        // Act
        var result = await _service.ValidateOpenApiSpecificationAsync(request);
        var errors = result.SpecificationValidation!.Errors;

        // Assert
        Assert.That(errors, Has.Count.EqualTo(1), "Entries with the same normalized path should collapse to the first error");
        Assert.That(errors[0].Path, Is.EqualTo("items.name"));
        Assert.That(errors[0].Severity, Is.EqualTo("Error"));
        Assert.That(errors[0].ErrorCode, Is.EqualTo("VALIDATION_ERROR"));
        Assert.That(errors[0].Message, Is.EqualTo("items.name is required"));
    }

    [Test]
    public async Task ValidateOpenApiSpecificationAsync_UsesDefaultOptionsWhenNull()
    {
        // Arrange
        var json = CreateOpenApi30Spec();
        var request = new OpenApiValidationRequest
        {
            OpenApiSchema = new OpenApiSchema
            {
                Url = "https://example.com/openapi.json"
            }
        };
        SetupHttpMock(json);

        // Act
        var result = await _service.ValidateOpenApiSpecificationAsync(request);

        // Assert
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task ValidateOpenApiSpecificationAsync_WhenValidateSpecificationTrueAndDeclaredSchemaUnsupported_ReturnsFailureErrors()
    {
        // Arrange
        var request = new OpenApiValidationRequest
        {
            OpenApiSchema = new OpenApiSchema
            {
                Url = "https://example.com/openapi.json"
            },
            Options = new OpenApiValidationOptions
            {
                ValidateSpecification = true,
                TestEndpoints = false
            }
        };

        var openApiWithUnsupportedDialect = @"{
            ""openapi"": ""3.1.0"",
            ""jsonSchemaDialect"": ""https://example.com/unknown-schema"",
            ""info"": {
                ""title"": ""Test API"",
                ""version"": ""1.0.0""
            },
            ""paths"": {
                ""/test"": {
                    ""get"": {
                        ""responses"": {
                            ""200"": { ""description"": ""OK"" }
                        }
                    }
                }
            }
        }";

        SetupHttpMock(openApiWithUnsupportedDialect);

        // Act
        var result = await _service.ValidateOpenApiSpecificationAsync(request);

        // Assert
        Assert.That(result.IsValid, Is.False);
        Assert.That(result.SpecificationValidation, Is.Not.Null);
        Assert.That(result.SpecificationValidation!.IsValid, Is.False);
        Assert.That(result.SpecificationValidation.Errors.Any(e => e.ErrorCode == "UNSUPPORTED_SCHEMA_VERSION" && string.Equals(e.Severity, "Error", StringComparison.OrdinalIgnoreCase)), Is.True);
    }

    [Test]
    public async Task ValidateOpenApiSpecificationAsync_FailsWhenRequiredHsdsEndpointMissing()
    {
        // Arrange
        var feedSpecUrl = "https://feed.example.com/openapi.json";
        var hsdsSpecUrl = "https://openreferraluk.org/specifications/3.0/openapi.json";
        var request = new OpenApiValidationRequest
        {
            OpenApiSchema = new OpenApiSchema { Url = feedSpecUrl },
            Options = new OpenApiValidationOptions { ValidateSpecification = true, TestEndpoints = false },
            ProfileReason = "Standard version [user: 3.0] read from '/' endpoint"
        };

        SetupHttpMock((httpRequest, ct) =>
        {
            var requestUrl = httpRequest.RequestUri?.ToString();
            if (string.Equals(requestUrl, feedSpecUrl, StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent(CreateFeedSpecMissingRequiredHsdsEndpoint())
                };
            }

            if (string.Equals(requestUrl, hsdsSpecUrl, StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent(CreateHsdsProfileSpec())
                };
            }

            return new HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
        });

        // Act
        var result = await _service.ValidateOpenApiSpecificationAsync(request);

        // Assert
        Assert.That(result.IsValid, Is.False);
        Assert.That(result.SpecificationValidation, Is.Not.Null);
        Assert.That(result.SpecificationValidation!.Errors.Any(e => e.ErrorCode == "HSDS_MISSING_ENDPOINT"), Is.True);
        Assert.That(result.Metadata?.Profile, Is.EqualTo("3.0"));
    }

    [Test]
    public async Task ValidateOpenApiSpecificationAsync_ReportsAdditionalHsdsEndpointAsInfoOnly()
    {
        // Arrange
        var feedSpecUrl = "https://feed.example.com/openapi.json";
        var hsdsSpecUrl = "https://openreferraluk.org/specifications/3.0/openapi.json";
        var request = new OpenApiValidationRequest
        {
            OpenApiSchema = new OpenApiSchema { Url = feedSpecUrl },
            Options = new OpenApiValidationOptions { ValidateSpecification = true, TestEndpoints = false },
            ProfileReason = "Standard version [user: 3.0] read from '/' endpoint"
        };

        SetupHttpMock((httpRequest, ct) =>
        {
            var requestUrl = httpRequest.RequestUri?.ToString();
            if (string.Equals(requestUrl, feedSpecUrl, StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent(CreateFeedSpecWithAdditionalEndpoint())
                };
            }

            if (string.Equals(requestUrl, hsdsSpecUrl, StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent(CreateHsdsProfileSpec())
                };
            }

            return new HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
        });

        // Act
        var result = await _service.ValidateOpenApiSpecificationAsync(request);

        // Assert
        Assert.That(result.IsValid, Is.True);
        Assert.That(result.SpecificationValidation, Is.Not.Null);
        Assert.That(result.SpecificationValidation!.Errors.Any(e => e.ErrorCode == "HSDS_ADDITIONAL_ENDPOINT"), Is.True);
        Assert.That(result.SpecificationValidation.Errors.Any(e =>
            e.ErrorCode == "HSDS_ADDITIONAL_ENDPOINT" &&
            string.Equals(e.Severity, "Info", StringComparison.OrdinalIgnoreCase)), Is.True);
    }

    [Test]
    public async Task ValidateOpenApiSpecificationAsync_FailsWhenRequiredHsdsRequestFieldMissing()
    {
        // Arrange
        var feedSpecUrl = "https://feed.example.com/openapi.json";
        var hsdsSpecUrl = "https://openreferraluk.org/specifications/3.0/openapi.json";
        var request = new OpenApiValidationRequest
        {
            OpenApiSchema = new OpenApiSchema { Url = feedSpecUrl },
            Options = new OpenApiValidationOptions { ValidateSpecification = true, TestEndpoints = false },
            ProfileReason = "Standard version [user: 3.0] read from '/' endpoint"
        };

        SetupHttpMock((httpRequest, ct) =>
        {
            var requestUrl = httpRequest.RequestUri?.ToString();
            if (string.Equals(requestUrl, feedSpecUrl, StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent(CreateFeedSpecMissingRequiredHsdsRequestField())
                };
            }

            if (string.Equals(requestUrl, hsdsSpecUrl, StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent(CreateHsdsProfileSpecWithRequestBody())
                };
            }

            return new HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
        });

        // Act
        var result = await _service.ValidateOpenApiSpecificationAsync(request);

        // Assert
        Assert.That(result.IsValid, Is.False);
        Assert.That(result.SpecificationValidation, Is.Not.Null);
        Assert.That(result.SpecificationValidation!.Errors.Any(e => e.ErrorCode == "HSDS_MISSING_REQUIRED_REQUEST_FIELD"), Is.True);
    }

    [Test]
    public async Task ValidateOpenApiSpecificationAsync_ReportsAdditionalHsdsRequestFieldAsInfoOnly()
    {
        // Arrange
        var feedSpecUrl = "https://feed.example.com/openapi.json";
        var hsdsSpecUrl = "https://openreferraluk.org/specifications/3.0/openapi.json";
        var request = new OpenApiValidationRequest
        {
            OpenApiSchema = new OpenApiSchema { Url = feedSpecUrl },
            Options = new OpenApiValidationOptions { ValidateSpecification = true, TestEndpoints = false },
            ProfileReason = "Standard version [user: 3.0] read from '/' endpoint"
        };

        SetupHttpMock((httpRequest, ct) =>
        {
            var requestUrl = httpRequest.RequestUri?.ToString();
            if (string.Equals(requestUrl, feedSpecUrl, StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent(CreateFeedSpecWithAdditionalHsdsRequestField())
                };
            }

            if (string.Equals(requestUrl, hsdsSpecUrl, StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent(CreateHsdsProfileSpecWithRequestBody())
                };
            }

            return new HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
        });

        // Act
        var result = await _service.ValidateOpenApiSpecificationAsync(request);

        // Assert
        Assert.That(result.IsValid, Is.True);
        Assert.That(result.SpecificationValidation, Is.Not.Null);
        Assert.That(result.SpecificationValidation!.Errors.Any(e => e.ErrorCode == "HSDS_ADDITIONAL_REQUEST_FIELD"), Is.True);
        Assert.That(result.SpecificationValidation.Errors.Any(e =>
            e.ErrorCode == "HSDS_ADDITIONAL_REQUEST_FIELD" &&
            string.Equals(e.Severity, "Info", StringComparison.OrdinalIgnoreCase)), Is.True);
    }

    [Test]
    public async Task ValidateOpenApiSpecificationAsync_UsesWarmupCachedProfileBeforeExternalProfileFetch()
    {
        // Arrange
        var feedSpecUrl = "https://feed.example.com/openapi.json";
        var hsdsSpecUrl = "https://openreferraluk.org/specifications/3.0/openapi.json";
        var request = new OpenApiValidationRequest
        {
            OpenApiSchema = new OpenApiSchema { Url = feedSpecUrl },
            Options = new OpenApiValidationOptions { ValidateSpecification = true, TestEndpoints = false },
            ProfileReason = "Standard version [user: 3.0] read from '/' endpoint"
        };

        _schemaResolverServiceMock
            .Setup(service => service.ResolveAsync(It.Is<string>(s => s.Contains("\"$ref\"", StringComparison.Ordinal) && s.Contains(hsdsSpecUrl, StringComparison.OrdinalIgnoreCase)), hsdsSpecUrl, It.IsAny<DataSourceAuthentication>()))
            .ReturnsAsync(CreateHsdsProfileSpec());

        var requestCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        SetupHttpMock((httpRequest, ct) =>
        {
            var requestUrl = httpRequest.RequestUri?.ToString() ?? string.Empty;
            requestCounts[requestUrl] = requestCounts.TryGetValue(requestUrl, out var count) ? count + 1 : 1;

            if (string.Equals(requestUrl, feedSpecUrl, StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent(CreateFeedSpecMissingRequiredHsdsEndpoint())
                };
            }

            if (string.Equals(requestUrl, hsdsSpecUrl, StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
            }

            return new HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
        });

        // Act
        var result = await _service.ValidateOpenApiSpecificationAsync(request);

        // Assert
        Assert.That(result.IsValid, Is.False);
        Assert.That(result.SpecificationValidation, Is.Not.Null);
        Assert.That(result.SpecificationValidation!.Errors.Any(e => e.ErrorCode == "HSDS_MISSING_ENDPOINT"), Is.True);

        Assert.That(requestCounts.TryGetValue(feedSpecUrl, out var feedFetchCount), Is.True);
        Assert.That(feedFetchCount, Is.EqualTo(1));
        Assert.That(requestCounts.ContainsKey(hsdsSpecUrl), Is.False, "HSDS profile URL should not be fetched when warmup-path resolver returns it.");
    }

    [Test]
    public async Task ValidateOpenApiSpecificationAsync_FastMode_DoesNotRunFullHsdsRuntimeValidationPass()
    {
        // Arrange
        var feedSpecUrl = "https://feed.example.com/openapi.json";
        var hsdsSpecUrl = "https://openreferraluk.org/specifications/3.0/openapi.json";

        _jsonValidatorServiceMock.Invocations.Clear();
        _jsonValidatorServiceMock
            .Setup(service => service.ValidateAsync(It.IsAny<ValidationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ValidationResult
            {
                IsValid = true,
                Errors = new List<OpenReferralApi.Core.Models.Validation.ValidationError>(),
                SchemaVersion = "test",
                Duration = TimeSpan.Zero
            });

        var request = new OpenApiValidationRequest
        {
            OpenApiSchema = new OpenApiSchema { Url = feedSpecUrl },
            BaseUrl = "https://feed.example.com",
            ProfileReason = "Standard version [user: 3.0] read from '/' endpoint",
            Options = new OpenApiValidationOptions
            {
                ValidateSpecification = true,
                TestEndpoints = true
            }
        };

        SetupHttpMock((httpRequest, ct) =>
        {
            var requestUrl = httpRequest.RequestUri?.ToString() ?? string.Empty;
            if (string.Equals(requestUrl, feedSpecUrl, StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent(CreateFeedSpecPermissiveOrganisationResponse())
                };
            }

            if (string.Equals(requestUrl, hsdsSpecUrl, StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent(CreateHsdsProfileSpec())
                };
            }

            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("[{\"id\":\"1\"}]")
            };
        });

        // Act
        var result = await _service.ValidateOpenApiSpecificationAsync(request);

        // Assert
        Assert.That(result.IsValid, Is.True);
        _jsonValidatorServiceMock.Verify(service => service.ValidateAsync(It.IsAny<ValidationRequest>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Test]
    public async Task ValidateOpenApiSpecificationAsync_FullMode_RunsHsdsRuntimeValidationAndCanFail()
    {
        // Arrange
        var feedSpecUrl = "https://feed.example.com/openapi.json";
        var hsdsSpecUrl = "https://openreferraluk.org/specifications/3.0/openapi.json";

        var hsdsComplianceServiceMock = new Mock<IHsdsComplianceService>();
        hsdsComplianceServiceMock
            .Setup(s => s.ExtractClaimedProfileVersion(It.IsAny<string>(), It.IsAny<string>()))
            .Returns("3.0");

        hsdsComplianceServiceMock
            .Setup(s => s.TryGetKnownHsdsSchemaUrl("3.0", out hsdsSpecUrl))
            .Returns(true);

        hsdsComplianceServiceMock
            .Setup(s => s.CompareFeedSpecAgainstHsdsProfile(It.IsAny<Newtonsoft.Json.Linq.JObject>(), It.IsAny<Newtonsoft.Json.Linq.JObject>()))
            .Returns(new List<OpenReferralApi.Core.Models.Validation.ValidationError>());

        hsdsComplianceServiceMock
            .Setup(s => s.ValidateEndpointResponsesAgainstHsdsProfileAsync(
                It.IsAny<List<EndpointTestResult>>(),
                It.IsAny<Newtonsoft.Json.Linq.JObject>(),
                It.IsAny<OpenApiValidationOptions>(),
                It.IsAny<CancellationToken>()))
            .Callback<List<EndpointTestResult>, Newtonsoft.Json.Linq.JObject, OpenApiValidationOptions, CancellationToken>((tests, _, _, _) =>
            {
                if (tests.Count > 0)
                {
                    tests[0].Status = EndpointTestStatus.FailedValidation;
                }
            })
            .Returns(Task.CompletedTask);

        var endpointTestingServiceMock = new Mock<IEndpointTestingService>();
        endpointTestingServiceMock
            .Setup(s => s.TestEndpointsAsync(
                It.IsAny<Newtonsoft.Json.Linq.JObject>(),
                It.IsAny<string>(),
                It.IsAny<OpenApiValidationOptions>(),
                It.IsAny<DataSourceAuthentication>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<EndpointTestResult>
            {
                new()
                {
                    Path = "/organisations",
                    Method = "GET",
                    IsTested = true,
                    Status = EndpointTestStatus.PassedValidation,
                    TestResults = new List<HttpTestResult>
                    {
                        new()
                        {
                            ResponseStatusCode = 200,
                            ResponseBody = "[{\"id\":\"1\"}]",
                            IsSuccessStatusCode = true,
                            ValidationResult = new ValidationResult
                            {
                                IsValid = true,
                                Errors = new List<OpenReferralApi.Core.Models.Validation.ValidationError>(),
                                SchemaVersion = "test",
                                Duration = TimeSpan.Zero
                            }
                        }
                    }
                }
            });

        _jsonValidatorServiceMock.Invocations.Clear();
        _jsonValidatorServiceMock
            .SetupSequence(service => service.ValidateAsync(It.IsAny<ValidationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ValidationResult
            {
                IsValid = true,
                Errors = new List<OpenReferralApi.Core.Models.Validation.ValidationError>(),
                SchemaVersion = "test",
                Duration = TimeSpan.Zero
            })
            .ReturnsAsync(new ValidationResult
            {
                IsValid = true,
                Errors = new List<OpenReferralApi.Core.Models.Validation.ValidationError>(),
                SchemaVersion = "test",
                Duration = TimeSpan.Zero
            })
            .ReturnsAsync(new ValidationResult
            {
                IsValid = false,
                Errors = new List<OpenReferralApi.Core.Models.Validation.ValidationError>
                {
                    new()
                    {
                        Path = "[].name",
                        Message = "Required property 'name' not found",
                        ErrorCode = "VALIDATION_ERROR",
                        Severity = "Error"
                    }
                },
                SchemaVersion = "test",
                Duration = TimeSpan.Zero
            });

        var request = new OpenApiValidationRequest
        {
            OpenApiSchema = new OpenApiSchema { Url = feedSpecUrl },
            BaseUrl = "https://feed.example.com",
            ProfileReason = "Standard version [user: 3.0] read from '/' endpoint",
            Options = new OpenApiValidationOptions
            {
                ValidateSpecification = true,
                TestEndpoints = true
            }
        };

        var fullModeServerOptions = Options.Create(new OpenApiValidationServerOptions
        {
            HsdsValidationMode = HsdsValidationMode.FullHsdsRuntime
        });

        var serviceWithFullMode = new OpenApiValidationService(
            _loggerMock.Object,
            CreateFactory(_httpClient),
            _jsonValidatorServiceMock.Object,
            _schemaResolverServiceMock.Object,
            _profileDiscoveryServiceMock.Object,
            _feedSpecDiscoveryMock.Object,
            _authOptions,
            hsdsComplianceService: hsdsComplianceServiceMock.Object,
            endpointTestingService: endpointTestingServiceMock.Object,
            authenticationValidationService: null,
            openApiBootstrapService: null,
            cacheOptions: null,
            specificationOptions: Options.Create(new SpecificationOptions
            {
                Urls = new Dictionary<string, string>
                {
                    ["HSDS-UK-1.0"] = "https://openreferraluk.org/specifications/1.0/openapi.json",
                    ["HSDS-UK-3.0"] = "https://openreferraluk.org/specifications/3.0/openapi.json"
                }
            }),
            openApiValidationServerOptions: fullModeServerOptions);

        SetupHttpMock((httpRequest, ct) =>
        {
            var requestUrl = httpRequest.RequestUri?.ToString() ?? string.Empty;
            if (string.Equals(requestUrl, feedSpecUrl, StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent(CreateFeedSpecPermissiveOrganisationResponse())
                };
            }

            if (string.Equals(requestUrl, hsdsSpecUrl, StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent(CreateHsdsProfileSpec())
                };
            }

            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("[{\"id\":\"1\"}]")
            };
        });

        // Act
        var result = await serviceWithFullMode.ValidateOpenApiSpecificationAsync(request);

        // Assert
        Assert.That(result.IsValid, Is.False);
        hsdsComplianceServiceMock.Verify(s => s.TryGetKnownHsdsSchemaUrl("3.0", out hsdsSpecUrl), Times.AtLeastOnce);
    }

    [Test]
    public async Task ValidateOpenApiSpecificationAsync_WhenStrictOwnSchemaValidationTrue_FailsEndpointValidation()
    {
        // Arrange
        _jsonValidatorServiceMock
            .Setup(service => service.ValidateAsync(It.IsAny<ValidationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ValidationResult
            {
                IsValid = false,
                Errors = new List<OpenReferralApi.Core.Models.Validation.ValidationError>
                {
                    new()
                    {
                        Path = "[].extra",
                        Message = "Field '[].extra' is not defined in the schema",
                        ErrorCode = "ADDITIONAL_FIELD",
                        Severity = "Warning"
                    }
                },
                SchemaVersion = "test",
                Duration = TimeSpan.Zero
            });

        var request = new OpenApiValidationRequest
        {
            OpenApiSchema = new OpenApiSchema { Url = "https://example.com/openapi.json" },
            BaseUrl = "https://api.example.com",
            Options = new OpenApiValidationOptions
            {
                ValidateSpecification = false,
                TestEndpoints = true
            }
        };

        SetupHttpMock(CreateOpenApi30SpecWithResponseSchema(), endpointResponseBody: "[{\"name\":\"ok\",\"extra\":\"x\"}]");

        // Act — default server setting StrictOwnSchemaValidation = true causes errors
        var result = await _service.ValidateOpenApiSpecificationAsync(request);

        // Assert
        Assert.That(result.IsValid, Is.False);
        Assert.That(result.EndpointTests[0].Status, Is.EqualTo(EndpointTestStatus.FailedValidation));
    }

    [Test]
    public async Task ValidateOpenApiSpecificationAsync_WhenStrictOwnSchemaValidationFalse_ReportsWarningsWithoutFailure()
    {
        // Arrange
        _jsonValidatorServiceMock
            .Setup(service => service.ValidateAsync(It.IsAny<ValidationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ValidationResult
            {
                IsValid = false,
                Errors = new List<OpenReferralApi.Core.Models.Validation.ValidationError>
                {
                    new()
                    {
                        Path = "[].extra",
                        Message = "Field '[].extra' is not defined in the schema",
                        ErrorCode = "ADDITIONAL_FIELD",
                        Severity = "Warning"
                    }
                },
                SchemaVersion = "test",
                Duration = TimeSpan.Zero
            });

        var request = new OpenApiValidationRequest
        {
            OpenApiSchema = new OpenApiSchema { Url = "https://example.com/openapi.json" },
            BaseUrl = "https://api.example.com",
            Options = new OpenApiValidationOptions
            {
                ValidateSpecification = false,
                TestEndpoints = true
            }
        };

        SetupHttpMock(CreateOpenApi30SpecWithResponseSchema(), endpointResponseBody: "[{\"name\":\"ok\",\"extra\":\"x\"}]");

        var lenientValidationOptions = Options.Create(new OpenApiValidationServerOptions { StrictOwnSchemaValidation = false });
        var serviceWithLenientPolicy = new OpenApiValidationService(
            _loggerMock.Object,
            CreateFactory(_httpClient),
            _jsonValidatorServiceMock.Object,
            _schemaResolverServiceMock.Object,
            _profileDiscoveryServiceMock.Object,
            _feedSpecDiscoveryMock.Object,
            _authOptions,
            openApiValidationServerOptions: lenientValidationOptions);

        // Act — server setting StrictOwnSchemaValidation = false downgrades errors to warnings
        var result = await serviceWithLenientPolicy.ValidateOpenApiSpecificationAsync(request);

        // Assert
        Assert.That(result.IsValid, Is.True);
        Assert.That(result.EndpointTests[0].Status, Is.EqualTo(EndpointTestStatus.PassedWithWarnings));
    }

    #endregion

    #region HTTP Response Handling

    [Test]
    public void ValidateOpenApiSpecificationAsync_ThrowsOnHttpNotFound()
    {
        // Arrange
        var request = new OpenApiValidationRequest
        {
            OpenApiSchema = new OpenApiSchema
            {
                Url = "https://example.com/notfound.json"
            }
        };
        
        var mockHandler = new MockHttpMessageHandler((req, ct) =>
            new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
        
        var httpClient = TestHttpClientFactory.CreateClient(mockHandler);
        var service = new OpenApiValidationService(
            _loggerMock.Object, CreateFactory(httpClient), _jsonValidatorServiceMock.Object,
            _schemaResolverServiceMock.Object, _profileDiscoveryServiceMock.Object, _feedSpecDiscoveryMock.Object,
            Options.Create(new AuthenticationOptions { AllowUserSuppliedAuth = true }));

        try
        {
            // Act
            var result = service.ValidateOpenApiSpecificationAsync(request).GetAwaiter().GetResult();

            // Assert
            Assert.That(result.IsValid, Is.False);
            Assert.That(result.Summary, Is.Not.Null);
            Assert.That(result.Metadata, Is.Null);
            Assert.That(result.Notifications, Has.Count.EqualTo(1));
            Assert.That(result.Notifications[0], Does.Contain("Unable to get or resolve the OpenAPI specification"));
            Assert.That(result.Notifications[0], Does.Contain("https://example.com/notfound.json"));
        }
        finally
        {
            httpClient?.Dispose();
        }
    }

    [Test]
    public void ValidateOpenApiSpecificationAsync_ThrowsOnNetworkError()
    {
        // Arrange
        var request = new OpenApiValidationRequest
        {
            OpenApiSchema = new OpenApiSchema
            {
                Url = "https://invalid.example.com/openapi.json"
            }
        };
        
        var mockHandler = new MockHttpMessageHandler((req, ct) =>
            throw new HttpRequestException("Network failed"));
        
        var httpClient = TestHttpClientFactory.CreateClient(mockHandler);
        var service = new OpenApiValidationService(
            _loggerMock.Object, CreateFactory(httpClient), _jsonValidatorServiceMock.Object,
            _schemaResolverServiceMock.Object, _profileDiscoveryServiceMock.Object, _feedSpecDiscoveryMock.Object,
            Options.Create(new AuthenticationOptions { AllowUserSuppliedAuth = true }));

        try
        {
            // Act
            var result = service.ValidateOpenApiSpecificationAsync(request).GetAwaiter().GetResult();

            // Assert
            Assert.That(result.IsValid, Is.False);
            Assert.That(result.Summary, Is.Not.Null);
            Assert.That(result.Metadata, Is.Null);
            Assert.That(result.Notifications, Has.Count.EqualTo(1));
            Assert.That(result.Notifications[0], Does.Contain("Unable to get or resolve the OpenAPI specification"));
            Assert.That(result.Notifications[0], Does.Contain("https://invalid.example.com/openapi.json"));
        }
        finally
        {
            httpClient?.Dispose();
        }
    }

    [Test]
    public void ValidateOpenApiSpecificationAsync_ThrowsOnInvalidJson()
    {
        // Arrange
        var request = new OpenApiValidationRequest
        {
            OpenApiSchema = new OpenApiSchema
            {
                Url = "https://example.com/invalid.json"
            }
        };
        
        var mockHandler = new MockHttpMessageHandler((req, ct) =>
            new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("Not valid JSON at all {{{")
            });
        
        var httpClient = TestHttpClientFactory.CreateClient(mockHandler);
        var service = new OpenApiValidationService(
            _loggerMock.Object, CreateFactory(httpClient), _jsonValidatorServiceMock.Object,
            _schemaResolverServiceMock.Object, _profileDiscoveryServiceMock.Object, _feedSpecDiscoveryMock.Object,
            Options.Create(new AuthenticationOptions { AllowUserSuppliedAuth = true }));

        try
        {
            // Act
            var result = service.ValidateOpenApiSpecificationAsync(request).GetAwaiter().GetResult();

            // Assert
            Assert.That(result.IsValid, Is.False);
            Assert.That(result.Summary, Is.Not.Null);
            Assert.That(result.Metadata, Is.Null);
            Assert.That(result.Notifications, Has.Count.EqualTo(1));
            Assert.That(result.Notifications[0], Does.Contain("Unable to get or resolve the OpenAPI specification"));
            Assert.That(result.Notifications[0], Does.Contain("https://example.com/invalid.json"));
        }
        finally
        {
            httpClient?.Dispose();
        }
    }

    [Test]
    public async Task ValidateOpenApiSpecificationAsync_FallsBackToHsdsProfileWhenFeedOpenApiFetchFails()
    {
        // Arrange
        var feedSpecUrl = "https://feed.example.com/openapi-missing.json";
        var hsdsSpecUrl = "https://openreferraluk.org/specifications/3.0/openapi.json";

        var request = new OpenApiValidationRequest
        {
            OpenApiSchema = new OpenApiSchema
            {
                Url = feedSpecUrl
            },
            ProfileReason = "Standard version [user: 3.0] read from '/' endpoint",
            Options = new OpenApiValidationOptions
            {
                ValidateSpecification = true,
                TestEndpoints = false
            }
        };

        SetupHttpMock((httpRequest, ct) =>
        {
            var requestUrl = httpRequest.RequestUri?.ToString() ?? string.Empty;

            if (string.Equals(requestUrl, feedSpecUrl, StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
            }

            if (string.Equals(requestUrl, hsdsSpecUrl, StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent(CreateHsdsProfileSpec())
                };
            }

            return new HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
        });

        // Act
        var result = await _service.ValidateOpenApiSpecificationAsync(request);

        // Assert
        Assert.That(result.IsValid, Is.True);
        Assert.That(result.Notifications.Any(n => n.Contains("Falling back to the HSDS profile OpenAPI specification", StringComparison.OrdinalIgnoreCase)), Is.True);
        Assert.That(request.OpenApiSchema!.Url, Is.EqualTo(hsdsSpecUrl));
    }

    [Test]
    public async Task ValidateOpenApiSpecificationAsync_UsesCachedResolvedFeedSpecBeforeRefetching()
    {
        // Arrange
        var uniqueFeedSpecUrl = $"https://cache-test.example.com/{Guid.NewGuid():N}/openapi.json";
        var feedSpec = @"{
            ""openapi"": ""3.0.0"",
            ""info"": {
                ""title"": ""Cache Test API"",
                ""version"": ""feed""
            },
            ""paths"": {
                ""/test"": {
                    ""get"": {
                        ""responses"": {
                            ""200"": { ""description"": ""OK"" }
                        }
                    }
                }
            }
        }";

        var requestCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        SetupHttpMock((httpRequest, ct) =>
        {
            var requestUrl = httpRequest.RequestUri?.ToString() ?? string.Empty;
            if (!requestCounts.ContainsKey(requestUrl))
            {
                requestCounts[requestUrl] = 0;
            }

            requestCounts[requestUrl]++;

            if (string.Equals(requestUrl, uniqueFeedSpecUrl, StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent(feedSpec)
                };
            }

            return new HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
        });

        var serviceWithCache = new OpenApiValidationService(
            _loggerMock.Object,
            CreateFactory(_httpClient),
            _jsonValidatorServiceMock.Object,
            _schemaResolverServiceMock.Object,
            _profileDiscoveryServiceMock.Object,
            _feedSpecDiscoveryMock.Object,
            _authOptions,
            cacheOptions: Options.Create(new CacheOptions
            {
                Enabled = true,
                ExpirationMinutes = 30
            }));

        var request = new OpenApiValidationRequest
        {
            OpenApiSchema = new OpenApiSchema
            {
                Url = uniqueFeedSpecUrl
            },
            Options = new OpenApiValidationOptions
            {
                ValidateSpecification = false,
                TestEndpoints = false
            }
        };

        // Act
        var firstResult = await serviceWithCache.ValidateOpenApiSpecificationAsync(request);
        var secondResult = await serviceWithCache.ValidateOpenApiSpecificationAsync(request);

        // Assert
        Assert.That(firstResult.IsValid, Is.True);
        Assert.That(secondResult.IsValid, Is.True);
        Assert.That(requestCounts.TryGetValue(uniqueFeedSpecUrl, out var feedFetchCount), Is.True);
        Assert.That(feedFetchCount, Is.EqualTo(1));
    }

    #endregion

    #region Cancellation Support

    [Test]
    public void ValidateOpenApiSpecificationAsync_RespectsCancellationToken()
    {
        // Arrange
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var json = CreateOpenApi30Spec();
        var request = new OpenApiValidationRequest
        {
            OpenApiSchema = new OpenApiSchema
            {
                Url = "https://example.com/openapi.json"
            }
        };
        var mockHandler = new MockHttpMessageHandler((req, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(json)
            };
        });
        _httpClient?.Dispose();
        _httpClient = TestHttpClientFactory.CreateClient(mockHandler);
        _service = new OpenApiValidationService(
            _loggerMock.Object,
            CreateFactory(_httpClient),
            _jsonValidatorServiceMock.Object,
            _schemaResolverServiceMock.Object,
            _profileDiscoveryServiceMock.Object,
            _feedSpecDiscoveryMock.Object,
            Options.Create(new AuthenticationOptions { AllowUserSuppliedAuth = true }));

        // Act
        var result = _service.ValidateOpenApiSpecificationAsync(request, cts.Token).GetAwaiter().GetResult();

        // Assert
        Assert.That(result.IsValid, Is.False);
        Assert.That(result.Summary, Is.Not.Null);
    }

    #endregion

    #region Options Processing

    [Test]
    public async Task ValidateOpenApiSpecificationAsync_RespondsToResponseBodyOption()
    {
        // Arrange
        var json = CreateOpenApi30Spec();
        var request = new OpenApiValidationRequest
        {
            OpenApiSchema = new OpenApiSchema
            {
                Url = "https://example.com/openapi.json"
            },
            BaseUrl = "https://api.example.com",
            Options = new OpenApiValidationOptions { IncludeResponseBody = false, TestEndpoints = true }
        };
        SetupHttpMock(json, endpointResponseBody: "{\"data\":[{\"id\":\"1\"}]}");

        // Act
        var result = await _service.ValidateOpenApiSpecificationAsync(request);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.EndpointTests, Is.Not.Empty);
        Assert.That(result.EndpointTests.SelectMany(e => e.TestResults), Is.Not.Empty);
        Assert.That(result.EndpointTests.SelectMany(e => e.TestResults).All(tr => tr.ResponseBody == null), Is.True);
    }

    [Test]
    public async Task ValidateOpenApiSpecificationAsync_RespondsToTestResultsOption()
    {
        // Arrange
        var json = CreateOpenApi30Spec();
        var request = new OpenApiValidationRequest
        {
            OpenApiSchema = new OpenApiSchema
            {
                Url = "https://example.com/openapi.json"
            },
            BaseUrl = "https://api.example.com",
            Options = new OpenApiValidationOptions { IncludeTestResults = false, TestEndpoints = true }
        };
        SetupHttpMock(json, endpointResponseBody: "{\"data\":[{\"id\":\"1\"}]}");

        // Act
        var result = await _service.ValidateOpenApiSpecificationAsync(request);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.EndpointTests, Is.Not.Empty);
        Assert.That(result.EndpointTests.All(e => e.TestResults.Count == 0), Is.True);
        Assert.That(result.EndpointTests.All(e => e.ValidationErrors != null), Is.True);
    }

    [Test]
    public async Task ValidateOpenApiSpecificationAsync_WithIncludeTestResultsFalse_PreservesFlattenedValidationErrors()
    {
        // Arrange
        var json = CreateOpenApi30SpecWithResponseSchema();
        var request = new OpenApiValidationRequest
        {
            OpenApiSchema = new OpenApiSchema
            {
                Url = "https://example.com/openapi.json"
            },
            BaseUrl = "https://api.example.com",
            Options = new OpenApiValidationOptions { IncludeTestResults = false, TestEndpoints = true, ValidateSpecification = false }
        };

        _jsonValidatorServiceMock
            .Setup(service => service.ValidateAsync(It.IsAny<ValidationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ValidationResult
            {
                IsValid = false,
                Errors = new List<OpenReferralApi.Core.Models.Validation.ValidationError>
                {
                    new()
                    {
                        Path = "data[0].name",
                        Message = "data[0].name is required",
                        ErrorCode = "VALIDATION_ERROR",
                        Severity = "Error"
                    }
                }
            });

        SetupHttpMock(json, endpointResponseBody: "[{\"name\":\"a\"}]");

        // Act
        var result = await _service.ValidateOpenApiSpecificationAsync(request);

        // Assert
        Assert.That(result.EndpointTests, Is.Not.Empty);
        Assert.That(result.EndpointTests.All(e => e.TestResults.Count == 0), Is.True);
        Assert.That(result.EndpointTests.Any(e => e.ValidationErrors.Any()), Is.True);
    }

    #endregion

    #region Endpoint Testing

    [Test]
    public async Task ValidateOpenApiSpecificationAsync_WithPaginatedEndpoint_RequestsMultiplePages()
    {
        // Arrange
        var json = CreateOpenApi30PaginatedSpec();
        var request = new OpenApiValidationRequest
        {
            OpenApiSchema = new OpenApiSchema
            {
                Url = "https://example.com/openapi.json"
            },
            BaseUrl = "https://api.example.com",
            Options = new OpenApiValidationOptions { TestEndpoints = true }
        };

        SetupHttpMock((req, ct) =>
        {
            var requestUri = req.RequestUri?.ToString() ?? string.Empty;
            if (requestUri.Contains("openapi", StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent(json)
                };
            }

            var responseBody = requestUri.Contains("page=3", StringComparison.OrdinalIgnoreCase)
                ? "{\"total_pages\":3,\"data\":[{\"id\":\"3\"}]}"
                : requestUri.Contains("page=2", StringComparison.OrdinalIgnoreCase)
                    ? "{\"total_pages\":3,\"data\":[{\"id\":\"2\"}]}"
                    : "{\"total_pages\":3,\"data\":[{\"id\":\"1\"}]}";

            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody)
            };
        });

        // Act
        var result = await _service.ValidateOpenApiSpecificationAsync(request);

        // Assert
        Assert.That(result.EndpointTests, Has.Count.EqualTo(1));
        Assert.That(result.EndpointTests[0].IsTested, Is.True);
        Assert.That(result.EndpointTests[0].Status, Is.EqualTo(EndpointTestStatus.PassedValidation));
        Assert.That(result.EndpointTests[0].TestResults, Has.Count.EqualTo(3));
        Assert.That(result.EndpointTests[0].TestResults.All(tr => tr.IsSuccessStatusCode), Is.True);
    }

    [Test]
    public async Task ValidateOpenApiSpecificationAsync_PerformsFullSuiteAfterSpecAndHsdsChecks()
    {
        // Arrange
        var callOrder = new List<string>();
        var feedSpecUrl = "https://feed.example.com/openapi.json";
        var hsdsProfileUrl = "https://openreferraluk.org/specifications/3.0/openapi.json";

        var specServiceMock = new Mock<IOpenApiSpecificationService>();
        specServiceMock
            .Setup(s => s.ValidateAsync(It.IsAny<Newtonsoft.Json.Linq.JObject>(), It.IsAny<CancellationToken>()))
            .Callback(() => callOrder.Add("spec"))
            .ReturnsAsync(new OpenApiSpecificationValidation
            {
                IsValid = false,
                Errors = new List<OpenReferralApi.Core.Models.Validation.ValidationError>
                {
                    new()
                    {
                        Path = "openapi",
                        Message = "Declared spec validation failed",
                        ErrorCode = "SPEC_ERROR",
                        Severity = "Error"
                    }
                }
            });

        var hsdsServiceMock = new Mock<IHsdsComplianceService>();
        hsdsServiceMock
            .Setup(s => s.ExtractClaimedProfileVersion(It.IsAny<string>(), It.IsAny<string>()))
            .Returns("3.0");

        hsdsServiceMock
            .Setup(s => s.TryGetKnownHsdsSchemaUrl(It.IsAny<string>(), out hsdsProfileUrl))
            .Returns(true);

        hsdsServiceMock
            .Setup(s => s.CompareFeedSpecAgainstHsdsProfile(It.IsAny<Newtonsoft.Json.Linq.JObject>(), It.IsAny<Newtonsoft.Json.Linq.JObject>()))
            .Callback(() => callOrder.Add("hsds"))
            .Returns(new List<OpenReferralApi.Core.Models.Validation.ValidationError>
            {
                new()
                {
                    Path = "paths.GET /required",
                    Message = "Missing required HSDS endpoint",
                    ErrorCode = "HSDS_MISSING_ENDPOINT",
                    Severity = "Error"
                }
            });

        hsdsServiceMock
            .Setup(s => s.ValidateEndpointResponsesAgainstHsdsProfileAsync(
                It.IsAny<List<EndpointTestResult>>(),
                It.IsAny<Newtonsoft.Json.Linq.JObject>(),
                It.IsAny<OpenApiValidationOptions>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var endpointTestingMock = new Mock<IEndpointTestingService>();
        endpointTestingMock
            .Setup(s => s.TestEndpointsAsync(
                It.IsAny<Newtonsoft.Json.Linq.JObject>(),
                It.IsAny<string>(),
                It.IsAny<OpenApiValidationOptions>(),
                It.IsAny<DataSourceAuthentication>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Callback(() => callOrder.Add("endpoints"))
            .ReturnsAsync(new List<EndpointTestResult>
            {
                new()
                {
                    Path = "/services",
                    Method = "GET",
                    IsTested = true,
                    Status = EndpointTestStatus.PassedValidation,
                    TestResults = new List<HttpTestResult>()
                }
            });

        var httpClient = TestHttpClientFactory.CreateClient(new MockHttpMessageHandler((req, ct) =>
        {
            var requestUrl = req.RequestUri?.ToString() ?? string.Empty;
            if (string.Equals(requestUrl, feedSpecUrl, StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent(CreateOpenApi30Spec())
                };
            }

            if (string.Equals(requestUrl, hsdsProfileUrl, StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent(CreateHsdsProfileSpec())
                };
            }

            return new HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
        }));

        var service = new OpenApiValidationService(
            _loggerMock.Object,
            CreateFactory(httpClient),
            _jsonValidatorServiceMock.Object,
            _schemaResolverServiceMock.Object,
            _profileDiscoveryServiceMock.Object,
            _feedSpecDiscoveryMock.Object,
            _authOptions,
            openApiSpecificationService: specServiceMock.Object,
            hsdsComplianceService: hsdsServiceMock.Object,
            endpointTestingService: endpointTestingMock.Object);

        var request = new OpenApiValidationRequest
        {
            OpenApiSchema = new OpenApiSchema { Url = feedSpecUrl },
            BaseUrl = "https://feed.example.com",
            ProfileReason = "Standard version [user: 3.0] read from '/' endpoint",
            Options = new OpenApiValidationOptions
            {
                ValidateSpecification = true,
                TestEndpoints = true
            }
        };

        try
        {
            // Act
            var result = await service.ValidateOpenApiSpecificationAsync(request);

            // Assert
            Assert.That(callOrder, Is.EqualTo(new[] { "spec", "hsds", "endpoints" }));
            Assert.That(result.EndpointTests, Has.Count.EqualTo(1));
            Assert.That(result.SpecificationValidation, Is.Not.Null);
            Assert.That(result.SpecificationValidation!.Errors.Any(e => e.ErrorCode == "SPEC_ERROR"), Is.True);
            Assert.That(result.SpecificationValidation.Errors.Any(e => e.ErrorCode == "HSDS_MISSING_ENDPOINT"), Is.True);
        }
        finally
        {
            httpClient.Dispose();
        }
    }

    [Test]
    public async Task ValidateOpenApiSpecificationAsync_WithEmptyPaginatedFeed_AddsWarning()
    {
        // Arrange
        var json = CreateOpenApi30PaginatedSpec();
        var request = new OpenApiValidationRequest
        {
            OpenApiSchema = new OpenApiSchema
            {
                Url = "https://example.com/openapi.json"
            },
            BaseUrl = "https://api.example.com",
            Options = new OpenApiValidationOptions { TestEndpoints = true }
        };

        SetupHttpMock((req, ct) =>
        {
            var requestUri = req.RequestUri?.ToString() ?? string.Empty;
            var responseBody = requestUri.Contains("openapi", StringComparison.OrdinalIgnoreCase)
                ? json
                : "{\"total_pages\":1,\"data\":[]}";

            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody)
            };
        });

        // Act
        var result = await _service.ValidateOpenApiSpecificationAsync(request);

        // Assert
        Assert.That(result.EndpointTests, Has.Count.EqualTo(1));
        Assert.That(result.EndpointTests[0].IsTested, Is.True);
        Assert.That(result.EndpointTests[0].Status, Is.EqualTo(EndpointTestStatus.PassedWithWarnings));
        Assert.That(result.EndpointTests[0].TestResults, Has.Count.EqualTo(1));
        Assert.That(result.EndpointTests[0].TestResults[0].ValidationResult, Is.Not.Null);
        Assert.That(result.EndpointTests[0].TestResults[0].ValidationResult!.Errors,
            Has.Some.Matches<OpenReferralApi.Core.Models.Validation.ValidationError>(e => e.ErrorCode == "EMPTY_FEED_WARNING"));
    }

    [Test]
    public async Task ValidateOpenApiSpecificationAsync_WithPaginatedEndpoint_TestedEndpointsDoNotRemainNotTested()
    {
        // Arrange
        var json = CreateOpenApi30PaginatedSpec();
        var request = new OpenApiValidationRequest
        {
            OpenApiSchema = new OpenApiSchema
            {
                Url = "https://example.com/openapi.json"
            },
            BaseUrl = "https://api.example.com",
            Options = new OpenApiValidationOptions { TestEndpoints = true }
        };

        SetupHttpMock((req, ct) =>
        {
            var requestUri = req.RequestUri?.ToString() ?? string.Empty;
            if (requestUri.Contains("openapi", StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent(json)
                };
            }

            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("{\"total_pages\":3,\"data\":[{\"id\":\"1\"}]}")
            };
        });

        // Act
        var result = await _service.ValidateOpenApiSpecificationAsync(request);

        // Assert
        Assert.That(result.EndpointTests, Is.Not.Empty);
        Assert.That(result.EndpointTests.Where(e => e.IsTested), Is.Not.Empty);
        Assert.That(result.EndpointTests.Where(e => e.IsTested).All(e => e.Status != EndpointTestStatus.NotTested), Is.True);
    }

    [Test]
    public async Task ValidateOpenApiSpecificationAsync_NormalizesAndDeduplicatesEndpointValidationErrorsAndWarnings()
    {
        // Arrange
        var json = CreateOpenApi30SpecWithResponseSchema();
        var request = new OpenApiValidationRequest
        {
            OpenApiSchema = new OpenApiSchema
            {
                Url = "https://example.com/openapi.json"
            },
            BaseUrl = "https://api.example.com",
            Options = new OpenApiValidationOptions
            {
                ValidateSpecification = false,
                TestEndpoints = true
            }
        };

        _jsonValidatorServiceMock
            .Setup(service => service.ValidateAsync(It.IsAny<ValidationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ValidationResult
            {
                IsValid = false,
                Errors = new List<OpenReferralApi.Core.Models.Validation.ValidationError>
                {
                    new()
                    {
                        Path = "data[0].name",
                        Message = "data[0].name is required",
                        ErrorCode = "VALIDATION_ERROR",
                        Severity = "Error"
                    },
                    new()
                    {
                        Path = "data[1].name",
                        Message = "data[1].name is required",
                        ErrorCode = "VALIDATION_ERROR",
                        Severity = "Error"
                    },
                    new()
                    {
                        Path = "data[0].extra",
                        Message = "data[0].extra is not expected",
                        ErrorCode = "VALIDATION_WARNING",
                        Severity = "Warning"
                    },
                    new()
                    {
                        Path = "data[4].extra",
                        Message = "data[4].extra is not expected",
                        ErrorCode = "VALIDATION_WARNING",
                        Severity = "Warning"
                    }
                }
            });

        SetupHttpMock(json, endpointResponseBody: "[{\"name\":\"a\"},{\"name\":\"b\"}]");

        // Act
        var result = await _service.ValidateOpenApiSpecificationAsync(request);
        var errors = result.EndpointTests[0].TestResults[0].ValidationResult!.Errors;

        // Assert
        Assert.That(errors, Has.Count.EqualTo(2));
        Assert.That(errors.All(e => !e.Path.Contains("[")), Is.True);
        Assert.That(errors.All(e => !e.Message.Contains("[")), Is.True);
    }

    [Test]
    public async Task ValidateOpenApiSpecificationAsync_EndpointValidation_DeduplicatesByPathNotMessage()
    {
        // Arrange
        var json = CreateOpenApi30SpecWithResponseSchema();
        var request = new OpenApiValidationRequest
        {
            OpenApiSchema = new OpenApiSchema
            {
                Url = "https://example.com/openapi.json"
            },
            BaseUrl = "https://api.example.com",
            Options = new OpenApiValidationOptions
            {
                ValidateSpecification = false,
                TestEndpoints = true
            }
        };

        _jsonValidatorServiceMock
            .Setup(service => service.ValidateAsync(It.IsAny<ValidationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ValidationResult
            {
                IsValid = false,
                Errors = new List<OpenReferralApi.Core.Models.Validation.ValidationError>
                {
                    new()
                    {
                        Path = "data[0]",
                        Message = "data[0] should be object",
                        ErrorCode = "VALIDATION_ERROR",
                        Severity = "Error"
                    },
                    new()
                    {
                        Path = "data[0]",
                        Message = "data[0] missing required property 'name'",
                        ErrorCode = "VALIDATION_ERROR",
                        Severity = "Error"
                    }
                }
            });

        SetupHttpMock(json, endpointResponseBody: "[{\"name\":\"a\"}]");

        // Act
        var result = await _service.ValidateOpenApiSpecificationAsync(request);
        var errors = result.EndpointTests[0].TestResults[0].ValidationResult!.Errors;

        // Assert
        Assert.That(errors, Has.Count.EqualTo(1));
        Assert.That(errors[0].Path, Is.EqualTo("data"));
        Assert.That(errors[0].Message, Is.EqualTo("data should be object"));
    }

    [Test]
    public async Task ValidateOpenApiSpecificationAsync_NoIdsAvailableWarning_IsNormalized()
    {
        // Arrange
        var json = CreateOpenApi30ParameterizedOnlySpecWithIndexedPath();
        var request = new OpenApiValidationRequest
        {
            OpenApiSchema = new OpenApiSchema
            {
                Url = "https://example.com/openapi.json"
            },
            BaseUrl = "https://api.example.com",
            Options = new OpenApiValidationOptions
            {
                ValidateSpecification = false,
                TestEndpoints = true
            }
        };

        SetupHttpMock(json);

        // Act
        var result = await _service.ValidateOpenApiSpecificationAsync(request);
        var warning = result.EndpointTests[0].TestResults[0].ValidationResult!.Errors
            .First(e => e.ErrorCode == "NO_IDS_AVAILABLE");

        // Assert
        Assert.That(result.EndpointTests[0].Status, Is.EqualTo(EndpointTestStatus.NotTested));
        Assert.That(warning.Path.Contains("["), Is.False, "Path should be normalized");
        Assert.That(warning.Message.Contains("["), Is.False, "Message should be normalized");
    }

    [Test]
    public async Task ValidateOpenApiSpecificationAsync_WithOptionalEndpointAnd404_ReturnsWarning()
    {
        // Arrange
        var json = CreateOpenApi30OptionalEndpointSpec();
        var request = new OpenApiValidationRequest
        {
            OpenApiSchema = new OpenApiSchema
            {
                Url = "https://example.com/openapi.json"
            },
            BaseUrl = "https://api.example.com",
            Options = new OpenApiValidationOptions
            {
                TestEndpoints = true,
                TestOptionalEndpoints = true,
                TreatOptionalEndpointsAsWarnings = true
            }
        };

        SetupHttpMock((req, ct) =>
        {
            var requestUri = req.RequestUri?.ToString() ?? string.Empty;
            if (requestUri.Contains("openapi", StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent(json)
                };
            }

            return new HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
        });

        // Act
        var result = await _service.ValidateOpenApiSpecificationAsync(request);

        // Assert
        Assert.That(result.EndpointTests, Has.Count.EqualTo(1));
        Assert.That(result.EndpointTests[0].Status, Is.EqualTo(EndpointTestStatus.PassedWithWarnings));
        Assert.That(result.EndpointTests[0].TestResults, Has.Count.EqualTo(1));
        Assert.That(result.EndpointTests[0].TestResults[0].ValidationResult, Is.Not.Null);
        Assert.That(result.EndpointTests[0].TestResults[0].ValidationResult!.Errors,
            Has.Some.Matches<OpenReferralApi.Core.Models.Validation.ValidationError>(e => e.ErrorCode == "OPTIONAL_ENDPOINT_NON_SUCCESS" && e.Severity == "Warning"));
    }

    [Test]
    public async Task ValidateOpenApiSpecificationAsync_WithOptionalEndpointSchemaFailureAndWarningMode_DoesNotFailValidation()
    {
        // Arrange
        var json = CreateOpenApi30OptionalEndpointSpec();
        _jsonValidatorServiceMock
            .Setup(service => service.ValidateAsync(It.IsAny<ValidationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ValidationResult
            {
                IsValid = false,
                Errors = new List<OpenReferralApi.Core.Models.Validation.ValidationError>
                {
                    new()
                    {
                        Path = "data",
                        Message = "Validation failed for optional endpoint payload",
                        ErrorCode = "SCHEMA_MISMATCH",
                        Severity = "Error"
                    }
                },
                SchemaVersion = "test",
                Duration = TimeSpan.Zero
            });

        var request = new OpenApiValidationRequest
        {
            OpenApiSchema = new OpenApiSchema
            {
                Url = "https://example.com/openapi.json"
            },
            BaseUrl = "https://api.example.com",
            Options = new OpenApiValidationOptions
            {
                ValidateSpecification = false,
                TestEndpoints = true,
                TestOptionalEndpoints = true,
                TreatOptionalEndpointsAsWarnings = true
            }
        };

        SetupHttpMock((req, ct) =>
        {
            var requestUri = req.RequestUri?.ToString() ?? string.Empty;
            if (requestUri.Contains("openapi", StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent(json)
                };
            }

            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("{}")
            };
        });

        // Act
        var result = await _service.ValidateOpenApiSpecificationAsync(request);

        // Assert
        Assert.That(result.EndpointTests, Has.Count.EqualTo(1));
        Assert.That(result.EndpointTests[0].IsOptional, Is.True);
        Assert.That(result.EndpointTests[0].Status, Is.EqualTo(EndpointTestStatus.PassedWithWarnings));
        Assert.That(result.Summary, Is.Not.Null);
        Assert.That(result.Summary!.FailedTests, Is.EqualTo(0));
        Assert.That(result.IsValid, Is.True);
    }

    [Test]
    public async Task ValidateOpenApiSpecificationAsync_WithOptionalEndpointAndTestingDisabled_SkipsEndpoint()
    {
        // Arrange
        var json = CreateOpenApi30OptionalEndpointSpec();
        var request = new OpenApiValidationRequest
        {
            OpenApiSchema = new OpenApiSchema
            {
                Url = "https://example.com/openapi.json"
            },
            BaseUrl = "https://api.example.com",
            Options = new OpenApiValidationOptions
            {
                TestEndpoints = true,
                TestOptionalEndpoints = false
            }
        };

        SetupHttpMock((req, ct) =>
        {
            var requestUri = req.RequestUri?.ToString() ?? string.Empty;
            var responseBody = requestUri.Contains("openapi", StringComparison.OrdinalIgnoreCase)
                ? json
                : "{}";

            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody)
            };
        });

        // Act
        var result = await _service.ValidateOpenApiSpecificationAsync(request);

        // Assert
        Assert.That(result.EndpointTests, Has.Count.EqualTo(1));
        Assert.That(result.EndpointTests[0].Status, Is.EqualTo(EndpointTestStatus.Skipped));
        Assert.That(result.EndpointTests[0].TestResults, Is.Empty);
    }

    [Test]
    public async Task ValidateOpenApiSpecificationAsync_WithMixedRequiredAndOptionalEndpoints_TestedEndpointsNeverRemainNotTested()
    {
        // Arrange
        var json = CreateOpenApi30MixedRequiredAndOptionalSpec();
        var request = new OpenApiValidationRequest
        {
            OpenApiSchema = new OpenApiSchema
            {
                Url = "https://example.com/openapi.json"
            },
            BaseUrl = "https://api.example.com",
            Options = new OpenApiValidationOptions
            {
                TestEndpoints = true,
                TestOptionalEndpoints = true,
                TreatOptionalEndpointsAsWarnings = true
            }
        };

        SetupHttpMock((req, ct) =>
        {
            var requestUri = req.RequestUri?.ToString() ?? string.Empty;
            if (requestUri.Contains("openapi", StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent(json)
                };
            }

            if (requestUri.Contains("/optional", StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
            }

            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("{}")
            };
        });

        // Act
        var result = await _service.ValidateOpenApiSpecificationAsync(request);

        // Assert
        Assert.That(result.EndpointTests, Has.Count.EqualTo(2));
        Assert.That(result.EndpointTests.All(e => e.IsTested), Is.True);
        Assert.That(result.EndpointTests.All(e => e.Status != EndpointTestStatus.NotTested), Is.True);
        Assert.That(result.EndpointTests.Any(e => e.Status == EndpointTestStatus.PassedValidation), Is.True);
        Assert.That(result.EndpointTests.Any(e => e.Status == EndpointTestStatus.PassedWithWarnings), Is.True);
    }

    #endregion

    #region Authentication Tests

    [Test]
    public async Task ValidateOpenApiSpecificationAsync_WithApiKeyAuth_AddsCorrectHeader()
    {
        // Arrange
        var json = CreateOpenApi30Spec();
        HttpRequestMessage? capturedRequest = null;

        SetupHttpMock((req, ct) =>
        {
            var requestUri = req.RequestUri?.ToString() ?? string.Empty;
            if (!requestUri.Contains("openapi", StringComparison.OrdinalIgnoreCase))
            {
                capturedRequest = req;
            }

            var responseBody = requestUri.Contains("openapi", StringComparison.OrdinalIgnoreCase)
                ? json
                : "{}";

            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody)
            };
        });

        var request = new OpenApiValidationRequest
        {
            OpenApiSchema = new OpenApiSchema
            {
                Url = "https://example.com/openapi.json"
            },
            BaseUrl = "https://api.example.com",
            DataSourceAuth = new DataSourceAuthentication
            {
                ApiKey = "test-api-key-12345"
            },
            Options = new OpenApiValidationOptions
            {
                TestEndpoints = true
            }
        };

        // Act
        var result = await _service.ValidateOpenApiSpecificationAsync(request);

        // Assert
        Assert.That(capturedRequest, Is.Not.Null, "Expected endpoint request to be captured");
        Assert.That(capturedRequest!.Headers.Contains("X-API-Key"), Is.True, "Expected X-API-Key header to be present");
        Assert.That(capturedRequest.Headers.GetValues("X-API-Key").First(), Is.EqualTo("test-api-key-12345"));
    }

    [Test]
    public async Task ValidateOpenApiSpecificationAsync_WithCustomApiKeyHeader_AddsCorrectHeader()
    {
        // Arrange
        var json = CreateOpenApi30Spec();
        HttpRequestMessage? capturedRequest = null;

        SetupHttpMock((req, ct) =>
        {
            var requestUri = req.RequestUri?.ToString() ?? string.Empty;
            if (!requestUri.Contains("openapi", StringComparison.OrdinalIgnoreCase))
            {
                capturedRequest = req;
            }

            var responseBody = requestUri.Contains("openapi", StringComparison.OrdinalIgnoreCase)
                ? json
                : "{}";

            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody)
            };
        });

        var request = new OpenApiValidationRequest
        {
            OpenApiSchema = new OpenApiSchema
            {
                Url = "https://example.com/openapi.json"
            },
            BaseUrl = "https://api.example.com",
            DataSourceAuth = new DataSourceAuthentication
            {
                ApiKey = "custom-key-value",
                ApiKeyHeader = "X-Custom-Auth-Key"
            },
            Options = new OpenApiValidationOptions
            {
                TestEndpoints = true
            }
        };

        // Act
        var result = await _service.ValidateOpenApiSpecificationAsync(request);

        // Assert
        Assert.That(capturedRequest, Is.Not.Null);
        Assert.That(capturedRequest!.Headers.Contains("X-Custom-Auth-Key"), Is.True);
        Assert.That(capturedRequest.Headers.GetValues("X-Custom-Auth-Key").First(), Is.EqualTo("custom-key-value"));
    }

    [Test]
    public async Task ValidateOpenApiSpecificationAsync_WithBearerToken_AddsAuthorizationHeader()
    {
        // Arrange
        var json = CreateOpenApi30Spec();
        HttpRequestMessage? capturedRequest = null;

        SetupHttpMock((req, ct) =>
        {
            var requestUri = req.RequestUri?.ToString() ?? string.Empty;
            if (!requestUri.Contains("openapi", StringComparison.OrdinalIgnoreCase))
            {
                capturedRequest = req;
            }

            var responseBody = requestUri.Contains("openapi", StringComparison.OrdinalIgnoreCase)
                ? json
                : "{}";

            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody)
            };
        });

        var request = new OpenApiValidationRequest
        {
            OpenApiSchema = new OpenApiSchema
            {
                Url = "https://example.com/openapi.json"
            },
            BaseUrl = "https://api.example.com",
            DataSourceAuth = new DataSourceAuthentication
            {
                BearerToken = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.test"
            },
            Options = new OpenApiValidationOptions
            {
                TestEndpoints = true
            }
        };

        // Act
        var result = await _service.ValidateOpenApiSpecificationAsync(request);

        // Assert
        Assert.That(capturedRequest, Is.Not.Null);
        Assert.That(capturedRequest!.Headers.Authorization, Is.Not.Null);
        Assert.That(capturedRequest.Headers.Authorization!.Scheme, Is.EqualTo("Bearer"));
        Assert.That(capturedRequest.Headers.Authorization.Parameter, Is.EqualTo("eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.test"));
    }

    [Test]
    public async Task ValidateOpenApiSpecificationAsync_WithBasicAuth_AddsAuthorizationHeader()
    {
        // Arrange
        var json = CreateOpenApi30Spec();
        HttpRequestMessage? capturedRequest = null;

        SetupHttpMock((req, ct) =>
        {
            var requestUri = req.RequestUri?.ToString() ?? string.Empty;
            if (!requestUri.Contains("openapi", StringComparison.OrdinalIgnoreCase))
            {
                capturedRequest = req;
            }

            var responseBody = requestUri.Contains("openapi", StringComparison.OrdinalIgnoreCase)
                ? json
                : "{}";

            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody)
            };
        });

        var request = new OpenApiValidationRequest
        {
            OpenApiSchema = new OpenApiSchema
            {
                Url = "https://example.com/openapi.json"
            },
            BaseUrl = "https://api.example.com",
            DataSourceAuth = new DataSourceAuthentication
            {
                BasicAuth = new BasicAuthentication
                {
                    Username = "testuser",
                    Password = "testpass123"
                }
            },
            Options = new OpenApiValidationOptions
            {
                TestEndpoints = true
            }
        };

        // Act
        var result = await _service.ValidateOpenApiSpecificationAsync(request);

        // Assert
        Assert.That(capturedRequest, Is.Not.Null);
        Assert.That(capturedRequest!.Headers.Authorization, Is.Not.Null);
        Assert.That(capturedRequest.Headers.Authorization!.Scheme, Is.EqualTo("Basic"));
        
        // Decode and verify credentials
        var credentials = System.Text.Encoding.ASCII.GetString(
            Convert.FromBase64String(capturedRequest.Headers.Authorization.Parameter!));
        Assert.That(credentials, Is.EqualTo("testuser:testpass123"));
    }

    [Test]
    public async Task ValidateOpenApiSpecificationAsync_WithCustomHeaders_AddsAllHeaders()
    {
        // Arrange
        var json = CreateOpenApi30Spec();
        HttpRequestMessage? capturedRequest = null;

        SetupHttpMock((req, ct) =>
        {
            var requestUri = req.RequestUri?.ToString() ?? string.Empty;
            if (!requestUri.Contains("openapi", StringComparison.OrdinalIgnoreCase))
            {
                capturedRequest = req;
            }

            var responseBody = requestUri.Contains("openapi", StringComparison.OrdinalIgnoreCase)
                ? json
                : "{}";

            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody)
            };
        });

        var request = new OpenApiValidationRequest
        {
            OpenApiSchema = new OpenApiSchema
            {
                Url = "https://example.com/openapi.json"
            },
            BaseUrl = "https://api.example.com",
            DataSourceAuth = new DataSourceAuthentication
            {
                CustomHeaders = new Dictionary<string, string>
                {
                    { "X-Client-Id", "client-123" },
                    { "X-Request-Id", "req-456" },
                    { "X-Tenant-Id", "tenant-789" }
                }
            },
            Options = new OpenApiValidationOptions
            {
                TestEndpoints = true
            }
        };

        // Act
        var result = await _service.ValidateOpenApiSpecificationAsync(request);

        // Assert
        Assert.That(capturedRequest, Is.Not.Null);
        Assert.That(capturedRequest!.Headers.Contains("X-Client-Id"), Is.True);
        Assert.That(capturedRequest.Headers.GetValues("X-Client-Id").First(), Is.EqualTo("client-123"));
        Assert.That(capturedRequest.Headers.Contains("X-Request-Id"), Is.True);
        Assert.That(capturedRequest.Headers.GetValues("X-Request-Id").First(), Is.EqualTo("req-456"));
        Assert.That(capturedRequest.Headers.Contains("X-Tenant-Id"), Is.True);
        Assert.That(capturedRequest.Headers.GetValues("X-Tenant-Id").First(), Is.EqualTo("tenant-789"));
    }

    [Test]
    public async Task ValidateOpenApiSpecificationAsync_WithMultipleAuthMethods_AddsAllHeaders()
    {
        // Arrange
        var json = CreateOpenApi30Spec();
        HttpRequestMessage? capturedRequest = null;

        SetupHttpMock((req, ct) =>
        {
            var requestUri = req.RequestUri?.ToString() ?? string.Empty;
            if (!requestUri.Contains("openapi", StringComparison.OrdinalIgnoreCase))
            {
                capturedRequest = req;
            }

            var responseBody = requestUri.Contains("openapi", StringComparison.OrdinalIgnoreCase)
                ? json
                : "{}";

            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody)
            };
        });

        var request = new OpenApiValidationRequest
        {
            OpenApiSchema = new OpenApiSchema
            {
                Url = "https://example.com/openapi.json"
            },
            BaseUrl = "https://api.example.com",
            DataSourceAuth = new DataSourceAuthentication
            {
                CustomHeaders = new Dictionary<string, string>
                {
                    { "X-Client-Id", "multi-auth-client" },
                    { "X-Request-Id", "req-12345" }
                }
            },
            Options = new OpenApiValidationOptions
            {
                TestEndpoints = true
            }
        };

        // Act
        var result = await _service.ValidateOpenApiSpecificationAsync(request);

        // Assert
        Assert.That(capturedRequest, Is.Not.Null);
        Assert.That(capturedRequest!.Headers.Contains("X-Client-Id"), Is.True);
        Assert.That(capturedRequest.Headers.GetValues("X-Client-Id").First(), Is.EqualTo("multi-auth-client"));
        Assert.That(capturedRequest.Headers.Contains("X-Request-Id"), Is.True);
        Assert.That(capturedRequest.Headers.GetValues("X-Request-Id").First(), Is.EqualTo("req-12345"));
    }

    [Test]
    public async Task ValidateOpenApiSpecificationAsync_WithoutDataSourceAuth_DoesNotAddHeaders()
    {
        // Arrange
        var json = CreateOpenApi30Spec();
        HttpRequestMessage? capturedRequest = null;

        SetupHttpMock((req, ct) =>
        {
            var requestUri = req.RequestUri?.ToString() ?? string.Empty;
            if (!requestUri.Contains("openapi", StringComparison.OrdinalIgnoreCase))
            {
                capturedRequest = req;
            }

            var responseBody = requestUri.Contains("openapi", StringComparison.OrdinalIgnoreCase)
                ? json
                : "{}";

            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody)
            };
        });

        var request = new OpenApiValidationRequest
        {
            OpenApiSchema = new OpenApiSchema
            {
                Url = "https://example.com/openapi.json"
            },
            BaseUrl = "https://api.example.com",
            DataSourceAuth = null,  // No authentication provided
            Options = new OpenApiValidationOptions
            {
                TestEndpoints = true
            }
        };

        // Act
        var result = await _service.ValidateOpenApiSpecificationAsync(request);

        // Assert
        Assert.That(capturedRequest, Is.Not.Null);
        Assert.That(capturedRequest!.Headers.Contains("X-API-Key"), Is.False);
        Assert.That(capturedRequest.Headers.Authorization, Is.Null);
    }

    [Test]
    public async Task ValidateOpenApiSpecificationAsync_WithEmptyAuthData_DoesNotAddHeaders()
    {
        // Arrange
        var json = CreateOpenApi30Spec();
        HttpRequestMessage? capturedRequest = null;

        SetupHttpMock((req, ct) =>
        {
            var requestUri = req.RequestUri?.ToString() ?? string.Empty;
            if (!requestUri.Contains("openapi", StringComparison.OrdinalIgnoreCase))
            {
                capturedRequest = req;
            }

            var responseBody = requestUri.Contains("openapi", StringComparison.OrdinalIgnoreCase)
                ? json
                : "{}";

            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody)
            };
        });

        var request = new OpenApiValidationRequest
        {
            OpenApiSchema = new OpenApiSchema
            {
                Url = "https://example.com/openapi.json"
            },
            BaseUrl = "https://api.example.com",
            DataSourceAuth = new DataSourceAuthentication(),  // Empty auth data
            Options = new OpenApiValidationOptions
            {
                TestEndpoints = true
            }
        };

        // Act
        var result = await _service.ValidateOpenApiSpecificationAsync(request);

        // Assert
        Assert.That(capturedRequest, Is.Not.Null);
        Assert.That(capturedRequest!.Headers.Authorization, Is.Null);
    }

    [Test]
    public async Task ValidateOpenApiSpecificationAsync_WithBasicAuthEmptyPassword_RejectsAuth()
    {
        // Arrange
        var json = CreateOpenApi30Spec();
        HttpRequestMessage? capturedRequest = null;

        SetupHttpMock((req, ct) =>
        {
            var requestUri = req.RequestUri?.ToString() ?? string.Empty;
            if (!requestUri.Contains("openapi", StringComparison.OrdinalIgnoreCase))
            {
                capturedRequest = req;
            }

            var responseBody = requestUri.Contains("openapi", StringComparison.OrdinalIgnoreCase)
                ? json
                : "{}";

            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody)
            };
        });

        var request = new OpenApiValidationRequest
        {
            OpenApiSchema = new OpenApiSchema
            {
                Url = "https://example.com/openapi.json"
            },
            BaseUrl = "https://api.example.com",
            DataSourceAuth = new DataSourceAuthentication
            {
                BasicAuth = new BasicAuthentication
                {
                    Username = "testuser",
                    Password = string.Empty  // Empty password - should be rejected
                }
            },
            Options = new OpenApiValidationOptions
            {
                TestEndpoints = true
            }
        };

        // Act
        var result = await _service.ValidateOpenApiSpecificationAsync(request);

        // Assert
        // Empty passwords are not allowed, so Authorization header should not be applied
        Assert.That(capturedRequest, Is.Not.Null);
        Assert.That(capturedRequest!.Headers.Authorization, Is.Null);
    }

    [Test]
    public async Task ValidateOpenApiSpecificationAsync_WithHttpBaseUrl_DoesNotApplyDataSourceAuthHeaders()
    {
        // Arrange
        var json = CreateOpenApi30Spec();
        HttpRequestMessage? capturedDataSourceRequest = null;

        SetupHttpMock((req, ct) =>
        {
            var requestUri = req.RequestUri?.ToString() ?? string.Empty;
            if (!requestUri.Contains("openapi", StringComparison.OrdinalIgnoreCase))
            {
                capturedDataSourceRequest = req;
            }

            var responseBody = requestUri.Contains("openapi", StringComparison.OrdinalIgnoreCase)
                ? json
                : "{}";

            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody)
            };
        });

        var request = new OpenApiValidationRequest
        {
            OpenApiSchema = new OpenApiSchema
            {
                Url = "https://example.com/openapi.json"
            },
            BaseUrl = "http://api.example.com",
            DataSourceAuth = new DataSourceAuthentication
            {
                ApiKey = "do-not-send-over-http"
            },
            Options = new OpenApiValidationOptions
            {
                TestEndpoints = true
            }
        };

        // Act
        await _service.ValidateOpenApiSpecificationAsync(request);

        // Assert
        Assert.That(capturedDataSourceRequest, Is.Not.Null);
        Assert.That(capturedDataSourceRequest!.RequestUri, Is.Not.Null);
        Assert.That(capturedDataSourceRequest.RequestUri!.Scheme, Is.EqualTo(Uri.UriSchemeHttp));
        Assert.That(capturedDataSourceRequest.Headers.Contains("X-API-Key"), Is.False);
        Assert.That(capturedDataSourceRequest.Headers.Authorization, Is.Null);
    }

    [Test]
    public async Task ValidateOpenApiSpecificationAsync_WhenUserSuppliedAuthEnabled_AppliesAuthToSchemaAndDatasourceRequests()
    {
        // Arrange
        var json = CreateOpenApi30Spec();
        HttpRequestMessage? capturedSchemaRequest = null;
        HttpRequestMessage? capturedDataSourceRequest = null;

        SetupHttpMock((req, ct) =>
        {
            var requestUri = req.RequestUri?.ToString() ?? string.Empty;
            if (requestUri.Contains("openapi", StringComparison.OrdinalIgnoreCase))
            {
                capturedSchemaRequest = req;
            }
            else
            {
                capturedDataSourceRequest = req;
            }

            var responseBody = requestUri.Contains("openapi", StringComparison.OrdinalIgnoreCase)
                ? json
                : "{}";

            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody)
            };
        });

        var request = new OpenApiValidationRequest
        {
            OpenApiSchema = new OpenApiSchema
            {
                Url = "https://example.com/openapi.json",
                Authentication = new DataSourceAuthentication
                {
                    BearerToken = "schema-token"
                }
            },
            BaseUrl = "https://api.example.com",
            DataSourceAuth = new DataSourceAuthentication
            {
                ApiKey = "data-source-api-key"
            },
            Options = new OpenApiValidationOptions
            {
                TestEndpoints = true
            }
        };

        // Act
        await _service.ValidateOpenApiSpecificationAsync(request);

        // Assert
        Assert.That(capturedSchemaRequest, Is.Not.Null);
        Assert.That(capturedSchemaRequest!.Headers.Authorization, Is.Not.Null);
        Assert.That(capturedSchemaRequest.Headers.Authorization!.Scheme, Is.EqualTo("Bearer"));
        Assert.That(capturedSchemaRequest.Headers.Authorization.Parameter, Is.EqualTo("schema-token"));

        Assert.That(capturedDataSourceRequest, Is.Not.Null);
        Assert.That(capturedDataSourceRequest!.Headers.Contains("X-API-Key"), Is.True);
        Assert.That(capturedDataSourceRequest.Headers.GetValues("X-API-Key").First(), Is.EqualTo("data-source-api-key"));
    }

    [Test]
    public async Task ValidateOpenApiSpecificationAsync_WhenUserSuppliedAuthDisabled_DoesNotApplyAuthToSchemaOrDatasourceRequests()
    {
        // Arrange
        var json = CreateOpenApi30Spec();
        HttpRequestMessage? capturedSchemaRequest = null;
        HttpRequestMessage? capturedDataSourceRequest = null;

        var mockHandler = new MockHttpMessageHandler((req, ct) =>
        {
            var requestUri = req.RequestUri?.ToString() ?? string.Empty;
            if (requestUri.Contains("openapi", StringComparison.OrdinalIgnoreCase))
            {
                capturedSchemaRequest = req;
            }
            else
            {
                capturedDataSourceRequest = req;
            }

            var responseBody = requestUri.Contains("openapi", StringComparison.OrdinalIgnoreCase)
                ? json
                : "{}";

            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody)
            };
        });

        _httpClient?.Dispose();
        _httpClient = TestHttpClientFactory.CreateClient(mockHandler);
        var service = new OpenApiValidationService(
            _loggerMock.Object,
            CreateFactory(_httpClient),
            _jsonValidatorServiceMock.Object,
            _schemaResolverServiceMock.Object,
            _profileDiscoveryServiceMock.Object,
            _feedSpecDiscoveryMock.Object,
            Options.Create(new AuthenticationOptions { AllowUserSuppliedAuth = false }));

        var request = new OpenApiValidationRequest
        {
            OpenApiSchema = new OpenApiSchema
            {
                Url = "https://example.com/openapi.json",
                Authentication = new DataSourceAuthentication
                {
                    BearerToken = "schema-token"
                }
            },
            BaseUrl = "https://api.example.com",
            DataSourceAuth = new DataSourceAuthentication
            {
                ApiKey = "data-source-api-key"
            },
            Options = new OpenApiValidationOptions
            {
                TestEndpoints = true
            }
        };

        // Act
        await service.ValidateOpenApiSpecificationAsync(request);

        // Assert
        Assert.That(capturedSchemaRequest, Is.Not.Null);
        Assert.That(capturedSchemaRequest!.Headers.Authorization, Is.Null);
        Assert.That(capturedSchemaRequest.Headers.Contains("X-API-Key"), Is.False);

        Assert.That(capturedDataSourceRequest, Is.Not.Null);
        Assert.That(capturedDataSourceRequest!.Headers.Authorization, Is.Null);
        Assert.That(capturedDataSourceRequest.Headers.Contains("X-API-Key"), Is.False);
    }

    #endregion

    #region Helper Methods

    private void SetupHttpMock(string responseJson, string endpointResponseBody = "{}")
    {
        var mockHandler = new MockHttpMessageHandler((request, ct) =>
        {
            var requestUri = request.RequestUri?.ToString() ?? string.Empty;
            var responseBody = requestUri.Contains("openapi", StringComparison.OrdinalIgnoreCase)
                ? responseJson
                : endpointResponseBody;

            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody)
            };
        });

        _httpClient?.Dispose();
        _httpClient = TestHttpClientFactory.CreateClient(mockHandler);

        _service = new OpenApiValidationService(
            _loggerMock.Object,
            CreateFactory(_httpClient),
            _jsonValidatorServiceMock.Object,
            _schemaResolverServiceMock.Object,
            _profileDiscoveryServiceMock.Object,
            _feedSpecDiscoveryMock.Object,
            _authOptions,
            specificationOptions: Options.Create(new SpecificationOptions
            {
                Urls = new Dictionary<string, string>
                {
                    ["HSDS-UK-1.0"] = "https://openreferraluk.org/specifications/1.0/openapi.json",
                    ["HSDS-UK-3.0"] = "https://openreferraluk.org/specifications/3.0/openapi.json"
                }
            }));
    }

    private void SetupHttpMock(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> handler)
    {
        var mockHandler = new MockHttpMessageHandler(handler);
        _httpClient?.Dispose();
        _httpClient = TestHttpClientFactory.CreateClient(mockHandler);

        _service = new OpenApiValidationService(
            _loggerMock.Object,
            CreateFactory(_httpClient),
            _jsonValidatorServiceMock.Object,
            _schemaResolverServiceMock.Object,
            _profileDiscoveryServiceMock.Object,
            _feedSpecDiscoveryMock.Object,
            _authOptions,
            specificationOptions: Options.Create(new SpecificationOptions
            {
                Urls = new Dictionary<string, string>
                {
                    ["HSDS-UK-1.0"] = "https://openreferraluk.org/specifications/1.0/openapi.json",
                    ["HSDS-UK-3.0"] = "https://openreferraluk.org/specifications/3.0/openapi.json"
                }
            }));
    }

    private static IHttpClientFactory CreateFactory(HttpClient httpClient)
    {
        var mock = new Mock<IHttpClientFactory>();
        mock.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(httpClient);
        return mock.Object;
    }

    private string CreateOpenApi30Spec()
    {
        return @"{
            ""openapi"": ""3.0.0"",
            ""info"": {
                ""title"": ""Test API"",
                ""version"": ""1.0.0""
            },
            ""paths"": {
                ""/test"": {
                    ""get"": {
                        ""responses"": {
                            ""200"": { ""description"": ""OK"" }
                        }
                    }
                }
            }
        }";
    }

    private string CreateOpenApi30SpecWithResponseSchema()
    {
        return @"{
            ""openapi"": ""3.0.0"",
            ""info"": {
                ""title"": ""Test API"",
                ""version"": ""1.0.0""
            },
            ""paths"": {
                ""/test"": {
                    ""get"": {
                        ""responses"": {
                            ""200"": {
                                ""description"": ""OK"",
                                ""content"": {
                                    ""application/json"": {
                                        ""schema"": {
                                            ""type"": ""array"",
                                            ""items"": {
                                                ""type"": ""object"",
                                                ""properties"": {
                                                    ""name"": { ""type"": ""string"" }
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }";
    }

    private string CreateOpenApi30ParameterizedOnlySpecWithIndexedPath()
    {
        return @"{
            ""openapi"": ""3.0.0"",
            ""info"": {
                ""title"": ""Test API"",
                ""version"": ""1.0.0""
            },
            ""paths"": {
                ""/items[0]/{id}"": {
                    ""get"": {
                        ""responses"": {
                            ""200"": { ""description"": ""OK"" }
                        }
                    }
                }
            }
        }";
    }

    private string CreateOpenApi30PaginatedSpec()
    {
        return @"{
            ""openapi"": ""3.0.0"",
            ""info"": {
                ""title"": ""Test API"",
                ""version"": ""1.0.0""
            },
            ""paths"": {
                ""/items"": {
                    ""get"": {
                        ""parameters"": [
                            {
                                ""name"": ""page"",
                                ""in"": ""query"",
                                ""schema"": { ""type"": ""integer"" }
                            }
                        ],
                        ""responses"": {
                            ""200"": {
                                ""description"": ""OK"",
                                ""content"": {
                                    ""application/json"": {
                                        ""schema"": {
                                            ""type"": ""object"",
                                            ""properties"": {
                                                ""total_pages"": { ""type"": ""integer"" },
                                                ""data"": {
                                                    ""type"": ""array"",
                                                    ""items"": { ""type"": ""object"" }
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }";
    }

    private string CreateOpenApi30OptionalEndpointSpec()
    {
        return @"{
            ""openapi"": ""3.0.0"",
            ""info"": {
                ""title"": ""Test API"",
                ""version"": ""1.0.0""
            },
            ""paths"": {
                ""/optional"": {
                    ""get"": {
                        ""tags"": [""Optional""],
                        ""responses"": {
                            ""200"": { ""description"": ""OK"" }
                        }
                    }
                }
            }
        }";
    }

    private string CreateOpenApi30MixedRequiredAndOptionalSpec()
    {
        return @"{
            ""openapi"": ""3.0.0"",
            ""info"": {
                ""title"": ""Test API"",
                ""version"": ""1.0.0""
            },
            ""paths"": {
                ""/required"": {
                    ""get"": {
                        ""responses"": {
                            ""200"": { ""description"": ""OK"" }
                        }
                    }
                },
                ""/optional"": {
                    ""get"": {
                        ""tags"": [""Optional""],
                        ""responses"": {
                            ""200"": { ""description"": ""OK"" }
                        }
                    }
                }
            }
        }";
    }

    private string CreateSwagger20Spec()
    {
        return @"{
            ""swagger"": ""2.0"",
            ""info"": {
                ""title"": ""Test API"",
                ""version"": ""1.0.0""
            },
            ""paths"": {
                ""/test"": {
                    ""get"": {
                        ""responses"": {
                            ""200"": { ""description"": ""OK"" }
                        }
                    }
                }
            }
        }";
    }

    private string CreateHsdsProfileSpec()
    {
        return @"{
            ""openapi"": ""3.0.0"",
            ""info"": {
                ""title"": ""HSDS Profile"",
                ""version"": ""3.0""
            },
            ""paths"": {
                ""/organisations"": {
                    ""get"": {
                        ""responses"": {
                            ""200"": {
                                ""description"": ""OK"",
                                ""content"": {
                                    ""application/json"": {
                                        ""schema"": {
                                            ""type"": ""array"",
                                            ""items"": {
                                                ""type"": ""object"",
                                                ""required"": [""id"", ""name""],
                                                ""properties"": {
                                                    ""id"": { ""type"": ""string"" },
                                                    ""name"": { ""type"": ""string"" }
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }";
    }

    private string CreateFeedSpecMissingRequiredHsdsEndpoint()
    {
        return @"{
            ""openapi"": ""3.0.0"",
            ""info"": {
                ""title"": ""Feed API"",
                ""version"": ""1.0.0""
            },
            ""paths"": {
                ""/services"": {
                    ""get"": {
                        ""responses"": {
                            ""200"": { ""description"": ""OK"" }
                        }
                    }
                }
            }
        }";
    }

    private string CreateFeedSpecWithAdditionalEndpoint()
    {
        return @"{
            ""openapi"": ""3.0.0"",
            ""info"": {
                ""title"": ""Feed API"",
                ""version"": ""1.0.0""
            },
            ""paths"": {
                ""/organisations"": {
                    ""get"": {
                        ""responses"": {
                            ""200"": {
                                ""description"": ""OK"",
                                ""content"": {
                                    ""application/json"": {
                                        ""schema"": {
                                            ""type"": ""array"",
                                            ""items"": {
                                                ""type"": ""object"",
                                                ""required"": [""id"", ""name""],
                                                ""properties"": {
                                                    ""id"": { ""type"": ""string"" },
                                                    ""name"": { ""type"": ""string"" }
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                },
                ""/custom"": {
                    ""get"": {
                        ""responses"": {
                            ""200"": { ""description"": ""OK"" }
                        }
                    }
                }
            }
        }";
    }

    private string CreateFeedSpecPermissiveOrganisationResponse()
    {
        return @"{
            ""openapi"": ""3.0.0"",
            ""info"": {
                ""title"": ""Feed API"",
                ""version"": ""1.0.0""
            },
            ""paths"": {
                ""/organisations"": {
                    ""get"": {
                        ""responses"": {
                            ""200"": {
                                ""description"": ""OK"",
                                ""content"": {
                                    ""application/json"": {
                                        ""schema"": {
                                            ""type"": ""array"",
                                            ""items"": {
                                                ""type"": ""object"",
                                                ""properties"": {
                                                    ""id"": { ""type"": ""string"" },
                                                    ""name"": { ""type"": ""string"" }
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }";
    }

    private string CreateHsdsProfileSpecWithRequestBody()
    {
        return @"{
            ""openapi"": ""3.0.0"",
            ""info"": {
                ""title"": ""HSDS Profile"",
                ""version"": ""3.0""
            },
            ""paths"": {
                ""/organisations"": {
                    ""post"": {
                        ""requestBody"": {
                            ""required"": true,
                            ""content"": {
                                ""application/json"": {
                                    ""schema"": {
                                        ""type"": ""object"",
                                        ""required"": [""name""],
                                        ""properties"": {
                                            ""name"": { ""type"": ""string"" },
                                            ""description"": { ""type"": ""string"" }
                                        }
                                    }
                                }
                            }
                        },
                        ""responses"": {
                            ""201"": { ""description"": ""Created"" }
                        }
                    }
                }
            }
        }";
    }

    private string CreateFeedSpecMissingRequiredHsdsRequestField()
    {
        return @"{
            ""openapi"": ""3.0.0"",
            ""info"": {
                ""title"": ""Feed API"",
                ""version"": ""1.0.0""
            },
            ""paths"": {
                ""/organisations"": {
                    ""post"": {
                        ""requestBody"": {
                            ""required"": true,
                            ""content"": {
                                ""application/json"": {
                                    ""schema"": {
                                        ""type"": ""object"",
                                        ""properties"": {
                                            ""description"": { ""type"": ""string"" }
                                        }
                                    }
                                }
                            }
                        },
                        ""responses"": {
                            ""201"": { ""description"": ""Created"" }
                        }
                    }
                }
            }
        }";
    }

    private string CreateFeedSpecWithAdditionalHsdsRequestField()
    {
        return @"{
            ""openapi"": ""3.0.0"",
            ""info"": {
                ""title"": ""Feed API"",
                ""version"": ""1.0.0""
            },
            ""paths"": {
                ""/organisations"": {
                    ""post"": {
                        ""requestBody"": {
                            ""required"": true,
                            ""content"": {
                                ""application/json"": {
                                    ""schema"": {
                                        ""type"": ""object"",
                                        ""required"": [""name""],
                                        ""properties"": {
                                            ""name"": { ""type"": ""string"" },
                                            ""description"": { ""type"": ""string"" },
                                            ""customAttribute"": { ""type"": ""string"" }
                                        }
                                    }
                                }
                            }
                        },
                        ""responses"": {
                            ""201"": { ""description"": ""Created"" }
                        }
                    }
                }
            }
        }";
    }

    private class MockHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> _handler;

        public MockHttpMessageHandler()
            : this((req, ct) => new HttpResponseMessage(System.Net.HttpStatusCode.OK))
        {
        }

        public MockHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            try
            {
                var response = _handler(request, cancellationToken);
                return Task.FromResult(response);
            }
            catch (HttpRequestException ex)
            {
                return Task.FromException<HttpResponseMessage>(ex);
            }
        }
    }

    #endregion
}
