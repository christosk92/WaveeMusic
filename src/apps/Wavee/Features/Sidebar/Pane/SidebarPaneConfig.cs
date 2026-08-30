using System;
using FluentGpu.Dsl;
using Wavee.Core.Sidebar;

namespace Wavee;

// R3.0.1 — THE ONLY MODE SEAM.
//
// The three sidebar modes shipped as three separate pane CONTAINERS (Classic's hand-built body, V3's Index/List/Row/Rail
// stack, Curated's planner + slots) that shared only leaf primitives. Paddings, badges, section rhythm and motion
// therefore tripled and drifted, which is exactly what the user's screenshot review found. Full unification means there
// is now ONE renderer (`SidebarPane` + `SidebarPaneSlot` + `SidebarPaneRail`) and every mode is a DOCUMENT plus this
// config. Drift becomes impossible by construction: a mode cannot reach around the config, because the renderer takes
// nothing else.
//
// EVERY MEMBER IS A DELEGATE OR A FLAG, never a snapshot. The config is built ONCE per mode mount (a
// `UseMemo(..., DepKey.Empty)`) and frozen into the pane's ctor — the component-props-freeze contract — so a value member
// would pin the first frame's state forever. `Document`/`Input`/`ModeEpoch` are therefore providers the pane invokes
// inside ITS render, which is also what makes the signals they read subscribe the pane.
sealed record SidebarPaneConfig
{
    /// <summary>Which mode this is. Used for the log field and for the pane's scroll/telemetry identity only — the
    /// renderer never branches on it (that would be the drift this seam exists to prevent).</summary>
    public required SidebarDesign Design { get; init; }

    /// <summary>Scroll-restoration key prefix. The pane appends <c>".drawer"</c> for the narrow-drawer mount, so the
    /// docked pane and the drawer never fight over one saved offset.</summary>
    public required string ScrollKeyPrefix { get; init; }

    /// <summary>The live document. Classic returns the LOCKED built-in
    /// (<see cref="SidebarBuiltInDocuments.Classic"/>) rebuilt from its three persisted section flags; Curated returns
    /// <c>prefs.Layout</c>; V3 returns its synthesized ephemeral document. Invoked inside the pane's render, so any
    /// signal it reads subscribes the pane.</summary>
    public required Func<SidebarCustomLayout> Document { get; init; }

    /// <summary>Optional transform of the binder's planner input (the mode's own filter/sort/search state folded in).
    /// Null ⇒ the binder's input verbatim. The pane still applies its own search-head override on top.</summary>
    public Func<SidebarProjectionInput, SidebarProjectionInput>? Input { get; init; }

    /// <summary>Any MODE-OWNED state that changes what the plan or a realized row draws, folded into one int. It is read
    /// both in the plan's <c>DepKey</c> and in the per-row epoch, so a mode state change re-plans AND re-skins the
    /// realized window. Classic folds its three section flags; V3 folds filter/qualifier/sort/desc/view/search/drill.
    /// Read the signals with <c>.Value</c> here — that read IS the subscription.</summary>
    public Func<int>? ModeEpoch { get; init; }

    /// <summary>Toggle a section's collapse state. Curated dispatches the undoable <c>SetSectionCollapsed</c> command;
    /// Classic writes its own persisted per-section flag (its document is NOT the Curated document and must never be
    /// mutated by a header click). Null ⇒ non-collapsible headers.</summary>
    public Action<string, bool>? SetSectionCollapsed { get; init; }

    /// <summary>The document is not the user's to edit here: suppresses the inline EntityList controls (chips +
    /// sort/view, which write section specs), the missing-entity "Remove" verb, and the empty-pane customize CTA.
    /// Classic is read-only; Curated is not; V3 is (its chrome owns its state).</summary>
    public bool ReadOnly { get; init; }

    /// <summary>Put a "+" CREATE affordance in every <c>PlaylistTree</c> section header (a flyout offering
    /// <i>New playlist</i> / <i>New folder</i>, and a drop destination that makes a new folder out of what you drop on
    /// it). Default false.
    ///
    /// <para>A CONFIG MEMBER, never a <c>Design</c> branch (rule 1). Classic and Curated set it; Library V3 does not,
    /// because its own chrome already carries a "+" and two create affordances for one command is exactly the per-mode
    /// duplication the unified renderer exists to remove.</para>
    ///
    /// <para>It replaces the deleted <c>CreateAction</c> ROW: a permanently-mounted row at the end of a 10k-playlist
    /// tree bought one command at the cost of the section's rhythm, and it also owned the "top level, at the end" drop
    /// slot that belongs to the tree's own closing gutter.</para></summary>
    public bool HeaderCreate { get; init; }

    /// <summary>Render the pane's own library-only search box above the scroll surface (only when the document actually
    /// contains a visible EntityList — searching a pane with no library list would filter nothing).</summary>
    public bool SearchHead { get; init; }

