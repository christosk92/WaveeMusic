// ── Themes/WinUi/Structure/Views/Podcast/WinUiPodcastShow.cs — WinUI's show page (podcast pages v2, concept winui-podcast-v2 "Show") ──────────────────
//
//   WinUiPodcastKit.Page (scroller · wash · the 48 bar over it)
//   └─ column on the gutter
//      ├─ hero        160 cover · eyebrow (Podcast · topics · Video / Serial tags) · Title Large · meta (publisher · ★ rating (count) · cadence · N
//      │              episodes) · top right: Following · bell · Share · More · the up row: the smart SplitButton + what it plays (bar, time left)
//      ├─ BarSentinel (the bar shows once the hero has gone under it)
//      ├─ About the show (641-1007 and ≤640: an Expander above the list; ≥1008 it is the pane's second tab)
//      └─ cols: the list column [toolbar (sticky under the bar): SelectorBar filters with counts (≤640: a ComboBox) · search · sort;
//                                groups: a sticky header (opens the Jump to grid; "New since your last visit" offers Mark all as played) + rows;
//                                Load more] · the 360 details pane (≥1008, sticky): Episode | About the show
//
// The tier record is `WinUiPodcastShowTiers` (window tier). Loading is this same layout over a placeholder snapshot with flat fills (SkelRegionEl).

using System.Collections.Immutable;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scroll.Effects;
using FluentGpu.Scroll.Runtime;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;
using K = Wavee.Themes.WinUi.WinUiPodcastKitRules;
using S = Wavee.Themes.WinUi.WinUiPodcastShowRules;

namespace Wavee.Themes.WinUi;

/// <summary>WinUI's show page.</summary>
internal static class WinUiPodcastShow
{
    public static Element Template(PodcastShowPageProps p, in ViewContext ctx) => Embed.Comp(p, static () => new PageHost());

    /// <summary>The loading snapshot: the loaded layout's shape with placeholder words (the shimmer is derived from it).</summary>
    static readonly PodcastShowView s_placeholder = Placeholder();

    static PodcastShowView Placeholder()
    {
        var rows = ImmutableArray.CreateBuilder<PodcastEpisodeVm>();
        for (int i = 0; i < 8; i++)
            rows.Add(new PodcastEpisodeVm("seed:" + i, "Episode title for a placeholder row", "", 0, false, "12 Nov", 0, "1 hr 10 min", "", 0f,
                PodcastRowState.Unplayed, false, false, false, true, 0, false, null));
        var head = PodcastShowView.Empty.Head with
        {
            Title = "Podcast title", Kind = "Podcast", Publisher = "Publisher", Cadence = "Weekly", Count = "100 episodes", About = "About this show.",
        };
        var smart = new PodcastSmart(PodcastSmartKind.PlayNewest, "seed:0", "Play newest", "New", "Episode title", "12 Nov · 1 hr", -1f, true, []);
        return PodcastShowView.Empty with
        {
            Head = head, Smart = smart, Episodes = rows.ToImmutable(),
            Groups = [new PodcastGroup("y", "2026", 8, false, [0, 1, 2, 3, 4, 5, 6, 7])],
            Filters = [new PodcastFilterVm(PodcastFilter.All, "All", 0), new PodcastFilterVm(PodcastFilter.Unplayed, "Unplayed", 0),
                new PodcastFilterVm(PodcastFilter.InProgress, "In progress", 0), new PodcastFilterVm(PodcastFilter.Saved, "Saved", 0)],
        };
    }

    sealed class PageHost : Component
    {
        PodcastShowPageProps _p = null!;
        readonly Signal<bool> _barOn = new(false);
        readonly Signal<int> _filterIx = new(0), _paneIx = new(0), _comboIx = new(0);
        readonly Signal<bool> _searchOpen = new(false);
        readonly ScrollHandle _scroll = new();
        PodcastRowVerbs? _verbs;
        NodeHandle _viewport;
        readonly Dictionary<string, NodeHandle> _groupNodes = [];
        IOverlayService? _overlay;
        OverlayHandle? _jump;
        Action<NodeHandle>? _captureViewport;
        Action? _syncCells;
        Func<bool>? _pending, _failed;

