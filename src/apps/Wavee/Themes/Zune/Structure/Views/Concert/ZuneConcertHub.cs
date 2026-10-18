// ── Themes/Zune/Structure/Views/Concert/ZuneConcertHub.cs — Zune's `Pages.Concerts` and `Concert.Tail` (themes plan 6.3; P5 Z6b2) ────────────
//
// No board (built from Zune's parts, owner Q18). One column on the frame's gutter, capped at 2400 and centred: the page's title (the 40/48 light page
// voice, lowercased "concerts") over its quiet line, the filter row PINNED under the band (`ZuneConcertFilterBar`), and the feed — shelves of event
// tiles, playlist promos, the virtualized "all events" grid and the append tail — in its skeleton region. The band is outside the viewport, so
// nothing is cut or reserved at the top. `Concert.HubHost` (core) keeps the filter tuple and its feed subject, the demand, the near-tail watch, the
// append gate, the readiness verdicts and the feed's plan (`ConcertHubBody`); this file draws the plan.
//
// THE FEED. The grid is the LazyGrid over the page scroll (min column 200 · 220 at 4K, 16 apart, the tile's stated extent + the row gap, overscan 3):
// an append grows the COUNT the host's memo publishes, never the realized tree; it is keyed per subject. The pending silhouette is four shelf tiles,
// then six grid tiles in a PLAIN AutoGrid — never the LazyGrid, which must not mount inside a shimmer derivation.
//
// THE COUNT TICKER: the events figure, a FIXED right-aligned number box + " events"; a new target eases the shown value 380 ms cubic-out on the
// FRAME clock; the per-frame clock child mounts only while a tween runs (keyed per target, so a retarget restarts cleanly).

using System.Globalization;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Theming.Roles;
using static FluentGpu.Dsl.Ui;
using Cells = Wavee.Themes.Zune.ZuneConcertCells;
using Rules = Wavee.Themes.Zune.ZuneConcertRules;

namespace Wavee.Themes.Zune;

/// <summary>Zune's Concerts hub.</summary>
internal static class ZuneConcertHub
{
    static readonly Func<ConcertShelfItem, int, string> s_itemKey = static (item, _) => item.Slot.ToString(CultureInfo.InvariantCulture);

    static readonly Func<ConcertShelfItem, int, float, Element> s_concertCard = static (item, _, _) =>
    {
        int slot = item.Slot;
        return Cells.Tile(Concert.TileOf(new Concert(slot)), () => ConcertVerbs.OpenConcert(slot));
    };

    static readonly Func<ConcertShelfItem, int, float, Element> s_promoCard = static (item, _, w) => PromoCard(item.Slot, w);

    static readonly SurfaceShape s_promoShape = Shape.Shelf() with { TitleLines = Rules.PromoTitleLines };

    /// <summary>The registered template of <see cref="ViewKeys.ConcertsHub"/>.</summary>
    public static Element Template(ConcertHubProps props, in ViewContext ctx)
    {
        var facts = ctx.Tier();
        var lay = ZuneConcertTiers.Of(in facts);
        float gutter = ZuneTiers.FrameOf(in facts).Gutter;
        Element region = Cells.Region(props.Pending, props.Failed, () => props.Body is { } body ? FeedOf(props, body, lay) : new BoxEl(), () => FeedSeed(lay),
            () => Controls.Vacancy(Controls.VacancyVoice.Error, onAction: props.Retry), "concert-hub");
        var content = new BoxEl
        {
            Key = "concert-hub:page", Direction = 1, Gap = lay.ZoneGap, MinWidth = 0f,
            Children =
            [
                Head(),
                Embed.Comp(new ZuneConcertFilterBar.Args(props.Bar, props.ScrollScope), static () => new ZuneConcertFilterBar()) with { Key = "concert-filter-bar" },
                new BoxEl { Direction = 1, MinWidth = 0f, Children = [region] },
            ],
        };
        return ScrollView(Cells.PageColumn(content, gutter)) with
        {
            Grow = 1f, MinHeight = 0f, ScrollKey = props.ScrollScope + "concert-hub", Handle = props.Scroll,
        };
    }

