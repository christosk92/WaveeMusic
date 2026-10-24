// ── Theming/InspectSwitches.cs — switches only the inspect server turns ────────────────────────────────────────────────────────────────────
//
// A headless run has no pointer over a hover-revealed band, so an agent could never see it. The inspect server (`mount chrome=shown`) sets this; a structure that reveals chrome on hover
// reads it and draws that chrome at rest, and the two idle machines read it too: the fullscreen stage's (`ChromeIdle.Start` turns its machine off, so
// the controls never idle away) and the video presenter's (`AutoHideTransportControls` off on the `MediaPlayerElement`). `--fake` has no Connect cluster, so no output is ever remote: `mount remote=<name>` (before the mount) and
// `play remote=<name>|off` (on a live mount, the way a Connect transfer lands while playing) stand one in for the play bar's "Playing on <device>" line.
// The app never sets them.

using FluentGpu.Signals;

namespace Wavee.Theming;

/// <summary>Switches the inspect server flips for an agent's run.</summary>
public static class InspectSwitches
{
    /// <summary>Hover-revealed chrome (the fullscreen video band) is drawn at rest, and the stage's and the video presenter's chrome never auto-hides.</summary>
    public static bool HoverChromeAtRest { get; set; }

    /// <summary>The name of a remote output the play bar says it is playing on (null: the real roster decides). A signal, so a reader re-renders
    /// when it flips on a live mount, as it does for a real roster change.</summary>
    public static string? RemoteDevice { get => s_remoteDevice.Value; set => s_remoteDevice.Value = value; }

    static readonly Signal<string?> s_remoteDevice = new(null);
}
