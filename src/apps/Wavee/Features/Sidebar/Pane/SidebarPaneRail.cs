using System;
using System.Collections.Generic;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using Wavee.Core.Sidebar;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

// §C5.2 — THE ONE 56-DIP RAIL (renamed from `CuratedRail` by R3.0.1; every mode's rail now comes out of here).
//
// Locked decision 7 keeps the rail's CONTENT mode-specific, and it still is — but as DATA, not as code: the content is
// exactly the sections whose spec sets `ShowInRail`, in document order, as decided by `SidebarRowPlanner.BuildRail` (which
// also applies the per-kind caps and the 40-tile total cap, collapses consecutive dividers and drops leading/trailing
// ones). This file only draws the tiles, so Classic's rail and Curated's rail cannot drift.
//
// The TILE is the shared one (`SidebarRailItem`), so every rail has the same hit box, corner ladder, selected treatment
// and tooltip behaviour. A 56-DIP strip has no room for text, so the tooltip IS the label — which is why every tile
// passes one.
//
// NOT VIRTUALIZED, deliberately: the planner caps the rail at `SidebarRowPlanner.RailTileCap` = 40 tiles, so the whole
// strip is bounded by construction and a virtual viewport would add a scroll seam for nothing (this is also the landed
// Classic rail's shape). §C5.2's `RepeatLayout.Stack(48f)` is therefore not used — see the DEVIATIONS note.
static class SidebarPaneRail
{
    /// <summary>Rail tiles rendered while a source is genuinely pending and nothing else resolved yet.</summary>
    const int PendingTiles = 4;

    public static Element Build(SidebarPane owner, SidebarRowPlan plan)
    {
        // PEEKED: this whole subtree is memoized by the pane on (plan version, selected route, theme, culture), so the
        // route lives in that dep key — a raw subscription here would re-render the pane on every navigation for a
        // tile strip the memo is about to rebuild anyway.
        string sel = owner.SelectedRoutePeek;
        var rows = plan.Rows;
        var kids = new List<Element>(rows.Count + 4);

        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            if (row.Kind == SidebarRowKind.Divider) { kids.Add(SidebarRailItem.Divider()); continue; }
            var section = owner.SectionOf(row.SectionId);
            if (section is null) continue;
            var tile = Tile(owner, section, row, plan, sel);
            if (tile is not null) kids.Add(tile);
        }

        // Pending + nothing resolved ⇒ the rail shimmers instead of looking like an empty pane.
        if (kids.Count == 0 && owner.Prefs?.Binder is null) kids.Add(SidebarSkeletons.RailStack(PendingTiles));

        // The mode's own rail affordance (Classic's create-playlist button). A rail plan cannot express authored chrome,
        // so it is appended here rather than injected positionally.
        if (owner.Config.RailFooter?.Invoke() is { } footer)
        {
            kids.Add(SidebarRailItem.Divider());
            kids.Add(footer);
        }

        // §C6.4 — the quick layout menu is reachable from the rail too, so a collapsed pane can still switch designs.
        if (owner.Config.RailLayoutMenu && owner.Prefs is { } prefs)
        {
            kids.Add(SidebarRailItem.Divider());
            kids.Add(SidebarLayoutMenu.Button(prefs, owner.Navigate, box: SidebarRailItem.Box));
        }

        // PHASE 1 — the shortcut band's rail form used to be PREPENDED here from `Config.RailHead`, a second hand-written
        // tile path beside the loop above. It is gone: the band is now the document's first section
        // (`SidebarShortcutsSection`), so `SidebarRowPlanner.BuildRail` emits its tiles from `ShowInRail` in document
        // order like every other section's, and the divider under them comes from the plan's own divider collapse rules.
        // One owner for rail CONTENT, which is what locked decision 7 always said.

