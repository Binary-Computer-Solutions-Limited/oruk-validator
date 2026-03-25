# Open Referral UK API - Wiki Home

Welcome to the **Open Referral UK (ORUK) API** documentation wiki!

This API provides comprehensive validation services for Open Referral UK implementations, helping organizations build standards-compliant service directory APIs.

## 📚 Documentation Overview

### Getting Started

- **[Development Setup](Development-Setup)** - Complete guide to setting up your local development environment
  - Prerequisites and installation
  - MongoDB configuration options
  - Running the application
  - Troubleshooting common issues

### Technical Documentation

- **[Current State Of Play](CURRENT_STATE_OF_PLAY)** - Source-of-truth summary of implemented routes and runtime behavior
  - Current endpoint surface
  - Health and warmup behavior
  - Recently documented implemented features

- **[Technical Architecture](Technical-Architecture)** - Comprehensive system architecture documentation
  - Technology stack
  - System components
  - Data flow diagrams
  - API endpoints reference
  - Security considerations
  - Deployment architecture

- **[Validation Flow](Validation-Flow)** - Detailed explanation of the validation process
  - OpenAPI specification validation
  - Endpoint testing workflow
  - Schema validation process
  - Quality metrics analysis

### Quick Links

- 🔗 [GitHub Repository](https://github.com/OpenReferralUK/oruk-validator)
- 🐛 [Report Issues](https://github.com/OpenReferralUK/oruk-validator/issues)
- 🌐 [Open Referral UK Website](https://openreferraluk.org/)
- 💬 [Community Forum](https://forum.openreferral.org/)

## 🚀 Quick Start

```bash
# Clone the repository
git clone https://github.com/OpenReferralUK/oruk-validator.git
cd OpenReferralApi

# Run with Docker
docker-compose -f docker-compose.dev.yml up

# OR run with .NET CLI
dotnet restore
dotnet run --project OpenReferralApi/OpenReferralApi.csproj

# Access Swagger UI at http://localhost:6969
```

## 🎯 What Does This API Do?

The Open Referral UK API provides:

- ✅ **OpenAPI Specification Validation** - Validates OpenAPI 2.0/3.x specifications for compliance
- ✅ **Automated Endpoint Testing** - Tests live API endpoints against HSDS-UK standards
- ✅ **Schema Validation** - Validates responses against HSDS-UK JSON schemas (v1.0, v3.0, v3.1)
- ✅ **Quality Analysis** - Assesses API documentation quality and best practices
- ✅ **Performance Metrics** - Measures endpoint response times
- ✅ **Mock Data Service** - Provides test data for development

## 💻 Technology Stack

- **Framework**: .NET 10.0 with ASP.NET Core
- **Language**: C# 13+
- **Database**: MongoDB (optional)
- **Validation**: JSON Schema (Newtonsoft.Json.Schema, JsonSchema.Net)
- **Observability**: OpenTelemetry with OTLP export
- **Containerization**: Docker with Heroku deployment

## 📖 Key Features

### Validation & Testing
- Multi-version HSDS-UK schema support (1.0, 3.0, 3.1)
- Intelligent endpoint dependency ordering
- Real ID extraction and substitution
- Pagination testing
- Optional vs. required endpoint differentiation

### Developer Experience
- Interactive Swagger UI documentation
- Comprehensive health check endpoints
- Rate limiting protection
- CORS configuration
- Hot reload support in development
- Correlation ID support via `X-Correlation-ID`

### Current State (March 2026)
- Validation routes: `POST /openreferraluk/validate`, `POST /openreferral/validate`, and legacy alias `POST /api/openapi/validate`
- Feed validation operations: `GET /api/feedvalidation/feeds`, `POST /api/feedvalidation/validate-all`, `POST /api/feedvalidation/validate/{feedId}`
- Liveness endpoint `GET /health-check/live` includes schema warmup status data under `schemaWarmup`
- Feed validation background service runs only when MongoDB is configured; otherwise a null feed service is used
- Global environment variable prefix for overrides is `ORUK_API_`

### Production Ready
- Docker containerization
- Heroku deployment configuration
- OpenTelemetry observability
- Kubernetes-compatible health checks
- Configurable security settings

## 🤝 Community & Support

This project is part of the broader Open Referral ecosystem:

- **For API-specific issues**: Use the [GitHub Issues](https://github.com/OpenReferralUK/oruk-validator/issues) page
- **For HSDS/ORUK standard discussions**: Visit the [Community Forum](https://forum.openreferral.org/)
- **Global Open Referral**: [openreferral.org](https://openreferral.org/)

## 📄 License

- **HSDS-UK Schema & Standards**: Creative Commons Attribution-ShareAlike 4.0 (CC BY-SA 4.0)
- **API Code**: BSD 3-Clause License

See the repository [LICENSE](https://github.com/OpenReferralUK/oruk-validator/blob/main/LICENSE) and [LICENSE-BSD](https://github.com/OpenReferralUK/oruk-validator/blob/main/LICENSE-BSD) files for details.

## 🔍 Need Help?

- Review the [Development Setup](Development-Setup) guide for local environment configuration
- Check the [Technical Architecture](Technical-Architecture) for system design details
- Explore the [Validation Flow](Validation-Flow) for understanding the validation process
- Visit the [Community Forum](https://forum.openreferral.org/) for broader discussions

---

**Last Updated**: March 2026  
**Maintained By**: iStandUK & Open Referral UK Community
