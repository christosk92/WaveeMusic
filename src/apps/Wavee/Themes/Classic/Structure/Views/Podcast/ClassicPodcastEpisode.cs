// ── Themes/Classic/Structure/Views/Podcast/ClassicPodcastEpisode.cs — Classic's episode page (podcast pages v2, concept "Episode", theme=classic) ──────
//
//   ClassicPodcastKit.Page
//   ├─ the cover column: the still (Video top left, the length bottom right) · the show (its art and name, a link) · Episode N · the 22/28 title · the
//   │    date, length, chapters, Played · the full-width primary in the cover's colour (its menu on the chevron) · the time left over a live bar · Saved ·
//   │    Mark as played · Share · ⋯ · the description (rich text, Read more)
//   └─ the list column: the chapter map · "Up next in this show" (a played episode) · the cards: Chapters | Comments beside the transcript (Standard
//        and up), else one card whose tabs are Chapters | Transcript | Comments · Next in this show · More like this
//
// About is not a tab here: the description is in the column (concept `.t-classic .sbi[data-etab="about"]`). Numbers: `ClassicPodcastRules`, the tier
// record `ClassicPodcastLayout`.

using System.Collections.Immutable;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scroll.Runtime;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;
using P = Wavee.Themes.Classic.ClassicPodcastRules;

namespace Wavee.Themes.Classic;

/// <summary>Classic's episode page.</summary>
internal static class ClassicPodcastEpisode
{
    public static Element Template(PodcastEpisodePageProps p, in ViewContext ctx)
        => new BoxEl { Direction = 1, Grow = 1f, MinHeight = 0f, MinWidth = 0f, Children = [Embed.Comp(p, static () => new Page()) with { Key = "cpe-page" }] };

    static string Run(int n) => new((char)0x2007, n);

    static readonly PodcastEpisodeVm s_seedEp = new("", Run(28), Run(60), 0, false, Run(6), 0, Run(6), "", 0f, PodcastRowState.Unplayed, false, false, false,
        true, 0, false, null);

    /// <summary>The loading snapshot: the loaded layout's shape with figure-space words (the shimmer fills them; they are never read).</summary>
    static readonly IReadSignal<PodcastEpisodeView> s_seed = new Signal<PodcastEpisodeView>(PodcastEpisodeView.Empty with
    {
        Load = PodcastLoad.Ready, Episode = s_seedEp, ShowTitle = Run(16), Crumb = Run(9), Meta = [Run(11), Run(6), Run(11)], PrimaryLabel = Run(6),
        MapHead = Run(24), MapSub = Run(12), MapTime = Run(7), About = Run(120),
        Chapters = [.. Enumerable.Range(0, 8).Select(i => new PodcastChapterVm(i, Run(20 + i % 4 * 4), i * 100, i * 100 + 100, Run(5), Run(6), 0f, false, false))],
        Transcript = [.. Enumerable.Range(0, 12).Select(i => new PodcastLineVm(i * 10, Run(4), Run(40 + i % 3 * 12), i == 0 ? Run(20) : null))],
        TranscriptLoad = PodcastLoad.Ready, TranscriptSub = Run(20), MoreSub = Run(30), More = [s_seedEp, s_seedEp],
    });

    sealed record Args(PodcastEpisodePageProps P, ClassicPodcastLayout Lay, ColorF Accent, ClassicRowVerbs Verbs);

    sealed class Page : Component
    {
        readonly ScrollHandle _scroll = new();
        ClassicRowVerbs? _verbs;
        Func<uint>? _toneOf;

