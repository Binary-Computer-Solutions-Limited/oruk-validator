using Microsoft.OpenApi;

namespace OpenReferralApi.Swagger;

public class SwaggerDocumentationOptions
{
    public const string SectionName = "Swagger";

    public string DocName { get; set; } = "v2";

    public string Version { get; set; } = "v2";

    public string Title { get; set; } = "Open Referral UK API";

    public string Description { get; set; } = "API for validating and monitoring Open Referral UK data feeds";

    [ConfigurationKeyName("OpenApiSpecVersion")]
    public string OpenApiSpecVersionName { get; set; } = "OpenApi2_0";

    public OpenApiSpecVersion ResolveSpecVersion()
    {
        return Enum.TryParse<OpenApiSpecVersion>(OpenApiSpecVersionName, ignoreCase: true, out var parsed)
            ? parsed
            : OpenApiSpecVersion.OpenApi2_0;
    }
}
