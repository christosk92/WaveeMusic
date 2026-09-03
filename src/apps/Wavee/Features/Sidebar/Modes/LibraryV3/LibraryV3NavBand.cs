using System;
using System.Collections.Generic;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using Wavee.Core.Sidebar;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

// W3 — THE NAV BAND: HOME ABOVE THE HEADER, NOT IN THE LIST.
//
// V3 used to prepend the shortcut band as the document's FIRST SECTION (Phase 1 / Decision A — see
// `SidebarPaneConfig.cs`'s "RailHead is GONE" note and `LibraryV3Document.cs`), which is the right shape for Classic
// and Curated: their bands scroll WITH the rest of the pane, and a section is exactly what a scrolling band is. V3's
// own target design (Spotify's "Your Library") disagrees — the Home row sits ABOVE the header, never scrolls and is
// never touched by a filter, a search or a drill level. Modelling that as a section made every one of V3's "why does
// this move / disappear / reorder" complaints inevitable: a section lives inside the ONE virtualized list, which is
// exactly the surface search/filter/drill own.
//
// So the band is CHROME here — the first child of `LibraryV3Chrome`, mounted through `SidebarPaneConfig.Head`, above
// the scroll surface entirely — while its CONTENT stays exactly what it always was: `SidebarPreferences.TopBar`
// (`SidebarCustomLayout.EffectiveTopBar`), mutated only through the existing `AddTopBarItem`/`MoveTopBarItem`/
// `RemoveTopBarItem` commands. This file adds no schema, no command and no second source of truth — it is a second
// RENDER of the same list Classic/Curated still prepend as a section (`SidebarShortcutsSection`, untouched).
//
// WHY IT NEVER FILTERS. The band is navigation chrome, not library content: a search that hides every playlist must
// not also hide the way home. Reading `prefs.LayoutVersion.Value` (not the V3 mode epoch) is deliberate — the band
// reacts to an edit of ITS OWN list, never to V3's filter/sort/search/drill state.
//
// WHY EDITS STILL GO THROUGH THE CUSTOMIZER. This component only READS the band and draws it — no drag, no reorder,
// no context menu, no "Remove" verb. `SidebarItemSpec`'s target vocabulary (Route/Entity/Track/Action) is shaped by
// the same pure rules `SidebarNavBandModel` already has tests for (route resolution, selection), which this file
// reuses rather than re-deriving; a hand-edited band is still authored in one place, the customizer's Top bar editor.
sealed class LibraryV3NavBand : Component
{
    readonly LibraryV3Session _session;

    public LibraryV3NavBand(LibraryV3Session session) => _session = session;

    public override Element Render()
    {
        var prefs = UseContext(SidebarPreferences.Slot);
        if (prefs is null) return new BoxEl();

        // Subscribe to the band's OWN edit signal — not the V3 mode epoch (`_session.ReadState()`), which folds in
        // filter/sort/search/drill: the band must re-render when the user adds/removes/renames a tile, and must NOT
        // re-render on every keystroke of a library search it does not draw.
        _ = prefs.LayoutVersion.Value;
        var items = prefs.TopBar;
        if (items.Count == 0) return new BoxEl();   // the user emptied it on purpose — same rule as `Renders`

        string route = _session.Route.Value.Name;
        var registry = UseContext(WaveeExtensionRegistry.Slot) ?? _session.Acts?.Extensions;

        var kids = new List<Element>(items.Count + 1);
        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (item is null || item.Hidden) continue;
            Element row = item.Target switch
            {
                SidebarItemTarget.Action => ActionRow(item, registry),
                SidebarItemTarget.Track => TrackRow(item),
                SidebarItemTarget.Entity => EntityRow(item, route),
                _ => RouteRow(item, route),
            };
            // Keyed independently of whatever the row builder wraps it in (an Action row's disabled arm is a
            // ToolTip.Wrap, which does not carry the inner row's Key forward), so identity survives a kind that
            // toggles between its enabled/disabled shape.
            // Direction = 1: a BoxEl defaults to a ROW, and a row wrapper arranges its single child at the child's own
            // measured width — the hover/selected plate then hugs "Home" instead of spanning the pane (the same trap
            // pitfalls.md records for Reorderable.Item). A column stretches its child across.
            kids.Add(new BoxEl { Key = item.Id, Direction = 1, Children = [row] });
        }
        if (kids.Count == 0) return new BoxEl();

        // Spans the content lane exactly like `LibraryV3Chrome`'s "v3-chrome-rule": the parent pads to PaneEdge (8),
        // so the margin here makes up the rest of ContentLane/ContentLaneEnd, net.
        kids.Add(Divider() with
        {
            Key = "v3-nav-rule",
            Margin = new Edges4(SidebarPaneMetrics.ContentLane - SidebarRowGeometry.PaneEdge, 4f,
                                 SidebarPaneMetrics.ContentLaneEnd - SidebarRowGeometry.PaneEdge, 4f),
        });

