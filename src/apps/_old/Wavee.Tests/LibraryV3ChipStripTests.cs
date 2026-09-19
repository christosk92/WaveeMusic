using Xunit;

namespace Wavee.Tests;

/// <summary>The Library V3 filter rail's ORDER, driven directly: what idle/filtered/fused look like, what a tap on
/// each position writes back, and how roving focus survives a relayout. These are the rules the eye reads — the ✕
/// pops in only once something is active, the qualifier only ever rides Playlists — so they are pinned away from
/// the renderer (the <c>HomeFacetStripTests</c> pattern).</summary>
public sealed class LibraryV3ChipStripTests
{
    const int All = (int)SidebarV3Filter.All;
    const int Playlists = (int)SidebarV3Filter.Playlists;
    const int Podcasts = (int)SidebarV3Filter.Podcasts;
    const int Albums = (int)SidebarV3Filter.Albums;
    const int Artists = (int)SidebarV3Filter.Artists;
    const int Any = (int)SidebarV3Qualifier.Any;
    const int ByYou = (int)SidebarV3Qualifier.ByYou;
    const int BySpotify = (int)SidebarV3Qualifier.BySpotify;
    const int Mixed = (int)SidebarV3Qualifier.Mixed;

    // ── idle ──────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Idle_NoFilter_IsFourUnselectedFacets_NoClear()
    {
        var slots = LibraryV3ChipStrip.Slots(All, Any, qualifiersAvailable: true);

        Assert.Equal(4, slots.Count);
        Assert.All(slots, s => Assert.Equal(V3ChipKind.Facet, s.Kind));
        Assert.All(slots, s => Assert.False(s.Selected));
        Assert.DoesNotContain(slots, s => s.Kind == V3ChipKind.Clear);
        Assert.Equal(new[] { Playlists, Podcasts, Albums, Artists }, new[] { slots[0].Code, slots[1].Code, slots[2].Code, slots[3].Code });

        // Tapping an idle facet SELECTS it (Any qualifier — a fresh filter never carries over a stale sub-filter).
        Assert.Equal(Playlists, slots[0].SelectFilter);
        Assert.Equal(Any, slots[0].SelectQualifier);
        Assert.Equal("v3f" + Playlists, slots[0].Key);
    }

    // ── filtered, no qualifier available/relevant ────────────────────────────────────────────────────────────────

    [Fact]
    public void Filtered_NonPlaylists_IsClearPlusTheOneSelectedFacet_NoOptions()
    {
        foreach (var f in new[] { Podcasts, Albums, Artists })
        {
            var slots = LibraryV3ChipStrip.Slots(f, Any, qualifiersAvailable: true);

            Assert.Equal(2, slots.Count);
            Assert.Equal(V3ChipKind.Clear, slots[0].Kind);
            Assert.Equal(V3ChipKind.Facet, slots[1].Kind);
            Assert.True(slots[1].Selected);
            Assert.Equal(f, slots[1].Code);
            Assert.DoesNotContain(slots, s => s.Kind == V3ChipKind.Option);
        }
    }

    [Fact]
    public void Filtered_Playlists_QualifiersUnavailable_NoOptionsSpill()
    {
        var slots = LibraryV3ChipStrip.Slots(Playlists, Any, qualifiersAvailable: false);

        Assert.Equal(2, slots.Count);
        Assert.Equal(V3ChipKind.Clear, slots[0].Kind);
        Assert.Equal(V3ChipKind.Facet, slots[1].Kind);
        Assert.True(slots[1].Selected);
        Assert.DoesNotContain(slots, s => s.Kind == V3ChipKind.Option);
    }

    // ── filtered, Playlists with qualifiers evidenced (spilled) ──────────────────────────────────────────────────

