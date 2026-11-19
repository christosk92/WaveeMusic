// ── Screens/SettingsHome.cs — the Settings home's data: six facts at a glance, three suggestions, five recent changes ─────────────
//
// Role: CORE (settings rework phase 3, handoff/settings-2026-11-18/DESIGN.md 3.3; owner decisions 11 and 14), ready for each theme's
// page (phase 4). The home holds NO controls (a second control for a dial would be a second home): state and doors.
//   · `Glance()`       six read-only tiles, each the current value at rest and the dial it links to (Theme, Audio quality, Notifications,
//                      Storage, Lyrics, Account);
//   · `Suggestions()`  the hint service's top three for the home (`Hints.Suggestions`), worded and wired as in the app
//                      (`SuggestionProps`); the page records what it showed (`Hints.SuggestionShown`), which counts against the same
//                      show budget;
//   · `Recent()`       the last five dials written from any surface (the hint blob's ring, persisted across restarts), each with
//                      where it came from ("from the queue").
// Every link goes through `SettingsLinks`. Nothing here is per frame: the page reads these when it renders (its epoch moves on a write).

using FluentGpu.Localization;
using Wavee.Theming;
using Wavee.Theming.Contracts;

namespace Wavee;

/// <summary>One at-a-glance tile: its title (a loc key), its value at rest (resolved) and the dial it links to.</summary>
public sealed record SettingsGlance(string Id, string TitleKey, string Value, string DialId);

/// <summary>One Recently changed row: the dial (its id, its title key, or null when the dial is not here now: another theme's option),
/// its value now, where the write came from (a <see cref="SettingsSurfaces"/> name and its "from …" loc key, or null) and when (unix ms).</summary>
public sealed record SettingsRecentChange(string DialId, string? TitleKey, object? Value, string Surface, string? FromKey, long AtMs);

/// <summary>The Settings home's data (see the file header).</summary>
public static class SettingsHome
{
    /// <summary>The six tiles, in the home's order.</summary>
    public static IReadOnlyList<SettingsGlance> Glance() =>
    [
        new("theme", Strings.Settings.Landing.Glance.Theme, ThemeValue(), "appearance.theme"),
        new("quality", Strings.Settings.Landing.Glance.Quality, QualityValue(), "sound.quality"),
        new("notifications", Strings.Settings.Landing.Glance.Notifications, NotificationsValue(), "notifications.windows"),
        new("storage", Strings.Settings.Landing.Glance.Storage, StorageValue(), "storage.cache.budget"),
        new("lyrics", Strings.Settings.Landing.Glance.Lyrics, LyricsValue(), "lyrics.blur"),
        new("account", Strings.Settings.Landing.Glance.Account, AccountValue(), "account.identity"),
    ];

    /// <summary>"WinUI · Dark".</summary>
    static string ThemeValue()
    {
        string theme = "";
        foreach (ThemePickerEntry entry in ThemePickerFacts.State.Peek().Entries(developerMode: true))
            if (entry.Selected) theme = Loc.Get(entry.NameKey);
        string mode = Loc.Get(Platform.Settings.Get(Platform.Keys.ThemeMode) switch
        {
            1 => Strings.Settings.Choice.Light,
            2 => Strings.Settings.Choice.Dark,
            _ => Strings.Settings.Choice.System,
        });
        return Strings.Settings.Landing.Theme(theme, mode);
    }

    /// <summary>"Very high · Normalization on".</summary>
    static string QualityValue()
    {
        string quality = Loc.Get((Spotify.Audio.Quality)Math.Clamp(Platform.Settings.Get(Platform.Keys.PlaybackQuality), 0, 3) switch
        {
            Spotify.Audio.Quality.Normal96 => Strings.Settings.Playback.QualityNormal,
            Spotify.Audio.Quality.High160 => Strings.Settings.Playback.QualityHigh,
            Spotify.Audio.Quality.VeryHigh320 => Strings.Settings.Playback.QualityVeryHigh,
            _ => Strings.Settings.Playback.QualityLossless,
        });
        string normalization = Loc.Get(Platform.Settings.Get(Platform.Keys.NormalizationEnabled)
            ? Strings.Settings.Landing.NormalizationOn : Strings.Settings.Landing.NormalizationOff);
        return Strings.Settings.Landing.Quality(quality, normalization);
    }

    /// <summary>"Windows banners off · quiet 22:00–08:00".</summary>
    static string NotificationsValue()
    {
        var policy = Notify.Prefs.Policy();
        string banners = Loc.Get(policy.WindowsEnabled ? Strings.Settings.Landing.BannersOn : Strings.Settings.Landing.BannersOff);
        if (!policy.Quiet.Enabled) return Strings.Settings.Landing.QuietOff(banners);
        return Strings.Settings.Landing.Quiet(banners, Hour(Platform.Settings.Get(Platform.Keys.NotifyQuietFromHour)),
            Hour(Platform.Settings.Get(Platform.Keys.NotifyQuietToHour)));
    }

