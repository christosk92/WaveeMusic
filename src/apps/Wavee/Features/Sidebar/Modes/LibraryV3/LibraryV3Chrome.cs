using System;
using System.Collections.Generic;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

/// <summary>
/// §3.2.2's fixed vertical stack — V3's CHROME, mounted above the pane's scroll surface through
/// <c>SidebarPaneConfig.Head</c>: header band · toolbar (search + sort/view) · the ONE filter-chip rail · the drill-in
/// breadcrumb · the retry banner · the actionable empty state.
///
/// <para>The qualifier used to be a second chip band here; it is now a segment that fuses into the selected facet's pill
/// inside <c>LibraryV3Chips</c> (the <c>HomeFacetChips</c> grammar), which is why this band list is one shorter than
/// §3.2.2's original stack.</para>
///
/// <para>R3.0.3 — this is everything V3 owns that is NOT the document. The content itself is planned and virtualized by the
/// ONE <c>SidebarPane</c>, so nothing here draws a row, a tile or a separator between rows; and because it is its own
/// component, opening the search field or flipping a chip re-renders ~120 DIP of chrome instead of the pane.</para>
///
/// <para>WHY THE EMPTY STATE LIVES HERE and not in the pane. §3.2.10's three empty states are ACTIONABLE — "clear search",
/// "clear filter", "create playlist" — and they name the query. The shared renderer's empty row is deliberately a quiet
/// one-line hint (R3.1.6) with no verb and no access to V3's search text, so the library section is authored
/// <c>EmptyBehavior.HideBody</c> and V3 keeps its own states. There is therefore exactly ONE empty message on screen.</para>
/// </summary>
sealed class LibraryV3Chrome : Component
{
    /// <summary>Folder navigation is <c>MotionTok.ConnectedFly</c> (Revision 2's motion table).</summary>
    static readonly LayoutTransition BreadcrumbFly = new(
        TransitionChannels.Position | TransitionChannels.Opacity, MotionTok.ConnectedFly.ToDynamics(),
        Enter: new EnterExit(Dx: 12f, Opacity: 0f, Active: true),
        Exit: new EnterExit(Dx: 12f, Opacity: 0f, Active: true));

    readonly LibraryV3Session _session;

    public LibraryV3Chrome(LibraryV3Session session) => _session = session;

    public override Element Render()
    {
        var prefs = UseContext(SidebarPreferences.Slot);
        // The state read SUBSCRIBES this component to every V3 signal the pane's document is a function of, so the
        // breadcrumb, the banner and the empty state can never disagree with the rows below them.
        var state = _session.ReadState();
        int drillVersion = _session.DrillVersion.Value;

        // Revision 2: a drilled-into folder that vanished from the projection (unfollowed, filtered away, library reloaded)
        // pops the stack rather than showing a level whose breadcrumb points at nothing. An EFFECT, never the render body —
        // a pop writes a signal.
        bool missing = _session.View.DrillTargetMissing;
        UseLayoutEffect(() =>
        {
            if (missing) _session.PopFolder();
        }, DepKey.From(missing ? 1 : 0, drillVersion));

        var bands = new List<Element>(7)
        {
            Embed.Comp(() => new LibraryV3Header(_session)) with { Key = "v3-header" },
            Embed.Comp(() => new LibraryV3Toolbar(_session)) with { Key = "v3-toolbar" },
            Embed.Comp(() => new LibraryV3Chips(_session)) with { Key = "v3-chips" },
            // Spans the CONTENT LANE, exactly like the plan's own SidebarSectionHeader.ExplicitDivider — a rule that
            // stopped 6 DIP short of the rows it separates is the same ragged edge in hairline form.
            Divider() with
            {
                Key = "v3-chrome-rule",
                Margin = new Edges4(SidebarPaneMetrics.ContentLane, 4f, SidebarPaneMetrics.ContentLaneEnd, 4f),
            },
        };

        if (state.Drilled) bands.Add(Breadcrumb());

        if (prefs is { } p)
        {
            _ = p.Entries.Version.Value;                       // subscribe: state/count move with the projection
            var load = p.Entries.State;
            bool anyPending = p.Entries.AnyContributingKindPending;
            int pinBand = state.PinsBandVisible ? p.Entries.PinCount : 0;
            int rows = _session.View.Count + pinBand;

            // §3.2.10's rule this file exists to keep: loaded content is NEVER blanked. A failure WITH rows present is a
            // one-line retry banner above them; only a failure with nothing to show takes the pane.
            if (load == LoadState.Failed && rows > 0) bands.Add(ErrorBanner(p));
            else if (rows == 0 && !anyPending && load != LoadState.Pending) bands.Add(EmptyBand(p, load, in state));
        }

        return new BoxEl
        {
            // NO horizontal padding here — each band pads itself to SidebarPaneMetrics.BandInset (the ONE content lane),
            // because the chip rail's inset has to live INSIDE its scroller and a shared wrapper padding would clip it.
            // Only the top air belongs here (the landed ExpandedBody's (0,8,0,8), minus the bottom padding PanePad supplies).
            Direction = 1, Shrink = 0f, Padding = new Edges4(0f, 8f, 0f, 0f),
            Children = [.. bands],
        };
    }

    // ── the drill-in breadcrumb (Revision 2) ──────────────────────────────────────────────────────────────────────────

