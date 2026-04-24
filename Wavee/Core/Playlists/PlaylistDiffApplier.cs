using System;
using System.Collections.Generic;
using Wavee.Protocol.Playlist;

namespace Wavee.Core.Playlists;

/// <summary>
/// Result of <see cref="PlaylistDiffApplier.Apply"/>: the post-diff item list
/// plus any list-level attribute deltas that need to be merged into the
/// surrounding <see cref="CachedPlaylist"/> by the caller.
/// </summary>
internal sealed record DiffApplyResult(
    IReadOnlyList<CachedPlaylistItem> Items,
    ListAttributesPartialState? AccumulatedListAttrs);

/// <summary>
/// Applies an ordered sequence of playlist <see cref="Op"/> deltas to a starting
/// items list and returns the resulting list. Used by
/// <see cref="PlaylistCacheService"/> when the diff endpoint returns the small
/// "ops" form (ADD/REM/MOV/UPDATE_*) instead of a full <c>Contents</c> snapshot —
/// the whole point of <c>/diff</c> is to transfer just the deltas, not re-ship
/// the item list for a single-track edit.
/// </summary>
/// <remarks>
/// Throws <see cref="System.InvalidOperationException"/> on any op the caller
/// can't honor with the given starting list (out-of-range index, malformed
/// payload, unknown op kind). The cache service treats that as "diff too stale
/// to reconcile" and falls back to a full fetch.
/// <para/>
/// Indices in each op are interpreted relative to the intermediate list AFTER
/// all preceding ops in the sequence have been applied — that's how Spotify's
/// diff endpoint emits them and matches what every other client does.
/// </remarks>
internal static class PlaylistDiffApplier
{
    private static readonly IReadOnlyDictionary<string, string> EmptyAttrs
        = new Dictionary<string, string>(0);

    public static DiffApplyResult Apply(
        IReadOnlyList<CachedPlaylistItem> from,
        IEnumerable<Op> ops)
    {
        var working = new List<CachedPlaylistItem>(from);
        ListAttributesPartialState? accumulatedListAttrs = null;

        foreach (var op in ops)
        {
            switch (op.Kind)
            {
                case Op.Types.Kind.Add:
                {
                    var add = op.Add ?? throw new InvalidOperationException("ADD op with null payload");

                    // Insertion-position precedence:
                    //   AddFirst (insert at 0) > AddLast (append) > FromIndex.
                    // Both flags set is undefined per the proto; treat as AddFirst
                    // (the conservative choice — same convention librespot uses).
                    int idx;
                    if (add.HasAddFirst && add.AddFirst) idx = 0;
                    else if (add.HasAddLast && add.AddLast) idx = working.Count;
                    else idx = add.FromIndex;

                    if (idx < 0 || idx > working.Count)
                        throw new InvalidOperationException(
                            $"ADD index {idx} out of range for list of {working.Count}");
                    for (int i = 0; i < add.Items.Count; i++)
                        working.Insert(idx + i, SelectedListContentMapper.MapPlaylistItem(add.Items[i]));
                    break;
                }
                case Op.Types.Kind.Rem:
                {
                    var rem = op.Rem ?? throw new InvalidOperationException("REM op with null payload");
                    var idx = rem.FromIndex;
                    var len = rem.Length;
                    if (idx < 0 || len < 0 || idx + len > working.Count)
                        throw new InvalidOperationException(
                            $"REM index {idx}+{len} out of range for list of {working.Count}");
                    working.RemoveRange(idx, len);
                    break;
                }
                case Op.Types.Kind.Mov:
                {
                    var mov = op.Mov ?? throw new InvalidOperationException("MOV op with null payload");
                    var fromIdx = mov.FromIndex;
                    var len = mov.Length;
                    var toIdx = mov.ToIndex;
                    if (fromIdx < 0 || len <= 0 || fromIdx + len > working.Count)
                        throw new InvalidOperationException(
                            $"MOV source {fromIdx}+{len} out of range for list of {working.Count}");
                    var slice = working.GetRange(fromIdx, len);
                    working.RemoveRange(fromIdx, len);
                    // After removal, ToIndex is interpreted in the new (shrunk)
                    // coordinate system — clamp so a tail-move can't blow up.
                    var insertAt = toIdx;
                    if (insertAt < 0) insertAt = 0;
                    if (insertAt > working.Count) insertAt = working.Count;
                    working.InsertRange(insertAt, slice);
                    break;
                }
                case Op.Types.Kind.UpdateItemAttributes:
                {
                    var u = op.UpdateItemAttributes
                        ?? throw new InvalidOperationException("UPDATE_ITEM_ATTRIBUTES op with null payload");
                    var idx = u.Index;
                    if (idx < 0 || idx >= working.Count)
                        throw new InvalidOperationException(
                            $"UPDATE_ITEM_ATTRIBUTES index {idx} out of range for list of {working.Count}");
                    working[idx] = MergeItemAttributes(working[idx], u.NewAttributes);
                    break;
                }
                case Op.Types.Kind.UpdateListAttributes:
                {
                    var u = op.UpdateListAttributes
                        ?? throw new InvalidOperationException("UPDATE_LIST_ATTRIBUTES op with null payload");
                    // Coalesce successive list-attr ops: protobuf MergeFrom on the
                    // values gives last-write-wins per field, and we union the
                    // noValue kinds. Caller applies the merged result once.
                    accumulatedListAttrs = MergeListAttrPartials(accumulatedListAttrs, u.NewAttributes);
                    break;
                }
                default:
                    throw new InvalidOperationException($"Unknown diff op kind: {op.Kind}");
            }
        }
        return new DiffApplyResult(working, accumulatedListAttrs);
    }

