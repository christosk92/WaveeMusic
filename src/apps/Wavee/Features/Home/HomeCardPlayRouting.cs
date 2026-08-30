using Wavee.Core;

namespace Wavee.Features.Home;

/// <summary>How a Home card's play button starts playback — the one rule HomePage's PlayCard and HomeModules' section
/// grid both route through. Pure static (Wavee.Core only) — source-included by Wavee.Tests
/// (<c>HomeCardPlayRoutingTests</c>).</summary>
public static class HomeCardPlayRouting
{
    /// <summary>True for the kinds that are ONE playable item (a track, an episode): the player starts that item. Every
    /// other kind is a CONTEXT (playlist, album, artist, show, audiobook, Liked) the player starts from the top of.
    /// Sending a track uri as a context is the fire-and-forget that used to do nothing on a section grid.</summary>
    public static bool PlaysAsItem(HomeCardKind kind) => kind is HomeCardKind.Track or HomeCardKind.Episode;
}
