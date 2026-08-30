using System;
using System.Collections.Generic;
using Wavee.Core.Sidebar;
using Xunit;

namespace Wavee.Tests;

/// <summary>
/// PHASE 2 / DECISION B — the pure rules an edit session implies (<see cref="SidebarEditPlan"/>), plus the two
/// band-slot → command translations the canvas commits through.
///
/// <para>"Customize" stopped being a page that redraws the sidebar and became a MODE OVER THE LIVE PANE, so every
/// decision the canvas makes — which sections reveal their rows, whether section drag is armed, what a card's count
/// says, and how a dropped card or a dropped palette chip becomes ONE undoable command — lives in the engine-free half
/// and is driven here against production code rather than only by the eye.</para>
///
/// <para><b>The index trap this suite exists for.</b> Two index spaces meet in the translations: BAND SLOTS enumerate
/// the <c>SectionCard</c> rows of the PLAN (which is built over the RENDER-PATH document — the one carrying Phase 1's
/// materialised Shortcuts head at index 0), while <c>MoveSection.NewIndex</c>/<c>AddSection.Index</c> index the
/// PERSISTED document, which does not contain that head at all. Every row array below is therefore built over the
/// render document while the command is asserted against the persisted one, so an off-by-one would fail here rather
/// than silently file a section one slot too high.</para>
/// </summary>
public class SidebarEditPlanTests
{
    // ── helpers ──────────────────────────────────────────────────────────────────────────────────────────────────────

    static SidebarSectionSpec Sec(string id, SidebarSectionKind kind, bool hidden = false,
        IReadOnlyList<SidebarItemSpec>? items = null, IReadOnlyList<SidebarSectionSpec>? children = null)
        => new(id, kind, Title: null, TitleLocKey: null, Hidden: hidden, Collapsed: false, Display: null,
               Items: items, Query: null, Children: children, Extension: null);

    static SidebarItemSpec Route(string key, string id, bool hidden = false)
        => new(id, SidebarItemTarget.Route, key, Hidden: hidden);

    static SidebarCustomLayout Doc(params SidebarSectionSpec[] sections)
        => new(SidebarTemplates.Curated, sections);

    /// <summary>The render document a pane in edit mode actually plans: the persisted document with the materialised
    /// Shortcuts head prepended, exactly as <c>CuratedSidebar</c> builds it.</summary>
    static SidebarCustomLayout Rendered(SidebarCustomLayout persisted)
        => SidebarShortcutsSection.Prepend(persisted, SidebarCustomLayout.DefaultTopBar);

    /// <summary>The card rows <c>SidebarRowPlanner.BuildEdit</c> emits for a document: ONE <c>SectionCard</c> per
    /// top-level section this build understands, in document order, with the card's honest count. The shape is copied
    /// from BuildEdit deliberately — the rules under test consume rows, not a planner.</summary>
    static List<SidebarRow> Cards(SidebarCustomLayout document)
    {
        var rows = new List<SidebarRow>();
        foreach (var s in document.Sections)
        {
            if (!SidebarSectionKinds.IsKnown(s.Kind)) continue;
            rows.Add(new SidebarRow(SidebarRowKind.SectionCard, s.Id, 0, -1, SidebarEditPlan.CardCount(s), s.Id));
        }
        return rows;
    }

    // ── ShowsBody ────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Exactly ONE section reveals its real rows under its card. One at a time on purpose: a 60-row expanded
    /// sidebar turns section dragging into a scroll-fight, and a card-only plan has the uniform pitch
    /// <c>Reorderable</c> wants.</summary>
    [Fact]
    public void ShowsBody_RevealsOnlyTheExpandedSection()
    {
        var a = Sec("sec_a", SidebarSectionKind.StaticLinks);
        var b = Sec("sec_b", SidebarSectionKind.EntityList);
        var edit = new SidebarEditState(ExpandedSection: "sec_a");

        Assert.True(SidebarEditPlan.ShowsBody(in edit, a));
        Assert.False(SidebarEditPlan.ShowsBody(in edit, b));

        // No session state at all ⇒ every section is a card.
        var none = new SidebarEditState();
        Assert.False(SidebarEditPlan.ShowsBody(in none, a));
        Assert.False(SidebarEditPlan.ShowsBody(in none, b));

        // A blank id is "nothing expanded", not "the section whose id is empty".
        var blank = new SidebarEditState(ExpandedSection: "");
        Assert.False(SidebarEditPlan.ShowsBody(in blank, a));
    }

