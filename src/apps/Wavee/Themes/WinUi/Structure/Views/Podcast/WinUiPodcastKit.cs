// ── Themes/WinUi/Structure/Views/Podcast/WinUiPodcastKit.cs — the parts WinUI's podcast pages v2 share (show, episode, audiobook) ────────────────────
//
// Concept `winui-podcast-v2.html` (theme=winui). The show lane owns this file; the episode and audiobook lanes reuse it. Its API is STABLE: additive changes
// only.
//
//   Page(...)          the page scaffold: ONE scroller (keyed per route) whose content is the cover-tone wash (232, fading out before the list) under the
//                      page column, and the 48 collapsed header bar OVER the scroller, shown once the page's BarSentinel (placed after the hero) pins
//                      under it: it fades and drops 8 in over 167 ms on reserved space (it never moves the layout).
//   BarSentinel(on)    a zero-height marker: put it right after the hero; `on` turns true when the hero has scrolled under the bar.
//   Bar(...)           the bar itself (WinUI's pinned detail bar, WinUiDetailHero.Bar): a 32 cover, the title (Body Strong) over a caption, the primary
//                      button at the end, on the card layer with a hairline at its foot. While it shows, the page's content cuts itself at its lower edge
//                      (a StickyClip engaged with the bar), so nothing scrolls visibly under it and no opaque band is needed.
//   PinnedBand(...)    a row that pins (a toolbar): it paints NOTHING of its own; `stuck` is Sticky's engaged edge. What scrolls under it must cut
//                      itself at the band's lower edge (`.StickyClip(line)` on that content), as WinUI's Home facet row and artist pivot do.
//   PinnedLine(...)    the hairline a pinned row shows at its foot while `stuck` (the row places it, so it can end where its column ends).
//   Description(...)   an HTML / plain description through the house rich-text renderer (paragraphs, lists, bold, links through the link policy).
//   PlayNextIcon       the app's play-next glyph (ActionIcons.PlayNext, the Wavee icon face), never Icons.Queue.
//   SmartSplit(...)    the accent SplitButton that names its episode: the main half plays, the chevron opens "Other ways to play" (PodcastMenuItem list,
//                      the default with a check); no accent when caught up; `fill` stretches the main half (Compact's full-width 40).
//   EpisodeRow(...)    the episode row: the play button (36 / 40) with the progress ring when in progress and the check for played (replay on hover); the
//                      title semibold when unplayed, regular secondary when played; the meta (New dot + word, date, length or the accent "N min left",
//                      Played, the Video tag); the saved glyph at rest, add on hover, the ellipsis always; one line of description when asked; a click
//                      selects (the details pane) or opens the episode's page.
//   SectionHeader(...) Subtitle + a caption + a trailing link.
//   MoreLikeThis(...)  the house media surface (`Controls.Surface`): shelf cards (hover plate, play, pressed, focus, menu, hand cursor), or, in a narrow
//                      column, the house row (`Shape.Row`).
//
// No numbers here: `WinUiPodcastKitRules` (Rules/WinUiPodcastRules.cs). No Entities / Playback: every verb comes in as a delegate.

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
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;
using K = Wavee.Themes.WinUi.WinUiPodcastKitRules;

namespace Wavee.Themes.WinUi;

/// <summary>The verbs and live cells an <see cref="WinUiPodcastKit.EpisodeRow"/> reads. Build ONE per page (the rows compare it by reference).</summary>
internal sealed class PodcastRowVerbs
{
    /// <summary>The selected / playing episode ids (null = none).</summary>
    public required IReadSignal<string?> Selected { get; init; }
    public required IReadSignal<string?> Playing { get; init; }
    public required Action<string> Play { get; init; }
    public required Action<string> Open { get; init; }
    public required Action<string> ToggleSaved { get; init; }
    public required Action<string?> Select { get; init; }
    public required Func<string, ContextMenuModel?> Menu { get; init; }
    /// <summary>A row's drag payload (the page props' <c>RowDrag</c>; null: the rows carry no drag).</summary>
    public Func<string, object?>? Drag { get; init; }
}

/// <summary>WinUI's podcast kit (see the file header).</summary>
internal static class WinUiPodcastKit
{
    // ══ 1. THE PAGE SCAFFOLD ════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The page: the scroller (key <c>podcast-scroll:</c> + <paramref name="routeKey"/>) over the wash and <paramref name="content"/> (a column
    /// that already carries the gutters), and <paramref name="bar"/> over it while <paramref name="barOn"/> holds. <paramref name="scroll"/> is the page's
    /// handle (the caller keeps it, e.g. to jump to a year); <paramref name="viewport"/> receives the scroller's node (for <c>scene.BringIntoView</c>).</summary>
    public static Element Page(string routeKey, uint tone, IReadSignal<bool> barOn, Element content, Element bar, ScrollHandle? scroll = null,
        Action<NodeHandle>? viewport = null)
    {
        var wash = K.Wash(tone);
        Element body = Embed.Comp(new ClipProps(barOn, wash, content), static () => new ClipHost());
        Element scroller = ScrollView(new BoxEl { Direction = 1, MinWidth = 0f, Children = [body] }) with
        {
            Key = "podcast-scroll:" + routeKey, Grow = 1f, ScrollKey = "podcast-scroll:" + routeKey, Handle = scroll,
            EdgeCues = ScrollEdgeCues.None, OnRealized = viewport,
        };
        Element barLayer = new BoxEl
        {
            Direction = 1, Height = K.BarH, AlignSelf = FlexAlign.Start, HitTestPassThrough = true,
            Children = [Flow.Show(() => barOn.Value, new BoxEl { Key = "podcast-bar", Direction = 1, Enter = K.BarFade, Exit = K.BarFade, Children = [bar] })],
        };
        return new BoxEl { Key = "podcast-page:" + routeKey, ZStack = true, Direction = 1, Grow = 1f, MinWidth = 0f, Children = [scroller, barLayer] };
    }

    /// <summary>The page body under the bar: the wash and the content, cut at the bar's lower edge only while the bar shows (an engaged StickyClip from the
    /// first frame would cut the hero's top at rest).</summary>
    sealed record ClipProps(IReadSignal<bool> BarOn, GradientSpec? Wash, Element Content);

    sealed class ClipHost : Component
    {
        public override Element Render()
        {
            var p = UseProps<ClipProps>();
            bool on = p.BarOn.Value;
            var body = new BoxEl
            {
                Key = "podcast-body", ZStack = true, Direction = 1, MinWidth = 0f,
                Children =
                [
                    new BoxEl { Key = "podcast-wash", Height = K.WashH, AlignSelf = FlexAlign.Start, Gradient = p.Wash, HitTestVisible = false },
                    p.Content,
                ],
            };
            return new BoxEl { Direction = 1, MinWidth = 0f, Children = [on ? body.StickyClip(K.BarH) : body] };
        }
    }

