using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text.Json;
using Wavee.Core.Sidebar;

namespace Wavee;

// The companion page's PURE model (Phase 3): the searchable section palette (now including the DESTINATIONS group),
// the display-option projection the generated property controls bind, and the opaque-config editing helpers they write
// through.
//
// ENGINE-FREE BY CONSTRUCTION (System + System.Text.Json + Wavee.Core + the engine-free Data/ contract types only), for
// the same reason as Features/Sidebar/Data/*: src/apps/Wavee.Tests source-includes THIS file, so SidebarCustomizerLayout
// Tests drive the REAL palette filter, the REAL destination table and the REAL config rewriter rather than copies of
// them. Nothing here may reference Element, Icons, Loc or Tok — glyph NAMES travel as strings
// (SidebarCustomizerPalette maps them app-side) and every label is a loc KEY resolved at the UI edge.
//
// THE ONE ENGINE TYPE ALLOWED IS `Signal<T>`, and only through `DeveloperMode.Enabled` in `SidebarPalette.Filter` (the
// developer-only destination gate). That is the SidebarPinStore precedent, and it does not cost the property this file
// actually needs: Wavee.Tests has no FluentGpu.Engine reference, but it does supply `FluentGpu.Signals.Signal<T>`
// (VirtualCollectionSignalShim), so the file still compiles — and the test drives the REAL gate. A mirrored plain bool
// would be a second source of truth for one switch, which is the drift this whole corpus exists to prevent.
//
// PHASE 3 DELETIONS, recorded so nobody re-adds them: the four-tier region ladder (`SidebarCustomizerTier` +
// `SidebarCustomizerLayout`), the command-fit table (`SidebarCustomizerCommandLayout` and friends) and the outline
// flattening + drag translation (`SidebarOutlineRow`/`SidebarOutlineRows`/`SidebarOutlineDrag`) all died WITH the
// surfaces they described. The page is now ONE scrolling column at every width, so there is no tier to resolve and no
// command to demote; the outline is gone because the docked pane IS the canvas (Decision B), and the one section-drag
// translation that survives is `SidebarEditPlan.ToMoveSection`, which works in the PANE's band slots.

/// <summary>The query controls a section kind owns. Kept beside the other pure customizer tables so the property panel
/// cannot grow a second, untested kind switch.</summary>
public readonly record struct SidebarQueryPanelShape(bool ShowKinds, bool ShowQualifier)
{
    public static SidebarQueryPanelShape For(SidebarSectionKind kind, bool qualifiersAvailable) => kind switch
    {
        SidebarSectionKind.PlaylistTree => new(false, true),
        SidebarSectionKind.EntityList => new(true, qualifiersAvailable),
        _ => new(false, false),
    };
}

/// <summary>The discrete-number editor's one normalization rule. The UI uses the returned integer both for dispatch and
/// for rejection snap-back; tests drive this pure seam instead of copying NumberBox behavior.</summary>
public static class SidebarNumberEdit
{
    public static int Normalize(double value, int min, int max)
        => Math.Clamp((int)Math.Round(value), min, max);
}

// ── the palette (grouped + searchable; Phase 3 adds Destinations) ─────────────────────────────────────────────────────

/// <summary>The palette's groups. <see cref="Destinations"/> is APPENDED (7) rather than inserted at 0 even though it
/// renders FIRST: the numeric order is only this enum's storage, and <see cref="SidebarPalette.Groups"/> is the single
/// authority on render order — renumbering the six that shipped would silently rewrite every existing entry's group.</summary>
public enum SidebarPaletteGroup : byte
{
    Navigation = 0, Library = 1, Playback = 2, DynamicFeeds = 3, Layout = 4, Actions = 5, Extensions = 6,

    /// <summary>Phase 3 — real app pages, so typing "home" answers with <b>Home</b> instead of "Links — shortcuts to
    /// pages like Home or Search". Entries are generated from <c>SidebarPinId.PinnableRoutes</c> plus the three
    /// destinations that are reachable but not pinnable; their labels come from <c>ShellNav.Dest</c> at the UI edge, so
    /// they follow the UI culture and can never disagree with the tab strip or the breadcrumb.</summary>
    Destinations = 7,
}

