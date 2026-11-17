// ── Themes/WinUi/Structure/Views/Podcast/WinUiPodcastEpisodeParts.cs — the episode page's own parts (podcast pages v2, concept "Episode") ─────────────
//
//   · THE CHAPTER MAP (concept note 6): a card whose head names where the listener is ("Chapter 7 of 12 · name" + the clock, "Played · finished 9 Nov",
//     "12 chapters · select one to start there") over a band in which every chapter is a segment drawn to its length, filled to the position (the
//     current one live, through a compositor scale), a thumb on the current one; a segment grows under the pointer and seeks on click.
//   · THE CHAPTERS LIST: number, name, "start · length · time left", the current row's bar (its space reserved when hidden), a check once played.
//   · THE TRANSCRIPT CARD: the header (Transcript, "Auto-generated · English", Find in transcript, the follow button which keeps its space), a sticky
//     chapter heading, the lines (a measured virtual list: a long episode holds thousands) with their times; the current line highlights and, while
//     following, stays centred; a scroll by the user stops following, "Back to current line" brings it back; a click seeks; Find marks the matches.
//   · THE SMART SPLIT: the accent SplitButton (no accent once played) with the host's menu.
// Numbers: `WinUiPodcastEpisodeRules` and the tier's `WinUiPodcastEpisodeLayout`. No data access (WVTH002): everything comes on the props.

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
using P = Wavee.Themes.WinUi.WinUiPodcastEpisodeRules;

namespace Wavee.Themes.WinUi;

/// <summary>The episode page's own parts.</summary>
internal static class WinUiPodcastEpisodeParts
{
    // ══ 1. THE CHAPTER MAP ══════════════════════════════════════════════════════════════════════════════════════════════════

    public static Element Map(PodcastEpisodePageProps p) => Embed.Comp(p, static () => new MapHost()) with { Key = P.MapKey };

    sealed class MapHost : Component
    {
        readonly Signal<int> _hover = new(-1);

