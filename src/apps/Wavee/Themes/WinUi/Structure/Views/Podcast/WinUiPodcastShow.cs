// ── Themes/WinUi/Structure/Views/Podcast/WinUiPodcastShow.cs — WinUI's show page (podcast pages v2, concept winui-podcast-v2 "Show") ──────────────────
//
//   ONE virtualised list (WinUiPodcastShow.List.cs: the hero, About, the toolbar, then the groups' headers and rows), the pinned group header and
//   the details pane as layers over it, the 48 bar over everything:
//      ├─ hero        160 cover · eyebrow (Podcast · topics · Video / Serial tags) · Title Large · meta on one line (publisher · ★ rating (count) ·
//      │              cadence · N episodes) · top right: Following · bell · Share · More · the up row: the smart SplitButton + what it plays
//      ├─ About the show (Small and Compact: an Expander above the list; Standard: the pane's second tab)
//      └─ the list column: toolbar (pinned under the bar): SelectorBar filters with counts (Compact: a ComboBox) · search · sort; groups: a header
//         (opens the Jump to grid; "New since your last visit" offers Mark all as played) + rows (the kit's bound EpisodeRow); Load more ·
//         the 360 details pane (Standard): Episode | About the show
//
// The tier record is `WinUiPodcastShowTiers` (the CONTENT tier: a docked pane narrows it). Loading is the live list page over a placeholder view
// (`s_placeholder`), derived flat by the SkelRegionEl with its components (WinUiPodcastShow.List.cs `ShowList`).
// The toolbar pins under the bar with nothing painted at rest and a hairline while pinned (the kit's PinnedBand / PinnedLine); what scrolls under it cuts
// itself at its lower edge. The pane pins under the bar; taller than the viewport, it rides the page until its foot reaches the viewport's foot and pins
// there (no scroller of its own). A filter or a search re-forms the list's items.

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
using K = Wavee.Themes.WinUi.WinUiPodcastKitRules;
using S = Wavee.Themes.WinUi.WinUiPodcastShowRules;

namespace Wavee.Themes.WinUi;

/// <summary>WinUI's show page.</summary>
internal static partial class WinUiPodcastShow
{
    public static Element Template(PodcastShowPageProps p, in ViewContext ctx) => Embed.Comp(p, static () => new PageHost());

    /// <summary>The loading view: the loaded layout's shape with placeholder words (the shimmer is the list page over it, derived). Two of them: the
    /// library already knows whether the show is followed, which decides whether About the show starts open (641-1007 and below).</summary>
    static readonly PodcastShowView s_placeholder = Placeholder(following: false), s_placeholderFollowing = Placeholder(following: true);

    static PodcastShowView Placeholder(bool following)
    {
        var rows = ImmutableArray.CreateBuilder<PodcastEpisodeVm>();
        for (int i = 0; i < 8; i++)
            rows.Add(new PodcastEpisodeVm("seed:" + i, "Episode title for a placeholder row", "", 0, false, "12 Nov", 0, "1 hr 10 min", "", 0f,
                PodcastRowState.Unplayed, false, false, false, true, 0, false, null));
        var head = PodcastShowView.Empty.Head with
        {
            // The meta line holds two words: a derived "·" is a 32 bar, so a longer line wrapped at Compact (the loaded line does not) and the shimmer
            // stood 15 taller than the page that lands.
            Title = "Podcast title", Kind = "Podcast", Publisher = "Publisher", Cadence = "Weekly", About = "About this show.",
            Following = following,
        };
        var smart = new PodcastSmart(PodcastSmartKind.PlayNewest, "seed:0", "Play newest", "New", "Episode title", "12 Nov · 1 hr", -1f, true, []);
        var items = ImmutableArray.CreateBuilder<PodcastListItem>(rows.Count + 1);
        items.Add(new PodcastListItem(PodcastItemKind.Group, 0, -1));
        for (int i = 0; i < rows.Count; i++) items.Add(new PodcastListItem(PodcastItemKind.Row, 0, i, i == 0));
        return PodcastShowView.Empty with
        {
            Head = head, Smart = smart, Episodes = rows.ToImmutable(), Items = items.MoveToImmutable(),
            Groups = [new PodcastGroup("y", "2026", 8, false, [0, 1, 2, 3, 4, 5, 6, 7])],
            Filters = [new PodcastFilterVm(PodcastFilter.All, "All", 0), new PodcastFilterVm(PodcastFilter.Unplayed, "Unplayed", 0),
                new PodcastFilterVm(PodcastFilter.InProgress, "In progress", 0), new PodcastFilterVm(PodcastFilter.Saved, "Saved", 0)],
        };
    }

