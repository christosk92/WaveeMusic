// ── Themes/Zune/Structure/Views/Home/Board/ZuneHomeUndoWord.cs — undo by the title (spec 8.2 "Undo"; owner 2026-11-16) ────────────────────
//
// "undo should not be here [the app bar] but instead on hover on the title somewhere for zune". Every title (the page's and each group's) keeps a
// slot for a lowercase "undo" word in its own line; the word shows by the ONE title the last change belongs to (`ZuneHomeBoard.UndoAt`: the group
// the change was in while it shows, else the page title, and always the page title while all items is open). Hover on the title or the word, or
// focus on the word, shows it at full strength; after each change it shows for 5 s, then rests at half strength, still a target (touch has no
// hover). A press undoes one step (50 deep) and the word moves to the title of the step before. Keyed `home:undo` where it shows.
//
// The slot is always laid out (the word's width is reserved), so the word arriving or leaving moves nothing: only its opacity changes (seeded on
// its own node, a 200 ms fade; reduced motion keeps the fade, it has no travel).

using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Theming.Roles;
using B = Wavee.Themes.Zune.ZuneHomeBoardRules;

namespace Wavee.Themes.Zune;

/// <summary>Zune's undo word.</summary>
internal static class ZuneHomeUndoWord
{
    /// <summary>The word slot of the title <paramref name="at"/> ("page" or a group id); <paramref name="hover"/> is the title's hover.</summary>
    public static Element Word(HomeBoardProps board, string at, IReadSignal<bool> hover)
        => Embed.Comp(new WordProps(board, at, hover), static () => new WordView()) with { Key = "home:undo-slot:" + at };

    internal sealed record WordProps(HomeBoardProps Board, string At, IReadSignal<bool> Hover);

    sealed class WordView : Component
    {
        readonly Signal<bool> _fresh = new(false);
        readonly Signal<bool> _focused = new(false);
        readonly Action _stale;
        readonly Action<NodeHandle> _onRealized;
        readonly Action<bool> _onFocus;
        WordProps? _p;
        NodeHandle _node;
        int _version = -1;
        float _shown = -1f;
        float _initial = -1f;
        TimerHandle _timer;

        public WordView()
        {
            _stale = () => _fresh.Value = false;
            _onRealized = h => _node = h;
            _onFocus = f => _focused.Value = f;
        }

        public override Element Render()
        {
            var p = UseProps<WordProps>();
            _p = p;
            var board = p.Board;
            var u = board.Undo.Value;
            bool here = u.CanUndo && ZuneHomeBoard.UndoAt(board) == p.At;
            _timer = UseTimeout(_stale, B.UndoFreshMs);
            // A change (a new undo version) shows the word at full strength for 5 s.
            int version = u.Version;
            bool changed = _version >= 0 && version != _version;
            _version = version;
            UseLayoutEffect(() => { if (changed && here) { _fresh.Value = true; _timer.RestartIn(B.UndoFreshMs); } }, DepKey.From(version));

            float target = !here ? 0f : p.Hover.Value || _focused.Value || _fresh.Value ? 1f : B.UndoRest;
            if (_initial < 0f) _initial = target;
            UseLayoutEffect(() => Fade(target), DepKey.From((int)(target * 100f)));

            return new BoxEl
            {
                Key = here ? HomeBoardKeys.Undo : null,
                Direction = 0, Shrink = 0f, AlignItems = FlexAlign.Center, AlignSelf = FlexAlign.End,
                Padding = new Edges4(B.UndoPadX, B.UndoPadY, B.UndoPadX, B.UndoPadY),
                Opacity = _initial,
                OnRealized = _onRealized,
                Focusable = here, HitTestVisible = here, Role = here ? AutomationRole.Button : AutomationRole.None,
                Cursor = CursorId.Hand, AllowFocusOnInteraction = false,
                OnFocusChanged = _onFocus,
                OnClick = here ? board.Verbs.Undo : null,
                Children =
                [
                    R.Type.CardTitle(Loc.Get(Strings.Theme.Zune.Home.Undo)) with
                    {
                        Color = Tok.TextSecondary, HoverColor = Tok.AccentTextPrimary, Case = R.Case.For(LabelKind.Command), MaxLines = 1, Wrap = TextWrap.NoWrap,
                    },
                ],
            };
        }

        void Fade(float target)
        {
            if (MathF.Abs(target - _shown) < 0.001f) return;
            _shown = target;
            if (Context.Anim is not { } anim || Context.Scene is not { } s || _node.IsNull || !s.IsLive(_node)) return;
            anim.SeedValue(_node, AnimChannel.Opacity, target, MotionTokenDef.Eased(B.UndoFadeMs, Easing.FluentStandard));
        }
    }
}
