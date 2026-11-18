// ── Themes/Zune/Structure/Views/Podcast/ZunePodcastEpisode.cs — Zune's episode page (podcast pages v2, concept `epZune`) ───────────────────────────────
//
//   ZunePodcastKit.Page (one scroller over the page column)
//   └─ the episode's name (120 light, lowercase, bleeding under the fade; 80 wrapping at Compact)
//      the pivots: chapters N · about · (transcript, below Standard) · comments N; they swap the left column (the whole column below Standard)
//      the hero: the flat 16:9 still (its video tag, its clock) · [the accent label ("continue", "played", "new") · the split primary and, while under
//                way, the time left over the live line · the meta (the show, a link · episode N · the date · the length) · saved / mark as played /
//                share / ...]
//      the chapter map: "chapter 7 of 12 · why you forget names" (20/28) and the clock, over a band of square segments drawn to their length, filled to
//                the position (the current one live); a segment grows under the pointer and seeks on click
//      "up next in this show" (a played episode)
//      the columns: Standard and up [380: the pivot's body | the transcript]; below, one column (the transcript is a pivot)
//      "next in this show" (the rows) · "more like this" (the house cards)
//
// The transcript follows playback (the current line centred while following; a user scroll stops it, "back to current line" brings it back; a click
// seeks; find marks the matches). Loading is the loaded layout over a seed snapshot, derived flat. Numbers: `ZunePodcastRules`.

using System.Collections.Immutable;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scene;
using FluentGpu.Scroll.Runtime;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Theming.Roles;
using static FluentGpu.Dsl.Ui;
using K = Wavee.Themes.Zune.ZunePodcastKit;
using Z = Wavee.Themes.Zune.ZunePodcastRules;

namespace Wavee.Themes.Zune;

/// <summary>Zune's episode page.</summary>
internal static class ZunePodcastEpisode
{
    public static Element Template(PodcastEpisodePageProps p, in ViewContext ctx)
        => new BoxEl { Direction = 1, Grow = 1f, MinHeight = 0f, MinWidth = 0f, Children = [Embed.Comp(p, static () => new Page()) with { Key = "zune-podcast-episode" }] };

    static readonly IReadSignal<PodcastEpisodeView> s_seed = new Signal<PodcastEpisodeView>(ZunePodcastSeed.Episode);
    static readonly IReadSignal<string?> s_none = new Signal<string?>(null);

    sealed record Args(PodcastEpisodePageProps P, ZunePodcastLayout Lay, float Gutter, ZunePodcastRowVerbs Verbs);

    sealed class Page : Component
    {
        readonly ScrollHandle _scroll = new();
        ZunePodcastRowVerbs? _verbs;
        Func<(string Title, int Chapters, bool ChPending, int Comments, bool CoPending)>? _head;
        Action<int>? _select;
        PodcastEpisodeTab[] _tabs = [];

        public override Element Render()
        {
            var p = UseProps<PodcastEpisodePageProps>();
            var facts = TierContext.DrawnIn(UseContext(ThemeFactsContext.Signal)).Value.Tiers;
            var lay = ZunePodcastTiers.Of(in facts);
            float gutter = ZuneTiers.GutterOf(in facts);
            _verbs ??= new ZunePodcastRowVerbs
            {
                Expanded = s_none, Playing = p.OnDeck, Play = p.PlayEpisode, Open = p.Open, PlayNext = p.PlayNext, Expand = null, Menu = p.RowMenu,
                Drag = p.RowDrag,
            };
            _head ??= () =>
            {
                var v = p.View.Value;
                return (v.Episode.Title, v.Chapters.Length, v.ChaptersPending, v.CommentCount, v.CommentsPending);
            };
            var h = UseComputed(_head).Value;
            _tabs = lay.TwoColumns ? s_wideTabs : s_narrowTabs;
            var tab = p.Tab.Value;
            if (lay.TwoColumns && tab == PodcastEpisodeTab.Transcript) tab = PodcastEpisodeTab.Chapters;
            var tabs = _tabs;
            _select ??= i => p.SetTab(_tabs[Math.Clamp(i, 0, _tabs.Length - 1)]);
            var items = ImmutableArray.CreateBuilder<K.PivotItem>(tabs.Length);
            foreach (var t in tabs)
                items.Add(t switch
                {
                    PodcastEpisodeTab.Chapters => new K.PivotItem(Loc.Get(Strings.Podcast.Pages.Chapters), Count(h.Chapters, h.ChPending)),
                    PodcastEpisodeTab.About => new K.PivotItem(Loc.Get(Strings.Podcast.Pages.About), ""),
                    PodcastEpisodeTab.Transcript => new K.PivotItem(Loc.Get(Strings.Podcast.Pages.Transcript), ""),
                    _ => new K.PivotItem(Loc.Get(Strings.Podcast.Pages.Comments), Count(h.Comments, h.CoPending)),
                });
            var a = new Args(p, lay, gutter, _verbs);
            var seed = new Args(p with { View = s_seed }, lay, gutter, _verbs);
            var region = new SkelRegionEl(
                Pending: () => p.View.Value.Load == PodcastLoad.Pending, Failed: () => p.View.Value.Load == PodcastLoad.Failed,
                Content: () => Column(a), ShimmerSource: () => Column(seed),
                OnFailed: () => Controls.Vacancy(Controls.VacancyVoice.Error), Reveal: SkelReveal.Soft,
                Style: SkeletonStyle.Default with { DeriveComponents = true }, Group: null, SmoothResize: false);
            var column = new BoxEl
            {
                Key = "zune-episode-column", Direction = 1, MinWidth = 0f,
                Children =
                [
                    K.Title(h.Title, lay, gutter),
                    K.Pivots("ze-pivots", items.ToImmutable(), Math.Max(0, Array.IndexOf(tabs, tab)), _select, lay, gutter),
                    region,
                ],
            };
            return K.Page(p.RouteKey, column, gutter, _scroll);
        }

