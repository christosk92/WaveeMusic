// ── Themes/Classic/Structure/Views/Screens/ClassicSettings.Explain.cs — Classic's explanation badge and its tip (settings rework 6.5, 7.1) ──────────
//
// A dial whose effect is something you see or hear carries an info glyph button beside its control (`settings:explain:<dialId>`): hover shows a "Show me"
// tooltip only; a click, a tap or Enter opens its flyout card in Classic's plate (light dismiss): the 320 x 180 clip, looping while the tip is open, over the
// one-line caption (also the clip's alt text). Reduced motion shows the clip's poster frame and a play button instead of playing it (a value, not a branch). A
// dial whose clip is not recorded yet (the phase 6 recording lands them) shows the clip's place as a poster with the play glyph, so the tip keeps its size and
// its caption. Registered for `ViewKeys.Explainer` too, so an in-context panel mounts the same badge.
//
// PLACEMENT. The house positioner places the card under its badge, left edges aligned, and clamps it into the window FLUSH with the edge, while a Classic card
// keeps the structure's window margin (`OverlayFit.Layout.Dialog.Inset`, Classic's overlay tier records). So the card is never wider than the window less that
// margin on each side (the clip keeps 16 : 9 inside it), and the open nudges it (`PopupOptions.AnchorOffsetX`) from where the positioner would land it to the
// nearest spot that keeps the margin: still under its badge, as close to left-aligned as the margin allows.

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
                var (width, nudge) = Fit(Context.Scene, anchor.Value);
                var tip = new ExplainTipProps(props, width);
                var opened = svc.Open(() => anchor.Value, () => Embed.Comp(tip, static () => new ExplainTip()),
                    FlyoutPlacement.BottomEdgeAlignedLeft,
                    new PopupOptions(FocusTrap: false, DismissBehavior: DismissBehavior.LightDismiss) { ConstrainToRootBounds = true, AnchorOffsetX = nudge });
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

        /// <summary>The card's width (the tip's own, or the window less the structure's margin on each side) and the nudge that moves it from where
        /// <see cref="FlyoutPlacement.BottomEdgeAlignedLeft"/> lands (left-aligned to the badge, clamped flush into the window) to where it keeps that margin.</summary>
        static (float Width, float Nudge) Fit(FluentGpu.Scene.SceneStore? scene, NodeHandle badge)
        {
            float window = OverlayFit.Viewport.Width;
            float margin = OverlayFit.Layout.Dialog.Inset;
            float width = MathF.Min(ClassicSettingsRules.ExplainTipWidth, MathF.Max(1f, window - 2f * margin));
            if (scene is null || badge.IsNull || !scene.IsLive(badge)) return (width, 0f);
            float left = scene.AbsoluteRect(badge).X;
            float lands = Math.Clamp(left, 0f, MathF.Max(0f, window - width));
            float keeps = Math.Clamp(left, margin, MathF.Max(margin, window - margin - width));
            return (width, keeps - lands);
        }
    }

    /// <summary>What the tip draws: the dial's explanation and the card's width (the tip's own, narrower where the window is).</summary>
    sealed record ExplainTipProps(ExplainerProps Explain, float Width);

    /// <summary>The tip: the clip (or its poster) over the caption, in the flyout's plate.</summary>
    sealed class ExplainTip : Component
    {
        public override Element Render()
        {
            var tip = UseProps<ExplainTipProps>();
            var p = tip.Explain;
            var (played, setPlayed) = UseState(false);
            bool playing = !p.ReducedMotion || played;
            // The clip fills the card inside its padding at the clip's own 16 : 9 (320 x 180 in the tip's own width).
            float clipW = MathF.Min(ClassicSettingsRules.ExplainClipWidth, MathF.Max(1f, tip.Width - 2f * ClassicSettingsRules.ExplainTipPadding));
            float clipH = clipW * ClassicSettingsRules.ExplainClipHeight / ClassicSettingsRules.ExplainClipWidth;
            Element clip = p.ClipPath is { } path
                ? new BoxEl
                {
                    ZStack = true, Width = clipW, Height = clipH, Shrink = 0f,
                    Corners = Radii.ControlAll, ClipToBounds = true, Fill = Tok.FillSolidBase,
                    Children = playing
                        ? [ClipImage(path, clipW, clipH, animate: true)]
                        : [ClipImage(path, clipW, clipH, animate: false), PlayButton(() => setPlayed(true))],
                }
                : new BoxEl
                {
                    Width = clipW, Height = clipH, Shrink = 0f,
                    Corners = Radii.ControlAll, Fill = Tok.FillSolidBase,
                    BorderWidth = ClassicSettingsRules.ExplainPosterBorder, BorderColor = Tok.StrokeControlDefault,
                    AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, HitTestVisible = false,
                    Children = [Icon(Icons.Play, ClassicSettingsRules.ExplainPosterGlyph, Tok.TextSecondary)],
                };
            return new BoxEl
            {
                Key = "settings:explain-tip:" + p.DialId, Direction = 1, Gap = ClassicSettingsRules.ExplainGap,
                Width = tip.Width, Padding = Edges4.All(ClassicSettingsRules.ExplainTipPadding),
                Children =
                [
                    clip,
                    Caption(p.Caption) with { Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, MinWidth = 0f },
                ],
            };
        }

        static Element ClipImage(string path, float width, float height, bool animate) => new ImageEl
        {
            Source = path, Fit = ImageFit.Cover, Width = width, Height = height,
            DecodePx = ClassicSettingsRules.ExplainClipDecodePx, Placeholder = ColorF.Transparent, Animate = animate,
        };

        static Element PlayButton(Action play) => new BoxEl
        {
            AlignSelf = FlexAlign.Center, JustifySelf = FlexAlign.Center,
            Children = [ToolTip.Wrap(IconButton.Create(Icons.Play, play) with { Key = "settings:explain-play" }, Loc.Get(Strings.Theme.Classic.Settings.Play))],
        };
    }
}
