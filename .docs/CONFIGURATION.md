# OpenReferral API Configuration Guide

This document describes all available configuration settings for the OpenReferral API. Configuration is managed through `appsettings.json` and can be overridden by environment-specific files (`appsettings.Development.json`, `appsettings.Production.json`, etc.) or environment variables.

## Table of Contents

- [Specification](#specification)
- [Database](#database)
- [Authentication](#authentication)
- [Cache](#cache)
- [FeedValidation](#feedvalidation)
- [Security](#security)
- [RateLimiting](#ratelimiting)
- [OpenTelemetry](#opentelemetry)
- [Serilog](#serilog)
- [Environment Variables](#environment-variables)

---

## Specification

Configuration for OpenReferral specification URLs.

### Specification Properties

| Property | Type | Default | Description |
| --- | --- | --- | --- |
| `BaseUrl` | string | `""` | Base URL for OpenReferral specification documents |

### Specification Example

```json
{
  "Specification": {
    "BaseUrl": "https://openreferraluk.org/specifications/"
  }
}
```

### Specification Notes

- Used to construct full URLs for schema validation
- Should include trailing slash
- Can be overridden for local development

---

## Database

MongoDB database connection settings.

### Database Properties

| Property | Type | Default | Description |
| --- | --- | --- | --- |
| `ConnectionString` | string | `""` | MongoDB connection string (e.g., `mongodb://localhost:27017` or Atlas connection string) |
| `DatabaseName` | string | `"oruk-v3"` | Name of the MongoDB database |
| `ServicesCollection` | string | `"services"` | Name of the collection storing service feed data |

### Database Example

```json
{
  "Database": {
    "ConnectionString": "mongodb://localhost:27017",
    "DatabaseName": "oruk-v3",
    "ServicesCollection": "services"
  }
}
```

### Database Notes

- Connection string should be stored securely (use environment variables or secrets management)
- Supports MongoDB Atlas connection strings for cloud deployments
- Leave `ConnectionString` empty to disable database-dependent features

---

## Authentication

Controls authentication behavior for external API requests.

### Authentication Properties

| Property | Type | Default | Description |
| --- | --- | --- | --- |
| `AllowUserSuppliedAuth` | bool | `false` | Whether to allow user-supplied authentication credentials for OpenAPI schema and data source requests |

### Authentication Example

```json
{
  "Authentication": {
    "AllowUserSuppliedAuth": true
  }
}
```

### Authentication Notes

- **Security Consideration**: When enabled, authentication details provided in API validation requests will be used for both schema fetching and data source requests
- When disabled, all external requests are made without authentication
- Default is `false` for security - only enable if you trust the source of validation requests

---

## Cache

In-memory caching configuration for OpenAPI schemas to reduce external HTTP traffic.

### Cache Properties

| Property | Type | Default | Description |
| --- | --- | --- | --- |
| `Enabled` | bool | `true` | Whether schema caching is enabled |
| `ExpirationMinutes` | int | `60` | Duration in minutes for absolute cache expiration (0 = cache until memory pressure) |
| `MaxSizeMB` | int | `100` | Maximum cache size in megabytes |
| `UseSlidingExpiration` | bool | `false` | Enable sliding expiration to extend cache lifetime on access |
| `SlidingExpirationMinutes` | int | `30` | Duration in minutes for sliding expiration (only used when `UseSlidingExpiration` is true) |

### Cache Example

```json
{
  "Cache": {
    "Enabled": true,
    "ExpirationMinutes": 120,
    "MaxSizeMB": 100,
    "UseSlidingExpiration": true,
    "SlidingExpirationMinutes": 60
  }
}
```

### Cache Notes

- Reduces load on external specification servers
- Improves validation response times for repeated requests
- Memory usage is automatically managed within `MaxSizeMB` limit
- Use sliding expiration for frequently accessed schemas

---

## FeedValidation

Background service configuration for automated feed validation.

### FeedValidation Properties

| Property | Type | Default | Description |
| --- | --- | --- | --- |
| `Enabled` | bool | `false` | Whether background feed validation is enabled |
| `IntervalHours` | double | `24` | Time interval in hours between validation runs |
| `RunAtMidnight` | bool | `true` | Whether to schedule validation runs at midnight (when true, first run waits until midnight) |

### FeedValidation Example

```json
{
  "FeedValidation": {
    "Enabled": true,
    "IntervalHours": 48,
    "RunAtMidnight": true
  }
}
```

### FeedValidation Notes

- Requires database configuration to be set
- Validates all registered service feeds in the database
- Results are stored back to the database with timestamps
- Set `Enabled` to `false` to disable the background service entirely

---

## Security

Security-related configuration settings.

### Security Properties

| Property | Type | Default | Description |
| --- | --- | --- | --- |
| `ValidateSslCertificates` | bool | `true` | Whether to validate SSL certificates for HTTPS requests |
| `AllowedCorsOrigins` | string[] | `["*"]` | Array of allowed CORS origins (use `"*"` to allow all) |

### Security Example

```json
{
  "Security": {
    "ValidateSslCertificates": true,
    "AllowedCorsOrigins": [
      "https://example.com",
      "https://app.example.com"
    ]
  }
}
```

### Security Notes

- **`ValidateSslCertificates`**: Set to `false` only in development/testing with self-signed certificates
- **`AllowedCorsOrigins`**:
  - Use `["*"]` for development or public APIs
  - Specify exact origins for production environments
  - Multiple origins can be specified as an array

---

## RateLimiting

Rate limiting configuration for API endpoints.

### RateLimiting Properties

| Property | Type | Default | Description |
| --- | --- | --- | --- |
| `PermitLimit` | int | `100` | Maximum number of requests allowed within the time window |
| `Window` | int | `60` | Time window in seconds for rate limiting |
| `QueueLimit` | int | `0` | Maximum number of requests that can be queued (0 = no queueing) |

### RateLimiting Example

```json
{
  "RateLimiting": {
    "PermitLimit": 100,
    "Window": 60,
    "QueueLimit": 0
  }
}
```

### RateLimiting Notes

- Applied per client IP address
- `PermitLimit` of 100 with `Window` of 60 = 100 requests per minute
- `QueueLimit` of 0 means requests exceeding the limit are immediately rejected
- Increase `QueueLimit` to buffer burst traffic

---

## OpenApiValidation

Server-side OpenAPI validation settings that are not overridable by client requests.

### OpenApiValidation Properties

| Property | Type | Default | Description |
| --- | --- | --- | --- |
| `ValidateSpecification` | bool | `true` | Whether to validate OpenAPI specification structure and quality analysis (server-side setting, not overridable per-request) |
| `OwnSchemaValidation` | string | `"StrictOwnSchemaValidation"` | Own-schema validation mode: `"None"`, `"AllowAdditionalProperties"`, or `"StrictOwnSchemaValidation"` |
| `HsdsValidationMode` | string | `"SpecAndFeedRuntimeFast"` | HSDS conformance depth: `"SpecAndFeedRuntimeFast"` or `"FullHsdsRuntime"` |
| `AllowUserSuppliedAuth` | bool | `false` | Whether to allow user-supplied authentication credentials for OpenAPI schema and data source requests |

### OpenApiValidation Example

```json
{
  "OpenApiValidation": {
    "ValidateSpecification": true,
    "OwnSchemaValidation": "AllowAdditionalProperties",
    "HsdsValidationMode": "SpecAndFeedRuntimeFast",
    "AllowUserSuppliedAuth": true
  }
}
```

### OpenApiValidation Notes

- **`ValidateSpecification`**: This is a server-side setting that applies to all validation requests. Client requests cannot override this setting. When `true`, the response includes comprehensive specification validation results. When `false`, specification validation is skipped entirely.
- **`OwnSchemaValidation`**:
  - `"None"`: Validate endpoint responses against HSDS profile schema instead of feed schema (when profile schema is available)
  - `"AllowAdditionalProperties"`: Keep own-schema validation enabled, but downgrade own-schema `ADDITIONAL_FIELD` findings to warnings
  - `"StrictOwnSchemaValidation"`: Keep own-schema validation enabled and treat own-schema `ADDITIONAL_FIELD` findings as errors
- **`HsdsValidationMode`**:
  - `"SpecAndFeedRuntimeFast"` (default): Performs strict feed-vs-own-spec runtime validation and feed-spec-vs-HSDS-spec comparison
  - `"FullHsdsRuntime"`: Additionally validates live feed responses against HSDS response schemas
- **`AllowUserSuppliedAuth`**: Security consideration - only enable if you trust the source of validation requests

---

## OpenTelemetry

Observability and distributed tracing configuration.

### OpenTelemetry Properties

| Property | Type | Default | Description |
| --- | --- | --- | --- |
| `Enabled` | bool | `false` | Whether OpenTelemetry is enabled |
| `OtlpEndpoint` | string | `null` | OTLP exporter endpoint URL for traces and metrics |

### OpenTelemetry Example

```json
{
  "OpenTelemetry": {
    "Enabled": true,
    "OtlpEndpoint": "http://localhost:4317"
  }
}
```

### OpenTelemetry Notes

- Provides distributed tracing and metrics collection
- Supports any OTLP-compatible backend (Jaeger, Zipkin, Grafana Tempo, etc.)
- When disabled, minimal performance overhead
- Endpoint typically uses gRPC (port 4317) or HTTP (port 4318)

---

## Serilog

Structured logging configuration using Serilog.

### Serilog Properties

Serilog configuration is extensive. See [Serilog documentation](https://github.com/serilog/serilog-settings-configuration) for full details.

### Serilog Common Settings

| Property | Type | Description |
| --- | --- | --- |
| `MinimumLevel.Default` | string | Minimum log level (`Verbose`, `Debug`, `Information`, `Warning`, `Error`, `Fatal`) |
| `WriteTo` | array | Array of log sinks (Console, File, etc.) |

### Serilog Example

```json
{
  "Serilog": {
    "Using": ["Serilog.Sinks.Console", "Serilog.Sinks.File"],
    "MinimumLevel": {
      "Default": "Information",
      "Override": {
        "Microsoft.AspNetCore": "Warning",
        "System.Net.Http.HttpClient": "Warning"
      }
    },
    "WriteTo": [
      {
        "Name": "Console",
        "Args": {
          "outputTemplate": "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}"
        }
      },
      {
        "Name": "File",
        "Args": {
          "path": "logs/oruk-.log",
          "rollingInterval": "Day",
          "retainedFileCountLimit": 7
        }
      }
    ],
    "Enrich": ["FromLogContext"]
  }
}
```

### Serilog Notes

- Console sink is useful for containerized deployments
- File sink stores logs with rolling interval support
- Override specific namespaces to reduce noise
- Use structured logging properties for better searchability

---

## Environment Variables

Configuration values can be overridden using environment variables with double underscore notation:

```bash
# Database configuration
Database__ConnectionString="mongodb://localhost:27017"
Database__DatabaseName="oruk-prod"

# Authentication
Authentication__AllowUserSuppliedAuth=true

# Feed Validation
FeedValidation__Enabled=true
FeedValidation__IntervalHours=24

# Cache
Cache__Enabled=true
Cache__ExpirationMinutes=120

# Security
Security__ValidateSslCertificates=true

# Rate Limiting
RateLimiting__PermitLimit=200
RateLimiting__Window=60

# OpenApiValidation
OpenApiValidation__ValidateSpecification=true
OpenApiValidation__OwnSchemaValidation=AllowAdditionalProperties
OpenApiValidation__HsdsValidationMode="SpecAndFeedRuntimeFast"
OpenApiValidation__AllowUserSuppliedAuth=true

# OpenTelemetry
OpenTelemetry__Enabled=true
OpenTelemetry__OtlpEndpoint="http://otel-collector:4317"
```

### Docker Compose Example

```yaml
services:
  api:
    environment:
      - Database__ConnectionString=mongodb://mongo:27017
      - Database__DatabaseName=oruk-v3
      - FeedValidation__Enabled=true
      - Cache__ExpirationMinutes=60
      - Security__ValidateSslCertificates=true
```

---

## Configuration Files

The API uses a layered configuration approach:

1. **`appsettings.json`** - Base configuration with defaults
2. **`appsettings.{Environment}.json`** - Environment-specific overrides
3. **Environment Variables** - Runtime overrides (highest priority)

### Configuration Environment Detection

The environment is determined by the `ASPNETCORE_ENVIRONMENT` variable:

- `Development` → loads `appsettings.Development.json`
- `Staging` → loads `appsettings.Staging.json`
- `Production` → loads `appsettings.Production.json`

---

## Best Practices

### Best Practices Security

- ✅ Store connection strings in environment variables or secrets management
- ✅ Keep `ValidateSslCertificates` enabled in production
- ✅ Use specific CORS origins instead of `"*"` in production
- ✅ Keep `AllowUserSuppliedAuth` disabled unless explicitly needed

### Best Practices Performance

- ✅ Enable caching to reduce external HTTP requests
- ✅ Tune cache size based on available memory
- ✅ Use sliding expiration for frequently accessed schemas
- ✅ Adjust rate limiting based on expected traffic

### Best Practices Monitoring

- ✅ Enable OpenTelemetry in production for observability
- ✅ Configure appropriate log levels (avoid `Debug` in production)
- ✅ Use structured logging for better analytics
- ✅ Monitor feed validation results if background service is enabled

### Best Practices Development

- ✅ Use local specification URLs for faster development
- ✅ Disable SSL validation when using self-signed certificates
- ✅ Enable more verbose logging for troubleshooting
- ✅ Use shorter cache expiration during development

---

## Troubleshooting

### Troubleshooting Database Connection Issues

- Verify `ConnectionString` format
- Check network connectivity to MongoDB
- Ensure database user has appropriate permissions

### Troubleshooting Caching Not Working

- Check `Cache.Enabled` is `true`
- Verify sufficient memory is available
- Review `ExpirationMinutes` settings

### Troubleshooting Rate Limiting Too Restrictive

- Increase `PermitLimit` or extend `Window`
- Consider enabling `QueueLimit` for burst handling

### Troubleshooting Feed Validation Not Running

- Ensure `FeedValidation.Enabled` is `true`
- Verify database configuration is correct
- Check logs for background service errors
