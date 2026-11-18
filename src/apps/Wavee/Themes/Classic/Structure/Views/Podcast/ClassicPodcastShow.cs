// ── Themes/Classic/Structure/Views/Podcast/ClassicPodcastShow.cs — Classic's show page (podcast pages v2, concept "Show", theme=classic) ──────────────
//
//   ClassicPodcastKit.Page
//   ├─ the cover column (stays while the list scrolls; stacked over the list at Compact)
//   │    cover · Podcast (its hub) [Video] [Serial] · title · publisher · ★ rating · N ratings / N episodes · cadence · the listened bar and its words ·
//   │    the full-width primary in the cover's colour (its menu on the chevron) and what it plays under it · Following · bell · Share · ⋯ ·
//   │    About (rich text, Read more) · the trailer · Topics / Order
//   └─ the list column
//        the list bar (pinned; a backing only while pinned): the filter words with their counts · search · sort
//        groups: a lowercase label (pinned under the bar; opens the Jump to grid; "new since your last visit" offers Mark all as played) + rows
//        (expand in place; at Compact a row opens its episode) · Load more · More like this
//
// The tier record is `ClassicPodcastLayout` (window tier). Loading is the same column over a placeholder snapshot (SkelRegionEl, flat fills).

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

/// <summary>Classic's show page.</summary>
internal static class ClassicPodcastShow
{
    public static Element Template(PodcastShowPageProps p, in ViewContext ctx) => Embed.Comp(p, static () => new PageHost());

    static readonly PodcastShowView s_placeholder = Placeholder();

    static PodcastShowView Placeholder()
    {
        var rows = ImmutableArray.CreateBuilder<PodcastEpisodeVm>();
        for (int i = 0; i < 8; i++)
            rows.Add(new PodcastEpisodeVm("seed:" + i, "Episode title for a placeholder row", "A line of description for a placeholder row", 0, false,
                "12 Nov", 0, "1 hr 10 min", "", 0f, PodcastRowState.Unplayed, false, false, false, true, 0, false, null));
        var head = PodcastShowView.Empty.Head with
        {
            Title = "Podcast title", Kind = "Podcast", Publisher = "Publisher", Rating = "4.8", Ratings = "1,000 ratings", Cadence = "Weekly",
            Count = "100 episodes", About = "About this show, a few lines long so the column keeps its shape while the show is on its way.",
        };
        var smart = new PodcastSmart(PodcastSmartKind.PlayNewest, "seed:0", "Play newest", "New", "Episode title", "12 Nov · 1 hr", -1f, true, []);
        return PodcastShowView.Empty with
        {
            Head = head, Smart = smart, Episodes = rows.ToImmutable(),
            Groups = [new PodcastGroup("y", "2026", 8, false, [0, 1, 2, 3, 4, 5, 6, 7])],
            Filters = [new PodcastFilterVm(PodcastFilter.All, "All", 0), new PodcastFilterVm(PodcastFilter.Unplayed, "Unplayed", 0),
                new PodcastFilterVm(PodcastFilter.InProgress, "In progress", 0), new PodcastFilterVm(PodcastFilter.Saved, "Saved", 0)],
            Ledger = new PodcastLedger(4, 1, 3, 0, "4 played · 1 in progress · 3 to go", ""),
        };
    }

    sealed record HeadView(PodcastLoad Load, PodcastShowHead Head, PodcastSmart? Smart, ImmutableArray<PodcastCardVm> Similar, PodcastLedger Ledger);

    static HeadView HeadOf(PodcastShowView v) => new(v.Load, v.Head, v.Smart, v.Similar, v.Ledger);

    sealed class PageHost : Component
    {
        PodcastShowPageProps _p = null!;
        Func<HeadView>? _project;
        Func<bool>? _pending, _failed;
        readonly ScrollHandle _scroll = new();
        NodeHandle _viewport;
        Action<NodeHandle>? _capture;
        Func<NodeHandle>? _viewportOf;

