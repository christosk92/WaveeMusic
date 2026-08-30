using System;
using System.Collections.Generic;
using Wavee.Core.Sidebar;
using Xunit;

namespace Wavee.Tests;

/// <summary>
/// PHASE 1 / DECISION A — the shortcut band as an ORDINARY SECTION, and the sentinel-id addressing that makes editing it
/// one decision instead of a copy per call site.
///
/// <para>What did NOT move is as load-bearing as what did: <c>SidebarCustomLayout.TopBar</c> is still the wire member,
/// still mutated only through <c>AddTopBarItem</c>/<c>MoveTopBarItem</c>/<c>RemoveTopBarItem</c>, still capped by the
/// reducer and still carried by the same undo ring — all of which <c>SidebarLayoutReducerTests</c> and
/// <c>SidebarLayoutJsonTests</c> already pin and this suite deliberately does not restate. What moved is the RENDER
/// PATH: <see cref="SidebarShortcutsSection"/> materialises the band as a <c>StaticLinks</c> section carrying the
/// sentinel id, prepended to the document handed to the pane and to nothing else.</para>
///
/// <para>Both halves are engine-free Wavee.Core, and the two DOCUMENTS that consume them
/// (<c>SidebarBuiltInDocuments.Classic</c>, <c>LibraryV3Document.Build</c>) are source-included, so every rule below is
/// driven against production code. The third consumer — Curated's <c>CuratedSidebar.BuildDocument</c> — is engine-bound
/// and calls the very same <c>Prepend</c>, which is what the Prepend section here covers.</para>
/// </summary>
public class SidebarShortcutsSectionTests
{
    static SidebarItemSpec Route(string key, string id = "itm_r", bool hidden = false)
        => new(id, SidebarItemTarget.Route, key, Hidden: hidden);

    static SidebarItemSpec Entity(string uri, string id = "itm_e")
        => new(id, SidebarItemTarget.Entity, uri, SidebarEntityKind.Playlist);

    static SidebarCustomLayout Doc(params SidebarSectionSpec[] sections)
        => new(SidebarTemplates.Curated, sections);

    static SidebarSectionSpec Sec(string id, SidebarSectionKind kind)
        => new(id, kind);

    // ── the synthesized section ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The shape, field by field. It is a <c>StaticLinks</c> section because that is exactly what the band is —
    /// a hand-authored list of routes / entities / tracks / bound actions — which is what lets the planner, the row
    /// slot, the rail, the reorder band and the selection indicator serve it with no new code.</summary>
    [Fact]
    public void From_IsAStaticLinksSectionUnderTheSentinelId()
    {
        var band = new[] { Route("home", "itm_home"), Route("search", "itm_search") };
        var section = SidebarShortcutsSection.From(band);

        Assert.Equal(SidebarIds.TopBarSection, section.Id);
        Assert.True(SidebarIds.IsTopBar(section.Id));
        Assert.Equal(SidebarSectionKind.StaticLinks, section.Kind);
        Assert.Same(band, section.Items);

        // The title follows the culture through a KEY, never a frozen literal.
        Assert.Null(section.Title);
        Assert.Equal(SidebarShortcutsSection.TitleLocKey, section.TitleLocKey);
        Assert.Equal("sidebar.topbar.title", SidebarShortcutsSection.TitleLocKey);
    }

    /// <summary>The DISPLAY preset is the StaticLinks one (<c>Links</c>), never the CollectionShortcuts one: defect 8's
    /// <c>CountBadges = true</c> is a flag <c>AllowsDisplayField(StaticLinks, CountBadges)</c> forbids, so a band
    /// carrying it would have a default the user can neither see nor change. <c>ShowInRail</c> is a REQUIREMENT of
    /// Decision A (the 56-DIP rail form of the band), not an inherited accident, so it is asserted rather than
    /// assumed.</summary>
    [Fact]
    public void From_TakesTheLinksPresetAndAlwaysShowsInTheRail()
    {
        var section = SidebarShortcutsSection.From(SidebarCustomLayout.DefaultTopBar);

        Assert.Equal(SidebarDisplayOptions.Links with { ShowInRail = true }, section.Opts);
        Assert.True(section.Opts.ShowInRail);
        Assert.False(section.Opts.CountBadges);
        Assert.False(SidebarSectionKinds.AllowsDisplayField(SidebarSectionKind.StaticLinks,
                                                            SidebarDisplayField.CountBadges));
    }

