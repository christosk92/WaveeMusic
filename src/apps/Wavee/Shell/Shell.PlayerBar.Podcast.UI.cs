using System.Globalization;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;

namespace Wavee;

public static partial class Shell
{
    static readonly PlayerSeekAccumulator s_barSeek = new();
    public const float EpisodeSpeedButtonWidth = 48f;

    static void BarSeekBy(int deltaMs)
    {
        if (!SeekRail.Enabled(!Playback.CurrentId.Peek().IsEmpty, Playback.Error.Peek(),
                Playback.PhaseSignal.Peek(), Playback.CanSeek.Peek()))
        {
            s_barSeek.Reset();
            return;
        }
        var state = Playback.Snap();
        long now = Playback.FrameNowMs();
        var live = Playback.Live.Peek();
        long start = live.HasWindow ? live.SeekableStartMs : 0;
        long end = live.HasWindow ? live.SeekableEndMs : Playback.DurationMs.Peek();
        int target = s_barSeek.Step(Playback.CurrentId.Peek(), Entities.ScopeEpoch.Peek(),
            state.Owner, Playback.ActiveDeviceSlot.Peek(), live.HasWindow ? live.PositionMs : state.Position(now),
            end, now, deltaMs, start);
        Playback.SeekTo(target);
    }

    static Element BarSeekStepButton(int deltaMs, float box)
    {
        bool enabled = SeekRail.Enabled(!Playback.CurrentId.Value.IsEmpty, Playback.Error.Value,
            Playback.PhaseSignal.Value, Playback.CanSeek.Value);
        return ToolTip.Wrap(new BoxEl
        {
            Width = box, Height = box, Shrink = 0f,
            Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Corners = Radii.ControlAll, HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary,
            Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false,
            IsEnabled = enabled, Cursor = enabled ? CursorId.Hand : null,
            OnClick = () => BarSeekBy(deltaMs),
            Children = [new TextEl(deltaMs < 0 ? "\u221210" : "+10")
                { Size = 12f, Weight = 600, Color = enabled ? Tok.TextSecondary : Tok.TextDisabled, HoverColor = Tok.TextPrimary }],
        }, Loc.Get(deltaMs < 0 ? "player.seekBack10" : "player.seekForward10"));
    }

    static string EpisodeRateText(float rate) => rate.ToString("0.0#", CultureInfo.CurrentCulture) + "\u00D7";

    sealed class BarEpisodeSpeed : Component
    {
        public override Element Render()
        {
            var anchor = UseRef<NodeHandle>(default);
            var handle = UseRef<OverlayHandle?>(null);
            var overlay = UseContext(Overlay.Service);
            UseEffect(() => () => handle.Value?.Close(), DepKey.Empty);
            void Toggle()
            {
                if (handle.Value is { IsOpen: true } open) { open.Close(); return; }
                handle.Value = overlay.Open(() => anchor.Value,
                    static () => Embed.Comp(static () => new BarEpisodeSpeedPanel()),
                    FlyoutPlacement.TopEdgeAlignedLeft,
                    new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss));
                handle.Value.ClosedAction = () => handle.Value = null;
            }
            return ToolTip.Wrap(new BoxEl
            {
                Width = EpisodeSpeedButtonWidth, Height = PlayerBarLayout.MinButtonBox, Shrink = 0f,
                Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Corners = Radii.ControlAll, HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary,
                Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false, Cursor = CursorId.Hand,
                OnRealized = node => anchor.Value = node, OnClick = Toggle,
                Children = [new TextEl(EpisodeRateText(Playback.EpisodeSpeed.Value)) { Size = 12f, Weight = 600, Color = Tok.TextSecondary }],
            }, Loc.Get("podcast.reader.speed"));
        }
    }

    sealed class BarEpisodeSpeedPanel : Component
    {
        static readonly Slider.SliderOptions Options = new()
        {
            Min = .5f, Max = 3f, Step = .05f, SmallChange = .05f, LargeChange = .25f,
            ThumbToolTipValueConverter = EpisodeRateText,
        };
        public override Element Render()
        {
            var value = UseFloatSignal(Playback.EpisodeSpeed.Peek());
            UseSignalEffect(() => value.SetIfChanged(Playback.EpisodeSpeed.Value));
            Element Preset(float rate) => FluentGpu.Controls.Button.Create(EpisodeRateText(rate),
                () => Playback.SetEpisodeSpeed(rate), ButtonAppearance.Subtle, ControlSize.Small);
            return new BoxEl
            {
                Width = 256f, Direction = 1, Gap = Spacing.S, Padding = Edges4.All(Spacing.M),
                Children =
                [
                    FluentGpu.Dsl.Ui.Text(Loc.Get("podcast.reader.speed")),
                    Slider.Create(value, static rate => Playback.SetEpisodeSpeed(rate), Options, length: 224f),
                    new BoxEl { Direction = 0, Gap = Spacing.XXS, Justify = FlexJustify.SpaceBetween,
                        Children = [Preset(1f), Preset(1.5f), Preset(2f), Preset(3f)] },
                ],
            };
        }
    }
}
