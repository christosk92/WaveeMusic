// ── Themes/Classic/Structure/Views/Detail/ClassicPlaylistEditor.cs — Classic's inline playlist editor (themes plan 6.1, T9b) ─────────────────────────
//
// From `Entities\Playlist.UI.cs` §2 and §3 (W14, W15): the status chip, the Save / Cancel buttons and the inline editor of the playlist's name and
// description. ONE box across read ↔ edit whose height tweens (CardResize), the status row BELOW it — never inline, so the title's measure never moves (ch 06
// §0.6). The read arm is the hero run itself with a pencil that appears on hover; the edit arm is the engine's `EditableText` (focus WITHOUT a visual cue: the
// caret sits at the end and nothing is selected) over Save / Cancel.
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

    /// <summary>Save / Cancel: 32 tall, r16, pad 10/0/12/0, gap 6; Save accent-tinted (.16 / .26 / .12).</summary>
    static Element EditButton(string glyph, string label, bool accent, Action onClick) => new BoxEl
    {
        Direction = 0, Gap = ClassicPlaylistLayout.ChipGap, Height = ClassicPlaylistLayout.EditButtonH, AlignItems = FlexAlign.Center,
        Padding = new Edges4(ClassicPlaylistLayout.EditButtonPadL, 0f, ClassicPlaylistLayout.EditButtonPadR, 0f),
        Corners = CornerRadius4.All(ClassicPlaylistLayout.EditButtonRadius),
        Fill = accent ? Tok.AccentTextPrimary with { A = ClassicPlaylistLayout.EditAccentRest } : ColorF.Transparent,
        HoverFill = accent ? Tok.AccentTextPrimary with { A = ClassicPlaylistLayout.EditAccentHover } : Tok.FillSubtleSecondary,
        PressedFill = accent ? Tok.AccentTextPrimary with { A = ClassicPlaylistLayout.EditAccentPressed } : Tok.FillSubtleTertiary,
        BrushTransitionMs = Design.Motion.Faster,
        Role = AutomationRole.Button, Cursor = CursorId.Hand, Focusable = true, OnClick = onClick,
        Children =
        [
            Icon(glyph, ClassicPlaylistLayout.EditButtonGlyph, accent ? Tok.AccentTextPrimary : Tok.TextSecondary),
            new TextEl(label) { Size = ClassicPlaylistLayout.EditButtonText, Weight = 600, Color = accent ? Tok.AccentTextPrimary : Tok.TextSecondary },
        ],
    };

    // ══ 2. THE INLINE EDITOR (W14, W15) ═════════════════════════════════════════════════════════════════════════════════

    /// <summary>The registered template of <see cref="ViewKeys.PlaylistEditor"/>. The title's read arm is NOT keyed on its size: the props are re-pushed, so the
    /// read arm follows the frame's title rule (which moves the size with the measure) without a remount — a remount would replay the read arm's fade-in at every
    /// size step of a rail drag. The edit field, whose font size freezes at mount, is keyed on its own size instead.</summary>
    public static Element EditorTemplate(PlaylistEditorProps p, in ViewContext ctx)
    {
        var hooks = ctx.Hooks;
        var hovered = hooks.UseSignal(false);
        var input = hooks.UseContext(InputHooks.Current);
        var post = hooks.UsePost();

        Element body = p.Editing ? EditArm(p, input, post) : ReadArm(p, hovered);
        Element statusRow = new BoxEl
        {
            Key = "pl-status-row", Direction = 0, Justify = FlexJustify.End, Gap = ClassicPlaylistLayout.ChipGap,
            Height = p.Description && p.Status == 0 ? 0f : float.NaN, ClipToBounds = true,
            Children = p.Status == 0 ? [] : [StatusChip(saved: p.Status == 2)],
        };
        return new BoxEl
        {
            Direction = 1, Gap = p.Status != 0 ? ClassicPlaylistLayout.StatusRowGap : 0f, MinWidth = 0f,
            Children =
            [
                new BoxEl { Key = "pl-swap", Direction = 1, MinWidth = 0f, Animate = MotionRecipes.CardResize, Children = [body] },
                statusRow,
            ],
        };
    }

    static Element ReadArm(PlaylistEditorProps p, Signal<bool> hovered)
    {
        bool description = p.Description;
        string text = p.Text;
        bool blank = string.IsNullOrWhiteSpace(text);
        string shown = blank ? Loc.Get(description ? Strings.Detail.Edit.DescriptionPlaceholder : Strings.Detail.Edit.NamePlaceholder) : text;
        // Links take the PAGE accent in both arms (§4: 0.2.9's editable arm hard-coded AccentTextPrimary — fixed).
        Element run = description
            ? (blank ? new TextEl(shown) { Size = ClassicPlaylistLayout.DescriptionSize, Color = Tok.TextTertiary, Grow = 1f, Basis = 0f, MinWidth = 0f, Wrap = TextWrap.Wrap }
                     : Controls.RichTextFlex(text, ClassicPlaylistLayout.DescriptionSize, Tok.TextSecondary, p.Accent(), ClassicPlaylistLayout.DescriptionLines, p.NavRoute))
            : R.Type.DetailHero(shown) with
            {
                // The frame's title rule: the size it chose, drawn exactly (no auto-fit under an authored line box), wrapping freely, ellipsised only past
                // the pathological cap.
                Size = p.Size, Weight = 600, Grow = 1f, Basis = 0f, MinWidth = 0f,
                LineHeight = p.LineHeight, Wrap = TextWrap.WrapWholeWords,
                MaxLines = Detail.VerticalLayout.TitleLineCap, Trim = TextTrim.CharacterEllipsis,
                Color = blank ? Tok.TextTertiary : Tok.TextPrimary,
            };
        if (!p.Editable) return run;
        int status = p.Status;
        float glyph = description ? ClassicPlaylistLayout.PencilGlyphDescription : MathF.Max(ClassicPlaylistLayout.PencilGlyphMin, p.Size * ClassicPlaylistLayout.PencilGlyphScale);
        return new BoxEl
        {
            Key = "pl-read", Direction = 0, Gap = Spacing.S, MinWidth = 0f,
            AlignItems = description ? FlexAlign.Start : FlexAlign.Center,
            Margin = new Edges4(-ClassicPlaylistLayout.ReadBleedX, -ClassicPlaylistLayout.ReadBleedY, -ClassicPlaylistLayout.ReadBleedX, -ClassicPlaylistLayout.ReadBleedY),
            Padding = new Edges4(ClassicPlaylistLayout.ReadBleedX, ClassicPlaylistLayout.ReadBleedY, ClassicPlaylistLayout.ReadBleedX, ClassicPlaylistLayout.ReadBleedY),
            Corners = Radii.ControlAll, HoverFill = Tok.FillSubtleSecondary,
            BorderWidth = 1f, BorderColor = ColorF.Transparent, HoverBorderColor = Tok.StrokeControlDefault,
            Role = AutomationRole.Button, Cursor = CursorId.IBeam, Focusable = true, OnClick = p.Begin,
            OnHoverMove = _ => { if (!hovered.Peek()) hovered.Value = true; },
            OnPointerExit = () => hovered.Value = false,
            Enter = new EnterExit(Opacity: 0f, Active: true),
            Children =
            [
                run,
                new BoxEl
                {
                    Width = description ? ClassicPlaylistLayout.PencilBoxDescriptionW : ClassicPlaylistLayout.PencilBox,
                    Height = description ? ClassicPlaylistLayout.PencilBoxDescriptionH : ClassicPlaylistLayout.PencilBox, Shrink = 0f,
                    AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, HitTestVisible = false,
                    Opacity = (Func<float>)(() => hovered.Value && status == 0 ? 1f : 0f),
                    Transition = MotionTok.ControlFast,
                    Children = [Icon(Icons.Edit, glyph, Tok.TextSecondary)],
                },
            ],
        };
    }

    static Element EditArm(PlaylistEditorProps p, InputHooks? hooks, Action<Action> post)
    {
        bool description = p.Description;
        float fieldH = description ? ClassicPlaylistLayout.DescriptionFieldH : MathF.Max(ClassicPlaylistLayout.TitleFieldMinH, p.Size + ClassicPlaylistLayout.TitleFieldExtra);
        float fontSize = description ? ClassicPlaylistLayout.DescriptionSize : p.Size;
        var draft = p.Draft();
        var commit = p.Commit;
        var cancel = p.Cancel;
        // FocusOnMount (W14): focus WITHOUT a visual cue; with PlaceCaretAtEndOnFocus the caret sits at the end and nothing is selected.
        var parts = new TemplateParts
        {
            [EditableText.PartRoot] = b => b with { OnRealized = n => post(() => hooks?.FocusNode?.Invoke(n, false)) },
        };
        string placeholder = Loc.Get(description ? Strings.Detail.Edit.DescriptionPlaceholder : Strings.Detail.Edit.NamePlaceholder);
        return new BoxEl
        {
            Key = "pl-edit", Direction = 1, Gap = Spacing.S, MinWidth = 0f,
            Children =
            [
                Embed.Comp(() => new EditableText
                {
                    Text = draft, Width = float.NaN, Height = fieldH, FontSize = fontSize, Placeholder = placeholder,
                    PlaceCaretAtEndOnFocus = true, CommitOnLostFocus = true, OnCommit = commit, OnCancel = cancel,
                    Parts = parts,
                }) with { Key = "pl-field:" + (int)fontSize },   // its FontSize/Height freeze at mount: a new size remounts it
                new BoxEl
                {
                    Direction = 0, Gap = Spacing.S, Justify = FlexJustify.End,
                    Children =
                    [
                        EditButton(Icons.Accept, Loc.Get(Strings.Detail.Edit.Save), accent: true, p.Save),
                        EditButton(Icons.Cancel, Loc.Get(Strings.Detail.Edit.Cancel), accent: false, p.Cancel),
                    ],
                },
            ],
        };
    }
}
