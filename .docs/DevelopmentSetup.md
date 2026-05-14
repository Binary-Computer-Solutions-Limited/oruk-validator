# Development Setup

This guide covers everything you need to get the Open Referral UK API running locally for development.

## Table of Contents

- [Prerequisites](#prerequisites)
- [Dependencies](#dependencies)
- [Getting Started](#getting-started)
- [Configuration](#configuration)
- [Running the Application](#running-the-application)
- [Troubleshooting](#troubleshooting)

## Prerequisites

Before you begin, ensure you have the following installed on your development machine:

### Required

- **.NET 10.0 SDK** - [Download here](https://dotnet.microsoft.com/download/dotnet/10.0)
- **MongoDB** - [Installation guide](https://www.mongodb.com/docs/manual/installation/)
  - Either a local MongoDB instance or access to a MongoDB Atlas cluster
- **Git** - For cloning the repository

### Optional

- **Docker** - If you prefer running MongoDB in a container
- **Visual Studio 2022** or **JetBrains Rider** - For a full IDE experience
- **VS Code** - Lightweight alternative with C# extension

### Verify Installation

```bash
# Check .NET version
dotnet --version
# Should output 10.0.x or higher

# Check MongoDB is running (if installed locally)
mongosh --version
# Should output MongoDB shell version
```

## Dependencies

The project uses the following key dependencies (automatically restored when building):

### Backend (.NET)

- **ASP.NET Core 10.0** - Web framework
- **MongoDB.Driver 3.5.2** - MongoDB database driver
- **JsonSchema.Net 9.2.0** - JSON Schema validation and meta-schema support
- **Octokit 14.0.0** - GitHub API integration
- **Swashbuckle 10.1.0** - OpenAPI/Swagger documentation
- **AspNetCore.HealthChecks.MongoDb 9.0.0** - Health check endpoints
- **FluentResults 4.0.0** - Result pattern library

All dependencies are defined in the `.csproj` files and will be automatically restored.

## Getting Started

### 1. Clone the Repository

```bash
git clone https://github.com/OpenReferralUK/oruk-validator.git
cd OpenReferralApi
```

### 2. Set Up MongoDB

#### Option A: Local MongoDB

Install MongoDB locally and start the service:

```bash
# macOS (using Homebrew)
brew tap mongodb/brew
brew install mongodb-community
brew services start mongodb-community

# Linux (Ubuntu/Debian)
sudo systemctl start mongod
sudo systemctl enable mongod

# Verify MongoDB is running
mongosh
```

#### Option B: MongoDB with Docker

```bash
# Run MongoDB in a Docker container
docker run -d \
  --name mongodb \
  -p 27017:27017 \
  -e MONGO_INITDB_ROOT_USERNAME=admin \
  -e MONGO_INITDB_ROOT_PASSWORD=password123 \
  mongo:latest

# If using authentication, your connection string will be:
# mongodb://admin:password123@localhost:27017
```

#### Option C: MongoDB Atlas (Cloud)

1. Create a free account at [MongoDB Atlas](https://www.mongodb.com/cloud/atlas)
2. Create a new cluster
3. Whitelist your IP address
4. Create a database user
5. Get your connection string (looks like: `mongodb+srv://username:password@cluster.mongodb.net/`)

### 3. Restore Dependencies

```bash
# Navigate to the solution directory
cd /path/to/OpenReferralApi

# Restore all NuGet packages
dotnet restore
```

### 4. Build the Solution

```bash
# Build the entire solution
dotnet build

# Or build in Release mode
dotnet build -c Release
```

## Configuration

The application uses `appsettings.json` for configuration, but **sensitive settings should be provided via environment variables or user secrets**.

### Required Configuration

#### Database Settings

Configure MongoDB connection in one of the following ways:

#### Option 1: Environment Variables (Recommended)

The application reads environment variables prefixed with `ORUK_API_`. Set these before running:

```bash
# MongoDB connection
export ORUK_API_Database__ConnectionString="mongodb://localhost:27017"
export ORUK_API_Database__DatabaseName="oruk"
export ORUK_API_Database__ServicesCollection="services"
export ORUK_API_Database__ColumnsCollection="columns"
export ORUK_API_Database__ViewsCollection="views"
```

#### Option 2: User Secrets (Development)

For local development, use .NET user secrets to avoid committing sensitive data:

```bash
cd OpenReferralApi

# Initialize user secrets
dotnet user-secrets init

# Set MongoDB connection string
dotnet user-secrets set "Database:ConnectionString" "mongodb://localhost:27017"
dotnet user-secrets set "Database:DatabaseName" "oruk"
dotnet user-secrets set "Database:ServicesCollection" "services"
dotnet user-secrets set "Database:ColumnsCollection" "columns"
dotnet user-secrets set "Database:ViewsCollection" "views"
```

**Note**: MongoDB configuration is optional. The API can run without a database connection, though some features may be limited.

#### Option 3: appsettings.Development.json (Local Only)

Create an `appsettings.Development.json` file (this file is gitignored):

```json
{
  "Database": {
    "ConnectionString": "mongodb://localhost:27017",
    "DatabaseName": "oruk",
    "ServicesCollection": "services",
    "ColumnsCollection": "columns",
    "ViewsCollection": "views"
  }
}
```

#### Optional: Feed Validation Settings

Configure periodic feed validation via the background service:

```bash
# Enable/disable periodic feed validation (default: false)
dotnet user-secrets set "FeedValidation:Enabled" "true"

# Set validation interval in hours (default: 24)
dotnet user-secrets set "FeedValidation:IntervalHours" "24"

# Run validation at midnight UTC (default: true, otherwise uses fixed interval)
dotnet user-secrets set "FeedValidation:RunAtMidnight" "true"
```

### Configuration Summary

| Setting | Required | Default | Description |
| --- | --- | --- | --- |
| `Database:ConnectionString` | **Yes** | `""` | MongoDB connection string |
| `Database:DatabaseName` | **Yes** | `""` | MongoDB database name |
| `Database:ServicesCollection` | No | `"services"` | Collection for service data |
| `Database:ColumnsCollection` | No | `"columns"` | Collection for column metadata |
| `Database:ViewsCollection` | No | `"views"` | Collection for view data |
| `FeedValidation:Enabled` | No | `false` | Enable periodic feed validation background service |
| `FeedValidation:IntervalHours` | No | `24` | Hours between validation runs |
| `FeedValidation:RunAtMidnight` | No | `true` | Run at midnight UTC instead of fixed interval |

## Running the Application

### Using .NET CLI (Recommended)

The simplest way to run the application:

```bash
cd OpenReferralApi

# Run the application
dotnet run

# The API will start at http://localhost:6969
# Swagger UI will be available at http://localhost:6969
```

### Using Visual Studio / Rider

1. Open `OpenReferralApi.sln`
2. Set `OpenReferralApi` as the startup project
3. Press F5 to run with debugging or Ctrl+F5 to run without debugging

### Using Docker

Build and run using Docker:

```bash
# Build the Docker image
docker build -t openreferral-api .

# Run the container
docker run -d \
  -p 8080:80 \
  -e ORUK_API_Database__ConnectionString="mongodb://host.docker.internal:27017" \
  -e ORUK_API_Database__DatabaseName="oruk" \
  --name openreferral-api \
  openreferral-api

# View logs
docker logs -f openreferral-api
```

**Note**: Use `host.docker.internal` instead of `localhost` in the connection string when connecting to MongoDB running on your host machine from a Docker container.

### Accessing the Application

Once running, you can access:

- **Swagger UI (Interactive API Documentation)**: [http://localhost:6969](http://localhost:6969) (or [http://localhost:5000](http://localhost:5000) depending on configuration)
- **Health Check (Overall)**: [http://localhost:6969/health-check](http://localhost:6969/health-check)
- **Health Check (Ready)**: [http://localhost:6969/health-check/ready](http://localhost:6969/health-check/ready)
- **Health Check (Live)**: [http://localhost:6969/health-check/live](http://localhost:6969/health-check/live)

### Running Tests

``` bash
# Run all tests
dotnet test

# Run tests with verbose output
dotnet test -v normal

# Run tests in a specific project
dotnet test OpenReferralApi.Tests/OpenReferralApi.Tests.csproj

# Run tests with code coverage (requires coverage tool)
dotnet test --collect:"XPlat Code Coverage"
```

## Troubleshooting

### MongoDB Connection Issues

**Problem**: `Unable to connect to MongoDB`

**Solutions**:

- Verify MongoDB is running: `mongosh` or check Docker container status
- Check connection string format is correct
- Ensure MongoDB port (27017) is not blocked by firewall
- If using MongoDB Atlas, verify IP whitelist and credentials

### Port Already in Use

**Problem**: `Address already in use: localhost:6969`

**Solutions**:

- Check if another instance is running: `lsof -i :6969` (macOS/Linux)
- Kill the process using the port or change the port in `launchSettings.json`
- Modify `ASPNETCORE_URLS` environment variable to use a different port:

  ```bash
  export ASPNETCORE_URLS="http://localhost:7000"
  dotnet run
  ```

### Missing Dependencies

**Problem**: Build errors related to NuGet packages

**Solutions**:

```bash
# Clear NuGet cache
dotnet nuget locals all --clear

# Restore packages
dotnet restore --force

# Rebuild
dotnet build
```

### Health Check Fails

**Problem**: `/health-check` endpoint returns unhealthy status

**Solutions**:

- Check MongoDB connection is configured correctly
- Verify MongoDB is accessible from the application
- Review application logs for specific error messages
- Test MongoDB connection independently using `mongosh`

### User Secrets Not Loading

**Problem**: Configuration values from user secrets not being read

**Solutions**:

```bash
# Verify user secrets are set
dotnet user-secrets list

# Ensure you're in the correct project directory (OpenReferralApi/)
cd OpenReferralApi
dotnet user-secrets list

# Re-initialize if needed
dotnet user-secrets init
```

### Certificate/SSL Issues

**Problem**: SSL certificate validation errors when calling external APIs

**Note**: The application is configured to accept all certificates in development for testing purposes. This is handled by the `HttpClientHandler` configuration in `Program.cs`. For production, proper certificate validation should be implemented.

## Next Steps

Now that you have the development environment set up:

1. **Explore the API**: Visit [http://localhost:6969](http://localhost:6969) to see the Swagger UI
2. **Read the Documentation**: Check out the other wiki pages
   - [Technical Architecture](Technical-Architecture) - System design and components
   - [Validation Flow](Validation-Flow) - How validation works
   - [Legacy Documentation](https://github.com/OpenReferralUK/oruk-validator/blob/main/docs/legacy-documentation-and-design-decisions.md) - Historical context
3. **Run Tests**: Familiarize yourself with the test suite

   ```bash
   dotnet test
   ```

4. **Make Changes**: Start contributing!

## Development Tips

### Hot Reload

.NET 10.0 supports hot reload. When running with `dotnet watch`, changes to C# files will automatically reload:

```bash
dotnet watch run
```

### Database Debugging

Use MongoDB Compass (GUI) to inspect your local database:

- Download: [https://www.mongodb.com/products/compass](https://www.mongodb.com/products/compass)
- Connect to: `mongodb://localhost:27017`

### API Testing

Besides Swagger UI, you can use:

- **curl**: Command-line HTTP requests
- **Postman**: GUI-based API testing
- **HTTPie**: User-friendly curl alternative
- **OpenReferralApi.http**: VS Code REST Client file in the project

Example using curl:

```bash
# Test health endpoint
curl http://localhost:6969/health-check

# Validate an API endpoint
curl -X POST http://localhost:6969/openreferraluk/validate \
  -H "Content-Type: application/json" \
  -d '{"openApiSchema":{"url":"https://example.com/openapi.json"},"baseUrl":"https://example.com"}'
```

---

**Questions or Issues?**

If you encounter problems not covered in this guide:

- Check existing [GitHub Issues](https://github.com/OpenReferralUK/oruk-validator/issues)
- Create a new issue with detailed information about your environment and the problem
- Visit the [Open Referral community forum](https://forum.openreferral.org/)
