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
    public const float EpisodeSpeedButtonWidth = 48f;

    // The ±10 s step buttons call `IPlayerVerbs.SeekBy` (ONE seek straight from the reported position; no accumulator — the reducer
    // paints the drop point at the post, so a second press reads it), and the arrow keys are the scrub ladder
    // (`ScrubController.KeyStep`, the bar-wide `KeyboardScrub`; D4, V-PA22): the state, the grace and the commit all live in core.

    /// <summary>The ladder's commit clock: a zero-size component every seek rail mounts, so the interval runs where a rail does (bar, stage,
    /// on-media) and only while a ladder is live — the rail itself never re-renders for it. Several rails ticking is harmless: the first
    /// to see the quiet key commits, the rest find the ladder already committed.</summary>
    sealed class BarKeyLadderTick : Component
    {
        readonly Action _tick = KeyboardScrub.Shared.Tick;

        public override Element Render()
        {
            UseInterval(_tick, KeyboardScrub.TickMs, enabled: KeyboardScrub.Shared.Active.Value);
            // Nobody left to commit a live ladder (the only rail unmounted inside the grace): drop it, or the labels would hold its target forever.
            UseEffect(() => { KeyboardScrub.Shared.TickMounted(); return () => KeyboardScrub.Shared.TickUnmounted(); }, DepKey.Empty);
            return new BoxEl { Width = 0f, Height = 0f, HitTestVisible = false };
        }
    }

    static Element BarSeekStepButton(int deltaMs, float box)
    {
        bool enabled = PlaybackScrubSource.Instance.Gate(track: true).Enabled;
        return ToolTip.Wrap(new BoxEl
        {
            Width = box, Height = box, Shrink = 0f,
            Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Corners = Radii.ControlAll, HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary,
            Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false,
            IsEnabled = enabled, Cursor = enabled ? CursorId.Hand : null,
            OnClick = () => PlayerVerbs.Default.SeekBy(deltaMs),
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
                () => PlayerVerbs.Default.SetSpeed(rate), ButtonAppearance.Subtle, ControlSize.Small);
            return new BoxEl
            {
                Width = 256f, Direction = 1, Gap = Spacing.S, Padding = Edges4.All(Spacing.M),
                Children =
                [
                    FluentGpu.Dsl.Ui.Text(Loc.Get("podcast.reader.speed")),
                    Slider.Create(value, static rate => PlayerVerbs.Default.SetSpeed(rate), Options, length: 224f),
                    new BoxEl { Direction = 0, Gap = Spacing.XXS, Justify = FlexJustify.SpaceBetween,
                        Children = [Preset(1f), Preset(1.5f), Preset(2f), Preset(3f)] },
                ],
            };
        }
    }
}