    /// <summary>"Show section contents" reveals EVERY visible section's body at once, for item-level work.</summary>
    [Fact]
    public void ShowsBody_ShowContentsRevealsEveryVisibleSection()
    {
        var edit = new SidebarEditState(ShowContents: true);
        Assert.True(SidebarEditPlan.ShowsBody(in edit, Sec("sec_a", SidebarSectionKind.StaticLinks)));
        Assert.True(SidebarEditPlan.ShowsBody(in edit, Sec("sec_b", SidebarSectionKind.EntityList)));
        Assert.True(SidebarEditPlan.ShowsBody(in edit, Sec("sec_c", SidebarSectionKind.Pinned)));
    }

    /// <summary>A HIDDEN section NEVER reveals a body — not even while it is the expanded one, and not under
    /// ShowContents. Its rows are not in the user's live sidebar, so drawing them would be the editor lying about the
    /// artifact it edits (P1). The CARD still exists (dimmed, eye-off): nothing vanishes into an invisible elsewhere
    /// (P2), which is what the planner's separate "hidden still gets a card" rule guarantees.</summary>
    [Fact]
    public void ShowsBody_AHiddenSectionNeverRevealsOne()
    {
        var hidden = Sec("sec_h", SidebarSectionKind.StaticLinks, hidden: true);

        foreach (var edit in new[]
                 {
                     new SidebarEditState(ExpandedSection: "sec_h"),
                     new SidebarEditState(ShowContents: true),
                     new SidebarEditState(ExpandedSection: "sec_h", ShowContents: true),
                 })
            Assert.False(SidebarEditPlan.ShowsBody(in edit, hidden));
    }

    /// <summary>A Divider and a Header are pure chrome — the planner has no body arm for either — so their cards carry
    /// no disclosure mark rather than offering a chevron that opens onto nothing.</summary>
    [Theory]
    [InlineData(SidebarSectionKind.Divider)]
    [InlineData(SidebarSectionKind.Header)]
    public void ShowsBody_ChromeKindsHaveNoBodyToShow(SidebarSectionKind kind)
    {
        Assert.False(SidebarEditPlan.HasBody(kind));

        var section = Sec("sec_x", kind);
        var expanded = new SidebarEditState(ExpandedSection: "sec_x");
        var all = new SidebarEditState(ShowContents: true);
        Assert.False(SidebarEditPlan.ShowsBody(in expanded, section));
        Assert.False(SidebarEditPlan.ShowsBody(in all, section));
    }

    [Fact]
    public void HasBody_IsTrueForEveryOtherKnownKind()
    {
        foreach (SidebarSectionKind kind in Enum.GetValues<SidebarSectionKind>())
        {
            if (kind is SidebarSectionKind.Divider or SidebarSectionKind.Header) continue;
            Assert.True(SidebarEditPlan.HasBody(kind), kind + " lost its body arm");
        }
    }

    // ── SectionsReorderable ──────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Section drag is armed only while EVERY section is a card. A <c>Reorderable</c> band is one CONTIGUOUS
    /// run at ONE uniform pitch; the moment a section expands, its body rows split the card run in two and the slot math
    /// would address body rows as if they were cards — the same guard the pane already applies to a Pinned band whose
    /// folder is expanded. Explicit Move up / Move down stay available from every card's "…" menu, so a section can
    /// always be reordered: drag is one of several ways, never the only way (P6).</summary>
    [Fact]
    public void SectionsReorderable_IsDisarmedByAnyRevealedBody()
    {
        var idle = new SidebarEditState();
        Assert.True(SidebarEditPlan.SectionsReorderable(in idle));

        var expanded = new SidebarEditState(ExpandedSection: "sec_a");
        Assert.False(SidebarEditPlan.SectionsReorderable(in expanded));

        var contents = new SidebarEditState(ShowContents: true);
        Assert.False(SidebarEditPlan.SectionsReorderable(in contents));

        var both = new SidebarEditState(ExpandedSection: "sec_a", ShowContents: true);
        Assert.False(SidebarEditPlan.SectionsReorderable(in both));

        // An OPEN OPTIONS POPOVER changes no rows, so it must not disarm the band either.
        var options = new SidebarEditState(OptionsSection: "sec_a");
        Assert.True(SidebarEditPlan.SectionsReorderable(in options));

        // A blank expanded id is "nothing expanded" here too — the two rules must not disagree about that.
        var blank = new SidebarEditState(ExpandedSection: "");
        Assert.True(SidebarEditPlan.SectionsReorderable(in blank));
    }

