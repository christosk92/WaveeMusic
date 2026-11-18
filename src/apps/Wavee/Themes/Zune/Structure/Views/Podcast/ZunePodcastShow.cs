// ── Themes/Zune/Structure/Views/Podcast/ZunePodcastShow.cs — Zune's show page (podcast pages v2, concept `showZune`) ───────────────────────────────────
//
//   ZunePodcastKit.Page (one scroller over the page column)
//   └─ the name (120 light, lowercase, bleeding under the fade; 80 wrapping at Compact)
//      the pivots: episodes N · about · more like this (one line; the lit one leads and the strip slides like a Windows Phone pivot)
//      the hero: the flat square art · [the accent label ("continue", "new", "all caught up") · the split primary + what it plays (up next · title, the
//                line and the time left) · the meta (publisher · ★ rating (count) · cadence · N episodes) · follow / bell / share / ...]
//      the pivot's body:
//        episodes  the toolbar (pinned under the viewport's top: the filter words with their counts, the search pill, the sort) and the groups (a
//                  pinned 26/32 heading per year or "new since your last visit" with mark all as played; the rows, which expand in place)
//        about     the description (the house rich text), the trailer, the rating and the facts
//        more      the house cards
//
// The pinned rows paint nothing at rest: what scrolls under them cuts itself at their lower edge (StickyClip), so no band shows (owner 2026-11-18).
// Loading is this layout over a placeholder snapshot with flat fills (SkelRegionEl). Numbers: `ZunePodcastRules`, the record `ZunePodcastLayout`.

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
using Wavee.Theming.Roles;
using static FluentGpu.Dsl.Ui;
using K = Wavee.Themes.Zune.ZunePodcastKit;
using Z = Wavee.Themes.Zune.ZunePodcastRules;

namespace Wavee.Themes.Zune;

/// <summary>Zune's show page.</summary>
internal static partial class ZunePodcastShow
{
    public static Element Template(PodcastShowPageProps p, in ViewContext ctx)
        => new BoxEl { Direction = 1, Grow = 1f, MinHeight = 0f, MinWidth = 0f, Children = [Embed.Comp(p, static () => new PageHost()) with { Key = "zune-podcast-show" }] };

    static readonly PodcastShowView s_placeholder = Placeholder();

    static PodcastShowView Placeholder()
    {
        var rows = ImmutableArray.CreateBuilder<PodcastEpisodeVm>();
        for (int i = 0; i < 8; i++)
            rows.Add(new PodcastEpisodeVm("seed:" + i, "Episode title for a placeholder row", "A line of description for the placeholder row.", 0, false,
                "12 Nov", 0, "1 hr 10 min", "", 0f, PodcastRowState.Unplayed, false, false, false, true, 0, false, null));
        var head = PodcastShowView.Empty.Head with
        {
            Title = "Podcast title", Kind = "Podcast", Publisher = "Publisher", Cadence = "Weekly", Count = "100 episodes", About = "About this show.",
        };
        var smart = new PodcastSmart(PodcastSmartKind.PlayNewest, "seed:0", "Play newest", "New", "Episode title", "12 Nov · 1 hr", -1f, true, []);
        var groups = ImmutableArray.Create(new PodcastGroup("y", "2026", 8, false, [0, 1, 2, 3, 4, 5, 6, 7]));
        var items = ImmutableArray.CreateBuilder<PodcastListItem>();
        items.Add(new PodcastListItem(PodcastItemKind.Group, 0, -1));
        for (int i = 0; i < 8; i++) items.Add(new PodcastListItem(PodcastItemKind.Row, 0, i, i == 0));
        return PodcastShowView.Empty with
        {
            Head = head, Smart = smart, Episodes = rows.ToImmutable(), Groups = groups, Items = items.ToImmutable(), ShownCount = 8,
            Filters = [new PodcastFilterVm(PodcastFilter.All, "All", 0), new PodcastFilterVm(PodcastFilter.Unplayed, "Unplayed", 0),
                new PodcastFilterVm(PodcastFilter.InProgress, "In progress", 0), new PodcastFilterVm(PodcastFilter.Saved, "Saved", 0)],
        };
    }

    /// <summary>The pivots.</summary>
    enum Pivot { Episodes, About, More }

