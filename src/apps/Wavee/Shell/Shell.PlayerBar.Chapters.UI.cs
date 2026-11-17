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
    /// <summary>The rail's pointer TIP, as one core host over the existing scrub surface: what follows the pointer while it hovers or
    /// scrubs. Two things can ride in it, and a rail has either or both:
    /// <list type="bullet">
    /// <item>an episode's CHAPTERS (<paramref name="chapterRail"/>): the chapter lease, the markers' arithmetic and the chapter under the
    /// pointer. The ticks and the tip's anchor are <see cref="ViewKeys.SeekChapters"/>;</item>
    /// <item>a video's SEEK PREVIEW (<paramref name="sheets"/>): the picture under the pointer, cut out of the source's seek sheets
    /// (<see cref="SeekFrame"/>), with the time and, on an episode, the chapter's title;</item>
    /// <item>a song's LYRIC line (<paramref name="lyricTrack"/>, the playing song's lyrics-store id; empty on any other rail): the line
    /// sung at the time under the pointer (<see cref="PlayerBarFacts.Seek.LyricAt"/>). The tip's first opening asks the store for the
    /// document (<see cref="Lyrics.Store.Ensure(string)"/>, idempotent: a lyrics surface that already asked joined the same fetch), and
    /// until it lands the tip is the time alone.</item>
    /// </list>
    /// The bubble is <see cref="ViewKeys.SeekPreview"/> when there are sheets, <see cref="ViewKeys.SeekLyric"/> on a lyric rail and
    /// <see cref="ViewKeys.ChapterTip"/> otherwise: ONE tip per rail, never a picture beside a second bubble. Neither the ticks nor the
    /// moving tip receive input. Clock ticks never render this component; only resource / duration / width changes rebuild its marker
    /// array.</summary>
    [CoreHost]
    sealed class BarRailTip(ChapterResources.Identity identity, bool chapterRail, Playback.Video.SeekPanels? sheets, string lyricTrack,
        ScrubController scrub, IReadSignal<float> hoverFraction, IReadSignal<bool> hovering, SeekRailSurface surface, IReadSignal<int> precise)
        : Component
    {
        static readonly float[] s_noMarkers = [];

        ChapterResources.Lease? _lease;
        readonly Signal<PodcastChapterTimeline.Interval[]> _chapters = new([]);
        readonly Signal<int> _duration = new(0);
        NodeHandle _anchor;
        OverlayHandle? _tip;
        IOverlayService? _overlay;
        Func<Element>? _tipContent;
        Action<NodeHandle>? _onAnchor;
        Func<float>? _fraction;
        // The lyric rail's document (timed, or null until it lands) and the line under the pointer (-1: the time alone), so the bubble's
        // text and visibility rebind only when the LINE changes, not on every move.
        readonly Signal<Lyrics.Doc?> _lyrics = new(null);
        readonly Signal<int> _lyricLine = new(-1);
        Action? _followLyrics, _followLine;
        bool LyricRail => lyricTrack.Length > 0;
        float Fraction => scrub.Scrubbing.Value ? scrub.Fraction.Value : hoverFraction.Value;
        int HoverTime => PodcastChapterTimeline.HoverTime(Fraction, _duration.Value);
        int HoverChapter => PodcastChapterTimeline.At(_chapters.Value, HoverTime);

        /// <summary>The time under the pointer: an episode's chapter clock, else the rail's own commit target (what a release seeks
        /// to), so the picture, the label and the seek are one number.</summary>
        long TipMs => chapterRail ? HoverTime : scrub.TargetMsAt(Fraction);

        public override Element Render()
        {
            _overlay = UseContext(Overlay.Service);
            if (chapterRail)
            {
                _lease ??= ChapterResources.Shared.Acquire(identity);
                var lease = _lease;
                UseLayoutEffect(lease.Start, DepKey.Empty);
                UseSignalEffect(() =>
                {
                    bool ready = (LoadState)lease.Data.State.Value == LoadState.Ready;
                    int duration = Playback.DurationMs.Value;
                    // The items are read only once the request is ready (the subscription follows the data, not the pending seed).
                    var chapters = PlayerBarFacts.Seek.Chapters(ready, ready ? lease.Data.Value.Value.Items : [], duration);
                    _duration.SetIfChanged(duration);
                    _chapters.Value = chapters;
                });
            }
            if (LyricRail)
            {
                // The store's publication counter beside the document read (the store's UI contract): the tip follows the document's
                // arrival and its upgrades. Only a timed document is kept; anything else is the time alone.
                UseSignalEffect(_followLyrics ??= () =>
                {
                    _ = Lyrics.Store.Changed.Value;
                    var doc = Lyrics.Store.Doc(lyricTrack);
                    _lyrics.Value = PlayerBarFacts.Seek.LyricDoc(true, doc) == LyricTipDoc.Timed ? doc : null;
                });
                UseSignalEffect(_followLine ??= () => _lyricLine.SetIfChanged(PlayerBarFacts.Seek.LyricAt(_lyrics.Value, TipMs)));
            }
            UseEffect(() => () => { CloseTip(); _lease?.Dispose(); }, DepKey.Empty);
            UseSignalEffect(() =>
            {
                bool show = (hovering.Value || scrub.Scrubbing.Value) && (sheets is not null || _chapters.Value.Length > 0 || LyricRail)
                    && Playback.CurrentId.Value.Equals(identity.Episode)
                    && Entities.ScopeEpoch.Value == identity.Epoch;
                if (!show) { CloseTip(); return; }
                if (_tip is { IsOpen: true } || _overlay is null) return;
                // The playing song's lyrics, asked for on the tip's first opening (a no-op once asked: one fetch per song, never another's).
                if (LyricRail) Lyrics.Store.Ensure(lyricTrack);
                // A picture or a lyric fades in and out as the Slider's own tip does (Raw: the theme lifts it clear of the thumb); the
                // text-only chapter tip stays the instant popup it was.
                bool raw = sheets is not null || LyricRail;
                _tip = _overlay.Open(() => _anchor, _tipContent ??= sheets is not null ? PreviewContent(sheets) : LyricRail ? LyricContent() : ChapterContent(),
                    FlyoutPlacement.Top, new PopupOptions(FocusTrap: false, DismissBehavior: DismissBehavior.None, Chrome: raw ? PopupChrome.Raw : PopupChrome.Static));
            });
            var width = scrub.RailPx;
            float[] fractions = chapterRail ? PodcastChapterTimeline.MarkerFractions(_chapters.Value, _duration.Value, width.Value) : s_noMarkers;
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

        string ChapterTitle()
        {
            int index = HoverChapter;
            if (index < 0) return "";
            string title = _chapters.Value[index].Title;
            return title.Length > 0 ? title : Loc.Get(Strings.Podcast.Reader.Chapters);
        }

        /// <summary>The seek preview's second line. While a scrub is slowed (precise seeking) it names the speed; otherwise an episode shows
        /// the chapter under the pointer, and a track, while it is being scrubbed, says how to slow the scrub.</summary>
        string PreviewLabel()
        {
            int step = precise.Value;
            if (step > 0) return Loc.Get(step == 1 ? Strings.Player.SeekHalfSpeed : Strings.Player.SeekQuarterSpeed);
            if (chapterRail) return ChapterTitle();
            return scrub.Scrubbing.Value ? Loc.Get(Strings.Player.SeekPreciseHint) : "";
        }

        bool PreviewHasLabel() => precise.Value > 0 || (chapterRail ? HoverChapter >= 0 : scrub.Scrubbing.Value);

        /// <summary>The chapter tip's content: the time under the pointer, the chapter's title and its range, as live text (built once).</summary>
        Func<Element> ChapterContent()
        {
            var props = new ChapterTipProps
            {
                Time = () => PodcastReaderRules.Timestamp(HoverTime),
                Title = ChapterTitle,
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

        /// <summary>The lyric tip's content: the time under the pointer (the rail's own commit target) and the line sung then, as live text
        /// (built once).</summary>
        Func<Element> LyricContent()
        {
            var props = new SeekLyricProps
            {
                Time = () => s_clockText.Get(TipMs / 1000L, static sec => Playback.TimeFormat.Clock(sec * 1000L)),
                Line = () =>
                {
                    int index = _lyricLine.Value;
                    return index >= 0 && _lyrics.Value is { } doc && index < doc.Lines.Count ? doc.Lines[index].Text : "";
                },
                HasLine = () => _lyricLine.Value >= 0,
                Scrubbing = scrub.Scrubbing,
                Surface = surface,
            };
            return () => ViewSlots.View(ViewKeys.SeekLyric, props);
        }

        /// <summary>The seek preview's content: the picture's core host (the theme says how wide and which corners), the time, and on an
        /// episode the chapter's title. Built once: everything that moves is a bind.</summary>
        Func<Element> PreviewContent(Playback.Video.SeekPanels panels)
        {
            Func<long> time = () => TipMs;
            var props = new SeekPreviewProps
            {
                Frame = (width, corners) => Embed.Comp(() => new SeekFrame(panels, time, width, corners)),
                Aspect = panels.Aspect,
                Time = () => chapterRail
                    ? PodcastReaderRules.Timestamp(HoverTime)
                    : s_clockText.Get(TipMs / 1000L, static sec => Playback.TimeFormat.Clock(sec * 1000L)),
                Label = PreviewLabel,
                HasLabel = PreviewHasLabel,
                Scrubbing = scrub.Scrubbing,
                Surface = surface,
            };
            return () => ViewSlots.View(ViewKeys.SeekPreview, props);
        }
    }

    /// <summary>The PICTURE of a seek preview: the frame for a time, cut out of the source's seek sheets. Core on purpose: which sheet is
    /// mounted, when the fine one is fetched and which cell shows are decisions, and the boxes (<see cref="SeekFrameBoxes"/>) carry no
    /// look. A theme gives the width and the corners and draws everything around it.
    /// <para><b>Two layers.</b> The COARSE sheets (36 frames each: a music video is seven of them) follow the pointer at once: the cell
    /// is a compositor transform, so a move inside a sheet repaints and nothing else. The FINE sheets (9 larger frames each) follow only
    /// once the pointer has rested <see cref="SeekRail.PreviewSharpenMs"/>, so a sweep across the rail fetches none of them; until the
    /// fine frame is the one under the pointer its layer is hidden, so a stale picture is never drawn over a newer one.</para>
    /// <para><b>No wrong picture.</b> A sheet shows only while the frame under the pointer is one of ITS cells (transform and opacity
    /// read the same signal, so they change in the same bind flush); a sheet that has not decoded yet draws nothing and the theme's
    /// plate shows through.</para></summary>
    [CoreHost]
    sealed class SeekFrame(Playback.Video.SeekPanels sheets, Func<long> timeMs, float width, CornerRadius4 corners) : Component
    {
        readonly Signal<int> _coarse = new(0);       // the frame the coarse layer shows
        readonly Signal<int> _fineWanted = new(0);   // the frame under the pointer, in the fine variant
        readonly Signal<int> _fine = new(-1);        // the frame the fine layer settled on (−1: none yet)
        readonly Dictionary<int, (Prop<Affine2D> Transform, Prop<float> Opacity, Prop<string> Source)> _coarseBinds = [];
        readonly Dictionary<int, (Prop<Affine2D> Transform, Prop<float> Opacity, Prop<string> Source)> _fineBinds = [];
        int _fineSeen = -1;
        Action? _follow, _sharpen;
        Func<int>? _coarsePanel, _finePanel;
        Func<bool>? _sharpening;

        public override Element Render()
        {
            var coarse = sheets.Coarse;
            var fine = sheets.Fine;
            bool twoLayers = !ReferenceEquals(coarse, fine);
            float scale = UseContext(Viewport.Scale);
            float height = width / sheets.Aspect;

            UseSignalEffect(_follow ??= () =>
            {
                long ms = timeMs();
                _coarse.SetIfChanged(coarse.FrameAt(ms));
                _fineWanted.SetIfChanged(fine.FrameAt(ms));
            });
            // A remote set: ask for every coarse sheet as the tip opens (the pointer's first, then outwards), so by the time the pointer
            // travels the sheets are files. They are 50-100 KB each; a three-hour podcast is about 40.
            UseEffect(() =>
            {
                Playback.Video.SeekSheets.Prefetch(sheets, coarse, coarse.FrameAt(timeMs()) / coarse.PerPanel);
                return static () => { };
            }, DepKey.Empty);
            // The sharpen dwell: armed only while the fine layer is behind the pointer, so a resting tip runs no timer.
            bool sharpening = twoLayers && UseComputed(_sharpening ??= () => _fine.Value != _fineWanted.Value).Value;
            UseInterval(_sharpen ??= () =>
            {
                int wanted = _fineWanted.Peek();
                if (wanted == _fineSeen) _fine.SetIfChanged(wanted);   // unmoved for a whole tick: it rested
                _fineSeen = wanted;
            }, SeekRail.PreviewSharpenMs, enabled: sharpening);

            bool all = coarse.Panels <= SeekRail.PreviewMountAll;      // a short video keeps every coarse sheet: a sweep never waits
            int coarseAt = all ? 0 : UseComputed(_coarsePanel ??= () => _coarse.Value / coarse.PerPanel).Value;
            int fineAt = twoLayers ? UseComputed(_finePanel ??= () => _fine.Value < 0 ? -1 : _fine.Value / fine.PerPanel).Value : -1;

            var layers = new List<Element>(coarse.Panels + 3);
            int from = all ? 0 : Math.Max(0, coarseAt - SeekRail.PreviewWindow);
            int to = all ? coarse.Panels - 1 : Math.Min(coarse.Panels - 1, coarseAt + SeekRail.PreviewWindow);
            for (int panel = from; panel <= to; panel++)
                layers.Add(Sheet(coarse, panel, _coarse, null, _coarseBinds, scale, height, fine: false));
            if (fineAt >= 0)
                for (int panel = Math.Max(0, fineAt - SeekRail.PreviewWindow); panel <= Math.Min(fine.Panels - 1, fineAt + SeekRail.PreviewWindow); panel++)
                    layers.Add(Sheet(fine, panel, _fine, _fineWanted, _fineBinds, scale, height, fine: true));
            return SeekFrameBoxes.Window(width, height, corners, [.. layers]);
        }

        /// <summary>One sheet: its image at the sheet's own pixel size (so the decode is the file's, whatever the tip's width) under a
        /// transform that scales one cell to the window and moves the cell under the pointer into it. The binds are kept per panel, so a
        /// re-render (the window of sheets moved) hands a sheet that stays the SAME thunks and rebinds nothing.</summary>
        Element Sheet(Playback.Video.SeekPanels.Variant v, int panel, Signal<int> frame, Signal<int>? gate,
            Dictionary<int, (Prop<Affine2D> Transform, Prop<float> Opacity, Prop<string> Source)> binds, float scale, float height, bool fine)
        {
            float unit = scale > 0f ? 1f / scale : 1f;                       // DIP per sheet pixel: the image box is the file's size
            float cellW = v.FrameW * unit, cellH = v.FrameH * unit;
            if (!binds.TryGetValue(panel, out var bind))
            {
                float sx = width / cellW, sy = height / cellH;
                float squash = v.RowsIn(panel) / (float)v.Rows;              // the last sheet is cropped to its rows; the box is not
                float tipW = width, tipH = height;
                int per = v.PerPanel, columns = v.Columns;
                bind = (
                    Prop.Of(() =>
                    {
                        int f = frame.Value;
                        if (f < 0 || f / per != panel) return Affine2D.Identity;
                        int cell = f % per;
                        return new Affine2D(sx, 0f, 0f, sy * squash, -(cell % columns) * tipW, -(cell / columns) * tipH);
                    }),
                    Prop.Of(() =>
                    {
                        int f = frame.Value;
                        return f >= 0 && f / per == panel && (gate is null || gate.Value == f) ? 1f : 0f;
                    }),
                    // A remote sheet's source is its FILE once it is on disk (Playback.Video.SeekSheets); a local template is itself.
                    Playback.Video.SeekSheets.Source(sheets.Url(v, panel)));
                binds[panel] = bind;
            }
            return SeekFrameBoxes.Sheet((fine ? "seek-fine:" : "seek-coarse:") + panel, bind.Source,
                v.Columns * cellW, v.Rows * cellH, bind.Transform, bind.Opacity, fine);
        }
    }

    /// <summary>The seek picture's boxes, built in one place and carrying no look (as <c>HoleBoxes</c> does for the video hole): a clip
    /// window of the size and corners the theme asked for, and the sheets stacked in it. Nothing here has a colour, a border or motion
    /// of its own; a sheet that has not decoded is transparent, so the theme's plate under the window is what shows.</summary>
    static class SeekFrameBoxes
    {
        /// <summary>How a decoded sheet arrives: the coarse one at once (it is what a sweep reads), the fine one as a short sharpen.</summary>
        static readonly ImageTransition s_coarseReveal = ImageTransition.Fade(90f), s_fineReveal = ImageTransition.Fade(160f);

        public static Element Window(float width, float height, CornerRadius4 corners, Element[] sheets) => new BoxEl
        {
            Key = "seek-frame", Width = width, Height = height, Shrink = 0f, ZStack = true, ClipToBounds = true, Corners = corners,
            HitTestVisible = false, Children = sheets,
        };

        public static Element Sheet(string key, Prop<string> source, float width, float height, Prop<Affine2D> transform, Prop<float> opacity, bool fine) => new BoxEl
        {
            Key = key, Width = width, Height = height, Shrink = 0f, AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start,
            TransformOriginX = 0f, TransformOriginY = 0f, Transform = transform, Opacity = opacity, HitTestVisible = false,
            Children =
            [
                new ImageEl
                {
                    Source = source, Width = width, Height = height, Fit = ImageFit.Fill, Placeholder = ColorF.Transparent,
                    RevealTransition = fine ? s_fineReveal : s_coarseReveal,
                },
            ],
        };
    }
}