    // ── Fold ─────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The session folded into the pane's plan <c>DepKey</c>. "No session" must be 0 and a LIVE session must
    /// never be 0, or entering edit mode on a document that happened to fold to 0 would not re-plan at all.</summary>
    [Fact]
    public void Fold_SeparatesNoSessionFromEverySession()
    {
        SidebarEditState? none = null;
        Assert.Equal(0, SidebarEditPlan.Fold(none));

        foreach (var live in new SidebarEditState?[]
                 {
                     new SidebarEditState(),
                     new SidebarEditState(ShowContents: true),
                     new SidebarEditState(ExpandedSection: "sec_a"),
                     new SidebarEditState(ExpandedSection: "sec_a", ShowContents: true),
                     new SidebarEditState(OptionsSection: "sec_a"),
                 })
            Assert.NotEqual(0, SidebarEditPlan.Fold(live));
    }

    [Fact]
    public void Fold_ChangesWithTheExpandedSectionAndTheContentsSwitch()
    {
        SidebarEditState? idle = new SidebarEditState();
        SidebarEditState? a = new SidebarEditState(ExpandedSection: "sec_a");
        SidebarEditState? b = new SidebarEditState(ExpandedSection: "sec_b");
        SidebarEditState? contents = new SidebarEditState(ShowContents: true);

        Assert.NotEqual(SidebarEditPlan.Fold(idle), SidebarEditPlan.Fold(a));
        Assert.NotEqual(SidebarEditPlan.Fold(a), SidebarEditPlan.Fold(b));      // a DIFFERENT section, not just "one"
        Assert.NotEqual(SidebarEditPlan.Fold(idle), SidebarEditPlan.Fold(contents));

        // Deterministic: the same session folds the same way, or the pane would re-plan on every frame.
        Assert.Equal(SidebarEditPlan.Fold(a), SidebarEditPlan.Fold(new SidebarEditState(ExpandedSection: "sec_a")));
    }

    /// <summary>OptionsSection is EXCLUDED on purpose: opening a popover changes nothing about the planned rows, and
    /// folding it in would re-plan the whole pane on every open.</summary>
    [Fact]
    public void Fold_IgnoresTheOpenOptionsPopover()
    {
        SidebarEditState? closed = new SidebarEditState(ExpandedSection: "sec_a");
        SidebarEditState? open = new SidebarEditState(ExpandedSection: "sec_a", OptionsSection: "sec_b");
        Assert.Equal(SidebarEditPlan.Fold(closed), SidebarEditPlan.Fold(open));
    }

    // ── CardCount ────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A card counts what the DOCUMENT holds, never "how many rows would this section plan" — the latter is
    /// only knowable by planning the body, and planning a 10 000-entry EntityList once per card per re-plan to print a
    /// number would be a real cost for a decoration.</summary>
    [Fact]
    public void CardCount_CountsAGroupsChildren()
    {
        var group = Sec("sec_g", SidebarSectionKind.CustomGroup,
            children: [Sec("sec_c1", SidebarSectionKind.StaticLinks), Sec("sec_c2", SidebarSectionKind.Divider)]);
        Assert.Equal(2, SidebarEditPlan.CardCount(group));
        Assert.Equal(0, SidebarEditPlan.CardCount(Sec("sec_empty", SidebarSectionKind.CustomGroup)));
    }

    /// <summary>An authored item list counts its VISIBLE items — a hidden item draws no row, so counting it would make
    /// the card disagree with the sidebar beside it.</summary>
    [Fact]
    public void CardCount_CountsVisibleAuthoredItemsOnly()
    {
        var links = Sec("sec_l", SidebarSectionKind.StaticLinks,
            items: [Route("home", "itm_1"), Route("search", "itm_2", hidden: true), Route("liked", "itm_3")]);
        Assert.Equal(2, SidebarEditPlan.CardCount(links));
        Assert.Equal(0, SidebarEditPlan.CardCount(Sec("sec_l2", SidebarSectionKind.StaticLinks)));

        // The materialised Shortcuts head is an ordinary StaticLinks section, so its card counts its shortcuts.
        Assert.Equal(SidebarCustomLayout.DefaultTopBar.Count,
            SidebarEditPlan.CardCount(SidebarShortcutsSection.From(SidebarCustomLayout.DefaultTopBar)));
    }