        /// <summary>A pivot's count: the number, or a figure space while it is on its way (the word keeps its room).</summary>
        static string Count(int n, bool pending) => pending ? " " : n > 0 ? n.ToString(System.Globalization.CultureInfo.CurrentCulture) : "";

        static readonly PodcastEpisodeTab[] s_wideTabs = [PodcastEpisodeTab.Chapters, PodcastEpisodeTab.About, PodcastEpisodeTab.Comments];
        static readonly PodcastEpisodeTab[] s_narrowTabs = [PodcastEpisodeTab.Chapters, PodcastEpisodeTab.About, PodcastEpisodeTab.Transcript, PodcastEpisodeTab.Comments];
    }

    static Element Column(Args a) => new BoxEl
    {
        Direction = 1, MinWidth = 0f,
        Children =
        [
            Embed.Comp(a, static () => new Hero()) with { Key = "ze-hero", Layout = ZuneDetailKit.Shove },
            Embed.Comp(a, static () => new MapHost()) with { Key = "ze-map", Layout = ZuneDetailKit.Shove },
            Embed.Comp(a, static () => new NextUp()) with { Key = "ze-nextup", Layout = ZuneDetailKit.Shove },
            Embed.Comp(a, static () => new Cols()) with { Key = "ze-cols", Layout = ZuneDetailKit.Shove },
            Embed.Comp(a, static () => new More()) with { Key = "ze-more", Layout = ZuneDetailKit.Shove },
        ],
    };

    // ══ THE HERO ════════════════════════════════════════════════════════════════════════════════════════════════════════════

    sealed class Hero : Component
    {
        public override Element Render()
        {
            var a = UseProps<Args>();
            var p = a.P;
            var lay = a.Lay;
            var v = p.View.Value;
            var e = v.Episode;
            float stillW = lay.Still > 0f ? lay.Still : lay.StillMax;
            float stillH = stillW / Z.StillAspect;
            var stillKids = new List<Element>(3) { Controls.Artwork(e.ArtUrl ?? v.ShowArtUrl, stillW, stillH, 0f) };
            if (v.VideoLabel.Length > 0) stillKids.Add(Tag(v.VideoLabel, Icons.Video, FlexJustify.Start, FlexAlign.Start));
            if (v.StillTime.Length > 0) stillKids.Add(Tag(v.StillTime, null, FlexJustify.End, FlexAlign.End));
            Element still = new BoxEl
            {
                Key = "ze-still", Width = stillW, Height = stillH, Shrink = 0f, ZStack = true, AlignSelf = FlexAlign.Start, Layout = ZuneMotion.GroupResize,
                Children = stillKids.ToArray(),
            };
            string label = Loc.Get(e.State switch
            {
                PodcastRowState.InProgress => Strings.Podcast.Pages.Continue,
                PodcastRowState.Played => Strings.Podcast.Pages.Played,
                _ => Strings.Podcast.Pages.Fresh,
            });
            var main = new List<Element>(5) { K.Label(label), Up(a, v), Meta(p, v), Commands(p, v, lay) };
            var words = new BoxEl { Direction = 1, Gap = Z.HeroLineGap, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f, Children = main.ToArray() };
            return new BoxEl
            {
                Direction = (byte)(lay.Stacked ? 1 : 0), Gap = lay.Stacked ? Z.HeroRowGap : Z.HeroColGap, MinWidth = 0f,
                AlignItems = lay.Stacked ? FlexAlign.Stretch : FlexAlign.Start, Margin = new Edges4(0f, 0f, 0f, Z.HeroBottom), Layout = ZuneMotion.GroupResize,
                Children = [still, words],
            };
        }

