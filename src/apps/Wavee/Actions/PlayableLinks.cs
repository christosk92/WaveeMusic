using Wavee.Backend.Modules;
using Wavee.Core;

namespace Wavee;

/// <summary>Which part of a now-playing identity cluster was clicked. The three slots are the ONE vocabulary the
/// player bar, the immersive stage and any future identity surface share, so "where does the art go?" is answered in
/// one table rather than re-decided per surface.</summary>
public enum LinkSlot
{
    /// <summary>The track TITLE. Spotify: its album. A module playable: its own page.</summary>
    Title,

    /// <summary>The SUBTITLE (the artist row / the "artist — album" line). Spotify: the primary artist. A module
    /// playable: whoever published it — a YouTube channel, a Twitch channel, a radio station.</summary>
    Artist,

    /// <summary>The ART tile. A module playable: its own page. Spotify: nothing here — the bar's art tile opens the
    /// playback CONTEXT (which is not derivable from the track), so the caller keeps that answer.</summary>
    Art,
}

// ── WHERE A NOW-PLAYING SPAN GOES ────────────────────────────────────────────────────────────────────────────────────
// The player bar's identity cluster and the stage's meta link used to decide this inline, each with the same gate:
// `ArtistRef.Uri.Length > 0`. That gate is a SPOTIFY question wearing a general name — a module playable carries artist
// NAMES with no uri (LocalPlayables.ForModule builds them that way), so every module subtitle was styled-but-inert and
// every module title/art tile was dead, even once the module had told us exactly which page it wanted.
//
// This is the one table both surfaces ask instead. It is engine-free and unit-tested: the decision is a rule, not a
// rendering, and a rule that lives inside a Render() cannot be pinned.
public static class PlayableLinks
{
    /// <summary>The app route a track's identity slot navigates to, or null when the slot must stay INERT.
    ///
    /// <para>A MODULE track answers from the resolve cache (<see cref="ModulePages"/>): title/art open the playable's
    /// own page, the subtitle opens the entity the module named as its publisher. A module that stated no page for a
    /// slot gets null — an honest dead span, never a route nothing renders.</para>
    ///
    /// <para>Every other track keeps the behaviour it always had: the title is its album, the subtitle its primary
    /// artist, and the ART is not answered here at all (the bar's art tile opens the playback context, which the
    /// track does not know).</para></summary>
    /// <param name="track">The track, or null.</param>
    /// <param name="slot">Which part of the identity cluster was clicked.</param>
    public static string? RouteFor(Track? track, LinkSlot slot)
    {
        if (track is null) return null;
        if (ModulePages.RouteFor(track, slot) is { Length: > 0 } moduleRoute) return moduleRoute;
        if (IsModule(track)) return null;   // a module playable never falls through to the Spotify arms

        return slot switch
        {
            LinkSlot.Title => track.Album is { Uri.Length: > 0 } album ? "album:" + album.Uri : null,
            LinkSlot.Artist => track.Artists is { Count: > 0 } artists && artists[0].Uri.Length > 0
                ? "artist:" + artists[0].Uri
                : null,
            _ => null,
        };
    }

    /// <summary>The DISPLAY name that goes with <see cref="RouteFor"/>'s key (the route Arg — the tab strip and the
    /// breadcrumb read it). Null when the slot is inert.</summary>
    /// <param name="track">The track, or null.</param>
    /// <param name="slot">Which part of the identity cluster was clicked.</param>
    public static string? LabelFor(Track? track, LinkSlot slot)
    {
        if (track is null || RouteFor(track, slot) is null) return null;
        if (IsModule(track))
            return slot == LinkSlot.Artist && track.Artists is { Count: > 0 } a ? a[0].Name : track.Title;

        return slot switch
        {
            LinkSlot.Title => track.Album?.Name,
            LinkSlot.Artist => track.Artists is { Count: > 0 } artists ? artists[0].Name : null,
            _ => null,
        };
    }

    /// <summary>Does this track belong to a playback module? (The uri routes; nothing else is asked.)</summary>
    /// <param name="track">The track, or null.</param>
    public static bool IsModule(Track? track)
        => track is not null && Wavee.Sdk.ModuleUri.TryDecode(track.Uri, out _, out _);
}
