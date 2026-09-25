// ── Wavee.Tests/EvidenceReportTests.cs — the evidence bundle's pure half (Screens/EvidenceReport.cs) ─────────────────
//
// docs/plans/evidence-diagnostics-implementation.md §B: the diag verb parse, the bundle folder name, the viewport match,
// the card verdict and every file's rows — a reader (Read-Bundle.py) parses these, so their columns are a contract.

using System.Linq;
using System.Text.Json;
using FluentGpu.Foundation;
using FluentGpu.Hosting;
using FluentGpu.Render.Evidence;
using FluentGpu.Render.Tiles;
using FluentGpu.Rhi;
using Wavee;
using Xunit;

namespace Wavee.Tests;

public class EvidenceReportTests
{
    static string Name(int index, uint gen) => "node" + index + "g" + gen;

    /// <summary>The extended-metadata evidence probe (2026-09-25): uris required (spotify uris only, ≤ 300), kinds default
    /// to the track row's identity + both video traits + the play count, a bad kind refuses the verb.</summary>
    [Fact]
    public void TryParseDiag_ReadsTheXmProbe()
    {
        Assert.True(EvidenceReport.TryParseDiag("?cmd=xm&uris=spotify%3Atrack%3Aa,%20spotify:track:b,bogus&tag=m", out var v));
        Assert.Equal(DiagCommand.Xm, v.Command);
        Assert.Equal(new[] { "spotify:track:a", "spotify:track:b" }, v.Uris);
        Assert.Equal(new[] { 10, 99, 182, 185 }, v.Kinds);
        Assert.Equal("m", v.Tag);

        Assert.True(EvidenceReport.TryParseDiag("?cmd=xm&uris=spotify:track:a&kinds=185,99,185", out var k));
        Assert.Equal(new[] { 185, 99 }, k.Kinds);
        Assert.Equal("xm", k.Tag);

        Assert.False(EvidenceReport.TryParseDiag("?cmd=xm", out _));
        Assert.False(EvidenceReport.TryParseDiag("?cmd=xm&uris=bogus", out _));
        Assert.False(EvidenceReport.TryParseDiag("?cmd=xm&uris=spotify:track:a&kinds=10,x", out _));
    }

    [Fact]
    public void TryParseDiag_ReadsEveryCommand_AndRefusesIncompleteOnes()
    {
        Assert.True(EvidenceReport.TryParseDiag("?cmd=bundle&tag=x", out var b));
        Assert.Equal(new DiagVerb(DiagCommand.Bundle, Tag: "x"), b);
        Assert.True(EvidenceReport.TryParseDiag("cmd=pixel&x=10.7&y=-3&dip=true", out var p));
        Assert.Equal(new DiagVerb(DiagCommand.Pixel, X: 10, Y: -3, Dip: true), p);
        Assert.True(EvidenceReport.TryParseDiag("?cmd=scroll&vp=artist&to=1e3", out var s));
        Assert.Equal(new DiagVerb(DiagCommand.Scroll, Viewport: "artist", To: 1000.0), s);
        Assert.True(EvidenceReport.TryParseDiag("?cmd=probe&level=off", out var o));
        Assert.Equal(0, o.Level);
        Assert.False(EvidenceReport.TryParseDiag("", out _));
        Assert.False(EvidenceReport.TryParseDiag("?tag=x", out _));
        Assert.False(EvidenceReport.TryParseDiag("?cmd=scroll&vp=a&to=NaN", out _));
    }

    [Fact]
    public void BundleFolder_IsSortableAndTagSafe()
    {
        var at = new DateTime(2026, 9, 24, 21, 5, 9);
        Assert.Equal("20260924-210509-band-16", EvidenceReport.BundleFolderName(at, "band-16"));
        Assert.Equal("20260924-210509-a_b_c", EvidenceReport.BundleFolderName(at, "a/b\\c"));
        Assert.Equal("20260924-210509-bundle", EvidenceReport.BundleFolderName(at, "  "));
        Assert.Equal(48, EvidenceReport.SanitizeTag(new string('x', 100)).Length);
    }