/// <summary>What the palette ADDS when clicked. Kept as data (not a switch in a render) so the palette, its search
/// filter and the tests all read one table.</summary>
public enum SidebarPaletteAdd : byte
{
    /// <summary>A plain <c>AddSection(Kind)</c>.</summary>
    Section = 0,
    /// <summary>An <c>AddSection(JumpBackIn)</c> that then flips its recents source to the play log.</summary>
    RecentlyPlayed = 1,
    /// <summary>An <c>AddSection(Extension, Extension: ref)</c> for <see cref="SidebarPaletteEntry.ContributionId"/>.</summary>
    Contribution = 2,
    /// <summary>The action picker, then <c>AddSection(StaticLinks, Item: the bound action item)</c> — ONE undo step.</summary>
    ActionShortcut = 3,
    /// <summary>The contribution picker (every registered source), then <see cref="Contribution"/>.</summary>
    AnyContribution = 4,
    /// <summary>A pre-seeded StaticLinks section containing the localized Liked Songs route.</summary>
    LikedSongsShortcut = 5,

    /// <summary>Phase 3 — an app PAGE. One <c>AddSection(StaticLinks, Item: the route)</c>, so the click is one
    /// undoable step that produces a working row, never the empty Links section defect 7 named. When a StaticLinks
    /// section is already the session's subject the same click APPENDS to it instead (see
    /// <see cref="SidebarPalette.AppendsToSelection"/>).</summary>
    Destination = 6,

    /// <summary>Phase 3 / defect 7 — a BARE Links section. Identical to <see cref="Section"/> except that the surface
    /// opens the destination picker immediately, so the section is never left with zero items.</summary>
    LinksWithPicker = 7,
}

/// <summary>One palette row. <paramref name="IconName"/> is a GLYPH NAME (this file is engine-free — the app-side
/// palette view maps it); the two loc keys come from <c>SidebarSectionKinds</c> wherever a kind owns them, so a section's
/// name is never spelled twice.
/// <para><paramref name="RouteKey"/> is set only on <see cref="SidebarPaletteGroup.Destinations"/> rows. Those carry NO
/// name loc key on purpose: a destination's label is <c>ShellNav.Dest(routeKey).Title</c>, which is the ONE owner of
/// "what this page is called" for the tab strip, the breadcrumb, the pinned rows and now the palette — minting a second
/// spelling here is exactly the drift the single-owner rule exists to catch.</para></summary>
public sealed record SidebarPaletteEntry(
    string Id,
    SidebarPaletteGroup Group,
    SidebarSectionKind Kind,
    SidebarPaletteAdd Add,
    string NameLocKey,
    string DescriptionLocKey,
    string IconName,
    string? ContributionId = null,
    string? RouteKey = null);

