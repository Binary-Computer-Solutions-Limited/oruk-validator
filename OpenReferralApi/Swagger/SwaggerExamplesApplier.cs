using System.Text.Json.Nodes;
using Microsoft.OpenApi;

namespace OpenReferralApi.Swagger;

internal static class SwaggerExamplesApplier
{
    private const string ValidationRequestExample = """
{
    "openApiSchema": {
        "url": "https://example.org/openapi.json"
    },
    "baseUrl": "https://api.example.org",
    "options": {
        "includeResponseBody": false,
        "includeTestResults": true
    }
}
""";

    private const string OpenReferralValidationResponseExample = """
{
    "isValid": true,
    "summary": {
        "totalEndpoints": 42,
        "successfulTests": 42,
        "failedTests": 0,
        "skippedTests": 0
    },
    "notifications": [],
    "metadata": {
        "profile": "HSDS-UK-3.0"
    }
}
""";

    private const string OpenReferralUkValidationResponseExample = """
{
    "service": {
        "url": "https://api.example.org",
        "isValid": true,
        "profile": "HSDS-UK-3.0",
        "profileReason": "Matched configured schema URL"
    },
    "testSuites": [],
    "specificationValidation": null,
    "notifications": []
}
""";

    private const string ValidationProblemResponseExample = """
{
    "type": "https://tools.ietf.org/html/rfc9110#section-15.5.1",
    "title": "One or more validation errors occurred.",
    "status": 400,
    "errors": {
        "request": [
            "OpenAPI schema URL must be provided or discoverable from baseUrl"
        ]
    }
}
""";

    private const string ProblemDetailsRateLimitExample = """
{
    "type": "https://tools.ietf.org/html/rfc6585#section-4",
    "title": "Too Many Requests",
    "status": 429
}
""";

    private const string ProblemDetailsServerErrorExample = """
{
    "type": "https://tools.ietf.org/html/rfc9110#section-15.6.1",
    "title": "An error occurred while processing your request.",
    "status": 500
}
""";

    private const string FeedListResponseExample = """
[
    {
        "id": "67f6fa9f5cb2fc547f5e2b10",
        "name": "Example Service Feed",
        "url": "https://api.example.org",
        "isUp": true,
        "isValid": true,
        "lastChecked": "2026-03-28T20:00:00Z"
    }
]
""";

    private const string FeedValidateAllResponseExample = """
{
    "totalFeeds": 1,
    "upFeeds": 1,
    "validFeeds": 1,
    "downFeeds": 0,
    "invalidFeeds": 0,
    "averageResponseTimeMs": 123.4,
    "results": [
        {
            "feedId": "67f6fa9f5cb2fc547f5e2b10",
            "feedName": "Example Service Feed",
            "feedUrl": "https://api.example.org",
            "isUp": true,
            "isValid": true,
            "responseTimeMs": 123.4,
            "validationErrorCount": 0
        }
    ]
}
""";

    private const string FeedValidateSingleResponseExample = """
{
    "feedId": "67f6fa9f5cb2fc547f5e2b10",
    "feedName": "Example Service Feed",
    "feedUrl": "https://api.example.org",
    "isUp": true,
    "isValid": true,
    "responseTimeMs": 123.4,
    "validationErrorCount": 0
}
""";

    private const string FeedNotFoundResponseExample = """
{
    "error": "Feed not found",
    "feedId": "67f6fa9f5cb2fc547f5e2b10"
}
""";

    private const string MockApiDetailsResponseExample = """
{
    "api_version": "3.0",
    "data": {
        "id": "example-api",
        "name": "Example Open Referral API",
        "description": "Mock metadata payload"
    }
}
""";

    private const string MockServiceListResponseExample = """
{
    "data": [
        {
            "id": "service-001",
            "name": "Food Bank Support",
            "description": "Support with emergency food parcels"
        }
    ]
}
""";

    private const string MockServiceDetailResponseExample = """
{
    "data": {
        "id": "service-001",
        "name": "Food Bank Support",
        "description": "Support with emergency food parcels",
        "status": "active"
    }
}
""";

