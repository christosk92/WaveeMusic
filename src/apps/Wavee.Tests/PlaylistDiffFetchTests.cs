using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Wavee.Backend;
using Wavee.Backend.Playlists;
using Wavee.Backend.Spotify;
using Wavee.Backend.Sync;
using Wavee.Core;
using Xunit;
using Pl = Wavee.Protocol.Playlist;

namespace Wavee.Tests;

public class PlaylistDiffFetchTests
{
    const string Uri = "spotify:playlist:x";
    static CancellationToken Ct => TestContext.Current.CancellationToken;
    static byte[] Rev(int counter, params byte[] hash)
    {
        var bytes = new byte[24];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(bytes, counter);
        hash.CopyTo(bytes, 4);
        return bytes;
    }
    static HttpResp Ok(byte[] body) => new(200, new Dictionary<string, string>(), body);
    static HttpResp Status(int status) => new(status, new Dictionary<string, string>(), Array.Empty<byte>());
    static byte[] FullSlc(byte[] rev, params string[] uris)
    {
        var slc = new Pl.SelectedListContent { Revision = ByteString.CopyFrom(rev), Contents = new Pl.ListItems() };
        foreach (var uri in uris) slc.Contents.Items.Add(new Pl.Item { Uri = uri });
        return slc.ToByteArray();
    }
    static byte[] DiffSlc(byte[] from, byte[] to, params Pl.Op[] ops)
    {
        var diff = new Pl.Diff { FromRevision = ByteString.CopyFrom(from), ToRevision = ByteString.CopyFrom(to) };
        diff.Ops.AddRange(ops);
        return new Pl.SelectedListContent { Diff = diff }.ToByteArray();
    }
    static Pl.Op AddLast(string uri)
    {
        var add = new Pl.Add { AddLast = true };
        add.Items.Add(new Pl.Item { Uri = uri });
        return new Pl.Op { Kind = Pl.Op.Types.Kind.Add, Add = add };
    }
    static PlaylistReplicaBaseline Baseline(byte[]? rev, params string[] uris)
        => new(Uri, uris.Select((uri, i) => new PlaylistMember("id" + i, uri, "seed", 1_700_000_000_000L)).ToImmutableArray(), rev, null,
            rev is null ? ReplicaBaselineState.Missing : ReplicaBaselineState.Verified);
    static (PlaylistFetcher Fetcher, List<HttpReq> Requests) Rig(Func<HttpReq, int, HttpResp> respond)
    {
        var requests = new List<HttpReq>();
        var http = new FakeExchange((request, n) => { requests.Add(request); return respond(request, n); });
        return (new PlaylistFetcher(http, () => "https://x", () => "bob"), requests);
    }

    [Fact]
    public void FormatRevision_CounterCommaLowerHex()
    {
        Assert.Equal("123,ab12cd", PlaylistFetcher.FormatRevision([0, 0, 0, 123, 0xAB, 0x12, 0xCD]));
        Assert.Contains("%2C", System.Uri.EscapeDataString(PlaylistFetcher.FormatRevision(Rev(1, 0xFF))));
    }

    [Fact]
    public async Task Diff_returns_observation_without_mutating_baseline()
    {
        var from = Rev(1, 0xAA); var to = Rev(2, 0xBB);
        var baseline = Baseline(from, "spotify:track:t1", "spotify:track:t2");
        var (fetcher, requests) = Rig((_, _) => Ok(DiffSlc(from, to, AddLast("spotify:track:new"))));
        var read = await fetcher.FetchPlaylistDiffAsync(Uri, baseline, Ct);
        Assert.Equal(PlaylistReadKind.Delta, read.Kind);
        var url = Assert.Single(requests).Url;
        Assert.Contains("/diff?revision=1%2Caa", url);
        Assert.Contains("&handlesContent=", url);
        Assert.Contains("hint_revision=1%2Caa", url);
        Assert.Equal(2, baseline.Members.Length);
        Assert.Equal(from, baseline.Revision);
        var adopted = PlaylistReplicaReducer.ApplyServer(baseline, read);
        Assert.Equal(3, adopted.Members.Length);
        Assert.Equal("spotify:track:new", adopted.Members[2].ItemUri);
        Assert.Equal(to, adopted.Revision);
    }

