// ── Themes/WinUi/Structure/Views/Podcast/WinUiPodcastEpisode.cs — WinUI's episode page (podcast pages v2, concept "Episode", theme=winui) ──────────────
//
// One scroller, top to bottom (concept note 6, "Three widths"):
//   the cover-tone wash (behind the hero, fading out before the cards) · the hero (a 16:9 still with its Video tag and length; the crumb with the
//   show's tiny art and link; Title 28/36; the meta; the action row: the smart SplitButton, the in-progress bar with the time left, then Saved,
//   Mark as played, Share, More) · the chapter map · "Up next in this show" when played · the cards: at Standard a 400 card (SelectorBar: Chapters |
//   About | Comments) beside the Transcript, both 456 tall; below 1008 one column, the Transcript the fourth tab · "Next in this show" · "More like this".
// The 48 bar fades in over the page's top once the hero has scrolled under it (an overlay: it moves nothing). Loading is the loaded layout over a seed
// snapshot, derived flat (`SkelRegionEl`).
// Numbers: `WinUiPodcastEpisodeRules` and `Tiers.PodcastEpisode.cs`. The episode-only parts: `WinUiPodcastEpisodeParts.cs`.

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
using P = Wavee.Themes.WinUi.WinUiPodcastEpisodeRules;

namespace Wavee.Themes.WinUi;

/// <summary>WinUI's episode page.</summary>
internal static class WinUiPodcastEpisode
{
    public static Element Template(PodcastEpisodePageProps p, in ViewContext ctx)
        => new BoxEl { Direction = 1, Grow = 1f, MinHeight = 0f, Children = [Embed.Comp(p, static () => new Page()) with { Key = P.PageKey }] };

    static readonly IReadSignal<PodcastEpisodeView> s_seed = new Signal<PodcastEpisodeView>(WinUiPodcastEpisodeSeed.View);

    sealed class Page : Component
    {
        readonly ScrollHandle _scroll = new();
        readonly Signal<bool> _barOn = new(false);
        PodcastRowVerbs? _verbs;
        Func<uint>? _toneOf;

        public override Element Render()
        {
            var p = UseProps<PodcastEpisodePageProps>();
            var facts = TierContext.DrawnIn(UseContext(ThemeFactsContext.Signal)).Value.Tiers;
            var lay = WinUiPodcastEpisodeTiers.Of(in facts);
            float inset = WinUiTiers.InsetOf(in facts);
            _toneOf ??= () => p.View.Value.Tone;
            uint tone = UseComputed(_toneOf).Value;
            _verbs ??= new PodcastRowVerbs
            {
                Selected = s_none, Expanded = s_none, Playing = p.OnDeck, Play = p.PlayEpisode, Open = p.Open, ToggleSaved = p.ToggleSavedEpisode,
                PlayNext = p.PlayNext, Select = static _ => { }, Expand = static _ => { }, Menu = p.RowMenu,
            };
            var seed = p with { View = s_seed };
            var verbs = _verbs;
            var barOn = _barOn;
            var region = new SkelRegionEl(
                Pending: () => p.View.Value.Load == PodcastLoad.Pending, Failed: () => p.View.Value.Load == PodcastLoad.Failed,
                Content: () => Column(p, lay, inset, barOn, verbs), ShimmerSource: () => Column(seed, lay, inset, barOn, verbs),
                OnFailed: () => Controls.Vacancy(Controls.VacancyVoice.Error), Reveal: SkelReveal.Soft,
                Style: SkeletonStyle.Default with { DeriveComponents = true }, Group: null, SmoothResize: false);
            Element content = new BoxEl { Key = "pe-body", Direction = 1, MinWidth = 0f, Children = [region] };
            Element bar = Embed.Comp(new BarArgs(p, inset), static () => new Bar()) with { Key = P.BarKey };
            return WinUiPodcastKit.Page(p.RouteKey, tone, _barOn, content, bar, _scroll);
        }
    }

