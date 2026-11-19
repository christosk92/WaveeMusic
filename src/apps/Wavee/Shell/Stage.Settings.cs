// ── Shell/Stage.Settings.cs ────────────────────────────────────────────────────────────────────────────────────────────
// the stage option surfaces' writes as verbs (themes plan T7a1): what the options popover and the gallery pane set
//
// Role: CORE
// Spec: docs/guide/theming.md section 2b (the stage)
//
// ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────
// The popover and the gallery wrote `Prefs.Stage.*` straight from their pictures (and the sync offset also pushed the audio
// graph's spectrum offset). A theme may not name `Prefs`, so each write is a verb of `IStageSettingsVerbs`; the production
// implementation is those same calls, in the same order. The stage's own signals (`Stage.StageCtx`) are mirrored from the stored
// values by SurfaceCore's one epoch effect, so a verb never touches them.
//
// TWO FACES, ONE WRITER SET (settings rework phase 5, design 5.4). `Shared` is the store writes themselves: what the Settings dials'
// writers call (`SettingsVerbs.SetStage*`). The SURFACES get `Options` and `Gallery` (`StageSurfaceVerbs`): each verb there is the
// registry dial's own writer (`SettingsDial.Write*`) with that surface as its source, so a pick on the stage shows in Settings live AND
// in Recently changed as "from the full screen"; the dial's writer lands back in `Shared`. Only the gallery's open state is no dial.

using Wavee.Theming.Contracts;

namespace Wavee;

/// <summary>The production <see cref="IStageSettingsVerbs"/>: <c>Prefs.Stage</c>, the audio seam and the pick line.</summary>
public sealed class StageSettingsVerbs : IStageSettingsVerbs
{
    /// <summary>The one cached instance every stage surface shares.</summary>
    public static readonly StageSettingsVerbs Shared = new();

    public void SetLayout(int layout) => Prefs.Stage.SetLayout(layout);

    public void SetSpectrum(int style) => Prefs.Stage.SetSpectrum(style);

    public void SetHeroLyrics(bool on) => Prefs.Stage.SetHeroLyrics(on);

    public void SetHeroMotion(bool on) => Prefs.Stage.SetHeroMotion(on);

    public void SetHeroDim(int percent) => Prefs.Stage.SetHeroDim(percent);

    public void SetSensitivity(float value) => Prefs.Stage.SetSensitivity(value);

    public void SetLyricsOverlay(bool on) => Prefs.Stage.SetLyricsOverlay(on);

    public void SetCalm(bool on) => Prefs.Stage.SetCalm(on);

    public void SetMoments(bool on) => Prefs.Stage.SetMoments(on);

    public void SetSyncOffsetMs(int ms)
    {
        Prefs.Stage.SetSyncOffsetMs(ms);
        Playback.Audio.SetSpectrumOffsetMs(ms);
    }

    public void SetGalleryOpen(bool open) => Prefs.Stage.SetGalleryOpen(open);

    public void PickFace(Visualizer.Kind face)
    {
        Prefs.Stage.SetVisualizer((int)face);
        Stage.Diagnostics.NotePick(face);
    }

    public void SetLyricsBlur(int strength) => Prefs.Lyrics.SetBlurStrength(strength);

    /// <summary>The options popover's verbs: the dials' writers, recorded as written from the full screen.</summary>
    public static readonly IStageSettingsVerbs Options = new StageSurfaceVerbs(SettingsSurfaces.StageOptions);

    /// <summary>The gallery's verbs: the dials' writers, recorded as written from the full screen's gallery.</summary>
    public static readonly IStageSettingsVerbs Gallery = new StageSurfaceVerbs(SettingsSurfaces.StageGallery);
}

/// <summary>A stage surface's <see cref="IStageSettingsVerbs"/>: each write is the registry dial's own writer with <see cref="Surface"/> as its
/// source (see the file header). A dial is looked up once, on its first write.</summary>
public sealed class StageSurfaceVerbs(string surface) : IStageSettingsVerbs
{
    /// <summary>What a write records as its source (<see cref="SettingsSurfaces"/>).</summary>
    public string Surface { get; } = surface;

    SettingsDial? _layout, _spectrum, _heroLyrics, _heroMotion, _heroDim, _sensitivity, _overlay, _calm, _moments, _sync, _face, _blur;

    static SettingsDial Of(ref SettingsDial? cell, string id) => cell ??= SettingsSpec.Dial(id);

    public void SetLayout(int layout) => Of(ref _layout, "lyrics.stage.layout").WriteChoice(layout, Surface);

    public void SetSpectrum(int style) => Of(ref _spectrum, "lyrics.stage.spectrum").WriteChoice(style, Surface);

    public void SetHeroLyrics(bool on) => Of(ref _heroLyrics, "lyrics.stage.heroLyrics").WriteToggle(on, Surface);

    public void SetHeroMotion(bool on) => Of(ref _heroMotion, "lyrics.stage.heroMotion").WriteToggle(on, Surface);

    public void SetHeroDim(int percent) => Of(ref _heroDim, "lyrics.stage.heroDim").WriteSlider(percent, Surface);

    public void SetSensitivity(float value) => Of(ref _sensitivity, "lyrics.stage.sensitivity").WriteSlider(value, Surface);

    public void SetLyricsOverlay(bool on) => Of(ref _overlay, "lyrics.stage.lyricsOverlay").WriteToggle(on, Surface);

    public void SetCalm(bool on) => Of(ref _calm, "lyrics.stage.calm").WriteToggle(on, Surface);

    public void SetMoments(bool on) => Of(ref _moments, "lyrics.stage.moments").WriteToggle(on, Surface);

    public void SetSyncOffsetMs(int ms) => Of(ref _sync, "lyrics.stage.syncOffset").WriteSlider(ms, Surface);

    /// <summary>The gallery's open state is the stage's, not a dial.</summary>
    public void SetGalleryOpen(bool open) => StageSettingsVerbs.Shared.SetGalleryOpen(open);

    /// <summary>The face dial's value is the face's place among the gallery's shown faces; a face it does not show writes the store directly.</summary>
    public void PickFace(Visualizer.Kind face)
    {
        var shown = Visualizer.Catalog.Shown;
        int index = -1;
        for (int i = 0; i < shown.Length; i++)
            if (shown[i] == face) { index = i; break; }
        if (index >= 0) Of(ref _face, "lyrics.stage.visualizer").WriteChoice(index, Surface);
        else Prefs.Stage.SetVisualizer((int)face);
        Stage.Diagnostics.NotePick(face);
    }

    public void SetLyricsBlur(int strength) => Of(ref _blur, "lyrics.blur").WriteSlider(strength, Surface);
}
