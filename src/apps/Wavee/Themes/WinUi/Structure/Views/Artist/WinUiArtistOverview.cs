// ── Themes/WinUi/Structure/Views/Artist/WinUiArtistOverview.cs — WinUI's artist overview (themes plan 6.2; P6 W6a) ────────────────────────────────────
//
// Board W-Artist: under the pivot the OVERVIEW is two columns 36 apart, Popular (the chart, weight 1.2) | the latest release card over the Discography preview (weight 1),
// stacking below a 1100-wide page (the column count is `WinUiArtistTiers.OverviewColumns`, held 24 once two); at Huge (W-Artist-Huge) three: Popular | Discography | the latest
// release (and the upcoming card and the compact pick under it; at two columns and one they follow Discography). The Discography preview is three covers of the chosen type under two combos (type, order) and "Show all N albums"; at Huge it
// is two titled rows (Albums, Singles & EPs) under "Newest" and "Show all". The full facets (the core's grid and album drawer) follow as the Albums, Singles & EPs and Compilations sections.
//
// About and Fans stay sections under the overview (their pivot anchors live there): the Huge board stacks them in the third column; here the third column holds the latest release alone.

using System.Globalization;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;

namespace Wavee.Themes.WinUi;

internal static class WinUiArtistOverview
{
    /// <summary>The overview block at the page's measure: the columns by the tier's rule, each keyed so a crossing re-parents nothing it need not.</summary>
    public static Element Block(ArtistPageProps p, WinUiArtistLatch latch, WidthTier tier, float inset, in TierFacts facts)
    {
        Artist a = p.Artist;
        bool popular = false, latest = false, upcoming = false;
        for (int i = 0; i < p.Sections.Count; i++)
        {
            var s = p.Sections[i].Section;
            popular |= s == ArtistSection.Popular;
            latest |= s == ArtistSection.LatestRelease;
            upcoming |= s == ArtistSection.Upcoming;
        }
        var accentSignal = p.AccentSignal;
        var accentFn = p.AccentFn;
        var overlay = p.Overlay;
        var surface = latch.Surface;
        long stamp = PreviewStamp(a);
        Func<int, float, PreviewState, PopularHold, Element> build = (rung, slot, state, hold) =>
        {
            int columns = latch.Columns = WinUiArtistTiers.OverviewColumnsOf(rung, tier);
            Element? chart = popular ? new BoxEl { Key = "ov:chart", Direction = 1, MinWidth = 0f, Children = [Artist.Chart(a, accentSignal)] } : null;
            // W-Artist: the latest release, then Discography; the upcoming card and the artist's pick (no board draws one) follow under Discography, the pick
            // compact (no photograph band) so it never outweighs the column.
            float target = WinUiDetailRules.TargetOf(tier);
            Element? latestCard = latest ? LatestCard(a, target) with { Layout = WinUiArtistMotion.Block } : null;
            var after = new List<Element>(2);
            if (upcoming)
                after.Add(WinUiArtistSections.SectionBlock(Loc.Get(Strings.Artist.Upcoming), WinUiArtistBanners.UpcomingCard(a, accentFn, wide: false))
                    with { Key = "ov:upcoming", Layout = WinUiArtistMotion.Block });
            // The compact pick is one composition in every arrangement, so it moves with the restack like any other block.
            if (a.HasPick)
                after.Add(WinUiArtistPick.Card(a, accentFn, horizontal: false, overlay, surface, target, photo: false) with { Key = "ov:pick", Layout = WinUiArtistMotion.Block });
            // The preview's covers are sized from its column's width, worked out here from the slot and the arrangement (not measured by the preview), so
            // the commit that restacks lays the side column out at its final height and the overview's eased height aims at the right number.
            Element preview = Embed.Comp(new PreviewProps(a, stamp, columns >= 3, target, PreviewWidth(slot, columns), state), static () => new PreviewHost()) with { Key = "ov:preview" };

            var side = new List<Element>(4);
            if (latestCard is not null) side.Add(latestCard);
            if (columns >= 3)
            {
                side.AddRange(after);
                return Grid(0, WinUiArtistRules.HugeColumnGap,
                    Popular(PopularWidth(slot, columns), chart, hold, row: true),
                    Column("ov:col:discography", WinUiArtistRules.PopularGrow, [preview]),
                    Column(SideKey(columns), WinUiArtistRules.OtherGrow, side.ToArray()));
            }

            // One column and two are one grid: it turns from a row into a column and Popular keeps its node (it resizes in place). The side column is keyed
            // by the arrangement, so a restack fades the old one out where it stands and the new one in at its slot (WinUiArtistMotion): nothing slides across
            // Popular. Stacked, the columns stop growing and stack at their content's height: Popular, then the side column, one section gap apart.
            side.Add(new BoxEl { Key = "ov:preview:slot", Direction = 1, MinWidth = 0f, Layout = WinUiArtistMotion.Block, Children = [preview] });
            side.AddRange(after);
            bool two = columns == 2;
            float grow = two ? 1f : 0f;
            Element sideColumn = Column(SideKey(columns), grow * WinUiArtistRules.OtherGrow, side.ToArray());
            // Stacked without a chart, an empty Popular column would add a section gap above the side column.
            if (!two && chart is null) return Grid(1, WinUiArtistRules.SectionGap, sideColumn);
            return Grid((byte)(two ? 0 : 1), two ? WinUiArtistRules.ColumnGap : WinUiArtistRules.SectionGap,
                Popular(two ? PopularWidth(slot, columns) : float.NaN, chart, hold, two),
                sideColumn);
        };
        // Popular's laid-out width in an arrangement (the slot stacked), NaN without a chart: what a restack's hold compares.
        Func<int, float, float> popularWidth = (rung, slot) => popular ? PopularWidth(slot, WinUiArtistTiers.OverviewColumnsOf(rung, tier)) : float.NaN;
        return Embed.Comp(new GridProps(build, popularWidth, inset, p.Width, WinUiArtistTiers.OverviewRungOf(in facts)),
            static () => new GridHost()) with { Key = "artist:overview" };
    }