    /// <summary>NOT collapsible, and not hidden. The sentinel is not in <c>Sections</c>, so there is no section-scoped
    /// command that could ever write those bits back — a <c>true</c> here would be a state the user could not undo.</summary>
    [Fact]
    public void From_IsNeitherHiddenNorCollapsed()
    {
        var section = SidebarShortcutsSection.From(SidebarCustomLayout.DefaultTopBar);
        Assert.False(section.Hidden);
        Assert.False(section.Collapsed);

        // …and every section-scoped command addressed at the sentinel is an UnknownSection rejection, which is exactly
        // why the two bits above must stay false: nothing can flip them back.
        var doc = SidebarShortcutsSection.Prepend(Doc(Sec("sec_a", SidebarSectionKind.Pinned)),
                                                  SidebarCustomLayout.DefaultTopBar);
        foreach (var cmd in new SidebarCommand[]
        {
            new SetSectionHidden(SidebarIds.TopBarSection, true),
            new SetSectionCollapsed(SidebarIds.TopBarSection, true),
            new RemoveSection(SidebarIds.TopBarSection),
            new DuplicateSection(SidebarIds.TopBarSection),
            new MoveSection(SidebarIds.TopBarSection, null, 0),
        })
        {
            // Against the PERSISTED document — the render-path document is never dispatched against.
            var r = SidebarLayoutReducer.Apply(Doc(Sec("sec_a", SidebarSectionKind.Pinned)), cmd);
            Assert.False(r.Changed);
            Assert.Equal(SidebarRejectReason.UnknownSection, r.Reason);
        }

        // The materialised document does carry the section, which is the point: it renders, it just is not addressable.
        Assert.NotNull(doc.Find(SidebarIds.TopBarSection));
    }

    /// <summary>An EMPTY band means the user emptied it on purpose (Home is genuinely removable) and contributes no
    /// section at all — no header, no rows, no rail tiles. Null is accepted so a probe/headless caller with no
    /// preference service is legal.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Renders_IsFalseForNothingToShow(bool useNull)
    {
        IReadOnlyList<SidebarItemSpec>? band = useNull ? null : Array.Empty<SidebarItemSpec>();
        Assert.False(SidebarShortcutsSection.Renders(band));
        Assert.True(SidebarShortcutsSection.Renders(SidebarCustomLayout.DefaultTopBar));
    }

    // ── Prepend: the render-path projection ──────────────────────────────────────────────────────────────────────────

    /// <summary>An empty band costs the caller NOTHING — not a copy, not a new list — so the pane's first section header
    /// (which hosts the quick layout menu) falls back to the document's own first section by reference identity.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Prepend_OfAnEmptyBandReturnsTheInputUnchanged(bool useNull)
    {
        var doc = Doc(Sec("sec_a", SidebarSectionKind.Pinned), Sec("sec_b", SidebarSectionKind.PlaylistTree));
        IReadOnlyList<SidebarItemSpec>? band = useNull ? null : Array.Empty<SidebarItemSpec>();

        Assert.Same(doc, SidebarShortcutsSection.Prepend(doc, band));
    }

    /// <summary>A non-empty band is a HEAD INSERTION: the shortcuts section is index 0 and every original section keeps
    /// its order and its identity behind it. The input document is never touched.</summary>
    [Fact]
    public void Prepend_PutsTheBandFirstAndLeavesTheOriginalSectionsIntact()
    {
        var doc = Doc(Sec("sec_a", SidebarSectionKind.Pinned),
                      Sec("sec_b", SidebarSectionKind.PlaylistTree),
                      Sec("sec_c", SidebarSectionKind.Divider));
        var band = new[] { Route("home", "itm_home") };

        var rendered = SidebarShortcutsSection.Prepend(doc, band);

        Assert.NotSame(doc, rendered);
        Assert.Equal(4, rendered.Sections.Count);
        Assert.Equal(SidebarIds.TopBarSection, rendered.Sections[0].Id);
        for (int i = 0; i < doc.Sections.Count; i++)
            Assert.Same(doc.Sections[i], rendered.Sections[i + 1]);

        // Everything else about the document rides along untouched — the template identity included.
        Assert.Equal(doc.TemplateId, rendered.TemplateId);
        Assert.Equal(3, doc.Sections.Count);                 // …and the input was not mutated
    }

