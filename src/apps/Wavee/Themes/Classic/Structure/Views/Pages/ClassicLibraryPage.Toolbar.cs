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

    /// <summary>The master column's head (W1): the page title (PageHero, one line) with the live count beside it, the word rail + the view toggle, then the filter.</summary>
    static Element Toolbar(LibraryPageProps p, PageCache c, bool title)
    {
        // Podcasts' row-1 words ARE Followed shows / Your Episodes (A2 plan §3.5) — the shows sort rail only makes sense while browsing shows, and the mode switch belongs in the SAME
        // row Albums uses for its own words, not a wrapper of its own. Podcasts' mode words own their row: sharing it with the view toggle scrolled "Your Episodes" out of a narrow
        // column. The filter and the view toggle only browse SHOWS, so they share the next row and Your Episodes (which has neither) drops it.
        c.Picker ??= new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S, MinWidth = 0f,
            Children = p.IsPodcasts
                ? [Controls.Words.Rail([new(Loc.Bind(Strings.Podcast.Reader.FollowedShows)), new(Loc.Bind(Strings.Podcast.Reader.YourEpisodes))], p.PodcastsMode(), fill: true)]
                : [ClassicLibraryRails.WordRail(p.Entity, p.Sort(), p.Desc()), ViewToggle(p.View(), p.Size())],
        };
        Element? filter = p.SavedEpisodesMode
            ? null
            : AutoSuggestBox.Create(s_noSuggest, Loc.Get(Strings.Library.Filter), text: p.Filter(), queryIcon: Icons.Search, grow: 1f,
                maxFillWidth: ClassicLibraryRules.FilterMaxFill, minHeight: ClassicLibraryRules.FilterMinHeight, cornerRadius: Radii.Control);
        if (p.IsPodcasts && filter is not null)
            filter = new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S, MinWidth = 0f,
                Children = [filter, ViewToggle(p.View(), p.Size())],
            };
        // The two arms differ in CHILD COUNT (the collapsed one drops the title row), so they carry different keys: paired by ordinal they would have matched the title row against
        // `Picker` and `Picker` against the filter — and the count is a BOUND text (`CountText`) that only a mount wires. The two arms live under "lib:wide" and "lib:collapsed"
        // respectively, which already keeps them apart; this is the second lock.
        if (!title)
            return new BoxEl
            {
                Key = "lib:toolbar:compact",
                Direction = 1, Gap = Spacing.S, Shrink = 0f, Padding = new Edges4(Spacing.M, Spacing.M, Spacing.M, Spacing.S),
                Children = filter is null ? [c.Picker] : [c.Picker, filter],
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
        return new BoxEl
        {
            Key = "lib:toolbar",
            Direction = 1, Gap = Spacing.S, Shrink = 0f, Padding = new Edges4(Spacing.M, Spacing.L, Spacing.M, Spacing.S),
            Children = filter is null ? [titleRow, c.Picker] : [titleRow, c.Picker, filter],
        };
    }

    // ══ THE VIEW TOGGLE AND THE TRIMMED VIEW PANEL ═══════════════════════════════════════════════════════════════════

    /// <summary>The toggle beside the rail: list (1) / grid (3) glyphs, then "…" for the trimmed <see cref="ViewPanel"/> (the compact variants + S/M/L). View codes stay 0..3 and stay
    /// persisted; the list/grid glyphs keep whichever compactness the persisted code already carried.</summary>
    static Element ViewToggle(Signal<int> view, Signal<int> size) => Embed.Comp(() => new ViewToggleHost(view, size));

    sealed class ViewToggleHost(Signal<int> view, Signal<int> size) : Component
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
                    () => ViewPanel(view, size),
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
                    ViewIcon(Icons.ViewList, !User.IsGridView(v), () => view.Value = User.IsCompactView(view.Peek()) ? 0 : 1),
                    ViewIcon(Icons.ViewGrid, User.IsGridView(v), () => view.Value = User.IsCompactView(view.Peek()) ? 2 : 3),
                    ViewIcon(Icons.More, false, Flyout),
                ],
            };
        }

        static Element ViewIcon(string glyph, bool on, Action tap) => new BoxEl
        {
            Width = ClassicLibraryRules.ViewIconEdge, Height = ClassicLibraryRules.ViewIconEdge, Shrink = 0f,
            AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Corners = CornerRadius4.All(ClassicLibraryRules.ViewIconCorner), Fill = on ? Tok.FillSubtleTertiary : Tok.FillSubtleTransparent,
            Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand, OnClick = tap,
            Children = [Icon(glyph, ClassicLibraryRules.ViewIconGlyph, on ? Tok.TextPrimary : Tok.TextSecondary)],
        }.Interactive(Interaction.Subtle);
    }

    /// <summary>The old sort flyout minus its sort rows — the rail owns sorting now: "View as" + (grid only) "Size". The same rows, the same code; the <c>hasCreator</c>/<c>hasRelease</c>
    /// flags went with the deleted sort rows.</summary>
    static Element ViewPanel(Signal<int> view, Signal<int> size) => Embed.Comp(() => new ViewPanelHost(view, size));

    sealed class ViewPanelHost(Signal<int> view, Signal<int> size) : Component
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
                    Width = ClassicLibraryRules.ViewCellW, Height = ClassicLibraryRules.ViewCellH, Grow = 1f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
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
