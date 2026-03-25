# OpenAPI Validation Flow

## Purpose

This document describes the current end-to-end validation flow used by this API, from incoming request to returned result.

It is aligned with the implementation in:

- `OpenReferralApi/Controllers/OpenApiController.cs`
- `OpenReferralApi/Controllers/OpenReferralUkController.cs`
- `OpenReferralApi.Core/Services/OpenApiValidationService.cs`
- `OpenReferralApi.Core/Services/EndpointTestingService.cs`
- `OpenReferralApi.Core/Services/HsdsComplianceService.cs`
- `OpenReferralApi.Core/Services/JsonValidatorService.cs`
- `OpenReferralApi.Core/Services/OpenApiDiscoveryService.cs`

## High-Level Architecture

```text
Client Request
  -> Controller (raw or mapped format)
    -> Base request validation
      -> OpenApiValidationService
        -> OpenApiDiscoveryService (optional schema URL discovery)
        -> OpenApiSpecFetcher (fetch spec, optional auth, optional lazy ref resolution)
        -> Specification validation
        -> Endpoint testing (dependency-aware + pagination-aware)
        -> JsonValidatorService (response schema validation)
      -> Optional response mapping (Open Referral UK format)
```

## API Endpoints

Two controllers expose validation under different routes/response contracts.

### Raw Result Endpoint

- Route: `POST /openreferral/validate`
- Controller: `OpenReferralController`
- Returns: `OpenApiValidationResult` (raw technical result)

### Open Referral UK Format Endpoint

- Routes:
  - `POST /openreferraluk/validate`
  - `POST /api/openapi/validate` (legacy alias)
- Controller: `OpenReferralUkController`
- Returns: `OpenReferralUKValidationResponse` (mapped format)

## Request Contract

The request payload is `OpenApiValidationRequest`.

```json
{
  "openApiSchema": {
    "url": "https://example.org/openapi.json",
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
    "validateSpecification": true,
    "testEndpoints": true,
    "testOptionalEndpoints": true,
    "treatOptionalEndpointsAsWarnings": true,
    "timeoutSeconds": 30,
    "maxConcurrentRequests": 5,
    "includeResponseBody": true,
    "includeTestResults": true,
    "reportAdditionalFields": false
  }
}
```

Notes:

- `baseUrl` is currently required by base controller validation.
- If `openApiSchema.url` is missing, discovery is attempted from `baseUrl`.

## Configuration Ownership (User vs Server)

### User-Supplied (Request Payload)

The following values come from `OpenApiValidationRequest` and can be changed per request:

- `openApiSchema.url`
- `openApiSchema.authentication` (only applied when server allows it)
- `baseUrl`
- `dataSourceAuth` (only applied when server allows it)
- `options.*` (for example `validateSpecification`, `testEndpoints`, `testOptionalEndpoints`, `treatOptionalEndpointsAsWarnings`, `includeResponseBody`, `includeTestResults`, concurrency/timeout knobs)

### Server-Controlled (App Configuration)

The following values are configured under `OpenApiValidation` in server settings and are **not** client-overridable:

- `StrictOwnSchemaValidation`
- `HsdsValidationMode`
- `AllowUserSuppliedAuth`

Current default app configuration (`appsettings.json`) is:

- `StrictOwnSchemaValidation = false`
- `HsdsValidationMode = FullHsdsRuntime`
- `AllowUserSuppliedAuth = true`

## Flow Details

### 1. Controller-Level Validation

Shared logic in `BaseOpenApiController.ValidateRequestAndReturnErrorIfInvalid` enforces:

- either `openApiSchema.url` or `baseUrl` must be provided
- `baseUrl` must be provided

If validation fails, a `400` with `ValidationProblemDetails` is returned.

### 2. Options Initialization

`OpenApiValidationService` ensures options are non-null:

```csharp
request.Options ??= new OpenApiValidationOptions();
```

### 3. OpenAPI URL Discovery (Optional)

If `openApiSchema.url` is missing, `OpenApiDiscoveryService` attempts discovery by calling `baseUrl`.

Discovery behavior:

1. Try `GET baseUrl`
2. Parse JSON body
3. Prefer `version` field when present and build spec URL from configured specification base
4. Else check `openapi_url` / `openapiUrl` / `open_api_url`
5. If missing or parse/request fails, default to HSDS-UK 1.0 spec URL

The service also returns a human-readable `reason` string captured in metadata (`profileReason`).

### 4. Fetch OpenAPI Spec (Lazy Resolution)

`OpenApiSpecFetcher.FetchOpenApiSpecFromUrlAsync` fetches the spec first with reference resolution disabled.

Important behavior:

- user-supplied authentication is feature-gated by server config
- credentials are only sent to HTTPS targets
- invalid authentication payloads are rejected

References are resolved later only if endpoint testing is enabled. This avoids unnecessary work for spec-only runs.

### 5. Specification Validation

If `options.validateSpecification` is true:

- required structural checks:
  - `openapi` or `swagger`
  - `info`
  - `paths`