        static Element Tag(string text, string? glyph, FlexJustify v, FlexAlign h)
        {
            var kids = new List<Element>(2);
            if (glyph is not null) kids.Add(Icon(glyph, Z.StillTagGlyph, Tok.OnMediaPrimary));
            kids.Add(new TextEl(text) { Size = Z.StillTagSize, LineHeight = Z.StillTagH, Color = Tok.OnMediaPrimary, Case = R.Case.For(LabelKind.Label) });
            var tag = new BoxEl
            {
                Direction = 0, Gap = Z.MetaGap, AlignItems = FlexAlign.Center, Height = Z.StillTagH, Padding = new Edges4(Z.StillTagPadX, 0f, Z.StillTagPadX, 0f),
                Fill = Tok.MediaScrim, Children = kids.ToArray(),
            };
            return new BoxEl { Direction = 1, Padding = Edges4.All(Z.StillTagInset), HitTestVisible = false, Justify = v, AlignItems = h, Children = [tag] };
        }

        static Element Up(Args a, PodcastEpisodeView v)
        {
            var p = a.P;
            var lay = a.Lay;
            var e = v.Episode;
            bool pausing = v.PrimaryLabel == Loc.Get(Strings.Podcast.Pages.Pause);
            Element split = K.Split("ze-split", v.PrimaryLabel, v.PrimaryAccent, pausing, p.Play, v.Menu, lay.Target, lay.PrimaryFills);
            if (e.State != PodcastRowState.InProgress)
                return new BoxEl { Key = "ze-up", Direction = 0, MinWidth = 0f, Children = [split] };
            var pos = p.PositionMs;
            int duration = Math.Max(1, v.DurationMs);
            var left = new BoxEl
            {
                Key = "ze-left", Direction = 1, Gap = Z.UpLineGap, Grow = 1f, Basis = 0f, MinWidth = 0f, Justify = FlexJustify.Center,
                Children =
                [
                    new TextEl("") { Text = Prop.Bind(p.Left), Size = Z.ButtonTextSize, LineHeight = Z.ButtonTextLine, Weight = Z.Strong, Color = Tok.TextPrimary, Case = R.Case.For(LabelKind.Label), MaxLines = 1 },
                    K.Line(() => pos.Value / (float)duration),
                ],
            };
            return new BoxEl
            {
                Key = "ze-up", Direction = (byte)(lay.Stacked ? 1 : 0), Gap = lay.Stacked ? Z.HeroLineGap : Z.UpGap, MinWidth = 0f,
                AlignItems = lay.Stacked ? FlexAlign.Stretch : FlexAlign.Center,
                Children = [new BoxEl { Direction = 0, Shrink = 0f, MinWidth = 0f, Children = [split] }, left],
            };
        }

        static Element Meta(PodcastEpisodePageProps p, PodcastEpisodeView v)
        {
            var parts = new List<Element>(6);
            if (v.ShowTitle.Length > 0) parts.Add(ShowLink(v.ShowTitle, p.OpenShow));
            if (v.Crumb.Length > 0) parts.Add(K.MetaWord(v.Crumb));
            foreach (var m in v.Meta) parts.Add(K.MetaWord(m));
            return K.Meta("ze-meta", parts);
        }

        static Element ShowLink(string name, Action open) => new BoxEl
        {
            Key = "ze-show", Direction = 0, Shrink = 1f, MinWidth = 0f, Cursor = CursorId.Hand, Role = AutomationRole.Hyperlink, Focusable = true,
            AllowFocusOnInteraction = false, OnClick = open,
            Children = [K.MetaWord(name) with { Case = TextCase.AsWritten, HoverColor = Tok.TextPrimary, PressedColor = Tok.TextTertiary, Trim = TextTrim.CharacterEllipsis, Shrink = 1f, MinWidth = 0f }],
        };

        static Element Commands(PodcastEpisodePageProps p, PodcastEpisodeView v, ZunePodcastLayout lay)
        {
            var e = v.Episode;
            float t = lay.Target;
            string save = Loc.Get(e.Saved ? Strings.Podcast.Pages.Saved : Strings.Podcast.Pages.Save);
            string mark = Loc.Get(e.State == PodcastRowState.Played ? Strings.Podcast.Pages.MarkUnplayed : Strings.Podcast.Pages.MarkPlayed);
            var kids = new List<Element>(4)
            {
                K.Named(K.Button("cmd:save", lay.Words ? save : null, e.Saved ? Icons.HeartFill : Icons.Heart, p.ToggleSaved, t, on: e.Saved), save, lay.Words),
                K.Named(K.Button("cmd:mark", lay.Words ? mark : null, Icons.Check, p.ToggleCompleted, t, on: e.State == PodcastRowState.Played), mark, lay.Words),
            };
            if (p.Share is { } share) kids.Add(K.Named(K.Button("cmd:share", null, Icons.Share, share, t), Loc.Get(Strings.Podcast.Pages.Share), false));
            kids.Add(K.More("cmd:more", p.Menu, t));
            return new BoxEl { Key = "ze-cmds", Direction = 0, Gap = Z.CommandGap, Wrap = true, AlignItems = FlexAlign.Center, MinWidth = 0f, Children = kids.ToArray() };
        }
    }