        public override Element Render()
        {
            _p = UseProps<PodcastShowPageProps>();
            var p = _p;
            var facts = TierContext.DrawnIn(UseContext(ThemeFactsContext.Signal)).Value.Tiers;
            var lay = WinUiPodcastShowTiers.Of(in facts);
            float inset = WinUiTiers.InsetOf(in facts);
            float viewportH = facts.ViewportHeight;
            _overlay = UseContext(Overlay.Service);
            _captureViewport ??= h => _viewport = h;
            _pending ??= () => _p.View.Value.Load == PodcastLoad.Pending;
            _failed ??= () => _p.View.Value.Load == PodcastLoad.Failed;
            _syncCells ??= () =>
            {
                _filterIx.SetIfChanged((int)_p.Filter.Value);
                _comboIx.SetIfChanged((int)_p.Filter.Value);
                _paneIx.SetIfChanged((int)_p.PaneTab.Value);
            };
            UseEffect(_syncCells);
            _verbs ??= new PodcastRowVerbs
            {
                Selected = p.Selected, Expanded = p.Expanded, Playing = p.Playing, Play = p.Play, Open = p.Open, ToggleSaved = p.ToggleSaved,
                PlayNext = p.PlayNext, Select = p.Select, Expand = p.Expand, Menu = p.RowMenu,
            };

            var v = p.View.Value;
            bool loading = v.Load == PodcastLoad.Pending;
            var shown = loading ? s_placeholder : v;
            Element content = new SkelRegionEl(
                Pending: _pending, Failed: _failed, Content: () => Compose(shown, lay, inset, viewportH, live: true),
                ShimmerSource: () => Compose(s_placeholder, lay, inset, viewportH, live: false),
                OnFailed: () => Controls.Vacancy(Controls.VacancyVoice.Error), Reveal: SkelReveal.Soft, Style: SkeletonStyle.Default, Group: null,
                SmoothResize: false);
            var head = shown.Head;
            Element bar = WinUiPodcastKit.Bar(head.ArtUrl, head.Title,
                shown.Smart is { Kind: PodcastSmartKind.CaughtUp } ? Loc.Get(Strings.Podcast.Pages.CaughtUp) : shown.Smart?.EpisodeTitle ?? "",
                BarButton(shown), inset, captionShown: !lay.Stacked);
            return WinUiPodcastKit.Page(p.RouteKey, loading ? 0u : head.Tone, _barOn,
                new BoxEl { Key = "podcast-show-body", Direction = 1, MinWidth = 0f, Children = [content] }, bar, _scroll, _captureViewport);
        }

        Element BarButton(PodcastShowView v)
        {
            var s = v.Smart;
            if (s is null) return new BoxEl();
            bool pausing = _p.Playing.Value is { } id && id == s.EpisodeId;
            string label = pausing ? Loc.Get(Strings.Podcast.Pages.Pause) : s.Label;
            return Button.Create(label, _p.PlaySmart, s.Accent ? ButtonAppearance.Accent : ButtonAppearance.Standard,
                glyph: pausing ? WinUiDetailRules.PauseSolid : WinUiDetailRules.PlaySolid) with { Key = "bar:play", Shrink = 0f };
        }

        // ══ THE PAGE ════════════════════════════════════════════════════════════════════════════════════════════════════════

        Element Compose(PodcastShowView v, WinUiPodcastShowLayout lay, float inset, float viewportH, bool live)
        {
            var kids = new List<Element>(4) { Hero(v, lay, live) };
            if (live) kids.Add(WinUiPodcastKit.BarSentinel(_barOn));
            if (!lay.Pane) kids.Add(AboutExpander(v));
            Element list = ListColumn(v, lay, live);
            if (lay.Pane)
                kids.Add(new BoxEl
                {
                    Key = "show-cols", Direction = 0, Gap = S.ColGap, AlignItems = FlexAlign.Start, MinWidth = 0f,
                    Children = [list, Pane(v, viewportH, live)],
                });
            else kids.Add(list);
            return new BoxEl
            {
                Key = "show-column", Direction = 1, MinWidth = 0f,
                Padding = new Edges4(inset, 0f, inset, K.PageFoot + R.Space.DockReserve),
                Children = kids.ToArray(),
            };
        }

        // ── the hero ──

        Element Hero(PodcastShowView v, WinUiPodcastShowLayout lay, bool live)
        {
            var h = v.Head;
            Element cover = Controls.Artwork(h.ArtUrl, lay.Cover, lay.Cover, lay.CoverRadius, decodePx: (int)(lay.Cover * 2f));
            var idKids = new List<Element>(3)
            {
                Eyebrow(h),
                (lay.TitleLarge ? TitleLarge(h.Title) : Title(h.Title)) with
                {
                    Key = WinUiFrameLayout.PageTitleKey, Wrap = TextWrap.Wrap, MaxLines = 2, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                },
                Meta(h),
            };
            Element id = new BoxEl { Direction = 1, Gap = S.IdGap, Grow = 1f, Basis = 0f, MinWidth = 0f, Children = idKids.ToArray() };
            Element commands = Commands(v, lay, live);
            Element up = Up(v, lay, live);
            if (lay.Stacked)
                return new BoxEl
                {
                    Key = "show-hero", Direction = 1, Gap = lay.CoverGap, MinWidth = 0f, Padding = new Edges4(0f, lay.HeroTop, 0f, lay.HeroBottom),
                    Children =
                    [
                        new BoxEl { Direction = 0, Gap = lay.CoverGap, AlignItems = FlexAlign.Start, MinWidth = 0f, Children = [cover, id] },
                        up,
                        commands,
                    ],
                };
            return new BoxEl
            {
                Key = "show-hero", Direction = 0, Gap = lay.CoverGap, AlignItems = FlexAlign.Stretch, MinWidth = 0f,
                Padding = new Edges4(0f, lay.HeroTop, 0f, lay.HeroBottom),
                Children =
                [
                    new BoxEl { Shrink = 0f, AlignSelf = FlexAlign.Start, Children = [cover] },
                    new BoxEl
                    {
                        Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Gap = S.UpGap, Justify = FlexJustify.SpaceBetween,
                        Children =
                        [
                            new BoxEl { Direction = 0, Gap = S.UpGap, AlignItems = FlexAlign.Start, MinWidth = 0f, Children = [id, commands] },
                            up,
                        ],
                    },
                ],
            };
        }