    static readonly IReadSignal<string?> s_none = new Signal<string?>(null);

    // ══ THE COLUMN ══════════════════════════════════════════════════════════════════════════════════════════════════════════

    static Element Column(PodcastEpisodePageProps p, WinUiPodcastEpisodeLayout lay, float inset, Signal<bool> barOn, PodcastRowVerbs verbs)
    {
        var lr = new HeroArgs(p, lay, inset, verbs);
        return new BoxEl
        {
            Direction = 1, MinWidth = 0f, Padding = new Edges4(inset, 0f, inset, P.SectionTop),
            Children =
            [
                Embed.Comp(lr, static () => new Hero()) with { Key = P.HeroKey },
                WinUiPodcastKit.BarSentinel(barOn),
                WinUiPodcastEpisodeParts.Map(p, lay.Target),
                Embed.Comp(lr, static () => new NextUp()) with { Key = P.NextUpKey },
                Embed.Comp(lr, static () => new Cards()) with { Key = P.ColsKey },
                Embed.Comp(lr, static () => new More()) with { Key = P.MoreKey },
            ],
        };
    }

    sealed record HeroArgs(PodcastEpisodePageProps P, WinUiPodcastEpisodeLayout Lay, float Inset, PodcastRowVerbs Verbs);

    // ══ THE HERO ════════════════════════════════════════════════════════════════════════════════════════════════════════════

    sealed class Hero : Component
    {
        public override Element Render()
        {
            var a = UseProps<HeroArgs>();
            var p = a.P;
            var lay = a.Lay;
            var v = p.View.Value;
            var e = v.Episode;
            float stillH = lay.StillW / P.StillAspect;

            var stillKids = new List<Element>(3) { Controls.Artwork(e.ArtUrl ?? v.ShowArtUrl, lay.StillW, stillH, lay.StillRadius) };
            if (v.VideoLabel.Length > 0) stillKids.Add(Tag(v.VideoLabel, Icons.Video, FlexAlign.Start, FlexAlign.Start));
            if (v.StillTime.Length > 0) stillKids.Add(Tag(v.StillTime, null, FlexAlign.End, FlexAlign.End));
            Element still = new BoxEl
            {
                Width = lay.StillW, Height = stillH, Shrink = 0f, ZStack = true, Corners = CornerRadius4.All(lay.StillRadius), Children = stillKids.ToArray(),
            };

            // The crumb: the show's tiny art, its name (the link), a chevron, "Episode 9".
            var crumb = new BoxEl
            {
                Direction = 0, Gap = P.CrumbGap, AlignItems = FlexAlign.Center, MinWidth = 0f,
                Children =
                [
                    Controls.Artwork(v.ShowArtUrl, P.CrumbArt, P.CrumbArt, P.CrumbArtRadius),
                    new BoxEl
                    {
                        Shrink = 1f, MinWidth = 0f, Cursor = CursorId.Hand, Role = AutomationRole.Hyperlink, Focusable = true, OnClick = p.OpenShow,
                        Children = [BodyStrong(v.ShowTitle) with { Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f }],
                    },
                    Icon(Icons.ChevronRight, P.CrumbChevron, Tok.TextTertiary),
                    Body(v.Crumb) with { Color = Tok.TextSecondary, Shrink = 0f, Wrap = TextWrap.NoWrap },
                ],
            };
            TextEl title = (lay.Stacked ? Subtitle(e.Title) : Title(e.Title)) with
            {
                Color = Tok.TextPrimary, Wrap = TextWrap.Wrap, MinWidth = 0f,
            };
            var meta = Body(string.Join(P.Dot, v.Meta)) with { Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, MinWidth = 0f };

            var words = new BoxEl
            {
                Direction = 1, Gap = P.MainGap, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f, AlignSelf = FlexAlign.Stretch,
                Children = [crumb, title, meta, Actions(p, v, lay)],
            };
            return new BoxEl
            {
                // The hero's column is the page's lead edge (the still sits on the gutter, the title beside it): the lint guide's page.title edge.
                Key = WinUiFrameLayout.PageTitleKey,
                Direction = lay.Stacked ? (byte)1 : (byte)0, Gap = lay.HeroGap, MinWidth = 0f, AlignItems = FlexAlign.Start,
                Padding = new Edges4(0f, lay.HeroTop, 0f, lay.HeroBottom),
                Children = [still, words],
            };
        }