- advisory checks:
  - `info.title`
  - `info.version`
- schema validation using `JsonValidatorService`
- additional outputs:
  - schema analysis
  - quality metrics
  - recommendations

Schema URI selection behavior:

- uses `jsonSchemaDialect` when it matches a known dialect
- otherwise falls back to `https://json-schema.org/draft/2020-12/schema`

### 6. Endpoint Testing

If `options.testEndpoints` is true:

1. Resolve OpenAPI references (if not already resolved)
2. Group endpoints by dependency roots
3. For each group:
   - run collection endpoints sequentially (to extract IDs)
   - run parameterized endpoints concurrently (using extracted IDs)

#### Optional Endpoint Detection

Endpoints are treated as optional when tagged `Optional` in OpenAPI tags.

Behavior:

- optional failures can be downgraded to warnings based on options
- required endpoint non-2xx responses are validation errors

### 6.1 Own-Schema Strictness (Server-Controlled)

`StrictOwnSchemaValidation` controls severity for own-schema `ADDITIONAL_FIELD` findings during runtime validation:

- `true`: treated as `Error` (can fail endpoint/result validity)
- `false`: treated as `Warning` (does not fail validity on its own)

This policy is applied centrally by `HsdsComplianceService.ApplyAdditionalFieldPolicy`.

### 6.2 HSDS Runtime Depth (Server-Controlled)

`HsdsValidationMode` is read from server configuration and applied after endpoint testing:

- `SpecAndFeedRuntimeFast`: no HSDS runtime response-schema pass is executed
- `FullHsdsRuntime`: successful endpoint responses are additionally validated against HSDS profile response schemas (for matched required HSDS operations)

If `FullHsdsRuntime` is set but no known HSDS profile schema can be resolved, processing continues with a notification.

#### Compact Truth Table: `HsdsValidationMode`

| `HsdsValidationMode` | Feed spec vs HSDS profile (spec-time) | Runtime validate responses vs feed schema | Runtime validate responses vs HSDS schema | Practical effect |
| --- | --- | --- | --- | --- |
| `SpecAndFeedRuntimeFast` | Yes (when profile schema is resolved and `validateSpecification=true`) | Yes (when `testEndpoints=true`) | No | Faster run; catches feed-spec issues and feed-runtime mismatches, but skips HSDS runtime response conformance pass |
| `FullHsdsRuntime` | Yes (same as above) | Yes (same as above) | Yes (when HSDS profile schema resolved) | Deepest validation; can add HSDS runtime warnings/errors and change endpoint status to `FailedValidation` when HSDS runtime errors occur |

Notes:

- Both modes still run the feed-spec-vs-HSDS-spec comparison step during specification validation when enabled.
- The runtime HSDS pass only applies to successful endpoint responses that map to required HSDS operations.

#### Pagination Testing

For `GET` endpoints with a `page` query parameter:

- test `page=1`
- detect pagination metadata (`total_pages`, `totalPages`, and common variants)
- if multi-page, test middle and last pages
- warn on empty feeds

#### Response Validation

For successful HTTP responses with JSON schema definitions:

- resolve response schema from operation responses
- validate payload using `JsonValidatorService`
- normalize and deduplicate errors (array index normalization is applied)

### 7. Summary and Post-Processing

Service builds summary counts and metadata, then applies output trimming options:

- if `includeResponseBody` is false: clear response bodies from results
- if `includeTestResults` is false: clear detailed test arrays while preserving flattened summary fields

Unhandled execution failures do not throw from the service; they are captured into:

- `isValid = false`
- `notifications[]` with sanitized, user-facing context

## Output Formats

### Raw Output

`OpenReferralController` returns `OpenApiValidationResult` directly.

Key fields:

- `isValid`
- `specificationValidation`
- `endpointTests`
- `summary`
- `duration`
- `notifications`
- `metadata`

### Open Referral UK Mapped Output

`OpenReferralUkController` maps raw output via `IOpenReferralUKValidationResponseMapper`.

Mapped response includes:

- `service` block (url, profile, profileReason, isValid)
- `testSuites` grouped into required vs optional endpoint suites
- `notifications`

## Operational Notes

- Timeouts and request concurrency are controlled by `timeoutSeconds` and `maxConcurrentRequests`.
- Request/response logging is sanitized to reduce sensitive data leakage.
- Authentication data from clients is intentionally constrained by server policy and validation rules.
- `includeResponseBody` and `includeTestResults` are output-shaping options only; they do not change what is tested, only what is returned.

## Quick Sequence Diagram

```text
POST /openreferral*/validate
  -> BaseOpenApiController request checks
  -> OpenApiValidationService
     -> Discover URL (if needed)
     -> Fetch spec (initially unresolved)
     -> Validate spec (optional)
     -> Resolve refs lazily (if endpoint tests enabled)
     -> Test endpoints (dependency + pagination logic)
     -> Build summary + metadata + notifications
  -> (optional) map to Open Referral UK response
  -> 200 OK
```