    [Fact]
    public void Filtered_Playlists_QualifiersAvailable_SpillsTheThreeOptionsAfterTheFacet()
    {
        var slots = LibraryV3ChipStrip.Slots(Playlists, Any, qualifiersAvailable: true);

        Assert.Equal(5, slots.Count);
        Assert.Equal(V3ChipKind.Clear, slots[0].Kind);

        Assert.Equal(V3ChipKind.Facet, slots[1].Kind);
        Assert.True(slots[1].Selected);
        Assert.Equal(Playlists, slots[1].Code);
        Assert.Equal("v3f" + Playlists, slots[1].Key);

        Assert.Equal(V3ChipKind.Option, slots[2].Kind);
        Assert.Equal(ByYou, slots[2].Code);
        Assert.Equal("v3q" + ByYou, slots[2].Key);
        Assert.False(slots[2].Selected);
        Assert.Equal(Playlists, slots[2].SelectFilter);
        Assert.Equal(ByYou, slots[2].SelectQualifier);

        Assert.Equal(BySpotify, slots[3].Code);
        Assert.Equal(Mixed, slots[4].Code);
    }

    // ── fused ─────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Fused_PlaylistsWithQualifierPicked_IsClearPlusOneFusedSlot_NoOptions()
    {
        var slots = LibraryV3ChipStrip.Slots(Playlists, ByYou, qualifiersAvailable: true);

        Assert.Equal(2, slots.Count);
        Assert.Equal(V3ChipKind.Clear, slots[0].Kind);

        var fused = slots[1];
        Assert.Equal(V3ChipKind.Fused, fused.Kind);
        Assert.True(fused.Selected);
        Assert.Equal(Playlists, fused.Code);
        Assert.DoesNotContain(slots, s => s.Kind == V3ChipKind.Option);

        // Tapping the fused pill steps back ONE level — drops the qualifier, keeps the facet — not all the way to All.
        Assert.Equal(Playlists, fused.SelectFilter);
        Assert.Equal(Any, fused.SelectQualifier);
    }

    [Fact]
    public void Fused_And_LooseFacet_ShareTheSameKey_ForTheSameCode()
    {
        // The morph mechanism: the reconciler must see ONE node across the loose ⇄ fused transition.
        var loose = LibraryV3ChipStrip.Slots(Playlists, Any, qualifiersAvailable: true)[1];
        var fused = LibraryV3ChipStrip.Slots(Playlists, ByYou, qualifiersAvailable: true)[1];

        Assert.Equal(V3ChipKind.Facet, loose.Kind);
        Assert.Equal(V3ChipKind.Fused, fused.Kind);
        Assert.Equal(loose.Key, fused.Key);
        Assert.Equal("v3f" + Playlists, loose.Key);
    }

    [Fact]
    public void QualifierPicked_ButNotPlaylists_NeverFuses()
    {
        // A stale/unevidenced qualifier under a non-Playlists filter must not fuse — Slots is a pure function of its
        // three inputs and never assumes the caller already normalized qualifier against filter.
        var slots = LibraryV3ChipStrip.Slots(Albums, ByYou, qualifiersAvailable: true);

        Assert.Equal(2, slots.Count);
        Assert.Equal(V3ChipKind.Facet, slots[1].Kind);
        Assert.DoesNotContain(slots, s => s.Kind == V3ChipKind.Fused);
    }

    // ── what a tap writes ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ClearSlot_WritesAllAndAny()
    {
        var clear = LibraryV3ChipStrip.Slots(Playlists, ByYou, qualifiersAvailable: true)[0];
        Assert.Equal(V3ChipKind.Clear, clear.Kind);
        Assert.Equal(All, clear.SelectFilter);
        Assert.Equal(Any, clear.SelectQualifier);
    }

    [Fact]
    public void SelectedFacetSlot_TapClears_TheChosenPillIsItsOwnToggle()
    {
        var facet = LibraryV3ChipStrip.Slots(Podcasts, Any, qualifiersAvailable: false)[1];
        Assert.True(facet.Selected);
        Assert.Equal(All, facet.SelectFilter);
        Assert.Equal(Any, facet.SelectQualifier);
    }

    [Fact]
    public void OptionSlot_WritesItsOwnFilterAndQualifier()
    {
        var option = LibraryV3ChipStrip.Slots(Playlists, Any, qualifiersAvailable: true)[3]; // BySpotify
        Assert.Equal(V3ChipKind.Option, option.Kind);
        Assert.Equal(BySpotify, option.Code);
        Assert.Equal(Playlists, option.SelectFilter);
        Assert.Equal(BySpotify, option.SelectQualifier);
    }

    // ── FocusIndex ────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void FocusIndex_FindsTheMatchingKey()
    {
        var slots = LibraryV3ChipStrip.Slots(All, Any, qualifiersAvailable: false);
        Assert.Equal(2, LibraryV3ChipStrip.FocusIndex(slots, "v3f" + Albums));
    }

