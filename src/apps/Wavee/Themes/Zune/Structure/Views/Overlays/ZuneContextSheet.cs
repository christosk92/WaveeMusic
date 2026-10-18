// ── Themes/Zune/Structure/Views/Overlays/ZuneContextSheet.cs — Zune's `Overlay.ContextSheet` (themes plan 6.3, P5 Z8a) ───────────────────────────────
//
// What a context menu opens as at Compact (the policy's `OverlayLayout.ContextSheet`, `ZuneOverlayTiers.Compact`): a sheet over the page's lower edge. No board draws it (owner
// Q18): it is built from the track menu's parts (`Z-Overlays`) and the Compact boards' scrim.
//
//   window-sized layer                       hit-test pass-through; the title row stays reachable above the scrim
//   ├─ scrim                                 black 72 %, a press closes the sheet
//   └─ card                                  the raised plate, square, the flyout shadow, as tall as its content up to the window less the title row
//        ├─ identity header                  64 tall: the tile (40), the title (15/20) over the subtitle (13/18), when the menu has one
//        └─ body (scrolls past the window)   the round verbs (40 across, 18 apart), then 48-DIP rows: the label (15), a chevron for a sub-menu or a check for a toggle
//
// A sub-menu row opens its rows in place (a back row heads the page), so the sheet is one surface from the first press to the last. Every row closes the sheet FIRST, then
// acts (the engine menu's rule). The core decides when this is the menu (`ContextSheets`) and what is in it; this draws it. The card slides up and fades on entry; the host
// fades the layer out on close. There is no grabber and no drag: Zune closes a sheet by its scrim, its Escape and its rows.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Themes.Common;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Theming.Roles;
using static FluentGpu.Dsl.Ui;

namespace Wavee.Themes.Zune;

/// <summary>Zune's context sheet.</summary>
internal static class ZuneContextSheet
{
    /// <summary>The registered template of <see cref="ViewKeys.OverlayContextSheet"/>.</summary>
    public static Element Template(ContextSheetProps props, in ViewContext ctx) => Embed.Comp(props, static () => new SheetHost());

    /// <summary>The sheet's host: the window size, and the sub-menu page the sheet is showing.</summary>
    sealed class SheetHost : ZuneOverlayComponent
    {
        /// <summary>The rows of the sub-menu page on show, with its title; null is the menu's own page.</summary>
        readonly Signal<(string Title, IReadOnlyList<MenuFlyoutItem> Rows)?> _page = new(null);

        public override Element Render()
        {
            var props = UseProps<ContextSheetProps>();
            var layout = UseLayout();
            Size2 vp = UseContextSignal(Viewport.Size).Value;
            var page = _page.Value;

            Action close = props.Close;
            float cardMax = MathF.Max(1f, vp.Height - layout.ChromeRow);

            var card = new BoxEl
            {
                Key = ZuneOverlayRules.SheetCardKey,
                Direction = 1, AlignSelf = FlexAlign.Stretch, MaxHeight = cardMax,
                Padding = new Edges4(0f, ZuneOverlayRules.SheetTop, 0f, ZuneOverlayRules.SheetBottom),
                Fill = Tok.FillSolidTertiary, Shadow = Elevation.Flyout,
                ClipToBounds = true,
                OnClick = static () => { },                    // the card is a hit target: a press on its inert area must not reach the scrim
                TabStop = false,                               // ... and no tab stop: the trap's first stop is the first command
                BlocksBackgroundScroll = true,                 // the page is a sibling under the overlay: the wheel on the card's inert parts stays here
                Enter = new EnterExit(Dy: ZuneOverlayRules.SheetEnterDy, Opacity: 0f, Active: true),
                Exit = new EnterExit(Dy: ZuneOverlayRules.SheetEnterDy, Opacity: 0f, Active: true),
                Children = props.Content is { } content ? ContentPage(content, layout, vp)
                    : page is { } sub ? SubPage(sub.Title, sub.Rows, close, layout, vp) : MenuPage(props, close, layout, vp),
            };

            return new BoxEl
            {
                Key = "zsheet:layer", Width = vp.Width, Height = vp.Height, Direction = 1, HitTestPassThrough = true,
                Children =
                [
                    new BoxEl { Height = layout.ChromeRow, Shrink = 0f, HitTestVisible = false },
                    new BoxEl
                    {
                        Grow = 1f, MinHeight = 0f, ZStack = true,
                        Children =
                        [
                            new BoxEl
                            {
                                Key = "zsheet:scrim", Fill = ZuneOverlayRules.Scrim, OnClick = close, TabStop = false, BlocksBackgroundScroll = true,
                                Enter = new EnterExit(Opacity: 0f, Active: true), Exit = new EnterExit(Opacity: 0f, Active: true),
                            },
                            new BoxEl { Direction = 1, Justify = FlexJustify.End, HitTestPassThrough = true, Children = [card] },
                        ],
                    },
                ],
            };
        }