    // ══ THE CHAPTER MAP ═════════════════════════════════════════════════════════════════════════════════════════════════════

    sealed class MapHost : Component
    {
        readonly Signal<int> _hover = new(-1);
        float[] _segW = [];

        public override Element Render()
        {
            var a = UseProps<Args>();
            var p = a.P;
            var v = p.View.Value;
            var chapters = v.Chapters;
            if (chapters.IsDefaultOrEmpty) return new BoxEl();
            if (_segW.Length != chapters.Length) _segW = new float[chapters.Length];
            var starts = new int[chapters.Length];
            for (int i = 0; i < starts.Length; i++) starts[i] = chapters[i].StartMs;
            var strong = new TextEl(v.MapHead)
            {
                FontFamily = R.Type.Pivot("").FontFamily, Size = Z.MapHeadSize, LineHeight = Z.MapHeadLine, Weight = Z.Strong, Color = Tok.TextPrimary,
                // The head is the current chapter's NAME (as written) when the quiet words lead it, else the theme's words ("played", "12 chapters").
                Case = v.MapQuietFirst ? TextCase.AsWritten : R.Case.For(LabelKind.Label), MaxLines = 1, Trim = TextTrim.CharacterEllipsis, Shrink = 1f, MinWidth = 0f,
            };
            var quiet = new TextEl(v.MapSub)
            {
                FontFamily = R.Type.Pivot("").FontFamily, Size = Z.MapHeadSize, LineHeight = Z.MapHeadLine, Weight = Z.Light, Color = Tok.TextTertiary,
                Case = R.Case.For(LabelKind.Label), MaxLines = 1, Trim = TextTrim.CharacterEllipsis, Shrink = v.MapQuietFirst ? 0f : 1f, MinWidth = 0f,
            };
            var head = new BoxEl
            {
                Direction = 0, Gap = Z.MapGap, AlignItems = FlexAlign.Center, MinWidth = 0f,
                Children =
                [
                    new BoxEl { Direction = 0, Grow = 1f, Shrink = 1f, MinWidth = 0f, AlignItems = FlexAlign.Center, Children = v.MapQuietFirst ? [quiet, strong] : [strong, quiet] },
                    new TextEl("") { Text = Prop.Bind(p.MapClock), Size = Z.CountSize, LineHeight = Z.CountLine, Color = Tok.TextSecondary, Shrink = 0f },
                ],
            };
            var segs = new Element[chapters.Length];
            var cur = p.CurrentChapter;
            var pos = p.PositionMs;
            bool playedAll = v.Episode.State == PodcastRowState.Played;
            for (int i = 0; i < chapters.Length; i++)
            {
                var c = chapters[i];
                int index = i, start = c.StartMs, end = c.EndMs;
                float fill = c.Fill;
                Element bar = new BoxEl
                {
                    Height = Z.SegH, AlignSelf = FlexAlign.Stretch, ClipToBounds = true, Direction = 0, TransformOriginY = 0.5f,
                    Fill = Prop.Of(static () => Tok.TextPrimary with { A = Z.SegTrackAlpha }),
                    Transform = Prop.Of(() => Affine2D.Scale(1f, _hover.Value == index ? Z.SegHover / Z.SegH : 1f)),
                    Children =
                    [
                        new BoxEl
                        {
                            Grow = 1f, TransformOriginX = 0f,
                            Fill = c.Played && playedAll ? Prop.Of(static () => Tok.TextTertiary) : Prop.Of(static () => Tok.AccentDefault),
                            Transform = Prop.Of(() => Affine2D.Scale(MathF.Max(0.001f, cur.Value == index
                                ? Math.Clamp((pos.Value - start) / (float)Math.Max(1, end - start), 0f, 1f) : fill), 1f)),
                        },
                    ],
                };
                segs[i] = new BoxEl
                {
                    Key = "ze-seg:" + i, Grow = MathF.Max(1f, end - start), Basis = 0f, MinWidth = Z.SegMin, Height = Z.MapBand, Direction = 1,
                    Justify = FlexJustify.Center, HitTestVisible = false,
                    OnBoundsChanged = r => { if (index < _segW.Length) _segW[index] = r.W; },
                    Children = [bar],
                };
            }
            var segW = _segW;
            return new BoxEl
            {
                Direction = 1, Gap = Z.MapGap, MinWidth = 0f, Margin = new Edges4(0f, 0f, 0f, Z.MapBottom),
                Children =
                [
                    head,
                    new BoxEl
                    {
                        Key = "ze-map-band", Direction = 0, Gap = Z.SegGap, Height = MathF.Max(Z.MapBand, a.Lay.Target), AlignItems = FlexAlign.Center, MinWidth = 0f,
                        Cursor = CursorId.Hand, Role = AutomationRole.Button, Focusable = true,
                        OnHoverMove = pt => _hover.SetIfChanged(IndexAt(segW, pt.X)),
                        OnPointerExit = () => _hover.SetIfChanged(-1),
                        OnClick = () =>
                        {
                            int at = _hover.Peek();
                            if (at >= 0 && at < starts.Length) p.SeekTo(starts[at]);
                        },
                        Children = segs,
                    },
                ],
            };
        }

