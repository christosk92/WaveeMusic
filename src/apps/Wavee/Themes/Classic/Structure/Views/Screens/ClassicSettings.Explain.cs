// ── Themes/Classic/Structure/Views/Screens/ClassicSettings.Explain.cs — Classic's explanation badge and its tip (settings rework 6.5, 7.1) ──────────
//
// A dial whose effect is something you see or hear carries an info glyph button beside its control (`settings:explain:<dialId>`): hover shows a "Show me"
// tooltip only; a click, a tap or Enter opens its flyout card in Classic's plate (light dismiss): the 320 x 180 clip, looping while the tip is open, over the
// one-line caption (also the clip's alt text). Reduced motion shows the clip's poster frame and a play button instead of playing it (a value, not a branch). A
// dial whose clip is not recorded yet (the phase 6 recording lands them) shows the clip's place as a poster with the play glyph, so the tip keeps its size and
// its caption. Registered for `ViewKeys.Explainer` too, so an in-context panel mounts the same badge.

using FluentGpu;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Theming;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;

namespace Wavee.Themes.Classic;

internal static partial class ClassicSettings
{
    /// <summary>The registered template of <see cref="ViewKeys.Explainer"/> (a panel's badge; the core mounts it only where the clip exists).</summary>
    public static Element Explainer(ExplainerProps props, in ViewContext ctx) => ExplainBadge(props);

    /// <summary>A dial's badge (see the file header).</summary>
    static Element ExplainBadge(ExplainerProps props) => Embed.Comp(props, static () => new ExplainBadgeView());

    sealed class ExplainBadgeView : Component
    {
        public override Element Render()
        {
            var p = UseProps<ExplainerProps>();
            var anchor = UseRef(NodeHandle.Null);
            var handle = UseRef<OverlayHandle?>(null);
            var (open, setOpen) = UseState(false);
            var svc = UseContext(Overlay.Service);
            ExplainerProps props = p;

            void Toggle()
            {
                if (handle.Value is { IsOpen: true } live) { live.Close(); return; }
                if (svc is null) return;
                var opened = svc.Open(() => anchor.Value, () => Embed.Comp(props, static () => new ExplainTip()),
                    FlyoutPlacement.BottomEdgeAlignedLeft,
                    new PopupOptions(FocusTrap: false, DismissBehavior: DismissBehavior.LightDismiss) { ConstrainToRootBounds = true });
                handle.Value = opened;
                setOpen(true);
                opened.ClosedAction = () =>
                {
                    if (ReferenceEquals(handle.Value, opened)) handle.Value = null;
                    setOpen(false);
                };
            }

            float size = MathF.Max(ClassicSettingsRules.ExplainBadge, s_layout.Target);
            Element badge = new BoxEl
            {
                Key = SettingsKeys.Explain(p.DialId), Width = size, Height = size, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Corners = Radii.PillAll, Fill = open ? Tok.AccentDefault with { A = ClassicSettingsRules.HighlightFillAlpha } : ColorF.Transparent,
                HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary, Cursor = CursorId.Hand, Focusable = true,
                Role = AutomationRole.Button, OnClick = Toggle, OnRealized = h => anchor.Value = h,
                Children = [Icon(Icons.Info, ClassicSettingsRules.ExplainGlyph, open ? Tok.TextPrimary : Tok.TextSecondary)],
            };
            return ToolTip.Wrap(badge, Loc.Get(Strings.Theme.Classic.Settings.ShowMe));
        }
    }

    /// <summary>The tip: the clip (or its poster) over the caption, in the flyout's plate.</summary>
    sealed class ExplainTip : Component
    {
        public override Element Render()
        {
            var p = UseProps<ExplainerProps>();
            var (played, setPlayed) = UseState(false);
            bool playing = !p.ReducedMotion || played;
            Element clip = p.ClipPath is { } path
                ? new BoxEl
                {
                    ZStack = true, Width = ClassicSettingsRules.ExplainClipWidth, Height = ClassicSettingsRules.ExplainClipHeight, Shrink = 0f,
                    Corners = Radii.ControlAll, ClipToBounds = true, Fill = Tok.FillSolidBase,
                    Children = playing
                        ? [ClipImage(path, animate: true)]
                        : [ClipImage(path, animate: false), PlayButton(() => setPlayed(true))],
                }
                : new BoxEl
                {
                    Width = ClassicSettingsRules.ExplainClipWidth, Height = ClassicSettingsRules.ExplainClipHeight, Shrink = 0f,
                    Corners = Radii.ControlAll, Fill = Tok.FillSolidBase,
                    BorderWidth = ClassicSettingsRules.ExplainPosterBorder, BorderColor = Tok.StrokeControlDefault,
                    AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, HitTestVisible = false,
                    Children = [Icon(Icons.Play, ClassicSettingsRules.ExplainPosterGlyph, Tok.TextSecondary)],
                };
            return new BoxEl
            {
                Key = "settings:explain-tip:" + p.DialId, Direction = 1, Gap = ClassicSettingsRules.ExplainGap,
                Width = ClassicSettingsRules.ExplainTipWidth, Padding = Edges4.All(ClassicSettingsRules.ExplainTipPadding),
                Children =
                [
                    clip,
                    Caption(p.Caption) with { Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, MinWidth = 0f },
                ],
            };
        }

        static Element ClipImage(string path, bool animate) => new ImageEl
        {
            Source = path, Fit = ImageFit.Cover, Width = ClassicSettingsRules.ExplainClipWidth, Height = ClassicSettingsRules.ExplainClipHeight,
            DecodePx = ClassicSettingsRules.ExplainClipDecodePx, Placeholder = ColorF.Transparent, Animate = animate,
        };

        static Element PlayButton(Action play) => new BoxEl
        {
            AlignSelf = FlexAlign.Center, JustifySelf = FlexAlign.Center,
            Children = [ToolTip.Wrap(IconButton.Create(Icons.Play, play) with { Key = "settings:explain-play" }, Loc.Get(Strings.Theme.Classic.Settings.Play))],
        };
    }
}