    [Fact]
    public void FocusIndex_FallsBackToZero_WhenTheKeyIsGone()
    {
        // The focused chip (an option, say) vanished from the rail on this render — focus lands on the leading slot
        // instead of throwing or leaving the rail with no roving position at all.
        var slots = LibraryV3ChipStrip.Slots(Albums, Any, qualifiersAvailable: false);
        Assert.Equal(0, LibraryV3ChipStrip.FocusIndex(slots, "v3q" + ByYou));
        Assert.Equal(0, LibraryV3ChipStrip.FocusIndex(slots, null));
    }

    [Fact]
    public void FocusIndex_SurvivesTheLooseToFusedRelayout_BecauseTheKeyIsShared()
    {
        var loose = LibraryV3ChipStrip.Slots(Playlists, Any, qualifiersAvailable: true);
        int focusedAt = LibraryV3ChipStrip.FocusIndex(loose, "v3f" + Playlists);
        Assert.Equal(1, focusedAt);

        var fused = LibraryV3ChipStrip.Slots(Playlists, ByYou, qualifiersAvailable: true);
        Assert.Equal(1, LibraryV3ChipStrip.FocusIndex(fused, "v3f" + Playlists));
    }

    // ── Route (issue #85, H4 approach 3) ─────────────────────────────────────────────────────────────────────────────
    // Exactly Albums/Artists/Podcasts have an actual library page; Playlists has no "all playlists" destination, so
    // its chip — loose OR fused — never carries one, same as Clear and every Option slot.

    [Theory]
    [InlineData(Albums, "albums")]
    [InlineData(Artists, "artists")]
    [InlineData(Podcasts, "podcasts")]
    [InlineData(Playlists, null)]
    public void RouteFor_IsPopulatedForExactlyTheThreeKindsWithAPage(int filter, string? expected)
        => Assert.Equal(expected, LibraryV3ChipStrip.RouteFor(filter));

    [Fact]
    public void IdleFacets_CarryTheirRoute_ExceptPlaylists()
    {
        var slots = LibraryV3ChipStrip.Slots(All, Any, qualifiersAvailable: true);
        foreach (var s in slots)
            Assert.Equal(LibraryV3ChipStrip.RouteFor(s.Code), s.Route);
        Assert.Null(slots[0].Route);   // Playlists is always index 0 in the idle order
    }

    [Fact]
    public void SelectedFacetSlot_StillCarriesItsRoute()
    {
        var facet = LibraryV3ChipStrip.Slots(Podcasts, Any, qualifiersAvailable: false)[1];
        Assert.Equal("podcasts", facet.Route);
    }

    [Fact]
    public void ClearAndOptionSlots_NeverCarryARoute()
    {
        var slots = LibraryV3ChipStrip.Slots(Playlists, Any, qualifiersAvailable: true);
        Assert.Equal(V3ChipKind.Clear, slots[0].Kind);
        Assert.Null(slots[0].Route);
        foreach (var s in slots)
            if (s.Kind == V3ChipKind.Option) Assert.Null(s.Route);
    }

    [Fact]
    public void FusedPlaylistsSlot_HasNoRoute_PlaylistsNeverHasOne()
    {
        var fused = LibraryV3ChipStrip.Slots(Playlists, ByYou, qualifiersAvailable: true)[1];
        Assert.Equal(V3ChipKind.Fused, fused.Kind);
        Assert.Null(fused.Route);
    }

    [Fact]
    public void ATap_OnlyEverWritesTheFilterOrQualifier_RouteIsASeparateSecondaryField()
    {
        // The contract H4 decided: Route is data a RENDERER may act on with a secondary gesture (Library V3 chose a
        // double-click) — it must never change what SelectFilter/SelectQualifier themselves write for a plain tap.
        var albums = LibraryV3ChipStrip.Slots(All, Any, qualifiersAvailable: false)[2];
        Assert.Equal(Albums, albums.Code);
        Assert.Equal("albums", albums.Route);
        Assert.Equal(Albums, albums.SelectFilter);   // a tap still only selects the Albums filter
        Assert.Equal(Any, albums.SelectQualifier);
    }
}
