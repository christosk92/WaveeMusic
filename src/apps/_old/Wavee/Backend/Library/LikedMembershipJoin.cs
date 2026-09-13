using System;
using System.Collections.Generic;
using Wavee.Core;

namespace Wavee.Backend.Library;

/// <summary>The Liked Songs read: one row per MEMBER, hydrated or not. <see cref="MemberCount"/> is the count every
/// other surface shows (sidebar/home/stats read <c>SavedUris("liked").Count</c>); <see cref="HydratedCount"/> is how
/// many rows carry a real track record.</summary>
public sealed record LikedRows(IReadOnlyList<Track> Tracks, int MemberCount, int HydratedCount);

// ── Membership ⋈ entity for Liked Songs — an OUTER join ──────────────────────────────────────────────────────────────
// Liked Songs used to be an INNER join: a member whose track row had not landed yet was simply skipped, so the page
// header (tracks.Count), the sidebar (SavedUris.Count) and the server disagreed with each other — three answers to
// "how many liked songs" — and a hydration gap looked exactly like a sync gap. The membership IS the collection; the
// entity row is decoration that arrives later. So every member yields a row: the hydrated track when the store has it,
// else a Placeholder that carries only what membership knows (uri + add date) and an EMPTY title. The empty title is
// the placeholder's whole contract: the detail table already renders a titleless row as its shimmer row, the library
// search index skips titleless rows, and IsPlaceholder is what row actions (play/menu/drag) gate on — a placeholder must
// never be handed to playback or search as if it were a track.
public static class LikedMembershipJoin
{
    public static LikedRows Join(IReadOnlyList<SavedItem> membersNewestFirst, Func<string, Track?> getTrack)
    {
        var list = new List<Track>(membersNewestFirst.Count);
        int hydrated = 0;
        for (int i = 0; i < membersNewestFirst.Count; i++)
        {
            var m = membersNewestFirst[i];
            var t = getTrack(m.Uri);
            if (t is null) { list.Add(Placeholder(m.Uri, m.AddedAtMs)); continue; }
            hydrated++;
            // Stamp AddedAt onto the read-model copy (the same shape JoinMembership gives playlist rows), so the detail
            // surface derives the Date-added column + default sort from the data itself.
            list.Add(m.AddedAtMs > 0 ? t with { AddedAt = DateTimeOffset.FromUnixTimeMilliseconds(m.AddedAtMs) } : t);
        }
        return new LikedRows(list, membersNewestFirst.Count, hydrated);
    }

    /// <summary>A membership-only row: the uri, the add date, and nothing else — no title, no artists, zero duration.</summary>
    public static Track Placeholder(string uri, long addedAtMs)
    {
        string id = uri[(uri.LastIndexOf(':') + 1)..];
        return new Track(id, uri, "", Array.Empty<ArtistRef>(), new AlbumRef("", "", ""), 0, false, null,
            AddedAt: addedAtMs > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(addedAtMs) : null);
    }

    /// <summary>True for a row <see cref="Join"/> synthesised for an unhydrated member. A real track always has a
    /// title (the metadata plane never writes a nameless one), so the empty title is the discriminator.</summary>
    public static bool IsPlaceholder(Track track) => track.Title.Length == 0;
}
