// ── Entities/Browse.Modules.cs ─────────────────────────────────────────────────────────────────────────────────────
// The module geometry and shell factories Browse and Search still call directly: the Fold deck (Home's section
// directory, Home's Charts row and Browse's own Charts band shared ONE row factory) and the drill grid (the Home
// section page's and Browse's FlattenOne body). Split out of the superseded Wave-5 Home UI (Home.UI.cs, Home.Rules.cs
// §6) when the Home page itself was rebuilt (docs/plans/wavee/home-rebuild-implementation.md §7) — moved UNCHANGED so
// Browse.Page.cs / Browse.UI.cs / Search.UI.cs need no call-site edits. Only the members those callers (and each
// other) actually reach are kept; the landing-only geometry (HomeGroupKind columns/heights/row-gaps, the estimator's
// ContentExtent) went with the rest of the Wave-5 Home UI.

using System.Collections.Generic;
using System.Globalization;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Reconciler;
using FluentGpu.Scene;
using FluentGpu.Scroll.Runtime;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

/// <summary>ONE source of truth for the shelf/grid/fold-tile geometry Browse and Search read, read by the renderer AND
/// (via <see cref="Design.Size"/>/<see cref="Spacing"/>) anything sizing the same shapes. The shelf height and the grid
/// chrome DELEGATE to <see cref="Controls"/> so the two cannot be handed different numbers.</summary>
public static class HomeModuleLayout
{
    public const float FallbackWidth = 1100f;

    public const float ShelfCardMin = Design.Size.ShelfCardMin;
    public const float ShelfCardMax = Design.Size.ShelfCardMax;
    public const float ShelfEdgeFade = Design.Size.FadeShelf;
    /// <summary>The gap BETWEEN shelf cards: zero, because the shared card already insets its cover by its plate padding
    /// (8 each side), so two covers sit 16 apart — the prototype's gutter. A 12 gap on top read as 28 between covers.</summary>
    public const float ShelfGap = 0f;

    public const float GridGap = Spacing.M;

    /// <summary>The cell reserve from how many lines the title may wrap to and whether a metadata line renders (0.2.9's
    /// clamp of <paramref name="titleLines"/> at 1 kept; the arithmetic is <see cref="Controls.GridCardChromeFor"/>).</summary>
    public static float GridCardChromeFor(int titleLines, bool hasSubtitle)
        => Controls.GridCardChromeFor(titleLines < 1 ? 1 : titleLines, hasSubtitle);

    // ── the Fold tile ──
    public const float FoldCardHeight = 176f;
    public const float FoldCover = 124f;
    public const float FoldCopyMaxFrac = 0.70f;
    public const float FoldCardMin = 440f;
    public const float FoldCardMax = 9999f;

    /// <summary>Cover i's REST pose in card-local DIP (right:-40 / top:6 / width:250), clamped so a 0-width first frame
    /// cannot park covers at a negative X.</summary>
    public static void FoldRest(int i, float cardW, out float x, out float y, out float rot)
    {
        float left = MathF.Max(0f, cardW - 250f + 40f);
        (float lx, float ly, float r) = i switch
        {
            0 => (0f, 32f, -11f),
            1 => (44f, 16f, 5f),
            _ => (92f, 2f, -2f),
        };
        x = left + lx; y = 6f + ly; rot = r;
    }

    /// <summary>Cover i's hover DELTA on the rest pose (WhileHover is additive).</summary>
    public static void FoldFan(int i, out float dx, out float dy, out float drot)
        => (dx, dy, drot) = i switch
        {
            0 => (-10f, 6f, -5f),
            1 => (2f, -6f, 3f),
            _ => (10f, 0f, 3f),
        };

    /// <summary>A Browse/Home shelf cell's extent: title + ONE caption line (the house `ShelfCell` caps its second line at
    /// the item's CaptionLines, 1 by default) — no reserved second line under every card.</summary>
    public static float ShelfCardHeight(float cardW) => Controls.ShelfHeight(cardW, 1f, captionLines: 1, metaLine: false);