        static Element Eyebrow(PodcastShowHead h)
        {
            var words = new List<string>(4) { h.Kind };
            words.AddRange(h.Topics);
            var kids = new List<Element>(3) { Caption(string.Join(" · ", words)) with { Color = Tok.TextSecondary, MaxLines = 1, Shrink = 1f, MinWidth = 0f } };
            if (h.Video) kids.Add(Tag(Loc.Get(Strings.Podcast.Pages.Video), Icons.Video));
            if (h.Serial) kids.Add(Tag(Loc.Get(Strings.Podcast.Pages.Serial), null));
            return new BoxEl { Direction = 0, Gap = S.EyebrowGap, AlignItems = FlexAlign.Center, MinWidth = 0f, Children = kids.ToArray() };
        }

        static Element Tag(string word, string? glyph)
        {
            var kids = new List<Element>(2);
            if (glyph is not null) kids.Add(Icon(glyph, S.TagGlyph, Tok.TextSecondary));
            kids.Add(Caption(word) with { Color = Tok.TextSecondary });
            return new BoxEl
            {
                Direction = 0, Gap = S.TagGap, Height = S.TagH, AlignItems = FlexAlign.Center, Padding = new Edges4(S.TagPadX, 0f, S.TagPadX, 0f), Shrink = 0f,
                Corners = Radii.ControlAll, BorderWidth = 1f, BorderBrush = Tok.ControlElevationBorder, Children = kids.ToArray(),
            };
        }

        static Element Meta(PodcastShowHead h)
        {
            var kids = new List<Element>(8);
            void Dot() { if (kids.Count > 0) kids.Add(Body("·") with { Color = Tok.TextTertiary }); }
            if (h.Publisher.Length > 0) kids.Add(Body(h.Publisher) with { MaxLines = 1, Shrink = 1f, MinWidth = 0f });
            if (h.Rating.Length > 0)
            {
                Dot();
                kids.Add(new BoxEl
                {
                    Direction = 0, Gap = S.TagGap, AlignItems = FlexAlign.Center, Shrink = 0f,
                    Children =
                    [
                        Icon(Icons.FavoriteStarFill, S.MetaStar, Tok.TextSecondary),
                        Body(h.Rating) with { Color = Tok.TextSecondary },
                        Body(h.RatingCount) with { Color = Tok.TextTertiary },
                    ],
                });
            }
            if (h.Cadence.Length > 0) { Dot(); kids.Add(Body(h.Cadence) with { Color = Tok.TextSecondary, Shrink = 0f }); }
            if (h.Count.Length > 0) { Dot(); kids.Add(Body(h.Count) with { Color = Tok.TextSecondary, Shrink = 0f }); }
            return new BoxEl { Direction = 0, Gap = S.EyebrowGap, AlignItems = FlexAlign.Center, Wrap = true, MinWidth = 0f, Children = kids.ToArray() };
        }

        Element Commands(PodcastShowView v, WinUiPodcastShowLayout lay, bool live)
        {
            var h = v.Head;
            var size = lay.Target > WinUiChoiceRules.ControlH ? ControlSize.Large : ControlSize.Medium;
            var kids = new List<Element>(4)
            {
                new BoxEl { Key = "cmd:follow", Shrink = 0f, Children = [WinUiDetailKit.Follow(live ? h.Uri : "", h.Title, size)] },
            };
            if (h.Following)
                kids.Add(Controls.Named(Controls.IconAction(Icons.Bell, null, size: lay.Target, isEnabled: false) with { Key = "cmd:bell" },
                    Loc.Get(Strings.Podcast.Pages.NotifyUnavailable)));
            if (_p.Share is { } share)
                kids.Add(lay.ShareWord
                    ? Button.Create(Loc.Get(Strings.Podcast.Pages.Share), share, glyph: Icons.Share) with { Key = "cmd:share", Height = lay.Target, Shrink = 0f }
                    : Controls.Named(Controls.IconAction(Icons.Share, share, size: lay.Target) with { Key = "cmd:share" }, Loc.Get(Strings.Podcast.Pages.Share)));
            kids.Add(WinUiDetailKit.MoreButton(_p.ShowMenu, lay.Target, WinUiDetailKit.MoreLook.Bordered));
            return new BoxEl { Key = "show-commands", Direction = 0, Gap = S.CommandGap, AlignItems = FlexAlign.Center, Shrink = 0f, Children = kids.ToArray() };
        }