        static Element Tag(string text, string? glyph, FlexAlign v, FlexAlign h)
        {
            var kids = new List<Element>(2);
            if (glyph is not null) kids.Add(Icon(glyph, P.CrumbChevron, Tok.OnMediaPrimary));
            kids.Add(Caption(text) with { Color = Tok.OnMediaPrimary });
            return new BoxEl
            {
                Direction = 0, Gap = P.BarGap, AlignItems = FlexAlign.Center, Height = P.StillTagH, Padding = new Edges4(P.StillTagPadX, 0f, P.StillTagPadX, 0f),
                Margin = Edges4.All(P.StillTagInset), AlignSelf = v, JustifySelf = h, Corners = Radii.ControlAll, Fill = Tok.MediaScrim, HitTestVisible = false,
                Children = kids.ToArray(),
            };
        }

        static Element Actions(PodcastEpisodePageProps p, PodcastEpisodeView v, WinUiPodcastEpisodeLayout lay)
        {
            var e = v.Episode;
            float h = lay.Target;
            var kids = new List<Element>(8)
            {
                WinUiPodcastKit.SmartSplit("pe-split", v.PrimaryLabel, v.PrimaryAccent, v.PrimaryLabel == Loc.Get(Strings.Podcast.Pages.Pause), p.Play, v.Menu, h,
                    lay.SplitFills),
            };
            if (e.State == PodcastRowState.InProgress)
            {
                var pos = p.PositionMs;
                int duration = Math.Max(1, v.DurationMs);
                kids.Add(new BoxEl
                {
                    Key = "pe-mini", Direction = 1, Gap = P.BarGap, Width = lay.SplitFills ? P.MiniBarCompactW : P.MiniBarW, Shrink = 1f, MinWidth = 0f,
                    Children =
                    [
                        new TextEl("") { Text = Prop.Bind(p.Left), Size = Caption("").Size, LineHeight = Caption("").LineHeight, Color = Tok.TextPrimary, MaxLines = 1 },
                        WinUiPodcastEpisodeParts.LiveBar(() => pos.Value / (float)duration),
                    ],
                });
            }
            if (!lay.SplitFills) kids.Add(new BoxEl { Grow = 1f });
            string saveLabel = Loc.Get(e.Saved ? Strings.Podcast.Pages.Saved : Strings.Podcast.Pages.Save);
            kids.Add(lay.SavedWord
                ? ToggleButton.Controlled(Loc.Get(Strings.Podcast.Pages.Save), e.Saved, _ => p.ToggleSaved(), glyph: Icons.Heart, checkedGlyph: Icons.HeartFill,
                    checkedLabel: Loc.Get(Strings.Podcast.Pages.Saved), style: WinUiDetailKit.NeutralToggle(ControlSize.Medium))
                : Controls.Named(Bordered(e.Saved ? Icons.HeartFill : Icons.Heart, p.ToggleSaved, h), saveLabel));
            string markLabel = Loc.Get(e.State == PodcastRowState.Played ? Strings.Podcast.Pages.MarkUnplayed : Strings.Podcast.Pages.MarkPlayed);
            kids.Add(lay.MarkWord
                ? Button.Create(markLabel, p.ToggleCompleted, ButtonAppearance.Standard, glyph: Icons.Check) with { Height = h, Shrink = 0f, MinWidth = 0f }
                : Controls.Named(Bordered(Icons.Check, p.ToggleCompleted, h), markLabel));
            if (p.Share is { } share) kids.Add(Controls.Named(Bordered(Icons.Share, share, h), Loc.Get(Strings.Podcast.Pages.Share)));
            kids.Add(WinUiDetailKit.MoreButton(p.Menu, h, WinUiDetailKit.MoreLook.Bordered));
            var row = new BoxEl
            {
                Key = "pe-actions", Direction = 0, Wrap = true, Gap = P.ActionGap, AlignItems = FlexAlign.Center, MinWidth = 0f,
                Margin = new Edges4(0f, P.ActionTop - P.MainGap, 0f, 0f), Children = kids.ToArray(),
            };
            // Compact (concept w-s): the split takes the whole first line, the bar and the glyph buttons the line under it.
            if (!lay.SplitFills) return row;
            var split = kids[0];
            kids.RemoveAt(0);
            return new BoxEl
            {
                Key = "pe-actions", Direction = 1, Gap = P.ActionGap, MinWidth = 0f, Margin = new Edges4(0f, P.ActionTop - P.MainGap, 0f, 0f),
                Children = [new BoxEl { Direction = 0, MinWidth = 0f, Children = [split] }, row with { Key = "pe-actions:rest", Margin = default, Children = kids.ToArray() }],
            };
        }
    }

