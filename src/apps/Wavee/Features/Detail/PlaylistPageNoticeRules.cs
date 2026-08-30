using System;

namespace Wavee;

/// <summary>Why the open playlist page is showing a notice strip instead of its ordinary edit affordances.
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
}

/// <summary>
/// The PURE rule behind the playlist page's notice strip. Engine-free by construction (System only) so it is pinned by
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
    /// <param name="canView">The playlist's <c>Capabilities.CanView</c>.</param>
    /// <param name="isOwner">The playlist's <c>Capabilities.IsOwner</c> — an owner is never "revoked" from their own list.</param>
    /// <param name="isCreatePending">An optimistic create for this uri is still in flight: the server does not have the
    /// playlist yet, so "it is not there" is the EXPECTED state and must not be reported as a deletion.</param>
    public static DetailNotice Next(DetailNotice prev, bool freshIsNull, bool headerDeleted, bool canView, bool isOwner,
                                   bool isCreatePending)
    {
        // A create that failed is terminal and self-explanatory: the follow-up reload will also find nothing, and
        // re-deciding would relabel "couldn't be created" as "was deleted", which is a different (and wrong) story.
        if (prev == DetailNotice.CreateFailed) return DetailNotice.CreateFailed;

        // While the create is still riding the outbox the page is showing a playlist the server has never heard of.
        // Absence is not news yet.
        if (isCreatePending) return prev == DetailNotice.Deleted ? DetailNotice.None : prev;

        if (headerDeleted || freshIsNull) return DetailNotice.Deleted;

        // Someone else's playlist we may no longer read. An OWNER always retains view rights on their own list, so a
        // false CanView there is a capability we failed to seed rather than a revocation — never accuse on it.
        if (!canView && !isOwner) return DetailNotice.AccessRevoked;

        // The playlist is back (undeleted, re-shared, or the earlier verdict was a transient read): clear the notice.
        return DetailNotice.None;
    }

    /// <summary>The notice for a page opened COLD (a deep link / a fresh navigation): there is no previous state and no
    /// create in flight, so the header alone decides.</summary>
    public static DetailNotice Cold(bool headerDeleted, bool canView, bool isOwner)
        => Next(DetailNotice.None, freshIsNull: false, headerDeleted, canView, isOwner, isCreatePending: false);
}