        Element Up(PodcastShowView v, WinUiPodcastShowLayout lay, bool live)
        {
            var s = v.Smart;
            if (s is null) return new BoxEl { Key = "show-up", MinHeight = S.UpMinH };
            bool pausing = live && _p.Playing.Value is { } id && id == s.EpisodeId;
            Element button = s.Kind == PodcastSmartKind.CaughtUp
                ? Button.Create(pausing ? Loc.Get(Strings.Podcast.Pages.Pause) : s.Label, _p.PlaySmart, glyph: pausing ? WinUiDetailRules.PauseSolid : Icons.Refresh)
                    with { Key = "smart", Height = lay.Target, Shrink = 0f, Grow = lay.Stacked ? 1f : 0f }
                : WinUiPodcastKit.SmartSplit("smart", s.Label, s.Accent, pausing, _p.PlaySmart, s.Menu, lay.Target, fill: lay.Stacked);
            // What it plays: "Up next · Title" over the bar, the time left (or the date, the length, the part count, the caught-up line).
            Element line = s.Why.Length > 0
                ? new SpanTextEl(new TextSpans([new TextSpan(s.Why + " · ", Color: Tok.TextSecondary), new TextSpan(s.EpisodeTitle, Weight: S.Strong)], 2))
                    { MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f }
                : BodyStrong(s.EpisodeTitle) with { MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f };
            var sub = new List<Element>(2);
            if (s.Progress >= 0f) sub.Add(new BoxEl { Width = lay.UpBar, Shrink = 0f, Children = [ProgressBar.Determinate(s.Progress * 100f, lay.UpBar)] });
            sub.Add(Caption(s.Sub) with { Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, Shrink = 1f, MinWidth = 0f });
            Element label = new BoxEl
            {
                Direction = 1, Gap = S.UpLabelGap, Grow = 1f, Basis = 0f, MinWidth = 0f,
                Children = [line, new BoxEl { Direction = 0, Gap = S.UpSubGap, AlignItems = FlexAlign.Center, MinWidth = 0f, Children = sub.ToArray() }],
            };
            return new BoxEl
            {
                Key = "show-up", Direction = (byte)(lay.Stacked ? 1 : 0), Gap = lay.Stacked ? S.UpSubGap : S.UpGap,
                AlignItems = lay.Stacked ? FlexAlign.Stretch : FlexAlign.Center, MinHeight = S.UpMinH, MinWidth = 0f,
                Children = [new BoxEl { Direction = 0, Shrink = 0f, Children = [button] }, label],
            };
        }

        // ── About the show (the Expander at 641-1007 and below; the pane's tab at 1008 and up) ──

        Element AboutExpander(PodcastShowView v)
        {
            var header = new BoxEl
            {
                Direction = 0, Gap = S.CommandGap, AlignItems = FlexAlign.Center, MinWidth = 0f,
                Children =
                [
                    BodyStrong(Loc.Get(Strings.Podcast.Pages.PaneAbout)) with { Shrink = 0f },
                    Caption(v.Head.About) with { MaxLines = 1, Trim = TextTrim.CharacterEllipsis, Shrink = 1f, MinWidth = 0f, Color = Tok.TextTertiary },
                ],
            };
            bool open = !v.Head.Following;
            return new BoxEl
            {
                Key = "show-about-x", Direction = 1, Margin = new Edges4(0f, 0f, 0f, K.SectionBottom), MinWidth = 0f,
                Children = [Embed.Comp(new Expander.ExpanderSlots(header, AboutBody(v, rows: true), null), () => new Expander { InitiallyExpanded = open })],
            };
        }

        Element AboutBody(PodcastShowView v, bool rows)
        {
            var h = v.Head;
            var kids = new List<Element>(6);
            if (h.About.Length > 0) kids.Add(Body(h.About) with { Color = Tok.TextSecondary });
            if (h.TrailerId is not null)
                kids.Add(new BoxEl
                {
                    Key = "about-trailer", Direction = 0, Gap = K.RowColGap, AlignItems = FlexAlign.Center, MinWidth = 0f,
                    Children =
                    [
                        Controls.Named(Controls.IconAction(WinUiDetailRules.PlaySolid, _p.PlayTrailer, size: K.PlayButton) with
                        {
                            Corners = CornerRadius4.All(K.PlayButton / 2f), Fill = Tok.FillControlDefault,
                        }, Loc.Get(Strings.Podcast.Pages.PlayTrailer)),
                        new BoxEl
                        {
                            Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f,
                            Children = [Body(Loc.Get(Strings.Podcast.Pages.Trailer)), Caption(h.TrailerSub) with { MaxLines = 1, Trim = TextTrim.CharacterEllipsis }],
                        },
                    ],
                });
            if (h.Rating.Length > 0) kids.Add(RatingCard(h));
            if (h.Facts.Length > 0) kids.Add(FactList(h.Facts));
            if (v.Similar.Length > 0)
            {
                kids.Add(WinUiPodcastKit.SectionHeader(Loc.Get(Strings.Podcast.Pages.MoreLikeThis), strong: true));
                kids.Add(WinUiPodcastKit.MoreLikeThis(v.Similar, _p.OpenUri, rows));
            }
            return new BoxEl { Direction = 1, Gap = K.SectionBottom, MinWidth = 0f, Children = kids.ToArray() };
        }

