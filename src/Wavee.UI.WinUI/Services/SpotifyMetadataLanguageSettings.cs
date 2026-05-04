using System;
using System.Globalization;

namespace Wavee.UI.WinUI.Services;

public static class SpotifyMetadataLanguageSettings
{
    public const string MatchApp = "app";

    public static string NormalizeSetting(string? value)
    {
        if (value is null)
        {
            return MatchApp;
        }

        var trimmed = value.Trim();
        if (trimmed.Length == 0)
        {
            return string.Empty;
        }

        if (trimmed.Equals(MatchApp, StringComparison.OrdinalIgnoreCase))
        {
            return MatchApp;
        }

        if (trimmed.Equals("default", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        return NormalizeLocaleCode(trimmed);
    }

    public static string NormalizeLocaleCode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var trimmed = value.Trim();
        if (trimmed.Length != 2)
        {
            return string.Empty;
        }

        if (!char.IsLetter(trimmed[0]) || !char.IsLetter(trimmed[1]))
        {
            return string.Empty;
        }

        return trimmed.ToLowerInvariant();
    }

    public static string? ResolveEffectiveLocale(string? setting)
    {
        var normalized = NormalizeSetting(setting);
        if (normalized == MatchApp)
        {
            var appLanguage = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
            var appLocale = NormalizeLocaleCode(appLanguage);
            return string.IsNullOrEmpty(appLocale) ? null : appLocale;
        }

        return string.IsNullOrEmpty(normalized) ? null : normalized;
    }
}