    sealed record HeadView(PodcastLoad Load, PodcastShowHead Head, PodcastSmart? Smart, ImmutableArray<PodcastCardVm> Similar, string AllCount);

    sealed partial class PageHost : Component
    {
        PodcastShowPageProps _p = null!;
        readonly Signal<int> _pivot = new(0);
        Func<HeadView>? _project;
        Memo<HeadView>? _head;
        internal Action<int>? _select;
        ZunePodcastRowVerbs? _verbs;
        Func<bool>? _pending, _failed;
        readonly ScrollHandle _scroll = new();

        internal PodcastShowPageProps Props => _p;
        internal ZunePodcastRowVerbs Verbs => _verbs!;
        internal ScrollHandle Scroll => _scroll;
        internal readonly ListState State = new();
        internal IOverlayService? OverlaySvc;

        public override Element Render()
        {
            _p = UseProps<PodcastShowPageProps>();
            var p = _p;
            var facts = TierContext.DrawnIn(UseContext(ThemeFactsContext.Signal)).Value.Tiers;
            var lay = ZunePodcastTiers.Of(in facts);
            float gutter = ZuneTiers.GutterOf(in facts);
            _select ??= i => _pivot.Value = i;
            _pending ??= () => _p.View.Value.Load == PodcastLoad.Pending;
            _failed ??= () => _p.View.Value.Load == PodcastLoad.Failed;
            OverlaySvc = UseContext(Overlay.Service);
            _verbs ??= new ZunePodcastRowVerbs
            {
                Expanded = p.Expanded, Playing = p.Playing, Play = p.Play, Open = p.Open, PlayNext = p.PlayNext, Expand = p.Expand, Menu = p.RowMenu,
                Drag = p.RowDrag,
            };
            _project ??= () =>
            {
                var v = _p.View.Value;
                string all = v.Filters.Length > 0 ? v.Filters[0].CountText : "";
                return new HeadView(v.Load, v.Head, v.Smart, v.Similar, all);
            };
            _head = UseComputed(_project);
            UseListMemos();
            var h = _head.Value;
            bool loading = h.Load == PodcastLoad.Pending;
            var shown = loading ? new HeadView(PodcastLoad.Pending, s_placeholder.Head, s_placeholder.Smart, [], "") : h;
            int pivot = _pivot.Value;
            if (pivot == (int)Pivot.More && shown.Similar.IsDefaultOrEmpty) pivot = (int)Pivot.Episodes;

            var pivots = ImmutableArray.CreateBuilder<K.PivotItem>(3);
            pivots.Add(new K.PivotItem(Loc.Get(Strings.Podcast.Pages.Episodes), shown.AllCount));
            pivots.Add(new K.PivotItem(Loc.Get(Strings.Podcast.Pages.About), ""));
            if (!shown.Similar.IsDefaultOrEmpty) pivots.Add(new K.PivotItem(Loc.Get(Strings.Podcast.Pages.MoreLikeThis), ""));

            var pivotItems = pivots.ToImmutable();
            float lead = ZuneFrameRules.ColumnLead(facts.ContentWidth, gutter);
            _layS.SetIfChanged(lay);
            _leadS.SetIfChanged(lead);
            _pivotItems = pivotItems;
            _pivotNow = pivot;
            var placeholder = new HeadView(PodcastLoad.Pending, s_placeholder.Head, s_placeholder.Smart, [], "");
            return new SkelRegionEl(
                Pending: _pending, Failed: _failed,
                Content: () => LivePage(),
                ShimmerSource: () => K.Page(p.RouteKey, new BoxEl
                {
                    Key = "zune-show-column", Direction = 1, MinWidth = 0f,
                    Children =
                    [
                        K.Title(placeholder.Head.Title, lay, gutter),
                        K.Pivots("zp-pivots", pivotItems, 0, _select, lay, gutter),
                        Compose(placeholder, lay, 0, live: false),
                    ],
                }, gutter),
                OnFailed: () => Controls.Vacancy(Controls.VacancyVoice.Error), Reveal: SkelReveal.Soft, Style: SkeletonStyle.Default with { DeriveComponents = true },
                Group: null, SmoothResize: false);
        }