        public override Element Render()
        {
            var p = UseProps<PodcastEpisodePageProps>();
            var facts = TierContext.DrawnIn(UseContext(ThemeFactsContext.Signal)).Value.Tiers;
            var lay = ClassicPodcastTiers.Of(in facts);
            _toneOf ??= () => p.View.Value.Tone;
            uint tone = UseComputed(_toneOf).Value;
            ColorF accent = ClassicPodcastKit.Accent(tone);
            _verbs ??= new ClassicRowVerbs
            {
                Playing = p.OnDeck, Play = p.PlayEpisode, Open = p.Open, ToggleSaved = p.ToggleSavedEpisode,
                Menu = p.RowMenu, Drag = p.RowDrag,
            };
            var a = new Args(p, lay, accent, _verbs);
            var seed = a with { P = p with { View = s_seed } };
            Element column = new SkelRegionEl(
                Pending: () => p.View.Value.Load == PodcastLoad.Pending, Failed: () => p.View.Value.Load == PodcastLoad.Failed,
                Content: () => Embed.Comp(a, static () => new Column()) with { Key = "cpe-column" },
                ShimmerSource: () => Embed.Comp(seed, static () => new Column()) with { Key = "cpe-column" },
                OnFailed: () => Controls.Vacancy(Controls.VacancyVoice.Error), Reveal: SkelReveal.Soft,
                Style: SkeletonStyle.Default with { DeriveComponents = true }, Group: null, SmoothResize: true);
            Element main = new SkelRegionEl(
                Pending: () => p.View.Value.Load == PodcastLoad.Pending, Failed: static () => false,
                Content: () => MainColumn(a), ShimmerSource: () => MainColumn(seed), OnFailed: null, Reveal: SkelReveal.Soft,
                Style: SkeletonStyle.Default with { DeriveComponents = true }, Group: null, SmoothResize: true);
            return ClassicPodcastKit.Page(p.RouteKey, lay, tone, column, main, _scroll);
        }
    }


    static Element MainColumn(Args a) => new BoxEl
    {
        Key = "cpe-main", Direction = 1, MinWidth = 0f, Padding = new Edges4(0f, a.Lay.Stacked ? 0f : P.ColumnPadTop, 0f, 0f),
        Children =
        [
            ClassicPodcastEpisodeParts.Map(a.P, a.Lay.Target, a.Accent) with { Layout = ClassicDetailKit.Shove },
            Embed.Comp(a, static () => new NextUp()) with { Key = "cpe-nextup", Layout = ClassicDetailKit.Shove },
            Embed.Comp(a, static () => new Cards()) with { Key = "cpe-cards", Layout = ClassicTierMotion.Reflow },
            Embed.Comp(a, static () => new More()) with { Key = "cpe-more", Layout = ClassicDetailKit.Shove },
        ],
    };

    // ══ THE COLUMN ══════════════════════════════════════════════════════════════════════════════════════════════════════════

