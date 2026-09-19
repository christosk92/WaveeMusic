// ── Wavee.Tests/EncodeTests.cs — the library writes' request bodies, byte-exact against the desktop captures ─────────
//
// Gap batch B2 (G-043). `Spotify.Encode` is the port of 0.2.9's `PlaylistWireMapper` build half, `SpotifyIds.NewGroupId`
// and `RootlistOps`' folder builders, and the playlist service keys dedup, echo suppression and conflict detection on the
// exact envelope desktop sends — so the assertion that matters is BYTE EQUALITY with a real desktop body.
//
// THE FIXTURES are 0.2.9's `Fixtures/playlist-wire/*.bin` (two official-desktop Fiddler captures, 2026-08-15), copied
// verbatim: raw HTTP bodies only, zstd-decompressed where the capture was compressed, no header block and no bearer.
// Two layers, as 0.2.9's `WireGoldenTests` had them:
//   (1) REBUILD — the capture is read with the generated protos, mapped to ops HERE (the mapping is test-side on purpose:
//       production never replays wire ops), and re-serialized by the production builder with the capture's own user,
//       timestamp and nonce; the bytes must be identical.
//   (2) BUILT — the rootlist goldens are ALSO produced by the production op BUILDERS from a marker stream (a164 create,
//       b037/b128 rename, a042 add, a281 remove, b049 move), so the builders cannot drift from the envelope either.
// Pure: no scope, no engine, no network.

using System.Buffers.Binary;
using Google.Protobuf;
using Wavee;
using Xunit;
using Pl = Wavee.Protocol.Playlist;

namespace Wavee.Tests;

/// <summary>The playlist-wire golden manifest (0.2.9 <c>WireGoldenFixtures.Golden</c>).</summary>
static class PlaylistWireGolden
{
    public static string Dir => Path.Combine(AppContext.BaseDirectory, "Fixtures", "playlist-wire");

    public static byte[] Bytes(string name) => File.ReadAllBytes(Path.Combine(Dir, name + ".bin"));

    public static Pl.ListChanges Changes(string name) => Pl.ListChanges.Parser.ParseFrom(Bytes(name));

    public static readonly IReadOnlyDictionary<string, int> Sizes = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["a031-create-p1"] = 84,
        ["a042-rootlist-add-p1"] = 135,
        ["a046-add-50-tracks"] = 3034,
        ["a143-keyed-rem-x3"] = 254,
        ["a148-keyed-mov-after-item"] = 290,
        ["a154-keyed-mov-add-first"] = 133,
        ["a164-folder-create"] = 197,
        ["a281-rootlist-index-rem"] = 126,
        ["a498-keyed-mov-add-last"] = 240,
        ["b037-folder-rename"] = 160,
        ["b049-rootlist-mov"] = 85,
        ["b063-update-list-name"] = 106,
        ["b128-folder-rename-outer"] = 166,
        ["a178-create-response"] = 322,
        ["a164-folder-create-response"] = 113,
    };

    /// <summary>The captured wire ops as production op values — the inverse of the builder, test-side only.</summary>
    public static List<PlaylistOp> OpsOf(Pl.Delta delta)
    {
        var ops = new List<PlaylistOp>(delta.Ops.Count);
        foreach (var op in delta.Ops)
        {
            switch (op.Kind)
            {
                case Pl.Op.Types.Kind.Add:
                    ops.Add(new PlaylistOp(PlaylistOpKind.Add, FromIndex: op.Add.FromIndex, AddFirst: op.Add.AddFirst,
                        AddLast: op.Add.AddLast, Items: Members(op.Add.Items)));
                    break;
                case Pl.Op.Types.Kind.Rem:
                    ops.Add(op.Rem.ItemsAsKey
                        ? new PlaylistOp(PlaylistOpKind.Remove, ItemsAsKey: true, Items: Members(op.Rem.Items))
                        : new PlaylistOp(PlaylistOpKind.Remove, FromIndex: op.Rem.FromIndex, Length: op.Rem.Length,
                            Items: op.Rem.Items.Count > 0 ? Members(op.Rem.Items) : null));
                    break;
                case Pl.Op.Types.Kind.Mov when op.Mov.Items.Count > 0:
                    {
                        var anchor = op.Mov.AddFirst ? PlaylistMoveAnchor.First
                                   : op.Mov.AddLast ? PlaylistMoveAnchor.Last : PlaylistMoveAnchor.AfterItem;
                        var after = op.Mov.AddAfterItem;
                        ops.Add(new PlaylistOp(PlaylistOpKind.Move, ItemsAsKey: true, Items: Members(op.Mov.Items), Anchor: anchor,
                            AnchorItemId: after?.Attributes is { HasItemId: true } a ? Convert.ToHexStringLower(a.ItemId.Span) : "",
                            AnchorUri: after?.Uri ?? ""));
                        break;
                    }
                case Pl.Op.Types.Kind.Mov:
                    ops.Add(new PlaylistOp(PlaylistOpKind.Move, FromIndex: op.Mov.FromIndex, Length: op.Mov.Length, ToIndex: op.Mov.ToIndex));
                    break;
                case Pl.Op.Types.Kind.UpdateListAttributes:
                    ops.Add(new PlaylistOp(PlaylistOpKind.UpdateList,
                        Patch: new PlaylistListPatch(Name: op.UpdateListAttributes.NewAttributes.Values.HasName
                            ? op.UpdateListAttributes.NewAttributes.Values.Name : null)));
                    break;
                default:
                    throw new InvalidOperationException("a golden op this suite does not map: " + op.Kind);
            }
        }
        return ops;
    }

    static List<PlaylistMember> Members(Google.Protobuf.Collections.RepeatedField<Pl.Item> items)
    {
        var list = new List<PlaylistMember>(items.Count);
        foreach (var item in items)
        {
            var a = item.Attributes;
            list.Add(new PlaylistMember(item.Uri,
                a is { HasItemId: true } ? Convert.ToHexStringLower(a.ItemId.Span) : "",
                a is { HasTimestamp: true } ? a.Timestamp : 0));
        }
        return list;
    }
}

