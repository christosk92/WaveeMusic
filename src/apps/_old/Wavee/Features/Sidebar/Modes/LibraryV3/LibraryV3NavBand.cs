using System;
using System.Collections.Generic;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scene;
using FluentGpu.Scroll;
using FluentGpu.Signals;
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

    // ── W4 — hover-revealed pager chevrons for the destination word rail. `LibraryV3NavBand` is mounted ONCE per
    // pane (`Embed.Comp` in `LibraryV3Chrome`, component-props-freeze contract), so these fields persist across
    // renders exactly like `PagedShelfCore`'s own scroll-state fields — no hooks needed for them.
    //
    // The rail's own viewport handle (`ScrollEl.OnRealized`, the same seam `LyricsView.cs`'s follow-scroll captures)
    // is the ONE thing a chevron click needs: it reads the LIVE offset/extent off it and drives
    // `FluentGpu.Scroll.ScrollIntoView.ScrollTo` — the engine's one programmatic scroll seam. A plain `ScrollView`
    // has no `ItemsViewController` (that seam is `ItemsView`-only, `DetailTracks.cs`'s `ScrollBy`), so this
    // NodeHandle is the equivalent handle for a bare scroller.
    NodeHandle _railViewport = NodeHandle.Null;
    // Whether there is more rail content to reach in each direction — read off the rail's own `ScrollEl` geometry
    // (`OnScrollGeometryChanged`, see `DestinationRail`). Drives BOTH the conditional edge fade's `EdgeMask` and the
    // chevrons' visibility off the SAME two booleans: a direction with nothing to scroll to gets no fade and a
    // HIDDEN chevron, never a dimmed one (rule #4). Signals (not plain bools) so a change re-renders the band.
    readonly Signal<bool> _railCanScrollLeft = new(false);
    readonly Signal<bool> _railCanScrollRight = new(false);

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
        // NOTE: no early-out on an empty band. The user can empty the shortcut band on purpose, but the
        // destination strip below is not part of it and must still render.

        string route = _session.Route.Value.Name;
        var registry = UseContext(WaveeExtensionRegistry.Slot) ?? _session.Acts?.Extensions;

        var kids = new List<Element>(items.Count + 2);

        // The five fixed library destinations are NOT top-bar items and never were: they are app destinations, the same
        // five Classic renders in its own "Your Library" section. V3 draws them here as ONE compact strip, always,
        // independent of whatever the user has put in the shortcut band. Five full-width rows spent ~200 DIP above the
        // fold and read as library CONTENT rather than as places to go — V3 exists to put the library first.
        // (docs/plans/wavee/library-v3-destinations-v2-mica.html, option A.)
        kids.Add(DestinationRail(route));

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

    /// <summary>The library destinations as ONE row of tiles — glyph, count, short label — instead of one full-width
    /// row each. Each tile grows equally, so the strip always spans the content lane exactly and never leaves a ragged
    /// gap on the right. Below <see cref="LibraryV3Metrics.DestinationLabelW"/> per tile the labels drop and the strip
    /// becomes five glyphs, which is what keeps it usable at the 180-DIP pane floor.</summary>
    /// <summary>The library destinations as a row of WORDS — no boxes, no icon-only tiles. 13.5px type on a 30-DIP
    /// band, a 2-DIP accent underline under the active one, the count on the active word only, and a horizontal scroll
    /// whose clipped last word peeks past a fade.
    ///
    /// <para>This replaced a strip of five equal icon tiles, and the research is one-sided about why. Nielsen Norman:
    /// "Icon labels should be visible at all times, without any interaction from the user" — only a handful of glyphs
    /// (home, print, search) have near-universal recognition, and Albums-vs-Artists-vs-Podcasts is exactly the
    /// ambiguous case. The tiles dropped their labels below ~270 DIP of pane, i.e. at the default width, leaving five
    /// bare glyphs. A sweep for prior art found NO shipped desktop music app that puts an icon-tile strip for library
    /// destinations in a persistent sidebar. And Microsoft's own NavigationView guidance recommends TOP navigation
    /// for precisely this shape — "5 or fewer top-level categories", "show all navigation options on screen", "icons
    /// cannot clearly describe your categories".</para>
    ///
    /// <para>Words are also what makes this read as Zune rather than as a toolbar: Metro grew out of Zune's habit of
    /// large type "used as a primary navigation element, with words being cut off at the edge of the screen" — which
    /// is what the peeking scroll below is. It also cannot be confused with the filter chips two rows down: different
    /// size, different weight, no pill, and on the other side of the rule.</para></summary>
    Element DestinationRail(string route)
    {
        var store = UseContext(LibraryStore.Slot);
        var keys = SidebarShortcutsSection.LibraryDestinations;

        var words = new Element[keys.Length];
        for (int i = 0; i < keys.Length; i++)
        {
            string key = keys[i];
            var dest = ShellNav.Dest(key);
            bool on = string.Equals(route, key, StringComparison.Ordinal);

            var line = new List<Element>(2)
            {
                new TextEl(dest.Title)
                {
                    Size = LibraryV3Metrics.DestinationWordSize,
                    Weight = on ? (ushort)600 : (ushort)400,
                    Color = on ? Tok.TextPrimary : Tok.TextSecondary,
                    MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                },
            };
            // The count rides the ACTIVE word only. Five permanent counts is the "different types of InfoBadge in one
            // NavigationView" mismatch MS warns against — four of these are countable and Local files is not.
            if (on && DestinationCount(store, key) is { } n) line.Add(SidebarCounts.Number(n));

            words[i] = new BoxEl
            {
                Key = "v3-dest:" + key,
                Direction = 1, Shrink = 0f, Gap = 2f,
                AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Height = LibraryV3Metrics.DestinationRailH,
                Cursor = CursorId.Hand, Focusable = true, Role = AutomationRole.Button,
                OnClick = () => _session.Go(key, null),
                Children =
                [
                    new BoxEl
                    {
                        Direction = 0, Gap = 5f, AlignItems = FlexAlign.Center, Grow = 1f,
                        Children = [.. line],
                    },
                    // The 2-DIP accent underline — the one selected-state marker, always present (transparent when
                    // off) so the word never shifts as it becomes active. AlignSelf STRETCH is load-bearing: the
                    // column centres its children on the cross axis, and a child with no Width centred that way
                    // measures its content — which for an empty box is zero, so the underline never drew at all.
                    new BoxEl
                    {
                        AlignSelf = FlexAlign.Stretch,
                        Height = 2f, Corners = CornerRadius4.All(1f),
                        Fill = on ? Tok.AccentDefault : ColorF.Transparent,
                    },
                ],
            };
        }

        // Subscribed reads: a change re-renders the band so the fade mask and the chevrons' HoverOpacity actually
        // flip. Both consume the SAME two booleans — one source of truth for "is there more to reach", rather than
        // the engine's own AutoEdgeFade computing an independent (and here unverifiable-by-eye) second opinion.
        bool canLeft = _railCanScrollLeft.Value, canRight = _railCanScrollRight.Value;
        EdgeMask fadeMask = (canLeft, canRight) switch
        {
            (true, true) => EdgeMask.Horizontal,
            (true, false) => EdgeMask.Left,
            (false, true) => EdgeMask.Right,
            _ => EdgeMask.None,
        };

        Element scroller = Ui.ScrollView(new BoxEl
        {
            Direction = 0, Gap = LibraryV3Metrics.DestinationWordGap, AlignItems = FlexAlign.Center,
            // Start on the rows' own leading edge. The band pads to the bare PaneEdge so that each ROW's
            // internal inset lands its glyph at ArtX(0); the rail has no such inset of its own, so without
            // this it began a full lane to the left of every row beneath it.
            Padding = new Edges4(SidebarRowGeometry.ArtX(0) - SidebarRowGeometry.PaneEdge, 0f, 0f, 0f),
            Children = words,
        }, horizontal: true) with
        {
            ContentSized = true, Grow = 1f,
            // Change-only geometry observer — the escape hatch every paged shelf uses (`PagedShelfCore.PageScrollSync`)
            // to read the live offset/extent off the ScrollEl/ScrollPort surface. Projects to a 2-bit coarse key so
            // the action fires only on an ENABLE/DISABLE edge, never per-pixel, never per-frame.
            OnScrollGeometryChanged = (
                g => (g.OffsetX > 0.5f ? 1L : 0L) | (g.OffsetX < g.ContentW - g.ViewportW - 0.5f ? 2L : 0L),
                g =>
                {
                    _railCanScrollLeft.Value = g.OffsetX > 0.5f;
                    _railCanScrollRight.Value = g.OffsetX < g.ContentW - g.ViewportW - 0.5f;
                }),
            OnRealized = h => _railViewport = h,
        };

        return new BoxEl
        {
            Key = "v3-dest-rail",
            Height = LibraryV3Metrics.DestinationRailH, Shrink = 0f,
            Margin = new Edges4(0f, 0f, 0f, 2f),
            ClipToBounds = true,
            // Rule #3 — CONDITIONAL fade: the same explicit alpha-mask cue this rail always had (kept, rather than
            // switched to ScrollEl's own AutoEdgeFade, so the visual stays byte-identical to before this change),
            // now MASKED by the live canLeft/canRight truth instead of a hardcoded EdgeMask.Right. A fade with
            // nothing behind it is a lie.
            EdgeFade = fadeMask == EdgeMask.None ? null : new EdgeFadeSpec(fadeMask, LibraryV3Metrics.DestinationRailFade),
            // The HOVER SCOPE for the pager reveal (mirrors Rail.cs's header-hover scope exactly). The engine
            // reveals a HoverOpacity-bearing DESCENDANT on its container's hover; scoping it here is safe only
            // because nothing else under this box carries a reveal style — the words have no WhileHover/HoverOpacity
            // of their own, so only the chevrons (added below) ever respond. The no-op handlers are what make this
            // node interactive so the dispatcher publishes HoverWithin on it.
            OnHoverMove = static _ => { },
            OnPointerExit = static () => { },
            Children =
            [
                ZStack(scroller, RailChevrons(canLeft, canRight)) with { Grow = 1f },
            ],
        };
    }

    /// <summary>The two hover-revealed pager chevrons, overlaid at the rail's edges (over the fades) — a ZStack
    /// sibling of the scroller, PagedShelf's <c>ShelfPager.HoverEdge</c> shape. Each one is hidden outright (never
    /// dimmed) when its direction has nothing to scroll to (rule #4).</summary>
    Element RailChevrons(bool canLeft, bool canRight) => new BoxEl
    {
        Key = "v3-dest-chevrons",
        Direction = 0, Grow = 1f, AlignItems = FlexAlign.Center, Justify = FlexJustify.SpaceBetween,
        Children = [RailChevron(leading: true, canLeft), RailChevron(leading: false, canRight)],
    };

    /// <summary>One pager chevron. Rest opacity is 0 (not Rail.cs's quiet 0.7): this is a pure hover-over-the-rail
    /// affordance (rule #4), and — unlike Rail.cs's shelf chevrons — it carries no keyboard path of its own
    /// (<c>Focusable = false</c>; the words remain the rail's keyboard-reachable surface), so there is no
    /// hidden-but-tabbable trap to guard against by keeping it dimly visible at rest.</summary>
    Element RailChevron(bool leading, bool canScroll)
    {
        Action onClick = () => ScrollRailBy(leading ? -1 : 1);
        return new BoxEl
        {
            Key = leading ? "v3-dest-chevron-prev" : "v3-dest-chevron-next",
            Width = LibraryV3Metrics.DestinationRailChevronSize, Height = LibraryV3Metrics.DestinationRailChevronSize,
            Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Corners = CornerRadius4.All(LibraryV3Metrics.DestinationRailChevronSize / 2f),
            Fill = Tok.FillSubtleTransparent, HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary,
            Opacity = 0f, HoverOpacity = canScroll ? 1f : 0f,
            HoverDurationMs = WaveeMotion.Fast, HoverEasing = Easing.FluentDecelerate,
            Cursor = canScroll ? CursorId.Hand : null,
            OnClick = canScroll ? onClick : null,
            Focusable = false,
            Children = [Icon(leading ? Icons.ChevronLeft : Icons.ChevronRight,
                              LibraryV3Metrics.DestinationRailChevronGlyph, Tok.TextSecondary)],
        };
    }

    /// <summary>The ONE write path a chevron uses: read the rail viewport's LIVE offset/extent off the scene
    /// (copied out before the call — <c>ScrollIntoView.ScrollTo</c> takes its own ref, so holding one across it
    /// would alias, the same discipline <c>PagedShelfCore.CommitPendingSnap</c> follows) and post a glide through
    /// <c>ScrollIntoView.ScrollTo</c> — the engine's one programmatic scroll seam (<c>LyricsView.cs</c>'s
    /// follow-scroll is the other app-side call site). The step is a FRACTION of the live viewport width
    /// (<see cref="LibraryV3Metrics.DestinationRailPageStep"/>), not a fixed DIP figure, so it scales with the pane.</summary>
    void ScrollRailBy(int dir)
    {
        if (Context.Scene is not { } scene) return;
        var vp = _railViewport;
        if (vp.IsNull || !scene.IsLive(vp) || !scene.HasScroll(vp)) return;
        float offset, viewportW;
        {
            ref ScrollState sc = ref scene.ScrollRef(vp);
            offset = sc.OffsetX; viewportW = sc.ViewportW;
        }
        float target = offset + dir * viewportW * LibraryV3Metrics.DestinationRailPageStep;
        ScrollIntoView.ScrollTo(Context, vp, target, animate: !Motion.ReducedMotion);
    }

    /// <summary>The destination's library count, or null when it has none (Local files) or the stats are not ready.
    /// Mirrors <c>SidebarPaneSlot.CountBadge</c>'s mapping — the stats are already warmed by LibraryV3Sidebar.</summary>
    static int? DestinationCount(LibraryStore? store, string routeKey)
    {
        int index = routeKey switch { "albums" => 0, "artists" => 1, "liked" => 2, "podcasts" => 3, _ => -1 };
        if (index < 0 || store is null) return null;
        var stats = store.Stats;
        if ((Wavee.Backend.LoadState)stats.State.Value != Wavee.Backend.LoadState.Ready ||
            stats.Value.Value is not { } s) return null;
        return index switch { 0 => s.Albums, 1 => s.Artists, 2 => s.LikedSongs, _ => s.Podcasts };
    }

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
