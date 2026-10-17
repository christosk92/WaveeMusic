// ── Themes/Classic/Structure/Views/Player/ClassicPlayerBarPodcast.cs — Classic's podcast transport (themes plan 6.1, T6a) ────
//
// From `Shell.PlayerBar.Podcast.UI.cs`: the ±10 s step buttons, the speed button and the speed panel it opens. They sit in the
// transport beside the primary when the deck holds an episode. What a press DOES is the verbs' (`IPlayerVerbs.SeekBy`, `SetSpeed`:
// ONE seek straight from the reported position, no accumulator), and what the podcast bar carries at each level is the policy's
// (`PlayerTier.PodcastSet`); this file draws.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Classic;

internal static partial class ClassicPlayerBar
{
    /// <summary>A ±10 s step button: a box with the signed label, armed while the rail can be scrubbed.</summary>
    private static Element SeekStepButton(PlayerBarProps props, int deltaMs, float box, bool enabled)
    {
        var verbs = props.Verbs;
        return ToolTip.Wrap(new BoxEl
        {
            Width = box, Height = box, Shrink = 0f,
            Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Corners = Radii.ControlAll, HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary,
            Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false,
            IsEnabled = enabled, Cursor = enabled ? CursorId.Hand : null,
            OnClick = () => verbs.SeekBy(deltaMs),
            Children = [new TextEl(deltaMs < 0 ? "−10" : "+10")
                { Size = ClassicPlayerBarLayout.StepLabelSize, Weight = ClassicPlayerBarLayout.StepLabelWeight,
                  Color = enabled ? Tok.TextSecondary : Tok.TextDisabled, HoverColor = Tok.TextPrimary }],
        }, Loc.Get(deltaMs < 0 ? "player.seekBack10" : "player.seekForward10"));
    }

    /// <summary>The speed button: the current rate as text; a press opens the speed panel upward.</summary>
    private sealed class EpisodeSpeed(PlayerBarProps props, float width, float height) : Component
    {
        readonly PlayerBarProps _props = props;
        readonly float _width = width, _height = height;

        public override Element Render()
        {
            var props = _props;
            var anchor = UseRef<NodeHandle>(default);
            var handle = UseRef<OverlayHandle?>(null);
            var overlay = UseContext(Overlay.Service);
            UseEffect(() => () => handle.Value?.Close(), DepKey.Empty);
            void Toggle()
            {
                if (handle.Value is { IsOpen: true } open) { open.Close(); return; }
                handle.Value = overlay.Open(() => anchor.Value,
                    () => Embed.Comp(() => new EpisodeSpeedPanel(props)),
                    FlyoutPlacement.TopEdgeAlignedLeft,
                    new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss));
                handle.Value.ClosedAction = () => handle.Value = null;
            }
            return ToolTip.Wrap(new BoxEl
            {
                Width = _width, Height = _height, Shrink = 0f,
                Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Corners = Radii.ControlAll, HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary,
                Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false, Cursor = CursorId.Hand,
                OnRealized = node => anchor.Value = node, OnClick = Toggle,
                Children = [new TextEl(props.RateText(props.EpisodeSpeed.Value))
                    { Size = ClassicPlayerBarLayout.StepLabelSize, Weight = ClassicPlayerBarLayout.StepLabelWeight, Color = Tok.TextSecondary }],
            }, Loc.Get("podcast.reader.speed"));
        }
    }

    /// <summary>The speed panel: a slider over the 0.5-3 band and four presets.</summary>
    private sealed class EpisodeSpeedPanel(PlayerBarProps props) : Component
    {
        readonly PlayerBarProps _props = props;
        readonly Slider.SliderOptions _options = new()
        {
            Min = ClassicPlayerBarLayout.SpeedMin, Max = ClassicPlayerBarLayout.SpeedMax,
            Step = ClassicPlayerBarLayout.SpeedStep, SmallChange = ClassicPlayerBarLayout.SpeedStep,
            LargeChange = ClassicPlayerBarLayout.SpeedLargeStep,
            ThumbToolTipValueConverter = props.RateText,
        };

        public override Element Render()
        {
            var props = _props;
            var verbs = props.Verbs;
            var value = UseFloatSignal(props.EpisodeSpeed.Peek());
            UseSignalEffect(() => value.SetIfChanged(props.EpisodeSpeed.Value));
            Element Preset(float rate) => FluentGpu.Controls.Button.Create(props.RateText(rate),
                () => verbs.SetSpeed(rate), ButtonAppearance.Subtle, ControlSize.Small);
            var presets = new List<Element>(ClassicPlayerBarLayout.SpeedPresets.Length);
            foreach (float rate in ClassicPlayerBarLayout.SpeedPresets) presets.Add(Preset(rate));
            return new BoxEl
            {
                Width = ClassicPlayerBarLayout.SpeedPanelWidth, Direction = 1, Gap = Spacing.S, Padding = Edges4.All(Spacing.M),
                Children =
                [
                    FluentGpu.Dsl.Ui.Text(Loc.Get("podcast.reader.speed")),
                    Slider.Create(value, rate => verbs.SetSpeed(rate), _options, length: ClassicPlayerBarLayout.SpeedSliderLength),
                    new BoxEl { Direction = 0, Gap = Spacing.XXS, Justify = FlexJustify.SpaceBetween, Children = presets.ToArray() },
                ],
            };
        }
    }
}
