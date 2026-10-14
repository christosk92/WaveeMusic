// ── Screens/SettingsDoc.Rows.Appearance.cs — the Appearance tab as document rows + their verbs ─────────────────────
//
// Role: CORE (PR C13; extraction X35 writers). Theme (the picker) · Theme options (the rows the active structure still binds
// to legacy keys, in their final groups: T1b) · Display (app mode, zoom, marquee) · Lyrics · Fullscreen · Now playing · Home.
// The rows still point at TODAY'S keys (each moves to a theme option in its M-PR, §2.3, re-binding exactly one row here). Every write goes
// through the epoch it always bumped (ch 27 W2 "three facts"): marquee / track list
// style / daylist clock → `Prefs.Appearance`; the lyrics blur →
// `Prefs.Lyrics`; the two Now-playing rows → `Prefs.NpvPlayer` (the tab reads that epoch, no page re-read); the Fullscreen
// rows → `Prefs.Stage` (raw key writes included — the stage reads that epoch). A verb returns whether the PAGE must re-read.

using Wavee.Theming;
using T = Wavee.Settings.Tab;
using K = Wavee.SettingsRowKind;

namespace Wavee;

public static partial class SettingsDoc
{
    /// <summary>The Q7 reason a sidebar control gives while the sidebar is being edited.</summary>
    public const string FinishEditingKey = "sidebar.pane.finishEditing";