    /// <summary>Arbitrary MODE CHROME above the scroll surface (V3's header band, toolbar, chips, breadcrumb). Rendered
    /// before <see cref="SearchHead"/>. Invoked in the pane's render.</summary>
    public Func<Element?>? Head { get; init; }

    // PHASE 1 / Decision A — `NavBand` and `RailHead` are GONE. The shortcut band used to reach the renderer as two
    // delegates all three modes set identically, which is the shape of a seam that should never have existed: the band
    // is CONTENT, so it is now the first SECTION of the document each mode returns from `Document`
    // (`SidebarShortcutsSection`). The expanded pane plans it like any other StaticLinks section, the 56-DIP rail draws
    // it from `ShowInRail` through `SidebarRowPlanner.BuildRail`, and the selection indicator is the pane's ordinary
    // route-keyed transaction — which also retires the documented opt-out the band needed while it was chrome.

    /// <summary>PHASE 2 / DECISION B — THE CANVAS SEAM. Non-null delegate returning a non-null state ⇒ this pane renders
    /// as the CUSTOMIZE CANVAS: every section collapses to one uniform-height card (grip · kind glyph · title · count ·
    /// eye · "…"), one section expands at a time to reveal its real rows, hidden sections stay in view dimmed, and the
    /// section-card drag band is armed. Null (or a null return) ⇒ the ordinary sidebar, byte for byte.
    ///
    /// <para>A DELEGATE, per this config's one rule, and this member is the reason the rule exists: the session it reads
    /// changes several times a minute while the config is frozen at mount, so a <c>SidebarEditState</c> VALUE here would
    /// pin frame 1's session — the pane would render its first frame's edit state forever. It is invoked inside the
    /// PANE's render, which is also what subscribes the pane to the session's signals
    /// (<c>SidebarEditSession.Read</c>).</para>
    ///
    /// <para>Only <c>CuratedSidebar</c> supplies one — Classic's and V3's documents are read-only, so there is nothing
    /// for an editor to edit. The RENDERER must never learn that: it reads the delegate and nothing else, exactly as it
    /// never branches on <see cref="Design"/>.</para></summary>
    public Func<SidebarEditState?>? Edit { get; init; }

    /// <summary>Hang the quick sidebar-layout menu off the pane's FIRST section header (§C6.4 — the design switch must be
    /// reachable from the pane itself). V3 embeds those rows in its own overflow menu instead.</summary>
    public bool ShowLayoutMenu { get; init; } = true;

    /// <summary>Put the quick layout menu at the bottom of the 56-DIP rail too, so a collapsed pane can still switch.</summary>
    public bool RailLayoutMenu { get; init; } = true;

    /// <summary>An extra affordance appended to the rail after the plan's tiles (Classic's create-playlist button).</summary>
    public Func<Element?>? RailFooter { get; init; }

    /// <summary>What ACTIVATING a folder disclosure row does. Null ⇒ toggle the SHARED folder-expansion state
    /// (<c>SidebarPreferences.ToggleFolder</c>), which is what a pane that discloses folders inline wants — Classic and
    /// Curated both leave it null.
    ///
    /// <para>A mode whose pane can instead NAVIGATE into a folder (a narrow pane's session-only drill level, Library V3's
    /// Revision-2 amendment) supplies this: it receives the folder's id and its display name, so the handler can push a
    /// breadcrumb without re-reading the projection, and it decides — per width, per view — whether that gesture discloses,
    /// navigates or switches view. The renderer itself therefore never learns what a drill level is.</para>
    ///
    /// <para>It replaces the row's click AND the expand/collapse verb in its context menu, so the two can never disagree.</para></summary>
    public Action<string, string>? ActivateFolder { get; init; }

    /// <summary>Whether a folder activation currently expands/collapses descendants inside this pane. Null means true
    /// (Classic and Custom). A mode with alternate navigation, such as LibraryV3's narrow drill stack, supplies a live
    /// probe so the shared renderer animates only genuine inline structural changes.</summary>
    public Func<bool>? DisclosesFoldersInline { get; init; }

    /// <summary>Which section kinds reorder IN PLACE. Default: Pinned / StaticLinks / CustomGroup (§C5.1). PlaylistTree
    /// uses generic resource-drop destinations unless a mode explicitly opts into a local view-order overlay.</summary>
    public Func<SidebarSectionKind, bool>? IsReorderableSection { get; init; }

    /// <summary>Is the playlist tree currently showing a NON-CUSTOM sort? Null ⇒ false (Classic and Curated always show
    /// rootlist order). A sorted view cannot honour a positional insert — the rows are not in the order the mutation
    /// writes — so the drop resolver refuses Before/After/EndOfList there with the existing "clear sorting to reorder"
    /// sentence, while INTO a folder or a playlist stays legal (it needs no position at all).
    /// <para>A live probe, never a value: the pane's config freezes at mount and V3's sort changes under it.</para></summary>
    public Func<bool>? TreeSortedNonCustom { get; init; }