        public override Element Render()
        {
            _p = UseProps<PodcastShowPageProps>();
            _capture ??= h => _viewport = h;
            _viewportOf ??= () => _viewport;
            var p = _p;
            var facts = TierContext.DrawnIn(UseContext(ThemeFactsContext.Signal)).Value.Tiers;
            var lay = ClassicPodcastTiers.Of(in facts);
            _project ??= () => HeadOf(_p.View.Value);
            _pending ??= () => _p.View.Value.Load == PodcastLoad.Pending;
            _failed ??= () => _p.View.Value.Load == PodcastLoad.Failed;
            var hv = UseComputed(_project).Value;
            bool loading = hv.Load == PodcastLoad.Pending;
            var shown = loading ? HeadOf(s_placeholder) : hv;
            uint tone = loading ? 0u : shown.Head.Tone;
            ColorF accent = ClassicPodcastKit.Accent(tone);

            Element column = new SkelRegionEl(
                Pending: _pending, Failed: _failed, Content: () => Column(shown, lay, accent, live: true),
                ShimmerSource: () => Column(HeadOf(s_placeholder), lay, accent, live: false),
                OnFailed: () => Controls.Vacancy(Controls.VacancyVoice.Error), Reveal: SkelReveal.Soft, Style: SkeletonStyle.Default with { DeriveComponents = true },
                Group: null, SmoothResize: false);
            Element main = Embed.Comp(new ListProps(p, lay, accent, tone, hv.Similar, _viewportOf), static () => new ListHost()) with { Key = "cp-show-list" };
            return ClassicPodcastKit.Page(p.RouteKey, lay, tone, new BoxEl { Key = "cp-show-column", Direction = 1, Gap = P.ColumnGap, MinWidth = 0f, Children = [column] },
                main, _scroll, _capture);
        }

        // ── the cover column ──

        Element Column(HeadView v, ClassicPodcastLayout lay, ColorF accent, bool live)
        {
            var h = v.Head;
            var kids = new List<Element>(12)
            {
                new BoxEl
                {
                    Key = "cp-cover", Shrink = 0f, AlignSelf = FlexAlign.Start, Layout = ClassicTierMotion.Reflow,
                    Children = [Controls.Artwork(h.ArtUrl, lay.Cover, lay.Cover, P.CoverRadius, decodePx: P.CoverDecode)],
                },
            };
            var tags = new List<Element>(2);
            if (h.Video) tags.Add(ClassicPodcastKit.Tag(Loc.Get(Strings.Podcast.Pages.Video), Icons.Video));
            if (h.Serial) tags.Add(ClassicPodcastKit.Tag(Loc.Get(Strings.Podcast.Pages.Serial), null));
            var words = new List<Element>(4)
            {
                ClassicPodcastKit.Kind(h.Kind, live ? _p.OpenKind : null, [.. tags]),
                ClassicPodcastKit.Title(h.Title),
            };
            if (h.Publisher.Length > 0) words.Add(ClassicPodcastKit.Sub(h.Publisher));
            var line = new List<TextSpan>(6);
            if (h.Rating.Length > 0)
            {
                line.Add(ClassicPodcastKit.Strong(h.Rating));
                line.Add(new TextSpan(" ★"));
                if (h.Ratings.Length > 0) line.Add(new TextSpan(P.Dot + h.Ratings));
                line.Add(new TextSpan("\n"));
            }
            string counts = h.Count.Length > 0 && h.Cadence.Length > 0 ? h.Count + P.Dot + h.Cadence : h.Count + h.Cadence;
            if (counts.Length > 0) line.Add(new TextSpan(counts));
            if (line.Count > 0) words.Add(ClassicPodcastKit.Line([.. line]));
            kids.Add(new BoxEl { Key = "cp-id", Direction = 1, Gap = P.FactsRowGap, MinWidth = 0f, Children = words.ToArray() });
            if (v.Ledger.Played + v.Ledger.InProgress + v.Ledger.ToGo + v.Ledger.New > 0) kids.Add(ClassicPodcastKit.Ledger(v.Ledger, accent));
            kids.Add(Up(v, lay, accent, live));
            kids.Add(Commands(h, lay));
            if (h.AboutHtml.Length > 0 || h.About.Length > 0) kids.Add(ClassicPodcastKit.Description(h.AboutHtml.Length > 0 ? h.AboutHtml : h.About, "cp-about"));
            if (h.TrailerId is not null) kids.Add(Trailer(h));
            var facts = new List<PodcastFact>(2);
            foreach (var f in h.Facts)
                if (f.Label == Loc.Get(Strings.Podcast.Pages.Topics) || f.Label == Loc.Get(Strings.Podcast.Pages.Order)) facts.Add(f);
            if (facts.Count > 0) kids.Add(ClassicPodcastKit.Facts([.. facts], "cp-facts"));
            return new BoxEl { Direction = 1, Gap = P.ColumnGap, MinWidth = 0f, Children = kids.ToArray() };
        }