    /// <summary>The bar's trigger: a zero-height box pinned under the bar; <paramref name="on"/> is true while it is held there (the hero has gone).</summary>
    public static Element BarSentinel(Signal<bool> on)
        => new BoxEl { Key = "podcast-bar-sentinel", Height = 0f, HitTestVisible = false }.Sticky(K.BarH, engaged: on);

    /// <summary>The collapsed header bar: the cover, <paramref name="title"/> over <paramref name="caption"/>, then <paramref name="primary"/>.</summary>
    public static Element Bar(string? artUrl, string title, string caption, Element primary, float inset, bool captionShown = true)
    {
        var words = new List<Element>(2)
        {
            BodyStrong(title) with { MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f },
        };
        if (captionShown && caption.Length > 0)
            words.Add(Caption(caption) with { MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f });
        return new BoxEl
        {
            ZStack = true, Height = K.BarH, HitTestVisible = true, Fill = Prop.Of(static () => Tok.FillCardDefault),
            Children =
            [
                new BoxEl
                {
                    Direction = 0, Height = K.BarH, Padding = new Edges4(inset, 0f, inset, 0f), Gap = K.BarGap, AlignItems = FlexAlign.Center,
                    Children =
                    [
                        Controls.Artwork(artUrl, K.BarCover, K.BarCover, K.BarCoverRadius, decodePx: K.BarCoverDecode),
                        new BoxEl { Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Justify = FlexJustify.Center, Children = words.ToArray() },
                        new BoxEl { Shrink = 0f, Children = [primary] },
                    ],
                },
                new BoxEl
                {
                    Height = K.BarH, Direction = 1, Justify = FlexJustify.End, HitTestVisible = false,
                    Children = [new BoxEl { Height = 1f, Fill = Prop.Of(static () => Tok.StrokeDividerDefault) }],
                },
            ],
        };
    }

    /// <summary>A band that pins <paramref name="top"/> below the viewport's edge (a toolbar), <paramref name="height"/> tall. It paints nothing of its own;
    /// <paramref name="stuck"/> is Sticky's engaged edge (its content shows <see cref="PinnedLine"/> from it). <paramref name="scope"/>: the ancestor it
    /// releases at (null = its parent). The content that scrolls under it cuts itself at <paramref name="top"/> + <paramref name="height"/>.</summary>
    public static BoxEl PinnedBand(string key, Element content, float height, float top, Signal<bool> stuck, string? scope = null)
        => new BoxEl { Key = key, ZStack = true, Direction = 1, Height = height, MinWidth = 0f, Children = [content] }.Sticky(top, scope, stuck);

    /// <summary>The hairline at a pinned row's foot, shown only while <paramref name="stuck"/>; <paramref name="margin"/> ends it where the row's column
    /// ends (never across a pane beside it). A layer of the row's ZStack; air on a skeleton.</summary>
    public static Element PinnedLine(string key, Signal<bool> stuck, Edges4 margin)
        => new BoxEl
        {
            Key = key, AlignSelf = FlexAlign.End, Height = 1f, MinWidth = 0f, HitTestVisible = false, Margin = margin,
            Fill = Tok.StrokeDividerDefault, Opacity = Prop.Of(() => stuck.Value ? 1f : 0f),
        }.Skeletonized(false);

    /// <summary>A page whose scroller is a virtualised list (<paramref name="list"/>, an ItemsView carrying its own scroll handle), with
    /// <paramref name="layers"/> over it (an overlay pane) and the 48 bar over everything while <paramref name="barOn"/> holds.</summary>
    public static Element ListPage(string routeKey, Element list, Element bar, IReadSignal<bool> barOn, params Element[] layers)
    {
        Element barLayer = new BoxEl
        {
            Direction = 1, Height = K.BarH, AlignSelf = FlexAlign.Start, HitTestPassThrough = true,
            // The bar coming and going relayouts this layer alone (a boundary: the page owns its size), never the page around the list.
            IsolateLayout = true,
            Children = [Flow.Show(() => barOn.Value, new BoxEl { Key = "podcast-bar", Direction = 1, Enter = K.BarFade, Exit = K.BarFade, Children = [bar] })],
        };
        var kids = new Element[layers.Length + 2];
        kids[0] = list;
        layers.CopyTo(kids, 1);
        kids[^1] = barLayer;
        return new BoxEl { Key = "podcast-page:" + routeKey, ZStack = true, Direction = 1, Grow = 1f, MinWidth = 0f, Children = kids };
    }

    /// <summary>A description: HTML (paragraphs, lists, bold, links routed in-app or opened through the link policy) or plain text, through the house
    /// renderer, never a tag or an entity on screen. <paramref name="maxLines"/> 0 = all.</summary>
    public static Element Description(string html, int maxLines = 0, bool secondary = true)
        => new BoxEl
        {
            Direction = 1, MinWidth = 0f,
            Children = [Controls.RichTextFlex(html, K.BodySize, secondary ? Tok.TextSecondary : Tok.TextPrimary, Tok.AccentTextPrimary, maxLines, DetailVerbs.NavigateKey)],
        };

    /// <summary>The app's play-next glyph (the menus' and the queue's: <c>ActionIcons.PlayNext</c> in the Wavee icon face).</summary>
    public static Element PlayNextIcon(float size, ColorF color) => Icon(WaveeIcons.PlayNext, size, color, WaveeIcons.Font);

    // ══ 2. THE SMART SPLITBUTTON ════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The smart action: <paramref name="label"/> (Pause while <paramref name="pausing"/>) on the main half, the menu on the chevron. Without
    /// <paramref name="accent"/> it is a standard button (caught up). <paramref name="fill"/>: the main half takes the row's width (Compact).</summary>
    public static Element SmartSplit(string key, string label, bool accent, bool pausing, Action play, ImmutableArray<PodcastMenuItem> menu, float height,
        bool fill = false)
        => Embed.Comp(new SplitProps(label, accent, pausing, play, menu, height, fill), static () => new SplitHost()) with { Key = key };

    sealed record SplitProps(string Label, bool Accent, bool Pausing, Action Play, ImmutableArray<PodcastMenuItem> Menu, float Height, bool Fill)
    {
        public bool Equals(SplitProps? o) => o is not null && Label == o.Label && Accent == o.Accent && Pausing == o.Pausing && Height == o.Height
            && Fill == o.Fill && Menu.Length == o.Menu.Length && (Menu.IsDefaultOrEmpty || Menu[0].Label == o.Menu[0].Label);
        public override int GetHashCode() => HashCode.Combine(Label, Accent, Pausing, Height, Fill, Menu.Length);
    }

