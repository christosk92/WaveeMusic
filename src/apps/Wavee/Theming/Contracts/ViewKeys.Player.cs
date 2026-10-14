// ── Theming/Contracts/ViewKeys.Player.cs — the Player family of published view keys (themes plan 6.0, T6a) ───────────────
//
// T6a publishes the first key of the Player family: the player bar. The rest of the family (device picker, on-media transport,
// now-playing hero, deck flyout) and the right panel are T6b's, in `ViewKeys.Panel.cs`. The leftovers lane (P3) adds the transport
// pieces the bar, the stage's transport card and the on-media strip share: the seek rail, its chapter layer and the two bubbles, the
// time label.

using FluentGpu.Theming;

namespace Wavee.Theming.Contracts;

public static partial class ViewKeys
{
    /// <summary>The player bar: the dock under the content with the now-playing identity, the transport and seek rail, and the
    /// right cluster. Mounted by the frame. Props: <see cref="PlayerBarProps"/>.</summary>
    public static readonly ViewKey<PlayerBarProps> PlayerBar = new("Player.Bar");

    // The transport pieces the bar, the stage's transport card and the on-media strip share (leftovers P3; props in PlayerTransportProps.cs).

    /// <summary>The seek rail (or, with nothing to rewind, the live line), drawn over its core host's binds and handlers.</summary>
    public static readonly ViewKey<SeekRailProps> SeekRail = new("Player.SeekRail");

    /// <summary>An episode's chapter ticks on the seek rail and the chapter tip's anchor.</summary>
    public static readonly ViewKey<SeekChaptersProps> SeekChapters = new("Player.SeekChapters");

    /// <summary>The chapter tip's bubble (inside the rail host's overlay).</summary>
    public static readonly ViewKey<ChapterTipProps> ChapterTip = new("Player.ChapterTip");

    /// <summary>The scrub readout's bubble (inside the rail host's overlay).</summary>
    public static readonly ViewKey<SeekReadoutProps> SeekReadout = new("Player.SeekReadout");

    /// <summary>One transport time label: the digits' slot or the live slot.</summary>
    public static readonly ViewKey<TimeLabelProps> TimeLabel = new("Player.TimeLabel");
}