        Element RatingCard(PodcastShowHead h)
        {
            var stars = new Element[S.Stars];
            int full = (h.RatingX100 + S.StarRound) / S.StarScale;
            for (int i = 0; i < stars.Length; i++) stars[i] = Icon(Icons.FavoriteStarFill, S.RatingStar, i < full ? Tok.TextPrimary : Tok.TextDisabled);
            Element rate = _p.Rate is { } r
                ? Button.Create(Loc.Get(Strings.Podcast.Pages.Rate), r)
                : Controls.Named(Button.Create(Loc.Get(Strings.Podcast.Pages.Rate), static () => { }, isEnabled: false), Loc.Get(Strings.Podcast.Pages.RateUnavailable));
            return new BoxEl
            {
                Key = "about-rating", Direction = 0, Gap = S.CommandGap, AlignItems = FlexAlign.Center, Padding = Edges4.All(S.RatingCardPad), MinWidth = 0f,
                Corners = Radii.ControlAll, Fill = Tok.FillCardSecondary, BorderWidth = 1f, BorderBrush = Tok.ControlElevationBorder,
                Children =
                [
                    Title(h.Rating) with { Shrink = 0f },
                    new BoxEl
                    {
                        Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f,
                        Children = [new BoxEl { Direction = 0, Children = stars }, Caption(h.Ratings) with { MaxLines = 1 }],
                    },
                    new BoxEl { Shrink = 0f, Children = [rate] },
                ],
            };
        }

        static Element FactList(ImmutableArray<PodcastFact> facts)
        {
            var rows = new Element[facts.Length];
            for (int i = 0; i < facts.Length; i++)
                rows[i] = new BoxEl
                {
                    Direction = 0, Gap = S.FactsColGap, MinWidth = 0f,
                    Children =
                    [
                        new BoxEl { Width = S.FactLabelW, Shrink = 0f, Children = [Body(facts[i].Label) with { Color = Tok.TextTertiary }] },
                        new BoxEl { Grow = 1f, Basis = 0f, MinWidth = 0f, Children = [Body(facts[i].Value)] },
                    ],
                };
            return new BoxEl { Key = "about-facts", Direction = 1, Gap = S.FactsRowGap, MinWidth = 0f, Children = rows };
        }

        // ── the list column ──

        Element ListColumn(PodcastShowView v, WinUiPodcastShowLayout lay, bool live)
        {
            float toolbarBottom = K.BarH + S.ToolbarH;
            var groups = new List<Element>(v.Groups.Length + 2);
            foreach (var g in v.Groups) groups.Add(Group(v, g, lay, toolbarBottom, live));
            if (v.Groups.Length == 0 && v.Load == PodcastLoad.Ready)
                groups.Add(new BoxEl
                {
                    Key = "show-empty", Direction = 0, Gap = S.CommandGap, AlignItems = FlexAlign.Center, Wrap = true,
                    Padding = new Edges4(0f, S.EmptyPad, 0f, S.EmptyPad),
                    Children = [Body(Loc.Get(Strings.Podcast.Pages.NoMatch)) with { Color = Tok.TextSecondary },
                        HyperlinkButton.Create(Loc.Get(Strings.Podcast.Pages.ClearFilter), _p.ClearFilter)],
                });
            if (v.HasMore)
                groups.Add(new BoxEl
                {
                    Key = "show-more", Direction = 0, Justify = FlexJustify.Center, Padding = new Edges4(0f, K.SectionBottom, 0f, K.SectionBottom),
                    Children = [Button.Create(Loc.Get(Strings.Podcast.Pages.LoadMore), _p.LoadMore) with { Key = "load-more" }],
                });
            Element body = new BoxEl { Key = "show-groups", Direction = 1, MinWidth = 0f, Children = groups.ToArray() }.StickyClip(toolbarBottom);
            return new BoxEl
            {
                Key = "show-list", Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f,
                Children = [Toolbar(v, lay).Sticky(K.BarH), body],
            };
        }

