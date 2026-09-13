// ── Wavee.Tests/SidebarReducerTests.cs — the sidebar layout document, its reducer, undo ring, top-bar band and
// the five seed templates ──────────────────────────────────────────────────────────────────────────────────────────
//
// Ported from the 0.2.9 suite (src/apps/_old/Wavee.Tests/SidebarLayoutReducerTests.cs +
// SidebarTemplateTests.cs + SidebarShortcutsSectionTests.cs) against the 0.3 production code in
// src/apps/Wavee/Shell/Sidebar.Doc.cs. The types kept their 0.2.9 names and shapes (the on-disk format is frozen
// byte for byte — see that file's banner) but dropped the `Wavee.Core.Sidebar` namespace; everything now lives
// directly under `Wavee`.
//
// What this suite exists to guarantee, in one place: `SidebarLayoutReducer.Apply` is PURE (it never mutates its
// input; a rejection returns the SAME document instance and one of the 13 `SidebarRejectReason`s; nothing is
// persisted, localized or dispatched to a service from in here); every one of the 21 command records is exercised
// through both its accept and its reject paths; every numeric cap (`MaxSections`, `MaxItemsPerSection`,
// `MaxTitleLength`, `MaxTopBarItems`, `MaxUrisPerSet`) is pinned by number, not just by shape; the five templates'
// exact section composition is pinned row-for-row (a template is the only thing that decides what a fresh Curated
// sidebar looks like); and the shell top-bar band renders as an ordinary — but unaddressable — section through the
// sentinel id, never through its own bespoke plumbing.
//
// Three regions, one per ported file: SidebarLayoutReducerTests (the reducer + undo ring + top-bar band commands),
// SidebarTemplateTests (the five seed layouts + the per-kind defaults they and the palette share), and
// SidebarShortcutsSectionTests (the band-as-a-section projection, the sentinel-id command routing, and its effect on
// the band's two real consumers — `SidebarBuiltInDocuments.Classic`, which prepends it, and `LibraryV3Document.Build`,
// which never does — both public in Sidebar.Modes.cs). One 0.2.9 fact in the third file is still dropped, not
// weakened: the one asserting V3 no longer emits its own `v3.liked` row is about V3's own content rather than the
// band, and belongs to SidebarDesignTests.cs instead. See the report for the list.

using Wavee;
using Xunit;

namespace Wavee.Tests;

// ═════════════════════════════════════════════════════════════════════════════════════════════════════════════════
// ── REGION 1 — THE REDUCER: one entry point, one verdict, zero side effects ────────────────────────────────────────
// ═════════════════════════════════════════════════════════════════════════════════════════════════════════════════
//
// One fact per row of the command table, plus the invariants the reducer exists to guarantee: it never mutates its
// input, a rejection changes nothing at all, and every per-kind rule (nesting depth 1, EntityEmbed's single item,
// the query legality repair, the lazy Pinned-override prune) holds no matter how the document was hand-edited.
// The undo/redo facts drive the same reducer through `SidebarUndo` — the pure 50-entry pre-image ring that the
// SHELL's `Sidebar.Dispatch` wraps (not part of this file; out of scope here).
public sealed class SidebarLayoutReducerTests
{
    // ── helpers ──────────────────────────────────────────────────────────────────────────────────────────────────

    static SidebarCustomLayout Doc(params SidebarSectionSpec[] sections)
        => new(SidebarTemplates.Curated, sections);

    static SidebarSectionSpec Sec(string id, SidebarSectionKind kind, SidebarDisplayOptions? display = null,
        IReadOnlyList<SidebarItemSpec>? items = null, SidebarEntityQuery? query = null,
        IReadOnlyList<SidebarSectionSpec>? children = null, bool hidden = false, bool collapsed = false,
        string? title = null, string? titleLocKey = null)
        => new(id, kind, title,
            titleLocKey ?? (title is null ? SidebarSectionKinds.DefaultTitleLocKey(kind) : null),
            hidden, collapsed, display, items, query, children);

    static SidebarItemSpec Route(string id, string key, string? icon = null)
        => new(id, SidebarItemTarget.Route, key, IconOverride: icon);

    static SidebarItemSpec Entity(string id, string uri,
        SidebarEntityKind kind = SidebarEntityKind.Playlist, string? label = null)
        => new(id, SidebarItemTarget.Entity, uri, kind, LabelOverride: label);

    static SidebarItemSpec Track(string id, string uri)
        => new(id, SidebarItemTarget.Track, uri, SidebarEntityKind.Track);

    static SidebarCommandResult Apply(SidebarCustomLayout l, SidebarCommand c) => SidebarLayoutReducer.Apply(l, c);

    static void AssertRejected(SidebarCustomLayout l, SidebarCommand c, SidebarRejectReason reason)
    {
        var r = SidebarLayoutReducer.Apply(l, c);
        Assert.False(r.Changed);
        Assert.Equal(reason, r.Reason);
        Assert.Same(l, r.Layout);            // a rejection returns the SAME document — no copy, nothing to save
    }

    static string IdOf(SidebarCustomLayout l, SidebarSectionKind kind)
    {
        for (int i = 0; i < l.Sections.Count; i++) if (l.Sections[i].Kind == kind) return l.Sections[i].Id;
        throw new InvalidOperationException("no " + kind);
    }

    static SidebarCustomLayout Curated() => SidebarTemplates.Build(SidebarTemplates.Curated);

    static string[] Ids(SidebarCustomLayout l)
    {
        var ids = new string[l.Sections.Count];
        for (int i = 0; i < ids.Length; i++) ids[i] = l.Sections[i].Id;
        return ids;
    }

    static string[] KeysOf(SidebarSectionSpec s)
    {
        var k = new string[s.ItemList.Count];
        for (int i = 0; i < k.Length; i++) k[i] = s.ItemList[i].Key;
        return k;
    }

    static string[] KeysOfBand(SidebarCustomLayout l)
    {
        var band = l.EffectiveTopBar;
        var keys = new string[band.Count];
        for (int i = 0; i < keys.Length; i++) keys[i] = band[i].Key;
        return keys;
    }

    static SidebarCustomLayout Step(ref SidebarCustomLayout current, SidebarUndo undo, SidebarCommand cmd)
    {
        var r = SidebarLayoutReducer.Apply(current, cmd);
        Assert.True(r.Changed, cmd.GetType().Name + " -> " + r.Reason);
        undo.Push(current, cmd);
        current = r.Layout;
        return r.Layout;
    }

    static SidebarExtensionRef Ref(string contribution = "artist.topTracks", string? config = null)
        => new("wavee", contribution, 1, config is null ? SidebarJson.EmptyObject : SidebarJson.Detach(config));

    static SidebarSectionSpec ExtensionSection(string id, SidebarExtensionRef? extension)
        => new(id, SidebarSectionKind.Extension, null,
            SidebarSectionKinds.DefaultTitleLocKey(SidebarSectionKind.Extension), false, false,
            SidebarSectionKinds.DefaultDisplay(SidebarSectionKind.Extension), null, null, null, extension);

    static SidebarItemSpec ActionItem(string id, string key)
        => new(id, SidebarItemTarget.Action, key);

    static SidebarItemSpec TopRoute(string key, string? icon = null)
        => new(SidebarIds.NewItem(), SidebarItemTarget.Route, key, IconOverride: icon);

    static SidebarSectionKind KindThatAllows(SidebarDisplayField f) => f switch
    {
        SidebarDisplayField.PlayButton => SidebarSectionKind.EntityEmbed,
        SidebarDisplayField.RecentsSource => SidebarSectionKind.JumpBackIn,
        _ => SidebarSectionKind.EntityList,       // allows every other field, incl. InlineControls
    };

    static int Read(SidebarDisplayOptions o, SidebarDisplayField f) => f switch
    {
        SidebarDisplayField.Density => (int)o.Density,
        SidebarDisplayField.Presentation => (int)o.Presentation,
        SidebarDisplayField.Artwork => o.Artwork ? 1 : 0,
        SidebarDisplayField.Subtitles => o.Subtitles ? 1 : 0,
        SidebarDisplayField.CountBadges => o.CountBadges ? 1 : 0,
        SidebarDisplayField.CollapsedByDefault => o.CollapsedByDefault ? 1 : 0,
        SidebarDisplayField.ShowInRail => o.ShowInRail ? 1 : 0,
        SidebarDisplayField.MaxItems => o.MaxItems,
        SidebarDisplayField.GridColumns => o.GridColumns,
        SidebarDisplayField.InlineControls => o.InlineControls ? 1 : 0,
        SidebarDisplayField.PlayButton => o.PlayButton ? 1 : 0,
        SidebarDisplayField.RecentsSource => (int)o.Recents,
        _ => -1,
    };

    // ── AddSection ───────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Adding_a_section_clamps_an_out_of_range_index()
    {
        var l = Doc(Sec("sec_a", SidebarSectionKind.Header), Sec("sec_b", SidebarSectionKind.Divider));

        var lo = Apply(l, new AddSection(SidebarSectionKind.Pinned, -5)).Layout;
        Assert.Equal(SidebarSectionKind.Pinned, lo.Sections[0].Kind);

        var hi = Apply(l, new AddSection(SidebarSectionKind.Pinned, 99)).Layout;
        Assert.Equal(SidebarSectionKind.Pinned, hi.Sections[^1].Kind);
        Assert.Equal(3, hi.Sections.Count);
    }

    [Fact]
    public void A_freshly_added_section_seeds_its_kind_defaults()
    {
        var l = Doc();

        var list = Apply(l, new AddSection(SidebarSectionKind.EntityList, 0)).Layout.Sections[0];
        Assert.Equal("sidebar.section.entityList", list.TitleLocKey);
        Assert.Null(list.Title);
        Assert.False(list.Hidden);
        Assert.False(list.Collapsed);
        Assert.Equal(SidebarEntityQuery.Default, list.Query);
        Assert.Empty(list.ItemList);
        Assert.Equal(SidebarDisplayOptions.Entities, list.Opts);
        Assert.StartsWith(SidebarIds.SectionPrefix, list.Id, StringComparison.Ordinal);

        // DEFECT 8 — StaticLinks seeds `Links`, NOT `Shortcuts`. The two differ in exactly one field: `Shortcuts`
        // carries `CountBadges = true`, which `AllowsDisplayField(StaticLinks, CountBadges)` forbids — so the old
        // seed was a default the user could neither see nor change. Asserted as the whole options bag (not just the
        // flag) so a future field that drifts between the two presets is caught here.
        var links = Apply(l, new AddSection(SidebarSectionKind.StaticLinks, 0)).Layout.Sections[0];
        Assert.Equal(SidebarDisplayOptions.Links, links.Opts);
        Assert.False(links.Opts.CountBadges);
        Assert.False(SidebarSectionKinds.AllowsDisplayField(SidebarSectionKind.StaticLinks,
                                                            SidebarDisplayField.CountBadges));
        Assert.Null(links.Query);

        // …while CollectionShortcuts — which DOES allow count badges (Classic's Your Library counts) — keeps them.
        var shortcuts = Apply(l, new AddSection(SidebarSectionKind.CollectionShortcuts, 0)).Layout.Sections[0];
        Assert.Equal(SidebarDisplayOptions.Shortcuts, shortcuts.Opts);
        Assert.True(shortcuts.Opts.CountBadges);

        // Collapsed seeds from CollapsedByDefault, and the feeds ship their spec'd top-N.
        Assert.Equal(4, Apply(l, new AddSection(SidebarSectionKind.NewReleases, 0)).Layout.Sections[0].Opts.MaxItems);
        Assert.Equal(3, Apply(l, new AddSection(SidebarSectionKind.Concerts, 0)).Layout.Sections[0].Opts.MaxItems);
    }

    [Fact]
    public void Adding_a_section_past_the_forty_section_cap_is_rejected()
    {
        var many = new SidebarSectionSpec[SidebarLayoutReducer.MaxSections];
        for (int i = 0; i < many.Length; i++) many[i] = Sec("sec_" + i.ToString("x8"), SidebarSectionKind.Divider);
        var l = Doc(many);
        Assert.Equal(SidebarLayoutReducer.MaxSections, l.SectionCount);

        AssertRejected(l, new AddSection(SidebarSectionKind.Header, 0), SidebarRejectReason.SectionCapReached);
    }

    [Fact]
    public void The_section_cap_counts_group_children_too()
    {
        var kids = new SidebarSectionSpec[5];
        for (int i = 0; i < kids.Length; i++) kids[i] = Sec("sec_k" + i, SidebarSectionKind.Header);
        var tops = new List<SidebarSectionSpec> { Sec("sec_g", SidebarSectionKind.CustomGroup, children: kids) };
        for (int i = 0; i < SidebarLayoutReducer.MaxSections - 6; i++)
            tops.Add(Sec("sec_t" + i, SidebarSectionKind.Divider));
        var l = new SidebarCustomLayout(SidebarTemplates.Curated, tops);

        Assert.Equal(SidebarLayoutReducer.MaxSections, l.SectionCount);
        AssertRejected(l, new AddSection(SidebarSectionKind.Header, 0), SidebarRejectReason.SectionCapReached);
    }

    [Fact]
    public void Adding_into_a_non_group_section_is_rejected()
    {
        var l = Doc(Sec("sec_a", SidebarSectionKind.Pinned));
        AssertRejected(l, new AddSection(SidebarSectionKind.Header, 0, "sec_a"), SidebarRejectReason.KindNotNestable);
    }

    [Fact]
    public void A_group_can_never_nest_inside_another_group()
    {
        var l = Doc(Sec("sec_g", SidebarSectionKind.CustomGroup));
        AssertRejected(l, new AddSection(SidebarSectionKind.CustomGroup, 0, "sec_g"),
            SidebarRejectReason.NestingTooDeep);
    }

    [Fact]
    public void Adding_into_a_group_child_is_rejected_as_too_deep()
    {
        var l = Doc(Sec("sec_g", SidebarSectionKind.CustomGroup,
            children: [Sec("sec_c", SidebarSectionKind.Header)]));
        AssertRejected(l, new AddSection(SidebarSectionKind.Divider, 0, "sec_c"),
            SidebarRejectReason.NestingTooDeep);
    }

    [Fact]
    public void Adding_into_an_unknown_parent_is_rejected()
    {
        var l = Doc(Sec("sec_a", SidebarSectionKind.CustomGroup));
        AssertRejected(l, new AddSection(SidebarSectionKind.Header, 0, "sec_nope"),
            SidebarRejectReason.UnknownSection);
    }

    [Fact]
    public void Adding_into_a_group_nests_the_new_section()
    {
        var l = Doc(Sec("sec_g", SidebarSectionKind.CustomGroup));
        var r = Apply(l, new AddSection(SidebarSectionKind.Header, 0, "sec_g"));
        Assert.True(r.Changed);
        Assert.Single(r.Layout.Sections);
        Assert.Single(r.Layout.Sections[0].ChildList);
        Assert.Equal(SidebarSectionKind.Header, r.Layout.Sections[0].ChildList[0].Kind);
        Assert.Equal(2, r.Layout.SectionCount);
    }

    [Fact]
    public void Adding_a_section_with_a_seed_item_is_one_undoable_step()
    {
        var l = Doc();
        var seed = Entity("itm_seed", "spotify:album:1", SidebarEntityKind.Album);
        var r = Apply(l, new AddSection(SidebarSectionKind.EntityEmbed, 0, null, seed));
        Assert.True(r.Changed);
        var sec = r.Layout.Sections[0];
        Assert.Single(sec.ItemList);
        Assert.Equal("spotify:album:1", sec.ItemList[0].Key);
        Assert.NotEqual("itm_seed", sec.ItemList[0].Id);      // ids are always minted by the reducer
        Assert.StartsWith(SidebarIds.ItemPrefix, sec.ItemList[0].Id, StringComparison.Ordinal);
    }

    [Fact]
    public void The_seed_item_is_validated_like_any_other_item()
    {
        var l = Doc();
        AssertRejected(l, new AddSection(SidebarSectionKind.StaticLinks, 0, null,
            Route("itm_x", "home", "NotAGlyph")), SidebarRejectReason.InvalidIcon);
        AssertRejected(l, new AddSection(SidebarSectionKind.EntityEmbed, 0, null, Route("itm_x", "home")),
            SidebarRejectReason.KindDoesNotAcceptItems);
        AssertRejected(l, new AddSection(SidebarSectionKind.PlaylistTree, 0, null, Entity("itm_x", "spotify:playlist:1")),
            SidebarRejectReason.KindDoesNotAcceptItems);
    }

    [Fact]
    public void Adding_a_kind_this_build_does_not_know_is_rejected()
        => AssertRejected(Doc(), new AddSection((SidebarSectionKind)200, 0), SidebarRejectReason.NoChange);

    // ── RemoveSection / DuplicateSection ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Removing_the_last_section_yields_an_empty_layout()
    {
        var l = Doc(Sec("sec_a", SidebarSectionKind.Pinned));
        var r = Apply(l, new RemoveSection("sec_a"));
        Assert.True(r.Changed);
        Assert.Empty(r.Layout.Sections);
        Assert.Equal(SidebarTemplates.Curated, r.Layout.TemplateId);   // the template identity survives
    }

