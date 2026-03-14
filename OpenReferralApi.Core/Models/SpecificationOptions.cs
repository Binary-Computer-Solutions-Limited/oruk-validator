namespace OpenReferralApi.Core.Models;

public class SpecificationOptions
{
    public const string SectionName = "Specification";
    
    public string BaseUrl { get; set; } = "";

    /// <summary>
    /// Environment variable name containing profile-version to OpenAPI URL mappings.
    /// Supported formats:
    /// 1) JSON object, e.g. {"3.0":"https://.../3.0/openapi.json"}
    /// 2) Delimited pairs, e.g. 3.0=https://.../3.0/openapi.json;3.1=https://.../3.1/openapi.json
    /// </summary>
    public string ProfileVersionUrlMapEnvironmentVariable { get; set; } = "ORUK_API_PROFILE_VERSION_URL_MAP";

    /// <summary>
    /// Optional configuration-based profile-version to OpenAPI URL mappings.
    /// Values here are merged with defaults and can be overridden by the environment variable mapping.
    /// </summary>
    public Dictionary<string, string> ProfileVersionUrlMap { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