        ImmutableArray<K.PivotItem> _pivotItems = [];
        int _pivotNow;

        Element Compose(HeadView h, ZunePodcastLayout lay, int pivot, bool live)
        {
            Element body = (Pivot)pivot switch
            {
                Pivot.About => K.Body("zp-body:about", About(h, lay)),
                Pivot.More => K.Body("zp-body:more", K.Cards("zp-more", h.Similar, _p.OpenUri, lay.Stacked, _p.CardMenu, _p.PlayCard)),
                _ => K.Body("zp-body:episodes", ListColumn(this, s_placeholder, lay, live: false)),
            };
            return new BoxEl { Direction = 1, MinWidth = 0f, Children = [Hero(h, lay, live), body] };
        }

        // ── the hero ──

        internal Element Hero(HeadView h, ZunePodcastLayout lay, bool live)
        {
            var head = h.Head;
            Element art = new BoxEl
            {
                Key = "zp-art", Width = lay.Art, Height = lay.Art, Shrink = 0f, AlignSelf = FlexAlign.Start, Layout = ZuneMotion.GroupResize,
                Children = [Controls.Artwork(head.ArtUrl, lay.Art, lay.Art, 0f, decodePx: (int)(lay.Art * 2f))],
            };
            var s = h.Smart;
            var main = new List<Element>(5);
            // Caught up: the up line says it (the label would say it twice).
            if (s is { Kind: not PodcastSmartKind.CaughtUp }) main.Add(K.Label(LabelOf(s)));
            main.Add(Up(s, lay, live));
            main.Add(Meta(head, lay.Target));
            main.Add(Commands(head, lay));
            var words = new BoxEl { Key = "zp-hmain", Direction = 1, Gap = Z.HeroLineGap, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f, Children = main.ToArray() };
            return new BoxEl
            {
                Key = "zp-hero", Direction = (byte)(lay.Stacked ? 1 : 0), Gap = lay.Stacked ? Z.HeroRowGap : Z.HeroColGap, MinWidth = 0f,
                AlignItems = lay.Stacked ? FlexAlign.Stretch : FlexAlign.Start, Margin = new Edges4(0f, 0f, 0f, Z.HeroBottom), Layout = ZuneMotion.GroupResize,
                Children = [art, words],
            };
        }

        static string LabelOf(PodcastSmart s) => s.Kind switch
        {
            PodcastSmartKind.Resume => Loc.Get(Strings.Podcast.Pages.Continue),
            PodcastSmartKind.PlayNewest => Loc.Get(Strings.Podcast.Pages.Fresh),
            PodcastSmartKind.CaughtUp => Loc.Get(Strings.Podcast.Pages.CaughtUp),
            _ => s.Why.Length > 0 ? s.Why : Loc.Get(Strings.Podcast.Pages.NextInOrder),
        };