    [Fact]
    public void Removing_an_unknown_section_is_rejected()
        => AssertRejected(Doc(Sec("sec_a", SidebarSectionKind.Pinned)), new RemoveSection("sec_zz"),
            SidebarRejectReason.UnknownSection);

    [Fact]
    public void Removing_a_group_takes_its_children_with_it()
    {
        var l = Doc(Sec("sec_g", SidebarSectionKind.CustomGroup, children: [Sec("sec_c", SidebarSectionKind.Header)]));
        var r = Apply(l, new RemoveSection("sec_g"));
        Assert.True(r.Changed);
        Assert.Empty(r.Layout.Sections);
        Assert.Null(r.Layout.Find("sec_c"));
    }

    [Fact]
    public void Removing_a_child_keeps_its_group()
    {
        var l = Doc(Sec("sec_g", SidebarSectionKind.CustomGroup, children: [Sec("sec_c", SidebarSectionKind.Header)]));
        var r = Apply(l, new RemoveSection("sec_c"));
        Assert.True(r.Changed);
        Assert.Single(r.Layout.Sections);
        Assert.Empty(r.Layout.Sections[0].ChildList);
    }

    /// <summary>DEFECT 10 — a clone that carries an authored <c>TitleLocKey</c> KEEPS IT and REFUSES the caller's
    /// literal. The deciding question is recoverability: <c>RenameSection(null)</c> reverts to the KIND DEFAULT, not
    /// to whatever key the section carried, so freezing "{name} (copy)" over a culture-following key would lose
    /// that key for good — a copy of "Playlists" made under nl would read "Afspeellijsten (kopie)" in every
    /// language, forever. The stated cost is that the copy reads the same as the original until the user renames
    /// it.</summary>
    [Fact]
    public void Duplicating_a_section_deep_clones_with_fresh_ids_and_keeps_an_authored_title_key()
    {
        var l = Doc(
            Sec("sec_g", SidebarSectionKind.CustomGroup,
                items: [Route("itm_1", "home"), Entity("itm_2", "spotify:playlist:1")],
                children: [Sec("sec_c", SidebarSectionKind.Header, titleLocKey: "sidebar.section.header")]),
            Sec("sec_after", SidebarSectionKind.Divider));

        // `Sec` seeds the kind default key when no title is given, so sec_g carries one — the defect-10 arm.
        Assert.Equal(SidebarSectionKinds.DefaultTitleLocKey(SidebarSectionKind.CustomGroup), l.Sections[0].TitleLocKey);

        var r = Apply(l, new DuplicateSection("sec_g", "Group (copy)"));
        Assert.True(r.Changed);
        Assert.Equal(3, r.Layout.Sections.Count);

        var clone = r.Layout.Sections[1];                       // inserted immediately after the original
        Assert.Null(clone.Title);                                // the literal is REFUSED…
        Assert.Equal(l.Sections[0].TitleLocKey, clone.TitleLocKey);   // …and the culture-following key survives
        Assert.NotEqual("sec_g", clone.Id);
        Assert.Equal(2, clone.ItemList.Count);
        Assert.Equal("home", clone.ItemList[0].Key);
        Assert.NotEqual("itm_1", clone.ItemList[0].Id);
        Assert.NotEqual("itm_2", clone.ItemList[1].Id);
        Assert.Single(clone.ChildList);
        Assert.NotEqual("sec_c", clone.ChildList[0].Id);

        var ids = SidebarLayoutCompare.AllIds(r.Layout);
        Assert.Equal(ids.Count, new HashSet<string>(ids, StringComparer.Ordinal).Count);

        // Everything except the ids is identical — the naming included.
        Assert.True(SidebarLayoutCompare.EqualIgnoringIds(
            new SidebarCustomLayout("t", [l.Sections[0]]),
            new SidebarCustomLayout("t", [clone])));
    }

    /// <summary>…and the complementary arm: with NO authored key, nothing localized is at stake and the caller's
    /// literal lands verbatim (clearing it later returns to exactly what the original shows).</summary>
    [Fact]
    public void Duplicating_a_keyless_section_takes_the_callers_literal_title()
    {
        var l = Doc(Sec("sec_g", SidebarSectionKind.CustomGroup, title: "My group", titleLocKey: null));
        Assert.Null(l.Sections[0].TitleLocKey);

        var clone = Apply(l, new DuplicateSection("sec_g", "My group (copy)")).Layout.Sections[1];
        Assert.Equal("My group (copy)", clone.Title);
        Assert.Null(clone.TitleLocKey);
    }

    [Fact]
    public void Duplicating_without_a_title_override_keeps_the_original_naming()
    {
        // A HEADER, not Pinned: since defect 9 the store-backed kinds refuse duplication outright (below), so the
        // "no override ⇒ keep the naming" rule has to be shown on a kind that can actually be cloned.
        var l = Doc(Sec("sec_a", SidebarSectionKind.Header, titleLocKey: "sidebar.section.header"));
        var clone = Apply(l, new DuplicateSection("sec_a")).Layout.Sections[1];
        Assert.Equal("sidebar.section.header", clone.TitleLocKey);
        Assert.Null(clone.Title);
    }

    /// <summary>DEFECT 9 — a STORE-BACKED section cannot be duplicated. Its rows and their ORDER live in a shared
    /// store (the pin store, the rootlist), not in the spec, so a clone is a second WRITER onto one list: both
    /// copies render the same pins and both commit their reorders into the same store, so a drag in the copy
    /// silently reshuffles the original. Fresh ids cannot separate them — the KIND is what binds a section to the
    /// store, never the id.</summary>
    [Theory]
    [InlineData(SidebarSectionKind.Pinned)]
    [InlineData(SidebarSectionKind.PlaylistTree)]
    public void Duplicating_a_store_backed_section_is_refused(SidebarSectionKind kind)
    {
        Assert.True(SidebarSectionKinds.IsStoreBacked(kind));
        AssertRejected(Doc(Sec("sec_a", kind)), new DuplicateSection("sec_a", "copy"),
            SidebarRejectReason.KindNotDuplicable);
    }

    /// <summary>…and one level down: a GROUP is refused when ANY child is store-backed, because cloning the group
    /// clones that child with it. The group itself is duplicable — it is the child that decides.</summary>
    [Theory]
    [InlineData(SidebarSectionKind.Pinned)]
    [InlineData(SidebarSectionKind.PlaylistTree)]
    public void Duplicating_a_group_holding_a_store_backed_child_is_refused(SidebarSectionKind childKind)
    {
        var l = Doc(Sec("sec_g", SidebarSectionKind.CustomGroup,
            children: [Sec("sec_head", SidebarSectionKind.Header), Sec("sec_c", childKind)]));
        AssertRejected(l, new DuplicateSection("sec_g", "copy"), SidebarRejectReason.KindNotDuplicable);

        // The same group WITHOUT that child duplicates fine — the refusal is about the child, not about groups.
        var safe = Doc(Sec("sec_g", SidebarSectionKind.CustomGroup,
            children: [Sec("sec_head", SidebarSectionKind.Header)]));
        Assert.True(Apply(safe, new DuplicateSection("sec_g", "copy")).Changed);
    }

    [Fact]
    public void Duplicating_an_unknown_section_is_rejected()
        => AssertRejected(Doc(Sec("sec_a", SidebarSectionKind.Pinned)), new DuplicateSection("sec_zz"),
            SidebarRejectReason.UnknownSection);

    // ── RenameSection ────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Renaming_trims_and_truncates_the_title_to_sixty_characters()
    {
        var l = Doc(Sec("sec_a", SidebarSectionKind.Pinned));
        var r = Apply(l, new RenameSection("sec_a", "   My pins   "));
        Assert.True(r.Changed);
        Assert.Equal("My pins", r.Layout.Sections[0].Title);
        Assert.Null(r.Layout.Sections[0].TitleLocKey);

        var long1 = new string('x', 200);
        var t = Apply(l, new RenameSection("sec_a", long1)).Layout.Sections[0].Title;
        Assert.Equal(SidebarLayoutReducer.MaxTitleLength, t!.Length);
    }

    [Fact]
    public void Renaming_to_blank_restores_the_kind_default_title_key()
    {
        var named = Doc(Sec("sec_a", SidebarSectionKind.Pinned, title: "Mine", titleLocKey: null));
        foreach (var blank in new string?[] { null, "", "   " })
        {
            var r = Apply(named, new RenameSection("sec_a", blank));
            Assert.True(r.Changed);
            Assert.Null(r.Layout.Sections[0].Title);
            Assert.Equal("sidebar.pinned", r.Layout.Sections[0].TitleLocKey);
        }
    }

    [Fact]
    public void Renaming_to_the_same_title_is_a_no_change()
    {
        var l = Doc(Sec("sec_a", SidebarSectionKind.Pinned, title: "Mine", titleLocKey: null));
        AssertRejected(l, new RenameSection("sec_a", "  Mine  "), SidebarRejectReason.NoChange);
        AssertRejected(Doc(Sec("sec_a", SidebarSectionKind.Pinned)), new RenameSection("sec_a", null),
            SidebarRejectReason.NoChange);
        AssertRejected(l, new RenameSection("sec_zz", "x"), SidebarRejectReason.UnknownSection);
    }

    // ── hidden / collapsed ───────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Hiding_a_section_toggles_and_rejects_the_same_value()
    {
        var l = Doc(Sec("sec_a", SidebarSectionKind.Pinned));
        var r = Apply(l, new SetSectionHidden("sec_a", true));
        Assert.True(r.Changed);
        Assert.True(r.Layout.Sections[0].Hidden);
        AssertRejected(l, new SetSectionHidden("sec_a", false), SidebarRejectReason.NoChange);
        AssertRejected(l, new SetSectionHidden("sec_zz", true), SidebarRejectReason.UnknownSection);
    }

    [Fact]
    public void Collapsing_a_section_toggles_and_rejects_the_same_value()
    {
        var l = Doc(Sec("sec_a", SidebarSectionKind.Pinned));
        var r = Apply(l, new SetSectionCollapsed("sec_a", true));
        Assert.True(r.Changed);
        Assert.True(r.Layout.Sections[0].Collapsed);
        AssertRejected(l, new SetSectionCollapsed("sec_a", false), SidebarRejectReason.NoChange);
    }

    [Fact]
    public void Collapsing_works_on_a_group_child_without_touching_its_parent()
    {
        var l = Doc(Sec("sec_g", SidebarSectionKind.CustomGroup, children: [Sec("sec_c", SidebarSectionKind.Pinned)]));
        var r = Apply(l, new SetSectionCollapsed("sec_c", true));
        Assert.True(r.Changed);
        Assert.True(r.Layout.Find("sec_c")!.Collapsed);
        Assert.False(r.Layout.Sections[0].Collapsed);
    }

    // ── MoveSection ──────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Moving_a_section_interprets_the_index_after_removal()
    {
        var l = Doc(Sec("sec_a", SidebarSectionKind.Pinned), Sec("sec_b", SidebarSectionKind.Header),
            Sec("sec_c", SidebarSectionKind.Divider));

        // Move the FIRST section to the end: after removal the list is [b, c], so index 2 is the tail.
        var r = Apply(l, new MoveSection("sec_a", null, 2));
        Assert.True(r.Changed);
        Assert.Equal(new[] { "sec_b", "sec_c", "sec_a" }, Ids(r.Layout));

        // Over-large indices clamp.
        Assert.Equal(new[] { "sec_b", "sec_c", "sec_a" }, Ids(Apply(l, new MoveSection("sec_a", null, 99)).Layout));
        Assert.Equal(new[] { "sec_c", "sec_a", "sec_b" }, Ids(Apply(l, new MoveSection("sec_c", null, -4)).Layout));
    }

    [Fact]
    public void Moving_a_section_to_its_own_position_is_a_no_change()
    {
        var l = Doc(Sec("sec_a", SidebarSectionKind.Pinned), Sec("sec_b", SidebarSectionKind.Header));
        AssertRejected(l, new MoveSection("sec_a", null, 0), SidebarRejectReason.NoChange);
        AssertRejected(l, new MoveSection("sec_b", null, 1), SidebarRejectReason.NoChange);
    }

    [Fact]
    public void Moving_a_group_into_its_own_child_or_itself_is_rejected()
    {
        var l = Doc(Sec("sec_g", SidebarSectionKind.CustomGroup, children: [Sec("sec_c", SidebarSectionKind.Header)]),
            Sec("sec_other", SidebarSectionKind.CustomGroup));

        AssertRejected(l, new MoveSection("sec_g", "sec_c", 0), SidebarRejectReason.NestingTooDeep);
        AssertRejected(l, new MoveSection("sec_g", "sec_g", 0), SidebarRejectReason.NestingTooDeep);
        // A group can never nest, not even in an unrelated group.
        AssertRejected(l, new MoveSection("sec_g", "sec_other", 0), SidebarRejectReason.NestingTooDeep);
    }

    [Fact]
    public void Moving_into_a_non_group_or_an_unknown_section_is_rejected()
    {
        var l = Doc(Sec("sec_a", SidebarSectionKind.Header), Sec("sec_p", SidebarSectionKind.Pinned));
        AssertRejected(l, new MoveSection("sec_a", "sec_p", 0), SidebarRejectReason.KindNotNestable);
        AssertRejected(l, new MoveSection("sec_zz", null, 0), SidebarRejectReason.UnknownSection);
    }

    [Fact]
    public void Moving_a_section_into_and_back_out_of_a_group()
    {
        var l = Doc(Sec("sec_h", SidebarSectionKind.Header), Sec("sec_g", SidebarSectionKind.CustomGroup));

        var into = Apply(l, new MoveSection("sec_h", "sec_g", 0));
        Assert.True(into.Changed);
        Assert.Single(into.Layout.Sections);
        Assert.Equal("sec_h", into.Layout.Sections[0].ChildList[0].Id);

        var back = Apply(into.Layout, new MoveSection("sec_h", null, 0));
        Assert.True(back.Changed);
        Assert.Equal(new[] { "sec_h", "sec_g" }, Ids(back.Layout));
        Assert.Empty(back.Layout.Sections[1].ChildList);
    }

    // ── items ────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Adding_an_item_rejects_a_duplicate_target_and_key()
    {
        var l = Doc(Sec("sec_s", SidebarSectionKind.StaticLinks, items: [Route("itm_1", "home")]));
        AssertRejected(l, new AddItem("sec_s", Route("itm_2", "home"), 0), SidebarRejectReason.DuplicateItem);

        // A different TARGET with the same key is a different item.
        var r = Apply(l, new AddItem("sec_s", Entity("itm_3", "home"), 1));
        Assert.True(r.Changed);
        Assert.Equal(2, r.Layout.Sections[0].ItemList.Count);
    }

    [Fact]
    public void Adding_an_item_rejects_an_icon_outside_the_whitelist()
    {
        var l = Doc(Sec("sec_s", SidebarSectionKind.StaticLinks));
        AssertRejected(l, new AddItem("sec_s", Route("itm_1", "home", "Skull"), 0), SidebarRejectReason.InvalidIcon);
        Assert.True(Apply(l, new AddItem("sec_s", Route("itm_1", "home", "Home"), 0)).Changed);
    }

    [Fact]
    public void Adding_an_item_regenerates_a_colliding_incoming_id()
    {
        var l = Doc(Sec("sec_s", SidebarSectionKind.StaticLinks, items: [Route("itm_dup", "home")]));
        var r = Apply(l, new AddItem("sec_s", Route("itm_dup", "search"), 1));
        Assert.True(r.Changed);
        var items = r.Layout.Sections[0].ItemList;
        Assert.NotEqual(items[0].Id, items[1].Id);
        Assert.Equal("itm_dup", items[0].Id);
        Assert.Equal("search", items[1].Key);

        // A unique incoming id is KEPT.
        var keep = Apply(l, new AddItem("sec_s", Route("itm_fresh", "search"), 1));
        Assert.Equal("itm_fresh", keep.Layout.Sections[0].ItemList[1].Id);
    }

    [Fact]
    public void Adding_an_item_is_rejected_on_kinds_that_do_not_accept_items()
    {
        foreach (var kind in new[] { SidebarSectionKind.PlaylistTree, SidebarSectionKind.JumpBackIn,
            SidebarSectionKind.EntityList, SidebarSectionKind.Header, SidebarSectionKind.Divider,
            SidebarSectionKind.NewReleases, SidebarSectionKind.Concerts })
        {
            var l = Doc(Sec("sec_x", kind));
            AssertRejected(l, new AddItem("sec_x", Entity("itm_1", "spotify:playlist:1"), 0),
                SidebarRejectReason.KindDoesNotAcceptItems);
        }

        // Pinned DOES accept items — they are the override side-table, not the pin list.
        Assert.True(Apply(Doc(Sec("sec_p", SidebarSectionKind.Pinned)),
            new AddItem("sec_p", Entity("itm_1", "spotify:playlist:1", label: "Alias"), 0)).Changed);
    }

