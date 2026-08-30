using System;
using Wavee.Core;

namespace Wavee;

/// <summary>The now-playing relations a card can have with what is playing. Pure static (System + Wavee.Core only) —
/// source-included by Wavee.Tests (<c>NowPlayingOverlayMatchTests</c>).
///
/// <para>These were ONE predicate, and that was the Home "play does nothing" defect: the equalizer wants the LOOSE answer
/// (an artist card lights up while any of that artist's tracks plays), but the play button routed on the same answer, so
/// an artist card's ▶ paused whatever happened to be playing instead of starting the artist — and its glyph flipped to
/// Pause. <see cref="OwnsPlayback"/> is the strict relation and the only one a click may consult;
/// <see cref="RelatesToPlaying"/> is the loose one and only ever drives a reveal.</para></summary>
public static class NowPlayingMatch
{
    /// <summary>Strict: this card's uri IS the playing context.</summary>
    public static bool MatchesContext(string uri, string? contextUri)
        => !string.IsNullOrEmpty(uri) && !string.IsNullOrEmpty(contextUri)
           && string.Equals(uri, contextUri, StringComparison.OrdinalIgnoreCase);

    /// <summary>Strict: this card's uri IS the playing item (a track row, a track card).</summary>
    public static bool MatchesTrack(string uri, Track? track)
        => !string.IsNullOrEmpty(uri) && track is not null
           && string.Equals(uri, track.Uri, StringComparison.OrdinalIgnoreCase);

    /// <summary>What a card's play button may pause/resume: the playing context itself, or the exact playing item. Never
    /// an album or artist DERIVED from the playing track — those cards start their own context instead.</summary>
    public static bool OwnsPlayback(string uri, string? contextUri, Track? track)
        => MatchesContext(uri, contextUri) || MatchesTrack(uri, track);

    /// <summary>Loose: what the equalizer / active reveal follows — the context, the item, or the item's album or any of
    /// its artists (so album and artist cards light up too).</summary>
    public static bool RelatesToPlaying(string uri, string? contextUri, Track? track)
    {
        if (OwnsPlayback(uri, contextUri, track)) return true;
        if (string.IsNullOrEmpty(uri) || track is null) return false;
        if (string.Equals(uri, track.Album.Uri, StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var a in track.Artists)
            if (string.Equals(uri, a.Uri, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