    /// <summary>A PROJECTED section shows nothing rather than a number it would have to guess. Pinned is the subtle one:
    /// its "items" are display OVERRIDES for pins made elsewhere, not the pin list — counting them would print "0" over
    /// a band showing twelve pins.</summary>
    [Fact]
    public void CardCount_IsMinusOneForEveryProjectedSection()
    {
        Assert.Equal(-1, SidebarEditPlan.CardCount(Sec("sec_p", SidebarSectionKind.Pinned)));
        Assert.Equal(-1, SidebarEditPlan.CardCount(Sec("sec_p2", SidebarSectionKind.Pinned,
            items: [Route("home", "itm_1")])));

        foreach (var kind in new[]
                 {
                     SidebarSectionKind.PlaylistTree, SidebarSectionKind.EntityList, SidebarSectionKind.JumpBackIn,
                     SidebarSectionKind.NewReleases, SidebarSectionKind.Concerts, SidebarSectionKind.Extension,
                     SidebarSectionKind.Divider, SidebarSectionKind.Header,
                 })
            Assert.Equal(-1, SidebarEditPlan.CardCount(Sec("sec_x", kind)));
    }

    // ── IsPinnedCard ─────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The Shortcuts head is not in <c>Sections</c>, so MoveSection / SetSectionHidden / DuplicateSection /
    /// RemoveSection addressed at it are all UnknownSection rejections. Its card therefore carries no grip, no eye and
    /// no "…": an affordance that silently rejects is strictly worse than one that is not offered.</summary>
    [Fact]
    public void IsPinnedCard_IsExactlyTheSentinel()
    {
        Assert.True(SidebarEditPlan.IsPinnedCard(SidebarIds.TopBarSection));
        Assert.False(SidebarEditPlan.IsPinnedCard("sec_a"));
        Assert.False(SidebarEditPlan.IsPinnedCard(null));
        Assert.False(SidebarEditPlan.IsPinnedCard(""));
    }

    // ── SectionIdAt ──────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void SectionIdAt_ReadsOnlyCardRowsInsideTheBand()
    {
        var persisted = Doc(Sec("sec_a", SidebarSectionKind.StaticLinks), Sec("sec_b", SidebarSectionKind.EntityList));
        var rows = Cards(Rendered(persisted));                 // [topbar, sec_a, sec_b]

        Assert.Equal("sec_a", SidebarEditPlan.SectionIdAt(rows, 1, 2, 0));
        Assert.Equal("sec_b", SidebarEditPlan.SectionIdAt(rows, 1, 2, 1));
        Assert.Equal("", SidebarEditPlan.SectionIdAt(rows, 1, 2, 2));        // past the band
        Assert.Equal("", SidebarEditPlan.SectionIdAt(rows, 1, 2, -1));
        Assert.Equal("", SidebarEditPlan.SectionIdAt(null, 0, 2, 0));
        Assert.Equal("", SidebarEditPlan.SectionIdAt(rows, 9, 2, 0));        // past the plan

        // A non-card row inside the band's span is NOT a card, so it resolves to "" rather than to its section id.
        rows[1] = new SidebarRow(SidebarRowKind.IconRow, "sec_a", 0, -1, 0, "home");
        Assert.Equal("", SidebarEditPlan.SectionIdAt(rows, 1, 2, 0));
    }

    // ── ToMoveSection ────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Dropping past the last card is APPEND, which in the post-removal index space is the persisted tail. The
    /// row array is built over the RENDER document (Shortcuts head at plan index 0) while the answer indexes the
    /// PERSISTED one — so a stray +1 fails here.</summary>
    [Fact]
    public void ToMoveSection_PastTheEndIsThePostRemovalTail()
    {
        var persisted = Doc(Sec("sec_a", SidebarSectionKind.Pinned), Sec("sec_b", SidebarSectionKind.EntityList),
                            Sec("sec_c", SidebarSectionKind.StaticLinks), Sec("sec_d", SidebarSectionKind.Divider));
        var rows = Cards(Rendered(persisted));
        Assert.Equal(5, rows.Count);                                   // the head plus four cards
        Assert.Equal(SidebarIds.TopBarSection, rows[0].SectionId);

        var move = Assert.IsType<MoveSection>(
            SidebarEditPlan.ToMoveSection(persisted, rows, bandStart: 1, bandCount: 4, from: 0, to: 3));
        Assert.Equal("sec_a", move.SectionId);
        Assert.Null(move.NewParentId);
        Assert.Equal(3, move.NewIndex);                                // NOT 4: the head is not in `Sections`

        var result = SidebarLayoutReducer.Apply(persisted, move);
        Assert.True(result.Changed);
        Assert.Equal(new[] { "sec_b", "sec_c", "sec_d", "sec_a" }, IdsOf(result.Layout));
    }

