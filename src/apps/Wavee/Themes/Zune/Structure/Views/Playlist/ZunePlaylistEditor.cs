// ── Themes/Zune/Structure/Views/Playlist/ZunePlaylistEditor.cs — Zune's inline playlist editor and status (themes plan 6.3; P5 Z6b1) ─────────────
//
// Boards Z-Playlist-Owned-Followed ("the title and description are the editor (hover underlines them)"), Z-State-Empty-Playlist. The page keeps the state
// machine (editing, the draft, the status 0 / 1 / 2, "a create that navigated opens IN edit mode", the "Saved" chip's hold), the permission decision and the
// verbs (Begin, Commit); this draws the two arms:
//
//   read   the text itself (the frame's title run in the heading voice as written; the description in the 13/20 secondary voice, a rich-text run with
//          links), a hairline under it that rises on hover and the pencil that appears with it. A blank text shows its placeholder in the tertiary ink.
//   edit   the engine's field (focus WITHOUT a visual cue: the caret at the end, nothing selected) over two words, "save" in the accent and "cancel".
//
// ONE box across read <-> edit whose height eases; the status ("saving" / "saved") stands BELOW it, never inline, so the title's measure never moves.
// Numbers: `Rules\ZunePlaylistRules.cs`. A name is never re-cased; the words the theme writes (the placeholders, save, cancel, the status) are.

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
using Wavee.Theming.Roles;
using Z = Wavee.Themes.Zune.ZunePlaylistRules;

namespace Wavee.Themes.Zune;

internal static partial class ZunePlaylist
{
    // ══ 1. THE STATUS AND THE EDIT WORDS ════════════════════════════════════════════════════════════════════════════════

    /// <summary>The "saving" / "saved" status: a ring or a tick and the word, flat. The editors' status row and the cover's saving state.</summary>
    static Element Status(bool saved) => new BoxEl
    {
        Key = "pl-status", Direction = 0, Gap = Z.StatusGap, AlignItems = FlexAlign.Center, Shrink = 0f,
        Children =
        [
            new BoxEl
            {
                Key = saved ? "ico:saved" : "ico:saving", Width = Z.StripRing, Height = Z.StripRing, Shrink = 0f,
                AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Animate = MotionRecipes.IconSwap,
                Children =
                [
                    saved
                        ? Ui.Icon(Icons.Accept, Z.StripRing, Tok.AccentTextPrimary)
                        : ProgressRing.Indeterminate(Z.StripRing, foreground: Tok.TextSecondary),
                ],
            },
            new BoxEl
            {
                Key = saved ? "txt:saved" : "txt:saving", Animate = MotionRecipes.TextSwap,
                Children =
                [
                    R.Type.MicroMeta(Loc.Get(saved ? Strings.Detail.Edit.Saved : Strings.Detail.Edit.Saving)) with
                    {
                        Color = Tok.TextSecondary, Case = R.Case.For(LabelKind.Label), MaxLines = 1, Wrap = TextWrap.NoWrap,
                    },
                ],
            },
        ],
    };

    /// <summary>A flat command word with a <paramref name="target"/>-tall hit edge ("save", "cancel", "refresh", "add"): the label voice, lowercased as a command,
    /// the accent ink where it is the primary word.</summary>
    static Element Word(string key, string label, bool accent, Action onClick, float target) => new BoxEl
    {
        Key = key, Direction = 0, Height = target, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Padding = new Edges4(Z.EditWordPad, 0f, Z.EditWordPad, 0f),
        HoverFill = R.Layer.RowHover, PressedFill = R.Layer.RowPressed,
        Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false, Cursor = CursorId.Hand, BlocksDragArm = true, OnClick = onClick,
        Children =
        [
            R.Type.DenseTitle(label) with
            {
                Color = accent ? Tok.AccentTextPrimary : Tok.TextSecondary, HoverColor = accent ? Tok.AccentTextPrimary : Tok.TextPrimary,
                Case = R.Case.For(LabelKind.Command), MaxLines = 1, Wrap = TextWrap.NoWrap,
            },
        ],
    };