        BoxEl Toolbar(PodcastShowView v, WinUiPodcastShowLayout lay)
        {
            var labels = new string[v.Filters.Length];
            for (int i = 0; i < labels.Length; i++) labels[i] = WinUiChoice.Counted(v.Filters[i].Label, v.Filters[i].CountText);
            var kids = new List<Element>(4);
            if (lay.Stacked)
            {
                if (_searchOpen.Value)
                    kids.Add(new BoxEl { Grow = 1f, Basis = 0f, MinWidth = 0f, Children = [SearchBox(lay, fill: true)] });
                else
                    kids.Add(new BoxEl
                    {
                        Grow = 1f, Basis = 0f, MinWidth = 0f,
                        Children = [ComboBox.Create(labels, _comboIx, width: S.ComboW, onChange: i => _p.SetFilter((PodcastFilter)i))],
                    });
                kids.Add(Controls.Named(Controls.IconAction(Icons.Search, () => _searchOpen.Value = !_searchOpen.Peek(), size: lay.Target) with { Key = "tb:search" },
                    Loc.Get(Strings.Podcast.Pages.Search)));
            }
            else
            {
                kids.Add(new BoxEl
                {
                    Grow = 1f, Basis = 0f, MinWidth = 0f, ClipToBounds = true,
                    Children = [WinUiChoice.Pivot(labels, _filterIx, WinUiChoiceRules.PivotItemH, i => _p.SetFilter((PodcastFilter)i))],
                });
                kids.Add(SearchBox(lay, fill: false));
            }
            bool oldest = _p.Oldest.Value;
            string sortWord = Loc.Get(oldest ? Strings.Podcast.Pages.OldestFirst : Strings.Podcast.Pages.NewestFirst);
            kids.Add(lay.Tier == WidthTier.Small
                ? Button.Create(sortWord, _p.ToggleSort, ButtonAppearance.Subtle, glyph: Icons.Sort) with { Key = "tb:sort", Shrink = 0f }
                : Controls.Named(Controls.IconAction(Icons.Sort, _p.ToggleSort, size: lay.Target) with { Key = "tb:sort" }, sortWord));
            return new BoxEl
            {
                Key = "show-toolbar", Direction = 1, Height = S.ToolbarH, MinWidth = 0f, Fill = Prop.Of(static () => R.Layer.ContentSurface),
                Children =
                [
                    new BoxEl { Direction = 0, Gap = S.CommandGap, AlignItems = FlexAlign.Center, Grow = 1f, MinWidth = 0f, Children = kids.ToArray() },
                    new BoxEl { Height = 1f, Fill = Prop.Of(static () => Tok.StrokeDividerDefault) },
                ],
            };
        }

        Element SearchBox(WinUiPodcastShowLayout lay, bool fill)
            => new BoxEl
            {
                Key = "tb:find", Shrink = 0f, Grow = fill ? 1f : 0f, MinWidth = 0f,
                Children = [AutoSuggestBox.Create(s_noSuggest, Loc.Get(Strings.Podcast.Pages.Search), width: fill ? S.ComboW : lay.Search, text: _p.Query(),
                    queryIcon: Icons.Search, grow: fill ? 1f : 0f)],
            };

        static readonly string[] s_noSuggest = [];

        Element Group(PodcastShowView v, PodcastGroup g, WinUiPodcastShowLayout lay, float toolbarBottom, bool live)
        {
            bool jumps = v.Years.Length > 1;
            var headKids = new List<Element>(3);
            var buttonKids = new List<Element>(3) { BodyStrong(g.Title) };
            if (g.CountText.Length > 0) buttonKids.Add(Caption(g.CountText) with { Color = Tok.TextTertiary });
            if (jumps) buttonKids.Add(Icon(Icons.ChevronDown, K.SplitChevron, Tok.TextSecondary));
            string key = g.Key;
            var years = v.Years;
            headKids.Add(new BoxEl
            {
                Key = "gh:jump", Direction = 0, Gap = S.CommandGap, AlignItems = FlexAlign.Center, Height = S.GroupButtonH, Shrink = 0f,
                Padding = new Edges4(S.GroupButtonPad, 0f, S.GroupButtonPad, 0f), Margin = new Edges4(-S.GroupButtonPad, 0f, 0f, 0f),
                Corners = Radii.ControlAll, Fill = Tok.FillSubtleTransparent, HoverFill = jumps ? Tok.FillSubtleSecondary : Tok.FillSubtleTransparent,
                PressedFill = jumps ? Tok.FillSubtleTertiary : Tok.FillSubtleTransparent, Role = AutomationRole.Button, Focusable = jumps,
                OnRealized = live ? h => _groupNodes["head:" + key] = h : null,
                OnClick = jumps && live ? () => OpenJump(years, key) : null,
                Children = buttonKids.ToArray(),
            });
            headKids.Add(new BoxEl { Grow = 1f });
            if (g.Fresh) headKids.Add(HyperlinkButton.Create(Loc.Get(Strings.Podcast.Pages.MarkAllPlayed), _p.MarkFreshPlayed));
            Element header = new BoxEl
            {
                Key = "gh", Direction = 0, Height = S.GroupH, AlignItems = FlexAlign.Center, MinWidth = 0f, Fill = Prop.Of(static () => R.Layer.ContentSurface),
                Children = headKids.ToArray(),
            }.Sticky(toolbarBottom);
            var rows = new Element[g.Rows.Length];
            for (int i = 0; i < rows.Length; i++)
                rows[i] = WinUiPodcastKit.EpisodeRow(v.Episodes[g.Rows[i]], lay.RowMode, lay.RowDescription, lay.Stacked, _verbs!);
            return new BoxEl
            {
                Key = "g:" + g.Key, Direction = 1, MinWidth = 0f, OnRealized = live ? h => _groupNodes[key] = h : null,
                Children = [header, new BoxEl { Key = "rows", Direction = 1, Gap = K.RowGap, MinWidth = 0f, Children = rows }.StickyClip(toolbarBottom + S.GroupH)],
            };
        }

