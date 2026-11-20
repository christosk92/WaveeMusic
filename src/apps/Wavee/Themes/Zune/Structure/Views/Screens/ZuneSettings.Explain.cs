// ── Themes/Zune/Structure/Views/Screens/ZuneSettings.Explain.cs — "see it", the clip under a row, and the arrival's lit label (settings rework phase 4) ─
//
// Design 6.5 (explanation badges) in Zune: no boxes. A dial whose effect is something you see or hear carries an accent "see it" word after its label
// (`settings:explain:<dialId>`); a press opens its clip INLINE under the row (the clip, or its poster under reduced motion with a "play" word, or a quiet
// placeholder of the clip's size while the clip is not recorded yet), the caption under it in the hint voice; a second press closes it. One clip is open at a
// time. The clip under the row is Zune's template of `ViewKeys.Explainer` (`ExplainerProps`, contract 1.115), so an in-context panel that mounts the key gets
// the same picture; it opens with the drawer reveal (`Design.Reveal.Drawer`), so the rows below ride its edge.
//
// Design 4.9 / 7.3 (the arrival's highlight): the arrival's row's label takes the accent and returns to the ink over the policy's `HighlightMs` (coloured words,
// not plates), once per link (a remounted lane never replays it); reduced motion holds the accent for the same time and then cuts back.

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
using ViewSlots = FluentGpu.Theming.Slots;
using Z = Wavee.Themes.Zune.ZuneSettingsRules;

namespace Wavee.Themes.Zune;

internal static partial class ZuneSettings
{
    /// <summary>The dial whose clip is open under its row (empty for none): one at a time.</summary>
    static readonly Signal<string> s_explainOpen = new("");

    /// <summary>The accent "see it" word after a label: opens the dial's clip under its row, or closes it.</summary>
    static Element SeeIt(ZuneKit k, string dialId) => new BoxEl
    {
        Key = SettingsKeys.Explain(dialId), Direction = 0, AlignItems = FlexAlign.Center, Shrink = 0f, MinHeight = k.Lay.Target, MinWidth = k.Lay.Target,
        Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand,
        OnClick = () => s_explainOpen.Value = string.Equals(s_explainOpen.Peek(), dialId, StringComparison.Ordinal) ? "" : dialId,
        Children = [ZuneText.Command(Loc.Get(Strings.Theme.Zune.Screens.SettingsSeeIt)) with { Color = R.Ink.AccentDecor, HoverColor = Tok.AccentSecondary }],
    }.Interactive(Interaction.Subtle);

    /// <summary>The clip's place under a row: the explanation view (Zune's own template), open while the row's "see it" says so.</summary>
    static Element ExplainSlot(SettingsExplainInfo info) => ViewSlots.View(ViewKeys.Explainer, info.Props);

    /// <summary>The registered template of <see cref="ViewKeys.Explainer"/>: the clip under its row (see the file header).</summary>
    public static Element ExplainerTemplate(ExplainerProps props, in ViewContext ctx)
        => Embed.Comp(props, static () => new ExplainPanel()) with { Key = "zs:explain:" + props.DialId };

    /// <summary>The clip under a row while its "see it" is open: mounted to open (the drawer's reveal), removed to close.</summary>
    sealed class ExplainPanel : ZuneScreenComponent
    {
        readonly Signal<bool> _play = new(false);

        public override Element Render()
        {
            var p = UseProps<ExplainerProps>();
            var lay = UseLayout();
            bool open = string.Equals(s_explainOpen.Value, p.DialId, StringComparison.Ordinal);
            return new BoxEl
            {
                Direction = 1, AlignSelf = FlexAlign.Stretch, MinWidth = 0f,
                Children = open ? [Clip(p, lay)] : [],
            };
        }

        Element Clip(ExplainerProps p, ZuneSettingsLayout lay)
        {
            // Reduced motion shows the poster (the clip's first frame) and a play word instead of the loop (a value, not a branch).
            bool playing = !p.ReducedMotion || _play.Value;
            var play = _play;
            Element picture = p.ClipPath is { } path
                ? new ImageEl { Source = path, Fit = ImageFit.Contain, Width = Z.ClipW, Height = Z.ClipH, Animate = playing, Placeholder = Tok.FillSubtleSecondary }
                : new BoxEl
                {
                    Width = Z.ClipW, Height = Z.ClipH, Shrink = 1f, MinWidth = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                    BorderWidth = Z.ClipRing, BorderColor = Tok.TextPrimary with { A = Z.ClipRingA }, HitTestVisible = false,
                    Children = [Icon(Icons.Play, Z.ClipGlyph, Tok.TextTertiary)],
                };
            var kids = new List<Element>(3) { picture };
            if (p.ClipPath is not null && !playing)
                kids.Add(new ZuneKit(lay).Cmd(Loc.Get(Strings.Theme.Zune.Screens.SettingsPlayClip), () => play.Value = true, accent: true) with { Key = "zs:explain:play:" + p.DialId });
            kids.Add(ZuneText.Hint(p.Caption) with { MaxWidth = Z.ClipW });
            return new BoxEl
            {
                Key = "zs:explain:clip:" + p.DialId, Direction = 1, Gap = Z.ClipGap, AlignSelf = FlexAlign.Stretch, MinWidth = 0f, AlignItems = FlexAlign.Start,
                Padding = new Edges4(0f, Z.ClipGap, 0f, Z.ClipGap), ClipToBounds = true, Animate = Design.Reveal.Drawer,
                Children = kids.ToArray(),
            };
        }
    }

    // ── the arrival's lit label ───────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A row's label: lit (the accent fading back to the ink) while it is the arrival's dial, else the label as it is.</summary>
    static Element LabelOf(SettingsArrival focus, string dialId, TextEl label)
        => focus.IsNone || !string.Equals(focus.DialId, dialId, StringComparison.Ordinal)
            ? label
            : Embed.Comp(new LitProps(label, focus.Seq, focus.HighlightMs), static () => new LitLabel());

    sealed record LitProps(TextEl Label, int Seq, int HighlightMs);

    /// <summary>The last arrival whose label lit (a remounted row shows the ink).</summary>
    static int s_litSeq;

    /// <summary>The arrival's label: the accent at once, then back to the ink over the highlight (reduced motion: the accent held, then a cut).</summary>
    sealed class LitLabel : Component
    {
        readonly Signal<int> _faded = new(0);
        int _seq;
        bool _lit;

        public override Element Render()
        {
            var p = UseProps<LitProps>();
            int seq = p.Seq;
            if (seq != _seq)
            {
                _seq = seq;
                _lit = seq > s_litSeq;
                if (_lit) s_litSeq = seq;
            }
            bool reduced = Design.Reduced;
            var faded = _faded;
            UseTimeout(() => faded.Value = seq, reduced ? p.HighlightMs : Z.HighlightLeadMs, DepKey.From(seq));
            bool accent = _lit && _faded.Value != seq;
            // Both inks static: a bound colour snaps, and the return to the ink is the fade.
            return p.Label with
            {
                Color = accent ? R.Ink.AccentDecor : Tok.TextPrimary,
                BrushTransitionMs = _lit && !accent && !reduced ? p.HighlightMs : float.NaN,
            };
        }
    }
}