    [Fact]
    public void Adding_an_item_clamps_the_index_and_rejects_at_the_item_cap()
    {
        var full = new SidebarItemSpec[SidebarLayoutReducer.MaxItemsPerSection];
        for (int i = 0; i < full.Length; i++) full[i] = Route("itm_" + i.ToString("x8"), "route" + i);
        var l = Doc(Sec("sec_s", SidebarSectionKind.StaticLinks, items: full));
        AssertRejected(l, new AddItem("sec_s", Route("itm_new", "home"), 0), SidebarRejectReason.SectionCapReached);

        var small = Doc(Sec("sec_s", SidebarSectionKind.StaticLinks, items: [Route("itm_1", "home")]));
        Assert.Equal("search", Apply(small, new AddItem("sec_s", Route("itm_2", "search"), 99))
            .Layout.Sections[0].ItemList[^1].Key);
        Assert.Equal("search", Apply(small, new AddItem("sec_s", Route("itm_2", "search"), -9))
            .Layout.Sections[0].ItemList[0].Key);
    }

    [Fact]
    public void Adding_an_item_trims_its_label_override()
    {
        var l = Doc(Sec("sec_s", SidebarSectionKind.StaticLinks));
        var r = Apply(l, new AddItem("sec_s", Entity("itm_1", "spotify:playlist:1", label: "   "), 0));
        Assert.Null(r.Layout.Sections[0].ItemList[0].LabelOverride);
    }

    [Fact]
    public void Track_items_are_legal_in_groups_and_static_links()
    {
        foreach (var kind in new[] { SidebarSectionKind.CustomGroup, SidebarSectionKind.StaticLinks })
        {
            var l = Doc(Sec("sec_x", kind));
            var r = Apply(l, new AddItem("sec_x", Track("itm_t", "spotify:track:1"), 0));
            Assert.True(r.Changed);
            Assert.Equal(SidebarItemTarget.Track, r.Layout.Sections[0].ItemList[0].Target);
            Assert.Equal(SidebarEntityKind.Track, r.Layout.Sections[0].ItemList[0].EntityKind);
        }
    }

    [Fact]
    public void EntityEmbed_keeps_exactly_one_item_and_retargets_on_a_second_add()
    {
        var l = Doc(Sec("sec_e", SidebarSectionKind.EntityEmbed,
            items: [Entity("itm_1", "spotify:album:1", SidebarEntityKind.Album)]));

        // A second add RETARGETS the spotlight rather than stacking.
        var r = Apply(l, new AddItem("sec_e", Entity("itm_2", "spotify:artist:9", SidebarEntityKind.Artist), 1));
        Assert.True(r.Changed);
        Assert.Single(r.Layout.Sections[0].ItemList);
        Assert.Equal("spotify:artist:9", r.Layout.Sections[0].ItemList[0].Key);

        // Re-adding the SAME target is a no-op.
        AssertRejected(r.Layout, new AddItem("sec_e",
            Entity("itm_3", "spotify:artist:9", SidebarEntityKind.Artist), 0), SidebarRejectReason.NoChange);

        // A track/route can never be the spotlight.
        AssertRejected(l, new AddItem("sec_e", Track("itm_4", "spotify:track:1"), 0),
            SidebarRejectReason.KindDoesNotAcceptItems);
    }

    [Fact]
    public void EntityEmbed_normalizes_a_hand_edited_multi_item_document_on_next_touch()
    {
        var l = Doc(Sec("sec_e", SidebarSectionKind.EntityEmbed,
            items: [Entity("itm_1", "spotify:album:1", SidebarEntityKind.Album),
                    Entity("itm_2", "spotify:album:2", SidebarEntityKind.Album)]));

        var r = Apply(l, new SetSectionCollapsed("sec_e", true));
        Assert.True(r.Changed);
        Assert.Single(r.Layout.Sections[0].ItemList);
        Assert.Equal("spotify:album:1", r.Layout.Sections[0].ItemList[0].Key);
    }

    [Fact]
    public void Moving_an_item_across_sections_relocates_it()
    {
        var l = Doc(
            Sec("sec_g1", SidebarSectionKind.CustomGroup, items: [Route("itm_1", "home"), Route("itm_2", "search")]),
            Sec("sec_g2", SidebarSectionKind.CustomGroup, items: [Route("itm_3", "liked")]));

        var r = Apply(l, new MoveItem("sec_g1", 0, "sec_g2", 0));
        Assert.True(r.Changed);
        Assert.Single(r.Layout.Find("sec_g1")!.ItemList);
        Assert.Equal("search", r.Layout.Find("sec_g1")!.ItemList[0].Key);
        Assert.Equal(new[] { "home", "liked" }, KeysOf(r.Layout.Find("sec_g2")!));

        // The source emptying out drops to a null item list rather than an empty one (the JSON stays small).
        var drain = Apply(r.Layout, new MoveItem("sec_g1", 0, "sec_g2", 2));
        Assert.Empty(drain.Layout.Find("sec_g1")!.ItemList);
        Assert.Equal(new[] { "home", "liked", "search" }, KeysOf(drain.Layout.Find("sec_g2")!));
    }

    [Fact]
    public void Moving_an_item_within_a_section_interprets_the_index_after_removal()
    {
        var l = Doc(Sec("sec_g", SidebarSectionKind.CustomGroup,
            items: [Route("itm_1", "a"), Route("itm_2", "b"), Route("itm_3", "c")]));

        var r = Apply(l, new MoveItem("sec_g", 0, "sec_g", 2));
        Assert.True(r.Changed);
        Assert.Equal(new[] { "b", "c", "a" }, KeysOf(r.Layout.Sections[0]));
        AssertRejected(l, new MoveItem("sec_g", 1, "sec_g", 1), SidebarRejectReason.NoChange);
    }

    [Fact]
    public void Moving_an_item_rejects_bad_endpoints()
    {
        var l = Doc(Sec("sec_g", SidebarSectionKind.CustomGroup, items: [Route("itm_1", "a")]),
            Sec("sec_t", SidebarSectionKind.PlaylistTree));

        AssertRejected(l, new MoveItem("sec_zz", 0, "sec_g", 0), SidebarRejectReason.UnknownSection);
        AssertRejected(l, new MoveItem("sec_g", 0, "sec_zz", 0), SidebarRejectReason.UnknownSection);
        AssertRejected(l, new MoveItem("sec_g", 0, "sec_t", 0), SidebarRejectReason.KindDoesNotAcceptItems);
        AssertRejected(l, new MoveItem("sec_g", 5, "sec_g", 0), SidebarRejectReason.UnknownItem);
    }

    [Fact]
    public void Moving_an_item_rejects_a_duplicate_in_the_target_section()
    {
        var l = Doc(Sec("sec_g1", SidebarSectionKind.CustomGroup, items: [Route("itm_1", "home")]),
            Sec("sec_g2", SidebarSectionKind.CustomGroup, items: [Route("itm_2", "home")]));
        AssertRejected(l, new MoveItem("sec_g1", 0, "sec_g2", 0), SidebarRejectReason.DuplicateItem);
    }

    [Fact]
    public void Removing_an_unknown_item_is_rejected()
    {
        var l = Doc(Sec("sec_g", SidebarSectionKind.CustomGroup, items: [Route("itm_1", "a")]));
        AssertRejected(l, new RemoveItem("sec_g", "itm_nope"), SidebarRejectReason.UnknownItem);
        AssertRejected(l, new RemoveItem("sec_zz", "itm_1"), SidebarRejectReason.UnknownSection);

        var r = Apply(l, new RemoveItem("sec_g", "itm_1"));
        Assert.True(r.Changed);
        Assert.Empty(r.Layout.Sections[0].ItemList);
    }

    [Fact]
    public void Only_RemoveItem_drops_a_missing_entity_row_every_other_command_keeps_it()
    {
        // An unresolvable item survives every other command (missing-entity retention, §C1.4).
        var gone = new SidebarItemSpec("itm_gone", SidebarItemTarget.Entity, "spotify:playlist:vanished",
            SidebarEntityKind.Playlist, FallbackTitle: "Old mix", FallbackImageUrl: "http://x/y.jpg");
        var l = Doc(Sec("sec_g", SidebarSectionKind.CustomGroup, items: [gone]));

        foreach (var cmd in new SidebarCommand[]
        {
            new SetSectionCollapsed("sec_g", true),
            new SetSectionHidden("sec_g", true),
            new RenameSection("sec_g", "Renamed"),
            new SetDisplayOption("sec_g", SidebarDisplayField.Density, 0),
        })
        {
            var r = SidebarLayoutReducer.Apply(l, cmd);
            Assert.True(r.Changed, cmd.GetType().Name);
            var kept = r.Layout.Find("sec_g")!.ItemList[0];
            Assert.Equal("spotify:playlist:vanished", kept.Key);
            Assert.Equal("Old mix", kept.FallbackTitle);
            Assert.Equal("http://x/y.jpg", kept.FallbackImageUrl);
        }

        Assert.Empty(Apply(l, new RemoveItem("sec_g", "itm_gone")).Layout.Find("sec_g")!.ItemList);
    }

    [Fact]
    public void Setting_an_item_label_trims_truncates_and_clears()
    {
        var l = Doc(Sec("sec_g", SidebarSectionKind.CustomGroup,
            items: [Entity("itm_1", "spotify:playlist:1", label: "Old")]));

        Assert.Equal("New", Apply(l, new SetItemLabel("sec_g", "itm_1", "  New  "))
            .Layout.Sections[0].ItemList[0].LabelOverride);
        Assert.Null(Apply(l, new SetItemLabel("sec_g", "itm_1", "  ")).Layout.Sections[0].ItemList[0].LabelOverride);
        Assert.Equal(SidebarLayoutReducer.MaxTitleLength,
            Apply(l, new SetItemLabel("sec_g", "itm_1", new string('y', 99)))
                .Layout.Sections[0].ItemList[0].LabelOverride!.Length);

        AssertRejected(l, new SetItemLabel("sec_g", "itm_1", "Old"), SidebarRejectReason.NoChange);
        AssertRejected(l, new SetItemLabel("sec_g", "itm_zz", "x"), SidebarRejectReason.UnknownItem);
    }

    [Fact]
    public void Setting_an_item_icon_clears_and_validates()
    {
        var l = Doc(Sec("sec_s", SidebarSectionKind.StaticLinks, items: [Route("itm_1", "home", "Home")]));

        Assert.Equal("Heart", Apply(l, new SetItemIcon("sec_s", "itm_1", "Heart"))
            .Layout.Sections[0].ItemList[0].IconOverride);
        Assert.Null(Apply(l, new SetItemIcon("sec_s", "itm_1", null)).Layout.Sections[0].ItemList[0].IconOverride);
        Assert.Null(Apply(l, new SetItemIcon("sec_s", "itm_1", "")).Layout.Sections[0].ItemList[0].IconOverride);
        AssertRejected(l, new SetItemIcon("sec_s", "itm_1", "Skull"), SidebarRejectReason.InvalidIcon);
        AssertRejected(l, new SetItemIcon("sec_s", "itm_1", "Home"), SidebarRejectReason.NoChange);
    }

    // ── display options ──────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(ClampCases))]
    public void Setting_a_display_option_clamps_every_field(SidebarDisplayField field, int input, int expected)
    {
        var kind = KindThatAllows(field);
        SidebarEntityQuery? query = kind == SidebarSectionKind.EntityList ? SidebarEntityQuery.Default : null;
        IReadOnlyList<SidebarItemSpec>? items = kind == SidebarSectionKind.EntityEmbed
            ? new[] { Entity("itm_1", "spotify:album:1", SidebarEntityKind.Album) }
            : null;
        var l = Doc(Sec("sec_x", kind, query: query, items: items));

        var r = SidebarLayoutReducer.Apply(l, new SetDisplayOption("sec_x", field, input));
        Assert.Equal(expected, Read(r.Layout.Sections[0].Opts, field));
    }

    [Theory]
    [MemberData(nameof(EveryField))]
    public void Setting_an_inapplicable_display_field_is_a_no_change(SidebarDisplayField field)
    {
        // Divider honours ShowInRail and nothing else — every other field must be refused, not silently written.
        var l = Doc(Sec("sec_d", SidebarSectionKind.Divider));
        if (field == SidebarDisplayField.ShowInRail)
        {
            Assert.True(SidebarLayoutReducer.Apply(l, new SetDisplayOption("sec_d", field, 0)).Changed);
            return;
        }
        AssertRejected(l, new SetDisplayOption("sec_d", field, 1), SidebarRejectReason.NoChange);
    }

    [Fact]
    public void Kind_scoped_display_fields_are_refused_on_the_wrong_kind()
    {
        var pinned = Doc(Sec("sec_p", SidebarSectionKind.Pinned));
        AssertRejected(pinned, new SetDisplayOption("sec_p", SidebarDisplayField.InlineControls, 1),
            SidebarRejectReason.NoChange);
        AssertRejected(pinned, new SetDisplayOption("sec_p", SidebarDisplayField.PlayButton, 0),
            SidebarRejectReason.NoChange);
        AssertRejected(pinned, new SetDisplayOption("sec_p", SidebarDisplayField.RecentsSource, 1),
            SidebarRejectReason.NoChange);

        // NewReleases has no meaningful single rail tile, and CountBadges never applies to a feed.
        var feed = Doc(Sec("sec_n", SidebarSectionKind.NewReleases));
        AssertRejected(feed, new SetDisplayOption("sec_n", SidebarDisplayField.ShowInRail, 0),
            SidebarRejectReason.NoChange);
        AssertRejected(feed, new SetDisplayOption("sec_n", SidebarDisplayField.CountBadges, 1),
            SidebarRejectReason.NoChange);

        // The tree is never truncated.
        AssertRejected(Doc(Sec("sec_t", SidebarSectionKind.PlaylistTree)),
            new SetDisplayOption("sec_t", SidebarDisplayField.MaxItems, 5), SidebarRejectReason.NoChange);
    }

    [Fact]
    public void Setting_collapsed_by_default_does_not_touch_the_live_collapse_state()
    {
        var l = Doc(Sec("sec_p", SidebarSectionKind.Pinned));
        var r = Apply(l, new SetDisplayOption("sec_p", SidebarDisplayField.CollapsedByDefault, 1));
        Assert.True(r.Changed);
        Assert.True(r.Layout.Sections[0].Opts.CollapsedByDefault);
        Assert.False(r.Layout.Sections[0].Collapsed);
    }

    [Fact]
    public void Setting_the_recents_source_retargets_the_default_title_but_never_a_rename()
    {
        var def = Doc(Sec("sec_j", SidebarSectionKind.JumpBackIn));
        Assert.Equal("sidebar.section.jumpBackIn", def.Sections[0].TitleLocKey);

        var played = Apply(def, new SetDisplayOption("sec_j", SidebarDisplayField.RecentsSource, 1)).Layout;
        Assert.Equal(SidebarRecentsSource.Played, played.Sections[0].Opts.Recents);
        Assert.Equal("sidebar.section.recentlyPlayed", played.Sections[0].TitleLocKey);

        var renamed = Doc(Sec("sec_j", SidebarSectionKind.JumpBackIn, title: "My picks", titleLocKey: null));
        var still = Apply(renamed, new SetDisplayOption("sec_j", SidebarDisplayField.RecentsSource, 1)).Layout;
        Assert.Equal("My picks", still.Sections[0].Title);
        Assert.Null(still.Sections[0].TitleLocKey);
    }

    [Fact]
    public void Setting_a_display_option_on_an_unknown_section_is_rejected()
        => AssertRejected(Doc(Sec("sec_p", SidebarSectionKind.Pinned)),
            new SetDisplayOption("sec_zz", SidebarDisplayField.Density, 0), SidebarRejectReason.UnknownSection);

    // ── query ────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Setting_a_query_repairs_illegal_combinations()
    {
        var l = Doc(Sec("sec_e", SidebarSectionKind.EntityList, query: SidebarEntityQuery.Default));

        // CustomOrder outside Playlists -> Alphabetical.
        var a = Apply(l, new SetQuery("sec_e",
            new SidebarEntityQuery(SidebarEntityKinds.All, SidebarSortMode.CustomOrder))).Layout.Sections[0].Query!;
        Assert.Equal(SidebarSortMode.Alphabetical, a.Sort);
        Assert.Equal(SidebarEntityKinds.All, a.Kinds);

        // A qualifier without playlists -> Any.
        var b = Apply(l, new SetQuery("sec_e", new SidebarEntityQuery(SidebarEntityKinds.Albums,
            SidebarSortMode.Alphabetical, false, SidebarPlaylistQualifier.ByYou))).Layout.Sections[0].Query!;
        Assert.Equal(SidebarPlaylistQualifier.Any, b.Qualifier);

        // Kinds.None -> All (and the CustomOrder repair then sees All, not None).
        var c = Apply(l, new SetQuery("sec_e",
            new SidebarEntityQuery(SidebarEntityKinds.None, SidebarSortMode.CustomOrder))).Layout.Sections[0].Query!;
        Assert.Equal(SidebarEntityKinds.All, c.Kinds);
        Assert.Equal(SidebarSortMode.Alphabetical, c.Sort);

        // CustomOrder WITH playlists only is legal and preserved.
        var d = Apply(l, new SetQuery("sec_e",
            new SidebarEntityQuery(SidebarEntityKinds.Playlists, SidebarSortMode.CustomOrder,
                Qualifier: SidebarPlaylistQualifier.BySpotify))).Layout.Sections[0].Query!;
        Assert.Equal(SidebarSortMode.CustomOrder, d.Sort);
        Assert.Equal(SidebarPlaylistQualifier.BySpotify, d.Qualifier);
    }

