// ── Themes/Classic/Structure/Views/Podcast/ClassicPodcastEpisodeParts.cs — the episode page's own parts in Classic (podcast pages v2) ──────────────
//
//   · THE CHAPTER MAP: a card whose head names where the listener is ("Chapter 7 of 12 · name" and the clock, "Played · finished 9 Nov", "12 chapters ·
//     select one to start there") over a band in which every chapter is ONE node drawn to its length (a crowded map's gap and floor shrink), coloured
//     played or not by binds, and one overlay that fills the current segment to the position, plus the thumb: a playback event re-renders nothing here.
//   · THE CHAPTERS LIST: a BOUND virtual list (its own scroller, rows of one height): each visible slot is built once and recycled by its index signal,
//     so a scroll rebinds and builds nothing, whatever the count (a long episode has a hundred). Number, name, "start · length · time left", the current
//     row's bar (its room kept on every row), a check once played.
//   · THE TRANSCRIPT CARD: Transcript, "Auto-generated · English", Find in transcript, the follow button (its width held); the chapter heading over the
//     lines on BOUND virtual slots over a measured layout (a 3-hour episode has ~2,500 lines); the current line highlights and, while following, stays
//     centred (a far target lands at once); a user scroll stops following, "Back to current line" brings it back; a click seeks; Find marks the matches.
// Numbers: `ClassicPodcastRules`. No data access (WVTH002): everything comes on the props.

using System.Collections.Immutable;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Reconciler;
using FluentGpu.Scene;
using FluentGpu.Scroll.Runtime;
using FluentGpu.Signals;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;
using P = Wavee.Themes.Classic.ClassicPodcastRules;

namespace Wavee.Themes.Classic;

/// <summary>The episode page's own parts (Classic).</summary>
internal static class ClassicPodcastEpisodeParts
{
    /// <summary>A Classic card: the card fill, its 1-DIP stroke, r8.</summary>
    public static BoxEl Card(string key, Element[] kids, float gap = 0f, Edges4 padding = default) => new()
    {
        Key = key, Direction = 1, Gap = gap, Padding = padding, MinWidth = 0f, Corners = CornerRadius4.All(P.CardRadius), Fill = Tok.FillCardDefault,
        BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault, ClipToBounds = true, Children = kids,
    };

    // ══ 1. THE CHAPTER MAP ══════════════════════════════════════════════════════════════════════════════════════════════════

    public sealed record MapArgs(PodcastEpisodePageProps P, float Target, ColorF Accent);

    public static Element Map(PodcastEpisodePageProps p, float target, ColorF accent)
        => Embed.Comp(new MapArgs(p, target, accent), static () => new MapHost()) with { Key = "cpe-map" };

    /// <summary>The map: its head (its own small component) over the band, which renders only when the chapters change. Everything that moves with
    /// playback is a bind: each segment's colour, one overlay filling the current segment to the position, the thumb.</summary>
    sealed class MapHost : Component
    {
        readonly Signal<int> _hover = new(-1);
        readonly Signal<int> _laid = new(0);
        Func<ImmutableArray<PodcastChapterVm>>? _chaptersOf;
        PodcastEpisodePageProps? _p;
        float[] _segW = [];