    [Fact]
    public void ToMoveSection_AboveALaterNeighbourLandsInThatNeighboursPlace()
    {
        var persisted = Doc(Sec("sec_a", SidebarSectionKind.Pinned), Sec("sec_b", SidebarSectionKind.EntityList),
                            Sec("sec_c", SidebarSectionKind.StaticLinks), Sec("sec_d", SidebarSectionKind.Divider));
        var rows = Cards(Rendered(persisted));

        // sec_a dropped at band slot 1 — i.e. between sec_b and sec_c.
        var move = Assert.IsType<MoveSection>(
            SidebarEditPlan.ToMoveSection(persisted, rows, 1, 4, from: 0, to: 1));
        Assert.Equal("sec_a", move.SectionId);
        Assert.Equal(1, move.NewIndex);
        Assert.Equal(new[] { "sec_b", "sec_a", "sec_c", "sec_d" },
                     IdsOf(SidebarLayoutReducer.Apply(persisted, move).Layout));
    }

    [Fact]
    public void ToMoveSection_AboveAnEarlierNeighbourLandsAboveIt()
    {
        var persisted = Doc(Sec("sec_a", SidebarSectionKind.Pinned), Sec("sec_b", SidebarSectionKind.EntityList),
                            Sec("sec_c", SidebarSectionKind.StaticLinks), Sec("sec_d", SidebarSectionKind.Divider));
        var rows = Cards(Rendered(persisted));

        var move = Assert.IsType<MoveSection>(
            SidebarEditPlan.ToMoveSection(persisted, rows, 1, 4, from: 3, to: 1));
        Assert.Equal("sec_d", move.SectionId);
        Assert.Equal(1, move.NewIndex);
        Assert.Equal(new[] { "sec_a", "sec_d", "sec_b", "sec_c" },
                     IdsOf(SidebarLayoutReducer.Apply(persisted, move).Layout));
    }

    /// <summary>THE GAP. An unknown (future) section kind plans no card, exactly as it renders no rows — so the band's
    /// slots and the document's indexes diverge. Bridging through the NEIGHBOUR the drop landed above is the only
    /// translation that stays exact when a card is missing from the middle of the run, and the unknown section must come
    /// out of the move exactly where it went in (the round-trip-untouched policy).</summary>
    [Fact]
    public void ToMoveSection_BridgesTheGapAnUnknownKindLeavesInTheBand()
    {
        var persisted = Doc(
            Sec("sec_a", SidebarSectionKind.StaticLinks),
            new SidebarSectionSpec("sec_future", (SidebarSectionKind)200, Title: "From the future"),
            Sec("sec_b", SidebarSectionKind.EntityList),
            Sec("sec_c", SidebarSectionKind.Divider));

        var rows = Cards(Rendered(persisted));
        Assert.Equal(4, rows.Count);                                   // head + THREE cards: sec_future plans none
        Assert.Equal(new[] { SidebarIds.TopBarSection, "sec_a", "sec_b", "sec_c" },
                     new[] { rows[0].SectionId, rows[1].SectionId, rows[2].SectionId, rows[3].SectionId });

        // sec_a dropped at band slot 1 — visually between sec_b and sec_c. Bridged through sec_c (document index 3).
        var move = Assert.IsType<MoveSection>(
            SidebarEditPlan.ToMoveSection(persisted, rows, 1, 3, from: 0, to: 1));
        Assert.Equal("sec_a", move.SectionId);
        Assert.Equal(2, move.NewIndex);

        var after = SidebarLayoutReducer.Apply(persisted, move);
        Assert.True(after.Changed);
        Assert.Equal(new[] { "sec_future", "sec_b", "sec_a", "sec_c" }, IdsOf(after.Layout));
    }

