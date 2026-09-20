// ── Wavee.Tests/HomeLayoutTests.cs — the layout document, its reducer, its wire, the projection's row table ──────────
//
// Wave 5, owner P; ported from 0.2.9 `HomeLayoutTests` (reducer: hide / reorder / cap / reset; the DTO round trip and
// the unknown-kind + unknown-member carry; the landing projection applying the layout BEFORE row synthesis, and the
// Charts chrome row). The store's fail-soft facts moved to `HomeLayoutStoreTests`. Added: the customizer's pure half —
// the fault-banner predicate (ch 12 §9 trap 4) and the row-label table (0.2.9 `HomeCustomizeLabels.Of`).

using System.Text.Json;
using Wavee;
using Xunit;

namespace Wavee.Tests;

[Collection(EntitiesCollection.Name)]
public sealed class HomeLayoutTests
{
    static HomeCard Card(string id) => HomeFixtures.Card(HomeFixtures.Playlist("spotify:playlist:layout-" + id, id));

    static HomeFeedView FeedWith(params HomeGroup[] groups) => new("", groups);

    static HomeGroupKind[] KindsOf(HomeLayoutDoc doc)
    {
        var kinds = new HomeGroupKind[doc.Modules.Count];
        for (int i = 0; i < kinds.Length; i++) kinds[i] = doc.Modules[i].Kind;
        return kinds;
    }

    static int IndexOf(IReadOnlyList<HomeRow> rows, HomeRow row)
    {
        for (int i = 0; i < rows.Count; i++)
            if (rows[i] == row) return i;
        return -1;
    }

    // ── reducer ───────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Hide_OmitsKindFromVisibleOrder_AndIsNoChangeWhenAlreadyHidden()
    {
        var layout = HomeLayoutDoc.Default;
        var hidden = HomeLayoutReducer.Apply(layout, new SetHomeModuleHidden(HomeGroupKind.Hero, true));
        Assert.True(hidden.Changed);
        Assert.True(hidden.Layout.IsHidden(HomeGroupKind.Hero));
        Assert.DoesNotContain(HomeGroupKind.Hero, hidden.Layout.VisibleFixedModules());

        var again = HomeLayoutReducer.Apply(hidden.Layout, new SetHomeModuleHidden(HomeGroupKind.Hero, true));
        Assert.False(again.Changed);
        Assert.Equal(HomeLayoutRejectReason.NoChange, again.Reason);
    }

    [Fact]
    public void Hide_ANonLandingKind_IsAnUnknownModule()
    {
        var result = HomeLayoutReducer.Apply(HomeLayoutDoc.Default, new SetHomeModuleHidden(HomeGroupKind.Topic, true));
        Assert.False(result.Changed);
        Assert.Equal(HomeLayoutRejectReason.UnknownModule, result.Reason);
    }

    [Fact]
    public void Reorder_UsesPostRemovalIndex_AndRejectsANoOp()
    {
        var layout = HomeLayoutDoc.Default;
        int from = layout.IndexOf(HomeGroupKind.Hero);
        var moved = HomeLayoutReducer.Apply(layout, new MoveHomeModule(from, 3));
        Assert.True(moved.Changed);
        Assert.Equal(HomeGroupKind.Hero, moved.Layout.Modules[3].Kind);

        var noop = HomeLayoutReducer.Apply(layout, new MoveHomeModule(from, from));
        Assert.False(noop.Changed);
        Assert.Equal(HomeLayoutRejectReason.NoChange, noop.Reason);
    }

    [Fact]
    public void Move_UnknownIndex_IsRejected()
    {
        var result = HomeLayoutReducer.Apply(HomeLayoutDoc.Default, new MoveHomeModule(99, 0));
        Assert.False(result.Changed);
        Assert.Equal(HomeLayoutRejectReason.UnknownModule, result.Reason);
    }

    [Fact]
    public void Cap_RejectsGrowingPastMaxModules()
    {
        var extras = new HomeModuleSpec[HomeLayoutReducer.MaxModules];
        for (int i = 0; i < extras.Length; i++) extras[i] = new HomeModuleSpec(HomeGroupKind.Hero);
        var full = new HomeLayoutDoc(extras);

        var result = HomeLayoutReducer.Apply(full, new SetHomeModuleHidden(HomeGroupKind.WeeklyPair, true));
        Assert.False(result.Changed);
        Assert.Equal(HomeLayoutRejectReason.CapReached, result.Reason);
    }

