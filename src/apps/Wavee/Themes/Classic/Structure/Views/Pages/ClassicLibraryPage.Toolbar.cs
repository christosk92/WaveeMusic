// ── Themes/Classic/Structure/Views/Pages/ClassicLibraryPage.Toolbar.cs — the library navigator's head (themes plan 6.1, T10c1) ──────────────────
//
// From `Entities\User.Page.Library.cs` (`Toolbar`) and `Entities\User.UI.cs` §3 (the view toggle and its trimmed panel): the page title with the live count beside it, the word
// rail (or Podcasts' mode words) and the view toggle, then the filter. The collapsed layout drops the title row — the crumb root names the kind. The count is a BOUND text, so a
// row landing re-fires one property instead of the toolbar. The rails themselves are `ClassicLibraryRails.cs`.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;

namespace Wavee.Themes.Classic;

internal static partial class ClassicLibraryPage
{
    static readonly string[] s_noSuggest = [];

    /// <summary>The master column's head (W1): the page title (PageHero, one line) with the live count beside it, the word rail + the view toggle, then the filter. At Compact
    /// (<c>C-Library-Compact</c>) the kind chips stand over the title, the sort is a button with a menu and every control is 40 tall; at Small (<c>C-Library-Small</c>) the sort is that button too, at the stock 32.</summary>
    static Element Toolbar(LibraryPageProps p, PageCache c)
    {
        var layout = c.Layout;
        // Podcasts' row-1 words ARE Followed shows / Your Episodes (A2 plan §3.5) — the shows sort rail only makes sense while browsing shows, and the mode switch belongs in the SAME
        // row Albums uses for its own words, not a wrapper of its own. Podcasts' mode words own their row: sharing it with the view toggle scrolled "Your Episodes" out of a narrow
        // column. The filter and the view toggle only browse SHOWS, so they share the next row and Your Episodes (which has neither) drops it. The Compact picker is its own cached
        // element (its controls are 40, not 32 / 28): the two never share a node.
        // The sort is a button (with its own menu) at Compact and at Small (C-Library-Small), except for the artists' list and Podcasts' mode words, whose rails the boards keep. The
        // Small button (32) is its own cached element: it never shares a node with the Compact one (40).
        bool sortButton = layout.SortButton && !p.IsPodcasts && p.Entity != EntityKind.Artist;
        Element picker = layout.Single ? c.PickerCompact ??= CompactPicker(p, layout)
            : sortButton ? c.PickerSort ??= CompactPicker(p, layout)
            : c.Picker ??= WidePicker(p, layout);
        // The sort button (Small) and the word rail (Standard, Huge) are different trees in ONE slot: a crossing between them swaps under the shared tier cross-fade (the entering
        // form fades in, the leaving one fades out as an orphan beside it) in a slot that RESERVES the row's height, so the head and the list under it never jump while the two overlap.
        // Only the kinds that can show either form swap (the artists' list and Podcasts keep one form at every tier).
        bool swaps = !p.IsPodcasts && p.Entity != EntityKind.Artist;
        picker = new BoxEl
        {
            Key = "lib:picker", Direction = 1, MinWidth = 0f, MinHeight = layout.ControlH, Layout = ClassicTierMotion.Shove,
            // The slot's ONE child is an unkeyed column that holds the keyed form: the engine reconciles a slot's lone child in place (same element type, key ignored), which would patch
            // the sort button into the word rail instead of swapping them; a children LIST matches by key, so the leaving form exits and the entering one enters.
            Children =
            [
                new BoxEl
                {
                    Direction = 1, MinWidth = 0f,
                    Children =
                    [
                        swaps
                            ? picker with
                            {
                                Key = layout.Single || sortButton ? "lib:picker:sort" : "lib:picker:rail",
                                Enter = ClassicTierMotion.SwapFade, Exit = ClassicTierMotion.SwapFade, Transition = ClassicTierMotion.SwapFadeMotion,
                            }
                            : picker,
                    ],
                },
            ],
        };
        Element? filter = p.SavedEpisodesMode
            ? null
            : AutoSuggestBox.Create(s_noSuggest, Loc.Get(Strings.Library.Filter), text: p.Filter(), queryIcon: Icons.Search, grow: 1f,
                maxFillWidth: ClassicLibraryRules.FilterMaxFill, minHeight: layout.ControlH, cornerRadius: Radii.Control);
        if (p.IsPodcasts && filter is not null)
            filter = new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S, MinWidth = 0f,
                Children = [filter, ViewToggle(p.View(), p.Size(), layout.ViewCell)],
            };
        Element titleRow = new BoxEl
        {
            // FlexAlign.End, not Baseline: the engine's FlexAlign has no baseline arm (Foundation/LayoutTypes.cs), and END puts the 20-line count's bottom on the 52-line title's, which
            // is the prototype's reading. The title is the page title's one style (40/52 display, regular weight).
            Direction = 0, AlignItems = FlexAlign.End, Gap = Spacing.S, MinWidth = 0f,
            Children =
            [
                R.Type.SurfaceDisplay(p.Title) with { MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, Shrink = 1f, MinWidth = 0f },
                new TextEl(p.CountText)
                {
                    Size = ClassicLibraryRules.CountSize, LineHeight = ClassicLibraryRules.CountLine, Color = Tok.TextTertiary, Shrink = 0f,
                },
            ],
        };
        var rows = new List<Element>(4);
        if (layout.KindChips) rows.Add(KindChips(p, layout));
        // The head's rows glide when a crossing changes their gap (10 at Small, 8 at Standard) instead of snapping.
        rows.Add(titleRow with { Layout = ClassicTierMotion.Shove });
        rows.Add(picker);
        if (filter is not null) rows.Add(filter with { Layout = ClassicTierMotion.Shove });
        return new BoxEl
        {
            Key = "lib:toolbar",
            Direction = 1, Gap = layout.HeadGap, Shrink = 0f, Padding = layout.HeadPad,
            Children = rows.ToArray(),
        };
    }

    /// <summary>The wide head's second row: the kind's word rail (or Podcasts' mode words) and the view toggle.</summary>
    static Element WidePicker(LibraryPageProps p, ClassicLibraryLayout layout) => new BoxEl
    {
        Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S, MinWidth = 0f,
        Children = p.IsPodcasts
            ? [Controls.Words.Rail([new(Loc.Bind(Strings.Podcast.Reader.FollowedShows)), new(Loc.Bind(Strings.Podcast.Reader.YourEpisodes))], p.PodcastsMode(), fill: true)]
            : [ClassicLibraryRails.WordRail(p.Entity, p.Sort(), p.Desc()), ViewToggle(p.View(), p.Size(), layout.ViewCell)],
    };

    /// <summary>The Compact head's second row (<c>C-Library-Compact</c>): the sort as a 40 button with its menu and the 40 view cells; Podcasts' mode words as two 40 pills.</summary>
    static Element CompactPicker(LibraryPageProps p, ClassicLibraryLayout layout)
    {
        if (p.IsPodcasts)
            return Embed.Comp(() => new ModePills(p.PodcastsMode(), layout.ChipH));
        return new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.XXS, MinWidth = 0f,
            Children =
            [
                Embed.Comp(() => new SortButton(p.Entity, p.Sort(), p.Desc(), layout.ControlH)),
                new BoxEl { Grow = 1f, Basis = 0f, MinWidth = 0f },
                ViewToggle(p.View(), p.Size(), layout.ViewCell),
            ],
        };
    }

    // ══ THE COMPACT HEAD: KIND CHIPS, THE SORT BUTTON, THE MODE PILLS (C-Library-Compact) ═════════════════════════════

    /// <summary>The library's kinds as 40 pills in one scrolling line (the Compact head has no nav pane to switch kinds from): the page on screen filled with the accent, the rest the stock
    /// off chip face, the line's end fading where more wait.</summary>
    static Element KindChips(LibraryPageProps p, ClassicLibraryLayout layout)
    {
        var kinds = p.Kinds();
        var kids = new Element[kinds.Count];
        for (int i = 0; i < kids.Length; i++)
            kids[i] = Pill("kind:" + FormatCache.Int(i), kinds[i].Title, kinds[i].Selected, kinds[i].Open, layout.ChipH);
        // `Controls.ChipRail`'s line without its bottom margin (the head's own 12 gap spaces the rows): one scrolling row, its end fading where more chips wait.
        return ScrollView(new BoxEl
        {
            Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, MinWidth = 0f,
            Padding = new Edges4(Controls.ChipRailInsetX, 0f, Controls.ChipRailInsetX, 0f),
            Children = kids,
        }, horizontal: true) with
        {
            Key = "lib:kinds", Grow = 0f, Height = layout.ChipH, AutoEdgeFade = true, SuppressScrollBar = true, ScrollKey = "lib:kinds",
        };
    }

    /// <summary>One pill: a selected one is the accent plate with its label at 600 on the accent's ink, any other the stock link-chip face (the off ToggleButton plate). Both are
    /// <paramref name="height"/> tall with a full corner and 16 of padding, so a chip is a 40 target.</summary>
    static Element Pill(string key, string label, bool selected, Action onClick, float height)
    {
        if (!selected) return Controls.LinkChip(key, label, onClick, minHeight: height, cornerRadius: height / 2f, padX: Spacing.L);
        return new BoxEl
        {
            Key = key, Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Shrink = 0f, MinWidth = 0f, Height = height,
            Padding = new Edges4(Spacing.L, 0f, Spacing.L, 0f), Corners = CornerRadius4.All(height / 2f),
            Fill = Tok.AccentDefault, HoverFill = Tok.AccentSecondary, PressedFill = Tok.AccentTertiary, BrushTransitionMs = Design.Motion.Faster,
            Role = AutomationRole.Hyperlink, Focusable = true, Cursor = CursorId.Hand, OnClick = onClick,
            Children =
            [
                new TextEl(label)
                {
                    Size = ClassicLibraryRules.PillSize, LineHeight = ClassicLibraryRules.PillLine, Weight = 600, Color = Tok.TextOnAccentPrimary,
                    MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                },
            ],
        };
    }

    /// <summary>Podcasts' two mode words (Followed shows / Your Episodes) as 40 pills; the selection is the persisted signal, read here so the pills follow it.</summary>
    sealed class ModePills(Signal<int> mode, float height) : Component
    {
        public override Element Render()
        {
            int selected = mode.Value;
            return new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S, MinWidth = 0f,
                Children =
                [
                    Pill("mode:0", Loc.Get(Strings.Podcast.Reader.FollowedShows), selected == 0, () => mode.Value = 0, height),
                    Pill("mode:1", Loc.Get(Strings.Podcast.Reader.YourEpisodes), selected == 1, () => mode.Value = 1, height),
                ],
            };
        }
    }

    /// <summary>The sort as ONE button (the board's "recents ▾"): the active word at 13.5/600 and a chevron saying which way, opening a radio menu of the kind's words. Picking the
    /// active word flips the direction and any other resets it to ascending, exactly as the wide rail does. The button is <paramref name="height"/> tall (a 40 target).</summary>
    sealed class SortButton(EntityKind kind, Signal<int> sort, Signal<bool> desc, float height) : Component
    {
        NodeHandle _anchor;
        OverlayHandle? _handle;

        public override Element Render()
        {
            var overlay = UseContext(Overlay.Service);
            var current = LibraryWordRail.Clamp(kind, sort.Value);
            bool descending = desc.Value;

            void Flyout()
            {
                if (Controls.IsNullOverlay(overlay)) return;
                if (_handle is { IsOpen: true } open) { open.Close(); return; }
                _handle = overlay.Open(
                    () => _anchor,
                    () => MenuFlyout.Create(Items(), () => _handle?.Close(), size: OverlayFit.MenuSize),
                    FlyoutPlacement.BottomEdgeAlignedLeft,
                    new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss, Chrome: PopupChrome.Popup) { ConstrainToRootBounds = false });
                _handle.ClosedAction = () => _handle = null;
            }

            return new BoxEl
            {
                Key = "lib:sort", Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S - 2f, Shrink = 0f, Height = height,
                Padding = new Edges4(Spacing.S, 0f, Spacing.S, 0f), Margin = new Edges4(-Spacing.S, 0f, 0f, 0f),
                Corners = Radii.ControlAll, Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand,
                OnClick = Flyout, OnRealized = h => _anchor = h,
                Children =
                [
                    new TextEl(Loc.Get(LibraryWordRail.WordKey(current)))
                    {
                        Size = Controls.Words.Size, LineHeight = Controls.Words.Line, Weight = 600, Color = Tok.TextPrimary, MaxLines = 1, Wrap = TextWrap.NoWrap, Shrink = 0f,
                    },
                    Icon(descending ? Icons.ChevronUp : Icons.ChevronDown, ClassicLibraryRules.SortChevron, Tok.TextTertiary),
                ],
            }.Interactive(Interaction.Subtle);
        }

        IReadOnlyList<MenuFlyoutItem> Items()
        {
            var words = LibraryWordRail.WordsFor(kind);
            var items = new MenuFlyoutItem[words.Length];
            int active = (int)LibraryWordRail.Clamp(kind, sort.Peek());
            for (int i = 0; i < items.Length; i++)
            {
                var word = words[i];
                int code = (int)word;
                items[i] = MenuFlyoutItem.RadioItem(Loc.Get(LibraryWordRail.WordKey(word)), code == active, () =>
                {
                    if (code == (int)LibraryWordRail.Clamp(kind, sort.Peek())) desc.Value = !desc.Peek();
                    else { sort.Value = code; desc.Value = false; }
                });
            }
            return items;
        }
    }

    // ══ THE VIEW TOGGLE AND THE TRIMMED VIEW PANEL ═══════════════════════════════════════════════════════════════════

    /// <summary>The toggle beside the rail: list (1) / grid (3) glyphs, then "…" for the trimmed <see cref="ViewPanel"/> (the compact variants + S/M/L). View codes stay 0..3 and stay
    /// persisted; the list/grid glyphs keep whichever compactness the persisted code already carried.</summary>
    static Element ViewToggle(Signal<int> view, Signal<int> size, float edge) => Embed.Comp(() => new ViewToggleHost(view, size, edge));

    sealed class ViewToggleHost(Signal<int> view, Signal<int> size, float edge) : Component
    {
        NodeHandle _anchor;
        OverlayHandle? _handle;

        public override Element Render()
        {
            var overlay = UseContext(Overlay.Service);
            int v = view.Value;   // subscribe → the pressed glyph follows the persisted code

            void Flyout()
            {
                if (Controls.IsNullOverlay(overlay)) return;
                if (_handle is { IsOpen: true } open) { open.Close(); return; }
                _handle = overlay.Open(
                    () => _anchor,
                    () => ViewPanel(view, size, MathF.Max(ClassicLibraryRules.ViewCellH, edge)),
                    FlyoutPlacement.BottomEdgeAlignedRight,
                    new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss, Chrome: PopupChrome.Popup)
                    { ConstrainToRootBounds = false });
                _handle.ClosedAction = () => _handle = null;
            }

            return new BoxEl
            {
                Direction = 0, Gap = Spacing.XXS, AlignItems = FlexAlign.Center, Shrink = 0f, OnRealized = h => _anchor = h,
                Children =
                [
                    ViewIcon(Icons.ViewList, !User.IsGridView(v), () => view.Value = User.IsCompactView(view.Peek()) ? 0 : 1, edge),
                    ViewIcon(Icons.ViewGrid, User.IsGridView(v), () => view.Value = User.IsCompactView(view.Peek()) ? 2 : 3, edge),
                    ViewIcon(Icons.More, false, Flyout, edge),
                ],
            };
        }

        static Element ViewIcon(string glyph, bool on, Action tap, float edge) => new BoxEl
        {
            Width = edge, Height = edge, Shrink = 0f,
            AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Corners = CornerRadius4.All(ClassicLibraryRules.ViewIconCorner), Fill = on ? Tok.FillSubtleTertiary : Tok.FillSubtleTransparent,
            Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand, OnClick = tap,
            Children = [Icon(glyph, ClassicLibraryRules.ViewIconGlyph, on ? Tok.TextPrimary : Tok.TextSecondary)],
        }.Interactive(Interaction.Subtle);
    }

    /// <summary>The old sort flyout minus its sort rows — the rail owns sorting now: "View as" + (grid only) "Size". The same rows, the same code; the <c>hasCreator</c>/<c>hasRelease</c>
    /// flags went with the deleted sort rows.</summary>
    static Element ViewPanel(Signal<int> view, Signal<int> size, float cellH) => Embed.Comp(() => new ViewPanelHost(view, size, cellH));

    sealed class ViewPanelHost(Signal<int> view, Signal<int> size, float cellH) : Component
    {
        static readonly string[] SizeLabels = ["S", "M", "L"];

        public override Element Render()
        {
            int v = view.Value; _ = size.Value;   // subscribe
            // M1 (RC10 / D12): the Size header + bar used to be ADDED/REMOVED as `v` crossed list<->grid, so the OPEN flyout changed height under the cursor mid-click. It is now always in
            // the tree — same row count, same Gap, every time — and for a list view (v < 2, where S/M/L does nothing, D13) the wrapper is dimmed and taken out of hit-testing instead of
            // removed, so the popup's size never moves while it is open.
            BoxEl sizeBank = new()
            {
                Direction = 1, Gap = 1f,
                Children = [PanelHeader(Loc.Get(Strings.Library.Size)), SelectorBar.Create(SizeLabels, size)],
            };
            if (!User.IsGridView(v)) sizeBank = sizeBank with { Opacity = ClassicLibraryRules.SizeBankDim, HitTestVisible = false };   // Opacity/HitTestVisible are BoxEl-only
            Element[] rows = [PanelHeader(Loc.Get(Strings.Library.ViewAs)), ViewToggles(v), sizeBank];
            // PopupChrome.Popup supplies the one WinUI FlyoutPresenter acrylic, stroke, corners and shadow.
            return new BoxEl { Direction = 1, Gap = 1f, MinWidth = ClassicLibraryRules.PanelMinWidth, Padding = Edges4.All(Spacing.XS), Children = rows };
        }

        // Glyph-only cells (ch 15 §3: the four view labels are resolved by 0.2.9 and never drawn).
        Element ViewToggles(int v)
        {
            var glyphSizes = ClassicLibraryRules.ViewGlyphSizes;
            var cells = new Element[glyphSizes.Length];
            for (int i = 0; i < cells.Length; i++)
            {
                int idx = i; bool on = v == i;
                cells[i] = new BoxEl
                {
                    Width = ClassicLibraryRules.ViewCellW, Height = cellH, Grow = 1f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                    Corners = CornerRadius4.All(ClassicLibraryRules.ViewCellCorner),
                    Fill = on ? Tok.AccentDefault : Tok.FillSubtleSecondary, HoverFill = on ? Tok.AccentSecondary : Tok.FillSubtleTertiary,
                    Role = AutomationRole.Button, Cursor = CursorId.Hand,
                    OnClick = () => view.Value = idx,
                    Children = [Icon(User.ViewGlyph(i), glyphSizes[i], on ? Tok.TextOnAccentPrimary : Tok.TextSecondary)],
                };
            }
            return new BoxEl { Direction = 0, Gap = ClassicLibraryRules.ViewCellGap, Padding = ClassicLibraryRules.ViewCellsPad, Children = cells };
        }

        static Element PanelHeader(string t) => new BoxEl
        {
            Padding = new Edges4(Spacing.S, Spacing.XS, Spacing.S, Spacing.XXS),
            Children = [R.Type.Eyebrow(t) with { Color = Tok.TextTertiary }],
        };
    }
}