    [Fact]
    public void Setting_a_query_on_a_non_query_kind_is_a_no_change()
    {
        AssertRejected(Doc(Sec("sec_p", SidebarSectionKind.Pinned)),
            new SetQuery("sec_p", SidebarEntityQuery.PlaylistsAlphabetical), SidebarRejectReason.NoChange);
        AssertRejected(Doc(Sec("sec_e", SidebarSectionKind.EntityList, query: SidebarEntityQuery.Default)),
            new SetQuery("sec_e", SidebarEntityQuery.Default), SidebarRejectReason.NoChange);
        AssertRejected(Doc(Sec("sec_e", SidebarSectionKind.EntityList)),
            new SetQuery("sec_zz", SidebarEntityQuery.Default), SidebarRejectReason.UnknownSection);
    }

    [Fact]
    public void Setting_a_query_is_an_ordinary_undoable_edit()
    {
        var l = SidebarTemplates.Build(SidebarTemplates.V3Inspired);
        var id = IdOf(l, SidebarSectionKind.EntityList);
        var undo = new SidebarUndo();

        var cmd = new SetQuery(id, SidebarEntityQuery.PlaylistsAlphabetical);
        var r = SidebarLayoutReducer.Apply(l, cmd);
        Assert.True(r.Changed);
        undo.Push(l, cmd);

        Assert.Equal(SidebarSortMode.Alphabetical, r.Layout.Find(id)!.Query!.Sort);
        Assert.True(undo.TryUndo(r.Layout, out var back, out var label));
        Assert.Equal(SidebarUndoLabels.SetQuery, label);
        Assert.Equal(SidebarSortMode.Recents, back.Find(id)!.Query!.Sort);
    }

    // ── LAYOUT V2: extension sections ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Adding_an_extension_section_requires_a_well_formed_ref()
    {
        var l = Doc();
        AssertRejected(l, new AddSection(SidebarSectionKind.Extension, 0), SidebarRejectReason.ExtensionRefMissing);
        AssertRejected(l, new AddSection(SidebarSectionKind.Extension, 0, null, null,
            new SidebarExtensionRef("", "artist.topTracks", 1, SidebarJson.EmptyObject)),
            SidebarRejectReason.ExtensionRefMissing);
        AssertRejected(l, new AddSection(SidebarSectionKind.Extension, 0, null, null,
            new SidebarExtensionRef("wavee", "", 1, SidebarJson.EmptyObject)),
            SidebarRejectReason.ExtensionRefMissing);
    }

    [Fact]
    public void Adding_an_extension_section_stamps_the_ref_and_the_kind_defaults()
    {
        var r = Apply(Doc(), new AddSection(SidebarSectionKind.Extension, 0, null, null,
            Ref(config: """{"artistUri":"spotify:artist:7","limit":5}""")));
        Assert.True(r.Changed);

        var sec = r.Layout.Sections[0];
        Assert.Equal(SidebarSectionKind.Extension, sec.Kind);
        Assert.True(sec.IsExtension);
        Assert.False(sec.IsUnboundExtension);
        Assert.Equal("sidebar.section.extension", sec.TitleLocKey);
        Assert.Equal(10, sec.Opts.MaxItems);                        // a contributed feed ships bounded
        Assert.Empty(sec.ItemList);                                  // rows come from the contribution, never Items
        Assert.Equal("wavee/artist.topTracks", sec.Extension!.ContributionKey);
        Assert.Equal(5, sec.Extension.Config.GetProperty("limit").GetInt32());
        // The ids are trimmed on the way in.
        var trimmed = Apply(Doc(), new AddSection(SidebarSectionKind.Extension, 0, null, null,
            new SidebarExtensionRef("  wavee  ", "  queue  ", 2, SidebarJson.EmptyObject))).Layout.Sections[0];
        Assert.Equal("wavee", trimmed.Extension!.ExtensionId);
        Assert.Equal("queue", trimmed.Extension.ContributionId);
    }

    [Fact]
    public void Adding_an_extension_section_rejects_an_oversized_config()
    {
        var big = SidebarJson.Detach("{\"blob\":\"" + new string('x', SidebarExtensionRef.MaxConfigBytes) + "\"}");
        AssertRejected(Doc(), new AddSection(SidebarSectionKind.Extension, 0, null, null,
            new SidebarExtensionRef("wavee", "queue", 1, big)), SidebarRejectReason.ConfigTooLarge);
    }

    [Fact]
    public void A_ref_offered_on_a_non_extension_kind_is_ignored()
    {
        var sec = Apply(Doc(), new AddSection(SidebarSectionKind.Pinned, 0, null, null, Ref())).Layout.Sections[0];
        Assert.True(sec.Extension is null);
    }

    [Fact]
    public void Extension_sections_accept_no_items_and_only_their_supported_display_fields()
    {
        var l = Doc(ExtensionSection("sec_x", Ref()));
        AssertRejected(l, new AddItem("sec_x", Entity("itm_1", "spotify:playlist:1"), 0),
            SidebarRejectReason.KindDoesNotAcceptItems);

        foreach (var field in Enum.GetValues<SidebarDisplayField>())
        {
            bool allowed = field is SidebarDisplayField.Density or SidebarDisplayField.CollapsedByDefault
                or SidebarDisplayField.ShowInRail or SidebarDisplayField.MaxItems
                or SidebarDisplayField.EmptyBehavior;
            Assert.Equal(allowed, SidebarSectionKinds.AllowsDisplayField(SidebarSectionKind.Extension, field));
        }

        Assert.True(Apply(l, new SetDisplayOption("sec_x", SidebarDisplayField.MaxItems, 25)).Changed);
        AssertRejected(l, new SetDisplayOption("sec_x", SidebarDisplayField.Presentation, 1),
            SidebarRejectReason.NoChange);
        AssertRejected(l, new SetDisplayOption("sec_x", SidebarDisplayField.Artwork, 0),
            SidebarRejectReason.NoChange);
    }

    [Fact]
    public void Setting_the_extension_config_replaces_it_wholesale()
    {
        var l = Doc(ExtensionSection("sec_x", Ref(config: """{"limit":5}""")));
        var next = SidebarJson.Detach("""{"limit":20,"newField":"kept"}""");

        var r = Apply(l, new SetExtensionConfig("sec_x", next));
        Assert.True(r.Changed);
        var x = r.Layout.Sections[0].Extension!;
        Assert.Equal(20, x.Config.GetProperty("limit").GetInt32());
        Assert.Equal("kept", x.Config.GetProperty("newField").GetString());
        // Only the config changed — the ref's identity is untouched.
        Assert.Equal("wavee", x.ExtensionId);
        Assert.Equal("artist.topTracks", x.ContributionId);
        Assert.Equal(1, x.SchemaVersion);
    }

    [Fact]
    public void Setting_the_same_extension_config_is_a_no_change_even_from_a_different_instance()
    {
        var l = Doc(ExtensionSection("sec_x", Ref(config: """{"limit":5}""")));
        // A DIFFERENT JsonElement instance with the same content: the comparison is by raw JSON, not by backing
        // document (that is exactly the bug a synthesized record equality would have shipped).
        AssertRejected(l, new SetExtensionConfig("sec_x", SidebarJson.Detach("""{"limit":5}""")),
            SidebarRejectReason.NoChange);
    }

    [Fact]
    public void Setting_the_extension_config_rejects_over_the_sixty_four_kib_cap()
    {
        var l = Doc(ExtensionSection("sec_x", Ref()));
        var big = SidebarJson.Detach("{\"blob\":\"" + new string('x', SidebarExtensionRef.MaxConfigBytes) + "\"}");
        AssertRejected(l, new SetExtensionConfig("sec_x", big), SidebarRejectReason.ConfigTooLarge);
        Assert.True(SidebarJson.ByteCount(big) > SidebarExtensionRef.MaxConfigBytes);

        // Just under the cap is accepted — the boundary is a budget, not a suggestion.
        var ok = SidebarJson.Detach("{\"blob\":\"" + new string('x', SidebarExtensionRef.MaxConfigBytes - 64) + "\"}");
        Assert.True(SidebarJson.ByteCount(ok) <= SidebarExtensionRef.MaxConfigBytes);
        Assert.True(Apply(l, new SetExtensionConfig("sec_x", ok)).Changed);
    }

    [Fact]
    public void Setting_the_extension_config_on_the_wrong_kind_or_an_unbound_section_changes_nothing()
    {
        var config = SidebarJson.Detach("""{"limit":1}""");
        AssertRejected(Doc(Sec("sec_p", SidebarSectionKind.Pinned)), new SetExtensionConfig("sec_p", config),
            SidebarRejectReason.NoChange);
        AssertRejected(Doc(ExtensionSection("sec_x", null)), new SetExtensionConfig("sec_x", config),
            SidebarRejectReason.ExtensionRefMissing);
        AssertRejected(Doc(ExtensionSection("sec_x", Ref())), new SetExtensionConfig("sec_zz", config),
            SidebarRejectReason.UnknownSection);
    }

    [Fact]
    public void Setting_the_extension_config_is_undoable()
    {
        var l = Doc(ExtensionSection("sec_x", Ref(config: """{"limit":5}""")));
        var undo = new SidebarUndo();

        var cmd = new SetExtensionConfig("sec_x", SidebarJson.Detach("""{"limit":50}"""));
        var r = SidebarLayoutReducer.Apply(l, cmd);
        Assert.True(r.Changed);
        undo.Push(l, cmd);

        Assert.True(undo.TryUndo(r.Layout, out var back, out var label));
        Assert.Equal(SidebarUndoLabels.SetExtensionConfig, label);
        Assert.Equal(5, back.Sections[0].Extension!.Config.GetProperty("limit").GetInt32());
        Assert.True(undo.TryRedo(back, out var again, out _));
        Assert.Equal(50, again.Sections[0].Extension!.Config.GetProperty("limit").GetInt32());
    }

    [Fact]
    public void An_extension_section_moves_and_removes_like_any_other_section()
    {
        var l = Doc(ExtensionSection("sec_x", Ref()), Sec("sec_g", SidebarSectionKind.CustomGroup));

        var moved = Apply(l, new MoveSection("sec_x", "sec_g", 0));      // a contribution may nest in a group
        Assert.True(moved.Changed);
        Assert.Equal("sec_x", moved.Layout.Sections[0].ChildList[0].Id);
        Assert.Equal("wavee", moved.Layout.Sections[0].ChildList[0].Extension!.ExtensionId);   // the ref survives the move

        Assert.True(Apply(l, new RemoveSection("sec_x")).Changed);
        var clone = Apply(l, new DuplicateSection("sec_x")).Layout.Sections[1];
        Assert.NotEqual("sec_x", clone.Id);
        Assert.Equal(Ref(), clone.Extension);                            // the duplicate carries the same contribution
    }

    // ── LAYOUT V2: action bindings ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Setting_an_item_action_binds_clears_and_undoes()
    {
        var l = Doc(Sec("sec_g", SidebarSectionKind.CustomGroup, items: [ActionItem("itm_a", "wavee.play")]));
        var binding = new SidebarActionBinding("wavee", "play", SidebarActionTargetMode.FixedEntity,
            "spotify:playlist:1", SidebarJson.Detach("""{"shuffle":true}"""));
        var undo = new SidebarUndo();

        var bind = new SetItemAction("sec_g", "itm_a", binding);
        var bound = SidebarLayoutReducer.Apply(l, bind);
        Assert.True(bound.Changed);
        undo.Push(l, bind);
        Assert.Equal(binding, bound.Layout.Sections[0].ItemList[0].Action);
        Assert.True(bound.Layout.Sections[0].ItemList[0].HasRunnableAction);

        // Re-binding the SAME binding is a NoChange (content equality over the arguments element).
        AssertRejected(bound.Layout, new SetItemAction("sec_g", "itm_a",
            new SidebarActionBinding("wavee", "play", SidebarActionTargetMode.FixedEntity, "spotify:playlist:1",
                SidebarJson.Detach("""{"shuffle":true}"""))), SidebarRejectReason.NoChange);

        // null CLEARS.
        var cleared = SidebarLayoutReducer.Apply(bound.Layout, new SetItemAction("sec_g", "itm_a", null));
        Assert.True(cleared.Changed);
        Assert.Null(cleared.Layout.Sections[0].ItemList[0].Action);
        Assert.False(cleared.Layout.Sections[0].ItemList[0].HasRunnableAction);
        // …and clearing an already-unbound item is a NoChange.
        AssertRejected(cleared.Layout, new SetItemAction("sec_g", "itm_a", null), SidebarRejectReason.NoChange);

        Assert.True(undo.TryUndo(bound.Layout, out var back, out var label));
        Assert.Equal(SidebarUndoLabels.SetItemAction, label);
        Assert.Null(back.Sections[0].ItemList[0].Action);
        Assert.True(undo.TryRedo(back, out var again, out _));
        Assert.Equal(binding, again.Sections[0].ItemList[0].Action);
    }

    [Fact]
    public void Setting_an_item_action_normalizes_the_binding()
    {
        var l = Doc(Sec("sec_g", SidebarSectionKind.CustomGroup, items: [ActionItem("itm_a", "wavee.play")]));

        // Ids are trimmed; a target key the MODE cannot use is dropped rather than kept as dead data.
        var r = Apply(l, new SetItemAction("sec_g", "itm_a",
            new SidebarActionBinding("  wavee  ", "  shuffleAll  ", SidebarActionTargetMode.NowPlaying,
                "spotify:track:stale", null)));
        Assert.True(r.Changed);
        var bound = r.Layout.Sections[0].ItemList[0].Action!;
        Assert.Equal("wavee", bound.ProviderId);
        Assert.Equal("shuffleAll", bound.ActionId);
        Assert.Equal("wavee.shuffleAll", bound.ActionKey);
        Assert.Null(bound.TargetKey);
        Assert.True(bound.IsResolvable);

        // A fixed mode KEEPS its key…
        var fixedKey = Apply(l, new SetItemAction("sec_g", "itm_a",
            SidebarActionBinding.Fixed("wavee", "play", "spotify:album:9"))).Layout.Sections[0].ItemList[0].Action!;
        Assert.Equal("spotify:album:9", fixedKey.TargetKey);
        Assert.True(fixedKey.RequiresTargetKey);
        // …and a fixed mode with NO key stays bound but unresolvable (visible-but-disabled, never dropped).
        var orphan = Apply(l, new SetItemAction("sec_g", "itm_a",
            new SidebarActionBinding("wavee", "play", SidebarActionTargetMode.FixedTrack, "   ", null)))
            .Layout.Sections[0].ItemList[0].Action!;
        Assert.Null(orphan.TargetKey);
        Assert.False(orphan.IsResolvable);
    }

    [Fact]
    public void Setting_a_malformed_action_binding_is_rejected_rather_than_clearing_it()
    {
        var l = Doc(Sec("sec_g", SidebarSectionKind.CustomGroup,
            items: [ActionItem("itm_a", "wavee.play") with { Action = SidebarActionBinding.Simple("wavee", "play") }]));

        // A blank provider/action id cannot address anything — refusing protects the binding that IS there.
        AssertRejected(l, new SetItemAction("sec_g", "itm_a",
            new SidebarActionBinding("", "play", SidebarActionTargetMode.None, null, null)),
            SidebarRejectReason.NoChange);
        AssertRejected(l, new SetItemAction("sec_g", "itm_a",
            new SidebarActionBinding("wavee", "   ", SidebarActionTargetMode.None, null, null)),
            SidebarRejectReason.NoChange);
        Assert.Equal(SidebarActionBinding.Simple("wavee", "play"), l.Sections[0].ItemList[0].Action);

        AssertRejected(l, new SetItemAction("sec_g", "itm_zz", SidebarActionBinding.Simple("wavee", "play")),
            SidebarRejectReason.UnknownItem);
        AssertRejected(l, new SetItemAction("sec_zz", "itm_a", SidebarActionBinding.Simple("wavee", "play")),
            SidebarRejectReason.UnknownSection);
    }

    [Fact]
    public void Setting_an_item_action_rejects_oversized_arguments()
    {
        var l = Doc(Sec("sec_g", SidebarSectionKind.CustomGroup, items: [ActionItem("itm_a", "wavee.play")]));
        var big = SidebarJson.Detach("{\"blob\":\"" + new string('x', SidebarExtensionRef.MaxConfigBytes) + "\"}");
        AssertRejected(l, new SetItemAction("sec_g", "itm_a",
            new SidebarActionBinding("wavee", "play", SidebarActionTargetMode.None, null, big)),
            SidebarRejectReason.ConfigTooLarge);
    }

    [Fact]
    public void Setting_an_item_action_never_changes_the_items_target()
    {
        // Binding an action onto an ENTITY row does not silently turn it into an action row: the picker sets Target
        // when it creates the item, and this command only rewrites the binding.
        var l = Doc(Sec("sec_g", SidebarSectionKind.CustomGroup, items: [Entity("itm_e", "spotify:playlist:1")]));
        var r = Apply(l, new SetItemAction("sec_g", "itm_e", SidebarActionBinding.Simple("wavee", "play")));
        Assert.True(r.Changed);
        Assert.Equal(SidebarItemTarget.Entity, r.Layout.Sections[0].ItemList[0].Target);
        Assert.False(r.Layout.Sections[0].ItemList[0].HasRunnableAction);   // gated on Target == Action
    }

