using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Wavee.Backend.Playlists;
using Wavee.Backend.Spotify;
using Wavee.Core;
using Xunit;
using Pl = Wavee.Protocol.Playlist;

namespace Wavee.Tests;

// ── B-1 regression: the recents /page/diff no-change reply must never blank the list ──────────────────────────────────
// The captured no-change reply (all_home.saz session 292) is NOT a 304. It is HTTP 200, 123 bytes, uncompressed, and it
// carries `diff { from_revision == to_revision, no ops }` with `contents` ABSENT and `up_to_date` ABSENT. Mapping that
// body yields ZERO items, so a fetcher that treats every 200 as a full snapshot hands back an EMPTY snapshot and the
// resident ~1,708 rows are replaced by 0. These tests pin the branching (up_to_date / diff / contents / nothing) and the
// invariant behind it: FetchDiffAsync may never return rows derived from a body that carried no `contents`.
public class RecentsDiffTests
{
    /// <summary>The EXACT captured 123-byte no-change body (session 292), base64. Not synthesised.</summary>
    const string NoChangeBodyBase64 =
        "ChgAAAAAAAAAAEjEGm7aj9bNCOEdt7RaZ78yNAoYAAAAAAAAAABIxBpu2o/WzQjhHbe0Wme/GhgAAAAAAAAAAEjEGm7aj9bNCOEdt7RaZ7+IAQCSAQwIARAAIAAoAEIAeAGyARMKEQoEYXV0bxIEYXV0bxoDCMgBuAEA";

    const string PageUrl = "https://x/playlist/v2/list/recents/page";

    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static HttpResp Ok(byte[] body) => new(200, new Dictionary<string, string>(), body);
    static HttpResp Status(int status) => new(status, new Dictionary<string, string>(), Array.Empty<byte>());

    static (RecentsFetcher Fetcher, List<HttpReq> Reqs) Rig(Func<HttpReq, int, HttpResp> respond)
    {
        var reqs = new List<HttpReq>();
        var http = new FakeExchange((req, n) => { reqs.Add(req); return respond(req, n); });
        return (new RecentsFetcher(http, () => "https://x"), reqs);
    }

    // a resident page: the rows + revision the UI already holds when it revalidates.
    static (byte[] Revision, IReadOnlyList<RecentsRow> Rows) Resident(byte[] revision, params Pl.Item[] items)
        => (revision, RecentsWire.Rows(RecentsWire.Page(revision, items)));

    static byte[] DiffBody(byte[] from, byte[] to, params Pl.Op[] ops)
    {
        var diff = new Pl.Diff { FromRevision = ByteString.CopyFrom(from), ToRevision = ByteString.CopyFrom(to) };
        for (int i = 0; i < ops.Length; i++) diff.Ops.Add(ops[i]);
        return new Pl.SelectedListContent { Diff = diff }.ToByteArray();
    }

    static Pl.Op AddLast(string uri)
    {
        var add = new Pl.Add { AddLast = true };
        add.Items.Add(new Pl.Item { Uri = uri });
        return new Pl.Op { Kind = Pl.Op.Types.Kind.Add, Add = add };
    }

    // ── the captured body really is the shape the fix is written against ─────────────────────────────────────────────
    [Fact]
    public void CapturedNoChangeBody_HasDiffWithoutOps_AndNoContents()
    {
        var body = Convert.FromBase64String(NoChangeBodyBase64);
        Assert.Equal(123, body.Length);

        var slc = Pl.SelectedListContent.Parser.ParseFrom(body);
        Assert.Null(slc.Contents);                        // ← what makes the naive "every 200 is a snapshot" path fatal
        Assert.False(slc.HasUpToDate);                    // the server does NOT set the up_to_date flag here
        Assert.NotNull(slc.Diff);
        Assert.Empty(slc.Diff.Ops);                       // no ops …
        Assert.Equal(slc.Diff.FromRevision, slc.Diff.ToRevision);   // … and from == to: nothing changed
    }