    /// <summary>The page's head: the eyebrow ("live music"), the title in the page voice (lowercased as a heading), the quiet line under it.</summary>
    static BoxEl Head() => new()
    {
        Key = "concert-hub:head", Direction = 1, MinWidth = 0f, Gap = Rules.HeadLineGap,
        Children =
        [
            R.Type.DenseMeta(Loc.Get(Strings.Concerts.LiveMusic)) with
            {
                Color = Prop.Of(static () => R.Ink.AccentDecor), Case = R.Case.For(LabelKind.Label), MaxLines = 1, Wrap = TextWrap.NoWrap,
            },
            R.Type.PageHero(Loc.Get(Strings.Concerts.Title)) with
            {
                Case = R.Case.For(LabelKind.Heading), Color = Tok.TextPrimary, MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
            },
            R.Type.TrackMeta(Loc.Get(Strings.Concerts.Subtitle)) with
            {
                Case = R.Case.For(LabelKind.Label), MaxLines = 2, Wrap = TextWrap.Wrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
            },
        ],
    };

    // ── the feed's body (§1.1: shelves · promos · the virtualized grid · the tail) ─────────────────────────────────

    static Element FeedOf(ConcertHubProps props, ConcertHubBody body, ZuneConcertLayout lay)
    {
        switch (body.Kind)
        {
            case ConcertHubBodyKind.NoLocation:
                // W3: no place ⇒ the prompt, whose one quiet action anchors the picker at the WHERE PILL.
                return Controls.Vacancy(Controls.VacancyVoice.Empty, title: Loc.Get(Strings.Concerts.EmptyTitle),
                    subtitle: Loc.Get(Strings.Concerts.EmptyWithoutLocation), actionLabel: Loc.Get(Strings.Concerts.Location.Set),
                    onAction: props.OpenPicker);
            case ConcertHubBodyKind.Empty:
                return Controls.Vacancy(Controls.VacancyVoice.Empty, title: Loc.Get(Strings.Concerts.EmptyTitle),
                    subtitle: Loc.Get(Strings.Concerts.EmptyForLocation));
        }

        var kids = new List<Element>(body.Blocks.Count);
        foreach (var block in body.Blocks)
        {
            switch (block.Kind)
            {
                case ConcertBlockKind.Shelf: kids.Add(ConcertShelf(block)); break;
                case ConcertBlockKind.Promos: kids.Add(PromoShelf(block)); break;
                case ConcertBlockKind.Grid: kids.Add(EventsGrid(props, block.Feed, lay)); break;
                default: kids.Add(props.Tail(block.Key)); break;
            }
        }
        return new BoxEl { Direction = 1, Gap = lay.ZoneGap, MinWidth = 0f, Children = kids.ToArray() };
    }

    /// <summary>A Nearby / Recommended shelf: measured PagedShelf, headerGap 8, ≤ 16 tiles. "Near you" is Nearby's
    /// caption; every other non-grid kind is "Recommended for you" (§1.1).</summary>
    static Element ConcertShelf(ConcertFeedBlock block)
        => PagedShelf.Create(block.Items, s_concertCard,
               header: Cells.SectionHead(Loc.Get(block.Nearby ? Strings.Concerts.NearYou : Strings.Concerts.RecommendedForYou)),
               headerGap: Rules.SectionHeadGap, measured: true, keyOf: s_itemKey, maxItems: Rules.ShelfMax)
           with { Key = block.Key };

    static Element PromoShelf(ConcertFeedBlock block)
        => PagedShelf.Create(block.Items, s_promoCard, onInvoke: static (item, _) => ConcertVerbs.OpenPlaylist(item.Slot),   // the slot owns the click
               header: Cells.SectionHead(Loc.Get(Strings.Concerts.PlaylistsForScene)),
               headerGap: Rules.SectionHeadGap, measured: true, keyOf: s_itemKey, maxItems: Rules.ShelfMax)
           with { Key = block.Key };

