using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using HealthChecks.UI.Client;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.OpenApi;
using OpenReferralApi.Core.Services;
using OpenReferralApi.HealthChecks;
using OpenReferralApi.Middleware;
using OpenReferralApi.Services;
using OpenReferralApi.Telemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddEnvironmentVariables("ORUK_API_");

// Configure Serilog
builder.Host.UseSerilog((context, configuration) =>
    configuration.ReadFrom.Configuration(context.Configuration));

// Configure strongly-typed options
builder.Services.Configure<SpecificationOptions>(
    builder.Configuration.GetSection(SpecificationOptions.SectionName));

builder.Services.Configure<CacheOptions>(
    builder.Configuration.GetSection(CacheOptions.SectionName));

builder.Services.Configure<SchemaResolutionOptions>(
    builder.Configuration.GetSection(SchemaResolutionOptions.SectionName));

builder.Services.Configure<DatabaseOptions>(
    builder.Configuration.GetSection(DatabaseOptions.SectionName));

builder.Services.Configure<FeedValidationOptions>(
    builder.Configuration.GetSection(FeedValidationOptions.SectionName));

builder.Services.Configure<SecurityOptions>(
    builder.Configuration.GetSection(SecurityOptions.SectionName));

builder.Services.Configure<RateLimitingOptions>(
    builder.Configuration.GetSection(RateLimitingOptions.SectionName));

builder.Services.Configure<OpenTelemetryOptions>(
    builder.Configuration.GetSection(OpenTelemetryOptions.SectionName));

builder.Services.Configure<OpenApiValidationServerOptions>(
    builder.Configuration.GetSection(OpenApiValidationServerOptions.SectionName));

var swaggerDocName = builder.Configuration["Swagger:DocName"] ?? "v2";
var swaggerVersion = builder.Configuration["Swagger:Version"] ?? swaggerDocName;
var swaggerTitle = builder.Configuration["Swagger:Title"] ?? "Open Referral UK API";
var swaggerDescription = builder.Configuration["Swagger:Description"]
    ?? "API for validating and monitoring Open Referral UK data feeds";
var swaggerOpenApiVersionSetting = builder.Configuration["Swagger:OpenApiSpecVersion"];

var swaggerOpenApiVersion = Enum.TryParse<OpenApiSpecVersion>(
    swaggerOpenApiVersionSetting,
    ignoreCase: true,
    out var configuredOpenApiVersion)
    ? configuredOpenApiVersion
    : OpenApiSpecVersion.OpenApi2_0;

// Add services to the container.
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    var xmlFilename = $"{Assembly.GetExecutingAssembly().GetName().Name}.xml";
    options.IncludeXmlComments(Path.Combine(AppContext.BaseDirectory, xmlFilename));
    options.UseInlineDefinitionsForEnums();
    options.DescribeAllParametersInCamelCase();

    options.CustomOperationIds(apiDescription =>
    {
        var controller = apiDescription.ActionDescriptor.RouteValues["controller"];
        var action = apiDescription.ActionDescriptor.RouteValues["action"];
        var method = apiDescription.HttpMethod?.ToUpperInvariant();
        var relativePath = apiDescription.RelativePath?.Replace("/", "_")?.Replace("{", string.Empty).Replace("}", string.Empty);
        return $"{controller}_{action}_{method}_{relativePath}";
    });

    options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        Description = "Optional bearer token support for deployments that secure this API."
    });

    options.AddSecurityDefinition("ApiKey", new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.ApiKey,
        Name = "X-API-Key",
        In = ParameterLocation.Header,
        Description = "Optional API key support for deployments that secure this API."
    });

    options.SwaggerDoc(swaggerDocName, new()
    {
        Title = swaggerTitle,
        Version = swaggerVersion,
        Description = swaggerDescription,
        Contact = new()
        {
            Name = "Open Referral UK",
            Url = new Uri("https://openreferraluk.org")
        }
    });
});

// CORS - Environment-specific origins
var securityOptions = builder.Configuration.GetSection(SecurityOptions.SectionName).Get<SecurityOptions>() ?? new SecurityOptions();

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        if (securityOptions.AllowedCorsOrigins.Contains("*"))
        {
            policy.AllowAnyOrigin()
                  .AllowAnyMethod()
                  .AllowAnyHeader();
        }
        else
        {
            policy.WithOrigins(securityOptions.AllowedCorsOrigins)
                  .AllowAnyMethod()
                  .AllowAnyHeader()
                  .AllowCredentials();
        }
    });
});