    static IReadOnlyList<SettingsSection> AppearanceSections() =>
    [
        // The theme picker: one row, one writer (`ThemeHost.Switch`). Its value is the index of the selected card among the
        // cards the picker shows (`ThemePickerEntry`s), so a view and a test read the same answer.
        Sect(T.Appearance, "Theme", Strings.Settings.Appearance.Title, Strings.Settings.ThemePicker.Subtitle,
            Make(T.Appearance, "Theme", "themePicker", K.Picker, Strings.Settings.Appearance.Title,
                SettingsWriter.OfChoice("theme.pick", SettingsVerbs.PickTheme),
                captionKey: Strings.Settings.ThemePicker.Sub, value: _ => SettingsVerbs.ThemeIndex())),

        // The rows the ACTIVE structure still binds to today's keys (T1b). The view lists them by `IStructureLayer.LegacyRows`
        // (id and group are the theme's), so a theme with other options, or none, shows none of them.
        Sect(T.Appearance, "Theme options", Strings.Settings.ThemeOptions.TitleKey, null,
            Make(T.Appearance, "Theme options", "sidebarReset", K.Action, Strings.Settings.Sidebar.Reset,
                SettingsWriter.OfAction("sidebar.reset", SettingsVerbs.ResetSidebarLayout),
                captionKey: Strings.Settings.Sidebar.ResetSub,
                enabled: f => f.SidebarEditing ? SettingsEnablement.Off(FinishEditingKey)
                    : f.SidebarModified ? SettingsEnablement.On : SettingsEnablement.Off(null),
                silent: true, catalog: false),
            Make(T.Appearance, "Theme options", "sidebarCustomize", K.Action, "sidebar.menu.edit",
                SettingsWriter.OfAction("sidebar.edit", SettingsVerbs.EditSidebar),
                captionKey: "settings.sidebar.editSub", enabled: NotWhileEditing),
            Make(T.Appearance, "Theme options", "sidebarResetEverything", K.Action, "sidebar.menu.resetEverything",
                SettingsWriter.OfAction("sidebar.resetEverything", SettingsVerbs.ResetSidebarEverything),
                captionKey: "settings.sidebar.resetEverythingSub", enabled: NotWhileEditing, catalog: false),
            Make(T.Appearance, "Theme options", "trackListStyle", K.Picker, Strings.Settings.Appearance.TrackListStyle,
                SettingsWriter.OfChoice("lists.trackListStyle", SettingsVerbs.SetTrackListStyle),
                captionKey: Strings.Settings.Appearance.TrackListStyleSub, value: s => Prefs.Clamp(s.Get(Platform.Keys.TrackRowStyle), 2))),

        // The core presentation prefs every theme honours (the plan's App mode and Zoom rows; marquee is a core pref, owner Q27).
        Sect(T.Appearance, "Display", Strings.Settings.ThemeOptions.DisplayTitle, null,
            Make(T.Appearance, "Display", "appMode", K.Segmented, Strings.Settings.Appearance.AppMode,
                SettingsWriter.OfChoice("appearance.mode", SettingsVerbs.SetThemeMode),
                captionKey: Strings.Settings.Appearance.ThemeSub, value: s => (int)Appearance.ModeOf(s.Get(Platform.Keys.ThemeMode))),
            // A combo, not segments: thirteen items (Auto + twelve rungs). Index 0 is "Auto (N%)" for both Auto and Dense.
            Make(T.Appearance, "Display", "zoom", K.Choice, Strings.Settings.Appearance.Zoom,
                SettingsWriter.OfChoice("zoom.pick", SettingsVerbs.PickZoom),
                captionKey: Strings.Settings.Appearance.ZoomSub, value: _ => SettingsVerbs.ZoomIndex()),
            Make(T.Appearance, "Display", "marquee", K.Toggle, Strings.Settings.Appearance.Marquee,
                SettingsWriter.OfToggle("appearance.marquee", on => SettingsVerbs.SetAppearanceToggle(Platform.Keys.MarqueeEnabled, on)),
                captionKey: Strings.Settings.Appearance.MarqueeSub, value: BoolOf(Platform.Keys.MarqueeEnabled))),

        Sect(T.Appearance, "Lyrics", Strings.Settings.Lyrics.Title, Strings.Settings.Lyrics.Subtitle,
            // The 0..100 slider (0 turns the blur off); there is no Auto choice.
            Make(T.Appearance, "Lyrics", "lyricsBlur", K.Slider, Strings.Settings.Appearance.LyricsBlur,
                SettingsWriter.OfSlider("lyrics.blur", SettingsVerbs.SetLyricsBlur),
                captionKey: Strings.Settings.Appearance.LyricsBlurSub,
                value: s => (float)Prefs.Lyrics.FromStored(s.Get(Platform.Keys.LyricsBlurStrength))),
            // On-device AI lyrics (AiLyrics.UI.Settings.cs): the card is the AI module's own state machine; its switches
            // are rows here, its per-language and per-file verbs stay the module's.
            Make(T.Appearance, "Lyrics", "aiLyrics", K.Toggle, Strings.Settings.Lyrics.Ai.Title,
                SettingsWriter.OfToggle("ai.enabled", SettingsVerbs.SetAiLyricsEnabled)),
            Make(T.Appearance, "Lyrics", "aiWordSync", K.Toggle, Strings.Settings.Lyrics.Ai.WordSync,
                SettingsWriter.OfToggle("ai.wordSync", on => SettingsVerbs.SetAiPref(Platform.Keys.AiLyricsWordSync, on)),
                captionKey: Strings.Settings.Lyrics.Ai.WordSyncSub, value: BoolOf(Platform.Keys.AiLyricsWordSync)),
            Make(T.Appearance, "Lyrics", "aiPlainText", K.Toggle, Strings.Settings.Lyrics.Ai.PlainText,
                SettingsWriter.OfToggle("ai.plainText", on => SettingsVerbs.SetAiPref(Platform.Keys.AiLyricsPlainText, on)),
                captionKey: Strings.Settings.Lyrics.Ai.PlainTextSub, value: BoolOf(Platform.Keys.AiLyricsPlainText)),
            Make(T.Appearance, "Lyrics", "aiBatterySaver", K.Toggle, Strings.Settings.Lyrics.Ai.BatterySaver,
                SettingsWriter.OfToggle("ai.batterySaver", on => SettingsVerbs.SetAiPref(Platform.Keys.AiLyricsOnBatterySaver, on)),
                captionKey: Strings.Settings.Lyrics.Ai.BatterySaverSub, value: BoolOf(Platform.Keys.AiLyricsOnBatterySaver)),
            Make(T.Appearance, "Lyrics", "aiLanguages", K.Composite, Strings.Settings.Lyrics.Ai.Languages,
                SettingsWriter.Owned("AiLyrics.InstallLanguage/RemoveLanguage")),
            Make(T.Appearance, "Lyrics", "aiFiles", K.Composite, Strings.Settings.Lyrics.Ai.Files,
                SettingsWriter.Owned("AiLyrics.OpenFolder/RemoveFiles"))),

        // The fullscreen stage's settings, mirrored from its gallery and options popover. The page reads `Prefs.Stage.Epoch`,
        // so a pick made on the stage shows here live (and these writes wake the stage).
        Sect(T.Appearance, "Fullscreen", Strings.Stage.Settings.Title, Strings.Stage.Settings.Subtitle,
            Make(T.Appearance, "Fullscreen", "stageVisualizer", K.Choice, Strings.Stage.Settings.Visualizer,
                SettingsWriter.OfChoice("stage.visualizer", SettingsVerbs.SetStageVisualizer),
                captionKey: Strings.Stage.Settings.VisualizerSub, value: _ => Settings.VisualizerIndex()),
            Make(T.Appearance, "Fullscreen", "stageLayout", K.Choice, Strings.Stage.Settings.Layout,
                SettingsWriter.OfChoice("stage.layout", SettingsVerbs.SetStageLayout),
                captionKey: Strings.Stage.Settings.LayoutSub, value: _ => Prefs.Stage.Layout()),
            Make(T.Appearance, "Fullscreen", "stageSensitivity", K.Slider, Strings.Stage.Settings.Sensitivity,
                SettingsWriter.OfSlider("stage.sensitivity", SettingsVerbs.SetStageSensitivity),
                captionKey: Strings.Stage.Settings.SensitivitySub, value: _ => Prefs.Stage.Sensitivity()),
            Make(T.Appearance, "Fullscreen", "stageLyricsOverlay", K.Toggle, Strings.Stage.Settings.LyricsOverlay,
                SettingsWriter.OfToggle("stage.lyricsOverlay", on => SettingsVerbs.SetStageToggle(Platform.Keys.StageLyricsOverlay, on)),
                captionKey: Strings.Stage.Settings.LyricsOverlaySub, value: BoolOf(Platform.Keys.StageLyricsOverlay)),
            Make(T.Appearance, "Fullscreen", "stageSyncOffset", K.Slider, Strings.Stage.Settings.SyncOffset,
                SettingsWriter.OfSlider("stage.syncOffset", SettingsVerbs.SetStageSyncOffset),
                captionKey: Strings.Stage.Settings.SyncOffsetSub, value: _ => (float)Prefs.Stage.SyncOffsetMs()),
            Make(T.Appearance, "Fullscreen", "stageCalm", K.Toggle, Strings.Stage.Settings.Calm,
                SettingsWriter.OfToggle("stage.calm", on => SettingsVerbs.SetStageToggle(Platform.Keys.StageCalm, on)),
                captionKey: Strings.Stage.Settings.CalmSub, value: BoolOf(Platform.Keys.StageCalm)),
            Make(T.Appearance, "Fullscreen", "stageMoments", K.Toggle, Strings.Stage.Settings.Moments,
                SettingsWriter.OfToggle("stage.moments", on => SettingsVerbs.SetStageToggle(Platform.Keys.StageMoments, on)),
                captionKey: Strings.Stage.Settings.MomentsSub, value: BoolOf(Platform.Keys.StageMoments))),

        Sect(T.Appearance, "Now playing", Strings.Settings.NowPlaying.Title, Strings.Settings.NowPlaying.Subtitle,
            // DEVELOPER-ONLY, exactly like the rail header switch it duplicates — a normal build shows the cover. No page re-read:
            // the tab reads `Prefs.NpvPlayer`'s epoch.
            Make(T.Appearance, "Now playing", "npvPresentation", K.Segmented, Strings.Settings.Appearance.NpvPresentation,
                SettingsWriter.OfChoice("npv.presentation", SettingsVerbs.SetNpvPresentation),
                captionKey: Strings.Settings.Appearance.NpvPresentationSub, value: _ => Rail.PlayerPrefs.Presentation()),
            Make(T.Appearance, "Now playing", "npvStyle", K.Choice, Strings.Settings.Appearance.NpvStyle,
                SettingsWriter.OfChoice("npv.style", SettingsVerbs.SetNpvStyle),
                captionKey: Strings.Settings.Appearance.NpvStyleSub, value: _ => Rail.PlayerPrefs.Style())),

        Sect(T.Appearance, "Home", Strings.Settings.Home.Title, Strings.Settings.Home.Subtitle,
            // No page re-read: nothing else on this tab shows the clock.
            Make(T.Appearance, "Home", "daylistClock", K.Choice, Strings.Settings.Appearance.DaylistClock,
                SettingsWriter.OfChoice("lists.daylistClock", SettingsVerbs.SetDaylistClock),
                captionKey: Strings.Settings.Appearance.DaylistClockSub, value: _ => (int)Prefs.Appearance.DaylistClock())),
    ];

