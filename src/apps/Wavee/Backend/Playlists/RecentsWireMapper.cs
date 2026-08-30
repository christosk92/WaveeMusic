using System;
using System.Collections.Generic;
using Wavee.Core;
using Pl = Wavee.Protocol.Playlist;

namespace Wavee.Backend.Playlists;

// The recents-page boundary mapper: a playlist4 SelectedListContent → the proto-free RecentsItem vector (Wavee.Core).
// Kept in the Backend (like PlaylistWireMapper) so it is unit-tested against crafted protos. It is the recents SIBLING
// of PlaylistWireMapper.ToMember — which deliberately DROPS format_attributes; a recents item carries almost all of its
// meaning IN those attributes (the value is empty, the KEY is the payload), so this mapper keeps every one that matters:
//   group_id_<N>        — 0 = group header, >0 = a collapsed member of the preceding header
//   children_group_id   — the header-only marker that it collapses a run of members
//   recent_type_played  — played vs saved (recent_type_saved)
//   content_type_music  — the content facet the filter chips select (content_type_*)
//   group_metadata      — the ONLY valued attribute: base64(RecentsGroupMetadata) → child_count + child_uris (+ Kind)
public static class RecentsWireMapper
{
    /// <summary>Project a recents SelectedListContent into the ordered flat items + the opaque revision bytes. The items
    /// still need <see cref="RecentsList.Group"/> to collapse into display rows.</summary>
    public static (IReadOnlyList<RecentsItem> Items, byte[]? Revision) Map(Pl.SelectedListContent slc)
    {
        byte[]? rev = slc.HasRevision ? slc.Revision.ToByteArray() : null;
        var items = new List<RecentsItem>();
        if (slc.Contents is { } contents)
            foreach (var item in contents.Items)
                items.Add(ToItem(item));
        return (items, rev);
    }

    static RecentsItem ToItem(Pl.Item item)
    {
        string itemId = "";
        string uri = item.Uri;   // may be "" for a single-context group header
        long playedAt = 0;
        var reason = RecentsReason.Unknown;
        string? contentType = null;
        int? groupId = null;
        bool hasChildrenGroupId = false;
        RecentsGroupInfo? group = null;

        if (item.Attributes is { } a)
        {
            if (a.HasItemId) itemId = Convert.ToHexStringLower(a.ItemId.Span);   // hex item_id — the reconciler key
            if (a.HasTimestamp) playedAt = a.Timestamp;
            for (int i = 0; i < a.FormatAttributes.Count; i++)
            {
                var fa = a.FormatAttributes[i];
                if (!fa.HasKey) continue;
                string key = fa.Key;
                if (key == "group_metadata")
                {
                    group = DecodeGroupMetadata(fa.HasValue ? fa.Value : null);
                }
                else if (key.StartsWith("children_group_id", StringComparison.Ordinal))
                {
                    hasChildrenGroupId = true;
                }
                else if (key.StartsWith("group_id_", StringComparison.Ordinal)
                    && int.TryParse(key.AsSpan("group_id_".Length), out int n))
                {
                    // A header (0) wins over a member index when both are somehow present — the min collapses correctly.
                    groupId = groupId is int cur ? Math.Min(cur, n) : n;
                }
                else if (key.StartsWith("recent_type_", StringComparison.Ordinal))
                {
                    var suffix = key.AsSpan("recent_type_".Length);
                    if (suffix.SequenceEqual("played")) reason = RecentsReason.Played;
                    else if (suffix.SequenceEqual("saved")) reason = RecentsReason.Saved;
                }
                else if (key.StartsWith("content_type_", StringComparison.Ordinal))
                {
                    contentType = key["content_type_".Length..];   // "music", "podcast", "audiobook", …
                }
            }
        }

        return new RecentsItem(itemId, uri, playedAt, reason, contentType, groupId, hasChildrenGroupId, group);
    }

    // group_metadata rides as a base64 string VALUE under that one key — decode → RecentsGroupMetadata → proto-free info.
    // A malformed/absent payload nulls (one bad header must not sink a 9k-row page), mirroring the metadata-projection
    // "skip one, keep the batch" discipline.
    static RecentsGroupInfo? DecodeGroupMetadata(string? base64)
    {
        if (string.IsNullOrEmpty(base64)) return null;
        try
        {
            var bytes = Convert.FromBase64String(base64);
            var gm = Pl.RecentsGroupMetadata.Parser.ParseFrom(bytes);
            var uris = new List<string>(gm.ChildUri.Count);
            for (int i = 0; i < gm.ChildUri.Count; i++) uris.Add(gm.ChildUri[i]);
            var kind = gm.Kind;
            return new RecentsGroupInfo(
                gm.ChildCount,
                uris,
                KindName: kind is { Name.Length: > 0 } ? kind.Name : null,
                KindCount: kind?.Count ?? 0);
        }
        catch (FormatException) { return null; }                       // not valid base64
        catch (Google.Protobuf.InvalidProtocolBufferException) { return null; }   // valid base64, not a group_metadata
    }
}