public class EncodeTests
{
    const string CaptureUser = "31unjfmo3oefvlz36ef3eb6kj5tq";

    // ── the manifest ─────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Fixtures_AllPresent_AndSizesMatchCapture()
    {
        foreach (var (name, size) in PlaylistWireGolden.Sizes)
        {
            Assert.True(File.Exists(Path.Combine(PlaylistWireGolden.Dir, name + ".bin")), "missing golden: " + name);
            Assert.Equal(size, PlaylistWireGolden.Bytes(name).Length);
        }
    }

    // ── (1) rebuild: capture → ops → the production envelope → the same bytes ────────────────────────────────────────

    [Theory]
    [InlineData("a046-add-50-tracks", 2L)]           // ADD add_last, 50 client-minted item_ids
    [InlineData("a143-keyed-rem-x3", 5L)]            // one delta, three keyed REMs
    [InlineData("a148-keyed-mov-after-item", 6L)]    // keyed MOV, add_after_item
    [InlineData("a154-keyed-mov-add-first", 7L)]     // keyed MOV, add_first
    [InlineData("a498-keyed-mov-add-last", 8L)]      // keyed MOV, add_last
    [InlineData("b063-update-list-name", 11L)]       // UPDATE_LIST name
    public void PlaylistRequestGoldens_RebuildByteExact(string name, long nonce)
    {
        var changes = PlaylistWireGolden.Changes(name);
        var delta = Assert.Single(changes.Deltas);
        byte[] rebuilt = Spotify.Encode.PlaylistChanges(changes.BaseRevision.Span, PlaylistWireGolden.OpsOf(delta),
                                                        delta.Info.User, delta.Info.Timestamp, nonce);
        Assert.Equal(PlaylistWireGolden.Bytes(name), rebuilt);
    }

    [Theory]
    [InlineData("a042-rootlist-add-p1", 1L)]         // ADD at index 0, attrs{timestamp, public}
    [InlineData("a281-rootlist-index-rem", 13L)]     // index REM, one bare Item{uri}
    [InlineData("b049-rootlist-mov", 17L)]           // positional MOV{from, len, to}
    [InlineData("a164-folder-create", 2L)]           // two ADDs, one delta, NO public on the markers
    [InlineData("b037-folder-rename", 16L)]          // REM (no items) + ADD with the original timestamp
    [InlineData("b128-folder-rename-outer", 18L)]    // the same rename one level out
    public void RootlistRequestGoldens_RebuildByteExact(string name, long nonce)
    {
        var changes = PlaylistWireGolden.Changes(name);
        var delta = Assert.Single(changes.Deltas);
        byte[] rebuilt = Spotify.Encode.RootlistChanges(changes.BaseRevision.Span, PlaylistWireGolden.OpsOf(delta),
                                                        delta.Info.User, delta.Info.Timestamp, nonce);
        Assert.Equal(PlaylistWireGolden.Bytes(name), rebuilt);
    }

