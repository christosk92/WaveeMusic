// ── Themes/Classic/Structure/Views/Dialogs/ClassicDestinationPicker.cs — Classic's `Dialog.FolderPicker` (themes plan 6.1, T11a) ──
//
// The destination picker's panel (ch 25 section 9), moved from `Actions.UI.cs`: one line of guidance naming WHAT is moving (13 secondary,
// at most two lines), a live filter field, then the rows in a bounded scroll (no colour edge cue: the list sits on ContentDialog's
// translucent FillLayerAlt overlay, where the surface-colour cue samples the plate behind it and paints a darker band) or the filtered-empty
// line. A row is a 40- or 44-DIP button: a plated glyph (the "New playlist" row), a 40-DIP cover or a bare glyph, then the label (14) over an
// optional subtitle (12), indented 12 DIP per tree level. The dialog shell (320 floor, Cancel, the default button) is the engine's; the core
// picker keeps the filter's text, the match rule (a pinned row is never filtered away) and what choosing a row does.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Theming;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;

namespace Wavee.Themes.Classic;

/// <summary>Wavee Classic's destination picker.</summary>
internal static class ClassicDestinationPicker
{
    /// <summary>The registered template of <see cref="ViewKeys.DialogFolderPicker"/>.</summary>
    public static Element Template(DestinationPickerProps props, in ViewContext ctx)
    {
        var rows = new List<Element>(props.Rows.Count);
        var commit = props.Commit;
        for (int i = 0; i < props.Rows.Count; i++)
        {
            var item = props.Rows[i];
            rows.Add(PickerRow(item, () => commit(item)));
        }

        Element list = rows.Count > 0
            ? new ScrollEl
            {
                // No colour edge cue: the list sits on ContentDialog's translucent FillLayerAlt overlay, where the
                // surface-colour cue samples the plate behind it and paints a darker band instead.
                ContentSized = true, MaxHeight = ClassicDialogRules.PickerListMaxHeight, EdgeCues = ScrollEdgeCues.None,
                Content = new BoxEl { Direction = 1, Gap = ClassicDialogRules.PickerListGap, Children = rows.ToArray() },
            }
            : new BoxEl
            {
                Height = ClassicDialogRules.PickerEmptyHeight, AlignItems = FlexAlign.Center, Padding = ClassicDialogRules.PickerEmptyPadding,
                Children = [R.Type.DenseMeta(props.EmptyText) with { Color = Tok.TextSecondary }],
            };

        var kids = new List<Element>(3);
        if (props.Body is { Length: > 0 } body)
            kids.Add(R.Type.DenseMeta(body) with
            {
                Color = Tok.TextSecondary, MaxLines = 2, Trim = TextTrim.CharacterEllipsis,
            });
        var query = props.Query();
        string placeholder = props.Placeholder;
        kids.Add(Embed.Comp(() => new EditableText
        {
            Placeholder = placeholder, Width = ClassicDialogRules.PickerFieldWidth, Height = ClassicDialogRules.PickerFieldHeight, Text = query,
        }));
        kids.Add(list);

        return new BoxEl
        {
            Direction = 1, Width = ClassicDialogRules.PickerPanelWidth, Gap = Spacing.XS, Padding = Edges4.All(props.PanelPadding),
            Children = kids.ToArray(),
        };
    }

    static Element PickerRow(in Actions.PickerItem item, Action onClick)
    {
        bool art = item.ArtUrl is { Length: > 0 } || item.Plated;
        float lead = ClassicDialogRules.PickerLead;
        Element leading = item.Plated
            ? new BoxEl
            {
                Width = lead, Height = lead, Shrink = 0f, Corners = CornerRadius4.All(ClassicDialogRules.PickerLeadRadius), Fill = Tok.FillSubtleSecondary,
                AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Children = [Icon(item.Glyph ?? Icons.Add, ClassicDialogRules.PickerPlatedIcon, Tok.TextSecondary)],
            }
            : item.ArtUrl is { Length: > 0 } url
                ? Controls.Artwork(url, lead, lead, ClassicDialogRules.PickerLeadRadius, decodePx: ClassicDialogRules.PickerLeadDecodePx)
                : Icon(item.Glyph ?? Icons.Folder, ClassicDialogRules.PickerBareIcon, Tok.TextSecondary);

        Element text = item.Subtitle is { Length: > 0 } sub
            ? new BoxEl
            {
                Direction = 1, Grow = 1f, Shrink = 1f, MinWidth = 0f, Gap = ClassicDialogRules.PickerTextGap,
                Children =
                [
                    new TextEl(item.Label)
                    {
                        Size = ClassicDialogRules.PickerLabelSize, LineHeight = ClassicDialogRules.PickerLabelLine, Color = Tok.TextPrimary,
                        MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                    },
                    new TextEl(sub)
                    {
                        Size = ClassicDialogRules.PickerSubSize, LineHeight = ClassicDialogRules.PickerSubLine, Color = Tok.TextSecondary,
                        MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                    },
                ],
            }
            : new TextEl(item.Label)
            {
                Size = ClassicDialogRules.PickerLabelSize, LineHeight = ClassicDialogRules.PickerLabelLine, Color = Tok.TextPrimary, Grow = 1f, MaxLines = 1,
                Trim = TextTrim.CharacterEllipsis,
            };

        return new BoxEl
        {
            Key = item.Key,
            Direction = 0, Height = art ? ClassicDialogRules.PickerRowArtHeight : ClassicDialogRules.PickerRowHeight, AlignItems = FlexAlign.Center,
            Gap = ClassicDialogRules.PickerRowGap,
            Padding = new Edges4(ClassicDialogRules.PickerRowLeft + item.Depth * ClassicDialogRules.PickerIndentStep, 0f, ClassicDialogRules.PickerRowRight, 0f),
            Corners = CornerRadius4.All(ClassicDialogRules.PickerRowRadius),
            Role = AutomationRole.Button, Focusable = true, FocusVisualMargin = Design.FocusInsetRow,
            OnClick = onClick,
            Children = [leading, text],
        }.Interactive(Interaction.Subtle);
    }
}