    sealed class SplitHost : Component, IPropsHost
    {
        SplitProps? _p;
        readonly Signal<SplitProps?> _props = new(null);
        readonly Signal<bool> _open = new(false);
        IOverlayService? _overlay;
        NodeHandle _anchor;
        OverlayHandle? _menu;
        readonly Action _toggle, _play;
        readonly Action<NodeHandle> _capture;

        public SplitHost()
        {
            _toggle = Toggle;
            _play = () => _p?.Play();
            _capture = h => _anchor = h;
        }

        public void ApplyProps(object props)
        {
            _p = (SplitProps)props;
            _props.Value = _p;
        }

        public override Element Render()
        {
            _ = _props.Value;
            var p = _p!;
            _overlay = UseContext(Overlay.Service);
            bool open = _open.Value;
            float h = p.Height, r = Radii.Control;
            string glyph = p.Pausing ? WinUiDetailRules.PauseSolid : WinUiDetailRules.PlaySolid;
            string label = p.Pausing ? Loc.Get(Strings.Podcast.Pages.Pause) : p.Label;
            var appearance = p.Accent ? ButtonAppearance.Accent : ButtonAppearance.Standard;
            BoxEl main = Button.Create(label, _play, appearance, glyph: glyph) with
            {
                Key = "smart:play", Height = h, MinWidth = 0f, Shrink = p.Fill ? 1f : 0f, Grow = p.Fill ? 1f : 0f,
                Corners = new CornerRadius4(r, 0f, 0f, r),
            };
            BoxEl chevron = Button.Create("", _toggle, appearance) with
            {
                Key = "smart:menu", Width = h, Height = h, MinWidth = 0f, Shrink = 0f, Padding = default, Gap = 0f,
                Justify = FlexJustify.Center, AlignItems = FlexAlign.Center, Corners = new CornerRadius4(0f, r, r, 0f),
                Children = [Icon(Icons.ChevronDown, K.SplitChevron, p.Accent ? ColorContrast.PickContrast(Tok.AccentDefault) : Tok.TextPrimary)],
            };
            _ = open;
            return new BoxEl
            {
                Direction = 0, Height = h, Gap = 1f, AlignItems = FlexAlign.Center, Shrink = p.Fill ? 1f : 0f, Grow = p.Fill ? 1f : 0f,
                MinWidth = 0f, OnRealized = _capture,
                Children = [main, ToolTip.Wrap(chevron, Loc.Get(Strings.Podcast.Pages.OtherWays))],
            };
        }

        void Toggle()
        {
            if (_menu is { IsOpen: true } shown) { shown.Close(); return; }
            if (_p is not { } p || Controls.IsNullOverlay(_overlay)) return;
            var items = new List<MenuFlyoutItem>(p.Menu.Length + 1);
            bool sep = false;
            foreach (var m in p.Menu)
            {
                if (!sep && (m.Glyph == Icons.Video || m.Glyph == Icons.Volume)) { items.Add(MenuFlyoutItem.Separator); sep = true; }
                var invoke = m.Invoke;
                items.Add(MenuFlyoutItem.Toggle(m.Label, m.Checked, invoke, m.Glyph) with { AcceleratorText = m.Sub });
            }
            OverlayHandle? opened = null;
            opened = _overlay!.Open(() => _anchor, () => MenuFlyout.Create(items, () => opened?.Close(), size: OverlayFit.MenuSize),
                FlyoutPlacement.BottomEdgeAlignedLeft, new PopupOptions(FocusTrap: true) { ConstrainToRootBounds = false });
            _menu = opened;
            opened.ClosedAction = () =>
            {
                if (!ReferenceEquals(_menu, opened)) return;
                _menu = null;
                _open.Value = false;
            };
            _open.Value = true;
        }
    }

    // ══ 3. THE EPISODE ROW ══════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>One episode row (see the file header), keyed <c>ep:</c> + the id. <paramref name="compact"/>: the 40 targets, a two-line title, the
    /// actions always shown.</summary>
    /// <remarks><paramref name="wide"/>: the date and the length stand at the row's end and the description under its title (else the stacked
    /// form). <paramref name="lead"/>: the list's lead row (the newest episode, not started): a card with a headline. <paramref name="numbered"/>:
    /// the show numbers its episodes, so the row keeps the numeral lane.</remarks>
    public static Element EpisodeRow(PodcastEpisodeVm vm, PodcastRowMode mode, bool description, bool compact, PodcastRowVerbs verbs,
        bool wide = false, bool lead = false, bool numbered = false)
        => Embed.Comp(new RowProps(vm, null, mode, description, compact, verbs, wide, lead, numbered), static () => new RowHost()) with { Key = "ep:" + vm.Id };

    /// <summary>The row of a recycled list slot: it reads its episode from <paramref name="vm"/>, so a slot that binds to another episode writes the
    /// row's words through its binds and re-renders only when the row's SHAPE changes (its state, its parts, its selection): a fast scroll
    /// realising many rows a frame neither re-renders nor allocates a row per slot.</summary>
    public static Element EpisodeRow(IReadSignal<PodcastEpisodeVm?> vm, PodcastRowMode mode, bool description, bool compact, PodcastRowVerbs verbs,
        bool wide = false, bool lead = false, bool numbered = false)
        => Embed.Comp(new RowProps(null, vm, mode, description, compact, verbs, wide, lead, numbered), static () => new RowHost());

    sealed record RowProps(PodcastEpisodeVm? Vm, IReadSignal<PodcastEpisodeVm?>? Source, PodcastRowMode Mode, bool Description, bool Compact,
        PodcastRowVerbs Verbs, bool Wide, bool Lead, bool Numbered)
    {
        public bool Equals(RowProps? o) => o is not null && Equals(Vm, o.Vm) && ReferenceEquals(Source, o.Source) && Mode == o.Mode
            && Description == o.Description && Compact == o.Compact && ReferenceEquals(Verbs, o.Verbs) && Wide == o.Wide && Lead == o.Lead
            && Numbered == o.Numbered;
        public override int GetHashCode() => HashCode.Combine(Vm?.Id, Mode, Description, Compact, Wide, Lead, Numbered);
    }