    // ══ 2. THE INLINE EDITOR ════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The registered template of <see cref="ViewKeys.PlaylistEditor"/>. The title's read arm is NOT keyed on its size: the props are re-pushed, so the
    /// read arm follows the frame's title rule without a remount. The edit field, whose font size freezes at mount, is keyed on its own size instead.</summary>
    public static Element EditorTemplate(PlaylistEditorProps p, in ViewContext ctx)
    {
        var hooks = ctx.Hooks;
        var hovered = hooks.UseSignal(false);
        var input = hooks.UseContext(InputHooks.Current);
        var post = hooks.UsePost();
        var lay = ZunePlaylistTiers.Of(ctx.Tier());
        // In the panorama the title hangs past the column; where that hang reaches past the column's old clip, the room is the title's glyphs' alone
        // (ZuneDetailKit.PanoramaBand's past): the lines and the ring stand back to the column (audit FU2d).
        float past = hooks.UseContext(ZuneDetailKit.TitlePast);

        Element body = p.Editing ? EditArm(p, input, post, lay, past) : ReadArm(p, hovered, past);
        Element statusRow = new BoxEl
        {
            Key = "pl-status-row", Direction = 0, Justify = FlexJustify.End,
            Height = p.Description && p.Status == 0 ? 0f : float.NaN, ClipToBounds = true,
            Children = p.Status == 0 ? [] : [Status(saved: p.Status == 2)],
        };
        return new BoxEl
        {
            Direction = 1, Gap = p.Status != 0 ? Z.StatusRowGap : 0f, MinWidth = 0f,
            Children =
            [
                new BoxEl { Key = "pl-swap", Direction = 1, MinWidth = 0f, Animate = MotionRecipes.CardResize, Children = [body] },
                statusRow,
            ],
        };
    }

    static Element ReadArm(PlaylistEditorProps p, Signal<bool> hovered, float past)
    {
        bool description = p.Description;
        string text = p.Text;
        bool blank = string.IsNullOrWhiteSpace(text);
        string shown = blank ? Loc.Get(description ? Strings.Detail.Edit.DescriptionPlaceholder : Strings.Detail.Edit.NamePlaceholder) : text;

        Element run;
        if (description)
        {
            run = blank
                ? R.Type.DenseMeta(shown) with
                {
                    Size = Z.DescriptionSize, Color = Tok.TextTertiary, Grow = 1f, Basis = 0f, MinWidth = 0f, Wrap = TextWrap.Wrap,
                    Case = R.Case.For(LabelKind.Label),
                }
                : Controls.RichTextFlex(text, Z.DescriptionSize, Tok.TextSecondary, p.Accent(), Z.DescriptionLines, p.NavRoute);
        }
        else
        {
            var title = R.Type.Heading(shown);
            // In the panorama (a step above the heading voice) the read arm FITS like the plain title (ZuneDetailKit.Panorama: one line first, then two
            // lines at the floor, then the ellipsis); the rail's heading-voice title has no floor and wraps as before.
            float floor = ZuneDetailKit.PanoramaFloor(p.Size);
            bool fit = !float.IsNaN(floor);
            // A title is a name (as written); the placeholder is a word the theme writes (lowercased by the heading case role).
            run = title with
            {
                Size = p.Size, LineHeight = float.IsNaN(p.LineHeight) ? title.LineHeight : p.LineHeight,
                LineStacking = float.IsNaN(p.LineHeight) ? title.LineStacking : LineStacking.BlockLineHeight, Grow = 1f, Basis = 0f, MinWidth = 0f,
                Wrap = TextWrap.WrapWholeWords, MaxLines = fit ? ZuneDetailRules.Compact.PanoramaLines : Detail.VerticalLayout.TitleLineCap,
                Trim = TextTrim.CharacterEllipsis, MinSize = floor, FitMode = fit ? TextFitMode.OneLineFirst : TextFitMode.Lines,
                Color = blank ? Tok.TextTertiary : Tok.TextPrimary, Case = blank ? R.Case.For(LabelKind.Heading) : TextCase.AsWritten,
            };
        }
        if (!p.Editable) return run;

        int status = p.Status;
        // The hairline is the run's underline (the boards: a text decoration, faint at rest, raised on hover), not a rule across the column: the run
        // hugs its words (it still wraps at the column) and the hairline takes the run's width, offset clear of its descenders.
        // `past` (the panorama's hang past the column's old clip): the arm stands at the column and its words alone hang back over that room, so the
        // hairline, the hit area and the focus ring keep the column's edge (the ring opens no wider there) and the words wrap at the same measure.
        Edges4 hang = past > 0f ? new Edges4(-past, 0f, 0f, 0f) : default;
        Element words = run.Is(out TextEl t) ? t with { Grow = 0f, Basis = float.NaN, Shrink = 1f, MinWidth = 0f, Margin = hang }
            : run.Is(out SpanTextEl s) ? s with { Grow = 0f, Basis = float.NaN, Shrink = 1f, MinWidth = 0f, Margin = hang }
            : run;
        return new BoxEl
        {
            Key = Z.EditorReadKey, Direction = 1, MinWidth = 0f,
            Margin = past > 0f ? new Edges4(past, 0f, 0f, 0f) : default,
            FocusVisualMargin = past > 0f ? new Edges4(0f, -3f, -3f, -3f) : null,
            Role = AutomationRole.Button, Cursor = CursorId.IBeam, Focusable = true, AllowFocusOnInteraction = false, OnClick = p.Begin,
            OnHoverMove = _ => { if (!hovered.Peek()) hovered.Value = true; },
            OnPointerExit = () => hovered.Value = false,
            Enter = new EnterExit(Opacity: 0f, Active: true),
            Children =
            [
                new BoxEl
                {
                    Direction = 0, Gap = Spacing.S, MinWidth = 0f, AlignItems = description ? FlexAlign.Start : FlexAlign.Center,
                    Children =
                    [
                        new BoxEl
                        {
                            Direction = 1, Shrink = 1f, MinWidth = 0f,
                            Children =
                            [
                                words,
                                new BoxEl
                                {
                                    Height = Z.UnderlineH, Margin = new Edges4(0f, Z.UnderlineGapFor(description ? Z.DescriptionSize : p.Size), 0f, 0f),
                                    Fill = Tok.TextPrimary, HitTestVisible = false,
                                    Opacity = (Func<float>)(() => hovered.Value ? 1f : Z.UnderlineRest), Transition = MotionTok.ControlFast,
                                },
                            ],
                        },
                        new BoxEl
                        {
                            Width = Z.PencilBox, Height = Z.PencilBox, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, HitTestVisible = false,
                            Opacity = (Func<float>)(() => hovered.Value && status == 0 ? 1f : 0f), Transition = MotionTok.ControlFast,
                            Children = [Ui.Icon(Icons.Edit, Z.PencilGlyph, Tok.TextSecondary)],
                        },
                    ],
                },
            ],
        };
    }