    /// <summary>Popular's width in a <paramref name="slot"/>-wide overview of <paramref name="columns"/> columns: the slot stacked; the slot less the
    /// side column and the gap beside it; the discography column's share at Huge.</summary>
    static float PopularWidth(float slot, int columns) => columns switch
    {
        <= 1 => slot,
        2 => MathF.Max(1f, slot - WinUiArtistRules.ColumnGap - PreviewWidth(slot, columns)),
        _ => PreviewWidth(slot, columns),
    };

    /// <summary>The preview's width in a <paramref name="slot"/>-wide overview of <paramref name="columns"/> columns: the slot stacked; the side column's
    /// share beside Popular; the discography column's share at Huge.</summary>
    static float PreviewWidth(float slot, int columns)
    {
        if (columns <= 1) return slot;
        if (columns == 2)
            return MathF.Max(1f, (slot - WinUiArtistRules.ColumnGap) * WinUiArtistRules.OtherGrow / (WinUiArtistRules.PopularGrow + WinUiArtistRules.OtherGrow));
        float weights = 2f * WinUiArtistRules.PopularGrow + WinUiArtistRules.OtherGrow;
        return MathF.Max(1f, (slot - 2f * WinUiArtistRules.HugeColumnGap) * WinUiArtistRules.PopularGrow / weights);
    }