    /// <summary>What a row's TREE depends on (its words are bound, never in here): equal shapes keep the row's render. <c>Lead</c>: the row is the
    /// list's lead and its episode is not started; <c>Desc</c>: it writes its description (one line under the title of a wide row at rest, two on
    /// the lead).</summary>
    readonly record struct RowShape(PodcastRowState State, float Progress, bool Saved, bool Video, bool Date, bool Length, bool Left, bool Desc,
        bool Selected, bool Playing, bool Lead)
    {
        /// <summary>The episode cannot be played (its play button is off) / it carries the lock tag (its words are bound).</summary>
        public bool Blocked { get; init; }
        public bool Locked { get; init; }
        /// <summary>The row's tags after its words: explicit, bonus, trailer, the transcript mark.</summary>
        public bool Explicit { get; init; }
        public bool Bonus { get; init; }
        public bool Trailer { get; init; }
        public bool Transcript { get; init; }
        /// <summary>The row carries a tag (the lock, or one of the four).</summary>
        public bool Tagged => Locked || Explicit || Bonus || Trailer || Transcript;
        /// <summary>The picture of the month's card (the row is its month's lead): a video episode's still (<c>ArtWide</c>), else its cover;
        /// null on a row.</summary>
        public string? Art { get; init; }
        public bool ArtWide { get; init; }
    }

    sealed class RowHost : Component, IPropsHost
    {
        readonly Signal<bool> _hover = new(false);
        readonly Signal<RowProps?> _props = new(null);
        readonly Signal<PodcastEpisodeVm?> _own = new(null);
        RowProps _p = null!;
        string? _shownId;
        readonly Func<RowShape> _shapeOf;
        readonly Func<string> _title, _date, _length, _desc, _numeral, _numeralSigned, _lock;
        readonly Func<ContextMenuModel?> _menu;
        readonly Action _click, _play, _save, _exit;
        readonly Action<Point2> _move;
        readonly Action<KeyEventArgs> _key;
        readonly Action<PointerEventArgs> _press;
        DragSource? _drag;
        Action<ContextRequestEventArgs>? _context;
        IOverlayService? _contextOverlay;

        public RowHost()
        {
            _shapeOf = ShapeOf;
            // The row writes what THIS episode says: the title without what the show repeats, the number in its lane, the description after
            // the opening the episodes share.
            _title = () => Vm?.ShortTitle ?? "";
            _numeral = () => Vm?.Numeral ?? "";
            _numeralSigned = () => Vm is { Numeral.Length: > 0 } v ? K.NumberSign + v.Numeral : "";
            _date = () => Vm?.Date ?? "";
            _length = () => Vm is { } v ? v.State == PodcastRowState.InProgress && v.Left.Length > 0 ? v.Left : v.Length : "";
            _desc = () => Vm?.Blurb ?? "";
            _lock = () => Vm?.Lock ?? "";
            _menu = () => _p.Verbs.Menu(Id);
            _click = Click;
            _play = () => _p.Verbs.Play(Id);
            _save = () => _p.Verbs.ToggleSaved(Id);
            _move = _ => _hover.SetIfChanged(true);
            _exit = () => _hover.SetIfChanged(false);
            _key = OnKey;
            _press = OnPress;
        }

        public void ApplyProps(object props)
        {
            _p = (RowProps)props;
            if (_p.Vm is not null) _own.Value = _p.Vm;
            _props.Value = _p;
        }

        /// <summary>The episode the row shows (tracked: a bind or the shape memo reading it follows a rebind).</summary>
        PodcastEpisodeVm? Vm => (_props.Value?.Source ?? _own).Value;

        string Id => ((_p.Source ?? _own).Peek())?.Id ?? "";

        RowShape ShapeOf()
        {
            var p = _props.Value;
            var vm = Vm;
            if (p is null || vm is null) return default;
            var v = p.Verbs;
            string id = vm.Id;
            bool left = vm.State == PodcastRowState.InProgress && vm.Left.Length > 0;
            bool rest = vm.State is PodcastRowState.New or PodcastRowState.Unplayed;
            // The first episode of each month is the month's card, whatever its state; a started one writes its bar where the description stood.
            bool lead = p.Lead && !vm.Trailer;
            bool desc = vm.Blurb.Length > 0 && (lead ? vm.State != PodcastRowState.InProgress : rest && p.Wide && !p.Compact && p.Description);
            return new RowShape(vm.State, vm.State == PodcastRowState.InProgress ? vm.Progress : 0f, vm.Saved, vm.Video, vm.Date.Length > 0,
                left || vm.Length.Length > 0, left, desc,
                p.Mode == PodcastRowMode.Select && v.Selected.Value == id, v.Playing.Value == id, lead)
            {
                Blocked = !vm.Playable, Locked = vm.Lock.Length > 0, Explicit = vm.Explicit, Bonus = vm.Bonus, Trailer = vm.Trailer, Transcript = vm.HasTranscript,
                Art = lead ? vm.StillUrl ?? vm.ArtUrl : null, ArtWide = lead && vm.StillUrl is not null,
            };
        }