    /// <summary>The field root's layers after its content row (its lines), set in from the leading edge by <paramref name="past"/>.</summary>
    static Element[] StandBack(Element[] layers, float past)
    {
        var kids = new Element[layers.Length];
        for (int i = 0; i < layers.Length; i++)
            kids[i] = i > 0 && layers[i].Is(out BoxEl line) ? line with { Margin = new Edges4(past, 0f, 0f, 0f) } : layers[i];
        return kids;
    }

    static Element EditArm(PlaylistEditorProps p, InputHooks? hooks, Action<Action> post, ZunePlaylistLayout lay, float past)
    {
        bool description = p.Description;
        float fieldH = description ? Z.DescriptionFieldH : p.Size + Z.TitleFieldExtra;
        float fontSize = description ? Z.DescriptionSize : p.Size;
        var draft = p.Draft();
        var commit = p.Commit;
        var cancel = p.Cancel;
        // Focus WITHOUT a visual cue; with PlaceCaretAtEndOnFocus the caret sits at the end and nothing is selected.
        var parts = new TemplateParts
        {
            // The field keeps the read arm's origin (its words hang like the title's); its lines (every layer after the content row: the resting
            // underline, the focus bar) stand back to the column where the hang reaches past the column's old clip (`past`, audit FU2d).
            [EditableText.PartRoot] = b => b with
            {
                OnRealized = n => post(() => hooks?.FocusNode?.Invoke(n, false)),
                Children = past > 0f ? StandBack(b.Children, past) : b.Children,
            },
        };
        string placeholder = Loc.Get(description ? Strings.Detail.Edit.DescriptionPlaceholder : Strings.Detail.Edit.NamePlaceholder);
        return new BoxEl
        {
            Key = Z.EditorEditKey, Direction = 1, Gap = Spacing.S, MinWidth = 0f,
            Children =
            [
                Embed.Comp(() => new EditableText
                {
                    Text = draft, Width = float.NaN, Height = fieldH, FontSize = fontSize, Placeholder = placeholder,
                    PlaceCaretAtEndOnFocus = true, CommitOnLostFocus = true, OnCommit = commit, OnCancel = cancel,
                    Parts = parts,
                }) with { Key = "pl-field:" + (int)fontSize },   // its FontSize / Height freeze at mount: a new size remounts it
                new BoxEl
                {
                    Direction = 0, Gap = Z.EditWordsGap, Justify = FlexJustify.End,
                    Children =
                    [
                        Word("pl-edit:save", Loc.Get(Strings.Detail.Edit.Save), accent: true, p.Save, lay.Target),
                        Word("pl-edit:cancel", Loc.Get(Strings.Detail.Edit.Cancel), accent: false, p.Cancel, lay.Target),
                    ],
                },
            ],
        };
    }
}
