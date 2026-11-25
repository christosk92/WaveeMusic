// ── Themes/WinUi/Structure/Views/Detail/WinUiPlaylistEditor.cs — WinUI's inline playlist editor (`Playlist.Editor`; P6 playlist-load lane) ─────────────
//
// The hero's title and description of a playlist the viewer may edit (W-Playlist), drawn by the editable label (FluentGpu `.Editable()`, with Platform/EditRow.cs): the same
// pattern as Classic's. The run is the hero's own (the title at the frame's size, the description one 14/20 line in Text 2). Numbers are `WinUiDetailRules`.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;

namespace Wavee.Themes.WinUi;

internal static class WinUiPlaylistEditor
{
    /// <summary>The registered template of <see cref="ViewKeys.PlaylistEditor"/>: the hero's title or description as the shared inline edit
    /// (FluentGpu `.Editable()` with Platform/EditRow.cs, the same pattern as Classic): the run as the hero draws it, a plate and a pencil on hover, the field in the plate's
    /// place with Save and Cancel under it, the status ("Saving…", "Saved") a layer at the box's end.</summary>
    public static Element Template(PlaylistEditorProps p, in ViewContext ctx)
    {
        bool description = p.Description;
        bool blank = string.IsNullOrWhiteSpace(p.Text);
        TextEl look = description
            ? new TextEl(p.Text)
            {
                Size = WinUiDetailRules.DescriptionSize, LineHeight = WinUiDetailRules.DescriptionLine, Color = Tok.TextSecondary,
                MaxLines = 1, Trim = TextTrim.CharacterEllipsis, Grow = 1f, Basis = 0f, MinWidth = 0f,
            }
            : R.Type.DetailHero(p.Text) with
            {
                Size = p.Size, LineHeight = p.LineHeight, Weight = 600, Grow = 1f, Basis = 0f, MinWidth = 0f,
                Wrap = TextWrap.WrapWholeWords, MaxLines = Detail.VerticalLayout.TitleLineCap, Trim = TextTrim.CharacterEllipsis, Color = Tok.TextPrimary,
            };
        // A description reads as rich text (its links in the page accent); the field edits its plain text.
        Element? read = description && !blank
            ? new BoxEl
            {
                Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Height = WinUiDetailRules.DescriptionLine, ClipToBounds = true,
                Children = [Controls.RichTextFlex(p.Text, WinUiDetailRules.DescriptionSize, Tok.TextSecondary, p.Accent(), WinUiDetailRules.DescriptionLines, p.NavRoute)],
            }
            : null;
        // The hero's run made editable in place (FluentGpu's `.Editable()`), with the page's Save / Cancel row and status around it (EditRow).
        var draft = p.Draft();
        bool canSave = description || draft.Value.Trim().Length > 0;
        Element label = (read ?? look).Editable(p.Text, look, p.Commit, new EditableOptions
        {
            Key = description ? "pl-description" : "pl-title",
            Placeholder = Loc.Get(description ? Strings.Detail.Edit.DescriptionPlaceholder : Strings.Detail.Edit.NamePlaceholder),
            IsReadOnly = !p.Editable, Required = !description, Compact = ctx.Tier().Window == WidthTier.Compact,
            Editing = p.Editing, OnBegin = p.Begin, Draft = draft, OnCancel = p.Cancel,
        });
        return p.Editable ? EditRow.Hero(label, p.Editing, p.Status, p.Save, p.Cancel, canSave, Loc.Get(Strings.Detail.Edit.NameRequired)) : label;
    }
}