    /// <summary>The overview's grid: a row of columns (two, three at Huge) or, stacked, a column of them. Its height eases through real layout
    /// (<see cref="WinUiArtistMotion.OverviewHeight"/>) so the sections under it glide when a restack changes it. A reflow clips its box for its
    /// life, so the box reaches past its top and its end by Popular's hit room (padding in, margin out: nothing moves), or that clip cut the room
    /// Popular's own clip keeps for the chart head's chevrons for the length of every restack, and their hit floor with it.</summary>
    static Element Grid(byte direction, float gap, params Element[] columns) => new BoxEl
    {
        Key = "ov:grid", Direction = direction, Gap = gap, MinWidth = 0f, AlignItems = direction == 0 ? FlexAlign.Start : FlexAlign.Stretch,
        Padding = new Edges4(0f, WinUiArtistRules.PopularHitRoom, WinUiArtistRules.PopularHitRoom, 0f),
        Margin = new Edges4(0f, -WinUiArtistRules.PopularHitRoom, -WinUiArtistRules.PopularHitRoom, 0f),
        Layout = WinUiArtistMotion.OverviewHeight, Children = columns,
    };

    /// <summary>A column: in a row it grows by its weight from a zero basis; stacked (<paramref name="grow"/> 0) it takes its content's height.</summary>
    static BoxEl Column(string key, float grow, Element[] kids) => new BoxEl
    {
        Key = key, Direction = 1, Gap = WinUiArtistRules.SectionGap, Grow = grow, Basis = grow > 0f ? 0f : float.NaN, MinWidth = 0f,
        // Popular's height never follows its width (the chart's rows are fixed), so it re-lays at the eased width once the side column has faded; the side
        // column fades through a restack (it is keyed by the arrangement).
        Layout = key == PopularKey ? WinUiArtistMotion.Column : WinUiArtistMotion.Side, Children = kids,
        // Popular holds its old width while the side column fades out (laid out at it when the restack widens it, presented at it when the restack
        // narrows it): the clip keeps the chart's far edge (the count, the durations) inside the held width instead of over the outgoing column.
        ClipToBounds = key == PopularKey,
        // The clip reaches past the chart head's corner by the chevrons' touch slop, up into the gap above and out past the end (the padding takes it
        // back, so nothing moves): the pager's next chevron sits on the column's top-right corner, and a clip flush with it would cut its hit floor at a
        // Compact window.
        Padding = key == PopularKey ? new Edges4(0f, WinUiArtistRules.PopularHitRoom, WinUiArtistRules.PopularHitRoom, 0f) : default,
        Margin = key == PopularKey ? new Edges4(0f, -WinUiArtistRules.PopularHitRoom, -WinUiArtistRules.PopularHitRoom, 0f) : default,
    };

    /// <summary>The Popular column, <paramref name="width"/> wide beside the other columns (its weight's share of the slot; NaN stacked: the slot).
    /// An explicit width, not a grow from a zero basis: its restack resize re-lays the chart at the eased width it writes into the column's width, and
    /// a row re-solved under it (the page easing through a docked pane's slide) hands a growing column its flex share instead, which presented
    /// Popular at its new width for the whole of the resize's delay, then back at the old one when the resize started.
    /// <para>A restack that WIDENS it (two columns or three to one: a docked pane opening, a window narrowing) lays it out at its old width while the
    /// side column fades out (<paramref name="hold"/>), then releases it into an undelayed resize. Laid out at the new width at once and only PRESENTED
    /// at the old one for the resize's delay, the chart's rows (a virtual strip that fits its cells to the width it last measured) were laid out at
    /// the new width under the held clip: the durations and the pager sat past the clip's edge for the whole delay.</para></summary>
    /// <para>Beside the other columns (<paramref name="row"/>) it may shrink on the main axis (its width); stacked it never does: the main axis is then
    /// its height, which the overview's eased height would otherwise squeeze while it grows.</para>
    static Element Popular(float width, Element? chart, PopularHold hold, bool row)
        => Column(PopularKey, 0f, chart is null ? [] : [chart]) with
        {
            Width = float.IsNaN(hold.Width) ? width : hold.Width, Shrink = row ? 1f : 0f,
            Layout = hold.Released ? WinUiArtistMotion.ColumnReleased : WinUiArtistMotion.Column,
        };

    /// <summary>Popular's hold through a widening restack: the width it is laid out at until the side column has faded out (NaN: none), and whether
    /// the last restack's hold has been released (its resize then runs without the delay the hold spent).</summary>
    readonly record struct PopularHold(float Width, bool Released);