        return new BoxEl
        {
            Key = "v3-nav-band",
            Direction = 1, Shrink = 0f,
            // The rows carry their own IndentFor(0) inset, so padding to the bare PaneEdge (not ContentLane) lands
            // their glyph at SidebarRowGeometry.ArtX(0) = 27 — the same x every list row's art/glyph column starts
            // at, which is the whole point of a band mounted OUTSIDE the list still sharing its left edge.
            Padding = new Edges4(SidebarRowGeometry.PaneEdge, 0f, SidebarRowGeometry.PaneEdge, 0f),
            Children = [.. kids],
        };
    }

    // ── the four tile shapes (mirrors SidebarPaneSlot.RouteRow/ActionRow/TrackItemRow and the Entity arm of
    //    EntryRow — the pane's per-row vocabulary — minus everything a plan row needs and a chrome band does not:
    //    no drag source, no drop target, no context menu, no reorder, no multi-select) ─────────────────────────────

    Element RouteRow(SidebarItemSpec item, string route)
    {
        var dest = ShellNav.Dest(item.Key);
        string label = item.LabelOverride is { Length: > 0 } alias ? alias : dest.Title;
        var spec = new SidebarRowSpec
        {
            Key = item.Id,
            Label = label,
            Selected = string.Equals(route, item.Key, StringComparison.Ordinal),
            Enabled = true,
            Depth = 0,
            Density = SidebarDensity.Cozy,
            Height = LibraryV3Metrics.NavRowHeight,
            Glyph = SidebarIcons.For(item, dest.Glyph),
            OnClick = () => _session.Go(item.Key, null),
            Focusable = true,
            Overflow = false,
        };
        return SidebarEntityRow.Create(spec);
    }

    /// <summary>An ACTION shortcut, resolved ONLY through the extension registry (never `AppActions.All`, rule 7). An
    /// unavailable target renders visible-but-disabled with the reason as its tooltip — it never vanishes.</summary>
    Element ActionRow(SidebarItemSpec item, WaveeExtensionRegistry? registry)
    {
        var binding = item.Action;
        var acts = _session.Acts;

        string label = item.LabelOverride ?? "";
        var icon = default(IconRef);
        bool enabled = false;
        string? reason = Loc.Get(SidebarPaneLoc.ExtensionNotNow);
        Action? click = null;

        if (binding is null)
        {
            reason = Loc.Get(SidebarPaneLoc.ExtensionMissing);
        }
        else
        {
            var bound = binding;   // non-nullable local: Execute takes it by `in`
            if (registry is not null && registry.TryGetAction(bound, out var descriptor))
            {
                label = label.Length > 0 ? label : descriptor.Label();
                icon = descriptor.Icon();
            }
            if (registry is not null && acts is { } services)
            {
                var resolution = registry.Resolve(services, bound);
                enabled = resolution.Available;
                reason = resolution.ReasonLocKey is { } key ? Loc.Get(key) : null;
                var reg = registry;
                if (enabled) click = () => reg.Execute(services, in bound);
            }
        }
        if (label.Length == 0) label = Loc.Get(SidebarPaneLoc.ExtensionManage);

        var spec = new SidebarRowSpec
        {
            Key = item.Id,
            Label = label,
            Enabled = enabled,
            Density = SidebarDensity.Cozy,
            Height = LibraryV3Metrics.NavRowHeight,
            // Art-wide leading column + the row's own LeadingGap (W7): the action row's label lines up with Home's.
            Leading = SidebarPaneIcon.Leading(item.IconOverride, icon, enabled, SidebarRowMetrics.ArtFor(SidebarDensity.Cozy)),
            OnClick = click,
            Focusable = enabled,
        };
        Element row = SidebarEntityRow.Create(spec);
        return reason is { Length: > 0 } r ? ToolTip.Wrap(row, r, grow: 1f) : row;
    }

    /// <summary>A hand-placed TRACK: click PLAYS, never navigates (a track has no route, §C1.8.3).</summary>
    Element TrackRow(SidebarItemSpec item)
    {
        string uri = item.Key;
        string label = item.LabelOverride is { Length: > 0 } alias ? alias
            : item.FallbackTitle is { Length: > 0 } cached ? cached
            : SidebarPaneText.ShortUri(uri);

        var spec = new SidebarRowSpec
        {
            Key = item.Id,
            Label = label,
            Enabled = true,
            Density = SidebarDensity.Cozy,
            Height = LibraryV3Metrics.NavRowHeight,
            Leading = SidebarCover.Art(SidebarPaneText.FallbackImage(item), null, uri, 32f),
            Track = true,
            OnClick = () => PlayTrack(uri),
            Focusable = true,
        };
        return SidebarEntityRow.WithPlayTrackHint(SidebarEntityRow.Create(spec));
    }

    /// <summary>A hand-placed ENTITY (playlist/album/artist/show). No projected entry to join against here — the band
    /// draws straight from the item's own fallback title/art, exactly as a missing-entity retention row would, except
    /// this one is very much alive: <see cref="SidebarNavBandModel.RouteKeyOf"/> is the SAME uri-to-route mapping the
    /// pin scheme owns, so this tile navigates identically to the pane's own rows.</summary>
    Element EntityRow(SidebarItemSpec item, string route)
    {
        string uri = item.Key;
        string label = item.LabelOverride is { Length: > 0 } alias ? alias
            : item.FallbackTitle is { Length: > 0 } cached ? cached
            : SidebarPaneText.ShortUri(uri);
        string? target = SidebarNavBandModel.RouteKeyOf(item);
        bool selected = SidebarNavBandModel.SelectsRoute(item, route);

        var spec = new SidebarRowSpec
        {
            Key = item.Id,
            Label = label,
            Selected = selected,
            Enabled = true,
            Density = SidebarDensity.Cozy,
            Height = LibraryV3Metrics.NavRowHeight,
            Leading = SidebarCover.Art(SidebarPaneText.FallbackImage(item), null, uri, 32f,
                                       circular: item.EntityKind == SidebarEntityKind.Artist),
            OnClick = target is { Length: > 0 } r ? () => _session.Go(r, label) : null,
            Focusable = target is { Length: > 0 },
        };
        return SidebarEntityRow.Create(spec);
    }

    void PlayTrack(string uri)
    {
        if (uri.Length == 0) return;
        var player = _session.Acts?.Svc?.Player;
        if (player is null) return;
        _ = player.PlayTrackAsync(uri);
    }
}