        static int IndexAt(float[] widths, float x)
        {
            float at = 0f;
            for (int i = 0; i < widths.Length; i++)
            {
                at += widths[i] + Z.SegGap;
                if (x < at) return i;
            }
            return widths.Length - 1;
        }
    }

    // ══ "UP NEXT IN THIS SHOW" ══════════════════════════════════════════════════════════════════════════════════════════════

    sealed class NextUp : Component
    {
        public override Element Render()
        {
            var a = UseProps<Args>();
            var v = a.P.View.Value;
            if (v.Next is not { } n) return new BoxEl();
            return new BoxEl
            {
                Direction = 1, MinWidth = 0f, Margin = new Edges4(0f, 0f, 0f, Z.MapBottom),
                Children = [K.Section("ze-nextup-sec", Loc.Get(Strings.Podcast.Pages.UpNextInShow), K.EpisodeRow(n, a.Lay.RowDescription, a.Lay.Stacked, a.Verbs))],
            };
        }
    }

    // ══ THE COLUMNS ═════════════════════════════════════════════════════════════════════════════════════════════════════════

    sealed class Cols : Component
    {
        readonly ScrollHandle _chaptersScroll = new();
        ImmutableArray<PodcastChapterVm> _stampChapters;
        int _stamp;

        public override Element Render()
        {
            var a = UseProps<Args>();
            var p = a.P;
            var lay = a.Lay;
            var v = p.View.Value;
            var tab = p.Tab.Value;
            if (lay.TwoColumns && tab == PodcastEpisodeTab.Transcript) tab = PodcastEpisodeTab.Chapters;
            if (!_stampChapters.Equals(v.Chapters)) { _stampChapters = v.Chapters; _stamp++; }
            // The list keeps the current chapter in view as playback crosses into it (the minimal move: a row already shown stays put); a far one (a
            // start or a seek deep in a long episode) lands at once, since a glide would realize every row on its way.
            var scroll = _chaptersScroll;
            int count = v.Chapters.Length;
            bool chaptersShown = tab == PodcastEpisodeTab.Chapters;
            UseEffect(() =>
            {
                int at = p.CurrentChapter.Value;
                _ = scroll.ViewportSignal.Value;
                if (!chaptersShown || at < 0 || at >= count) return;
                double top = at * Z.ChRowH, vh = scroll.ViewportSignal.Peek();
                bool far = Math.Abs(top - scroll.Offset.Peek()) > vh * Z.FollowJumpViewports;
                scroll.BringIntoView(top, Z.ChRowH, float.NaN, far ? ScrollMove.Immediate : ScrollMove.Glide, 0f);
            });
            Element body = tab switch
            {
                PodcastEpisodeTab.About => new BoxEl { Direction = 1, MinWidth = 0f, MaxWidth = Z.AboutMax, Children = [K.Description(v.About, 0, Z.BodySize)] },
                PodcastEpisodeTab.Comments => p.Comments?.Invoke() ?? new BoxEl(),
                PodcastEpisodeTab.Transcript => Transcript(p),
                _ => Chapters(p, v, _chaptersScroll, _stamp, lay.TwoColumns),
            };
            Element left = new BoxEl
            {
                Key = "ze-left-col", Direction = 1, MinWidth = 0f, Width = lay.TwoColumns ? Z.ChaptersW : float.NaN, Shrink = 0f, Animate = ZuneMotion.GroupResize,
                // Beside the transcript the column takes the row's height and the chapters' list fills it (its scroller ends with the transcript's).
                Children = [K.Body("ze-tab:" + tab, body, fill: lay.TwoColumns)],
            };
            var kids = new List<Element>(2) { left };
            if (lay.TwoColumns)
                kids.Add(new BoxEl
                {
                    Key = "ze-tr-col", Grow = 1f, Basis = 0f, MinWidth = 0f, Direction = 1, Enter = ZuneMotion.SwapFade, Exit = ZuneMotion.SwapFade, Children = [Transcript(p)],
                });
            return new BoxEl
            {
                Direction = (byte)(lay.TwoColumns ? 0 : 1), Gap = lay.TwoColumns ? Z.ColsGap : 0f, MinWidth = 0f,
                AlignItems = FlexAlign.Stretch, Children = kids.ToArray(),
            };
        }
    }