    const string PopularKey = "ov:col:popular";

    /// <summary>The side column's key in an arrangement of <paramref name="columns"/> columns: a restack replaces it, which is the fade-through.</summary>
    static string SideKey(int columns) => columns switch { <= 1 => "ov:col:side:1", 2 => "ov:col:side:2", _ => "ov:col:side:3" };

    /// <summary>The preview's choices (the type and the order), owned by the grid host so they outlive the side column a restack replaces.</summary>
    sealed class PreviewState
    {
        public readonly Signal<int> Facet = new(0);
        public readonly Signal<bool> Oldest = new(false);
    }

    // ── the column rung ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The grid host's props: the arrangement for a column rung, the page inset (the page is the slot plus the inset either side), the page
    /// width the host measured (the slot's seed before the first measure) and the committed column rung (<see cref="WinUiArtistTiers.OverviewRung"/>).
    /// The builder is a live closure (a fresh one rebuilds with the page).</summary>
    sealed record GridProps(Func<int, float, PreviewState, PopularHold, Element> Build, Func<int, float, float> PopularWidth, float Inset, float Seed,
        int Rung);

    /// <summary>The overview's arrangement. The column rung is the policy's content-axis mark, which the core resolves from the content width the shell
    /// computes (the width the page lands at) and commits right before FLIP capture: on a docked pane's open or close that is the slide's first frame,
    /// on a window resize the first frame that is not resized.
    /// <para>The overview is laid out at the page's LIVE width. A docked pane's slide eases the page's width through real layout and the restack commits
    /// on its first frame: the parts' transitions then run against the live layout (the engine re-aims a revealed size and keeps a FLIP's offset over the
    /// re-solve while a sibling reflow resizes the page), so the grid narrows with the page instead of jumping to the landing width and waiting there
    /// while the page caught up with it.</para></summary>
    sealed class GridHost : Component
    {
        readonly Signal<float> _slot = new(0f);   // a version: the width a render uses is _slotNow
        readonly PreviewState _state = new();
        readonly Action<RectF> _onBounds;
        readonly Signal<int> _held = new(0);   // a version: bumped when Popular's hold below is let go
        readonly Action _releaseHold;
        float _inset, _measured, _slotNow;
        float _hold = float.NaN;   // Popular's old width, held through a widening restack's fade-out (NaN: none)
        bool _holdReleased, _following;   // _following: a restack's rung is ahead of the page's width (the overview follows the live width)
        int _rung = -1, _holds;

        public GridHost()
        {
            _releaseHold = () =>
            {
                if (float.IsNaN(_hold)) return;
                _hold = float.NaN;
                _holdReleased = true;
                _held.Value++;
            };
            _onBounds = r =>
            {
                if (!(r.W > 0f)) return;
                _measured = r.W;
                // A width across the column edge from the committed rung waits for the restack: re-rendering the preview at it now (an in-frame rebuild with
                // no FLIP capture) would snap the overview's height a frame or two before the restack animates it. After a restack the rung is the
                // one the page LANDS in, ahead of the page's width (a docked pane's slide): the overview follows the live width until the width
                // reaches the rung, so its columns' targets move with the page (the engine re-aims Popular's resize at each).
                bool crossing = _rung >= 0 && WinUiArtistTiers.OverviewLadder.Resolve(r.W + 2f * _inset, _rung) != _rung;
                if (!crossing) _following = false;
                if ((!crossing || _following) && MathF.Abs(r.W - _slotNow) > 0.5f) { _slotNow = r.W; _slot.Value = r.W; }
            };
        }