    /// <summary>The sidebar's layout verbs refuse while it is being edited, and say so (Q7).</summary>
    static SettingsEnablement NotWhileEditing(ISettingsFacts f)
        => f.SidebarEditing ? SettingsEnablement.Off(FinishEditingKey) : SettingsEnablement.On;
}

public static partial class SettingsVerbs
{
    // ── Theme ────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The theme picker's pick: switch to the card at <paramref name="index"/> of the cards the picker shows. The picked
    /// theme's root mounts fresh (owner Q26), so the page never needs a re-read; picking the selected card, or an index no card
    /// has (the list changed under a stale view), does nothing.</summary>
    public static bool PickTheme(int index)
    {
        var entries = ThemePickerFacts.State.Peek().Entries(Platform.Developer.Enabled.Peek());
        if ((uint)index >= (uint)entries.Count || entries[index].Selected) return false;
        _ = ThemeHost.Switch(entries[index].Id);
        return false;
    }

    /// <summary>The index of the selected card among the cards the picker shows; -1 before a theme is installed.</summary>
    public static int ThemeIndex()
    {
        var entries = ThemePickerFacts.State.Peek().Entries(Platform.Developer.Enabled.Peek());
        for (int i = 0; i < entries.Count; i++)
            if (entries[i].Selected) return i;
        return -1;
    }

