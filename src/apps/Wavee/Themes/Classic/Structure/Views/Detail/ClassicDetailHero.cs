// ── Themes/Classic/Structure/Views/Detail/ClassicDetailHero.cs — Classic's vertical hero and its loading band (themes plan 6.1, T9a) ──────────────
//
// From `Entities\Detail.UI.Hero.cs`: THE VERTICAL HERO SYSTEM (ch 03 §0.3, §0.6-§0.8, §0.14-§0.18; W6-W9, W12-W14, W27-W28) — the expanded
// presentation and the 56-DIP context band with its scroll binds and input handoff (`HeroHost`), the hero's actions and attribution, and
// `HeroSkeleton`, the loading band that IS the loaded band.
//
// WHO CALLS WHAT. The TABLE (`Track.Table.cs`) owns the vertical list: it calls `Detail.Hero(spec, parts)` for item 0 (a plain box that the table
// pins with `Sticky(0)` and collapses with `Collapse`, around the `Detail.Hero` slot this file's `HeroTemplate` draws) and reads the loading band
// from `StyleKeys.DetailHero` (`HeroStyle`: the skeleton deriver cannot run a view slot, so it is a pure builder the table takes from the
// registry). This file never scrolls anything: the expanded presentation binds TransY/Opacity to the NEAREST scroller, the band reveals over the
// last 44 DIP of the collapse, and the chrome's engaged edge the table publishes (`CompactInteractive`) moves hit-testing between the two.
//
// THE THREE NUMBERS THAT MUST AGREE (D49). The skeleton's reserved band, the pre-measure collapse height and the loaded hero are all derived from
// ONE bucketed width (`BucketW`, once, at the top), ONE presence-flag set (`Detail.FlagsOf`) and ONE title plan (`VerticalLayout.TitleTypeFor` over
// the identity's title — pure, so all three agree whenever the title is known): the rules and the arithmetic stay core, this picture draws them.
//
// The band helpers (`Detail.Band` / `BandTitle` / `BandByline` / `BandHairline` / `Pivot`) STAY in `Detail`: the artist, profile and episode pages
// build the same band.
//
// NAME NOTE: `Text`, `Skeleton` and `Config` are nested `Detail` classes; the aliases keep their short names here.

using System.Globalization;
using FluentGpu.Animation;
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
using BandLayout = Wavee.Detail.BandLayout;
using Config = Wavee.Detail.Config;
using FrameActions = Wavee.Detail.FrameActions;
using FrameSlots = Wavee.Detail.FrameSlots;
using HeroParts = Wavee.Detail.HeroParts;
using Identity = Wavee.Detail.Identity;
using InsightsToggle = Wavee.Detail.InsightsToggle;
using Skeleton = Wavee.Detail.Skeleton;
using Text = Wavee.Detail.Text;
using VerticalLayout = Wavee.Detail.VerticalLayout;
using VerticalSpec = Wavee.Detail.VerticalSpec;

namespace Wavee.Themes.Classic;

internal static partial class ClassicDetailFrame
{
    // ══ 1. THE HERO ═══════════════════════════════════════════════════════════════════════════════════════════════════════

    static readonly LayoutTransition HeroGeometryMotion = new(
        TransitionChannels.Bounds, TransitionDynamics.Tween(280f, Easing.SmoothOut), SizeMode.ScaleCorrect);

    static readonly LayoutTransition HeroReflowMotion = new(
        TransitionChannels.Position | TransitionChannels.Size, TransitionDynamics.Tween(280f, Easing.SmoothOut),
        SizeMode.Reveal);

    /// <summary>The hero's loading band, as the style the table reads (<see cref="StyleKeys.DetailHero"/>).</summary>
    public static readonly DetailHeroStyle HeroStyle = new() { Skeleton = HeroSkeleton };