        public override Element Render()
        {
            var props = UseProps<GridProps>();
            _inset = props.Inset;
            bool restack = _rung >= 0 && props.Rung != _rung;
            int previousRung = _rung;
            float previousSlot = _slotNow;
            _rung = props.Rung;
            _ = _slot.Value;
            _ = _held.Value;
            if (restack && _measured > 0.5f) { _slotNow = _measured; _following = true; }
            else if (_measured > 0.5f && MathF.Abs(_measured - _slotNow) > 0.5f
                && WinUiArtistTiers.OverviewLadder.Resolve(_measured + 2f * props.Inset, _rung) == _rung) _slotNow = _measured;
            float slot = _slotNow;
            if (!(slot > 0.5f)) slot = MathF.Max(1f, props.Seed - 2f * props.Inset);
            if (restack)
            {
                // A restack that widens Popular holds it at its old width while the side column fades out, then releases it (Popular's summary).
                float from = previousSlot > 0.5f ? props.PopularWidth(previousRung, previousSlot) : float.NaN;
                float to = props.PopularWidth(_rung, slot);
                _holdReleased = false;
                _hold = from + 0.5f < to ? from : float.NaN;
                if (!float.IsNaN(_hold)) _holds++;
            }
            UseTimeout(_releaseHold, WinUiArtistMotion.FadeOutMs, DepKey.From(_holds));
            // The measured box follows the page, and the overview inside it with it.
            return new BoxEl
            {
                Direction = 1, MinWidth = 0f, OnBoundsChanged = _onBounds,
                Children = [new BoxEl
                {
                    Direction = 1, MinWidth = 0f,
                    Children = [props.Build(_rung, slot, _state, new PopularHold(_hold, _holdReleased))],
                }],
            };
        }
    }

    // ── the latest release ───────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The latest release card (W-Artist): r8 on the card fill, a 64 cover, "Latest release" over the title over "Single · 12 Jun 2026", Play at 32; the card opens the release.</summary>
    static Element LatestCard(Artist a, float target)
    {
        Album al = a.Latest;
        string title = al.Title;
        string uri = al.Uri.Text;
        string? cover = Controls.ArtUrl(al.ImageId);
        string date = DiscoCardText.ReleaseDateLabel(al);
        string sub = Detail.Text.KindLabel(al.Kind) + (date.Length > 0 ? WinUiDetailRules.MetaLead + date : "");
        var album = al;
        var id = al.Id;
        return new BoxEl
        {
            Key = "ov:latest:" + uri, Direction = 0, AlignItems = FlexAlign.Center, Gap = WinUiArtistRules.LatestGap, MinWidth = 0f,
            Padding = Edges4.All(WinUiArtistRules.LatestPad), Corners = CornerRadius4.All(WinUiDetailRules.CardRadius),
            Fill = Prop.Of(static () => Tok.FillCardDefault), HoverFill = Prop.Of(static () => Tok.FillCardSecondary),
            BorderWidth = 1f, BorderColor = Prop.Of(static () => Tok.StrokeCardDefault),
            Role = AutomationRole.Button, Focusable = true, FocusVisualMargin = Design.FocusInsetBordered, Cursor = CursorId.Hand,
            OnClick = () => Track.GoToAlbum(album),
            Children =
            [
                new BoxEl
                {
                    Width = WinUiArtistRules.LatestCoverEdge, Height = WinUiArtistRules.LatestCoverEdge, Shrink = 0f,
                    Corners = CornerRadius4.All(WinUiArtistRules.LatestCoverRadius), ClipToBounds = true,
                    Children = [Controls.Artwork(cover, WinUiArtistRules.LatestCoverEdge, WinUiArtistRules.LatestCoverEdge, WinUiArtistRules.LatestCoverRadius, decodePx: WinUiArtistRules.LatestDecode)],
                },
                new BoxEl
                {
                    Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Gap = WinUiArtistRules.CopyGap,
                    Children =
                    [
                        Line(Loc.Get(Strings.Artist.LatestRelease), WinUiArtistRules.LatestEyebrow, WinUiArtistRules.LatestEyebrowLine, 400, Tok.TextSecondary),
                        Line(title, WinUiArtistRules.LatestTitle, WinUiArtistRules.LatestTitleLine, 600, Tok.TextPrimary),
                        Line(sub, WinUiArtistRules.LatestSub, WinUiArtistRules.LatestSubLine, 400, Tok.TextSecondary),
                    ],
                },
                Button.Create(Loc.Get(Strings.Artist.Play), () => ArtistVerbs.PlayOrToggle(id), ButtonAppearance.Standard) with { Key = "ov:latest:play", Shrink = 0f, Height = target },
            ],
        };
    }

