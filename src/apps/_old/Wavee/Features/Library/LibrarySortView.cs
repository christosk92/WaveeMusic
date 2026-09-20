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

// The library sort/view dropdown (WaveeMusic's LibrarySortViewPanel). Trigger = a compact pill showing the active sort
// label + direction chevron + the current view-mode glyph; tapping opens a flyout with "Sort by" rows (tap a different
// key to switch, tap the active key to flip direction), a "View as" toggle bank (Compact/Default × List/Grid), and a
// grid-size selector (grid modes only). All state is shared Signals owned by LibraryPage, so the list re-skins live.
sealed class LibrarySortView : Component
{
    readonly Signal<int> _sort, _view, _size;
    readonly Signal<bool> _desc;
    readonly bool _hasCreator, _hasRelease;

    public LibrarySortView(Signal<int> sort, Signal<bool> desc, Signal<int> view, Signal<int> size, bool hasCreator, bool hasRelease)
    { _sort = sort; _desc = desc; _view = view; _size = size; _hasCreator = hasCreator; _hasRelease = hasRelease; }

    public static string SortLabel(int k) => k switch
    {
        1 => Loc.Get(Strings.Library.Sort.RecentlyAdded),
        2 => Loc.Get(Strings.Library.Sort.Alphabetical),
        3 => Loc.Get(Strings.Library.Sort.Creator),
        4 => Loc.Get(Strings.Library.Sort.ReleaseDate),
        _ => Loc.Get(Strings.Library.Sort.Recents),
    };
    public static string ViewGlyph(int v) => v >= 2 ? Icons.ViewGrid : Icons.ViewList;

    public override Element Render()
    {
        var anchor = UseRef<NodeHandle>(default);
        var handle = UseRef<OverlayHandle?>(null);
        var svc = UseContext(Overlay.Service);

        int sort = _sort.Value; bool desc = _desc.Value; int view = _view.Value;   // subscribe → trigger reflects state

        void Toggle()
        {
            if (svc is null) return;
            if (handle.Value is { IsOpen: true } open) { open.Close(); return; }
            handle.Value = svc.Open(
                () => anchor.Value,
                () => Embed.Comp(() => new LibrarySortPanel(_sort, _desc, _view, _size, _hasCreator, _hasRelease)),
                FlyoutPlacement.BottomEdgeAlignedLeft,
                new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss, Chrome: PopupChrome.Popup)
                { ConstrainToRootBounds = false });
            handle.Value.ClosedAction = () => handle.Value = null;
        }

        return new BoxEl
        {
            Direction = 0, Height = 32f, AlignItems = FlexAlign.Center, Gap = 5f, Shrink = 0f,
            Padding = new Edges4(10f, 0f, 8f, 0f), Corners = CornerRadius4.All(Radii.Control),
            OnRealized = h => anchor.Value = h, OnClick = Toggle,
            Children =
            [
                Icon(Icons.Sort, 14f, Tok.TextSecondary),
                new TextEl(SortLabel(sort)) { Size = 14f, LineHeight = 20f, Weight = 600, Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                Icon(desc ? Icons.ChevronDown : Icons.ChevronUp, 12f, Tok.TextTertiary),
                new BoxEl { Width = 1f, Height = 16f, Fill = Tok.StrokeDividerDefault },
                Icon(ViewGlyph(view), 14f, Tok.TextSecondary),
            ],
        }.Interactive(Interaction.Subtle);
    }
}

// The flyout body — its own Component so the rows / toggles track the live signals (a static snapshot would stale out).
sealed class LibrarySortPanel : Component
{
    readonly Signal<int> _sort, _view, _size;
    readonly Signal<bool> _desc;
    readonly bool _hasCreator, _hasRelease;
    public LibrarySortPanel(Signal<int> sort, Signal<bool> desc, Signal<int> view, Signal<int> size, bool hasCreator, bool hasRelease)
    { _sort = sort; _desc = desc; _view = view; _size = size; _hasCreator = hasCreator; _hasRelease = hasRelease; }

    public override Element Render()
    {
        int sort = _sort.Value; bool desc = _desc.Value; int view = _view.Value; int size = _size.Value;   // subscribe

        var rows = new List<Element>(10) { Header(Loc.Get(Strings.Library.SortBy)) };
        rows.Add(SortRow(0, sort, desc));
        rows.Add(SortRow(1, sort, desc));
        rows.Add(SortRow(2, sort, desc));
        if (_hasCreator) rows.Add(SortRow(3, sort, desc));
        if (_hasRelease) rows.Add(SortRow(4, sort, desc));
        rows.Add(Divider());
        rows.Add(Header(Loc.Get(Strings.Library.ViewAs)));
        rows.Add(ViewToggles(view));
        if (view >= 2)
        {
            rows.Add(Header(Loc.Get(Strings.Library.Size)));
            rows.Add(SelectorBar.Create(["S", "M", "L"], _size));
        }

        // PopupChrome.Popup supplies the one WinUI FlyoutPresenter acrylic, stroke, corners, and shadow.
        return new BoxEl
        {
            Direction = 1, Gap = 1f, MinWidth = 230f, Padding = new Edges4(Spacing.XS, Spacing.XS, Spacing.XS, Spacing.XS),
            Children = rows.ToArray(),
        };
    }

    Element SortRow(int key, int sort, bool desc)
    {
        bool active = sort == key;
        return new BoxEl
        {
            Direction = 0, Height = 32f, AlignItems = FlexAlign.Center, Gap = Spacing.S,
            Padding = new Edges4(10f, 0f, 8f, 0f), Corners = CornerRadius4.All(5f),
            OnClick = () => { if (_sort.Peek() == key) _desc.Value = !_desc.Peek(); else { _sort.Value = key; _desc.Value = false; } },
            Children =
            [
                new TextEl(LibrarySortView.SortLabel(key)) { Size = 14f, Weight = (ushort)(active ? 600 : 400), Color = active ? Tok.AccentTextPrimary : Tok.TextPrimary, Grow = 1f, Basis = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                active ? Icon(desc ? Icons.ChevronDown : Icons.ChevronUp, 11f, Tok.AccentTextPrimary) : new BoxEl(),
                active ? Icon(Icons.Check, 12f, Tok.AccentTextPrimary) : new BoxEl { Width = 12f },
            ],
        }.Interactive(Interaction.Subtle);
    }

    Element ViewToggles(int view)
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
            int idx = i; bool on = view == i;
            cells[i] = new BoxEl
            {
                Width = 40f, Height = 30f, Grow = 1f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Corners = CornerRadius4.All(5f),
                Fill = on ? Tok.AccentDefault : Tok.FillSubtleSecondary, HoverFill = on ? Tok.AccentSecondary : Tok.FillSubtleTertiary,
                OnClick = () => _view.Value = idx,
                Children = [Icon(defs[i].Glyph, defs[i].Size, on ? Tok.TextOnAccentPrimary : Tok.TextSecondary)],
            };
        }
        return new BoxEl { Direction = 0, Gap = 4f, Padding = new Edges4(2f, 2f, 2f, 4f), Children = cells };
    }

    static Element Header(string t) => new BoxEl { Padding = new Edges4(Spacing.S, Spacing.XS, Spacing.S, Spacing.XXS), Children = [WaveeType.Eyebrow(t) with { Color = Tok.TextTertiary }] };
    static Element Divider() => new BoxEl { Height = 1f, Fill = Tok.StrokeDividerDefault, Margin = new Edges4(4f, 4f, 4f, 4f) };
}