/// <summary>The palette table + its pure search filter.</summary>
public static class SidebarPalette
{
    /// <summary>The SECTION half of the palette, in group order. Seventeen ENTRIES in the spec's wording — eighteen rows
    /// here, because "Queue/Now Playing" names two distinct first-party contributions (<c>wavee.queue</c> and
    /// <c>wavee.nowPlaying</c>) with their own loc keys, and offering them as one row would make the second
    /// unreachable.</summary>
    public static readonly SidebarPaletteEntry[] Sections =
    [
        // Navigation
        new("pinned", SidebarPaletteGroup.Navigation, SidebarSectionKind.Pinned, SidebarPaletteAdd.Section,
            "sidebar.section.pinned", "sidebar.section.pinnedSub", "Pin"),
        new("shortcuts", SidebarPaletteGroup.Navigation, SidebarSectionKind.CollectionShortcuts,
            SidebarPaletteAdd.Section, "sidebar.section.shortcuts", "sidebar.section.shortcutsSub", "Heart"),
        new("likedSongs", SidebarPaletteGroup.Navigation, SidebarSectionKind.StaticLinks,
            SidebarPaletteAdd.LikedSongsShortcut, "nav.likedSongs", "sidebar.customizer.likedSongsSub", "Heart"),
        // DEFECT 7 — a bare "Links" section used to add a zero-item section that plans as one generic grey hint, and
        // adding it twice gave two identical dead rows. It now opens the destination picker on the way in, so the
        // section it creates always has something in it.
        new("staticLinks", SidebarPaletteGroup.Navigation, SidebarSectionKind.StaticLinks,
            SidebarPaletteAdd.LinksWithPicker, "sidebar.section.staticLinks", "sidebar.section.staticLinksSub", "Link"),

        // Library
        new("playlistTree", SidebarPaletteGroup.Library, SidebarSectionKind.PlaylistTree, SidebarPaletteAdd.Section,
            "sidebar.section.playlistTree", "sidebar.section.playlistTreeSub", "Folder"),
        new("entityList", SidebarPaletteGroup.Library, SidebarSectionKind.EntityList, SidebarPaletteAdd.Section,
            "sidebar.section.entityList", "sidebar.section.entityListSub", "Filter"),
        new("entityEmbed", SidebarPaletteGroup.Library, SidebarSectionKind.EntityEmbed, SidebarPaletteAdd.Section,
            "sidebar.section.entityEmbed", "sidebar.section.entityEmbedSub", "FavoriteStar"),

        // Playback
        new("recentlyPlayed", SidebarPaletteGroup.Playback, SidebarSectionKind.JumpBackIn,
            SidebarPaletteAdd.RecentlyPlayed, "sidebar.section.recentlyPlayed", "sidebar.section.recentlyPlayedSub",
            "Headphones"),
        new("queue", SidebarPaletteGroup.Playback, SidebarSectionKind.Extension, SidebarPaletteAdd.Contribution,
            "sidebar.section.queue", "sidebar.section.queueSub", "Queue", SidebarContributions.Queue),
        new("nowPlaying", SidebarPaletteGroup.Playback, SidebarSectionKind.Extension, SidebarPaletteAdd.Contribution,
            "sidebar.section.nowPlaying", "sidebar.section.nowPlayingSub", "Play", SidebarContributions.NowPlaying),

        // Dynamic feeds
        new("jumpBackIn", SidebarPaletteGroup.DynamicFeeds, SidebarSectionKind.JumpBackIn, SidebarPaletteAdd.Section,
            "sidebar.section.jumpBackIn", "sidebar.section.jumpBackInSub", "Clock"),
        new("artistTopTracks", SidebarPaletteGroup.DynamicFeeds, SidebarSectionKind.Extension,
            SidebarPaletteAdd.Contribution, "sidebar.section.artistTopTracks", "sidebar.section.artistTopTracksSub",
            "Contact", SidebarContributions.ArtistTopTracks),
        new("newReleases", SidebarPaletteGroup.DynamicFeeds, SidebarSectionKind.NewReleases, SidebarPaletteAdd.Section,
            "sidebar.section.newReleases", "sidebar.section.newReleasesSub", "Album"),
        new("concerts", SidebarPaletteGroup.DynamicFeeds, SidebarSectionKind.Concerts, SidebarPaletteAdd.Section,
            "sidebar.section.concerts", "sidebar.section.concertsSub", "Calendar"),

        // Layout
        new("group", SidebarPaletteGroup.Layout, SidebarSectionKind.CustomGroup, SidebarPaletteAdd.Section,
            "sidebar.section.group", "sidebar.section.groupSub", "Grid"),
        new("header", SidebarPaletteGroup.Layout, SidebarSectionKind.Header, SidebarPaletteAdd.Section,
            "sidebar.section.header", "sidebar.section.headerSub", "Font"),
        new("divider", SidebarPaletteGroup.Layout, SidebarSectionKind.Divider, SidebarPaletteAdd.Section,
            "sidebar.section.divider", "sidebar.section.dividerSub", "Remove"),

        // Actions
        new("actionShortcut", SidebarPaletteGroup.Actions, SidebarSectionKind.StaticLinks,
            SidebarPaletteAdd.ActionShortcut, "sidebar.customizer.itemAction", "sidebar.customizer.itemActionSub",
            "RefineSparkle"),

        // Extensions
        new("extension", SidebarPaletteGroup.Extensions, SidebarSectionKind.Extension,
            SidebarPaletteAdd.AnyContribution, "sidebar.section.extension", "sidebar.section.extensionSub", "Code"),
    ];