    [Fact]
    public async Task Diff_attribute_ops_do_not_wait_for_another_header_request()
    {
        var from = Rev(1); var to = Rev(2);
        var patch = new Pl.Op { Kind = Pl.Op.Types.Kind.UpdateListAttributes,
            UpdateListAttributes = new Pl.UpdateListAttributes { NewAttributes = new Pl.ListAttributesPartialState { Values = new Pl.ListAttributes { Name = "Renamed" } } } };
        var (fetcher, requests) = Rig((_, _) => Ok(DiffSlc(from, to, patch)));
        var read = await fetcher.FetchPlaylistDiffAsync(Uri, Baseline(from, "spotify:track:a"), Ct);
        Assert.Equal(PlaylistReadKind.Delta, read.Kind);
        Assert.Equal("Renamed", Assert.Single(read.Ops).ListPatch?.Name);
        Assert.Single(requests);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Diff_unchanged_keeps_known_empty_membership(bool notModified)
    {
        var baseline = Baseline(Rev(7));
        var (fetcher, requests) = Rig((_, _) => notModified ? Status(304)
            : Ok(new Pl.SelectedListContent { UpToDate = true }.ToByteArray()));
        var read = await fetcher.FetchPlaylistDiffAsync(Uri, baseline, Ct);
        Assert.Equal(PlaylistReadKind.Unchanged, read.Kind);
        Assert.Equal(baseline.Revision, read.ExpectedBase);
        Assert.Empty(PlaylistReplicaReducer.ApplyServer(baseline, read).Members);
        Assert.Contains("/diff?", Assert.Single(requests).Url);
    }

    [Theory]
    [InlineData(509)]
    [InlineData(500)]
    public async Task Diff_rejected_revision_reads_full_snapshot(int status)
    {
        var fresh = Rev(9);
        var (fetcher, requests) = Rig((request, _) => request.Url.Contains("/diff?") ? Status(status)
            : Ok(FullSlc(fresh, "spotify:track:f1", "spotify:track:f2")));
        var read = await fetcher.FetchPlaylistDiffAsync(Uri, Baseline(Rev(1), "spotify:track:old"), Ct);
        Assert.Equal(PlaylistReadKind.Snapshot, read.Kind);
        Assert.Equal(2, requests.Count);
        Assert.Equal(2, read.Members.Length);
        Assert.Equal(fresh, read.Revision);
    }

    [Fact]
    public async Task Diff_torn_apply_reads_full_snapshot()
    {
        var from = Rev(1); var fresh = Rev(3);
        var torn = new Pl.Op { Kind = Pl.Op.Types.Kind.Rem, Rem = new Pl.Rem { FromIndex = 5, Length = 2 } };
        var (fetcher, requests) = Rig((request, _) => request.Url.Contains("/diff?") ? Ok(DiffSlc(from, fresh, torn))
            : Ok(FullSlc(fresh, "spotify:track:f1")));
        var read = await fetcher.FetchPlaylistDiffAsync(Uri, Baseline(from, "spotify:track:t1"), Ct);
        Assert.Equal(PlaylistReadKind.Snapshot, read.Kind);
        Assert.Equal(2, requests.Count);
        Assert.Equal("spotify:track:f1", Assert.Single(read.Members).ItemUri);
    }

    [Fact]
    public async Task Missing_baseline_goes_straight_to_full_fetch()
    {
        var (fetcher, requests) = Rig((_, _) => Ok(FullSlc(Rev(1), "spotify:track:t1")));
        var read = await fetcher.FetchPlaylistDiffAsync(Uri, Baseline(null), Ct);
        Assert.Equal(PlaylistReadKind.Snapshot, read.Kind);
        Assert.DoesNotContain("/diff?", Assert.Single(requests).Url);
        Assert.Single(read.Members);
    }

    [Fact]
    public async Task Zstd_diff_body_decodes()
    {
        var from = Rev(1); var to = Rev(2);
        using var compressor = new ZstdSharp.Compressor();
        var bytes = compressor.Wrap(DiffSlc(from, to, AddLast("spotify:track:new"))).ToArray();
        var (fetcher, _) = Rig((_, _) => Ok(bytes));
        var baseline = Baseline(from, "spotify:track:t1");
        var read = await fetcher.FetchPlaylistDiffAsync(Uri, baseline, Ct);
        Assert.Equal(PlaylistReadKind.Delta, read.Kind);
        Assert.Equal(2, PlaylistReplicaReducer.ApplyServer(baseline, read).Members.Length);
    }

    [Fact]
    public async Task Full_fetch_returns_owner_identity_in_header()
    {
        var slc = new Pl.SelectedListContent { Revision = ByteString.CopyFrom(Rev(1)), OwnerUsername = "catherine",
            Attributes = new Pl.ListAttributes { Name = "Summer" }, Contents = new Pl.ListItems() };
        var (fetcher, _) = Rig((_, _) => Ok(slc.ToByteArray()));
        var read = await fetcher.FetchPlaylistAsync(Uri, Ct);
        Assert.Equal("catherine", read.Header?.Owner?.Id);
        Assert.Equal("catherine", read.Header?.Owner?.Name);
        Assert.Null(read.Header?.Owner?.Avatar);
    }

    [Fact]
    public async Task LibrarySync_open_stale_playlist_uses_diff_and_respects_freshness()
    {
        int requests = 0;
        await using var host = new ReplicaTestHost();
        await host.SeedPlaylistAsync(Uri, Baseline(Rev(1), "spotify:track:t1").Members, Rev(1));
        var sync = host.AttachSync(new FakeExchange((_, _) =>
        { requests++; return Ok(new Pl.SelectedListContent { UpToDate = true }.ToByteArray()); }), new StubTransport());
        await sync.OpenPlaylistAsync(Uri, Ct);
        await sync.OpenPlaylistAsync(Uri, Ct);
        Assert.Equal(1, requests);
        Assert.Equal(1, sync.DiffUpToDate);
    }

    [Fact]
    public async Task LibrarySync_rolling_header_refreshes_inside_freshness_window()
    {
        int headers = 0;
        await using var host = new ReplicaTestHost();
        await host.SeedPlaylistAsync(Uri, Baseline(Rev(1), "spotify:track:t1").Members, Rev(1),
            new Playlist("x", Uri, "Old", null, "spotify", null, 1) { Format = "daylist" });
        var sync = host.AttachSync(new FakeExchange((request, _) =>
        {
            if (request.Url.Contains("/diff?")) return Ok(new Pl.SelectedListContent { UpToDate = true }.ToByteArray());
            headers++;
            return Ok(new Pl.SelectedListContent { Length = 1, OwnerUsername = "spotify",
                Attributes = new Pl.ListAttributes { Name = headers == 1 ? "Old" : "New", Format = "daylist" } }.ToByteArray());
        }), new StubTransport());
        await sync.OpenPlaylistAsync(Uri, Ct);
        await sync.OpenPlaylistAsync(Uri, Ct);
        Assert.Equal(2, headers);
        Assert.Equal("New", host.ReadHeader(Uri)?.Name);
        Assert.Single(host.Replicas.ReadPlaylist(Uri).Members);
    }
}
