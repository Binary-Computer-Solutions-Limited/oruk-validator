using System.Text.RegularExpressions;

namespace OpenReferralApi.Core.Services;

internal static partial class ProfileVersionNormalizer
{
    /// <summary>
    /// Extracts the trailing major.minor version number from a raw profile version string.
    /// For example "HSDS-UK-3.0" → "3.0", "V3" → "3.0", "3.2" → "3.2", "SOMESCHEMA-1.5" → "1.5".
    /// Returns null if no version number can be extracted.
    /// </summary>
    [GeneratedRegex("(?<major>\\d+)(?:\\.(?<minor>\\d+))?$")]
    private static partial Regex TrailingVersionRegex();
    // Note: GeneratedRegex attribute provides the implementation for the above partial method.

    internal static string? ExtractMajorMinor(string? rawVersion)
    {
        if (string.IsNullOrWhiteSpace(rawVersion))
        {
            return null;
        }

        var match = TrailingVersionRegex().Match(rawVersion.Trim());
        if (!match.Success)
        {
            return null;
        }

        var major = match.Groups["major"].Value;
        var minor = match.Groups["minor"].Success ? match.Groups["minor"].Value : "0";
        return $"{major}.{minor}";
    }

    /// <summary>
    /// Kept for backward compatibility with call sites that have not yet been migrated.
    /// Prefer ExtractMajorMinor for new code.
    /// </summary>
    internal static string? NormalizeVersionNumber(string? rawVersion) => ExtractMajorMinor(rawVersion);
}
