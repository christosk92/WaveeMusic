// ── Themes/Zune/Structure/Views/Detail/ZuneDetailHero.cs — Zune's vertical hero and its loading band (themes plan 6.3; P5 Z6a1) ───────────────
//
// Board Z-Album-Compact: the vertical arm's item 0 (the table pins it with `Sticky(0)` and collapses it with `Collapse`, around the `Detail.Hero`
// slot this file draws). The EXPANDED presentation is the Compact panorama (64/72, bleeding or fitting), the cover (144 at 400, 0.42 of the
// column, 96 to 144) beside the artist (18/24) and the meta line, the 40 ring row (play and saved with their words where the column has the room,
// share and "..." pushed to its end), the description when the page has one, then the table's list bar. The STUCK band (56) is the title and the
// byline beside the table's Find · Filter · Play words; the selection arm swaps the band's content at the same height; the expanded search field
// takes the title's place.
//
// THE NUMBERS AGREE. The hero's height is ONE rule (`ZuneDetailRules.HeroHeight`: the panorama band, the cover row, the action row and its air,
// the description's reserved lines, the bottom air, the list bar's block), every block laid out at exactly that height; the loading band
// (`HeroStyle`, the style the table reads: the skeleton deriver cannot run a view slot) reserves the same blocks, and the stuck band's collapse
// distance is derived from the same number until the table measured the hero (`OnHeroMeasured`). So the skeleton dissolves into the hero and
// nothing under it moves.
//
// SCROLL. The hero never scrolls anything: the expanded presentation binds its offset and opacity to the nearest scroller, the band reveals over
// the tail of the collapse, and the table's `CompactInteractive` edge moves hit-testing between the two (one live entry point per control).
// MOTION (production, deliberate): the late rows fade up, every keyed row glides, the cover's bounds tween from its top-left.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
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

