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
        float fallback = MathF.Max(1f, p.Width - 2f * inset);
        long stamp = PreviewStamp(a);
        return Responsive.Of("artist:overview", w =>
        {
            int columns = latch.Columns = WinUiArtistTiers.OverviewColumns(w + 2f * inset, tier, latch.Columns);
            Element? chart = popular ? new BoxEl { Key = "ov:chart", Direction = 1, MinWidth = 0f, Children = [Artist.Chart(a, accentSignal)] } : null;
            // W-Artist: the latest release, then Discography; the upcoming card and the artist's pick (no board draws one) follow under Discography, the pick
            // compact (no photograph band) so it never outweighs the column.
            float target = WinUiDetailRules.TargetOf(tier);
            Element? latestCard = latest ? LatestCard(a, target) : null;
            var after = new List<Element>(2);
            if (upcoming) after.Add(WinUiArtistSections.SectionBlock(Loc.Get(Strings.Artist.Upcoming), WinUiArtistBanners.UpcomingCard(a, accentFn, wide: false)));
            if (a.HasPick) after.Add(WinUiArtistPick.Card(a, accentFn, horizontal: false, overlay, surface, target, photo: false) with { Key = "ov:pick" });
            var side = new List<Element>(3);
            if (latestCard is not null) side.Add(latestCard);
            side.AddRange(after);
            Element preview = Embed.Comp(new PreviewProps(a, stamp, columns >= 3, target), static () => new PreviewHost()) with { Key = "ov:preview" };

            if (columns <= 1)
            {
                var all = new List<Element>(6);
                if (chart is not null) all.Add(chart);
                if (latestCard is not null) all.Add(latestCard);
                all.Add(preview);
                all.AddRange(after);
                return new BoxEl { Direction = 1, Gap = WinUiArtistRules.SectionGap, MinWidth = 0f, Children = all.ToArray() };
            }
            if (columns == 2)
            {
                var right = new List<Element>(5);
                if (latestCard is not null) right.Add(latestCard);
                right.Add(preview);
                right.AddRange(after);
                return Row(WinUiArtistRules.ColumnGap,
                    Column("ov:col:popular", WinUiArtistRules.PopularGrow, chart is null ? [] : [chart]),
                    Column("ov:col:side", WinUiArtistRules.OtherGrow, right.ToArray()));
            }
            return Row(WinUiArtistRules.HugeColumnGap,
                Column("ov:col:popular", WinUiArtistRules.PopularGrow, chart is null ? [] : [chart]),
                Column("ov:col:discography", WinUiArtistRules.PopularGrow, [preview]),
                Column("ov:col:side", WinUiArtistRules.OtherGrow, side.ToArray()));
        }, fallback: fallback);
    }

    static Element Row(float gap, params Element[] columns) => new BoxEl
    {
        Direction = 0, Gap = gap, MinWidth = 0f, AlignItems = FlexAlign.Start, Children = columns,
    };

    static Element Column(string key, float grow, Element[] kids) => new BoxEl
    {
        Key = key, Direction = 1, Gap = WinUiArtistRules.SectionGap, Grow = grow, Basis = 0f, MinWidth = 0f, Children = kids,
    };

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

    sealed record PreviewProps(Artist A, long Stamp, bool Rows, float Target)
    {
        public bool Equals(PreviewProps? o) => o is not null && o.A == A && o.Stamp == Stamp && o.Rows == Rows && o.Target == Target;
        public override int GetHashCode() => HashCode.Combine(A.Slot, Stamp, Rows, Target);
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
                    if (pools[f].Length > 0) rows.Add(Group(Artist.FacetTitle(facets[f]), Cards(pools[f], oldest), f));
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
                    Cards(pools[chosen], oldest),
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

        /// <summary>Three covers in equal columns of the measured width, 16 apart: the shared media surface (cover, title, "2014 · Album").</summary>
        static Element Cards(int[] slots, bool oldest) => Responsive.Of(w =>
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
        }, fallback: WinUiDetailRules.AsideWidth);
    }
}
