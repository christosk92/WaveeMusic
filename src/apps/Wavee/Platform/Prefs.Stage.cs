// ── Platform/Prefs.Stage.cs ────────────────────────────────────────────────────────────────────────────────────────
// Prefs.Stage — the fullscreen stage's preference family (mode, visualizer, sensitivity, lyrics overlay, sync offset,
// calm, moments, gallery, tip) behind ONE epoch
//
// Role: CORE
// Plan: docs/plans/wavee/fullscreen-flagship-implementation.md §2.11, §4.6
//
// Every reader is the Prefs.cs:12-22 two-liner (`_ = Epoch.Value; return Platform.Settings.Get(key)`), every writer
// clamps through the CORE rule owner and bumps once. INSIDE `Prefs`, bare `Stage` binds to THIS class, so the CORE owners
// are spelled `global::Wavee.Stage` / `global::Wavee.Visualizer` (the Lyrics.cs:1097 idiom in reverse).

using FluentGpu.Signals;

namespace Wavee;

public static partial class Prefs
{
    /// <summary>The fullscreen stage's epoch: mode, visualizer, sensitivity, lyrics overlay, sync offset, calm, moments,
    /// gallery, tip. One bump per write. Read ONLY from render/effect code (never a tick, a bind thunk or a band loop — O8).</summary>
    public static class Stage
    {
        /// <inheritdoc cref="Stage"/>
        public static readonly Signal<int> Epoch = new(0);
        /// <inheritdoc cref="Appearance.Bump"/>
        public static void Bump() => Epoch.Value = Epoch.Peek() + 1;

        public static int Mode() { _ = Epoch.Value; return global::Wavee.Stage.ModeRules.Coerce(Platform.Settings.Get(Platform.Keys.StageMode)); }
        public static void SetMode(int mode) { Platform.Settings.Set(Platform.Keys.StageMode, global::Wavee.Stage.ModeRules.Coerce(mode)); Bump(); }
        public static int Visualizer() { _ = Epoch.Value; return global::Wavee.Visualizer.Catalog.Coerce(Platform.Settings.Get(Platform.Keys.StageVisualizer)); }
        public static void SetVisualizer(int kind) { Platform.Settings.Set(Platform.Keys.StageVisualizer, global::Wavee.Visualizer.Catalog.Coerce(kind)); Bump(); }
        public static float Sensitivity() { _ = Epoch.Value; return global::Wavee.Visualizer.Bands.ClampSensitivity(Platform.Settings.Get(Platform.Keys.StageSensitivity)); }
        public static void SetSensitivity(float v) { Platform.Settings.Set(Platform.Keys.StageSensitivity, global::Wavee.Visualizer.Bands.ClampSensitivity(v)); Bump(); }
        public static bool LyricsOverlay() { _ = Epoch.Value; return Platform.Settings.Get(Platform.Keys.StageLyricsOverlay); }
        public static void SetLyricsOverlay(bool on) { Platform.Settings.Set(Platform.Keys.StageLyricsOverlay, on); Bump(); }
        public static int SyncOffsetMs() { _ = Epoch.Value; return System.Math.Clamp(Platform.Settings.Get(Platform.Keys.StageSyncOffsetMs), -500, 500); }
        public static void SetSyncOffsetMs(int ms) { Platform.Settings.Set(Platform.Keys.StageSyncOffsetMs, System.Math.Clamp(ms, -500, 500)); Bump(); }
        public static bool Calm() { _ = Epoch.Value; return Platform.Settings.Get(Platform.Keys.StageCalm); }
        public static void SetCalm(bool on) { Platform.Settings.Set(Platform.Keys.StageCalm, on); Bump(); }
        public static bool Moments() { _ = Epoch.Value; return Platform.Settings.Get(Platform.Keys.StageMoments); }
        public static void SetMoments(bool on) { Platform.Settings.Set(Platform.Keys.StageMoments, on); Bump(); }
        public static bool GalleryOpen() { _ = Epoch.Value; return Platform.Settings.Get(Platform.Keys.StageGalleryOpen); }
        public static void SetGalleryOpen(bool on) { Platform.Settings.Set(Platform.Keys.StageGalleryOpen, on); Bump(); }
        public static bool TipSeen() { _ = Epoch.Value; return Platform.Settings.Get(Platform.Keys.StageTipSeen); }
        public static void SetTipSeen() { Platform.Settings.Set(Platform.Keys.StageTipSeen, true); Bump(); }
    }
}
