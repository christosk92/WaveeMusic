// ── Theming/Policies/ArtworkColorPolicy.cs — what a structure decides about colour taken from artwork (tuned Classic CT9) ────
//
// The page accent (the detail pages' Play, Follow, hearts, accent rule and links) and the now-playing colours (the fullscreen stage, its lyrics,
// the deck faces) are derived by core hosts (`Detail.AccentFor`, `Stage.Host`, `Deck`). Whether they follow the artwork at all is the
// structure's choice: a structure that offers it publishes it here, and the core hosts read it as `ShellPolicy.Tracked.Artwork`. A structure
// that declares nothing is the shell as it shipped: both follow the artwork. The washes (how much artwork tints the surfaces) are not here:
// the structure's own material views read that option themselves.

namespace Wavee.Theming;

/// <summary>What the active structure decides about colour taken from artwork.</summary>
/// <param name="AccentFromArtwork">The page accent follows the cover (floored at 4.5 : 1, D13). False: the system accent everywhere.</param>
/// <param name="NowPlayingColors">The now-playing surfaces (the stage, its lyrics, the deck faces, the queue chip, the cover placeholders)
/// take their colour from the playing cover. False: the neutral stage and the system accent.</param>
public sealed record ArtworkColorPolicy(bool AccentFromArtwork, bool NowPlayingColors)
{
    /// <summary>The shell as it shipped before themes: both follow the artwork.</summary>
    public static ArtworkColorPolicy Default { get; } = new(AccentFromArtwork: true, NowPlayingColors: true);
}