    sealed partial class PageHost : Component
    {
        PodcastShowPageProps _p = null!;
        readonly Signal<int> _filterIx = new(0), _paneIx = new(0), _comboIx = new(0);
        readonly Signal<bool> _searchOpen = new(false), _toolbarStuck = new(false);
        readonly Signal<float> _paneH = new(0f);
        Action<RectF>? _measurePane;
        Func<HeadView>? _projectHead;
        Memo<HeadView>? _headView;
        float _paneShift;
        double _paneLastOffset = double.NaN;
        Action<bool>? _followToggle;
        string[] _filterLabels = [];
        string[]? _paneLabels;
        readonly ScrollHandle _scroll = new();
        PodcastRowVerbs? _verbs;
        IOverlayService? _overlay;
        OverlayHandle? _jump;
        Action? _syncCells;
        Func<bool>? _pending, _failed;
        Func<PodcastShowView>? _placeholderOf;

        /// <summary>Drawn inside another page's column (the library's): the head is the show's head there, not the page's title.</summary>
        bool _embedded;
        /// <summary>Opens the show's own page from another page's column (null on its own route): the cover and the title are a way there, and the
        /// command row ends on the "View details" link.</summary>
        Action? _openFull;

        public override Element Render()
        {
            _p = UseProps<PodcastShowPageProps>();
            var p = _p;
            _embedded = UseContext(PodcastPagePlace.Embedded);
            _openFull = UseContext(PodcastPagePlace.OpenFull);
            var facts = TierContext.DrawnIn(UseContext(ThemeFactsContext.Signal)).Value.Tiers;
            var lay = WinUiPodcastShowTiers.Of(in facts);
            float inset = WinUiTiers.InsetOf(in facts);
            float contentW = facts.ContentWidth;
            _overlay = UseContext(Overlay.Service);
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
                Selected = p.Selected, Playing = p.Playing, Play = p.Play, Open = p.Open, ToggleSaved = p.ToggleSaved,
                Select = p.Select, Menu = p.RowMenu, Drag = p.RowDrag,
            };