        void OpenJump(ImmutableArray<PodcastYear> years, string groupKey)
        {
            if (_jump is { IsOpen: true } open) { open.Close(); return; }
            if (Controls.IsNullOverlay(_overlay) || !_groupNodes.TryGetValue("head:" + groupKey, out var anchor)) return;
            OverlayHandle? opened = null;
            opened = _overlay!.Open(() => anchor, () => JumpGrid(years, () => opened?.Close()), FlyoutPlacement.BottomEdgeAlignedLeft,
                new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss) { ConstrainToRootBounds = false });
            _jump = opened;
        }

        Element JumpGrid(ImmutableArray<PodcastYear> years, Action close)
        {
            var rows = new List<Element>();
            for (int i = 0; i < years.Length; i += S.JumpCols)
            {
                var cells = new Element[S.JumpCols];
                for (int c = 0; c < S.JumpCols; c++)
                {
                    if (i + c >= years.Length) { cells[c] = new BoxEl { Grow = 1f, Basis = 0f }; continue; }
                    var y = years[i + c];
                    cells[c] = new BoxEl
                    {
                        Key = "jump:" + y.Label, Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Height = S.JumpCellH, Justify = FlexJustify.Center,
                        Padding = new Edges4(S.JumpPad, 0f, S.JumpPad, 0f), Corners = Radii.ControlAll, Fill = Tok.FillControlDefault,
                        HoverFill = Tok.FillControlSecondary, PressedFill = Tok.FillControlTertiary, BorderWidth = 1f, BorderBrush = Tok.ControlElevationBorder,
                        Role = AutomationRole.Button, Focusable = true,
                        OnClick = () => { close(); JumpTo("y" + y.Label); },
                        Children = [Subtitle(y.Label), Caption(y.Count) with { Color = Tok.TextTertiary }],
                    };
                }
                rows.Add(new BoxEl { Direction = 0, Gap = S.JumpGap, Children = cells });
            }
            return new BoxEl
            {
                Key = "show-jump", Direction = 1, Gap = S.JumpGap, Width = S.JumpW, Padding = Edges4.All(S.JumpPad),
                Children = [BodyStrong(Loc.Get(Strings.Podcast.Pages.JumpTo)), .. rows],
            };
        }

        void JumpTo(string groupKey)
        {
            if (!_groupNodes.TryGetValue(groupKey, out var node) || _viewport.IsNull || Context.Scene is not { } scene) return;
            scene.BringIntoView(_viewport, node, align: 0f, Design.Reduced ? ScrollMove.Immediate : ScrollMove.Glide, margin: K.BarH + S.ToolbarH);
        }

        // ── the details pane (≥1008) ──

        Element Pane(PodcastShowView v, float viewportH, bool live)
        {
            var tab = live ? _p.PaneTab.Value : PodcastPaneTab.Episode;
            string[] labels = [Loc.Get(Strings.Podcast.Pages.PaneEpisode), Loc.Get(Strings.Podcast.Pages.PaneAbout)];
            Element body = tab == PodcastPaneTab.About ? AboutBody(v, rows: true) : EpisodePreview(v, live);
            float maxH = MathF.Max(S.PaneW, viewportH - S.PaneTop - S.PaneFoot - R.Space.DockReserve);
            return new BoxEl
            {
                Key = "show-pane", Direction = 1, Width = S.PaneW, Shrink = 0f, MinWidth = 0f, MaxHeight = maxH,
                Corners = CornerRadius4.All(K.CardRadius), Fill = Tok.FillCardDefault, BorderWidth = 1f, BorderBrush = Tok.ControlElevationBorder,
                Padding = new Edges4(S.PanePad, S.PanePadTop, S.PanePad, 0f),
                Children =
                [
                    WinUiChoice.Pivot(labels, _paneIx, WinUiChoiceRules.PivotItemH, i => _p.SetPaneTab((PodcastPaneTab)i)),
                    new BoxEl { Height = 1f, Fill = Prop.Of(static () => Tok.StrokeDividerDefault) },
                    ScrollView(new BoxEl
                    {
                        Key = "pane:" + (tab == PodcastPaneTab.About ? "about" : "ep"), Direction = 1, MinWidth = 0f,
                        Padding = new Edges4(0f, K.SectionBottom, 0f, S.PanePad), Children = [body],
                    }) with { Key = "pane-scroll", Shrink = 1f, EdgeCues = ScrollEdgeCues.None },
                ],
            }.Sticky(S.PaneTop);
        }

