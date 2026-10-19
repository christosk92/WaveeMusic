// ── Theming/Contracts/LyricsLinesStyle.cs — what a structure supplies for the lyrics lines (themes plan 6.1, T7b) ───────────────
//
// A theme cannot hand back a finished `Element` for the lyrics list: the document, the follow, the cascade, the depth-of-field ramp, the
// word wipe and the node handles they write are core (`Lyrics.ViewCore`, `LyricsModel`). So the lines are a STYLE: the structure registers
// a `LyricsLinesStyle` under `StyleKeys.LyricsLines` and the core host calls it where it needs a picture, handing itself as the
// `ILyricsHost`. Every member is a pure picture of what the host decided: the host decides WHICH to draw (a timed document that is not
// suppressed is the list, any other is the unsynced block, a reader browsing an episode that does not play is the peek).

using FluentGpu.Dsl;
using FluentGpu.Hooks;

namespace Wavee.Theming.Contracts;

/// <summary>The structure's style for the lyrics lines (<c>StyleKeys.LyricsLines</c>).</summary>
public sealed class LyricsLinesStyle
{
    public LyricsLinesStyle(
        Func<ILyricsHost, string, Element> message,
        Func<ILyricsHost, Element> shimmer,
        Func<ILyricsHost, SkeletonStyle> skeleton,
        Func<ILyricsHost, Lyrics.Doc, Element> unsynced,
        Func<ILyricsHost, Lyrics.Doc, Element> lines,
        Func<ILyricsHost, Lyrics.Doc, Element> peek)
    {
        Message = message;
        Shimmer = shimmer;
        Skeleton = skeleton;
        Unsynced = unsynced;
        Lines = lines;
        Peek = peek;
    }

    /// <summary>A centred one-line state ("No lyrics available", "Nothing playing").</summary>
    public Func<ILyricsHost, string, Element> Message { get; }

    /// <summary>The loading bars, at the exact side pad the first lines will take.</summary>
    public Func<ILyricsHost, Element> Shimmer { get; }

    /// <summary>The skeleton region's style (bar colour, gap, radius, text ratio); inert under the custom shimmer but kept as it was.</summary>
    public Func<ILyricsHost, SkeletonStyle> Skeleton { get; }

    /// <summary>An untimed (or suppressed) document: a reading block, no highlight, no follow.</summary>
    public Func<ILyricsHost, Lyrics.Doc, Element> Unsynced { get; }

    /// <summary>A timed document: the measured virtual list and its rows.</summary>
    public Func<ILyricsHost, Lyrics.Doc, Element> Lines { get; }

    /// <summary>The podcast peek: the transcript's opening lines dissolving under the gate.</summary>
    public Func<ILyricsHost, Lyrics.Doc, Element> Peek { get; }

    /// <summary>Contract 1.1 (optional): the OFFLINE voice, for a track whose lyrics are not saved on this PC and could not be fetched
    /// because the app is offline (<c>LyricsState.Offline</c>). The action retries the fetch. Null = the core falls back to
    /// <see cref="Message"/> with the offline sentence (a 1.0 style keeps working).</summary>
    public Func<ILyricsHost, Action, Element>? Offline { get; init; }

    /// <summary>P6 WK (optional): the fullscreen stage's lyrics DEPTH-BLUR their far lines too (the WinUI board's LargeArt, NQ5), at the user's blur
    /// strength through the stage's flatter ladder (<c>Lyrics.Fx.StageSigma</c>; <c>Lyrics.BlurPolicy.ResolveFor(setting, onStage, styleBlur)</c>).
    /// Default false: the stage never depth-blurs (its depth cue is the scrim and the pill, and the per-line blur layers are its largest per-frame
    /// cost); the rail and the reader follow the setting either way.</summary>
    public bool StageDepthBlur { get; init; }

    /// <summary>P6 fix (optional): the side pane's (the rail's) own reading form. Null = the rail keeps the core's focal band and follows the
    /// user's depth blur (Classic, Zune). Set, the rail follows the active line at <see cref="LyricsRailForm.Band"/> and, when
    /// <see cref="LyricsRailForm.Crisp"/>, never depth-blurs; the style's <see cref="Lines"/> draws the pane's rows when
    /// <see cref="ILyricsHost.OnRail"/> says so. The stage and the podcast reader are untouched.</summary>
    public LyricsRailForm? Rail { get; init; }
}

/// <summary>The side pane's reading form (<see cref="LyricsLinesStyle.Rail"/>).</summary>
/// <param name="Band">Where the active line's content centres, as a fraction of the pane's lyrics viewport (the core clamps it to 0.05..0.95).</param>
/// <param name="Crisp">No depth blur on the pane (its distance cue is the ink), whatever the blur setting says.</param>
public sealed record LyricsRailForm(float Band, bool Crisp = true);
