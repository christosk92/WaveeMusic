using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Wavee.Backend.Sync;
using Wavee.Core;

namespace Wavee.Backend.Playlists;

/// <summary>Pure confirmed-plus-intent projection. A rejected edit removes only its overlay, never restores an old baseline.</summary>
public static class PlaylistReplicaReducer
{
    public static PlaylistReplicaBaseline ApplyServer(PlaylistReplicaBaseline prior, PlaylistReadResult read)
    {
        if (read.Kind != PlaylistReadKind.Snapshot && !PlaylistRevisions.Equal(prior.Revision, read.ExpectedBase))
            throw new ReplicaBaseMismatchException(read.Uri);
        var rows = read.Kind == PlaylistReadKind.Snapshot ? read.Members.ToList() : prior.Members.ToList();
        if (read.Kind == PlaylistReadKind.Delta) PlaylistDiffApplier.Apply(rows, read.Ops);
        var revision = read.Kind == PlaylistReadKind.Unchanged ? prior.Revision : read.Revision;
        // New rows with no valid corresponding head MUST NOT inherit the old head.
        var known = PlaylistRevisions.IsWellFormed(revision);
        var header = read.Header ?? prior.Header;
        if (read.Kind == PlaylistReadKind.Delta) header = ApplyHeader(header, read.Ops);
        if (prior.Header?.DeletedByOwner == true && header is not null) header = header with { DeletedByOwner = true };
        if (header?.DeletedByOwner == true) rows.Clear();
        return new PlaylistReplicaBaseline(read.Uri, rows.ToImmutableArray(), known ? revision!.ToArray() : null,
            header, known ? ReplicaBaselineState.Verified : ReplicaBaselineState.NeedsResync, prior.Version + 1);
    }

    public static PlaylistReplicaBaseline Project(PlaylistReplicaBaseline baseline, IEnumerable<OutboxOp> intents,
        ICollection<ReplicaDeadLetter>? conflicts = null)
    {
        var rows = baseline.Members.ToList();
        var header = baseline.Header;
        foreach (var intent in intents.OrderBy(x => x.Id))
        {
            if (intent.Type != "oprebase" || intent.EntityKey != baseline.Uri) continue;
            if (header?.DeletedByOwner == true)
            {
                conflicts?.Add(new ReplicaDeadLetter(intent, PlaylistMutationFailure.Deleted, "playlist-deleted"));
                continue;
            }
            var candidate = new List<PlaylistMember>(rows);
            try { PlaylistDiffApplier.Apply(candidate, PlaylistIntentRebaser.Rebase(intent, baseline.Revision, rows)); }
            catch (Exception error) when (error is ArgumentOutOfRangeException or PlaylistMutationException)
            {
                if (intent.State == ReplicaIntentState.Pending)
                    conflicts?.Add(new ReplicaDeadLetter(intent, PlaylistMutationFailure.Conflict, "overlay-conflict"));
                continue;
            }
            rows = candidate;
            header = ApplyHeader(header, intent.Ops ?? Array.Empty<PlaylistOp>());
        }
        return baseline with { Members = rows.ToImmutableArray(), Header = header };
    }

    public static Playlist? ApplyHeader(Playlist? header, IReadOnlyList<PlaylistOp> ops)
    {
        if (header is null) return null;
        foreach (var op in ops)
        {
            if (op.Kind != PlaylistOpKind.UpdateList || op.ListPatch is not { } p) continue;
            header = header with
            {
                Name = p.ClearName ? "" : p.Name ?? header.Name,
                Description = p.ClearDescription ? null : p.Description ?? header.Description,
                Cover = p.ClearPicture ? null : p.PictureBytes is { Length: > 0 } picture
                    ? new Image("https://i.scdn.co/image/" + Convert.ToHexStringLower(picture)) : header.Cover,
                Capabilities = header.Capabilities with { IsCollaborative = p.Collaborative ?? header.Capabilities.IsCollaborative },
                DeletedByOwner = header.DeletedByOwner || p.DeletedByOwner == true
            };
        }
        return header;
    }

    /// <summary>Without a known acknowledged head, only an idempotent operation's effect is proof. Positional edits
    /// cannot be certified by replaying their indices against a later list.</summary>
    public static bool IsEffectPresent(PlaylistReplicaBaseline observed, OutboxOp intent)
    {
        if (intent.AcknowledgedRevision is { } head && PlaylistRevisions.Equal(head, observed.Revision)) return true;
        if (intent.Type == "create") return observed.State == ReplicaBaselineState.Verified && observed.Header is not null;
        if (intent.Type != "oprebase" || intent.Ops is not { Count: > 0 } ops) return false;
        if (observed.Header is null && ops.Any(x => x.Kind == PlaylistOpKind.UpdateList)) return false;
        foreach (var op in ops)
        {
            if (op.Kind == PlaylistOpKind.UpdateItem) return false;
            if (op.Kind == PlaylistOpKind.Add && (op.Items is not { Count: > 0 } items || items.Any(x => string.IsNullOrEmpty(x.ItemId)))) return false;
            if (op.Kind is PlaylistOpKind.Remove or PlaylistOpKind.Move && !op.ItemsAsKey) return false;
        }
        foreach (var op in ops.Where(x => x.Kind == PlaylistOpKind.Add))
            if (op.Items!.Any(item => !observed.Members.Any(row => row.ItemId == item.ItemId && row.ItemUri == item.ItemUri))) return false;
        var candidate = observed.Members.ToList();
        try { PlaylistDiffApplier.Apply(candidate, ops.Where(x => x.Kind != PlaylistOpKind.Add).ToArray()); }
        catch (ArgumentOutOfRangeException) { return false; }
        return candidate.SequenceEqual(observed.Members) && Equals(ApplyHeader(observed.Header, ops), observed.Header);
    }
}

public sealed class ReplicaBaseMismatchException(string uri) : InvalidOperationException("Replica base changed for " + uri);