        Element Up(PodcastSmart? s, ZunePodcastLayout lay, bool live)
        {
            if (s is null) return new BoxEl { Key = "zp-up", MinHeight = lay.Target };
            bool pausing = live && _p.Playing.Value is { } id && id == s.EpisodeId;
            Element button = s.Kind == PodcastSmartKind.CaughtUp
                ? K.Button("smart", pausing ? Loc.Get(Strings.Podcast.Pages.Pause) : s.Label, pausing ? ZunePlayerLayout.PauseSolid : Icons.Refresh, _p.PlaySmart,
                    lay.Target, fill: lay.PrimaryFills)
                : K.Split("smart", s.Label, s.Accent, pausing, _p.PlaySmart, s.Menu, lay.Target, lay.PrimaryFills);
            // What it plays: "up next · Title" (20/28 light), then the line and the time left (or the date and length, the parts, the caught-up line).
            // "up next ·" is the theme's word (lowered by the label role); the episode's title is a name, as written.
            TextEl UpWord(string text) => new(text)
            {
                FontFamily = R.Type.Pivot("").FontFamily, Size = Z.UpTitleSize, LineHeight = Z.UpTitleLine, Weight = Z.Light,
                MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
            };
            Element line = s.Kind == PodcastSmartKind.CaughtUp
                // The caught-up line is the theme's sentence, not a name: lowercased as a label.
                ? new TextEl(s.EpisodeTitle)
                {
                    FontFamily = R.Type.Pivot("").FontFamily, Size = Z.UpTitleSize, LineHeight = Z.UpTitleLine, Weight = Z.Light, Color = Tok.TextPrimary,
                    Case = R.Case.For(LabelKind.Label), MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                }
                : new BoxEl
                {
                    Direction = 0, AlignItems = FlexAlign.Center, MinWidth = 0f,
                    Children = s.Why.Length > 0
                        ? [UpWord(s.Why + K.Dot) with { Color = Tok.TextTertiary, Case = R.Case.For(LabelKind.Label), Shrink = 0f },
                           UpWord(s.EpisodeTitle) with { Color = Tok.TextPrimary, Shrink = 1f }]
                        : [UpWord(s.EpisodeTitle) with { Color = Tok.TextPrimary, Shrink = 1f }],
                };
            var sub = new List<Element>(2);
            float progress = s.Progress;
            if (progress >= 0f) sub.Add(K.Line(() => progress, Z.UpBarW));
            sub.Add(R.Type.TrackMeta(s.Sub) with { Color = Tok.TextSecondary, Case = R.Case.For(LabelKind.Label), MaxLines = 1, Trim = TextTrim.CharacterEllipsis, Shrink = 1f, MinWidth = 0f });
            var label = new BoxEl
            {
                Direction = 1, Gap = Z.UpLineGap, Grow = 1f, Basis = 0f, MinWidth = 0f,
                Children = [line, new BoxEl { Direction = 0, Gap = Z.UpSubGap, AlignItems = FlexAlign.Center, MinWidth = 0f, Children = sub.ToArray() }],
            };
            return new BoxEl
            {
                Key = "zp-up", Direction = (byte)(lay.Stacked ? 1 : 0), Gap = lay.Stacked ? Z.HeroLineGap : Z.UpGap, MinWidth = 0f,
                AlignItems = lay.Stacked ? FlexAlign.Stretch : FlexAlign.Center,
                Children = [new BoxEl { Direction = 0, Shrink = 0f, MinWidth = 0f, Children = [button] }, label],
            };
        }

        /// <summary>publisher · ★ rating (count) · cadence · N episodes · serial: the kind word links to its hub; each part carries its own "·".</summary>
        Element Meta(PodcastShowHead h, float target)
        {
            var parts = new List<Element>(6);
            if (h.Publisher.Length > 0) parts.Add(K.MetaWord(h.Publisher) with { Case = TextCase.AsWritten, Trim = TextTrim.CharacterEllipsis, Shrink = 1f, MinWidth = 0f });
            if (h.Rating.Length > 0)
                parts.Add(new BoxEl
                {
                    Direction = 0, Gap = Z.MetaGap, AlignItems = FlexAlign.Center, Shrink = 0f,
                    Children =
                    [
                        Icon(Icons.FavoriteStarFill, Z.MetaStar, Tok.TextSecondary),
                        K.MetaWord(h.Rating),
                        K.MetaWord(h.RatingCount) with { Color = Tok.TextTertiary },
                    ],
                });
            if (h.Cadence.Length > 0) parts.Add(K.MetaWord(h.Cadence));
            if (h.Count.Length > 0) parts.Add(K.MetaWord(h.Count));
            if (h.Serial) parts.Add(K.MetaWord(Loc.Get(Strings.Podcast.Pages.Serial)));
            if (h.Kind.Length > 0)
                parts.Add(_p.OpenKind is { } kind ? K.Link("zp-kind", h.Kind, kind, 0f) : K.MetaWord(h.Kind));
            // The topics: each a link that searches its words (names: as written); the ones that do not fit leave the line whole.
            if (h.Topics.Length > 0) parts.Add(K.WordLinks("zp-topic", h.Topics, _p.OpenTopic, target, leadDot: parts.Count > 0));
            return K.Meta("zp-meta", parts);
        }

