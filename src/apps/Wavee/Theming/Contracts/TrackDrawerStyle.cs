// ── Theming/Contracts/TrackDrawerStyle.cs — what a structure supplies for the track drawer (themes plan 6.1, T8c) ───────────────
//
// The expanded-row drawer is two pictures the core composes: the FRAME the table mounts under an open row (the clip box that springs
// its presented height, the presence box that carries the indent; one drawer at a time, keyed by row identity) and the BODY a page
// hands the table (`TableProfile.Drawer`, `Track.DrawerSeam`): the facts strip (hero figures, the middot prose line, the genre / flag
// line), "Versions and formats" (the connector rail, the self row, the reserved music-video row, the alternate audio rows with their
// waveform and format split button). The structure registers a `TrackDrawerStyle` under `StyleKeys.TrackDrawer`.
//
// WHAT THE CORE KEEPS. Whether a drawer is open and which one (the table's keyed mount), the clip's reveal wiring (`OnRealized`, the
// drawer node the keyboard and the scroll anchor read), the zebra parity decision, the indent (`RowMetrics.DrawerIndent`), the demand
// (kinds 222, tags, ISRC, files, video; the versions, waveform and credits relations), the facts (`Track.Facts.For`, the Added-by name),
// the version entries and their order (`DrawerRules.VersionOrder`, the reserved video row `VideoRowFor`), the waveform's peaks, the
// "arriving" latch (what the drawer opens WITH rides its clip reveal; only a fact that lands while it is open fades up), the format
// split button's component (`FormatSplitHost`: the radio ladder, the persisted override, the overlay and its focus) and the play verb of
// a version. The View-credits sheet stays core (a dialog: T11). A template draws and never names `Playback`, `Entities`, `Store` or
// `Shell` (WVTH002).
//
// A host mounted where no registry provides the key draws nothing (logged once, `ListStyleLookup`).

using FluentGpu.Dsl;
using FluentGpu.Foundation;

namespace Wavee.Theming.Contracts;

/// <summary>What the table decided for the open row's drawer frame.</summary>
internal sealed class TrackDrawerFrameArgs
{
    /// <summary>The row's identity (<c>MembershipDiff.RowKey</c>): the frame, its presence box and the body are keyed by it.</summary>
    public required string RowKey { get; init; }

    /// <summary>The page's drawer body (<c>TableProfile.Drawer</c>), not yet keyed: the template places it under
    /// <c>"drawer-body:" + RowKey</c>.</summary>
    public required Element Body { get; init; }

    /// <summary>The indent: the row's artwork centre less the rail's own offset (<c>RowMetrics.DrawerIndent</c>).</summary>
    public required float Indent { get; init; }

    /// <summary>The row style (Classic rows have no zebra and no inset).</summary>
    public required bool Classic { get; init; }

    /// <summary>Does the drawer continue the table's zebra at an odd position? A live read for the fill's bound thunk.</summary>
    public required Func<bool> Odd { get; init; }

    /// <summary>The clip box's realized edge: the table keeps the node the reveal, the keyboard and the scroll anchor read.</summary>
    public required Action<NodeHandle> OnRealized { get; init; }
}

/// <summary>One entry of the drawer's versions list: the guaranteed self row, the music video (possibly reserved before the relation
/// answers) or an alternate audio.</summary>
internal readonly record struct TrackDrawerEntry(byte Kind, Track Version, string RowKey, bool IsNow, bool Reserved, float[]? Peaks);

/// <summary>What one render of the core drawer host decided.</summary>
internal sealed class TrackDrawerBodyArgs
{
    /// <summary>The drawer's track (the parent of every version).</summary>
    public required Track Track { get; init; }

    /// <summary>The facts the strip states, in order (empty: a bare box).</summary>
    public required IReadOnlyList<Track.Fact> Facts { get; init; }

    /// <summary>False on the first body: what the drawer opens WITH rides its clip reveal as part of the drawer; only a fact that lands
    /// while it is open fades up (a second motion on top of the reveal read as a fade-pop).</summary>
    public required bool Arriving { get; init; }

    /// <summary>The versions, flat and in order: the self row, the video, then every alternate audio.</summary>
    public required IReadOnlyList<TrackDrawerEntry> Entries { get; init; }

    /// <summary>The core's format split button (a component with the radio ladder and its overlay) for one version, keyed.</summary>
    public required Func<Track, Track, byte, Element> FormatButton { get; init; }

    /// <summary>Play the FORM the user clicked (a music video plays through the PARENT song); the deck's own item toggles.</summary>
    public required Action<Track, Track, byte> PlayVersion { get; init; }
}

/// <summary>What the format split button's component hands the template: the two verbs and the anchor capture.</summary>
internal sealed class TrackDrawerFormatArgs
{
    /// <summary>The play half.</summary>
    public required Action Play { get; init; }

    /// <summary>The caret half: opens the radio ladder.</summary>
    public required Action Open { get; init; }

    /// <summary>The root's realized edge: the ladder's flyout anchors to it.</summary>
    public required Action<NodeHandle> OnRealized { get; init; }
}

/// <summary>Draws the drawer's frame around the page's body.</summary>
internal delegate Element TrackDrawerFrameTemplate(TrackDrawerFrameArgs args);

/// <summary>Draws the drawer's body from what the host decided.</summary>
internal delegate Element TrackDrawerBodyTemplate(TrackDrawerBodyArgs args);

/// <summary>Draws the format split button (play half, caret half).</summary>
internal delegate Element TrackDrawerFormatTemplate(TrackDrawerFormatArgs args);

/// <summary>The structure's style for the track drawer (<c>StyleKeys.TrackDrawer</c>).</summary>
public sealed class TrackDrawerStyle
{
    internal TrackDrawerStyle(TrackDrawerFrameTemplate frame, TrackDrawerBodyTemplate body, TrackDrawerFormatTemplate formatSplit)
    {
        Frame = frame;
        Body = body;
        FormatSplit = formatSplit;
    }

    /// <summary>The frame.</summary>
    internal TrackDrawerFrameTemplate Frame { get; }

    /// <summary>The body.</summary>
    internal TrackDrawerBodyTemplate Body { get; }

    /// <summary>The format split button.</summary>
    internal TrackDrawerFormatTemplate FormatSplit { get; }
}
