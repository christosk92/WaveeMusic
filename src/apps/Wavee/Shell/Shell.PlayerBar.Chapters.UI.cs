using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;

namespace Wavee;

public static partial class Shell
{
    /// <summary>Pure decoration over the existing scrub surface. Neither ticks nor the moving tip receive input.
    /// Clock ticks never render this component; only resource/duration/width changes rebuild its marker array.</summary>
    sealed class BarChapterTimeline(ChapterResources.Identity identity, IReadSignal<float> width,
        IReadSignal<float> hoverFraction, IReadSignal<float> scrubFraction,
        IReadSignal<bool> hovering, IReadSignal<bool> scrubbing) : Component
    {
        ChapterResources.Lease? _lease;
        readonly Signal<PodcastChapterTimeline.Interval[]> _chapters = new([]);
        readonly Signal<int> _duration = new(0);
        NodeHandle _anchor;
        OverlayHandle? _tip;
        IOverlayService? _overlay;
        float Fraction => scrubbing.Value ? scrubFraction.Value : hoverFraction.Value;
        int HoverTime => PodcastChapterTimeline.HoverTime(Fraction, _duration.Value);
        int HoverChapter => PodcastChapterTimeline.At(_chapters.Value, HoverTime);

        public override Element Render()
        {
            _overlay = UseContext(Overlay.Service);
            _lease ??= ChapterResources.Shared.Acquire(identity);
            var lease = _lease;
            UseLayoutEffect(lease.Start, DepKey.Empty);
            UseEffect(() => () => { CloseTip(); lease.Dispose(); }, DepKey.Empty);
            UseSignalEffect(() =>
            {
                var state = (LoadState)lease.Data.State.Value;
                int duration = Playback.DurationMs.Value;
                var chapters = state == LoadState.Ready
                    ? PodcastChapterTimeline.Normalize(lease.Data.Value.Value.Items, duration) : [];
                _duration.SetIfChanged(duration);
                _chapters.Value = chapters;
            });
            UseSignalEffect(() =>
            {
                bool show = (hovering.Value || scrubbing.Value) && _chapters.Value.Length > 0
                    && Playback.CurrentId.Value.Equals(identity.Episode)
                    && Entities.ScopeEpoch.Value == identity.Epoch;
                if (!show) { CloseTip(); return; }
                if (_tip is { IsOpen: true } || _overlay is null) return;
                _tip = _overlay.Open(() => _anchor, TipContent, FlyoutPlacement.Top,
                    new PopupOptions(FocusTrap: false, DismissBehavior: DismissBehavior.None, Chrome: PopupChrome.Static));
            });
            float[] fractions = PodcastChapterTimeline.MarkerFractions(_chapters.Value, _duration.Value, width.Value);
            var marks = new Element[fractions.Length + 1];
            for (int i = 0; i < fractions.Length; i++)
            {
                float fraction = fractions[i];
                marks[i] = new BoxEl
                {
                    Key = "chapter-tick:" + i, Width = 1f, Height = 8f, Shrink = 0, AlignSelf = FlexAlign.Center,
                    Fill = Tok.TextSecondary, HitTestVisible = false,
                    Transform = Prop.Of(() => Affine2D.Translation(fraction * width.Value, 0)),
                };
            }
            // A live node anchor lets the stock overlay positioner follow the composited pointer position and
            // clamp the bubble to the window. It is invisible and cannot steal the rail's capture/keyboard focus.
            marks[^1] = new BoxEl
            {
                Key = "chapter-tip-anchor", Width = 1, Height = 8, Shrink = 0, AlignSelf = FlexAlign.Center,
                HitTestVisible = false, OnRealized = node => _anchor = node,
                Transform = Prop.Of(() => Affine2D.Translation(Math.Clamp(Fraction, 0, 1) * width.Value, 0)),
            };
            return new BoxEl
            {
                ZStack = true, Grow = 1, Shrink = 1, MinWidth = 0, Height = 32,
                AlignItems = FlexAlign.Center, HitTestVisible = false, Children = marks,
            };
        }

        void CloseTip() { _tip?.Close(); _tip = null; }
        Element TipContent() => new BoxEl
        {
            Direction = 1, Gap = 3, Width = 260, MaxWidth = 320,
            Acrylic = Tok.AcrylicFlyout, BorderColor = Tok.StrokeFlyoutDefault, BorderWidth = 1,
            Corners = Radii.ControlAll, Shadow = Elevation.Tooltip, Padding = new Edges4(9, 6, 9, 8),
            HitTestVisible = false,
            Children =
            [
                new TextEl(Prop.Of(() => PodcastReaderRules.Timestamp(HoverTime)))
                { Size = 12, Weight = 600, Color = Tok.TextPrimary },
                new TextEl(Prop.Of(() =>
                {
                    int index = HoverChapter;
                    if (index < 0) return "";
                    string title = _chapters.Value[index].Title;
                    return title.Length > 0 ? title : Loc.Get(Strings.Podcast.Reader.Chapters);
                })) { Size = 13, Weight = 600, MaxLines = 2, Wrap = TextWrap.Wrap, Trim = TextTrim.CharacterEllipsis,
                    Color = Tok.TextPrimary, MinWidth = 0,
                    Visible = Prop.Of(() => HoverChapter >= 0) },
                new TextEl(Prop.Of(() =>
                {
                    int index = HoverChapter;
                    if (index < 0) return "";
                    var chapter = _chapters.Value[index];
                    return PodcastReaderRules.Timestamp(chapter.StartMs) + " \u2013 " + PodcastReaderRules.Timestamp(chapter.EndMs);
                })) { Size = 12, Color = Tok.TextSecondary,
                    Visible = Prop.Of(() => HoverChapter >= 0) },
            ],
        };
    }
}