            _projectHead ??= () => HeadOf(_p.View.Value);
            _headView = UseComputed(_projectHead);
            var v = ViewOf(_headView.Value);
            _live ??= new ShowList(this, p.View, live: true);
            _placeholderOf ??= () => _p.View.Value.Head.Following ? s_placeholderFollowing : s_placeholder;
            var placeholder = UseComputed(_placeholderOf);
            _shim ??= new ShowList(this, placeholder, live: false);
            _barOnOf ??= () => _live!.HeroH.Value > 0f && _scroll.Offset.Value >= _live.HeroH.Value - K.BarH;
            _barOnList = UseComputed(_barOnOf);
            UsePinnedHead();
            PublishLayout(lay, inset, MathF.Max(0f, contentW - 2f * inset - (lay.Pane ? S.PaneW + S.ColGap : 0f)));
            var head = v.Head;
            Element bar = WinUiPodcastKit.Bar(head.ArtUrl, head.Title,
                v.Smart is { Kind: PodcastSmartKind.CaughtUp } ? Loc.Get(Strings.Podcast.Pages.CaughtUp) : v.Smart?.EpisodeTitle ?? "",
                BarButton(v), inset, captionShown: !lay.Stacked);
            // The shimmer IS the live list page over the placeholder view: its slot components mount over that view and the region derives their output.
            Element content = new SkelRegionEl(
                Pending: _pending, Failed: _failed, Content: () => LivePage(lay, inset, bar, head.Tone), ShimmerSource: () => ShimmerPage(lay, inset),
                OnFailed: () => Controls.Vacancy(Controls.VacancyVoice.Error), Reveal: SkelReveal.Soft, Style: SkeletonStyle.Default with { DeriveComponents = true }, Group: null,
                SmoothResize: false);
            return new BoxEl { Key = "podcast-show-body", Direction = 1, Grow = 1f, MinWidth = 0f, ClipToBounds = true, Children = [content] };
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
            var p = _p;
            string sid = s.EpisodeId;
            // Play/Pause is a leaf: a press re-renders the button, not the page.
            return WinUiPodcastPlayLeaf.Of("bar:play:leaf", () => p.Playing.Value is { } id && id == sid, pausing => Button.Create(
                pausing ? Loc.Get(Strings.Podcast.Pages.Pause) : s.Label, p.PlaySmart, s.Accent ? ButtonAppearance.Accent : ButtonAppearance.Standard,
                glyph: pausing ? WinUiDetailRules.PauseSolid : WinUiDetailRules.PlaySolid) with { Key = "bar:play", Shrink = 0f });
        }

        // ══ THE PAGE ════════════════════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>The details pane: its own component over the selected episode (its filter flag set aside), the head and the similar shows, so a filter
        /// switch does not re-render it.</summary>
        sealed record PaneProps(ShowList List);

        sealed record PaneView(PodcastLoad Load, PodcastEpisodeVm? Selected, PodcastShowHead Head, ImmutableArray<PodcastCardVm> Similar);

        sealed class PaneHost : Component
        {
            ShowList? _list;
            Func<PaneView>? _project;

            public override Element Render()
            {
                _list = UseProps<PaneProps>().List;
                _project ??= Project;
                var pv = UseComputed(_project).Value;
                var v = PodcastShowView.Empty with
                {
                    Load = pv.Load, Head = pv.Head, Similar = pv.Similar, Episodes = pv.Selected is { } vm ? [vm] : [],
                };
                return _list.Host.Pane(v, _list.Live);
            }

            PaneView Project()
            {
                var list = _list!;
                var full = list.View.Value;
                string? id = list.Live ? list.Host._p.Selected.Value : null;
                PodcastEpisodeVm? vm = null;
                if (id is not null)
                    foreach (var e in full.Episodes)
                        if (e.Id == id) { vm = e; break; }
                return new PaneView(full.Load, vm, full.Head, full.Similar);
            }
        }

        // ── the hero ──

