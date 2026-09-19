using System;
using Wavee.Core;

namespace Wavee;

/// <summary>Which EDIT the user was performing when it failed. The kind says what went wrong; the verb says what the
/// user was doing — together they pick the sentence. Only <see cref="Reorder"/> currently changes any copy, but the
/// verb is threaded through every call site so a future kind×verb cell has somewhere to land instead of a second
/// mapping table growing next to this one.</summary>
enum PlaylistEditVerb : byte { Generic = 0, Add, Remove, Reorder, Rename }

/// <summary>
/// The PURE failure→copy decision behind <c>PlaylistEditErrors</c>: exception → <see cref="PlaylistMutationFailure"/>
/// → a localization KEY. Engine-free by construction (System + Wavee.Core + the generated <c>Strings</c> consts) so the
/// rule is pinned by <c>PlaylistEditErrorKindsTests</c> against the production code rather than a copy of it.
/// <para>There is no message inspection here and no raw-exception-text passthrough: every playlist mutation failure the
/// backend surfaces is a typed <see cref="PlaylistMutationException"/> (P1 shared contract), and anything else is
/// engine prose that a listener cannot act on. An unrecognised exception is <see cref="PlaylistMutationFailure.Unknown"/>
/// — which has its own honest sentence.</para>
/// </summary>
static class PlaylistEditErrorKinds
{
    /// <summary>Classify one exception. The typed failure wins wherever it appears in the inner chain (a wrapper task /
    /// aggregate must not downgrade a real Conflict to Unknown); <see cref="NotSupportedException"/> is the "this build
    /// cannot edit Spotify playlists" wiring stub; everything else is Unknown.</summary>
    public static PlaylistMutationFailure KindOf(Exception? ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is PlaylistMutationException typed) return typed.Kind;
            if (e is AggregateException agg && agg.InnerExceptions.Count > 0)
            {
                for (int i = 0; i < agg.InnerExceptions.Count; i++)
                {
                    var k = KindOf(agg.InnerExceptions[i]);
                    if (k != PlaylistMutationFailure.Unknown) return k;
                }
            }
            if (e is NotSupportedException) return PlaylistMutationFailure.NotSupported;
        }
        return PlaylistMutationFailure.Unknown;
    }

    /// <summary>The localization key for one (kind, verb). Never returns null/empty — Unknown is a sentence, not a gap.</summary>
    public static string KeyFor(PlaylistMutationFailure kind, PlaylistEditVerb verb = PlaylistEditVerb.Generic) => kind switch
    {
        // A reorder that lost a race has a better sentence than the generic one: it names the edit, so the user knows
        // the ORDER is what did not stick (nothing was added, nothing was removed).
        PlaylistMutationFailure.Conflict => verb == PlaylistEditVerb.Reorder
            ? Strings.Detail.Edit.ReorderConflict
            : Strings.Detail.Edit.Conflict,
        PlaylistMutationFailure.Forbidden => Strings.Detail.Edit.Forbidden,
        PlaylistMutationFailure.Deleted => Strings.Detail.Edit.DeletedElsewhere,
        PlaylistMutationFailure.Offline => Strings.Detail.Edit.QueuedOffline,
        // A REORDER is the one verb that can be refused outright for being pending: the wire names both the moved rows
        // and their landing anchor by membership item_id, and a row whose id has not landed yet cannot be moved at all
        // (there is no positional fallback). "Saved on this device — still syncing" would then be a lie about an edit
        // that was never accepted, so the reorder cell says the honest, transient thing — the same sentence the drag
        // chip refuses with, so the two channels tell one story.
        PlaylistMutationFailure.Pending => verb == PlaylistEditVerb.Reorder
            ? Strings.Drag.StillSyncing
            : Strings.Detail.Edit.PendingSync,
        PlaylistMutationFailure.NotSupported => Strings.Detail.Edit.OfflineSpotifyEdits,
        // A move the seam REFUSED to build. For a reorder these are the drag chip's own two sentences, so the toast and
        // the cue tell one story: "Already there" for a destination the item already occupies, "Can't move here" for a
        // placement the rootlist cannot express. They are not "something went wrong" — nothing did.
        PlaylistMutationFailure.NoOp => verb == PlaylistEditVerb.Reorder
            ? Strings.Drag.AlreadyThere
            : Strings.Detail.Edit.Failed,
        PlaylistMutationFailure.Invalid => verb == PlaylistEditVerb.Reorder
            ? Strings.Drag.CantMoveHere
            : Strings.Detail.Edit.Failed,
        _ => Strings.Detail.Edit.Failed,
    };

    /// <summary>Offline and Pending are not failures the user has to fix — the edit is kept and will sync; NoOp and
    /// Invalid are not failures at all — the drop asked for a place the list already is, or for one it cannot express,
    /// and nothing was lost. All four are INFORMATIONAL; everything else is an error the edit did not survive.</summary>
    public static bool IsInformational(PlaylistMutationFailure kind)
        => kind is PlaylistMutationFailure.Offline or PlaylistMutationFailure.Pending
                or PlaylistMutationFailure.NoOp or PlaylistMutationFailure.Invalid;
}
