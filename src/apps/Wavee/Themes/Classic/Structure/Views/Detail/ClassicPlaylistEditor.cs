// ── Themes/Classic/Structure/Views/Detail/ClassicPlaylistEditor.cs — Classic's inline playlist editor (themes plan 6.1, T9b) ─────────────────────────
//
// From `Entities\Playlist.UI.cs` §2 and §3 (W14, W15): the status chip (the cover scrim's saving state and the access panel's switch rows) and the inline
// editor of the playlist's name and description, which is the editable label (FluentGpu `.Editable()`, with Platform/EditRow.cs): the same pattern as WinUI's.
//
// `Playlist.InlineEditor` keeps the state machine (editing, the draft, the status 0 / 1 / 2, "a create that navigated opens IN edit mode", the "Saved" chip's
// 1.8 s hold), the permission decision (`EditableMetadata`) and the verbs: Begin, and Commit (Enter / blur / Save: an empty (title) or unchanged trimmed value is
// discarded; the write carries the value as it stood when editing STARTED, so a refusal cannot restore another device's rename, §6.1). The pencil's hover cell
// is the picture's own. A11Y: every clickable node declares Role + Cursor + a name.

using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;

namespace Wavee.Themes.Classic;

internal static partial class ClassicPlaylist
{
    // ══ 1. THE STATUS CHIP AND THE EDIT BUTTONS (W14) ═══════════════════════════════════════════════════════════════════

    /// <summary>The "Saving…" / "Saved" chip: the editors' status row, the cover scrim's saving state and the access panel's switch rows.</summary>
    static Element StatusChip(bool saved) => new BoxEl
    {
        Key = "pl-status", Direction = 0, Gap = ClassicPlaylistLayout.ChipGap, AlignItems = FlexAlign.Center, Shrink = 0f,
        Padding = new Edges4(ClassicPlaylistLayout.ChipPadL, ClassicPlaylistLayout.ChipPadY, ClassicPlaylistLayout.ChipPadR, ClassicPlaylistLayout.ChipPadY),
        Corners = CornerRadius4.All(ClassicPlaylistLayout.ChipRadius), Fill = Tok.FillSubtleSecondary,
        Children =
        [
            new BoxEl
            {
                Key = saved ? "ico:saved" : "ico:saving", Width = ClassicPlaylistLayout.ChipIcon, Height = ClassicPlaylistLayout.ChipIcon,
                AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Animate = MotionRecipes.IconSwap,
                Children =
                [
                    saved
                        ? Icon(Icons.Accept, ClassicPlaylistLayout.ChipGlyph, Tok.AccentTextPrimary)
                        : ProgressRing.Indeterminate(ClassicPlaylistLayout.ChipIcon, foreground: Tok.TextSecondary),
                ],
            },
            new BoxEl
            {
                Key = saved ? "txt:saved" : "txt:saving", Animate = MotionRecipes.TextSwap,
                Children = [R.Type.MicroMeta(Loc.Get(saved ? Strings.Detail.Edit.Saved : Strings.Detail.Edit.Saving)) with { Weight = 600, Color = Tok.TextSecondary, MaxLines = 1 }],
            },
        ],
    };

    // ══ 2. THE INLINE EDITOR (W14, W15) ═════════════════════════════════════════════════════════════════════════════════

    /// <summary>The registered template of <see cref="ViewKeys.PlaylistEditor"/>: the hero's title or description as the shared inline edit
    /// (FluentGpu `.Editable()` with Platform/EditRow.cs, the same pattern as WinUI's): the run as the hero draws it, a plate and a pencil on hover, the field in the plate's
    /// place with Save and Cancel under it, the status a layer at the box's end (it no longer moves the page).</summary>
    public static Element EditorTemplate(PlaylistEditorProps p, in ViewContext ctx)
    {
        bool description = p.Description;
        bool blank = string.IsNullOrWhiteSpace(p.Text);
        // Links take the PAGE accent (§4: 0.2.9's editable arm hard-coded AccentTextPrimary — fixed).
        TextEl look = description
            ? new TextEl(p.Text) { Size = ClassicPlaylistLayout.DescriptionSize, Color = Tok.TextSecondary, Grow = 1f, Basis = 0f, MinWidth = 0f, Wrap = TextWrap.Wrap }
            : R.Type.DetailHero(p.Text) with
            {
                // The frame's title rule: the size it chose, drawn exactly, wrapping freely, ellipsised only past the pathological cap.
                Size = p.Size, Weight = 600, Grow = 1f, Basis = 0f, MinWidth = 0f, LineHeight = p.LineHeight, Wrap = TextWrap.WrapWholeWords,
                MaxLines = Detail.VerticalLayout.TitleLineCap, Trim = TextTrim.CharacterEllipsis, Color = Tok.TextPrimary,
            };
        Element? read = description && !blank
            ? Controls.RichTextFlex(p.Text, ClassicPlaylistLayout.DescriptionSize, Tok.TextSecondary, p.Accent(), ClassicPlaylistLayout.DescriptionLines, p.NavRoute)
            : null;
        // The hero's run made editable in place (FluentGpu's `.Editable()`), with the page's Save / Cancel row and status around it (EditRow).
        var draft = p.Draft();
        bool canSave = description || draft.Value.Trim().Length > 0;
        Element label = (read ?? look).Editable(p.Text, look, p.Commit, new EditableOptions
        {
            Key = description ? "pl-description" : "pl-title",
            Placeholder = Loc.Get(description ? Strings.Detail.Edit.DescriptionPlaceholder : Strings.Detail.Edit.NamePlaceholder),
            IsReadOnly = !p.Editable, Required = !description, Multiline = description, Compact = ctx.Tier().Window == WidthTier.Compact,
            Editing = p.Editing, OnBegin = p.Begin, Draft = draft, OnCancel = p.Cancel,
        });
        return p.Editable ? EditRow.Hero(label, p.Editing, p.Status, p.Save, p.Cancel, canSave, Loc.Get(Strings.Detail.Edit.NameRequired)) : label;
    }
}
