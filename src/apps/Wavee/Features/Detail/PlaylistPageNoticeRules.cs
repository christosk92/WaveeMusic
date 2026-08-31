using System;
using System.Collections.Generic;
using Wavee.Core;

namespace Wavee;

/// <summary>Why the open detail page is showing a notice strip instead of its ordinary affordances.
/// <see cref="None"/> is the overwhelmingly normal state — the strip mounts for nothing else.</summary>
public enum DetailNotice : byte
{
    /// <summary>Nothing to say. The page is live and editable per its own capabilities.</summary>
    None = 0,
    /// <summary>The playlist was deleted (by us on another device, or by its owner) while we were looking at it.</summary>
    Deleted,
    /// <summary>The playlist still exists but we may no longer view it — a permission flip landed under us.</summary>
    AccessRevoked,
    /// <summary>An optimistic create never became real: the page is showing a playlist the server rejected.</summary>
    CreateFailed,
    /// <summary>ALBUM path: the tracklist still contains unnamed rows — AlbumV4's gid-only disc rows whose TrackV4
    /// repair has not landed yet (<c>ExtendedMetadataSource.ProjectAlbum</c> mints them with an empty title and a zero
    /// duration; play counts arrive on a separate trait pass, so the rows can show a Plays value beside a blank name).
    /// A MODEL fact, not a probe: the surface never asks the network whether it is thin — the rows it was handed say
    /// so. The full <c>album:</c> page self-heals (its trailing band asks for Full); the embedded library pane never
    /// does, which is why it says where the details WILL load instead of pretending the album has none.</summary>
    MinifiedAlbum,
}

/// <summary>
/// The PURE rules behind the detail page's notice strip (playlist verdicts + the album thinness fact). Engine-free by
/// construction (System + Wavee.Core only) so it is pinned by
/// <c>PlaylistPageNoticeRulesTests</c> against the production code rather than a copy of it.
/// <para>The shape of the answer is deliberate: a notice never un-renders the page. A playlist that vanished under the
/// user keeps its content on screen (they were reading it, and blanking it to a skeleton or an error state loses their
/// place and tells them nothing) — it only loses its edit affordances and gains one sentence saying what happened.</para>
/// </summary>
static class PlaylistPageNoticeRules
{
    /// <summary>The notice for the next model.</summary>
    /// <param name="prev">The notice the page is currently showing (a terminal notice is STICKY — see below).</param>
    /// <param name="freshIsNull">The reload produced no playlist at all (evicted / 404 / gone).</param>
    /// <param name="headerDeleted">The store header carries <c>DeletedByOwner</c> (the tombstone push landed).</param>
    /// <param name="capabilitiesKnown">The header has actually carried a capabilities block (<c>Capabilities.Known</c>).
    /// A thin rootlist-seeded header has not: its all-false rights are placeholders, and "unknown" must never be read
    /// as "revoked" — the notice keeps whatever it was saying until a real block arrives.</param>
    /// <param name="canView">The playlist's <c>Capabilities.CanView</c>.</param>
    /// <param name="isOwner">The playlist's <c>Capabilities.IsOwner</c> — an owner is never "revoked" from their own list.</param>
    /// <param name="isCreatePending">An optimistic create for this uri is still in flight: the server does not have the
    /// playlist yet, so "it is not there" is the EXPECTED state and must not be reported as a deletion.</param>
    public static DetailNotice Next(DetailNotice prev, bool freshIsNull, bool headerDeleted, bool capabilitiesKnown,
                                   bool canView, bool isOwner, bool isCreatePending)
    {
        // A create that failed is terminal and self-explanatory: the follow-up reload will also find nothing, and
        // re-deciding would relabel "couldn't be created" as "was deleted", which is a different (and wrong) story.
        if (prev == DetailNotice.CreateFailed) return DetailNotice.CreateFailed;

        // While the create is still riding the outbox the page is showing a playlist the server has never heard of.
        // Absence is not news yet.
        if (isCreatePending) return prev == DetailNotice.Deleted ? DetailNotice.None : prev;

        if (headerDeleted || freshIsNull) return DetailNotice.Deleted;

        // No capabilities block yet (a thin header): the rights below are placeholders, so this reload can neither
        // accuse nor acquit — hold the current verdict. A prior deletion still clears, because the header IS back.
        if (!capabilitiesKnown) return prev == DetailNotice.Deleted ? DetailNotice.None : prev;

        // Someone else's playlist we may no longer read. An OWNER always retains view rights on their own list, so a
        // false CanView there is a capability we failed to seed rather than a revocation — never accuse on it.
        if (!canView && !isOwner) return DetailNotice.AccessRevoked;

        // The playlist is back (undeleted, re-shared, or the earlier verdict was a transient read): clear the notice.
        return DetailNotice.None;
    }

    /// <summary>The notice for a page opened COLD (a deep link / a fresh navigation): there is no previous state and no
    /// create in flight, so the header alone decides.</summary>
    public static DetailNotice Cold(bool headerDeleted, bool capabilitiesKnown, bool canView, bool isOwner)
        => Next(DetailNotice.None, freshIsNull: false, headerDeleted, capabilitiesKnown, canView, isOwner, isCreatePending: false);

    /// <summary>The ALBUM path's one verdict: <see cref="DetailNotice.MinifiedAlbum"/> when the tracklist the model
    /// carries still holds any unnamed row, else <see cref="DetailNotice.None"/>.
    /// <para>Stateless on purpose (no <c>prev</c>): thinness is re-read off every projection, so the notice clears
    /// itself the moment a later hydration pass fills the names — nothing to un-latch. The emptiness predicate is NOT
    /// restated here: <see cref="HydrationLevels.TrackUnnamed"/> is the app's single notion of a thin row (blank or
    /// uri-placeholder title, or artist uris with no names), the same one the album ladder's Open rung and the TrackV4
    /// repair key on — so the strip and the repair can never disagree about which rows are broken.</para>
    /// <para>An EMPTY tracklist is deliberately <see cref="DetailNotice.None"/>: no rows is "still loading" (the list
    /// renders its own shimmer), not "minified" — the notice is about rows the user can SEE being blank.</para></summary>
    public static DetailNotice ForAlbum(IReadOnlyList<Track> tracks)
    {
        for (int i = 0; i < tracks.Count; i++)
            if (HydrationLevels.TrackUnnamed(tracks[i])) return DetailNotice.MinifiedAlbum;
        return DetailNotice.None;
    }
}
