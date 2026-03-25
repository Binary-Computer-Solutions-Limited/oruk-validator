# Technical Architecture - Open Referral UK API

## Table of Contents
- [Overview](#overview)
- [Technology Stack](#technology-stack)
- [System Architecture](#system-architecture)
- [Core Components](#core-components)
- [Data Flow](#data-flow)
- [API Endpoints](#api-endpoints)
- [Validation Process](#validation-process)
- [Data Persistence](#data-persistence)
- [External Integrations](#external-integrations)
- [Configuration & Settings](#configuration--settings)
- [Background Services](#background-services)
- [Security Considerations](#security-considerations)
- [Deployment Architecture](#deployment-architecture)
- [Development Setup](#development-setup)
- [Testing Strategy](#testing-strategy)
- [Performance & Caching](#performance--caching)
- [Monitoring & Observability](#monitoring--observability)
- [Extension Points](#extension-points)

---

## Overview

The Open Referral UK (ORUK) API is a .NET-based validation service designed to verify that service directory APIs conform to the Human Services Data Specification UK (HSDS-UK) standard. The system validates both individual service endpoints and bulk dashboard services against JSON schemas, ensuring compliance with Open Referral UK standards.

### Purpose
- Validate service directory APIs against HSDS-UK specifications (versions 1.0, 3.0)
- Provide detailed validation reports with schema compliance issues
- Support batch validation of multiple registered services
- Enable periodic automated validation through background workers
- Offer a public API for third-party validation requests

### Key Features
- **Multi-version Support**: Validates against HSDS-UK v1.0 and v3.0
- **Flexible Validation**: Single service or bulk dashboard validation
- **Pagination Testing**: Comprehensive validation of paginated endpoints
- **Test Profiles**: Configurable test suites for different validation scenarios
- **Caching**: In-memory caching for improved performance
- **Background Processing**: Periodic validation scheduler
- **Detailed Reporting**: Issue-level error messages with line numbers and paths

---

## Technology Stack

### Runtime & Framework
- **.NET 10.0**: Core runtime and framework
- **ASP.NET Core**: Web API framework
- **C# 13+**: Programming language with nullable reference types

### Core Libraries
```xml
MongoDB.Driver (3.6.0)           - MongoDB data access
Newtonsoft.Json.Schema (4.0.1)   - JSON Schema validation
FluentResults (4.0.0)            - Result pattern implementation
Swashbuckle.AspNetCore (10.1.0)  - OpenAPI/Swagger documentation
```

### Health & Monitoring
```xml
AspNetCore.HealthChecks.MongoDb (9.0.0)
AspNetCore.HealthChecks.UI.Client (9.0.0)
```

### GitHub Integration
```xml
Octokit (14.0.0)                 - GitHub API client
GitHubJwt (0.0.6)                - GitHub JWT authentication
```

### Additional Dependencies
```xml
JsonSchema.Net (8.0.5)           - Additional JSON schema support
System.IdentityModel.Tokens.Jwt (8.15.0) - JWT handling
OpenTelemetry.Exporter.Console (1.15.0) - OpenTelemetry console exporter
OpenTelemetry.Exporter.OpenTelemetryProtocol (1.15.0) - OTLP exporter
OpenTelemetry.Extensions.Hosting (1.15.0) - OpenTelemetry hosting extensions
OpenTelemetry.Instrumentation.AspNetCore (1.15.0) - ASP.NET Core instrumentation
OpenTelemetry.Instrumentation.Http (1.15.0) - HTTP instrumentation
AspNetCore.HealthChecks.Uris (9.0.0) - URL health checks
```

### Infrastructure
- **MongoDB**: NoSQL database for service metadata storage
- **Docker**: Containerization (multi-stage builds)
- **Heroku**: Cloud deployment platform
- **GitHub**: Version control and CI/CD integration

---

## System Architecture

### High-Level Architecture

```
┌─────────────────────────────────────────────────────────────────┐
│                         Client Layer                             │
│  ┌────────────────┐  ┌────────────────┐  ┌──────────────────┐  │
│  │  Web Browsers  │  │  API Clients   │  │  Swagger UI      │  │
│  └────────────────┘  └────────────────┘  └──────────────────┘  │
└────────────────────────────┬────────────────────────────────────┘
                             │ HTTP/HTTPS
┌────────────────────────────▼────────────────────────────────────┐
│                   ASP.NET Core Web API                           │
│  ┌──────────────────────────────────────────────────────────┐  │
│  │               Controllers Layer                           │  │
│  │  ┌────────────────────┐  ┌─────────────────────────┐     │  │
│  │  │ OpenApiController  │  │ MockController          │     │  │
│  │  │ /api/validate      │  │ /api/mock               │     │  │
│  │  └────────────────────┘  └─────────────────────────┘     │  │
│  └──────────────────────────────────────────────────────────┘  │
│                             ▼                                    │
│  ┌──────────────────────────────────────────────────────────┐  │
│  │               Services Layer (Business Logic)             │  │
│  │  ┌────────────────┐  ┌─────────────────┐  ┌───────────┐ │  │
│  │  │ValidatorService│  │DashboardService │  │RequestServ│ │  │
│  │  └────────────────┘  └─────────────────┘  └───────────┘ │  │
│  │  ┌────────────────┐  ┌─────────────────┐  ┌───────────┐ │  │
│  │  │TestProfileServ │  │PaginationTestSrv│  │PeriodicVal│ │  │
│  │  └────────────────┘  └─────────────────┘  └───────────┘ │  │
│  └──────────────────────────────────────────────────────────┘  │
│                             ▼                                    │
│  ┌──────────────────────────────────────────────────────────┐  │
│  │               Repository Layer                            │  │
│  │  ┌────────────────────────────────────────────────────┐  │  │
│  │  │  DataRepository (MongoDB Driver)                   │  │  │
│  │  └────────────────────────────────────────────────────┘  │  │
│  └──────────────────────────────────────────────────────────┘  │
│                             ▼                                    │
│  ┌──────────────────────────────────────────────────────────┐  │
│  │          Static Resources (File System)                  │  │
│  │  ┌─────────────────┐  ┌──────────────────────────────┐  │  │
│  │  │ JSON Schemas    │  │ Mock Data                    │  │  │
│  │  │ V1.0-UK/        │  │ V1.0-UK-Default/            │  │  │
│  │  │ V3.0-UK/        │  │ V3.0-UK-Default/            │  │  │
│  │  │                 │  │ V3.0-UK-Fail/               │  │  │
│  │  │                 │  │ V3.0-UK-Test/               │  │  │
│  │  │                 │  │ V3.0-UK-Warn/               │  │  │
│  │  └─────────────────┘  └──────────────────────────────┘  │  │
│  └──────────────────────────────────────────────────────────┘  │
└─────────────────────────────────────────────────────────────────┘
                             │
         ┌───────────────────┴───────────────────┐
         ▼                                       ▼
┌──────────────────┐                   ┌──────────────────────┐
│   MongoDB Atlas  │                   │  External Service    │
│                  │                   │  Directory APIs      │
│  Collections:    │                   │  (Being Validated)   │
│  - services      │                   │                      │
│  - columns       │                   │  - HSDS-UK v1.0      │
│  - views         │                   │  - HSDS-UK v3.0      │
└──────────────────┘                   │  - HSDS-UK v3.1      │
                                       └──────────────────────┘
```

### Project Structure

```
OpenReferralApi/
├── OpenReferralApi/              # Main API project
│   ├── Controllers/              # HTTP endpoints
│   ├── Services/                 # Business logic
│   │   └── Interfaces/          # Service contracts
│   ├── Repositories/            # Data access
│   │   └── Interfaces/          # Repository contracts
│   ├── Models/                  # Domain models & DTOs
│   │   ├── Responses/          # Response DTOs
│   │   └── Settings/           # Configuration models
│   ├── Schemas/                 # JSON Schema files
│   │   ├── V1.0-UK/
│   │   └── V3.0-UK/
│   ├── TestProfiles/            # Test suite definitions
│   ├── Constants/               # Constant values
│   └── Program.cs               # App startup & DI config
├── OpenReferralApi.Core/        # Shared core library
│   ├── Models/                  # Shared domain models
│   └── Services/                # Shared services
├── OpenReferralApi.Tests/       # Unit & integration tests
│   ├── Mocks/                   # Test data
│   └── TestData/                # Mock responses
└── Docs/                        # Documentation
```

---

## Core Components

### 1. Controllers Layer

#### OpenApiController
**Path**: `/api/validate`  
**Responsibility**: Handles OpenAPI validation requests

```csharp
[HttpPost("validate")]
public async Task<IActionResult> ValidateOpenApi(
    [FromQuery] string url,
    [FromQuery] string? version,
    [FromQuery] bool? validateExamples)
```

**Functionality**:
- Accepts OpenAPI URL and optional parameters
- Delegates to `IOpenApiValidationService` and `IProfileDiscoveryService`
- Returns validation results with detailed error information
- Supports rate limiting and correlation ID tracking

#### MockController
**Path**: `/api/mock`  
**Responsibility**: Serves mock HSDS-UK data for testing

**Functionality**:
- Provides mock responses for different test scenarios
- Supports V1.0-UK and V3.0-UK data formats
- Includes test scenarios: Default, Fail, Test, Warn
- Useful for testing validation logic without external dependencies

### 2. Services Layer

#### OpenApiValidationService
**Lifetime**: Scoped  
**Core Responsibility**: OpenAPI schema validation and endpoint testing

**Key Methods**:
- `ValidateAsync(url, version, validateExamples)`: Main validation orchestrator
- `ValidateEndpoint(endpoint, schema)`: Individual endpoint validation
- `ValidateExamples(openApiDoc)`: Example validation

**Process Flow**:
1. Discover OpenAPI specification at provided URL
2. Parse and validate OpenAPI document structure
3. Validate against HSDS-UK schemas
4. Optionally validate examples in specification
5. Return comprehensive validation results

**Dependencies**:
- `IJsonValidatorService`: JSON Schema validation
- `IJsonSchemaResolverService`: Schema resolution
- `HttpClient`: HTTP communication

#### ProfileDiscoveryService
**Lifetime**: Scoped  
**Core Responsibility**: OpenAPI specification discovery and parsing

**Key Methods**:
- `DiscoverAsync(url)`: Discover OpenAPI specification
- `ParseOpenApiDocument(content)`: Parse OpenAPI JSON/YAML
- `ExtractEndpoints(openApiDoc)`: Extract endpoint information

#### JsonValidatorService
**Lifetime**: Scoped  
**Core Responsibility**: JSON Schema validation

**Key Methods**:
- `ValidateAsync(jsonData, schema)`: Validate JSON against schema
- `ParseValidationErrors(errors)`: Convert schema errors to readable format

**Process Flow**:
1. Parse JSON data
2. Load and resolve schema references
3. Validate using Newtonsoft.Json.Schema
4. Format validation errors with paths and messages

#### RequestProcessingService
**Lifetime**: Singleton  
**Core Responsibility**: HTTP request processing and caching

**Features**:
- 2-minute timeout on requests (configurable)
- Memory cache support for responses
- Custom User-Agent header (`OpenReferral-Validator/1.0`)
- Compression support (GZip, Deflate)
- SSL certificate validation (configurable)
- Result pattern for error propagation

**Key Method**:
```csharp
Task<Result<string>> ProcessRequestAsync(string url)
```

#### JsonSchemaResolverService
**Lifetime**: Scoped  
**Core Responsibility**: JSON Schema resolution and loading

**Key Methods**:
- `ResolveSchema(schemaPath)`: Load schema from file system
- `ResolveReferences(schema)`: Resolve $ref references in schemas

**Schema Resolution Logic**:
1. Load base schema from Schemas directory
2. Resolve any $ref references to other schemas
3. Cache resolved schemas for performance
4. Return fully resolved schema for validation

#### PathParsingService
**Lifetime**: Scoped  
**Core Responsibility**: URL and path parsing

**Key Methods**:
- `ParsePath(path)`: Parse URL path components
- `ExtractParameters(path)`: Extract path parameters
- `BuildUrl(baseUrl, path, queryParams)`: Construct URLs

**Validation Checks**:
- URL format validation
- Path parameter extraction
- Query string parsing
- URL normalization

#### OpenApiToValidationResponseMapper
**Lifetime**: Scoped  
**Core Responsibility**: Maps OpenAPI validation results to response DTOs

**Key Methods**:
- `MapToValidationResponse(result)`: Convert validation result to response DTO
- `MapErrors(errors)`: Format error messages
- `MapEndpointResults(endpoints)`: Map endpoint validation results

**Functionality**:
- Transforms internal validation results to API responses
- Formats error messages for readability
- Groups validation issues by severity
- Provides detailed endpoint-level feedback

### 3. Middleware Layer

#### CorrelationIdMiddleware
**Lifetime**: Singleton (per request)  
**Core Responsibility**: Request tracking and correlation

**Functionality**:
- Generates or extracts correlation ID from request headers
- Adds correlation ID to response headers
- Logs correlation ID for request tracing
- Enables distributed tracing across services

#### GlobalExceptionHandler
**Lifetime**: Singleton  
**Core Responsibility**: Centralized exception handling

**Functionality**:
- Catches unhandled exceptions
- Formats error responses consistently
- Logs exceptions with context
- Returns appropriate HTTP status codes
- Includes correlation ID in error responses

---

## Data Flow

### Single Service Validation Flow

```
┌──────────┐
│  Client  │
└────┬─────┘
     │ POST /api/validate?serviceUrl=...&profile=...
     ▼
┌────────────────────┐
│ValidateController │
└────────┬───────────┘
         │ ValidateService(url, profile)
         ▼
┌────────────────────┐
│ ValidatorService   │──────────────┐
└────────┬───────────┘              │
         │                          │
         │ 1. SelectTestSchema      │
         ▼                          │
┌────────────────────┐              │
│TestProfileService  │              │
└────────┬───────────┘              │
         │ Fetch '/' endpoint       │
         │ Read version field       │
         │ Return HSDS-UK-X.0       │
         ▼                          │
┌────────────────────┐              │
│  RequestService    │◄─────────────┤
└────────┬───────────┘              │
         │ HTTP GET                 │
         ▼                          │
┌────────────────────┐              │
│  Service API       │              │
│  (External)        │              │
└────────┬───────────┘              │
         │ JSON Response            │
         ▼                          │
┌────────────────────┐              │
│  RequestService    │──────────────┤
│  (Cache 20s)       │              │
└────────┬───────────┘              │
         │ Return JsonNode          │
         ▼                          │
┌────────────────────┐              │
│ ValidatorService   │◄─────────────┘
└────────┬───────────┘
         │ 2. Load Test Profile (TestProfiles/HSDS-UK-X.0.json)
         │ 3. Execute Test Groups in Parallel
         │ 4. For each test case:
         │    ├─ Fetch endpoint response
         │    ├─ Load JSON Schema (Schemas/V3.0-UK/...)
         │    ├─ Validate with Newtonsoft.Json.Schema
         │    └─ Run pagination tests
         │ 5. Aggregate Issues
         ▼
┌────────────────────┐
│ ValidationResponse │
│  - Service info    │
│  - Test suites     │
│  - Issues list     │
└────────┬───────────┘
         │ Return 200 OK or 400 Bad Request
         ▼
┌──────────┐
│  Client  │
└──────────┘
```

### Dashboard Validation Flow

```
┌──────────┐
│  Client  │
└────┬─────┘
     │ GET /api/dashboard/validate
     ▼
┌────────────────────┐
│DashboardController │
└────────┬───────────┘
         │ ValidateDashboardServices()
         ▼
┌────────────────────┐
│ DashboardService   │
└────────┬───────────┘
         │ 1. GetServices()
         ▼
┌────────────────────┐
│  DataRepository    │
└────────┬───────────┘
         │ Find active services
         ▼
┌────────────────────┐
│    MongoDB         │
│  services collection
└────────┬───────────┘
         │ List<ServiceData>
         ▼
┌────────────────────┐
│ DashboardService   │
└────────┬───────────┘
         │ 2. Parallel validation (Task.WhenAll)
         │
         ├─────────────┬─────────────┬─────────────┐
         ▼             ▼             ▼             ▼
  ValidateOne   ValidateOne   ValidateOne   ValidateOne
   Service#1     Service#2     Service#3     Service#N
         │             │             │             │
         │    Each service validation:              │
         │    ├─ IsServiceAvailable()               │
         │    │  └─ GET {url}/services              │
         │    ├─ ValidatorService.ValidateService() │
         │    └─ UpdateServiceTestStatus()          │
         │                                           │
         └─────────────┴─────────────┴───────────────┘
                       │
                       ▼
         ┌─────────────────────────┐
         │ List<DashboardValidation│
         │       Response>          │
         └────────┬────────────────┘
                  │ Return results with timing
                  ▼
         ┌──────────┐
         │  Client  │
         └──────────┘
```

---

## API Endpoints

### 1. POST /api/validate
**Purpose**: Validate an OpenAPI specification

**Request**:
```http
POST /api/validate?url=https://example.com/openapi.json&version=3.0&validateExamples=true
```

**Query Parameters**:
| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| url | string | Yes | Full URL to the OpenAPI specification |
| version | string | No | HSDS-UK version (1.0, 3.0). Auto-detected if omitted |
| validateExamples | bool | No | Whether to validate examples in the specification |

**Response 200 OK**:
```json
{
  "url": "https://example.com/openapi.json",
  "version": "3.0",
  "isValid": true,
  "errors": [],
  "warnings": [],
  "endpoints": [
    {
      "path": "/services",
      "method": "GET",
      "isValid": true,
      "errors": []
    }
  ],
  "validatedAt": "2026-01-19T10:30:00Z"
}
```

**Response 400 Bad Request**:
```json
{
  "errors": [
    "Invalid URL provided"
  ]
}
```

**Response 500 Internal Server Error**:
```json
{
  "title": "Internal Server Error",
  "detail": "A critical failure occurred during validation...",
  "instance": "/api/validate?serviceUrl=...",
  "status": 500
}
```

### 2. GET /api/mock/{version}/{scenario}
**Purpose**: Serve mock HSDS-UK data for testing

**Request**:
```http
GET /api/mock/v3.0-uk-default/services
```

**Path Parameters**:
| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| version | string | Yes | Version directory (v1.0-uk-default, v3.0-uk-default, v3.0-uk-fail, v3.0-uk-test, v3.0-uk-warn) |
| scenario | string | Yes | Endpoint path within the mock data |

**Response 200 OK**:
```json
{
  "services": [
    {
      "id": "12345",
      "name": "Example Service",
      "description": "A mock service for testing"
    }
  ]
}
```

### 3. GET /
**Purpose**: Serve Swagger/OpenAPI JSON specification

### 4. GET /swagger
**Purpose**: Interactive Swagger UI for API exploration

### 5. GET /health-check
**Purpose**: Comprehensive health check endpoint for monitoring

**Response 200 OK**:
```json
{
  "status": "Healthy",
  "checks": {
    "self": {
      "status": "Healthy"
    },
    "mongodb": {
      "status": "Healthy"
    },
    "openreferral-website": {
      "status": "Healthy"
    }
  },
  "totalDuration": "00:00:00.1234567"
}
```

### 6. GET /health-check/ready
**Purpose**: Readiness probe for container orchestration

**Response 200 OK**: Returns health status for services tagged with "ready"

### 7. GET /health-check/live
**Purpose**: Liveness probe for container orchestration

**Response 200 OK**:
```json
{
  "status": "Healthy",
  "timestamp": "2026-01-19T10:30:00Z"
}
```

---

## Validation Process

### Test Profile Structure

Test profiles define the validation test suite for each HSDS-UK version.

**Location**: `TestProfiles/HSDS-UK-{version}.json`

**Schema**:
```json
{
  "profile": "HSDS-UK-3.0",
  "testGroups": [
    {
      "name": "Core Endpoints",
      "description": "Validates required endpoints",
      "required": true,
      "messageLevel": "error",
      "tests": [
        {
          "name": "Services List",
          "description": "GET /services endpoint",
          "endpoint": "/services",
          "schema": "V3.0-UK/services-schema.json",
          "pagination": true,
          "useIdFrom": null
        },
        {
          "name": "Service Detail",
          "description": "GET /services/{id}",
          "endpoint": "/services/",
          "schema": "V3.0-UK/service-schema.json",
          "pagination": false,
          "useIdFrom": "/services"
        }
      ]
    }
  ]
}
```

### Validation Steps

#### 1. Profile Selection
```csharp
// Priority order:
// 1. Explicit profile parameter
// 2. Version field from GET {url}/
// 3. Default to HSDS-UK-3.0
```

#### 2. Schema Loading
- Schemas stored in `Schemas/{version}/` directory
- Loaded using `Newtonsoft.Json.Schema.JSchema`
- Supports JSON Schema Draft 4/6/7

#### 3. Test Execution
```csharp
// Parallel execution of test groups
var testGroupTasks = testProfile.TestGroups
    .Select(testGroup => TestTestGroup(serviceUrl, testSchema, testGroup));
var results = await Task.WhenAll(testGroupTasks);
```

#### 4. Response Validation
```csharp
// Parse response and validate against schema
var responseJToken = JToken.Parse(response.ToString());
var isValid = responseJToken.IsValid(schema, out IList<ValidationError> errors);
```

#### 5. Issue Collection
```csharp
// Convert validation errors to Issue objects
var issues = errors.Select(error => new Issue
{
    Description = "Schema validation issue",
    Name = error.ErrorType.ToString(),
    Message = error.Message,
    ErrorAt = $"{error.Path}, line {error.LineNumber}",
    ErrorIn = error.SchemaId.ToString()
});
```

### Pagination Validation

**Tests Performed**:
1. **First Page Validation**
   - Correct page number (1)
   - Content matches schema
   - Metadata consistency

2. **Last Page Validation**
   - Correct `last_page` flag
   - Content within expected size
   - No broken pagination links

3. **Random Page Sampling**
   - Fetch 3 random pages
   - Validate content consistency
   - Check ID uniqueness

4. **Edge Cases**
   - Empty pages
   - Single-page results
   - Large page sizes

---

## Data Persistence

### MongoDB Schema

#### Services Collection
```json
{
  "_id": "ObjectId",
  "name": { "value": "Service Name" },
  "serviceUrl": { 
    "url": "https://example.com/api",
    "value": "https://example.com/api"
  },
  "schemaVersion": { "value": "3.0" },
  "active": true,
  "statusIsUp": { "value": "pass" },
  "statusIsValid": { "value": "pass" },
  "statusOverall": { "value": "pass" },
  "lastTested": { "value": "2026-01-09T10:30:00Z" },
  "testDate": { "value": "2026-01-09T10:30:00Z" }
}
```

#### Columns Collection
```json
{
  "_id": "ObjectId",
  "fieldName": "name",
  "displayName": "Service Name",
  "dataType": "string",
  "visible": true
}
```

#### Views Collection
```json
{
  "_id": "ObjectId",
  "viewName": "default",
  "columns": ["name", "status", "lastTested"],
  "sortField": "name",
  "sortOrder": "asc"
}
```

### Database Operations

**Read Operations**:
- `GetServices()`: Retrieves all active services
- `GetServiceById(id)`: Retrieves single service
- `GetColumns()`: Retrieves column definitions
- `GetViews()`: Retrieves view configurations

**Write Operations**:
- `UpdateServiceTestStatus()`: Updates validation results
  - Sets `lastTested` timestamp
  - Updates `statusIsUp` (API availability)
  - Updates `statusIsValid` (validation result)
  - Updates `statusOverall` (combined status)

---

## External Integrations

### 1. Service Directory APIs
**Purpose**: APIs being validated for HSDS-UK compliance

**Supported Versions**:
- HSDS-UK 1.0
- HSDS-UK 3.0

**Expected Endpoints**:
- `GET /`: Root endpoint with version metadata
- `GET /services`: Paginated service list
- `GET /services/{id}`: Individual service details
- Additional endpoints per HSDS-UK specification

**Requirements**:
- Must return valid JSON
- Must include CORS headers for browser clients
- Should respond within 30 seconds
- Must follow HSDS-UK schema specifications

### 2. MongoDB Atlas
**Purpose**: Service registry and validation history

**Connection**:
```json
{
  "Database": {
    "ConnectionString": "mongodb://...",
    "DatabaseName": "oruk",
    "ServicesCollection": "services",
    "ColumnsCollection": "columns",
    "ViewsCollection": "views"
  }
}
```

**Features Used**:
- Document queries with filters
- Update operations
- Connection pooling (singleton repository)

### 3. GitHub API (Optional)
**Purpose**: Potential integration for issue tracking or schema updates

**Configuration**:
```json
{
  "Github": {
    "token": "ghp_...",
    "appId": "12345",
    "installationId": "67890"
  }
}
```

**Dependencies**:
- `Octokit` (14.0.0)
- `GitHubJwt` (0.0.6)

---

## Configuration & Settings

### Configuration Sources
1. `appsettings.json` (base configuration)
2. `appsettings.Development.json` (environment-specific)
3. `appsettings.Production.json` (production-specific)
4. `appsettings.Staging.json` (staging-specific)
5. Environment variables with `ORUK_API_` prefix

### Settings Structure

#### Database Settings
```csharp
public class DatabaseSettings
{
    public string ConnectionString { get; set; }
    public string DatabaseName { get; set; }
}
```

#### Security Settings
```csharp
public class SecuritySettings
{
    public string[] AllowedCorsOrigins { get; set; }
    public bool ValidateSslCertificates { get; set; }
}
```

#### Rate Limiting Settings
```csharp
public class RateLimitingSettings
{
    public int PermitLimit { get; set; }  // default: 100
    public int Window { get; set; }       // seconds, default: 60
    public int QueueLimit { get; set; }   // default: 0
}
```

#### OpenTelemetry Settings
```csharp
public class OpenTelemetrySettings
{
    public bool Enabled { get; set; }
    public string OtlpEndpoint { get; set; }
}
```

### Environment Variable Mapping

```bash
# Example environment variables
ORUK_API_Database__ConnectionString="mongodb://localhost:27017"
ORUK_API_Database__DatabaseName="oruk"
ORUK_API_Security__AllowedCorsOrigins__0="https://example.com"
ORUK_API_Security__ValidateSslCertificates="true"
ORUK_API_RateLimiting__PermitLimit="100"
ORUK_API_RateLimiting__Window="60"
ORUK_API_OpenTelemetry__Enabled="true"
ORUK_API_OpenTelemetry__OtlpEndpoint="http://localhost:4317"
ORUK_API_FeedValidation__Enabled="true"
ORUK_API_FeedValidation__IntervalHours="24"
ORUK_API_FeedValidation__RunAtMidnight="true"
```

#### Feed Validation Settings
```csharp
public class FeedValidationSettings
{
    public bool Enabled { get; set; }           // default: false
    public double IntervalHours { get; set; }   // default: 24
    public bool RunAtMidnight { get; set; }     // default: true
}
```

---

## Background Services

### FeedValidationBackgroundService

**Purpose**: Automatically validates all registered feeds on a scheduled basis

**Lifetime**: Singleton background service running throughout application lifetime

**Key Features**:

- **Scheduled Execution**: Runs at midnight UTC (configurable) or fixed intervals
- **Multiple Feed Validation**: Tests all registered feeds in parallel
- **Status Updates**: Stores validation results in MongoDB
- **Graceful Error Handling**: Captures and logs individual feed failures without stopping the service
- **Performance Monitoring**: Logs validation duration and summary statistics

**Configuration**:

```json
{
  "FeedValidation": {
    "Enabled": false,
    "IntervalHours": 24,
    "RunAtMidnight": true
  }
}
```

**Settings**:

| Setting | Description | Default |
|---------|-------------|---------|
| `Enabled` | Enable/disable the background service | `false` |
| `IntervalHours` | Hours between validation runs (used if `RunAtMidnight` is false) | `24` |
| `RunAtMidnight` | Schedule validations at midnight UTC (when true) or use fixed interval | `true` |

**Validation Process**:

1. **Startup**: Waits until the next scheduled run time
2. **Retrieval**: Loads all registered feeds from MongoDB
3. **Validation**: Tests each feed in parallel:
   - Checks if feed URL is reachable (IsUp)
   - Validates response against HSDS-UK schema (IsValid)
   - Records response time and error details
4. **Storage**: Updates feed status in database
5. **Logging**: Records summary statistics (total, up, valid, down feeds)
6. **Scheduling**: Waits for next scheduled run

**Logging Output**:

```
Feed Validation Background Service started. Interval: 24 hours, RunAtMidnight: True
Next validation scheduled for 2026-01-30 00:00:00 (in 3.2 hours)
Starting scheduled feed validation run at 2026-01-30 00:00:00
Found 42 registered feeds to validate
Feed validation summary: Total=42, Up=40, Valid=38, Down=2, Duration=125.3s
Completed scheduled feed validation run at 2026-01-30 02:05:23
```

**Error Handling**:

- Individual feed failures don't stop the service
- Failed feeds are logged with error details
- Service continues to next scheduled run
- Gracefully stops when application shuts down

---

## Middleware & Request Pipeline

### Request Pipeline Order

1. **Exception Handler**: Catches and formats unhandled exceptions
2. **Correlation ID Middleware**: Adds correlation tracking
3. **Swagger UI** (Development only): Interactive API documentation
4. **HSTS**: HTTP Strict Transport Security (Production)
5. **Health Checks**: `/health-check`, `/health-check/ready`, `/health-check/live`
6. **Routing**: Maps requests to controllers
7. **CORS**: Cross-origin resource sharing
8. **HTTPS Redirection**: Redirects HTTP to HTTPS
9. **Response Caching**: Caches responses for performance
10. **Output Cache**: Additional caching layer
11. **Rate Limiter**: Throttles excessive requests
12. **Controllers**: Handles API requests

### Middleware Configuration

```csharp
// Program.cs pipeline
app.UseExceptionHandler();
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseRouting();
app.UseCors();
app.UseHttpsRedirection();
app.UseResponseCaching();
app.UseOutputCache();
app.UseRateLimiter();
```

---

## Security Considerations

### Current Implementation

**Authentication**: None (public API)

**Authorization**: None (public API)

**Rate Limiting**:
- Fixed window rate limiting (100 requests per 60 seconds by default)
- Configurable permit limit, window, and queue size
- Returns 429 Too Many Requests when limit exceeded

**Input Validation**:
- URL format validation (HTTP/HTTPS only)
- URL trimming and sanitization
- Query parameter validation

**HTTPS**:
- Supported and recommended
- HSTS enabled in production
- Configurable SSL certificate validation

**CORS**:
- Configurable allowed origins
- Supports wildcard (*) for development
- Credential support for specific origins

**External API Security**:
- 2-minute timeout to prevent long-running requests
- SSL certificate validation (configurable)
- No credentials stored or transmitted
- User-Agent header identification (`OpenReferral-Validator/1.0`)
- Compression support (GZip, Deflate)

### Production Recommendations

1. **Add Authentication**:
   - API key authentication
   - JWT bearer tokens
   - OAuth 2.0 integration

2. **Rate Limiting**:
   - Per-client request throttling
   - IP-based rate limiting
   - Queue management for bulk operations

3. **CORS Configuration**:
   - Whitelist allowed origins
   - Restrict allowed methods
   - Configure credential policies

4. **Secrets Management**:
   - Use Azure Key Vault or similar
   - Rotate MongoDB credentials
   - Encrypt sensitive configuration

5. **Logging & Monitoring**:
   - Log authentication attempts
   - Monitor for suspicious patterns
   - Alert on validation failures

---

## Deployment Architecture

### Docker Container

**Multi-Stage Build**:
```dockerfile
# Stage 1: Base runtime
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base

# Stage 2: Build
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
# ... build steps ...

# Stage 3: Publish
FROM build AS publish

# Stage 4: Final
FROM base AS final
# Runtime configuration with dynamic PORT binding
```

**Port Configuration**:
- Default: 80, 443
- Heroku: Dynamic via `$PORT` environment variable

### Heroku Deployment

**Configuration**: `heroku.yml`
```yaml
build:
  docker:
    web: Dockerfile
```

**Process**:
1. Git push to Heroku remote
2. Heroku builds Docker image
3. Container starts with dynamic port binding
4. Health check validates deployment

**Scaling**:
- Horizontal scaling via dyno count
- MongoDB connection pooling handles concurrent requests

### Environment Configuration

**Development**:
- Local MongoDB instance
- `appsettings.Development.json`
- Debug logging enabled

**Production**:
- MongoDB Atlas (cloud)
- Environment variables for secrets
- Information-level logging
- Health checks enabled

---

## Development Setup

### Prerequisites
- .NET 10.0 SDK
- MongoDB 3.0+ (local or Atlas) - Optional
- Docker (optional)
- Visual Studio 2022, VS Code, or Rider

### Local Development

1. **Clone Repository**:
   ```bash
  git clone https://github.com/OpenReferralUK/oruk-validator.git
   cd OpenReferralApi
   ```

2. **Configure Settings**:
   ```bash
   # Edit appsettings.Development.json
   {
     "Database": {
       "ConnectionString": "mongodb://localhost:27017",
       "DatabaseName": "oruk_dev"
     }
   }
   ```

3. **Restore Dependencies**:
   ```bash
   dotnet restore
   ```

4. **Build Solution**:
   ```bash
   dotnet build OpenReferralApi.sln
   ```

5. **Run API**:
   ```bash
   cd OpenReferralApi
   dotnet run
   ```

6. **Access Swagger**:
   ```
   http://localhost:5000/swagger
   ```

### Docker Development

```bash
# Build image
docker build -t openreferral-api .

# Run container
docker run -p 8080:80 \
  -e ORUK_API_Database__ConnectionString="mongodb://host.docker.internal:27017" \
  -e ORUK_API_Database__DatabaseName="oruk" \
  openreferral-api
```

---

## Testing Strategy

### Unit Tests
**Location**: `OpenReferralApi.Tests/`

**Test Categories**:
1. **Service Tests**
   - `ValidatorServiceShould.cs`: Validation logic
   - `PaginationTestingServiceShould.cs`: Pagination testing

2. **Mock Data**
   - `Mocks/V1.0-UK-Default/`: HSDS-UK 1.0 responses
   - `Mocks/V3.0-UK-*`: HSDS-UK 3.0 test scenarios
   - `TestData/pagination*.json`: Pagination test data

**Test Framework**:
- xUnit
- Moq (via `RequestServiceMock.cs`)

**Running Tests**:
```bash
dotnet test OpenReferralApi.Tests/OpenReferralApi.Tests.csproj
```

### Integration Tests

**Approach**:
- Test against real MongoDB (test database)
- Mock external service APIs
- Validate end-to-end flows

### Test Coverage Areas

1. **Validation Logic**
   - Schema validation accuracy
   - Error message formatting
   - Issue collection

2. **Pagination**
   - First/last page detection
   - Content consistency
   - Metadata validation

3. **Error Handling**
   - Network failures
   - Timeout scenarios
   - Invalid JSON responses
   - Schema mismatches

---

## Performance & Caching

### Caching Strategy

**In-Memory Cache** (`IMemoryCache`):
- **Duration**: 20 seconds absolute expiration
- **Scope**: Per `RequestService` instance (Scoped lifetime)
- **Cache Key**: Full request URL
- **Purpose**: Avoid redundant API calls during validation

**Benefits**:
- Reduces load on external APIs
- Improves validation speed for repeated endpoints
- Handles multiple test cases hitting same URL

**Limitations**:
- Not distributed (single instance only)
- Clears on application restart
- Memory footprint grows with unique URLs

### Performance Optimizations

1. **Parallel Execution**:
   ```csharp
   // Test groups run in parallel
   var tasks = testGroups.Select(group => TestGroup(group));
   await Task.WhenAll(tasks);
   
   // Dashboard services validated in parallel
   var validations = services.Select(svc => ValidateOne(svc));
   await Task.WhenAll(validations);
   ```

2. **Connection Pooling**:
   - MongoDB Driver handles connection pooling
   - HttpClient factory manages HTTP connections
   - Singleton repository lifetime

3. **Timeout Management**:
   - 30-second HTTP timeout prevents hanging
   - Graceful degradation on timeout

### Scalability Considerations

**Horizontal Scaling**:
- Stateless API design
- No session state
- MongoDB handles concurrent access

**Bottlenecks**:
- External API response times
- MongoDB query performance
- JSON schema validation CPU usage

**Recommendations**:
- Implement distributed caching (Redis)
- Add request queuing for bulk operations
- Consider response streaming for large datasets

---

## Monitoring & Observability

### OpenTelemetry Integration

**Configuration**:
```json
{
  "OpenTelemetry": {
    "Enabled": true,
    "OtlpEndpoint": "http://localhost:4317"
  }
}
```

**Features**:
- **Distributed Tracing**: Traces HTTP requests and dependencies
- **Metrics**: Collects ASP.NET Core and HTTP client metrics
- **Resource Attributes**: Service name, version, environment
- **OTLP Export**: Exports to OpenTelemetry Collector
- **Console Export**: Development debugging

**Instrumentation**:
- ASP.NET Core requests (with exception recording)
- HTTP client calls
- Custom activity source: `OpenReferralApi`
- Filters out `/health-check` endpoints from tracing

### Logging

**Framework**: `ILogger<T>` (ASP.NET Core)

**Log Levels**:
- **Information**: Successful operations, timing metrics
- **Warning**: Retryable errors, deprecated features
- **Error**: Validation failures, external API errors
- **Critical**: System failures, configuration errors

**Key Log Points**:
```csharp
_logger.LogInformation("Validation completed in {ms} ms", elapsed);
_logger.LogError(ex, "Error occurred while validating {url}", url);
_logger.LogWarning("Service unavailable: {url}", serviceUrl);
```

### Health Checks

**Endpoint**: `/health-check`

**Health Check Configuration**:
```csharp
builder.Services.AddHealthChecks();
app.MapHealthChecks("/health-check");
```

**Future Enhancements**:
- MongoDB connection health check
- External API availability checks
- Disk space monitoring

### Metrics

**Current Metrics**:
- Validation execution time (logged)
- Service availability status (stored in DB)
- Test pass/fail counts

**Available Metrics** (via OpenTelemetry):
- Request rate (requests per minute)
- Error rate (failures per minute)
- Request duration
- HTTP client call duration
- Response status code distribution

**Custom Metrics** (via Instrumentation class):
```csharp
public static class Instrumentation
{
    public static readonly ActivitySource ActivitySource = new(ServiceName, ServiceVersion);
    public const string ServiceName = "OpenReferralApi";
    public const string ServiceVersion = "1.0.0";
}
```

---

## Extension Points

### Adding New HSDS-UK Versions

1. **Create Schema Directory**:
   ```bash
   mkdir OpenReferralApi/Schemas/V4.0-UK
   ```

2. **Add JSON Schemas**:
   - Place schema files in new directory
   - Follow naming convention: `{entity}-schema.json`

3. **Create Test Profile**:
   ```json
   // TestProfiles/HSDS-UK-4.0.json
   {
     "profile": "HSDS-UK-4.0",
     "testGroups": [...]
   }
   ```

4. **Update Constants**:
   ```csharp
   // Constants/HSDSUKVersions.cs
   public const string V4 = "4.0";
   ```

5. **Update Version Detection**:
   ```csharp
   // TestProfileService.cs
   return apiResult.Value["version"]!.ToString() switch
   {
       HSDSUKVersions.V4 => (HSDSUKVersions.V4, "..."),
       // ...
   };
   ```

### Adding New Validation Rules

1. **Extend Test Profile**:
   - Add new test cases to test group
   - Reference appropriate schema

2. **Create Custom Validators**:
   ```csharp
   public interface ICustomValidator
   {
       Task<Result<List<Issue>>> Validate(JsonNode response);
   }
   ```

3. **Register in DI**:
   ```csharp
   builder.Services.AddScoped<ICustomValidator, MyValidator>();
   ```

### Adding Authentication

1. **Install Package**:
   ```bash
   dotnet add package Microsoft.AspNetCore.Authentication.JwtBearer
   ```

2. **Configure in Program.cs**:
   ```csharp
   builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
       .AddJwtBearer(options => { ... });
   
   app.UseAuthentication();
   app.UseAuthorization();
   ```

3. **Protect Endpoints**:
   ```csharp
   [Authorize]
   [ApiController]
   public class ValidateController : ControllerBase
   ```

### Adding Rate Limiting

```csharp
// Install: AspNetCoreRateLimit
builder.Services.AddInMemoryRateLimiting();
builder.Services.Configure<IpRateLimitOptions>(options => { ... });
app.UseIpRateLimiting();
```

---

## Related Documentation

- [Development Setup](Development-Setup) - Local development environment setup
- [Validation Flow](Validation-Flow) - Detailed validation process documentation
- [Legacy Documentation](https://github.com/OpenReferralUK/oruk-validator/blob/main/docs/legacy-documentation-and-design-decisions.md) - Historical design decisions and archived documentation

---

## Glossary

- **HSDS-UK**: Human Services Data Specification UK - Open standard for service directory data
- **ORUK**: Open Referral UK - UK implementation of Open Referral specification
- **Test Profile**: JSON configuration defining validation test suite for a specific HSDS-UK version
- **Schema**: JSON Schema definition describing expected data structure
- **Service Directory**: API providing human services data (e.g., social services, community resources)
- **Dashboard**: Collection of registered service directories being monitored
- **Pagination Testing**: Validation of paginated API responses across multiple pages

---

**Last Updated**: January 28, 2026  
**Version**: 2.0  
**Maintained By**: iStandUK ORUK Team