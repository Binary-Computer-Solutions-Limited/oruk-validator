using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using OpenReferralApi.Telemetry;

namespace OpenReferralApi.Extensions;

public static class OpenTelemetryServiceExtensions
{
    public static void ConfigureOpenTelemetry(this WebApplicationBuilder builder)
    {
        var openTelemetryOptions = builder.Configuration
            .GetSection(OpenTelemetryOptions.SectionName)
            .Get<OpenTelemetryOptions>() ?? new OpenTelemetryOptions();

        if (!openTelemetryOptions.Enabled)
        {
            return;
        }

        var otlpEndpoint = Uri.TryCreate(openTelemetryOptions.OtlpEndpoint, UriKind.Absolute, out var parsedOtlpEndpoint)
            ? parsedOtlpEndpoint
            : null;

        builder.Services
            .AddOpenTelemetry()
            .ConfigureResource(resourceBuilder =>
                resourceBuilder.AddService(
                    serviceName: Instrumentation.ServiceName,
                    serviceVersion: Instrumentation.ServiceVersion))
            .WithTracing(tracingBuilder =>
            {
                tracingBuilder
                    .AddSource(Instrumentation.ActivitySource.Name)
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation();

                if (otlpEndpoint is not null)
                {
                    tracingBuilder.AddOtlpExporter(opts =>
                    {
                        opts.Endpoint = otlpEndpoint;
                    });
                }
                else if (builder.Environment.IsDevelopment())
                {
                    tracingBuilder.AddConsoleExporter();
                }
            })
            .WithMetrics(metricsBuilder =>
            {
                metricsBuilder
                    .AddMeter(Instrumentation.ServiceName)
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation();

                if (otlpEndpoint is not null)
                {
                    metricsBuilder.AddOtlpExporter(opts =>
                    {
                        opts.Endpoint = otlpEndpoint;
                    });
                }
                else if (builder.Environment.IsDevelopment())
                {
                    metricsBuilder.AddConsoleExporter();
                }
            });
    }
}