// Rate Limiting
var rateLimitingOptions = builder.Configuration.GetSection(RateLimitingOptions.SectionName).Get<RateLimitingOptions>() ?? new RateLimitingOptions();

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    options.AddFixedWindowLimiter("fixed", opt =>
    {
        opt.PermitLimit = rateLimitingOptions.PermitLimit;
        opt.Window = TimeSpan.FromSeconds(rateLimitingOptions.Window);
        opt.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
        opt.QueueLimit = rateLimitingOptions.QueueLimit;
    });
});

// Configure HTTP client with environment-based security settings
builder.Services.AddHttpClient(nameof(OpenApiValidationService), client =>
{
    client.DefaultRequestHeaders.Add("User-Agent", "OpenReferral-Validator/1.0");
    client.Timeout = TimeSpan.FromMinutes(2);
})
.ConfigurePrimaryHttpMessageHandler(sp =>
{
    var securityOpts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<SecurityOptions>>().Value;
    var handler = new HttpClientHandler();

    if (!securityOpts.ValidateSslCertificates)
    {
        handler.ServerCertificateCustomValidationCallback = (sender, cert, chain, sslPolicyErrors) => true;
    }

    handler.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;

    return handler;
});

builder.Services.AddHttpClient();
builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
    });

// Response Caching
builder.Services.AddResponseCaching();
builder.Services.AddOutputCache(options =>
{
    options.AddBasePolicy(builder => builder.Cache());
    options.AddPolicy("MockEndpoints", builder =>
        builder.Expire(TimeSpan.FromMinutes(5)));
});

// Health Checks
var healthChecksBuilder = builder.Services.AddHealthChecks()
    .AddCheck("self", () => HealthCheckResult.Healthy(), tags: new[] { "ready" });

var databaseOptions = builder.Configuration.GetSection(DatabaseOptions.SectionName).Get<DatabaseOptions>() ?? new DatabaseOptions();
if (!string.IsNullOrEmpty(databaseOptions.ConnectionString))
{
    // Register MongoDB client for health checks and feed validation
    builder.Services.AddSingleton<MongoDB.Driver.IMongoClient>(sp =>
    {
        return new MongoDB.Driver.MongoClient(databaseOptions.ConnectionString);
    });

    healthChecksBuilder.AddMongoDb(
        name: "mongodb",
        tags: new[] { "ready", "db" });

    // Feed validation services - only register if MongoDB is configured
    builder.Services.AddScoped<IFeedValidationService, FeedValidationService>();
    builder.Services.AddHostedService<FeedValidationBackgroundService>();
}
else
{
    // Register null implementation when MongoDB is not configured
    builder.Services.AddScoped<IFeedValidationService, NullFeedValidationService>();
}

healthChecksBuilder.AddCheck<FeedValidationHealthCheck>(
    "feed-validation",
    tags: new[] { "ready", "service" });

// Services
builder.Services.AddScoped<IPathParsingService, PathParsingService>();
builder.Services.AddSingleton<IRequestProcessingService, RequestProcessingService>();
builder.Services.AddSingleton<ISchemaWarmupStatusTracker, SchemaWarmupStatusTracker>();
builder.Services.AddSingleton<ISchemaWarmupStatusProvider>(sp => sp.GetRequiredService<ISchemaWarmupStatusTracker>());

// Schema Resolver Service - resolves $ref in remote schema files and creates JSchema objects
builder.Services.AddScoped<ISchemaResolverService, SchemaResolverService>();
builder.Services.AddHostedService<SchemaWarmupBackgroundService>();

builder.Services.AddScoped<IJsonValidatorService, JsonValidatorService>();
builder.Services.AddScoped<IAuthenticationValidationService, AuthenticationValidationService>();
builder.Services.AddScoped<IOpenApiSpecificationService, OpenApiSpecificationService>();
builder.Services.AddScoped<IHsdsComplianceService, HsdsComplianceService>();
builder.Services.AddScoped<IEndpointTestingService, EndpointTestingService>();
builder.Services.AddScoped<IOpenApiValidationService, OpenApiValidationService>();

builder.Services.AddScoped<IOpenApiDiscoveryService, OpenApiDiscoveryService>();
builder.Services.AddScoped<IProfileDiscoveryService, ProfileDiscoveryService>();
builder.Services.AddScoped<IOpenApiBootstrapService, OpenApiBootstrapService>();
builder.Services.AddScoped<IOpenReferralUKValidationResponseMapper, OpenReferralUKValidationResponseMapper>();