    /// <summary>The sentinel is not in <c>Sections</c>, so a drag of the Shortcuts head has no honest command — the
    /// canvas does not offer the grip, and the translation refuses it a second time.</summary>
    [Fact]
    public void ToMoveSection_RefusesTheShortcutsHead()
    {
        var persisted = Doc(Sec("sec_a", SidebarSectionKind.Pinned), Sec("sec_b", SidebarSectionKind.EntityList));
        var rows = Cards(Rendered(persisted));

        // A band that (wrongly) covered the head: slot 0 IS the sentinel.
        Assert.Null(SidebarEditPlan.ToMoveSection(persisted, rows, bandStart: 0, bandCount: 3, from: 0, to: 2));
    }

    [Fact]
    public void ToMoveSection_RefusesEveryDegenerateSlot()
    {
        var persisted = Doc(Sec("sec_a", SidebarSectionKind.Pinned), Sec("sec_b", SidebarSectionKind.EntityList));
        var rows = Cards(Rendered(persisted));

        Assert.Null(SidebarEditPlan.ToMoveSection(null, rows, 1, 2, 0, 1));
        Assert.Null(SidebarEditPlan.ToMoveSection(persisted, null, 1, 2, 0, 1));
        Assert.Null(SidebarEditPlan.ToMoveSection(persisted, rows, 1, 2, 1, 1));      // from == to is silence
        Assert.Null(SidebarEditPlan.ToMoveSection(persisted, rows, 1, 1, 0, 1));      // a one-card band cannot reorder
        Assert.Null(SidebarEditPlan.ToMoveSection(persisted, rows, 1, 2, -1, 1));
        Assert.Null(SidebarEditPlan.ToMoveSection(persisted, rows, 1, 2, 0, 2));      // past the band
    }

    /// <summary>A card whose section the persisted document does not contain (a stale plan, a hand-edited document)
    /// produces nothing rather than a command aimed at a section that is not there.</summary>
    [Fact]
    public void ToMoveSection_RefusesASectionThePersistedDocumentDoesNotHold()
    {
        var persisted = Doc(Sec("sec_a", SidebarSectionKind.Pinned), Sec("sec_b", SidebarSectionKind.EntityList));
        var rows = Cards(Rendered(persisted));
        rows[1] = new SidebarRow(SidebarRowKind.SectionCard, "sec_stale", 0, -1, -1, "sec_stale");

        Assert.Null(SidebarEditPlan.ToMoveSection(persisted, rows, 1, 2, 0, 1));
    }

    /// <summary>A card is always a TOP-LEVEL section. A group CHILD reaching the band would mean the canvas cards
    /// nested sections too, and a top-level <c>NewIndex</c> computed for one would file it somewhere the cue never
    /// pointed — so it is refused rather than guessed.</summary>
    [Fact]
    public void ToMoveSection_RefusesAGroupChild()
    {
        var persisted = Doc(
            Sec("sec_g", SidebarSectionKind.CustomGroup, children: [Sec("sec_c1", SidebarSectionKind.StaticLinks)]),
            Sec("sec_b", SidebarSectionKind.EntityList));

        var rows = new List<SidebarRow>
        {
            new(SidebarRowKind.SectionCard, SidebarIds.TopBarSection, 0, -1, 1, SidebarIds.TopBarSection),
            new(SidebarRowKind.SectionCard, "sec_c1", 0, -1, 0, "sec_c1"),      // a CHILD, wrongly carded
            new(SidebarRowKind.SectionCard, "sec_b", 0, -1, -1, "sec_b"),
        };

        Assert.Null(SidebarEditPlan.ToMoveSection(persisted, rows, 1, 2, 0, 1));   // moving a child
        Assert.Null(SidebarEditPlan.ToMoveSection(persisted, rows, 1, 2, 1, 0));   // landing above a child
    }

    // ── ToAddSection ─────────────────────────────────────────────────────────────────────────────────────────────────

    static SidebarSectionDropPayload Payload(SidebarSectionKind kind = SidebarSectionKind.StaticLinks,
        SidebarItemSpec? item = null)
        => new(kind, "Label", item);

