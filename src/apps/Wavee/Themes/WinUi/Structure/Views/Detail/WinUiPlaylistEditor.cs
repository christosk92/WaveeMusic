// ── Themes/WinUi/Structure/Views/Detail/WinUiPlaylistEditor.cs — WinUI's inline playlist editor (`Playlist.Editor`; P6 playlist-load lane) ─────────────
//
// The hero's title and description of a playlist the viewer may edit (W-Playlist): the READ arm draws the text as the hero draws it (the title at the frame's size,
// the description one 14/20 line in Text 2, "Add an optional description" in Text 3 while empty) on a hover plate that opens the EDIT arm on click; the edit arm is
// the stock editable text over Save (accent) and Cancel, and a small status line ("Saving…", "Saved") follows a commit. Numbers are `WinUiDetailRules` (WVTH003).

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
    /// <summary>The registered template of <see cref="ViewKeys.PlaylistEditor"/>. The read arm is not keyed on the title's size (the props are re-pushed); the edit
    /// field, whose font size freezes at mount, is keyed on its own size.</summary>
    public static Element Template(PlaylistEditorProps p, in ViewContext ctx)
    {
        var hooks = ctx.Hooks;
        var input = hooks.UseContext(InputHooks.Current);
        var post = hooks.UsePost();
        // The read arm's plate is a Compact 40 target where the words are shorter than it (the description's 20 line).
        float bleedY = ctx.Tier().Window == WidthTier.Compact ? WinUiDetailRules.EditBleedYCompact : WinUiDetailRules.EditBleedY;
        Element body = p.Editing ? EditArm(p, input, post) : ReadArm(p, bleedY);
        // The status ("Saving…", "Saved") is a LAYER at the box's end: it takes no room, so a commit moves neither the words nor anything under them.
        return new BoxEl
        {
            ZStack = true, Direction = 1, MinWidth = 0f,
            Children =
            [
                new BoxEl { Key = "pl-swap", Direction = 1, MinWidth = 0f, Animate = MotionRecipes.CardResize, Children = [body] },
                .. p.Status == 0 || p.Editing ? Array.Empty<Element>() : [Status(saved: p.Status == 2) with { AlignSelf = FlexAlign.Center, JustifySelf = FlexAlign.End }],
            ],
        };
    }

    static Element ReadArm(PlaylistEditorProps p, float bleedY)
    {
        bool description = p.Description;
        bool blank = string.IsNullOrWhiteSpace(p.Text);
        string shown = blank ? Loc.Get(description ? Strings.Detail.Edit.DescriptionPlaceholder : Strings.Detail.Edit.NamePlaceholder) : p.Text;
        Element run = description
            ? (blank
                ? new TextEl(shown)
                {
                    Size = WinUiDetailRules.DescriptionSize, LineHeight = WinUiDetailRules.DescriptionLine, Color = Tok.TextTertiary,
                    MaxLines = 1, Trim = TextTrim.CharacterEllipsis, Grow = 1f, Basis = 0f, MinWidth = 0f,
                }
                : new BoxEl
                {
                    Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Height = WinUiDetailRules.DescriptionLine, ClipToBounds = true,
                    Children = [Controls.RichTextFlex(p.Text, WinUiDetailRules.DescriptionSize, Tok.TextSecondary, p.Accent(), WinUiDetailRules.DescriptionLines, p.NavRoute)],
                })
            : R.Type.DetailHero(shown) with
            {
                Size = p.Size, LineHeight = p.LineHeight, Weight = 600, Grow = 1f, Basis = 0f, MinWidth = 0f,
                Wrap = TextWrap.WrapWholeWords, MaxLines = Detail.VerticalLayout.TitleLineCap, Trim = TextTrim.CharacterEllipsis,
                Color = blank ? Tok.TextTertiary : Tok.TextPrimary,
            };
        if (!p.Editable) return run;
        // The hover plate bleeds past the words (so they keep their place) and opens the editor.
        return new BoxEl
        {
            Key = "pl-read", Direction = 0, MinWidth = 0f, AlignItems = FlexAlign.Center,
            Margin = new Edges4(-WinUiDetailRules.EditBleedX, -bleedY, -WinUiDetailRules.EditBleedX, -bleedY),
            Padding = new Edges4(WinUiDetailRules.EditBleedX, bleedY, WinUiDetailRules.EditBleedX, bleedY),
            Corners = Radii.ControlAll, HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary,
            Role = AutomationRole.Button, Cursor = CursorId.IBeam, Focusable = true, OnClick = p.Begin,
            Children = [run],
        };
    }

    static Element EditArm(PlaylistEditorProps p, InputHooks? hooks, Action<Action> post)
    {
        bool description = p.Description;
        float fontSize = description ? WinUiDetailRules.DescriptionSize : p.Size;
        float fieldH = description ? WinUiDetailRules.DescriptionFieldH : MathF.Max(WinUiDetailRules.TitleFieldMinH, p.Size + WinUiDetailRules.TitleFieldExtra);
        var draft = p.Draft();
        var parts = new TemplateParts
        {
            [EditableText.PartRoot] = b => b with { OnRealized = n => post(() => hooks?.FocusNode?.Invoke(n, false)) },
        };
        string placeholder = Loc.Get(description ? Strings.Detail.Edit.DescriptionPlaceholder : Strings.Detail.Edit.NamePlaceholder);
        return new BoxEl
        {
            Key = "pl-edit", Direction = 1, Gap = WinUiDetailRules.ActionGap, MinWidth = 0f,
            Children =
            [
                Embed.Comp(() => new EditableText
                {
                    Text = draft, Width = float.NaN, Height = fieldH, FontSize = fontSize, Placeholder = placeholder,
                    PlaceCaretAtEndOnFocus = true, CommitOnLostFocus = true, OnCommit = p.Commit, OnCancel = p.Cancel, Parts = parts,
                }) with { Key = "pl-field:" + (int)fontSize },
                new BoxEl
                {
                    Direction = 0, Gap = WinUiDetailRules.ActionGap, Justify = FlexJustify.End,
                    Children =
                    [
                        Button.Create(Loc.Get(Strings.Detail.Edit.Save), p.Save, ButtonAppearance.Accent) with { Key = "pl-save", Shrink = 0f },
                        Button.Create(Loc.Get(Strings.Detail.Edit.Cancel), p.Cancel, ButtonAppearance.Standard) with { Key = "pl-cancel", Shrink = 0f },
                    ],
                },
            ],
        };
    }

    /// <summary>"Saving…" with a ring, then "Saved" with the check, in Caption Text 2.</summary>
    static BoxEl Status(bool saved) => new BoxEl
    {
        Key = "pl-status", Direction = 0, Gap = WinUiDetailRules.ChipGap, AlignItems = FlexAlign.Center, Height = WinUiDetailRules.MetaLine,
        Padding = new Edges4(WinUiDetailRules.ChipPad, 0f, WinUiDetailRules.ChipPad, 0f), Corners = CornerRadius4.All(WinUiDetailRules.ChipRadius),
        Fill = Tok.FillCardDefault, HitTestVisible = false,
        Children =
        [
            saved
                ? Icon(Icons.Check, WinUiDetailRules.ChipCheck) with { Color = Tok.AccentTextPrimary }
                : ProgressRing.Indeterminate(WinUiDetailRules.ChipCheck, foreground: Tok.TextSecondary),
            new TextEl(Loc.Get(saved ? Strings.Detail.Edit.Saved : Strings.Detail.Edit.Saving))
            {
                Size = WinUiDetailRules.MetaSize, LineHeight = WinUiDetailRules.MetaLine, Color = Tok.TextSecondary, MaxLines = 1,
            },
        ],
    };
}
