using System;
using System.Globalization;
using Microsoft.Windows.ApplicationModel.Resources;
using Windows.Globalization;

namespace Wavee.UI.WinUI.Services;

public interface IAppLocalizationService
{
    string GetString(string key);
    string Format(string key, params object?[] args);
}

public sealed class AppLocalizationService : IAppLocalizationService
{
    public string GetString(string key) => AppLocalization.GetString(key);

    public string Format(string key, params object?[] args) => AppLocalization.Format(key, args);
}

public static class AppLocalization
{
    private static readonly Lazy<ResourceLoader?> ResourceLoader = new(static () =>
    {
        try
        {
            return new ResourceLoader();
        }
        catch
        {
            return null;
        }
    });

    public static string GetString(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return string.Empty;

        try
        {
            var value = ResourceLoader.Value?.GetString(key);
            return string.IsNullOrEmpty(value) ? key : value;
        }
        catch
        {
            return key;
        }
    }

    public static string Format(string key, params object?[] args)
    {
        var format = GetString(key);
        return args.Length == 0
            ? format
            : string.Format(CultureInfo.CurrentUICulture, format, args);
    }

    public static void ApplyLanguageOverride(string? language)
    {
        var normalized = NormalizeLanguage(language);
        ApplicationLanguages.PrimaryLanguageOverride = normalized;

        var cultureName = string.IsNullOrWhiteSpace(normalized)
            ? CultureInfo.CurrentUICulture.Name
            : normalized;

        try
        {
            var culture = CultureInfo.GetCultureInfo(cultureName);
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = culture;
            CultureInfo.DefaultThreadCurrentCulture = culture;
            CultureInfo.DefaultThreadCurrentUICulture = culture;
        }
        catch (CultureNotFoundException)
        {
            // Windows resource resolution will fall back to the app default.
        }
    }

    public static string NormalizeLanguage(string? language)
    {
        if (string.IsNullOrWhiteSpace(language) ||
            language.Equals("system", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        return language switch
        {
            "en" => "en-US",
            "ko" => "ko-KR",
            "en-US" => "en-US",
            "ko-KR" => "ko-KR",
            _ => string.Empty
        };
    }
}