    /// <summary>The real destinations that are NOT in <c>SidebarPinId.PinnableRoutes</c>: settings and the API console
    /// are refused by <c>SidebarPinId.FromRoute</c> as tooling/editor surfaces, and the concerts hub is pinnable
    /// (<c>SidebarPinId.AlsoPinnableRoutes</c>) but deliberately absent from the curated PIN picker. Neither is a reason
    /// to hide a real page from a shortcut list — a shortcut and a pin are different offers.
    ///
    /// <para>The API console is developer surface: the table still CONTAINS it (this is a static, process-wide array —
    /// it cannot be reactive, and rebuilding it per render would defeat the reference-stable palette rows every
    /// consumer keys on), and <see cref="Filter"/> drops it while developer mode is off. That is why the gate lives at
    /// the one read path rather than at the table.</para>
    ///
    /// <para>DECLARED ABOVE <see cref="Destinations"/> ON PURPOSE: C# runs static field initializers in TEXTUAL order,
    /// so declaring this below the field that reads it would leave it null inside <c>BuildDestinations</c> and ship an
    /// empty Destinations group — a defect a reader would blame on the palette rather than on line order.</para></summary>
    static readonly string[] ExtraDestinationRoutes = ["settings", "api-console", ConcertsRoute];

    /// <summary>Spelled as a literal for the same reason <c>ShellNav</c> spells it that way: this file is source-included
    /// by <c>Wavee.Tests</c>, which cannot see <c>Wavee.Features.Concerts.ConcertRoutes</c>.</summary>
    const string ConcertsRoute = "concerts";

    /// <summary>The DESTINATIONS the palette offers, in the order they are listed here.
    ///
    /// <para>The set is <c>SidebarPinId.PinnableRoutes</c> ∪ <see cref="ExtraDestinationRoutes"/>, exactly as the plan
    /// specifies. The union is spelled with the pinnable list as the SOURCE rather than re-typed, so a route that
    /// becomes pinnable later shows up here for free.</para>
    ///
    /// <para>Every entry is <c>StaticLinks</c> + <see cref="SidebarPaletteAdd.Destination"/>: ONE
    /// <c>AddSection(StaticLinks, Item: route)</c>, one undo step. There is no <c>IconOverride</c> on the seeded item on
    /// purpose — a route row resolves its glyph through <c>ShellNav.Dest</c> at the row site
    /// (<c>SidebarPaneSlot</c>), and freezing a whitelisted icon name here would both duplicate that owner and risk an
    /// <c>InvalidIcon</c> rejection for any glyph outside <c>SidebarIconNames.Allowed</c>.</para></summary>
    public static readonly SidebarPaletteEntry[] Destinations = BuildDestinations();

    static SidebarPaletteEntry[] BuildDestinations()
    {
        var routes = SidebarPinId.PinnableRoutes;
        var extra = ExtraDestinationRoutes;
        var into = new SidebarPaletteEntry[routes.Length + extra.Length];
        for (int i = 0; i < routes.Length; i++) into[i] = Destination(routes[i]);
        for (int i = 0; i < extra.Length; i++) into[routes.Length + i] = Destination(extra[i]);
        return into;
    }

    /// <summary>One destination row. The name key is EMPTY — the label is resolved from the route at the UI edge (see
    /// <see cref="SidebarPaletteEntry.RouteKey"/>); the description is one shared string for the whole group, because
    /// twelve near-identical sentences is a translation cost with no reader benefit.</summary>
    static SidebarPaletteEntry Destination(string routeKey) => new(
        "dest:" + routeKey, SidebarPaletteGroup.Destinations, SidebarSectionKind.StaticLinks,
        SidebarPaletteAdd.Destination, "", DestinationSubLocKey, "Link", ContributionId: null, RouteKey: routeKey);

