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
internal static partial class ClassicPodcastShow
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
            Count = "100 episodes", About = "About this show, a few lines long so the column keeps its shape while the show is on its way, the way a real one runs.",
            TrailerId = "seed:trailer", TrailerSub = "2 min", Facts = [new PodcastFact("Order", "Newest first"), new PodcastFact("Topics", "Topic, topic")],
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

    sealed partial class PageHost : Component
    {
        PodcastShowPageProps _p = null!;
        Func<HeadView>? _project;
        Memo<HeadView>? _head;
        Func<bool>? _pending, _failed;
        readonly ScrollHandle _scroll = new();

        public override Element Render()
        {
            _p = UseProps<PodcastShowPageProps>();
            var p = _p;
            var facts = TierContext.DrawnIn(UseContext(ThemeFactsContext.Signal)).Value.Tiers;
            var lay = ClassicPodcastTiers.Of(in facts);
            _project ??= () => HeadOf(_p.View.Value);
            _pending ??= () => _p.View.Value.Load == PodcastLoad.Pending;
            _failed ??= () => _p.View.Value.Load == PodcastLoad.Failed;
            _head = UseComputed(_project);
            var hv = _head.Value;
            bool loading = hv.Load == PodcastLoad.Pending;
            uint tone = loading ? 0u : hv.Head.Tone;
            ColorF accent = ClassicPodcastKit.Accent(tone);
            UseList(lay, tone, accent);
            Element list = ListColumn();
            return ClassicPodcastKit.ListPage(p.RouteKey, lay, tone, lay.Stacked ? null : ColumnRegion(hv, lay, accent), list);
        }

        /// <summary>The cover column behind its skeleton boundary (the loading form is the same column over the placeholder), keyed as a declared swap: it
        /// stands in the column frame from Small up and in the list's first slot at Compact.</summary>
        Element ColumnRegion(HeadView hv, ClassicPodcastLayout lay, ColorF accent)
        {
            var shown = hv.Load == PodcastLoad.Pending ? HeadOf(s_placeholder) : hv;
            Element region = new SkelRegionEl(
                Pending: _pending!, Failed: _failed!, Content: () => Column(shown, lay, accent, live: true),
                ShimmerSource: () => Column(HeadOf(s_placeholder), lay, accent, live: false),
                OnFailed: () => Controls.Vacancy(Controls.VacancyVoice.Error), Reveal: SkelReveal.Soft, Style: SkeletonStyle.Default with { DeriveComponents = true },
                Group: null, SmoothResize: true);
            return new BoxEl
            {
                Key = lay.Stacked ? "cp-show-column:stacked" : "cp-show-column", Direction = 1, Gap = P.ColumnGap, MinWidth = 0f,
                Enter = ClassicTierMotion.SwapFade, Exit = ClassicTierMotion.SwapFade, Children = [region],
            };
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
            if (h.Rating.Length > 0) words.Add(Rating(h, lay, accent));
            string counts = h.Count.Length > 0 && h.Cadence.Length > 0 ? h.Count + P.Dot + h.Cadence : h.Count + h.Cadence;
            if (counts.Length > 0) words.Add(ClassicPodcastKit.Line(new TextSpan(counts)));
            kids.Add(new BoxEl { Key = "cp-id", Direction = 1, Gap = P.FactsRowGap, MinWidth = 0f, Children = words.ToArray() });
            if (v.Ledger.Played + v.Ledger.InProgress + v.Ledger.ToGo + v.Ledger.New > 0) kids.Add(ClassicPodcastKit.Ledger(v.Ledger, accent));
            kids.Add(Up(v, lay, accent, live));
            kids.Add(Commands(h, lay));
            if (h.AboutHtml.Length > 0 || h.About.Length > 0) kids.Add(ClassicPodcastKit.Description(h.AboutHtml.Length > 0 ? h.AboutHtml : h.About, "cp-about"));
            if (h.TrailerId is not null) kids.Add(Trailer(h));
            var facts = new List<PodcastFact>(2);
            var values = new List<Element?>(2);
            string topicsLabel = Loc.Get(Strings.Podcast.Pages.Topics);
            foreach (var f in h.Facts)
            {
                bool topics = f.Label == topicsLabel;
                if (!topics && f.Label != Loc.Get(Strings.Podcast.Pages.Order)) continue;
                facts.Add(f);
                // The topics as links: each searches its words.
                values.Add(topics && _p.OpenTopic is { } open && h.Topics.Length > 0 ? ClassicPodcastKit.Links("cp-topic", h.Topics, open, lay.Target) : null);
            }
            if (facts.Count > 0) kids.Add(ClassicPodcastKit.Facts([.. facts], "cp-facts", values.ToArray()));
            return new BoxEl { Direction = 1, Gap = P.ColumnGap, MinWidth = 0f, Children = kids.ToArray() };
        }

        /// <summary>"4.8 ★ · 12,431 ratings · yours 5★" (the listener's own stars at rest) and Rate, which opens the shared picker.</summary>
        Element Rating(PodcastShowHead h, ClassicPodcastLayout lay, ColorF accent)
        {
            var state = _p.RateState;
            string line = state.Value.Line;
            return new BoxEl
            {
                Key = "cp-rating", Direction = 0, Gap = P.CommandGap, AlignItems = FlexAlign.Center, Wrap = true, MinWidth = 0f,
                Children =
                [
                    ClassicPodcastKit.Line(ClassicPodcastKit.Strong(h.Rating), new TextSpan(line.Length > 0 ? " ★" + P.Dot + line : " ★")) with
                    {
                        Grow = 1f, Shrink = 1f, Basis = 0f,
                    },
                    Controls.RateFlyout("cp-rate", Button.Create(Loc.Get(Strings.Podcast.Pages.Rate), static () => { }) with { Height = lay.Target, MinWidth = 0f },
                        state, _p.Rate, P.RateStar, () => Design.Palette.ReadableInk(accent)),
                ],
            };
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

        // Keyed by the trailer episode: its Play acts on the trailer (the lint's subject), not on the show the smart split plays from.
        Element Trailer(PodcastShowHead h) => new BoxEl
        {
            Key = "cp-trailer:" + h.TrailerId, Direction = 0, Gap = P.TrailerGap, AlignItems = FlexAlign.Center, MinWidth = 0f,
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
            var b = Button.Create(label, _toggle, ButtonAppearance.Subtle) with { Key = "tb:filter-button", Height = P.TargetCompact, MinWidth = 0f, Shrink = 1f,
                Margin = new Edges4(-P.FilterPadX, 0f, 0f, 0f) };
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
