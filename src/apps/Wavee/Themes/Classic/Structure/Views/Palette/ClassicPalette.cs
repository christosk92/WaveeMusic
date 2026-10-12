// ── Themes/Classic/Structure/Views/Palette/ClassicPalette.cs — Classic's `Shell.CommandPalette` (themes plan 6.1, T4c) ───────
//
// From `Shell.Palette.cs` §3: the lane and the card. The command table, its scoring and the empty-query order (the builtin rows in
// declaration order), the open flag and what Enter does are core (`Shell.CommandTable`, `PaletteOpen`, `InvokePaletteEntry`); the card
// reads them through an `IPaletteSession` the host builds once per mount. This file keeps the keyboard navigation over the results:
//
//   lane   a hit-test-transparent ZStack sibling whose top pad puts the card's top edge exactly 64 DIP down, centred, a hard 560 wide
//          with no clamp (W1). The popup control (wired to the open flag by the host) takes the lane's zero-height anchor and the card.
//   card   the popup mounts it FRESH per open, so the query and the selection reset for free. The selection is UNCLAMPED in its signal;
//          the per-render clamp is the second tier. Down and Up wrap, Enter commits the selected row, Escape closes. "Command
//          palette" is announced once on open and the match count (throttled) on every edit (W3).

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Input;
using FluentGpu.Localization;
using FluentGpu.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Classic;

/// <summary>Wavee Classic's command palette.</summary>
internal static class ClassicPalette
{
    /// <summary>The registered template of <see cref="ViewKeys.ShellCommandPalette"/>.</summary>
    public static Element Template(PaletteProps props, in ViewContext ctx)
        => new BoxEl
        {
            Grow = 1f, Direction = 1, AlignItems = FlexAlign.Center, HitTestVisible = false,
            Padding = new Edges4(0f, ClassicFrameBitsRules.PaletteLaneTop, 0f, 0f),
            Children =
            [
                props.Popup(
                    new BoxEl { Width = ClassicFrameBitsRules.PaletteCardW, Height = 0f },
                    () => Embed.Comp(props, static () => new ClassicPaletteCard())),
            ],
        };
}

/// <summary>The card body. The popup mounts it FRESH per open, so the query and the selection reset for free; the session (the
/// index, the result array and the catalog row) is allocated once per mount (ch 19 §9.1 item 2).</summary>
internal sealed class ClassicPaletteCard : Component
{
    public override Element Render()
    {
        var props = UseProps<PaletteProps>();
        var query = UseSignal("");
        var sel = UseSignal(0);            // UNCLAMPED; the per-render clamp below is the second tier (§9.1 item 1)
        var session = UseRef<IPaletteSession?>(null);
        var announced = UseRef(false);

        session.Value ??= props.NewSession();
        var results = session.Value;

        string q = query.Value;
        int count = results.Filter(q);
        int selected = count == 0 ? 0 : Math.Clamp(sel.Value, 0, count - 1);

        // "Command palette" once on open, then the throttled count on every edit (W3).
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

        void Commit(int i) => results.Invoke(i);

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
                    Commit(selected);
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
                    Padding = new Edges4(Spacing.L, Spacing.M, Spacing.L, Spacing.M),
                    Children = [R.Type.DenseMeta(Loc.Get(Strings.Palette.NoMatches)) with { Color = Tok.TextTertiary }],
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
                    Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S,
                    MinHeight = Design.Size.ControlH, MinWidth = 0f,
                    Padding = new Edges4(Spacing.L, Spacing.XS, Spacing.L, Spacing.XS),
                    Corners = Radii.ControlAll,
                    // Selection and hover paint the SAME fill; the accent glyph + "Enter" disambiguate (W34).
                    Fill = active ? Tok.FillSubtleSecondary : ColorF.Transparent,
                    HoverFill = Tok.FillSubtleSecondary,
                    OnClick = () => Commit(at),
                    Children =
                    [
                        new TextEl(results.GlyphAt(at))
                        {
                            Size = ClassicFrameBitsRules.PaletteRowTextSize, FontFamily = Tok.Definition.Fonts.Icon,
                            Color = active ? Tok.AccentDefault : Tok.TextTertiary,
                        },
                        new BoxEl
                        {
                            Grow = 1f, MinWidth = 0f,
                            Children =
                            [
                                new TextEl(results.LabelAt(at))
                                {
                                    Size = ClassicFrameBitsRules.PaletteRowTextSize, Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                                },
                            ],
                        },
                        active ? R.Type.MicroMeta(enter) with { Color = Tok.TextTertiary } : new BoxEl(),
                    ],
                };
            }
        }

        return new BoxEl
        {
            Direction = 1, Width = ClassicFrameBitsRules.PaletteCardW, MaxHeight = ClassicFrameBitsRules.PaletteCardMaxH, Gap = Spacing.XXS,
            Padding = Edges4.All(Spacing.S),
            OnKeyDown = OnKey,
            Children =
            [
                TextBox.Create(query, options: new TextBox.TextBoxOptions
                {
                    Placeholder = Loc.Get(Strings.Palette.Placeholder),
                    Width = ClassicFrameBitsRules.PaletteCardW - Spacing.S * 2f,
                }),
                new BoxEl { Height = Spacing.XXS },
                new BoxEl { Direction = 1, Gap = ClassicFrameBitsRules.PaletteRowGap, Children = rows },
            ],
        };
    }
}