        Element Commands(PodcastShowHead h, ZunePodcastLayout lay)
        {
            float t = lay.Target;
            var kids = new List<Element>(4)
            {
                K.Button("cmd:follow", Loc.Get(h.Following ? Strings.Podcast.Pages.Following : Strings.Podcast.Pages.Follow), h.Following ? Icons.Check : Icons.Add,
                    _p.ToggleFollow, t, on: h.Following),
            };
            if (h.Following)
                kids.Add(K.Named(K.Button("cmd:bell", null, Icons.Bell, null, t), Loc.Get(Strings.Podcast.Pages.NotifyUnavailable), false));
            if (_p.Share is { } share)
                kids.Add(K.Named(K.Button("cmd:share", lay.Words ? Loc.Get(Strings.Podcast.Pages.Share) : null, Icons.Share, share, t),
                    Loc.Get(Strings.Podcast.Pages.Share), lay.Words));
            kids.Add(K.More("cmd:more", _p.ShowMenu, t));
            return new BoxEl { Key = "zp-cmds", Direction = 0, Gap = Z.CommandGap, Wrap = true, AlignItems = FlexAlign.Center, MinWidth = 0f, Children = kids.ToArray() };
        }

        // ── about ──

        internal Element About(HeadView v, ZunePodcastLayout lay)
        {
            var h = v.Head;
            var kids = new List<Element>(5);
            if (h.About.Length > 0 || h.AboutHtml.Length > 0)
                kids.Add(new BoxEl
                {
                    Direction = 1, MinWidth = 0f, MaxWidth = Z.AboutMax,
                    Children = [Controls.RichTextFlex(h.AboutHtml.Length > 0 ? h.AboutHtml : h.About, Z.BodySize, Tok.TextSecondary, R.Ink.AccentDecor, 0, DetailVerbs.NavigateKey)],
                });
            if (h.TrailerId is not null)
                kids.Add(new BoxEl
                {
                    Key = "about-trailer", Direction = 0, Gap = Z.RowGap, AlignItems = FlexAlign.Center, MinWidth = 0f,
                    Children =
                    [
                        K.Named(K.Button("about:trailer", null, ZunePlayerLayout.PlaySolid, _p.PlayTrailer, lay.Target), Loc.Get(Strings.Podcast.Pages.PlayTrailer), false),
                        new BoxEl
                        {
                            Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f,
                            Children =
                            [
                                R.Type.TrackTitle(Loc.Get(Strings.Podcast.Pages.Trailer)) with { Case = R.Case.For(LabelKind.Label) },
                                R.Type.TrackMeta(h.TrailerSub) with { MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f },
                            ],
                        },
                    ],
                });
            if (h.Rating.Length > 0)
            {
                var stars = new Element[5];
                int full = (h.RatingX100 + 50) / 100;
                for (int i = 0; i < stars.Length; i++) stars[i] = Icon(Icons.FavoriteStarFill, Z.RatingStar, i < full ? Tok.TextPrimary : Tok.TextDisabled);
                var state = _p.RateState;
                kids.Add(new BoxEl
                {
                    Key = "about-rating", Direction = 0, Gap = Z.SectionHeadGap, AlignItems = FlexAlign.Center, MinWidth = 0f, Wrap = true,
                    Children =
                    [
                        R.Type.Heading(h.Rating) with { Shrink = 0f },
                        new BoxEl
                        {
                            Direction = 1, Shrink = 0f,
                            Children = [new BoxEl { Direction = 0, Children = stars }, K.MetaWord(h.Ratings) with { Text = Prop.Of(() => state.Value.Line) }],
                        },
                        // Rate opens the shared picker (the listener's stars, busy, the failure, why it is off); the line keeps the listener's own at rest.
                        Controls.RateFlyout("about:rate-flyout", K.Button("about:rate", Loc.Get(Strings.Podcast.Pages.Rate), null, static () => { }, lay.Target),
                            state, _p.Rate, Z.RatingStar, static () => R.Ink.AccentDecor),
                    ],
                });
            }
            if (h.Facts.Length > 0) kids.Add(K.Facts("about-facts", h.Facts));
            return new BoxEl { Key = "zp-about", Direction = 1, Gap = Z.SectionGap, MinWidth = 0f, MaxWidth = Z.AboutMax, Children = kids.ToArray() };
        }
    }

    // ══ THE EPISODES PIVOT ══════════════════════════════════════════════════════════════════════════════════════════════════

