using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Wavee.Backend;
using Wavee.Backend.Playlists;
using Wavee.Backend.Sync;
using Wavee.Core;
using Xunit;
using Pl = Wavee.Protocol.Playlist;

namespace Wavee.Tests;

public class MutationOpRebaseTests
{
    const string Uri = "spotify:playlist:p";
    sealed class ScriptedTransport(Func<string, Resp> respond) : ITransport
    {
        public Task<Resp> Request(Channel channel, string route, ReadOnlyMemory<byte> body, CancellationToken ct = default,
            string? method = null, IReadOnlyDictionary<string, string>? headers = null) => Task.FromResult(respond(route));
        public IObservable<WireEvent> Events(string topicPrefix) => new SimpleSubject<WireEvent>();
        public IObservable<WireRequest> Requests(string identPrefix) => new SimpleSubject<WireRequest>();
        public Task Reply(string requestId, RequestResult result) => Task.CompletedTask;
        public Task<Resp> Publish(string deviceId, string connectionId, ReadOnlyMemory<byte> state, CancellationToken ct = default)
            => Task.FromResult(new Resp(true, [], 200));
    }
    static PlaylistMember M(string name)
    {
        ulong hash = 1469598103934665603UL;
        foreach (char c in name) { hash ^= c; hash *= 1099511628211UL; }
        return new PlaylistMember(hash.ToString("x16"), "spotify:track:" + name, "bob", 1);
    }
    static byte[] Rev(byte tag) { var bytes = new byte[24]; bytes[3] = tag; bytes[23] = tag; return bytes; }
    static PlaylistOp Remove(string name) => new(PlaylistOpKind.Remove, ItemsAsKey: true, Items: [M(name)]);
    static PlaylistOp Add(string name) => new(PlaylistOpKind.Add, AddLast: true, Items: [M(name)]);
    static byte[] Reply(byte[]? revision, Pl.Op[]? ops = null, bool multipleHeads = false, bool requiresResync = false)
    {
        var slc = new Pl.SelectedListContent { MultipleHeads = multipleHeads, ChangesRequireResync = requiresResync };
        if (revision is not null) slc.ResultingRevisions.Add(ByteString.CopyFrom(revision));
        if (ops is not null) { slc.SyncResult = new Pl.Diff(); slc.SyncResult.Ops.AddRange(ops); }
        return slc.ToByteArray();
    }
    static ScriptedTransport Accepted(byte[] body) => new(_ => new Resp(true, body, 200));

    [Fact]
    public async Task Edit_publishes_overlay_then_commits_confirmed_state_on_ack()
    {
        await using var host = new ReplicaTestHost();
        await host.SeedPlaylistAsync(Uri, [M("a"), M("b")], Rev(1));
        await host.Mutations.EditAsync(Uri, [Remove("a")], Rev(1));
        Assert.Equal("spotify:track:b", Assert.Single(host.Replicas.ReadPlaylist(Uri).Members).ItemUri);
        Assert.Equal(2, host.Replicas.ReadConfirmedPlaylist(Uri).Members.Length);
        string? route = null;
        await host.Mutations.Drain(new ScriptedTransport(path => { route = path; return new Resp(true, Reply(Rev(2)), 200); }), host.Context);
        Assert.Equal(0, host.Mutations.Pending);
        Assert.Equal("/playlist/v2/playlist/p/changes", route);
        Assert.Single(host.Replicas.ReadConfirmedPlaylist(Uri).Members);
    }

    [Fact]
    public async Task Terminal_failure_removes_only_overlay_and_preserves_latest_foreign_snapshot()
    {
        await using var host = new ReplicaTestHost();
        await host.SeedPlaylistAsync(Uri, [M("a"), M("b")], Rev(1));
        await host.Mutations.EditAsync(Uri, [Remove("a")], Rev(1));
        await host.SeedPlaylistAsync(Uri, [M("a"), M("b"), M("foreign")], Rev(2));
        await host.Mutations.Drain(new ScriptedTransport(_ => new Resp(false, [], 403)), host.Context);
        Assert.Equal(0, host.Mutations.Pending);
        Assert.Single(host.Mutations.DeadLetter);
        Assert.Equal(new[] { "spotify:track:a", "spotify:track:b", "spotify:track:foreign" },
            host.Replicas.ReadPlaylist(Uri).Members.Select(x => x.ItemUri));
    }

    [Fact]
    public async Task Edits_are_distinct_and_later_keyed_edit_survives_first_ack()
    {
        await using var host = new ReplicaTestHost();
        await host.SeedPlaylistAsync(Uri, [M("a"), M("b"), M("c")], Rev(1));
        await host.Mutations.EditAsync(Uri, [Remove("a")], Rev(1));
        await host.Mutations.EditAsync(Uri, [Remove("b")], Rev(1));
        Assert.Equal(2, host.Mutations.Pending);
        Assert.Equal("spotify:track:c", Assert.Single(host.Replicas.ReadPlaylist(Uri).Members).ItemUri);
        int calls = 0;
        await host.Mutations.Drain(new ScriptedTransport(_ => new Resp(true, Reply(Rev((byte)(++calls + 1))), 200)), host.Context);
        Assert.Equal(2, calls);
        Assert.Equal(0, host.Mutations.Pending);
        Assert.Equal("spotify:track:c", Assert.Single(host.Replicas.ReadConfirmedPlaylist(Uri).Members).ItemUri);
    }