    [Fact]
    public void Reset_RestoresDefaultOrderAndVisibility()
    {
        var edited = HomeLayoutReducer.Apply(HomeLayoutDoc.Default, new SetHomeModuleHidden(HomeGroupKind.Hero, true)).Layout;
        edited = HomeLayoutReducer.Apply(edited, new MoveHomeModule(0, 4)).Layout;
        var reset = HomeLayoutReducer.Apply(edited, new ResetHomeLayout());
        Assert.True(reset.Changed);
        Assert.Equal(HomeLayoutModules.DefaultOrder, KindsOf(reset.Layout));
        Assert.False(reset.Layout.IsHidden(HomeGroupKind.Hero));
    }

    [Fact]
    public void Commands_NameTheirUndoLabels()
    {
        Assert.Equal(HomeLayoutUndoLabels.HideModule, new SetHomeModuleHidden(HomeGroupKind.Hero, true).LabelLocKey);
        Assert.Equal(HomeLayoutUndoLabels.ShowModule, new SetHomeModuleHidden(HomeGroupKind.Hero, false).LabelLocKey);
        Assert.Equal(HomeLayoutUndoLabels.MoveModule, new MoveHomeModule(0, 1).LabelLocKey);
        Assert.Equal(HomeLayoutUndoLabels.Reset, new ResetHomeLayout().LabelLocKey);
    }

    [Fact]
    public void KindNames_RoundTripThroughTryParseKind_ForEveryKind()
    {
        foreach (var kind in Enum.GetValues<HomeGroupKind>())
        {
            Assert.True(HomeLayoutModules.TryParseKind(HomeLayoutModules.KindName(kind), out var back));
            Assert.Equal(kind, back);
        }
        Assert.False(HomeLayoutModules.TryParseKind("futureModule", out _));
        Assert.False(HomeLayoutModules.TryParseKind(null, out _));
    }

    // ── DTO round trip + unknown carry ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Dto_RoundTripsModulesDeckOrderAndHidden()
    {
        var layout = new HomeLayoutDoc(
            [
                new HomeModuleSpec(HomeGroupKind.MixBand, Hidden: true),
                new HomeModuleSpec(HomeGroupKind.Hero),
            ],
            DeckOrder: ["spotify:section:a", "spotify:section:b"]);

        var dto = HomeLayoutWire.Write(layout, null);
        dto.Version = HomeLayoutStore.CurrentVersion;
        string json = JsonSerializer.Serialize(dto, HomeLayoutJsonCtx.Default.HomeLayoutDocDto);
        var parsed = JsonSerializer.Deserialize(json, HomeLayoutJsonCtx.Default.HomeLayoutDocDto);
        var read = HomeLayoutWire.Read(parsed);

        Assert.True(read.Layout.IsHidden(HomeGroupKind.MixBand));
        Assert.Equal(HomeGroupKind.MixBand, read.Layout.Modules[0].Kind);
        Assert.Equal(HomeGroupKind.Hero, read.Layout.Modules[1].Kind);
        Assert.Equal(["spotify:section:a", "spotify:section:b"], read.Layout.DeckList);
        // Missing fixed kinds are appended visible so a new module cannot vanish.
        Assert.Contains(read.Layout.Modules, m => m.Kind == HomeGroupKind.QuickGrid && !m.Hidden);
        Assert.Equal(HomeLayoutModules.DefaultOrder.Length, read.Layout.ModuleCount);
    }

    [Fact]
    public void Dto_WritesTheCamelCaseWireNames_AndOmitsVisibleHiddenFlags()
    {
        var dto = HomeLayoutWire.Write(new HomeLayoutDoc([new HomeModuleSpec(HomeGroupKind.Hero)]), null);
        string json = JsonSerializer.Serialize(dto, HomeLayoutJsonCtx.Default.HomeLayoutDocDto);
        Assert.Contains("\"version\"", json, StringComparison.Ordinal);
        Assert.Contains("\"kind\": \"hero\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"hidden\"", json, StringComparison.Ordinal);   // null (visible) is not written
    }

