// ── Themes/Zune/Structure/Views/Palette/ZunePalette.cs — Zune's `Shell.CommandPalette` (themes plan 6.3; P5 Z2a) ───────────────────────
//
// No board draws the palette (owner Q18: built from Zune's parts). The command table, its scoring, the open flag and what Enter does are core
// (`IPaletteSession`); the theme draws the lane and the card and runs the keyboard navigation:
//
//   lane   a hit-test-transparent column whose top pad puts the card 64 down, centred, 560 wide (the shell's lane, kept).
//   card   the popup's plate (the style's square raised plate), 16 in: the ruled field (the style's TextBox), then 36-tall rows inset 16, the
//          glyph and the label 12 apart. The selected row is the 12 % accent wash with the glyph in the accent and the lowercase "enter"
//          hint; a hovered row the 5 % ink wash. Down and Up wrap, Enter commits, Escape closes. "Command palette" is announced once on open,
//          the throttled match count on every edit. The popup mounts the card FRESH per open, so the query and the selection reset for free.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Input;
using FluentGpu.Localization;
using FluentGpu.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Zune;

/// <summary>Zune's command palette.</summary>
internal static class ZunePalette
{
    /// <summary>The registered template of <see cref="ViewKeys.ShellCommandPalette"/>.</summary>
    public static Element Template(PaletteProps props, in ViewContext ctx)
        => new BoxEl
        {
            Grow = 1f, Direction = 1, AlignItems = FlexAlign.Center, HitTestVisible = false,
            Padding = ZuneFrameRules.PaletteLanePadding,
            Children =
            [
                props.Popup(
                    new BoxEl { Width = ZuneFrameRules.PaletteW, Height = 0f },
                    () => Embed.Comp(props, static () => new ZunePaletteCard())),
            ],
        };
}

/// <summary>The card body: the session is allocated once per mount.</summary>
internal sealed class ZunePaletteCard : Component
{
    public override Element Render()
    {
        var props = UseProps<PaletteProps>();
        var query = UseSignal("");
        var sel = UseSignal(0);            // UNCLAMPED; the per-render clamp below is the second tier
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
                    MinHeight = ZuneFrameRules.PaletteRowH, AlignItems = FlexAlign.Center, Padding = ZuneFrameRules.PaletteRowPadding,
                    Children = [R.Type.DenseMeta(Loc.Get(Strings.Palette.NoMatches)) with { Color = Tok.TextTertiary, Case = R.Case.For(LabelKind.Label) }],
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
                    Direction = 0, AlignItems = FlexAlign.Center, Gap = ZuneFrameRules.PaletteRowGap,
                    MinHeight = ZuneFrameRules.PaletteRowH, MinWidth = 0f, Padding = ZuneFrameRules.PaletteRowPadding,
                    Corners = R.Shape.All(ShapeRole.Control),
                    Fill = active ? R.Layer.SelectedRest : ColorF.Transparent,
                    HoverFill = active ? R.Layer.SelectedHover : R.Layer.RowHover,
                    OnClick = () => Commit(at),
                    Children =
                    [
                        new TextEl(results.GlyphAt(at))
                        {
                            Size = ZuneFrameRules.PaletteGlyph, FontFamily = Tok.Definition.Fonts.Icon,
                            Color = active ? R.Ink.AccentSelection : Tok.TextTertiary,
                        },
                        new BoxEl
                        {
                            Grow = 1f, MinWidth = 0f,
                            Children = [R.Type.TrackTitle(results.LabelAt(at)) with { Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis }],
                        },
                        active ? R.Type.MicroMeta(enter) with { Color = Tok.TextTertiary, Case = R.Case.For(LabelKind.Command) } : new BoxEl(),
                    ],
                };
            }
        }

        return new BoxEl
        {
            Direction = 1, Width = ZuneFrameRules.PaletteW, MaxHeight = ZuneFrameRules.PaletteMaxH, Gap = ZuneFrameRules.PaletteFieldGap,
            Padding = ZuneFrameRules.PalettePadding,
            OnKeyDown = OnKey,
            Children =
            [
                TextBox.Create(query, options: new TextBox.TextBoxOptions
                {
                    Placeholder = Loc.Get(Strings.Palette.Placeholder),
                    Width = ZuneFrameRules.PaletteFieldW,
                }),
                new BoxEl { Direction = 1, Children = rows },
            ],
        };
    }
}