    // ── ContainsRoute: the one owner of "is this destination already a shortcut?" ─────────────────────────────────────

    [Fact]
    public void ContainsRoute_OnlyCountsVisibleRouteItems()
    {
        Assert.True(SidebarShortcutsSection.ContainsRoute(new[] { Route("liked") }, "liked"));
        Assert.False(SidebarShortcutsSection.ContainsRoute(new[] { Route("liked") }, "home"));
        Assert.False(SidebarShortcutsSection.ContainsRoute(new[] { Route("liked") }, "Liked"));   // ordinal, on purpose

        // A HIDDEN shortcut does not render, so it cannot be the reason another surface drops its own row.
        Assert.False(SidebarShortcutsSection.ContainsRoute(new[] { Route("liked", hidden: true) }, "liked"));

        // An ENTITY item whose uri maps onto the same destination is a DIFFERENT row — different art, different menu —
        // so it deliberately does not count.
        Assert.False(SidebarShortcutsSection.ContainsRoute(
            new[] { Entity(SidebarPinId.LikedSongsUri) }, "liked"));

        Assert.False(SidebarShortcutsSection.ContainsRoute(null, "liked"));
        Assert.False(SidebarShortcutsSection.ContainsRoute(new[] { Route("liked") }, ""));
    }

    // ── document 1: Classic ──────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Classic's LOCKED document gains the band as its first section and is otherwise byte-for-byte what it
    /// was. It is never persisted, so the sentinel id can never reach the wire.</summary>
    [Fact]
    public void Classic_PrependsTheBandAndIsOtherwiseIdentical()
    {
        var plain = SidebarBuiltInDocuments.Classic(true, true, true);
        var withBand = SidebarBuiltInDocuments.Classic(true, true, true, SidebarCustomLayout.DefaultTopBar);

        Assert.Equal(plain.Sections.Count + 1, withBand.Sections.Count);
        Assert.Equal(SidebarIds.TopBarSection, withBand.Sections[0].Id);
        Assert.Equal(SidebarBuiltInDocuments.ClassicId, withBand.TemplateId);

        for (int i = 0; i < plain.Sections.Count; i++)
            Assert.Equal(plain.Sections[i].Id, withBand.Sections[i + 1].Id);

        // Pinned is STILL Classic's first real section — the band is ahead of it, not instead of it.
        Assert.Equal(SidebarBuiltInDocuments.PinnedId, withBand.Sections[1].Id);
    }

    [Fact]
    public void Classic_WithNoBandIsExactlyTheDocumentItAlwaysWas()
    {
        var plain = SidebarBuiltInDocuments.Classic(true, true, true);
        var empty = SidebarBuiltInDocuments.Classic(true, true, true, Array.Empty<SidebarItemSpec>());

        Assert.Equal(SidebarBuiltInDocuments.PinnedId, empty.Sections[0].Id);
        Assert.Equal(plain.Sections.Count, empty.Sections.Count);
    }

    // ── document 2: Library V3 ───────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Decision C is satisfied BY Decision A, not by a V3-specific branch: V3's navigation is the same
    /// materialised section, placed ahead of the pin band because it is the APP's navigation and not the library's.</summary>
    [Fact]
    public void LibraryV3_PutsShortcutsAheadOfThePinBand()
    {
        var state = new LibraryV3DocState(HasPins: true);
        var doc = LibraryV3Document.Build(in state, SidebarCustomLayout.DefaultTopBar);

        Assert.Equal(SidebarIds.TopBarSection, doc.Sections[0].Id);
        Assert.Equal(LibraryV3Document.PinsId, doc.Sections[1].Id);

        // …and with no band, V3's document is exactly what it was.
        var bare = LibraryV3Document.Build(in state);
        Assert.Equal(LibraryV3Document.PinsId, bare.Sections[0].Id);
        Assert.Equal(doc.Sections.Count - 1, bare.Sections.Count);
    }