        // ── the menu's own page ──────────────────────────────────────────────────────────────────────────────────

        Element[] MenuPage(ContextSheetProps props, Action close, ZuneOverlayLayout layout, Size2 vp)
        {
            var kids = new List<Element>(2);
            float chrome = ZuneOverlayRules.SheetTop + ZuneOverlayRules.SheetBottom;
            if (props.Header is { } header)
            {
                kids.Add(Header(header));
                chrome += ZuneOverlayRules.SheetHeaderHeight;
            }
            var body = new List<Element>(props.Rows.Count + 2);
            if (props.Primary.Count > 0)
            {
                // A verb closes the sheet first, then acts (the rows' rule); the engine strip runs and closes, so a command is invoked from here.
                var commands = props.Primary;
                body.Add(ZuneContextMenu.Verbs(commands, touch: true, run: at =>
                {
                    close();
                    commands[at].Invoke?.Invoke();
                }));
                body.Add(ZuneOverlayParts.Divider());
            }
            AddRows(body, props.Rows, close, onSub: OpenSub);
            kids.Add(Body(body, layout, vp, chrome));
            return kids.ToArray();
        }

        void OpenSub(MenuFlyoutItem item) => _page.Value = (item.Label, item.SubItems ?? []);

        // ── a card's own body (the list's filter card at Compact) ────────────────────────────────────────────────

        static Element[] ContentPage(Element content, ZuneOverlayLayout layout, Size2 vp)
            => [Body([content], layout, vp, ZuneOverlayRules.SheetTop + ZuneOverlayRules.SheetBottom)];

        // ── a sub-menu page ──────────────────────────────────────────────────────────────────────────────────

        Element[] SubPage(string title, IReadOnlyList<MenuFlyoutItem> rows, Action close, ZuneOverlayLayout layout, Size2 vp)
        {
            var body = new List<Element>(rows.Count + 2)
            {
                Row(Icons.ChevronLeft, title, null, enabled: true, trail: null, onClick: () => _page.Value = null, key: "zsheet:back", role: AutomationRole.Button),
                ZuneOverlayParts.Divider(),
            };
            AddRows(body, rows, close, onSub: OpenSub);
            return [Body(body, layout, vp, ZuneOverlayRules.SheetTop + ZuneOverlayRules.SheetBottom)];
        }

        // ── parts ────────────────────────────────────────────────────────────────────────────────────────────────