// Configure Memory Cache with size limit from cache options
builder.Services.AddMemoryCache(options =>
{
    var cacheOpts = builder.Configuration.GetSection(CacheOptions.SectionName).Get<CacheOptions>() ?? new CacheOptions();
    options.SizeLimit = cacheOpts.MaxSizeMB * 1024 * 1024; // Convert MB to bytes
});

// Exception Handlers
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddProblemDetails();

// OpenTelemetry Configuration
var otelOptions = builder.Configuration.GetSection(OpenTelemetryOptions.SectionName).Get<OpenTelemetryOptions>() ?? new OpenTelemetryOptions();
if (otelOptions.Enabled)
{
    var resourceBuilder = ResourceBuilder.CreateDefault()
        .AddService(
            serviceName: Instrumentation.ServiceName,
            serviceVersion: Instrumentation.ServiceVersion)
        .AddAttributes(new Dictionary<string, object>
        {
            ["deployment.environment"] = builder.Environment.EnvironmentName
        });

    builder.Services.AddOpenTelemetry()
        .WithMetrics(metrics =>
        {
            metrics
                .SetResourceBuilder(resourceBuilder)
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddMeter(Instrumentation.ServiceName);

            if (!string.IsNullOrEmpty(otelOptions.OtlpEndpoint))
            {
                metrics.AddOtlpExporter(options =>
                {
                    options.Endpoint = new Uri(otelOptions.OtlpEndpoint);
                });
            }

            if (builder.Environment.IsDevelopment())
            {
                metrics.AddConsoleExporter();
            }
        })
        .WithTracing(tracing =>
        {
            tracing
                .SetResourceBuilder(resourceBuilder)
                .AddAspNetCoreInstrumentation(options =>
                {
                    options.RecordException = true;
                    options.Filter = httpContext =>
                    {
                        return !httpContext.Request.Path.StartsWithSegments("/health-check");
                    };
                })
                .AddHttpClientInstrumentation()
                .AddSource(Instrumentation.ActivitySource.Name);

            if (!string.IsNullOrEmpty(otelOptions.OtlpEndpoint))
            {
                tracing.AddOtlpExporter(options =>
                {
                    options.Endpoint = new Uri(otelOptions.OtlpEndpoint);
                });
            }

            if (builder.Environment.IsDevelopment())
            {
                tracing.AddConsoleExporter();
            }
        });
}

var app = builder.Build();

var openApiValidationSettings = app.Configuration
    .GetSection(OpenApiValidationServerOptions.SectionName)
    .Get<OpenApiValidationServerOptions>() ?? new OpenApiValidationServerOptions();

app.Logger.LogInformation(
    "OpenApiValidation settings at startup: {@OpenApiValidationSettings}",
    openApiValidationSettings);

const string validationRequestExample = """
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

const string openReferralValidationResponseExample = """
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

const string openReferralUkValidationResponseExample = """
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

const string validationProblemResponseExample = """
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

const string problemDetailsRateLimitExample = """
{
    "type": "https://tools.ietf.org/html/rfc6585#section-4",
    "title": "Too Many Requests",
    "status": 429
}
""";

const string problemDetailsServerErrorExample = """
{
    "type": "https://tools.ietf.org/html/rfc9110#section-15.6.1",
    "title": "An error occurred while processing your request.",
    "status": 500
}
""";

void ApplyValidationOperationExamples(OpenApiDocument document)
{
    OpenApiOperation? GetPostOperation(IOpenApiPathItem? pathItem)
    {
        if (pathItem?.Operations == null)
        {
            return null;
        }

        return pathItem.Operations.TryGetValue(HttpMethod.Post, out var operation) ? operation : null;
    }

        if (document.Paths.TryGetValue("/openreferral/validate", out var openReferralPath))
        {
                ApplyExamplesToOperation(
            GetPostOperation(openReferralPath),
                        validationRequestExample,
                        new Dictionary<string, string>
                        {
                                ["200"] = openReferralValidationResponseExample,
                                ["400"] = validationProblemResponseExample,
                                ["429"] = problemDetailsRateLimitExample,
                                ["500"] = problemDetailsServerErrorExample
                        });
        }

        if (document.Paths.TryGetValue("/openreferraluk/validate", out var openReferralUkPath))
        {
                ApplyExamplesToOperation(
                GetPostOperation(openReferralUkPath),
                        validationRequestExample,
                        new Dictionary<string, string>
                        {
                                ["200"] = openReferralUkValidationResponseExample,
                                ["400"] = validationProblemResponseExample,
                                ["429"] = problemDetailsRateLimitExample,
                                ["500"] = problemDetailsServerErrorExample
                        });
        }

        if (document.Paths.TryGetValue("/api/openapi/validate", out var legacyPath))
        {
                ApplyExamplesToOperation(
                GetPostOperation(legacyPath),
                        validationRequestExample,
                        new Dictionary<string, string>
                        {
                                ["200"] = openReferralUkValidationResponseExample,
                                ["400"] = validationProblemResponseExample,
                                ["429"] = problemDetailsRateLimitExample,
                                ["500"] = problemDetailsServerErrorExample
                        });
        }
}