    /// <summary>A bordered standard glyph button (the control fill, the elevation stroke, r4).</summary>
    static BoxEl Bordered(string glyph, Action onClick, float size) => Controls.IconAction(glyph, onClick, size: size) with
    {
        Fill = Tok.FillControlDefault, HoverFill = Tok.FillControlSecondary, PressedFill = Tok.FillControlTertiary,
        BorderWidth = 1f, BorderBrush = Tok.ControlElevationBorder,
    };

    static string Join(string a, string b) => a.Length == 0 ? b : b.Length == 0 ? a : a + P.Dot + b;

    // ══ "UP NEXT IN THIS SHOW" (a played episode) ═══════════════════════════════════════════════════════════════════════════

    sealed class NextUp : Component
    {
        public override Element Render()
        {
            var a = UseProps<HeroArgs>();
            var p = a.P;
            var v = p.View.Value;
            if (v.Next is not { } n) return new BoxEl();
            string id = n.Id;
            var words = new BoxEl
            {
                Direction = 1, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f,
                Children =
                [
                    Caption(Loc.Get(Strings.Podcast.Pages.UpNextInShow)) with { Color = Tok.TextSecondary },
                    BodyStrong(n.Title) with { Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f },
                    Caption(Join(n.Date, n.State == PodcastRowState.InProgress ? n.Left : n.Length)) with { Color = Tok.TextSecondary },
                ],
            };
            var play = Button.Create(Loc.Get(Strings.Podcast.Pages.PlayNext), () => p.PlayEpisode(id), ButtonAppearance.Accent, glyph: WinUiDetailRules.PlaySolid)
                with { Shrink = 0f, MinWidth = 0f, Height = a.Lay.Target, Grow = a.Lay.Stacked ? 1f : 0f };
            return new BoxEl
            {
                Direction = 0, Wrap = a.Lay.Stacked, Gap = P.NextGap, AlignItems = FlexAlign.Center, MinWidth = 0f, Margin = new Edges4(0f, P.CardGap, 0f, 0f),
                Padding = new Edges4(P.MapPadX, P.NextPadY, P.MapPadX, P.NextPadY), Corners = Radii.CardAll, Fill = Tok.FillCardDefault, BorderWidth = 1f,
                BorderColor = Tok.StrokeCardDefault,
                Children =
                [
                    new BoxEl
                    {
                        Shrink = 0f, Cursor = CursorId.Hand, OnClick = () => p.Open(id),
                        Children = [Controls.Artwork(n.ArtUrl ?? v.ShowArtUrl, P.NextArt, P.NextArt, P.NextArtRadius)],
                    },
                    words, play,
                ],
            };
        }
    }

    // ══ THE CARDS: CHAPTERS | ABOUT | (TRANSCRIPT) | COMMENTS, AND THE TRANSCRIPT ═══════════════════════════════════════════

