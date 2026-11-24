// ── Themes/Common/CommonColourEditor.cs — the custom colours editor's body, every theme (#184) ──────────────────────────────────
//
// `Dialog.ColourEditor`: one design for Classic, WinUI and Zune (the owner's rule: Classic and WinUI share one; Zune takes the same body in its
// own voice), drawn from the active theme's roles: the card's corners are its overlay radius (Zune: square), the text its type and its case
// (Zune: lowercase labels), the buttons its control styles. The card:
//
//   Custom dark colours
//   [■ Background   #1E1E2E]   ┌──────── preview ────────┐
//   [■ Surface      #313244]   │ strip                    │
//   [■ Text         #CDD6F4]   │ pane │ heading           │
//   [■ Muted text   #A6ADC8]   │      │ ▭ ▭  [Play] link  │
//   [■ Accent       #CBA6F7]   │ bar ───●──────────       │
//   [■ Second surf.   auto ]   └──────────────────────────┘
//   ┌ colour picker ┐          note (two lines, reserved)
//   [Start from ⌄]                        Reset  Cancel  [Apply]
//
// Below a 640 card the preview and its note go under the picker, and the card scrolls inside the window. The wells, their words and the
// preview are BOUND to the core's solved draft, so a drag on the picker repaints them and never re-renders the card; selecting another well
// re-renders it once (its ring) and re-seeds the picker (keyed on the core's epoch).

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Theming.Roles;
using E = Wavee.Themes.Common.CommonColourEditorRules;

namespace Wavee.Themes.Common;

/// <summary>The custom colours editor's body (see the file header).</summary>
internal static class CommonColourEditor
{
    /// <summary>Registers the body under <paramref name="templateId"/>.</summary>
    public static void Register(ViewRegistryBuilder views, string templateId) => views.Add(ViewKeys.DialogColourEditor, Template, templateId);

    /// <summary>The registered template of <see cref="ViewKeys.DialogColourEditor"/>.</summary>
    public static Element Template(ColourEditorProps p, in ViewContext ctx)
    {
        var viewport = ctx.Hooks.UseContextSignal(Viewport.Size).Value;
        float width = MathF.Min(E.CardWidth, viewport.Width - 2f * E.WindowGutter);
        bool beside = !CommonTiers.Stacks(ctx.Tier().Window);   // the preview beside the wells above Compact, under them at Compact
        float maxHeight = viewport.Height - 2f * E.WindowGutter;
        float target = CommonTiers.Target(ctx.Tier().Window);
        int selected = p.Selected.Value;
        int epoch = p.PickerEpoch.Value;
        var draft = p.Draft;

        var wells = new Element[p.WellLabels.Count];
        for (int i = 0; i < wells.Length; i++) wells[i] = Well(p, draft, i, i == selected);

        Element picker = new BoxEl
        {
            Key = "colours:picker:" + epoch.ToString(System.Globalization.CultureInfo.InvariantCulture), Direction = 1, MinWidth = 0f,
            Children = [ColorPicker.Create(p.Picker(), alphaEnabled: false, spectrumW: E.Spectrum, spectrumH: E.Spectrum, onChange: p.Pick)],
        };
        Element left = new BoxEl
        {
            Key = "colours:wells", Direction = 1, Gap = Spacing.S, MinWidth = 0f, Shrink = 1f,
            Children = [new BoxEl { Direction = 1, Children = wells }, picker],
        };
        Element right = new BoxEl
        {
            Key = "colours:preview-column", Direction = 1, Gap = Spacing.S, MinWidth = 0f, Shrink = 1f, AlignItems = FlexAlign.Start,
            Children = [Preview(draft), Note(draft)],
        };
        Element body = new BoxEl
        {
            Direction = (byte)(beside ? 0 : 1), Gap = E.BlockGap, MinWidth = 0f, AlignItems = FlexAlign.Start,
            Children = [left, right],
        };

        var startFrom = new Signal<int>(-1);
        Element commands = new BoxEl
        {
            Key = "colours:commands", Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, MinWidth = 0f, Wrap = true,
            Children =
            [
                p.StartFromChoices.Count == 0 ? new BoxEl { Width = 0f } : new BoxEl
                {
                    Key = "colours:start-from", MinHeight = target, AlignItems = FlexAlign.Center,
                    Children = [ComboBox.Create(p.StartFromChoices, startFrom, width: E.StartFromWidth,
                        placeholder: Loc.Get(Strings.Settings.Appearance.ColoursEditorStartFrom), onChange: p.StartFrom)],
                },
                new BoxEl { Grow = 1f, MinWidth = 0f },
                Button.Standard(Loc.Get(Strings.Settings.Appearance.ColoursEditorReset), p.Reset) with { Key = "colours:reset", MinHeight = target },
                Button.Standard(Loc.Get(Strings.Auth.Cancel), p.Cancel) with { Key = "colours:cancel", MinHeight = target },
                Button.Accent(Loc.Get(Strings.Settings.Appearance.ColoursEditorApply), p.Apply) with { Key = "colours:apply", MinHeight = target },
            ],
        };

        return new BoxEl
        {
            Key = "colours:card", Direction = 1, Width = width, MaxWidth = width, MaxHeight = maxHeight,
            Corners = Radii.OverlayAll, Fill = Tok.FillSolidBase, BorderColor = Tok.StrokeSurfaceDefault, BorderWidth = 1f, Shadow = Elevation.Dialog,
            ClipToBounds = true,
            Children =
            [
                new ScrollEl
                {
                    ContentSized = true, MaxHeight = maxHeight, EdgeCues = ScrollEdgeCues.None,
                    Content = new BoxEl
                    {
                        Direction = 1, Gap = E.BlockGap, Padding = Edges4.All(E.CardPadding), MinWidth = 0f,
                        Children =
                        [
                            new TextEl(p.Title)
                            {
                                Size = E.TitleSize, LineHeight = E.TitleLine, Weight = 600, Color = Tok.TextPrimary, Wrap = TextWrap.Wrap,
                                Case = R.Case.For(LabelKind.Heading),
                            },
                            body,
                            commands,
                        ],
                    },
                },
            ],
        };
    }