        static Element Header(in ContextMenuHeader header)
        {
            var kids = new List<Element>(2);
            if (header.Leading is { } leading)
                kids.Add(new BoxEl
                {
                    Width = ZuneOverlayRules.MenuTile, Height = ZuneOverlayRules.MenuTile, Shrink = 0f,
                    AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, HitTestVisible = false, Children = [leading],
                });
            var words = new List<Element>(2)
            {
                R.Type.TrackTitle(header.Title) with { Color = Tok.TextPrimary, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
            };
            if (header.Subtitle is { Length: > 0 } subtitle)
                words.Add(R.Type.DenseMeta(subtitle) with { Color = Tok.TextSecondary, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis });
            kids.Add(new BoxEl
            {
                Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Justify = FlexJustify.Center, HitTestVisible = false, Children = words.ToArray(),
            });
            return new BoxEl
            {
                Key = "zsheet:header", Direction = 0, Height = ZuneOverlayRules.SheetHeaderHeight, Shrink = 0f, AlignItems = FlexAlign.Center,
                Gap = ZuneOverlayRules.SheetHeaderGap, Padding = ZuneOverlayRules.SheetHeaderPadding, Children = kids.ToArray(),
            };
        }

        /// <summary>The verbs and rows in a scroll bounded by what is left of the window under the card's fixed parts.</summary>
        static Element Body(List<Element> body, ZuneOverlayLayout layout, Size2 vp, float fixedChrome)
        {
            float room = MathF.Max(1f, vp.Height - layout.ChromeRow - fixedChrome);
            return new ScrollEl
            {
                ContentSized = true, MaxHeight = room, EdgeCues = ScrollEdgeCues.None,
                Content = new BoxEl { Direction = 1, Children = body.ToArray() },
            };
        }

        static void AddRows(List<Element> into, IReadOnlyList<MenuFlyoutItem> rows, Action close, Action<MenuFlyoutItem> onSub)
        {
            for (int i = 0; i < rows.Count; i++)
            {
                var it = rows[i];
                if (it.IsSeparator)
                {
                    // Two hairlines in a row, or one at an edge, are noise: draw it only between rows.
                    if (into.Count == 0 || i == rows.Count - 1) continue;
                    into.Add(ZuneOverlayParts.Divider());
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
                into.Add(Row(default, item.Label, item.AcceleratorText, item.Enabled, trailGlyph.Length > 0 ? trailGlyph : null, click, key: ZuneOverlayRules.SheetRowKeyPrefix + i,
                    role: AutomationRole.MenuItem, literal: item.Literal));
            }
        }

        /// <summary>A 48-DIP row of words: the label, an accelerator where the menu has one, and a trailing chevron or check. Zune's menus carry no leading icons (the board's rows are
        /// words); the back row keeps its chevron.</summary>
        static BoxEl Row(IconRef icon, string label, string? accelerator, bool enabled, string? trail, Action? onClick, string key, AutomationRole role, bool literal = false)
        {
            ColorF ink = enabled ? Tok.TextPrimary : Tok.TextDisabled;
            var kids = new List<Element>(4);
            if (icon.Glyph is { Length: > 0 } || icon.ThemedName is { Length: > 0 })
                kids.Add(ZuneOverlayParts.GlyphOf(icon, ZuneOverlayRules.SheetRowTrail, ink));
            kids.Add(R.Type.TrackTitle(label) with
            {
                Color = ink, Grow = 1f, Basis = 0f, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                Case = literal ? TextCase.AsWritten : R.Case.For(LabelKind.Command),   // the menu's words are lowercase (Z-Overlays); a name is content
            });
            if (accelerator is { Length: > 0 }) kids.Add(R.Type.DenseMeta(accelerator) with { Color = Tok.TextTertiary, Shrink = 0f });
            if (trail is not null) kids.Add(Icon(trail, ZuneOverlayRules.SheetRowTrail, enabled ? Tok.TextSecondary : Tok.TextDisabled));
            return new BoxEl
            {
                Key = key, Direction = 0, Height = ZuneOverlayRules.SheetRowHeight, Shrink = 0f, AlignItems = FlexAlign.Center, Gap = ZuneOverlayRules.SheetRowGap,
                Padding = new Edges4(ZuneOverlayRules.SheetRowPadX, 0f, ZuneOverlayRules.SheetRowPadX, 0f),
                Role = role, Focusable = true, OnClick = onClick, Children = kids.ToArray(),
            }.Interactive(Interaction.Subtle, isEnabled: enabled);
        }
    }
}

/// <summary>A stateful card of the overlays: it subscribes to the tier facts (so a crossing re-renders it) and reads its layout record from them.</summary>
internal abstract class ZuneOverlayComponent : CommonTierComponent
{
    /// <summary>The overlay record of the window tier on screen now.</summary>
    protected ZuneOverlayLayout UseLayout() => ZuneOverlayTiers.Of(ReadTier());
}
