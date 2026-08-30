using System;
using System.Diagnostics;
using FluentGpu.Dsl;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.WindowsApi.Power;

namespace Wavee;

/// <summary>
/// Wavee's ambient-cadence POLICY (not a subsystem): when may the always-on ambient motion — the seek playhead, the
/// now-playing equalizer, skeleton shimmer, the buffering spinner, the karaoke lyrics wipe — free-run at the panel's
/// full refresh, and when should it be paced?
///
/// <para><b>The rule.</b> Always explicit ~30 fps while ambient motion is the only wake (plugged/focused OR
/// battery/background). Ambient frames are full-window presents on this engine (translucent shell), so
/// <see cref="AmbientRateMode.Uncapped"/> is never used — it made <c>AmbientCapEngaged</c> false and disabled the
/// adaptive-FPS governor exactly when a 13 px equalizer could pin ~56% GPU. <see cref="AmbientRateMode.HalfRefresh"/>
/// is also avoided: on a 120 Hz panel it resolves to 60 fps, which is still too hot for full-window Present of the
/// Wavee shell (measured ~35% <c>engtype_3d</c> while playing).</para>
///
/// <para><b>Why the two inputs.</b> Battery is the economics an always-open music app actually pays; focus is the
/// attention — a background window's shimmer is worth nothing at any rate. Neither input alone is enough (a plugged-in
/// background window still burns the pipeline; a focused laptop on battery still drains).</para>
///
/// <para><b>Debounce.</b> Power reads are debounced ~2 s (<see cref="DebounceSeconds"/>): an AC blip — a dock
/// re-negotiating, a charger nudged — must not flip the render cadence, which is a visible change. Focus edges apply
/// immediately: they are user-intent, never noise.</para>
///
/// <para><b>Threading.</b> Every entry point runs on the UI thread (the attach site is the pre-loop
/// <c>FluentApp.DiagnosticRun</c> hook; the focus/poll sites are component hooks), and
/// <see cref="AppHost.AmbientRate"/> is a volatile scalar that is documented safe to flip live. No locks.</para>
///
/// <para><b>Escape hatch.</b> <c>FG_ANIM_FPS</c> outranks this policy inside the host (see
/// <see cref="AppHost.AmbientAnimationFps"/>), so a diagnostic capture pinned to a fixed cadence stays pinned.</para>
/// </summary>
static class AmbientPowerPolicy
{
    /// <summary>How long a NEW power reading must hold before it may change the cadence.</summary>
    private const double DebounceSeconds = 2.0;
    /// <summary>Foreground ambient rate — explicit fps (full-window Present tax; not HalfRefresh).</summary>
    private const int ForegroundAmbientFps = 30;
    /// <summary>Background / battery ambient rate — explicit fps, refresh-independent.</summary>
    private const int BackgroundAmbientFps = 24;
    /// <summary>Power-read cadence — deliberately equal to the debounce, so a real transition costs exactly two reads
    /// (~2-4 s to apply) and a blip shorter than one interval is usually never even sampled. It is NOT finer: the
    /// hosting <c>UseInterval</c> is a frame-clock timer that clamps the loop's idle wait, so every tick is a wake, and
    /// a policy that exists to save power should not spend a wake per second to do it. (It does auto-pause while the
    /// window is parked/minimized — where the verdict is already the capped one, since parked implies unfocused.)
    /// <para>
    /// The wake this costs is now BOUNDED at both ends by <c>AppHost.ClampWaitToTimers</c>: a due tick clamps the wait
    /// to ≥1 ms rather than 0, and a minimized host is left blocking outright — so a frame that skips Paint (the only
    /// drain site) can no longer turn this always-mounted interval into a poll loop. Moving off the poll entirely and
    /// onto power-notification callbacks is the real fix and is deliberately NOT done here (separate ticket).
    /// </para></summary>
    private const float PollMs = 2000f;

    private static AppHost? s_host;
    private static bool s_focused = true;      // window activation (WM_ACTIVATE via InputHooks.IsWindowActive)
    private static bool s_plugged = true;      // the APPLIED power verdict
    private static bool s_pending = true;      // the most recent READ verdict, still inside the debounce window
    private static long s_pendingSince;        // QPC stamp of the read that started the current debounce window

    /// <summary>Bind the policy to the host and apply the launch verdict. Called once per launch from the composition
    /// root's <c>FluentApp.DiagnosticRun</c> hook — the only app-reachable point that holds the <see cref="AppHost"/>.</summary>
    public static void Attach(AppHost host)
    {
        s_host = host;
        s_plugged = s_pending = ReadPlugged();
        s_pendingSince = Stopwatch.GetTimestamp();
        Apply();
    }

    /// <summary>Window activation changed (applied immediately — user intent, not noise).</summary>
    public static void SetFocused(bool focused)
    {
        if (s_focused == focused) return;
        s_focused = focused;
        Apply();
    }

    /// <summary>One debounced power sample: a changed reading only re-arms the window; the cadence changes when the new
    /// reading has held for <see cref="DebounceSeconds"/>.</summary>
    public static void PollPower()
    {
        bool now = ReadPlugged();
        if (now != s_pending)
        {
            s_pending = now;
            s_pendingSince = Stopwatch.GetTimestamp();
            return;
        }
        if (now == s_plugged) return;   // settled on what is already applied — nothing to do
        if ((Stopwatch.GetTimestamp() - s_pendingSince) < (long)(DebounceSeconds * Stopwatch.Frequency)) return;
        s_plugged = now;
        Apply();
    }

    /// <summary>"Is this machine on wall power?" A desktop reports NO battery (<c>BATTERY_FLAG_NO_BATTERY</c>), and some
    /// report <c>ACLineStatus</c> as unknown — both must resolve as plugged in, or every desktop would run permanently
    /// half-capped. Windows battery-saver counts as "not plugged": the OS is explicitly asking for less work.
    /// A failed read (the API returning FALSE) also resolves plugged — the policy must never dim the app on a hiccup.</summary>
    private static bool ReadPlugged()
    {
        try
        {
            var p = PowerSession.ReadPower();
            if (p.EnergySaverOn) return false;
            return !p.HasBattery || p.Source != PowerSource.Dc;
        }
        catch
        {
            return true;
        }
    }

    private static void Apply()
    {
        if (s_host is not { } host) return;
        // Never Uncapped / HalfRefresh: ambient Present is a full-window pass. Explicit fps only.
        host.AmbientAnimationFps = (s_plugged && s_focused) ? ForegroundAmbientFps : BackgroundAmbientFps;
    }

    /// <summary>
    /// The policy's mount point: a zero-size, hit-test-invisible component whose only job is to own the two
    /// subscriptions (activation + the power poll). A component rather than a raw callback because the engine's
    /// activation signal (<c>InputHooks.WindowChromeEpoch</c>, bumped on WindowFocus/WindowBlur/WindowStateChanged) and
    /// the auto-pausing frame-clock timer are both hook surfaces. It reads the epoch inside a signal EFFECT, so an
    /// alt-tab re-runs five lines instead of re-rendering the shell.
    /// </summary>
    public sealed class Watcher : Component
    {
        public override Element Render()
        {
            var hooks = UseContext(InputHooks.Current);
            UseSignalEffect(() =>
            {
                _ = hooks.WindowChromeEpoch?.Value ?? 0;                 // subscribe: re-run on focus/blur/placement change
                SetFocused(hooks.IsWindowActive?.Invoke() ?? true);      // pull the settled state (the epoch is only the edge)
            });
            UseInterval(PollPower, PollMs);
            return new BoxEl { Width = 0f, Height = 0f, HitTestVisible = false };
        }
    }
}