    /// <summary>Back + the current level's name. The BACK target's own label is the button's accessible name, so the control
    /// always says where it goes and no new loc key is needed.</summary>
    Element Breadcrumb() => new BoxEl
    {
        Key = "v3-breadcrumb",
        Direction = 0, Height = LibraryV3Metrics.BreadcrumbHeight, AlignItems = FlexAlign.Center, Gap = 4f,
        // OPTICAL lane alignment: the leading affordance is a 24-DIP box around a 12-DIP glyph, so the BOX starts 6 DIP
        // before the lane to put the GLYPH on it (a row gets the same optics for free — its 16-DIP glyph sits in a
        // 6-DIP inset). This is the lane expressed for a centred box, NOT a second ad-hoc inset: the old bare 2 put the
        // back arrow at 8, one lane short of every row below it.
        Padding = new Edges4(SidebarPaneMetrics.ContentLane - 6f, 0f, SidebarPaneMetrics.ContentLaneEnd, 0f),
        Animate = BreadcrumbFly,
        Children =
        [
            ToolTip.Wrap(new BoxEl
            {
                Width = 24f, Height = 24f, Shrink = 0f,
                AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Corners = Radii.ControlAll,
                Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand,
                OnClick = _session.PopFolder,
                Children = [Icon(Icons.Back, 12f, Tok.TextSecondary)],
            }.Interactive(Interaction.Subtle), _session.ParentName),
            new TextEl(_session.CurrentFolderName)
            {
                Size = 12f, Weight = 600, Color = Tok.TextSecondary,
                Grow = 1f, Basis = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
            },
        ],
    };

    // ── degraded states ───────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Everything that can leave the pane with zero rows, in §3.2.10's priority order. The caller has already
    /// excluded the pending cases: a warming library shows the pane's own skeleton rows, never an empty state.</summary>
    Element EmptyBand(SidebarPreferences prefs, LoadState load, in LibraryV3DocState state)
    {
        Element body;
        if (load == LoadState.Failed)
        {
            body = ErrorState.Build(prefs.Entries.Error, _session.Retry);
        }
        else if (state.Searching)
        {
            string q = prefs.V3Search.Peek();
            if (q.Length > 24) q = q.Substring(0, 24) + "…";
            // Compact throughout the sidebar: a 240-DIP rail is exactly what EmptyState.Compact exists for.
            body = EmptyState.Compact(Strings.Sidebar.V3.Empty.Search(q),
                                    Loc.Get(Strings.Sidebar.V3.Empty.SearchSub),
                                    Loc.Get(Strings.Sidebar.V3.ClearSearch), () => prefs.V3Search.SetIfChanged(""));
        }
        else if (state.Filter != (int)SidebarV3Filter.All)
        {
            // The qualifier case falls through to here using the FILTER label: the qualifier itself auto-clears (§3.2.4)
            // rather than persisting an invisible filter, so there is no separate empty-by-qualifier state to author.
            body = EmptyState.Compact(Strings.Sidebar.V3.Empty.Filter(LibraryV3Labels.Filter(state.Filter)),
                                    null,
                                    Loc.Get(Strings.Sidebar.V3.ClearFilter),
                                    () => prefs.SetV3Filter((int)SidebarV3Filter.All));
        }
        else
        {
            body = EmptyState.Compact(Loc.Get(Strings.Sidebar.V3.Empty.Library),
                                    Loc.Get(Strings.Sidebar.V3.Empty.LibrarySub),
                                    Loc.Get(Strings.Sidebar.CreatePlaylistTooltip), _session.CreatePlaylist);
        }

        // Shrink=0 and no Grow: the chrome sits ABOVE the (now empty) scroll surface, so the state reads as the content it
        // replaces instead of fighting the list for the remaining height.
        return new BoxEl { Key = "v3-empty", Direction = 1, Shrink = 0f, Children = [body] };
    }

    /// <summary>A one-line retry banner, used only when rows ARE present: the failure is reported without the pin band and
    /// every already-loaded row vanishing under an error page.</summary>
    Element ErrorBanner(SidebarPreferences prefs) => new BoxEl
    {
        Key = "v3-error-banner",
        Direction = 0, AlignItems = FlexAlign.Center, Gap = 8f, Shrink = 0f,
        // CARD family, not a band: the plate's EDGE takes the pane edge (the same x a row's fill plate starts at) and
        // its content sits one card padding inside — identical to SidebarPaneSlot.Card / Prompt inside the list. Audited
        // against the content lane deliberately: a card's own border is what the eye aligns on, not its text.
        Padding = new Edges4(8f, 6f, 8f, 6f),
        Margin = new Edges4(SidebarRowGeometry.PaneEdge, 0f, SidebarRowGeometry.PaneEdge, 4f),
        Corners = Radii.ControlAll, Fill = Tok.FillSubtleSecondary,
        Children =
        [
            Icon(Icons.StatusWarning, 12f, Tok.TextSecondary),
            new TextEl(Loc.Get(Strings.Common.ErrorTitle))
            {
                Size = 12f, Color = Tok.TextSecondary, Grow = 1f, Basis = 0f, MaxLines = 1,
                Trim = TextTrim.CharacterEllipsis,
            },
            new BoxEl
            {
                Padding = new Edges4(6f, 2f, 6f, 2f), Corners = Radii.ControlAll,
                Role = AutomationRole.Button, Cursor = CursorId.Hand, Focusable = true,
                HoverFill = Tok.FillSubtleTertiary,
                OnClick = _session.Retry,
                Children =
                [
                    new TextEl(Loc.Get(Strings.Common.Retry)) { Size = 12f, Weight = 600, Color = Tok.AccentTextPrimary },
                ],
            },
        ],
    };
}