    /// <summary>The registered template of <see cref="ViewKeys.DetailHero"/>: the vertical arm's item 0 — the expanded presentation (artwork · eyebrow ·
    /// title · rule · attribution · meta · pulse · chart · actions · description, then the toolbar) and the 56-DIP context band, scroll-bound. The hero
    /// owns its flow hysteresis, so it is a small component; the props gate it on data (the spec) and on the reference of the table's pieces.</summary>
    public static Element HeroTemplate(DetailHeroProps p, in ViewContext ctx)
        => Embed.Comp(p, static () => new HeroHost());

    /// <summary>The hero host: the expanded presentation and the stuck band.</summary>
    sealed class HeroHost : Component, IPropsHost
    {
        DetailHeroProps? _latest;
        readonly Signal<DetailHeroProps?> _props = new(null);
        readonly Action<RectF> _measure;

        // hysteresis state — plain fields, written in render (not signals: nothing re-renders off them). The TITLE keeps
        // none: its plan is a pure function of (bucketed width, flow, title), so it cannot disagree with its own lines.
        bool _rowFlow, _flowInit, _boundsSeen;
        float _lastMeasuredH;

        public HeroHost() => _measure = Measure;

        public void ApplyProps(object props)
        {
            _latest = (DetailHeroProps)props;
            _props.Value = _latest;
        }

        void Measure(RectF r)
        {
            if (r.H <= 1f) return;
            _boundsSeen = true;
            if (MathF.Abs(r.H - _lastMeasuredH) <= 1f) return;
            _lastMeasuredH = r.H;
            _latest?.Parts.OnHeroMeasured?.Invoke(r.H);           // the table republishes its collapse height
        }

