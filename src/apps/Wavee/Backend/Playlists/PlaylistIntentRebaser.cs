using System;
using System.Collections.Generic;
using System.Linq;
using Wavee.Core;

namespace Wavee.Backend.Playlists;

/// <summary>Re-express base-bound local edits against occurrence IDs before display or transport.</summary>
public static class PlaylistIntentRebaser
{
    public static IReadOnlyList<PlaylistOp> Rebase(OutboxOp intent, byte[]? revision, IReadOnlyList<PlaylistMember> members)
    {
        var ops = intent.Ops ?? Array.Empty<PlaylistOp>();
        if (intent.BaseRev is null || revision is null || PlaylistRevisions.Equal(intent.BaseRev, revision) || !NeedsRebase(ops)) return ops;
        var rows = members.ToList();
        var result = new List<PlaylistOp>(ops.Count);
        foreach (var op in ops)
        {
            var next = RebaseOne(op, rows);
            PlaylistDiffApplier.Apply(rows, [next]);
            result.Add(next);
        }
        return result;
    }
    static bool NeedsRebase(IReadOnlyList<PlaylistOp> ops)
    {
        for (int i = 0; i < ops.Count; i++)
        {
            var o = ops[i];
            if (o.Kind == PlaylistOpKind.Add && !o.AddFirst && !o.AddLast) return true;
            if (o.Kind == PlaylistOpKind.Remove && !o.ItemsAsKey) return true;
        }
        return false;
    }

    static PlaylistOp RebaseOne(PlaylistOp op, IReadOnlyList<PlaylistMember> membership)
    {
        if (op.Kind == PlaylistOpKind.Add && !op.AddFirst && !op.AddLast)
        {
            if (op.Anchor is not { } anchor) return op with { FromIndex = 0, AddLast = true };
            if (anchor.Kind == PlaylistMoveAnchorKind.First) return op with { FromIndex = 0 };
            int at = IndexOfId(membership, anchor.AfterItemId);
            return at < 0 ? op with { FromIndex = 0, AddLast = true } : op with { FromIndex = at + 1 };
        }
        if (op.Kind == PlaylistOpKind.Remove && !op.ItemsAsKey)
        {
            if (op.Items is not { Count: > 0 } items || items.Count != op.Length)
                throw new PlaylistMutationException(PlaylistMutationFailure.Conflict,
                    "That playlist changed while your edit was saving.");
            for (int i = 0; i < items.Count; i++)
                if (string.IsNullOrEmpty(items[i].ItemId))
                    throw new PlaylistMutationException(PlaylistMutationFailure.Conflict,
                        "That playlist changed while your edit was saving.");
            return op with { ItemsAsKey = true, FromIndex = 0, Length = 0 };
        }
        return op;
    }

    static int IndexOfId(IReadOnlyList<PlaylistMember> membership, string? itemId)
    {
        if (string.IsNullOrEmpty(itemId)) return -1;
        for (int i = 0; i < membership.Count; i++)
            if (string.Equals(membership[i].ItemId, itemId, StringComparison.Ordinal)) return i;
        return -1;
    }

}