    [Fact]
    public void Read_ANullDocument_IsTheDefault_AndDuplicateKindsKeepTheFirst()
    {
        Assert.Equal(HomeLayoutModules.DefaultOrder, KindsOf(HomeLayoutWire.Read(null).Layout));

        var dto = new HomeLayoutDocDto
        {
            Version = 1,
            Modules = [new HomeModuleDto { Kind = "hero", Hidden = true }, new HomeModuleDto { Kind = "hero", Hidden = false }],
        };
        var read = HomeLayoutWire.Read(dto);
        Assert.True(read.Layout.IsHidden(HomeGroupKind.Hero));
        Assert.Equal(1, read.Layout.Modules.Count(m => m.Kind == HomeGroupKind.Hero));
    }

    [Fact]
    public void UnknownKindAndUnknownFields_SurviveRoundTrip()
    {
        const string json = """
            {
              "version": 1,
              "futureTop": "keep-me",
              "deckOrder": ["sec:later"],
              "modules": [
                { "kind": "hero", "hidden": false, "futureFlag": true },
                { "kind": "futureModule", "hidden": true, "payload": { "n": 1 } }
              ]
            }
            """;

        var parsed = JsonSerializer.Deserialize(json, HomeLayoutJsonCtx.Default.HomeLayoutDocDto)!;
        Assert.NotNull(parsed.Extra);
        Assert.True(parsed.Extra!.ContainsKey("futureTop"));

        var read = HomeLayoutWire.Read(parsed);
        read.Carry.CaptureDoc(parsed);
        Assert.Equal(1, read.Carry.UnknownModuleCount);
        Assert.False(read.Layout.IsHidden(HomeGroupKind.Hero));

        var written = HomeLayoutWire.Write(read.Layout, read.Carry);
        read.Carry.ReattachDoc(written);
        string back = JsonSerializer.Serialize(written, HomeLayoutJsonCtx.Default.HomeLayoutDocDto);
        Assert.Contains("futureTop", back, StringComparison.Ordinal);
        Assert.Contains("keep-me", back, StringComparison.Ordinal);
        Assert.Contains("futureModule", back, StringComparison.Ordinal);
        Assert.Contains("futureFlag", back, StringComparison.Ordinal);
        Assert.Contains("sec:later", back, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownModule_IsReinsertedAtItsOriginalIndex()
    {
        var parsed = new HomeLayoutDocDto
        {
            Version = 1,
            Modules = [new HomeModuleDto { Kind = "hero" }, new HomeModuleDto { Kind = "futureModule" }, new HomeModuleDto { Kind = "mixBand" }],
        };
        var read = HomeLayoutWire.Read(parsed);
        var written = HomeLayoutWire.Write(read.Layout, read.Carry);
        Assert.Equal("hero", written.Modules![0].Kind);
        Assert.Equal("futureModule", written.Modules[1].Kind);
        Assert.Equal("mixBand", written.Modules[2].Kind);
    }

    // ── projection applies layout BEFORE row synthesis ────────────────────────────────────────────────────────────────

    [Fact]
    public void Projection_HiddenHero_OmitsRow_AndDoesNotLeaveAHole()
    {
        TestScope.Fresh();
        var feed = FeedWith(new HomeGroup(HomeGroupKind.Hero, null, [Card("daylist")]));
        var hidden = HomeLayoutReducer.Apply(HomeLayoutDoc.Default, new SetHomeModuleHidden(HomeGroupKind.Hero, true)).Layout;

        var landing = HomeLandingProjection.Project(feed, HomeModuleTitles.Default, hidden);
        Assert.Null(landing.Get(HomeGroupKind.Hero));
        Assert.DoesNotContain(HomeRow.Hero, landing.Rows);
        Assert.Equal(HomeRow.Chips, landing.Rows[0]);
        Assert.Equal(HomeRow.Tail, landing.Rows[^1]);
    }

    [Fact]
    public void Projection_Reorder_IsVisibleInRows()
    {
        TestScope.Fresh();
        var feed = FeedWith(
            new HomeGroup(HomeGroupKind.Hero, null, [Card("hero")]),
            new HomeGroup(HomeGroupKind.MixBand, "Made for you", [Card("mix")]));
        int from = HomeLayoutDoc.Default.IndexOf(HomeGroupKind.MixBand);
        var moved = HomeLayoutReducer.Apply(HomeLayoutDoc.Default, new MoveHomeModule(from, 0)).Layout;

        var landing = HomeLandingProjection.Project(feed, HomeModuleTitles.Default, moved);
        int mix = IndexOf(landing.Rows, HomeRow.MixBand);
        int hero = IndexOf(landing.Rows, HomeRow.Hero);
        Assert.True(mix >= 0 && hero >= 0);
        Assert.True(mix < hero);
        // The Artists chrome row follows MixBand wherever it goes.
        Assert.Equal(mix + 1, IndexOf(landing.Rows, HomeRow.Artists));
    }

    [Fact]
    public void Projection_DefaultLayout_MatchesDesignedRowTable()
    {
        var landing = HomeLandingProjection.Project(HomeFeedView.Empty, HomeModuleTitles.Default, HomeLayoutDoc.Default);
        Assert.Equal(HomeLandingProjection.DefaultRows, landing.Rows);
    }

    [Fact]
    public void Projection_QueueAndBooks_CollapseOnlyWhenAdjacent()
    {
        int from = HomeLayoutDoc.Default.IndexOf(HomeGroupKind.RatedShelf);
        var apart = HomeLayoutReducer.Apply(HomeLayoutDoc.Default, new MoveHomeModule(from, 0)).Layout;

        var rows = HomeLandingProjection.Project(HomeFeedView.Empty, HomeModuleTitles.Default, apart).Rows;
        Assert.DoesNotContain(HomeRow.EpisodesAndBooks, rows);
        Assert.Contains(HomeRow.Queue, rows);
        Assert.Contains(HomeRow.Books, rows);
    }

    // ── HomeRow.Charts: chrome, never a HomeGroupKind ─────────────────────────────────────────────────────────────────

    [Fact]
    public void DefaultRows_ContainsExactlyOneCharts_ImmediatelyBeforeSections()
    {
        var rows = HomeLandingProjection.DefaultRows;
        Assert.Equal(1, rows.Count(r => r == HomeRow.Charts));
        int charts = IndexOf(rows, HomeRow.Charts);
        int sections = IndexOf(rows, HomeRow.Sections);
        Assert.True(charts >= 0 && sections >= 0);
        Assert.Equal(charts + 1, sections);
    }

    public static IEnumerable<object[]> LayoutsThatMustKeepChartsBeforeSections()
    {
        yield return new object[] { "default layout", HomeLayoutDoc.Default };

        var modulesHidden = HomeLayoutReducer.Apply(HomeLayoutDoc.Default, new SetHomeModuleHidden(HomeGroupKind.Hero, true)).Layout;
        modulesHidden = HomeLayoutReducer.Apply(modulesHidden, new SetHomeModuleHidden(HomeGroupKind.MixBand, true)).Layout;
        yield return new object[] { "unrelated modules hidden", modulesHidden };

        var podcastHidden = HomeLayoutReducer.Apply(HomeLayoutDoc.Default, new SetHomeModuleHidden(HomeGroupKind.PodcastShelf, true)).Layout;
        yield return new object[] { "PodcastShelf hidden (absent)", podcastHidden };

        int from = HomeLayoutDoc.Default.IndexOf(HomeGroupKind.PodcastShelf);
        var podcastMoved = HomeLayoutReducer.Apply(HomeLayoutDoc.Default, new MoveHomeModule(from, 0)).Layout;
        yield return new object[] { "PodcastShelf moved to front", podcastMoved };
    }

    [Theory]
    [MemberData(nameof(LayoutsThatMustKeepChartsBeforeSections))]
    public void ApplyLayout_PlacesExactlyOneCharts_ImmediatelyBeforeSections(string label, HomeLayoutDoc layout)
    {
        var landing = HomeLandingProjection.Project(HomeFeedView.Empty, HomeModuleTitles.Default, layout);
        Assert.Equal(1, landing.Rows.Count(r => r == HomeRow.Charts));
        int charts = IndexOf(landing.Rows, HomeRow.Charts);
        int sections = IndexOf(landing.Rows, HomeRow.Sections);
        Assert.True(charts >= 0 && sections >= 0, $"{label}: charts={charts} sections={sections}");
        Assert.Equal(charts + 1, sections);
    }

    [Fact]
    public void Charts_IsPresent_RegardlessOfWhichHomeGroupKindsTheFeedCarries()
    {
        TestScope.Fresh();
        var richFeed = FeedWith(
            new HomeGroup(HomeGroupKind.Hero, null, [Card("hero")]),
            new HomeGroup(HomeGroupKind.MixBand, "Made for you", [Card("mix")]),
            new HomeGroup(HomeGroupKind.PodcastShelf, "Podcasts", [Card("pod")]));
        Assert.Contains(HomeRow.Charts, HomeLandingProjection.Project(richFeed, HomeModuleTitles.Default).Rows);
        Assert.Contains(HomeRow.Charts, HomeLandingProjection.Project(HomeFeedView.Empty, HomeModuleTitles.Default).Rows);
    }

    [Fact]
    public void Charts_IsNotAHomeGroupKind_AndDefaultOrderCarriesNoChartsEntry()
    {
        Assert.DoesNotContain("Charts", Enum.GetNames<HomeGroupKind>());
        Assert.All(HomeLayoutModules.DefaultOrder, kind => Assert.NotEqual("charts", HomeLayoutModules.KindName(kind)));
    }

    [Fact]
    public void HidingEveryFixedModule_StillLeavesExactlyOneChartsInRows()
    {
        var layout = HomeLayoutDoc.Default;
        foreach (var kind in HomeLayoutModules.DefaultOrder)
            layout = HomeLayoutReducer.Apply(layout, new SetHomeModuleHidden(kind, true)).Layout;

        var landing = HomeLandingProjection.Project(HomeFeedView.Empty, HomeModuleTitles.Default, layout);
        Assert.Equal(1, landing.Rows.Count(r => r == HomeRow.Charts));
        Assert.Equal([HomeRow.Chips, HomeRow.Artists, HomeRow.Timeline, HomeRow.Charts, HomeRow.Sections, HomeRow.Tail], landing.Rows);
    }

    // ── the customizer's pure half ────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(HomeLayoutLoadFault.None, false, false, false)]         // healthy: no banner
    [InlineData(HomeLayoutLoadFault.Corrupt, true, false, true)]        // a load fault shows it
    [InlineData(HomeLayoutLoadFault.TooNew, true, false, true)]
    [InlineData(HomeLayoutLoadFault.Unreadable, true, false, true)]
    [InlineData(HomeLayoutLoadFault.None, true, false, true)]           // trap 4: a failed "Start fresh" still blocks writes
    [InlineData(HomeLayoutLoadFault.Corrupt, true, true, false)]        // dismissed for this mount
    [InlineData(HomeLayoutLoadFault.None, true, true, false)]
    public void CustomizerShowsFaultBanner_WhileFaultedOrBlocked_UntilDismissed(
        HomeLayoutLoadFault fault, bool writesBlocked, bool dismissed, bool expected)
        => Assert.Equal(expected, Home.CustomizerShowsFaultBanner(fault, writesBlocked, dismissed));

    [Theory]
    [InlineData(HomeGroupKind.Hero, "HERO")]
    [InlineData(HomeGroupKind.WeeklyPair, "PAIR")]
    [InlineData(HomeGroupKind.QuickGrid, "Jump back in")]
    [InlineData(HomeGroupKind.Recents, "Recents")]
    [InlineData(HomeGroupKind.MixBand, "Made for you")]
    [InlineData(HomeGroupKind.ChipCards, "Your top mixes")]
    [InlineData(HomeGroupKind.RadioDial, "Radio")]
    [InlineData(HomeGroupKind.QueueList, "Up next")]
    [InlineData(HomeGroupKind.RatedShelf, "Audiobooks for you")]
    [InlineData(HomeGroupKind.PodcastShelf, "Podcasts")]
    [InlineData(HomeGroupKind.Featured, "Editors' picks")]
    [InlineData(HomeGroupKind.DiscoverFeed, "Because you listened")]
    [InlineData(HomeGroupKind.Topic, "topic")]                          // not a landing module: its wire name
    public void CustomizerLabelOf_NamesEveryModule(HomeGroupKind kind, string expected)
        => Assert.Equal(expected, Home.CustomizerLabelOf(kind, HomeModuleTitles.Default, "HERO", "PAIR"));

    [Fact]
    public void CustomizerLabelOf_ReadsTheTitlesItIsHanded()
    {
        var titles = HomeModuleTitles.Default with { MadeForYou = "Voor jou" };
        Assert.Equal("Voor jou", Home.CustomizerLabelOf(HomeGroupKind.MixBand, titles, "", ""));
    }
}