        public override Element Render()
        {
            _ = _props.Value;
            var p = _latest!;
            var spec = p.Spec;
            var parts = p.Parts;
            var id = spec.Identity;
            var cfg = spec.Config;
            var acts = spec.Actions;
            var slots = spec.Slots;

            bool compactCanHit = parts.CompactInteractive.Value;   // subscribe: the stuck edge moves input ownership

            // ── geometry: the flow off the raw width (hysteretic), everything else off ONE bucketed width ──
            float availW = parts.ColumnWidth > 0f ? parts.ColumnWidth : VerticalLayout.FallbackW;
            _rowFlow = VerticalLayout.RowFlow(availW, _rowFlow, _flowInit);
            if (parts.ColumnWidth > 0f) _flowInit = true;
            bool rowFlow = _rowFlow;
            float bw = VerticalLayout.BucketW(availW);
            float pad = VerticalLayout.HeroPadFor(bw, rowFlow);
            float gap = VerticalLayout.HeroGapFor(bw, rowFlow);
            float art = VerticalLayout.ArtworkFor(bw, rowFlow);
            float contentW = VerticalLayout.ContentWidthFor(bw, rowFlow);
            int descLines = VerticalLayout.DescriptionMaxLines(rowFlow);
            float descW = VerticalLayout.DescriptionWidthFor(bw, rowFlow);
            // Unmeasured asks the 256 bucket the preview/skeleton already resolved (a cache hit, not a guess).
            int decodePx = VerticalLayout.ArtworkDecodePx(art, _boundsSeen);
            ColorF accent = spec.Accent();
            var f = Detail.FlagsOf(spec);

            // ── the title TYPE PLAN: ONE size per (bucketed width, title), drawn exactly (no auto-fit, no hysteresis) ──
            var plan = VerticalLayout.TitleTypeFor(bw, rowFlow, id.Title,
                eyebrow: f.Eyebrow, attribution: f.Attribution, meta: f.Meta, pulse: f.Pulse, chart: f.Chart);

            // ── the identity column, block for block (each keyed; late rows fade up, every row FLIPs) ──
            var blocks = new List<Element>(10);
            if (f.Eyebrow) blocks.Add(Block("hero-eyebrow", ClassicDetailKit.EyebrowRun(id.Eyebrow), late: true));
            blocks.Add(Block("hero-title", slots.Title is { } editTitle
                ? editTitle(plan.Size, float.NaN)
                : R.Type.DetailHero(id.Title) with
                {
                    Size = plan.Size, Weight = 600,
                    // CLEARED: the natural line box of the size drawn (the plan's LineHeight is that same number).
                    LineHeight = float.NaN,
                    Width = plan.WrapWidth, MaxWidth = plan.WrapWidth,
                    // WRAPS freely: the cap is the pathological-length guard, never the plan's line count.
                    Wrap = TextWrap.WrapWholeWords, MaxLines = VerticalLayout.TitleLineCap,
                    Trim = TextTrim.CharacterEllipsis, Color = Tok.TextPrimary,
                }));
            blocks.Add(Block("hero-rule", Controls.AccentRule(accent)));
            if (f.Attribution) blocks.Add(Block("hero-attribution", HeroAttribution(id, slots, contentW, accent), late: true));
            if (f.Meta) blocks.Add(Block("hero-meta", HeroMeta(id, contentW), late: true));
            if (f.Pulse && slots.Pulse is { } pulse) blocks.Add(Block("hero-pulse", pulse(), late: true));
            if (f.Chart) blocks.Add(Block("hero-chart", ChartCaption(id, slots), late: true));
            blocks.Add(Block("hero-actions", HeroActions(spec, id, cfg, acts)));

            // The description is no longer a row of the identity column: it is the padded box's second child, after
            // the whole [artwork, identity] row, in both flows (D49's band arithmetic already excludes it here).
            Element? description = null;
            if (id.EditableMetadata && slots.Description is { } editDescription)
                description = editDescription(descW);
            else if (id.DescriptionHtml is { Length: > 0 } html)
                // Clamped with the inline "… More" the engine shows only when the body overflows, and the whole text on
                // hover while collapsed (fullTextTip).
                description = Controls.ExpandableRichText(html, 13f, Tok.TextSecondary, accent, descW, descLines,
                                                          id.Subject.Text, DetailVerbs.NavigateKey, fullTextTip: true);

            float identityGap = VerticalLayout.IdentityGapFor(bw, rowFlow, plan,
                f.Eyebrow, f.Attribution, f.Meta, pulse: f.Pulse, chart: f.Chart);

            // AlignItems Stretch (+ a definite width when stacked) is load-bearing: the action row WRAPS, and a wrap
            // needs a definite width to wrap against.
            Element identity = new BoxEl
            {
                Direction = 1, Gap = identityGap, AlignItems = FlexAlign.Stretch,
                Width = rowFlow ? float.NaN : contentW,
                Grow = rowFlow ? 1f : 0f, Basis = rowFlow ? 0f : float.NaN, MinWidth = 0f,
                MinHeight = VerticalLayout.IdentityMinHeightFor(bw, rowFlow),
                Children = blocks.ToArray(),
            };

            // The page's ANCHOR: no entrance, no morph. The bounds tween grows from the top-left so the cover's corner
            // stays pinned to the title's top across a resize.
            Element artwork = ClickableCover(new BoxEl
            {
                Width = art, Height = art, Shrink = 0f,
                Corners = CornerRadius4.All(Radii.Card), Shadow = Elevation.Card, ClipToBounds = true,
                Animate = HeroGeometryMotion, TransformOriginX = 0f, TransformOriginY = 0f,
                Draggable = acts.CoverDrag is { } drag ? Drag.Source(drag) : null,
                Children = [slots.Cover?.Invoke(art)
                    ?? Controls.Artwork(id.CoverUrl, art, art, Radii.Card, decodePx: decodePx, saturation: 1.18f)],
            }, acts.CoverClick);

            // Stacked ↔ row is the SAME two children in the same order: the reflow animates as one gesture.
            Element hero = new BoxEl
            {
                Direction = rowFlow ? (byte)0 : (byte)1, Gap = gap, AlignItems = FlexAlign.Start,
                Animate = HeroReflowMotion,
                Children = [artwork, identity],
            };

            Element expanded = new BoxEl
            {
                Direction = 1, Animate = HeroReflowMotion, OnBoundsChanged = _measure,
                Children =
                [
                    new BoxEl
                    {
                        Direction = 1, Animate = HeroReflowMotion,
                        Padding = new Edges4(pad, pad, pad, VerticalLayout.HeroBottomPad),
                        Children = description is null
                            ? [hero]
                            : [hero, Block("hero-description", description, late: true) with
                                  { Margin = new Edges4(0f, VerticalLayout.DescriptionGapFor(rowFlow), 0f, 0f) }],
                    },
                    new BoxEl
                    {
                        Direction = 1,
                        Padding = new Edges4(parts.CompactLeft, VerticalLayout.ExpandedToolbarTopPad,
                                             parts.CompactLeft, VerticalLayout.ExpandedToolbarBottomPad),
                        // ── INSIGHTS SHEET (additive) ── the toolbar ROW: the table's command bar, plus the sheet's
                        //    toggle pinned to its trailing end when this page has facts and no rail to show them in
                        //    (Detail.Insights.cs). The bar keeps the whole row when there is no toggle, so a page
                        //    without facts composes exactly the tree it composed before. This is the PRE-STUCK half of
                        //    the control: once the hero collapses the pinned band's word (§7) is the reachable one.
                        Children = [ToolbarRow(parts, spec.Insights)],
                    },
                ],
            };

            // ── the STUCK BAND: typography only, NO fill (the offset model — the rows are clipped under it) ──
            float heroH = parts.HeroHeight > 1f ? parts.HeroHeight : Detail.HeroBandHeight(spec, availW);
            float cd = VerticalLayout.CollapseDistance(heroH);

            string? byline = Text.Byline(id.OwnerName, id.Meta, id.Eyebrow);
            Element identityBlock = new BoxEl
            {
                Direction = 1, MinWidth = 0f, Shrink = 1f, Gap = 0f,
                Children = byline is { Length: > 0 } ? [Detail.BandTitle(id.Title), Detail.BandByline(byline)] : [Detail.BandTitle(id.Title)],
            };
            // The expanded search field takes the TITLE's place, never the actions' — one zero-gap slot, so the hidden
            // arm cannot leave a cluster gap behind.
            Element lead = identityBlock;
            if (parts.SearchField is { } searchField)
            {
                var searchExpanded = parts.SearchExpanded;
                lead = new BoxEl
                {
                    Direction = 0, MinWidth = 0f, Shrink = 1f, Gap = 0f, AlignItems = FlexAlign.Center,
                    Children =
                    [
                        Flow.Show(() => !searchExpanded.Value, identityBlock),
                        Flow.Show(() => searchExpanded.Value, new BoxEl { Shrink = 0f, Children = [searchField] }),
                    ],
                };
            }
            Element normalBand = Detail.Band(availW, parts.CompactLeft, lead, parts.BandActions);

            // The selection arm swaps the band's CONTENT at the same 56 — the same band in another mode, not a plate.
            Element bandContent = normalBand;
            if (parts.SelectionBar is { } selectionBar)
            {
                var selectionVisible = parts.SelectionVisible;
                Element selectionBand = new BoxEl
                {
                    Direction = 1, Width = availW, Height = BandLayout.Height,
                    Padding = new Edges4(parts.CompactLeft, 4f, parts.CompactLeft, 4f),
                    Justify = FlexJustify.Center, HitTestVisible = true,
                    Children = [selectionBar],
                };
                bandContent = new BoxEl
                {
                    ZStack = true, Width = availW, Height = BandLayout.Height,
                    Children =
                    [
                        Flow.Show(() => !selectionVisible.Value, normalBand),
                        Flow.Show(() => selectionVisible.Value, selectionBand),
                    ],
                };
            }

            // Input ownership crosses WITH the band: it takes hits only once stuck (and passes the rest through).
            // ── INSIGHTS SHEET (additive) ── this gate is what makes the sheet's TWO entry points one control rather
            //    than two. `parts.BandActions` now carries the sheet's toggle beside Find · Filter · Play, and it is
            //    live exactly when `compactCanHit` is true, while the hero's own toolbar toggle (inside `expanded`,
            //    under `presentation` below) is live exactly when it is false. Never both, never neither.
            Element compact = new BoxEl
            {
                ZStack = true, Width = availW, Height = BandLayout.Height,
                HitTestVisible = compactCanHit, HitTestPassThrough = true,
                Children = [bandContent],
            }.Reveal(VerticalLayout.CompactRevealStart(cd), cd - VerticalLayout.CompactRevealStart(cd), Design.Reduced ? 0f : Spacing.XS);

            // …and the scrolled-away hero stops eating clicks at the same edge.
            // It rides the scroll back out of the pinned hero (0 → −cd over the collapse) and fades over the tail of it.
            Element presentation = (ZStack(expanded) with { Direction = 1, HitTestVisible = !compactCanHit })
                .Parallax(0.0, cd, 0f, -cd)
                .Fade(VerticalLayout.ExpandedFadeStart(cd), cd, 1f, 0f);
            return ZStack(presentation, compact) with { Direction = 1 };
        }
    }