/// <summary>Zune's vertical hero (<see cref="ViewKeys.DetailHero"/>) and its loading band (<see cref="StyleKeys.DetailHero"/>).</summary>
internal static class ZuneDetailHero
{
    /// <summary>The hero's loading band, as the style the table reads.</summary>
    public static readonly DetailHeroStyle Style = new() { Skeleton = Skeleton };

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
        // One home per verb (audit A4): play and saved leave the ring row where the window's band carries them; a playlist's or Liked Songs'
        // panorama takes its own step (audit A8), its band sized to it, and the skeleton reads the same geometry.
        var lay = ZunePlaylistTiers.Panorama(
            ZuneDetailTiers.Hero with { PlayWord = words, SaveWord = words, PrimaryInHero = ZuneDetailTiers.PrimaryInHero(in tiers) },
            ZunePlaylistTiers.Compact, id.Kind);
        float cover = Z.HeroCoverFor(w, gutter);
        bool description = (id.EditableMetadata && spec.Slots.Description is not null) || id.DescriptionHtml is { Length: > 0 };
        return new Geometry(lay, w, gutter, cover, description, Z.HeroHeight(lay, cover, description, ToolbarBlock));
    }

    /// <summary>The hero host: the expanded presentation and the stuck band.</summary>
    sealed class HeroHost : Component, IPropsHost
    {
        DetailHeroProps? _latest;
        readonly Signal<DetailHeroProps?> _props = new(null);
        readonly Action<RectF> _measure;
        float _lastMeasuredH;

        public HeroHost() => _measure = Measure;

        public void ApplyProps(object props)
        {
            _latest = (DetailHeroProps)props;
            _props.Value = _latest;
        }

        void Measure(RectF r)
        {
            if (r.H <= 1f || MathF.Abs(r.H - _lastMeasuredH) <= 1f) return;
            _lastMeasuredH = r.H;
            _latest?.Parts.OnHeroMeasured?.Invoke(r.H);       // the table republishes its collapse height
        }

        public override Element Render()
        {
            _ = _props.Value;
            var tiers = TierContext.DrawnIn(UseContext(ThemeFactsContext.Signal)).Value.Tiers;
            var p = _latest!;
            var spec = p.Spec;
            var parts = p.Parts;
            var id = spec.Identity;
            var slots = spec.Slots;
            bool compactCanHit = parts.CompactInteractive.Value;   // subscribe: the stuck edge moves input ownership
            var g = GeometryOf(spec, parts.ColumnWidth, tiers);
            var lay = g.Layout;
            float inner = MathF.Max(0f, g.Width - 2f * g.Gutter);

            // ── the cover row: the cover, then the artist and the meta at its foot ──
            Element identity = new BoxEl
            {
                Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Gap = Z.HeroIdentityGap, Justify = FlexJustify.End,
                Children =
                [
                    K.LateRow("hero:artist", new BoxEl
                    {
                        Direction = 0, AlignItems = FlexAlign.Center, Height = Z.HitHeight(tiers.Window, lay.ArtistLine), MinWidth = 0f,
                        Children = [slots.Attribution?.Invoke(inner - g.Cover - Z.HeroCoverGap)
                            ?? K.Artists(id, inner - g.Cover - Z.HeroCoverGap, lay, Z.HitHeight(tiers.Window, lay.ArtistLine))],
                    }),
                    K.LateRow("hero:meta", K.Meta(id, float.NaN, lay, lines: 2)),
                ],
            };
            Element coverRow = new BoxEl
            {
                Key = "hero:cover-row", Direction = 0, Gap = Z.HeroCoverGap, Height = g.Cover, Shrink = 0f, MinWidth = 0f,
                Padding = new Edges4(g.Gutter, 0f, g.Gutter, 0f),
                Children =
                [
                    K.Cover("hero:cover", id, spec.Actions, slots, g.Cover) with
                    {
                        Animate = Z.HeroGeometry, TransformOriginX = 0f, TransformOriginY = 0f,
                    },
                    identity,
                ],
            };
            Element actions = new BoxEl
            {
                Key = "hero:actions", Direction = 1, Height = lay.RingSize, Shrink = 0f, MinWidth = 0f, ClipToBounds = true,
                Margin = new Edges4(0f, Z.HeroActionsTop, 0f, 0f), Padding = new Edges4(g.Gutter, 0f, g.Gutter, 0f),
                Children = [K.Rings(Z.ActionsKey, id, spec.Config, spec.Actions, slots, spec.Accent, spec.PlayAll ?? DetailVerbs.PlayContext(id.Subject), lay, spring: true)],
            };
            var blocks = new List<Element>(6) { K.PanoramaBand(id, slots, lay, g.Gutter), coverRow, actions };
            if (g.Description && K.Description(id, slots, inner, spec.Accent) is { } description)
                blocks.Add(K.LateRow("hero:description", new BoxEl
                {
                    Direction = 1, Height = Z.DescriptionLines * Z.DescriptionLine, MinWidth = 0f, ClipToBounds = true,
                    Margin = new Edges4(0f, Z.HeroDescriptionTop, 0f, 0f), Padding = new Edges4(g.Gutter, 0f, g.Gutter, 0f),
                    Children = [description],
                }));
            else if (g.Description)
                blocks.Add(new BoxEl { Height = Z.DescriptionLines * Z.DescriptionLine, Margin = new Edges4(0f, Z.HeroDescriptionTop, 0f, 0f) });
            blocks.Add(new BoxEl { Height = Z.HeroBottom, Shrink = 0f });
            blocks.Add(new BoxEl
            {
                Key = "hero:toolbar", Direction = 1,
                Padding = new Edges4(parts.CompactLeft, VerticalLayout.ExpandedToolbarTopPad, parts.CompactLeft, VerticalLayout.ExpandedToolbarBottomPad),
                // The table's list bar; on a facts page it carries the insights toggle (the pre-stuck entry point; the band's word is the stuck one).
                Children = [parts.Toolbar ?? new BoxEl { Height = VerticalLayout.ToolbarRowHeight }],
            });

            Element expanded = new BoxEl
            {
                Key = Z.HeroKey, Direction = 1, MinHeight = g.Height, OnBoundsChanged = _measure,
                Children = blocks.ToArray(),
            };

            // ── the STUCK band: typography only, no fill (the rows are clipped under it) ──
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

            Element compact = new BoxEl
            {
                ZStack = true, Width = g.Width, Height = BandLayout.Height,
                HitTestVisible = compactCanHit, HitTestPassThrough = true,
                Children = [bandContent],
            }.Reveal(VerticalLayout.CompactRevealStart(cd), cd - VerticalLayout.CompactRevealStart(cd), Design.Reduced ? 0f : Spacing.XS);

            Element presentation = (ZStack(expanded) with { Direction = 1, HitTestVisible = !compactCanHit })
                .Parallax(0.0, cd, 0f, -cd)
                .Fade(VerticalLayout.ExpandedFadeStart(cd), cd, 1f, 0f);
            return ZStack(presentation, compact) with { Direction = 1 };
        }
    }

    /// <summary>The loading band: the same blocks at the same heights, unfilled (the deriver paints the bars); a known cover is painted for real. The
    /// band's floor is the hero's own height, the number the collapse assumes until the hero is measured.</summary>
    static Element Skeleton(VerticalSpec spec, float columnWidth, float compactLeft, TierFacts tiers)
    {
        var g = GeometryOf(spec, columnWidth, tiers);
        var lay = g.Layout;
        var id = spec.Identity;
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