    [Fact]
    public void Adding_an_item_carries_and_normalizes_an_inline_action_binding()
    {
        var l = Doc(Sec("sec_g", SidebarSectionKind.CustomGroup));
        var seeded = Apply(l, new AddItem("sec_g",
            ActionItem("itm_a", "wavee.play") with
            {
                Action = new SidebarActionBinding(" wavee ", " play ", SidebarActionTargetMode.ActiveRoute, "stale", null),
            }, 0));
        Assert.True(seeded.Changed);
        var action = seeded.Layout.Sections[0].ItemList[0].Action!;
        Assert.Equal("wavee", action.ProviderId);
        Assert.Null(action.TargetKey);

        // A malformed inline binding is dropped; the item itself is still legal.
        var dropped = Apply(l, new AddItem("sec_g",
            ActionItem("itm_b", "wavee.play") with
            {
                Action = new SidebarActionBinding("", "", SidebarActionTargetMode.None, null, null),
            }, 0));
        Assert.True(dropped.Changed);
        Assert.Null(dropped.Layout.Sections[0].ItemList[0].Action);
        Assert.Equal(SidebarItemTarget.Action, dropped.Layout.Sections[0].ItemList[0].Target);
    }

    // ── LAYOUT V2: include / exclude uri sets ────────────────────────────────────────────────────────────────────

    [Fact]
    public void Setting_a_query_normalizes_the_include_and_exclude_uri_sets()
    {
        var l = Doc(Sec("sec_e", SidebarSectionKind.EntityList, query: SidebarEntityQuery.Default));

        var q = Apply(l, new SetQuery("sec_e", SidebarEntityQuery.Default with
        {
            Kinds = SidebarEntityKinds.Artists,
            IncludeUris = ["  spotify:artist:a  ", "spotify:artist:a", "", "   ", "spotify:artist:b"],
            ExcludeUris = [],
        })).Layout.Sections[0].Query!;

        Assert.Equal(new[] { "spotify:artist:a", "spotify:artist:b" }, q.IncludeList);   // trimmed + deduped, order kept
        Assert.Null(q.ExcludeUris);                                                      // empty ⇒ null, never []
        Assert.True(q.HasIncludeSet);
        Assert.False(q.HasExcludeSet);
    }

    [Fact]
    public void The_uri_sets_truncate_at_the_five_hundred_cap()
    {
        var l = Doc(Sec("sec_e", SidebarSectionKind.EntityList, query: SidebarEntityQuery.Default));
        var many = new string[SidebarLayoutReducer.MaxUrisPerSet + 25];
        for (int i = 0; i < many.Length; i++) many[i] = "spotify:artist:" + i;

        var q = Apply(l, new SetQuery("sec_e", SidebarEntityQuery.Default with { IncludeUris = many }))
            .Layout.Sections[0].Query!;
        Assert.Equal(SidebarLayoutReducer.MaxUrisPerSet, q.IncludeList.Count);
        Assert.Equal("spotify:artist:0", q.IncludeList[0]);
    }

    [Fact]
    public void The_uri_sets_survive_independently_of_the_scalar_repairs()
    {
        var l = Doc(Sec("sec_e", SidebarSectionKind.EntityList, query: SidebarEntityQuery.Default));
        var q = Apply(l, new SetQuery("sec_e", new SidebarEntityQuery(SidebarEntityKinds.None,
            SidebarSortMode.CustomOrder, true, SidebarPlaylistQualifier.ByYou,
            IncludeUris: ["spotify:artist:a"]))).Layout.Sections[0].Query!;

        Assert.Equal(SidebarEntityKinds.All, q.Kinds);                 // the existing repairs still apply…
        Assert.Equal(SidebarSortMode.Alphabetical, q.Sort);
        Assert.Equal(new[] { "spotify:artist:a" }, q.IncludeList);      // …and the uri set survives them
    }

    [Fact]
    public void The_same_uri_set_content_in_a_different_list_instance_is_a_no_change()
    {
        var l = Doc(Sec("sec_e", SidebarSectionKind.EntityList,
            query: SidebarEntityQuery.Default with { IncludeUris = ["spotify:artist:a"] }));
        // A different list INSTANCE with the same content: element-wise equality, not reference equality.
        AssertRejected(l, new SetQuery("sec_e", SidebarEntityQuery.Default with
        {
            IncludeUris = new List<string> { "spotify:artist:a" },
        }), SidebarRejectReason.NoChange);
    }

    [Fact]
    public void Uri_sets_survive_only_on_a_library_query_kind()
    {
        Assert.True(SidebarSectionKinds.SupportsLibraryQuery(SidebarSectionKind.EntityList));
        Assert.True(SidebarSectionKinds.SupportsLibraryQuery(SidebarSectionKind.PlaylistTree));
        Assert.False(SidebarSectionKinds.SupportsLibraryQuery(SidebarSectionKind.Pinned));
        Assert.False(SidebarSectionKinds.SupportsLibraryQuery(SidebarSectionKind.Extension));

        // A hand-edited document that parked a library query (with uri sets) on a Pinned section: the section keeps
        // its query scalars, but the sets are stripped the next time a command TOUCHES it — never eagerly, never
        // elsewhere.
        var stray = SidebarEntityQuery.Default with
        {
            Sort = SidebarSortMode.Alphabetical,
            IncludeUris = ["spotify:artist:a"],
            ExcludeUris = ["spotify:artist:b"],
        };
        var l = Doc(Sec("sec_p", SidebarSectionKind.Pinned, query: stray), Sec("sec_h", SidebarSectionKind.Header));

        var elsewhere = Apply(l, new SetSectionHidden("sec_h", true));
        Assert.True(elsewhere.Changed);
        Assert.True(elsewhere.Layout.Find("sec_p")!.Query!.HasIncludeSet);   // untouched section, untouched query

        var touched = Apply(l, new SetSectionCollapsed("sec_p", true));
        Assert.True(touched.Changed);
        var repaired = touched.Layout.Find("sec_p")!.Query!;
        Assert.Null(repaired.IncludeUris);
        Assert.Null(repaired.ExcludeUris);
        Assert.Equal(SidebarSortMode.Alphabetical, repaired.Sort);            // the scalars are NOT discarded

        // The kind-aware repair says the same thing directly.
        Assert.False(SidebarLayoutReducer.RepairQuery(stray, SidebarSectionKind.Pinned).HasIncludeSet);
        Assert.True(SidebarLayoutReducer.RepairQuery(stray, SidebarSectionKind.EntityList).HasIncludeSet);
        Assert.True(SidebarLayoutReducer.RepairQuery(stray).HasIncludeSet);   // the 1-arg overload IS the EntityList one
    }

    [Fact]
    public void A_playlist_trees_query_pins_kinds_to_playlists_and_preserves_the_other_fields()
    {
        var layout = Doc(Sec("sec_tree", SidebarSectionKind.PlaylistTree));
        var requested = new SidebarEntityQuery(
            SidebarEntityKinds.Albums | SidebarEntityKinds.Artists,
            SidebarSortMode.Creator,
            Descending: true,
            Qualifier: SidebarPlaylistQualifier.BySpotify,
            IncludeUris: ["spotify:playlist:keep"],
            ExcludeUris: ["spotify:playlist:drop"]);

        var result = Apply(layout, new SetQuery("sec_tree", requested));
        Assert.True(result.Changed);
        var query = result.Layout.Find("sec_tree")!.Query!;
        Assert.Equal(SidebarEntityKinds.Playlists, query.Kinds);
        Assert.Equal(SidebarSortMode.Creator, query.Sort);
        Assert.True(query.Descending);
        Assert.Equal(SidebarPlaylistQualifier.BySpotify, query.Qualifier);
        Assert.Equal(["spotify:playlist:keep"], query.IncludeList);
        Assert.Equal(["spotify:playlist:drop"], query.ExcludeList);
    }

    [Fact]
    public void A_null_playlist_tree_query_means_source_order_not_the_entity_list_default()
    {
        Assert.Equal(SidebarEntityQuery.PlaylistTreeSourceOrder,
            SidebarSectionKinds.EffectiveQuery(SidebarSectionKind.PlaylistTree, null));
        Assert.Equal(SidebarEntityQuery.Default,
            SidebarSectionKinds.EffectiveQuery(SidebarSectionKind.EntityList, null));

        var implicitOrder = Doc(Sec("tree", SidebarSectionKind.PlaylistTree));
        var explicitOrder = Doc(Sec("other", SidebarSectionKind.PlaylistTree,
            query: SidebarEntityQuery.PlaylistTreeSourceOrder));
        Assert.True(SidebarLayoutCompare.EqualIgnoringIds(implicitOrder, explicitOrder));

        var recents = Doc(Sec("other", SidebarSectionKind.PlaylistTree,
            query: SidebarEntityQuery.Default with { Kinds = SidebarEntityKinds.Playlists }));
        Assert.False(SidebarLayoutCompare.EqualIgnoringIds(implicitOrder, recents));
    }

    [Fact]
    public void Template_pristine_comparison_ignores_the_top_bar_but_full_comparison_does_not()
    {
        var template = SidebarTemplates.Build(SidebarTemplates.Curated);
        var changedTopBar = template with
        {
            TopBar = [new SidebarItemSpec("item", SidebarItemTarget.Route, "liked")],
        };

        Assert.True(SidebarLayoutCompare.EqualTemplateSectionsIgnoringIds(template, changedTopBar));
        Assert.False(SidebarLayoutCompare.EqualIgnoringIds(template, changedTopBar));
    }

    [Fact]
    public void NormalizeUris_is_a_total_function()
    {
        Assert.Null(SidebarLayoutReducer.NormalizeUris(null));
        Assert.Null(SidebarLayoutReducer.NormalizeUris(Array.Empty<string>()));
        Assert.Null(SidebarLayoutReducer.NormalizeUris(["", "   "]));
        // An already-canonical list is returned AS IS (no pointless copy).
        var canonical = new[] { "a", "b" };
        Assert.Same(canonical, SidebarLayoutReducer.NormalizeUris(canonical));
        Assert.Equal(new[] { "a", "b" }, SidebarLayoutReducer.NormalizeUris([" a ", "a", "b", ""]));
    }

    // ── templates / reset ────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Applying_a_template_replaces_the_sections_and_the_template_id()
    {
        var l = Doc(Sec("sec_x", SidebarSectionKind.Header));
        var r = Apply(l, new ApplyTemplate(SidebarTemplates.Minimal));
        Assert.True(r.Changed);
        Assert.Equal(SidebarTemplates.Minimal, r.Layout.TemplateId);
        Assert.True(SidebarLayoutCompare.EqualIgnoringIds(r.Layout,
            SidebarTemplates.Build(SidebarTemplates.Minimal)));
    }

    [Fact]
    public void Applying_an_unknown_template_id_is_rejected()
        => AssertRejected(Curated(), new ApplyTemplate("nope"), SidebarRejectReason.UnknownTemplate);

    [Fact]
    public void Resetting_the_layout_rebuilds_from_its_own_template_id()
    {
        var l = SidebarTemplates.Build(SidebarTemplates.Minimal);
        var edited = Apply(l, new AddSection(SidebarSectionKind.Header, 0)).Layout;
        Assert.Equal(4, edited.Sections.Count);

        var r = Apply(edited, new ResetLayout());
        Assert.True(r.Changed);
        Assert.Equal(SidebarTemplates.Minimal, r.Layout.TemplateId);
        Assert.True(SidebarLayoutCompare.EqualIgnoringIds(r.Layout, l));
    }

    [Fact]
    public void Resetting_a_hand_edited_document_falls_back_to_wavee_curated()
    {
        var l = new SidebarCustomLayout("hand-edited", [Sec("sec_x", SidebarSectionKind.Header)]);
        var r = Apply(l, new ResetLayout());
        Assert.True(r.Changed);
        Assert.Equal(SidebarTemplates.Curated, r.Layout.TemplateId);
        Assert.True(SidebarLayoutCompare.EqualIgnoringIds(r.Layout, Curated()));
    }

    // ── the shell TOP BAR band ───────────────────────────────────────────────────────────────────────────────────
    // The band is ONE global list on the same document, so it rides the same reducer/undo/compare machinery. What
    // these facts pin down: null == "never customized" resolves to the built-in Home; [] == "emptied on purpose" is
    // a DIFFERENT state and stays empty; the cap and the (target, key) dedupe are the reducer's, not the UI's; and
    // a template/reset never touches shell chrome.

    [Fact]
    public void The_default_top_bar_is_the_home_route_shortcut()
    {
        var l = Doc(Sec("sec_p", SidebarSectionKind.Pinned));
        Assert.Null(l.TopBar);                                    // never customized
        var band = l.EffectiveTopBar;
        Assert.Single(band);
        Assert.Equal(SidebarItemTarget.Route, band[0].Target);
        Assert.Equal("home", band[0].Key);
        Assert.Equal(SidebarIds.TopBarHomeItem, band[0].Id);      // STABLE id — remove-by-id depends on it
        Assert.Same(SidebarCustomLayout.DefaultTopBar, band);      // no per-read allocation
    }

    [Fact]
    public void Adding_a_top_bar_item_materializes_the_default_alongside_it()
    {
        var l = Doc(Sec("sec_p", SidebarSectionKind.Pinned));
        var r = Apply(l, new AddTopBarItem(TopRoute("liked", "Heart"), 1));
        Assert.True(r.Changed);
        Assert.NotNull(r.Layout.TopBar);
        Assert.Equal(new[] { "home", "liked" }, KeysOfBand(r.Layout));
    }

    [Fact]
    public void Adding_a_top_bar_item_clamps_the_index_and_keeps_a_unique_incoming_id()
    {
        var l = Doc(Sec("sec_p", SidebarSectionKind.Pinned));
        var seed = new SidebarItemSpec("itm_abcdef01", SidebarItemTarget.Route, "search");
        var r = Apply(l, new AddTopBarItem(seed, 99));
        Assert.True(r.Changed);
        Assert.Equal(new[] { "home", "search" }, KeysOfBand(r.Layout));
        Assert.Equal("itm_abcdef01", r.Layout.EffectiveTopBar[1].Id);

        var front = Apply(r.Layout, new AddTopBarItem(TopRoute("albums"), -5));
        Assert.True(front.Changed);
        Assert.Equal(new[] { "albums", "home", "search" }, KeysOfBand(front.Layout));
    }

    [Fact]
    public void Adding_a_top_bar_item_dedupes_by_target_and_key()
    {
        var l = Doc(Sec("sec_p", SidebarSectionKind.Pinned));
        // The built-in Home counts: the band the user SEES is the band being edited.
        AssertRejected(l, new AddTopBarItem(TopRoute("home"), 0), SidebarRejectReason.DuplicateItem);

        // Same key, DIFFERENT target ⇒ not a duplicate.
        var track = new SidebarItemSpec(SidebarIds.NewItem(), SidebarItemTarget.Track, "home");
        Assert.True(Apply(l, new AddTopBarItem(track, 1)).Changed);
    }

    [Fact]
    public void Adding_a_top_bar_item_enforces_the_six_item_cap()
    {
        var l = Doc(Sec("sec_p", SidebarSectionKind.Pinned));
        string[] keys = ["liked", "albums", "artists", "podcasts", "local"];   // + the built-in home == 6
        for (int i = 0; i < keys.Length; i++)
            l = Apply(l, new AddTopBarItem(TopRoute(keys[i]), int.MaxValue)).Layout;

        Assert.Equal(SidebarLayoutReducer.MaxTopBarItems, l.EffectiveTopBar.Count);
        AssertRejected(l, new AddTopBarItem(TopRoute("history"), 0), SidebarRejectReason.SectionCapReached);
    }

    [Fact]
    public void Adding_a_top_bar_item_with_an_invalid_icon_is_rejected()
        => AssertRejected(Doc(Sec("sec_p", SidebarSectionKind.Pinned)),
            new AddTopBarItem(TopRoute("liked", "NotAnIcon"), 0), SidebarRejectReason.InvalidIcon);

    [Fact]
    public void Moving_a_top_bar_item_clamps_the_destination_and_no_ops_in_place()
    {
        var l = Doc(Sec("sec_p", SidebarSectionKind.Pinned));
        l = Apply(l, new AddTopBarItem(TopRoute("liked"), 1)).Layout;
        l = Apply(l, new AddTopBarItem(TopRoute("albums"), 2)).Layout;

        var moved = Apply(l, new MoveTopBarItem(0, 99));
        Assert.True(moved.Changed);
        Assert.Equal(new[] { "liked", "albums", "home" }, KeysOfBand(moved.Layout));

        AssertRejected(l, new MoveTopBarItem(1, 1), SidebarRejectReason.NoChange);
        AssertRejected(l, new MoveTopBarItem(3, 0), SidebarRejectReason.UnknownItem);
        AssertRejected(l, new MoveTopBarItem(-1, 0), SidebarRejectReason.UnknownItem);
    }

    [Fact]
    public void Removing_the_last_top_bar_item_empties_the_list_rather_than_reverting_to_the_default()
    {
        var l = Doc(Sec("sec_p", SidebarSectionKind.Pinned));
        var r = Apply(l, new RemoveTopBarItem(SidebarIds.TopBarHomeItem));
        Assert.True(r.Changed);
        Assert.NotNull(r.Layout.TopBar);            // NOT null — null would re-render the Home the user just removed
        Assert.Empty(r.Layout.TopBar!);
        Assert.Empty(r.Layout.EffectiveTopBar);
    }