    sealed class ListState
    {
        public readonly Signal<bool> ToolbarStuck = new(false), SearchOpen = new(false);
        public readonly Dictionary<string, NodeHandle> GroupNodes = [];
        public OverlayHandle? Jump;
    }

    static Element ListColumn(PageHost host, PodcastShowView v, ZunePodcastLayout lay, bool live)
    {
        var st = host.State;
        var p = host.Props;
        float groupsTop = Z.ToolbarH;
        var kids = new List<Element>(v.Items.Length + 1);
        var groupRows = new List<Element>();
        string? groupKey = null;
        Element? groupHead = null;
        void Flush()
        {
            if (groupHead is null) return;
            kids.Add(new BoxEl
            {
                Key = "g:" + groupKey, Direction = 1, MinWidth = 0f, Layout = ZuneDetailKit.Shove,
                Children = [groupHead.Value, new BoxEl { Key = "rows", Direction = 1, MinWidth = 0f, Children = groupRows.ToArray() }.StickyClip(groupsTop + Z.GroupH)],
            });
            groupRows.Clear();
            groupHead = null;
        }
        foreach (var it in v.Items)
        {
            switch (it.Kind)
            {
                case PodcastItemKind.Group:
                    Flush();
                    var g = v.Groups[it.Group];
                    groupKey = g.Key;
                    groupHead = GroupHeader(host, st, v.Years, g, live);
                    break;
                case PodcastItemKind.Row:
                    groupRows.Add(K.EpisodeRow(v.Episodes[it.Episode], lay.RowDescription, lay.Stacked, host.Verbs));
                    break;
                case PodcastItemKind.Empty:
                    Flush();
                    kids.Add(new BoxEl
                    {
                        Key = "show-empty", Direction = 0, Gap = Z.CommandGap, AlignItems = FlexAlign.Center, Wrap = true, Padding = new Edges4(0f, Z.EmptyPad, 0f, Z.EmptyPad),
                        Children =
                        [
                            R.Type.TrackTitle(Loc.Get(Strings.Podcast.Pages.NoMatch)) with { Color = Tok.TextSecondary, Case = R.Case.For(LabelKind.Label) },
                            K.Link("show-clear", Loc.Get(Strings.Podcast.Pages.ClearFilter), p.ClearFilter, lay.Target),
                        ],
                    });
                    break;
                case PodcastItemKind.More:
                    Flush();
                    kids.Add(new BoxEl
                    {
                        Key = "show-more", Direction = 0, Padding = new Edges4(0f, Z.MoreTop, 0f, 0f),
                        Children = [K.Button("load-more", Loc.Get(Strings.Podcast.Pages.LoadMore), null, p.LoadMore, lay.Target)],
                    });
                    break;
            }
        }
        Flush();
        Element body = new BoxEl { Key = "show-groups", Direction = 1, MinWidth = 0f, Children = kids.ToArray() }.StickyClip(groupsTop);
        Element toolbar = new BoxEl
        {
            Key = "show-toolbar", Direction = 0, Height = Z.ToolbarH, Gap = Z.CommandGap, AlignItems = FlexAlign.Center, MinWidth = 0f,
            Children = Toolbar(host, st, v, lay, live),
        }.Sticky(0f, null, st.ToolbarStuck);
        return new BoxEl { Key = "show-list", Direction = 1, MinWidth = 0f, Children = [toolbar, body] };
    }

    static readonly string[] s_noSuggest = [];
    static readonly string[] s_filterKeys = ["filter:all", "filter:unplayed", "filter:progress", "filter:saved"];