    sealed class Column : Component
    {
        public override Element Render()
        {
            var a = UseProps<Args>();
            var p = a.P;
            var lay = a.Lay;
            var v = p.View.Value;
            var e = v.Episode;
            var kids = new List<Element>(8) { Still(v, lay) };

            var crumb = new List<Element>(3)
            {
                Controls.Artwork(v.ShowArtUrl, P.CrumbArt, P.CrumbArt, P.CrumbArtRadius),
                new BoxEl
                {
                    Key = "cpe-show-link", Shrink = 1f, MinWidth = 0f, Cursor = CursorId.Hand, Role = AutomationRole.Hyperlink, Focusable = true, OnClick = p.OpenShow,
                    Children = [Caption(v.ShowTitle) with
                    {
                        Weight = P.Strong, Color = Tok.TextPrimary, HoverColor = Tok.TextSecondary, PressedColor = Tok.TextTertiary,
                        MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                    }],
                },
            };
            if (v.Crumb.Length > 0) crumb.Add(Caption("· " + v.Crumb) with { Color = Tok.TextTertiary, Shrink = 0f, Wrap = TextWrap.NoWrap });
            var words = new List<Element>(4)
            {
                new BoxEl { Key = "cpe-crumb", Direction = 0, Gap = P.KindGap, AlignItems = FlexAlign.Center, MinWidth = 0f, Children = crumb.ToArray() },
                ClassicPodcastKit.Title(e.Title, small: true),
            };
            if (v.Meta.Length > 0) words.Add(ClassicPodcastKit.Line(new TextSpan(string.Join(P.Dot, v.Meta))));
            kids.Add(new BoxEl { Key = "cpe-id", Direction = 1, Gap = P.FactsRowGap, MinWidth = 0f, Children = words.ToArray() });

            bool pausing = v.PrimaryLabel == Loc.Get(Strings.Podcast.Pages.Pause);
            var up = new List<Element>(2)
            {
                ClassicPodcastKit.Primary("cpe-split", v.PrimaryLabel, pausing ? Icons.Pause : e.State == PodcastRowState.Played ? Icons.Refresh : Icons.Play,
                    a.Accent, v.PrimaryAccent, p.Play, v.Menu, lay.Target),
            };
            if (e.State == PodcastRowState.InProgress)
            {
                var pos = p.PositionMs;
                int duration = Math.Max(1, v.DurationMs);
                up.Add(new BoxEl
                {
                    Key = "cpe-left", Direction = 1, Gap = P.FactsRowGap, MinWidth = 0f,
                    Children =
                    [
                        new TextEl("") { Text = Prop.Bind(p.Left), Size = P.LineSize, LineHeight = P.LineHeight, Weight = P.Strong, Color = Tok.TextPrimary, MaxLines = 1 },
                        ClassicPodcastKit.LiveBar(() => pos.Value / (float)duration, a.Accent),
                    ],
                });
            }
            kids.Add(new BoxEl { Key = "cpe-up", Direction = 1, Gap = P.UpGap, MinWidth = 0f, Children = up.ToArray() });

            string saveLabel = Loc.Get(e.Saved ? Strings.Podcast.Pages.Saved : Strings.Podcast.Pages.Save);
            string markLabel = Loc.Get(e.State == PodcastRowState.Played ? Strings.Podcast.Pages.MarkUnplayed : Strings.Podcast.Pages.MarkPlayed);
            var cmds = new List<Element>(4)
            {
                ClassicPodcastKit.Glyph("cpe-save", e.Saved ? Icons.HeartFill : Icons.Heart, saveLabel, p.ToggleSaved, lay.Target),
                ClassicPodcastKit.Glyph("cpe-mark", e.State == PodcastRowState.Played ? Icons.Undo : Icons.Check, markLabel, p.ToggleCompleted, lay.Target),
            };
            if (p.Share is { } share) cmds.Add(ClassicPodcastKit.Glyph("cpe-share", Icons.Share, Loc.Get(Strings.Podcast.Pages.Share), share, lay.Target));
            cmds.Add(new BoxEl { Key = "cpe-more-button", Shrink = 0f, Children = [ClassicDetailKit.MoreButton(p.Menu, lay.Target)] });
            kids.Add(new BoxEl { Key = "cpe-commands", Direction = 0, Wrap = true, Gap = P.CommandGap, AlignItems = FlexAlign.Center, MinWidth = 0f, Children = cmds.ToArray() });
            if (v.About.Length > 0) kids.Add(ClassicPodcastKit.Description(v.About, "cpe-about"));
            return new BoxEl { Direction = 1, Gap = P.ColumnGap, MinWidth = 0f, Children = kids.ToArray() };
        }

        static Element Still(PodcastEpisodeView v, ClassicPodcastLayout lay)
        {
            bool video = v.VideoLabel.Length > 0;
            float w = video ? lay.StillW : lay.Cover, h = video ? lay.StillW / P.StillAspect : lay.Cover;
            var kids = new List<Element>(3) { Controls.Artwork(v.Episode.ArtUrl ?? v.ShowArtUrl, w, h, P.CoverRadius, decodePx: P.CoverDecode) };
            if (video) kids.Add(StillTag(v.VideoLabel, Icons.Video, FlexJustify.Start, FlexAlign.Start));
            if (v.StillTime.Length > 0) kids.Add(StillTag(v.StillTime, null, FlexJustify.End, FlexAlign.End));
            return new BoxEl
            {
                Key = "cpe-still", Width = w, Height = h, Shrink = 0f, ZStack = true, AlignSelf = FlexAlign.Start, Layout = ClassicTierMotion.Reflow,
                Corners = CornerRadius4.All(P.CoverRadius), Children = kids.ToArray(),
            };
        }

