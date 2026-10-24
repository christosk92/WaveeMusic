// ── Themes/Zune/Structure/Views/Detail/ZuneDetailHero.cs — Zune's detail hero: ONE tree for the page's two forms (themes plan 6.3; P5 Z6a1; audit B9) ─
//
// Boards Z-Album-Compact (the HERO form: the narrow page) and Z-Album / -Small / -Huge / -Huge4K (the COLUMNS form). The table hosts this view as its item 0 at
// every width (`DetailHeroProps.Parts.Form` says which arrangement the page is in, a live prop, never a key), so the panorama, the cover, the artist and
// the meta, the ring row and the list bar are the SAME nodes in both forms and a crossing of the 640 edge re-arranges them (they glide) instead of
// rebuilding them:
//
//   · the HERO form: the panorama (64/72, fitted: one smaller line, or two at 40, its band hugging it) on the frame's gutter, the cover (144 at 400, 0.42 of
//     the column, 96 to 144) BESIDE the artist (18/24) and the meta line, the 40 ring row (play and saved with their words where the column has the room,
//     share and "..." pushed to its end), the description when the page has one, then the table's list bar. The table pins it (`Sticky(0)`) and collapses
//     it (`Collapse`) into the STUCK band (56): the title and the byline beside the table's Find · Filter · Play words; the selection arm swaps the band's
//     content at the same height; the expanded search field takes the title's place;
//   · the COLUMNS form: the panorama over [the identity column | the column gap | the list head ("songs", the list bar) | the aside]. The table pins it
//     whole; the identity column and the aside HANG below it (their slots lay out 0 tall), beside the rows, which the table insets by exactly their widths
//     (`Style.ListInsets`). The identity column is the cover at the column's width OVER the artist and the meta, the ring row (share and "...": play, saved
//     and shuffle live in the band's row 2 from Small up, audit A4), the status rows, the release's courtesy lines and the description;
//   · what one form has and the other has not enters and leaves on a declared swap fade (`ZuneMotion.SwapFade`): the play and saved rings (the band's verbs
//     at the columns form), the column's owner, badges, rating, status, topics, about, countdown and release rows (the band under the rows holds the
//     countdown and the release at the hero form: `Detail.VerticalTrailing`), a playlist's leading description, the "songs" heading's words, the stuck band.
//
// THE NUMBERS AGREE (the hero form). Its height is ONE rule (`ZuneDetailRules.HeroHeight`: the panorama band, the cover row, the action row and its air, the
// description's reserved lines, the bottom air, the list bar's block), every block laid out at exactly that height; the loading band (`Style.Skeleton`, the
// style the table reads: the skeleton deriver cannot run a view slot) reserves the same blocks, and the stuck band's collapse distance is derived from the
// same number until the table measured the hero (`OnHeroMeasured`). So the skeleton dissolves into the hero and nothing under it moves.
//
// SCROLL. The hero never scrolls anything: the hero form's expanded presentation binds its offset and opacity to the nearest scroller, the band reveals over
// the tail of the collapse, and the table's `CompactInteractive` edge moves hit-testing between the two (one live entry point per control). The columns form
// binds nothing (the table pins it whole).
// MOTION (production, deliberate): the late rows fade up, every keyed row glides, the cover's bounds tween from its top-left. A crossing: the arrangement's
// boxes move and resize through real layout (`ZuneDetailRules.Arrange`), the gutters ease (`ZuneFrameRules.GutterMotion`), the one-form pieces cross-fade.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scroll.Effects;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Theming.Roles;
using static FluentGpu.Dsl.Ui;
using BandLayout = Wavee.Detail.BandLayout;
using K = Wavee.Themes.Zune.ZuneDetailKit;
using VerticalLayout = Wavee.Detail.VerticalLayout;
using VerticalSpec = Wavee.Detail.VerticalSpec;
using Z = Wavee.Themes.Zune.ZuneDetailRules;

namespace Wavee.Themes.Zune;

/// <summary>Zune's detail hero (<see cref="ViewKeys.DetailHero"/>) and its loading band (<see cref="StyleKeys.DetailHero"/>).</summary>
internal static class ZuneDetailHero
{
    /// <summary>The hero's loading band, the list's insets beside the columns form's side columns, and their ease, as the style the table reads.</summary>
    public static readonly DetailHeroStyle Style = new() { Skeleton = Skeleton, ListInsets = ListInsets, InsetMotion = ZuneFrameRules.GutterMotion };

    /// <summary>The registered template of <see cref="ViewKeys.DetailHero"/>.</summary>
    public static Element Template(DetailHeroProps p, in ViewContext ctx) => Embed.Comp(p, static () => new HeroHost());

    /// <summary>The list bar's block under the hero: the core's list bar row and its pads.</summary>
    static float ToolbarBlock => VerticalLayout.ExpandedToolbarTopPad + VerticalLayout.ToolbarRowHeight + VerticalLayout.ExpandedToolbarBottomPad;

    /// <summary>The geometry every arm of the hero derives from one column width.</summary>
    readonly record struct Geometry(ZuneDetailLayout Layout, float Width, float Gutter, float Cover, bool Description, float Height);

    static Geometry GeometryOf(VerticalSpec spec, float columnWidth, TierFacts tiers)
    {
        float w = columnWidth > 0f ? columnWidth : Z.HeroFallbackW;
        float gutter = ZuneTiers.FrameOf(tiers).Gutter;
        bool words = Z.HeroWords(w, gutter);
        var id = spec.Identity;
        // One home per verb (audit A4): play and saved leave the ring row where the window's band carries them; the panorama is the vertical hero's
        // step for every kind (a long title fits smaller and the band hugs it), and the skeleton reads the same geometry.
        var lay = ZuneDetailTiers.Hero with { PlayWord = words, SaveWord = words, PrimaryInHero = ZuneDetailTiers.PrimaryInHero(in tiers) };
        float cover = Z.HeroCoverFor(w, gutter);
        bool description = (id.EditableMetadata && spec.Slots.Description is not null) || id.DescriptionHtml is { Length: > 0 };
        return new Geometry(lay, w, gutter, cover, description, Z.HeroHeight(lay, cover, description, ToolbarBlock));
    }