        public override Element Render()
        {
            var a = UseProps<MapArgs>();
            var p = a.P;
            _p = p;
            var chapters = UseComputed(_chaptersOf ??= () => _p!.View.Value.Chapters).Value;
            int n = chapters.IsDefault ? 0 : chapters.Length;
            if (_segW.Length != n) _segW = new float[n];
            var segW = _segW;
            var starts = new int[n];
            for (int i = 0; i < n; i++) starts[i] = chapters[i].StartMs;
            var cur = p.CurrentChapter;
            var state = p.State;
            var pos = p.PositionMs;
            var hover = _hover;
            var laid = _laid;
            ColorF accent = a.Accent;
            float gap = P.SegGapFor(n), segMin = P.SegMinFor(n);
            bool Played(int i) => state?.Value == PodcastRowState.Played || (cur.Value >= 0 && i < cur.Value);
            float FillOf(int c) => c < 0 || c >= n ? 0f
                : Math.Clamp((pos.Value - chapters[c].StartMs) / (float)Math.Max(1, chapters[c].EndMs - chapters[c].StartMs), 0f, 1f);
            float StartOf(int c) { _ = laid.Value; float x = 0f; for (int k = 0; k < c && k < segW.Length; k++) x += segW[k] + gap; return x; }
            float WidthOf(int c) { _ = laid.Value; return c >= 0 && c < segW.Length ? segW[c] : 0f; }

            var segs = new Element[n];
            for (int i = 0; i < n; i++)
            {
                int index = i;
                segs[i] = new BoxEl
                {
                    Key = "cpe-seg:" + i, Grow = MathF.Max(1f, chapters[i].EndMs - chapters[i].StartMs), Basis = 0f, MinWidth = segMin, Height = P.SegRest,
                    AlignSelf = FlexAlign.Center, Corners = CornerRadius4.All(P.SegRadius), HitTestVisible = false, TransformOriginY = 0.5f,
                    OnBoundsChanged = r => { if (index < segW.Length && segW[index] != r.W) { segW[index] = r.W; laid.Value++; } },
                    Fill = Prop.Of(() => Played(index)
                        ? (state?.Value == PodcastRowState.Played ? Tok.FillControlStrong : accent)
                        : hover.Value == index ? Tok.FillControlTertiary : Tok.FillControlSecondary),
                    Transform = Prop.Of(() => Affine2D.Scale(1f, hover.Value == index ? P.SegHover / P.SegRest : 1f)),
                };
            }
            Element fill = new BoxEl
            {
                Height = P.SegRest, Width = Prop.Of(() => WidthOf(cur.Value)), AlignSelf = FlexAlign.Center, JustifySelf = FlexAlign.Start, HitTestVisible = false,
                Corners = CornerRadius4.All(P.SegRadius), Fill = accent, TransformOriginX = 0f, Visible = Prop.Of(() => cur.Value >= 0),
                Transform = Prop.Of(() => { int c = cur.Value; return new Affine2D(MathF.Max(0.001f, FillOf(c)), 0f, 0f, 1f, StartOf(c), 0f); }),
            };
            Element thumb = new BoxEl
            {
                Width = P.Thumb, Height = P.Thumb, Corners = CornerRadius4.All(P.ThumbHalf), Fill = Tok.FillControlSolid, BorderWidth = P.ThumbStroke,
                BorderColor = Tok.StrokeControlDefault, AlignSelf = FlexAlign.Center, JustifySelf = FlexAlign.Start, AlignItems = FlexAlign.Center,
                Justify = FlexJustify.Center, HitTestVisible = false, Visible = Prop.Of(() => cur.Value >= 0),
                Transform = Prop.Of(() => { int c = cur.Value; return Affine2D.Translation(StartOf(c) + FillOf(c) * WidthOf(c) - P.ThumbHalf, 0f); }),
                Children = [new BoxEl { Width = P.ThumbDot, Height = P.ThumbDot, Corners = CornerRadius4.All(P.ThumbDotHalf), Fill = accent }],
            };
            var band = new BoxEl
            {
                Key = "cpe-map-band", Direction = 0, Gap = gap, Height = a.Target, AlignItems = FlexAlign.Center, MinWidth = 0f,
                Cursor = CursorId.Hand, Role = AutomationRole.Button, Focusable = n > 0,
                OnHoverMove = pt => hover.SetIfChanged(IndexAt(segW, pt.X)),
                OnPointerExit = () => hover.SetIfChanged(-1),
                OnClick = () =>
                {
                    int at = hover.Peek();
                    if (at >= 0 && at < starts.Length) p.SeekTo(starts[at]);
                },
                Children = segs,
            };
            return Card("cpe-map-card",
            [
                Embed.Comp(p, static () => new MapHeadHost()) with { Key = "cpe-map-head" },
                new BoxEl
                {
                    ZStack = true, MinWidth = 0f, Margin = new Edges4(0f, (P.SegBand - a.Target) / 2f, 0f, (P.SegBand - a.Target) / 2f),
                    Children = [band, fill, thumb],
                },
            ], P.MapGap, new Edges4(P.MapPadX, P.MapPadTop, P.MapPadX, P.MapPadBottom));
        }

        static int IndexAt(float[] widths, float x)
        {
            float at = 0f;
            for (int i = 0; i < widths.Length; i++)
            {
                at += widths[i] + P.SegGapFor(widths.Length);
                if (x < at) return i;
            }
            return widths.Length - 1;
        }
    }

