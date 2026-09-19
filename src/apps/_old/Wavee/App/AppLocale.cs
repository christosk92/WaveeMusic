using System;
using System.IO;
using FluentGpu.Localization;
using FluentGpu.WindowsApi.Globalization;

namespace Wavee;

/// <summary>The immutable locale captured for one Wavee process. UI and Spotify metadata move together on restart.</summary>
public readonly record struct AppLocale(string UiCulture, string SpotifyLanguage)
{
    public static readonly AppLocale English = new("en-US", "en");
}

public static class AppLocaleBootstrap
{
    public static AppLocale Initialize(IAppSettings settings, string? localizationFolder = null)
    {
        Localization.DefaultCulture = "en-US";
        Localization.LoadFolder(localizationFolder ?? Path.Combine(AppContext.BaseDirectory, "assets", "loc"));
        Localization.OsCultureProvider = WindowsCulture.GetUserDefaultLocaleName;

        // Always establish the terminal fallback first. An unsupported OS/explicit locale must not leave an old process-
        // global culture selected, and must send English to Spotify because that is what the UI will actually display.
        Localization.SetCulture("en-US");
        string selected = settings.Get(WaveeSettings.UiCulture);
        if (string.Equals(selected, "system", StringComparison.OrdinalIgnoreCase))
            Localization.UseOsCulture();
        else if (!Localization.TrySetCulture(selected))
            Localization.SetCulture("en-US");

        string effective = Localization.CurrentCulture;
        return new AppLocale(effective, SpotifyLanguage(effective));
    }

    public static string SpotifyLanguage(string? culture)
    {
        if (string.IsNullOrWhiteSpace(culture)) return "en";
        ReadOnlySpan<char> value = culture.AsSpan().Trim();
        int separator = value.IndexOfAny('-', '_');
        ReadOnlySpan<char> language = separator >= 0 ? value[..separator] : value;
        return language.Length == 2 && char.IsAsciiLetter(language[0]) && char.IsAsciiLetter(language[1])
            ? language.ToString().ToLowerInvariant()
            : "en";
    }
}