    /// <summary>THE DROP RULE, both directions. V3's own <c>v3.liked</c> row is dropped exactly when the band already
    /// carries a <c>liked</c> ROUTE item — two rows to the same destination a hand apart is the duplication Decision A
    /// exists to remove. It is NOT dropped unconditionally: a user who removed Liked from their shortcuts still gets
    /// V3's own row, which is the §3.0 obligation.</summary>
    [Fact]
    public void LibraryV3_DropsItsOwnLikedRowOnlyWhenTheBandAlreadyCarriesThatRoute()
    {
        var state = new LibraryV3DocState();                 // LikedVisible: not pinned, not searching, not drilled
        Assert.True(state.LikedVisible);

        var withLiked = LibraryV3Document.Build(in state,
            new[] { Route("home", "itm_home"), Route(LibraryV3Document.LikedRouteKey, "itm_liked") });
        Assert.Null(withLiked.Find(LibraryV3Document.LikedId));

        var withoutLiked = LibraryV3Document.Build(in state, new[] { Route("home", "itm_home") });
        Assert.NotNull(withoutLiked.Find(LibraryV3Document.LikedId));

        // No band at all ⇒ the row is kept, which is the pre-Phase-1 behaviour unchanged.
        Assert.NotNull(LibraryV3Document.Build(in state).Find(LibraryV3Document.LikedId));

        // An ENTITY shortcut to the same songs is a different row, so V3's route row survives beside it.
        var entityShortcut = LibraryV3Document.Build(in state,
            new[] { Entity(SidebarPinId.LikedSongsUri, "itm_ent") });
        Assert.NotNull(entityShortcut.Find(LibraryV3Document.LikedId));

        // A HIDDEN liked shortcut renders nothing, so it must not suppress V3's row either.
        var hidden = LibraryV3Document.Build(in state,
            new[] { Route(LibraryV3Document.LikedRouteKey, "itm_liked", hidden: true) });
        Assert.NotNull(hidden.Find(LibraryV3Document.LikedId));
    }

    // ── sentinel-id dispatch routing ─────────────────────────────────────────────────────────────────────────────────
    //
    // The section carries the sentinel, so an edit addressed at it must reach the BAND's command family. That choice
    // used to be spelled out at each call site; it is now one decision in Wavee.Core, which is what makes "a move inside
    // the Shortcuts section emits MoveTopBarItem, not MoveItem" assertable at all.

    [Fact]
    public void ItemCommands_RouteTheSentinelToTheBandFamily()
    {
        var item = Route("search", "itm_s");

        var add = Assert.IsType<AddTopBarItem>(SidebarItemCommands.Add(SidebarIds.TopBarSection, item, 2));
        Assert.Same(item, add.Item);
        Assert.Equal(2, add.Index);

        var move = Assert.IsType<MoveTopBarItem>(SidebarItemCommands.Move(SidebarIds.TopBarSection, 0, 3));
        Assert.Equal(0, move.FromIndex);
        Assert.Equal(3, move.ToIndex);

        var remove = Assert.IsType<RemoveTopBarItem>(
            SidebarItemCommands.Remove(SidebarIds.TopBarSection, SidebarIds.TopBarHomeItem));
        Assert.Equal(SidebarIds.TopBarHomeItem, remove.ItemId);
    }

    [Fact]
    public void ItemCommands_RouteARealSectionIdToTheGenericFamily()
    {
        var item = Route("search", "itm_s");

        var add = Assert.IsType<AddItem>(SidebarItemCommands.Add("sec_a", item, 2));
        Assert.Equal("sec_a", add.SectionId);
        Assert.Same(item, add.Item);
        Assert.Equal(2, add.Index);

        // A within-section move, expressed as MoveItem's cross-section shape with both ends the same section.
        var move = Assert.IsType<MoveItem>(SidebarItemCommands.Move("sec_a", 0, 3));
        Assert.Equal("sec_a", move.FromSectionId);
        Assert.Equal("sec_a", move.ToSectionId);
        Assert.Equal(0, move.FromIndex);
        Assert.Equal(3, move.ToIndex);

        var remove = Assert.IsType<RemoveItem>(SidebarItemCommands.Remove("sec_a", "itm_x"));
        Assert.Equal("sec_a", remove.SectionId);
        Assert.Equal("itm_x", remove.ItemId);
    }

