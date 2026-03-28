using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;

namespace OpenReferralApi.Swagger;

public static class SwaggerDocumentationExtensions
{
    public static IServiceCollection AddSwaggerDocumentation(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<SwaggerDocumentationOptions>(
            configuration.GetSection(SwaggerDocumentationOptions.SectionName));

        var swaggerOptions = configuration
            .GetSection(SwaggerDocumentationOptions.SectionName)
            .Get<SwaggerDocumentationOptions>() ?? new SwaggerDocumentationOptions();

        services.AddSingleton(new SwaggerRuntimeOptions(
            swaggerOptions.DocName,
            swaggerOptions.Version,
            swaggerOptions.Title,
            swaggerOptions.Description,
            swaggerOptions.ResolveSpecVersion()));

        services.AddEndpointsApiExplorer();
        services.AddSwaggerGen(options =>
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
                var relativePath = apiDescription.RelativePath
                    ?.Replace("/", "_")
                    ?.Replace("{", string.Empty)
                    .Replace("}", string.Empty);

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

            options.SwaggerDoc(swaggerOptions.DocName, new()
            {
                Title = swaggerOptions.Title,
                Version = swaggerOptions.Version,
                Description = swaggerOptions.Description,
                Contact = new()
                {
                    Name = "Open Referral UK",
                    Url = new Uri("https://openreferraluk.org")
                }
            });
        });

        return services;
    }

    public static WebApplication UseSwaggerDocumentation(this WebApplication app)
    {
        var runtimeOptions = app.Services.GetRequiredService<SwaggerRuntimeOptions>();

        app.UseSwagger(options =>
        {
            options.OpenApiVersion = runtimeOptions.OpenApiVersion;
            options.PreSerializeFilters.Add((document, _) => SwaggerExamplesApplier.Apply(document));
        });

        app.UseSwaggerUI(options =>
        {
            options.SwaggerEndpoint(
                $"/swagger/{runtimeOptions.DocName}/swagger.json",
                $"{runtimeOptions.Title} {runtimeOptions.Version}");
            options.RoutePrefix = string.Empty;
            options.DisplayRequestDuration();
        });

        return app;
    }

    private sealed record SwaggerRuntimeOptions(
        string DocName,
        string Version,
        string Title,
        string Description,
        OpenApiSpecVersion OpenApiVersion);
}
