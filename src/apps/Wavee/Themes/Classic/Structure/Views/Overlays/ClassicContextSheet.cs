// ── Themes/Classic/Structure/Views/Overlays/ClassicContextSheet.cs — Classic's `Overlay.ContextSheet` (themes plan 3.6, TC9) ─────────────
//
// What a context menu opens as at Compact (boards C-ContextMenu-Compact and C-Flyouts-Compact): a bottom sheet over the page's lower edge.
//
//   window-sized layer                       hit-test pass-through; the 48-DIP chrome row stays reachable above the scrim
//   ├─ scrim                                 black 40 %, a press closes the sheet
//   └─ card                                  FillSolidTertiary, top corners 8, as tall as its content up to the window less the chrome row
//        ├─ grabber                          a 32 x 4 pill in a 16-DIP zone
//        ├─ identity header                  56 tall: the tile (40), the title (14/600) over the subtitle (12), when the menu has one
//        └─ body (scrolls past the window)   the quick-action strip (a column of 64-tall cells, one per primary command, equal widths), a divider,
//                                            then 44-DIP rows: icon (16), label (14), a chevron for a sub-menu or a check for a toggle
//
// A sub-menu row opens its rows in place (a back row heads the page), so the sheet is one surface from the first press to the last. Every row
// closes the sheet FIRST, then acts (the engine menu's rule). The core decides when this is the menu (`ContextSheets`, the structure's
// `OverlayLayout.ContextSheet`) and what is in it; this draws it. The card slides up and fades on entry; the host fades the layer out on close.
//
// THE DRAG (the Compact overlays fix): the head (the grabber and the identity header) is the shared bottom-sheet drag every Classic sheet uses
// (`ClassicSheetDrag`): the card follows the pointer, the scrim fading with it; a release past the threshold slides it the rest of the way
// out and closes the sheet, short of it the card springs back. The drag moves the card's CARRIER (the column that seats it at the bottom), so the
// card's own entry and exit motion compose with it. The scrim and the card are no tab stops (the trap's first stop is the first command), and
// the card keeps the wheel off the page behind it.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;

namespace Wavee.Themes.Classic;

/// <summary>Wavee Classic's context sheet.</summary>
internal static class ClassicContextSheet
{
    /// <summary>The registered template of <see cref="ViewKeys.OverlayContextSheet"/>.</summary>
    public static Element Template(ContextSheetProps props, in ViewContext ctx) => Embed.Comp(props, static () => new SheetHost());

    /// <summary>The sheet's host: the window size, and the sub-menu page the sheet is showing.</summary>
    sealed class SheetHost : Component
    {
        /// <summary>The rows of the sub-menu page on show, with its title; null is the menu's own page.</summary>
        readonly Signal<(string Title, IReadOnlyList<MenuFlyoutItem> Rows)?> _page = new(null);

        readonly ClassicSheetDrag _drag = new();
        NodeHandle _card;
        Action<NodeHandle>? _cardRealized, _carrierRealized, _scrimRealized;

