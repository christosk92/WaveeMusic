using System;
using Google.Protobuf;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend;
using Wavee.Backend.Playlists;
using Wavee.Core;
using Xunit;
using Pl = Wavee.Protocol.Playlist;

namespace Wavee.Tests;

// P2 item 6 — the permission dialect is PROTOBUF, not JSON, and not revision-chained. GET …/permission/base answers a
// Permission{revision, level}; the set is a POST to …/permission/base/level whose entire body is the two-byte
// SetPermissionLevelRequest. The client holds no state: the revision lives on the store header.
public sealed class PlaylistPermissionTests
{
    const string Uri = "spotify:playlist:6QbD3n4hCF6uP8jqyiDsS5";

    /// <summary>A scripted transport: one queued response per call, and every route/method/body recorded.</summary>
    sealed class ScriptedTransport(params Resp[] responses) : ITransport
    {
        readonly Queue<Resp> _queue = new(responses);
        public readonly List<string> Routes = new();
        public readonly List<string> Methods = new();
        public readonly List<byte[]> Bodies = new();
        public readonly List<string> ContentTypes = new();
        public readonly List<string> Accepts = new();

        public Task<Resp> Request(Channel ch, string route, ReadOnlyMemory<byte> body, CancellationToken ct = default,
            string? method = null, IReadOnlyDictionary<string, string>? headers = null)
        {
            Routes.Add(route);
            Methods.Add(method ?? "");
            Bodies.Add(body.ToArray());
            ContentTypes.Add(headers is not null && headers.TryGetValue("Content-Type", out var ct2) ? ct2 : "");
            Accepts.Add(headers is not null && headers.TryGetValue("Accept", out var a) ? a : "");
            return Task.FromResult(_queue.Count > 0 ? _queue.Dequeue() : new Resp(false, Array.Empty<byte>(), 500));
        }

        public IObservable<WireEvent> Events(string topicPrefix) => new SimpleSubject<WireEvent>();
        public IObservable<WireRequest> Requests(string identPrefix) => new SimpleSubject<WireRequest>();
        public Task Reply(string requestId, RequestResult result) => Task.CompletedTask;
        public Task<Resp> Publish(string deviceId, string connectionId, ReadOnlyMemory<byte> putState, CancellationToken ct = default)
            => Task.FromResult(new Resp(true, Array.Empty<byte>(), 200));
    }

    static byte[] PermissionBytes(Pl.PermissionLevel level, string revisionHex)
        => new Pl.Permission
        {
            PermissionLevel = level,
            Revision = Google.Protobuf.ByteString.CopyFrom(Convert.FromHexString(revisionHex)),
        }.ToByteArray();

    static byte[] SetResponseBytes(Pl.PermissionLevel level, string revisionHex)
        => new Pl.SetPermissionResponse
        {
            ResultingPermission = new Pl.Permission
            {
                PermissionLevel = level,
                Revision = Google.Protobuf.ByteString.CopyFrom(Convert.FromHexString(revisionHex)),
            },
        }.ToByteArray();

    // The 12-byte body captured off the wire right after the BLOCKED set (Fixtures/playlist-wire/perm-get-blocked.bin).
    [Fact]
    public void ParsePermission_ProtoBytes()
    {
        var perm = PlaylistPermissionClient.ParsePermission(Golden.Bytes("perm-get-blocked"));
        Assert.NotNull(perm);
        Assert.Equal(PlaylistPermissionLevel.Blocked, perm!.Value.Level);
        Assert.False(perm.Value.IsPublic);
        Assert.Equal("3b907c0d29c940a3", perm.Value.Revision);   // 8 opaque bytes as hex — NEVER a playlist4 revision

        var viewer = PlaylistPermissionClient.ParsePermission(PermissionBytes(Pl.PermissionLevel.Viewer, "0011223344556677"));
        Assert.Equal(PlaylistPermissionLevel.Viewer, viewer!.Value.Level);
        Assert.True(viewer.Value.IsPublic);
    }

    // b078 / b108: the whole request body is two bytes. Anything longer means we regressed to the JSON dialect.
    [Fact]
    public void BuildSetLevel_BlockedIsByte0801()
    {
        Assert.Equal(new byte[] { 0x08, 0x01 }, PlaylistPermissionClient.BuildSetLevel(PlaylistPermissionLevel.Blocked));
        Assert.Equal(new byte[] { 0x08, 0x02 }, PlaylistPermissionClient.BuildSetLevel(PlaylistPermissionLevel.Viewer));
        Assert.Equal(new byte[] { 0x08, 0x03 }, PlaylistPermissionClient.BuildSetLevel(PlaylistPermissionLevel.Contributor));
        Assert.Equal(Golden.Bytes("b078-perm-set-blocked"), PlaylistPermissionClient.BuildSetLevel(PlaylistPermissionLevel.Blocked));
        Assert.Equal(Golden.Bytes("b108-perm-set-viewer"), PlaylistPermissionClient.BuildSetLevel(PlaylistPermissionLevel.Viewer));
    }

    // A freshly created playlist has no permission row yet — desktop gets the same 404 right after its create.
    [Fact]
    public async Task Get_404_ReturnsNull()
    {
        var t = new ScriptedTransport(new Resp(false, Array.Empty<byte>(), 404));
        var client = new PlaylistPermissionClient(t);

        Assert.Null(await client.GetBasePermissionAsync(Uri));
        Assert.Equal("/playlist-permission/v1/playlist/6QbD3n4hCF6uP8jqyiDsS5/permission/base", Assert.Single(t.Routes));
        Assert.Equal("GET", Assert.Single(t.Methods));
        Assert.Equal("application/protobuf", Assert.Single(t.Accepts));
    }

