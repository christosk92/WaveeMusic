// ── Wavee.Tests/StoreShotReportTests.cs — the Store shot's pure half (Screens/EvidenceReport.cs, Screens/StoreShot.cs) ──
//
// The capture verbs (shot / present / seek / stage / rail) and shot.json: the Store composer
// (ops/release/tools/New-StoreImage.py) reads its keys to place spotlights and callouts, so its shape is a contract.

using System.Text.Json;
using Wavee;
using Xunit;

namespace Wavee.Tests;

public class StoreShotReportTests
{
    [Fact]
    public void TryParseDiag_Shot_DefaultsAndBounds()
    {
        Assert.True(EvidenceReport.TryParseDiag("?cmd=shot", out var v));
        Assert.Equal(DiagCommand.Shot, v.Command);
        Assert.Equal("shot", v.Tag);
        Assert.Equal(0, v.Zoom);            // as is
        Assert.Equal(700, v.SettleMs);
        Assert.True(v.Alpha);
        Assert.Empty(v.Keys!);

        Assert.True(EvidenceReport.TryParseDiag("?cmd=shot&tag=home%20hero&zoom=250&settle=1500&alpha=0&keys=badge,%20ai.status", out var z));
        Assert.Equal("home_hero", z.Tag);
        Assert.Equal(250, z.Zoom);
        Assert.Equal(1500, z.SettleMs);
        Assert.False(z.Alpha);
        Assert.Equal(new[] { "badge", "ai.status" }, z.Keys);

        Assert.True(EvidenceReport.TryParseDiag("?cmd=shot&zoom=100", out var same));
        Assert.Equal(0, same.Zoom);         // 100 % is "as is": no zoom round trip

        Assert.False(EvidenceReport.TryParseDiag("?cmd=shot&zoom=20", out _));
        Assert.False(EvidenceReport.TryParseDiag("?cmd=shot&zoom=900", out _));
        Assert.False(EvidenceReport.TryParseDiag("?cmd=shot&settle=-1", out _));
        Assert.False(EvidenceReport.TryParseDiag("?cmd=shot&settle=x", out _));
    }

    [Fact]
    public void TryParseDiag_PresentSeekStageRail()
    {
        Assert.True(EvidenceReport.TryParseDiag("?cmd=present&on=1", out var p));
        Assert.Equal((DiagCommand.Present, 1), (p.Command, p.On));
        Assert.True(EvidenceReport.TryParseDiag("?cmd=present&on=false", out var off));
        Assert.Equal(0, off.On);
        Assert.False(EvidenceReport.TryParseDiag("?cmd=present", out _));

        Assert.True(EvidenceReport.TryParseDiag("?cmd=seek&ms=42150&pause=1", out var s));
        Assert.Equal((DiagCommand.Seek, 42150, 1), (s.Command, s.Ms, s.On));
        Assert.True(EvidenceReport.TryParseDiag("?cmd=seek&ms=0", out var s0));
        Assert.Equal(-1, s0.On);            // leave play/pause alone
        Assert.False(EvidenceReport.TryParseDiag("?cmd=seek", out _));
        Assert.False(EvidenceReport.TryParseDiag("?cmd=seek&ms=-5", out _));

        Assert.True(EvidenceReport.TryParseDiag("?cmd=stage&open=1&mode=Visualizer&face=aurora&gallery=0", out var st));
        Assert.Equal((DiagCommand.Stage, 1, "visualizer", "aurora", 0), (st.Command, st.On, st.Mode, st.Face, st.Gallery));
        Assert.True(EvidenceReport.TryParseDiag("?cmd=stage&open=0", out var close));
        Assert.Equal((0, -1), (close.On, close.Gallery));
        Assert.False(EvidenceReport.TryParseDiag("?cmd=stage", out _));                 // changes nothing
        Assert.False(EvidenceReport.TryParseDiag("?cmd=stage&mode=karaoke", out _));

        Assert.True(EvidenceReport.TryParseDiag("?cmd=rail&mode=queue", out var r));
        Assert.Equal((DiagCommand.Rail, "queue"), (r.Command, r.Mode));
        Assert.False(EvidenceReport.TryParseDiag("?cmd=rail", out _));
        Assert.False(EvidenceReport.TryParseDiag("?cmd=rail&mode=friends", out _));
    }

    [Fact]
    public void ShotKeys_FiltersByPrefix_ClipsToTheWindow_AndOrdersStably()
    {
        ShotKeyRow[] shown =
        [
            new("row:2", "Box", 10, 300, 500, 40),
            new("badge.quality", "Box", 900, 20, 60, 20),
            new("row:1", "Box", 10, 250, 500, 40),
            new("row:offscreen", "Box", 10, 900, 500, 40),     // below an 800-DIP window
            new("row:edge", "Box", -20, 780, 500, 40),         // straddles the bottom-left corner
            new("row:empty", "Box", 10, 100, 0, 40),
        ];

        var all = EvidenceReport.ShotKeys(shown, 1200, 800, null);
        Assert.Equal(new[] { "badge.quality", "row:1", "row:2", "row:edge" }, all.Select(r => r.Key));
        var edge = all.Single(r => r.Key == "row:edge");
        Assert.Equal((0f, 780f, 480f, 20f), (edge.X, edge.Y, edge.W, edge.H));

        var rows = EvidenceReport.ShotKeys(shown, 1200, 800, ["ROW:"]);
        Assert.Equal(new[] { "row:1", "row:2", "row:edge" }, rows.Select(r => r.Key));
        Assert.True(EvidenceReport.ShotKeyWanted("anything", []));
        Assert.False(EvidenceReport.ShotKeyWanted("badge", ["row", ""]));
    }

    [Fact]
    public void ShotJson_IsValidJson_WithTheContractFields()
    {
        var meta = new ShotMeta("home", "home", "", 1.65f, 1.1f, 2098, 1131, Alpha: true, Presenting: true, PositionMs: 42150,
            CreatedLocal: "2026-10-05T19:30:00.000");
        string json = EvidenceReport.ShotJson(in meta, [new("chrome-profile", "Component", 833.5f, 8f, 32f, 32f)]);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("home", root.GetProperty("tag").GetString());
        Assert.Equal(1.65, root.GetProperty("scale").GetDouble(), 3);
        Assert.Equal(2098, root.GetProperty("widthPx").GetInt32());
        Assert.Equal(42150, root.GetProperty("positionMs").GetInt32());
        Assert.True(root.GetProperty("alpha").GetBoolean());
        Assert.True(root.GetProperty("presenting").GetBoolean());
        var k = root.GetProperty("keys")[0];
        Assert.Equal("chrome-profile", k.GetProperty("key").GetString());
        Assert.Equal(833.5, k.GetProperty("x").GetDouble(), 3);

        using var empty = JsonDocument.Parse(EvidenceReport.ShotJson(in meta, []));
        Assert.Equal(0, empty.RootElement.GetProperty("keys").GetArrayLength());
    }
}
