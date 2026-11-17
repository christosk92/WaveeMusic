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
// The pinned rows (the toolbar, the year headers) are the kit's PinnedBand: nothing painted at rest, a hairline while pinned; what scrolls under them cuts
// itself at their lower edge. The pane pins under the bar; taller than the viewport, it rides the page until its foot reaches the viewport's foot and pins
// there (no scroller of its own). A filter or a search HIDES rows (the rows stay mounted).

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
        readonly Signal<bool> _searchOpen = new(false), _toolbarStuck = new(false);
        readonly Signal<float> _paneH = new(0f);
        readonly Dictionary<string, Signal<bool>> _groupStuck = [];
        Action<RectF>? _measurePane;
        Func<HeadView>? _projectHead;
        Memo<HeadView>? _headView;
        Func<Affine2D>? _paneShiftOf;
        readonly Signal<bool> _paneStuck = new(false);
        float _paneShift;
        double _paneLastOffset = double.NaN;
        Action<bool>? _followToggle;
        string[] _filterLabels = [];
        string[]? _paneLabels;
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
            float viewportH = facts.ViewportHeight, contentW = facts.ContentWidth;
            _overlay = UseContext(Overlay.Service);
            _captureViewport ??= h => _viewport = h;
            _measurePane ??= r => { if (r.H > 1f && MathF.Abs(r.H - _paneH.Peek()) > 0.5f) _paneH.Value = r.H; };
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

            _projectHead ??= () => HeadOf(_p.View.Value);
            _headView = UseComputed(_projectHead);
            var v = ViewOf(_headView.Value);
            bool loading = v.Load == PodcastLoad.Pending;
            var shown = loading ? s_placeholder : v;
            Element content = new SkelRegionEl(
                Pending: _pending, Failed: _failed, Content: () => Compose(shown, lay, inset, viewportH, contentW, live: true),
                ShimmerSource: () => Compose(s_placeholder, lay, inset, viewportH, contentW, live: false),
                OnFailed: () => Controls.Vacancy(Controls.VacancyVoice.Error), Reveal: SkelReveal.Soft, Style: SkeletonStyle.Default with { DeriveComponents = true }, Group: null,
                SmoothResize: false);
            var head = shown.Head;
            Element bar = WinUiPodcastKit.Bar(head.ArtUrl, head.Title,
                shown.Smart is { Kind: PodcastSmartKind.CaughtUp } ? Loc.Get(Strings.Podcast.Pages.CaughtUp) : shown.Smart?.EpisodeTitle ?? "",
                BarButton(shown), inset, captionShown: !lay.Stacked);
            return WinUiPodcastKit.Page(p.RouteKey, loading ? 0u : head.Tone, _barOn,
                new BoxEl { Key = "podcast-show-body", Direction = 1, MinWidth = 0f, Layout = WinUiDiscoveryMotion.HeightReflow, Children = [content] }, bar, _scroll, _captureViewport);
        }

        /// <summary>What the page itself draws (the hero, the bar, the About expander): the head, the smart action, the similar shows and the load state.
        /// The host reuses these instances while their content holds, so the memo's equality keeps a filter switch away from the page.</summary>
        sealed record HeadView(PodcastLoad Load, PodcastShowHead Head, PodcastSmart? Smart, ImmutableArray<PodcastCardVm> Similar);

        static HeadView HeadOf(PodcastShowView v) => new(v.Load, v.Head, v.Smart, v.Similar);

        static PodcastShowView ViewOf(HeadView h) => PodcastShowView.Empty with { Load = h.Load, Head = h.Head, Smart = h.Smart, Similar = h.Similar };

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

        Element Compose(PodcastShowView v, WinUiPodcastShowLayout lay, float inset, float viewportH, float contentW, bool live)
        {
            var kids = new List<Element>(4) { Hero(v, lay, live) };
            if (live) kids.Add(WinUiPodcastKit.BarSentinel(_barOn));
            if (!lay.Pane) kids.Add(AboutExpander(v));
            float listW = MathF.Max(0f, contentW - 2f * inset - (lay.Pane ? S.PaneW + S.ColGap : 0f));
            Element list = live
                ? Embed.Comp(new ListProps(this, lay, WinUiPodcastShowTiers.SearchCollapsed(lay, listW), WinUiPodcastShowTiers.FilterCombo(lay, listW)),
                    static () => new ListHost()) with { Key = "show-list-host" }
                : ListColumn(v, lay, live, WinUiPodcastShowTiers.SearchCollapsed(lay, listW), WinUiPodcastShowTiers.FilterCombo(lay, listW));
            // ONE row at every tier: the pane comes and goes beside the list (a declared swap), the list reflows to its new width.
            kids.Add(new BoxEl
            {
                Key = "show-cols", Direction = 0, Gap = lay.Pane ? S.ColGap : 0f, AlignItems = FlexAlign.Start, MinWidth = 0f,
                Layout = WinUiDiscoveryMotion.Reflow,
                Children = lay.Pane ? [list, live ? PaneSlot() : Pane(v, live)] : [list],
            });
            return new BoxEl
            {
                Key = "show-column", Direction = 1, MinWidth = 0f,
                Padding = new Edges4(inset, 0f, inset, K.PageFoot + R.Space.DockReserve),
                Children = kids.ToArray(),
            };
        }

        /// <summary>The list column (toolbar, groups, rows): its own component, the only part of the page a filter, a search, the order or the row
        /// states re-render.</summary>
        sealed record ListProps(PageHost Host, WinUiPodcastShowLayout Lay, bool SearchIcon, bool Combo);

        sealed class ListHost : Component
        {
            public override Element Render()
            {
                var p = UseProps<ListProps>();
                var full = p.Host._p.View.Value;
                var v = full.Load == PodcastLoad.Pending ? s_placeholder : full;
                return p.Host.ListColumn(v, p.Lay, live: true, p.SearchIcon, p.Combo);
            }
        }

        /// <summary>The details pane: its own component over the selected episode (its filter flag set aside), the head and the similar shows, so a filter
        /// switch does not re-render it.</summary>
        sealed record PaneProps(PageHost Host);

        sealed record PaneView(PodcastLoad Load, PodcastEpisodeVm? Selected, PodcastShowHead Head, ImmutableArray<PodcastCardVm> Similar);

        sealed class PaneHost : Component
        {
            PageHost? _host;
            Func<PaneView>? _project;

            public override Element Render()
            {
                _host = UseProps<PaneProps>().Host;
                _project ??= Project;
                var pv = UseComputed(_project).Value;
                var v = PodcastShowView.Empty with
                {
                    Load = pv.Load, Head = pv.Head, Similar = pv.Similar, Episodes = pv.Selected is { } vm ? [vm] : [],
                };
                return _host.Pane(v, live: true);
            }

            PaneView Project()
            {
                var p = _host!._p;
                var full = p.View.Value;
                string? id = p.Selected.Value;
                PodcastEpisodeVm? vm = null;
                if (id is not null)
                    foreach (var e in full.Episodes)
                        if (e.Id == id) { vm = e.Hidden ? e with { Hidden = false } : e; break; }
                return new PaneView(full.Load, vm, full.Head, full.Similar);
            }
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
                    Wrap = TextWrap.Wrap, MaxLines = S.TitleLines, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                },
                Meta(h),
            };
            Element id = new BoxEl { Direction = 1, Gap = S.IdGap, Grow = 1f, Basis = 0f, MinWidth = 0f, Children = idKids.ToArray() };
            Element commands = Commands(v, lay, live);
            Element up = Up(v, lay, live);
            Element form = lay.Stacked ? Stacked() : Beside();
            return new BoxEl { Key = WinUiFrameLayout.PageTitleKey, Direction = 1, MinWidth = 0f, Layout = WinUiDiscoveryMotion.Reflow, Children = [form] };

            Element Stacked() => new BoxEl
                {
                    Key = "hero:stacked", Enter = WinUiDiscoveryMotion.SwapFade, Exit = WinUiDiscoveryMotion.SwapFade, Direction = 1, Gap = lay.CoverGap, MinWidth = 0f, Padding = new Edges4(0f, lay.HeroTop, 0f, lay.HeroBottom),
                    Children =
                    [
                        new BoxEl { Direction = 0, Gap = lay.CoverGap, AlignItems = FlexAlign.Start, MinWidth = 0f, Children = [cover, id] },
                        up,
                        commands,
                    ],
                };
            Element Beside() => new BoxEl
            {
                Key = "hero:beside", Enter = WinUiDiscoveryMotion.SwapFade, Exit = WinUiDiscoveryMotion.SwapFade, Direction = 0, Gap = lay.CoverGap, AlignItems = FlexAlign.Stretch, MinWidth = 0f,
                Padding = new Edges4(0f, lay.HeroTop, 0f, lay.HeroBottom),
                Children =
                [
                    new BoxEl { Key = "hero:cover", Shrink = 0f, AlignSelf = FlexAlign.Start, Layout = WinUiDiscoveryMotion.Reflow, Children = [cover] },
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

        /// <summary>The eyebrow: the kind word as a link to its hub, then the topics (plain: the data carries no destination for a topic), then the tags.</summary>
        Element Eyebrow(PodcastShowHead h)
        {
            var kids = new List<Element>(4);
            if (_p.OpenKind is { } openKind && h.Kind.Length > 0)
                kids.Add(new BoxEl
                {
                    Key = "eyebrow:kind", Shrink = 0f, Margin = new Edges4(-S.LinkPadX, 0f, -S.LinkPadX, 0f),
                    Children = [HyperlinkButton.Create(h.Kind, openKind, size: ControlSize.Small)],
                });
            else if (h.Kind.Length > 0) kids.Add(Caption(h.Kind) with { Color = Tok.TextSecondary });
            if (h.Topics.Length > 0)
                kids.Add(Caption("· " + string.Join(" · ", h.Topics)) with { Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, Shrink = 1f, MinWidth = 0f });
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

        /// <summary>publisher · ★ rating (count) · cadence · N episodes: each part after the first carries its own "·", so a wrapped line never ends on one.</summary>
        static Element Meta(PodcastShowHead h)
        {
            var parts = new List<Element>(4);
            if (h.Publisher.Length > 0) parts.Add(Body(h.Publisher) with { MaxLines = 1, Trim = TextTrim.CharacterEllipsis, Shrink = 1f, MinWidth = 0f });
            if (h.Rating.Length > 0)
                parts.Add(new BoxEl
                {
                    Direction = 0, Gap = S.TagGap, AlignItems = FlexAlign.Center, Shrink = 0f,
                    Children =
                    [
                        Icon(Icons.FavoriteStarFill, S.MetaStar, Tok.TextSecondary),
                        Body(h.Rating) with { Color = Tok.TextSecondary },
                        Body(h.RatingCount) with { Color = Tok.TextTertiary },
                    ],
                });
            if (h.Cadence.Length > 0) parts.Add(Body(h.Cadence) with { Color = Tok.TextSecondary });
            if (h.Count.Length > 0) parts.Add(Body(h.Count) with { Color = Tok.TextSecondary });
            var kids = new Element[parts.Count];
            for (int i = 0; i < kids.Length; i++)
                kids[i] = i == 0 ? parts[i] : new BoxEl
                {
                    Direction = 0, Gap = S.EyebrowGap, AlignItems = FlexAlign.Center, Shrink = 0f,
                    Children = [Body("·") with { Color = Tok.TextTertiary }, parts[i]],
                };
            return new BoxEl { Direction = 0, Gap = S.EyebrowGap, AlignItems = FlexAlign.Center, Wrap = true, MinWidth = 0f, Children = kids };
        }

        Element Commands(PodcastShowView v, WinUiPodcastShowLayout lay, bool live)
        {
            var h = v.Head;
            var size = lay.Target > WinUiChoiceRules.ControlH ? ControlSize.Large : ControlSize.Medium;
            var kids = new List<Element>(4)
            {
                new BoxEl
                {
                    Key = "cmd:follow", Shrink = 0f,
                    Children = [ToggleButton.Controlled(Loc.Get(Strings.Podcast.Pages.Follow), h.Following, _followToggle ??= _ => _p.ToggleFollow(),
                        checkedLabel: Loc.Get(Strings.Podcast.Pages.Following), style: WinUiDetailKit.NeutralToggle(size))],
                },
            };
            if (h.Following)
                kids.Add(Controls.Named(Controls.IconAction(Icons.Bell, null, size: lay.Target, isEnabled: false) with { Key = "cmd:bell" },
                    Loc.Get(Strings.Podcast.Pages.NotifyUnavailable)));
            if (_p.Share is { } share)
                kids.Add(lay.ShareWord
                    ? Swap("cmd:share-word", false, Button.Create(Loc.Get(Strings.Podcast.Pages.Share), share, glyph: Icons.Share) with { Height = lay.Target, Shrink = 0f })
                    : Swap("cmd:share", false, Controls.Named(Controls.IconAction(Icons.Share, share, size: lay.Target), Loc.Get(Strings.Podcast.Pages.Share))));
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
            if (s.Progress >= 0f) sub.Add(new BoxEl { Width = lay.UpBar, Shrink = 0f, Children = [ProgressBar.Determinate(s.Progress, lay.UpBar)] });
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
                Key = "show-about-x", Enter = WinUiDiscoveryMotion.SwapFade, Exit = WinUiDiscoveryMotion.SwapFade, Direction = 1, Margin = new Edges4(0f, 0f, 0f, K.SectionBottom), MinWidth = 0f,
                Children = [Embed.Comp(new Expander.ExpanderSlots(header, AboutBody(v, rows: true), null), () => new Expander { InitiallyExpanded = open })],
            };
        }

        Element AboutBody(PodcastShowView v, bool rows)
        {
            var h = v.Head;
            var kids = new List<Element>(6);
            if (h.About.Length > 0 || h.AboutHtml.Length > 0) kids.Add(WinUiPodcastKit.Description(h.AboutHtml.Length > 0 ? h.AboutHtml : h.About));
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
                kids.Add(WinUiPodcastKit.MoreLikeThis(v.Similar, _p.OpenUri, rows, _p.CardMenu, _p.PlayCard));
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

        Element ListColumn(PodcastShowView v, WinUiPodcastShowLayout lay, bool live, bool searchIcon, bool combo)
        {
            float toolbarBottom = K.BarH + S.ToolbarH;
            var groups = new List<Element>(v.Groups.Length + 2);
            foreach (var g in v.Groups) groups.Add(Group(v, g, lay, toolbarBottom, live));
            if (v.ShownCount == 0 && v.Load == PodcastLoad.Ready)
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
                Key = "show-list", Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Layout = WinUiDiscoveryMotion.Reflow,
                Children = [WinUiPodcastKit.PinnedBand("show-toolbar", Toolbar(v, lay, searchIcon, combo), S.ToolbarH, K.BarH, _toolbarStuck), body],
            };
        }

        BoxEl Toolbar(PodcastShowView v, WinUiPodcastShowLayout lay, bool searchIcon, bool combo)
        {
            // The labels array is REUSED while its words hold: a new array would re-push the SelectorBar / ComboBox on every snapshot (a filter switch).
            var labels = _filterLabels;
            bool same = labels.Length == v.Filters.Length;
            for (int i = 0; same && i < labels.Length; i++) same = labels[i] == WinUiChoice.Counted(v.Filters[i].Label, v.Filters[i].CountText);
            if (!same)
            {
                labels = new string[v.Filters.Length];
                for (int i = 0; i < labels.Length; i++) labels[i] = WinUiChoice.Counted(v.Filters[i].Label, v.Filters[i].CountText);
                _filterLabels = labels;
            }
            var kids = new List<Element>(4);
            bool searching = searchIcon && _searchOpen.Value;
            if (searching)
                kids.Add(Swap("tb:search-field", true, SearchBox(lay, fill: true)));
            else if (combo)
                kids.Add(Swap("tb:combo", true, ComboBox.Create(labels, _comboIx, width: S.ComboW, onChange: i => _p.SetFilter((PodcastFilter)i))));
            else
                kids.Add(Swap("tb:filters", true,
                    WinUiChoice.Pivot(labels, _filterIx, WinUiChoiceRules.PivotItemH, i => _p.SetFilter((PodcastFilter)i), s_filterKeys)) with { ClipToBounds = true });
            if (searchIcon)
                kids.Add(Controls.Named(Controls.IconAction(Icons.Search, () => _searchOpen.Value = !_searchOpen.Peek(), size: lay.Target) with { Key = "tb:search" },
                    Loc.Get(Strings.Podcast.Pages.Search)));
            else kids.Add(Swap("tb:find-box", false, SearchBox(lay, fill: false)));
            bool oldest = _p.Oldest.Value;
            string sortWord = Loc.Get(oldest ? Strings.Podcast.Pages.OldestFirst : Strings.Podcast.Pages.NewestFirst);
            kids.Add(lay.Tier == WidthTier.Small
                ? Swap("tb:sort-word", false, Button.Create(sortWord, _p.ToggleSort, ButtonAppearance.Subtle, glyph: Icons.Sort) with { Shrink = 0f })
                : Swap("tb:sort", false, Controls.Named(Controls.IconAction(Icons.Sort, _p.ToggleSort, size: lay.Target), sortWord)));
            return new BoxEl
            {
                Key = "show-toolbar:row", Direction = 1, Height = S.ToolbarH, MinWidth = 0f,
                Children =
                [
                    new BoxEl { Direction = 0, Gap = S.CommandGap, AlignItems = FlexAlign.Center, Grow = 1f, MinWidth = 0f, Children = kids.ToArray() },
                    new BoxEl { Height = 1f, Fill = Prop.Of(static () => Tok.StrokeDividerDefault) },
                ],
            };
        }

        /// <summary>A part only some tiers draw: keyed, faded in and out as a declared swap (a crossing never rebuilds it in place).
        /// <paramref name="fill"/>: it takes the row's free width.</summary>
        static BoxEl Swap(string key, bool fill, Element body) => new()
        {
            Key = key, Direction = 0, AlignItems = FlexAlign.Center, Grow = fill ? 1f : 0f, Basis = fill ? 0f : float.NaN, Shrink = fill ? 1f : 0f, MinWidth = 0f,
            Enter = WinUiDiscoveryMotion.SwapFade, Exit = WinUiDiscoveryMotion.SwapFade, Children = [body],
        };

        Element SearchBox(WinUiPodcastShowLayout lay, bool fill)
            => new BoxEl
            {
                Key = "tb:find", Shrink = 0f, Grow = fill ? 1f : 0f, MinWidth = 0f,
                Children = [AutoSuggestBox.Create(s_noSuggest, Loc.Get(Strings.Podcast.Pages.Search), width: fill ? S.ComboW : lay.Search, text: _p.Query(),
                    queryIcon: Icons.Search, grow: fill ? 1f : 0f)],
            };

        static readonly string[] s_noSuggest = [];
        static readonly string?[] s_filterKeys = ["filter:all", "filter:unplayed", "filter:progress", "filter:saved"];
        static readonly string?[] s_paneKeys = ["pane:tab:episode", "pane:tab:about"];

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
                Key = "gh:jump:" + g.Key, Direction = 0, Gap = S.CommandGap, AlignItems = FlexAlign.Center, Height = lay.GroupButtonH, Shrink = 0f,
                Padding = new Edges4(S.GroupButtonPad, 0f, S.GroupButtonPad, 0f), Margin = new Edges4(-S.GroupButtonPad, 0f, 0f, 0f),
                Corners = Radii.ControlAll, Fill = Tok.FillSubtleTransparent, HoverFill = jumps ? Tok.FillSubtleSecondary : Tok.FillSubtleTransparent,
                PressedFill = jumps ? Tok.FillSubtleTertiary : Tok.FillSubtleTransparent, Role = AutomationRole.Button, Focusable = jumps,
                OnRealized = live ? h => _groupNodes["head:" + key] = h : null,
                OnClick = jumps && live ? () => OpenJump(years, key) : null,
                Children = buttonKids.ToArray(),
            });
            headKids.Add(new BoxEl { Grow = 1f });
            if (g.Fresh) headKids.Add(HyperlinkButton.Create(Loc.Get(Strings.Podcast.Pages.MarkAllPlayed), _p.MarkFreshPlayed));
            if (!_groupStuck.TryGetValue(key, out var stuck)) _groupStuck[key] = stuck = new Signal<bool>(false);
            Element header = WinUiPodcastKit.PinnedBand("gh", new BoxEl
            {
                Direction = 0, Height = lay.GroupH, AlignItems = FlexAlign.Center, MinWidth = 0f, Children = headKids.ToArray(),
            }, lay.GroupH, toolbarBottom, stuck);
            var rows = new Element[g.Rows.Length];
            for (int i = 0; i < rows.Length; i++)
            {
                var vm = v.Episodes[g.Rows[i]];
                // The row sees its episode without the filter flag (a filter switch only flips the anchor's Visible: the row itself does not re-render).
                rows[i] = WinUiPodcastKit.EpisodeRow(vm.Hidden ? vm with { Hidden = false } : vm, lay.RowMode, lay.RowDescription, lay.Stacked, _verbs!)
                    with { Visible = !vm.Hidden };
            }
            return new BoxEl
            {
                Key = "g:" + g.Key, Direction = 1, MinWidth = 0f, Layout = WinUiDiscoveryMotion.Shove, Visible = !g.Hidden, OnRealized = live ? h => _groupNodes[key] = h : null,
                Children = [header, new BoxEl { Key = "rows", Direction = 1, Gap = K.RowGap, MinWidth = 0f, Children = rows }.StickyClip(toolbarBottom + lay.GroupH)],
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

        Element Pane(PodcastShowView v, bool live)
        {
            var tab = live ? _p.PaneTab.Value : PodcastPaneTab.Episode;
            string[] labels = _paneLabels ??= [Loc.Get(Strings.Podcast.Pages.PaneEpisode), Loc.Get(Strings.Podcast.Pages.PaneAbout)];
            Element body = tab == PodcastPaneTab.About ? AboutBody(v, rows: true) : EpisodePreview(v, live);
            _paneShiftOf ??= PaneShift;
            return new BoxEl
            {
                Key = "show-pane", Enter = WinUiDiscoveryMotion.SwapFade, Exit = WinUiDiscoveryMotion.SwapFade, Direction = 1, Width = S.PaneW, Shrink = 0f, MinWidth = 0f,
                Corners = CornerRadius4.All(K.CardRadius), Fill = Tok.FillCardDefault, BorderWidth = 1f, BorderBrush = Tok.ControlElevationBorder,
                Padding = new Edges4(S.PanePad, S.PanePadTop, S.PanePad, 0f), OnBoundsChanged = live ? _measurePane : null,
                Children =
                [
                    WinUiChoice.Pivot(labels, _paneIx, WinUiChoiceRules.PivotItemH, i => _p.SetPaneTab((PodcastPaneTab)i), s_paneKeys),
                    new BoxEl { Height = 1f, Fill = Prop.Of(static () => Tok.StrokeDividerDefault) },
                    new BoxEl
                    {
                        Key = "pane:" + (tab == PodcastPaneTab.About ? "about" : "ep"), Direction = 1, MinWidth = 0f,
                        Padding = new Edges4(0f, K.SectionBottom, 0f, S.PanePad), Children = [body],
                    },
                ],
            };
        }

        /// <summary>The pane's slot in the columns row: it carries the pin and the ride (a sticky root returned FROM a component never pins: its parent
        /// would be the component's anchor), the pane component inside it.</summary>
        Element PaneSlot()
        {
            _paneShiftOf ??= PaneShift;
            return new BoxEl
            {
                Key = "show-pane-slot", Enter = WinUiDiscoveryMotion.SwapFade, Exit = WinUiDiscoveryMotion.SwapFade, Direction = 1, Shrink = 0f, MinWidth = 0f,
                // The ride is a transform on a box INSIDE the pinned slot: the sticky effect owns the slot's own translation.
                Children =
                [
                    new BoxEl
                    {
                        Key = "show-pane-ride", Direction = 1, MinWidth = 0f, Transform = Prop.Of(_paneShiftOf),
                        Children = [Embed.Comp(new PaneProps(this), static () => new PaneHost()) with { Key = "show-pane-host" }],
                    },
                ],
            }.Sticky(S.PaneTop, engaged: _paneStuck);
        }

        /// <summary>The pane's ride (the owner, 2026-11-18): pinned under the bar while it fits; a pane taller than the viewport scrolls with the page
        /// until its foot reaches the viewport's foot and holds there, and on the way back up it scrolls the other way until its top is under the bar
        /// again. The pinned pane's shift (0 .. its overhang) follows each scroll step only while it is pinned; a bound transform, so nothing relayouts.</summary>
        Affine2D PaneShift()
        {
            double o = _scroll.Offset.Value;
            float view = (float)_scroll.ViewportSignal.Value;
            float h = _paneH.Value;
            float overhang = MathF.Max(0f, h + S.PaneTop + S.PaneFoot - view);
            double d = double.IsNaN(_paneLastOffset) ? 0.0 : o - _paneLastOffset;
            _paneLastOffset = o;
            if (_paneStuck.Peek()) _paneShift = Math.Clamp(_paneShift + (float)d, 0f, overhang);
            else _paneShift = MathF.Min(_paneShift, overhang);
            return Affine2D.Translation(0f, -_paneShift);
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
                        new BoxEl
                        {
                            Direction = 0, Gap = S.CommandGap, AlignItems = FlexAlign.Center, MinWidth = 0f,
                            Children =
                            [
                                Caption(vm.Eyebrow) with { Color = Tok.TextSecondary, Grow = 1f, Shrink = 1f, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                                Button.Create(Loc.Get(Strings.Podcast.Pages.OpenEpisode), () => _p.Open(eid), ButtonAppearance.Subtle, glyph: Icons.OpenInNewWindow)
                                    with { Key = "pane:open", Shrink = 0f },
                            ],
                        },
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
                        Controls.Named(Controls.IconAction(Icons.Play, () => _p.PlayNext(eid)) with
                        {
                            Key = "pane:next", Children = [WinUiPodcastKit.PlayNextIcon(S.PaneGlyph, Tok.TextPrimary)],
                        }, Loc.Get(Strings.Podcast.Pages.PlayNext)),
                        WinUiDetailKit.MoreButton(() => _p.RowMenu(eid), WinUiChoiceRules.ControlH),
                    ],
                },
            };
            if (vm.Description.Length > 0 || vm.DescriptionHtml.Length > 0)
                kids.Add(WinUiPodcastKit.Description(vm.DescriptionHtml.Length > 0 ? vm.DescriptionHtml : vm.Description, S.PaneClamp));
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
            return new BoxEl { Key = "pane-ep:" + vm.Id, Direction = 1, Gap = K.SectionBottom, MinWidth = 0f, Children = kids.ToArray() };
        }
    }
}
