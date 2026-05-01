namespace OpenReferralApi.Core.Helpers;

public static class TextSanitizer
{
    public static string SanitizeExceptionMessage(string? message)
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

    public static string SanitizeForLogging(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        // Remove CR/LF to keep logs single-line and prevent log forging.
        return value.Replace("\r", string.Empty)
            .Replace("\n", string.Empty);
    }

    /// <summary>
    /// Sanitizes a string for safe logging by stripping control characters (including newlines)
    /// that could be used for log-forging attacks. This is a general-purpose method for 
    /// sanitizing arbitrary user-supplied strings.
    /// </summary>
    public static string SanitizeStringForLogging(string input)
    {
        if (string.IsNullOrEmpty(input))
            return string.Empty;

        // Remove control characters (including CR/LF) and restrict to a conservative set of printable characters
        // to prevent log forging or confusing log output.
        var sanitizedChars = input
          .Where(c =>
            // Exclude control characters
            !char.IsControl(c) &&
            // Allow basic printable ASCII range; adjust as needed if wider Unicode is desired
            c >= ' ' && c <= '~')
          .ToArray();

        var sanitized = new string(sanitizedChars);

        // Normalize internal whitespace to a single space to avoid confusing spacing in logs.
        if (sanitized.Length > 0)
        {
            sanitized = string.Join(' ',
              sanitized
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        }

        // Limit length to prevent log flooding
        const int maxLength = 500;
        if (sanitized.Length > maxLength)
        {
            sanitized = sanitized.Substring(0, maxLength) + "...(truncated)";
        }

        // Escape brace characters that might be interpreted specially by some logging frameworks
        sanitized = sanitized
          .Replace("{", "{{")
          .Replace("}", "}}");

        // Clearly mark user-supplied content so it cannot be mistaken for static log text.
        return "[user: " + sanitized + "]";
    }

    /// <summary>
    /// Sanitizes a URL for safe logging by removing query parameters and fragments
    /// and stripping any control characters (including newlines) that could be used
    /// for log-forging attacks.
    /// </summary>
    public static string SanitizeUrlForLogging(string url)
    {
        if (string.IsNullOrEmpty(url))
            return string.Empty;

        // Normalize whitespace and strip control characters (including CR/LF) to prevent log forging
        var trimmed = url.Trim();
        // Allow only a conservative set of URL-safe printable characters; replace others with '?'
        var cleanedChars = trimmed
          .Where(c => !char.IsControl(c))
          .Select(c =>
          {
              // Unreserved and common reserved URL characters
              const string allowedPunctuation = "-._~:/?#[]@!$&'()*+,;=%";
              if ((c >= 'a' && c <= 'z') ||
              (c >= 'A' && c <= 'Z') ||
              (c >= '0' && c <= '9') ||
              allowedPunctuation.IndexOf(c) >= 0)
              {
                  return c;
              }
              // Replace any unusual characters with a placeholder to keep logs safe and readable
              return '?';
          })
          .ToArray();
        var cleaned = new string(cleanedChars);

        // Optionally limit length to avoid log flooding/obfuscation with attacker-controlled data
        const int maxLength = 2048;
        if (cleaned.Length > maxLength)
        {
            cleaned = cleaned.Substring(0, maxLength) + "...(truncated)";
        }

        try
        {
            // Prefer to log without query string or fragment where possible
            if (Uri.TryCreate(cleaned, UriKind.Absolute, out var uri))
            {
                // Return URL without query string or fragment
                var sanitized = $"{uri.Scheme}://{uri.Authority}{uri.AbsolutePath}";
                // Ensure no control characters are present in the final value
                return new string(sanitized.Where(c => !char.IsControl(c)).ToArray());
            }
            // For relative or non-absolute URLs, just remove query and fragment from the cleaned value
            var questionMarkIndex = cleaned.IndexOf('?');
            var hashIndex = cleaned.IndexOf('#');
            var endIndex = cleaned.Length;

            if (questionMarkIndex > 0)
                endIndex = Math.Min(endIndex, questionMarkIndex);
            if (hashIndex > 0)
                endIndex = Math.Min(endIndex, hashIndex);

            var withoutQueryOrFragment = cleaned[..endIndex];
            return new string(withoutQueryOrFragment.Where(c => !char.IsControl(c)).ToArray());
        }
        catch
        {
            // If parsing fails, return a safely truncated, control-character-free version
            var fallback = cleaned;
            const int fallbackMaxLength = 100;
            if (fallback.Length > fallbackMaxLength)
            {
                fallback = fallback[..fallbackMaxLength] + "...";
            }
            return new string(fallback.Where(c => !char.IsControl(c)).ToArray());
        }
    }
}