    // A well: its swatch (bound to the draft), its label, its value (bound); a radio row, the selected one ringed in the accent.
    static Element Well(ColourEditorProps p, IReadSignal<ColourEditorDraft> draft, int i, bool on) => new BoxEl
    {
        Key = "colours:well:" + i.ToString(System.Globalization.CultureInfo.InvariantCulture),
        Direction = 0, Gap = E.WellGap, AlignItems = FlexAlign.Center, Height = E.WellRowHeight, Padding = new Edges4(E.WellPadX, 0f, E.WellPadX, 0f),
        Corners = Radii.ControlAll, Fill = on ? Tok.FillSubtleSecondary : ColorF.Transparent, HoverFill = Tok.FillSubtleSecondary,
        PressedFill = Tok.FillSubtleTertiary, BorderWidth = E.SelectedRing, BorderColor = on ? Tok.AccentDefault : ColorF.Transparent,
        Role = AutomationRole.RadioButton, Focusable = true, Cursor = CursorId.Hand, OnClick = () => p.Select(i),
        Children =
        [
            new BoxEl
            {
                Width = E.Swatch, Height = E.Swatch, Shrink = 0f, Corners = Radii.ControlAll, BorderWidth = E.SwatchRing, BorderColor = Tok.StrokeControlDefault,
                Fill = Prop.Of(() => draft.Value.Wells[i]),
            },
            new TextEl(p.WellLabels[i])
            {
                Width = E.WellLabelWidth, Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, Case = R.Case.For(LabelKind.Label),
            },
            new TextEl("") { Text = Prop.Of(() => draft.Value.Hex[i]), Color = Tok.TextSecondary, MaxLines = 1 },
        ],
    };