    // Memoized per INSTANCE (0.2.9's ConditionalWeakTable, kept): a section-set is immutable, and a page hands a NEW
    // list whenever any section's Version or CardVersion moves, so the key changes exactly when the rendered structure
    // does.
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IReadOnlyList<HomeSectionView>, string> SectionSetKeys = new();

    public static string SectionSetKey(IReadOnlyList<HomeSectionView> sections)
        => SectionSetKeys.GetValue(sections, static s => ComputeSectionSetKey(s));

    static string ComputeSectionSetKey(IReadOnlyList<HomeSectionView> sections)
    {
        ulong h = Text(Offset, "sections");
        for (int i = 0; i < sections.Count; i++)
        {
            var s = sections[i];
            h = Text(Text(Text(h, s.Uri), s.Title), s.Subtitle);
            h = Value(Value(h, unchecked((ulong)s.TotalCount)), unchecked((ulong)s.Cards.Count));
            for (int c = 0; c < s.Cards.Count; c++) h = Value(h, unchecked((ulong)s.Cards[c].DedupeKey));
        }
        return "home-section-set:" + h.ToString("X16", CultureInfo.InvariantCulture);
    }

    const ulong Offset = 14695981039346656037UL;
    const ulong Prime = 1099511628211UL;

    static ulong Value(ulong h, ulong value)
    {
        for (int i = 0; i < 8; i++) { h ^= (byte)(value >> (i * 8)); h *= Prime; }
        return h;
    }

    static ulong Text(ulong h, string? value)
    {
        h = Value(h, unchecked((ulong)(value?.Length ?? -1)));
        if (value is null) return h;
        for (int i = 0; i < value.Length; i++) h = Value(h, value[i]);
        return h;
    }
}

/// <summary>The Fold deck and the drill grid — the two module shells Browse and Search still mount directly.</summary>
public static class HomeModules
{
    /// <summary>THE module header grammar: the display-face 20/28 title with its subtitle in the SAME paragraph (the
    /// 12-px run sits on the 20-px baseline), a 12-DIP chevron ONLY when there is somewhere to drill, and a trailing
    /// tools slot. A non-drillable header is the identical label with no chevron and no click wrapper.</summary>
    public static Element ModuleHeader(string title, string? subtitle, Element? tools, Action? open)
    {
        Element label = subtitle is { Length: > 0 } sub
            ? Design.Type.ModuleHeader(title, sub) with
              { Shrink = 1f, MinWidth = 0f, MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis }
            : Design.Type.ModuleHeader(title) with { Shrink = 1f, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis };
        Element titleEl = open is null
            ? new BoxEl { Direction = 0, Shrink = 1f, MinWidth = 0f, Children = [label] }
            : new BoxEl
            {
                Direction = 0, Gap = Spacing.XS, AlignItems = FlexAlign.Center, Shrink = 1f, MinWidth = 0f,
                OnClick = open, Cursor = CursorId.Hand, Role = AutomationRole.Hyperlink, Focusable = true,
                Children = [label, Icon(Icons.ChevronRight, 12f, Tok.TextTertiary)],
            };
        return new BoxEl
        {
            Direction = 0, Gap = Spacing.M, AlignItems = FlexAlign.Center, MinWidth = 0f,
            Children = [titleEl, new BoxEl { Grow = 1f, MinWidth = 0f }, tools ?? new BoxEl()],
        };
    }

    /// <summary>The chevron header the Fold deck and Browse's shelves share. A blank title is no header at all — an
    /// empty box, never an empty row that still pays the band's height.</summary>
    public static Element DrillHeader(string title, Action? open)
        => string.IsNullOrWhiteSpace(title) ? new BoxEl() : ModuleHeader(title, null, null, open);