        return new BoxEl
        {
            // Grow fills the rail's WIDTH so AlignItems=Center actually centres the 40-DIP tiles in the 56-DIP strip
            // (the body wrapper is a ROW, so without it this column hugs its content and left-aligns).
            Grow = 1f,
            Direction = 1, Gap = 6f, Padding = new Edges4(0f, 8f, 0f, 12f), AlignItems = FlexAlign.Center,
            Children = [.. kids],
        };
    }

    static Element? Tile(SidebarPane owner, SidebarSectionSpec section, in SidebarRow row, SidebarRowPlan plan,
                         string sel)
    {
        var entries = plan.Entries;
        switch (row.Kind)
        {
            // A projected entry: its cover, circular for an artist. A TRACK tile plays (it has no route); everything else
            // navigates.
            case SidebarRowKind.EntityRow when row.EntryIndex >= 0 && row.EntryIndex < entries.Count:
            {
                var entry = entries[row.EntryIndex];
                string key = "rail:" + entry.Id;
                string label = entry.Name.Length > 0 ? entry.Name : SidebarPaneText.ShortUri(entry.Uri);
                if (entry.Kind == SidebarEntryKind.AppRoute)
                {
                    var dest = ShellNav.Dest(entry.Id);
                    return SidebarRailItem.Icon(key, dest.Glyph, string.Equals(entry.Id, sel, StringComparison.Ordinal),
                        () => owner.Navigate(entry.Id, null), dest.Title);
                }
                Element art = SidebarCover.Art(entry.Cover, entry.MosaicTiles, entry.Id, SidebarRailItem.ArtEdge,
                    circular: entry.Circular || entry.Kind == SidebarEntryKind.Artist);
                string? route = entry.RouteKey;
                var snapshot = entry;
                Action? click = null;
                if (entry.IsTrack) click = () => owner.PlayTrack(snapshot.Uri);
                else if (route is { Length: > 0 } r) click = () => owner.Navigate(r, snapshot.Name);
                bool selected = route is { Length: > 0 } && string.Equals(route, sel, StringComparison.Ordinal);
                // An EDITABLE playlist cover is a real deposit target even at 56 DIP: the quick path for the two or three
                // playlists a user keeps at the top of their rail, without waiting out the pane's peek dwell. Everything
                // else (albums, artists, shows, routes) stays a pure navigation tile — it was never a track destination,
                // and the drop machinery's `transparent` arm keeps a drag crossing it silent rather than refusing.
                // It reuses the EXPANDED row's spec, so a rail deposit and a row deposit cannot diverge.
                bool depositable = entry.Kind == SidebarEntryKind.Playlist && entry.CanEdit;
                string depositUri = entry.Uri;
                var drop = depositable
                    ? owner.ResourceDropSpec(row.SectionId, slot: -1, depositUri, entry.Name, railCueUri: depositUri)
                    : null;
                return SidebarRailItem.Art(key, art, selected, click, label,
                    drop, dropActive: depositable ? () => owner.IsRailDropActive(depositUri) : null);
            }

            // A folder cannot disclose inside a 56-DIP strip, so its tile opens a SIDE FLYOUT listing its contents,
            // anchored to the tile's right edge — the same stacked drill-in the concert date flyout uses, with a back
            // header (see `SidebarRailFolderFlyout`). It used to EXPAND THE PANE instead, which answered "show me this
            // folder" by discarding the collapsed layout the user had chosen; that route survives as the tile's own
            // context menu (Expand folder), so nothing was taken away.
            case SidebarRowKind.FolderHeader when row.EntryIndex >= 0 && row.EntryIndex < entries.Count:
            {
                var entry = entries[row.EntryIndex];
                string folderId = entry.FolderId;
                string cueKey = entry.Id;
                string sectionId = row.SectionId;
                var snapshot = entry;   // an `in`-carried row's entry cannot be captured — copy the record struct
                // D16 — the tile is a real DESTINATION now, not just a way back into the pane. Into, and only Into:
                // a 56-DIP strip has nothing above or below a tile to be "before" or "after".
                var target = WaveeResourceDragPayload.FromEntry(entry, owner.Acts?.Svc, rootlistItem: true);
                var folderDrop = folderId.Length > 0 ? owner.RailFolderDropSpec(entry, target) : null;
                // The FULL folder menu, unchanged (`Menus.SidebarFolderRows` through `Menus.SidebarEntry`) — a rail tile
                // is the same rootlist folder as its expanded row, so right-click cannot offer less. Its Expand verb is
                // the pane-expanding gesture the click used to be.
                Func<ContextMenuModel?>? menu = owner.Acts is { } acts
                    ? () => Menus.SidebarEntry(acts, in snapshot,
                        folderId.Length > 0 ? () => owner.ExpandFolderInPane(folderId) : null,
                        folderExpanded: owner.Prefs?.IsFolderExpanded(folderId) ?? false)
                    : null;
                string key = SidebarPane.RailTileKey(in entry);
                string name = entry.Name.Length > 0 ? entry.Name : SidebarPaneText.ShortUri(entry.Id);
                // A 56-DIP strip has no room for a label OR a count, so the tooltip carries both — the tile's only
                // chance to say which folder it is and how much is inside before it is opened.
                string tip = name + " · " + Strings.Sidebar.V3.ItemCount(entry.ChildCount);
                return SidebarRailItem.Icon(key, Icons.Folder, false,
                    () => owner.OpenRailFolderFlyout(sectionId, in snapshot), tip,
                    folderDrop, folderDrop is null ? null : () => owner.IsRailDropActive(cueKey),
                    onRealized: h => owner.RegisterRailNode(key, h),
                    menuOverlay: owner.MenuOverlay, menu: menu);
            }

            // A hand-placed item (a route shortcut / a link) or a whole-section tile (Concerts, an extension contribution).
            case SidebarRowKind.IconRow:
                return section.Kind is SidebarSectionKind.CollectionShortcuts or SidebarSectionKind.StaticLinks
                    ? RouteTile(owner, section, row, sel)
                    : SectionTile(owner, section, row);

            default:
                return null;
        }
    }

    static Element? RouteTile(SidebarPane owner, SidebarSectionSpec section, in SidebarRow row, string sel)
    {
        var item = SidebarPaneText.ItemOf(section, row.Key);
        // An ACTION shortcut has no route and no artwork; a text-less rail cannot say what it would do, so it is omitted
        // from the rail rather than shown as an unlabelled mystery tile (the expanded pane owns it).
        if (item is { Target: SidebarItemTarget.Action } or { Target: SidebarItemTarget.Track }) return null;
        string key = row.Key;
        var dest = ShellNav.Dest(key);
        string label = item?.LabelOverride is { Length: > 0 } alias ? alias : dest.Title;
        return SidebarRailItem.Icon("rail:" + key, SidebarPaneText.Glyph(item, dest.Glyph),
            string.Equals(key, sel, StringComparison.Ordinal), () => owner.Navigate(key, null), label);
    }

    /// <summary>A whole SECTION as one tile. Concerts navigates to its hub; every other feed-shaped section (an extension
    /// contribution) EXPANDS the pane, because a 56-DIP strip cannot express a list and an unresolved contribution must not
    /// be able to fill the rail with prompts either.</summary>
    static Element SectionTile(SidebarPane owner, SidebarSectionSpec section, in SidebarRow row)
    {
        bool concerts = section.Kind == SidebarSectionKind.Concerts;
        string label = SidebarPaneText.TitleOf(section);
        Action click;
        if (concerts) click = () => owner.Navigate(Wavee.Features.Concerts.ConcertRoutes.Hub, null);
        else click = () => owner.Prefs?.SetCollapsed(false);
        return SidebarRailItem.Icon("rail:" + row.SectionId, concerts ? Icons.Calendar : Icons.Grid, false, click, label);
    }
}