    /// <summary>0 System · 1 Light · 2 Dark: <see cref="Appearance.SetMode"/> swaps the installed Wavee palette IN PLACE (System
    /// re-reads the OS theme and accent now), persists, and asks the host for the 250 ms re-theme cross-fade (ch 27 §5).</summary>
    public static bool SetThemeMode(int mode)
    {
        Appearance.SetMode((ThemeMode)Math.Clamp(mode, 0, 2));
        return true;
    }

    /// <summary>The Zoom combo's pick: index 0 is "Auto (N%)" (mode = Auto, never Dense, applying the suggestion the row last
    /// showed — <see cref="SettingsFacts.AutoZoom"/>); a rung writes Manual. A deliberate pick writes at once.</summary>
    public static bool PickZoom(int index)
    {
        if (index <= 0)
        {
            ZoomService.PickAuto(SettingsFacts.AutoZoom);
            return true;
        }
        int step = index - 1;
        if ((uint)step >= (uint)FluentGpu.Foundation.ZoomLadder.Steps.Length) return false;
        ZoomService.PickManual(FluentGpu.Foundation.ZoomLadder.Steps[step]);
        return true;
    }

    /// <summary>The Zoom combo's selection: the rung that matches the live zoom in Manual, else the head item (Auto and Dense).</summary>
    public static int ZoomIndex()
        => ZoomService.Mode == ZoomAutoMode.Manual
            ? 1 + Math.Max(0, Array.IndexOf(FluentGpu.Foundation.ZoomLadder.Steps, FluentGpu.Foundation.ZoomLadder.Snap(FluentGpu.FluentApp.Zoom)))
            : 0;

    /// <summary>An appearance switch: persist and bump the appearance epoch once; the page re-reads.</summary>
    public static bool SetAppearanceToggle(SettingKey<bool> key, bool on)
    {
        Platform.Settings.Set(key, on);
        Prefs.Appearance.Bump();
        return true;
    }

    // ── Lists ────────────────────────────────────────────────────────────────────────────────────────────────────────

    public static bool SetTrackListStyle(int i)
    {
        if ((uint)i >= 2u || Platform.Settings.Get(Platform.Keys.TrackRowStyle) == i) return false;
        Prefs.Appearance.Set(Platform.Keys.TrackRowStyle, i);
        return true;
    }