        static Element StillTag(string text, string? glyph, FlexJustify v, FlexAlign h)
        {
            var kids = new List<Element>(2);
            if (glyph is not null) kids.Add(Icon(glyph, P.TagGlyph, Tok.OnMediaPrimary));
            kids.Add(Caption(text) with { Color = Tok.OnMediaPrimary });
            return new BoxEl
            {
                Direction = 1, Padding = Edges4.All(P.StillTagInset), HitTestVisible = false, Justify = v, AlignItems = h,
                Children = [new BoxEl
                {
                    Direction = 0, Gap = P.StillTagGap, AlignItems = FlexAlign.Center, Height = P.StillTagH, Padding = new Edges4(P.StillTagPadX, 0f, P.StillTagPadX, 0f),
                    Corners = Radii.ControlAll, Fill = Tok.MediaScrim, Children = kids.ToArray(),
                }],
            };
        }
    }

    // ══ "UP NEXT IN THIS SHOW" (a played episode) ═══════════════════════════════════════════════════════════════════════════

    sealed class NextUp : Component
    {
        public override Element Render()
        {
            var a = UseProps<Args>();
            var p = a.P;
            var v = p.View.Value;
            if (v.Next is not { } n) return new BoxEl();
            string id = n.Id;
            var play = ClassicPodcastKit.Primary("cpe-nextup:play", Loc.Get(Strings.Podcast.Pages.PlayNext), Icons.Play, a.Accent, true, () => p.PlayEpisode(id), [], a.Lay.Target);
            string sub = n.State == PodcastRowState.InProgress ? n.Left : n.Length;
            var data = new Controls.CardData(id, n.Title, Caption(n.Date.Length > 0 && sub.Length > 0 ? n.Date + P.Dot + sub : n.Date + sub) with { Color = Tok.TextSecondary },
                n.ArtUrl ?? v.ShowArtUrl, () => p.Open(id), ShowMenu: false)
            {
                Eyebrow = Caption(Loc.Get(Strings.Podcast.Pages.UpNextInShow)) with { Color = Tok.TextSecondary },
                Trailing = new BoxEl { Shrink = 0f, Children = [play] },
                Menu = () => p.RowMenu(id),
            };
            return new BoxEl
            {
                Direction = 1, MinWidth = 0f, Margin = new Edges4(0f, P.SectionBottom, 0f, 0f),
                Children = [Controls.Surface(data, Shape.Row(P.RowCardCover) with { Plate = PlateKind.Tile })],
            };
        }
    }

    // ══ THE CARDS ═══════════════════════════════════════════════════════════════════════════════════════════════════════════

    sealed class Cards : Component
    {
        readonly Signal<int> _index = new(0);
        readonly ScrollHandle _chaptersScroll = new();
        Controls.Words.Word[]? _wide, _narrow;
        Args _a = null!;
        Func<PodcastEpisodeView>? _sliceOf;
        ImmutableArray<PodcastChapterVm> _stampChapters;
        int _chaptersStamp;

        /// <summary>A new stamp when the snapshot's chapters change: the bound slots rebuild from them.</summary>
        int ChaptersStamp(ImmutableArray<PodcastChapterVm> chapters)
        {
            if (!_stampChapters.Equals(chapters)) { _stampChapters = chapters; _chaptersStamp++; }
            return _chaptersStamp;
        }

        static readonly PodcastEpisodeTab[] s_wideTabs = [PodcastEpisodeTab.Chapters, PodcastEpisodeTab.Comments];
        static readonly PodcastEpisodeTab[] s_narrowTabs = [PodcastEpisodeTab.Chapters, PodcastEpisodeTab.Transcript, PodcastEpisodeTab.Comments];