        public override Element Render()
        {
            var p = _props.Value ?? _p;
            var s = UseComputed(_shapeOf).Value;
            bool hover = _hover.Value;
            var overlay = UseContext(Overlay.Service);
            var vm = (p.Source ?? _own).Peek();
            if (vm is null) return new BoxEl();
            // The row drags as its episode (onto a playlist, the queue, the player bar): ONE source per row, reading the episode the row holds when the
            // drag promotes (a rebind never rebuilds it); the payload is built then.
            if (_drag is null && p.Verbs.Drag is not null) _drag = Wavee.Drag.Source(() => _p.Verbs.Drag?.Invoke(Id));
            // A right-click (or the context key) on the row opens its episode's menu, the same one its "…" opens: ONE handler per row (per overlay
            // service), whose menu delegate reads the episode the row holds when the menu opens, so a rebind neither rebuilds nor allocates it.
            if (!ReferenceEquals(overlay, _contextOverlay))
            {
                _contextOverlay = overlay;
                _context = Controls.IsNullOverlay(overlay) ? null : ContextSheets.Attach(new BoxEl(), overlay!, _menu).OnContextRequested;
            }
            // A row a recycled slot binds to ANOTHER episode shows that episode's state at once (no fill fading from the last one it held); the same
            // episode selected or released fades.
            bool rebound = _shownId is not null && _shownId != vm.Id;
            _shownId = vm.Id;
            bool selected = s.Selected, played = s.State == PodcastRowState.Played, progress = s.State == PodcastRowState.InProgress;
            // The row's form follows what the episode is to the listener: the lead is a card with a headline, a row at rest is the title over its
            // line of description, a row in progress carries its bar, a played row is one quiet line. A narrow list keeps the stacked form.
            bool lead = s.Lead, rest = !played && !progress;
            bool wide = p.Wide && !p.Compact;

            float pb = lead ? K.LeadPlay : p.Compact ? K.PlayButtonCompact : K.PlayButton;
            float act = p.Compact ? K.RowActionCompact : K.RowAction;

            // ── the title column: the words are binds (a rebind writes them, it never re-renders the row) ──
            TextEl title = lead
                ? R.Type.Heading("") with
                {
                    Text = Prop.Of(_title), MaxLines = K.LeadTitleLines, Wrap = TextWrap.Wrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                    Color = played ? Tok.TextSecondary : Tok.TextPrimary,
                }
                : Body("") with
                {
                    Text = Prop.Of(_title),
                    MaxLines = p.Compact ? 2 : 1, Trim = TextTrim.CharacterEllipsis, Wrap = p.Compact ? TextWrap.Wrap : TextWrap.NoWrap, MinWidth = 0f,
                    Weight = played ? (ushort)400 : (ushort)600, Color = played ? Tok.TextSecondary : Tok.TextPrimary,
                };
            var text = new List<Element>(3);
            // The lead's description: under its title where the card is wide; under the whole line at Compact, where the column between the play
            // button and the actions is too narrow to read two lines in.
            Element? leadBlurb = lead && s.Desc
                ? Body("") with
                {
                    Text = Prop.Of(_desc), MaxLines = K.LeadBlurbLines, Wrap = TextWrap.Wrap, Trim = TextTrim.CharacterEllipsis, Color = Tok.TextSecondary,
                    MinWidth = 0f, MaxWidth = K.LeadBlurbW,
                }
                : null;
            // The card at Compact: the column between the cover, the play button and the actions holds the title alone; the meta line (with the
            // episode's number, which has no lane there) and the description run under the whole line.
            Element? underMeta = null;
            if (lead)
            {
                Element meta = RowMeta(s, _date, _length, _lock, numeral: p.Numbered && p.Compact ? _numeralSigned : null);
                if (p.Compact) underMeta = meta; else text.Add(meta);
                text.Add(title);
                if (progress) text.Add(RowBar(s.Progress));
                else if (leadBlurb is not null && !p.Compact) text.Add(leadBlurb);
            }
            else if (!wide)
            {
                text.Add(title);
                // A played row is one quiet line; one that carries a tag (the lock, the "E") says it under its title, after its date alone
                // (the whole line and the tags do not fit a phone's column: the words gave way to a letter).
                if (!played || s.Tagged) text.Add(RowMeta(s, _date, _length, _lock));
            }
            else
            {
                // A wide row has no meta line (its date and length stand at its end): its tags stand after its title.
                text.Add(s.Tagged ? TitleWithTags(title, s, _lock) : title);
                if (progress) text.Add(RowBar(s.Progress));
                else if (s.Desc)
                    text.Add(Body("") with { Text = Prop.Of(_desc), MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, Color = Tok.TextSecondary, MinWidth = 0f });
            }

            // ── the actions: saved at rest (add on hover), the ellipsis always ──
            bool showAdd = s.Saved || hover || selected || p.Compact;
            Element save = Controls.Named(Controls.IconAction(s.Saved ? Icons.HeartFill : Icons.Heart, _save, size: act) with
            {
                Opacity = showAdd ? 1f : 0f, Key = "row:save",
            }, Loc.Get(s.Saved ? Strings.Podcast.Pages.Saved : Strings.Podcast.Pages.Save));
            Element more = WinUiDetailKit.MoreButton(_menu, act);
            var cells = new List<Element>(6);
            if (lead)
            {
                // The episode's own picture leads the card: a video episode's still, wide as it was shot (cut square at Compact, where the card
                // has no room for it); else its cover (a show that gives its episodes none shows the show's).
                float cover = p.Compact ? K.LeadCoverCompact : K.LeadCover;
                float coverW = s.ArtWide && !p.Compact ? cover * K.LeadStillAspect : cover;
                cells.Add(new BoxEl
                {
                    Key = "row:cover", Width = coverW, Height = cover, Shrink = 0f, AlignSelf = FlexAlign.Start,
                    Children = [Controls.Artwork(s.Art, coverW, cover, K.LeadCoverRadius, decodePx: K.LeadCoverDecode)],
                });
            }
            cells.Add(PlayButton(s, pb, hover, _play, strong: lead && rest));
            if (p.Numbered && !(lead && p.Compact))
                cells.Add((lead ? R.Type.NumeralLarge("") : R.Type.Numeral("")) with
                {
                    Key = "row:numeral", Text = Prop.Of(_numeral), Width = lead ? K.NumeralLeadW : K.NumeralW, Shrink = 0f, TextAlign = TextAlign.End,
                    Color = Tok.TextTertiary, MaxLines = 1, Wrap = TextWrap.NoWrap,
                });
            cells.Add(new BoxEl { Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Gap = lead ? K.LeadGap : 0f, Children = text.ToArray() });
            // The date and the length at the row's end (the lead and a narrow list write them as their meta line).
            if (wide && !lead) cells.Add(RowMeta(s, _date, _length, _lock, end: true));
            cells.Add(new BoxEl { Direction = 0, Shrink = 0f, AlignItems = FlexAlign.Center, Children = [save, more] });
            Element line = new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Gap = K.RowColGap, MinWidth = 0f, Children = cells.ToArray() };