    // ══ THE LIST'S INSETS (the columns form's side columns) ═══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>What the columns form's side columns take beside the rows: the identity column and its gap (the collapsed strip where another theme stored a
    /// collapse; none on a page with no rail), and the docked aside with its gap (following the insights toggle where the aside holds them alone). The hero
    /// form has none. Called inside the table's binds: the toggle read is tracked.</summary>
    static DetailListInsets ListInsets(DetailInsetQuery q)
    {
        if (q.Form != DetailForm.Columns) return default;
        var lay = ZuneDetailTiers.Of(q.Tiers);
        var c = q.Columns;
        float lead = 0f;
        if (c?.TwoColumn is { } tc) lead = (tc.Collapsed ? tc.CompactWidth : lay.RailW) + lay.ColumnGap;
        float trail = AsideWidth(c, lay);
        // The list column keeps the two-column arm's floor: inside a crossing's hold band the aside yields a few DIP rather than squeeze the list under
        // it (the hero's list head carries the same floor, so the bar and the rows keep one column).
        if (q.TableWidth > 0f) trail = MathF.Min(trail, MathF.Max(0f, q.TableWidth - lead - Detail.Breakpoints.TwoColumnContentMinW));
        return new DetailListInsets(lead, trail);
    }

    /// <summary>Does the columns form dock the aside (Zune lays it out by the CONTENT tier: where a docked panel leaves the page Small the aside folds and
    /// what it held returns to the band under the rows)? A playlist's aside holds only its recommended songs, docked from Huge (Z-Playlist-Huge).</summary>
    internal static bool Docks(DetailAsideProps? aside, ZuneDetailLayout lay)
    {
        if (aside is null || lay.AsideW <= 0f) return false;
        bool recsOnly = aside is { Kind: DetailKind.Playlist, Body: not null };
        return !recsOnly || lay.Tier >= WidthTier.Huge;
    }

    /// <summary>The aside slot's width: the column gap and the record's column (the insights' own width where they are its only body), 0 where none docks; an
    /// aside that holds the insights alone follows their toggle (a tracked read).</summary>
    static float AsideWidth(DetailColumnsProps? c, ZuneDetailLayout lay)
    {
        if (c?.Aside is not { } aside || !Docks(aside, lay)) return 0f;
        float asideW = aside.Body is null && lay.InsightsW > 0f ? lay.InsightsW : lay.AsideW;
        float slotW = lay.ColumnGap + asideW;
        if (aside.Body is null && c.Sheet is { Facts: not null } only) return only.Toggle.Open.Value ? slotW : 0f;
        return slotW;
    }

    // ══ THE HERO ══════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The hero host: the one tree of both forms, and the hero form's stuck band.</summary>
    sealed class HeroHost : Component, IPropsHost
    {
        DetailHeroProps? _latest;
        readonly Signal<DetailHeroProps?> _props = new(null);
        readonly Action<RectF> _measure;
        readonly Action<RectF> _measureBand;
        readonly Action<RectF> _measureHead;
        /// <summary>The panorama band's laid-out height: where the columns form's side columns start below the hero's top (they fill the list's viewport from
        /// there down).</summary>
        readonly Signal<float> _bandH = new(0f);
        float _lastMeasuredH;

        public HeroHost()
        {
            _measure = Measure;
            _measureBand = r => { if (r.H >= 0f && MathF.Abs(_bandH.Peek() - r.H) > 0.5f) _bandH.Value = r.H; };
            _measureHead = MeasureHead;
        }

        public void ApplyProps(object props)
        {
            _latest = (DetailHeroProps)props;
            _props.Value = _latest;
        }

        /// <summary>The hero form's measure: its whole height (the table's collapse height).</summary>
        void Measure(RectF r)
        {
            if (_latest?.Parts.Form == DetailForm.Columns) return;
            Report(r.H);
        }

        /// <summary>The columns form's measure: the panorama band and the list head's foot under it, the PINNED head the chrome stands under (the identity
        /// column and the aside hang below it). The list head lands at its place at once while the body around it eases, so the chrome and the rows never
        /// wait on the ease.</summary>
        void MeasureHead(RectF r)
        {
            if (_latest?.Parts.Form != DetailForm.Columns) return;
            Report(_bandH.Peek() + r.Y + r.H);
        }

        void Report(float h)
        {
            // Reported again whenever the table's own value disagrees (it keeps one per form), not only on a change here.
            if (h <= 1f || (MathF.Abs(h - _lastMeasuredH) <= 1f && MathF.Abs(h - (_latest?.Parts.HeroHeight ?? 0f)) <= 1f)) return;
            _lastMeasuredH = h;
            _latest?.Parts.OnHeroMeasured?.Invoke(h);       // the table republishes its collapse height (the hero form) or its pinned line (the columns form)
        }