    [Fact]
    public void CreateChanges_GoldenA031_ByteExact()
    {
        var changes = PlaylistWireGolden.Changes("a031-create-p1");
        var delta = Assert.Single(changes.Deltas);
        string name = Assert.Single(delta.Ops).UpdateListAttributes.NewAttributes.Values.Name;

        byte[] rebuilt = Spotify.Encode.CreateChanges(name, delta.Info.User, delta.Info.Timestamp, nonce: 1);

        Assert.Equal(PlaylistWireGolden.Bytes("a031-create-p1"), rebuilt);
        Assert.Equal(Spotify.Encode.CreateBase.ToArray(), Pl.ListChanges.Parser.ParseFrom(rebuilt).BaseRevision.ToByteArray());
    }

    // ── (2) built: the op builders produce the captured ops ───────────────────────────────────────────────────────────

    static byte[] Rootlist(string golden, IReadOnlyList<PlaylistOp> ops, long nonce)
    {
        var changes = PlaylistWireGolden.Changes(golden);
        var delta = Assert.Single(changes.Deltas);
        return Spotify.Encode.RootlistChanges(changes.BaseRevision.Span, ops, delta.Info.User, delta.Info.Timestamp, nonce);
    }

    static long FirstAddStamp(string golden)
        => PlaylistWireGolden.Changes(golden).Deltas[0].Ops.First(o => o.Kind == Pl.Op.Types.Kind.Add).Add.Items[0].Attributes.Timestamp;

    [Fact]
    public void CreateFolder_BuildsTwoAddsOneDelta_GoldenA164()
    {
        var ops = Spotify.Encode.CreateFolder([], "edb339e10aebcf38", "New Folder", insertAt: 0,
                                              nowMs: FirstAddStamp("a164-folder-create"));

        Assert.Equal(PlaylistWireGolden.Bytes("a164-folder-create"), Rootlist("a164-folder-create", ops, 2));
        Assert.Equal("spotify:start-group:edb339e10aebcf38:New+Folder", ops[0].Items![0].Uri);
        Assert.Equal("spotify:end-group:edb339e10aebcf38", ops[1].Items![0].Uri);
        Assert.Equal((0, 1), (ops[0].FromIndex, ops[1].FromIndex));
    }

    [Fact]
    public void RenameFolder_ResendsTheOriginalStamp_GoldenB037()
    {
        long original = FirstAddStamp("b037-folder-rename");
        var entries = Spotify.Encode.EntriesFromUris(
            ["spotify:playlist:a", "spotify:playlist:b", "spotify:start-group:edb339e10aebcf38:New+Folder",
             "spotify:end-group:edb339e10aebcf38"],
            [0, 0, original, original]);

        var ops = Spotify.Encode.RenameFolder(entries, "edb339e10aebcf38", "named folder update", nowMs: 1)!;

        Assert.Equal(PlaylistWireGolden.Bytes("b037-folder-rename"), Rootlist("b037-folder-rename", ops, 16));
        Assert.Equal(PlaylistOpKind.Remove, ops[0].Kind);
        Assert.Equal((2, 1), (ops[0].FromIndex, ops[0].Length));
        Assert.Null(ops[0].Items);                                  // a rename REM names nothing
        Assert.Equal(original, ops[1].Items![0].AddedAtMs);          // NOT "now"
    }

    [Fact]
    public void RenameFolder_OneLevelOut_GoldenB128()
    {
        long original = FirstAddStamp("b128-folder-rename-outer");
        var entries = Spotify.Encode.EntriesFromUris(
            ["spotify:start-group:3dd9e795c88ae3e4:root+folder", "spotify:playlist:child", "spotify:end-group:3dd9e795c88ae3e4"],
            [original, 0, original]);

        var ops = Spotify.Encode.RenameFolder(entries, "3dd9e795c88ae3e4", "root folder updated name", nowMs: 1)!;

        Assert.Equal(PlaylistWireGolden.Bytes("b128-folder-rename-outer"), Rootlist("b128-folder-rename-outer", ops, 18));
    }