void ApplyExamplesToOperation(
        OpenApiOperation? operation,
        string requestExample,
        IReadOnlyDictionary<string, string> responseExamples)
{
        if (operation == null)
        {
                return;
        }

    if (operation.Responses == null)
    {
        return;
    }

        var requestExampleNode = JsonNode.Parse(requestExample);
        if (requestExampleNode != null)
        {
                if (operation.RequestBody?.Content != null)
                {
                        foreach (var mediaType in operation.RequestBody.Content.Values)
                        {
                                mediaType.Example = requestExampleNode;
                        }
                }
        }

        foreach (var (statusCode, responseExample) in responseExamples)
        {
            if (!operation.Responses.TryGetValue(statusCode, out var response))
            {
                continue;
            }

            if (response?.Content == null)
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

// Configure the HTTP request pipeline
app.UseExceptionHandler();

// Middleware
app.UseMiddleware<CorrelationIdMiddleware>();

// Enable Swagger in all environments
app.UseSwagger(options =>
{
    options.OpenApiVersion = swaggerOpenApiVersion;
    options.PreSerializeFilters.Add((document, _) => ApplyValidationOperationExamples(document));
});
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint($"/swagger/{swaggerDocName}/swagger.json", $"{swaggerTitle} {swaggerVersion}");
    c.RoutePrefix = string.Empty;
    c.DisplayRequestDuration();
});

if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

// Health check endpoints
app.MapHealthChecks("/health-check", new HealthCheckOptions
{
    Predicate = _ => true,
    ResponseWriter = UIResponseWriter.WriteHealthCheckUIResponse
});

app.MapHealthChecks("/health-check/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready"),
    ResponseWriter = UIResponseWriter.WriteHealthCheckUIResponse
});

// Overall service health for CI/deploy checks
app.MapHealthChecks("/health-check/overall", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready"),
    ResponseWriter = UIResponseWriter.WriteHealthCheckUIResponse
});

app.MapHealthChecks("/health-check/live", new HealthCheckOptions
{
    Predicate = _ => false,
    ResponseWriter = async (context, _) =>
    {
        var warmupStatus = context.RequestServices.GetRequiredService<ISchemaWarmupStatusProvider>().GetSnapshot();

        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(JsonSerializer.Serialize(new
        {
            status = "Healthy",
            timestamp = DateTime.UtcNow,
            schemaWarmup = warmupStatus
        }));
    }
});

app.UseRouting();
app.UseSerilogRequestLogging();
app.UseCors();
var configuredUrls = app.Configuration["ASPNETCORE_URLS"] ?? app.Configuration["urls"] ?? string.Empty;
var hasHttpsInUrls = configuredUrls
    .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
    .Any(url => url.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
var hasExplicitHttpsPort = !string.IsNullOrWhiteSpace(app.Configuration["ASPNETCORE_HTTPS_PORT"]) ||
                           !string.IsNullOrWhiteSpace(app.Configuration["HTTPS_PORT"]);
var hasKestrelHttpsEndpoint = !string.IsNullOrWhiteSpace(app.Configuration["Kestrel:Endpoints:Https:Url"]);

if (hasHttpsInUrls || hasExplicitHttpsPort || hasKestrelHttpsEndpoint)
{
    app.UseHttpsRedirection();
}
app.UseResponseCaching();
app.UseOutputCache();
app.UseRateLimiter();

app.MapControllerRoute(name: "default", pattern: "{controller}/{action=Index}/{id?}");

app.Run();

// Ensure logs are flushed on shutdown
Log.CloseAndFlush();

public partial class Program
{
}
