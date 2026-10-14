// ── Theming/Policies/ListPolicy.cs — what a structure decides about the track lists (themes plan 5.5, M4) ───────────────
//
// Track artwork is a decision of the structure: a structure that offers "hide track artwork" publishes it here, and every core list
// model that sizes lanes or builds row data around the cover (the track table's identity lane, the artist chart, the next-up rows, the
// stage's artist pane) reads it. The pictures that draw a hidden cover (the play-glyph square, the queue's text rows) are the structure's
// own and read the option itself; this is only the fact the core's geometry needs. `Default` is the shell before themes: artwork shown.

namespace Wavee.Theming;

/// <summary>What the active structure decides for the track lists.</summary>
/// <param name="TrackArtwork">Artwork is drawn inside TRACK cells. False hides it everywhere a track row carries a cover; page heroes, media cards, sidebar covers and the
/// player's identity artwork are deliberately unaffected.</param>
public sealed record ListPolicy(bool TrackArtwork)
{
    /// <summary>The shell as it shipped before themes: artwork shown.</summary>
    public static ListPolicy Default { get; } = new(TrackArtwork: true);
}