        public override Element Render()
        {
            var p = UseProps<PodcastEpisodePageProps>();
            var v = p.View.Value;
            var chapters = v.Chapters;
            var head = new List<Element>(3);
            var strong = BodyStrong(v.MapHead) with { Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, Shrink = 1f, MinWidth = 0f };
            var quiet = Body(v.MapSub) with { Color = Tok.TextTertiary, Wrap = TextWrap.NoWrap, MaxLines = 1, Shrink = v.MapQuietFirst ? 0f : 1f, MinWidth = 0f,
                Trim = TextTrim.CharacterEllipsis };
            var words = new BoxEl
            {
                Direction = 0, Grow = 1f, Shrink = 1f, MinWidth = 0f, AlignItems = FlexAlign.Center,
                Children = v.MapQuietFirst ? [quiet, strong] : [strong, quiet],
            };
            head.Add(words);
            head.Add(new TextEl("") { Text = Prop.Bind(p.MapClock), Size = Caption("").Size, LineHeight = Caption("").LineHeight, Color = Tok.TextSecondary, Shrink = 0f });

            var segs = new Element[chapters.Length];
            var cur = p.CurrentChapter;
            var pos = p.PositionMs;
            for (int i = 0; i < chapters.Length; i++)
            {
                var c = chapters[i];
                int index = i, start = c.StartMs, end = c.EndMs;
                float fill = c.Fill;
                Element bar = new BoxEl
                {
                    Height = P.SegRest, AlignSelf = FlexAlign.Stretch, Corners = CornerRadius4.All(P.SegRadius), ClipToBounds = true, Direction = 0,
                    Fill = Prop.Of(() => _hover.Value == index ? Tok.FillControlTertiary : Tok.FillControlSecondary),
                    TransformOriginY = 0.5f,
                    Transform = Prop.Of(() => Affine2D.Scale(1f, _hover.Value == index ? P.SegHover / P.SegRest : 1f)),
                    Children =
                    [
                        new BoxEl
                        {
                            Grow = 1f, TransformOriginX = 0f, Corners = CornerRadius4.All(P.SegRadius),
                            Fill = c.Played && v.Episode.State == PodcastRowState.Played ? Tok.FillControlStrong : Tok.AccentDefault,
                            Transform = Prop.Of(() => Affine2D.Scale(MathF.Max(0.001f, cur.Value == index
                                ? Math.Clamp((pos.Value - start) / (float)Math.Max(1, end - start), 0f, 1f) : fill), 1f)),
                        },
                    ],
                };
                Element thumb = new BoxEl
                {
                    Width = P.Thumb, Height = P.Thumb, Corners = CornerRadius4.All(P.ThumbHalf), Fill = Tok.FillControlSolid,
                    BorderWidth = P.ThumbStroke, BorderColor = Tok.StrokeControlDefault, AlignSelf = FlexAlign.Center, JustifySelf = FlexAlign.Start,
                    AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, HitTestVisible = false,
                    Visible = Prop.Of(() => cur.Value == index),
                    Transform = Prop.Of(() => Affine2D.Translation(-P.ThumbHalf + Math.Clamp((pos.Value - start) / (float)Math.Max(1, end - start), 0f, 1f)
                        * _segW[Math.Min(index, _segW.Length - 1)], 0f)),
                    Children = [new BoxEl { Width = P.ThumbDot, Height = P.ThumbDot, Corners = CornerRadius4.All(P.ThumbDotHalf), Fill = Tok.AccentDefault }],
                };
                segs[i] = Controls.Named(new BoxEl
                {
                    Key = "pe-seg:" + i, Grow = MathF.Max(1f, end - start), Basis = 0f, MinWidth = P.SegMin, Height = P.SegBand, ZStack = true,
                    AlignItems = FlexAlign.Center, Cursor = CursorId.Hand, Role = AutomationRole.Button, Focusable = true,
                    OnClick = () => p.SeekTo(start),
                    OnHoverMove = _ => _hover.SetIfChanged(index),
                    OnPointerExit = () => { if (_hover.Peek() == index) _hover.Value = -1; },
                    OnBoundsChanged = r => { if (index < _segW.Length) _segW[index] = r.W; },
                    Children = [new BoxEl { Direction = 1, Justify = FlexJustify.Center, Children = [bar] }, thumb],
                }, (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) + P.Dot + c.Title + P.Dot + c.Start);
            }
            if (_segW.Length != chapters.Length) _segW = new float[chapters.Length];

            return new BoxEl
            {
                Direction = 1, Gap = P.MapGap, MinWidth = 0f, Corners = Radii.CardAll, Fill = Tok.FillCardDefault, BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
                Padding = new Edges4(P.MapPadX, P.MapPadTop, P.MapPadX, P.MapPadBottom),
                Children =
                [
                    new BoxEl { Direction = 0, Gap = P.MapGap, AlignItems = FlexAlign.Center, MinWidth = 0f, Children = head.ToArray() },
                    new BoxEl { Direction = 0, Gap = P.SegGap, Height = P.SegBand, AlignItems = FlexAlign.Center, MinWidth = 0f, Children = segs },
                ],
            };
        }

        float[] _segW = [];
    }

    // ══ 2. THE CHAPTERS LIST ════════════════════════════════════════════════════════════════════════════════════════════════

    public static Element Chapters(PodcastEpisodePageProps p, PodcastEpisodeView v)
    {
        if (v.Chapters.IsDefaultOrEmpty)
            return new BoxEl { Padding = Edges4.All(P.ListBodyPad), Children = [Body(v.NoChapters) with { Color = Tok.TextSecondary }] };
        var rows = new Element[v.Chapters.Length];
        for (int i = 0; i < rows.Length; i++) rows[i] = ChapterRow(p, v.Chapters[i]);
        return new BoxEl { Direction = 1, MinWidth = 0f, Children = rows };
    }