    [Fact]
    public void ViewportMatches_ThroughThePageScope()
    {
        Assert.True(EvidenceReport.ViewportMatches("artist-scroll:spotify:artist:1", "artist"));
        Assert.True(EvidenceReport.ViewportMatches("tab0/artist-scroll:spotify:artist:1", "artist"));
        Assert.True(EvidenceReport.ViewportMatches("tab0/artist-scroll:x", "tab0/artist"));
        Assert.False(EvidenceReport.ViewportMatches("tab0/album-scroll:x", "artist"));
        Assert.False(EvidenceReport.ViewportMatches(null, "artist"));
    }

    [Fact]
    public void Health_NeedsZeroStaleZeroMissingZeroRefused()
    {
        Assert.True(EvidenceReport.IsHealthy(default, 0));
        Assert.False(EvidenceReport.IsHealthy(new TileCensus { StaleTiles = 1 }, 0));
        Assert.False(EvidenceReport.IsHealthy(new TileCensus { ExposedMissing = 1 }, 0));
        Assert.False(EvidenceReport.IsHealthy(default, 2));
    }

    static ItemRecord Item(int slice, byte kind = (byte)CompositeKind.Tiles) => new()
    {
        NodeIndex = 7, Gen = 2, SliceId = slice, Kind = kind, AlphaQ8 = 255, TransDx = 64, TransDy = -12,
        Flags = ItemRecordFlags.ClipBounded | ItemRecordFlags.StickyEngaged, ClipX = 0, ClipY = 84, ClipW = 800, ClipH = 400,
        Feather1 = new EdgeFeather(new RectF(0f, 84f, 800f, 400f), 0f, 36f, 0f, 0f, default), StickyTopPx = 84, Inherited = 1,
    };

    [Fact]
    public void ItemsTsv_HasAHeaderAndOneRowPerItem_WithTheNodeName()
    {
        string tsv = EvidenceReport.ItemsTsv(new[] { Item(3), Item(-1, (byte)CompositeKind.Group) }, Name);
        var lines = tsv.TrimEnd('\n').Split('\n');
        Assert.Equal(3, lines.Length);
        var header = lines[0].Split('\t');
        Assert.Equal("index", header[0]);
        Assert.Contains("nodeKey", header);
        var row = lines[1].Split('\t');
        Assert.Equal(header.Length, row.Length);
        Assert.Equal("Tiles", row[1]);
        Assert.Equal("7:2", row[2]);
        Assert.Equal("node7g2", row[3]);
        Assert.Equal("sticky|clip", row[7]);
        Assert.Equal("0,84,800,400", row[8]);
        Assert.Equal("64,-12", row[9]);
        Assert.Equal("84", row[13]);
        Assert.Equal("Group", lines[2].Split('\t')[1]);
    }

    [Fact]
    public void StaleTsv_ListsOnlyTheStalePlacements_WithTheirNode()
    {
        PlacementRecord[] placed =
        [
            new() { SliceId = 3, Tx = 0, Ty = 0, RasterHash = 1, WantHash = 1 },
            new() { SliceId = 3, Tx = 0, Ty = 1, RasterHash = 0xAB, WantHash = 0xCD, RasterFrame = 41, Stale = 1 },
        ];
        var lines = EvidenceReport.StaleTsv(placed, new[] { Item(3) }, Name).TrimEnd('\n').Split('\n');
        Assert.Equal(2, lines.Length);
        var row = lines[1].Split('\t');
        Assert.Equal(new[] { "3", "0", "1", "7:2", "node7g2", "00000000000000cd", "00000000000000ab", "41" }, row);
    }

    [Fact]
    public void LedgerAndWalks_NameTheReasonAndTheWhy()
    {
        var ledger = EvidenceReport.LedgerTsv(new[]
        {
            new RasterEntry { Frame = 9, NodeIndex = 7, Gen = 2, SliceId = 3, Tx = 0, Ty = 1, W = 1024, H = 512,
                Reason = (byte)InvalidationReason.Content, Order = 0, AlphaQ8 = 18, Flags = RasterEntryFlags.Faithful | RasterEntryFlags.ScratchRefused, Hash = 0xFF },
        }, Name).TrimEnd('\n').Split('\n');
        var r = ledger[1].Split('\t');
        Assert.Equal("Content", r[8]);
        Assert.Equal("0.071", r[10]);
        Assert.Equal("1", r[11]);
        Assert.Equal("1", r[12]);

        var walks = EvidenceReport.WalksTsv(new[] { new WalkEntry { Frame = 5, NodeIndex = 7, Gen = 2, Bytes = 4096, Why = (byte)WalkWhy.SigMiss } }, Name)
            .TrimEnd('\n').Split('\n');
        var w = walks[1].Split('\t');
        Assert.Equal("SigMiss", w[4]);
        Assert.Equal("4096", w[6]);
    }