        Element Hero(PodcastShowView v, WinUiPodcastShowLayout lay, bool live)
        {
            var h = v.Head;
            // Kept while scrolled away: the cover coming back into view at the top must not re-acquire its image (a scroll frame never allocates).
            // The cover drags as the show (the host's one source: a sidebar pin, a playlist, the queue); the shimmer's carries none.
            Element cover = new BoxEl
            {
                Key = "hero:art", Shrink = 0f, Draggable = live ? _p.CoverDrag : null,
                OnClick = live ? _openFull : null, Cursor = live && _openFull is not null ? CursorId.Hand : null,
                Children = [Controls.Artwork(h.ArtUrl, lay.Cover, lay.Cover, lay.CoverRadius, decodePx: (int)(lay.Cover * 2f), keepWhileHidden: true)],
            };
            Element title = (lay.TitleLarge ? TitleLarge(h.Title) : Title(h.Title)) with
            {
                Wrap = TextWrap.Wrap, MaxLines = S.TitleLines, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
            };
            var idKids = new List<Element>(3)
            {
                Eyebrow(h, lay),
                // In another page's column the title opens the show's own page (a plain box: no plate, no tint, the hand says it).
                live && _openFull is { } openTitle
                    ? new BoxEl
                    {
                        Key = "hero:title-link", Direction = 1, MinWidth = 0f, OnClick = openTitle, Cursor = CursorId.Hand, Role = AutomationRole.Hyperlink,
                        Fill = ColorF.Transparent, HoverFill = ColorF.Transparent, PressedFill = ColorF.Transparent, Children = [title],
                    }
                    : title,
                Meta(h),
            };
            Element id = new BoxEl { Direction = 1, Gap = S.IdGap, Grow = 1f, Basis = 0f, MinWidth = 0f, Children = idKids.ToArray() };
            Element commands = Commands(v, lay, live);
            Element up = Up(v, lay, live);
            if (live && !lay.Stacked && _openFull is { } openFull)
                up = new BoxEl
                {
                    Key = "show-up-row", Direction = 0, Gap = S.UpGap, AlignItems = FlexAlign.End, MinWidth = 0f,
                    Children = [new BoxEl { Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Children = [up] }, WinUiPodcastKit.ViewDetails(openFull)],
                };
            Element form = lay.Stacked ? Stacked() : Beside();
            // The head is the page's title for the lint guide, except in another page's column, where the page's title is that page's.
            return new BoxEl { Key = _embedded ? "podcast-head" : WinUiFrameLayout.PageTitleKey, Direction = 1, MinWidth = 0f, Layout = WinUiDiscoveryMotion.Reflow, Children = [form] };

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

        /// <summary>The eyebrow: the kind word as a link to its hub, then the topics as links (each searches its words), then the tags.</summary>
        Element Eyebrow(PodcastShowHead h, WinUiPodcastShowLayout lay)
        {
            var kids = new List<Element>(4);
            if (_p.OpenKind is { } openKind && h.Kind.Length > 0)
                kids.Add(new BoxEl
                {
                    Key = "eyebrow:kind", Shrink = 0f, Margin = new Edges4(-S.LinkPadX, 0f, -S.LinkPadX, 0f),
                    Children = [HyperlinkButton.Create(h.Kind, openKind, size: ControlSize.Small)],
                });
            else if (h.Kind.Length > 0) kids.Add(Caption(h.Kind) with { Color = Tok.TextSecondary });
            var tags = new List<Element>(4);
            if (h.Video) tags.Add(Tag(Loc.Get(Strings.Podcast.Pages.Video), Icons.Video));
            if (h.Serial) tags.Add(Tag(Loc.Get(Strings.Podcast.Pages.Serial), null));
            if (h.Exclusive) tags.Add(Tag(Loc.Get(Strings.Podcast.Pages.Exclusive), null));
            if (h.Explicit) tags.Add(WinUiRowCells.ExplicitMark());
            // The topics give way a whole word at a time; the tags ride with them, right after the last topic that shows.
            if (h.Topics.Length > 0) kids.Add(WinUiPodcastKit.WordLinks("eyebrow:topic", h.Topics, _p.OpenTopic, lay.Target, tags.ToArray()));
            else kids.AddRange(tags);
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

        /// <summary>publisher · ★ rating (count) · cadence · N episodes on ONE line (the audiobook hero's rule): the rating stays whole, the publisher
        /// and the tail ("· Monthly · 12 episodes", its separators inside the run) end in an ellipsis where the line is short, so no line ever starts or
        /// ends on a separator.</summary>
        static Element Meta(PodcastShowHead h)
        {
            var kids = new List<Element>(3);
            if (h.Publisher.Length > 0)
                kids.Add(Body(h.Publisher) with { MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, Shrink = 1f, MinWidth = 0f });
            if (h.Rating.Length > 0)
                kids.Add(new BoxEl
                {
                    Direction = 0, Gap = S.TagGap, AlignItems = FlexAlign.Center, Shrink = 0f,
                    Children =
                    [
                        .. kids.Count > 0 ? new Element[] { Body(Sep) with { Color = Tok.TextTertiary } } : [],
                        Icon(Icons.FavoriteStarFill, S.MetaStar, Tok.TextSecondary),
                        Body(h.Rating) with { Color = Tok.TextSecondary },
                        Body(h.RatingCount) with { Color = Tok.TextTertiary },
                    ],
                });
            string tail = h.Cadence.Length > 0 && h.Count.Length > 0 ? h.Cadence + " " + Sep + " " + h.Count : h.Cadence + h.Count;
            if (tail.Length > 0)
                kids.Add(Body(kids.Count > 0 ? Sep + " " + tail : tail) with
                {
                    Color = Tok.TextSecondary, MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, Shrink = 1f, MinWidth = 0f,
                });
            return new BoxEl { Direction = 0, Gap = S.EyebrowGap, AlignItems = FlexAlign.Center, MinWidth = 0f, Children = kids.ToArray() };
        }

        const string Sep = "·";

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
            // In another page's column: the one plain way to the show's own page (the album column's "Open album"). Stacked, the commands have a
            // line of their own and it ends them; beside the cover they share the head's width with the words, so it ends the up-next row instead (Hero).
            if (live && lay.Stacked && _openFull is { } open) kids.Add(WinUiPodcastKit.ViewDetails(open));
            return new BoxEl { Key = "show-commands", Direction = 0, Gap = S.CommandGap, AlignItems = FlexAlign.Center, Shrink = 0f, Children = kids.ToArray() };
        }

        Element Up(PodcastShowView v, WinUiPodcastShowLayout lay, bool live)
        {
            var s = v.Smart;
            if (s is null) return new BoxEl { Key = "show-up", MinHeight = S.UpMinH };
            var p = _p;
            string sid = s.EpisodeId;
            // Play/Pause is a leaf: a press re-renders the button, not the page.
            Element button = WinUiPodcastPlayLeaf.Of("smart:leaf", () => live && p.Playing.Value is { } id && id == sid, pausing => s.Kind == PodcastSmartKind.CaughtUp
                ? Button.Create(pausing ? Loc.Get(Strings.Podcast.Pages.Pause) : s.Label, p.PlaySmart, glyph: pausing ? WinUiDetailRules.PauseSolid : Icons.Refresh)
                    with { Key = "smart", Height = lay.Target, Shrink = 0f, Grow = lay.Stacked ? 1f : 0f }
                : WinUiPodcastKit.SmartSplit("smart", s.Label, s.Accent, pausing, p.PlaySmart, s.Menu, lay.Target, fill: lay.Stacked));
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
            // Rate opens the shared picker (the listener's stars, busy, the failure, why it is off); the line keeps the listener's own stars at rest.
            Element rate = Controls.RateFlyout("about:rate", Button.Create(Loc.Get(Strings.Podcast.Pages.Rate), static () => { }), _p.RateState, _p.Rate, S.RatingStar);
            var state = _p.RateState;
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
                        Children =
                        [
                            new BoxEl { Direction = 0, Children = stars },
                            Caption(h.Ratings) with { Text = Prop.Of(() => state.Value.Line), MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f },
                        ],
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

        // ── the list column's toolbar, its group headers and the jump list ──

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
                kids.Add(Controls.Named(Controls.IconAction(Icons.Search, () => _searchOpen.Value = !_searchOpen.Peek(), size: lay.Target) with { Key = "tb:search", Layout = WinUiDiscoveryMotion.Reflow },
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

        /// <summary>A group header's row: the title (the jump list's trigger where there are years to jump to) with its count, and "Mark all as played"
        /// on "New since your last visit".</summary>
        /// <remarks>A month's header says its year where the year opens in the list and wherever it is <paramref name="pinned"/> (the list scrolled
        /// away from the header that said it); the other groups say their count. <paramref name="realized"/> / <paramref name="chip"/>: the
        /// button's own node, kept by the slot that mounts it: the Jump to list opens under THIS header (a header keyed by its group stood twice,
        /// in the list and pinned, and the list opened on whichever mounted last, even after it had gone).</remarks>
        Element GroupHeadRow(PodcastGroup g, ImmutableArray<PodcastYear> years, WinUiPodcastShowLayout lay, bool live, bool pinned,
            Action<NodeHandle> realized, Func<NodeHandle> chip)
        {
            bool jumps = years.Length > 1;
            var headKids = new List<Element>(3);
            var buttonKids = new List<Element>(3) { BodyStrong(g.Title) };
            string beside = g.YearText.Length > 0 ? (pinned || g.OpensYear ? g.YearText : "") : g.CountText;
            if (beside.Length > 0) buttonKids.Add(Caption(beside) with { Color = Tok.TextTertiary });
            if (jumps) buttonKids.Add(Icon(Icons.ChevronDown, K.SplitChevron, Tok.TextSecondary));
            string key = g.Key;
            headKids.Add(new BoxEl
            {
                Key = "gh:jump:" + g.Key, Direction = 0, Gap = S.CommandGap, AlignItems = FlexAlign.Center, Height = lay.GroupButtonH, Shrink = 0f,
                // Its height steps at Compact (the 40 target): that eases; its width (the count) does not animate.
                Layout = WinUiDiscoveryMotion.HeightReflow,
                Padding = new Edges4(S.GroupButtonPad, 0f, S.GroupButtonPad, 0f), Margin = new Edges4(-S.GroupButtonPad, 0f, 0f, 0f),
                Corners = Radii.ControlAll, Fill = Tok.FillSubtleTransparent, HoverFill = jumps ? Tok.FillSubtleSecondary : Tok.FillSubtleTransparent,
                PressedFill = jumps ? Tok.FillSubtleTertiary : Tok.FillSubtleTransparent, Role = AutomationRole.Button, Focusable = jumps,
                OnRealized = live ? realized : null,
                OnClick = jumps && live ? () => OpenJump(years, chip) : null,
                Children = buttonKids.ToArray(),
            });
            headKids.Add(new BoxEl { Grow = 1f });
            if (g.Fresh) headKids.Add(HyperlinkButton.Create(Loc.Get(Strings.Podcast.Pages.MarkAllPlayed), _p.MarkFreshPlayed));
            return new BoxEl { Direction = 0, Height = lay.GroupH, AlignItems = FlexAlign.Center, MinWidth = 0f, Children = headKids.ToArray() };
        }

        void OpenJump(ImmutableArray<PodcastYear> years, Func<NodeHandle> anchor)
        {
            if (_jump is { IsOpen: true } open) { open.Close(); return; }
            if (Controls.IsNullOverlay(_overlay) || anchor().IsNull) return;
            OverlayHandle? opened = null;
            opened = _overlay!.Open(anchor, () => JumpGrid(years, () => opened?.Close()), FlyoutPlacement.BottomEdgeAlignedLeft,
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
                        OnClick = () => { close(); JumpToYear(y.Year); },
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

        // ── the details pane (≥1008) ──

        Element Pane(PodcastShowView v, bool live)
        {
            var tab = live ? _p.PaneTab.Value : PodcastPaneTab.Episode;
            string[] labels = _paneLabels ??= [Loc.Get(Strings.Podcast.Pages.PaneEpisode), Loc.Get(Strings.Podcast.Pages.PaneAbout)];
            Element body = tab == PodcastPaneTab.About ? AboutBody(v, rows: true) : EpisodePreview(v, live);
            return new BoxEl
            {
                // Its height eases (a chapter list landing after the episode, a tab or a selection of another length): the card's foot never steps.
                Key = "show-pane", Enter = WinUiDiscoveryMotion.SwapFade, Exit = WinUiDiscoveryMotion.SwapFade, Direction = 1, Width = S.PaneW, Shrink = 0f, MinWidth = 0f,
                Layout = WinUiDiscoveryMotion.HeightReflow, ClipToBounds = true,
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
