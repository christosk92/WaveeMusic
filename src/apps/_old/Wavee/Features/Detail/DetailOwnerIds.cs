using System;
using System.Collections.Generic;
using Wavee.Core;

namespace Wavee;

// ── Which user identities does an open detail page actually render? ──────────────────────────────────────────────────
// The open-playlist live-refresh subscription (DetailPage) reloads on a store change. A USER-kind bump has to be in
// that set — a byline and every Added-by cell render an Owner ROW (P4-C), so a profile landing after the page mapped
// really does make the model stale. But matching on the KIND alone matched EVERY `spotify:user:` bump in the process:
// the sidebar's profile prefetch, another page's owners, a Liked-Episodes sweep's added-by closure. On a library with
// several collaborative playlists that is a full re-map + re-project of the open page per resolved stranger.
//
// So the page captures the ids it renders when its model is mapped, and compares against those. Split out of DetailPage
// because it is pure (no engine, no signals) and is exactly the part worth pinning: the id NORMALIZATION has to agree
// with how the store keys owners, or the set silently never matches and we are back to no refresh at all.
static class DetailOwnerIds
{
    /// <summary>The distinct owner ids a playlist page renders: the header's owner, the resolved collaborators, the
    /// profile map the Added-by cells read, and every row's raw <c>AddedBy</c>. Ids are canonicalized through
    /// <see cref="UserProfileIds"/> — the same normalization <c>UserHydration</c> keys the store by — and anything that
    /// is not a legal user id is dropped.
    /// <para><paramref name="ownerName"/> is included deliberately: on a cold open the owner's profile is NOT resident,
    /// so the header carries the bare id and nothing else — and that late-landing profile is the single most common
    /// reason the refresh has to fire. A single-word DISPLAY name that happens to look like a user id can slip in; one
    /// extra re-map is the cost, against a byline that never updates for leaving it out.</para></summary>
    public static HashSet<string> From(string? ownerName, IReadOnlyList<Owner>? collaborators,
                                       IReadOnlyDictionary<string, Owner>? profilesById, IReadOnlyList<Track>? tracks)
    {
        // OrdinalIgnoreCase: user ids are case-insensitive on the wire, and the store's canonical form is lowercased.
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Raw values repeat heavily (a collaborative playlist is a handful of contributors across thousands of rows) and
        // Normalize allocates, so the RAW form is deduped before it is normalized — otherwise the AddedBy walk below is
        // one lowercase-and-concat per row on a 10k list.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Add(string? raw)
        {
            if (raw is not { Length: > 0 } || !seen.Add(raw)) return;
            if (UserProfileIds.Normalize(raw) is { } uri) ids.Add(UserProfileIds.BareId(uri));
        }

        Add(ownerName);
        if (profilesById is not null)
            foreach (var kv in profilesById) { Add(kv.Key); Add(kv.Value?.Id); }
        if (collaborators is { Count: > 0 })
            for (int i = 0; i < collaborators.Count; i++) Add(collaborators[i]?.Id);
        if (tracks is { Count: > 0 })
            for (int i = 0; i < tracks.Count; i++) Add(tracks[i].AddedBy);
        return ids;
    }

    /// <summary>Is this store-change uri a profile THIS page renders? Kind first (an alloc-free span walk through the
    /// ONE uri parser), so an album/track/playlist bump costs a comparison and nothing else.</summary>
    public static bool Matches(HashSet<string> ids, string uri)
        => ids.Count > 0
           && EntityUri.KindOf(uri) == EntityKind.User
           && ids.Contains(EntityUri.IdOf(uri));
}