    [Fact]
    public async Task Set_PostsTheProtoBody_AndReadsResultingPermission()
    {
        var t = new ScriptedTransport(new Resp(true, SetResponseBytes(Pl.PermissionLevel.Blocked, "3b907c0d29c940a3"), 200));
        var result = await new PlaylistPermissionClient(t).SetBasePermissionAsync(Uri, PlaylistPermissionLevel.Blocked);

        Assert.Equal("/playlist-permission/v1/playlist/6QbD3n4hCF6uP8jqyiDsS5/permission/base/level", Assert.Single(t.Routes));
        Assert.Equal("POST", Assert.Single(t.Methods));
        Assert.Equal("application/protobuf", Assert.Single(t.ContentTypes));
        Assert.Equal(new byte[] { 0x08, 0x01 }, Assert.Single(t.Bodies));
        Assert.Equal(PlaylistPermissionLevel.Blocked, result.Level);
        Assert.Equal("3b907c0d29c940a3", result.Revision);
    }

    // 409 = someone else changed the sharing state under us. One refresh, one retry, then it is a Conflict the UI can
    // word — never an unbounded retry loop and never a silent success.
    [Fact]
    public async Task Set_409_RefetchesAndRetriesOnce()
    {
        var t = new ScriptedTransport(
            new Resp(false, Array.Empty<byte>(), 409),
            new Resp(true, PermissionBytes(Pl.PermissionLevel.Viewer, "aabbccddeeff0011"), 200),
            new Resp(true, SetResponseBytes(Pl.PermissionLevel.Blocked, "1122334455667788"), 200));
        var result = await new PlaylistPermissionClient(t).SetBasePermissionAsync(Uri, PlaylistPermissionLevel.Blocked);

        Assert.Equal(new[] { "POST", "GET", "POST" }, t.Methods.ToArray());
        Assert.Equal(PlaylistPermissionLevel.Blocked, result.Level);
        Assert.Equal("1122334455667788", result.Revision);
    }

    [Fact]
    public async Task Set_409Twice_IsAConflict()
    {
        var t = new ScriptedTransport(
            new Resp(false, Array.Empty<byte>(), 409),
            new Resp(true, PermissionBytes(Pl.PermissionLevel.Viewer, "aabbccddeeff0011"), 200),
            new Resp(false, Array.Empty<byte>(), 409));

        var ex = await Assert.ThrowsAsync<PlaylistMutationException>(
            () => new PlaylistPermissionClient(t).SetBasePermissionAsync(Uri, PlaylistPermissionLevel.Blocked));
        Assert.Equal(PlaylistMutationFailure.Conflict, ex.Kind);
    }

    [Fact]
    public async Task Set_403_Forbidden()
    {
        var t = new ScriptedTransport(new Resp(false, Array.Empty<byte>(), 403));
        var ex = await Assert.ThrowsAsync<PlaylistMutationException>(
            () => new PlaylistPermissionClient(t).SetBasePermissionAsync(Uri, PlaylistPermissionLevel.Viewer));
        Assert.Equal(PlaylistMutationFailure.Forbidden, ex.Kind);
        Assert.Single(t.Routes);                                  // one attempt, no retry
    }

    [Fact]
    public void ToWireOp_UpdateItem_EmitsPublicFalse()
    {
        var op = new PlaylistOp(PlaylistOpKind.UpdateItem, FromIndex: 3, ItemPublic: false);
        var bytes = PlaylistWireMapper.BuildChanges(new byte[] { 1 }, new[] { op }, "alice", 1_700_000_000_000);
        var changes = Pl.ListChanges.Parser.ParseFrom(bytes);
        var wire = changes.Deltas[0].Ops[0];
        Assert.Equal(Pl.Op.Types.Kind.UpdateItemAttributes, wire.Kind);
        Assert.Equal(3, (int)wire.UpdateItemAttributes.Index);
        Assert.True(wire.UpdateItemAttributes.NewAttributes.Values.HasPublic);
        Assert.False(wire.UpdateItemAttributes.NewAttributes.Values.Public);
    }

    [Fact]
    public void FindPlaylistIndex_SkipsFolders()
    {
        var entries = new[]
        {
            new RootlistEntry(0, 1, "spotify:start-group:g:F", "F", 0),
            new RootlistEntry(1, 0, "spotify:playlist:a", null, 1),
            new RootlistEntry(2, 0, "spotify:playlist:b", null, 1),
        };
        Assert.Equal(2, RootlistOps.FindPlaylistIndex(entries, "spotify:playlist:b"));
    }

    [Fact]
    public void BuildRootlistChanges_RemAtResolvedIndex()
    {
        var ops = new[] { new PlaylistOp(PlaylistOpKind.Remove, FromIndex: 2, Length: 1) };
        var bytes = PlaylistWireMapper.BuildRootlistChanges(new byte[] { 9 }, ops, "alice", 1_700_000_000_000);
        var changes = Pl.ListChanges.Parser.ParseFrom(bytes);
        var rem = Assert.Single(changes.Deltas[0].Ops);
        Assert.Equal(Pl.Op.Types.Kind.Rem, rem.Kind);
        Assert.Equal(2, (int)rem.Rem.FromIndex);
        Assert.Equal(1, (int)rem.Rem.Length);
    }
}
