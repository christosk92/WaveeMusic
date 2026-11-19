// ── Screens/SettingsSpec.Lyrics.cs — Lyrics & Now playing: the screens you watch while music plays (the dials and their verbs) ────
//
// Role: CORE (settings rework phase 1). Lyrics (the blur, the seek rail's tip, on-device AI lyrics) · Now playing (the player style and
// its finish) · Full screen (the stage's face, layout and the layout's own options, the gallery's settings) · Mini player (its Options).
//
// Every write goes through the epoch it always bumped: the lyrics rows → `Prefs.Lyrics`; the player style and finish → `Prefs.NpvPlayer`
// (the page reads that epoch, no page re-read); the full screen → `Prefs.Stage` through the stage surfaces' own verbs, so a pick made on
// the stage shows here live (and these writes wake the stage); the mini player's options → its own signals, persisted.

using K = Wavee.SettingsRowKind;

namespace Wavee;

public static partial class SettingsSpec
{
    static SettingsCategory LyricsCategory() => new("lyrics", Strings.Settings.Categories.Lyrics.Title,
        Strings.Settings.Categories.Lyrics.Strip, Strings.Settings.Categories.Lyrics.Subtitle, "Microphone",
    [
        Group("lyrics", Strings.Settings.Groups.Lyrics.Text, Strings.Settings.Lyrics.Subtitle,
            // The 0..100 slider (0 turns the blur off); there is no Auto choice.
            Make("lyrics.blur", K.Slider, Strings.Settings.Appearance.LyricsBlur,
                SettingsWriter.OfSlider("lyrics.blur", SettingsVerbs.SetLyricsBlur), legacy: ["lyricsBlur"], glyph: "Filter",
                captionKey: Strings.Settings.Appearance.LyricsBlurSub,
                value: s => (float)Prefs.Lyrics.FromStored(s.Get(Platform.Keys.LyricsBlurStrength)),
                contexts: [SettingsSurfaces.StageOptions]),
            // The seek rail's lyric tip (the line sung at the time under the pointer); the rails read `Prefs.Lyrics`' epoch.
            Make("lyrics.seekTip", K.Toggle, Strings.Settings.Appearance.LyricsSeekTip,
                SettingsWriter.OfToggle("lyrics.seekTip", SettingsVerbs.SetLyricsSeekTip), legacy: ["lyricsSeekTip"], glyph: "MusicNote",
                captionKey: Strings.Settings.Appearance.LyricsSeekTipSub, value: BoolOf(Platform.Keys.LyricsSeekTip)),
            // On-device AI lyrics (AiLyrics.UI.Settings.cs): the card is the AI module's own state machine; its switches are dials
            // here, its per-language and per-file verbs stay the module's.
            Make("lyrics.ai", K.Toggle, Strings.Settings.Lyrics.Ai.Title,
                SettingsWriter.OfToggle("ai.enabled", SettingsVerbs.SetAiLyricsEnabled), legacy: ["aiLyrics"], glyph: "RefineSparkle",
                aliases: ["ai", "npu"]),
            Make("lyrics.ai.wordSync", K.Toggle, Strings.Settings.Lyrics.Ai.WordSync,
                SettingsWriter.OfToggle("ai.wordSync", on => SettingsVerbs.SetAiPref(Platform.Keys.AiLyricsWordSync, on)),
                legacy: ["aiWordSync"], parent: "lyrics.ai", glyph: "Font",
                captionKey: Strings.Settings.Lyrics.Ai.WordSyncSub, value: BoolOf(Platform.Keys.AiLyricsWordSync)),
            Make("lyrics.ai.plainText", K.Toggle, Strings.Settings.Lyrics.Ai.PlainText,
                SettingsWriter.OfToggle("ai.plainText", on => SettingsVerbs.SetAiPref(Platform.Keys.AiLyricsPlainText, on)),
                legacy: ["aiPlainText"], parent: "lyrics.ai", glyph: "Document",
                captionKey: Strings.Settings.Lyrics.Ai.PlainTextSub, value: BoolOf(Platform.Keys.AiLyricsPlainText)),
            Make("lyrics.ai.batterySaver", K.Toggle, Strings.Settings.Lyrics.Ai.BatterySaver,
                SettingsWriter.OfToggle("ai.batterySaver", on => SettingsVerbs.SetAiPref(Platform.Keys.AiLyricsOnBatterySaver, on)),
                legacy: ["aiBatterySaver"], parent: "lyrics.ai", glyph: "Clock",
                captionKey: Strings.Settings.Lyrics.Ai.BatterySaverSub, value: BoolOf(Platform.Keys.AiLyricsOnBatterySaver)),
            Make("lyrics.ai.languages", K.Composite, Strings.Settings.Lyrics.Ai.Languages,
                SettingsWriter.Owned("AiLyrics.InstallLanguage/RemoveLanguage"), legacy: ["aiLanguages"], parent: "lyrics.ai", glyph: "LocaleLanguage"),
            Make("lyrics.ai.files", K.Composite, Strings.Settings.Lyrics.Ai.Files,
                SettingsWriter.Owned("AiLyrics.OpenFolder/RemoveFiles"), legacy: ["aiFiles"], parent: "lyrics.ai", glyph: "Folder")),

        Group("nowPlaying", Strings.Settings.Groups.Lyrics.NowPlaying, Strings.Settings.NowPlaying.Subtitle,
            // The player style, the same setting the now-playing style chip changes.
            Make("lyrics.playerStyle", K.Choice, Strings.Settings.Appearance.NpvStyle,
                SettingsWriter.OfChoice("npv.style", SettingsVerbs.SetNpvStyle), legacy: ["npvStyle"], glyph: "Settings",
                captionKey: Strings.Settings.Appearance.NpvStyleSub, value: _ => Rail.PlayerPrefs.Style(),
                contexts: [SettingsSurfaces.NowPlayingPicker], choices: Array.ConvertAll(Rail.PlayerCatalog.Styles, static s => s.NameKey)),
            // The style's finish (Vinyl, Pocket): the style picker's swatches, composed only for a style that has one.
            Make("lyrics.playerStyle.finish", K.Choice, Strings.Settings.NowPlaying.Finish,
                SettingsWriter.OfChoice("npv.finish", SettingsVerbs.SetNpvFinish), parent: "lyrics.playerStyle", glyph: "Brush",
                captionKey: Strings.Settings.NowPlaying.FinishSub, value: _ => Rail.PlayerPrefs.Finish(Rail.PlayerPrefs.Style()),
                visible: static _ => Rail.PlayerCatalog.ById(Rail.PlayerPrefs.StylePeek()).HasFinish,
                contexts: [SettingsSurfaces.NowPlayingPicker])),

        Group("fullScreen", Strings.Settings.Groups.Lyrics.FullScreen, Strings.Stage.Settings.Subtitle,
            Make("lyrics.stage.visualizer", K.Choice, Strings.Stage.Settings.Visualizer,
                SettingsWriter.OfChoice("stage.visualizer", SettingsVerbs.SetStageVisualizer), legacy: ["stageVisualizer"], glyph: "Equalizer",
                captionKey: Strings.Stage.Settings.VisualizerSub, value: _ => Settings.VisualizerIndex(),
                contexts: [SettingsSurfaces.StageOptions], aliases: ["visualiser"], choices: VisualizerChoices()),
            Make("lyrics.stage.layout", K.Choice, Strings.Stage.Settings.Layout,
                SettingsWriter.OfChoice("stage.layout", SettingsVerbs.SetStageLayout), legacy: ["stageLayout"], glyph: "SplitView",
                captionKey: Strings.Stage.Settings.LayoutSub, value: _ => Prefs.Stage.Layout(),
                contexts: [SettingsSurfaces.StageOptions],
                choices: [Strings.Stage.Layout.Card, Strings.Stage.Layout.Large, Strings.Stage.Layout.Centered, Strings.Stage.Layout.Artist]),
            // The layout's own options, the options popover's Layout tab: the spectrum under the cover (Large art, Centered) and the
            // Artist layout's photo (its lyrics, its pan and zoom, its dimming). Each is composed only for the layouts that use it.
            Make("lyrics.stage.spectrum", K.Segmented, Strings.Stage.Options.SpectrumUnder,
                SettingsWriter.OfChoice("stage.spectrum", SettingsVerbs.SetStageSpectrum), parent: "lyrics.stage.layout", glyph: "Volume",
                value: s => Stage.LayoutRules.CoerceSpectrum(s.Get(Platform.Keys.StageSpectrum)),
                visible: static _ => StageLayoutIs(Stage.VizLayout.LargeArt) || StageLayoutIs(Stage.VizLayout.Centered),
                contexts: [SettingsSurfaces.StageOptions]),
            Make("lyrics.stage.heroLyrics", K.Toggle, Strings.Stage.Options.HeroLyrics,
                SettingsWriter.OfToggle("stage.heroLyrics", SettingsVerbs.SetStageHeroLyrics), parent: "lyrics.stage.layout", glyph: "Font",
                value: BoolOf(Platform.Keys.StageHeroLyrics), visible: static _ => StageLayoutIs(Stage.VizLayout.Artist),
                contexts: [SettingsSurfaces.StageOptions]),
            Make("lyrics.stage.heroMotion", K.Toggle, Strings.Stage.Options.HeroMotion,
                SettingsWriter.OfToggle("stage.heroMotion", SettingsVerbs.SetStageHeroMotion), parent: "lyrics.stage.layout", glyph: "Zoom",
                value: BoolOf(Platform.Keys.StageHeroMotion), visible: static _ => StageLayoutIs(Stage.VizLayout.Artist),
                enabled: static _ => Design.Reduced ? SettingsEnablement.Off(Strings.Stage.Options.HeroMotionReduced) : SettingsEnablement.On,
                contexts: [SettingsSurfaces.StageOptions]),
            Make("lyrics.stage.heroDim", K.Slider, Strings.Stage.Options.HeroDim,
                SettingsWriter.OfSlider("stage.heroDim", SettingsVerbs.SetStageHeroDim), parent: "lyrics.stage.layout", glyph: "Sun",
                value: s => (float)Stage.LayoutRules.ClampDim(s.Get(Platform.Keys.StageHeroDim)), visible: static _ => StageLayoutIs(Stage.VizLayout.Artist),
                contexts: [SettingsSurfaces.StageOptions]),
            Make("lyrics.stage.sensitivity", K.Slider, Strings.Stage.Settings.Sensitivity,
                SettingsWriter.OfSlider("stage.sensitivity", SettingsVerbs.SetStageSensitivity), legacy: ["stageSensitivity"], glyph: "Audio",
                captionKey: Strings.Stage.Settings.SensitivitySub, value: _ => Prefs.Stage.Sensitivity(),
                contexts: [SettingsSurfaces.StageGallery]),
            Make("lyrics.stage.lyricsOverlay", K.Toggle, Strings.Stage.Settings.LyricsOverlay,
                SettingsWriter.OfToggle("stage.lyricsOverlay", on => SettingsVerbs.SetStageToggle(Platform.Keys.StageLyricsOverlay, on)),
                legacy: ["stageLyricsOverlay"], glyph: "Document",
                captionKey: Strings.Stage.Settings.LyricsOverlaySub, value: BoolOf(Platform.Keys.StageLyricsOverlay),
                contexts: [SettingsSurfaces.StageGallery]),
            Make("lyrics.stage.syncOffset", K.Slider, Strings.Stage.Settings.SyncOffset,
                SettingsWriter.OfSlider("stage.syncOffset", SettingsVerbs.SetStageSyncOffset), legacy: ["stageSyncOffset"], glyph: "Clock",
                captionKey: Strings.Stage.Settings.SyncOffsetSub, value: _ => (float)Prefs.Stage.SyncOffsetMs(),
                contexts: [SettingsSurfaces.StageGallery]),
            Make("lyrics.stage.calm", K.Toggle, Strings.Stage.Settings.Calm,
                SettingsWriter.OfToggle("stage.calm", on => SettingsVerbs.SetStageToggle(Platform.Keys.StageCalm, on)),
                legacy: ["stageCalm"], glyph: "RefineSparkle",
                captionKey: Strings.Stage.Settings.CalmSub, value: BoolOf(Platform.Keys.StageCalm),
                contexts: [SettingsSurfaces.StageGallery]),
            Make("lyrics.stage.moments", K.Toggle, Strings.Stage.Settings.Moments,
                SettingsWriter.OfToggle("stage.moments", on => SettingsVerbs.SetStageToggle(Platform.Keys.StageMoments, on)),
                legacy: ["stageMoments"], glyph: "Brush",
                captionKey: Strings.Stage.Settings.MomentsSub, value: BoolOf(Platform.Keys.StageMoments),
                contexts: [SettingsSurfaces.StageGallery])),

        // The mini player's Options panel: the same writers (each loads the mini player's prefs first, so a write from here before the
        // window ever opened keeps the other two).
        Group("miniPlayer", Strings.Settings.Groups.Lyrics.MiniPlayer, null,
            Make("lyrics.mini.face", K.Picker, Strings.Mini.Face,
                SettingsWriter.OfChoice("mini.face", SettingsVerbs.SetMiniFace), glyph: "Picture",
                value: s => MiniPlayer.ClampFaceIndex(s.Get(Platform.Keys.MiniPlayerFace)), contexts: [SettingsSurfaces.MiniPlayerOptions],
                aliases: ["pip"]),
            Make("lyrics.mini.onTop", K.Toggle, Strings.Mini.OnTop,
                SettingsWriter.OfToggle("mini.onTop", SettingsVerbs.SetMiniOnTop), glyph: "Pin",
                value: BoolOf(Platform.Keys.MiniPlayerOnTop), contexts: [SettingsSurfaces.MiniPlayerOptions], aliases: ["pip"]),
            // Per theme: the active theme's slot of the mask, with that theme's default (its mini player style).
            Make("lyrics.mini.artColour", K.Toggle, Strings.Mini.ArtColour,
                SettingsWriter.OfToggle("mini.artColour", SettingsVerbs.SetMiniArtColour), glyph: "Brush",
                value: s => MiniPlayer.ArtColourForActiveTheme(s.Get(Platform.Keys.MiniPlayerArtColour)), contexts: [SettingsSurfaces.MiniPlayerOptions])),
    ]);