    // ── B-1: the real no-change reply → "unchanged" (null), never an empty snapshot, and no re-fetch ─────────────────
    [Fact]
    public async Task Diff_CapturedNoChangeBody_ReturnsUnchanged_NeverAnEmptySnapshot()
    {
        var body = Convert.FromBase64String(NoChangeBodyBase64);
        var (rev, rows) = Resident(RecentsWire.Rev(3396, 0xAB, 0xCD),
            RecentsWire.Item("spotify:playlist:a", [0x01], 100),
            RecentsWire.Item("spotify:track:b", [0x02], 90));
        Assert.Equal(2, rows.Count);

        var (fetcher, reqs) = Rig((_, _) => Ok(body));

        var result = await fetcher.FetchDiffAsync(rev, rows, Ct);

        // B-1: returning a snapshot here replaced 1,708 resident rows with 0.
        Assert.Null(result);
        Assert.False(result is { Rows.Count: 0 }, "a contents-less body must NEVER produce a snapshot");
        var req = Assert.Single(reqs);                                       // and no full page re-fetch either
        Assert.Contains("/playlist/v2/list/recents/page/diff?revision=3396%2Cabcd", req.Url);
        Assert.Contains("&handlesContent=", req.Url);
        Assert.Contains("hint_revision=3396%2Cabcd", req.Url);               // Wavee keeps sending it (the real client does not)
        Assert.Equal("CAEQAQ==", req.Headers["spotify-playlist-sync-reason"]);   // the refresh/diff sync reason
    }

    // ── a 304 (0-byte body) is unchanged too ────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task Diff_304_ReturnsUnchanged()
    {
        var (rev, rows) = Resident(RecentsWire.Rev(1, 0x01), RecentsWire.Item("spotify:playlist:a", [0x01], 100));
        var (fetcher, reqs) = Rig((_, _) => Status(304));

        Assert.Null(await fetcher.FetchDiffAsync(rev, rows, Ct));
        Assert.Single(reqs);
    }

    // ── an explicit up_to_date flag, and a 200 that carries nothing at all, are both unchanged ──────────────────────
    [Fact]
    public async Task Diff_UpToDateFlag_AndEmptyBody_ReturnUnchanged()
    {
        var (rev, rows) = Resident(RecentsWire.Rev(1, 0x01), RecentsWire.Item("spotify:playlist:a", [0x01], 100));

        var (a, _) = Rig((_, _) => Ok(new Pl.SelectedListContent { UpToDate = true }.ToByteArray()));
        Assert.Null(await a.FetchDiffAsync(rev, rows, Ct));

        var (b, _) = Rig((_, _) => Ok(Array.Empty<byte>()));
        Assert.Null(await b.FetchDiffAsync(rev, rows, Ct));

        var (c, _) = Rig((_, _) => Ok(new Pl.SelectedListContent().ToByteArray()));   // 200, parses, but no contents/diff
        Assert.Null(await c.FetchDiffAsync(rev, rows, Ct));
    }

    // ── a full-contents 200 with a DIFFERENT item vector is a real change → the new snapshot ─────────────────────────
    [Fact]
    public async Task Diff_FullContents_DifferentVector_ReturnsTheNewSnapshot()
    {
        var (rev, rows) = Resident(RecentsWire.Rev(1, 0x01), RecentsWire.Item("spotify:playlist:a", [0x01], 100));
        var next = RecentsWire.Rev(2, 0x02);
        var body = RecentsWire.Page(next,
            RecentsWire.Item("spotify:playlist:a", [0x01], 100),
            RecentsWire.Item("spotify:track:new", [0x09], 101)).ToByteArray();
        var (fetcher, reqs) = Rig((_, _) => Ok(body));

        var result = await fetcher.FetchDiffAsync(rev, rows, Ct);

        Assert.NotNull(result);
        Assert.Equal(2, result!.Rows.Count);
        Assert.Equal("spotify:track:new", result.Rows[1].Uri);
        Assert.Equal(Convert.ToHexStringLower(next), result.Revision);
        Assert.Single(reqs);
    }