    [Fact]
    public void RootlistAdd_IsOneIndexAddWithPublic_GoldenA042()
    {
        var op = Spotify.Encode.RootlistAdd("spotify:playlist:6EVbQZBiAg9zHzMjChxvRd", 0, FirstAddStamp("a042-rootlist-add-p1"));
        Assert.Equal(PlaylistWireGolden.Bytes("a042-rootlist-add-p1"), Rootlist("a042-rootlist-add-p1", [op], 1));
    }

    [Fact]
    public void RootlistRemove_IsAnIndexRemNamingTheBareRow_GoldenA281()
    {
        var entries = Spotify.Encode.EntriesFromUris(["spotify:playlist:4vkIrispQ6gcMNIojGPd0L", "spotify:playlist:other"]);
        var op = Spotify.Encode.RootlistRemove(entries, "spotify:playlist:4vkIrispQ6gcMNIojGPd0L")!.Value;
        Assert.Equal(PlaylistWireGolden.Bytes("a281-rootlist-index-rem"), Rootlist("a281-rootlist-index-rem", [op], 13));
        Assert.Null(Spotify.Encode.RootlistRemove(entries, "spotify:playlist:gone"));
    }

    [Fact]
    public void TryBuildMove_IsAPositionalMov_GoldenB049()
    {
        var entries = Spotify.Encode.EntriesFromUris(
            ["spotify:playlist:p0", "spotify:playlist:p1", "spotify:playlist:p2", "spotify:playlist:p3"]);
        Assert.True(Spotify.Encode.TryBuildMove(entries, new RootlistItemRef("spotify:playlist:p0", false),
            new RootlistItemRef("spotify:playlist:p2", false), RootlistDropPlacement.After, out var op, out var reason));
        Assert.Equal(RootlistMoveCheck.Ok, reason);
        Assert.Equal(PlaylistWireGolden.Bytes("b049-rootlist-mov"), Rootlist("b049-rootlist-mov", [op], 17));
    }

    [Fact]
    public void AppendTracks_IsOneAddLastWithMintedIds_GoldenA046()
    {
        var changes = PlaylistWireGolden.Changes("a046-add-50-tracks");
        var delta = Assert.Single(changes.Deltas);
        var captured = PlaylistWireGolden.OpsOf(delta)[0].Items!;
        var op = Spotify.Encode.AppendTracks(captured);
        byte[] built = Spotify.Encode.PlaylistChanges(changes.BaseRevision.Span, [op], delta.Info.User, delta.Info.Timestamp, 2);
        Assert.Equal(PlaylistWireGolden.Bytes("a046-add-50-tracks"), built);
    }

    // ── the envelope's own facts ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Envelope_CarriesBothFlags_OneNonce_AndUserPlusTimestampOnly()
    {
        byte[] body = Spotify.Encode.RootlistChanges([1, 2, 3, 4, 5], [Spotify.Encode.RootlistAdd("spotify:playlist:x", 0, 5)],
                                                     CaptureUser, 1234, 99);
        var c = Pl.ListChanges.Parser.ParseFrom(body);
        Assert.True(c.WantResultingRevisions && c.WantSyncResult);
        Assert.Equal(new[] { 99L }, c.Nonces.ToArray());
        var d = Assert.Single(c.Deltas);
        Assert.False(d.HasBaseVersion);
        Assert.Equal(CaptureUser, d.Info.User);
        Assert.Equal(1234, d.Info.Timestamp);
        Assert.False(d.Info.HasAdmin || d.Info.HasUndo || d.Info.HasMerge);
    }

    [Fact]
    public void KeyedMove_WithoutAnAnchor_IsRefusedRatherThanSent()
    {
        var op = new PlaylistOp(PlaylistOpKind.Move, ItemsAsKey: true, Items: [new PlaylistMember("spotify:track:x", "0011223344556677")]);
        Assert.Throws<InvalidOperationException>(() => Spotify.Encode.PlaylistChanges([], [op], CaptureUser, 1, 1));
    }

    // ── ids, names, revisions ────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void NewGroupId_Is16LowercaseHex_AndDiffersPerCall()
    {
        string a = Spotify.Encode.NewGroupId(), b = Spotify.Encode.NewGroupId();
        Assert.Equal(16, a.Length);
        Assert.All(a, c => Assert.True(c is >= '0' and <= '9' or >= 'a' and <= 'f'));
        Assert.NotEqual(a, b);
        Assert.InRange(Spotify.Encode.NewNonce(), 1, int.MaxValue);
    }