    /// <summary>The faces the visualizer dial offers (the gallery's shown faces, in gallery order), as loc keys.</summary>
    static string[] VisualizerChoices()
    {
        var shown = Visualizer.Catalog.Shown;
        var keys = new string[shown.Length];
        for (int i = 0; i < keys.Length; i++) keys[i] = Visualizer.NameKey(shown[i]);
        return keys;
    }

    /// <summary>Is the full screen's STORED layout <paramref name="layout"/>? (A layout option is composed only for its layouts.)</summary>
    static bool StageLayoutIs(Stage.VizLayout layout) => Stage.LayoutRules.Coerce(Platform.Settings.Get(Platform.Keys.StageLayout)) == (int)layout;
}

public static partial class SettingsVerbs
{
    // ── Lyrics ───────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Writes the 0..100 value (0 = off). An unchanged value writes nothing.</summary>
    public static bool SetLyricsBlur(float v)
    {
        int strength = Math.Clamp((int)MathF.Round(v), 0, 100);
        if (Platform.Settings.Get(Platform.Keys.LyricsBlurStrength) == strength) return false;
        Prefs.Lyrics.SetBlurStrength(strength);
        return true;
    }

    /// <summary>The seek rail's lyric tip: persist and bump the lyrics epoch (the rails re-read it); the page re-reads.</summary>
    public static bool SetLyricsSeekTip(bool on)
    {
        Prefs.Lyrics.SetSeekTip(on);
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

    // ── Now playing (the rail's adapter logs the source; the page reads its epoch) ───────────────────────────────────

    public static bool SetNpvStyle(int index)
    {
        Shell.DeckPrefs.SetStyle(index, Shell.DeckPrefSource.Settings);
        return false;
    }

    /// <summary>The stored style's finish (the style picker's swatch writer); a style without a finish ignores it.</summary>
    public static bool SetNpvFinish(int index)
    {
        Shell.DeckPrefs.SetFinish(Rail.PlayerPrefs.StylePeek(), index, Shell.DeckPrefSource.Settings);
        return false;
    }

    // ── Full screen (every write wakes the stage through `Prefs.Stage`) ─────────────────────────────────────────────

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

    /// <summary>The spectrum under the cover (Bars · Ring · Line · Off): the options popover's writer.</summary>
    public static bool SetStageSpectrum(int style)
    {
        StageSettingsVerbs.Shared.SetSpectrum(style);
        return false;
    }

    /// <summary>The Artist layout's "Show lyrics over the photo": the options popover's writer; the page re-reads.</summary>
    public static bool SetStageHeroLyrics(bool on)
    {
        StageSettingsVerbs.Shared.SetHeroLyrics(on);
        return true;
    }

    /// <summary>The Artist layout's "Slow pan and zoom": the options popover's writer; the page re-reads.</summary>
    public static bool SetStageHeroMotion(bool on)
    {
        StageSettingsVerbs.Shared.SetHeroMotion(on);
        return true;
    }

    /// <summary>The Artist layout's photo dimming (0..100 %): the options popover's writer.</summary>
    public static bool SetStageHeroDim(float percent)
    {
        StageSettingsVerbs.Shared.SetHeroDim((int)MathF.Round(percent));
        return false;
    }

    // ── Mini player (its Options panel's writers) ────────────────────────────────────────────────────────────────────

    public static bool SetMiniFace(int index)
    {
        MiniPlayer.ChooseFace(index);
        return true;
    }

    public static bool SetMiniOnTop(bool on)
    {
        MiniPlayer.SetOnTop(on);
        return true;
    }

    /// <summary>"Colour from album art" for the ACTIVE theme (its slot of the mask); the page re-reads.</summary>
    public static bool SetMiniArtColour(bool on)
    {
        MiniPlayer.SetArtColourForActiveTheme(on);
        return true;
    }
}
