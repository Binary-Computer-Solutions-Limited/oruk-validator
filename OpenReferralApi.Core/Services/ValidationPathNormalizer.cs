using System.Text.RegularExpressions;

namespace OpenReferralApi.Core.Services;

internal static class ValidationPathNormalizer
{
    private static readonly Regex NumericArrayIndexRegex = new(@"\[\d+\]", RegexOptions.Compiled);

    public static string NormalizeArrayIndexes(string? input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return string.Empty;
        }

        if (input.IndexOf('[') < 0)
        {
            return input;
        }

        return NumericArrayIndexRegex.Replace(input, "[]");
    }
}