    private const string MockOrganizationListResponseExample = """
{
    "data": [
        {
            "id": "org-001",
            "name": "Example Community Trust"
        }
    ]
}
""";

    private const string MockOrganizationDetailResponseExample = """
{
    "data": {
        "id": "org-001",
        "name": "Example Community Trust",
        "description": "Community services provider"
    }
}
""";

    private const string MockV1ValidateResponseExample = """
{
    "isValid": true,
    "profile": "HSDS-UK-1.0",
    "errors": []
}
""";

    private const string MockV1DashboardResponseExample = """
{
    "summary": {
        "totalFeeds": 1,
        "validFeeds": 1,
        "invalidFeeds": 0
    }
}
""";

    private const string MockNotFoundResponseExample = """
{
    "error": "Mock file not found",
    "file": "Mocks/V3.0-UK-Default/service_list.json"
}
""";

    private const string MockServerErrorResponseExample = """
{
    "error": "Error reading mock file",
    "message": "The process cannot access the file because it is being used by another process."
}
""";

    public static void Apply(OpenApiDocument document)
    {
        ApplyExamplesToOperation(
            GetOperation(document, "/openreferral/validate", HttpMethod.Post),
            ValidationRequestExample,
            new Dictionary<string, string>
            {
                ["200"] = OpenReferralValidationResponseExample,
                ["400"] = ValidationProblemResponseExample,
                ["429"] = ProblemDetailsRateLimitExample,
                ["500"] = ProblemDetailsServerErrorExample
            });

        ApplyExamplesToOperation(
            GetOperation(document, "/openreferraluk/validate", HttpMethod.Post),
            ValidationRequestExample,
            new Dictionary<string, string>
            {
                ["200"] = OpenReferralUkValidationResponseExample,
                ["400"] = ValidationProblemResponseExample,
                ["429"] = ProblemDetailsRateLimitExample,
                ["500"] = ProblemDetailsServerErrorExample
            });

        ApplyExamplesToOperation(
            GetOperation(document, "/api/openapi/validate", HttpMethod.Post),
            ValidationRequestExample,
            new Dictionary<string, string>
            {
                ["200"] = OpenReferralUkValidationResponseExample,
                ["400"] = ValidationProblemResponseExample,
                ["429"] = ProblemDetailsRateLimitExample,
                ["500"] = ProblemDetailsServerErrorExample
            });

        ApplyExamplesToOperation(
            GetOperation(document, "/api/feedvalidation/feeds", HttpMethod.Get),
            requestExample: null,
            new Dictionary<string, string>
            {
                ["200"] = FeedListResponseExample,
                ["429"] = ProblemDetailsRateLimitExample,
                ["500"] = ProblemDetailsServerErrorExample
            });

        ApplyExamplesToOperation(
            GetOperation(document, "/api/feedvalidation/validate-all", HttpMethod.Post),
            requestExample: null,
            new Dictionary<string, string>
            {
                ["200"] = FeedValidateAllResponseExample,
                ["429"] = ProblemDetailsRateLimitExample,
                ["500"] = ProblemDetailsServerErrorExample
            });

        ApplyExamplesToOperation(
            GetOperation(document, "/api/feedvalidation/validate/{feedId}", HttpMethod.Post),
            requestExample: null,
            new Dictionary<string, string>
            {
                ["200"] = FeedValidateSingleResponseExample,
                ["404"] = FeedNotFoundResponseExample,
                ["429"] = ProblemDetailsRateLimitExample,
                ["500"] = ProblemDetailsServerErrorExample
            });

        ApplyExamplesToOperation(
            GetOperation(document, "/api/mock", HttpMethod.Get),
            requestExample: null,
            new Dictionary<string, string>
            {
                ["200"] = MockApiDetailsResponseExample,
                ["404"] = MockNotFoundResponseExample,
                ["500"] = MockServerErrorResponseExample
            });

        ApplyExamplesToOperation(
            GetOperation(document, "/api/mock/services", HttpMethod.Get),
            requestExample: null,
            new Dictionary<string, string>
            {
                ["200"] = MockServiceListResponseExample,
                ["404"] = MockNotFoundResponseExample,
                ["500"] = MockServerErrorResponseExample
            });

        ApplyExamplesToOperation(
            GetOperation(document, "/api/mock/services/{id}", HttpMethod.Get),
            requestExample: null,
            new Dictionary<string, string>
            {
                ["200"] = MockServiceDetailResponseExample,
                ["404"] = MockNotFoundResponseExample,
                ["500"] = MockServerErrorResponseExample
            });

        ApplyExamplesToOperation(
            GetOperation(document, "/api/mock/organizations", HttpMethod.Get),
            requestExample: null,
            new Dictionary<string, string>
            {
                ["200"] = MockOrganizationListResponseExample,
                ["404"] = MockNotFoundResponseExample,
                ["500"] = MockServerErrorResponseExample
            });

        ApplyExamplesToOperation(
            GetOperation(document, "/api/mock/organizations/{id}", HttpMethod.Get),
            requestExample: null,
            new Dictionary<string, string>
            {
                ["200"] = MockOrganizationDetailResponseExample,
                ["404"] = MockNotFoundResponseExample,
                ["500"] = MockServerErrorResponseExample
            });

        ApplyExamplesToOperation(
            GetOperation(document, "/api/mock/v1/dashboard", HttpMethod.Get),
            requestExample: null,
            new Dictionary<string, string>
            {
                ["200"] = MockV1DashboardResponseExample,
                ["404"] = MockNotFoundResponseExample,
                ["500"] = MockServerErrorResponseExample
            });

        ApplyExamplesToOperation(
            GetOperation(document, "/api/mock/v1/validate", HttpMethod.Post),
            requestExample: null,
            new Dictionary<string, string>
            {
                ["200"] = MockV1ValidateResponseExample,
                ["404"] = MockNotFoundResponseExample,
                ["500"] = MockServerErrorResponseExample
            });
    }