            // Keyed `ep` (one family, never the episode: a recycled slot keeps its row): the row is the instance its verbs act on (the lint's subject,
            // the inspector's drag and click target).
            Element row = new BoxEl
            {
                Key = "ep", Direction = 1, MinWidth = 0f, Focusable = true, Role = AutomationRole.Button,
                Corners = lead ? CornerRadius4.All(K.CardRadius) : Radii.ControlAll,
                Padding = lead ? Edges4.All(K.LeadPad)
                    : p.Compact ? Edges4.All(K.RowPadCompact)
                    : played ? new Edges4(K.RowPadX, K.RowPadPlayed, K.RowPadX, K.RowPadPlayed)
                    : new Edges4(K.RowPadX, K.RowPadY, K.RowPadX, K.RowPadY),
                // The lead stands on a card; a row is bare until it is hovered or selected.
                Fill = lead ? Tok.FillCardDefault : selected ? Tok.FillSubtleSecondary : Tok.FillSubtleTransparent,
                HoverFill = lead ? Tok.FillCardSecondary : selected ? Tok.FillSubtleSecondary : Tok.FillSubtleTertiary,
                BorderWidth = lead ? 1f : 0f, BorderColor = lead ? Tok.StrokeCardDefault : ColorF.Transparent,
                PressedFill = lead ? Tok.FillCardSecondary : Tok.FillSubtleTertiary, BrushTransitionMs = rebound ? 0f : Design.Motion.Faster,
                OnClick = _click, OnHoverMove = _move, OnPointerExit = _exit, OnKeyDown = _key, OnPointerPressed = _press, Draggable = _drag,
                Gap = lead ? K.LeadGap + K.LeadGap : 0f, OnContextRequested = _context,
                Children = Lines(line, underMeta, p.Compact ? leadBlurb : null),
            };
            // ONE tree selected or not (the pill is present, painted only while selected: an opacity, never a presence change): a selection that comes
            // or goes with the row mode (a tier crossing, 1008) never rebuilds the row and what it holds (the progress ring keeps its mount).
            return new BoxEl
            {
                ZStack = true, Direction = 1, MinWidth = 0f,
                Children =
                [
                    row,
                    new BoxEl
                    {
                        Width = K.SelectPillW, Height = K.SelectPillH, AlignSelf = FlexAlign.Center, JustifySelf = FlexAlign.Start, Opacity = selected ? 1f : 0f,
                        Corners = CornerRadius4.All(K.SelectPillW), Fill = Tok.AccentDefault, HitTestVisible = false,
                    },
                ],
            };
        }

        void Click()
        {
            var p = _p;
            switch (p.Mode)
            {
                case PodcastRowMode.Select: p.Verbs.Select(Id); break;
                default: p.Verbs.Open(Id); break;
            }
        }

        /// <summary>A double-click plays (the list/details pattern: one click selects, two play).</summary>
        void OnPress(PointerEventArgs e)
        {
            if (e.ClickCount < 2 || e.Button != 0 || _p.Mode != PodcastRowMode.Select) return;
            _p.Verbs.Play(Id);
            e.Handled = true;
        }