    /// <summary>
    /// Merges an <see cref="ItemAttributesPartialState"/> patch into an existing
    /// <see cref="CachedPlaylistItem"/>. Fields present in <c>values</c> overwrite;
    /// fields listed in <c>noValue</c> are cleared; the rest are preserved.
    /// </summary>
    private static CachedPlaylistItem MergeItemAttributes(
        CachedPlaylistItem current, ItemAttributesPartialState? partial)
    {
        if (partial is null) return current;

        string? addedBy = current.AddedBy;
        DateTimeOffset? addedAt = current.AddedAt;
        byte[]? itemId = current.ItemId;
        IReadOnlyDictionary<string, string> formatAttrs = current.FormatAttributes;

        var values = partial.Values;
        if (values is not null)
        {
            // Use the protobuf has-bits where available; fall back to "non-empty"
            // for fields without one (string AddedBy gets a synthesized HasAddedBy).
            if (values.HasAddedBy && !string.IsNullOrEmpty(values.AddedBy))
                addedBy = values.AddedBy;
            if (values.HasTimestamp && values.Timestamp > 0)
                addedAt = DateTimeOffset.FromUnixTimeMilliseconds(values.Timestamp);
            if (values.ItemId is { Length: > 0 })
                itemId = values.ItemId.ToByteArray();
            if (values.FormatAttributes.Count > 0)
                formatAttrs = SelectedListContentMapper.ExtractFormatAttributes(values.FormatAttributes);
        }

        if (partial.NoValue.Count > 0)
        {
            foreach (var kind in partial.NoValue)
            {
                switch (kind)
                {
                    case ItemAttributeKind.ItemAddedBy: addedBy = null; break;
                    case ItemAttributeKind.ItemTimestamp: addedAt = null; break;
                    case ItemAttributeKind.ItemId: itemId = null; break;
                    case ItemAttributeKind.ItemFormatAttributes: formatAttrs = EmptyAttrs; break;
                    // SEEN_AT and PUBLIC aren't on CachedPlaylistItem — skip.
                }
            }
        }

        return current with
        {
            AddedBy = addedBy,
            AddedAt = addedAt,
            ItemId = itemId,
            FormatAttributes = formatAttrs,
        };
    }

    /// <summary>
    /// Coalesces two <see cref="ListAttributesPartialState"/> patches:
    /// values are merged via protobuf <c>MergeFrom</c> (last write wins per field);
    /// noValue kinds are unioned. Used when a single diff response contains
    /// multiple <c>UPDATE_LIST_ATTRIBUTES</c> ops in sequence.
    /// </summary>
    private static ListAttributesPartialState MergeListAttrPartials(
        ListAttributesPartialState? a, ListAttributesPartialState? b)
    {
        if (a is null) return b ?? new ListAttributesPartialState();
        if (b is null) return a;

        var merged = new ListAttributesPartialState();
        if (a.Values is not null)
            merged.Values = a.Values.Clone();
        if (b.Values is not null)
        {
            if (merged.Values is null)
                merged.Values = b.Values.Clone();
            else
                merged.Values.MergeFrom(b.Values);
        }
        foreach (var k in a.NoValue)
            merged.NoValue.Add(k);
        foreach (var k in b.NoValue)
        {
            if (!merged.NoValue.Contains(k))
                merged.NoValue.Add(k);
        }
        return merged;
    }
}