    // ── the REVISION LIES: a changed revision over a byte-identical item vector → unchanged ──────────────────────────
    // (three captured /diff replies re-sent all 1,422,080 bytes with 60 bytes different, all inside the list-level
    //  format_attributes; `contents` was byte-identical.)
    [Fact]
    public async Task Diff_FullContents_SameVector_ChangedRevision_ReturnsUnchanged()
    {
        Pl.Item[] items =
        [
            RecentsWire.Item("spotify:playlist:a", [0x01], 100),
            RecentsWire.Item("spotify:track:b", [0x02], 90),
        ];
        var (rev, rows) = Resident(RecentsWire.Rev(1, 0x01), items);
        var body = RecentsWire.Page(RecentsWire.Rev(2, 0xFF), items).ToByteArray();   // same items, NEW revision
        var (fetcher, _) = Rig((_, _) => Ok(body));

        Assert.Null(await fetcher.FetchDiffAsync(rev, rows, Ct));
    }

    // ── a diff that DOES carry ops converges through a full page read (recents rows are not op-appliable) ────────────
    [Fact]
    public async Task Diff_WithOps_ConvergesViaFullPageFetch()
    {
        var from = RecentsWire.Rev(1, 0x01);
        var to = RecentsWire.Rev(2, 0x02);
        var (rev, rows) = Resident(from, RecentsWire.Item("spotify:playlist:a", [0x01], 100));
        var full = RecentsWire.Page(to,
            RecentsWire.Item("spotify:playlist:a", [0x01], 100),
            RecentsWire.Item("spotify:track:fresh", [0x03], 110)).ToByteArray();
        var (fetcher, reqs) = Rig((req, _) => req.Url.Contains("/page/diff?") ? Ok(DiffBody(from, to, AddLast("spotify:track:fresh"))) : Ok(full));

        var result = await fetcher.FetchDiffAsync(rev, rows, Ct);

        Assert.NotNull(result);
        Assert.Equal(2, result!.Rows.Count);
        Assert.Equal(Convert.ToHexStringLower(to), result.Revision);
        Assert.Equal(2, reqs.Count);
        Assert.Contains("/page/diff?", reqs[0].Url);
        Assert.Equal(PageUrl, reqs[1].Url);                                       // the cold page path, no query
        Assert.Equal("CAwQAQ==", reqs[1].Headers["spotify-playlist-sync-reason"]);   // the COLD sync reason
    }

    // ── no usable baseline revision → straight to the full page, no /diff round-trip ─────────────────────────────────
    [Fact]
    public async Task Diff_NoBaselineRevision_GoesStraightToTheFullPage()
    {
        var body = RecentsWire.Page(RecentsWire.Rev(1, 0x01), RecentsWire.Item("spotify:playlist:a", [0x01], 100)).ToByteArray();
        var (fetcher, reqs) = Rig((_, _) => Ok(body));

        var result = await fetcher.FetchDiffAsync(null, Array.Empty<RecentsRow>(), Ct);

        Assert.NotNull(result);
        Assert.Single(result!.Rows);
        Assert.Equal(PageUrl, Assert.Single(reqs).Url);
    }

    // ── the cold page read maps + groups, and a zstd-framed body decodes through the magic sniff ────────────────────
    [Fact]
    public async Task FullPage_Grouped_AndZstdBodyDecodes()
    {
        var rev = RecentsWire.Rev(5, 0x55);
        var page = RecentsWire.Page(rev,
            RecentsWire.Item("spotify:playlist:p", [0x01], 100,
                RecentsWire.Attr("group_id_0"), RecentsWire.Attr("children_group_id", "4"),
                RecentsWire.Attr("group_metadata", RecentsWire.GroupMetadata(11, ["spotify:track:a"]))),
            RecentsWire.Item("spotify:track:a", [0x02], 99, RecentsWire.Attr("group_id_4")),
            RecentsWire.Item("spotify:track:b", [0x03], 98, RecentsWire.Attr("group_id_4"))).ToByteArray();
        using var compressor = new ZstdSharp.Compressor();
        var zstd = compressor.Wrap(page).ToArray();

        var (fetcher, reqs) = Rig((_, _) => Ok(zstd));
        var snapshot = await fetcher.FetchAsync(Ct);

        var row = Assert.Single(snapshot.Rows);                       // 3 wire items → 1 grouped row
        Assert.Equal(RecentsRowKind.Group, row.Kind);
        Assert.Equal(11, row.ChildCount);
        Assert.Equal(Convert.ToHexStringLower(rev), snapshot.Revision);
        Assert.Equal(PageUrl, Assert.Single(reqs).Url);
    }
}
