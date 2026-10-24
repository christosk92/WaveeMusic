// ── Theming/Contracts/ViewKeys.Lyrics.cs — the lyrics family's published keys (themes plan 6.0, T7b) ─────────────────────────
//
// T7b publishes the lyrics reading surface: ONE view key (`Lyrics.View`) for the surface both mounts share (the right panel's lyrics body
// and the fullscreen stage's lyrics pane, and the podcast reader's transcript), and ONE style key (`StyleKeys.LyricsLines`) for the
// lines the surface lists. The lyrics MODEL (`LyricsModel`: state, document, the follow and resync machine, the second line, the AI
// state, the verbs), the blur policy (`Lyrics.BlurPolicy`, `Lyrics.Prefs.BlurStrength`) and the frame driver (the media clock, the
// active-line cascade and scroll spring, the depth-of-field ramp, the word wipe: `Lyrics.ViewCore`) stay core. The panel's lyrics
// header and the now-playing panel's two-row peek are the panel family's keys (`Panel.LyricsHeader`, `Player.LyricsPeek`: `ViewKeys.Panel.cs`).

using FluentGpu.Theming;

namespace Wavee.Theming.Contracts;

public static partial class ViewKeys
{
    /// <summary>The lyrics reading surface: the layer stack over the document (the body, the frame ticker, the breathing dots, the
    /// resync pill, the "no sync during video" note, the developer layer), the AI disclosure under it, and the stage's reading column
    /// around it. Mounted by <c>Lyrics.ViewCore</c> (the core host). Props: <see cref="LyricsViewProps"/>.</summary>
    public static readonly ViewKey<LyricsViewProps> LyricsView = new("Lyrics.View");
}

public static partial class StyleKeys
{
    /// <summary>The lyrics lines' list style: the centred state line, the loading bars, the unsynced reading block, the synced virtual
    /// list with its rows (word wipe, bloom, depth of field, secondary line, accent pill) and the podcast peek (<see cref="LyricsLinesStyle"/>).
    /// Read by the core lyrics host through the ambient view registry.</summary>
    public static readonly StyleKey<LyricsLinesStyle> LyricsLines = new("Lyrics.Lines");
}
