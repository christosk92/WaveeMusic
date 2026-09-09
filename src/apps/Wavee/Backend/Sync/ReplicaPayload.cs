using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Wavee.Backend.Playlists;
using Wavee.Core;

namespace Wavee.Backend.Sync;

/// <summary>Own queued payloads and account for their retained bytes before admission to the shared commit queue.</summary>
static class ReplicaPayload
{
    public static ImmutableArray<PlaylistOp> Freeze(IReadOnlyList<PlaylistOp>? ops)
        => ops is null ? [] : ops.Select(x => x with
        {
            Items = x.Items?.ToImmutableArray(),
            ListPatch = x.ListPatch is { } patch ? patch with { PictureBytes = patch.PictureBytes?.ToArray() } : null,
        }).ToImmutableArray();

    static int Text(string? text) => text is null ? 0 : checked(24 + text.Length * sizeof(char));
    public static int Members(IEnumerable<PlaylistMember> rows)
        => rows.Sum(x => checked(80 + Text(x.ItemId) + Text(x.ItemUri) + Text(x.AddedBy)));
    public static int Header(Playlist? header)
        => header is null ? 0 : checked(512 + Text(header.Uri) + Text(header.Name) + Text(header.Description) + Text(header.OwnerName));
    public static int Ops(IReadOnlyList<PlaylistOp>? ops)
        => ops is null ? 0 : ops.Sum(x => checked(128 + Members(x.Items ?? [])
            + Text(x.ListPatch?.Name) + Text(x.ListPatch?.Description) + (x.ListPatch?.PictureBytes?.Length ?? 0)));
    public static int Intent(OutboxOp intent)
        => checked(160 + Text(intent.EntityKey) + Text(intent.SetId) + Text(intent.OwnerAccount) + Ops(intent.Ops));
    public static int Playlist(PlaylistReadResult read)
        => checked(128 + Text(read.Uri) + Members(read.Members) + Ops(read.Ops) + Header(read.Header));
    public static int Rootlist(RootlistReadResult read)
        => checked(128 + read.Entries.Sum(x => 64 + Text(x.Uri) + Text(x.GroupName)));
    public static int Collection(CollectionReadResult read)
        => checked(128 + Text(read.WireSet) + Text(read.Token) + read.Items.Sum(x => 48 + Text(x.Uri)));
}