    static Element[] Toolbar(PageHost host, ListState st, PodcastShowView v, ZunePodcastLayout lay, bool live)
    {
        var p = host.Props;
        var kids = new List<Element>(4);
        bool searching = !lay.SearchPill && live && st.SearchOpen.Value;
        if (searching)
            kids.Add(Swap("tb:search-field", true, AutoSuggestBox.Create(s_noSuggest, Loc.Get(Strings.Theme.Zune.Detail.SearchShow), width: Z.SearchW, text: p.Query(),
                queryIcon: Icons.Search, grow: 1f)));
        else
        {
            var filter = live ? p.Filter.Value : PodcastFilter.All;
            var words = new Element[v.Filters.Length];
            for (int i = 0; i < words.Length; i++)
            {
                var f = v.Filters[i];
                bool on = f.Filter == filter;
                var which = f.Filter;
                var wordKids = new List<Element>(2)
                {
                    new TextEl(f.Label)
                    {
                        FontFamily = R.Type.Pivot("").FontFamily, Size = Z.FilterSize, LineHeight = Z.FilterLine, Weight = Z.Light, Case = R.Case.For(LabelKind.Navigation),
                        Color = on ? Tok.TextPrimary : Tok.TextTertiary, HoverColor = Tok.TextPrimary, MaxLines = 1, Wrap = TextWrap.NoWrap, Shrink = 0f,
                    },
                };
                if (f.CountText.Length > 0)
                    wordKids.Add(new TextEl(f.CountText)
                    {
                        Size = Z.CountSize, LineHeight = Z.CountLine, Color = Tok.TextTertiary, Shrink = 0f, AlignSelf = FlexAlign.Start, Margin = new Edges4(0f, Z.FilterCountLift, 0f, 0f),
                    });
                words[i] = new BoxEl
                {
                    Key = s_filterKeys[Math.Min(i, s_filterKeys.Length - 1)], Direction = 0, Gap = Z.CountGap, AlignItems = FlexAlign.Center, MinHeight = lay.Target, Shrink = 0f,
                    Role = AutomationRole.Tab, Focusable = true, AllowFocusOnInteraction = false, Cursor = on ? CursorId.Arrow : CursorId.Hand,
                    OnClick = on ? null : () => p.SetFilter(which), Children = wordKids.ToArray(),
                };
            }
            kids.Add(Swap("tb:filters", true, new BoxEl
            {
                Direction = 0, Gap = Z.FilterGap, AlignItems = FlexAlign.Center, Grow = 1f, Basis = 0f, MinWidth = 0f, ClipToBounds = true,
                EdgeFade = new EdgeFadeSpec(EdgeMask.Right, 0f, 0f, Z.PivotFade, 0f), Children = words,
            }));
        }
        if (lay.SearchPill)
            kids.Add(Swap("tb:find", false, new BoxEl
            {
                Key = "tb:find-box", Shrink = 0f, MinWidth = 0f,
                Children = [AutoSuggestBox.Create(s_noSuggest, Loc.Get(Strings.Theme.Zune.Detail.SearchShow), width: Z.SearchW, text: p.Query(), queryIcon: Icons.Search)],
            }));
        else
            kids.Add(K.Named(K.Button("tb:search", null, searching ? Icons.ChromeClose : Icons.Search, () => st.SearchOpen.Value = !st.SearchOpen.Peek(), lay.Target)
                with { BorderWidth = 0f }, Loc.Get(Strings.Podcast.Pages.Search), false));
        bool oldest = live && p.Oldest.Value;
        string sortWord = Loc.Get(oldest ? Strings.Podcast.Pages.OldestFirst : Strings.Podcast.Pages.NewestFirst);
        kids.Add(K.Named(K.Button("tb:sort", null, Icons.Sort, p.ToggleSort, lay.Target) with { BorderWidth = 0f, Layout = ZuneMotion.GroupResize }, sortWord, false));
        return kids.ToArray();
    }

    /// <summary>A part only some tiers draw: keyed, faded in and out as a declared swap (a crossing never rebuilds it in place).</summary>
    static BoxEl Swap(string key, bool fill, Element body) => new()
    {
        Key = key, Direction = 0, AlignItems = FlexAlign.Center, Grow = fill ? 1f : 0f, Basis = fill ? 0f : float.NaN, Shrink = fill ? 1f : 0f, MinWidth = 0f,
        // A crossing that brings or drops a part beside it (the search pill) re-sizes it: it glides on the pivot slide's clock.
        Enter = ZuneMotion.SwapFade, Exit = ZuneMotion.SwapFade, Layout = ZuneMotion.GroupResize, Children = [body],
    };