    /// <summary>The drop convention is "insert BEFORE the card you aimed at" — the same neighbour bridging
    /// <see cref="SidebarEditPlan.ToMoveSection"/> uses, and for the same reason.</summary>
    [Fact]
    public void ToAddSection_InsertsBeforeTheCardUnderThePointer()
    {
        var persisted = Doc(Sec("sec_a", SidebarSectionKind.Pinned), Sec("sec_b", SidebarSectionKind.EntityList),
                            Sec("sec_c", SidebarSectionKind.Divider));
        var item = Route("home", "itm_home");

        var add = Assert.IsType<AddSection>(
            SidebarEditPlan.ToAddSection(persisted, "sec_b", Payload(item: item)));
        Assert.Equal(SidebarSectionKind.StaticLinks, add.Kind);
        Assert.Equal(1, add.Index);                                    // NOT 2: the render document's head is not here
        Assert.Null(add.ParentId);
        Assert.Same(item, add.Item);

        var result = SidebarLayoutReducer.Apply(persisted, add);
        Assert.True(result.Changed);
        Assert.Equal(SidebarSectionKind.StaticLinks, result.Layout.Sections[1].Kind);
        Assert.Equal("sec_b", result.Layout.Sections[2].Id);
        Assert.Equal("home", result.Layout.Sections[1].ItemList[0].Key);   // pre-seeded: never an empty Links section
    }

    /// <summary>The pinned Shortcuts head is not in <c>Sections</c>, so a drop on it resolves to index 0 — "above
    /// everything the reducer can address", which is exactly where the cue pointed.</summary>
    [Fact]
    public void ToAddSection_ADropOnTheShortcutsHeadIsIndexZero()
    {
        var persisted = Doc(Sec("sec_a", SidebarSectionKind.Pinned), Sec("sec_b", SidebarSectionKind.EntityList));

        var add = Assert.IsType<AddSection>(
            SidebarEditPlan.ToAddSection(persisted, SidebarIds.TopBarSection, Payload()));
        Assert.Equal(0, add.Index);

        var result = SidebarLayoutReducer.Apply(persisted, add);
        Assert.Equal(SidebarSectionKind.StaticLinks, result.Layout.Sections[0].Kind);
        Assert.Equal("sec_a", result.Layout.Sections[1].Id);
    }

    /// <summary>No card under the pointer ⇒ APPEND, which is also what a plain palette CLICK does — so drag is never
    /// the only way to add a section (P6).</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ToAddSection_WithNoCardUnderThePointerAppends(string? beforeId)
    {
        var persisted = Doc(Sec("sec_a", SidebarSectionKind.Pinned), Sec("sec_b", SidebarSectionKind.EntityList));

        var add = Assert.IsType<AddSection>(SidebarEditPlan.ToAddSection(persisted, beforeId, Payload()));
        Assert.Equal(persisted.Sections.Count, add.Index);
        Assert.Equal("sec_b", SidebarLayoutReducer.Apply(persisted, add).Layout.Sections[1].Id);
    }

    [Fact]
    public void ToAddSection_RefusesAChildCardAndAnUnknownKind()
    {
        var persisted = Doc(
            Sec("sec_g", SidebarSectionKind.CustomGroup, children: [Sec("sec_c1", SidebarSectionKind.StaticLinks)]),
            Sec("sec_b", SidebarSectionKind.EntityList));

        // A child card is not a top-level slot: refusing beats silently filing the section where the cue never pointed.
        Assert.Null(SidebarEditPlan.ToAddSection(persisted, "sec_c1", Payload()));

        // A section the document does not hold at all is the same refusal.
        Assert.Null(SidebarEditPlan.ToAddSection(persisted, "sec_stale", Payload()));

        // A payload this build cannot add (a future kind) never becomes a command the reducer would only reject.
        Assert.Null(SidebarEditPlan.ToAddSection(persisted, "sec_b", Payload((SidebarSectionKind)200)));

        Assert.Null(SidebarEditPlan.ToAddSection(null, "sec_b", Payload()));
        Assert.Null(SidebarEditPlan.ToAddSection(persisted, "sec_b", null));
    }

    /// <summary>The drag KIND has ONE owner. A drag kind typed twice is a drop that silently accepts nothing — the dnd
    /// rule for cross-list work — which is why the pane and the companion page both read this const.</summary>
    [Fact]
    public void SectionDragKind_IsOneNamedConstant()
        => Assert.Equal("wavee.sidebar.section", SidebarEditPlan.SectionDragKind);

    static string[] IdsOf(SidebarCustomLayout layout)
    {
        var ids = new string[layout.Sections.Count];
        for (int i = 0; i < ids.Length; i++) ids[i] = layout.Sections[i].Id;
        return ids;
    }
}