    static Element ChapterRow(PodcastEpisodePageProps p, PodcastChapterVm c)
    {
        int index = c.Index, start = c.StartMs, end = c.EndMs;
        var cur = p.CurrentChapter;
        var pos = p.PositionMs;
        // The row's look is the snapshot's (it is rebuilt when the position crosses a chapter); only the bar and the time left are live.
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
        // The current chapter's bar: its space is reserved on every row, shown on the current one.
        var bar = new BoxEl
        {
            Height = P.BarFill, Margin = new Edges4(0f, P.ChBarTop, 0f, 0f), Corners = CornerRadius4.All(P.BarRadius), ClipToBounds = true,
            Fill = Tok.FillControlStrong, Opacity = current ? 1f : 0f, Direction = 0,
            Children =
            [
                new BoxEl
                {
                    Grow = 1f, Fill = Tok.AccentDefault, TransformOriginX = 0f, Corners = CornerRadius4.All(P.BarRadius),
                    Transform = Prop.Of(() => Affine2D.Scale(MathF.Max(0.001f, cur.Value == index
                        ? Math.Clamp((pos.Value - start) / (float)Math.Max(1, end - start), 0f, 1f) : 0f), 1f)),
                },
            ],
        };
        var name = new TextEl(c.Title)
        {
            Size = Body("").Size, LineHeight = Body("").LineHeight, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
            Weight = current ? BodyStrong("").Weight : Body("").Weight,
            Color = c.Played ? Tok.TextSecondary : Tok.TextPrimary,
        };
        return new BoxEl
        {
            Key = "pe-ch:" + index, Direction = 0, Gap = P.ChGap, AlignItems = FlexAlign.Center, MinHeight = P.ChMinH, MinWidth = 0f,
            Padding = new Edges4(P.ChPadX, P.ChPadY, P.ChPadX, P.ChPadY), Corners = Radii.ControlAll, ZStack = false,
            Fill = current ? Tok.FillSubtleSecondary : Tok.FillSubtleTransparent, HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary,
            Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand, OnClick = () => p.SeekTo(start),
            Children =
            [
                new BoxEl
                {
                    Width = P.ChNum, Shrink = 0f, ZStack = true, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                    Children =
                    [
                        new BoxEl
                        {
                            Width = P.ChTickW, Height = P.ChTickH, Corners = CornerRadius4.All(P.ChTickRadius), Fill = Tok.AccentDefault, JustifySelf = FlexAlign.Start,
                            AlignSelf = FlexAlign.Center, Margin = new Edges4(-P.ChPadX, 0f, 0f, 0f), Opacity = current ? 1f : 0f, HitTestVisible = false,
                        },
                        new TextEl((index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture))
                        {
                            Size = Body("").Size, LineHeight = Body("").LineHeight, AlignSelf = FlexAlign.Center, JustifySelf = FlexAlign.Center,
                            Color = current ? Tok.AccentTextPrimary : Tok.TextTertiary,
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

    public sealed record TranscriptArgs(PodcastEpisodePageProps P, bool Chrome, float Height, bool Joined);

    public static Element Transcript(PodcastEpisodePageProps p, bool chrome, float height, bool joined)
        => Embed.Comp(new TranscriptArgs(p, chrome, height, joined), static () => new TranscriptHost()) with { Key = P.TranscriptKey };

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

            // Follow: keep the current line centred while following; a user scroll stops it.
            UseEffect(() =>
            {
                int line = p.CurrentLine.Value;
                bool follow = p.Follow.Value;
                double vh = _scroll.ViewportSignal.Value;
                if (!follow || line < 0 || vh <= 0 || layout.ItemCount < 0 && lines.Length == 0) return;
                double target = layout.OffsetOf(line, 0f) - vh / 2 + P.TrLineEstimate / 2;
                _scroll.ScrollTo(Math.Max(0, target), ScrollMove.Follow);
            });
            UseEffect(() =>
            {
                var m = _scroll.Motion.Value;
                if (m.UserDriven && p.Follow.Peek()) p.SetFollow(false);
            });

            var head = new List<Element>(4)
            {
                BodyStrong(Loc.Get(Strings.Podcast.Pages.Transcript)) with { Color = Tok.TextPrimary, Shrink = 0f, Grow = _a.Chrome ? 0f : 1f },
            };
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
            head.Add(FollowButton(p));

            Element body;
            if (v.TranscriptLoad == PodcastLoad.Ready && lines.Length == 0)
                body = new BoxEl { Padding = Edges4.All(P.TrPadX), Children = [Body(v.NoTranscript) with { Color = Tok.TextSecondary }] };
            else
            {
                var chrome = _a.Chrome;
                var cur = p.CurrentLine;
                var list = Virtual.Measured(lines.Length, layout, i => Line(p, lines[i], i, cur, chrome, find), i => "pe-ln:" + i)
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
                        new BoxEl
                        {
                            Direction = 0, Shrink = 0f, MinWidth = 0f, Fill = Tok.FillCardDefault,
                            Padding = new Edges4(P.TrPadX, P.TrHeadingTop, P.TrPadX, P.TrHeadingBottom), Children = [heading],
                        },
                        new BoxEl { Direction = 1, Grow = 1f, MinHeight = 0f, Padding = new Edges4(P.TrPadX, 0f, P.TrPadX, P.TrPadBottom), Children = [list] },
                    ],
                };
            }

            return new BoxEl
            {
                Direction = 1, Height = _a.Height, MinWidth = 0f, ClipToBounds = true, Fill = Tok.FillCardDefault, BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
                Corners = _a.Joined ? new CornerRadius4(0f, 0f, Radii.Card, Radii.Card) : Radii.CardAll,
                Children =
                [
                    new BoxEl
                    {
                        Direction = 0, Gap = P.TrHeadPad, AlignItems = FlexAlign.Center, MinHeight = P.TrHeadMinH, Shrink = 0f, MinWidth = 0f,
                        Padding = new Edges4(P.TrHeadPadL, P.TrHeadPad, P.TrHeadPad, P.TrHeadPad), BorderWidth = 0f,
                        Children = head.ToArray(),
                    },
                    new BoxEl { Height = 1f, Shrink = 0f, Fill = Tok.StrokeDividerDefault },
                    body,
                ],
            };
        }

        static readonly string[] s_noSuggest = [];

        /// <summary>The follow button: "Following playback" (subtle) / "Back to current line" (standard). Both words are laid in one ZStack so the button
        /// keeps the longer word's width: the toggle never moves the header.</summary>
        static Element FollowButton(PodcastEpisodePageProps p)
        {
            var follow = p.Follow;
            string on = Loc.Get(Strings.Podcast.Pages.FollowingPlayback), off = Loc.Get(Strings.Podcast.Pages.BackToLine);
            Element Word(string w, bool shownWhenOn) => new BoxEl
            {
                AlignSelf = FlexAlign.Center, Opacity = Prop.Of(() => follow.Value == shownWhenOn ? 1f : 0f),
                Children = [new TextEl(w) { Size = Body("").Size, LineHeight = Body("").LineHeight, Color = Tok.TextPrimary }],
            };
            return Controls.Named(new BoxEl
            {
                Key = "pe-follow", Direction = 0, Gap = P.ChGap, AlignItems = FlexAlign.Center, Shrink = 0f, MinHeight = Controls.ButtonHeight,
                Padding = new Edges4(P.ChPadX + P.TrTextPadY, 0f, P.ChPadX + P.TrTextPadY, 0f), Corners = Radii.ControlAll,
                Fill = Prop.Of(() => follow.Value ? Tok.FillSubtleTransparent : Tok.FillControlDefault),
                HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary,
                BorderWidth = 1f, BorderColor = Prop.Of<ColorF>(() => follow.Value ? Tok.FillSubtleTransparent : Tok.StrokeControlDefault),
                Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand, OnClick = () => p.SetFollow(!follow.Peek()),
                Children =
                [
                    Icon(Icons.MapPin, P.FollowGlyph, Tok.TextPrimary),
                    new BoxEl { ZStack = true, Children = [Word(on, true), Word(off, false)] },
                ],
            }, on);
        }

        static Element Line(PodcastEpisodePageProps p, PodcastLineVm l, int index, IReadSignal<int> cur, bool chrome, string find)
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
                Direction = 0, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f, MaxWidth = P.TrTextMax, ZStack = true,
                Padding = new Edges4(P.TrTextPadX, P.TrTextPadY, P.TrTextPadX, P.TrTextPadY), Corners = Radii.ControlAll,
                Fill = Prop.Of(() => isCur() ? Tok.FillSubtleSecondary : Tok.FillSubtleTransparent), HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary,
                Children =
                [
                    text,
                    new BoxEl
                    {
                        Width = P.TrCurrentStroke, JustifySelf = FlexAlign.Start, AlignSelf = FlexAlign.Stretch, Margin = new Edges4(-P.TrTextPadX, -P.TrTextPadY, 0f, -P.TrTextPadY),
                        Fill = Tok.AccentDefault, Opacity = Prop.Of(() => isCur() ? 1f : 0f), HitTestVisible = false,
                    },
                ],
            };
            var row = new List<Element>(2);
            if (chrome)
                row.Add(new BoxEl
                {
                    Width = P.TrTimeW, Shrink = 0f, Direction = 0, Justify = FlexJustify.End, Padding = new Edges4(0f, P.BarRadius, 0f, 0f),
                    Children = [new TextEl(l.Time) { Size = Caption("").Size, LineHeight = P.TrTimeLine,
                        Color = Prop.Of(() => isCur() ? Tok.AccentTextPrimary : Tok.TextDisabled) }],
                });
            row.Add(textBox);
            var kids = new List<Element>(2);
            if (l.Heading is { } h)
                kids.Add(new BoxEl
                {
                    Padding = new Edges4(chrome ? P.TrTimeW + P.TrColGap + P.TrTextPadX : P.TrTextPadX, P.SectionTop - P.TrHeadingTop, 0f, P.TrTextPadY),
                    Children = [BodyStrong(h) with { Color = Tok.TextPrimary, Wrap = TextWrap.Wrap, MinWidth = 0f }],
                });
            kids.Add(new BoxEl
            {
                Direction = 0, Gap = P.TrColGap, MinWidth = 0f, AlignItems = FlexAlign.Start, Cursor = CursorId.Hand, Role = AutomationRole.Button, Focusable = true,
                OnClick = () => p.SeekTo(start), Children = row.ToArray(),
            });
            return new BoxEl { Direction = 1, MinWidth = 0f, Padding = new Edges4(0f, P.TrLineGap, 0f, 0f), Children = kids.ToArray() };
        }

        /// <summary>The line's runs with every match of <paramref name="find"/> marked.</summary>
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

    // ══ 5. A PROGRESS BAR WITH A LIVE VALUE ═════════════════════════════════════════════════════════════════════════════════

    /// <summary>WinUI's progress bar (a 3-DIP fill on a 1-DIP track) whose fill follows <paramref name="value"/> (0..1) through a compositor scale.</summary>
    public static BoxEl LiveBar(Func<float> value) => new()
    {
        Height = P.BarFill, ZStack = true, MinWidth = 0f,
        Children =
        [
            new BoxEl { Height = P.BarTrack, AlignSelf = FlexAlign.Center, Fill = Tok.StrokeControlStrongDefault },
            new BoxEl
            {
                Height = P.BarFill, Corners = CornerRadius4.All(P.BarRadius), Fill = Tok.AccentDefault, TransformOriginX = 0f,
                Transform = Prop.Of(() => Affine2D.Scale(MathF.Max(0.001f, Math.Clamp(value(), 0f, 1f)), 1f)),
            },
        ],
    };
}
