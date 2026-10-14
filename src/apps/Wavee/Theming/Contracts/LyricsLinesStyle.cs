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
}