        public override Element Render()
        {
            _ = _props.Value;
            var tiers = TierContext.DrawnIn(UseContext(ThemeFactsContext.Signal)).Value.Tiers;
            var seen = UseRef(false);                              // a swap host fades in only on a re-arrangement, never with the page's own entrance
            var enter = ZuneMotion.SwapEnter(seen.Value);
            seen.Value = true;
            var p = _latest!;
            var spec = p.Spec;
            var parts = p.Parts;
            var id = spec.Identity;
            var slots = spec.Slots;
            bool columns = parts.Form == DetailForm.Columns;
            bool compactCanHit = parts.CompactInteractive.Value && !columns;   // subscribe: the stuck edge moves input ownership (the hero form's)
            var g = GeometryOf(spec, parts.ColumnWidth, tiers);
            var cols = parts.Columns;
            var tc = cols?.TwoColumn;
            var rail = tc?.Rail;
            bool hasRail = tc is not null;
            var lay = columns ? ZuneDetailTiers.ColumnOf(in tiers) : g.Layout;
            // The hero form stands on the frame's gutter itself (the page column has none there: the rows run full bleed); the columns form stands in the
            // page column. Spacers, never padding, so the inset eases with the page column's own gutters.
            float pad = columns ? 0f : g.Gutter;
            float railW = columns ? (tc is { Collapsed: true } ? tc.CompactWidth : lay.RailW) : float.NaN;
            float coverEdge = columns ? lay.RailW : g.Cover;
            float inner = MathF.Max(0f, g.Width - 2f * g.Gutter);
            float infoW = columns ? lay.RailW : MathF.Max(0f, inner - g.Cover - Z.HeroCoverGap);

            // ── the identity column (one tree: the cover beside the artist and the meta at the hero form, over them at the columns form) ──
            Element identity = Identity(p, lay, columns, coverEdge, infoW, g, enter, hasRail, ColumnHeight(parts.ViewportHeight, columns && hasRail));
            bool dock = columns && Docks(cols?.Aside, lay);

            // ── the body: [identity | gap | list head | aside], a row at the columns form, stacked at the hero form ──
            bool songs = columns && hasRail;
            var shown = rail?.Shown;
            Prop<float> headingH = !songs ? 0f : shown is null ? Z.HeadingRowH : Prop.Of(() => shown.Value.EmptyLead ? 0f : Z.HeadingRowH);
            Element listHead = new BoxEl
            {
                // Laid out at its final place and size from the commit (its siblings' widths land at once and only their pictures ease), so the list bar inside
                // fits once instead of re-fitting through every frame of an ease (a command it evicted and promoted again would be rebuilt); its picture
                // slides and reveals there.
                Key = "hero:list-head", Direction = 1, Grow = columns ? 1f : 0f, Shrink = 0f, Basis = columns ? 0f : float.NaN, OnBoundsChanged = _measureHead,
                MinWidth = columns ? Detail.Breakpoints.TwoColumnContentMinW : 0f,
                Layout = ZuneMotion.GroupResize,
                Children =
                [
                    new BoxEl
                    {
                        Key = Z.SongsHeadingKey, Direction = 0, AlignItems = FlexAlign.Center, Height = headingH, Shrink = 0f, MinWidth = 0f, ClipToBounds = true,
                        Animate = ZuneMotion.HeightResize, Opacity = songs ? 1f : 0f,
                        Padding = new Edges4(Z.SongsHeadingInset, 0f, Z.SongsHeadingInset, 0f),
                        Children = [K.Heading(Loc.Get(Strings.Theme.Zune.Detail.Songs))],
                    },
                    ToolbarBlock(parts, columns, hasRail),
                ],
            };
            Element body = new BoxEl
            {
                // Its height eases (the list head moves beside the identity column or under it: the body's height steps on the commit otherwise).
                // A page with no rail (Liked Songs) stacks its head over the list head in both forms.
                Key = "hero:body", Direction = (byte)(columns && hasRail ? 0 : 1), AlignItems = columns && hasRail ? FlexAlign.Start : FlexAlign.Stretch, MinWidth = 0f,
                Animate = columns && hasRail ? Z.HangingResize : ZuneMotion.HeightResize,
                Children =
                [
                    identity,
                    new BoxEl { Key = Z.ColumnGapKey, Width = columns && hasRail ? lay.ColumnGap : 0f, Height = 0f, Shrink = 0f, HitTestVisible = false, Animate = Z.SlotReveal },
                    listHead,
                    AsideSlot(cols, lay, dock, enter, AsideHeight(parts.ViewportHeight)),
                ],
            };

            Element expanded = new BoxEl
            {
                // No floor of its own: its blocks reserve their heights (the rule's), and a floor would land at once on a crossing while the blocks ease.
                Key = Z.HeroKey, Direction = 1, OnBoundsChanged = _measure,
                Children =
                [
                    Padded(new BoxEl
                    {
                        Direction = 1, MinWidth = 0f, OnBoundsChanged = _measureBand,
                        Children = [K.PanoramaBand(id, slots, lay, 0f, enter, ZuneMotion.HeightResize)],
                    }, columns ? 0f : pad),
                    body,
                ],
            };

            // ── the STUCK band (the hero form's): typography only, no fill (the rows are clipped under it) ──
            float heroH = parts.HeroHeight > 1f ? parts.HeroHeight : g.Height;
            float cd = VerticalLayout.CollapseDistance(heroH);
            string? byline = Detail.Text.Byline(id.OwnerName, id.Meta, id.Eyebrow);
            var titleRun = R.Type.TrackTitle(id.Title) with
            {
                Weight = (ushort)R.Type.FacetCompactStyle.SelectedWeight, Color = Tok.TextPrimary,
                MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f,
            };
            Element identityBlock = new BoxEl
            {
                Direction = 1, MinWidth = 0f, Shrink = 1f,
                Children = byline is { Length: > 0 }
                    ? [titleRun, R.Type.DenseMeta(byline) with
                        {
                            Color = Tok.TextSecondary, MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f,
                        }]
                    : [titleRun],
            };
            Element lead = identityBlock;
            if (parts.SearchField is { } searchField)
            {
                var searchExpanded = parts.SearchExpanded;
                lead = new BoxEl
                {
                    Direction = 0, MinWidth = 0f, Shrink = 1f, AlignItems = FlexAlign.Center,
                    Children =
                    [
                        Flow.Show(() => !searchExpanded.Value, identityBlock),
                        Flow.Show(() => searchExpanded.Value, new BoxEl { Shrink = 0f, Children = [searchField] }),
                    ],
                };
            }
            Element normalBand = new BoxEl
            {
                Key = Z.HeroBandKey, Direction = 0, Width = g.Width, Height = BandLayout.Height, AlignItems = FlexAlign.Center, Gap = Z.HeroCoverGap,
                Padding = new Edges4(parts.CompactLeft, 0f, parts.CompactLeft, 0f),
                Children = [new BoxEl { Direction = 0, Grow = 1f, Shrink = 1f, MinWidth = 0f, AlignItems = FlexAlign.Center, Children = [lead] }, parts.BandActions],
            };
            Element bandContent = normalBand;
            if (parts.SelectionBar is { } selectionBar)
            {
                var selectionVisible = parts.SelectionVisible;
                bandContent = new BoxEl
                {
                    ZStack = true, Width = g.Width, Height = BandLayout.Height,
                    Children =
                    [
                        Flow.Show(() => !selectionVisible.Value, normalBand),
                        Flow.Show(() => selectionVisible.Value, new BoxEl
                        {
                            Direction = 1, Width = g.Width, Height = BandLayout.Height, Justify = FlexJustify.Center,
                            Padding = new Edges4(parts.CompactLeft, 0f, parts.CompactLeft, 0f),
                            Children = [selectionBar],
                        }),
                    ],
                };
            }

            // The band exists in both forms (one tree) and is drawn only by the hero form's collapse: hidden, inert and unbound at the columns form.
            BoxEl compact = new()
            {
                Key = "hero:stuck-band", ZStack = true, Width = g.Width, Height = BandLayout.Height,
                HitTestVisible = compactCanHit, HitTestPassThrough = true, Visible = !columns,
                Children = [bandContent],
            };
            BoxEl presentation = ZStack(expanded) with { Direction = 1, HitTestVisible = !compactCanHit };
            if (!columns)
            {
                compact = compact.Reveal(VerticalLayout.CompactRevealStart(cd), cd - VerticalLayout.CompactRevealStart(cd), Design.Reduced ? 0f : Spacing.XS);
                presentation = presentation.Parallax(0.0, cd, 0f, -cd).Fade(VerticalLayout.ExpandedFadeStart(cd), cd, 1f, 0f);
            }
            return ZStack(presentation, compact) with { Direction = 1 };
        }