    /// <summary>Home's section directory, Home's Charts row and Browse's Charts band — ONE row factory: one card height,
    /// one key shape, rows 1, at most two tiles filling the row (two-up at ≥ 892 of content). No tile eyebrow: the
    /// 0.2.9 eyebrow parameters had no caller (code wins).</summary>
    public static Element FoldDeck(IReadOnlyList<HomeSectionView> sections, string title, Action<HomeSectionView> openTile,
                                   Action? openHeader = null)
        => PagedShelf.Create(sections,
            (section, i, cardW) => HomeFoldTile.Create(section, cardW, null, openTile),
            cardHeight: static _ => HomeModuleLayout.FoldCardHeight,
            header: DrillHeader(title, openHeader),
            minCardW: HomeModuleLayout.FoldCardMin, maxCardW: HomeModuleLayout.FoldCardMax,
            gap: Spacing.M, rows: 1, maxColumns: 2, edgeFade: HomeModuleLayout.ShelfEdgeFade,
            prevGlyph: Icons.ChevronLeft, nextGlyph: Icons.ChevronRight,
            keyOf: static (section, i) => "home-fold-tile:" + (section.Uri is { Length: > 0 } u ? u : i.ToString(CultureInfo.InvariantCulture)))
           with { Key = HomeModuleLayout.SectionSetKey(sections) + ":fold" };

    /// <summary>The drill grid — the Home section page's and Browse's FlattenOne body. Fit decides the COLUMN COUNT
    /// only; the covers are square because <see cref="AspectGridVirtualLayout"/> derives the row height from the ARRANGED
    /// cell width. <paramref name="charts"/> is the DECODED <c>SectionFlags.Chart</c> of the section, threaded in — never
    /// a uri lookup: it blanks the subtitle and drops that rung from the cell reserve, the same bool in both places.</summary>
    public static Element SectionGrid(IReadOnlyList<HomeCard> cards, string? sectionKey, float width,
        Action<HomeCard> open, NearTailWatch? nearTail = null,
        string? highlightQuery = null, int titleLines = 1, bool charts = false)
    {
        // Subscribing reads, taken by WHICHEVER render calls this (the page's responsive box): a hydrated card row must
        // re-describe its cell, and the grid host's props gate otherwise sees the same card list instance.
        var scope = Entities.Current;
        uint epoch = scope.Playlists.Changed.Value + scope.Albums.Changed.Value * 3u + scope.Artists.Changed.Value * 5u
                     + scope.Shows.Changed.Value * 7u + scope.Tracks.Changed.Value * 11u + scope.Episodes.Changed.Value * 13u;
        return Embed.Comp(new GridProps(cards, sectionKey, width, open, nearTail, highlightQuery, titleLines, charts, epoch),
                          static () => new SectionGridHost());
    }

    sealed record GridProps(IReadOnlyList<HomeCard> Cards, string? SectionKey, float Width, Action<HomeCard> Open,
        NearTailWatch? NearTail, string? Query, int TitleLines, bool Charts, uint Epoch)
    {
        // Delegates are behaviour, not data (component-props-contract): the gate compares the card list by reference,
        // the scalars by value, and only whether a near-tail watch exists.
        public bool Equals(GridProps? o) => o is not null && ReferenceEquals(Cards, o.Cards)
            && string.Equals(SectionKey, o.SectionKey, StringComparison.Ordinal) && Width == o.Width
            && string.Equals(Query, o.Query, StringComparison.Ordinal) && TitleLines == o.TitleLines && Charts == o.Charts
            && Epoch == o.Epoch && (NearTail is null) == (o.NearTail is null);

        public override int GetHashCode() => HashCode.Combine(Cards.Count, Width, TitleLines, Charts, Epoch);
    }

    sealed class SectionGridHost : Component
    {
        GridProps? _latest;
        /// <summary>The grid viewport's scroll handle: the near-tail watch (when the host armed one) reads its geometry.</summary>
        readonly ScrollHandle _scroll = new();
        readonly Action _watchTail;

        public SectionGridHost() => _watchTail = () =>
        {
            float y = (float)_scroll.Offset.Value, vh = (float)_scroll.ViewportSignal.Value, ch = (float)_scroll.ExtentSignal.Value;
            _latest?.NearTail?.Observe(y, vh, ch);
        };

