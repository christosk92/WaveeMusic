// ── Themes/Classic/Structure/Views/Podcast/ClassicPodcastEpisodeParts.cs — the episode page's own parts in Classic (podcast pages v2) ──────────────
//
//   · THE CHAPTER MAP: a card whose head names where the listener is ("Chapter 7 of 12 · name" and the clock, "Played · finished 9 Nov", "12 chapters ·
//     select one to start there") over a band in which every chapter is a segment drawn to its length, filled to the position in the page accent (the
//     current one live, through a compositor scale), a thumb on the current one; a segment grows under the pointer and seeks on click.
//   · THE CHAPTERS LIST: number, name, "start · length · time left", the current row's bar (its room kept on every row), a check once played; the list
//     keeps the current chapter in view as playback crosses into it.
//   · THE TRANSCRIPT CARD: Transcript, "Auto-generated · English", Find in transcript, the follow button (its width held); the chapter heading over the
//     lines (a measured virtual list); the current line highlights and, while following, stays centred; a user scroll stops following, "Back to current
//     line" brings it back; a click seeks; Find marks the matches.
// Numbers: `ClassicPodcastRules`. No data access (WVTH002): everything comes on the props.

using System.Collections.Immutable;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
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

    sealed class MapHost : Component
    {
        readonly Signal<int> _hover = new(-1);
        float[] _segW = [];

        public override Element Render()
        {
            var a = UseProps<MapArgs>();
            var p = a.P;
            var v = p.View.Value;
            var chapters = v.Chapters;
            if (_segW.Length != chapters.Length) _segW = new float[chapters.Length];
            var starts = new int[chapters.Length];
            for (int i = 0; i < starts.Length; i++) starts[i] = chapters[i].StartMs;
            var strong = BodyStrong(v.MapHead) with { Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, Shrink = 1f, MinWidth = 0f };
            var quiet = Body(v.MapSub) with { Color = Tok.TextSecondary, Wrap = TextWrap.NoWrap, MaxLines = 1, Shrink = v.MapQuietFirst ? 0f : 1f, MinWidth = 0f,
                Trim = TextTrim.CharacterEllipsis };
            var head = new Element[]
            {
                new BoxEl { Direction = 0, Grow = 1f, Shrink = 1f, MinWidth = 0f, AlignItems = FlexAlign.Center, Children = v.MapQuietFirst ? [quiet, strong] : [strong, quiet] },
                new TextEl("") { Text = Prop.Bind(p.MapClock), Size = Caption("").Size, LineHeight = Caption("").LineHeight, Color = Tok.TextSecondary, Shrink = 0f },
            };
            var segs = new Element[chapters.Length];
            var cur = p.CurrentChapter;
            var pos = p.PositionMs;
            ColorF accent = a.Accent;
            for (int i = 0; i < chapters.Length; i++)
            {
                var c = chapters[i];
                int index = i, start = c.StartMs, end = c.EndMs;
                float fill = c.Fill;
                Element bar = new BoxEl
                {
                    Height = P.SegRest, AlignSelf = FlexAlign.Stretch, Corners = CornerRadius4.All(P.SegRadius), ClipToBounds = true, Direction = 0,
                    Fill = Prop.Of(() => _hover.Value == index ? Tok.FillControlTertiary : Tok.FillControlSecondary), TransformOriginY = 0.5f,
                    Transform = Prop.Of(() => Affine2D.Scale(1f, _hover.Value == index ? P.SegHover / P.SegRest : 1f)),
                    Children =
                    [
                        new BoxEl
                        {
                            Grow = 1f, TransformOriginX = 0f, Corners = CornerRadius4.All(P.SegRadius),
                            Fill = c.Played && v.Episode.State == PodcastRowState.Played ? Tok.FillControlStrong : accent,
                            Transform = Prop.Of(() => Affine2D.Scale(MathF.Max(0.001f, cur.Value == index
                                ? Math.Clamp((pos.Value - start) / (float)Math.Max(1, end - start), 0f, 1f) : fill), 1f)),
                        },
                    ],
                };
                Element thumb = new BoxEl
                {
                    Width = P.Thumb, Height = P.Thumb, Corners = CornerRadius4.All(P.ThumbHalf), Fill = Tok.FillControlSolid, BorderWidth = P.ThumbStroke,
                    BorderColor = Tok.StrokeControlDefault, AlignSelf = FlexAlign.Center, JustifySelf = FlexAlign.Start, AlignItems = FlexAlign.Center,
                    Justify = FlexJustify.Center, HitTestVisible = false, Visible = Prop.Of(() => cur.Value == index),
                    Transform = Prop.Of(() => Affine2D.Translation(-P.ThumbHalf + Math.Clamp((pos.Value - start) / (float)Math.Max(1, end - start), 0f, 1f)
                        * _segW[Math.Min(index, _segW.Length - 1)], 0f)),
                    Children = [new BoxEl { Width = P.ThumbDot, Height = P.ThumbDot, Corners = CornerRadius4.All(P.ThumbDotHalf), Fill = accent }],
                };
                segs[i] = new BoxEl
                {
                    Key = "cpe-seg:" + i, Grow = MathF.Max(1f, end - start), Basis = 0f, MinWidth = P.SegMin, Height = P.SegBand, ZStack = true,
                    AlignItems = FlexAlign.Center, HitTestVisible = false,
                    OnBoundsChanged = r => { if (index < _segW.Length) _segW[index] = r.W; },
                    Children = [new BoxEl { Direction = 1, Justify = FlexJustify.Center, Children = [bar] }, thumb],
                };
            }
            var segW = _segW;
            return Card("cpe-map-card",
            [
                new BoxEl { Direction = 0, Gap = P.MapGap, AlignItems = FlexAlign.Center, MinWidth = 0f, Children = head },
                new BoxEl
                {
                    Key = "cpe-map-band", Direction = 0, Gap = P.SegGap, Height = a.Target, AlignItems = FlexAlign.Center, MinWidth = 0f,
                    Margin = new Edges4(0f, (P.SegBand - a.Target) / 2f, 0f, (P.SegBand - a.Target) / 2f),
                    Cursor = CursorId.Hand, Role = AutomationRole.Button, Focusable = chapters.Length > 0,
                    OnHoverMove = pt => _hover.SetIfChanged(IndexAt(segW, pt.X)),
                    OnPointerExit = () => _hover.SetIfChanged(-1),
                    OnClick = () =>
                    {
                        int at = _hover.Peek();
                        if (at >= 0 && at < starts.Length) p.SeekTo(starts[at]);
                    },
                    Children = segs,
                },
            ], P.MapGap, new Edges4(P.MapPadX, P.MapPadTop, P.MapPadX, P.MapPadBottom));
        }

        static int IndexAt(float[] widths, float x)
        {
            float at = 0f;
            for (int i = 0; i < widths.Length; i++)
            {
                at += widths[i] + P.SegGap;
                if (x < at) return i;
            }
            return widths.Length - 1;
        }
    }

    // ══ 2. THE CHAPTERS LIST ════════════════════════════════════════════════════════════════════════════════════════════════

    public static Element Chapters(PodcastEpisodePageProps p, PodcastEpisodeView v, float[] rowH, ColorF accent)
    {
        if (v.Chapters.IsDefaultOrEmpty)
            return new BoxEl { Padding = Edges4.All(P.BodyPad), Children = [Body(v.NoChapters) with { Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, MinWidth = 0f }] };
        var rows = new Element[v.Chapters.Length];
        for (int i = 0; i < rows.Length; i++) rows[i] = ChapterRow(p, v.Chapters[i], rowH, accent);
        return new BoxEl { Direction = 1, MinWidth = 0f, Children = rows };
    }

    static Element ChapterRow(PodcastEpisodePageProps p, PodcastChapterVm c, float[] rowH, ColorF accent)
    {
        int index = c.Index, start = c.StartMs, end = c.EndMs;
        var cur = p.CurrentChapter;
        var pos = p.PositionMs;
        bool current = c.Current;
        var meta = new BoxEl
        {
            Direction = 0, MinWidth = 0f,
            Children =
            [
                Caption(c.Start + P.Dot + c.Length) with { Color = Tok.TextSecondary, Shrink = 0f },
                new TextEl("") { Text = Prop.Of(() => cur.Value == index ? p.ChapterLeft.Value : ""), Size = Caption("").Size, LineHeight = Caption("").LineHeight,
                    Color = Tok.TextSecondary, Shrink = 1f, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
            ],
        };
        var bar = new BoxEl
        {
            Height = P.ChBarH, Margin = new Edges4(0f, P.ChBarTop, 0f, 0f), Corners = CornerRadius4.All(P.MiniBarRadius), ClipToBounds = true,
            Fill = Tok.FillControlStrong, Opacity = current ? 1f : 0f, Direction = 0,
            Children =
            [
                new BoxEl
                {
                    Grow = 1f, Fill = accent, TransformOriginX = 0f, Corners = CornerRadius4.All(P.MiniBarRadius),
                    Transform = Prop.Of(() => Affine2D.Scale(MathF.Max(0.001f, cur.Value == index
                        ? Math.Clamp((pos.Value - start) / (float)Math.Max(1, end - start), 0f, 1f) : 0f), 1f)),
                },
            ],
        };
        var name = new TextEl(c.Title)
        {
            Size = Body("").Size, LineHeight = Body("").LineHeight, Wrap = TextWrap.Wrap, MaxLines = 2, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
            Weight = current ? P.Strong : Body("").Weight, Color = c.Played ? Tok.TextSecondary : Tok.TextPrimary,
        };
        return new BoxEl
        {
            Key = "cpe-ch:" + index, Direction = 0, Gap = P.ChGap, AlignItems = FlexAlign.Center, MinHeight = P.ChMinH, MinWidth = 0f,
            Padding = new Edges4(P.ChPadX, P.ChPadY, P.ChPadX, P.ChPadY), Corners = Radii.ControlAll,
            Fill = current ? Tok.FillSubtleSecondary : Tok.FillSubtleTransparent, HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary,
            Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand, OnClick = () => p.SeekTo(start), BrushTransitionMs = Design.Motion.Faster,
            OnBoundsChanged = r => { if (index < rowH.Length) rowH[index] = r.H; },
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
                            AlignSelf = FlexAlign.Center, Margin = new Edges4(-P.ChPadX, 0f, 0f, 0f), Opacity = current ? 1f : 0f, HitTestVisible = false,
                        },
                        new TextEl((index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture))
                        {
                            Size = Body("").Size, LineHeight = Body("").LineHeight, AlignSelf = FlexAlign.Center, JustifySelf = FlexAlign.Center,
                            Color = current ? Design.Palette.ReadableInk(accent) : Tok.TextTertiary,
                        },
                    ],
                },
                new BoxEl { Direction = 1, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f, Children = [name, meta, bar] },
                new BoxEl
                {
                    Width = P.ChCheck, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                    Opacity = c.Played ? 1f : 0f, Children = [Icon(Icons.Check, P.ChCheckGlyph, Tok.TextTertiary)],
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

        public override Element Render()
        {
            _a = UseProps<TranscriptArgs>();
            var p = _a.P;
            var v = p.View.Value;
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
                double target = layout.OffsetOf(line, 0f) - vh / 2 + P.TrLineEstimate / 2;
                _scroll.ScrollTo(Math.Max(0, target), ScrollMove.Follow);
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
                head.Add(new BoxEl
                {
                    Width = P.FindW, Shrink = 1f, MinWidth = 0f,
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
                var list = Virtual.Measured(lines.Length, layout, i => Line(p, lines[i], i, cur, chrome, find, accent), i => "cpe-ln:" + i)
                    with { Handle = _scroll, Grow = 1f, TemplateStamp = find.GetHashCode() };
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
                    Direction = 0, Gap = P.MapGap, AlignItems = FlexAlign.Center, MinHeight = P.ListBarH, Shrink = 0f, MinWidth = 0f,
                    Padding = new Edges4(P.CardHeadPadX, 0f, P.BodyPad, 0f), Children = head.ToArray(),
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

        static Element Line(PodcastEpisodePageProps p, PodcastLineVm l, int index, IReadSignal<int> cur, bool chrome, string find, ColorF accent)
        {
            int start = l.StartMs;
            Func<bool> isCur = () => cur.Value == index;
            Element text = find.Length > 0 && l.Text.Contains(find, StringComparison.OrdinalIgnoreCase)
                ? new SpanTextEl(Marked(l.Text, find)) { Wrap = TextWrap.Wrap, Size = P.TrTextSize, LineHeight = P.TrTextLine, MinWidth = 0f }
                : new TextEl(l.Text)
                {
                    Wrap = TextWrap.Wrap, Size = P.TrTextSize, LineHeight = P.TrTextLine, MinWidth = 0f,
                    Color = Prop.Of<ColorF>(() => isCur() ? Tok.TextPrimary : cur.Value > index ? Tok.TextSecondary : Tok.TextTertiary),
                };
            var textBox = new BoxEl
            {
                Direction = 0, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f, ZStack = true,
                Padding = new Edges4(P.TrTextPadX, P.TrTextPadY, P.TrTextPadX, P.TrTextPadY), Corners = Radii.ControlAll, BrushTransitionMs = Design.Motion.Faster,
                Fill = Prop.Of(() => isCur() ? Tok.FillSubtleSecondary : Tok.FillSubtleTransparent), HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary,
                Children =
                [
                    text,
                    new BoxEl
                    {
                        Width = P.TrCurrentStroke, JustifySelf = FlexAlign.Start, AlignSelf = FlexAlign.Stretch, Margin = new Edges4(-P.TrTextPadX, -P.TrTextPadY, 0f, -P.TrTextPadY),
                        Fill = accent, Opacity = Prop.Of(() => isCur() ? 1f : 0f), HitTestVisible = false,
                    }.Skeletonized(false),
                ],
            };
            var row = new List<Element>(2);
            ColorF ink = Design.Palette.ReadableInk(accent);
            if (chrome)
                row.Add(new BoxEl
                {
                    Width = P.TrTimeW, Shrink = 0f, Direction = 0, Justify = FlexJustify.End, Padding = new Edges4(0f, P.TrTextPadY + P.TrTextPadY, 0f, 0f),
                    Children = [new TextEl(l.Time) { Size = Caption("").Size, LineHeight = Caption("").LineHeight, Color = Prop.Of(() => isCur() ? ink : Tok.TextTertiary) }],
                });
            row.Add(textBox);
            var kids = new List<Element>(2);
            if (l.Heading is { } h && index > 0)
                kids.Add(new BoxEl
                {
                    Padding = new Edges4(chrome ? P.TrTimeW + P.TrColGap + P.TrTextPadX : P.TrTextPadX, P.SectionBottom, 0f, P.TrTextPadY),
                    Children = [BodyStrong(h) with { Color = Tok.TextPrimary, Wrap = TextWrap.Wrap, MinWidth = 0f }],
                });
            kids.Add(new BoxEl
            {
                Direction = 0, Gap = P.TrColGap, MinWidth = 0f, AlignItems = FlexAlign.Start, Cursor = CursorId.Hand, Role = AutomationRole.Button, Focusable = true,
                OnClick = () => p.SeekTo(start), Children = row.ToArray(),
            });
            return new BoxEl { Direction = 1, MinWidth = 0f, Padding = new Edges4(0f, P.TrLineGap, 0f, 0f), Children = kids.ToArray() };
        }

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