        Element Up(HeadView v, ClassicPodcastLayout lay, ColorF accent, bool live)
        {
            var s = v.Smart;
            if (s is null) return new BoxEl { Key = "cp-up", Height = lay.Target };
            bool pausing = live && _p.Playing.Value is { } id && id == s.EpisodeId;
            string label = pausing ? Loc.Get(Strings.Podcast.Pages.Pause) : s.Label;
            string glyph = pausing ? Icons.Pause : s.Kind == PodcastSmartKind.CaughtUp ? Icons.Refresh : Icons.Play;
            return new BoxEl
            {
                Key = "cp-up", Direction = 1, Gap = P.UpGap, MinWidth = 0f,
                Children =
                [
                    ClassicPodcastKit.Primary("cp-smart", label, glyph, accent, s.Accent, _p.PlaySmart, s.Kind == PodcastSmartKind.CaughtUp ? [] : s.Menu, lay.Target),
                    ClassicPodcastKit.UpLabel(s.Why, s.EpisodeTitle, s.Sub, s.Progress, accent),
                ],
            };
        }

        Element Commands(PodcastShowHead h, ClassicPodcastLayout lay)
        {
            // Following is a state at rest: the check and the word while followed, Add and Follow while not (a standard button, not an accent).
            var kids = new List<Element>(4)
            {
                Button.Create(Loc.Get(h.Following ? Strings.Podcast.Pages.Following : Strings.Podcast.Pages.Follow), _p.ToggleFollow,
                    glyph: !lay.FollowGlyph ? null : h.Following ? Icons.Check : Icons.Add) with { Key = "cmd:follow", Height = lay.Target, Shrink = 0f, MinWidth = 0f },
            };
            if (h.Following)
                kids.Add(ClassicPodcastKit.Glyph("cmd:bell", Icons.Bell, Loc.Get(Strings.Podcast.Pages.NotifyUnavailable), null, lay.Target));
            if (_p.Share is { } share) kids.Add(ClassicPodcastKit.Glyph("cmd:share", Icons.Share, Loc.Get(Strings.Podcast.Pages.Share), share, lay.Target));
            kids.Add(new BoxEl { Key = "cmd:more", Shrink = 0f, Children = [ClassicDetailKit.MoreButton(_p.ShowMenu, lay.Target)] });
            return new BoxEl { Key = "cp-commands", Direction = 0, Wrap = true, Gap = P.CommandGap, AlignItems = FlexAlign.Center, MinWidth = 0f, Children = kids.ToArray() };
        }

        Element Trailer(PodcastShowHead h) => new BoxEl
        {
            Key = "cp-trailer", Direction = 0, Gap = P.TrailerGap, AlignItems = FlexAlign.Center, MinWidth = 0f,
            Children =
            [
                Controls.Named(Controls.IconAction(Icons.Play, _p.PlayTrailer, size: P.TrailerButton) with
                {
                    Corners = CornerRadius4.All(P.TrailerButton / 2f), Fill = Tok.FillControlDefault, HoverFill = Tok.FillControlSecondary, PressedFill = Tok.FillControlTertiary,
                }, Loc.Get(Strings.Podcast.Pages.PlayTrailer)),
                new BoxEl
                {
                    Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f,
                    Children = [Body(Loc.Get(Strings.Podcast.Pages.Trailer)), Caption(h.TrailerSub) with { Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, MaxLines = 2, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f }],
                },
            ],
        };
    }

    // ══ THE LIST COLUMN ═════════════════════════════════════════════════════════════════════════════════════════════════════

    sealed record ListProps(PodcastShowPageProps P, ClassicPodcastLayout Lay, ColorF Accent, uint Tone, ImmutableArray<PodcastCardVm> Similar,
        Func<NodeHandle> Viewport);

    sealed class ListHost : Component
    {
        ListProps _a = null!;
        readonly Signal<int> _filterIx = new(0);
        readonly Signal<bool> _barStuck = new(false), _searchOpen = new(false);
        readonly Dictionary<string, Signal<bool>> _groupStuck = [];
        readonly Dictionary<string, NodeHandle> _groupNodes = [];
        Controls.Words.Word[]? _words;
        ClassicRowVerbs? _verbs;
        IOverlayService? _overlay;
        OverlayHandle? _jump;
        Action? _sync;