        public override Element Render()
        {
            var p = UseProps<GridProps>();
            _latest = p;
            UseSignalEffect(_watchTail);
            var overlay = UseContext(Overlay.Service);
            IOverlayService? host = Controls.IsNullOverlay(overlay) ? null : overlay;
            var (columns, _) = FillRowVirtualLayout.Fit(p.Width, HomeModuleLayout.ShelfCardMin, HomeModuleLayout.ShelfCardMax,
                                                        HomeModuleLayout.GridGap);
            columns = Math.Max(1, columns);
            string tier = columns.ToString(CultureInfo.InvariantCulture) + ":" + p.TitleLines.ToString(CultureInfo.InvariantCulture);
            var cards = p.Cards;
            string key = p.SectionKey ?? "";
            return new VirtualListEl
            {
                ItemCount = cards.Count,
                ItemLayout = new AspectGridVirtualLayout(columns, 1f,
                    HomeModuleLayout.GridCardChromeFor(p.TitleLines, hasSubtitle: !p.Charts), HomeModuleLayout.GridGap),
                RenderItem = i => (uint)i < (uint)cards.Count ? Cell(cards[i], i, tier, p, host) : new BoxEl(),
                KeyOf = i => key + "" + ((uint)i < (uint)cards.Count && cards[i].Uri.Length > 0
                    ? cards[i].Uri : i.ToString(CultureInfo.InvariantCulture)),
                Grow = 1f, Shrink = 1f, MinHeight = 0f,
                Handle = _scroll,
            };
        }

        Element Cell(HomeCard card, int index, string tier, GridProps p, IOverlayService? host)
        {
            var c = card;
            bool circular = c.Kind == HomeCardKind.Artist;
            ChartTitleMatch.TryFind(c.Title, p.Query, out int matchStart, out int matchLen);
            Element titleEl = matchLen > 0
                ? Controls.SearchHighlight(c.Title, matchStart, matchLen, 14f, 600, Tok.TextPrimary, p.TitleLines)
                : Design.Type.CardTitle(c.Title) with
                {
                    MaxLines = p.TitleLines, Wrap = p.TitleLines > 1 ? TextWrap.Wrap : TextWrap.NoWrap,
                    Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                };
            string sub = p.Charts ? "" : HomeCards.PlainText(c.Subtitle);
            var labels = new BoxEl
            {
                Direction = 1, Gap = 2f, MinWidth = 0f, AlignItems = circular ? FlexAlign.Center : FlexAlign.Start,
                Children = sub.Length > 0
                    ? [titleEl, Design.Type.TrackMeta(sub) with { MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f }]
                    : [titleEl],
            };
            var menu = HomeCardNav.MenuOf(in c);
            bool hasMenu = menu is not null && host is not null;
            var open = _latest!.Open;
            BoxEl shell = Controls.CardShell(new BoxEl
            {
                Direction = 1, Gap = Spacing.S, Grow = 1f,
                Padding = new Edges4(Spacing.S, Spacing.S, Spacing.S, Spacing.M),
                Children =
                [
                    new BoxEl
                    {
                        ZStack = true, ClipToBounds = !circular,
                        Children = hasMenu
                            ? [Controls.ArtworkFill(c.ImageUrl, circular ? Radii.Full : Radii.Card),
                               Controls.NowPlayingOverlay(c.Uri, () => HomeCardNav.Play(in c), 44f, centred: true),
                               Controls.MoreCorner()]
                            : [Controls.ArtworkFill(c.ImageUrl, circular ? Radii.Full : Radii.Card),
                               Controls.NowPlayingOverlay(c.Uri, () => HomeCardNav.Play(in c), 44f, centred: true)],
                    },
                    labels,
                ],
            }, () => (_latest?.Open ?? open)(c), HomeCardNav.DragOf(in c));
            shell = shell with
            {
                Key = "home-section-card:" + tier + ":" + (c.Uri.Length > 0 ? c.Uri : index.ToString(CultureInfo.InvariantCulture)),
            };
            return hasMenu ? shell.WithContextMenu(host!, menu!) : shell;
        }
    }
}

