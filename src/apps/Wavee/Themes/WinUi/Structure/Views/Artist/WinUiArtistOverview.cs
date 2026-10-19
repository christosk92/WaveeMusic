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
    public static Element Block(ArtistPageProps p, WinUiArtistLatch latch, WidthTier tier, float inset)
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
        Func<int, float, Element> build = (rung, slot) =>
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
            Element preview = Embed.Comp(new PreviewProps(a, stamp, columns >= 3, target, PreviewWidth(slot, columns)), static () => new PreviewHost()) with { Key = "ov:preview" };

            var side = new List<Element>(4);
            if (latestCard is not null) side.Add(latestCard);
            if (columns >= 3)
            {
                side.AddRange(after);
                return Grid(0, WinUiArtistRules.HugeColumnGap,
                    Column(PopularKey, WinUiArtistRules.PopularGrow, chart is null ? [] : [chart]),
                    Column("ov:col:discography", WinUiArtistRules.PopularGrow, [preview]),
                    Column("ov:col:side", WinUiArtistRules.OtherGrow, side.ToArray()));
            }

            // One column and two are ONE tree: the grid turns from a row into a column and the two columns keep their nodes (and the blocks theirs), so a
            // restack keeps every node instead of re-parenting it (a re-parented node remounts). Stacked, the columns stop growing and stack at their
            // content's height: Popular, then the side column, one section gap apart (the board's stacked order).
            side.Add(new BoxEl { Key = "ov:preview:slot", Direction = 1, MinWidth = 0f, Layout = WinUiArtistMotion.Block, Children = [preview] });
            side.AddRange(after);
            bool two = columns == 2;
            float grow = two ? 1f : 0f;
            Element sideColumn = Column("ov:col:side", grow * WinUiArtistRules.OtherGrow, side.ToArray());
            // Stacked without a chart, an empty Popular column would add a section gap above the side column.
            if (!two && chart is null) return Grid(1, WinUiArtistRules.SectionGap, sideColumn);
            return Grid((byte)(two ? 0 : 1), two ? WinUiArtistRules.ColumnGap : WinUiArtistRules.SectionGap,
                Column(PopularKey, grow * WinUiArtistRules.PopularGrow, chart is null ? [] : [chart]),
                sideColumn);
        };
        return Embed.Comp(new GridProps(build, inset, p.Width), static () => new GridHost()) with { Key = "artist:overview" };
    }

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
    /// (<see cref="WinUiArtistMotion.OverviewHeight"/>) so the sections under it glide when a restack changes it.</summary>
    static Element Grid(byte direction, float gap, params Element[] columns) => new BoxEl
    {
        Key = "ov:grid", Direction = direction, Gap = gap, MinWidth = 0f, AlignItems = direction == 0 ? FlexAlign.Start : FlexAlign.Stretch,
        Layout = WinUiArtistMotion.OverviewHeight, Children = columns,
    };

    /// <summary>A column: in a row it grows by its weight from a zero basis; stacked (<paramref name="grow"/> 0) it takes its content's height.</summary>
    static Element Column(string key, float grow, Element[] kids) => new BoxEl
    {
        Key = key, Direction = 1, Gap = WinUiArtistRules.SectionGap, Grow = grow, Basis = grow > 0f ? 0f : float.NaN, MinWidth = 0f,
        // Popular's height never follows its width (the chart's rows are fixed), so it re-lays at the eased width; the side column's does (the
        // preview's covers scale with the width, the pick turns), so it lays out at the final size at once and reveals it.
        Layout = key == PopularKey ? WinUiArtistMotion.Column : WinUiArtistMotion.Block, Children = kids,
    };

    const string PopularKey = "ov:col:popular";

    // ── the column rung ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The grid host's props: the arrangement for a column rung, the page inset (the rung reads the PAGE width: the slot plus the inset either
    /// side) and the page width the host measured (the rung's seed before the first measure). The builder is a live closure (a fresh one rebuilds with
    /// the page, like the ungated responsive box this replaced).</summary>
    sealed record GridProps(Func<int, float, Element> Build, float Inset, float Seed);

    /// <summary>The overview's column rung, resolved with the engine's tier hook over the measured page width: a crossing found after mount is held
    /// until the first frame that is not resized and committed right before FLIP capture, so the restack animates. A measured width written into a
    /// signal the render reads (the responsive box this replaced) rebuilt the overview in the frame's in-frame flush with no capture, so every
    /// transition on the overview had nothing to animate from: the restack snapped.</summary>
    sealed class GridHost : Component
    {
        readonly Signal<float> _page = new(0f);
        readonly Signal<float> _slot = new(0f);
        readonly Func<float> _seedFn;
        readonly Action<RectF> _onBounds;
        float _inset, _seed;

        public GridHost()
        {
            _seedFn = () => _seed;
            _onBounds = r =>
            {
                if (!(r.W > 0f)) return;
                if (MathF.Abs(r.W - _slot.Peek()) > 0.5f) _slot.Value = r.W;
                float page = r.W + 2f * _inset;
                if (MathF.Abs(page - _page.Peek()) > 0.5f) _page.Value = page;
            };
        }

        public override Element Render()
        {
            var props = UseProps<GridProps>();
            _inset = props.Inset;
            _seed = props.Seed;
            var rung = UseTier(WinUiArtistTiers.OverviewLadder, TierSource.Measured(_page, _seedFn));
            // The slot's width (read: a width change rebuilds, as the responsive box did) sizes the preview's covers; the seed stands in before the first measure.
            float slot = _slot.Value;
            if (!(slot > 0.5f)) slot = MathF.Max(1f, props.Seed - 2f * props.Inset);
            return new BoxEl { Direction = 1, MinWidth = 0f, OnBoundsChanged = _onBounds, Children = [props.Build(rung.Value, slot)] };
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

    sealed record PreviewProps(Artist A, long Stamp, bool Rows, float Target, float Width)
    {
        public bool Equals(PreviewProps? o) => o is not null && o.A == A && o.Stamp == Stamp && o.Rows == Rows && o.Target == Target && o.Width == Width;
        public override int GetHashCode() => HashCode.Combine(A.Slot, Stamp, Rows, Target, Width);
    }

    sealed class PreviewHost : Component
    {
        readonly Signal<int> _facet = new(0);
        readonly Signal<bool> _oldest = new(false);

        public override Element Render()
        {
            var p = UseProps<PreviewProps>();
            Artist a = p.A;
            int[][] pools = [a.AlbumSlots.ToArray(), a.SingleSlots.ToArray(), a.CompilationSlots.ToArray()];
            DiscoFacet[] facets = [DiscoFacet.Albums, DiscoFacet.Singles, DiscoFacet.Compilations];
            bool oldest = _oldest.Value;
            Element order = Combo(p.Target, Loc.Get(oldest ? Strings.Theme.Winui.Detail.Oldest : Strings.Theme.Winui.Detail.Newest),
                [Item(Loc.Get(Strings.Theme.Winui.Detail.Newest), () => _oldest.Value = false), Item(Loc.Get(Strings.Theme.Winui.Detail.Oldest), () => _oldest.Value = true)],
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
            int chosen = present.Contains(_facet.Value) ? _facet.Value : present[0];
            var typeItems = new MenuFlyoutItem[present.Count];
            for (int i = 0; i < typeItems.Length; i++)
            {
                int f = present[i];
                typeItems[i] = Item(Artist.FacetTitle(facets[f]), () => _facet.Value = f);
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