        public override Element Render()
        {
            _a = UseProps<ListProps>();
            var a = _a;
            var p = a.P;
            _overlay = UseContext(Overlay.Service);
            _sync ??= () => _filterIx.SetIfChanged((int)_a.P.Filter.Value);
            UseEffect(_sync);
            _verbs ??= new ClassicRowVerbs
            {
                Expanded = p.Expanded, Playing = p.Playing, Play = p.Play, Open = p.Open, ToggleSaved = p.ToggleSaved, PlayNext = p.PlayNext, Expand = p.Expand,
                Menu = p.RowMenu,
            };
            var full = p.View.Value;
            var v = full.Load == PodcastLoad.Pending ? s_placeholder : full;
            var lay = a.Lay;
            var kids = new List<Element>(v.Groups.Length + 4) { ClassicPodcastKit.PinnedBar("cp-listbar", ListBar(v, lay), P.ListBarH, 0f, _barStuck, a.Tone) };
            foreach (var g in v.Groups) kids.Add(Group(v, g, lay));
            if (v.ShownCount == 0 && v.Load == PodcastLoad.Ready)
                kids.Add(new BoxEl
                {
                    Key = "cp-empty", Direction = 0, Gap = P.SectionGap, AlignItems = FlexAlign.Center, Wrap = true, Padding = new Edges4(0f, P.EmptyPad, 0f, P.EmptyPad),
                    Children = [Body(Loc.Get(Strings.Podcast.Pages.NoMatch)) with { Color = Tok.TextSecondary },
                        HyperlinkButton.Create(Loc.Get(Strings.Podcast.Pages.ClearFilter), p.ClearFilter)],
                });
            if (v.HasMore)
                kids.Add(new BoxEl
                {
                    Key = "cp-more", Direction = 0, Justify = FlexJustify.Center, Padding = new Edges4(0f, P.SectionBottom, 0f, P.SectionBottom),
                    Children = [Button.Create(Loc.Get(Strings.Podcast.Pages.LoadMore), p.LoadMore) with { Key = "load-more" }],
                });
            if (!a.Similar.IsDefaultOrEmpty)
            {
                kids.Add(ClassicPodcastKit.SectionHeader("cp-similar-head", Loc.Get(Strings.Podcast.Pages.MoreLikeThis)));
                kids.Add(ClassicPodcastKit.MoreLikeThis(a.Similar, p.OpenUri, lay.Stacked, p.CardMenu, p.PlayCard));
            }
            return new BoxEl { Key = "cp-list", Direction = 1, MinWidth = 0f, Children = kids.ToArray() };
        }

        Element ListBar(PodcastShowView v, ClassicPodcastLayout lay)
        {
            var p = _a.P;
            if (_words is null)
            {
                var filters = s_placeholder.Filters;
                _words = new Controls.Words.Word[filters.Length];
                for (int i = 0; i < _words.Length; i++)
                {
                    int ix = i;
                    _words[i] = new Controls.Words.Word(
                        Prop.Of(() => _a.P.View.Value.Filters is { Length: > 0 } f && ix < f.Length ? f[ix].Label : s_placeholder.Filters[ix].Label),
                        Prop.Of(() => _a.P.View.Value.Filters is { Length: > 0 } f && ix < f.Length ? f[ix].CountText : ""));
                }
            }
            var accent = _a.Accent;
            var kids = new List<Element>(4);
            bool searching = lay.Stacked && _searchOpen.Value;
            if (searching)
                kids.Add(Swap("tb:search-field", true, SearchBox(lay, fill: true)));
            else if (lay.FilterMenu)
                kids.Add(Swap("tb:filter-menu", true, Embed.Comp(_a.P, static () => new FilterMenu()) with { Key = "tb:filter-menu:host" }));
            else
                kids.Add(Swap("tb:filters", true, Controls.Words.Rail(_words, _filterIx, () => accent, onSelect: i => _a.P.SetFilter((PodcastFilter)i), fit: true)));
            if (lay.Stacked)
                kids.Add(Controls.Named(Controls.IconAction(searching ? Icons.Cancel : Icons.Search, () => _searchOpen.Value = !_searchOpen.Peek(), size: lay.Target) with { Key = "tb:search" },
                    Loc.Get(Strings.Podcast.Pages.Search)));
            else kids.Add(Swap("tb:find-box", false, SearchBox(lay, fill: false)));
            string sortWord = Loc.Get(p.Oldest.Value ? Strings.Podcast.Pages.OldestFirst : Strings.Podcast.Pages.NewestFirst);
            kids.Add(Controls.Named(Controls.IconAction(Icons.Sort, p.ToggleSort, size: lay.Target) with { Key = "tb:sort" }, sortWord));
            return new BoxEl
            {
                Key = "cp-listbar:row", Direction = 0, Gap = P.ListBarGap, AlignItems = FlexAlign.Center, Height = P.ListBarH, MinWidth = 0f, Children = kids.ToArray(),
            };
        }

