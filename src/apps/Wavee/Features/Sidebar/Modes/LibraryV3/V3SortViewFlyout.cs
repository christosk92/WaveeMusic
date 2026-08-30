using System;
using System.Collections.Generic;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scene;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

/// <summary>
/// §3.2.6 — the sidebar's sort + view control: a compact trigger pill that opens a light-dismiss flyout with the five
/// sorts (× direction) and the four view densities.
///
/// <para>EXTRACTED from <c>LibrarySortView</c>/<c>LibrarySortPanel</c> rather than reused in place, and that file is left
/// untouched: the sidebar needs a 5th sort code (local Custom order), a narrower 28-DIP trigger that can collapse to
/// icon-only, and NO grid-size row (the sidebar's cell size is derived from the pane width, never chosen). The building
/// blocks — the pill's child order, the sort-row semantics, the 4-cell view bank's exact metrics — are copied verbatim so
/// the library page and the sidebar read as one control family.</para>
///
/// <para>Sort codes deliberately share <c>LibrarySortView</c>'s numbering for 0–3 (Recents · Recently added ·
/// Alphabetical · Creator) so <c>library.sort.*</c> is reused; <c>SidebarV3Sort.Custom</c> is 4 and is offered ONLY under
/// the Playlists filter. Direction means REVERSED, not descending: false = the sort's natural direction. Custom order
/// pins the direction off and renders no chevron — reversing a hand-authored order is meaningless.</para>
/// </summary>
sealed class V3SortViewTrigger : Component
{
    readonly IReadSignal<bool> _iconOnly;

    public V3SortViewTrigger(IReadSignal<bool> iconOnly) => _iconOnly = iconOnly;

    public override Element Render()
    {
        var prefs = UseContext(SidebarPreferences.Slot);
        var svc = UseContext(Overlay.Service);
        var anchor = UseRef<NodeHandle>(default);
        var handle = UseRef<OverlayHandle?>(null);

        int sort = prefs is null ? 0 : LibraryV3Metrics.NormalizeSort(prefs.V3Sort.Value);
        int view = prefs is null ? 1 : LibraryV3Metrics.NormalizeView(prefs.V3View.Value);
        bool desc = prefs?.V3Desc.Value ?? false;
        int filter = prefs is null ? 0 : LibraryV3Metrics.NormalizeFilter(prefs.V3Filter.Value);
        bool iconOnly = _iconOnly.Value;

        // §3.2.6's availability edge case: Custom order exists only under Playlists. Leaving the filter falls the sort
        // back to Recents AND PERSISTS it; returning to Playlists deliberately does NOT restore Custom (an implicit
        // re-entry into a reorder-enabled mode would be surprising).
        UseLayoutEffect(() =>
        {
            if (prefs is not { } pf) return;
            if (pf.V3Sort.Peek() == (int)SidebarV3Sort.Custom && filter != (int)SidebarV3Filter.Playlists)
                pf.SetV3Sort((int)SidebarV3Sort.Recents, false);
        }, DepKey.From(sort, filter));

        if (prefs is not { } p) return new BoxEl();

        void Toggle()
        {
            if (svc is null) return;
            if (handle.Value is { IsOpen: true } openHandle) { openHandle.Close(); return; }
            handle.Value = svc.Open(
                () => anchor.Value,
                // prefs is passed EXPLICITLY, not read from context: the flyout body mounts under the OverlayHost, and a
                // popup subtree must not depend on where in the tree the provider happens to sit.
                () => Embed.Comp(() => new V3SortViewPanel(p)),
                FlyoutPlacement.BottomEdgeAlignedLeft,
                new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss, Chrome: PopupChrome.Popup)
                { ConstrainToRootBounds = false });
            handle.Value.ClosedAction = () => handle.Value = null;
        }