    sealed class Cards : Component
    {
        readonly Signal<int> _index = new(0);

        public override Element Render()
        {
            var a = UseProps<HeroArgs>();
            var p = a.P;
            var lay = a.Lay;
            var v = p.View.Value;
            var tab = p.Tab.Value;
            // The transcript is a tab only where the window is too narrow to keep it beside the chapters.
            if (lay.TwoCards && tab == PodcastEpisodeTab.Transcript) tab = PodcastEpisodeTab.Chapters;
            var tabs = lay.TwoCards ? s_wideTabs : s_narrowTabs;
            string chapters = Loc.Get(Strings.Podcast.Pages.Chapters), about = Loc.Get(Strings.Podcast.Pages.About),
                transcript = Loc.Get(Strings.Podcast.Pages.Transcript), comments = Loc.Get(Strings.Podcast.Pages.Comments);
            var labels = new List<string>(4);
            foreach (var t in tabs)
                labels.Add(t switch
                {
                    PodcastEpisodeTab.Chapters => WinUiChoice.Counted(chapters, Count(v.Chapters.Length)),
                    PodcastEpisodeTab.About => about,
                    PodcastEpisodeTab.Transcript => transcript,
                    _ => WinUiChoice.Counted(comments, Count(v.CommentCount)),
                });
            int index = Math.Max(0, Array.IndexOf(tabs, tab));
            UseEffect(() => { _index.SetIfChanged(index); }, DepKey.From(index));
            bool transcriptTab = !lay.TwoCards && tab == PodcastEpisodeTab.Transcript;

            Element body = tab switch
            {
                PodcastEpisodeTab.About => Ui.Body(v.About) with { Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, MinWidth = 0f, MaxWidth = P.TrTextMax },
                PodcastEpisodeTab.Comments => p.Comments?.Invoke() ?? new BoxEl(),
                _ => WinUiPodcastEpisodeParts.Chapters(p, v),
            };
            var pivot = new BoxEl
            {
                Padding = new Edges4(P.SelBarPad, P.SelBarPad, P.SelBarPad, 0f), Shrink = 0f, MinWidth = 0f,
                Children = [WinUiChoice.Pivot(labels, _index, Controls.ButtonHeight, i => p.SetTab(tabs[Math.Clamp(i, 0, tabs.Length - 1)]),
                    keys: lay.TwoCards ? s_wideKeys : s_narrowKeys)],
            };
            var divider = new BoxEl { Height = 1f, Shrink = 0f, Fill = Tok.StrokeDividerDefault };
            var left = new BoxEl
            {
                Key = P.SectionsKey, Direction = 1, MinWidth = 0f, ClipToBounds = true, Fill = Tok.FillCardDefault, BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
                Corners = transcriptTab ? new CornerRadius4(Radii.Card, Radii.Card, 0f, 0f) : Radii.CardAll,
                Width = lay.TwoCards ? P.ColsAside : float.NaN, Shrink = 0f, Height = transcriptTab ? float.NaN : lay.CardsH,
                Children = transcriptTab
                    ? [pivot, divider]
                    : [pivot, divider,
                       ScrollView(new BoxEl { Direction = 1, Padding = Edges4.All(P.ListBodyPad), MinWidth = 0f, Children = [body] }) with { Grow = 1f, MinHeight = 0f }],
            };
            var kids = new List<Element>(2) { left };
            if (lay.TwoCards)
                kids.Add(new BoxEl { Grow = 1f, Basis = 0f, MinWidth = 0f, Direction = 1, Children = [WinUiPodcastEpisodeParts.Transcript(p, lay.TranscriptChrome, lay.CardsH, false)] });
            else if (transcriptTab) kids.Add(WinUiPodcastEpisodeParts.Transcript(p, lay.TranscriptChrome, lay.CardsH, true));
            return new BoxEl
            {
                Direction = lay.TwoCards ? (byte)0 : (byte)1, Gap = lay.TwoCards ? P.CardGap : 0f, MinWidth = 0f, AlignItems = FlexAlign.Stretch,
                Margin = new Edges4(0f, P.CardGap, 0f, 0f), Children = kids.ToArray(),
            };
        }