    /// <summary>The daylist clock (#185): the index IS the <see cref="DaylistClockMode"/> int. The write goes through the ONE
    /// appearance writer, so the playlist hero strip and the Home card swap their clock live; the page does not re-read.</summary>
    public static bool SetDaylistClock(int i)
    {
        if ((uint)i >= DaylistClockShows.ModeCount || Platform.Settings.Get(Platform.Keys.DaylistClock) == i) return false;
        Prefs.Appearance.Set(Platform.Keys.DaylistClock, i);
        return false;
    }

    // ── Sidebar ──────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Reset the current layout to its authored form, with the sidebar's own undo toast.</summary>
    public static bool ResetSidebarLayout()
    {
        Sidebar.Dispatch(new ResetLayout(Sidebar.Layout.Peek()),
            FluentGpu.Localization.Loc.Format("sidebar.toast.reset", ("name", Sidebar.LayoutName(Sidebar.Layout.Peek()))));
        return false;
    }

    /// <summary>"Edit sidebar…" (P4).</summary>
    public static bool EditSidebar()
    {
        Sidebar.EnterEdit();
        return false;
    }

    /// <summary>"Reset everything": the sidebar's own confirm, opened over the Settings page's overlay host.</summary>
    public static bool ResetSidebarEverything()
    {
        if (SettingsContext.Overlay is { } overlay) Sidebar.SidebarMenus.ConfirmResetEverything(overlay);
        return false;
    }

    // ── Lyrics ───────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Writes the 0..100 value (0 = off). An unchanged value writes nothing.</summary>
    public static bool SetLyricsBlur(float v)
    {
        int strength = Math.Clamp((int)MathF.Round(v), 0, 100);
        if (Platform.Settings.Get(Platform.Keys.LyricsBlurStrength) == strength) return false;
        Prefs.Lyrics.SetBlurStrength(strength);
        return true;
    }

    /// <summary>The on-device AI lyrics master switch (the host answers; the card swaps its arm).</summary>
    public static bool SetAiLyricsEnabled(bool on)
    {
        AiLyrics.SetEnabled(on);
        return false;
    }

    /// <summary>An AI behaviour switch, written through <c>Prefs.AiLyrics.Set</c> so the driver's epoch re-evaluates the song.</summary>
    public static bool SetAiPref(SettingKey<bool> key, bool on)
    {
        Prefs.AiLyrics.Set(key, on);
        return false;
    }

    // ── Fullscreen stage (every write wakes the stage through `Prefs.Stage`) ─────────────────────────────────────────

    public static bool SetStageVisualizer(int index)
    {
        if ((uint)index < (uint)Visualizer.Catalog.ShownCount) Prefs.Stage.SetVisualizer((int)Visualizer.Catalog.Shown[index]);
        return false;
    }

    public static bool SetStageLayout(int index)
    {
        Prefs.Stage.SetLayout(index);
        return false;
    }

    public static bool SetStageSensitivity(float v)
    {
        Prefs.Stage.SetSensitivity(v);
        return false;
    }

    /// <summary>Lyrics overlay · Calm motion · Change with the music: persist, wake the stage, re-read the page.</summary>
    public static bool SetStageToggle(SettingKey<bool> key, bool on)
    {
        Platform.Settings.Set(key, on);
        Prefs.Stage.Bump();
        return true;
    }

    /// <summary>The spectrum offset snaps to 10 ms, persists, and is pushed to the audio host.</summary>
    public static bool SetStageSyncOffset(float v)
    {
        int ms = (int)MathF.Round(v / 10f) * 10;
        Prefs.Stage.SetSyncOffsetMs(ms);
        Playback.Audio.SetSpectrumOffsetMs(ms);
        return false;
    }

    // ── Now playing (the rail's adapter flips the hero to Player and logs the source; the tab reads its epoch) ───────

    public static bool SetNpvPresentation(int index)
    {
        Shell.DeckPrefs.SetPresentation(index, Shell.DeckPrefSource.Settings);
        return false;
    }

    public static bool SetNpvStyle(int index)
    {
        Shell.DeckPrefs.SetStyle(index, Shell.DeckPrefSource.Settings);
        return false;
    }
}