    [Fact]
    public void PixelTsv_AndCardRows_ReadTheQueryTopFirstOnTheCard()
    {
        var bottom = new PixelHit(0, Item(3), 1f, 1f, 1f, new TileKey(3, 0, 0), 5, 1, 1, false, true);
        var top = new PixelHit(1, Item(4), 0.17f, 1f, 0.07f, new TileKey(4, 0, 0), 9, 2, 3, true, true, FeatherBand: true);
        var hits = new[] { bottom, top };
        var header = new CompositeFrameHeader { Frame = 12, PublishSeq = 99, Scale = 1.5f };
        var lines = EvidenceReport.PixelTsv(10, 20, in header, hits, Name).TrimEnd('\n').Split('\n');
        Assert.StartsWith("# pixel x=10 y=20 frame=12 publishSeq=99", lines[0]);
        Assert.Equal(4, lines.Length);
        var topRow = lines[3].Split('\t');
        Assert.Equal("1", topRow[14]);      // the stale tile
        Assert.Equal("band", topRow[15]);   // a feathered item whose split interior does not hold the pixel

        var rows = EvidenceReport.PixelRows(hits, Name);
        Assert.Equal("1", rows[0].Index);                 // the card lists the TOP item first
        Assert.True(rows[0].Stale);
        Assert.Equal("0.17", rows[0].Feather1);
        Assert.Equal("f9", rows[0].Raster);
    }

    [Fact]
    public void MetaAndCensus_AreValidJson()
    {
        var meta = new EvidenceMeta("0.3.0", 42, "C:\\verify \"p\"", "artist", "spotify:artist:x", 7, 99, 12, 123456, 1.5f,
            1600, 1000, true, "band-16", "2026-09-24T21:05:09.000");
        var vps = new[] { new ViewportInfo(5, 1, "tab0/artist-scroll:x", 812.5, 4000, 900, false) };
        using (var doc = JsonDocument.Parse(EvidenceReport.MetaJson(in meta, vps)))
        {
            Assert.Equal(99, doc.RootElement.GetProperty("publishSeq").GetInt64());
            Assert.Equal("C:\\verify \"p\"", doc.RootElement.GetProperty("profile").GetString());
            Assert.Equal(812.5, doc.RootElement.GetProperty("viewports")[0].GetProperty("offset").GetDouble());
        }
        using (var doc = JsonDocument.Parse(EvidenceReport.MetaJson(in meta, Array.Empty<ViewportInfo>())))
            Assert.Equal(0, doc.RootElement.GetProperty("viewports").GetArrayLength());

        var census = new TileCensus { StaleTiles = 2, ExposedMissing = 1, Items = 40 };
        var device = new GpuFrameCounters(1, RepaintRoute.Composite, RepaintFullReason.None, 100f, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
            ScratchRefused: 3);
        using (var doc = JsonDocument.Parse(EvidenceReport.CensusJson(census, device, 17)))
        {
            Assert.Equal(2, doc.RootElement.GetProperty("tiles").GetProperty("staleTiles").GetInt32());
            Assert.Equal(3, doc.RootElement.GetProperty("device").GetProperty("scratchRefused").GetInt32());
            Assert.Equal(17, doc.RootElement.GetProperty("staleTurnsSinceLaunch").GetInt32());
        }
    }

    [Fact]
    public void Tail_KeepsTheLastLines()
    {
        var lines = Enumerable.Range(0, 10).Select(i => "l" + i).ToList();
        Assert.Equal("l8\nl9\n", EvidenceReport.Tail(lines, 2));
        Assert.Equal(string.Concat(lines.Select(l => l + "\n")), EvidenceReport.Tail(lines, 50));
    }
}
