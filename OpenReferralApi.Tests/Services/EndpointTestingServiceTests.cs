using Microsoft.Extensions.Logging;
using Moq;
using Newtonsoft.Json.Linq;
using OpenReferralApi.Core.Models;
using OpenReferralApi.Core.Services;

namespace OpenReferralApi.Tests.Services;

[TestFixture]
public class EndpointTestingServiceTests
{
    private Mock<ILogger<EndpointTestingService>> _loggerMock = null!;
    private Mock<IJsonValidatorService> _jsonValidatorServiceMock = null!;
    private Mock<IHsdsComplianceService> _hsdsComplianceServiceMock = null!;
    private HttpClient _httpClient = null!;
    private EndpointTestingService _service = null!;

    [SetUp]
    public void Setup()
    {
        _loggerMock = new Mock<ILogger<EndpointTestingService>>();
        _jsonValidatorServiceMock = new Mock<IJsonValidatorService>();
        _hsdsComplianceServiceMock = new Mock<IHsdsComplianceService>();

        _jsonValidatorServiceMock
            .Setup(x => x.ValidateAsync(It.IsAny<ValidationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ValidationResult
            {
                IsValid = true,
                Errors = new List<OpenReferralApi.Core.Models.ValidationError>(),
                SchemaVersion = "test",
                Duration = TimeSpan.Zero
            });

        _hsdsComplianceServiceMock
            .Setup(x => x.ApplyAdditionalFieldPolicy(It.IsAny<ValidationResult?>(), It.IsAny<OpenApiValidationOptions?>()));

        SetupService((_, __) => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent("{}")
        });
    }

    [TearDown]
    public void TearDown()
    {
        _httpClient.Dispose();
    }

    [Test]
    public async Task TestEndpointsAsync_ParameterizedEndpointWithoutExtractedIds_ReturnsNotTestedWarning()
    {
        var spec = CreateParameterizedOnlySpec();

        var results = await _service.TestEndpointsAsync(
            spec,
            "https://api.example.com",
            new OpenApiValidationOptions(),
            null,
            null,
            CancellationToken.None);

        Assert.That(results, Has.Count.EqualTo(1));
        var endpoint = results[0];
        Assert.That(endpoint.Status, Is.EqualTo(EndpointTestStatus.NotTested));
        Assert.That(endpoint.TestResults, Has.Count.EqualTo(1));
        Assert.That(endpoint.TestResults[0].ValidationResult, Is.Not.Null);
        Assert.That(endpoint.TestResults[0].ValidationResult!.Errors,
            Has.Some.Matches<OpenReferralApi.Core.Models.ValidationError>(e => e.ErrorCode == "NO_IDS_AVAILABLE"));
    }