    /// <summary>The chapters as a BOUND virtual list of one row height in its own scroller (the transcript's height beside it): each visible slot is
    /// built once and rebound by its index, so a long episode (a hundred chapters) costs the visible rows. <paramref name="stamp"/> rebuilds the slots
    /// when the snapshot's chapters change.</summary>
    static Element Chapters(PodcastEpisodePageProps p, PodcastEpisodeView v, ScrollHandle scroll, int stamp, bool fill)
    {
        if (v.Chapters.IsDefaultOrEmpty)
            return R.Type.TrackTitle(v.NoChapters) with { Color = Tok.TextSecondary, Case = R.Case.For(LabelKind.Label), Wrap = TextWrap.Wrap, MinWidth = 0f };
        var chapters = v.Chapters;
        return new BoxEl
        {
            // Beside the transcript it fills the column (its scroller ends with the transcript's); alone, it is the transcript's height at most.
            Key = "ze-chapters", Direction = 1, MinWidth = 0f, Grow = fill ? 1f : 0f, Basis = fill ? 0f : float.NaN,
            Height = fill ? float.NaN : MathF.Min(Z.TrH, chapters.Length * Z.ChRowH), MinHeight = fill ? MathF.Min(Z.TrH, chapters.Length * Z.ChRowH) : 0f,
            Children =
            [
                Virtual.ListBound(chapters.Length, Z.ChRowH, idx => ChapterRow(p, chapters, idx)) with
                {
                    Key = "ze-chapters:list", Grow = 1f, MinHeight = 0f, Handle = scroll, TemplateStamp = stamp,
                },
            ],
        };
    }

    /// <summary>One bound chapter slot: everything that differs by chapter binds on <paramref name="idx"/> (the clicks read it at the click).</summary>
    static Element ChapterRow(PodcastEpisodePageProps p, ImmutableArray<PodcastChapterVm> chapters, IReadSignal<int> idx)
    {
        var cur = p.CurrentChapter;
        var state = p.State;
        PodcastChapterVm At() => chapters[Math.Clamp(idx.Value, 0, chapters.Length - 1)];
        bool Current() => cur.Value == idx.Value;
        bool Played() => state?.Value == PodcastRowState.Played || (cur.Value >= 0 && idx.Value < cur.Value) || (cur.Value < 0 && At().Played);
        var metaVoice = R.Type.TrackMeta("");
        var name = new TextEl("")
        {
            Text = Prop.Of(() => At().Title), FontFamily = R.Type.Pivot("").FontFamily, Size = Z.ChNameSize, LineHeight = Z.ChNameLine, Weight = Z.Light,
            MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
            Color = Prop.Of(() => Current() ? R.Ink.AccentDecor : Played() ? Tok.TextTertiary : Tok.TextPrimary),
        };
        var meta = new BoxEl
        {
            Direction = 0, MinWidth = 0f,
            Children =
            [
                new TextEl("") { Text = Prop.Of(() => At().Span), Size = metaVoice.Size, LineHeight = metaVoice.LineHeight, Color = Tok.TextTertiary, Shrink = 0f },
                new TextEl("")
                {
                    Text = Prop.Of(() => Current() ? p.ChapterLeft.Value : ""), Size = metaVoice.Size, LineHeight = metaVoice.LineHeight,
                    Color = Prop.Of(static () => R.Ink.AccentDecor), Shrink = 1f, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                },
            ],
        };
        return new BoxEl
        {
            Direction = 0, Gap = Z.ChGap, AlignItems = FlexAlign.Center, Height = Z.ChRowH, MinWidth = 0f,
            Fill = ColorF.Transparent, HoverFill = Prop.Of(static () => Tok.TextPrimary with { A = Z.RowHoverAlpha }),
            PressedFill = Prop.Of(static () => Tok.TextPrimary with { A = Z.RowPressedAlpha }),
            Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand, BrushTransitionMs = Design.Motion.Faster,
            OnClick = () => p.SeekTo(chapters[Math.Clamp(idx.Peek(), 0, chapters.Length - 1)].StartMs),
            Children =
            [
                new BoxEl
                {
                    Width = Z.ChNumW, Shrink = 0f, Direction = 0, Justify = FlexJustify.Center,
                    Children = [new TextEl("")
                    {
                        Text = Prop.Of(() => At().Number), FontFamily = R.Type.Pivot("").FontFamily, Size = Z.ChNumSize, LineHeight = Z.ChNumLine, Weight = Z.Light,
                        Color = Prop.Of(() => Current() ? R.Ink.AccentDecor : Tok.TextTertiary),
                    }],
                },
                new BoxEl { Direction = 1, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f, Children = [name, meta] },
                new BoxEl
                {
                    Width = Z.ChCheck, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Opacity = Prop.Of(() => Played() ? 1f : 0f),
                    Children = [Icon(Icons.Check, Z.ChCheckGlyph, Tok.TextTertiary)],
                },
            ],
        };
    }

    // ══ THE TRANSCRIPT ══════════════════════════════════════════════════════════════════════════════════════════════════════

