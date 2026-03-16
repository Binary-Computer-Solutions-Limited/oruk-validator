namespace OpenReferralApi.Core.Models;

public class SpecificationOptions
{
    public const string SectionName = "Specification";
    
    public string BaseUrl { get; set; } = "";

    /// <summary>
    /// Enables schema warmup on application startup.
    /// </summary>
    public bool WarmupEnabled { get; set; } = true;

    /// <summary>
    /// Delay before warmup starts so app startup is not blocked.
    /// </summary>
    public int WarmupStartupDelaySeconds { get; set; } = 5;

    /// <summary>
    /// Controls whether live feed responses are validated strictly against the feed's own schema.
    /// When true, any validation errors (including additional fields) are raised as errors.
    /// When false, validation failures are downgraded to warnings.
    /// </summary>
    public bool StrictOwnSchemaValidation { get; set; } = true;

    /// <summary>
    /// Optional configuration-based profile-name to OpenAPI URL mappings.
    /// Values here are merged with the built-in defaults for resolution and are
    /// also used as the source list for schema warmup.
    /// </summary>
    public Dictionary<string, string> Urls { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