    [Fact]
    public void Removing_an_unknown_top_bar_item_is_rejected()
        => AssertRejected(Doc(Sec("sec_p", SidebarSectionKind.Pinned)),
            new RemoveTopBarItem("itm_nope"), SidebarRejectReason.UnknownItem);

    [Fact]
    public void Removing_then_re_adding_a_top_bar_item_at_its_former_index_restores_the_band()
    {
        var l = Doc(Sec("sec_p", SidebarSectionKind.Pinned));
        l = Apply(l, new AddTopBarItem(TopRoute("liked"), 1)).Layout;
        l = Apply(l, new AddTopBarItem(TopRoute("albums"), 2)).Layout;

        var removed = l.EffectiveTopBar[1];
        var after = Apply(l, new RemoveTopBarItem(removed.Id)).Layout;
        Assert.Equal(new[] { "home", "albums" }, KeysOfBand(after));

        // The toast's undo: a forward AddTopBarItem at the former index, keeping the item's id (it is free again).
        var restored = Apply(after, new AddTopBarItem(removed, 1));
        Assert.True(restored.Changed);
        Assert.Equal(new[] { "home", "liked", "albums" }, KeysOfBand(restored.Layout));
        Assert.Equal(removed.Id, restored.Layout.EffectiveTopBar[1].Id);
    }

    [Fact]
    public void Top_bar_items_share_the_documents_item_id_space()
    {
        // A minted top-bar id may never collide with a section item's, nor with the built-in Home id (which stays
        // RESERVED even after Home leaves the band, so a later re-add of the default cannot clash).
        var l = Doc(Sec("sec_g", SidebarSectionKind.CustomGroup, items: [Route("itm_1", "home")]));
        l = Apply(l, new AddTopBarItem(new SidebarItemSpec("itm_1", SidebarItemTarget.Route, "liked"), 1)).Layout;
        Assert.NotEqual("itm_1", l.EffectiveTopBar[1].Id);

        var emptied = Apply(l, new RemoveTopBarItem(SidebarIds.TopBarHomeItem)).Layout;
        var reused = Apply(emptied,
            new AddTopBarItem(new SidebarItemSpec(SidebarIds.TopBarHomeItem, SidebarItemTarget.Route, "search"), 0)).Layout;
        Assert.NotEqual(SidebarIds.TopBarHomeItem, reused.EffectiveTopBar[0].Id);
    }

    [Fact]
    public void Item_property_commands_address_the_band_through_the_sentinel_section_id()
    {
        var l = Doc(Sec("sec_p", SidebarSectionKind.Pinned));
        string home = SidebarIds.TopBarHomeItem;

        var labelled = Apply(l, new SetItemLabel(SidebarIds.TopBarSection, home, "  Start  "));
        Assert.True(labelled.Changed);
        Assert.Equal("Start", labelled.Layout.EffectiveTopBar[0].LabelOverride);   // trimmed by the same Shorten

        var reiconed = Apply(labelled.Layout, new SetItemIcon(SidebarIds.TopBarSection, home, "Star"));
        Assert.True(reiconed.Changed);
        Assert.Equal("Star", reiconed.Layout.EffectiveTopBar[0].IconOverride);

        var bound = Apply(reiconed.Layout,
            new SetItemAction(SidebarIds.TopBarSection, home, SidebarActionBinding.Simple("wavee", "play")));
        Assert.True(bound.Changed);
        Assert.Equal("wavee.play", bound.Layout.EffectiveTopBar[0].Action!.ActionKey);

        // Same value twice ⇒ NoChange; an unknown tile id ⇒ UnknownItem (never UnknownSection — "topbar" always
        // resolves).
        AssertRejected(bound.Layout, new SetItemIcon(SidebarIds.TopBarSection, home, "Star"),
            SidebarRejectReason.NoChange);
        AssertRejected(bound.Layout, new SetItemLabel(SidebarIds.TopBarSection, "itm_nope", "x"),
            SidebarRejectReason.UnknownItem);
        AssertRejected(bound.Layout, new SetItemIcon(SidebarIds.TopBarSection, home, "NotAnIcon"),
            SidebarRejectReason.InvalidIcon);
        // The sections are untouched by every one of those.
        Assert.Single(bound.Layout.Sections);
    }

    [Fact]
    public void Applying_a_template_or_resetting_preserves_the_top_bar()
    {
        var l = Doc(Sec("sec_p", SidebarSectionKind.Pinned));
        l = Apply(l, new AddTopBarItem(TopRoute("liked", "Heart"), 0)).Layout;
        var band = l.TopBar;
        Assert.Equal(new[] { "liked", "home" }, KeysOfBand(l));

        var templated = Apply(l, new ApplyTemplate(SidebarTemplates.Minimal));
        Assert.True(templated.Changed);
        Assert.Equal(new[] { "liked", "home" }, KeysOfBand(templated.Layout));
        Assert.True(ReferenceEquals(band, templated.Layout.TopBar));   // carried by reference: a template is a SECTION preset

        var reset = Apply(templated.Layout, new ResetLayout());
        Assert.True(reset.Changed);
        Assert.Equal(new[] { "liked", "home" }, KeysOfBand(reset.Layout));

        // …and an emptied band survives a template too (it must not silently restore Home).
        var emptied = Apply(l, new RemoveTopBarItem(SidebarIds.TopBarHomeItem)).Layout;
        emptied = Apply(emptied, new RemoveTopBarItem(emptied.EffectiveTopBar[0].Id)).Layout;
        var afterTemplate = Apply(emptied, new ApplyTemplate(SidebarTemplates.Blank));
        Assert.True(afterTemplate.Changed);
        Assert.NotNull(afterTemplate.Layout.TopBar);
        Assert.Empty(afterTemplate.Layout.EffectiveTopBar);
    }

    [Fact]
    public void Top_bar_edits_ride_the_same_undo_ring_as_section_edits()
    {
        var l = Doc(Sec("sec_p", SidebarSectionKind.Pinned));
        var undo = new SidebarUndo();

        Step(ref l, undo, new AddTopBarItem(TopRoute("liked"), 1));
        Assert.Equal(SidebarUndoLabels.AddTopBarItem, undo.UndoLabelLocKey);
        var removed = Step(ref l, undo, new RemoveTopBarItem(SidebarIds.TopBarHomeItem));
        Assert.Equal(new[] { "liked" }, KeysOfBand(removed));

        Assert.True(undo.TryUndo(removed, out var back1, out _));
        Assert.Equal(new[] { "home", "liked" }, KeysOfBand(back1));
        Assert.True(undo.TryUndo(back1, out var back2, out _));
        Assert.Null(back2.TopBar);                       // all the way back to "never customized"
        Assert.Equal(new[] { "home" }, KeysOfBand(back2));

        Assert.True(undo.TryRedo(back2, out var again, out _));
        Assert.Equal(new[] { "home", "liked" }, KeysOfBand(again));
    }

    [Fact]
    public void Comparison_distinguishes_never_customized_from_emptied()
    {
        var never = Doc(Sec("sec_p", SidebarSectionKind.Pinned));
        var emptied = never with { TopBar = Array.Empty<SidebarItemSpec>() };
        Assert.False(SidebarLayoutCompare.Equal(never, emptied));
        Assert.Equal("topBar", SidebarLayoutCompare.FirstDifference(never, emptied));

        var homeOnly = never with { TopBar = SidebarCustomLayout.DefaultTopBar };
        Assert.False(SidebarLayoutCompare.Equal(never, homeOnly));      // null and [home] are different DOCUMENTS…
        Assert.Equal(homeOnly.EffectiveTopBar.Count, never.EffectiveTopBar.Count);   // …that render identically

        var relabelled = never with
        {
            TopBar = [SidebarCustomLayout.DefaultTopBar[0] with { LabelOverride = "Start" }],
        };
        Assert.Equal("topBar[0].label", SidebarLayoutCompare.FirstDifference(homeOnly, relabelled));
    }

    // ── invariants ───────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Apply_never_mutates_its_input_document()
    {
        var l = Doc(
            Sec("sec_p", SidebarSectionKind.Pinned, items: [Entity("itm_o", "spotify:playlist:1", label: "Alias")]),
            Sec("sec_g", SidebarSectionKind.CustomGroup, items: [Route("itm_1", "home")],
                children: [Sec("sec_c", SidebarSectionKind.Header)]),
            Sec("sec_e", SidebarSectionKind.EntityList, query: SidebarEntityQuery.Default),
            Sec("sec_d", SidebarSectionKind.Divider));

        var idsBefore = SidebarLayoutCompare.AllIds(l);
        var countBefore = l.Sections.Count;
        var optsBefore = l.Sections[2].Opts;
        var queryBefore = l.Sections[2].Query;

        foreach (var cmd in new SidebarCommand[]
        {
            new AddSection(SidebarSectionKind.Header, 1),
            new AddSection(SidebarSectionKind.Header, 0, "sec_g"),
            new RemoveSection("sec_d"),
            // sec_g's only child is a Header, so this EXECUTES the deep-clone path rather than bouncing off
            // defect 9's KindNotDuplicable guard — a rejection would prove nothing here, because a rejection
            // copies nothing.
            new DuplicateSection("sec_g", "copy"),
            new RenameSection("sec_p", "Mine"),
            new SetSectionHidden("sec_p", true),
            new SetSectionCollapsed("sec_p", true),
            new MoveSection("sec_p", null, 3),
            new MoveSection("sec_c", null, 0),
            new AddItem("sec_g", Route("itm_x", "search"), 0),
            new MoveItem("sec_g", 0, "sec_p", 0),
            new RemoveItem("sec_g", "itm_1"),
            new SetItemLabel("sec_g", "itm_1", "L"),
            new SetItemIcon("sec_g", "itm_1", "Heart"),
            new SetDisplayOption("sec_e", SidebarDisplayField.MaxItems, 12),
            new SetQuery("sec_e", SidebarEntityQuery.PlaylistsAlphabetical),
            new SetQuery("sec_e", SidebarEntityQuery.Default with { IncludeUris = ["spotify:artist:a"] }),
            new SetExtensionConfig("sec_x", SidebarJson.Detach("""{"limit":9}""")),
            new SetItemAction("sec_g", "itm_1", SidebarActionBinding.Simple("wavee", "play")),
            new AddTopBarItem(new SidebarItemSpec("itm_t", SidebarItemTarget.Route, "liked"), 0),
            new MoveTopBarItem(0, 1),
            new RemoveTopBarItem(SidebarIds.TopBarHomeItem),
            new SetItemLabel(SidebarIds.TopBarSection, SidebarIds.TopBarHomeItem, "Start"),
            new SetItemIcon(SidebarIds.TopBarSection, SidebarIds.TopBarHomeItem, "Star"),
            new SetItemAction(SidebarIds.TopBarSection, SidebarIds.TopBarHomeItem,
                SidebarActionBinding.Simple("wavee", "play")),
            new ApplyTemplate(SidebarTemplates.Blank),
            new ResetLayout(),
        })
        {
            SidebarLayoutReducer.Apply(l, cmd);
            SidebarLayoutReducer.Apply(l, cmd, new HashSet<string>(StringComparer.Ordinal));
        }

        Assert.Equal(idsBefore, SidebarLayoutCompare.AllIds(l));
        Assert.Equal(countBefore, l.Sections.Count);
        Assert.Equal(optsBefore, l.Sections[2].Opts);
        Assert.Equal(queryBefore, l.Sections[2].Query);
        Assert.Equal("Alias", l.Sections[0].ItemList[0].LabelOverride);
    }

    [Fact]
    public void Stale_pinned_overrides_are_pruned_on_next_touch_not_eagerly()
    {
        var l = Doc(
            Sec("sec_p", SidebarSectionKind.Pinned, items:
            [
                Entity("itm_live", "spotify:playlist:live", label: "Keep me"),
                Entity("itm_gone", "spotify:playlist:gone", label: "Prune me"),
            ]),
            Sec("sec_h", SidebarSectionKind.Header));

        var pins = new HashSet<string>(StringComparer.Ordinal) { "spotify:playlist:live" };

        // Touching a DIFFERENT section prunes nothing — an accidental unpin+repin must keep the alias.
        var other = SidebarLayoutReducer.Apply(l, new SetSectionHidden("sec_h", true), pins);
        Assert.True(other.Changed);
        Assert.Equal(2, other.Layout.Find("sec_p")!.ItemList.Count);

        // Touching the Pinned section prunes the stale override.
        var touched = SidebarLayoutReducer.Apply(other.Layout, new SetSectionCollapsed("sec_p", true), pins);
        Assert.True(touched.Changed);
        Assert.Single(touched.Layout.Find("sec_p")!.ItemList);
        Assert.Equal("spotify:playlist:live", touched.Layout.Find("sec_p")!.ItemList[0].Key);
        Assert.Equal("Keep me", touched.Layout.Find("sec_p")!.ItemList[0].LabelOverride);

        // Without a pin set, nothing is ever pruned.
        var noPins = SidebarLayoutReducer.Apply(l, new SetSectionCollapsed("sec_p", true));
        Assert.Equal(2, noPins.Layout.Find("sec_p")!.ItemList.Count);
    }

    [Fact]
    public void An_unknown_section_kind_survives_every_command_that_touches_its_neighbours()
    {
        var future = new SidebarSectionSpec("sec_future", (SidebarSectionKind)200, Title: "From the future");
        var l = Doc(Sec("sec_a", SidebarSectionKind.Pinned), future);

        var r = Apply(l, new AddSection(SidebarSectionKind.Header, 0));
        Assert.True(r.Changed);
        var kept = r.Layout.Find("sec_future")!;
        Assert.Equal((SidebarSectionKind)200, kept.Kind);
        Assert.Equal("From the future", kept.Title);
        Assert.True(kept.IsUnknownKind);

        // It can still be moved and removed like any other section (the outline must not trap the user).
        Assert.True(Apply(l, new MoveSection("sec_future", null, 0)).Changed);
        Assert.True(Apply(l, new RemoveSection("sec_future")).Changed);
        // …but it accepts no items and no display edits.
        AssertRejected(l, new AddItem("sec_future", Route("itm_1", "home"), 0),
            SidebarRejectReason.KindDoesNotAcceptItems);
        AssertRejected(l, new SetDisplayOption("sec_future", SidebarDisplayField.Density, 0),
            SidebarRejectReason.NoChange);
    }

    // ── undo / redo (the pure 50-entry pre-image ring) ──────────────────────────────────────────────────────────

    [Fact]
    public void Undo_restores_the_pre_image_and_redo_reapplies_it()
    {
        var l0 = Doc(Sec("sec_p", SidebarSectionKind.Pinned));
        var undo = new SidebarUndo();
        Assert.False(undo.CanUndo);
        Assert.Null(undo.UndoLabelLocKey);

        var cmd = new RenameSection("sec_p", "Mine");
        var r = SidebarLayoutReducer.Apply(l0, cmd);
        undo.Push(l0, cmd);
        var l1 = r.Layout;

        Assert.True(undo.CanUndo);
        Assert.Equal(SidebarUndoLabels.RenameSection, undo.UndoLabelLocKey);

        Assert.True(undo.TryUndo(l1, out var back, out var label));
        Assert.Equal(SidebarUndoLabels.RenameSection, label);
        Assert.True(SidebarLayoutCompare.Equal(l0, back));
        Assert.True(undo.CanRedo);
        Assert.Equal(SidebarUndoLabels.RenameSection, undo.RedoLabelLocKey);

        Assert.True(undo.TryRedo(back, out var again, out _));
        Assert.True(SidebarLayoutCompare.Equal(l1, again));
        Assert.False(undo.CanRedo);
    }

    [Fact]
    public void A_rejected_command_pushes_nothing_onto_the_undo_ring()
    {
        var l = Doc(Sec("sec_p", SidebarSectionKind.Pinned));
        var undo = new SidebarUndo();

        var r = SidebarLayoutReducer.Apply(l, new SetSectionHidden("sec_p", false));
        Assert.False(r.Changed);
        if (r.Changed) undo.Push(l, SidebarUndoLabels.HideSection);      // the Dispatch contract, written out

        Assert.False(undo.CanUndo);
        Assert.Equal(0, undo.UndoDepth);
        Assert.False(undo.TryUndo(l, out var same, out _));
        Assert.Same(l, same);
    }

    [Fact]
    public void A_new_command_after_an_undo_clears_the_redo_stack()
    {
        var l0 = Doc(Sec("sec_p", SidebarSectionKind.Pinned));
        var undo = new SidebarUndo();

        var l1 = Step(ref l0, undo, new RenameSection("sec_p", "A"));
        Assert.True(undo.TryUndo(l1, out var back, out _));
        Assert.True(undo.CanRedo);

        var cur = back;
        Step(ref cur, undo, new RenameSection("sec_p", "B"));
        Assert.False(undo.CanRedo);
        Assert.Equal(0, undo.RedoDepth);
    }

