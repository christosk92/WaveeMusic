using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;

using Wavee.Theming.Contracts;

using ViewSlots = FluentGpu.Theming.Slots;

namespace Wavee;

public static partial class Shell
{
    /// <summary>The chapter decoration's CORE HOST over the existing scrub surface: the chapter lease, the markers' arithmetic, and the
    /// chapter tip's overlay (when it shows: hovering or scrubbing an episode that still plays, in the same scope). The ticks and the
    /// tip's anchor are <see cref="ViewKeys.SeekChapters"/>, the tip's bubble <see cref="ViewKeys.ChapterTip"/>. Neither the ticks nor
    /// the moving tip receive input. Clock ticks never render this component; only resource/duration/width changes rebuild its
    /// marker array.</summary>
    [CoreHost]
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
        Func<Element>? _tipContent;
        Action<NodeHandle>? _onAnchor;
        Func<float>? _fraction;
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
                bool ready = (LoadState)lease.Data.State.Value == LoadState.Ready;
                int duration = Playback.DurationMs.Value;
                // The items are read only once the request is ready (the subscription follows the data, not the pending seed).
                var chapters = PlayerBarFacts.Seek.Chapters(ready, ready ? lease.Data.Value.Value.Items : [], duration);
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
                _tip = _overlay.Open(() => _anchor, _tipContent ??= TipContent(), FlyoutPlacement.Top,
                    new PopupOptions(FocusTrap: false, DismissBehavior: DismissBehavior.None, Chrome: PopupChrome.Static));
            });
            float[] fractions = PodcastChapterTimeline.MarkerFractions(_chapters.Value, _duration.Value, width.Value);
            // A live node anchor lets the stock overlay positioner follow the composited pointer position and clamp the bubble to
            // the window: the view places it (invisible, it cannot steal the rail's capture/keyboard focus) and reports its node here.
            return ViewSlots.View(ViewKeys.SeekChapters, new SeekChaptersProps
            {
                Fractions = fractions,
                Width = width,
                TipFraction = _fraction ??= () => Fraction,
                TipAnchorRealized = _onAnchor ??= node => _anchor = node,
            });
        }

        void CloseTip() { _tip?.Close(); _tip = null; }

        /// <summary>The tip's content: the time under the pointer, the chapter's title and its range, as live text (built once).</summary>
        Func<Element> TipContent()
        {
            var props = new ChapterTipProps
            {
                Time = () => PodcastReaderRules.Timestamp(HoverTime),
                Title = () =>
                {
                    int index = HoverChapter;
                    if (index < 0) return "";
                    string title = _chapters.Value[index].Title;
                    return title.Length > 0 ? title : Loc.Get(Strings.Podcast.Reader.Chapters);
                },
                Range = () =>
                {
                    int index = HoverChapter;
                    if (index < 0) return "";
                    var chapter = _chapters.Value[index];
                    return PodcastReaderRules.Timestamp(chapter.StartMs) + " – " + PodcastReaderRules.Timestamp(chapter.EndMs);
                },
                HasChapter = () => HoverChapter >= 0,
            };
            return () => ViewSlots.View(ViewKeys.ChapterTip, props);
        }
    }
}