    /// <summary>The playlist promo (§0 #12): the shared shelf card — cover, a 2-line title, its source as the caption —
    /// with the play FAB (pause/resume while the playlist plays, <c>PlayOrToggleContext</c>) and the shared playlist
    /// container menu (<c>Menus.Container</c>); the one concert-surface drag source, because it stands for a real playlist. The shelf is measured, so the card sizes
    /// itself and nothing here estimates an extent.</summary>
    static Element PromoCard(int slot, float cardW)
    {
        var promo = ConcertVerbs.PromoOf(slot);
        string uri = promo.Uri, title = promo.Title, source = promo.Source;
        string? cover = promo.Cover;
        var data = new Controls.CardData(uri, title, null, cover,
            OnClick: () => ConcertVerbs.OpenPlaylist(slot), OnPlay: () => ConcertVerbs.PlayPlaylist(uri),
            Drag: Drag.Source(() => new DragPayload(DragKind.Playlist, uri, uri, title, new EntityRef(EntityKind.Playlist, slot), ArtUrl: cover)))
        {
            Caption = source,
            Menu = () =>
            {
                var target = ActionTarget.ForPlaylist(new Playlist(slot).Uri, title);
                return Menus.Container(in target, cover, source);
            },
        };
        return Controls.Surface(data, s_promoShape, cardW) with { Key = "promo:" + slot.ToString(CultureInfo.InvariantCulture) };
    }

    /// <summary>"all events": the LazyGrid over the page scroll (the tier's minimum column, 16 apart, the tile's stated extent + the row gap as the row
    /// extra, overscan 3). An append grows the COUNT read through the host's memo, never the realized tree; keyed per subject.</summary>
    static Element EventsGrid(ConcertHubProps props, int feed, ZuneConcertLayout lay) => new BoxEl
    {
        Key = "hub-grid", Direction = 1, Gap = Rules.SectionHeadGap, MinWidth = 0f,
        Children =
        [
            Cells.SectionHead(Loc.Get(Strings.Concerts.AllEvents)),
            Embed.Comp(() => new LazyGrid(count: props.GridCount, cell: (index, _) => GridCell(props, index), ensureRange: static (_, _) => { },
                minColWidth: lay.GridMinColumn, gap: Rules.GridGap,
                rowExtra: Rules.GridRowExtra, overscanRows: Rules.GridOverscanRows)) with { Key = "hub-grid-lazy:" + feed.ToString(CultureInfo.InvariantCulture) },
        ],
    };

    static Element GridCell(ConcertHubProps props, int index)
    {
        int slot = props.GridSlotAt(index);
        if (slot <= Table.None) return new BoxEl();   // an append reconcile's skew
        return Cells.Tile(Concert.TileOf(new Concert(slot)), () => ConcertVerbs.OpenConcert(slot));
    }

    /// <summary>The pending silhouette (§9 #4, #6): four shelf tiles, then six grid tiles in a PLAIN AutoGrid — never the
    /// LazyGrid, which must not mount inside a shimmer derivation.</summary>
    static Element FeedSeed(ZuneConcertLayout lay)
    {
        var shelf = new Concert.TileData[Rules.SeedShelfTiles];
        for (int i = 0; i < shelf.Length; i++) shelf[i] = Concert.SeedTile("seed:n:" + i.ToString(CultureInfo.InvariantCulture));
        var cards = new Element[Rules.SeedGridTiles];
        for (int i = 0; i < cards.Length; i++) cards[i] = Cells.Tile(Concert.SeedTile("seed:a:" + i.ToString(CultureInfo.InvariantCulture)), null);
        return new BoxEl
        {
            Direction = 1, Gap = lay.ZoneGap, MinWidth = 0f,
            Children =
            [
                PagedShelf.Create(shelf, static (d, _, _) => Cells.Tile(d, null), header: Cells.SectionHead(Loc.Get(Strings.Concerts.NearYou)),
                    headerGap: Rules.SectionHeadGap, measured: true, keyOf: static (d, _) => d.Key),
                new BoxEl
                {
                    Direction = 1, Gap = Rules.SectionHeadGap,
                    Children = [Cells.SectionHead(Loc.Get(Strings.Concerts.AllEvents)), AutoGrid(lay.GridMinColumn, Rules.GridGap, float.NaN, cards)],
                },
            ],
        };
    }

    // ══ THE APPEND TAIL (W11; §0 #15): the gate is core (`Concert.TailHost`), this is its picture ═════════════════

