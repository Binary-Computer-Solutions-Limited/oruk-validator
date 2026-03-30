namespace OpenReferralApi.Core.Services;

internal static class TextSanitizer
{
    internal static string SanitizeExceptionMessage(string? message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return string.Empty;
        }

        // Remove control characters (including CR/LF) to prevent log forging.
        var sanitized = new string(message.Where(c => !char.IsControl(c)).ToArray());

        const int maxLength = 500;
        if (sanitized.Length > maxLength)
        {
            sanitized = sanitized.Substring(0, maxLength) + "...(truncated)";
        }

        return sanitized;
    }

    internal static string SanitizeForLogging(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        // Remove CR/LF to keep logs single-line and prevent log forging.
        return value.Replace("\r", string.Empty)
            .Replace("\n", string.Empty);
    }
}
