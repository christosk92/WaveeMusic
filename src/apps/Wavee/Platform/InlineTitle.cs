// ── Platform/InlineTitle.cs ────────────────────────────────────────────────────────────────────────────────────────
// An editable title, in place (#240 version cards: a version of mine in Track details). At rest it is the title as its template styled it,
// with a pencil after it that shows on hover and the text cursor over it; a click (or F2 on its card: InlineTitle.Begin) turns the title into
// a chromeless text field in the SAME box: the title stays laid out underneath (invisible), so nothing around it moves. Enter or a click
// elsewhere commits, Esc cancels. The commit is the caller's (a rename with its Undo toast). One title edits at a time, app-wide.
//
// Role: SHELL (UI thread)

using FluentGpu;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;
namespace Wavee;

/// <summary>An editable title, in place (see the file header).</summary>
public static class InlineTitle
{
    /// <summary>The hover pencil after the title, the air before it, and the field's corners while it edits.</summary>
    const float PencilGlyph = 12f, PencilGap = 6f, FieldRadius = 3f;

    static readonly Signal<string?> s_editing = new(null);

    /// <summary>The key of the title being edited, or null.</summary>
    public static IReadSignal<string?> Editing => s_editing;

    /// <summary>Start editing the title keyed <paramref name="key"/> (F2 on its card, a click on it). UI thread.</summary>
    public static void Begin(string key) => s_editing.Value = key;

    static void End(string key)
    {
        if (s_editing.Peek() == key) s_editing.Value = null;
    }

    /// <summary>The title <paramref name="look"/> (the template's own TextEl) of <paramref name="title"/>, editable in place; <paramref name="commit"/>
    /// takes the new text (trimmed, changed, not empty).</summary>
    public static Element Of(string key, string title, TextEl look, Action<string> commit)
        => Embed.Comp(new Props(key, title, look, commit), static () => new Host()) with { Key = "ititle:" + key };

    sealed record Props(string Key, string Title, TextEl Look, Action<string> Commit);

    sealed class Host : Component
    {
        Props? _p;
        EditableText? _editor;
        Action? _begin;
        Action<string>? _commit;
        Action? _cancel;

        public override Element Render()
        {
            var p = UseProps<Props>();
            _p = p;
            var hooks = UseContext(InputHooks.Current);
            var post = UsePost();
            bool editing = s_editing.Value == p.Key;
            _begin ??= () => { if (_p is { } q) Begin(q.Key); };
            _commit ??= v =>
            {
                if (_p is not { } q) return;
                End(q.Key);
                string t = v.Trim();
                if (t.Length > 0 && t != q.Title) q.Commit(t);
            };
            _cancel ??= () => { if (_p is { } q) End(q.Key); };
            // The title keeps its box while it is edited (invisible under the field): the card's text column never moves.
            Element shown = new BoxEl
            {
                MinWidth = 0f, Shrink = 1f, Opacity = editing ? 0f : 1f, HitTestVisible = !editing,
                Children = [p.Look with { MaxLines = 1, Trim = TextTrim.CharacterEllipsis, Shrink = 1f, MinWidth = 0f }],
            };
            var layers = new List<Element>(2) { shown };
            if (editing)
            {
                var parts = new TemplateParts();
                parts[EditableText.PartRoot] = b => b with
                {
                    Height = float.NaN, Corners = CornerRadius4.All(FieldRadius),
                    // The field mounts because the user asked to edit: it takes focus with its text selected.
                    OnRealized = h => post(() => { hooks.FocusNode?.Invoke(hooks.FirstFocusableIn?.Invoke(h) ?? h, false); _editor?.SelectAll(); }),
                };
                parts[EditableText.PartLane] = b => b with { Padding = default };
                ushort weight = p.Look.Weight;
                parts.Set<TextEl>(EditableText.PartText, t => t with { Weight = weight });
                string initial = p.Title;
                float size = p.Look.Size;
                var commit = _commit;
                var cancel = _cancel;
                layers.Add(Embed.Comp(() => _editor = new EditableText
                {
                    Initial = initial, Width = float.NaN, Height = float.NaN, FontSize = size, Chromeless = true,
                    CommitOnLostFocus = true, PlaceCaretAtEndOnFocus = true, OnCommit = commit, OnCancel = cancel, Parts = parts,
                }) with { Key = "ititle-field" });
            }
            return new BoxEl
            {
                // At rest it hugs the title (the pencil, the I-beam and the click are the title's own); editing, the field spans the column.
                Direction = 0, AlignItems = FlexAlign.Center, Gap = PencilGap, MinWidth = 0f, Shrink = 1f, Grow = editing ? 1f : 0f,
                AlignSelf = editing ? FlexAlign.Stretch : FlexAlign.Start,
                Cursor = editing ? null : CursorId.IBeam, OnClick = editing ? null : _begin,
                Fill = ColorF.Transparent, HoverFill = ColorF.Transparent, PressedFill = ColorF.Transparent,
                Children =
                [
                    new BoxEl { ZStack = true, MinWidth = 0f, Shrink = 1f, Grow = editing ? 1f : 0f, Children = layers.ToArray() },
                    // The pencil rides the title's hover (a non-interactive wrapper); its slot is always laid out, so it never moves the title.
                    new BoxEl
                    {
                        Key = "ititle-pencil", Shrink = 0f, HitTestVisible = false, Opacity = 0f, HoverOpacity = editing ? 0f : 1f,
                        Children = [Icon(Icons.Edit, PencilGlyph, Tok.TextSecondary)],
                    },
                ],
            };
        }
    }
}