        public override Element Render()
        {
            var props = UseProps<ContextSheetProps>();
            Size2 vp = UseContextSignal(Viewport.Size).Value;
            var page = _page.Value;

            Action close = props.Close;
            float cardMax = MathF.Max(1f, vp.Height - ClassicSheetRules.ChromeRow);
            _drag.Bind(Context, UseContext(InputHooks.Current));
            _drag.Height ??= () => _card.IsNull || Context.Scene is not { } scene || !scene.IsLive(_card) ? 0f : scene.Bounds(_card).H;
            _drag.Dismiss = () =>
            {
                _drag.SlideOut(ClassicSheetRules.DismissMs);   // the rest of the way out, from where the drag left it
                close();
            };
            _cardRealized ??= h => _card = h;
            _carrierRealized ??= h => _drag.SheetNode = h;
            _scrimRealized ??= h => _drag.DimNode = h;

            var card = new BoxEl
            {
                Key = "context-sheet",
                Direction = 1, AlignSelf = FlexAlign.Stretch, MaxHeight = cardMax,
                Padding = new Edges4(ClassicSheetRules.SidePad, 0f, ClassicSheetRules.SidePad, ClassicSheetRules.BottomPad),
                Fill = Tok.FillSolidTertiary,
                Corners = new CornerRadius4(ClassicSheetRules.Radius, ClassicSheetRules.Radius, 0f, 0f),
                ClipToBounds = true,
                OnClick = static () => { },                    // the card is a hit target: a press on its inert area must not reach the scrim
                TabStop = false,                               // ... and no tab stop: the trap's first stop is the first command
                BlocksBackgroundScroll = true,                 // the page is a sibling under the overlay: the wheel on the card's inert parts stays here
                OnRealized = _cardRealized,
                Enter = new EnterExit(Dy: ClassicSheetRules.EnterDy, Opacity: 0f, Active: true),
                Exit = new EnterExit(Dy: ClassicSheetRules.EnterDy, Opacity: 0f, Active: true),
                Children = props.Content is { } content ? ContentPage(content, vp)
                    : page is { } sub ? SubPage(sub.Title, sub.Rows, close, vp) : MenuPage(props, close, vp),
            };

            return new BoxEl
            {
                Width = vp.Width, Height = vp.Height, Direction = 1, HitTestPassThrough = true,
                Children =
                [
                    new BoxEl { Height = ClassicSheetRules.ChromeRow, Shrink = 0f, HitTestVisible = false },
                    new BoxEl
                    {
                        Grow = 1f, MinHeight = 0f, ZStack = true,
                        Children =
                        [
                            new BoxEl
                            {
                                Fill = ClassicSheetRules.Scrim, OnClick = close, TabStop = false, BlocksBackgroundScroll = true, OnRealized = _scrimRealized,
                                Enter = new EnterExit(Opacity: 0f, Active: true), Exit = new EnterExit(Opacity: 0f, Active: true),
                            },
                            new BoxEl { Direction = 1, Justify = FlexJustify.End, HitTestPassThrough = true, OnRealized = _carrierRealized, Children = [card] },
                        ],
                    },
                ],
            };
        }

        // ── the menu's own page ──────────────────────────────────────────────────────────────────────────────────

        Element[] MenuPage(ContextSheetProps props, Action close, Size2 vp)
        {
            var kids = new List<Element>(3);
            float chrome = ClassicSheetRules.GrabberZone + ClassicSheetRules.BottomPad;
            if (props.Header is { } header)
            {
                kids.Add(Head(Grabber(), Header(header)));
                chrome += ClassicSheetRules.HeaderHeight;
            }
            else kids.Add(Head(Grabber()));
            var body = new List<Element>(props.Rows.Count + 2);
            if (props.Primary.Count > 0)
            {
                body.Add(Strip(props.Primary, close));
                body.Add(Divider());
            }
            AddRows(body, props.Rows, close, onSub: OpenSub);
            kids.Add(Body(body, vp, chrome));
            return kids.ToArray();
        }

        void OpenSub(MenuFlyoutItem item) => _page.Value = (item.Label, item.SubItems ?? []);

        // ── a card's own body (the list's filter card at Compact, board C-Flyouts-Compact, TC5) ─────────────────────────────

        Element[] ContentPage(Element content, Size2 vp)
            => [Head(Grabber()), Body([content], vp, ClassicSheetRules.GrabberZone + ClassicSheetRules.BottomPad)];

        // ── a sub-menu page ──────────────────────────────────────────────────────────────────────────────────────

        Element[] SubPage(string title, IReadOnlyList<MenuFlyoutItem> rows, Action close, Size2 vp)
        {
            var body = new List<Element>(rows.Count + 1)
            {
                Row(Icons.ChevronLeft, title, null, enabled: true, trail: null, onClick: () => _page.Value = null, key: "sheet-back", role: AutomationRole.Button),
                Divider(),
            };
            AddRows(body, rows, close, onSub: OpenSub);
            float chrome = ClassicSheetRules.GrabberZone + ClassicSheetRules.BottomPad;
            return [Head(Grabber()), Body(body, vp, chrome)];
        }