    /// <summary>Constrain a LIVE reorder gesture's reachable slots: <c>(section kind, from slot, requested slot)</c> ⇒
    /// the slot the gesture may actually reach. Null ⇒ every slot in the band is reachable (Classic, Curated, and V3's
    /// pin band).
    /// <para>It exists because a commit-time bail is not a cue: V3's local custom order cannot move an item between
    /// folders (that is a rootlist write), and the landed behaviour was a drag that animated all the way across a folder
    /// boundary and then silently did nothing. Clamping DURING the gesture means the gap never opens where the drop
    /// cannot land, so what the user sees is what commits — <c>LibraryV3Sidebar.CommitPaneReorder</c>'s same-parent bail
    /// stays as the invariant behind it rather than as the user's only feedback.</para>
    /// <para>Called from the displacement path while a drag is live, so it must stay allocation-free.</para></summary>
    public Func<SidebarSectionKind, int, int, int>? ClampReorderSlot { get; init; }

    /// <summary>Commit a same-list reorder. Null ⇒ <see cref="SidebarPaneReorderCommit.Default"/> (Pinned through the
    /// SHARED pin store, every other reorderable kind through the undoable <c>MoveItem</c> command). V3's local custom
    /// order supplies its own.</summary>
    public Action<SidebarPaneReorder>? CommitReorder { get; init; }

    /// <summary>Open the customizer (the empty-pane CTA and the unresolvable-contribution prompt row). Null ⇒ those
    /// surfaces render without their action rather than with a dead one.</summary>
    public Action? OnCustomize { get; init; }

    /// <summary>Create a playlist — the plain-click half of the header/rail "+" and the first row of its flyout. Null
    /// ⇒ the header "+" is not offered at all (see <see cref="HeaderCreate"/>) rather than offered dead, which is the
    /// honest shape for a mount with no library bridge.</summary>
    public Action? OnCreatePlaylist { get; init; }
}

/// <summary>
/// One committed reorder inside a reorderable band, in BAND-SLOT space (0..<see cref="SlotCount"/>-1). The renderer knows
/// the geometry; only the mode knows where the order LIVES, so this carries everything a commit could need and nothing
/// about the widget: the section spec, the two slots, and a resolver from slot → the row's stable key (a pin id, an item
/// key, an entry id).
/// </summary>
readonly record struct SidebarPaneReorder(
    SidebarSectionSpec Section,
    int FromSlot,
    int ToSlot,
    int SlotCount,
    Func<int, string> KeyAt);

/// <summary>The built-in reorder commit — Classic and Curated both use it verbatim, which is why it is not duplicated in
/// either mode component.</summary>
static class SidebarPaneReorderCommit
{
    /// <summary>Pinned commits through the SHARED pin store (the order every design sees); every other reorderable kind
    /// goes through the undoable, autosaved item command <see cref="SidebarItemCommands.Move"/> picks — <c>MoveItem</c>
    /// for a real section, <c>MoveTopBarItem</c> for the materialised Shortcuts band.</summary>
    public static void Default(SidebarPreferences? prefs, in SidebarPaneReorder r)
    {
        if (prefs is null || r.FromSlot == r.ToSlot) return;

        if (r.Section.Kind == SidebarSectionKind.Pinned)
        {
            // Map through the pin IDS rather than trusting band positions: a Pinned section may carry hidden overrides,
            // which shift the visible band relative to the store.
            int pf = prefs.Pins.IndexOf(r.KeyAt(r.FromSlot));
            int pt = prefs.Pins.IndexOf(r.KeyAt(r.ToSlot));
            if (pf < 0 || pt < 0) { prefs.MovePin(r.FromSlot, r.ToSlot); return; }
            prefs.MovePin(pf, pt);
            return;
        }

        int itemFrom = ItemIndexAt(r.Section, r.FromSlot);
        int itemTo = ItemIndexAt(r.Section, r.ToSlot);
        if (itemFrom < 0 || itemTo < 0) return;
        // PHASE 1 — the SENTINEL. A reorder inside the materialised Shortcuts section is addressed at
        // `SidebarIds.TopBarSection`, which is not in `layout.Sections`, so a bare `MoveItem` would be an
        // `UnknownSection` rejection: the row would snap back with no message. `SidebarItemCommands` owns that choice
        // once, for this commit AND for the customizer's call sites.
        prefs.Dispatch(SidebarItemCommands.Move(r.Section.Id, itemFrom, itemTo));
    }

    /// <summary>Band position → index in the section's ITEM list. The planner skips hidden items in order, so the n-th
    /// band row is the n-th VISIBLE item.</summary>
    static int ItemIndexAt(SidebarSectionSpec section, int slot)
    {
        var items = section.ItemList;
        int seen = 0;
        for (int i = 0; i < items.Count; i++)
        {
            if (items[i].Hidden) continue;
            if (seen == slot) return i;
            seen++;
        }
        return items.Count == 0 ? -1 : items.Count - 1;
    }
}