        public override Element Render()
        {
            _a = UseProps<Args>();
            var a = _a;
            var p = a.P;
            var lay = a.Lay;
            // Its slice of the snapshot: a change elsewhere (the primary's word, the state) does not re-render this section.
            var v = UseComputed(_sliceOf ??= () =>
            {
                var w = _a.P.View.Value;
                return PodcastEpisodeView.Empty with
                {
                    Load = w.Load, Chapters = w.Chapters, ChaptersPending = w.ChaptersPending, CommentCount = w.CommentCount, CommentsPending = w.CommentsPending,
                    NoChapters = w.NoChapters,
                };
            }).Value;
            var tab = p.Tab.Value;
            // About lives in the column; the transcript is a tab only where the window is too narrow to keep it beside the chapters.
            if (tab == PodcastEpisodeTab.About || lay.TwoCards && tab == PodcastEpisodeTab.Transcript) tab = PodcastEpisodeTab.Chapters;
            var tabs = lay.TwoCards ? s_wideTabs : s_narrowTabs;
            int index = Math.Max(0, Array.IndexOf(tabs, tab));
            UseEffect(() => { _index.SetIfChanged(index); }, DepKey.From(index));
            // The chapters list keeps the current chapter in view as playback crosses into it; a far chapter (a seek deep in a long episode) lands at
            // once (a glide would realise every row on its way).
            var scroll = _chaptersScroll;
            int count = v.Chapters.Length;
            bool chaptersShown = tab == PodcastEpisodeTab.Chapters;
            UseEffect(() =>
            {
                int at = p.CurrentChapter.Value;
                _ = scroll.ViewportSignal.Value;
                if (!chaptersShown || at < 0 || at >= count) return;
                double top = at * P.ChRowH, vh = scroll.ViewportSignal.Peek();
                bool far = Math.Abs(top - scroll.Offset.Peek()) > vh * P.FollowJumpViewports;
                scroll.BringIntoView(top, P.ChRowH, float.NaN, far ? ScrollMove.Immediate : ScrollMove.Glide, P.BodyPad);
            });

            var words = lay.TwoCards ? _wide ??= Words(s_wideTabs) : _narrow ??= Words(s_narrowTabs);
            ColorF accent = a.Accent;
            // The rail freezes its words at mount: each form keys its own (a declared swap); Compact takes the 40-high Display rail (Compact targets).
            Element rail = new BoxEl
            {
                Key = lay.TwoCards ? "cpe-tabs:wide" : lay.Stacked ? "cpe-tabs:big" : "cpe-tabs:narrow", Direction = 0, Grow = 1f, Basis = 0f, MinWidth = 0f,
                Enter = ClassicTierMotion.SwapFade, Exit = ClassicTierMotion.SwapFade,
                Children = [Controls.Words.Rail(words, _index, () => accent, big: lay.Stacked, onSelect: i => _a.P.SetTab((lay.TwoCards ? s_wideTabs : s_narrowTabs)[Math.Clamp(i, 0, (lay.TwoCards ? s_wideTabs : s_narrowTabs).Length - 1)]), fit: true)],
            };
            var head = new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Height = lay.Stacked ? P.ListBarH : P.ListBarH, Shrink = 0f, MinWidth = 0f,
                Padding = new Edges4(P.CardHeadPadX, 0f, P.CardHeadPadX, 0f), Children = [rail],
            };
            var divider = new BoxEl { Height = 1f, Shrink = 0f, Fill = Tok.StrokeDividerDefault };
            bool transcriptTab = !lay.TwoCards && tab == PodcastEpisodeTab.Transcript;
            Element left;
            if (transcriptTab)
                left = ClassicPodcastEpisodeParts.Card("cpe-sections", [head, divider,
                    new BoxEl { Direction = 1, Grow = 1f, MinHeight = 0f, Children = [ClassicPodcastEpisodeParts.Transcript(p, !lay.Stacked, lay.CardsH - P.ListBarH, accent, bare: true)] }])
                    with { Height = lay.CardsH };
            else
            {
                bool chaptersList = chaptersShown && count > 0;
                Element body = tab == PodcastEpisodeTab.Comments ? p.Comments?.Invoke() ?? new BoxEl() : ClassicPodcastEpisodeParts.NoChapters(v.NoChapters);
                left = ClassicPodcastEpisodeParts.Card("cpe-sections",
                [
                    head, divider,
                    chaptersList
                        ? ClassicPodcastEpisodeParts.Chapters(p, v.Chapters, _chaptersScroll, ChaptersStamp(v.Chapters), accent)
                        : ScrollView(new BoxEl { Direction = 1, Padding = Edges4.All(P.BodyPad), MinWidth = 0f, Children = [body] }) with
                        {
                            Key = "cpe-sections:scroll", Grow = 1f, MinHeight = 0f,
                        },
                ]) with { Height = lay.CardsH, Width = lay.TwoCards ? P.ChaptersCardW : float.NaN, Shrink = 0f, Layout = ClassicTierMotion.Reflow };
            }
            var kids = new List<Element>(2) { left };
            if (lay.TwoCards)
                kids.Add(new BoxEl
                {
                    Key = "cpe-tr-col", Grow = 1f, Basis = 0f, MinWidth = 0f, Direction = 1, Layout = ClassicTierMotion.Reflow,
                    Children = [ClassicPodcastEpisodeParts.Transcript(p, true, lay.CardsH, accent)],
                });
            return new BoxEl
            {
                Direction = lay.TwoCards ? (byte)0 : (byte)1, Gap = P.CardGap, MinWidth = 0f, AlignItems = FlexAlign.Stretch,
                Margin = new Edges4(0f, P.CardGap, 0f, 0f), Children = kids.ToArray(),
            };
        }