    /// <summary>A keyed identity block: a position-only FLIP always, a fade-up entrance when it can arrive late.</summary>
    static BoxEl Block(string key, Element child, bool late = false) => new()
    {
        Key = key, Direction = 1, Layout = ClassicDetailKit.Shove, Enter = late ? ClassicDetailKit.FadeUp : (EnterExit?)null, Children = [child],
    };

    /// <summary>W27: Play 32 + the 32-DIP satellites — Shuffle (when the page offers it) · the heart (DROPPED, not
    /// disabled, when <c>Heart == None</c>) · Share · More.</summary>
    static Element HeroActions(VerticalSpec spec, Identity id, in Config cfg, FrameActions acts)
    {
        var actions = new List<Element>(5)
        {
            Controls.PlayButton(spec.Accent, spec.PlayAll ?? DetailVerbs.PlayContext(id.Subject)) with { Key = "vhero-play" },
        };
        if (acts.Shuffle is { } shuffle)
            actions.Add(Detail.Satellite(Icons.Shuffle, Loc.Get(Strings.Detail.Shuffle), shuffle) with { Key = "vhero-shuffle" });
        if (cfg.Heart != HeartMode.None)
        {
            // Keyed on the TARGET: SaveButton's uri freezes at mount. The heart reads the page accent through
            // Design.AccentCtx, which the frame host provides.
            string saveUri = Detail.SaveTargetOf(id).Text;
            string saveName = id.Title;
            actions.Add(Embed.Comp(() => new Controls.SaveButton
                { Uri = saveUri, Name = saveName, Glyph = 16f, Box = Controls.IconButtonSize })
                with { Key = "vhero-save:" + saveUri });
        }
        if (Detail.ShareActionFor(id) is { } share)
            actions.Add(Detail.Satellite(Icons.Share, Loc.Get(Strings.Menu.Share), share) with { Key = "vhero-share" });
        if (acts.More is { } more)
            actions.Add(Detail.MoreButton(more, Controls.IconButtonSize) with { Key = "vhero-more:" + id.Subject.Text });
        return new BoxEl
        {
            Direction = 0, Gap = Spacing.S, Wrap = true,
            AlignItems = FlexAlign.Center, Justify = FlexJustify.Start,
            Margin = new Edges4(0f, Spacing.XS, 0f, 0f),
            Children = actions.ToArray(),
        };
    }

