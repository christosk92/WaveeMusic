// ── Themes/WinUi/Structure/Views/Palette/WinUiPalette.cs — WinUI's `Shell.CommandPalette` (themes plan 6.2; P6 W2a) ─────────────────────
//
// The command table, its scoring, the empty-query order, the open flag and what Enter does are core (`IPaletteSession`, the host's popup); the
// card is WinUI's flyout grammar (W-Flyouts' rules: acrylic, r8, 4 inner padding; items 36 high with a 4 radius, the subtle hover fill, the icon
// 16 at 12 from the edge, the accelerator in Caption, tertiary ink):
//
//   lane   a hit-test-transparent column whose top pad puts the card's top 64 under the window's top, centred, 560 wide.
//   card   mounted FRESH per open (the query and the selection reset). Down / Up wrap, Enter commits the selected row, Escape closes;
//          "Command palette" is announced once on open and the match count (throttled) on every edit.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Input;
using FluentGpu.Localization;
using FluentGpu.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.WinUi;

/// <summary>WinUI's command palette.</summary>
internal static class WinUiPalette
{
    /// <summary>The registered template of <see cref="ViewKeys.ShellCommandPalette"/>.</summary>
    public static Element Template(PaletteProps props, in ViewContext ctx) => new BoxEl
    {
        Grow = 1f, Direction = 1, AlignItems = FlexAlign.Center, HitTestVisible = false,
        Padding = new Edges4(0f, WinUiFrameRules.PaletteTop, 0f, 0f),
        Children =
        [
            props.Popup(
                new BoxEl { Width = WinUiFrameRules.PaletteW, Height = 0f },
                () => Embed.Comp(props, static () => new WinUiPaletteCard())),
        ],
    };
}

/// <summary>The palette's card. The popup mounts it fresh per open; the session is allocated once per mount.</summary>
internal sealed class WinUiPaletteCard : Component
{
    public override Element Render()
    {
        var props = UseProps<PaletteProps>();
        var query = UseSignal("");
        var sel = UseSignal(0);            // unclamped; the per-render clamp below is the second tier
        var session = UseRef<IPaletteSession?>(null);
        var announced = UseRef(false);

        session.Value ??= props.NewSession();
        var results = session.Value;

        string q = query.Value;
        int count = results.Filter(q);
        int selected = count == 0 ? 0 : Math.Clamp(sel.Value, 0, count - 1);

        UseEffect(() =>
        {
            if (!Announcer.IsAvailable) return;
            if (!announced.Value)
            {
                announced.Value = true;
                Announcer.Say(Loc.Get(Strings.Palette.Title));
            }
            Announcer.SayThrottled(count == 0 ? Loc.Get(Strings.Palette.NoMatches) : Strings.Palette.Matches(count));
        }, (DepKey)q);

        void OnKey(KeyEventArgs e)
        {
            switch (e.KeyCode)
            {
                case Keys.Down:
                    if (count > 0) sel.Value = (selected + 1) % count;
                    e.Handled = true;
                    break;
                case Keys.Up:
                    if (count > 0) sel.Value = (selected - 1 + count) % count;
                    e.Handled = true;
                    break;
                case Keys.Enter:
                    results.Invoke(selected);
                    e.Handled = true;
                    break;
                case Keys.Escape:
                    props.Close();
                    e.Handled = true;
                    break;
            }
        }

        Element[] rows;
        if (count == 0)
        {
            rows =
            [
                new BoxEl
                {
                    MinHeight = WinUiFrameRules.PaletteRowH, AlignItems = FlexAlign.Center, Padding = WinUiFrameRules.PaletteRowPadding,
                    Children = [new TextEl(Loc.Get(Strings.Palette.NoMatches)) { Size = WinUiFrameRules.PaletteTextSize, Color = Tok.TextTertiary }],
                },
            ];
        }
        else
        {
            rows = new Element[count];
            string enter = Loc.Get(Strings.Palette.Enter);
            for (int i = 0; i < count; i++)
            {
                int at = i;
                bool active = at == selected;
                rows[i] = new BoxEl
                {
                    Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M,
                    MinHeight = WinUiFrameRules.PaletteRowH, MinWidth = 0f, Padding = WinUiFrameRules.PaletteRowPadding,
                    Corners = Radii.ControlAll,
                    Fill = active ? Tok.FillSubtleSecondary : ColorF.Transparent,
                    HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary,
                    OnClick = () => results.Invoke(at),
                    Children =
                    [
                        new TextEl(results.GlyphAt(at))
                        {
                            Size = WinUiFrameRules.PaletteGlyph, FontFamily = Tok.Definition.Fonts.Icon,
                            Color = active ? Tok.AccentDefault : Tok.TextSecondary,
                        },
                        new BoxEl
                        {
                            Grow = 1f, MinWidth = 0f,
                            Children = [new TextEl(results.LabelAt(at)) { Size = WinUiFrameRules.PaletteTextSize, Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis }],
                        },
                        active ? Ui.Caption(enter) with { Color = Tok.TextTertiary } : new BoxEl(),
                    ],
                };
            }
        }

        return new BoxEl
        {
            Direction = 1, Width = WinUiFrameRules.PaletteW, MaxHeight = WinUiFrameRules.PaletteMaxH, Gap = WinUiFrameRules.PaletteGap,
            Padding = Edges4.All(WinUiFrameRules.PalettePad),
            OnKeyDown = OnKey,
            Children =
            [
                TextBox.Create(query, options: new TextBox.TextBoxOptions
                {
                    Placeholder = Loc.Get(Strings.Palette.Placeholder),
                    Width = WinUiFrameRules.PaletteW - WinUiFrameRules.PalettePad * 2f,
                }),
                new ScrollEl
                {
                    Grow = 1f, Shrink = 1f, MinHeight = 0f, ContentSized = true,
                    Content = new BoxEl { Direction = 1, Children = rows },
                },
            ],
        };
    }
}