        // ── parts ────────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>The sheet's head (the grabber, and the identity header when the menu has one): the shared drag handle.</summary>
        Element Head(params Element[] rows) => _drag.Head(new BoxEl { Key = "sheet-head", Direction = 1, Shrink = 0f, Children = rows });

        static Element Grabber() => new BoxEl
        {
            Height = ClassicSheetRules.GrabberZone, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, HitTestVisible = false,
            Children =
            [
                new BoxEl
                {
                    Width = ClassicSheetRules.GrabberW, Height = ClassicSheetRules.GrabberH, Corners = CornerRadius4.All(ClassicSheetRules.GrabberRadius),
                    Fill = ClassicSheetRules.Grabber,
                },
            ],
        };

        static Element Header(in ContextMenuHeader header)
        {
            var kids = new List<Element>(2);
            if (header.Leading is { } leading)
                kids.Add(new BoxEl
                {
                    Width = ClassicSheetRules.HeaderTile, Height = ClassicSheetRules.HeaderTile, Shrink = 0f,
                    AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, HitTestVisible = false, Children = [leading],
                });
            kids.Add(new BoxEl
            {
                Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Justify = FlexJustify.Center, HitTestVisible = false,
                Children =
                [
                    new TextEl(header.Title)
                    {
                        Size = ClassicSheetRules.TitleSize, LineHeight = ClassicSheetRules.TitleLine, Weight = 600, Color = Tok.TextPrimary,
                        MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                    },
                    new TextEl(header.Subtitle ?? "")
                    {
                        Size = ClassicSheetRules.SubtitleSize, LineHeight = ClassicSheetRules.SubtitleLine, Color = Tok.TextSecondary,
                        MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                    },
                ],
            });
            return new BoxEl
            {
                Direction = 0, Height = ClassicSheetRules.HeaderHeight, Shrink = 0f, AlignItems = FlexAlign.Center, Gap = ClassicSheetRules.HeaderGap,
                Padding = new Edges4(ClassicSheetRules.HeaderPadX, 0f, ClassicSheetRules.HeaderPadX, 0f), Children = kids.ToArray(),
            };
        }

        /// <summary>The strip and rows in a scroll bounded by what is left of the window under the card's fixed parts.</summary>
        static Element Body(List<Element> body, Size2 vp, float fixedChrome)
        {
            float room = MathF.Max(1f, vp.Height - ClassicSheetRules.ChromeRow - fixedChrome);
            return new ScrollEl
            {
                ContentSized = true, MaxHeight = room, EdgeCues = ScrollEdgeCues.None,
                Content = new BoxEl { Direction = 1, Children = body.ToArray() },
            };
        }

        static Element Strip(IReadOnlyList<AppBarCommand> commands, Action close)
        {
            var cells = new List<Element>(commands.Count);
            for (int i = 0; i < commands.Count; i++)
            {
                var c = commands[i];
                if (c.Kind == AppBarCommandKind.Separator) continue;
                cells.Add(StripCell(c, close));
            }
            return new BoxEl { Direction = 0, Gap = ClassicSheetRules.StripGap, Padding = ClassicSheetRules.StripPadding, Children = cells.ToArray() };
        }