        /// <summary>The identity column's scroll height at the columns form (the list's viewport below the panorama band, tracked), none at the hero form.</summary>
        Prop<float> ColumnHeight(IReadSignal<double>? viewportH, bool hangs)
        {
            if (!hangs || viewportH is null) return float.NaN;
            var band = _bandH;
            return Prop.Of(() => MathF.Max(0f, (float)viewportH.Value - band.Value));
        }

        /// <summary>The aside column's height: the list's viewport below the panorama band (it hangs from the band's foot), tracked.</summary>
        Func<float>? AsideHeight(IReadSignal<double>? viewportH)
        {
            if (viewportH is null) return null;
            var band = _bandH;
            return () => MathF.Max(0f, (float)viewportH.Value - band.Value);
        }

        /// <summary>The list bar's block: the hero form's (the rows' gutter either side, 8 over and 4 under the bar) or the columns form's (8 over the bar, the
        /// rows' plate inset at its end: the two-column list bar's). Spacers, so the bar glides between them.</summary>
        static Element ToolbarBlock(Detail.HeroParts parts, bool columns, bool hasRail)
        {
            // The columns form of a page with no rail draws the PAGE bar (Liked Songs: the chips inline): its commands start on the head's words (the rows'
            // plate inset less the bar's own 6, both sides) and 4 of air part it from the column header, as the two-column arm's page bar did.
            bool pageBar = columns && !hasRail;
            float pageInset = Track.RowMetrics.RowInset - VerticalLayout.ToolbarSurfacePadX;
            float lead = pageBar ? pageInset : columns ? 0f : parts.CompactLeft;
            float trail = pageBar ? pageInset : columns ? Track.RowMetrics.RowInset : parts.CompactLeft;
            return new BoxEl
            {
                Key = "hero:toolbar", Direction = 1, MinWidth = 0f,
                Children =
                [
                    ZuneMotion.Air("hero:toolbar-top", VerticalLayout.ExpandedToolbarTopPad),
                    new BoxEl
                    {
                        Direction = 0, MinWidth = 0f,
                        Children =
                        [
                            ZuneFrameRules.Gutter(lead),
                            // The table's list bar; on a facts page it carries the insights toggle (the pre-stuck entry point; the band's word is the stuck one).
                            new BoxEl { Direction = 1, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f, Children = [parts.Toolbar ?? new BoxEl { Height = VerticalLayout.ToolbarRowHeight }] },
                            ZuneFrameRules.Gutter(trail),
                        ],
                    },
                    ZuneMotion.Air("hero:toolbar-bottom", pageBar ? Spacing.XS : columns ? 0f : VerticalLayout.ExpandedToolbarBottomPad),
                ],
            };
        }

        /// <summary><paramref name="content"/> between two easing gutter spacers of <paramref name="pad"/>.</summary>
        static Element Padded(Element content, float pad) => new BoxEl
        {
            Direction = 0, MinWidth = 0f,
            Children = [ZuneFrameRules.Gutter(pad), new BoxEl { Direction = 1, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f, Children = [content] }, ZuneFrameRules.Gutter(pad)],
        };
    }