    static Element Transcript(PodcastEpisodePageProps p) => Embed.Comp(p, static () => new TranscriptHost()) with { Key = "ze-transcript" };

    sealed class TranscriptHost : Component
    {
        readonly ScrollHandle _scroll = new();
        MeasuredStackVirtualLayout? _layout;
        ImmutableArray<PodcastLineVm> _lines;
        string[] _headingAt = [];

        public override Element Render()
        {
            var p = UseProps<PodcastEpisodePageProps>();
            var v = p.View.Value;
            string find = p.Find().Value.Trim();
            if (_layout is null || !_lines.Equals(v.Transcript))
            {
                _lines = v.Transcript;
                var now = _lines;
                _layout = new MeasuredStackVirtualLayout(Z.TrLineEstimate, extentOf: i => i < now.Length && now[i].Heading is not null ? Z.TrHeadingEstimate : Z.TrLineEstimate);
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
                double target = layout.OffsetOf(line, 0f) - vh / 2 + Z.TrLineEstimate / 2;
                _scroll.ScrollTo(Math.Max(0, target), ScrollMove.Follow);
            });
            UseEffect(() =>
            {
                var m = _scroll.Motion.Value;
                if (m.UserDriven && p.Follow.Peek()) p.SetFollow(false);
            });

            var head = new BoxEl
            {
                Direction = 0, Gap = Z.TrHeadGap, AlignItems = FlexAlign.Center, MinWidth = 0f, Wrap = true,
                Children =
                [
                    K.Heading(Loc.Get(Strings.Podcast.Pages.Transcript)) with { Shrink = 0f },
                    R.Type.TrackMeta(v.TranscriptSub) with { Case = R.Case.For(LabelKind.Label), Color = Tok.TextSecondary, Grow = 1f, Shrink = 1f, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                    new BoxEl
                    {
                        Width = Z.FindW, Shrink = 0f, MinWidth = 0f,
                        Children = [AutoSuggestBox.Create(s_noSuggest, Loc.Get(Strings.Theme.Zune.Detail.FindInTranscript), width: Z.FindW, text: p.Find(), queryIcon: Icons.Search)],
                    },
                    Embed.Comp(p, static () => new FollowHost()) with { Key = "ze-follow" },
                ],
            };
            Element body;
            if (v.TranscriptLoad == PodcastLoad.Ready && lines.Length == 0)
                body = R.Type.TrackTitle(v.NoTranscript) with { Color = Tok.TextSecondary, Case = R.Case.For(LabelKind.Label), Wrap = TextWrap.Wrap, MinWidth = 0f };
            else
            {
                var cur = p.CurrentLine;
                var list = Virtual.Measured(lines.Length, layout, i => Line(p, lines[i], i, cur, find), i => "ze-ln:" + i)
                    with { Handle = _scroll, Grow = 1f, TemplateStamp = find.GetHashCode() };
                var heading = new TextEl("")
                {
                    Text = Prop.Of(() =>
                    {
                        if (_headingAt.Length == 0) return "";
                        int at = layout.IndexAt((float)_scroll.Offset.Value, 0f);
                        return _headingAt[Math.Clamp(at, 0, _headingAt.Length - 1)];
                    }),
                    FontFamily = R.Type.Pivot("").FontFamily, Size = Z.TrHeadingSize, LineHeight = Z.TrHeadingLine, Weight = Z.Light, Color = Tok.TextSecondary,
                    Case = TextCase.AsWritten, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                };
                body = new BoxEl
                {
                    Direction = 1, Height = Z.TrH, MinWidth = 0f,
                    Children =
                    [
                        new BoxEl { Direction = 0, Shrink = 0f, MinWidth = 0f, Padding = new Edges4(0f, Z.TrHeadingTop, 0f, Z.TrHeadingTop), Children = [heading] },
                        new BoxEl { Direction = 1, Grow = 1f, MinHeight = 0f, Children = [list] },
                    ],
                };
            }
            return new BoxEl { Key = "ze-tr", Direction = 1, Gap = Z.TrHeadGap, MinWidth = 0f, Children = [head, body] };
        }

        static readonly string[] s_noSuggest = [];