        void OnKey(KeyEventArgs e)
        {
            if (e.KeyCode != Keys.Enter) return;
            _p.Verbs.Play(Id);
            e.Handled = true;
        }
    }

    /// <summary>A row in progress: its bar (the accent on the ring's track), <paramref name="progress"/> 0..1.</summary>
    static Element RowBar(float progress) => new BoxEl
    {
        Key = "row:bar", Direction = 0, Height = K.RowBarH, MaxWidth = K.RowBarW, Shrink = 0f, Margin = new Edges4(0f, K.RowBarTop, 0f, 0f),
        Corners = CornerRadius4.All(K.RowBarH / 2f), Fill = Tok.StrokeControlStrongDefault with { A = K.RingTrackAlpha }, ClipToBounds = true,
        Children =
        [
            new BoxEl { Grow = 1f, Fill = Tok.AccentDefault, TransformOriginX = 0f, Transform = Affine2D.Scale(MathF.Max(0.001f, Math.Clamp(progress, 0f, 1f)), 1f) },
        ],
    };

    /// <summary>"View details": the link an embedded page (the library's column) ends its command row on, to the subject's own page. The house
    /// HyperlinkButton (rest, hover, pressed, focus).</summary>
    public static Element ViewDetails(Action open)
        => HyperlinkButton.Create(Loc.Get(Strings.Library.ViewDetails), open, size: ControlSize.Small) with { Key = "cmd:view-details", Shrink = 0f };

    /// <summary>The row's line and what runs under it (the Compact card's meta line and description), the absent ones left out.</summary>
    static Element[] Lines(Element line, Element? a, Element? b)
        => a is null ? (b is null ? [line] : [line, b]) : b is null ? [line, a] : [line, a, b];

    /// <summary>A wide row's title with its tags after it (the lock and why, the "E", Bonus, Trailer, the transcript mark): the title gives way
    /// with its ellipsis, the tags keep their width.</summary>
    static Element TitleWithTags(TextEl title, RowShape s, Func<string> lockTag)
    {
        var kids = new List<Element>(6) { title with { Shrink = 1f } };
        if (s.Locked) kids.Add(LockTag(MetaWord("") with { Text = Prop.Of(lockTag) }));
        Tags(kids, s.Explicit, s.Bonus, s.Trailer, s.Transcript);
        return new BoxEl { Direction = 0, Gap = K.MetaGap, AlignItems = FlexAlign.Center, MinWidth = 0f, Children = kids.ToArray() };
    }

    /// <summary>The row's play button: a circle (36 / 40) with the ring while in progress and the check while played (replay on hover).
    /// <paramref name="strong"/>: the lead's, filled with the accent.</summary>
    static Element PlayButton(RowShape s, float size, bool hover, Action play, bool strong = false)
    {
        bool prog = s.State == PodcastRowState.InProgress;
        bool played = s.State == PodcastRowState.Played;
        bool playing = s.Playing;
        bool off = s.Blocked;
        string glyph = playing ? WinUiDetailRules.PauseSolid : played ? (hover && !off ? Icons.Refresh : Icons.Check) : WinUiDetailRules.PlaySolid;
        string name = Loc.Get(playing ? Strings.Podcast.Pages.Pause : prog ? Strings.Podcast.Pages.Resume : played ? Strings.Podcast.Pages.PlayAgain
            : Strings.Podcast.Pages.Play);
        var layers = new List<Element>(3);
        if (prog)
            layers.Add(new BoxEl
            {
                Width = size, Height = size, AlignSelf = FlexAlign.Center, JustifySelf = FlexAlign.Center, HitTestVisible = false,
                // The determinate ring keeps its mount value: a new resting position mounts a new ring.
                Children = [ProgressRing.Determinate(s.Progress, size, foreground: Tok.AccentDefault, track: Tok.StrokeControlStrongDefault with { A = K.RingTrackAlpha }) with { Key = "ring:" + s.Progress.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture) }],
            });
        layers.Add(new BoxEl
        {
            Width = size, Height = size, AlignSelf = FlexAlign.Center, JustifySelf = FlexAlign.Center, HitTestVisible = false,
            Justify = FlexJustify.Center, AlignItems = FlexAlign.Center,
            Children = [Icon(glyph, K.PlayGlyph, off ? Tok.TextDisabled : strong ? Tok.OnAccent : played && !hover && !playing ? Tok.TextTertiary : Tok.TextPrimary)],
        });
        bool plate = !prog && !played;
        ColorF fill = off ? (plate ? Tok.FillControlDisabled : Tok.FillSubtleTransparent)
            : strong ? Tok.AccentDefault : plate ? Tok.FillControlDefault : Tok.FillSubtleTransparent;
        return Controls.Named(new BoxEl
        {
            // Its size steps at Compact (the 40 target): it eases, never snaps.
            Key = "row:play", ZStack = true, Width = size, Height = size, Shrink = 0f, Corners = CornerRadius4.All(size / 2f), Layout = WinUiDiscoveryMotion.SizeReflow,
            // An episode that cannot be played: the button is off (the disabled plate and glyph, no click); the row's lock tag says why.
            Fill = fill, HoverFill = off ? fill : strong ? Tok.AccentSecondary : Tok.FillControlSecondary,
            PressedFill = off ? fill : strong ? Tok.AccentTertiary : Tok.FillControlTertiary,
            BorderWidth = played ? 1f : 0f, BorderBrush = played ? Tok.ControlElevationBorder : null,
            Role = AutomationRole.Button, Focusable = !off, OnClick = off ? null : play, Children = layers.ToArray(),
        }, name);
    }

    /// <summary>The row's meta line (<see cref="MetaLine"/>'s parts) with its date and its length (or the accent time left) bound, then the row's
    /// tags (the lock and why, the "E", Bonus, Trailer, the transcript mark). <paramref name="end"/>: it stands at the row's end, where it keeps
    /// its width and carries no tag (they stand after the title). A played row says its date alone, there and under its title (the check and the
    /// quiet title say the rest), then its tags.</summary>
    /// <remarks><paramref name="numeral"/>: the episode's number leads the line ("#97") where the row has no numeral lane.</remarks>
    static Element RowMeta(RowShape s, Func<string> date, Func<string> length, Func<string> lockTag, bool end = false, Func<string>? numeral = null)
    {
        var kids = new List<Element>(5);
        bool quiet = s.State == PodcastRowState.Played && (end || s.Tagged);
        if (s.State == PodcastRowState.New) kids.Add(NewMark());
        if (numeral is not null) kids.Add(MetaPart(false, MetaWord("") with { Text = Prop.Of(numeral) }));
        if (s.Date) kids.Add(MetaPart(numeral is not null, MetaWord("") with { Text = Prop.Of(date) }));
        if (s.Length && !quiet) kids.Add(MetaPart(s.Date, MetaWord("") with { Text = Prop.Of(length), Color = s.Left ? Tok.AccentTextPrimary : Tok.TextTertiary }));
        if (s.State == PodcastRowState.Played && !quiet) kids.Add(MetaPart(s.Date || s.Length, MetaWord(Loc.Get(Strings.Podcast.Pages.Played))));
        if (s.Video && !quiet) kids.Add(VideoTag());
        if (!end)
        {
            if (s.Locked) kids.Add(LockTag(MetaWord("") with { Text = Prop.Of(lockTag) }));
            Tags(kids, s.Explicit, s.Bonus, s.Trailer, s.Transcript);
        }
        return new BoxEl
        {
            Key = end ? "row:aside" : null, Direction = 0, Gap = K.MetaGap, AlignItems = FlexAlign.Center, MinWidth = 0f, Shrink = end ? 0f : 1f,
            ClipToBounds = !end, Children = kids.ToArray(),
        };
    }

    /// <summary>The meta line: New (dot + word), the date, the length or the accent time left, Played, the Video tag. Each word after the first
    /// carries its "·" in one unit (the separator travels with its word); the words give way (an ellipsis) before the New mark and the Video tag,
    /// which never clip.</summary>
    public static Element MetaLine(PodcastEpisodeVm vm, bool showNew = true)
    {
        var kids = new List<Element>(5);
        if (showNew && vm.State == PodcastRowState.New) kids.Add(NewMark());
        bool any = false;
        if (vm.Date.Length > 0) { kids.Add(MetaPart(false, MetaWord(vm.Date))); any = true; }
        if (vm.State == PodcastRowState.InProgress && vm.Left.Length > 0) { kids.Add(MetaPart(any, MetaWord(vm.Left) with { Color = Tok.AccentTextPrimary })); any = true; }
        else if (vm.Length.Length > 0) { kids.Add(MetaPart(any, MetaWord(vm.Length))); any = true; }
        if (vm.State == PodcastRowState.Played) kids.Add(MetaPart(any, MetaWord(Loc.Get(Strings.Podcast.Pages.Played))));
        if (vm.Video) kids.Add(VideoTag());
        if (vm.Lock.Length > 0) kids.Add(LockTag(MetaWord(vm.Lock)));
        Tags(kids, vm.Explicit, vm.Bonus, vm.Trailer, vm.HasTranscript);
        return new BoxEl { Direction = 0, Gap = K.MetaGap, AlignItems = FlexAlign.Center, MinWidth = 0f, ClipToBounds = true, Children = kids.ToArray() };
    }

    /// <summary>One meta word with its leading "·" (<paramref name="dot"/>) as ONE unit: the separator never stands apart from its word. The word
    /// gives way with an ellipsis where the line is short, so what follows it (the Video tag) keeps its whole width.</summary>
    static Element MetaPart(bool dot, TextEl word)
    {
        var flex = word with { Shrink = 1f, MinWidth = 0f, Trim = TextTrim.CharacterEllipsis, Wrap = TextWrap.NoWrap };
        return dot
            ? new BoxEl { Direction = 0, Gap = K.MetaGap, AlignItems = FlexAlign.Center, Shrink = 1f, MinWidth = 0f, Children = [MetaWord("·"), flex] }
            : flex;
    }

    /// <summary>New: the accent dot and the word.</summary>
    static Element NewMark() => new BoxEl
    {
        Direction = 0, Gap = K.MetaGap, AlignItems = FlexAlign.Center, Shrink = 0f,
        Children =
        [
            new BoxEl { Width = K.NewDot, Height = K.NewDot, Corners = CornerRadius4.All(K.NewDot / 2f), Fill = Tok.AccentDefault },
            Caption(Loc.Get(Strings.Podcast.Pages.Fresh)) with { Color = Tok.AccentTextPrimary, Weight = 600 },
        ],
    };

    /// <summary>The Video tag: the glyph and the word.</summary>
    static Element VideoTag() => new BoxEl
    {
        Direction = 0, Gap = K.MetaGap / 2f, AlignItems = FlexAlign.Center, Shrink = 0f,
        Children = [Icon(Icons.Video, K.MetaGlyph, Tok.TextTertiary), MetaWord(Loc.Get(Strings.Podcast.Pages.Video))],
    };

    /// <summary>The topic / genre words of a header line, each a link of its own (the accent ink at rest, the subtle plate on hover and press, focus,
    /// the hand) that opens <paramref name="open"/> with its words; plain secondary words when <paramref name="open"/> is null. Each word carries
    /// the "·" before it (the line continues a kind word, so it starts on one), so no line starts or ends on a separator. ONE line that hugs its
    /// words and gives way a whole word at a time (<see cref="Controls.WordLine"/>): a word is never cut, never half a target, and
    /// <paramref name="trailing"/> (the tags) sits right after the last word that shows. <paramref name="target"/>:
    /// the tier's control height, which each word's hit rect reaches (its slop, inside the clip: the type and the line keep their size).</summary>
    public static Element WordLinks(string key, ImmutableArray<string> words, Action<string>? open, float target, Element[]? trailing = null)
    {
        var caption = Caption("");
        float line = caption.LineHeight;
        float slop = MathF.Max(0f, (target - line) / 2f);
        var units = new Element[words.Length];
        for (int i = 0; i < words.Length; i++)
        {
            string word = words[i];
            Element w = open is null
                ? caption with { Text = word, Color = Tok.TextSecondary, Wrap = TextWrap.NoWrap }
                : new BoxEl
                {
                    Key = key + ":" + i, Direction = 0, AlignItems = FlexAlign.Center, Shrink = 0f,
                    Padding = new Edges4(K.WordLinkPadX, 0f, K.WordLinkPadX, 0f), Margin = new Edges4(-K.WordLinkPadX, 0f, -K.WordLinkPadX, 0f),
                    HitSlop = new Edges4(0f, slop, 0f, slop), Corners = Radii.ControlAll,
                    Fill = Tok.FillSubtleTransparent, HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary,
                    Cursor = CursorId.Hand, Role = AutomationRole.Hyperlink, Focusable = true, OnClick = () => open(word),
                    Children = [caption with
                    {
                        Text = word, Color = Tok.AccentTextPrimary, HoverColor = Tok.AccentTextSecondary, PressedColor = Tok.AccentTextTertiary, Wrap = TextWrap.NoWrap,
                    }],
                };
            units[i] = new BoxEl
            {
                Direction = 0, Gap = K.WordLinkGap, AlignItems = FlexAlign.Center, Shrink = 0f, Height = line, Margin = new Edges4(0f, 0f, 0f, slop),
                Children = [caption with { Text = "·", Color = Tok.TextSecondary }, w],
            };
        }
        return Controls.WordLine(key, units, trailing ?? [], line, slop, K.WordLinkGap);
    }

    /// <summary>The row's tags after its words, each whole (never clipped; the words before them give way): the house "E", Bonus, Trailer and the
    /// transcript mark (the document glyph, named for assistive tech).</summary>
    static void Tags(List<Element> kids, bool isExplicit, bool bonus, bool trailer, bool transcript)
    {
        if (isExplicit) kids.Add(WinUiRowCells.ExplicitMark());
        if (bonus) kids.Add(MetaWord(Loc.Get(Strings.Podcast.Pages.Bonus)));
        if (trailer) kids.Add(MetaWord(Loc.Get(Strings.Podcast.Pages.Trailer)));
        if (transcript)
            kids.Add(Controls.Named(new BoxEl { Shrink = 0f, Children = [Icon(Icons.Document, K.MetaGlyph, Tok.TextTertiary)] }, Loc.Get(Strings.Podcast.Pages.TranscriptMark)));
    }

    /// <summary>The lock tag of a restricted episode ("Subscribers only", "Preview only"): the lock and the word, never clipped (as the Video tag).</summary>
    static Element LockTag(TextEl word) => new BoxEl
    {
        Direction = 0, Gap = K.MetaGap / 2f, AlignItems = FlexAlign.Center, Shrink = 0f,
        Children = [Icon(Icons.Lock, K.MetaGlyph, Tok.TextTertiary), word],
    };

    static TextEl MetaWord(string text) => Caption(text) with { Color = Tok.TextTertiary, MaxLines = 1, Shrink = 0f };

    // ══ 4. A SECTION HEADER AND THE CARDS ═══════════════════════════════════════════════════════════════════════════════════

    /// <summary>"More like this · See all": Subtitle, a caption and a trailing link.</summary>
    public static Element SectionHeader(string title, string? caption = null, string? link = null, Action? onLink = null, bool strong = false)
    {
        var kids = new List<Element>(3) { strong ? BodyStrong(title) : Subtitle(title) };
        if (caption is { Length: > 0 }) kids.Add(Caption(caption) with { Color = Tok.TextTertiary, Shrink = 1f, MinWidth = 0f, MaxLines = 1 });
        kids.Add(new BoxEl { Grow = 1f });
        if (link is { Length: > 0 } && onLink is not null) kids.Add(HyperlinkButton.Create(link, onLink));
        return new BoxEl { Direction = 0, Gap = K.SectionGap, AlignItems = FlexAlign.Center, MinWidth = 0f, Children = kids.ToArray() };
    }

    /// <summary>"More like this" on the house media surface: shelf cards <paramref name="cardWidth"/> wide (hover plate, play, pressed, focus, the
    /// context menu, the hand cursor), or the house rows when <paramref name="rows"/> (a narrow column). <paramref name="menu"/> and <paramref name="play"/>
    /// come from the page's host (null: the card has none).</summary>
    public static Element MoreLikeThis(ImmutableArray<PodcastCardVm> cards, Action<string> open, bool rows, Func<string, ContextMenuModel?>? menu = null,
        Action<string>? play = null, float cardWidth = K.CardCover)
    {
        var kids = new Element[cards.Length];
        for (int i = 0; i < cards.Length; i++)
        {
            var c = cards[i];
            string uri = c.Uri;
            var data = new Controls.CardData(uri, c.Title, c.Subtitle.Length > 0 ? Caption(c.Subtitle) with { MaxLines = 1, Trim = TextTrim.CharacterEllipsis } : null,
                c.ArtUrl, () => open(uri), play is null ? null : () => play(uri), ShowMenu: menu is not null)
            {
                Menu = menu is null ? null : () => menu(uri),
            };
            kids[i] = new BoxEl
            {
                Key = "card:" + uri, Direction = 1, MinWidth = 0f, Width = rows ? float.NaN : cardWidth, Shrink = 0f,
                Children = [rows ? Controls.Surface(data, Shape.Row(K.RowCardCover)) : Controls.Surface(data, Shape.Shelf(), cardWidth)],
            };
        }
        return rows
            ? new BoxEl { Key = "more-like-this", Direction = 1, MinWidth = 0f, Children = kids }
            : new BoxEl { Key = "more-like-this", Direction = 0, Wrap = true, Gap = K.CardGap, MinWidth = 0f, Children = kids };
    }
}
