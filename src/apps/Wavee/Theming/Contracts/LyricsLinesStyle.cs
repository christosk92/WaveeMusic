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

    /// <summary>Fix lane (optional): the fullscreen stage's MUSIC rows take the structure's own type, focal band and depth ladder instead of the
    /// core's (the WinUI board's W-Lyrics: a 7-line column centred in its band, 28 / 34 at 600, far lines blurred gently). Null (the default):
    /// the core's <c>Lyrics.Surface.Timed</c> metrics, <c>Lyrics.Surface.FocalBand</c> and <c>Lyrics.Fx.StageSigma</c>, unchanged. The rail,
    /// the reader and a podcast transcript never read it.</summary>
    public LyricsStageRows? StageRows { get; init; }
}

/// <summary>The stage's music rows as a structure draws them (<see cref="LyricsLinesStyle.StageRows"/>).</summary>
/// <param name="Metrics">The row type and rhythm for the stage's type scale (<c>Stage.Layout.LyricsTypeScale</c>, 1 = the core's 36 DIP stage
/// type): what the rows lay out at AND what the wipe's run length is measured at, so the two never disagree.</param>
/// <param name="FocalBand">Where the active row's content centres, as a fraction of the viewport (the core's stage band is 0.38).</param>
/// <param name="Sigma">The depth-of-field σ for a ring distance (0 = the active line; already clamped to <c>Lyrics.Emphasis.MaxBucket</c>), at
/// full strength: the user's blur strength still scales it and 0 turns it off.</param>
public sealed record LyricsStageRows(Func<float, Lyrics.RowMetrics> Metrics, float FocalBand, Func<int, float> Sigma);