        static Element Line(PodcastEpisodePageProps p, PodcastLineVm l, int index, IReadSignal<int> cur, string find)
        {
            int start = l.StartMs;
            Func<bool> isCur = () => cur.Value == index;
            Element text = find.Length > 0 && l.Text.Contains(find, StringComparison.OrdinalIgnoreCase)
                ? new SpanTextEl(Marked(l.Text, find)) { Wrap = TextWrap.Wrap, Size = Z.TrLineSize, LineHeight = Z.TrLineLine, MinWidth = 0f }
                : new TextEl(l.Text)
                {
                    Wrap = TextWrap.Wrap, Size = Z.TrLineSize, LineHeight = Z.TrLineLine, MinWidth = 0f,
                    Color = Prop.Of<ColorF>(() => isCur() ? Tok.TextPrimary : cur.Value > index ? Tok.TextSecondary : Tok.TextTertiary),
                };
            var row = new BoxEl
            {
                Direction = 0, Gap = Z.TrColGap, MinWidth = 0f, AlignItems = FlexAlign.Start, Cursor = CursorId.Hand, Role = AutomationRole.Button, Focusable = true,
                Fill = ColorF.Transparent, HoverFill = Prop.Of(static () => Tok.TextPrimary with { A = Z.RowHoverAlpha }), OnClick = () => p.SeekTo(start),
                Children =
                [
                    new BoxEl
                    {
                        Width = Z.TrTimeW, Shrink = 0f, Direction = 0, Justify = FlexJustify.End,
                        Children = [new TextEl(l.Time)
                        {
                            Size = Z.CountSize, LineHeight = Z.TrLineLine, Color = Prop.Of(() => isCur() ? R.Ink.AccentDecor : Tok.TextTertiary),
                        }],
                    },
                    new BoxEl { Direction = 0, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f, MaxWidth = Z.TrTextMax, Children = [text] },
                ],
            };
            var kids = new List<Element>(2);
            if (l.Heading is { } h && index > 0)
                kids.Add(new BoxEl
                {
                    Padding = new Edges4(Z.TrTimeW + Z.TrColGap, Z.TrHeadingTop, 0f, 0f),
                    Children = [new TextEl(h)
                    {
                        FontFamily = R.Type.Pivot("").FontFamily, Size = Z.TrHeadingSize, LineHeight = Z.TrHeadingLine, Weight = Z.Light, Color = Tok.TextSecondary,
                        Case = TextCase.AsWritten, Wrap = TextWrap.Wrap, MinWidth = 0f,
                    }],
                });
            kids.Add(row);
            return new BoxEl { Direction = 1, MinWidth = 0f, Padding = new Edges4(0f, Z.TrLineGap, 0f, 0f), Children = kids.ToArray() };
        }

        static TextSpans Marked(string text, string find)
        {
            var spans = new List<TextSpan>(4);
            int at = 0;
            var mark = Tok.AccentDefault with { A = Z.FindMarkAlpha };
            while (at < text.Length)
            {
                int hit = text.IndexOf(find, at, StringComparison.OrdinalIgnoreCase);
                if (hit < 0) { spans.Add(new TextSpan(text[at..], Color: Tok.TextPrimary)); break; }
                if (hit > at) spans.Add(new TextSpan(text[at..hit], Color: Tok.TextPrimary));
                spans.Add(new TextSpan(text.Substring(hit, find.Length), Color: Tok.TextPrimary, Background: mark));
                at = hit + find.Length;
            }
            return new TextSpans(spans.ToArray(), spans.Count);
        }
    }

    /// <summary>The follow command: "following playback" (the accent glyph) or "back to current line"; both words share one slot, so the toggle never
    /// moves the head.</summary>
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
                Children = [new TextEl(w) { Size = Z.ButtonTextSize, LineHeight = Z.ButtonTextLine, Color = Tok.TextPrimary, Case = R.Case.For(LabelKind.Command), MaxLines = 1 }],
            };
            var b = K.Button("ze-follow:button", null, Icons.MapPin, () => p.SetFollow(!p.Follow.Peek()), Z.RowAction, on: following) with
            {
                Width = float.NaN, BorderWidth = following ? 0f : 1f, Padding = new Edges4(Z.ButtonPadX, 0f, Z.ButtonPadX, 0f),
            };
            b = b with { Children = [.. b.Children, new BoxEl { ZStack = true, Children = [Word(on, following), Word(off, !following)] }] };
            return Controls.Named(b, following ? on : off);
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
            var kids = new List<Element>(2);
            if (!v.More.IsDefaultOrEmpty)
            {
                var rows = new Element[v.More.Length];
                for (int i = 0; i < rows.Length; i++) rows[i] = K.EpisodeRow(v.More[i], lay.RowDescription, lay.Stacked, a.Verbs);
                kids.Add(K.Section("ze-next", Loc.Get(Strings.Podcast.Pages.NextInShow), new BoxEl { Direction = 1, MinWidth = 0f, Children = rows },
                    K.Link("ze-all", Loc.Get(Strings.Podcast.Pages.AllEpisodes), p.OpenShow, lay.Target)));
            }
            if (!v.Similar.IsDefaultOrEmpty)
                kids.Add(K.Section("ze-similar", Loc.Get(Strings.Podcast.Pages.MoreLikeThis), K.Cards("ze-cards", v.Similar, p.OpenUri, lay.Stacked, p.CardMenu, p.PlayCard)));
            return new BoxEl { Direction = 1, Gap = Z.SectionGap, MinWidth = 0f, Margin = new Edges4(0f, kids.Count > 0 ? Z.SectionGap : 0f, 0f, 0f), Children = kids.ToArray() };
        }
    }
}