    [Fact]
    public void The_fifty_step_cap_evicts_the_oldest_entry_and_undo_stops_cleanly()
    {
        var l = Doc(Sec("sec_e", SidebarSectionKind.EntityList, query: SidebarEntityQuery.Default));
        var undo = new SidebarUndo();

        for (int i = 1; i <= 60; i++)
        {
            var cmd = new SetDisplayOption("sec_e", SidebarDisplayField.MaxItems, i);
            var r = SidebarLayoutReducer.Apply(l, cmd);
            Assert.True(r.Changed);
            undo.Push(l, cmd);
            l = r.Layout;
        }
        Assert.Equal(60, l.Sections[0].Opts.MaxItems);
        Assert.Equal(SidebarUndo.Capacity, undo.UndoDepth);

        for (int i = 0; i < SidebarUndo.Capacity; i++)
        {
            Assert.True(undo.TryUndo(l, out var back, out _));
            l = back;
        }

        // 50 undos from 60 land on the state AFTER command 10 — the oldest ten were evicted silently.
        Assert.Equal(10, l.Sections[0].Opts.MaxItems);
        Assert.False(undo.CanUndo);
        Assert.False(undo.TryUndo(l, out var unchanged, out _));
        Assert.Same(l, unchanged);
        Assert.Equal(10, unchanged.Sections[0].Opts.MaxItems);
    }

    [Fact]
    public void Undoing_across_apply_template_and_reset_is_one_step_each()
    {
        var l = Curated();
        var undo = new SidebarUndo();

        var afterTemplate = Step(ref l, undo, new ApplyTemplate(SidebarTemplates.Minimal));
        var cur = afterTemplate;
        var afterReset = Step(ref cur, undo, new ResetLayout());
        Assert.Equal(2, undo.UndoDepth);

        Assert.True(undo.TryUndo(afterReset, out var u1, out var lbl1));
        Assert.Equal(SidebarUndoLabels.Reset, lbl1);
        Assert.True(SidebarLayoutCompare.Equal(afterTemplate, u1));

        Assert.True(undo.TryUndo(u1, out var u2, out var lbl2));
        Assert.Equal(SidebarUndoLabels.ApplyTemplate, lbl2);
        Assert.Equal(SidebarTemplates.Curated, u2.TemplateId);
        Assert.Equal(7, u2.Sections.Count);
        Assert.False(undo.CanUndo);
    }

    [Fact]
    public void Clearing_the_undo_ring_drops_both_stacks()
    {
        var l = Doc(Sec("sec_p", SidebarSectionKind.Pinned));
        var undo = new SidebarUndo();
        var l1 = Step(ref l, undo, new RenameSection("sec_p", "A"));
        Assert.True(undo.TryUndo(l1, out var back, out _));
        Assert.True(undo.CanRedo);

        undo.Clear();
        Assert.False(undo.CanUndo);
        Assert.False(undo.CanRedo);
        Assert.False(undo.TryRedo(back, out _, out _));
    }

    [Fact]
    public void Every_command_type_carries_a_non_empty_undo_label_key()
    {
        var labels = new HashSet<string>(StringComparer.Ordinal);
        foreach (var command in EveryCommand())
        {
            Assert.False(string.IsNullOrEmpty(command.LabelLocKey), command.GetType().Name);
            Assert.StartsWith("sidebar.customizer.undo.", command.LabelLocKey, StringComparison.Ordinal);
            labels.Add(command.LabelLocKey);
        }
        // 21 command records (16 + LAYOUT V2's SetExtensionConfig/SetItemAction + the top-bar band's
        // AddTopBarItem/MoveTopBarItem/RemoveTopBarItem), and the two toggles carry a directional label each ->
        // 23 keys.
        Assert.Equal(23, labels.Count);
    }

    [Fact]
    public void Toggle_commands_carry_directional_undo_labels()
    {
        Assert.Equal(SidebarUndoLabels.HideSection, new SetSectionHidden("s", true).LabelLocKey);
        Assert.Equal(SidebarUndoLabels.ShowSection, new SetSectionHidden("s", false).LabelLocKey);
        Assert.Equal(SidebarUndoLabels.CollapseSection, new SetSectionCollapsed("s", true).LabelLocKey);
        Assert.Equal(SidebarUndoLabels.ExpandSection, new SetSectionCollapsed("s", false).LabelLocKey);
    }

    // ── theory data ──────────────────────────────────────────────────────────────────────────────────────────────

    public static TheoryData<SidebarDisplayField, int, int> ClampCases() => new()
    {
        { SidebarDisplayField.Density, 99, 2 },
        { SidebarDisplayField.Density, -5, 0 },
        { SidebarDisplayField.Presentation, 99, 1 },
        { SidebarDisplayField.Presentation, -5, 0 },
        { SidebarDisplayField.Artwork, 0, 0 },
        { SidebarDisplayField.Artwork, 7, 1 },
        { SidebarDisplayField.Subtitles, 0, 0 },
        { SidebarDisplayField.Subtitles, 7, 1 },
        { SidebarDisplayField.CountBadges, 0, 0 },
        { SidebarDisplayField.CountBadges, 7, 1 },
        { SidebarDisplayField.CollapsedByDefault, 0, 0 },
        { SidebarDisplayField.CollapsedByDefault, 7, 1 },
        { SidebarDisplayField.ShowInRail, 0, 0 },
        { SidebarDisplayField.ShowInRail, 7, 1 },
        { SidebarDisplayField.MaxItems, 9999, SidebarLayoutReducer.MaxItemsPerSection },
        { SidebarDisplayField.MaxItems, -3, 0 },
        { SidebarDisplayField.MaxItems, 12, 12 },
        { SidebarDisplayField.GridColumns, 99, 4 },
        { SidebarDisplayField.GridColumns, 0, 2 },
        { SidebarDisplayField.GridColumns, 3, 3 },
        { SidebarDisplayField.InlineControls, 1, 1 },
        { SidebarDisplayField.InlineControls, 0, 0 },
        { SidebarDisplayField.PlayButton, 0, 0 },
        { SidebarDisplayField.PlayButton, 1, 1 },
        { SidebarDisplayField.RecentsSource, 99, 1 },
        { SidebarDisplayField.RecentsSource, -5, 0 },
    };

    public static TheoryData<SidebarDisplayField> EveryField()
    {
        var d = new TheoryData<SidebarDisplayField>();
        foreach (var f in Enum.GetValues<SidebarDisplayField>()) d.Add(f);
        return d;
    }

    static SidebarCommand[] EveryCommand() =>
    [
        new AddSection(SidebarSectionKind.Pinned, 0),
        new RemoveSection("s"),
        new DuplicateSection("s"),
        new RenameSection("s", "t"),
        new SetSectionHidden("s", true),
        new SetSectionHidden("s", false),
        new SetSectionCollapsed("s", true),
        new SetSectionCollapsed("s", false),
        new MoveSection("s", null, 0),
        new AddItem("s", new SidebarItemSpec("i", SidebarItemTarget.Route, "home"), 0),
        new MoveItem("s", 0, "s", 1),
        new RemoveItem("s", "i"),
        new SetItemLabel("s", "i", "l"),
        new SetItemIcon("s", "i", "Heart"),
        new SetDisplayOption("s", SidebarDisplayField.Density, 0),
        new SetQuery("s", SidebarEntityQuery.Default),
        new SetExtensionConfig("s", SidebarJson.EmptyObject),
        new SetItemAction("s", "i", SidebarActionBinding.Simple("wavee", "play")),
        new AddTopBarItem(new SidebarItemSpec("i", SidebarItemTarget.Route, "home"), 0),
        new MoveTopBarItem(0, 1),
        new RemoveTopBarItem("i"),
        new ApplyTemplate(SidebarTemplates.Curated),
        new ResetLayout(),
    ];
}

// ═════════════════════════════════════════════════════════════════════════════════════════════════════════════════
// ── REGION 2 — THE FIVE SEED TEMPLATES ──────────────────────────────────────────────────────────────────────────────
// ═════════════════════════════════════════════════════════════════════════════════════════════════════════════════
//
// A template is the ONLY thing that decides what a fresh Curated sidebar looks like, so its composition is pinned
// row-for-row here rather than eyeballed in a screenshot — plus the per-kind defaults `SidebarSectionKinds` and the
// templates both lean on (the palette shares the same defaults, so drift there would surface as a template fact
// failing here first).
public sealed class SidebarTemplateTests
{
    // ── helpers ──────────────────────────────────────────────────────────────────────────────────────────────────

    static SidebarSectionKind[] Kinds(SidebarCustomLayout l)
    {
        var k = new SidebarSectionKind[l.Sections.Count];
        for (int i = 0; i < k.Length; i++) k[i] = l.Sections[i].Kind;
        return k;
    }

    static string[] Keys(SidebarSectionSpec s)
    {
        var k = new string[s.ItemList.Count];
        for (int i = 0; i < k.Length; i++) k[i] = s.ItemList[i].Key;
        return k;
    }

    static string?[] IconNames(SidebarSectionSpec s)
    {
        var k = new string?[s.ItemList.Count];
        for (int i = 0; i < k.Length; i++) k[i] = s.ItemList[i].IconOverride;
        return k;
    }

    static SidebarSectionSpec First(SidebarCustomLayout l, SidebarSectionKind kind)
    {
        for (int i = 0; i < l.Sections.Count; i++) if (l.Sections[i].Kind == kind) return l.Sections[i];
        throw new InvalidOperationException("no " + kind + " section in the template");
    }

    static int CountOf(SidebarCustomLayout l, SidebarSectionKind kind)
    {
        int n = 0;
        for (int i = 0; i < l.Sections.Count; i++) if (l.Sections[i].Kind == kind) n++;
        return n;
    }

    // ── Wavee Curated ────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Curated_has_the_exact_section_composition()
    {
        var l = SidebarTemplates.Build(SidebarTemplates.Curated);
        Assert.Equal(SidebarTemplates.Curated, l.TemplateId);

        Assert.Equal(new[]
        {
            SidebarSectionKind.Pinned,
            SidebarSectionKind.Divider,
            SidebarSectionKind.JumpBackIn,
            SidebarSectionKind.Divider,
            SidebarSectionKind.CollectionShortcuts,
            SidebarSectionKind.Divider,
            SidebarSectionKind.PlaylistTree,
        }, Kinds(l));

        Assert.Equal("sidebar.pinned", l.Sections[0].TitleLocKey);
        Assert.True(l.Sections[0].Opts.ShowInRail);
        Assert.False(l.Sections[0].Opts.CountBadges);
        Assert.False(l.Sections[0].Opts.CollapsedByDefault);

        // Curated's Jump Back In ships as RECENTLY PLAYED, top 4.
        var jump = l.Sections[2];
        Assert.Equal(SidebarRecentsSource.Played, jump.Opts.Recents);
        Assert.Equal(4, jump.Opts.MaxItems);
        Assert.Equal(SidebarPresentation.Grid, jump.Opts.Presentation);
        Assert.Equal(2, jump.Opts.GridColumns);
        Assert.True(jump.Opts.Artwork);
        Assert.False(jump.Opts.ShowInRail);
        Assert.False(jump.Opts.Subtitles);
        Assert.Equal("sidebar.section.recentlyPlayed", jump.TitleLocKey);

        // Liked Songs FIRST — the ordering divergence from today's sidebar.
        var shortcuts = l.Sections[4];
        Assert.Equal("sidebar.yourLibrary", shortcuts.TitleLocKey);
        // Cozy + Subtitles:false = the 40-DIP glyph row, with the SAME 32-DIP art column every content row shares —
        // pixel parity with Classic's locked document, reached without a Comfortable density override.
        Assert.Equal(SidebarDensity.Cozy, shortcuts.Opts.Density);
        Assert.True(shortcuts.Opts.CountBadges);
        Assert.True(shortcuts.Opts.ShowInRail);
        Assert.Equal(new[] { "liked", "albums", "artists", "podcasts", "local" }, Keys(shortcuts));
        Assert.Equal(new string?[] { "Heart", "Album", "Contact", "RadioTower", "Folder" }, IconNames(shortcuts));
        foreach (var it in shortcuts.ItemList) Assert.Equal(SidebarItemTarget.Route, it.Target);

        var tree = l.Sections[^1];
        Assert.Equal("sidebar.playlists", tree.TitleLocKey);
        Assert.True(tree.Opts.Subtitles);
        Assert.True(tree.Opts.ShowInRail);
    }

    // ── Classic-inspired ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ClassicInspired_matches_todays_information_architecture()
    {
        var l = SidebarTemplates.Build(SidebarTemplates.ClassicInspired);
        Assert.Equal(SidebarTemplates.ClassicInspired, l.TemplateId);

        Assert.Equal(new[]
        {
            SidebarSectionKind.Pinned,
            SidebarSectionKind.CollectionShortcuts,
            SidebarSectionKind.Divider,
            SidebarSectionKind.PlaylistTree,
            SidebarSectionKind.Divider,
            SidebarSectionKind.StaticLinks,
        }, Kinds(l));

        // Classic's rail carries no pin tiles.
        Assert.False(First(l, SidebarSectionKind.Pinned).Opts.ShowInRail);

        Assert.Equal(new[] { "albums", "artists", "liked", "podcasts", "local" },
            Keys(First(l, SidebarSectionKind.CollectionShortcuts)));

        var links = First(l, SidebarSectionKind.StaticLinks);
        Assert.Single(links.ItemList);
        Assert.Equal("api-console", links.ItemList[0].Key);
        Assert.Equal("Code", links.ItemList[0].IconOverride);
        Assert.Equal(SidebarItemTarget.Route, links.ItemList[0].Target);

        Assert.True(First(l, SidebarSectionKind.PlaylistTree).Opts.Subtitles);
    }

    // ── Library-inspired ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void V3Inspired_has_one_entity_list_over_all_kinds_sorted_by_recents()
    {
        var l = SidebarTemplates.Build(SidebarTemplates.V3Inspired);
        Assert.Equal(SidebarTemplates.V3Inspired, l.TemplateId);
        Assert.Equal(1, CountOf(l, SidebarSectionKind.EntityList));

        var list = First(l, SidebarSectionKind.EntityList);
        Assert.Equal("sidebar.yourLibrary", list.TitleLocKey);
        var q = list.Query!;
        Assert.Equal(SidebarEntityKinds.All, q.Kinds);
        Assert.Equal(SidebarSortMode.Recents, q.Sort);
        Assert.True(q.Descending);
        Assert.Equal(SidebarPlaylistQualifier.Any, q.Qualifier);

        // The V3-inspired template's unified section ships InlineControls.
        Assert.True(list.Opts.InlineControls);
        Assert.True(list.Opts.Subtitles);
        Assert.True(list.Opts.ShowInRail);

        Assert.Equal(new[] { "home", "search" }, Keys(First(l, SidebarSectionKind.StaticLinks)));
        Assert.True(First(l, SidebarSectionKind.Pinned).Opts.ShowInRail);
    }

    // ── Minimal / Blank ──────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Minimal_has_three_shortcuts_and_an_artless_playlist_tree()
    {
        var l = SidebarTemplates.Build(SidebarTemplates.Minimal);
        Assert.Equal(SidebarTemplates.Minimal, l.TemplateId);
        Assert.Equal(new[] { SidebarSectionKind.CollectionShortcuts, SidebarSectionKind.Divider,
            SidebarSectionKind.PlaylistTree }, Kinds(l));

        var sc = First(l, SidebarSectionKind.CollectionShortcuts);
        Assert.Equal(new[] { "liked", "albums", "artists" }, Keys(sc));
        Assert.False(sc.Opts.CountBadges);
        Assert.Equal(SidebarDensity.Compact, sc.Opts.Density);

        var tree = First(l, SidebarSectionKind.PlaylistTree);
        Assert.False(tree.Opts.Artwork);
        Assert.False(tree.Opts.Subtitles);
        Assert.Equal(SidebarDensity.Compact, tree.Opts.Density);
    }

    [Fact]
    public void Blank_is_empty()
    {
        var l = SidebarTemplates.Build(SidebarTemplates.Blank);
        Assert.Equal(SidebarTemplates.Blank, l.TemplateId);
        Assert.Empty(l.Sections);
        Assert.Equal(0, l.SectionCount);
    }

    // ── cross-template invariants ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void All_lists_every_template_once_in_palette_order()
    {
        Assert.Equal(new[] { SidebarTemplates.Curated, SidebarTemplates.ClassicInspired,
            SidebarTemplates.V3Inspired, SidebarTemplates.Minimal, SidebarTemplates.Blank }, SidebarTemplates.All);
        Assert.Equal(SidebarTemplates.All.Length, new HashSet<string>(SidebarTemplates.All, StringComparer.Ordinal).Count);
    }

    [Theory]
    [MemberData(nameof(TemplateIds))]
    public void Every_template_mints_unique_ids(string templateId)
    {
        var ids = SidebarLayoutCompare.AllIds(SidebarTemplates.Build(templateId));
        Assert.Equal(ids.Count, new HashSet<string>(ids, StringComparer.Ordinal).Count);
        foreach (var id in ids)
            Assert.True(id.StartsWith(SidebarIds.SectionPrefix, StringComparison.Ordinal) ||
                        id.StartsWith(SidebarIds.ItemPrefix, StringComparison.Ordinal), id);
    }

