// ── Theming/InspectSwitches.cs — switches only the inspect server turns ────────────────────────────────────────────────────────────────────
//
// A headless run has no pointer over a hover-revealed band, so an agent could never see it. The inspect server (`mount chrome=shown`) sets this; a structure that reveals chrome on hover
// reads it and draws that chrome at rest. `--fake` has no Connect cluster, so no output is ever remote: `mount remote=<name>` stands one in for the play bar's
// "Playing on <device>" line. The app never sets them.

namespace Wavee.Theming;

/// <summary>Switches the inspect server flips for an agent's run.</summary>
public static class InspectSwitches
{
    /// <summary>Hover-revealed chrome (the fullscreen video band) is drawn at rest.</summary>
    public static bool HoverChromeAtRest { get; set; }

    /// <summary>The name of a remote output the play bar says it is playing on (null: the real roster decides).</summary>
    public static string? RemoteDevice { get; set; }
}