        static Element StripCell(AppBarCommand c, Action close)
        {
            bool on = c.Kind == AppBarCommandKind.ToggleButton && c.IsChecked;
            ColorF ink = !c.Enabled ? Tok.TextDisabled : on ? Tok.AccentTextPrimary : Tok.TextPrimary;
            IconRef icon = on && c.CheckedIcon is { } filled ? filled : c.Icon;
            var run = c.Invoke;
            return new BoxEl
            {
                Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Height = ClassicSheetRules.StripCell,
                AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Gap = ClassicSheetRules.StripCellGap,
                Corners = CornerRadius4.All(ClassicSheetRules.StripCellRadius), Fill = Tok.FillControlDefault,
                Role = AutomationRole.Button, Focusable = true, Key = "sheet-cell:" + c.Label,
                OnClick = c.Enabled ? () => { close(); run?.Invoke(); } : null,
                Children =
                [
                    GlyphOf(icon, ClassicSheetRules.StripIcon, ink),
                    new TextEl(c.Label) { Size = ClassicSheetRules.StripLabel, Color = ink, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                ],
            }.Interactive(Interaction.Subtle, isEnabled: c.Enabled);
        }

        static Element Divider() => new BoxEl { Height = 1f, Shrink = 0f, Margin = ClassicSheetRules.DividerMargin, Fill = Tok.StrokeDividerDefault, HitTestVisible = false };

        void AddRows(List<Element> into, IReadOnlyList<MenuFlyoutItem> rows, Action close, Action<MenuFlyoutItem> onSub)
        {
            for (int i = 0; i < rows.Count; i++)
            {
                var it = rows[i];
                if (it.IsSeparator)
                {
                    // Two dividers in a row, or one at an edge, are noise: draw it only between rows.
                    if (into.Count == 0 || i == rows.Count - 1) continue;
                    into.Add(Divider());
                    continue;
                }
                var item = it;
                string trailGlyph = item.Kind switch
                {
                    MenuItemKind.SubMenu => Icons.ChevronRight,
                    MenuItemKind.Toggle or MenuItemKind.Radio => item.IsChecked ? Icons.Check : "",
                    _ => "",
                };
                Action? click = !item.Enabled ? null : item.Kind == MenuItemKind.SubMenu
                    ? () => onSub(item)
                    : () => { close(); item.Invoke?.Invoke(); };
                into.Add(Row(item.Icon, item.Label, item.AcceleratorText, item.Enabled, trailGlyph.Length > 0 ? trailGlyph : null, click, key: "sheet-row:" + i,
                    role: AutomationRole.MenuItem));
            }
        }

        static BoxEl Row(IconRef icon, string label, string? accelerator, bool enabled, string? trail, Action? onClick, string key, AutomationRole role)
        {
            ColorF ink = enabled ? Tok.TextPrimary : Tok.TextDisabled;
            var kids = new List<Element>(4)
            {
                // The icon column is reserved even for a row without one, so the labels share a left edge.
                new BoxEl
                {
                    Width = ClassicSheetRules.RowIcon, Height = ClassicSheetRules.RowIcon, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                    Children = [GlyphOf(icon, ClassicSheetRules.RowIcon, ink)],
                },
                new TextEl(label)
                {
                    Size = ClassicSheetRules.RowText, Color = ink, Grow = 1f, Basis = 0f, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                },
            };
            if (accelerator is { Length: > 0 }) kids.Add(new TextEl(accelerator) { Size = ClassicSheetRules.SubtitleSize, Color = Tok.TextTertiary });
            if (trail is not null) kids.Add(Icon(trail, ClassicSheetRules.RowTrail, enabled ? Tok.TextSecondary : Tok.TextDisabled));
            return new BoxEl
            {
                Key = key, Direction = 0, Height = ClassicSheetRules.RowHeight, Shrink = 0f, AlignItems = FlexAlign.Center, Gap = ClassicSheetRules.RowGap,
                Padding = new Edges4(ClassicSheetRules.RowPadX, 0f, ClassicSheetRules.RowPadX, 0f), Corners = CornerRadius4.All(ClassicSheetRules.RowRadius),
                Role = role, Focusable = true, OnClick = onClick, Children = kids.ToArray(),
            }.Interactive(Interaction.Subtle, isEnabled: enabled);
        }

        /// <summary>An <see cref="IconRef"/> as an element: the layered vector when it is registered, else the glyph in its font.</summary>
        static Element GlyphOf(IconRef icon, float size, ColorF ink)
        {
            if (icon.ThemedName is { Length: > 0 } themed && ThemedIconRegistry.Has(themed)) return ThemedIcon.Create(themed, size);
            return icon.Glyph is { Length: > 0 } glyph ? Icon(glyph, size, ink, icon.Font) : new BoxEl { Width = size, Height = size };
        }
    }
}