    // ══ THE IDENTITY COLUMN ══════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The identity's slot in the body: the columns form's column (the record's width, laid out 0 tall: the column hangs below the hero beside the
    /// rows) or the hero form's full-width block on the gutter. One tree; its rows are keyed, so a row that only one form draws enters or leaves alone.</summary>
    static Element Identity(DetailHeroProps p, ZuneDetailLayout lay, bool columns, float coverEdge, float infoW, Geometry g, EnterExit? enter, bool hasRail,
        Prop<float> scrollHeight)
    {
        var spec = p.Spec;
        var parts = p.Parts;
        var id = spec.Identity;
        var cfg = spec.Config;
        var slots = spec.Slots;
        var acts = spec.Actions;
        var tc = parts.Columns?.TwoColumn;
        var rail = tc?.Rail;
        bool shortWindow = columns && lay.RailTitle;
        // A page with no rail (Liked Songs) shows no identity column at the columns form: its head is the panorama and the meta over the list.
        bool head = columns && !hasRail;
        float gap = columns ? lay.RailGap : 0f;

        var kids = new List<Element>(14);
        // A short window's title leads the column (the panorama folds): the editor arrives from the panorama, a cross-fade of the swap's family.
        if (columns && hasRail && lay.RailTitle)
            kids.Add(K.Row("rail:title", slots.Title?.Invoke(R.Type.Heading("").Size, float.NaN) ?? RailTitle(id.Title, lay.RailW)) with
            {
                Enter = enter, Exit = ZuneMotion.SwapFade,
            });

        // ── the cover row: the cover BESIDE the artist and the meta (the hero form), OVER them (the columns form) ──
        bool descriptionLeads = columns && hasRail && id.Kind == DetailKind.Playlist && !shortWindow;
        var info = new List<Element>(5);
        if (columns && hasRail && id.OwnerName is { Length: > 0 } && id.Kind != DetailKind.Album && slots.Attribution is null)
            info.Add(K.LateRow("rail:owner", K.ArtistText(id.OwnerName, infoW, lay)) with { Enter = enter ?? K.FadeUp, Exit = ZuneMotion.SwapFade });
        if (!head)
            info.Add(K.LateRow("hero:artist", new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, MinHeight = columns ? lay.ArtistLine : 0f, Height = columns ? float.NaN : HitArtist(lay), MinWidth = 0f,
                Children = [slots.Attribution?.Invoke(infoW) ?? K.Artists(id, infoW, lay, columns ? 0f : HitArtist(lay))],
            }));
        if (columns && hasRail && slots.Badges is { } badges) info.Add(K.LateRow("rail:badges", badges()) with { Exit = ZuneMotion.SwapFade });
        if (columns && hasRail && slots.Rating is { } rating) info.Add(K.LateRow("rail:rating", rating(infoW)) with { Exit = ZuneMotion.SwapFade });
        // The meta's reserved lines: the record's in the identity column, one under a page head (Liked Songs), two beside the hero form's cover.
        int metaLines = head ? 1 : columns ? Math.Max(1, lay.MetaLines) : 2;
        float metaW = columns ? lay.RailW : float.NaN;
        if (head) metaW = float.NaN;
        // The meta line: the page's, or, while a find or a filter narrows the list, the shown count (one component in both forms, so it keeps its node).
        var shownCount = rail?.Shown ?? parts.Columns?.Head?.Shown;
        info.Add(K.LateRow("hero:meta", shownCount is { } count
            ? Embed.Comp(new MetaProps(id, metaW, lay, metaLines), () => new MetaLine(count))
            : K.Meta(id, metaW, lay, metaLines)));

        var coverKids = new List<Element>(3);
        if (!head)
            coverKids.Add(K.Cover("hero:cover", id, acts, slots, coverEdge) with
            {
                Animate = Z.HeroGeometry, TransformOriginX = 0f, TransformOriginY = 0f,
            });
        // A playlist's description leads its column under the cover (Z-Playlist-Owned-Followed-Huge: description, owner, meta, rings); the hero form draws it
        // after the rings: two places, two hosts, a cross-fade.
        if (descriptionLeads && rail is { DescriptionLines: > 0 } && K.Description(id, slots, lay.RailW, spec.Accent) is { } leading)
            coverKids.Add(K.LateRow("rail:desc", leading) with { Enter = enter ?? K.FadeUp, Exit = ZuneMotion.SwapFade });
        coverKids.Add(new BoxEl
        {
            Key = "hero:info", Direction = 1, MinWidth = 0f, Layout = Z.Arrange,
            Grow = columns ? 0f : 1f, Shrink = 1f, Basis = columns ? float.NaN : 0f, Width = columns ? lay.RailW : float.NaN,
            Gap = columns ? lay.RailGap : Z.HeroIdentityGap, Justify = columns ? FlexJustify.Start : FlexJustify.End,
            Children = info.ToArray(),
        });
        kids.Add(new BoxEl
        {
            Key = "hero:cover-row", Direction = (byte)(columns ? 1 : 0), MinWidth = 0f, Shrink = 0f, Layout = Z.Arrange,
            Gap = columns ? lay.RailGap : Z.HeroCoverGap, Height = columns ? float.NaN : g.Cover,
            Children = coverKids.ToArray(),
        });

        // ── the status rows (a show's ledger, the daylist's pulse, the chart's news): the columns form's, between the meta and the rings (after them on a
        //    short window, so the verbs and their state stay above the play bar at rest, V4) ──
        var status = new List<Element>(3);
        if (columns && hasRail)
        {
            if (slots.Ledger is { } ledger) status.Add(K.LateRow("rail:ledger", ledger(lay.RailW)) with { Exit = ZuneMotion.SwapFade });
            if (id.DaylistExpiresAtMs > 0 && slots.Pulse is { } pulse) status.Add(K.LateRow("rail:daylist", pulse()) with { Exit = ZuneMotion.SwapFade });
            if (id.ChartNewEntries > 0 && slots.Chart is { } chart) status.Add(K.LateRow("rail:chart", chart()) with { Exit = ZuneMotion.SwapFade });
        }
        if (!shortWindow) kids.AddRange(status);

