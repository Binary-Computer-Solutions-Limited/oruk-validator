using System.Text.RegularExpressions;

namespace OpenReferralApi.Core.Services;

internal static partial class ProfileVersionNormalizer
{
    [GeneratedRegex("^(?<major>\\d+)(?:\\.(?<minor>\\d+))?$")]
    private static partial Regex VersionRegex();

    internal static string? NormalizeVersionNumber(string? rawVersion)
    {
        if (string.IsNullOrWhiteSpace(rawVersion))
        {
            return null;
        }

        var normalizedInput = rawVersion.Trim();
        if (normalizedInput.StartsWith("HSDS-UK-", StringComparison.OrdinalIgnoreCase))
        {
            normalizedInput = normalizedInput.Substring("HSDS-UK-".Length);
        }

        if (normalizedInput.StartsWith("V", StringComparison.OrdinalIgnoreCase))
        {
            normalizedInput = normalizedInput.Substring(1);
        }

        var match = VersionRegex().Match(normalizedInput);
        if (!match.Success)
        {
            return null;
        }

        var major = match.Groups["major"].Value;
        var minor = match.Groups["minor"].Success ? match.Groups["minor"].Value : "0";
        return $"{major}.{minor}";
    }

    internal static string? NormalizeHsdsProfileVersion(string? rawVersion)
    {
        var versionNumber = NormalizeVersionNumber(rawVersion);
        return string.IsNullOrWhiteSpace(versionNumber)
            ? null
            : $"HSDS-UK-{versionNumber}";
    }
}