    [Fact]
    public void FolderMarkers_EncodeSpaceAsPlus_AndDecodeLikeTheDecoder()
    {
        Assert.Equal("spotify:start-group:g:New+Folder", Spotify.Encode.StartGroupUri("g", "New Folder"));
        Assert.Equal("spotify:start-group:g:a%2Fb", Spotify.Encode.StartGroupUri("g", "a/b"));
        Assert.Equal("spotify:end-group:g", Spotify.Encode.EndGroupUri("g"));
        Assert.Equal("A+B C", Spotify.Encode.FolderNameOf("spotify:start-group:g:A%2BB+C"));
        Assert.Equal("café mix", Spotify.Encode.FolderNameOf(Spotify.Encode.StartGroupUri("g", "café mix")));
        Assert.Equal("g", Spotify.Encode.GroupIdOf("spotify:start-group:g:Name"));
        Assert.Equal("g", Spotify.Encode.GroupIdOf("spotify:end-group:g"));
        Assert.Equal("bare", Spotify.Encode.GroupIdOf("bare"));
    }

    [Fact]
    public void RevisionText_RoundTripsThroughTheApisSpelling()
    {
        byte[] head = new byte[24];
        BinaryPrimitives.WriteUInt32BigEndian(head, 1234);
        for (int i = 4; i < 24; i++) head[i] = (byte)(i * 7);
        Span<char> text = stackalloc char[64];
        int written = Spotify.Api.FormatRevision(head, text);

        Span<byte> back = stackalloc byte[Spotify.Encode.MaxRevisionBytes];
        int length = Spotify.Encode.RevisionBytes(text[..written], back);

        Assert.Equal(head, back[..length].ToArray());
        Assert.Equal(0, Spotify.Encode.RevisionBytes("not a revision", back));
        Assert.Equal(0, Spotify.Encode.RevisionBytes("12,abc", back));           // odd hex
    }

    [Fact]
    public void ResultingRevision_PrefersTheTopLevelHead_ElseTheFirstResulting()
    {
        byte[] reply = PlaylistWireGolden.Bytes("a164-folder-create-response");
        var parsed = Pl.SelectedListContent.Parser.ParseFrom(reply);
        Span<byte> into = stackalloc byte[Spotify.Encode.MaxRevisionBytes];
        int n = Spotify.Encode.ResultingRevision(reply, into);
        Assert.Equal(parsed.Revision.ToByteArray(), into[..n].ToArray());

        var onlyResulting = new Pl.SelectedListContent { ResultingRevisions = { ByteString.CopyFrom([9, 9, 9, 9, 1]) } }.ToByteArray();
        n = Spotify.Encode.ResultingRevision(onlyResulting, into);
        Assert.Equal(new byte[] { 9, 9, 9, 9, 1 }, into[..n].ToArray());
        Assert.Equal(0, Spotify.Encode.ResultingRevision([], into));
    }

    [Fact]
    public void NextPlaylistName_TakesTheSmallestFreeNumber_CaseInsensitively()
    {
        Assert.Equal("My Playlist #1", Spotify.Encode.NextPlaylistName([], "My Playlist"));
        Assert.Equal("My Playlist #3", Spotify.Encode.NextPlaylistName(["my playlist #1", "My Playlist #2", "Other"], "My Playlist"));
        Assert.Equal("Playlist #1", Spotify.Encode.NextPlaylistName([], " "));
    }

    // ── folder delete and the local apply (no capture exists; pinned behaviourally, as 0.2.9 did) ────────────────────

    [Fact]
    public void DeleteFolder_RemovesTheEndMarkerFirst_AndTheChildrenStay()
    {
        var entries = Spotify.Encode.EntriesFromUris(
            ["spotify:playlist:before", "spotify:start-group:g1:Trips", "spotify:playlist:inside", "spotify:end-group:g1",
             "spotify:playlist:after"]);

        var ops = Spotify.Encode.DeleteFolder(entries, "g1")!;

        Assert.Equal((3, 1), (ops[0].FromIndex, ops[0].Length));      // END first — removing the start would shift it
        Assert.Equal((1, 1), (ops[1].FromIndex, ops[1].Length));
        var after = Spotify.Encode.ApplyLocally(entries, ops);
        Assert.Equal(new[] { "spotify:playlist:before", "spotify:playlist:inside", "spotify:playlist:after" },
                     after.Select(e => e.Uri).ToArray());
        Assert.All(after, e => Assert.Equal(0, e.Depth));
    }

