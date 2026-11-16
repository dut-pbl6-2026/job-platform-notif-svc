using System.Text.RegularExpressions;

namespace Notif.Infrastructure.Configuration;

/// <summary>
/// Normalises configured secrets. Env templates ship <c>__SET_VIA_INFISICAL__</c>
/// (pattern <c>__[A-Z][A-Z0-9_]*__</c>); a copied template must never be accepted as a real
/// secret, so the placeholder is treated exactly like an unset value.
/// </summary>
public static class SecretValue
{
    private static readonly Regex PlaceholderPattern = new(
        "^__[A-Z][A-Z0-9_]*__$",
        RegexOptions.CultureInvariant);

    /// <summary>Trimmed value, or <c>null</c> when unset, blank or a template placeholder.</summary>
    public static string? Usable(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        if (trimmed.StartsWith("<set via", StringComparison.Ordinal)
            || trimmed.Contains("${", StringComparison.Ordinal))
        {
            return null;
        }

        return IsPlaceholder(trimmed) ? null : trimmed;
    }

    /// <summary>True when the value is a <c>__UPPER_SNAKE__</c> template placeholder.</summary>
    public static bool IsPlaceholder(string? value) =>
        !string.IsNullOrWhiteSpace(value) && PlaceholderPattern.IsMatch(value.Trim());
}