        static string Count(int n) => n > 0 ? n.ToString(System.Globalization.CultureInfo.InvariantCulture) : "";

        static readonly string?[] s_wideKeys = ["pe-tab:chapters", "pe-tab:about", "pe-tab:comments"];
        static readonly string?[] s_narrowKeys = ["pe-tab:chapters", "pe-tab:about", "pe-tab:transcript", "pe-tab:comments"];
        static readonly PodcastEpisodeTab[] s_wideTabs = [PodcastEpisodeTab.Chapters, PodcastEpisodeTab.About, PodcastEpisodeTab.Comments];
        static readonly PodcastEpisodeTab[] s_narrowTabs = [PodcastEpisodeTab.Chapters, PodcastEpisodeTab.About, PodcastEpisodeTab.Transcript, PodcastEpisodeTab.Comments];
    }

    // ══ "NEXT IN THIS SHOW" AND "MORE LIKE THIS" ════════════════════════════════════════════════════════════════════════════

    sealed class More : Component
    {
        public override Element Render()
        {
            var a = UseProps<HeroArgs>();
            var p = a.P;
            var lay = a.Lay;
            var v = p.View.Value;
            bool compact = lay.Tier == WidthTier.Compact;
            var kids = new List<Element>(4);
            if (!v.More.IsDefaultOrEmpty)
            {
                kids.Add(Head(WinUiPodcastKit.SectionHeader(Loc.Get(Strings.Podcast.Pages.NextInShow), v.MoreSub, Loc.Get(Strings.Podcast.Pages.AllEpisodes), p.OpenShow)));
                var rows = new Element[v.More.Length];
                for (int i = 0; i < rows.Length; i++) rows[i] = WinUiPodcastKit.EpisodeRow(v.More[i], PodcastRowMode.Open, false, compact, a.Verbs);
                kids.Add(new BoxEl { Direction = 1, MinWidth = 0f, Children = rows });
            }
            if (!v.Similar.IsDefaultOrEmpty)
            {
                kids.Add(Head(WinUiPodcastKit.SectionHeader(Loc.Get(Strings.Podcast.Pages.MoreLikeThis))));
                kids.Add(WinUiPodcastKit.MoreLikeThis(v.Similar, p.OpenUri, compact));
            }
            return new BoxEl { Direction = 1, MinWidth = 0f, Children = kids.ToArray() };
        }

        static Element Head(Element header) => new BoxEl { Direction = 1, MinWidth = 0f, Margin = new Edges4(0f, P.SectionTop, 0f, P.SectionBottom), Children = [header] };
    }

    // ══ THE 48 BAR ══════════════════════════════════════════════════════════════════════════════════════════════════════════

    sealed record BarArgs(PodcastEpisodePageProps P, float Inset);

    /// <summary>The kit's 48 bar over the page: the still, the title over the time left (or the length), the primary.</summary>
    sealed class Bar : Component
    {
        public override Element Render()
        {
            var a = UseProps<BarArgs>();
            var p = a.P;
            var v = p.View.Value;
            string left = p.Left.Value;
            var primary = Button.Create(v.PrimaryLabel, p.Play, v.PrimaryAccent ? ButtonAppearance.Accent : ButtonAppearance.Standard,
                glyph: v.PrimaryLabel == Loc.Get(Strings.Podcast.Pages.Pause) ? WinUiDetailRules.PauseSolid : WinUiDetailRules.PlaySolid)
                with { Key = "pe-bar:play", Shrink = 0f, MinWidth = 0f };
            return WinUiPodcastKit.Bar(v.Episode.ArtUrl ?? v.ShowArtUrl, v.Episode.Title, left.Length > 0 ? left : v.Episode.Length, primary, a.Inset);
        }
    }
}
