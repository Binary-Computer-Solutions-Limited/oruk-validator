namespace OpenReferralApi.Core.Models;

public class SpecificationOptions
{
    public const string SectionName = "Specification";
    
    public string BaseUrl { get; set; } = "";

    /// <summary>
    /// Optional configuration-based profile-version to OpenAPI URL mappings.
    /// Values here are merged with the built-in defaults.
    /// </summary>
    public Dictionary<string, string> ProfileVersionUrlMap { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