    [Test]
    public async Task TestEndpointsAsync_CollectionThenParameterized_UsesExtractedIdsAndPasses()
    {
        SetupService((request, _) =>
        {
            var uri = request.RequestUri!.ToString();
            if (uri.EndsWith("/services", StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"data\":[{\"id\":\"1\"},{\"id\":\"2\"}]}")
                };
            }

            if (uri.Contains("/services/1", StringComparison.OrdinalIgnoreCase) ||
                uri.Contains("/services/2", StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"id\":\"ok\"}")
                };
            }

            return new HttpResponseMessage(System.Net.HttpStatusCode.NotFound)
            {
                Content = new StringContent("{}")
            };
        });

        var results = await _service.TestEndpointsAsync(
            CreateCollectionAndParameterizedSpec(),
            "https://api.example.com",
            new OpenApiValidationOptions(),
            null,
            null,
            CancellationToken.None);

        Assert.That(results, Has.Count.EqualTo(2));
        var parameterized = results.Single(r => r.Path == "/services/{id}");
        Assert.That(parameterized.Status, Is.EqualTo(EndpointTestStatus.PassedValidation));
        Assert.That(parameterized.TestResults, Has.Count.EqualTo(2));
        Assert.That(parameterized.TestResults.All(r => !string.IsNullOrWhiteSpace(r.TestedId)), Is.True);
    }

    [Test]
    public async Task TestEndpointsAsync_OptionalEndpoint404_ReturnsPassedWithWarnings()
    {
        SetupService((request, _) =>
        {
            if (request.RequestUri!.ToString().Contains("/optional", StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(System.Net.HttpStatusCode.NotFound)
                {
                    Content = new StringContent("{}")
                };
            }

            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("{}")
            };
        });

        var results = await _service.TestEndpointsAsync(
            CreateOptionalEndpointSpec(),
            "https://api.example.com",
            new OpenApiValidationOptions(),
            null,
            null,
            CancellationToken.None);

        Assert.That(results, Has.Count.EqualTo(1));
        var endpoint = results[0];
        Assert.That(endpoint.Status, Is.EqualTo(EndpointTestStatus.PassedWithWarnings));
        Assert.That(endpoint.TestResults[0].ValidationResult!.Errors,
            Has.Some.Matches<OpenReferralApi.Core.Models.ValidationError>(e => e.ErrorCode == "OPTIONAL_ENDPOINT_NON_SUCCESS"));
    }

    [Test]
    public async Task TestEndpointsAsync_RequiredEndpoint404_ReturnsFailedValidation()
    {
        SetupService((_, __) => new HttpResponseMessage(System.Net.HttpStatusCode.NotFound)
        {
            Content = new StringContent("{}")
        });

        var results = await _service.TestEndpointsAsync(
            CreateRequiredEndpointSpec(),
            "https://api.example.com",
            new OpenApiValidationOptions(),
            null,
            null,
            CancellationToken.None);

        Assert.That(results, Has.Count.EqualTo(1));
        var endpoint = results[0];
        Assert.That(endpoint.Status, Is.EqualTo(EndpointTestStatus.FailedValidation));
        Assert.That(endpoint.TestResults[0].ValidationResult!.Errors,
            Has.Some.Matches<OpenReferralApi.Core.Models.ValidationError>(e => e.ErrorCode == "REQUIRED_ENDPOINT_FAILED"));
    }

    [Test]
    public async Task TestEndpointsAsync_PaginatedEndpointEmptyFeed_ReturnsWarning()
    {
        SetupService((request, _) =>
        {
            if (request.RequestUri!.ToString().Contains("/services?page=1", StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"total_pages\":3,\"data\":[]}")
                };
            }

            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("{}")
            };
        });

        var results = await _service.TestEndpointsAsync(
            CreatePaginatedCollectionSpec(),
            "https://api.example.com",
            new OpenApiValidationOptions(),
            null,
            null,
            CancellationToken.None);

        Assert.That(results, Has.Count.EqualTo(1));
        var endpoint = results[0];
        Assert.That(endpoint.Status, Is.EqualTo(EndpointTestStatus.PassedWithWarnings));
        Assert.That(endpoint.TestResults[0].ValidationResult!.Errors,
            Has.Some.Matches<OpenReferralApi.Core.Models.ValidationError>(e => e.ErrorCode == "EMPTY_FEED_WARNING"));
    }

    private void SetupService(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> responder)
    {
        _httpClient?.Dispose();
        _httpClient = new HttpClient(new DelegateHttpMessageHandler(responder));
        _service = new EndpointTestingService(
            _loggerMock.Object,
            _httpClient,
            _jsonValidatorServiceMock.Object,
            _hsdsComplianceServiceMock.Object);
    }

    private static JObject CreateRequiredEndpointSpec()
    {
        return JObject.Parse("""
        {
          "openapi": "3.0.0",
          "info": { "title": "Test API", "version": "1.0.0" },
          "paths": {
            "/required": {
              "get": {
                "responses": {
                  "200": {
                    "description": "ok"
                  }
                }
              }
            }
          }
        }
        """);
    }

    private static JObject CreateOptionalEndpointSpec()
    {
        return JObject.Parse("""
        {
          "openapi": "3.0.0",
          "info": { "title": "Test API", "version": "1.0.0" },
          "paths": {
            "/optional": {
              "get": {
                "tags": ["Optional"],
                "responses": {
                  "200": {
                    "description": "ok"
                  }
                }
              }
            }
          }
        }
        """);
    }

    private static JObject CreatePaginatedCollectionSpec()
    {
        return JObject.Parse("""
        {
          "openapi": "3.0.0",
          "info": { "title": "Test API", "version": "1.0.0" },
          "paths": {
            "/services": {
              "get": {
                "parameters": [
                  { "name": "page", "in": "query", "required": false, "schema": { "type": "integer" } }
                ],
                "responses": {
                  "200": {
                    "description": "ok",
                    "content": {
                      "application/json": {
                        "schema": {
                          "type": "object",
                          "properties": {
                            "data": {
                              "type": "array",
                              "items": {
                                "type": "object",
                                "properties": {
                                  "id": { "type": "string" }
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
            }
          }
        }
        """);
    }

    private static JObject CreateParameterizedOnlySpec()
    {
        return JObject.Parse("""
        {
          "openapi": "3.0.0",
          "info": { "title": "Test API", "version": "1.0.0" },
          "paths": {
            "/services/{id}": {
              "get": {
                "parameters": [
                  { "name": "id", "in": "path", "required": true, "schema": { "type": "string" } }
                ],
                "responses": {
                  "200": {
                    "description": "ok"
                  }
                }
              }
            }
          }
        }
        """);
    }

    private static JObject CreateCollectionAndParameterizedSpec()
    {
        return JObject.Parse("""
        {
          "openapi": "3.0.0",
          "info": { "title": "Test API", "version": "1.0.0" },
          "paths": {
            "/services": {
              "get": {
                "responses": {
                  "200": {
                    "description": "ok",
                    "content": {
                      "application/json": {
                        "schema": {
                          "type": "object",
                          "properties": {
                            "data": {
                              "type": "array",
                              "items": {
                                "type": "object",
                                "properties": {
                                  "id": { "type": "string" }
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
            },
            "/services/{id}": {
              "get": {
                "parameters": [
                  { "name": "id", "in": "path", "required": true, "schema": { "type": "string" } }
                ],
                "responses": {
                  "200": {
                    "description": "ok"
                  }
                }
              }
            }
          }
        }
        """);
    }

    private sealed class DelegateHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> _responder;

        public DelegateHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> responder)
        {
            _responder = responder;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(_responder(request, cancellationToken));
        }
    }
}