        static BoxEl Swap(string key, bool fill, Element body) => new()
        {
            Key = key, Direction = 0, AlignItems = FlexAlign.Center, Grow = fill ? 1f : 0f, Basis = fill ? 0f : float.NaN, Shrink = fill ? 1f : 0f, MinWidth = 0f,
            Enter = ClassicTierMotion.SwapFade, Exit = ClassicTierMotion.SwapFade, Children = [body],
        };

        Element SearchBox(ClassicPodcastLayout lay, bool fill) => new BoxEl
        {
            Key = "tb:find", Shrink = 0f, Grow = fill ? 1f : 0f, MinWidth = 0f,
            Children = [AutoSuggestBox.Create(s_noSuggest, Loc.Get(Strings.Podcast.Pages.Search),
                width: fill ? P.SearchWSmall : lay.Tier == WidthTier.Small ? P.SearchWSmall : P.SearchW, text: _a.P.Query(), queryIcon: Icons.Search, grow: fill ? 1f : 0f)],
        };

        static readonly string[] s_noSuggest = [];

        Element Group(PodcastShowView v, PodcastGroup g, ClassicPodcastLayout lay)
        {
            var p = _a.P;
            bool jumps = v.Years.Length > 1;
            string key = g.Key;
            var years = v.Years;
            var labelKids = new List<Element>(3)
            {
                new TextEl(g.Title) { Size = P.GroupLabelSize, LineHeight = P.GroupLabelLine, Weight = P.Strong, Color = Tok.TextSecondary, Case = TextCase.Lower, Wrap = TextWrap.NoWrap },
            };
            if (g.CountText.Length > 0) labelKids.Add(new TextEl(g.CountText) { Size = P.GroupLabelSize, LineHeight = P.GroupLabelLine, Color = Tok.TextTertiary });
            if (jumps) labelKids.Add(Icon(Icons.ChevronDown, P.SplitChevron, Tok.TextTertiary));
            var headKids = new List<Element>(3)
            {
                new BoxEl
                {
                    Key = "gh:jump:" + key, Direction = 0, Gap = P.GroupGap, AlignItems = FlexAlign.Center, Height = P.FilterH, Shrink = 0f,
                    Padding = new Edges4(P.FilterPadX, 0f, P.FilterPadX, 0f), Margin = new Edges4(-P.FilterPadX, 0f, 0f, 0f), Corners = Radii.ControlAll,
                    Fill = Tok.FillSubtleTransparent, HoverFill = jumps ? Tok.FillSubtleSecondary : Tok.FillSubtleTransparent,
                    PressedFill = jumps ? Tok.FillSubtleTertiary : Tok.FillSubtleTransparent, Role = AutomationRole.Button, Focusable = jumps,
                    Cursor = jumps ? CursorId.Hand : CursorId.Arrow,
                    OnRealized = h => _groupNodes["head:" + key] = h, OnClick = jumps ? () => OpenJump(years, key) : null,
                    Children = labelKids.ToArray(),
                },
                new BoxEl { Grow = 1f },
            };
            if (g.Fresh) headKids.Add(HyperlinkButton.Create(Loc.Get(Strings.Podcast.Pages.MarkAllPlayed), p.MarkFreshPlayed) with { Key = "gh:mark" });
            if (!_groupStuck.TryGetValue(key, out var stuck)) _groupStuck[key] = stuck = new Signal<bool>(false);
            Element header = ClassicPodcastKit.PinnedBar("gh", new BoxEl
            {
                Direction = 0, Height = P.GroupH, AlignItems = FlexAlign.Center, MinWidth = 0f, Children = headKids.ToArray(),
            }, P.GroupH, P.ListBarH, stuck, _a.Tone);
            var rows = new Element[g.Rows.Length];
            for (int i = 0; i < rows.Length; i++)
            {
                var vm = v.Episodes[g.Rows[i]];
                rows[i] = ClassicPodcastKit.EpisodeRow(vm.Hidden ? vm with { Hidden = false } : vm, lay, lay.RowExpands, lay.RowDescription, _a.Accent, _verbs!)
                    with { Visible = !vm.Hidden };
            }
            return new BoxEl
            {
                Key = "g:" + key, Direction = 1, MinWidth = 0f, Layout = ClassicDetailKit.Shove, Visible = !g.Hidden, OnRealized = h => _groupNodes[key] = h,
                Children = [header, new BoxEl { Key = "rows", Direction = 1, MinWidth = 0f, Children = rows }],
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
            for (int i = 0; i < years.Length; i += P.JumpCols)
            {
                var cells = new Element[P.JumpCols];
                for (int c = 0; c < P.JumpCols; c++)
                {
                    if (i + c >= years.Length) { cells[c] = new BoxEl { Grow = 1f, Basis = 0f }; continue; }
                    var y = years[i + c];
                    cells[c] = new BoxEl
                    {
                        Key = "jump:" + y.Label, Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Height = P.JumpCellH, Justify = FlexJustify.Center,
                        Padding = new Edges4(P.JumpPad, 0f, P.JumpPad, 0f), Corners = Radii.ControlAll, Fill = Tok.FillControlDefault,
                        HoverFill = Tok.FillControlSecondary, PressedFill = Tok.FillControlTertiary, BorderWidth = 1f, BorderBrush = Tok.ControlElevationBorder,
                        Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand,
                        OnClick = () => { close(); JumpTo("y" + y.Label); },
                        Children = [Subtitle(y.Label), Caption(y.Count) with { Color = Tok.TextTertiary }],
                    };
                }
                rows.Add(new BoxEl { Direction = 0, Gap = P.JumpGap, Children = cells });
            }
            return new BoxEl
            {
                Key = "cp-jump", Direction = 1, Gap = P.JumpGap, Width = P.JumpW, Padding = Edges4.All(P.JumpPad),
                Children = [BodyStrong(Loc.Get(Strings.Podcast.Pages.JumpTo)), .. rows],
            };
        }

        void JumpTo(string groupKey)
        {
            var viewport = _a.Viewport();
            if (!_groupNodes.TryGetValue(groupKey, out var node) || viewport.IsNull || Context.Scene is not { } scene) return;
            scene.BringIntoView(viewport, node, align: 0f, Design.Reduced ? ScrollMove.Immediate : ScrollMove.Glide, margin: P.ListBarH);
        }
    }