    /// <summary>The commands the routing produces are the ones the reducer actually executes — the assertion the whole
    /// helper exists for. A raw <c>MoveItem</c> at the sentinel would be a silent <c>UnknownSection</c> rejection: a drag
    /// that snaps back with no message.</summary>
    [Fact]
    public void ItemCommands_TheRoutedBandMoveIsTheOneTheReducerAccepts()
    {
        var bare = Doc(Sec("sec_a", SidebarSectionKind.Pinned));
        var doc = bare with
        {
            TopBar = [Route("home", "itm_home"), Route("search", "itm_search"), Route("liked", "itm_liked")],
        };

        var routed = SidebarLayoutReducer.Apply(doc, SidebarItemCommands.Move(SidebarIds.TopBarSection, 0, 2));
        Assert.True(routed.Changed);
        Assert.Equal(new[] { "search", "liked", "home" }, KeysOf(routed.Layout.EffectiveTopBar));

        // The generic family aimed at the sentinel is the mistake this helper exists to prevent.
        var raw = SidebarLayoutReducer.Apply(doc,
            new MoveItem(SidebarIds.TopBarSection, 0, SidebarIds.TopBarSection, 2));
        Assert.False(raw.Changed);
        Assert.Equal(SidebarRejectReason.UnknownSection, raw.Reason);

        static string[] KeysOf(IReadOnlyList<SidebarItemSpec> band)
        {
            var keys = new string[band.Count];
            for (int i = 0; i < keys.Length; i++) keys[i] = band[i].Key;
            return keys;
        }
    }

    /// <summary>The READ side has to agree with the write side or a panel would edit one list and display another:
    /// the sentinel addresses the band, a real id addresses that section's items, and an id the document does not
    /// contain is an EMPTY list rather than a null-reference at the call site.</summary>
    [Fact]
    public void ItemsIn_AddressesTheBandForTheSentinelAndTheSectionOtherwise()
    {
        var links = Sec("sec_a", SidebarSectionKind.StaticLinks) with { Items = [Route("albums", "itm_albums")] };
        var doc = Doc(links) with { TopBar = [Route("home", "itm_home"), Route("search", "itm_search")] };

        Assert.Same(doc.EffectiveTopBar, SidebarItemCommands.ItemsIn(doc, SidebarIds.TopBarSection));
        Assert.Equal(2, SidebarItemCommands.ItemsIn(doc, SidebarIds.TopBarSection).Count);

        var section = SidebarItemCommands.ItemsIn(doc, "sec_a");
        Assert.Single(section);
        Assert.Equal("albums", section[0].Key);

        Assert.Empty(SidebarItemCommands.ItemsIn(doc, "sec_missing"));
        Assert.Empty(SidebarItemCommands.ItemsIn(doc, null));
        Assert.Empty(SidebarItemCommands.ItemsIn(null, "sec_a"));

        // A NEVER-CUSTOMIZED document still addresses a band — the built-in Home shortcut.
        var fresh = Doc(Sec("sec_a", SidebarSectionKind.Pinned));
        Assert.Null(fresh.TopBar);
        Assert.Single(SidebarItemCommands.ItemsIn(fresh, SidebarIds.TopBarSection));
    }

    [Fact]
    public void FindItem_LooksInsideWhicheverListTheSectionIdAddresses()
    {
        var links = Sec("sec_a", SidebarSectionKind.StaticLinks) with { Items = [Route("albums", "itm_albums")] };
        var doc = Doc(links) with { TopBar = [Route("home", "itm_home")] };

        Assert.Equal("home", SidebarItemCommands.FindItem(doc, SidebarIds.TopBarSection, "itm_home")!.Key);
        Assert.Equal("albums", SidebarItemCommands.FindItem(doc, "sec_a", "itm_albums")!.Key);

        // The two lists are NOT searched together: an item id is only found in the list its section id names.
        Assert.Null(SidebarItemCommands.FindItem(doc, "sec_a", "itm_home"));
        Assert.Null(SidebarItemCommands.FindItem(doc, SidebarIds.TopBarSection, "itm_albums"));

        Assert.Null(SidebarItemCommands.FindItem(doc, "sec_a", null));
        Assert.Null(SidebarItemCommands.FindItem(doc, "sec_a", ""));
    }
}