        // The pill's child order is LibrarySortView's, verbatim: [sort glyph · label · direction · rule · view glyph].
        // Custom order drops the direction chevron (it has no meaningful inverse), and the icon-only form drops everything
        // but the sort glyph so the search field can take the row.
        bool showDirection = SidebarSort.SupportsDirection((SidebarV3Sort)sort);
        var kids = new List<Element>(5) { Icon(Icons.Sort, 14f, Tok.TextSecondary) };
        if (!iconOnly)
        {
            kids.Add(new TextEl(LibraryV3Labels.Sort(sort))
            {
                Size = 13f, Weight = 600, Color = Tok.TextSecondary, MaxLines = 1,
                Trim = TextTrim.CharacterEllipsis, Shrink = 1f,
            });
            if (showDirection) kids.Add(Icon(desc ? Icons.ChevronUp : Icons.ChevronDown, 10f, Tok.TextTertiary));
            kids.Add(new BoxEl { Width = 1f, Height = 16f, Fill = Tok.StrokeDividerDefault, Shrink = 0f });
            kids.Add(Icon(LibraryV3Labels.ViewGlyph(view), 14f, Tok.TextSecondary));
        }

        var pill = new BoxEl
        {
            Direction = 0, Height = 28f, AlignItems = FlexAlign.Center, Gap = 5f, Shrink = 0f,
            Width = iconOnly ? 28f : float.NaN,
            Justify = iconOnly ? FlexJustify.Center : FlexJustify.Start,
            Padding = iconOnly ? new Edges4(0f, 0f, 0f, 0f) : new Edges4(10f, 0f, 8f, 0f),
            Corners = Radii.ControlAll,
            Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand,
            OnRealized = h => anchor.Value = h,
            OnClick = Toggle,
            Children = [.. kids],
        }.Interactive(Interaction.Subtle);

        // Icon-only has no visible label, so the tooltip carries the accessible name; the labelled form gets the same
        // tooltip because "Recents · List" alone does not say what tapping it does.
        string name = Loc.Get(Strings.Sidebar.A11y.SortView);
        if (desc && showDirection) name = name + " · " + Loc.Get(Strings.Sidebar.V3.Sort.Reversed);
        return ToolTip.Wrap(pill, name);
    }
}

/// <summary>The flyout body — its OWN component so every row tracks the live signals (a snapshot captured by the opener
/// would stale out the moment a row is tapped and the panel stays open).</summary>
sealed class V3SortViewPanel : Component
{
    readonly SidebarPreferences _prefs;

    public V3SortViewPanel(SidebarPreferences prefs) => _prefs = prefs;

    public override Element Render()
    {
        var prefs = _prefs;

        int sort = LibraryV3Metrics.NormalizeSort(prefs.V3Sort.Value);
        bool desc = prefs.V3Desc.Value;
        int view = LibraryV3Metrics.NormalizeView(prefs.V3View.Value);
        int filter = LibraryV3Metrics.NormalizeFilter(prefs.V3Filter.Value);
        bool customAvailable = filter == (int)SidebarV3Filter.Playlists;

        var rows = new List<Element>(10) { Header(Loc.Get(Strings.Library.SortBy)) };
        rows.Add(SortRow(prefs, (int)SidebarV3Sort.Recents, sort, desc));
        rows.Add(SortRow(prefs, (int)SidebarV3Sort.RecentlyAdded, sort, desc));
        rows.Add(SortRow(prefs, (int)SidebarV3Sort.Alphabetical, sort, desc));
        rows.Add(SortRow(prefs, (int)SidebarV3Sort.Creator, sort, desc));
        if (customAvailable) rows.Add(SortRow(prefs, (int)SidebarV3Sort.Custom, sort, desc));
        rows.Add(PanelDivider());
        rows.Add(Header(Loc.Get(Strings.Library.ViewAs)));
        rows.Add(ViewToggles(prefs, view));
        // NO size row: the sidebar's grid cell size is DERIVED from the pane width (§3.2.8), so a S/M/L selector would be
        // a control with nothing to control.

        return new BoxEl
        {
            Direction = 1, Gap = 1f, MinWidth = 220f,
            Padding = new Edges4(Spacing.XS, Spacing.XS, Spacing.XS, Spacing.XS),
            Children = [.. rows],
        };
    }