    /// <summary>The map's head: "Chapter 7 of 12 · name" / "Played · finished 9 Nov" / "12 chapters · select one to start there", and the clock.</summary>
    sealed class MapHeadHost : Component
    {
        Func<(string, string, bool)>? _headOf;
        PodcastEpisodePageProps? _p;

        public override Element Render()
        {
            var p = UseProps<PodcastEpisodePageProps>();
            _p = p;
            var (headText, subText, quietFirst) = UseComputed(_headOf ??= () => { var w = _p!.View.Value; return (w.MapHead, w.MapSub, w.MapQuietFirst); }).Value;
            var strong = BodyStrong(headText) with { Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, Shrink = 1f, MinWidth = 0f };
            var quiet = Body(subText) with { Color = Tok.TextSecondary, Wrap = TextWrap.NoWrap, MaxLines = 1, Shrink = quietFirst ? 0f : 1f, MinWidth = 0f,
                Trim = TextTrim.CharacterEllipsis };
            return new BoxEl
            {
                Direction = 0, Gap = P.MapGap, AlignItems = FlexAlign.Center, MinWidth = 0f,
                Children =
                [
                    new BoxEl { Direction = 0, Grow = 1f, Shrink = 1f, MinWidth = 0f, AlignItems = FlexAlign.Center, Children = quietFirst ? [quiet, strong] : [strong, quiet] },
                    new TextEl("") { Text = Prop.Bind(p.MapClock), Size = Caption("").Size, LineHeight = Caption("").LineHeight, Color = Tok.TextSecondary, Shrink = 0f },
                ],
            };
        }
    }

    // ══ 2. THE CHAPTERS LIST ════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The chapters as a BOUND virtual list (its own scroller, rows of one height). <paramref name="stamp"/> rebuilds the slots when the
    /// snapshot's chapters change.</summary>
    public static Element Chapters(PodcastEpisodePageProps p, ImmutableArray<PodcastChapterVm> chapters, ScrollHandle scroll, int stamp, ColorF accent)
        => Virtual.ListBound(chapters.Length, P.ChRowH, idx => ChapterRow(p, chapters, idx, accent)) with
        {
            Key = "cpe-sections:chapters", Grow = 1f, MinHeight = 0f, Handle = scroll, TemplateStamp = stamp,
            Margin = new Edges4(P.BodyPad, P.BodyPad, P.BodyPad, 0f),
        };

    /// <summary>"This episode has no chapters."</summary>
    public static Element NoChapters(string text) => Body(text) with { Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, MinWidth = 0f };