    /// <summary>The one description every destination row shares.</summary>
    public const string DestinationSubLocKey = "sidebar.customizer.destinationSub";

    /// <summary>The WHOLE palette: destinations first, then the section kinds. One array, so
    /// <see cref="Filter"/>, the grouping loop and the tests all read one table.</summary>
    public static readonly SidebarPaletteEntry[] All = Concat(Destinations, Sections);

    static SidebarPaletteEntry[] Concat(SidebarPaletteEntry[] a, SidebarPaletteEntry[] b)
    {
        var into = new SidebarPaletteEntry[a.Length + b.Length];
        Array.Copy(a, into, a.Length);
        Array.Copy(b, 0, into, a.Length, b.Length);
        return into;
    }

    /// <summary>Render order. DESTINATIONS FIRST: the whole point of the group is that a user who types (or scrolls
    /// looking for) "home" meets the page before they meet the abstraction that could hold it. The remaining six keep
    /// the order they shipped in.</summary>
    public static readonly SidebarPaletteGroup[] Groups =
    [
        SidebarPaletteGroup.Destinations,
        SidebarPaletteGroup.Navigation, SidebarPaletteGroup.Library, SidebarPaletteGroup.Playback,
        SidebarPaletteGroup.DynamicFeeds, SidebarPaletteGroup.Layout, SidebarPaletteGroup.Actions,
        SidebarPaletteGroup.Extensions,
    ];

    /// <summary>DEFECT 5 — the palette entry that NAMES a contribution id, or null.
    ///
    /// <para>The "pick a contribution" list used to print the raw source id twice — as the title and as the subtitle —
    /// under a comment saying no manifest name exists until M3/M4. That is true of a THIRD-PARTY contribution and false
    /// of every one Wavee ships: the palette's own Playback/Dynamic-feeds rows already carry localized names for
    /// <c>wavee.queue</c>, <c>wavee.nowPlaying</c> and <c>wavee.artist.topTracks</c>. Looking the id up here means the
    /// pick list says "Queue" where it can and falls back to the id exactly once where it cannot.</para></summary>
    public static SidebarPaletteEntry? EntryForContribution(string? contributionId)
    {
        if (contributionId is not { Length: > 0 }) return null;
        for (int i = 0; i < Sections.Length; i++)
        {
            var e = Sections[i];
            if (e.ContributionId is { Length: > 0 } id
                && string.Equals(id, contributionId, StringComparison.Ordinal)) return e;
        }
        return null;
    }

    /// <summary>Can this entry be DRAGGED onto the canvas, or is it click-only?
    ///
    /// <para>A drag has to resolve to ONE <c>AddSection</c> at the drop position, composed at promotion time. Three
    /// kinds of entry cannot: the two that open a modal first (an action shortcut, a bare Links section — the picker
    /// IS the gesture, and a dialog opening mid-drag would be absurd), the one that switches the palette into
    /// contribution-pick mode, and "Recently played", which is deliberately TWO commands (<c>AddSection</c> plus the
    /// recents-source flip) — dropping it would land a section whose source silently disagreed with its own name. Those
    /// rows stay click-only rather than shipping a drag that lies about its outcome.</para></summary>
    public static bool CanDrag(SidebarPaletteAdd add) => add is SidebarPaletteAdd.Section
        or SidebarPaletteAdd.Contribution or SidebarPaletteAdd.LikedSongsShortcut or SidebarPaletteAdd.Destination;

    /// <summary>Does clicking this entry APPEND to the currently-selected section instead of creating a sibling? Only a
    /// destination does, and only into a <c>StaticLinks</c> section: every other palette row creates a section by
    /// definition, and appending a route into (say) a PlaylistTree would be a <c>KindDoesNotAcceptItems</c> rejection
    /// dressed up as a feature. Pure so the rule is testable rather than a condition inside a click handler.</summary>
    public static bool AppendsToSelection(SidebarPaletteEntry? entry, SidebarSectionSpec? selected)
        => entry is { Add: SidebarPaletteAdd.Destination, RouteKey.Length: > 0 }
           && selected is { Kind: SidebarSectionKind.StaticLinks };