    static TextEl Line(string text, float size, float line, ushort weight, ColorF color) => new(text)
    {
        Size = size, LineHeight = line, Weight = weight, Color = color, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
    };

    // ── the discography preview ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The preview's gate: a stamp over the three facets' slots and their rows' versions (a landing release re-renders it).</summary>
    static long PreviewStamp(Artist a)
    {
        var h = new HashCode();
        h.Add(a.Slot);
        foreach (int s in a.AlbumSlots) { h.Add(s); h.Add(new Album(s).Version); }
        foreach (int s in a.SingleSlots) { h.Add(s); h.Add(new Album(s).Version); }
        foreach (int s in a.CompilationSlots) { h.Add(s); h.Add(new Album(s).Version); }
        return h.ToHashCode();
    }

    sealed record PreviewProps(Artist A, long Stamp, bool Rows, float Target, float Width, PreviewState State)
    {
        public bool Equals(PreviewProps? o) => o is not null && o.A == A && o.Stamp == Stamp && o.Rows == Rows && o.Target == Target && o.Width == Width
            && ReferenceEquals(o.State, State);
        public override int GetHashCode() => HashCode.Combine(A.Slot, Stamp, Rows, Target, Width);
    }

    sealed class PreviewHost : Component
    {
        public override Element Render()
        {
            var p = UseProps<PreviewProps>();
            Signal<int> facetSignal = p.State.Facet;
            Signal<bool> oldestSignal = p.State.Oldest;
            Artist a = p.A;
            int[][] pools = [a.AlbumSlots.ToArray(), a.SingleSlots.ToArray(), a.CompilationSlots.ToArray()];
            DiscoFacet[] facets = [DiscoFacet.Albums, DiscoFacet.Singles, DiscoFacet.Compilations];
            bool oldest = oldestSignal.Value;
            Element order = Combo(p.Target, Loc.Get(oldest ? Strings.Theme.Winui.Detail.Oldest : Strings.Theme.Winui.Detail.Newest),
                [Item(Loc.Get(Strings.Theme.Winui.Detail.Newest), () => oldestSignal.Value = false), Item(Loc.Get(Strings.Theme.Winui.Detail.Oldest), () => oldestSignal.Value = true)],
                "ov:order:" + oldest);

            if (p.Rows)
            {
                var rows = new List<Element>(2);
                for (int f = 0; f < WinUiArtistRules.HugePreviewRows; f++)
                    if (pools[f].Length > 0) rows.Add(Group(Artist.FacetTitle(facets[f]), Cards(pools[f], oldest, p.Width), f));
                Element more = Link(Loc.Get(Strings.Theme.Winui.Detail.ShowAll), a, DiscoFacet.Albums);
                return new BoxEl
                {
                    Direction = 1, Gap = WinUiArtistRules.BlockGap, MinWidth = 0f,
                    Children =
                    [
                        Header(Loc.Get(Strings.Theme.Winui.Detail.Discography), order, more),
                        new BoxEl { Direction = 1, Gap = WinUiArtistRules.BlockGap, MinWidth = 0f, Children = rows.ToArray() },
                    ],
                };
            }

            // The facets that have releases, in the core's order; the chosen one clamps when the data shrinks.
            var present = new List<int>(3);
            for (int f = 0; f < 3; f++) if (pools[f].Length > 0) present.Add(f);
            if (present.Count == 0) return new BoxEl { Key = "ov:preview:none" };
            int chosen = present.Contains(facetSignal.Value) ? facetSignal.Value : present[0];
            var typeItems = new MenuFlyoutItem[present.Count];
            for (int i = 0; i < typeItems.Length; i++)
            {
                int f = present[i];
                typeItems[i] = Item(Artist.FacetTitle(facets[f]), () => facetSignal.Value = f);
            }
            Element type = Combo(p.Target, Artist.FacetTitle(facets[chosen]), typeItems, "ov:type:" + chosen);
            int total = Artist.FacetTotal(a, facets[chosen]);
            Element all = Link(Strings.Theme.Winui.Detail.ShowAllAlbums(count: Math.Max(total, pools[chosen].Length)), a, facets[chosen]);
            return new BoxEl
            {
                Direction = 1, Gap = WinUiArtistRules.BlockGap, MinWidth = 0f,
                Children =
                [
                    Header(Loc.Get(Strings.Theme.Winui.Detail.Discography), new BoxEl
                    {
                        Direction = 0, Gap = WinUiArtistRules.ActionsGap, AlignItems = FlexAlign.Center, Shrink = 0f, Children = [type, order],
                    }, null),
                    Cards(pools[chosen], oldest, p.Width),
                    new BoxEl { Direction = 0, Children = [all] },
                ],
            };
        }