    static string Hour(int hour) => Math.Clamp(hour, 0, 23).ToString("00", System.Globalization.CultureInfo.InvariantCulture) + ":00";

    /// <summary>"12.4 GB of 32.0 GB cache" (the running count: no directory walk).</summary>
    static string StorageValue()
    {
        if (Spotify.Audio.DiskCache.IfCreated is not { } cache) return Loc.Get(Strings.Settings.Landing.StorageOff);
        var o = cache.Occupancy();
        string used = Settings.StorageFormat.Bytes(o.Bytes);
        return o.BudgetBytes is long budget
            ? Strings.Settings.Landing.Storage(used, Settings.StorageFormat.Bytes(budget))
            : Strings.Settings.Landing.StorageUnlimited(used);
    }

    /// <summary>"Blur 60 % · AI lyrics ready", or "· AI lyrics available on this PC" while it is off where it could run.</summary>
    static string LyricsValue()
    {
        object? blur = SettingsSpec.Dial("lyrics.blur").Current;
        int percent = blur is null ? 0 : (int)MathF.Round(Convert.ToSingle(blur, System.Globalization.CultureInfo.InvariantCulture));
        var ai = AiLyrics.Current.Peek();
        if (ai.Phase == AiLyrics.SetupPhase.Ready) return Strings.Settings.Landing.BlurAiReady(percent);
        if (ai.Availability == AiLyrics.Availability.Available && !Platform.Settings.Get(Platform.Keys.AiLyricsEnabled))
            return Strings.Settings.Landing.BlurAiAvailable(percent);
        return Strings.Settings.Landing.Blur(percent);
    }

    /// <summary>"Signed in as {name} · Spotify Premium".</summary>
    static string AccountValue()
    {
        var (name, _, tier) = Shell.AccountSummary();
        if (string.IsNullOrEmpty(name)) return Loc.Get(Strings.Settings.Landing.SignedOut);
        string plan = tier switch
        {
            Spotify.Tier.Premium => Loc.Get(Strings.Auth.PremiumBadge),
            Spotify.Tier.Free => Loc.Get(Strings.Setup.SignIn.Free),
            _ => "",
        };
        return Strings.Settings.Landing.Account(name, plan);
    }

    /// <summary>The home's suggestions: the hint service's top three (owner decision 14).</summary>
    public static IReadOnlyList<HintDef> Suggestions() => Hints.Suggestions();

    /// <summary>A suggestion's props: the same words and dismissal as its hint in the app; every row is a door to its dial.</summary>
    public static HintProps SuggestionProps(HintDef def) => Hints.PropsFor(def, HintSurfaces.SettingsHome);

    /// <summary>The last five dials written, newest first.</summary>
    public static IReadOnlyList<SettingsRecentChange> Recent()
    {
        int count = HintState.RecentCount;
        var rows = new List<SettingsRecentChange>(count);
        for (int i = 0; i < count; i++)
        {
            var write = HintState.Recent(i);
            SettingsDial? dial = SettingsSpec.TryResolve(write.DialId, out var d, out _) ? d : null;
            rows.Add(new SettingsRecentChange(write.DialId, dial?.TitleKey, dial?.Current, write.Surface, FromKey(write.Surface), write.AtMs));
        }
        return rows;
    }

    static readonly string[] s_labelled =
    [
        SettingsSurfaces.Settings, SettingsSurfaces.Hint, SettingsSurfaces.Palette, SettingsSurfaces.Inspector, SettingsSurfaces.QueuePanel,
        SettingsSurfaces.StageOptions, SettingsSurfaces.StageGallery, SettingsSurfaces.PlayerBar, SettingsSurfaces.DevicePicker,
        SettingsSurfaces.MiniPlayerOptions, SettingsSurfaces.LikedCover, SettingsSurfaces.TrackListMenu, SettingsSurfaces.NowPlayingPicker,
        SettingsSurfaces.WhatsNew, SettingsSurfaces.CrashPrompt, SettingsSurfaces.LogsMenu, SettingsSurfaces.TrayMenu, SettingsSurfaces.VideoMenu,
        SettingsSurfaces.SidebarMenu, SettingsSurfaces.SetupWizard,
    ];

    /// <summary>The "from …" words of a write's source (a loc key), or null for a source with none (a panel added later says nothing
    /// until its words are written).</summary>
    public static string? FromKey(string surface)
        => Array.IndexOf(s_labelled, surface) >= 0 ? "settings.landing.from." + surface : null;
}