// ══ THE APPEND PRELOADER (ch 10 §8, ch 12 W12) ═══════════════════════════════════════════════════════════════════════

/// <summary>The near-tail watch a host hands <see cref="HomeModules.SectionGrid"/>: the grid feeds it its viewport's
/// geometry (off the grid's scroll handle) and it writes the host's NearTail signal only when the coarse projection moves —
/// the 24-px-floored offset × 48-px-floored content height (so an append's own growth re-evaluates nearness), both
/// through <see cref="HomeNearTail"/>. UI thread only.</summary>
public sealed class NearTailWatch(Signal<bool> nearTail)
{
    long _key = long.MinValue;

    public void Observe(float offset, float viewport, float content)
    {
        long key = HomeNearTail.Project(offset, viewport, content);
        if (key == _key) return;
        _key = key;
        bool near = HomeNearTail.IsNear(offset, viewport, content);
        if (nearTail.Peek() != near) nearTail.Value = near;
    }
}

/// <summary>A grid's tail: silent infinite scroll onto the host's existing "Show all" pipeline, through three gates —
/// (C) the tail is NEAR (<see cref="NearTail"/>, published from the grid's own scroll geometry), (B) a 300-ms arm
/// debounce re-checked when it fires, (A) never concurrently (<see cref="Loading"/>) — with a bounded 3-attempt collapse.
/// No visual footprint at all: the grid it trails is a self-scrolling virtual viewport with nothing to append a loading
/// row to. The host remounts it on every cursor change (its <c>Key</c> is the prop channel), so the attempt counter is
/// per (uri, cursor).</summary>
public sealed class HomeSectionAppendPreloader : Component
{
    const int MaxAttempts = 3;
    const float ArmDelayMs = 300f;

    /// <summary>The host's append-in-flight signal; its false edge re-arms a retry.</summary>
    public required IReadSignal<bool> Loading;
    /// <summary>True while the grid's bottom edge is within ~1.5 viewport heights of the content end.</summary>
    public required IReadSignal<bool> NearTail;
    /// <summary>The host's append verb.</summary>
    public required Action Start;

    int _attempts;
    TimerHandle _arm;
    readonly Action _fire;

    public HomeSectionAppendPreloader() => _fire = Fire;

    public override Element Render()
    {
        _arm = UseTimeout(_fire, ArmDelayMs);
        UseSignalEffect(() =>
        {
            bool near = NearTail.Value, loading = Loading.Value;
            if (!near || loading || _attempts >= MaxAttempts) { _arm.Cancel(); return; }
            _arm.Restart();   // cancel-and-restart on every gate re-evaluation
        });
        return new BoxEl();
    }

    void Fire()
    {
        if (Loading.Peek() || !NearTail.Peek() || _attempts >= MaxAttempts) return;
        _attempts++;
        Start();
    }
}

