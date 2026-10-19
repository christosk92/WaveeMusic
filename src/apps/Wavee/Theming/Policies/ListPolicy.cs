// ── Theming/Policies/ListPolicy.cs — what a structure decides about the track lists (themes plan 5.5, M4, M2) ───────────
//
// The row style (M2) is a decision of the structure too: the track table's lane sets, header height and list key, the artist chart's metrics and cache
// keys read it here, and the structure's templates draw the matching skin (`TrackListStyle.Modern` / `.Classic`).
//
// Track artwork is a decision of the structure: a structure that offers "hide track artwork" publishes it here, and every core list
// model that sizes lanes or builds row data around the cover (the track table's identity lane, the artist chart, the next-up rows, the
// stage's artist pane) reads it. The pictures that draw a hidden cover (the play-glyph square, the queue's text rows) are the structure's
// own and read the option itself; this is only the fact the core's geometry needs. `Default` is the shell before themes: artwork shown.

namespace Wavee.Theming;

/// <summary>The skin of the track rows. The values are what a structure stores for its row-style option (the retired `detail.rowstyle` numbers): never renumber, append.</summary>
public enum TrackRowStyle
{
    /// <summary>The artwork-forward stacked rows (the default).</summary>
    Modern = 0,
    /// <summary>The classic column table: three text lanes over hairlines (the 0.2.9 look).</summary>
    Classic = 1,
}

/// <summary>What the active structure decides for the track lists.</summary>
/// <param name="RowStyle">Which of the structure's two row skins draws the lists.</param>
/// <param name="TrackArtwork">Artwork is drawn inside TRACK cells. False hides it everywhere a track row carries a cover; page heroes, media cards, sidebar covers and the
/// player's identity artwork are deliberately unaffected.</param>
public sealed record ListPolicy(bool TrackArtwork, TrackRowStyle RowStyle = TrackRowStyle.Modern)
{
    /// <summary>The shell as it shipped before themes: artwork shown.</summary>
    public static ListPolicy Default { get; } = new(TrackArtwork: true);

    /// <summary>Where the optional BPM · Key column's on/off lives: null (the default) the shared <c>detail.tempoColumn</c> setting every structure has read since
    /// the column shipped; a name gives this structure a setting of its own, <c>detail.tempoColumn.&lt;name&gt;</c>, OFF until the user turns it on from the list's
    /// column menu (WinUI: no board draws the column, so it rests off whatever the user chose under Classic). Appended (P6 playlist-load lane).</summary>
    public string? TempoColumnScope { get; init; }

    /// <summary>The hero arm's rows reveal TOGETHER: every mounted row holds its own shimmer (the row grid over an empty track) while the table's reveal gate is
    /// closed or the row has no face yet, and cross-dissolves to its real grid when the gate opens, so the list swaps in as one block (no lone cached row, no hole,
    /// no blank frame; covers fade in inside rows already laid out). False (the default) keeps the shipped per-row placeholder and the warm reveal ramp. WinUI
    /// sets it (the owner's recording of its playlist loads). Appended (P6 playlist-load lane).</summary>
    public bool RowsRevealTogether { get; init; }

    /// <summary>The hero arm's VIRTUALISED list (a playlist, Liked Songs: no trailing band) stands inside the page inset too (<c>DetailTierLayout.PageInsetX</c>), as the
    /// trailing band's list always has: its chrome, rows, shimmer, empty state and appended section are padded by it, and the lane ladder measures the inset width.
    /// False (the default) keeps the list full width. WinUI sets it (W-Playlist: the list on the page's 36 gutter). Appended (P6 playlist-load lane).</summary>
    public bool InsetHeroList { get; init; }

    /// <summary>The Classic row skin is on: what the core's lane sets and chart keys branch on.</summary>
    public bool ClassicRows => RowStyle == TrackRowStyle.Classic;
}
