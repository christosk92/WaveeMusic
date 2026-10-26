// ── Themes/Zune/Structure/Views/Dialogs/ZuneFolderPicker.cs — Zune's `Dialog.FolderPicker` (themes plan 6.3, P5 Z8a) ─────────────────────────────────
//
// The destination picker's panel ("add to playlist", "move to folder", the folder pickers; no board draws it, owner Q18): one line of guidance naming WHAT is moving (13/18
// secondary, at most two lines), a live filter field (the control table's rule), then the rows in a bounded scroll or the filtered-empty line. A row is a flat button (the row
// hover wash, square): a plated glyph (the "new playlist" row), a 40 cover or a bare glyph, then the label (15/20) over an optional subtitle (13/18), indented 12 DIP per tree
// level. Rows are 40 high (48 with art), 44 and 48 at Compact. The dialog shell (the control table's plate, Cancel, the default button) is the engine's; the core picker keeps
// the filter's text, the match rule (a pinned row is never filtered away) and what choosing a row does.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Theming.Roles;
using static FluentGpu.Dsl.Ui;

namespace Wavee.Themes.Zune;

/// <summary>Zune's destination picker.</summary>
internal static class ZuneFolderPicker
{
    /// <summary>The key a picker row is mounted under (the item's own key follows).</summary>
    public const string RowKeyPrefix = "zpick:row:";

    /// <summary>The registered template of <see cref="ViewKeys.DialogFolderPicker"/>.</summary>
    public static Element Template(DestinationPickerProps props, in ViewContext ctx)
    {
        // The panel's width and the list's height come from the dialog fit of the tier: the card fills the window at Compact, and a short window bounds the list.
        var layout = ZuneOverlayTiers.Of(ctx.Tier());
        var fit = layout.Core.Dialog;
        Size2 vp = ctx.Hooks.UseContextSignal(Viewport.Size).Value;
        float panelWidth = fit.BodyWidth(ZuneOverlayRules.PickerPanelWidth, vp.Width);
        float fieldWidth = panelWidth - Edges4.All(props.PanelPadding).Horizontal;
        float listMax = MathF.Max(layout.PickerRowArtHeight * 2f,
            fit.BodyMaxHeight(ZuneOverlayRules.PickerListMaxHeight + ZuneOverlayRules.PickerHeadHeight, vp.Height) - ZuneOverlayRules.PickerHeadHeight);
        var rows = new List<Element>(props.Rows.Count);
        var commit = props.Commit;
        for (int i = 0; i < props.Rows.Count; i++)
        {
            var item = props.Rows[i];
            rows.Add(PickerRow(item, () => commit(item), layout));
        }

        Element list = rows.Count > 0
            ? new ScrollEl
            {
                // No colour edge cue: the list sits on the dialog plate, where the surface-colour cue samples the plate behind it and paints a darker band.
                ContentSized = true, MaxHeight = listMax, EdgeCues = ScrollEdgeCues.None,
                Content = new BoxEl { Direction = 1, Gap = ZuneOverlayRules.PickerListGap, Children = rows.ToArray() },
            }
            : new BoxEl
            {
                Key = "zpick:empty", Height = ZuneOverlayRules.PickerEmptyHeight, AlignItems = FlexAlign.Center, Padding = ZuneOverlayRules.PickerEmptyPadding,
                Children = [R.Type.DenseMeta(props.EmptyText) with { Color = Tok.TextSecondary, MinWidth = 0f }],
            };

        var kids = new List<Element>(3);
        if (props.Body is { Length: > 0 } body)
            kids.Add(R.Type.DenseMeta(body) with { Color = Tok.TextSecondary, MaxLines = 2, Trim = TextTrim.CharacterEllipsis, Wrap = TextWrap.Wrap, MinWidth = 0f });
        var query = props.Query();
        string placeholder = props.Placeholder;
        kids.Add(Embed.Comp(() => new EditableText
        {
            Placeholder = placeholder, Width = fieldWidth, Height = layout.FieldHeight, Text = query,
        }));
        kids.Add(list);

        return new BoxEl
        {
            Key = "zpick:panel", Direction = 1, Width = panelWidth, Gap = Spacing.XS, Padding = Edges4.All(props.PanelPadding),
            Children = kids.ToArray(),
        };
    }

    static Element PickerRow(in Actions.PickerItem item, Action onClick, ZuneOverlayLayout layout)
    {
        bool art = item.ArtUrl is { Length: > 0 } || item.Plated;
        float lead = ZuneOverlayRules.PickerLead;
        Element leading = item.Plated
            ? new BoxEl
            {
                Width = lead, Height = lead, Shrink = 0f, Corners = R.Shape.All(ShapeRole.Cover), Fill = R.Layer.ArtPlaceholder,
                AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Children = [Icon(item.Glyph ?? Icons.Add, ZuneOverlayRules.PickerPlatedIcon, Tok.TextSecondary)],
            }
            : item.ArtUrl is { Length: > 0 } url
                ? Controls.Artwork(url, lead, lead, R.Shape.Cover, decodePx: ZuneOverlayRules.PickerLeadDecodePx)
                : Icon(item.Glyph ?? Icons.Folder, ZuneOverlayRules.PickerBareIcon, Tok.TextSecondary);

        Element text = item.Subtitle is { Length: > 0 } sub
            ? new BoxEl
            {
                Direction = 1, Grow = 1f, Shrink = 1f, MinWidth = 0f,
                Children =
                [
                    R.Type.TrackTitle(item.Label) with { Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f },
                    R.Type.DenseMeta(sub) with { Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f },
                ],
            }
            : R.Type.TrackTitle(item.Label) with
            {
                Color = Tok.TextPrimary, Grow = 1f, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
            };

        return new BoxEl
        {
            Key = RowKeyPrefix + item.Key,
            Direction = 0, Height = art ? layout.PickerRowArtHeight : layout.PickerRowHeight, AlignItems = FlexAlign.Center,
            Gap = ZuneOverlayRules.PickerRowGap,
            Padding = new Edges4(ZuneOverlayRules.PickerRowLeft + item.Depth * ZuneOverlayRules.PickerIndentStep, 0f, ZuneOverlayRules.PickerRowRight, 0f),
            Corners = R.Shape.All(ShapeRole.Control),
            Role = AutomationRole.Button, Focusable = true, FocusVisualMargin = Design.FocusInsetRow,
            OnClick = onClick,
            Children = [leading, text],
        }.Interactive(Interaction.Subtle);
    }
}
