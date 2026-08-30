using System;
using System.Collections.Generic;
using Wavee.Core.Sidebar;
using Xunit;

namespace Wavee.Tests;

// The companion page's PURE model (Phase 3): the searchable palette table + filter — now including the DESTINATIONS
// group — the display-option projection the generated property controls bind, and the opaque extension-config rewriter.
// All of it lives in Features/Sidebar/Curated/SidebarCustomizerLayout.cs, which is engine-free ON PURPOSE and
// source-included here, so these tests drive PRODUCTION code rather than a copy of it.
//
// PHASE 3 REMOVALS, recorded so nobody re-adds the tests either: the four-tier region ladder
// (`SidebarCustomizerTier`/`SidebarCustomizerLayout`), the header's command-fit table
// (`SidebarCustomizerCommandLayout`) and the outline flattening + its flat-index → MoveSection translation
// (`SidebarOutlineRow`/`SidebarOutlineRows`/`SidebarOutlineDrag`) all died WITH the surfaces they described — the page
// is ONE scrolling column at every width and the docked pane IS the canvas. The one section-drag translation that
// survives is `SidebarEditPlan.ToMoveSection`, which works in the PANE's band slots and is covered by
// SidebarEditPlanTests.
public class SidebarCustomizerLayoutTests
{
    // ── the palette table ─────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Palette_IdsAreUniqueAndEveryGroupIsPopulated()
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in SidebarPalette.All)
            Assert.True(ids.Add(entry.Id), "duplicate palette id: " + entry.Id);

        foreach (var group in SidebarPalette.Groups)
        {
            bool any = false;
            foreach (var entry in SidebarPalette.All)
                if (entry.Group == group) { any = true; break; }
            Assert.True(any, "empty palette group: " + group);
        }
    }

    /// <summary>Every row must be NAMEABLE — but by exactly one owner, and a destination's owner is not this table.
    ///
    /// <para>A SECTION row spells its own <c>NameLocKey</c>. A DESTINATION row deliberately leaves it EMPTY and carries
    /// a <c>RouteKey</c> instead, because "what this page is called" already has a single owner in
    /// <c>ShellNav.Dest(route).Title</c> — the same string the tab strip, the breadcrumb and a pinned row use. Minting a
    /// twelfth-and-thirteenth spelling here is exactly the drift the single-owner rule exists to catch. So the rule is
    /// EITHER a non-empty name key OR a non-empty route key, never neither.</para></summary>
    [Fact]
    public void Palette_EveryEntryAddsAKnownKindAndNamesItsLocKeys()
    {
        foreach (var entry in SidebarPalette.All)
        {
            Assert.True(SidebarSectionKinds.IsKnown(entry.Kind));
            Assert.False(string.IsNullOrEmpty(entry.DescriptionLocKey));
            Assert.False(string.IsNullOrEmpty(entry.IconName));

            bool named = !string.IsNullOrEmpty(entry.NameLocKey);
            bool routed = !string.IsNullOrEmpty(entry.RouteKey);
            Assert.True(named || routed, entry.Id + " can be labelled by neither a loc key nor a route");

            // …and never BOTH: two owners for one label is the drift, whichever one the renderer happens to pick.
            Assert.False(named && routed, entry.Id + " names its label twice");

            // The split is exactly the group split — a route key is a Destinations-only member.
            Assert.Equal(entry.Group == SidebarPaletteGroup.Destinations, routed);
        }
    }

    [Fact]
    public void Palette_ContributionEntriesNameARegisteredFirstPartySource()
    {
        int contributions = 0;
        foreach (var entry in SidebarPalette.All)
        {
            if (entry.Add != SidebarPaletteAdd.Contribution) continue;
            contributions++;
            Assert.Equal(SidebarSectionKind.Extension, entry.Kind);
            Assert.False(string.IsNullOrEmpty(entry.ContributionId));
            Assert.True(SidebarContributions.IsFirstParty(entry.ContributionId),
                "not a first-party contribution id: " + entry.ContributionId);
        }
        // Artist top tracks + Queue + Now Playing: the three built-in dynamic feeds that ship as Extension sections.
        Assert.Equal(3, contributions);
    }

    [Fact]
    public void Palette_TheGenericExtensionEntryDefersToTheContributionPicker()
    {
        SidebarPaletteEntry? generic = null;
        foreach (var entry in SidebarPalette.All)
            if (entry.Add == SidebarPaletteAdd.AnyContribution) generic = entry;

        Assert.NotNull(generic);
        Assert.Null(generic!.ContributionId);          // the user picks it; the table cannot know
        Assert.Equal(SidebarSectionKind.Extension, generic.Kind);
    }

    [Fact]
    public void Palette_LikedSongsIsOnePreseededShortcutGesture()
    {
        SidebarPaletteEntry? liked = null;
        foreach (var entry in SidebarPalette.All)
            if (entry.Add == SidebarPaletteAdd.LikedSongsShortcut) liked = entry;

        Assert.NotNull(liked);
        Assert.Equal("likedSongs", liked!.Id);
        Assert.Equal(SidebarPaletteGroup.Navigation, liked.Group);
        Assert.Equal(SidebarSectionKind.StaticLinks, liked.Kind);
        Assert.Equal("nav.likedSongs", liked.NameLocKey);
        Assert.Equal("Heart", liked.IconName);
    }

    [Fact]
    public void Palette_FilterIsTokenWiseAndCaseInsensitive()
    {
        var into = new List<SidebarPaletteEntry>();

        // The API console is DEVELOPER surface: the table still contains it (it is a static array — see the comment on
        // ExtraDestinationRoutes) and Filter is the one read path that drops it while developer mode is off, which is
        // what a test run always is. So "everything" is one row short of the table, and the missing row is that one.
        int offered = SidebarPalette.All.Length - 1;

        Assert.Equal(offered, SidebarPalette.Filter("", Label, Description, into));
        Assert.DoesNotContain(into, e => e.RouteKey == "api-console");
        Assert.Equal(offered, SidebarPalette.Filter("   ", Label, Description, into));

        // …and it is still offered to a developer — the gate is the switch, not a deletion.
        Assert.True(DeveloperMode.ShowsRoute("api-console", enabled: true));

        SidebarPalette.Filter("QUEUE", Label, Description, into);
        Assert.Single(into);
        Assert.Equal("queue", into[0].Id);

        // Two tokens, out of order, matching the label of one entry.
        SidebarPalette.Filter("tracks top", Label, Description, into);
        Assert.Single(into);
        Assert.Equal("artistTopTracks", into[0].Id);

        Assert.Equal(0, SidebarPalette.Filter("zzzznothing", Label, Description, into));

        // A token that only appears in the DESCRIPTION still matches.
        SidebarPalette.Filter("divider", Label, Description, into);
        Assert.Single(into);

        static string Label(SidebarPaletteEntry e) => e.Id;                  // stand-in for the localized name
        static string? Description(SidebarPaletteEntry e) => e.NameLocKey;   // …and its description
    }

    [Fact]
    public void Palette_MatchesEmptyQueryAlwaysWins()
    {
        Assert.True(SidebarPalette.Matches("", null, null));
        Assert.False(SidebarPalette.Matches("x", null, null));
        Assert.True(SidebarPalette.Matches(SidebarPalette.NormalizeQuery("  Pin  "), "Pinned", null));
    }

    // ── the Destinations group (Phase 3) ──────────────────────────────────────────────────────────────────────────────
    //
    // The fix for "search home, get Links": typing "home" used to return one row — *Links — Shortcuts to pages like Home
    // or Search* — whose click added an EMPTY StaticLinks section. Destinations answer the question with the page.

    static SidebarSectionSpec Section(string id, SidebarSectionKind kind,
        IReadOnlyList<SidebarSectionSpec>? children = null, bool hidden = false)
        => new(id, kind, Title: null, TitleLocKey: null, Hidden: hidden, Collapsed: false, Display: null,
               Items: null, Query: null, Children: children, Extension: null);

    /// <summary>The entry SET is derived, never re-typed: <c>SidebarPinId.PinnableRoutes</c> (the pin scheme's own list,
    /// so a route that becomes pinnable later shows up here for free) ∪ the three real pages that list omits — settings
    /// and the API console because the PIN policy refuses them as tooling surfaces, the concerts hub because the curated
    /// picker does not advertise it (it is pinnable when reached). Neither omission is a reason to hide a real page from
    /// a shortcut list: a shortcut and a pin are different offers.</summary>
    [Fact]
    public void Destinations_AreThePinnableRoutesPlusTheThreeUnpinnablePages()
    {
        var routes = new List<string>();
        foreach (var e in SidebarPalette.Destinations) routes.Add(e.RouteKey!);

        Assert.Equal(SidebarPinId.PinnableRoutes.Length + 3, routes.Count);

        // The pinnable routes come FIRST and in their own order — the union is spelled with that list as the source.
        for (int i = 0; i < SidebarPinId.PinnableRoutes.Length; i++)
            Assert.Equal(SidebarPinId.PinnableRoutes[i], routes[i]);

        Assert.Equal(new[] { "settings", "api-console", "concerts" },
                     routes.GetRange(SidebarPinId.PinnableRoutes.Length, 3).ToArray());

        // The extras are exactly the pages PinnableRoutes omits — otherwise this list would duplicate one row.
        foreach (var extra in new[] { "settings", "api-console", "concerts" })
            Assert.DoesNotContain(extra, SidebarPinId.PinnableRoutes);

        // …and they are omitted for two DIFFERENT reasons, which is why the set cannot be derived from one predicate:
        // the tooling surfaces are refused by the pin scheme outright, the concerts hub is pinnable but uncurated.
        Assert.Null(SidebarPinId.FromRoute("settings"));
        Assert.Null(SidebarPinId.FromRoute("api-console"));
        Assert.Equal("concerts", SidebarPinId.FromRoute("concerts"));
        Assert.Contains("concerts", SidebarPinId.AlsoPinnableRoutes);

        // "home" is in there, which is the whole point of the group.
        Assert.Contains("home", routes);
    }

    /// <summary>Every destination is ONE undoable <c>AddSection(StaticLinks, Item: route)</c> — a pre-seeded section, so
    /// the click produces a WORKING row rather than defect 7's empty grey hint. Its id is namespaced <c>dest:</c> so it
    /// can never collide with a section entry's id.</summary>
    [Fact]
    public void Destinations_AreOneSeededStaticLinksAddEach()
    {
        foreach (var e in SidebarPalette.Destinations)
        {
            Assert.Equal(SidebarPaletteGroup.Destinations, e.Group);
            Assert.Equal(SidebarSectionKind.StaticLinks, e.Kind);
            Assert.Equal(SidebarPaletteAdd.Destination, e.Add);
            Assert.Equal(SidebarPalette.DestinationSubLocKey, e.DescriptionLocKey);
            Assert.Null(e.ContributionId);
            Assert.StartsWith("dest:", e.Id, StringComparison.Ordinal);
            Assert.Equal("dest:" + e.RouteKey, e.Id);
        }

        // They render FIRST: a user hunting for "home" must meet the page before the abstraction that could hold it.
        Assert.Equal(SidebarPaletteGroup.Destinations, SidebarPalette.Groups[0]);
        for (int i = 0; i < SidebarPalette.Destinations.Length; i++)
            Assert.Same(SidebarPalette.Destinations[i], SidebarPalette.All[i]);
    }

    /// <summary>A destination clicked while a <c>StaticLinks</c> section is the subject APPENDS to it instead of minting
    /// a sibling — twelve destinations would otherwise be twelve one-row sections. Only a destination does this, and
    /// only into StaticLinks: appending a route into a PlaylistTree would be a <c>KindDoesNotAcceptItems</c> rejection
    /// dressed up as a feature.</summary>
    [Fact]
    public void Destinations_AppendIntoAStaticLinksSubjectAndNothingElse()
    {
        var dest = SidebarPalette.Destinations[0];
        var links = Section("sec_l", SidebarSectionKind.StaticLinks);

        Assert.True(SidebarPalette.AppendsToSelection(dest, links));
        Assert.False(SidebarPalette.AppendsToSelection(dest, Section("sec_t", SidebarSectionKind.PlaylistTree)));
        Assert.False(SidebarPalette.AppendsToSelection(dest, Section("sec_g", SidebarSectionKind.CustomGroup)));
        Assert.False(SidebarPalette.AppendsToSelection(dest, null));            // nothing selected ⇒ a new section
        Assert.False(SidebarPalette.AppendsToSelection(null, links));

        // No other palette row appends, whatever is selected — including the two other StaticLinks-kinded rows.
        foreach (var e in SidebarPalette.Sections)
            Assert.False(SidebarPalette.AppendsToSelection(e, links), e.Id + " appends into the selection");
    }

    /// <summary>CanDrag is about whether ONE <c>AddSection</c> can be composed at drag promotion. Three shapes cannot:
    /// the two that open a modal first (an action shortcut, a bare Links section — the picker IS the gesture, and a
    /// dialog opening mid-drag would be absurd), the one that switches the palette into contribution-pick mode, and
    /// "Recently played", which is deliberately TWO commands. Those stay CLICK-ONLY rather than shipping a drag that
    /// lies about its outcome.</summary>
    [Fact]
    public void CanDrag_RefusesExactlyTheRowsThatCannotResolveToOneAddSection()
    {
        Assert.True(SidebarPalette.CanDrag(SidebarPaletteAdd.Destination));
        Assert.True(SidebarPalette.CanDrag(SidebarPaletteAdd.Section));
        Assert.True(SidebarPalette.CanDrag(SidebarPaletteAdd.Contribution));
        Assert.True(SidebarPalette.CanDrag(SidebarPaletteAdd.LikedSongsShortcut));

        Assert.False(SidebarPalette.CanDrag(SidebarPaletteAdd.ActionShortcut));
        Assert.False(SidebarPalette.CanDrag(SidebarPaletteAdd.LinksWithPicker));
        Assert.False(SidebarPalette.CanDrag(SidebarPaletteAdd.AnyContribution));
        Assert.False(SidebarPalette.CanDrag(SidebarPaletteAdd.RecentlyPlayed));

        // Every destination is draggable — the group the canvas drop path was built for.
        foreach (var e in SidebarPalette.Destinations) Assert.True(SidebarPalette.CanDrag(e.Add));
    }

    /// <summary>DEFECT 7 — the bare "Links" row no longer adds a zero-item section. It opens the destination picker on
    /// the way in, which is a different <c>Add</c> verb, which is also why it is click-only above.</summary>
    [Fact]
    public void TheBareLinksRow_OpensTheDestinationPicker()
    {
        SidebarPaletteEntry? links = null;
        foreach (var e in SidebarPalette.All) if (e.Id == "staticLinks") links = e;

        Assert.NotNull(links);
        Assert.Equal(SidebarPaletteAdd.LinksWithPicker, links!.Add);
        Assert.Equal(SidebarSectionKind.StaticLinks, links.Kind);
    }

    // ── display options: the panel's projection is the reducer's inverse ──────────────────────────────────────────────

    /// <summary>THE ROUND TRIP, for EVERY field and EVERY choice the panel can offer: the exact index the panel hands
    /// <c>SetDisplayOption</c> must come back out of <c>SidebarDisplayValues.Read</c> unchanged.
    /// <para>This is the guard the round-2 "some things don't change" report demanded. A silent OFF-BY-ONE between the
    /// panel's choice index and the reducer's enum cast is invisible in the UI — the control moves, the document changes,
    /// and the WRONG value lands — so it can only be caught here. The enum arms are the prime suspects (a
    /// <c>ChoiceLocKeys</c> list reordered independently of its enum), which is why every index is probed rather than
    /// one representative value per field.</para></summary>
    [Fact]
    public void DisplayValues_EveryFieldRoundTripsEveryChoiceThePanelCanOffer()
    {
        foreach (var field in SidebarDisplayValues.Order)
        {
            foreach (int chosen in PanelChoices(field))
            {
                var opts = SidebarLayoutReducer.WithField(SidebarDisplayOptions.Default, field, chosen);
                int read = SidebarDisplayValues.Read(opts, field);
                Assert.True(chosen == read,
                    $"{field}: panel offered {chosen}, reducer+Read gave back {read}");
            }
        }
    }

    [Fact]
    public void PlaylistTreeQueryPanel_HidesKindsAndAlwaysShowsQualifier()
    {
        var tree = SidebarQueryPanelShape.For(SidebarSectionKind.PlaylistTree, qualifiersAvailable: false);
        Assert.False(tree.ShowKinds);
        Assert.True(tree.ShowQualifier);

        var entityWithoutCapability = SidebarQueryPanelShape.For(
            SidebarSectionKind.EntityList, qualifiersAvailable: false);
        Assert.True(entityWithoutCapability.ShowKinds);
        Assert.False(entityWithoutCapability.ShowQualifier);
    }

    [Fact]
    public void NumberEdit_RoundsAndClampsBothDirections()
    {
        Assert.Equal(3, SidebarNumberEdit.Normalize(3, 2, 4));
        Assert.Equal(2, SidebarNumberEdit.Normalize(2, 2, 4));
        Assert.Equal(2, SidebarNumberEdit.Normalize(1.2, 2, 4));
        Assert.Equal(4, SidebarNumberEdit.Normalize(9, 2, 4));
        Assert.Equal(3, SidebarNumberEdit.Normalize(2.6, 2, 4));
    }

    /// <summary>Exactly the values the property panel can send for one field: 0/1 for a flag, every choice index for an
    /// enum, and the range endpoints plus an interior point for a number. Nothing outside this set is reachable from the
    /// UI, and everything inside it must survive the trip.</summary>
    static IEnumerable<int> PanelChoices(SidebarDisplayField field)
    {
        if (SidebarDisplayValues.IsFlag(field)) return [0, 1];
        return field switch
        {
            // 0 = uncapped; the panel's slider spans 0..MaxItemsPerSection inclusive.
            SidebarDisplayField.MaxItems => [0, 1, 25, SidebarLayoutReducer.MaxItemsPerSection],
            // The reducer clamps to [2,4] and the panel's spinner offers exactly that.
            SidebarDisplayField.GridColumns => [2, 3, 4],
            _ => Indices(SidebarDisplayValues.ChoiceLocKeys(field).Length),
        };

        static IEnumerable<int> Indices(int count)
        {
            for (int i = 0; i < count; i++) yield return i;
        }
    }

    /// <summary>Every enum field's choice-label list must be exactly as long as the value range the reducer accepts. A
    /// list SHORTER than the range hides a legal value from the user; a list LONGER than it offers a choice the reducer
    /// silently clamps away — which is the "I picked it and nothing happened" shape of the round-2 report.</summary>
    [Fact]
    public void DisplayValues_EnumChoiceListsMatchTheReducersAcceptedRange()
    {
        foreach (var field in SidebarDisplayValues.Order)
        {
            if (SidebarDisplayValues.IsFlag(field)) continue;
            if (field is SidebarDisplayField.MaxItems or SidebarDisplayField.GridColumns) continue;

            int count = SidebarDisplayValues.ChoiceLocKeys(field).Length;
            Assert.True(count > 0, $"{field} has no choice labels");

            // One past the last offered choice must CLAMP back onto the last one. If it does not, the reducer accepts a
            // value the panel never offers and the choice list is short.
            var beyond = SidebarLayoutReducer.WithField(SidebarDisplayOptions.Default, field, count);
            Assert.Equal(count - 1, SidebarDisplayValues.Read(beyond, field));

            // ...and the last offered choice must survive unclamped (the list is not LONGER than the range).
            var last = SidebarLayoutReducer.WithField(SidebarDisplayOptions.Default, field, count - 1);
            Assert.Equal(count - 1, SidebarDisplayValues.Read(last, field));
        }
    }

    [Fact]
    public void DisplayValues_ReadIsTheInverseOfTheReducersWithField()
    {
        foreach (var field in SidebarDisplayValues.Order)
        {
            int wrote = field switch
            {
                SidebarDisplayField.Density => 2,
                SidebarDisplayField.Presentation => 1,
                SidebarDisplayField.MaxItems => 25,
                SidebarDisplayField.GridColumns => 3,
                SidebarDisplayField.RecentsSource => 1,
                _ => 1,
            };
            var opts = SidebarLayoutReducer.WithField(SidebarDisplayOptions.Default, field, wrote);
            Assert.Equal(wrote, SidebarDisplayValues.Read(opts, field));
        }
    }

    [Fact]
    public void DisplayValues_OrderCoversEveryFieldTheOptionTableCanAllow()
    {
        // Every field some kind allows must have a row; otherwise the panel silently cannot edit it.
        foreach (SidebarDisplayField field in Enum.GetValues<SidebarDisplayField>())
        {
            bool allowedSomewhere = false;
            foreach (SidebarSectionKind kind in Enum.GetValues<SidebarSectionKind>())
                if (SidebarSectionKinds.AllowsDisplayField(kind, field)) { allowedSomewhere = true; break; }
            if (!allowedSomewhere) continue;

            Assert.Contains(field, SidebarDisplayValues.Order);
            Assert.False(string.IsNullOrEmpty(SidebarDisplayValues.LabelLocKey(field)));
            if (!SidebarDisplayValues.IsFlag(field)
                && field is not (SidebarDisplayField.MaxItems or SidebarDisplayField.GridColumns))
                Assert.NotEmpty(SidebarDisplayValues.ChoiceLocKeys(field));
        }
    }

    [Fact]
    public void DisplayValues_ReadOfANullOptionsBagIsTheDefault()
        => Assert.Equal((int)SidebarDisplayOptions.Default.Density,
            SidebarDisplayValues.Read(null, SidebarDisplayField.Density));

    // ── the opaque extension-config rewriter ──────────────────────────────────────────────────────────────────────────

    static SidebarConfigSchema Schema() => new(1,
    [
        new SidebarConfigField("maxItems", SidebarConfigFieldKind.Int, "k.max", DefaultJson: "4", Min: 1, Max: 20),
        new SidebarConfigField("artistUri", SidebarConfigFieldKind.EntityUri, "k.artist", Required: true),
        new SidebarConfigField("kinds", SidebarConfigFieldKind.Enum, "k.kinds", DefaultJson: "\"all\"",
            EnumValues: ["all", "playlists"]),
    ]);

    [Fact]
    public void ConfigDefaults_SeedOnlyTheFieldsThatDeclareOne()
    {
        var config = new SidebarSourceConfig(SidebarConfigJson.Defaults(Schema()));
        Assert.True(config.IsObject);
        Assert.Equal(4, config.Int("maxItems", -1));
        Assert.Equal("all", config.Str("kinds"));
        Assert.Null(config.Str("artistUri"));
    }

    [Fact]
    public void ConfigDefaults_OfNoSchemaIsAnEmptyObject()
    {
        Assert.True(new SidebarSourceConfig(SidebarConfigJson.Defaults(null)).IsObject);
        Assert.True(new SidebarSourceConfig(SidebarConfigJson.Defaults(SidebarConfigSchema.None)).IsObject);
    }

    [Fact]
    public void ConfigWrite_PreservesMembersThisBuildDoesNotKnow()
    {
        var start = SidebarJson.Detach("{\"maxItems\":3,\"futureFlag\":true,\"nested\":{\"a\":1}}");

        var next = new SidebarSourceConfig(SidebarConfigJson.WithInt(start, "maxItems", 9));
        Assert.Equal(9, next.Int("maxItems"));
        Assert.True(next.Bool("futureFlag"));

        var again = new SidebarSourceConfig(SidebarConfigJson.WithBool(
            SidebarConfigJson.WithString(next.Value, "artistUri", "spotify:artist:1"), "descending", true));
        Assert.Equal("spotify:artist:1", again.Str("artistUri"));
        Assert.True(again.Bool("descending"));
        Assert.Equal(9, again.Int("maxItems"));
        Assert.True(again.Bool("futureFlag"));
    }

    [Fact]
    public void ConfigWrite_UriListNormalizesAndAnEmptyListRemovesTheMember()
    {
        var withList = SidebarConfigJson.WithStrings(SidebarJson.EmptyObject, "includeUris",
            [" spotify:artist:a ", "", "spotify:artist:b"]);
        var buffer = new List<string>();
        Assert.Equal(2, new SidebarSourceConfig(withList).Strings("includeUris", buffer));
        Assert.Equal("spotify:artist:a", buffer[0]);
        Assert.Equal("spotify:artist:b", buffer[1]);

        buffer.Clear();
        var cleared = SidebarConfigJson.WithStrings(withList, "includeUris", Array.Empty<string>());
        Assert.Equal(0, new SidebarSourceConfig(cleared).Strings("includeUris", buffer));
        Assert.True(new SidebarSourceConfig(cleared).IsObject);
    }

    [Fact]
    public void ConfigWrite_ANonObjectConfigDegradesToJustTheEditedMember()
    {
        var next = new SidebarSourceConfig(SidebarConfigJson.WithInt(SidebarJson.Detach("42"), "maxItems", 7));
        Assert.True(next.IsObject);
        Assert.Equal(7, next.Int("maxItems"));
    }

    [Fact]
    public void ConfigWrite_RoundTripsThroughTheReducersSetExtensionConfig()
    {
        var xref = new SidebarExtensionRef(SidebarContributions.WaveeExtensionId, SidebarContributions.Queue, 1,
            SidebarConfigJson.Defaults(Schema()));
        var section = new SidebarSectionSpec("sec_x", SidebarSectionKind.Extension, Title: null, TitleLocKey: null,
            Hidden: false, Collapsed: false, Display: null, Items: null, Query: null, Children: null, Extension: xref);
        var doc = new SidebarCustomLayout(SidebarTemplates.Blank, [section]);

        var edited = SidebarConfigJson.WithInt(xref.Config, "maxItems", 11);
        var result = SidebarLayoutReducer.Apply(doc, new SetExtensionConfig("sec_x", edited));

        Assert.True(result.Changed);
        var stored = result.Layout.Find("sec_x")!.Extension!;
        Assert.Equal(11, new SidebarSourceConfig(stored.Config).Int("maxItems"));
        Assert.Equal("all", new SidebarSourceConfig(stored.Config).Str("kinds"));

        // Re-applying the same value is a NoChange (the reducer compares canonical JSON) — so a mirror effect that writes
        // the control's own value back can never burn an undo slot.
        var again = SidebarLayoutReducer.Apply(result.Layout, new SetExtensionConfig("sec_x", edited));
        Assert.False(again.Changed);
        Assert.Equal(SidebarRejectReason.NoChange, again.Reason);
    }
}
