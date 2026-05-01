namespace OpenReferralApi.Core.Services;

internal static class OpenApiDiscoveryStandardPaths
{
    internal static readonly string[] Paths =
    {
        "",
        "openapi.json",
        "openapi",
        "swagger.json",
        "swagger.yaml",
        "swagger.yml",
        ".well-known/openapi.json",
        "api-docs/openapi.json",
        "api-docs/openapi.yaml",
        "api-docs/openapi.yml",
        "api-docs",
        "v3/api-docs",
        "v2/api-docs",
        "swagger/v1/swagger.json",
        "swagger/v1/swagger.yaml",
        "swagger/v1/swagger.yml"
    };
}