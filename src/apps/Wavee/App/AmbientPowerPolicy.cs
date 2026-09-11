using System;
using System.Diagnostics;
using FluentGpu.Dsl;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.WindowsApi.Power;

namespace Wavee;

/// <summary>
/// Wavee's ambient-cadence POLICY (not a subsystem): the engine now paces every perpetual loop PER SOURCE —
/// <c>AnimEngine.Keyframes</c>/<c>UseKeyframes</c> take a <see cref="Cadence"/> per slab row, and a bare
/// <c>loop: true</c> row with no cadence resolves to <c>host.Animation.DefaultLoopHz</c> (Zed GPUI's
/// <c>Animation::with_max_fps</c> model — a per-animation cap the host applies while it is not the foreground/active
/// window, rather than one host-wide "ambient" rate a frame-loop heuristic had to infer). This policy's only job is
/// setting that ONE knob from the machine's power state, plus the companion knob for an unfocused/inactive window.
///
/// <para><b>The two knobs.</b> <see cref="AppHost.Animation"/>.<c>DefaultLoopHz</c> (float) is the rate a
/// cadence-less <c>loop: true</c> row runs at — this policy sets it from AC power alone (<see cref="PluggedLoopHz"/>
/// plugged, <see cref="BatteryLoopHz"/> on battery). <c>host.InactiveFrameIntervalMs</c> is set once, unconditionally,
/// to floor the gap between animation-only frames while the window is not the foreground/active one — that is the
/// engine's own window-state throttle now, so this policy no longer tracks focus itself.</para>
///
/// <para><b>Why focus dropped out here.</b> Previously this policy read <c>WM_ACTIVATE</c> and switched the whole
/// host's ambient rate on the edge, because the host had no notion of "unfocused" on its own. Now every source
/// already declares its own cadence, and the host throttles animation-only frames to
/// <c>InactiveFrameIntervalMs</c> whenever the window is inactive — attention is the engine's problem, not this
/// policy's. What is NOT the engine's problem is battery economics, which is why <c>DefaultLoopHz</c> is still an
/// app-level knob this policy drives.</para>
///
/// <para><b>Debounce.</b> Power reads are debounced ~2 s (<see cref="DebounceSeconds"/>): an AC blip — a dock
/// re-negotiating, a charger nudged — must not flip the render cadence, which is a visible change.</para>
///
/// <para><b>Threading.</b> Every entry point runs on the UI thread (the attach site is the pre-loop
/// <c>FluentApp.DiagnosticRun</c> hook; the poll site is a component hook), and <c>host.Animation.DefaultLoopHz</c> is
/// documented safe to flip live. No locks.</para>
///
/// <para><b>Untouched.</b> <c>UseInterval</c> sources — the now-playing equalizer analyser, the seek playhead
/// (<c>DeckClock</c>), the deck's own tick — are self-paced timers, not slab animation rows, so they never read
/// <c>DefaultLoopHz</c> and this policy does not govern them.</para>
/// </summary>
static class AmbientPowerPolicy
{
    /// <summary>How long a NEW power reading must hold before it may change the cadence.</summary>
    private const double DebounceSeconds = 2.0;
    /// <summary>Plugged-in default loop rate (Hz) for a cadence-less <c>loop: true</c> row. Internal (not private) so
    /// a self-paced <c>UseInterval</c> source that wants to mirror this rate (<c>DeckClock.TickMs</c>) can reference
    /// it instead of carrying its own copy of the literal.</summary>
    internal const float PluggedLoopHz = 30f;
    /// <summary>Battery default loop rate (Hz) for a cadence-less <c>loop: true</c> row.</summary>
    private const float BatteryLoopHz = 24f;
    /// <summary>Floor on the gap between animation-only frames while the window is not the foreground/active one
    /// (<c>AppHost.InactiveFrameIntervalMs</c>). Set once; it is the engine's window-state throttle, not power-driven.</summary>
    private const int InactiveFrameIntervalMs = 33;
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
        host.Animation.DefaultLoopHz = s_plugged ? PluggedLoopHz : BatteryLoopHz;
        host.InactiveFrameIntervalMs = InactiveFrameIntervalMs;
    }

    /// <summary>
    /// The policy's mount point: a zero-size, hit-test-invisible component whose only job is to own the power poll.
    /// A component rather than a raw callback because <c>UseInterval</c> is a hook surface (it auto-pauses via
    /// <c>UseIsActive</c> while the window is parked/minimized). Focus/activation is no longer this policy's concern —
    /// the engine throttles animation-only frames to <c>InactiveFrameIntervalMs</c> on its own while the window is
    /// inactive — so the <c>Watcher</c> no longer reads <c>InputHooks.WindowChromeEpoch</c>.
    /// </summary>
    public sealed class Watcher : Component
    {
        public override Element Render()
        {
            UseInterval(PollPower, PollMs);
            return new BoxEl { Width = 0f, Height = 0f, HitTestVisible = false };
        }
    }
}