    /// <summary>One bound chapter slot: everything that differs by chapter binds on <paramref name="idx"/>; the click reads it at the click.</summary>
    static Element ChapterRow(PodcastEpisodePageProps p, ImmutableArray<PodcastChapterVm> chapters, IReadSignal<int> idx, ColorF accent)
    {
        var cur = p.CurrentChapter;
        var pos = p.PositionMs;
        var state = p.State;
        PodcastChapterVm At() => chapters[Math.Clamp(idx.Value, 0, chapters.Length - 1)];
        bool Current() => cur.Value == idx.Value;
        bool Played() => state?.Value == PodcastRowState.Played || (cur.Value >= 0 && idx.Value < cur.Value);
        ColorF ink = Design.Palette.ReadableInk(accent);
        var meta = new BoxEl
        {
            Direction = 0, MinWidth = 0f,
            Children =
            [
                new TextEl("") { Text = Prop.Of(() => At().Span), Size = Caption("").Size, LineHeight = Caption("").LineHeight, Color = Tok.TextSecondary, Shrink = 0f },
                new TextEl("") { Text = Prop.Of(() => cur.Value == idx.Value ? p.ChapterLeft.Value : ""), Size = Caption("").Size, LineHeight = Caption("").LineHeight,
                    Color = Tok.TextSecondary, Shrink = 1f, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
            ],
        };
        var bar = new BoxEl
        {
            Height = P.ChBarH, Margin = new Edges4(0f, P.ChBarTop, 0f, 0f), Corners = CornerRadius4.All(P.MiniBarRadius), ClipToBounds = true,
            Fill = Tok.FillControlStrong, Opacity = Prop.Of(() => Current() ? 1f : 0f), Direction = 0,
            Children =
            [
                new BoxEl
                {
                    Grow = 1f, Fill = accent, TransformOriginX = 0f, Corners = CornerRadius4.All(P.MiniBarRadius),
                    Transform = Prop.Of(() =>
                    {
                        var c = At();
                        float f = cur.Value == idx.Value ? Math.Clamp((pos.Value - c.StartMs) / (float)Math.Max(1, c.EndMs - c.StartMs), 0f, 1f) : 0f;
                        return Affine2D.Scale(MathF.Max(0.001f, f), 1f);
                    }),
                },
            ],
        };
        // The name: regular, or semibold on the current chapter (two runs laid over each other: a weight is no bind).
        TextEl Name(bool strong) => new("")
        {
            Text = Prop.Of(() => At().Title), Size = Body("").Size, LineHeight = Body("").LineHeight, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
            Weight = strong ? P.Strong : Body("").Weight,
            Color = Prop.Of<ColorF>(() => Current() != strong ? ColorF.Transparent : Played() ? Tok.TextSecondary : Tok.TextPrimary),
        };
        return new BoxEl
        {
            Direction = 0, Gap = P.ChGap, AlignItems = FlexAlign.Center, Height = P.ChRowH, MinWidth = 0f,
            Padding = new Edges4(P.ChPadX, P.ChPadY, P.ChPadX, P.ChPadY), Corners = Radii.ControlAll,
            Fill = Prop.Of(() => Current() ? Tok.FillSubtleSecondary : Tok.FillSubtleTransparent), HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary,
            Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand, BrushTransitionMs = Design.Motion.Faster,
            OnClick = () => p.SeekTo(chapters[Math.Clamp(idx.Peek(), 0, chapters.Length - 1)].StartMs),
            Children =
            [
                new BoxEl
                {
                    Width = P.ChNum, Shrink = 0f, ZStack = true, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                    Children =
                    [
                        new BoxEl
                        {
                            Width = P.ChTickW, Height = P.ChTickH, Corners = CornerRadius4.All(P.ChTickW), Fill = accent, JustifySelf = FlexAlign.Start,
                            AlignSelf = FlexAlign.Center, Margin = new Edges4(-P.ChPadX, 0f, 0f, 0f), Opacity = Prop.Of(() => Current() ? 1f : 0f), HitTestVisible = false,
                        }.Skeletonized(false),
                        new TextEl("")
                        {
                            Text = Prop.Of(() => At().Number), Size = Body("").Size, LineHeight = Body("").LineHeight, AlignSelf = FlexAlign.Center, JustifySelf = FlexAlign.Center,
                            Color = Prop.Of(() => Current() ? ink : Tok.TextTertiary),
                        },
                    ],
                },
                new BoxEl
                {
                    Direction = 1, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f,
                    Children = [new BoxEl { ZStack = true, MinWidth = 0f, Children = [Name(false), Name(true)] }, meta, bar],
                },
                new BoxEl
                {
                    Width = P.ChCheck, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                    Opacity = Prop.Of(() => Played() ? 1f : 0f), Children = [Icon(Icons.Check, P.ChCheckGlyph, Tok.TextTertiary)],
                },
            ],
        };
    }

    // ══ 3. THE TRANSCRIPT CARD ══════════════════════════════════════════════════════════════════════════════════════════════

    public sealed record TranscriptArgs(PodcastEpisodePageProps P, bool Chrome, float Height, ColorF Accent, bool Bare);

    /// <summary>The transcript card. <paramref name="bare"/>: the body of the sections card's Transcript tab (no card of its own, no second "Transcript":
    /// the tab names it).</summary>
    public static Element Transcript(PodcastEpisodePageProps p, bool chrome, float height, ColorF accent, bool bare = false)
        => Embed.Comp(new TranscriptArgs(p, chrome, height, accent, bare), static () => new TranscriptHost()) with { Key = "cpe-transcript" };

    sealed class TranscriptHost : Component
    {
        readonly ScrollHandle _scroll = new();
        MeasuredStackVirtualLayout? _layout;
        ImmutableArray<PodcastLineVm> _lines;
        string[] _headingAt = [];
        TranscriptArgs _a = null!;
        Func<PodcastEpisodeView>? _sliceOf;
        ImmutableArray<PodcastLineVm> _stampLines;
        string _stampFind = "";
        int _stamp;

        public override Element Render()
        {
            _a = UseProps<TranscriptArgs>();
            var p = _a.P;
            // Its slice of the snapshot: a change elsewhere (the primary's word, the state) does not re-render the transcript.
            var v = UseComputed(_sliceOf ??= () =>
            {
                var w = _a.P.View.Value;
                return PodcastEpisodeView.Empty with
                {
                    Load = w.Load, Transcript = w.Transcript, TranscriptLoad = w.TranscriptLoad, TranscriptSub = w.TranscriptSub, NoTranscript = w.NoTranscript,
                };
            }).Value;
            string find = p.Find().Value.Trim();
            if (_layout is null || !_lines.Equals(v.Transcript))
            {
                _lines = v.Transcript;
                _layout = new MeasuredStackVirtualLayout(P.TrLineEstimate, extentOf: i => i < _lines.Length && _lines[i].Heading is not null ? P.TrHeadingEstimate : P.TrLineEstimate);
                _headingAt = new string[_lines.Length];
                string last = "";
                for (int i = 0; i < _lines.Length; i++) { if (_lines[i].Heading is { } h) last = h; _headingAt[i] = last; }
            }
            var layout = _layout;
            var lines = _lines;
            UseEffect(() =>
            {
                int line = p.CurrentLine.Value;
                bool follow = p.Follow.Value;
                double vh = _scroll.ViewportSignal.Value;
                _ = _scroll.ExtentSignal.Value;
                if (!follow || line < 0 || vh <= 0 || lines.Length == 0) return;
                double target = Math.Max(0, layout.OffsetOf(line, 0f) - vh / 2 + P.TrLineEstimate / 2);
                // A far target (a seek deep in a long episode) lands at once: a glide over thousands of lines would realise every row on its way.
                bool far = Math.Abs(target - _scroll.Offset.Peek()) > vh * P.FollowJumpViewports;
                _scroll.ScrollTo(target, far ? ScrollMove.Immediate : ScrollMove.Follow);
            });
            UseEffect(() =>
            {
                var m = _scroll.Motion.Value;
                if (m.UserDriven && p.Follow.Peek()) p.SetFollow(false);
            });

            var head = new List<Element>(4);
            if (!_a.Bare) head.Add(BodyStrong(Loc.Get(Strings.Podcast.Pages.Transcript)) with { Color = Tok.TextPrimary, Shrink = 0f, Grow = _a.Chrome ? 0f : 1f });
            else if (!_a.Chrome) head.Add(new BoxEl { Grow = 1f });
            if (_a.Chrome)
            {
                head.Add(Caption(v.TranscriptSub) with { Color = Tok.TextSecondary, Grow = 1f, Shrink = 1f, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis });
                // The find box keeps its width (its slot shrank while the box inside did not, so the box ran under the follow button): where the
                // header is too narrow for both, the header wraps and they take its second line.
                head.Add(new BoxEl
                {
                    Width = P.FindW, Shrink = 0f,
                    Children = [AutoSuggestBox.Create(s_noSuggest, Loc.Get(Strings.Podcast.Pages.FindInTranscript), width: P.FindW, text: p.Find(),
                        queryIcon: Icons.Search, cornerRadius: Radii.Control)],
                });
            }
            head.Add(Embed.Comp(p, static () => new FollowHost()) with { Key = "cpe-follow" });

            Element body;
            if (v.TranscriptLoad == PodcastLoad.Ready && lines.Length == 0)
                body = new BoxEl { Padding = Edges4.All(P.TrPadX), Children = [Body(v.NoTranscript) with { Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, MinWidth = 0f }] };
            else
            {
                var chrome = _a.Chrome;
                var cur = p.CurrentLine;
                ColorF accent = _a.Accent;
                // BOUND slots over the measured layout: each visible slot is built once and recycled by its index signal (a scroll rebinds, builds
                // nothing). A new document or a new find text rebuilds the slots (the stamp).
                if (!_stampLines.Equals(lines) || _stampFind != find)
                {
                    _stampLines = lines;
                    _stampFind = find;
                    _stamp++;
                }
                var list = new VirtualListEl
                {
                    ItemCount = lines.Length, ItemLayout = layout, RowBind = idx => Line(p, lines, idx, cur, chrome, find, accent),
                    Handle = _scroll, Grow = 1f, TemplateStamp = _stamp,
                };
                var heading = new TextEl("")
                {
                    Text = Prop.Of(() =>
                    {
                        if (_headingAt.Length == 0) return "";
                        int at = layout.IndexAt((float)_scroll.Offset.Value, 0f);
                        return _headingAt[Math.Clamp(at, 0, _headingAt.Length - 1)];
                    }),
                    Size = BodyStrong("").Size, LineHeight = BodyStrong("").LineHeight, Weight = BodyStrong("").Weight, Color = Tok.TextPrimary,
                    MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                };
                body = new BoxEl
                {
                    Direction = 1, Grow = 1f, MinHeight = 0f, MinWidth = 0f,
                    Children =
                    [
                        new BoxEl { Direction = 0, Shrink = 0f, MinWidth = 0f, Padding = new Edges4(P.TrPadX, P.TrHeadingTop, P.TrPadX, P.TrHeadingBottom), Children = [heading] },
                        new BoxEl { Direction = 1, Grow = 1f, MinHeight = 0f, Padding = new Edges4(P.TrPadX, 0f, P.TrPadX, P.BodyPad), Children = [list] },
                    ],
                };
            }
            var card = Card("cpe-transcript-card",
            [
                new BoxEl
                {
                    Direction = 0, Wrap = true, Gap = P.MapGap, AlignItems = FlexAlign.Center, MinHeight = P.ListBarH, Shrink = 0f, MinWidth = 0f,
                    Padding = new Edges4(P.CardHeadPadX, P.BodyPad, P.BodyPad, P.BodyPad), Children = head.ToArray(),
                },
                new BoxEl { Height = 1f, Shrink = 0f, Fill = Tok.StrokeDividerDefault },
                body,
            ]) with { Height = _a.Height };
            return _a.Bare ? card with { Fill = ColorF.Transparent, BorderWidth = 0f, Corners = default } : card;
        }

        static readonly string[] s_noSuggest = [];

        /// <summary>The follow button: subtle while "Following playback", standard as "Back to current line"; both words share one slot, so it keeps the
        /// longer word's width and the toggle never moves the header.</summary>
        sealed class FollowHost : Component
        {
            public override Element Render()
            {
                var p = UseProps<PodcastEpisodePageProps>();
                bool following = p.Follow.Value;
                string on = Loc.Get(Strings.Podcast.Pages.FollowingPlayback), off = Loc.Get(Strings.Podcast.Pages.BackToLine);
                Element Word(string w, bool shown) => new BoxEl
                {
                    AlignSelf = FlexAlign.Center, Opacity = shown ? 1f : 0f,
                    Children = [new TextEl(w) { Size = Body("").Size, LineHeight = Body("").LineHeight, Color = Tok.TextPrimary }],
                };
                var button = Button.Create(following ? on : off, () => p.SetFollow(!p.Follow.Peek()),
                    following ? ButtonAppearance.Subtle : ButtonAppearance.Standard, glyph: Icons.MapPin) with { Key = "cpe-follow:button", Shrink = 0f, MinWidth = 0f };
                return Controls.Named(button with
                {
                    Children = [Icon(Icons.MapPin, P.PlayGlyph, Tok.TextPrimary), new BoxEl { ZStack = true, Children = [Word(on, following), Word(off, !following)] }],
                }, following ? on : off);
            }
        }

        /// <summary>One bound transcript slot: its line, time, highlight and chapter heading all bind on <paramref name="idx"/>. With a find text, the
        /// matching lines draw their runs with the matches marked.</summary>
        static Element Line(PodcastEpisodePageProps p, ImmutableArray<PodcastLineVm> lines, IReadSignal<int> idx, IReadSignal<int> cur, bool chrome, string find,
            ColorF accent)
        {
            PodcastLineVm At() => lines[Math.Clamp(idx.Value, 0, lines.Length - 1)];
            bool IsCur() => cur.Value == idx.Value;
            bool Marks() => find.Length > 0 && At().Text.Contains(find, StringComparison.OrdinalIgnoreCase);
            ColorF ink = Design.Palette.ReadableInk(accent);
            var plain = new TextEl("")
            {
                Text = Prop.Of(() => At().Text), Wrap = TextWrap.Wrap, Size = P.TrTextSize, LineHeight = P.TrTextLine, MinWidth = 0f,
                Visible = find.Length == 0 ? true : Prop.Of(() => !Marks()),
                Color = Prop.Of<ColorF>(() => IsCur() ? Tok.TextPrimary : cur.Value > idx.Value ? Tok.TextSecondary : Tok.TextTertiary),
            };
            var kidsText = new List<Element>(3) { plain };
            if (find.Length > 0)
                kidsText.Add(new SpanTextEl(Prop.Of(() => Marks() ? Marked(At().Text, find) : s_noSpans))
                {
                    Wrap = TextWrap.Wrap, Size = P.TrTextSize, LineHeight = P.TrTextLine, MinWidth = 0f, Visible = Prop.Of(() => Marks()),
                });
            kidsText.Add(new BoxEl
            {
                Width = P.TrCurrentStroke, JustifySelf = FlexAlign.Start, AlignSelf = FlexAlign.Stretch, Margin = new Edges4(-P.TrTextPadX, -P.TrTextPadY, 0f, -P.TrTextPadY),
                Fill = accent, Opacity = Prop.Of(() => IsCur() ? 1f : 0f), HitTestVisible = false,
            }.Skeletonized(false));
            var textBox = new BoxEl
            {
                Direction = 0, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f, ZStack = true,
                Padding = new Edges4(P.TrTextPadX, P.TrTextPadY, P.TrTextPadX, P.TrTextPadY), Corners = Radii.ControlAll, BrushTransitionMs = Design.Motion.Faster,
                Fill = Prop.Of(() => IsCur() ? Tok.FillSubtleSecondary : Tok.FillSubtleTransparent), HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary,
                Children = kidsText.ToArray(),
            };
            var row = new List<Element>(2);
            if (chrome)
                row.Add(new BoxEl
                {
                    Width = P.TrTimeW, Shrink = 0f, Direction = 0, Justify = FlexJustify.End, Padding = new Edges4(0f, P.TrTextPadY + P.TrTextPadY, 0f, 0f),
                    Children = [new TextEl("") { Text = Prop.Of(() => At().Time), Size = Caption("").Size, LineHeight = Caption("").LineHeight,
                        Color = Prop.Of(() => IsCur() ? ink : Tok.TextTertiary) }],
                });
            row.Add(textBox);
            var heading = new BoxEl
            {
                Padding = new Edges4(chrome ? P.TrTimeW + P.TrColGap + P.TrTextPadX : P.TrTextPadX, P.SectionBottom, 0f, P.TrTextPadY),
                Visible = Prop.Of(() => idx.Value > 0 && At().Heading is not null),
                Children = [new TextEl("") { Text = Prop.Of(() => At().Heading ?? ""), Size = BodyStrong("").Size, LineHeight = BodyStrong("").LineHeight,
                    Weight = BodyStrong("").Weight, Color = Tok.TextPrimary, Wrap = TextWrap.Wrap, MinWidth = 0f }],
            };
            var line = new BoxEl
            {
                Direction = 0, Gap = P.TrColGap, MinWidth = 0f, AlignItems = FlexAlign.Start, Cursor = CursorId.Hand, Role = AutomationRole.Button, Focusable = true,
                OnClick = () => p.SeekTo(lines[Math.Clamp(idx.Peek(), 0, lines.Length - 1)].StartMs), Children = row.ToArray(),
            };
            return new BoxEl { Direction = 1, MinWidth = 0f, Padding = new Edges4(0f, P.TrLineGap, 0f, 0f), Children = [heading, line] };
        }

        static readonly TextSpans s_noSpans = new([], 0);

        static TextSpans Marked(string text, string find)
        {
            var spans = new List<TextSpan>(4);
            int at = 0;
            while (at < text.Length)
            {
                int hit = text.IndexOf(find, at, StringComparison.OrdinalIgnoreCase);
                if (hit < 0) { spans.Add(new TextSpan(text[at..], Color: Tok.TextPrimary)); break; }
                if (hit > at) spans.Add(new TextSpan(text[at..hit], Color: Tok.TextPrimary));
                spans.Add(new TextSpan(text.Substring(hit, find.Length), Color: Tok.TextPrimary, Background: P.FindMark));
                at = hit + find.Length;
            }
            return new TextSpans(spans.ToArray(), spans.Count);
        }
    }
}