        Element EpisodePreview(PodcastShowView v, bool live)
        {
            string? id = live ? _p.Selected.Value : null;
            PodcastEpisodeVm? vm = null;
            if (id is not null) foreach (var e in v.Episodes) if (e.Id == id) { vm = e; break; }
            if (vm is null)
                return new BoxEl
                {
                    Direction = 1, Padding = new Edges4(0f, S.HintPad, 0f, S.HintPad),
                    Children = [Body(Loc.Get(Strings.Podcast.Pages.SelectHint)) with { Color = Tok.TextTertiary }],
                };
            bool playing = live && _p.Playing.Value == vm.Id;
            string playWord = Loc.Get(playing ? Strings.Podcast.Pages.Pause : vm.State switch
            {
                PodcastRowState.InProgress => Strings.Podcast.Pages.Resume,
                PodcastRowState.Played => Strings.Podcast.Pages.PlayAgain,
                _ => Strings.Podcast.Pages.Play,
            });
            string eid = vm.Id;
            var kids = new List<Element>(8)
            {
                new BoxEl
                {
                    Direction = 1, MinWidth = 0f, Gap = S.IdGap,
                    Children =
                    [
                        Caption(vm.Eyebrow) with { Color = Tok.TextSecondary },
                        Subtitle(vm.Title) with { Wrap = TextWrap.Wrap, MinWidth = 0f },
                        WinUiPodcastKit.MetaLine(vm, showNew: false),
                    ],
                },
                new BoxEl
                {
                    Direction = 0, Gap = S.CommandGap, AlignItems = FlexAlign.Center, Wrap = true,
                    Children =
                    [
                        Button.Create(playWord, () => _p.Play(eid), ButtonAppearance.Accent, glyph: playing ? WinUiDetailRules.PauseSolid : WinUiDetailRules.PlaySolid)
                            with { Key = "pane:play" },
                        Button.Create(Loc.Get(vm.Saved ? Strings.Podcast.Pages.Saved : Strings.Podcast.Pages.Save), () => _p.ToggleSaved(eid),
                            glyph: vm.Saved ? Icons.HeartFill : Icons.Heart) with { Key = "pane:save" },
                        Controls.Named(Controls.IconAction(Icons.Queue, () => _p.PlayNext(eid)) with { Key = "pane:next" }, Loc.Get(Strings.Podcast.Pages.PlayNext)),
                        WinUiDetailKit.MoreButton(() => _p.RowMenu(eid), WinUiChoiceRules.ControlH),
                    ],
                },
            };
            if (vm.Description.Length > 0)
                kids.Add(Body(vm.Description) with { Color = Tok.TextSecondary, MaxLines = S.PaneClamp, Trim = TextTrim.CharacterEllipsis });
            var ch = live ? _p.PaneChapters.Value : PodcastPaneChapters.None;
            if (ch.Shown.Length > 0)
            {
                kids.Add(WinUiPodcastKit.SectionHeader(Loc.Get(Strings.Podcast.Pages.Chapters), Strings.Podcast.Pages.ChaptersCount(ch.Total), strong: true));
                var rows = new Element[ch.Shown.Length];
                for (int i = 0; i < rows.Length; i++)
                {
                    var c = ch.Shown[i];
                    rows[i] = new BoxEl
                    {
                        Key = "ch:" + c.Index, Direction = 0, Gap = S.CommandGap, MinWidth = 0f, Padding = new Edges4(0f, S.FactsRowGap, 0f, S.FactsRowGap),
                        Children =
                        [
                            new BoxEl { Width = S.ChapterTimeW, Shrink = 0f, Children = [Caption(c.Start) with { Color = c.Current ? Tok.AccentTextPrimary : Tok.TextTertiary }] },
                            Body(c.Title) with { MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, Shrink = 1f, MinWidth = 0f,
                                Color = c.Current ? Tok.TextPrimary : Tok.TextSecondary },
                        ],
                    };
                }
                kids.Add(new BoxEl { Key = "pane-chapters", Direction = 1, MinWidth = 0f, Children = rows });
            }
            else if (!ch.Pending)
                kids.Add(Caption(Loc.Get(Strings.Podcast.Pages.NoChapters)) with { Color = Tok.TextTertiary });
            var links = new List<Element>(2)
            {
                HyperlinkButton.Create(ch.Total > ch.Shown.Length ? Strings.Podcast.Pages.OpenEpisodeAll(ch.Total) : Loc.Get(Strings.Podcast.Pages.OpenEpisode),
                    () => _p.Open(eid)),
            };
            if (vm.HasTranscript) links.Add(HyperlinkButton.Create(Loc.Get(Strings.Podcast.Pages.ReadTranscript), () => _p.Open(eid)));
            kids.Add(new BoxEl { Direction = 0, Gap = S.UpGap, Wrap = true, Children = links.ToArray() });
            return new BoxEl { Key = "pane-ep:" + vm.Id, Direction = 1, Gap = K.SectionBottom, MinWidth = 0f, Children = kids.ToArray() };
        }
    }
}