        Controls.Words.Word[] Words(PodcastEpisodeTab[] tabs)
        {
            var words = new Controls.Words.Word[tabs.Length];
            for (int i = 0; i < tabs.Length; i++)
            {
                var t = tabs[i];
                string label = Loc.Get(t switch
                {
                    PodcastEpisodeTab.Chapters => Strings.Podcast.Pages.Chapters,
                    PodcastEpisodeTab.Transcript => Strings.Podcast.Pages.Transcript,
                    _ => Strings.Podcast.Pages.Comments,
                });
                words[i] = new Controls.Words.Word(label, t switch
                {
                    PodcastEpisodeTab.Chapters => Prop.Of(() => _a.P.View.Value is var v ? P.CountLabel(v.Chapters.Length, v.ChaptersPending) : ""),
                    PodcastEpisodeTab.Comments => Prop.Of(() => _a.P.View.Value is var v ? P.CountLabel(v.CommentCount, v.CommentsPending) : ""),
                    _ => (Prop<string>?)null,
                });
            }
            return words;
        }
    }

    // ══ "NEXT IN THIS SHOW" AND "MORE LIKE THIS" ════════════════════════════════════════════════════════════════════════════

    sealed class More : Component
    {
        public override Element Render()
        {
            var a = UseProps<Args>();
            var p = a.P;
            var lay = a.Lay;
            var v = p.View.Value;
            var kids = new List<Element>(4);
            if (!v.More.IsDefaultOrEmpty)
            {
                kids.Add(ClassicPodcastKit.SectionHeader("cpe-next-head", Loc.Get(Strings.Podcast.Pages.NextInShow), Loc.Get(Strings.Podcast.Pages.AllEpisodes), p.OpenShow));
                var rows = new Element[v.More.Length];
                for (int i = 0; i < rows.Length; i++) rows[i] = ClassicPodcastKit.EpisodeRow(v.More[i], lay, description: lay.RowDescription, a.Accent, a.Verbs);
                kids.Add(new BoxEl { Key = "cpe-next-rows", Direction = 1, MinWidth = 0f, Children = rows });
            }
            if (!v.Similar.IsDefaultOrEmpty)
            {
                kids.Add(ClassicPodcastKit.SectionHeader("cpe-similar-head", Loc.Get(Strings.Podcast.Pages.MoreLikeThis)));
                kids.Add(ClassicPodcastKit.MoreLikeThis(v.Similar, p.OpenUri, lay.Stacked, p.CardMenu, p.PlayCard));
            }
            return new BoxEl { Direction = 1, MinWidth = 0f, Children = kids.ToArray() };
        }
    }
}
