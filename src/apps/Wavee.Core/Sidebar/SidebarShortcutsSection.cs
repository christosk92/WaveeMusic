using System.Diagnostics.CodeAnalysis;

namespace Wavee.Core.Sidebar;

// PHASE 1 / DECISION A — THE SHORTCUT BAND AS AN ORDINARY SECTION.
//
// `SidebarCustomLayout.TopBar` used to render through a bespoke component (`Shared/SidebarNavBand.cs`) hung off two
// `SidebarPaneConfig` delegates. That cost four things at once: the band was named "Top bar" in a surface it no longer
// lives in (P5), Library V3 had no navigation section of its own, the band had to OPT OUT of the pane's route-keyed
// selection transaction (a band tile for "home" plus a plan row for "home" were two registrations under one key), and
// the 56-DIP rail needed a second, hand-written tile path beside `SidebarRowPlanner.BuildRail`.
//
// WHAT MOVED AND WHAT DID NOT. Only the RENDER PATH moves. The list is still `SidebarCustomLayout.TopBar` /
// `EffectiveTopBar`, still mutated only through `AddTopBarItem`/`MoveTopBarItem`/`RemoveTopBarItem`, still capped at
// `SidebarLayoutReducer.MaxTopBarItems`, still carried by the same undo ring, the same rejection contract, the same
// autosave and the same wire member (`SidebarWireCarry` untouched). There is NO schema change and NO migration.
//
// MATERIALISED, NEVER PERSISTED. `From` mints a section carrying the SENTINEL id `SidebarIds.TopBarSection`, which can
// never collide with a real section id (every minted id starts with `SidebarIds.SectionPrefix`). It is prepended to the
// document HANDED TO THE RENDERER and to nothing else: the persisted document must never contain a section with the
// sentinel id, because the reducer has no arm for it and `SidebarLayoutDoc` would write a section the next load could
// not address. Edits addressed at the sentinel route back to the three band commands through `SidebarItemCommands`.
public static class SidebarShortcutsSection
{
    /// <summary>The section's localized title key.
    ///
    /// <para>NOT <c>sidebar.section.shortcuts</c>, which the rework plan named: that key is ALREADY TAKEN — it is
    /// <c>SidebarSectionKinds.PaletteNameLocKey(CollectionShortcuts)</c> ("Library shortcuts", with a
    /// <c>…Sub</c> sibling listing Liked Songs / Albums / Artists / Podcasts / Local files). Reusing it would title the
    /// navigation band "Library shortcuts", which is exactly the wrong noun for a band whose default member is Home.
    /// The band's own keys all live under <c>sidebar.topbar.*</c> — the wire identifier's namespace, which the plan
    /// keeps ("model identifiers stay; only the VALUES change") — so its title lives there too.</para></summary>
    public const string TitleLocKey = "sidebar.topbar.title";

    /// <summary>Does the band render at all? An EMPTY list means the user emptied it on purpose (Home is genuinely
    /// removable), and an emptied band contributes NO section — no header, no rows, no rail tiles. Null never reaches
    /// here from a live pane (<c>SidebarCustomLayout.EffectiveTopBar</c> resolves null to the built-in default), but it
    /// is accepted so a probe/headless caller with no preference service is legal.</summary>
    public static bool Renders([NotNullWhen(true)] IReadOnlyList<SidebarItemSpec>? topBar) => topBar is { Count: > 0 };

    /// <summary>The synthesized section. <c>Kind = StaticLinks</c> because that is exactly what the band is — a
    /// hand-authored list of routes / entities / tracks / bound actions — which means the planner, the row slot, the
    /// rail (<c>ShowInRail</c>), the reorder band and the selection indicator all serve it with no new code.
    ///
    /// <para><c>Display</c> is the StaticLinks preset (<see cref="SidebarDisplayOptions.Links"/>), not the
    /// CollectionShortcuts one: <c>AllowsDisplayField(StaticLinks, CountBadges)</c> is false, so a band carrying
    /// <c>CountBadges = true</c> would be a default the user can never see or change (defect 8). <c>ShowInRail</c> is
    /// spelled out even though it is already the model default — the 56-DIP rail form of this band is a REQUIREMENT of
    /// Decision A, not an inherited accident.</para>
    ///
    /// <para>Callers must gate on <see cref="Renders"/> first; this never returns null so the shape stays a plain
    /// record the tests can compare.</para></summary>
    public static SidebarSectionSpec From(IReadOnlyList<SidebarItemSpec> topBar)
    {
        ArgumentNullException.ThrowIfNull(topBar);
        return new SidebarSectionSpec(
            Id: SidebarIds.TopBarSection,
            Kind: SidebarSectionKind.StaticLinks,
            Title: null,
            TitleLocKey: TitleLocKey,
            Hidden: false,
            // NOT collapsible: the band has no persisted Collapsed bit and no section-scoped command that could write
            // one (the sentinel is not in `Sections`), so a `true` here could never be undone. The renderer suppresses
            // the chevron for the same reason — see SidebarPaneSlot.Header.
            Collapsed: false,
            Display: SidebarDisplayOptions.Links with { ShowInRail = true },
            Items: topBar);
    }

    /// <summary>The document a PANE renders: <paramref name="document"/> with the band prepended as its first section.
    /// Returns the input UNCHANGED when the band is empty, so an emptied band costs nothing and the pane's first
    /// section header (which hosts the quick layout menu) falls back to the document's own first section.
    ///
    /// <para>This is a RENDER-PATH projection. The result must never be dispatched, saved or compared against the
    /// persisted document — see the file header.</para></summary>
    public static SidebarCustomLayout Prepend(SidebarCustomLayout document, IReadOnlyList<SidebarItemSpec>? topBar)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!Renders(topBar)) return document;

        var sections = new List<SidebarSectionSpec>(document.Sections.Count + 1) { From(topBar) };
        sections.AddRange(document.Sections);
        return document with { Sections = sections };
    }

    /// <summary>Is <paramref name="routeKey"/> already a shortcut in the band? The one owner of that question, so a
    /// document builder never re-derives it. Only <see cref="SidebarItemTarget.Route"/> items count: an Entity item
    /// whose uri happens to map onto the same destination is a different row with different art and a different menu.
    ///
    /// <para>Library V3 uses it to drop its own <c>v3.liked</c> section when Liked Songs is already a shortcut —
    /// otherwise the pane would show the identical destination twice, two rows apart.</para></summary>
    public static bool ContainsRoute(IReadOnlyList<SidebarItemSpec>? topBar, string routeKey)
    {
        if (topBar is null || string.IsNullOrEmpty(routeKey)) return false;
        for (int i = 0; i < topBar.Count; i++)
        {
            var item = topBar[i];
            if (item is null || item.Hidden) continue;
            if (item.Target == SidebarItemTarget.Route &&
                string.Equals(item.Key, routeKey, StringComparison.Ordinal)) return true;
        }
        return false;
    }
}