        // ── the ring row: one row in both forms (its rings keyed: the play and saved rings the columns form leaves to the band cross-fade) ──
        if (!head)
        {
            // The hero form's air (the column's own gap stands between the rows of the columns form): present in the hero form only, so it never adds a gap.
            if (!columns) kids.Add(ZuneMotion.Air("hero:actions-top", Z.HeroActionsTop));
            kids.Add(new BoxEl
            {
                // The hero form's row is the ring's height (it never wraps there: the words drop first), clipped; the columns form's wraps (a Short window's
                // play and saved) and eases its height.
                Key = "hero:actions", Direction = 1, MinWidth = 0f, Shrink = 0f, Layout = K.GrowShove,
                Height = columns ? float.NaN : lay.RingSize, ClipToBounds = !columns,
                Children = [K.Rings(Z.ActionsKey, id, cfg, acts, slots, spec.Accent, spec.PlayAll ?? DetailVerbs.PlayContext(id.Subject), lay, spring: !columns, enter)],
            });
        }
        if (shortWindow) kids.AddRange(status);

        // ── the columns form's courtesy rows: topics, about, the countdown, About this release (the band under the rows holds the last two at the hero form) ──
        if (columns && hasRail)
        {
            if (slots.Topics is { } topics) kids.Add(K.LateRow("rail:topics", topics(lay.RailW)) with { Exit = ZuneMotion.SwapFade });
            if (slots.About is { } about) kids.Add(K.LateRow("rail:about", about(lay.RailW)) with { Exit = ZuneMotion.SwapFade });
            if (id.UpcomingAtUnixSeconds > 0 && slots.PreRelease is { } pre)
                kids.Add(K.LateRow("rail:prerelease", pre()) with { Enter = enter ?? K.FadeUp, Exit = ZuneMotion.SwapFade });
            if (cfg.Badges == BadgeStyle.TypeYear && slots.ReleasePanel is { } release)
                kids.Add(new BoxEl
                {
                    Key = "rail:release", Direction = 1, MinWidth = 0f, ClipToBounds = true, Layout = K.GrowShove, Enter = enter ?? K.FadeUp, Exit = ZuneMotion.SwapFade,
                    Margin = new Edges4(0f, Z.NotesTop - lay.RailGap, 0f, 0f), Children = [release(false)],
                });
        }

        // ── the description: after the rings (the hero form, three lines reserved; the columns form, the rung's clamp), one host in both forms ──
        bool heroDescription = !columns && g.Description;
        bool railDescription = columns && hasRail && !descriptionLeads && rail is { DescriptionLines: > 0 };
        if (heroDescription || railDescription)
        {
            float width = columns ? lay.RailW : MathF.Max(0f, g.Width - 2f * g.Gutter);
            var description = K.Description(id, slots, width, spec.Accent);
            if (!columns) kids.Add(ZuneMotion.Air("hero:description-top", Z.HeroDescriptionTop));
            kids.Add(K.LateRow("hero:description", new BoxEl
            {
                Direction = 1, MinWidth = 0f, ClipToBounds = true,
                Height = columns ? float.NaN : Z.DescriptionLines * Z.DescriptionLine,
                Children = description is null ? [] : [description],
            }) with { Layout = K.GrowShove, Enter = enter ?? K.FadeUp, Exit = ZuneMotion.SwapFade });
        }
        if (!columns) kids.Add(ZuneMotion.Air("hero:bottom", Z.HeroBottom));

