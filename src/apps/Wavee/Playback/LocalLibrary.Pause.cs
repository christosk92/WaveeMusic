// ── Playback/LocalLibrary.Pause.cs ─────────────────────────────────────────────────────────────────────────────────
// pausing the scan: the activity flyout's Pause, and "Pause scanning on battery saver" (#240 UX S2, S3, S11 "Scanning paused")
//
// Role: CORE (UI thread). Two reasons can hold the scan: the user (Pause in the flyout, until Resume; for the session) and battery saver
// (while Windows battery saver is on and the setting is on, default on). Either holds it (`LocalScan.Pause`: the workers wait before their next
// folder or file, so the scan resumes where it was). The user's reason is named first: "Paused" over "Paused · battery saver". A scan that is
// not running is not "paused": the chip and the flyout say so only while one runs or waits to run.

using FluentGpu.Foundation;
using FluentGpu.Signals;

namespace Wavee;

public static partial class LocalLibrary
{
    /// <summary>Why the scan is held.</summary>
    public enum PauseReason : byte { None, User, BatterySaver }

    static bool s_userPaused, s_saverPaused, s_saverOverride;
    static ReactiveRuntime? s_saverRuntime;
    static Effect? s_saverWatch;
    static bool s_saverFlushPosted;

    /// <summary>Why the scan is held now (None: it runs). UI thread.</summary>
    public static PauseReason PausedBy => s_userPaused ? PauseReason.User : s_saverPaused ? PauseReason.BatterySaver : PauseReason.None;

    /// <summary>The flyout's Pause / Resume. UI thread.</summary>
    public static void SetPaused(bool paused)
    {
        s_userPaused = paused;
        ApplyPause();
    }

    /// <summary>The flyout's Resume: the user's pause ends, and a hold by battery saver is lifted until battery saver turns on again (the user
    /// asked to run now). UI thread.</summary>
    public static void ResumeNow()
    {
        s_userPaused = false;
        if (s_saverPaused) { s_saverPaused = false; s_saverOverride = true; }
        ApplyPause();
    }

    /// <summary>"Pause scanning on battery saver": persists, and holds or releases the scan at once. UI thread.</summary>
    public static void SetPauseOnSaver(bool on)
    {
        Platform.Settings.Set(Platform.Keys.MusicPauseOnSaver, on);
        s_saverPaused = on && Platform.Power.EnergySaver.Peek();
        ApplyPause();
    }

    /// <summary>"Read tags of cloud-only files": persists; on, a rescan reads the placeholders (each read downloads one). UI thread.</summary>
    public static void SetReadCloud(bool on)
    {
        Platform.Settings.Set(Platform.Keys.MusicReadCloud, on);
        if (on) Rescan();
    }

    static void ApplyPause()
    {
        if (!s_fixture)
        {
            if (PausedBy == PauseReason.None) s_scan.Resume();
            else s_scan.Pause();
        }
        Log.Info("local", "local.scan pause=" + PausedBy);
        NoteProgress();
    }

    /// <summary>At start (UI thread): follow battery saver (the 2 s ambient power tick writes `Platform.Power.EnergySaver`).</summary>
    static void WatchBatterySaver()
    {
        if (s_saverRuntime is not null) return;
        s_saverRuntime = new ReactiveRuntime { FrameRequested = RequestSaverFlush };
        s_saverWatch = new Effect(s_saverRuntime, static () =>
        {
            bool saver = Platform.Power.EnergySaver.Value;
            if (!saver) s_saverOverride = false;              // the user's "run anyway" lasts until battery saver turns off
            bool hold = saver && !s_saverOverride && Platform.Settings.Get(Platform.Keys.MusicPauseOnSaver);
            if (hold == s_saverPaused) return;
            s_saverPaused = hold;
            ApplyPause();
        });
    }

    static void RequestSaverFlush()
    {
        if (s_saverFlushPosted) return;
        s_saverFlushPosted = true;
        Playback.ToUi(static () => { s_saverFlushPosted = false; s_saverRuntime?.Flush(); });
    }

    /// <summary>The inspector's `paused` fixture: the scanning fixture held by battery saver (no thread is held: the fixture has none).</summary>
    static void FixturePause(PauseReason reason)
    {
        s_userPaused = reason == PauseReason.User;
        s_saverPaused = reason == PauseReason.BatterySaver;
    }
}