/// <summary>The Fold tile Browse's and Home's directory decks share — a title over up to three fanned covers, washed by
/// the section's first card's raw accent.</summary>
public static class HomeFoldTile
{
    /// <summary>Build a tile for <paramref name="s"/> at the shelf's fitted <paramref name="cardW"/>. The width is baked
    /// into the key (a re-fit REPLACES the tile); the loading shape is ONE explicit card-silhouette bone, because a
    /// derived shimmer would zero the covers' authored pose and drop the plate.</summary>
    public static Element Create(HomeSectionView s, float cardW, string? eyebrow, Action<HomeSectionView> open)
    {
        var cards = s.Cards;
        // The wash is the FIRST card's RAW payload accent, never lifted, never graded, never invented: 0 → no layer.
        uint accent = cards.Count > 0 ? cards[0].Accent : 0u;
        var children = new List<Element>(5);
        if (accent != 0u)
        {
            ColorF c = Design.Palette.ToColor(accent) with { A = 1f };
            children.Add(new BoxEl
            {
                HitTestVisible = false, AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch,
                Height = HomeModuleLayout.FoldCardHeight,
                Gradient = new GradientSpec(GradientShape.Radial, 0f, [new GradientStop(0f, c with { A = 0.22f }), new GradientStop(0.72f, c with { A = 0f })])
                {
                    RadialCenter = new Point2(1.0f, 0.48f), RadialRadius = new Point2(0.70f, 1.10f),
                },
            });
        }

        // A missing slot is OMITTED, never padded with a repeated cover.
        int coverCount = Math.Min(3, cards.Count);
        for (int i = 0; i < coverCount; i++)
        {
            var card = cards[i];
            HomeModuleLayout.FoldRest(i, cardW, out float x, out float y, out float rot);
            HomeModuleLayout.FoldFan(i, out float dx, out float dy, out float drot);
            children.Add(new BoxEl
            {
                Width = HomeModuleLayout.FoldCover, Height = HomeModuleLayout.FoldCover,
                OffsetX = x, OffsetY = y, Rotation = rot,
                // DELTAS on the rest pose; the token carries the reduced-motion policy — no branch anywhere here.
                WhileHover = new MotionTarget { OffsetX = dx, OffsetY = dy, Rotation = drot },
                Transition = MotionTok.ControlNormal,
                HitTestVisible = false, Shadow = Elevation.Card, ClipToBounds = true,
                Corners = CornerRadius4.All(Radii.Control),
                Children = [HomeCards.Art(in card, HomeModuleLayout.FoldCover, HomeModuleLayout.FoldCover, Radii.Control, decodePx: 128)],
            });
        }

        string title = !string.IsNullOrWhiteSpace(s.Title) ? s.Title! : Loc.Get(Strings.Home.Sections);
        Element titleEl = Design.Type.FoldTitle(title) with { Wrap = TextWrap.Wrap, MaxLines = 3, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f };
        // The copy column is painted LAST, over the covers; hit-test off so the ROOT owns the one hyperlink.
        children.Add(new BoxEl
        {
            HitTestVisible = false, Direction = 1, Justify = FlexJustify.End, Gap = Spacing.XS,
            Height = HomeModuleLayout.FoldCardHeight, AlignSelf = FlexAlign.Stretch,
            // Edges4 is POSITIONAL (L, T, R, B): left 20, top 20, right 12, bottom 18 — NOT the prototype's CSS order.
            Padding = new Edges4(Spacing.XL, Spacing.XL, Spacing.M, 18f),
            MaxWidth = cardW * HomeModuleLayout.FoldCopyMaxFrac, Grow = 1f, Basis = 0f, MinWidth = 0f,
            Children = eyebrow is { Length: > 0 }
                ? [Design.Type.Eyebrow(eyebrow) with { Color = Tok.AccentTextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis }, titleEl]
                : [titleEl],
        });

        Element root = new BoxEl
        {
            ZStack = true, Width = cardW > 0f ? cardW : float.NaN, Height = HomeModuleLayout.FoldCardHeight, MinWidth = 0f,
            ClipToBounds = true, Corners = Radii.CardAll, Fill = Tok.FillCardDefault, HoverFill = Tok.FillCardSecondary,
            BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault, Shadow = Elevation.Card,
            OnClick = () => open(s), Cursor = CursorId.Hand, Role = AutomationRole.Hyperlink, Focusable = true,
            FocusVisualMargin = Design.FocusInsetBordered,
            Key = "home-fold-tile:" + (s.Uri ?? "") + ":" + cardW.ToString(CultureInfo.InvariantCulture),
            Children = children.ToArray(),
        };
        Element bone = new BoxEl
        {
            Width = cardW > 0f ? cardW : HomeModuleLayout.FoldCardMin, Height = HomeModuleLayout.FoldCardHeight, MinWidth = 0f,
            Corners = Radii.CardAll, Fill = SkeletonStyle.Default.BarColor, IsEnabled = false, HitTestVisible = false,
        };
        return root.Skel(bone);
    }
}