    /// <summary>Tapping the ACTIVE sort flips the direction; tapping a DIFFERENT sort selects it and resets the direction
    /// — verbatim <c>LibrarySortPanel.SortRow</c>. Custom order is the one documented exception: it pins the direction off
    /// and shows no chevron.</summary>
    static Element SortRow(SidebarPreferences prefs, int key, int sort, bool desc)
    {
        bool active = sort == key;
        bool directional = SidebarSort.SupportsDirection((SidebarV3Sort)key);
        return new BoxEl
        {
            Key = "v3sort" + key,
            Direction = 0, Height = 32f, AlignItems = FlexAlign.Center, Gap = Spacing.S,
            Padding = new Edges4(10f, 0f, 8f, 0f), Corners = CornerRadius4.All(5f),
            Role = AutomationRole.RadioButton, Cursor = CursorId.Hand, Focusable = true,
            OnClick = () =>
            {
                if (prefs.V3Sort.Peek() == key) prefs.SetV3Sort(key, directional && !prefs.V3Desc.Peek());
                else prefs.SetV3Sort(key, false);
            },
            Children =
            [
                new TextEl(LibraryV3Labels.Sort(key))
                {
                    Size = 14f, Weight = (ushort)(active ? 600 : 400),
                    Color = active ? Tok.AccentTextPrimary : Tok.TextPrimary,
                    Grow = 1f, Basis = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                },
                active && directional
                    ? Icon(desc ? Icons.ChevronUp : Icons.ChevronDown, 11f, Tok.AccentTextPrimary)
                    : new BoxEl(),
                active ? Icon(Icons.Check, 12f, Tok.AccentTextPrimary) : new BoxEl { Width = 12f },
            ],
        }.Interactive(Interaction.Subtle);
    }

    /// <summary>The 4-cell view bank — <c>LibrarySortPanel.ViewToggles</c>'s metrics exactly (height 30, radius 5, accent
    /// fill when on, glyphs ViewList 14 / ViewList 16 / ViewGrid 12 / ViewGrid 15).</summary>
    static Element ViewToggles(SidebarPreferences prefs, int view)
    {
        var defs = new (string Glyph, float Size, string Label)[]
        {
            (Icons.ViewList, 14f, Loc.Get(Strings.Library.View.CompactList)),
            (Icons.ViewList, 16f, Loc.Get(Strings.Library.View.List)),
            (Icons.ViewGrid, 12f, Loc.Get(Strings.Library.View.CompactGrid)),
            (Icons.ViewGrid, 15f, Loc.Get(Strings.Library.View.Grid)),
        };
        var cells = new Element[4];
        for (int i = 0; i < 4; i++)
        {
            int idx = i;
            bool on = view == i;
            var cell = new BoxEl
            {
                Key = "v3view" + i,
                Width = 40f, Height = 30f, Grow = 1f,
                AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Corners = CornerRadius4.All(5f),
                Fill = on ? Tok.AccentDefault : Tok.FillSubtleSecondary,
                HoverFill = on ? Tok.AccentSecondary : Tok.FillSubtleTertiary,
                BrushTransitionMs = WaveeMotion.Fast,
                Role = AutomationRole.RadioButton, Cursor = CursorId.Hand, Focusable = true,
                OnClick = () => prefs.SetV3View(idx),
                Children = [Icon(defs[i].Glyph, defs[i].Size, on ? Tok.TextOnAccentPrimary : Tok.TextSecondary)],
            };
            // An icon-only cell has no visible label, so its accessible name is its tooltip (the app-wide convention).
            cells[i] = ToolTip.Wrap(cell, defs[i].Label);
        }
        return new BoxEl { Direction = 0, Gap = 4f, Padding = new Edges4(2f, 2f, 2f, 4f), Children = cells };
    }

    static Element Header(string t) => new BoxEl
    {
        Padding = new Edges4(8f, 6f, 8f, 2f),
        Children = [WaveeType.Eyebrow(t) with { Color = Tok.TextTertiary }],
    };

    static Element PanelDivider() => new BoxEl
    {
        Height = 1f, Fill = Tok.StrokeDividerDefault, Margin = new Edges4(4f, 4f, 4f, 4f),
    };
}
