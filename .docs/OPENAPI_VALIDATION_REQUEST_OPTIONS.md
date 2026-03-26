# OpenApiValidationRequest Options Guide

This guide explains how to use the OpenApiValidationRequest payload when calling the validation endpoints to validate a feed.

## Validation Endpoints

Use either endpoint with the same request body:

- POST /openreferral/validate (raw validation result)
- POST /openreferraluk/validate (Open Referral UK formatted result)
- POST /api/openapi/validate (legacy alias of openreferraluk route)

## Request Shape

Each authentication object (`openApiSchema.authentication` and `dataSourceAuth`) must specify exactly one authentication method.

```json
{
  "openApiSchema": {
    "url": "https://api.example.org/openapi.json",
    "authentication": {
      "bearerToken": "..."
    }
  },
  "baseUrl": "https://api.example.org",
  "dataSourceAuth": {
    "apiKey": "...",
    "apiKeyHeader": "X-API-Key"
  },
  "options": {
    "includeResponseBody": true,
    "includeTestResults": true,
    "timeoutSeconds": 30,
    "maxConcurrentRequests": 5,
    "reportAdditionalFields": false
  }
}
```

## Top-Level Fields

### openApiSchema

Controls how the validator finds and reads your OpenAPI document.

- url: Direct URL for your OpenAPI document (JSON or YAML).
- authentication: Authentication settings for schema discovery/reference resolution.

Notes:

- If openApiSchema.url is not provided, the service attempts discovery from baseUrl.
- Request-supplied schema authentication is only used when server configuration enables it.
- Schema authentication headers are only applied for HTTPS schema URLs.

### baseUrl

Base URL of the live feed API to test.

- Example: <https://api.example.org>
- Used for endpoint testing and OpenAPI URL discovery fallback.

Important:

- Current controller validation requires baseUrl in the request.

### dataSourceAuth

Authentication used when calling your live API endpoints during endpoint tests.

Supported:

- apiKey + apiKeyHeader
- bearerToken
- basicAuth (username/password)
- customHeaders map

Rule:

- Provide exactly one method per auth object. Do not combine `apiKey`, `bearerToken`, `basicAuth`, and `customHeaders` in the same object.

Important:

- Request-supplied dataSourceAuth is only used when server configuration enables it.
- When disabled, the request is still processed but user-supplied auth values are ignored.
- Data source authentication headers are only applied when endpoint test URLs are HTTPS.

### options

Feature and runtime controls for validation behavior.

If omitted, defaults are applied internally.

**Note on validateSpecification:** Whether to perform OpenAPI specification validation is now controlled by server configuration (`OpenApiValidation.ValidateSpecification` in appsettings.json) and is not overridable per-request. See [Configuration Guide](CONFIGURATION.md#openapivalidation).

**Note on testEndpoints, testOptionalEndpoints, treatOptionalEndpointsAsWarnings:** These settings are controlled by server configuration (`OpenApiValidation.*` in appsettings.json) and are not overridable per-request. User requests only control output formatting, not validation behaviour.

## options Field Reference

### includeResponseBody (default: true)

Controls whether responseBody is returned per HTTP test in results.

- true: includes response body content
- false: strips response bodies from output after testing

Use false when:

- You want smaller responses
- You want to avoid exposing payload content in validator output

### includeTestResults (default: true)

Controls whether detailed per-request TestResults arrays are returned.

- true: include full detailed test entries
- false: keep summary/flattened errors but remove detailed list

Use false when:

- You want compact payloads for dashboards or scheduled jobs

### timeoutSeconds (default: 30)

Per-request timeout budget in seconds for endpoint calls.

Use when:

- Slow upstream endpoints need more time
- You want strict fail-fast behavior with lower values

### maxConcurrentRequests (default: 5)

Maximum parallel endpoint requests during testing.

Use when:

- Increasing throughput for large specs (raise value carefully)
- Reducing load on fragile APIs (lower value)

### reportAdditionalFields (default: false)

When true, JSON validation reports fields present in responses but not in schema.

Use when:

- You need tighter contract enforcement for schema drift detection

## Server Authentication Policy

User-supplied authentication in validation requests is feature-gated by server config:

```json
{
  "Authentication": {
    "AllowUserSuppliedAuth": true
  }
}
```

Behavior:

- When AllowUserSuppliedAuth is true:
  - openApiSchema.authentication can be applied to schema fetch and schema reference resolution.
  - dataSourceAuth can be applied to endpoint test calls.
- When AllowUserSuppliedAuth is false:
  - Both openApiSchema.authentication and dataSourceAuth are ignored.
  - Validation still runs without request-supplied credentials.

Additional rule:

- Schema authentication is only sent for HTTPS schema URLs.
- Data source authentication is only sent for HTTPS endpoint test URLs (derived from baseUrl).

## Practical Configurations

### 1) Full validation (recommended baseline)

```json
{
  "baseUrl": "https://api.example.org",
  "openApiSchema": {
    "url": "https://api.example.org/openapi.json"
  },
  "options": {
    "timeoutSeconds": 30,
    "maxConcurrentRequests": 5
  }
}
```

### 2) Endpoint-focused feed smoke check

```json
{
  "baseUrl": "https://api.example.org",
  "openApiSchema": {
    "url": "https://api.example.org/openapi.json"
  },
  "options": {
    "includeResponseBody": false,
    "includeTestResults": false,
    "timeoutSeconds": 20,
    "maxConcurrentRequests": 3
  }
}
```

### 3) Strict contract monitoring

```json
{
  "baseUrl": "https://api.example.org",
  "openApiSchema": {
    "url": "https://api.example.org/openapi.json"
  },
  "options": {
    "reportAdditionalFields": true,
    "timeoutSeconds": 45,
    "maxConcurrentRequests": 5
  }
}
```

## cURL Example

```bash
curl -X POST http://localhost:5000/openreferraluk/validate \
  -H "Content-Type: application/json" \
  -d '{
    "baseUrl": "https://api.example.org",
    "openApiSchema": {
      "url": "https://api.example.org/openapi.json"
    },
    "dataSourceAuth": {
      "bearerToken": "YOUR_TOKEN"
    },
    "options": {
      "includeResponseBody": false,
      "includeTestResults": true,
      "timeoutSeconds": 30,
      "maxConcurrentRequests": 5,
      "reportAdditionalFields": false
    }
  }'
```

## Notes on Internal Field

- profileReason is an internal field set by the service during OpenAPI URL discovery.
- Do not send profileReason in client requests.