        Element column = new BoxEl
        {
            // Its width eases (a tier that changes the record's column, 160 / 240 / 320 / 400, and the hero form's full width); the slot around it lands at once.
            Key = Z.RailKey + ":column", Direction = 1, Gap = gap, MinWidth = 0f, Width = columns && !head ? lay.RailW : float.NaN, Layout = Z.ColumnReflow,
            // The column's foot (the rail's section air); a page head's (Liked Songs) is the record's row gap before the list head.
            Padding = new Edges4(0f, 0f, 0f, !columns ? 0f : head ? lay.RailGap : Z.SectionBottom),
            Children = kids.ToArray(),
        };
        // The column's loading state at the columns form: the header shimmers the column whole until it has answered (the hero form's table shimmers the
        // whole hero band instead). One boundary in both forms, so the column never re-parents.
        Func<bool> pending = columns && rail is not null ? rail.Pending : K.Never;
        Element region = new SkelRegionEl(
            Pending: pending, Failed: K.Never,
            Content: () => new BoxEl { Direction = 1, MinWidth = 0f, Children = [column] },
            ShimmerSource: () => ColumnSkeleton(spec, lay, lay.RailW),
            OnFailed: null, Reveal: SkelReveal.FadeOnly, Style: SkeletonStyle.Default, Group: null, SmoothResize: false);
        // The slot: the record's width laid out 0 tall at the columns form (the column hangs beside the rows), the full width on the gutter at the hero form.
        return new BoxEl
        {
            // Its size lands at once (the list head beside it then lays out at its final width from the commit, so the list bar fits once instead of
            // re-fitting through every frame of an ease, and no sibling reflows beside the list head's slide); the body around it eases its height, and
            // its rows glide inside it.
            Key = Z.RailKey, Direction = 0, Shrink = 0f, MinWidth = 0f, Layout = ZuneMotion.PivotSlide,
            Width = columns && !head ? lay.RailW : float.NaN, Height = columns && !head ? 0f : float.NaN,
            Opacity = columns && tc is not null ? tc.RailOpacity : 1f,
            Children =
            [
                ZuneFrameRules.Gutter(columns ? 0f : g.Gutter),
                // The column's own scroller (a long description, a Short window): at the columns form as tall as the list's viewport below the panorama, so
                // what the column holds past the fold stays reachable; at the hero form it sizes to the column (never scrolls: the list carries the hero).
                new BoxEl
                {
                    Direction = 1, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f, MinHeight = 0f, Height = scrollHeight,
                    Children =
                    [
                        Ui.ScrollView(region) with
                        {
                            Key = "hero:identity-scroll", ContentSized = true, Shrink = 1f, MinHeight = 0f,
                            ScrollKey = "hero:identity:" + p.Spec.Identity.Subject.Text,
                        },
                    ],
                },
                ZuneFrameRules.Gutter(columns ? 0f : g.Gutter),
            ],
        };
    }

    /// <summary>The hero form's artist line height: the line, or the Compact touch minimum.</summary>
    static float HitArtist(ZuneDetailLayout lay) => MathF.Max(lay.ArtistLine, Z.CompactHit);

    /// <summary>The short window's title row: the heading voice as written (a name), two lines at most.</summary>
    static TextEl RailTitle(string title, float w) => R.Type.Heading(title) with
    {
        Color = Tok.TextPrimary, Width = w, MinWidth = 0f, MaxLines = Z.RailTitleLines, Wrap = TextWrap.WrapWholeWords, Trim = TextTrim.CharacterEllipsis,
    };

    /// <summary>The columns form's loading column: the same rows at the same heights, unfilled (the deriver paints the bars); a known cover is painted for real.</summary>
    static Element ColumnSkeleton(VerticalSpec spec, ZuneDetailLayout lay, float w)
    {
        var id = spec.Identity;
        var kids = new List<Element>(6);
        if (lay.RailTitle) kids.Add(K.Bar(w * Z.SkelMetaFraction, R.Type.Heading("").LineHeight));
        kids.Add(K.CoverSkeleton(id.CoverUrl, w));
        kids.Add(new BoxEl { Height = lay.ArtistLine, Direction = 0, AlignItems = FlexAlign.Center, Children = [K.Bar(w * Z.SkelArtistFraction, lay.ArtistSize)] });
        kids.Add(new BoxEl { Height = lay.MetaLine, Direction = 0, AlignItems = FlexAlign.Center, Children = [K.Bar(w * Z.SkelMetaFraction, lay.MetaSize)] });
        kids.Add(K.RingsSkeleton("skel:actions", K.RingCount(id, spec.Config, spec.Actions, spec.Slots, lay), lay));
        return new BoxEl { Direction = 1, Gap = lay.RailGap, Width = w, Shrink = 0f, Children = kids.ToArray() };
    }

    sealed record MetaProps(Detail.Identity Id, float Width, ZuneDetailLayout Layout, int Lines);

    /// <summary>The meta line, or, while a find or a filter narrows the list, "3 of 48 songs · 11 min" and the quiet "(filtered)" mark in the same
    /// slot (its own component: a keystroke re-renders one line).</summary>
    sealed class MetaLine(IReadSignal<Detail.ListCount> shown) : Component
    {
        public override Element Render()
        {
            var p = UseProps<MetaProps>();
            var count = shown.Value;
            if (Detail.Text.FilteredMeta(in count) is not { } filtered) return K.Meta(p.Id, p.Width, p.Layout, p.Lines);
            var lay = p.Layout;
            return new BoxEl
            {
                Direction = 0, Width = p.Width, Height = lay.MetaLine * Math.Max(1, p.Lines), Gap = Spacing.XS, AlignItems = FlexAlign.Start, MinWidth = 0f,
                Children =
                [
                    R.Type.DenseMeta(filtered.Count) with
                    {
                        Size = lay.MetaSize, LineHeight = lay.MetaLine, Color = Tok.TextSecondary, Case = R.Case.For(LabelKind.Label),
                        MaxLines = 1, Trim = TextTrim.CharacterEllipsis, Shrink = 1f, MinWidth = 0f,
                    },
                    R.Type.DenseMeta(filtered.Mark) with
                    {
                        Size = lay.MetaSize, LineHeight = lay.MetaLine, Color = Tok.TextTertiary, Case = R.Case.For(LabelKind.Label), MaxLines = 1, Shrink = 0f,
                    },
                ],
            };
        }
    }

    // ══ THE ASIDE ═════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The aside's slot, the body's last child in both forms: width 0 where none docks, else the column gap and the record's column with its own
    /// scroller (the page's aside body under <see cref="ZuneDetailFrame.InAside"/>, then the insights on a facts page), laid out 0 tall: it hangs below the hero
    /// beside the rows, as tall as the list's viewport below the panorama. An aside that holds only the insights follows their toggle (0 closed).</summary>
    static Element AsideSlot(DetailColumnsProps? cols, ZuneDetailLayout lay, bool dock, EnterExit? enter, Func<float>? height)
    {
        if (!dock || cols?.Aside is not { } aside)
            return new BoxEl { Key = Z.AsideKey, Width = 0f, Height = 0f, Shrink = 0f, MinHeight = 0f, Animate = Z.SlotReveal };
        var insights = cols.Sheet is { Facts: not null } sheet ? sheet : null;
        var kids = new List<Element>(2);
        if (aside.Body is { } body) kids.Add(Ctx.Provide(ZuneDetailFrame.InAside, true, body));
        float asideW = aside.Body is null && lay.InsightsW > 0f ? lay.InsightsW : lay.AsideW;
        if (insights is { } i) kids.Add(ZuneInsights.AsideSection(i.Toggle, i.Facts!));
        float slotW = lay.ColumnGap + asideW;
        Prop<float> width = slotW;
        if (aside.Body is null && insights is { } only)
        {
            var open = only.Toggle.Open;
            width = Prop.Of(() => open.Value ? slotW : 0f);
        }
        return new BoxEl
        {
            // Laid out at its final width at once (the list head beside it fits its bar once), its picture revealing; the rows beside it in the list ease
            // their own trail.
            Key = Z.AsideKey, Direction = 1, Width = width, Height = 0f, Shrink = 1f, MinWidth = 0f, ClipToBounds = false,
            Animate = Z.SlotReveal,
            Children =
            [
                new BoxEl
                {
                    Direction = 1, Height = height is null ? 0f : Prop.Of(height), MinWidth = 0f, ClipToBounds = true,
                    Padding = new Edges4(lay.ColumnGap, 0f, 0f, 0f),
                    Children =
                    [
                        // The column docks on a crossing (1008; 1920 for a playlist's recommendations): what it holds arrives from the band under the rows, a
                        // cross-fade (the swap's family), while the slot eases its width.
                        Ui.ScrollView(new BoxEl
                        {
                            Key = Z.AsideColumnKey, Direction = 1, Gap = Z.AsideSectionGap, MinWidth = 0f,
                            Padding = new Edges4(0f, 0f, 0f, Z.SectionBottom),
                            Enter = enter, Exit = ZuneMotion.SwapFade,
                            Children = kids.ToArray(),
                        }) with { Key = "aside:scroll:" + aside.RouteKey, Grow = 1f, Shrink = 1f, MinHeight = 0f },
                    ],
                },
            ],
        };
    }

    // ══ THE LOADING BAND ══════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The loading band: the same blocks at the same heights, unfilled (the deriver paints the bars); a known cover is painted for real. The hero
    /// form's floor is the hero's own height, the number the collapse assumes until the hero is measured; the columns form reserves the panorama and the list
    /// head, its identity column hanging beside the rows' shimmer as the loaded one does.</summary>
    static Element Skeleton(VerticalSpec spec, float columnWidth, float compactLeft, TierFacts tiers, DetailForm form, DetailColumnsProps? columns)
    {
        var g = GeometryOf(spec, columnWidth, tiers);
        var id = spec.Identity;
        if (form == DetailForm.Columns)
        {
            var col = ZuneDetailTiers.ColumnOf(in tiers);
            return new BoxEl
            {
                Key = "zune.detail.hero-skeleton", Direction = 1,
                Children =
                [
                    new BoxEl { Height = col.PanoramaH, Direction = 0, Children = [K.Bar(MathF.Max(0f, g.Width * Z.SkelMetaFraction * 0.5f), col.ArtistLine)] },
                    new BoxEl
                    {
                        Direction = 0, AlignItems = FlexAlign.Start,
                        Children =
                        [
                            new BoxEl { Width = col.RailW, Height = 0f, Shrink = 0f, Children = [ColumnSkeleton(spec, col, col.RailW)] },
                            new BoxEl { Width = col.ColumnGap, Shrink = 0f },
                            new BoxEl
                            {
                                Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f,
                                Children = [new BoxEl { Height = Z.HeadingRowH }, new BoxEl { Height = VerticalLayout.ExpandedToolbarTopPad + VerticalLayout.ToolbarRowHeight }],
                            },
                        ],
                    },
                ],
            };
        }
        var lay = g.Layout;
        float inner = MathF.Max(0f, g.Width - 2f * g.Gutter);
        float side = MathF.Max(0f, inner - g.Cover - Z.HeroCoverGap);
        var blocks = new List<Element>(6)
        {
            new BoxEl
            {
                Height = lay.PanoramaH, Direction = 0, Padding = new Edges4(g.Gutter, 0f, g.Gutter, 0f),
                Children = [K.Bar(inner * Z.SkelMetaFraction, lay.ArtistLine)],
            },
            new BoxEl
            {
                Direction = 0, Gap = Z.HeroCoverGap, Height = g.Cover, Padding = new Edges4(g.Gutter, 0f, g.Gutter, 0f),
                Children =
                [
                    K.CoverSkeleton(id.CoverUrl, g.Cover),
                    new BoxEl
                    {
                        Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Gap = Z.HeroIdentityGap, Justify = FlexJustify.End,
                        Children = [K.Bar(side * Z.SkelArtistFraction, lay.ArtistLine), K.Bar(side * Z.SkelMetaFraction, lay.MetaLine)],
                    },
                ],
            },
            new BoxEl
            {
                Height = lay.RingSize, Margin = new Edges4(0f, Z.HeroActionsTop, 0f, 0f), Padding = new Edges4(g.Gutter, 0f, g.Gutter, 0f),
                Children = [K.RingsSkeleton("hero-skel:actions", K.RingCount(id, spec.Config, spec.Actions, spec.Slots, lay), lay)],
            },
        };
        if (g.Description)
            blocks.Add(new BoxEl
            {
                Direction = 1, Height = Z.DescriptionLines * Z.DescriptionLine, Margin = new Edges4(0f, Z.HeroDescriptionTop, 0f, 0f),
                Padding = new Edges4(g.Gutter, 0f, g.Gutter, 0f),
                Children = [K.Bar(inner, Z.DescriptionSize), K.Bar(inner * Z.SkelMetaFraction, Z.DescriptionSize)],
            });
        blocks.Add(new BoxEl { Height = Z.HeroBottom });
        blocks.Add(new BoxEl
        {
            Direction = 1, Padding = new Edges4(compactLeft, VerticalLayout.ExpandedToolbarTopPad, compactLeft, VerticalLayout.ExpandedToolbarBottomPad),
            Children = [new BoxEl { Height = VerticalLayout.ToolbarRowHeight }],
        });
        return new BoxEl { Key = "zune.detail.hero-skeleton", Direction = 1, MinHeight = g.Height, Children = blocks.ToArray() };
    }
}