    /// <summary>The hero attribution — THREE arms in precedence order after the page's own slot: the collaborator pile
    /// (width-keyed), the owner run, the billed-artist accent spans (NoWrap, one line, ", " joined).</summary>
    static Element HeroAttribution(Identity id, FrameSlots slots, float contentW, ColorF accent)
    {
        if (slots.Attribution is { } attribution && id.Kind != DetailKind.Album) return attribution(contentW);
        if (id.Collaborators is { Count: > 0 } collaborators && Text.ShowCollaborators(collaborators.Count, id.Collaborative))
            return new BoxEl
            {
                Key = "pl-collab:" + ((int)contentW).ToString(CultureInfo.InvariantCulture),
                Direction = 0, AlignItems = FlexAlign.Center, MaxWidth = contentW,
                Children = [Controls.FacePile(collaborators)],
            };
        if (id.OwnerName is { Length: > 0 } owner)
        {
            var ownerText = new TextEl(owner)
            {
                Size = ClassicDetailLayout.AttributionSize, LineHeight = ClassicDetailLayout.AttributionLine, Weight = 600, Color = Tok.TextSecondary,
                MaxWidth = contentW, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
            };
            // The owner opens their profile (#161) — a text link (accent on hover), inert when the owner has no uri.
            Action? openProfile = DetailVerbs.OpenOwner(id.Owner, owner);
            if (openProfile is null) return ownerText;
            return new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, MinWidth = 0f, MaxWidth = contentW,
                Role = AutomationRole.Hyperlink, Focusable = true, Cursor = CursorId.Hand, OnClick = openProfile,
                Children = [ownerText with { HoverColor = Tok.AccentTextPrimary, BrushTransitionMs = Design.Motion.Faster }],
            };
        }
        var artists = id.Artists;
        if (artists is not { Count: > 0 }) return new BoxEl();
        var spans = new TextSpan[artists.Count * 2 - 1];
        int at = 0;
        for (int i = 0; i < artists.Count; i++)
        {
            if (i > 0) spans[at++] = new TextSpan(", ");
            var face = artists[i];
            spans[at++] = new TextSpan(face.Name, Weight: 600, Color: accent, OnClick: face.OnClick);
        }
        return new SpanTextEl(spans)
        {
            Size = ClassicDetailLayout.AttributionSize, LineHeight = ClassicDetailLayout.AttributionLine, Color = Tok.TextSecondary,
            MaxWidth = contentW, Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
        };
    }

    /// <summary>The hero meta line (12/16, two whole-word lines at the content measure), or its loading bar.</summary>
    static Element HeroMeta(Identity id, float contentW)
        => MetaLine(id, width: float.NaN, contentW, maxLines: 2);

    // ══ 4. THE SKELETON BAND (D49) ═══════════════════════════════════════════════════════════════════════════════════

    /// <summary>The vertical arm's reserved LOADING band: the same parts at the same sizes from the same resolver as the
    /// loaded hero, bucketed once. Unfilled boxes — the table mounts this as its skeleton region's shimmer SOURCE and
    /// the engine's deriver paints the bars; a usable preview cover is painted for real (exempt from the deriver).</summary>
    internal static Element HeroSkeleton(VerticalSpec spec, float columnWidth, float compactLeft)
    {
        float w = columnWidth > 0f ? columnWidth : VerticalLayout.FallbackW;
        bool rowFlow = VerticalLayout.RowFlow(w);
        float bw = VerticalLayout.BucketW(w);
        var f = Detail.FlagsOf(spec);
        var id = spec.Identity;
        float pad = VerticalLayout.HeroPadFor(bw, rowFlow);
        float gap = VerticalLayout.HeroGapFor(bw, rowFlow);
        float art = VerticalLayout.ArtworkFor(bw, rowFlow);
        float contentW = VerticalLayout.ContentWidthFor(bw, rowFlow);
        float descW = VerticalLayout.DescriptionWidthFor(bw, rowFlow);
        // The KNOWN title's plan (the loaded hero's own, so the swap does not jump), else the PESSIMISTIC null-title plan
        // (an empty Title is null to the estimator) — the one the pre-measure collapse height builds too.
        var plan = VerticalLayout.TitleTypeFor(bw, rowFlow, id.Title,
            eyebrow: f.Eyebrow, attribution: f.Attribution, meta: f.Meta, pulse: f.Pulse, chart: f.Chart);
        float identityGap = VerticalLayout.IdentityGapFor(bw, rowFlow, plan,
            f.Eyebrow, f.Attribution, f.Meta, pulse: f.Pulse, chart: f.Chart);

        var blocks = new List<Element>(8);
        if (f.Eyebrow) blocks.Add(Bar(Skeleton.BarWidth(contentW, Skeleton.EyebrowFraction), VerticalLayout.EyebrowRowHeight));
        blocks.Add(Lines(plan.WrapWidth, plan.LineHeight, plan.Lines, Skeleton.TitleLastLineFraction));
        blocks.Add(new BoxEl
        {
            Width = Skeleton.RuleWidth, Height = Skeleton.RuleHeight, AlignSelf = FlexAlign.Start,
            Margin = new Edges4(0f, Skeleton.RuleGap, 0f, 0f), Corners = CornerRadius4.All(Skeleton.RuleRadius),
        });
        if (f.Attribution) blocks.Add(Bar(Skeleton.BarWidth(contentW, Skeleton.AttributionFraction), VerticalLayout.AttributionRowHeight));
        if (f.Meta) blocks.Add(Bar(Skeleton.BarWidth(contentW, Skeleton.MetaFraction), VerticalLayout.MetaRowHeight));
        if (f.Pulse) blocks.Add(Bar(Skeleton.BarWidth(contentW, Skeleton.PulseFraction), VerticalLayout.PulseRowHeight));
        if (f.Chart) blocks.Add(Bar(Skeleton.BarWidth(contentW, Skeleton.ChartFraction), VerticalLayout.ChartRowHeight));
        blocks.Add(SkeletonActionRow());

        Element identity = new BoxEl
        {
            Direction = 1, Gap = identityGap, AlignItems = FlexAlign.Stretch,
            Width = rowFlow ? float.NaN : contentW,
            Grow = rowFlow ? 1f : 0f, Basis = rowFlow ? 0f : float.NaN, MinWidth = 0f,
            MinHeight = VerticalLayout.IdentityMinHeightFor(bw, rowFlow),
            Children = blocks.ToArray(),
        };

        // The REAL preview cover when one is known (the same 256 bucket the unmeasured hero asks for), exempted from the
        // deriver with a self-override; else the plain reserved card — the page SUBJECT's own shape, the rail's
        // skeleton cover (Detail.UI.cs) at the hero's edge.
        Element hero = new BoxEl
        {
            Direction = rowFlow ? (byte)0 : (byte)1, Gap = gap, AlignItems = FlexAlign.Start,
            Children = [SkeletonCover(id.CoverUrl, art), identity],
        };

        return new BoxEl
        {
            Key = "detail-skeleton:hero",
            Direction = 1,
            // A FLOOR (never a cap): the SAME number the loaded hero's collapse binds assume before their first measure.
            MinHeight = VerticalLayout.HeroBandHeight(bw, rowFlow, plan,
                f.Eyebrow, f.Attribution, f.Meta, f.Description, pulse: f.Pulse, chart: f.Chart),
            Children =
            [
                new BoxEl
                {
                    Direction = 1, Padding = new Edges4(pad, pad, pad, VerticalLayout.HeroBottomPad),
                    Children = f.Description
                        ? [hero, Lines(descW, VerticalLayout.DescriptionLineHeight,
                              VerticalLayout.DescriptionMaxLines(rowFlow), Skeleton.DescriptionLastLineFraction) with
                              { Margin = new Edges4(0f, VerticalLayout.DescriptionGapFor(rowFlow), 0f, 0f) }]
                        : [hero],
                },
                new BoxEl
                {
                    Direction = 1,
                    Padding = new Edges4(compactLeft, VerticalLayout.ExpandedToolbarTopPad,
                                         compactLeft, VerticalLayout.ExpandedToolbarBottomPad),
                    Children = [SkeletonToolbarRow()],
                },
            ],
        };
    }

    static Element Bar(float width, float height) => new BoxEl
    {
        Width = width, Height = height, Corners = CornerRadius4.All(Skeleton.BarRadius),
    };

    /// <summary>N stacked runs at the measure, the last one short — zero gap (the run's own line height is the spacing).</summary>
    static BoxEl Lines(float measure, float lineHeight, int lines, float lastFraction)
    {
        int count = Skeleton.LineCount(lines);
        var kids = new Element[count];
        for (int i = 0; i < count; i++) kids[i] = Bar(Skeleton.LineWidth(measure, i, lines, lastFraction), lineHeight);
        return new BoxEl { Direction = 1, Gap = 0f, Children = kids };
    }

    static Element SkeletonActionRow()
    {
        var kids = new Element[1 + Skeleton.SatelliteCount];
        kids[0] = new BoxEl
        {
            Width = Skeleton.PlayButtonWidth, Height = Controls.ButtonHeight,
            Corners = Radii.ControlAll,
        };
        for (int i = 1; i < kids.Length; i++)
            kids[i] = new BoxEl
            {
                Width = Controls.IconButtonSize, Height = Controls.IconButtonSize, Shrink = 0f, Corners = Radii.ControlAll,
            };
        return new BoxEl
        {
            Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center,
            Margin = new Edges4(0f, Spacing.XS, 0f, 0f), Height = Controls.ButtonHeight,
            Children = kids,
        };
    }

    /// <summary>The command bar's reserved box: the 44 band the arithmetic charges, pills at control height inset 6/5.</summary>
    static Element SkeletonToolbarRow() => new BoxEl
    {
        Direction = 1, Height = VerticalLayout.ToolbarRowHeight,
        Padding = new Edges4(VerticalLayout.ToolbarSurfacePadX, VerticalLayout.ToolbarSurfacePadY,
                             VerticalLayout.ToolbarSurfacePadX, VerticalLayout.ToolbarSurfacePadY),
        Children =
        [
            new BoxEl
            {
                Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, Grow = 1f, MinWidth = 0f,
                Children =
                [
                    ToolbarPill(Skeleton.ToolbarPillA), ToolbarPill(Skeleton.ToolbarPillB), ToolbarPill(Skeleton.ToolbarPillC),
                    new BoxEl { Grow = 1f, Height = 1f },
                    ToolbarPill(Track.CommandBarLayout.SearchPreferred),
                ],
            },
        ],
    };

    static Element ToolbarPill(float width) => new BoxEl
    {
        Width = width, Height = VerticalLayout.ToolbarPillHeight, Shrink = 0f, Corners = Radii.ControlAll,
    };

    // ══ 5. THE HERO TOOLBAR'S ROW ═══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The vertical arm's toolbar ROW: the table's own command bar and, trailing it, the sheet's toggle. With
    /// no toggle the bar IS the row — a page without facts composes exactly the tree it composed before.</summary>
    static Element ToolbarRow(in HeroParts parts, InsightsToggle? insights)
    {
        Element bar = parts.Toolbar ?? new BoxEl { Height = VerticalLayout.ToolbarRowHeight };
        if (insights is null) return bar;
        return new BoxEl
        {
            Key = "vhero:toolbar-row", Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.XS, MinWidth = 0f,
            Children =
            [
                // Direction 1 on the wrapper: a single-child row wrapper shrink-wraps its child on the MAIN axis, and
                // the command bar must keep the whole remaining width to resolve its own promote/evict fit.
                new BoxEl { Key = "vhero:toolbar", Direction = 1, Grow = 1f, Shrink = 1f, MinWidth = 0f, Children = [bar] },
                Slots.View(ViewKeys.DetailInsightsToggle, new DetailInsightsToggleProps(insights, DetailInsightsToggleForm.HeroButton))
                    with { Key = "insights:toggle-tip" },
            ],
        };
    }
}