    /// <summary>The filters at Compact: a 40-high button naming the filter and its count, opening the four as radio items (Compact targets are 40).</summary>
    sealed class FilterMenu : Component
    {
        IOverlayService? _overlay;
        NodeHandle _anchor;
        OverlayHandle? _menu;
        PodcastShowPageProps _p = null!;
        Action? _toggle;
        Action<NodeHandle>? _capture;

        public override Element Render()
        {
            _p = UseProps<PodcastShowPageProps>();
            _overlay = UseContext(Overlay.Service);
            _toggle ??= Toggle;
            _capture ??= h => _anchor = h;
            var v = _p.View.Value;
            var f = _p.Filter.Value;
            string label = "";
            foreach (var x in v.Filters)
                if (x.Filter == f) label = x.CountText.Length > 0 ? x.Label + P.Dot + x.CountText : x.Label;
            var b = Button.Create(label, _toggle, ButtonAppearance.Subtle) with { Key = "tb:filter-button", Height = P.TargetCompact, MinWidth = 0f, Shrink = 1f };
            return new BoxEl
            {
                Direction = 0, MinWidth = 0f, OnRealized = _capture,
                Children = [b with { Children = [.. b.Children, Icon(Icons.ChevronDown, P.SplitChevron, Tok.TextSecondary)] }],
            };
        }

        void Toggle()
        {
            if (_menu is { IsOpen: true } shown) { shown.Close(); return; }
            if (Controls.IsNullOverlay(_overlay)) return;
            var v = _p.View.Value;
            var f = _p.Filter.Peek();
            var items = new List<MenuFlyoutItem>(v.Filters.Length);
            foreach (var x in v.Filters)
            {
                var filter = x.Filter;
                items.Add(MenuFlyoutItem.RadioItem(x.Label, x.Filter == f, () => _p.SetFilter(filter)) with { AcceleratorText = x.CountText });
            }
            OverlayHandle? opened = null;
            opened = _overlay!.Open(() => _anchor, () => MenuFlyout.Create(items, () => opened?.Close(), size: OverlayFit.MenuSize),
                FlyoutPlacement.BottomEdgeAlignedLeft, new PopupOptions(FocusTrap: true) { ConstrainToRootBounds = false });
            _menu = opened;
        }
    }
}