    public static string GroupLocKey(SidebarPaletteGroup group) => group switch
    {
        SidebarPaletteGroup.Destinations => "sidebar.palette.destinations",
        SidebarPaletteGroup.Navigation => "sidebar.palette.navigation",
        SidebarPaletteGroup.Library => "sidebar.palette.library",
        SidebarPaletteGroup.Playback => "sidebar.palette.playback",
        SidebarPaletteGroup.DynamicFeeds => "sidebar.palette.dynamic",
        SidebarPaletteGroup.Layout => "sidebar.palette.layout",
        SidebarPaletteGroup.Actions => "sidebar.palette.actions",
        _ => "sidebar.palette.extensions",
    };

    /// <summary>Trim + lowercase, "" for nothing typed. Normalized ONCE per keystroke, never per row.</summary>
    public static string NormalizeQuery(string? query)
        => string.IsNullOrWhiteSpace(query) ? "" : query!.Trim().ToLowerInvariant();

    /// <summary>Token-wise contains: EVERY whitespace-separated token of the (already normalized) query must appear in
    /// the label or the description, so "top art" finds "Artist top tracks". An empty query matches everything.</summary>
    public static bool Matches(string normalizedQuery, string? label, string? description)
    {
        if (normalizedQuery.Length == 0) return true;
        int i = 0;
        while (i < normalizedQuery.Length)
        {
            while (i < normalizedQuery.Length && normalizedQuery[i] == ' ') i++;
            int start = i;
            while (i < normalizedQuery.Length && normalizedQuery[i] != ' ') i++;
            if (i == start) break;
            var token = normalizedQuery.AsSpan(start, i - start);
            bool hit = Contains(label, token) || Contains(description, token);
            if (!hit) return false;
        }
        return true;

        static bool Contains(string? haystack, ReadOnlySpan<char> token)
            => haystack is { Length: > 0 } && haystack.AsSpan().Contains(token, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Append the entries matching <paramref name="query"/> (in table order) to <paramref name="into"/>.
    /// The two text projections are delegates because the localized strings live at the UI edge — this file never calls
    /// <c>Loc</c>. Returns how many were appended.</summary>
    public static int Filter(string? query, Func<SidebarPaletteEntry, string> labelOf,
                            Func<SidebarPaletteEntry, string?>? descriptionOf, List<SidebarPaletteEntry> into)
    {
        ArgumentNullException.ThrowIfNull(labelOf);
        ArgumentNullException.ThrowIfNull(into);
        into.Clear();
        string q = NormalizeQuery(query);
        // The one gate for developer-only destinations (the API console). `Peek`, not `Value`: the palette is filtered
        // per keystroke inside a render that is ALREADY subscribed to the switch through the pane, and a second
        // subscription here would only widen what a keystroke invalidates.
        bool devMode = DeveloperMode.Enabled.Peek();
        for (int i = 0; i < All.Length; i++)
        {
            var e = All[i];
            if (!DeveloperMode.ShowsRoute(e.RouteKey, devMode)) continue;
            if (!Matches(q, labelOf(e), descriptionOf?.Invoke(e))) continue;
            into.Add(e);
        }
        return into.Count;
    }
}

// ── display options: the property panel's row order + the int projection its generated controls bind ───────────────────

/// <summary>The display-option half of the property panel, kept pure so the panel is a RENDERER of this table rather
/// than a hand-written per-kind form (the drift <c>SidebarSectionKinds.AllowsDisplayField</c> exists to prevent).</summary>
public static class SidebarDisplayValues
{
    /// <summary>Row order in the property panel. Which of these a KIND actually shows is
    /// <c>SidebarSectionKinds.AllowsDisplayField</c>'s answer — never a second table.</summary>
    public static readonly SidebarDisplayField[] Order =
    [
        SidebarDisplayField.Density,
        SidebarDisplayField.Presentation,
        SidebarDisplayField.GridColumns,
        SidebarDisplayField.Artwork,
        SidebarDisplayField.Subtitles,
        SidebarDisplayField.CountBadges,
        SidebarDisplayField.InlineControls,
        SidebarDisplayField.PlayButton,
        SidebarDisplayField.RecentsSource,
        SidebarDisplayField.MaxItems,
        SidebarDisplayField.EmptyBehavior,
        SidebarDisplayField.CollapsedByDefault,
        SidebarDisplayField.ShowInRail,
    ];

    /// <summary>The field's current value as the int <c>SetDisplayOption</c> carries (bools encode 0/1) — the exact
    /// inverse of <c>SidebarLayoutReducer.WithField</c>.</summary>
    public static int Read(SidebarDisplayOptions? options, SidebarDisplayField field)
    {
        var o = options ?? SidebarDisplayOptions.Default;
        return field switch
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
            SidebarDisplayField.EmptyBehavior => (int)o.EmptyBehavior,
            _ => 0,
        };
    }

    /// <summary>True for the fields the panel renders as a <c>ToggleSwitch</c> (everything that encodes 0/1).</summary>
    public static bool IsFlag(SidebarDisplayField field) => field is SidebarDisplayField.Artwork
        or SidebarDisplayField.Subtitles or SidebarDisplayField.CountBadges
        or SidebarDisplayField.CollapsedByDefault or SidebarDisplayField.ShowInRail
        or SidebarDisplayField.InlineControls or SidebarDisplayField.PlayButton;

    /// <summary>The row's label loc key (the catalog's <c>sidebar.option.*</c> family).</summary>
    public static string LabelLocKey(SidebarDisplayField field) => field switch
    {
        SidebarDisplayField.Density => "sidebar.option.density",
        SidebarDisplayField.Presentation => "sidebar.option.presentation",
        SidebarDisplayField.Artwork => "sidebar.option.artwork",
        SidebarDisplayField.Subtitles => "sidebar.option.subtitles",
        SidebarDisplayField.CountBadges => "sidebar.option.countBadges",
        SidebarDisplayField.CollapsedByDefault => "sidebar.option.collapsedByDefault",
        SidebarDisplayField.ShowInRail => "sidebar.option.showInRail",
        SidebarDisplayField.MaxItems => "sidebar.option.maxItems",
        SidebarDisplayField.GridColumns => "sidebar.option.gridColumns",
        SidebarDisplayField.InlineControls => "sidebar.a11y.sortView",          // the inline filter/sort row
        SidebarDisplayField.PlayButton => "detail.play",
        SidebarDisplayField.RecentsSource => "sidebar.option.sortRecents",
        SidebarDisplayField.EmptyBehavior => "sidebar.option.emptyBehavior",
        _ => "",
    };

    /// <summary>The choice labels for the ENUM fields (empty for flags and numbers). The panel picks the control from these
    /// labels at render time — Segmented when they are few and short, a ComboBox otherwise (<c>CzRow.Choice</c>) — so the
    /// ORDER here is load-bearing: index i must be enum value i, which
    /// <c>DisplayValues_EveryFieldRoundTripsEveryChoiceThePanelCanOffer</c> pins.</summary>
    public static string[] ChoiceLocKeys(SidebarDisplayField field) => field switch
    {
        SidebarDisplayField.Density =>
            ["sidebar.option.densityCompact", "sidebar.option.densityCozy", "sidebar.option.densityComfortable"],
        SidebarDisplayField.Presentation =>
            ["sidebar.option.presentationList", "sidebar.option.presentationGrid"],
        SidebarDisplayField.RecentsSource =>
            ["sidebar.recents.sourceVisited", "sidebar.recents.sourcePlayed"],
        SidebarDisplayField.EmptyBehavior =>
            [
                "sidebar.option.emptyDefault", "sidebar.option.emptyHide",
                "sidebar.option.emptyCompact", "sidebar.option.emptyAction",
            ],
        _ => Array.Empty<string>(),
    };
}

// ── opaque extension config: the writer behind the schema-generated property controls ─────────────────────────────────

/// <summary>Rewrites an <c>SidebarExtensionRef.Config</c> object one field at a time — the ONE place the customizer turns
/// a generated control's value back into JSON. Every write COPIES the untouched members through verbatim, so a config
/// member this build's schema does not know (a newer extension, a hand-edited document) survives an edit (the layout's
/// round-trip-untouched policy). Never throws: a non-object config is replaced by a fresh object.</summary>
public static class SidebarConfigJson
{
    /// <summary>The config a freshly added contributed section starts from: <c>{}</c> plus every schema field that
    /// declares a <c>DefaultJson</c>, so a queue/top-tracks section is bounded before the inspector is touched.</summary>
    public static JsonElement Defaults(SidebarConfigSchema? schema)
    {
        if (schema is null || schema.Fields.Count == 0) return SidebarJson.EmptyObject;
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            var fields = schema.Fields;
            for (int i = 0; i < fields.Count; i++)
            {
                var f = fields[i];
                if (f.DefaultJson is not { Length: > 0 } raw) continue;
                if (!TryWriteRaw(w, f.Key, raw)) continue;
            }
            w.WriteEndObject();
        }
        return Parse(buffer);
    }