    static readonly Func<bool> s_always = static () => true, s_never = static () => false;
    static readonly Func<Element> s_emptyTail = static () => new BoxEl();

    /// <summary>The registered template of <see cref="ViewKeys.ConcertTail"/>: nothing, a 16-DIP spacer with no shimmer subtree (no standing pulse), or
    /// four tiles of the grid's family shimmering.</summary>
    public static Element TailTemplate(ConcertTailProps props, in ViewContext ctx)
    {
        var lay = ZuneConcertTiers.Of(ctx.Tier());
        return props.State switch
        {
            ConcertTailState.None => new BoxEl(),
            ConcertTailState.Spacer => new BoxEl { Height = Spacing.L },
            _ => Cells.Region(s_always, s_never, s_emptyTail, () => TailShimmer(lay), null, "concert-hub-append"),
        };
    }

    static Element TailShimmer(ZuneConcertLayout lay)
    {
        var cards = new Element[Rules.SeedTailTiles];
        for (int i = 0; i < cards.Length; i++) cards[i] = Cells.Tile(Concert.SeedTile("append:" + i.ToString(CultureInfo.InvariantCulture)), null);
        return AutoGrid(lay.GridMinColumn, Rules.GridGap, float.NaN, cards);
    }
}

/// <summary>The events figure: a FIXED right-aligned number box + " events". A new target eases the shown value 380 ms cubic-out on the FRAME clock
/// (<see cref="FrameTime.NowQpc"/>); the per-frame clock child mounts only while a tween runs (keyed per target, so a retarget restarts cleanly).</summary>
internal sealed class ZuneConcertCountTicker : Component
{
    const double TweenMs = Rules.TickerTweenMs;
    readonly Signal<double> _shown = new(0);
    readonly Func<ZuneConcertTickerClock> _clock;
    readonly string _suffix = " " + Loc.Get(Strings.Concerts.Filter.Events);
    IReadSignal<int?> _target = null!;
    int _to = int.MinValue;
    double _from;
    long _startQpc;

    public ZuneConcertCountTicker()
    {
        Action step = Step;
        _clock = () => new ZuneConcertTickerClock(step);
    }

    public override Element Render()
    {
        _target = UseProps<IReadSignal<int?>>();
        int goal = _target.Value ?? 0;
        double shown = _shown.Value;
        bool animating = (int)Math.Round(shown) != goal;
        if (animating && _to != goal) { _to = goal; _from = shown; _startQpc = 0; }
        var kids = new Element[animating ? 3 : 2];
        kids[0] = new BoxEl
        {
            Width = Rules.TickerNumberW, Shrink = 0f, Direction = 0, Justify = FlexJustify.End,
            Children = [R.Type.TrackTitle(((int)Math.Round(shown)).ToString("N0", CultureInfo.CurrentCulture)) with { MaxLines = 1 }],
        };
        kids[1] = Body(_suffix) with { Color = Tok.TextSecondary, Case = R.Case.For(LabelKind.Label), MaxLines = 1 };
        if (animating) kids[2] = Embed.Comp(_clock) with { Key = "count-tick:" + goal.ToString(CultureInfo.InvariantCulture) };
        return new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Gap = Rules.TickerGap, Children = kids };
    }

    void Step()
    {
        long now = FrameTime.NowQpc;
        if (_startQpc == 0) _startQpc = now;
        double p = Math.Min(1.0, (now - _startQpc) * 1000.0 / System.Diagnostics.Stopwatch.Frequency / TweenMs);
        p = 1.0 - Math.Pow(1.0 - p, 3);
        _shown.Value = p >= 1.0 ? _to : _from + (_to - _from) * p;
    }
}

/// <summary>A 0-size leaf subscribed to the frame tick while mounted (the Track.Table ticker shape): the mount run only
/// subscribes; every later frame calls the step.</summary>
internal sealed class ZuneConcertTickerClock(Action step) : Component
{
    bool _subscribed;

    public override Element Render()
    {
        var tick = UseContextSignal(FrameClock.Tick);
        UseSignalEffect(() =>
        {
            _ = tick.Value;
            if (!_subscribed) { _subscribed = true; return; }
            step();
        });
        return new BoxEl { HitTestVisible = false, Width = 0f, Height = 0f };
    }
}