    [Fact]
    public async Task Sync_result_is_applied_before_resulting_head()
    {
        await using var host = new ReplicaTestHost();
        await host.SeedPlaylistAsync(Uri, [M("a"), M("b")], Rev(1));
        await host.Mutations.EditAsync(Uri, [Remove("a")], Rev(1));
        var add = new Pl.Add { AddLast = true };
        add.Items.Add(new Pl.Item { Uri = M("z").ItemUri, Attributes = new Pl.ItemAttributes
            { ItemId = ByteString.CopyFrom(Convert.FromHexString(M("z").ItemId)) } });
        await host.Mutations.Drain(Accepted(Reply(Rev(2), [new Pl.Op { Kind = Pl.Op.Types.Kind.Add, Add = add }])), host.Context);
        Assert.Equal(new[] { "spotify:track:b", "spotify:track:z" },
            host.Replicas.ReadConfirmedPlaylist(Uri).Members.Select(x => x.ItemUri));
        Assert.Equal(Rev(2), host.Replicas.ReadConfirmedPlaylist(Uri).Revision);
        Assert.Equal(0, host.Mutations.Pending);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Resync_flags_preserve_head_and_require_verification(bool multiple, bool resync)
    {
        await using var host = new ReplicaTestHost();
        await host.SeedPlaylistAsync(Uri, [M("a")], Rev(1));
        await host.Mutations.EditAsync(Uri, [Add("q")], Rev(1));
        await host.Mutations.Drain(Accepted(Reply(Rev(9), multipleHeads: multiple, requiresResync: resync)), host.Context);
        Assert.Equal(Rev(1), host.Replicas.ReadConfirmedPlaylist(Uri).Revision);
        var pending = Assert.Single(host.Replicas.Intents);
        Assert.Equal(ReplicaIntentState.AwaitingVerification, pending.State);
        Assert.False(host.Replicas.CanReplay(pending, host.Context.Account));
        Assert.Equal(2, host.Replicas.ReadPlaylist(Uri).Members.Length);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Missing_ack_head_requires_verification_and_is_never_resent(bool emptyBody)
    {
        await using var host = new ReplicaTestHost();
        await host.SeedPlaylistAsync(Uri, [M("a")], Rev(1));
        await host.Mutations.EditAsync(Uri, [Add("q")], Rev(1));
        int calls = 0;
        var transport = new ScriptedTransport(_ => { calls++; return new Resp(true, emptyBody ? [] : Reply(null), 200); });
        await host.Mutations.Drain(transport, host.Context);
        await host.Mutations.Drain(transport, host.Context);
        Assert.Equal(1, calls);
        Assert.Equal(Rev(1), host.Replicas.ReadConfirmedPlaylist(Uri).Revision);
        Assert.Equal(ReplicaIntentState.AwaitingVerification, Assert.Single(host.Replicas.Intents).State);
    }

    [Fact]
    public async Task Revision_only_ack_commits_exact_attempt_once()
    {
        await using var host = new ReplicaTestHost();
        await host.SeedPlaylistAsync(Uri, [M("a"), M("b")], Rev(1));
        await host.Mutations.EditAsync(Uri, [Remove("a")], Rev(1));
        await host.Mutations.Drain(Accepted(Reply(Rev(4))), host.Context);
        Assert.Equal("spotify:track:b", Assert.Single(host.Replicas.ReadConfirmedPlaylist(Uri).Members).ItemUri);
        Assert.Equal(Rev(4), host.Replicas.ReadConfirmedPlaylist(Uri).Revision);
    }

    [Fact]
    public async Task Torn_sync_result_preserves_confirmed_state_and_pending_overlay()
    {
        await using var host = new ReplicaTestHost();
        await host.SeedPlaylistAsync(Uri, [M("a")], Rev(1));
        await host.Mutations.EditAsync(Uri, [Add("q")], Rev(1));
        var torn = new Pl.Op { Kind = Pl.Op.Types.Kind.Rem, Rem = new Pl.Rem { FromIndex = 40, Length = 5 } };
        await host.Mutations.Drain(Accepted(Reply(Rev(7), [torn])), host.Context);
        Assert.Equal(Rev(1), host.Replicas.ReadConfirmedPlaylist(Uri).Revision);
        Assert.Single(host.Replicas.ReadConfirmedPlaylist(Uri).Members);
        Assert.Equal(2, host.Replicas.ReadPlaylist(Uri).Members.Length);
        Assert.Equal(ReplicaIntentState.AwaitingVerification, Assert.Single(host.Replicas.Intents).State);
    }

    [Fact]
    public async Task Conflict_requires_fresh_baseline_before_retry()
    {
        var clock = DateTime.UtcNow;
        await using var host = new ReplicaTestHost(clock: () => clock);
        await host.SeedPlaylistAsync(Uri, [M("a")], Rev(1));
        await host.Mutations.EditAsync(Uri, [Add("q")], Rev(1));
        int calls = 0;
        var transport = new ScriptedTransport(_ => { calls++; return calls == 1 ? new Resp(false, [], 409) : new Resp(true, Reply(Rev(3)), 200); });
        await host.Mutations.Drain(transport, host.Context);
        clock = clock.AddMinutes(2);
        await host.Mutations.Drain(transport, host.Context);
        Assert.Equal(1, calls);
        Assert.Equal(ReplicaBaselineState.NeedsResync, host.Replicas.ReadConfirmedPlaylist(Uri).State);
        await host.SeedPlaylistAsync(Uri, [M("a"), M("foreign")], Rev(2));
        await host.Mutations.Drain(transport, host.Context);
        Assert.Equal(2, calls);
        Assert.Equal(0, host.Mutations.Pending);
        Assert.Equal(3, host.Replicas.ReadConfirmedPlaylist(Uri).Members.Length);
    }
}