    // The preview: a fixed miniature of the app in the solved colours (title strip, pane, a heading over a line, two cards, the accent button
    // and link, the player bar with its seek line). Every fill is bound: a drag repaints it, nothing re-lays out.
    static Element Preview(IReadSignal<ColourEditorDraft> d)
    {
        Element Bar(float w, float h, Func<ColorF> c) => new BoxEl { Width = w, Height = h, Shrink = 0f, Corners = Radii.ControlAll, Fill = Prop.Of(c) };
        Element card = new BoxEl
        {
            Width = E.PreviewCardW, Height = E.PreviewCardH, Shrink = 0f, Corners = Radii.CardAll, Fill = Prop.Of(() => d.Value.Raised),
            BorderWidth = 1f, BorderColor = Prop.Of(() => d.Value.Hairline),
        };
        return new BoxEl
        {
            Key = "colours:preview", Width = E.PreviewW, Height = E.PreviewH, Shrink = 0f, Direction = 1, ClipToBounds = true,
            Corners = Radii.CardAll, BorderWidth = 1f, BorderColor = Tok.StrokeSurfaceDefault, Fill = Prop.Of(() => d.Value.Ground),
            Children =
            [
                new BoxEl { Height = E.PreviewStrip, Shrink = 0f, Fill = Prop.Of(() => d.Value.Well) },
                new BoxEl
                {
                    Direction = 0, Grow = 1f, MinHeight = 0f,
                    Children =
                    [
                        new BoxEl
                        {
                            Width = E.PreviewPane, Shrink = 0f, Direction = 1, Gap = E.PreviewGap, Padding = Edges4.All(E.PreviewPad),
                            Fill = Prop.Of(() => d.Value.Well),
                            Children = [Bar(E.PreviewNavW, E.PreviewLineH, () => d.Value.Ink), Bar(E.PreviewNavW, E.PreviewLineH, () => d.Value.InkSecondary),
                                Bar(E.PreviewNavW, E.PreviewLineH, () => d.Value.InkTertiary)],
                        },
                        new BoxEl
                        {
                            Grow = 1f, MinWidth = 0f, Direction = 1, Gap = E.PreviewGap, Padding = Edges4.All(E.PreviewPad),
                            Children =
                            [
                                Bar(E.PreviewHeadW, E.PreviewHeadH, () => d.Value.Ink),
                                Bar(E.PreviewSubW, E.PreviewLineH, () => d.Value.InkSecondary),
                                new BoxEl { Direction = 0, Gap = E.PreviewGap, Children = [card, card] },
                                new BoxEl
                                {
                                    Direction = 0, Gap = E.PreviewGap, AlignItems = FlexAlign.Center,
                                    Children =
                                    [
                                        new BoxEl
                                        {
                                            Width = E.PreviewButtonW, Height = E.PreviewButtonH, Shrink = 0f, Corners = Radii.ControlAll,
                                            Fill = Prop.Of(() => d.Value.AccentFill), AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                                            Children = [Bar(E.PreviewLinkW / 2f, E.PreviewLineH / 2f, () => d.Value.OnAccent)],
                                        },
                                        Bar(E.PreviewLinkW, E.PreviewLineH, () => d.Value.AccentInk),
                                    ],
                                },
                            ],
                        },
                    ],
                },
                new BoxEl
                {
                    Height = E.PreviewBar, Shrink = 0f, Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                    Fill = Prop.Of(() => d.Value.Well),
                    Children = [Bar(E.PreviewSeekW, E.PreviewSeekH, () => d.Value.AccentFill)],
                },
            ],
        };
    }

    // The note: what the floors changed, in two reserved lines (the buttons never move when it appears).
    static Element Note(IReadSignal<ColourEditorDraft> d) => new BoxEl
    {
        Key = "colours:note", Width = E.PreviewW, Height = E.NoteLine * E.NoteLines, Shrink = 0f,
        Children =
        [
            new TextEl("")
            {
                Text = Prop.Of(() => d.Value.Note), Size = E.NoteSize, LineHeight = E.NoteLine, Color = Tok.TextSecondary,
                Wrap = TextWrap.Wrap, MaxLines = (int)E.NoteLines, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
            },
        ],
    };
}
