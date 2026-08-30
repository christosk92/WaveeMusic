using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Wavee.Core;

/// <summary>The library mutation an activity entry records. Destructive kinds (Create/Delete/Cover*/Invite) are log-only.</summary>
public enum ActivityKind
{
    Save, Unsave,
    PlaylistAddTracks, PlaylistRemoveTracks, PlaylistMoveTracks,
    PlaylistRename, PlaylistVisibility,
    PlaylistCreate, PlaylistDelete,
    PlaylistCoverSet, PlaylistCoverClear,
    ContributorInvite,
}

/// <summary>An entry's lifecycle: optimistic Done, an immediate Task fault Failed, or reverted by Undo.</summary>
public enum ActivityStatus { Done, Failed, Undone }

/// <summary>A track reference captured at mutation time so Undo (and the inline detail) can re-resolve it by uri/name.</summary>
public sealed record ActivityTrackRef(string Uri, string? Name = null, string? ItemId = null);

/// <summary>The kind-specific detail an entry carries — persisted as JSON (via <see cref="ActivityJsonCtx"/>).</summary>
public sealed record ActivityPayload(
    IReadOnlyList<ActivityTrackRef>? Tracks = null,
    int? FromIndex = null, int? ToIndex = null,
    string? OldName = null, string? NewName = null,
    bool? NewIsPublic = null);

/// <summary>One logged library mutation. <see cref="Id"/> is assigned by the <see cref="ActivityLog"/> (monotonic).</summary>
public sealed record ActivityEntry(
    long Id, ActivityKind Kind, string TargetUri, string? TargetName,
    ActivityPayload? Payload, long TimestampMs, ActivityStatus Status, bool Read)
{
    /// <summary>Pure predicate: only Done, invertible kinds get an Undo button (Create/Delete/Cover*/Invite never do).</summary>
    public bool IsUndoable => Status == ActivityStatus.Done && Kind is
        ActivityKind.Save or ActivityKind.Unsave
        or ActivityKind.PlaylistAddTracks or ActivityKind.PlaylistRemoveTracks or ActivityKind.PlaylistMoveTracks
        or ActivityKind.PlaylistRename or ActivityKind.PlaylistVisibility;
}

// AOT-safe JSON for the persisted activity payload (the HistoryJsonCtx precedent). Only the locally-owned payload uses
// source-gen serialization; all network responses are parsed with manual JsonElement (the AOT rule).
[JsonSerializable(typeof(ActivityPayload))]
public partial class ActivityJsonCtx : JsonSerializerContext { }
