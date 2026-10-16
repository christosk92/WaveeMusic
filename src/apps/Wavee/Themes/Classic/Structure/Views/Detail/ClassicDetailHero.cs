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
// The band helpers (`Band` / `BandTitle` / `BandByline` / `BandHairline` / `Pivot`) are `ClassicDetailBand` (T10c2): the artist, profile and episode pages
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
using RailLayout = Wavee.Detail.RailLayout;
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
            // The window tier's target edge (Tiers.Detail.cs): the Compact hero's actions are 40 x 40 (board C-Album-Compact). A tracked read, so a
            // crossing re-renders the hero; the core's action-row height follows the same record (`VerticalLayout.ActionRowExtra`).
            blocks.Add(Block("hero-actions", HeroActions(spec, id, cfg, acts, ClassicDetailTiers.Now.Target)));

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
                        // The table's list bar. On a facts page it carries the insights toggle itself (tuned Classic I1) —
                        //    the PRE-STUCK half of the control: once the hero collapses the pinned band's word (§7) is the reachable one.
                        Children = [parts.Toolbar ?? new BoxEl { Height = VerticalLayout.ToolbarRowHeight }],
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
                Children = byline is { Length: > 0 } ? [ClassicDetailBand.BandTitle(id.Title), ClassicDetailBand.BandByline(byline)] : [ClassicDetailBand.BandTitle(id.Title)],
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
            Element normalBand = ClassicDetailBand.Band(availW, parts.CompactLeft, lead, parts.BandActions);

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

    /// <summary>The rail's action row (tuned Classic D1, D20): the Play split, then Shuffle (when the page offers it) · the
    /// heart (DROPPED, not disabled, when <c>Heart == None</c>) · Share · More, in one wrap.</summary>
    static Element HeroActions(VerticalSpec spec, Identity id, in Config cfg, FrameActions acts, float target)
        => ClassicDetailKit.ActionRow("vhero-actions",
            ClassicDetailKit.PlaySplit(id.Subject, spec.Accent, spec.PlayAll ?? DetailVerbs.PlayContext(id.Subject), target),
            ClassicDetailKit.FixedActions("vhero-", id, cfg.Heart != HeartMode.None, acts, spec.Accent, MathF.Max(RailLayout.FabSize, target)),
            Spacing.XS);

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
        blocks.Add(SkeletonActionRow(spec, ClassicDetailTiers.Now.Target));

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
                    Children = [SkeletonToolbarRow(spec, w - 2f * compactLeft)],
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

    /// <summary>The hero's action row while loading (tuned Classic X2): the loaded row's own shape, the Play split then exactly the buttons
    /// <see cref="ClassicDetailKit.FixedActions"/> will lay out for this page (<see cref="ClassicDetailKit.FixedActionCount"/>), in the same
    /// wrapping row. It used to reserve four squares on every page, one too many where the heart or Share is dropped.</summary>
    static Element SkeletonActionRow(VerticalSpec spec, float target)
    {
        float fab = MathF.Max(RailLayout.FabSize, target), h = MathF.Max(Controls.ButtonHeight, target);
        var fabs = new Element[ClassicDetailKit.FixedActionCount(spec.Identity, spec.Config.Heart != HeartMode.None, spec.Actions)];
        for (int i = 0; i < fabs.Length; i++)
            fabs[i] = new BoxEl { Width = fab, Height = fab, Shrink = 0f, Corners = Radii.ControlAll };
        Element split = new BoxEl
        {
            // The loaded split's own shape at the tier's height (both halves grow by what the height adds).
            Width = ClassicDetailTiers.PlaySplitWidthFor(h), Height = h, Shrink = 0f,
            Corners = Radii.ControlAll, Margin = new Edges4(0f, 0f, RailLayout.SplitTrail, 0f),
        };
        return ClassicDetailKit.ActionRow("vhero-skel-actions", split, fabs, Spacing.XS);
    }

    /// <summary>The list bar's reserved box (tuned Classic X2): the 44 band the arithmetic charges, inset 6/5, holding the bar the vertical arm
    /// lays out (CT5b): the commands the same fit (<see cref="Track.CommandBarLayout.Resolve"/> over the first-frame widths) keeps inline at
    /// this width (Filter, labelled or not, and Sort), then Find, Insights on a facts page and "…", 2 apart. The left group is empty: the
    /// vertical arm's Play and Shuffle are the hero's. It used to draw three pills on the left and a 240 search field on the right.</summary>
    static Element SkeletonToolbarRow(VerticalSpec spec, float rowWidth)
    {
        bool insights = spec.Insights is not null;
        float pane = MathF.Max(0f, rowWidth - 2f * VerticalLayout.ToolbarSurfacePadX);
        var fit = Track.CommandBarLayout.Resolve(pane, Track.CommandBarLayout.Nominal, vertical: true, hasTune: false,
                                                 hasInsights: insights, explicitSearch: false);
        var right = new List<Element>(5);
        if (fit.Has(Track.InlineCommand.Filter))
            right.Add(ToolbarPill(fit.Has(Track.InlineCommand.FilterLabel) ? Track.CommandBarLayout.Nominal.Filter : Track.CommandBarLayout.FilterIconWidth));
        if (fit.Has(Track.InlineCommand.Sort)) right.Add(ToolbarPill(Track.CommandBarLayout.Nominal.Sort));
        right.Add(ToolbarPill(Track.CommandBarLayout.SearchIconWidth) with
        {
            Margin = new Edges4(Track.CommandBarLayout.SearchGap - Track.CommandBarLayout.Gap, 0f, 0f, 0f),
        });
        if (insights) right.Add(ToolbarPill(Track.CommandBarLayout.InsightsWidth));
        right.Add(ToolbarPill(Track.CommandBarLayout.MoreWidth));
        return new BoxEl
        {
            Direction = 1, Height = VerticalLayout.ToolbarRowHeight,
            Padding = new Edges4(VerticalLayout.ToolbarSurfacePadX, VerticalLayout.ToolbarSurfacePadY,
                                 VerticalLayout.ToolbarSurfacePadX, VerticalLayout.ToolbarSurfacePadY),
            Children =
            [
                new BoxEl
                {
                    Direction = 0, AlignItems = FlexAlign.Center, Grow = 1f, MinWidth = 0f,
                    Children =
                    [
                        new BoxEl { Grow = 1f, Height = 1f },
                        new BoxEl { Direction = 0, Gap = Track.CommandBarLayout.Gap, AlignItems = FlexAlign.Center, Children = right.ToArray() },
                    ],
                },
            ],
        };
    }

    static BoxEl ToolbarPill(float width) => new BoxEl
    {
        Width = width, Height = VerticalLayout.ToolbarPillHeight, Shrink = 0f, Corners = Radii.ControlAll,
    };
}