    [Theory]
    [MemberData(nameof(TemplateIds))]
    public void Building_a_template_twice_yields_different_ids_but_equal_structure(string templateId)
    {
        var a = SidebarTemplates.Build(templateId);
        var b = SidebarTemplates.Build(templateId);

        Assert.True(SidebarLayoutCompare.EqualIgnoringIds(a, b),
            SidebarLayoutCompare.FirstDifference(a, b, ignoreIds: true));

        var idsA = SidebarLayoutCompare.AllIds(a);
        if (idsA.Count > 0)
        {
            Assert.NotEqual(idsA, SidebarLayoutCompare.AllIds(b));
            Assert.False(SidebarLayoutCompare.Equal(a, b));
        }
    }

    [Theory]
    [MemberData(nameof(TemplateIds))]
    public void Every_template_is_structurally_equal_to_itself(string templateId)
    {
        var a = SidebarTemplates.Build(templateId);
        Assert.True(SidebarLayoutCompare.Equal(a, a));
        Assert.Null(SidebarLayoutCompare.FirstDifference(a, a));
    }

    [Theory]
    [MemberData(nameof(TemplateIds))]
    public void Every_template_section_carries_a_localized_title_or_is_chrome(string templateId)
    {
        foreach (var s in SidebarTemplates.Build(templateId).Sections)
        {
            // Templates never author a LITERAL title — a template-seeded name must follow the UI culture.
            Assert.Null(s.Title);
            if (s.Kind is SidebarSectionKind.Divider or SidebarSectionKind.StaticLinks) continue;
            Assert.False(string.IsNullOrEmpty(s.TitleLocKey));
            Assert.StartsWith("sidebar.", s.TitleLocKey, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void An_unknown_template_id_builds_wavee_curated()
    {
        foreach (var bad in new string?[] { null, "", "nope", "CURATED" })
        {
            var l = SidebarTemplates.Build(bad);
            Assert.Equal(SidebarTemplates.Curated, l.TemplateId);
            Assert.True(SidebarLayoutCompare.EqualIgnoringIds(l, SidebarTemplates.Build(SidebarTemplates.Curated)));
            Assert.False(SidebarTemplates.IsKnown(bad));
        }
        foreach (var good in SidebarTemplates.All) Assert.True(SidebarTemplates.IsKnown(good));
    }

    [Theory]
    [MemberData(nameof(TemplateIds))]
    public void Name_and_description_keys_are_namespaced_and_distinct(string templateId)
    {
        var name = SidebarTemplates.NameLocKey(templateId);
        var sub = SidebarTemplates.DescriptionLocKey(templateId);
        Assert.Equal("sidebar.template." + templateId, name);
        Assert.Equal(name + "Sub", sub);
        Assert.NotEqual(name, sub);
    }

    [Fact]
    public void An_unknown_template_id_falls_back_to_the_curated_loc_keys()
    {
        Assert.Equal("sidebar.template.curated", SidebarTemplates.NameLocKey("nope"));
        Assert.Equal("sidebar.template.curatedSub", SidebarTemplates.DescriptionLocKey(null));
    }

    // ── kind defaults the templates and the palette both lean on ────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(KnownKinds))]
    public void Every_known_kind_has_a_default_title_loc_key_except_divider(SidebarSectionKind kind)
    {
        var key = SidebarSectionKinds.DefaultTitleLocKey(kind);
        if (kind == SidebarSectionKind.Divider) { Assert.Null(key); return; }
        Assert.False(string.IsNullOrEmpty(key));
        Assert.StartsWith("sidebar.", key, StringComparison.Ordinal);

        var palette = SidebarSectionKinds.PaletteNameLocKey(kind);
        Assert.False(string.IsNullOrEmpty(palette));
        Assert.Equal(palette + "Sub", SidebarSectionKinds.PaletteDescriptionLocKey(kind));
    }

    [Fact]
    public void JumpBackIns_default_title_follows_its_recents_source()
    {
        Assert.Equal("sidebar.section.jumpBackIn",
            SidebarSectionKinds.DefaultTitleLocKey(SidebarSectionKind.JumpBackIn, SidebarRecentsSource.Visited));
        Assert.Equal("sidebar.section.recentlyPlayed",
            SidebarSectionKinds.DefaultTitleLocKey(SidebarSectionKind.JumpBackIn, SidebarRecentsSource.Played));
    }

    [Fact]
    public void Feed_kinds_ship_their_specd_top_n()
    {
        Assert.Equal(4, SidebarSectionKinds.DefaultDisplay(SidebarSectionKind.NewReleases).MaxItems);
        Assert.Equal(3, SidebarSectionKinds.DefaultDisplay(SidebarSectionKind.Concerts).MaxItems);
        Assert.Equal(SidebarDisplayOptions.Shortcuts,
            SidebarSectionKinds.DefaultDisplay(SidebarSectionKind.CollectionShortcuts));
        // DEFECT 8 — StaticLinks has its OWN preset (`Links`), which is `Shortcuts with { CountBadges = false }`.
        // The two kinds shared one preset, so every Links section was seeded a count flag `AllowsDisplayField`
        // forbids: the panel never offered it and `SetDisplayOption` rejected it, yet the row renderer honoured it.
        Assert.Equal(SidebarDisplayOptions.Links,
            SidebarSectionKinds.DefaultDisplay(SidebarSectionKind.StaticLinks));
        Assert.NotEqual(SidebarDisplayOptions.Shortcuts,
            SidebarSectionKinds.DefaultDisplay(SidebarSectionKind.StaticLinks));
    }

    [Fact]
    public void The_icon_whitelist_is_ordered_stable_and_closed()
    {
        Assert.Equal(30, SidebarIconNames.Allowed.Length);
        Assert.Equal("MusicNote", SidebarIconNames.Allowed[0]);
        Assert.Equal("Download", SidebarIconNames.Allowed[^1]);
        Assert.Equal(SidebarIconNames.Allowed.Length,
            new HashSet<string>(SidebarIconNames.Allowed, StringComparer.Ordinal).Count);

        Assert.True(SidebarIconNames.IsAllowed("Heart"));
        Assert.False(SidebarIconNames.IsAllowed("heart"));      // ordinal, on purpose
        Assert.False(SidebarIconNames.IsAllowed("Nope"));
        Assert.False(SidebarIconNames.IsAllowed(null));
        Assert.False(SidebarIconNames.IsAllowed(""));

        // Every glyph a template authors must be whitelisted, or the reducer would reject the user's own default
        // layout.
        foreach (var t in SidebarTemplates.All)
            foreach (var s in SidebarTemplates.Build(t).Sections)
                foreach (var it in s.ItemList)
                    if (it.IconOverride is not null) Assert.True(SidebarIconNames.IsAllowed(it.IconOverride),
                        it.IconOverride);
    }

    public static TheoryData<string> TemplateIds()
    {
        var d = new TheoryData<string>();
        foreach (var t in SidebarTemplates.All) d.Add(t);
        return d;
    }

    public static TheoryData<SidebarSectionKind> KnownKinds()
    {
        var d = new TheoryData<SidebarSectionKind>();
        for (byte b = 0; b <= SidebarSectionKinds.MaxKnown; b++) d.Add((SidebarSectionKind)b);
        return d;
    }
}

// ═════════════════════════════════════════════════════════════════════════════════════════════════════════════════
// ── REGION 3 — THE SHELL TOP-BAR BAND AS A RENDERED SECTION ─────────────────────────────────────────────────────────
// ═════════════════════════════════════════════════════════════════════════════════════════════════════════════════
//
// The shortcut band renders as an ORDINARY SECTION under the sentinel id, and sentinel-id addressing routes an
// edit to the band's own command family in one place instead of at every call site. What is NOT under test here —
// deliberately, because it is pinned in the reducer region above — is `SidebarCustomLayout.TopBar` itself: still
// the wire member, still mutated only through AddTopBarItem/MoveTopBarItem/RemoveTopBarItem, still capped and
// carried by the same undo ring.
//
// A handful of 0.2.9 facts in this file exercised OTHER consumers of `SidebarShortcutsSection.Prepend`, both public
// in src/apps/Wavee/Shell/Sidebar.Modes.cs and both ported below: `SidebarBuiltInDocuments.Classic`, which prepends
// the band as its own first section, and `LibraryV3Document.Build`, which never does (V3's navigation is fixed
// chrome above "Your Library", never a section of the scrolling document). One 0.2.9 fact stays dropped for a
// reason that has nothing to do with accessibility: the one asserting V3 no longer emits its own `v3.liked` row is
// about V3's OWN CONTENT, not the band, and a separate SidebarDesignTests.cs owns that pin — restating it here would
// duplicate it. See the port report for the exact list.
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

    // ── the synthesized section ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>The shape, field by field. It is a <c>StaticLinks</c> section because that is exactly what the band
    /// is — a hand-authored list of routes / entities / tracks / bound actions — which is what lets the planner,
    /// the row slot, the rail, the reorder band and the selection indicator serve it with no new code.</summary>
    [Fact]
    public void From_produces_a_static_links_section_under_the_sentinel_id()
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

    /// <summary>The DISPLAY preset is the StaticLinks one (<c>Links</c>), never the CollectionShortcuts one:
    /// defect 8's <c>CountBadges = true</c> is a flag <c>AllowsDisplayField(StaticLinks, CountBadges)</c> forbids,
    /// so a band carrying it would have a default the user can neither see nor change. <c>ShowInRail</c> is a
    /// REQUIREMENT (the 56-DIP rail form of the band), not an inherited accident, so it is asserted rather than
    /// assumed.</summary>
    [Fact]
    public void From_takes_the_links_preset_and_always_shows_in_the_rail()
    {
        var section = SidebarShortcutsSection.From(SidebarCustomLayout.DefaultTopBar);

        Assert.Equal(SidebarDisplayOptions.Links with { ShowInRail = true }, section.Opts);
        Assert.True(section.Opts.ShowInRail);
        Assert.False(section.Opts.CountBadges);
        Assert.False(SidebarSectionKinds.AllowsDisplayField(SidebarSectionKind.StaticLinks,
                                                            SidebarDisplayField.CountBadges));
    }

    /// <summary>NOT collapsible, and not hidden. The sentinel is not in <c>Sections</c>, so there is no
    /// section-scoped command that could ever write those bits back — a <c>true</c> here would be a state the user
    /// could not undo.</summary>
    [Fact]
    public void From_is_neither_hidden_nor_collapsed()
    {
        var section = SidebarShortcutsSection.From(SidebarCustomLayout.DefaultTopBar);
        Assert.False(section.Hidden);
        Assert.False(section.Collapsed);

        // …and every section-scoped command addressed at the sentinel is an UnknownSection rejection, which is
        // exactly why the two bits above must stay false: nothing can flip them back.
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

        // The materialised document does carry the section, which is the point: it renders, it just is not
        // addressable.
        Assert.NotNull(doc.Find(SidebarIds.TopBarSection));
    }

    /// <summary>An EMPTY band means the user emptied it on purpose (Home is genuinely removable) and contributes no
    /// section at all — no header, no rows, no rail tiles. Null is accepted so a probe/headless caller with no
    /// preference service is legal.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Renders_is_false_when_there_is_nothing_to_show(bool useNull)
    {
        IReadOnlyList<SidebarItemSpec>? band = useNull ? null : Array.Empty<SidebarItemSpec>();
        Assert.False(SidebarShortcutsSection.Renders(band));
        Assert.True(SidebarShortcutsSection.Renders(SidebarCustomLayout.DefaultTopBar));
    }

    // ── Prepend: the render-path projection ──────────────────────────────────────────────────────────────────────

    /// <summary>An empty band costs the caller NOTHING — not a copy, not a new list — so the pane's first section
    /// header (which hosts the quick layout menu) falls back to the document's own first section by reference
    /// identity.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Prepend_of_an_empty_band_returns_the_input_unchanged(bool useNull)
    {
        var doc = Doc(Sec("sec_a", SidebarSectionKind.Pinned), Sec("sec_b", SidebarSectionKind.PlaylistTree));
        IReadOnlyList<SidebarItemSpec>? band = useNull ? null : Array.Empty<SidebarItemSpec>();

        Assert.Same(doc, SidebarShortcutsSection.Prepend(doc, band));
    }

    /// <summary>A non-empty band is a HEAD INSERTION: the shortcuts section is index 0 and every original section
    /// keeps its order and its identity behind it. The input document is never touched.</summary>
    [Fact]
    public void Prepend_puts_the_band_first_and_leaves_the_original_sections_intact()
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

    // ── ContainsRoute: the one owner of "is this destination already a shortcut?" ───────────────────────────────

    [Fact]
    public void ContainsRoute_only_counts_visible_route_items()
    {
        Assert.True(SidebarShortcutsSection.ContainsRoute(new[] { Route("liked") }, "liked"));
        Assert.False(SidebarShortcutsSection.ContainsRoute(new[] { Route("liked") }, "home"));
        Assert.False(SidebarShortcutsSection.ContainsRoute(new[] { Route("liked") }, "Liked"));   // ordinal, on purpose

        // A HIDDEN shortcut does not render, so it cannot be the reason another surface drops its own row.
        Assert.False(SidebarShortcutsSection.ContainsRoute(new[] { Route("liked", hidden: true) }, "liked"));

        // An ENTITY item whose uri happens to spell the same word as a route key is a DIFFERENT row — different
        // art, different menu — so it deliberately does not count, regardless of the uri.
        Assert.False(SidebarShortcutsSection.ContainsRoute(
            new[] { Entity("spotify:collection:tracks") }, "liked"));

        Assert.False(SidebarShortcutsSection.ContainsRoute(null, "liked"));
        Assert.False(SidebarShortcutsSection.ContainsRoute(new[] { Route("liked") }, ""));
    }

    // ── document: Classic ────────────────────────────────────────────────────────────────────────────────────────
    //
    // `SidebarBuiltInDocuments.Classic` (src/apps/Wavee/Shell/Sidebar.Modes.cs) is a real, public consumer of
    // `Prepend`: Classic's locked document gains the band as its own first section and is otherwise byte-for-byte
    // what it was. It is never persisted, so the sentinel id can never reach the wire. Only the BAND's effect on
    // this document is asserted here — Classic's own section composition (pinned/library/playlists/devtools) is
    // covered elsewhere (SidebarDesignTests), to avoid duplicating that pin.

    [Fact]
    public void Classic_prepends_the_band_and_is_otherwise_identical()
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
    public void Classic_with_no_band_is_exactly_the_document_it_always_was()
    {
        var plain = SidebarBuiltInDocuments.Classic(true, true, true);
        var empty = SidebarBuiltInDocuments.Classic(true, true, true, Array.Empty<SidebarItemSpec>());

        Assert.Equal(SidebarBuiltInDocuments.PinnedId, empty.Sections[0].Id);
        Assert.Equal(plain.Sections.Count, empty.Sections.Count);
    }

    // ── document: Library V3 ─────────────────────────────────────────────────────────────────────────────────────
    //
    // `LibraryV3Document.Build` (same file) is the OTHER real consumer of `Prepend` — or rather, of choosing not to
    // call it. V3's own navigation is fixed chrome above "Your Library", never a section of the scrolling document,
    // so it never materializes the band as a section at all: only Classic/Curated still `Prepend`. Only that
    // non-consumption is asserted here; V3's own section composition is SidebarDesignTests' pin, not this file's.

    /// <summary>W3 (library-v3-chrome plan): V3's navigation is FIXED CHROME above "Your Library", never a section
    /// of the scrolling document — so it can never be filtered, searched or drilled away. The document therefore
    /// leads with the pin band whether or not a shell band exists.</summary>
    [Fact]
    public void LibraryV3_never_carries_the_shortcut_band_as_a_section()
    {
        var state = new LibraryV3DocState(HasPins: true);
        var doc = LibraryV3Document.Build(in state, SidebarCustomLayout.DefaultTopBar);
        var bare = LibraryV3Document.Build(in state);

        Assert.DoesNotContain(doc.Sections, s => s.Id == SidebarIds.TopBarSection);
        Assert.Equal(LibraryV3Document.PinsId, doc.Sections[0].Id);
        Assert.Equal(LibraryV3Document.PinsId, bare.Sections[0].Id);
        Assert.Equal(bare.Sections.Count, doc.Sections.Count);
    }

    // ── sentinel-id dispatch routing ─────────────────────────────────────────────────────────────────────────────
    //
    // The section carries the sentinel, so an edit addressed at it must reach the BAND's command family. That
    // choice used to be spelled out at each call site; it is one decision in `SidebarItemCommands`, which is what
    // makes "a move inside the Shortcuts section emits MoveTopBarItem, not MoveItem" assertable at all.

    [Fact]
    public void ItemCommands_routes_the_sentinel_to_the_band_family()
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
    public void ItemCommands_routes_a_real_section_id_to_the_generic_family()
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

    /// <summary>The commands the routing produces are the ones the reducer actually executes — the assertion the
    /// whole helper exists for. A raw <c>MoveItem</c> at the sentinel would be a silent <c>UnknownSection</c>
    /// rejection: a drag that snaps back with no message.</summary>
    [Fact]
    public void ItemCommands_the_routed_band_move_is_the_one_the_reducer_accepts()
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
    public void ItemsIn_addresses_the_band_for_the_sentinel_and_the_section_otherwise()
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
    public void FindItem_looks_inside_whichever_list_the_section_id_addresses()
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
