using System;

namespace Wavee.Core;

/// <summary>An <see cref="Episode"/> projected onto the playable read-model (design §1.5). An episode already IS a
/// playable — it sits in the queue, it has a context, it plays — but every list surface joins <c>Track</c>, so an
/// episode row in a playlist rendered as nothing at all. This is the ONE projection: <c>JoinMembership</c>, the context
/// resolvers and Recents all use it, so an episode row looks the same everywhere.
/// <para>The mapping: <c>Id</c> = the EPISODE id (TrackRow.StateOf compares <c>Identity.Track.Id</c>, so borrowing the
/// show's id would light up the wrong row); no artists (a podcast has a show, not artists — the show rides in the
/// album slot so the metadata line and the "go to podcast" link resolve); never explicit; <c>Availability</c> stays
/// unknown (nobody files a verdict for episodes). <c>Episode.ProgressMs</c> has no home on <c>Track</c> and is
/// deliberately dropped — resume position is playback state, not row state.</para></summary>
public static class EpisodeAsTrack
{
    /// <param name="showUri">The owning show's uri when the CALLER knows it (a show page joining its own episodes) —
    /// it overrides the episode's own <see cref="Episode.ShowUri"/>, which the catalogue write stamps from EpisodeV4's
    /// embedded show ref. Neither known ⇒ a uri-less ref that still carries the show NAME.</param>
    public static Track? From(Episode? e, string? showUri = null)
    {
        if (e is null) return null;
        return new Track(
            e.Id, e.Uri, e.Title,
            Array.Empty<ArtistRef>(),
            new AlbumRef("", showUri ?? e.ShowUri ?? "", e.ShowName),
            e.DurationMs, IsExplicit: false, e.Image,
            Source: "podcast");
    }
}