    private static OpenApiOperation? GetOperation(OpenApiDocument document, string path, HttpMethod method)
    {
        if (!TryGetPathItem(document, path, out var pathItem) || pathItem?.Operations == null)
        {
            return null;
        }

        return pathItem.Operations.TryGetValue(method, out var operation) ? operation : null;
    }

    private static bool TryGetPathItem(OpenApiDocument document, string path, out IOpenApiPathItem? pathItem)
    {
        if (document.Paths.TryGetValue(path, out var exactPathItem))
        {
            pathItem = exactPathItem;
            return true;
        }

        var match = document.Paths.FirstOrDefault(kvp =>
            string.Equals(kvp.Key, path, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrEmpty(match.Key))
        {
            pathItem = match.Value;
            return true;
        }

        pathItem = null;
        return false;
    }

    private static void ApplyExamplesToOperation(
        OpenApiOperation? operation,
        string? requestExample,
        IReadOnlyDictionary<string, string> responseExamples)
    {
        if (operation?.Responses == null)
        {
            return;
        }

        JsonNode? requestExampleNode = null;
        if (!string.IsNullOrWhiteSpace(requestExample))
        {
            requestExampleNode = JsonNode.Parse(requestExample);
        }

        if (requestExampleNode != null && operation.RequestBody?.Content != null)
        {
            foreach (var mediaType in operation.RequestBody.Content.Values)
            {
                mediaType.Example = requestExampleNode;
            }
        }

        foreach (var (statusCode, responseExample) in responseExamples)
        {
            if (!operation.Responses.TryGetValue(statusCode, out var response) || response?.Content == null)
            {
                continue;
            }

            var responseExampleNode = JsonNode.Parse(responseExample);
            if (responseExampleNode == null)
            {
                continue;
            }

            foreach (var mediaType in response.Content.Values)
            {
                mediaType.Example = responseExampleNode;
            }
        }
    }
}