    public static JsonElement WithString(JsonElement config, string key, string? value)
        => Rewrite(config, key, value is null ? null : w => w.WriteStringValue(value));

    public static JsonElement WithInt(JsonElement config, string key, int value)
        => Rewrite(config, key, w => w.WriteNumberValue(value));

    public static JsonElement WithBool(JsonElement config, string key, bool value)
        => Rewrite(config, key, w => w.WriteBooleanValue(value));

    /// <summary>A string array (the <c>UriList</c> field kind). A null/empty list REMOVES the member rather than storing
    /// <c>[]</c> — the same "empty normalizes to absent" rule the query's uri sets follow.</summary>
    public static JsonElement WithStrings(JsonElement config, string key, IReadOnlyList<string>? values)
    {
        if (values is null || values.Count == 0) return Rewrite(config, key, null);
        return Rewrite(config, key, w =>
        {
            w.WriteStartArray();
            for (int i = 0; i < values.Count; i++)
            {
                string one = values[i]?.Trim() ?? "";
                if (one.Length == 0) continue;
                w.WriteStringValue(one);
            }
            w.WriteEndArray();
        });
    }

    /// <summary>Copy every member except <paramref name="key"/>, then write <paramref name="write"/> under it (null
    /// <paramref name="write"/> = remove the member).</summary>
    public static JsonElement Rewrite(JsonElement config, string key, Action<Utf8JsonWriter>? write)
    {
        if (string.IsNullOrEmpty(key)) return SidebarJson.Own(config);

        var buffer = new ArrayBufferWriter<byte>(256);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            try
            {
                if (config.ValueKind == JsonValueKind.Object)
                    foreach (var prop in config.EnumerateObject())
                    {
                        if (string.Equals(prop.Name, key, StringComparison.Ordinal)) continue;
                        prop.WriteTo(w);
                    }
            }
            catch (Exception) { /* a disposed/mangled element degrades to "just the edited member" */ }

            if (write is not null)
            {
                w.WritePropertyName(key);
                write(w);
            }
            w.WriteEndObject();
        }
        return Parse(buffer);
    }

    static bool TryWriteRaw(Utf8JsonWriter w, string key, string rawJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            w.WritePropertyName(key);
            doc.RootElement.WriteTo(w);
            return true;
        }
        catch (Exception) { return false; }
    }

    static JsonElement Parse(ArrayBufferWriter<byte> buffer)
    {
        try
        {
            using var doc = JsonDocument.Parse(buffer.WrittenMemory);
            return doc.RootElement.Clone();
        }
        catch (Exception) { return SidebarJson.EmptyObject; }
    }
}
