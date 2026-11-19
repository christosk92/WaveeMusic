// ── Themes/WinUi/Structure/Views/Screens/WinUiSettings.Explainer.cs — the explanation badge and its TeachingTip (design 6.5, the concept board W-Arrival) ──────
//
// WinUI's template of `ViewKeys.Explainer` (registered through `CommonBlocks.Register`). A dial whose effect you see or hear carries an InfoBadge-style info
// glyph right after its label (the row card's label line, `WinUiSettings.Kit.cs`), keyed `settings:explain:<dialId>`:
//   · hover (or keyboard focus) shows the "Show me" tooltip only; a click, a tap or Enter opens the TEACHING TIP, targeted at the badge, light-dismiss and
//     with no focus trap (the keyboard stays where it was): the 320 x 180 clip as its hero over the one-line caption (the clip's alt text). The open
//     badge is the filled accent dot of the board; a second press closes the tip;
//   · the clip (an animated WebP on disk) plays only while the tip is open, looping; with Windows' animation effects off it shows its poster frame and a
//     play button instead (`ExplainerProps.ReducedMotion` is a value: the clip's `Animate` reads it). Where the clip is not on disk yet (the recording
//     command lands with phase 6), the hero is the clip's placeholder at the same size, so the tip is the same shape either way.

using FluentGpu;
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

internal static partial class WinUiSettings
{
    /// <summary>The registered template of <see cref="ViewKeys.Explainer"/>.</summary>
    public static Element ExplainerTemplate(ExplainerProps props, in ViewContext ctx) => Embed.Comp(props, static () => new ExplainerBadge());

    sealed class ExplainerBadge : SettingsComponent
    {
        NodeHandle _node;
        OverlayHandle? _tip;
        readonly Action<NodeHandle> _onNode;

        public ExplainerBadge() => _onNode = h => _node = h;

        public override Element Render()
        {
            var layout = UseLayout();
            var p = UseProps<ExplainerProps>();
            var overlay = UseContext(Overlay.Service);
            var open = UseSignal(false);
            // Leaving the page (the row unmounts) closes the tip with it.
            UseEffect(() => (Action?)(() => { if (_tip is { IsOpen: true } tip) tip.Close(); }), DepKey.Empty);
            bool isOpen = open.Value;

            void Toggle()
            {
                if (_tip is { IsOpen: true } live)
                {
                    live.Close();
                    return;
                }
                if (overlay is null) return;
                var props = p;
                _tip = TeachingTip.Show(overlay, () => _node, t =>
                {
                    t.IsLightDismissEnabled = true;
                    t.PreferredPlacement = TeachingTip.PlacementMode.Bottom;
                    t.HeroContent = Embed.Comp(props, static () => new ExplainerClip());
                    t.HeroContentPlacement = TeachingTip.HeroPlacement.Top;
                    t.Body = props.Caption;
                    t.Closed = _ => open.Value = false;
                });
                open.Value = true;
            }

            var badge = new BoxEl
            {
                Key = SettingsKeys.Explain(p.DialId), Width = WinUiSettingsRules.ExplainCell, Height = WinUiSettingsRules.ExplainCell, Shrink = 0f,
                AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Corners = Radii.FullAll,
                Fill = isOpen ? Tok.AccentDefault : Tok.FillSubtleTransparent,
                HoverFill = isOpen ? Tok.AccentSecondary : Tok.FillSubtleSecondary, PressedFill = isOpen ? Tok.AccentTertiary : Tok.FillSubtleTertiary,
                HitSlop = layout.Touch ? WinUiSettingsRules.ExplainTouchSlop : WinUiSettingsRules.ExplainSlop,
                Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand, OnRealized = _onNode, OnClick = Toggle,
                Children = [Icon(Icons.Info, WinUiSettingsRules.ExplainGlyph, isOpen ? Tok.TextOnAccentPrimary : Tok.TextSecondary)],
            };
            return ToolTip.Wrap(badge, Loc.Get(Strings.Theme.Winui.Screens.Settings.ShowMe));
        }
    }

    /// <summary>The tip's hero: the clip at 320 x 180 (looping while the tip is open), its poster and a play button under reduced motion, or the clip's
    /// placeholder where the clip is not on disk yet.</summary>
    sealed class ExplainerClip : Component
    {
        public override Element Render()
        {
            var p = UseProps<ExplainerProps>();
            var playing = UseSignal(!p.ReducedMotion);
            bool play = playing.Value;
            var layers = new List<Element>(2);
            if (p.ClipPath is { } path)
                layers.Add(new ImageEl
                {
                    Source = path, Height = WinUiSettingsRules.ExplainClipH, AlignSelf = FlexAlign.Stretch, Fit = ImageFit.Cover,
                    Placeholder = Tok.FillSubtleSecondary, Animate = play,
                });
            if (p.ClipPath is null || !play)
                layers.Add(new BoxEl
                {
                    AlignSelf = FlexAlign.Center, JustifySelf = FlexAlign.Center, Width = WinUiSettingsRules.ExplainPlayCell, Height = WinUiSettingsRules.ExplainPlayCell,
                    AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Corners = Radii.FullAll,
                    Fill = Tok.FillSubtleSecondary, HoverFill = Tok.FillSubtleTertiary, PressedFill = Tok.FillSubtleTertiary,
                    Role = p.ClipPath is null ? AutomationRole.None : AutomationRole.Button, Focusable = p.ClipPath is not null,
                    HitTestVisible = p.ClipPath is not null,
                    OnClick = p.ClipPath is null ? null : () => playing.Value = true,
                    Children = [Icon(Icons.Play, WinUiSettingsRules.ExplainPlayGlyph, Tok.TextSecondary)],
                });
            return new BoxEl
            {
                // The tip's own width (320 to 336) is the hero's: the clip covers it at 180 tall (16 : 9 at 320).
                Key = "settings:explain:clip:" + p.DialId, ZStack = true, AlignSelf = FlexAlign.Stretch, MinWidth = WinUiSettingsRules.ExplainClipW, Height = WinUiSettingsRules.ExplainClipH,
                ClipToBounds = true, Fill = Tok.FillCardSecondary,
                Children = layers.ToArray(),
            };
        }
    }
}
