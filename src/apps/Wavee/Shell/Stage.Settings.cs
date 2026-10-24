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
}