        static MenuFlyoutItem Item(string label, Action invoke) => new(label, default, true, invoke);

        /// <summary>A combo: the current word and a chevron on the control fill; the menu is the choices. Keyed on the word (the label freezes at mount).</summary>
        static Element Combo(float target, string label, IReadOnlyList<MenuFlyoutItem> items, string key)
            => Embed.Comp(() => new DropDownButton
            {
                Label = label, Items = items,
                Parts = new TemplateParts { [DropDownButton.PartRoot] = b => target > WinUiDetailRules.Target ? b with { Height = target } : b },
            }) with { Key = key };

        static Element Header(string title, Element lead, Element? trailing) => new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S, MinWidth = 0f,
            Children = trailing is null
                ? [R.Type.RailHeader(title) with { Grow = 1f, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis }, lead]
                : [R.Type.RailHeader(title) with { Grow = 1f, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis }, lead, trailing],
        };

        static Element Group(string title, Element cards, int index) => new BoxEl
        {
            Key = "ov:group:" + index.ToString(CultureInfo.InvariantCulture), Direction = 1, Gap = WinUiArtistRules.BlockGap, MinWidth = 0f,
            Children =
            [
                new TextEl(title) { Size = WinUiArtistRules.GroupTitle, LineHeight = WinUiArtistRules.GroupTitleLine, Weight = 600, Color = Tok.TextSecondary, MaxLines = 1 },
                cards,
            ],
        };

        static Element Link(string text, Artist a, DiscoFacet facet)
        {
            string key = DiscoRoute.Key(facet, a.Uri.Text);
            return HyperlinkButton.Create(text, () => ArtistVerbs.OpenRoute(key), size: ControlSize.Small);
        }

        /// <summary>Three covers in equal columns of the preview's width <paramref name="w"/>, 16 apart: the shared media surface (cover, title, "2014 · Album").</summary>
        static Element Cards(int[] slots, bool oldest, float w)
        {
            int count = Math.Min(slots.Length, (int)WinUiArtistRules.PreviewColumns);
            float gap = WinUiArtistRules.PreviewGap;
            float cell = MathF.Max(1f, (w - (WinUiArtistRules.PreviewColumns - 1f) * gap) / WinUiArtistRules.PreviewColumns);
            var kids = new Element[(int)WinUiArtistRules.PreviewColumns];
            for (int i = 0; i < kids.Length; i++)
            {
                int at = oldest ? slots.Length - 1 - i : i;
                kids[i] = i < count ? WinUiArtistSections.AlbumCardAt(slots[at], cell) : new BoxEl { Width = cell, Shrink = 0f, HitTestVisible = false };
            }
            return new BoxEl { Direction = 0, Gap = gap, MinWidth = 0f, Children = kids };
        }
    }
}
