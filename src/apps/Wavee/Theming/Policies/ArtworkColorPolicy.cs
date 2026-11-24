// ── Theming/Policies/ArtworkColorPolicy.cs — what a structure decides about colour taken from artwork (tuned Classic CT9) ────
//
// The page accent (the detail pages' Play, Follow, hearts, accent rule and links) and the now-playing colours (the fullscreen stage, its lyrics,
// the deck faces) are derived by core hosts (`Detail.AccentFor`, `Stage.Host`, `Deck`). Whether they follow the artwork at all is the
// structure's choice: a structure that offers it publishes it here, and the core hosts read it as `ShellPolicy.Tracked.Artwork`. A structure
// that declares nothing is the shell as it shipped: both follow the artwork. How much artwork tints the surfaces is the structure's own option,
// read by its material views; `Washes` is the one veto above it: while the mode on screen has a user's colour set and "Artwork colours" is off
// (#184, plan D4) the theme host publishes it false (with `AccentFromArtwork` false), so the window keeps the set's exact colours.
// `NowPlayingColors` is never vetoed: the stage, its lyrics and the deck are media surfaces.

namespace Wavee.Theming;

/// <summary>What the active structure decides about colour taken from artwork.</summary>
/// <param name="AccentFromArtwork">The page accent follows the cover (floored at 4.5 : 1, D13). False: the system accent everywhere.</param>
/// <param name="NowPlayingColors">The now-playing surfaces (the stage, its lyrics, the deck faces, the queue chip, the cover placeholders)
/// take their colour from the playing cover. False: the neutral stage and the system accent.</param>
/// <param name="Washes">The artwork may tint the shell and the pages (the structure's own wash option decides how much). False: no wash at all.</param>
public sealed record ArtworkColorPolicy(bool AccentFromArtwork, bool NowPlayingColors, bool Washes = true)
{
    /// <summary>The shell as it shipped before themes: both follow the artwork.</summary>
    public static ArtworkColorPolicy Default { get; } = new(AccentFromArtwork: true, NowPlayingColors: true);
}