    [Fact]
    public void DeleteFolder_Nested_KeepsTheInnerFolderOneLevelUp()
    {
        var entries = Spotify.Encode.EntriesFromUris(
            ["spotify:start-group:outer:Outer", "spotify:start-group:inner:Inner", "spotify:playlist:deep",
             "spotify:end-group:inner", "spotify:end-group:outer"]);

        var after = Spotify.Encode.ApplyLocally(entries, Spotify.Encode.DeleteFolder(entries, "outer")!);

        Assert.Equal(3, after.Count);
        Assert.Equal("inner", Spotify.Encode.GroupIdOf(after[0].Uri));
        Assert.Equal((0, 1), (after[0].Depth, after[1].Depth));
    }

    [Fact]
    public void Builders_AnswerNull_WhenTheFolderIsGone()
    {
        var entries = Spotify.Encode.EntriesFromUris(["spotify:playlist:a"]);
        Assert.Null(Spotify.Encode.RenameFolder(entries, "nope", "x", 1));
        Assert.Null(Spotify.Encode.DeleteFolder(entries, "nope"));
        Assert.Equal(-1, Spotify.Encode.PlacementIndex(entries, "nope"));
        Assert.Equal(0, Spotify.Encode.PlacementIndex(entries, null));
    }

    [Fact]
    public void CreateFolder_InsideAFolder_LandsAsItsFirstChild()
    {
        var entries = Spotify.Encode.EntriesFromUris(["spotify:start-group:outer:Outer", "spotify:end-group:outer"]);
        int at = Spotify.Encode.PlacementIndex(entries, "outer");
        var after = Spotify.Encode.ApplyLocally(entries, Spotify.Encode.CreateFolder(entries, "new", "Trips", at, 5));

        Assert.Equal(new[] { 1, 1, 2, 2 }, after.Select(e => e.Kind).ToArray());
        Assert.Equal("new", Spotify.Encode.GroupIdOf(after[1].Uri));
        Assert.Equal(1, after[1].Depth);
        Assert.Equal("Trips", after[1].GroupName);
    }

    [Fact]
    public void ApplyLocally_RefusesAKeyedOp_AndAnIndexOutOfRange()
    {
        var entries = Spotify.Encode.EntriesFromUris(["spotify:playlist:a"]);
        Assert.Throws<ArgumentOutOfRangeException>(() => Spotify.Encode.ApplyLocally(entries,
            [new PlaylistOp(PlaylistOpKind.Remove, ItemsAsKey: true, Items: [new PlaylistMember("spotify:playlist:a")])]));
        Assert.Throws<ArgumentOutOfRangeException>(() => Spotify.Encode.ApplyLocally(entries,
            [new PlaylistOp(PlaylistOpKind.Remove, FromIndex: 3, Length: 1)]));
    }

    [Fact]
    public void TryBuildMoves_OneDeltaInOrder_AndANetIdentityIsANoOp()
    {
        var entries = Spotify.Encode.EntriesFromUris(
            ["spotify:playlist:a", "spotify:playlist:b", "spotify:playlist:c", "spotify:playlist:d"]);
        RootlistItemRef P(string s) => new("spotify:playlist:" + s, false);

        Assert.True(Spotify.Encode.TryBuildMoves(entries,
            RootlistBatchOrder.For([P("a"), P("b")], P("d"), RootlistDropPlacement.After), out var ops, out var reason));
        Assert.Equal(RootlistMoveCheck.Ok, reason);
        Assert.Equal(new[] { "c", "d", "a", "b" },
                     Spotify.Encode.ApplyLocally(entries, ops).Select(e => e.Uri["spotify:playlist:".Length..]).ToArray());

        Assert.False(Spotify.Encode.TryBuildMoves(entries,
            RootlistBatchOrder.For([P("b"), P("c")], P("d"), RootlistDropPlacement.Before), out var none, out reason));
        Assert.Equal(RootlistMoveCheck.NoOp, reason);
        Assert.Empty(none);
    }
}