    static Element GroupHeader(PageHost host, ListState st, ImmutableArray<PodcastYear> years, PodcastGroup g, bool live)
    {
        var p = host.Props;
        bool jumps = years.Length > 1;
        var words = new List<Element>(3) { ZuneDetailKit.Heading(g.Title) with { Color = Tok.TextTertiary, HoverColor = jumps ? Tok.TextPrimary : Tok.TextTertiary, Shrink = 1f } };
        if (g.CountText.Length > 0)
            words.Add(new TextEl(g.CountText) { Size = Z.CountSize, LineHeight = Z.CountLine, Color = Tok.TextTertiary, Shrink = 0f, AlignSelf = FlexAlign.Start, Margin = new Edges4(0f, Z.GroupCountLift, 0f, 0f) });
        if (jumps) words.Add(Icon(Icons.ChevronDown, Z.GroupGlyph, Tok.TextTertiary));
        string key = g.Key;
        var kids = new List<Element>(3)
        {
            new BoxEl
            {
                Key = "gh:jump:" + key, Direction = 0, Gap = Z.GroupGap, AlignItems = FlexAlign.Center, MinHeight = Z.GroupH, Shrink = 1f, MinWidth = 0f,
                Role = AutomationRole.Button, Focusable = jumps, Cursor = jumps ? CursorId.Hand : CursorId.Arrow, AllowFocusOnInteraction = false,
                OnRealized = live ? h => st.GroupNodes["head:" + key] = h : null,
                OnClick = jumps && live ? () => OpenJump(host, st, years, key) : null,
                Children = words.ToArray(),
            },
            new BoxEl { Grow = 1f },
        };
        if (g.Fresh) kids.Add(K.Link("gh:mark", Loc.Get(Strings.Podcast.Pages.MarkAllPlayed), p.MarkFreshPlayed, Z.GroupH));
        return new BoxEl { Key = "gh", Direction = 0, Height = Z.GroupH, AlignItems = FlexAlign.Center, MinWidth = 0f, Children = kids.ToArray() };
    }

    static void OpenJump(PageHost host, ListState st, ImmutableArray<PodcastYear> years, string groupKey)
    {
        if (st.Jump is { IsOpen: true } open) { open.Close(); return; }
        var overlay = host.OverlaySvc;
        if (Controls.IsNullOverlay(overlay) || !st.GroupNodes.TryGetValue("head:" + groupKey, out var anchor)) return;
        OverlayHandle? opened = null;
        opened = overlay!.Open(() => anchor, () => JumpGrid(host, st, years, () => opened?.Close()), FlyoutPlacement.BottomEdgeAlignedLeft,
            new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss) { ConstrainToRootBounds = false });
        st.Jump = opened;
    }

    static Element JumpGrid(PageHost host, ListState st, ImmutableArray<PodcastYear> years, Action close)
    {
        var rows = new List<Element>();
        for (int i = 0; i < years.Length; i += Z.JumpCols)
        {
            var cells = new Element[Z.JumpCols];
            for (int c = 0; c < Z.JumpCols; c++)
            {
                if (i + c >= years.Length) { cells[c] = new BoxEl { Grow = 1f, Basis = 0f }; continue; }
                var y = years[i + c];
                cells[c] = new BoxEl
                {
                    Key = "jump:" + y.Label, Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Height = Z.JumpCellH, Justify = FlexJustify.Center,
                    Padding = new Edges4(Z.JumpPad, 0f, Z.JumpPad, 0f), Fill = Prop.Of(static () => Tok.TextPrimary with { A = Z.RowHoverAlpha }),
                    HoverFill = Prop.Of(static () => Tok.AccentDefault), PressedFill = Prop.Of(static () => Tok.AccentTertiary),
                    Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand,
                    OnClick = () => { close(); JumpTo(host, st, "y" + y.Label); },
                    Children = [R.Type.Numeral(y.Label), R.Type.TrackMeta(y.Count) with { Case = R.Case.For(LabelKind.Label) }],
                };
            }
            rows.Add(new BoxEl { Direction = 0, Gap = Z.JumpGap, Children = cells });
        }
        return new BoxEl
        {
            Key = "show-jump", Direction = 1, Gap = Z.JumpGap, Width = Z.JumpW, Padding = Edges4.All(Z.JumpPad),
            Children = [K.Heading(Loc.Get(Strings.Podcast.Pages.JumpTo)), .. rows],
        };
    }

    static void JumpTo(PageHost host, ListState st, string groupKey) => host.JumpToGroup(groupKey);
}
