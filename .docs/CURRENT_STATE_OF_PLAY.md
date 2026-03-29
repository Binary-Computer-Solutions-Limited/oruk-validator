# Current State Of Play (March 2026)

This page captures the currently implemented behavior in the API and complements older architecture notes.

## Canonical Repository

- GitHub: https://github.com/OpenReferralUK/oruk-validator
- Website owner/maintainer reference: iStandUK and the Open Referral UK community

## API Routes In Use

### Validation

- `POST /openreferraluk/validate`
  - Main Open Referral UK formatted response route.
- `POST /api/openapi/validate`
  - Legacy alias for backward compatibility.
- `POST /openreferral/validate`
  - Raw validation response route.

### Feed Validation Operations

- `GET /api/feedvalidation/feeds`
  - List registered feeds and status.
- `POST /api/feedvalidation/validate-all`
  - Trigger manual validation for all feeds.
- `POST /api/feedvalidation/validate/{feedId}`
  - Trigger manual validation for one feed.

## Health Endpoints

- `GET /health-check`
  - Full health check output.
- `GET /health-check/ready`
  - Readiness checks.
- `GET /health-check/live`
  - Liveness endpoint that also includes `schemaWarmup` status in the JSON body.

## Newly Documented Implemented Features

### Correlation IDs

- Middleware reads incoming `X-Correlation-ID` or generates one if missing.
- The correlation ID is added to the response header as `X-Correlation-ID`.

### Schema Warmup Background Service

- Schema warmup runs at startup in the background.
- Warmup is configurable via `SchemaWarmup` options.
- Live health response surfaces warmup state so deploy checks can detect warmup progress.

### Conditional Feed Validation Service Registration

- If MongoDB is configured, feed validation services and background scheduler are enabled.
- If MongoDB is not configured, a null feed validation implementation is used.

### Fixed Window Rate Limiting

- Endpoints use a fixed window limiter policy named `fixed`.
- Defaults: 100 requests per 60 seconds and no queueing.

### Environment Variable Prefix

- Runtime config supports environment variables with the `ORUK_API_` prefix.

## Notes

- Swagger UI is exposed at the app root (`/`) and defaults to `http://localhost:6969` in development launch settings.
- Legacy docs may still contain historical details; use this page plus README as the source of truth for current behavior.
