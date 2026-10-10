// ── Entities/Detail.UI.Hero.cs ─────────────────────────────────────────────────────────────────────────────────────
// THE VERTICAL HERO SYSTEM (ch 03 §0.3, §0.6-§0.8, §0.14-§0.18; W6-W9, W12-W14, W27-W28): VerticalSpec + HeroParts (the
// table's view of the vertical arm), Hero — the expanded presentation and the 56-DIP context band with its scroll binds
// and input handoff — HeroBandHeight (the known title's plan, else the pessimistic null-title one), HeroSkeleton (the
// loading band that IS the loaded band), and the band helpers Band / BandTitle / BandByline / BandHairline / Pivot the artist page also consumes.
//
// Role: UI
// Owner: M
// Wave: 4.5
// Budget: part of Detail.UI.cs's 2600 (ch 03 §9: hero 650 · band 200 · skeleton 200)
// Spec: ch 03 §9
//
// ── WHO CALLS WHAT ───────────────────────────────────────────────────────────────────────────────────────────────────
//
// The TABLE (Track.Table.cs) owns the vertical list: it calls Hero(spec, parts) for item 0 (pinning it with Sticky(0) and
// collapsing its presented height with Collapse) and HeroSkeleton(...) for its shimmer, and pins its chrome with
// Sticky(56, engaged:). This file never scrolls anything: the expanded presentation binds TransY/Opacity to the NEAREST
// scroller, the band reveals over the last 44 DIP of the collapse, and the chrome's engaged edge the table publishes
// (CompactInteractive) moves hit-testing between the two.
//
// ── THE THREE NUMBERS THAT MUST AGREE (D49) ──────────────────────────────────────────────────────────────────────────
//
// The skeleton's reserved band, the pre-measure collapse height and the loaded hero are all derived from ONE bucketed
// width (BucketW, once, at the top), ONE presence-flag set (FlagsOf) and ONE title plan (VerticalLayout.TitleTypeFor over
// the identity's title — pure, so all three agree whenever the title is known): every block the hero adds has its flag,
// and the flow decision is read off the raw column width in all three (424 enter / 400 leave).
//
// THE COLLAPSE FLOOR. D49's three numbers are the EXPANDED hero's height and are floor-independent: the floor
// (VerticalLayout.BandFloor, the band's 56) only changes where the collapse ENDS. An album, a prerelease or a playlist has
// no Zune row 2 (ZuneSubRow.None): it keeps its own sticky band in every nav style, so its hero collapses to the band's 56
// and the chrome sticks below it. NO scroll compensation is needed or written (fluent-gpu ScrollEffect.cs:153-172):
// Collapse and Sticky are PAINT channels (PresentedH / ClipBottom / TransY), so the rows never move; the pairing
// over = H - floor with minH = floor keeps the hero's presented bottom at H - offset (clamped at the floor), so it rides
// the column header exactly. The band keeps drawing the whole time, so the stuck strip is never empty.
//
// NAME NOTE: `Text`, `Skeleton` and `Config` are nested Detail classes here — text runs are `new TextEl`/`Ui.*`.

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
using static FluentGpu.Dsl.Ui;

namespace Wavee;

public static partial class Detail
{
    // ══ 1. THE TABLE'S VIEW OF THE VERTICAL ARM ══════════════════════════════════════════════════════════════════════

    /// <summary>The vertical arm, as the TABLE sees it. Equality is DATA only (identity, config, verb/slot presence); the
    /// accent is a late-bound thunk (reading it subscribes the caller to the frame's accent signal).</summary>
    public sealed record VerticalSpec(Identity Identity, Config Config, FrameActions Actions, FrameSlots Slots, Func<ColorF> Accent)
    {
        /// <summary>The frame's play-all trampoline (the table's visible-order cell, else the context). Set by the frame
        /// only; not part of the table contract and not compared. Null ⇒ the hero plays the context.</summary>
        internal Action? PlayAll { get; init; }

        /// <summary>── INSIGHTS SHEET (additive) ── The frame's sheet toggle, present when this page's KIND carries facts
        /// (<see cref="InsightsSheet.ToggleSlotReserved"/>): the toggle's slot in the command bar and the band's cluster is
        /// RESERVED from the first frame. Set by the frame only — a stable per-host object, so like <see cref="PlayAll"/> its
        /// OPEN state travels by signal, never by a re-push. Null ⇒ this kind has no facts and the command bar carries no
        /// toggle.</summary>
        internal InsightsToggle? Insights { get; init; }

        /// <summary>── INSIGHTS SHEET ── The facts have settled (<see cref="InsightsSheet.ShowsToggle"/>): the reserved toggle is
        /// visible and live. Until then it is present at opacity 0 and inert, so a late fact adds no button and moves nothing.</summary>
        internal bool InsightsLive { get; init; }

        public bool Equals(VerticalSpec? o)
        {
            if (ReferenceEquals(this, o)) return true;
            return o is not null && Identity.Equals(o.Identity) && Config == o.Config
                && Actions.Equals(o.Actions) && Slots.Equals(o.Slots)
                // ── INSIGHTS SHEET (additive) ── PRESENCE, like every slot: the toggle appears when the page's facts
                //    arrive and goes when they do not, and the hero must re-render for exactly that (its open STATE is
                //    a signal the button binds, so opening the sheet still costs no render here).
                && (Insights is null) == (o.Insights is null) && InsightsLive == o.InsightsLive;
        }

        public override int GetHashCode() => HashCode.Combine(Identity, Config, Actions, Slots);
    }

    /// <summary>The pieces the table builds for the vertical arm, handed to <see cref="Hero"/>.</summary>
    public readonly record struct HeroParts(
        Element? Toolbar,                         // the command bar host under the identity (null ⇒ a reserved blank row)
        Element BandActions,                      // Find · Filter · Play (plateless words)
        Element? SelectionBar,                    // the band's selection arm
        IReadSignal<bool> SelectionVisible,
        IReadSignal<bool> CompactInteractive,     // the onStuck input handoff (ch 03 §0.18)
        Element? SearchField,                     // takes the band title's slot while expanded
        IReadSignal<bool> SearchExpanded,
        float ColumnWidth,                        // measured (or seeded) list width — bucketed ONCE inside (BucketW)
        float CompactLeft,                        // gutter: RowMetrics.PadXFor(tier) (+48 chips, +36 lens)
        float HeroHeight,                         // the band height the table's Collapse bind uses (measured, else HeroBandHeight)
        Action<float>? OnHeroMeasured);

    // ══ 2. THE PRESENCE FLAGS — one set for the hero, its skeleton and its reserved height ═══════════════════════════

    readonly record struct HeroFlags(bool Eyebrow, bool Attribution, bool Meta, bool Pulse, bool Chart, bool Description);

    /// <summary>The hero's emit predicates for THIS spec — exactly what <see cref="HeroHost"/> branches on, so the reserved
    /// band holds the blocks the hero will compose (an album's eyebrow, a playlist's owner row, a chart caption, W28).</summary>
    static HeroFlags FlagsOf(VerticalSpec spec)
    {
        var id = spec.Identity;
        var slots = spec.Slots;
        return new HeroFlags(
            Eyebrow: id.Eyebrow.Length > 0,
            Attribution: (slots.Attribution is not null && id.Kind != DetailKind.Album)
                || (id.Collaborators is { Count: > 0 } c && Text.ShowCollaborators(c.Count, id.Collaborative))
                || id.OwnerName is { Length: > 0 }
                || id.Artists is { Count: > 0 },
            Meta: id.MetaLoading || id.Meta is { Length: > 0 },
            Pulse: id.DaylistExpiresAtMs > 0 && slots.Pulse is not null,
            Chart: id.ChartNewEntries > 0,
            Description: (id.EditableMetadata && slots.Description is not null) || id.DescriptionHtml is { Length: > 0 });
    }

    /// <summary>The band height the skeleton reserves and the loaded hero's collapse binds assume before the first
    /// measure, at the bucketed width: the KNOWN title's plan — the very plan the hero draws — else the PESSIMISTIC
    /// null-title plan (a one-line title at the fluid cap).</summary>
    public static float HeroBandHeight(VerticalSpec spec, float columnWidth)
    {
        // The slim head's band is a function of the PRESENTED nav style alone (VerticalLayout.SlimHeadHeight), never of the
        // width or the data.
        if (spec.Config.SlimHead) return VerticalLayout.SlimHeadHeight(Shell.Ui.PresentedNavStyle.Peek() == ShellNavStyle.Zune);
        float w = columnWidth > 0f ? columnWidth : VerticalLayout.FallbackW;
        bool rowFlow = VerticalLayout.RowFlow(w);
        float bw = VerticalLayout.BucketW(w);
        var f = FlagsOf(spec);
        return VerticalLayout.HeroBandHeight(bw, rowFlow, f.Eyebrow, f.Attribution, f.Meta, f.Description,
                                             pulse: f.Pulse, chart: f.Chart, title: spec.Identity.Title,
                                             actionLines: VerticalLayout.ActionRowLinesFor(bw, rowFlow, Skeleton.HasLabelledShuffle(spec.Identity.Kind)));
    }

    // ══ 3. THE HERO ══════════════════════════════════════════════════════════════════════════════════════════════════

    static readonly LayoutTransition HeroGeometryMotion = new(
        TransitionChannels.Bounds, TransitionDynamics.Tween(280f, Easing.SmoothOut), SizeMode.ScaleCorrect);

    static readonly LayoutTransition HeroReflowMotion = new(
        TransitionChannels.Position | TransitionChannels.Size, TransitionDynamics.Tween(280f, Easing.SmoothOut),
        SizeMode.Reveal);

    /// <summary>The vertical arm's item 0: the expanded presentation (artwork · eyebrow · title · rule · attribution ·
    /// meta · pulse · chart · actions · description, then the toolbar) and the 56-DIP context band, scroll-bound. The
    /// hero owns its flow hysteresis, so it is a small component under a plain wrapper box.</summary>
    public static Element Hero(VerticalSpec spec, in HeroParts parts)
        => new BoxEl
        {
            Key = "vhero:root", Direction = 1,
            Children = [Embed.Comp(new HeroProps(spec, parts), static () => new HeroHost())],
        };

    /// <summary>The hero's props. The spec gates on data; the table's pieces gate on geometry and on element/signal
    /// REFERENCE (chrome is not data — a rebuilt toolbar re-renders the hero, never the list).</summary>
    sealed record HeroProps(VerticalSpec Spec, HeroParts Parts)
    {
        public bool Equals(HeroProps? o)
        {
            if (ReferenceEquals(this, o)) return true;
            if (o is null || !Spec.Equals(o.Spec)) return false;
            var a = Parts;
            var b = o.Parts;
            return a.ColumnWidth == b.ColumnWidth && a.CompactLeft == b.CompactLeft && a.HeroHeight == b.HeroHeight
                && ReferenceEquals(a.Toolbar, b.Toolbar) && ReferenceEquals(a.BandActions, b.BandActions)
                && ReferenceEquals(a.SelectionBar, b.SelectionBar) && ReferenceEquals(a.SearchField, b.SearchField)
                && ReferenceEquals(a.SelectionVisible, b.SelectionVisible)
                && ReferenceEquals(a.CompactInteractive, b.CompactInteractive)
                && ReferenceEquals(a.SearchExpanded, b.SearchExpanded)
                && (a.OnHeroMeasured is null) == (b.OnHeroMeasured is null);
        }

        public override int GetHashCode() => HashCode.Combine(Spec, Parts.ColumnWidth, Parts.HeroHeight);
    }

    sealed class HeroHost : Component, IPropsHost
    {
        HeroProps? _latest;
        readonly Signal<HeroProps?> _props = new(null);
        readonly Action<RectF> _measure;

        // hysteresis state — plain fields, written in render (not signals: nothing re-renders off them). The TITLE keeps
        // none: its plan is a pure function of (bucketed width, flow, title), so it cannot disagree with its own lines.
        bool _rowFlow, _flowInit, _boundsSeen;
        float _lastMeasuredH;

        public HeroHost() => _measure = Measure;

        public void ApplyProps(object props)
        {
            _latest = (HeroProps)props;
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

            float availW = parts.ColumnWidth > 0f ? parts.ColumnWidth : VerticalLayout.FallbackW;
            Element expanded;
            if (cfg.SlimHead)
            {
                // Liked Songs: the slim head - title (outside Zune), meta, ONE bar. No artwork, rule, description or flow.
                expanded = SlimExpanded(id, parts, Shell.Ui.PresentedNavStyle.Value == ShellNavStyle.Zune, _measure);
            }
            else
            {
                // ── geometry: the flow off the raw width (hysteretic), everything else off ONE bucketed width ──
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
                var f = FlagsOf(spec);

                // The action row's LINES at this width: the one rule the skeleton and the pre-measure band read too, so the
                // title budget, the identity gap and the reserved height all charge the row the hero actually draws.
                int actionLines = VerticalLayout.ActionRowLinesFor(bw, rowFlow, acts.HasPlaySplit);

                // ── the title TYPE PLAN: ONE size per (bucketed width, title), drawn exactly (no auto-fit, no hysteresis) ──
                var plan = VerticalLayout.TitleTypeFor(bw, rowFlow, id.Title,
                    eyebrow: f.Eyebrow, attribution: f.Attribution, meta: f.Meta, pulse: f.Pulse, chart: f.Chart,
                    actionLines: actionLines);

                // ── the identity column, block for block (each keyed; late rows fade up, every row FLIPs) ──
                var blocks = new List<Element>(10);
                if (f.Eyebrow) blocks.Add(Block("hero-eyebrow", EyebrowRun(id.Eyebrow), late: true));
                blocks.Add(Block("hero-title", slots.Title is { } editTitle
                    ? editTitle(plan.Size, float.NaN)
                    : Design.Type.DetailHero(id.Title) with
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
                blocks.Add(Block("hero-actions", HeroActions(spec, id, cfg, acts, VerticalLayout.LabelledShuffleFits(bw, rowFlow))));

                // The description is no longer a row of the identity column: it is the padded box's second child, after
                // the whole [artwork, identity] row, in both flows (D49's band arithmetic already excludes it here).
                Element? description = null;
                if (id.EditableMetadata && slots.Description is { } editDescription)
                    description = editDescription(descW);
                else if (id.DescriptionHtml is { Length: > 0 } html)
                    // Clamped with the inline "… More" the engine shows only when the body overflows, and the whole text on
                    // hover while collapsed (fullTextTip).
                    description = Controls.ExpandableRichText(html, 13f, Tok.TextSecondary, accent, descW, descLines,
                                                              id.Subject.Text, s_navRoute, fullTextTip: true);

                float identityGap = VerticalLayout.IdentityGapFor(bw, rowFlow, plan,
                    f.Eyebrow, f.Attribution, f.Meta, pulse: f.Pulse, chart: f.Chart, actionLines: actionLines);

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

                expanded = new BoxEl
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
                            // ── INSIGHTS SHEET (additive) ── the toolbar row IS the table's command bar: the sheet's toggle
                            //    is one of its members (between the search and "…") when this page has facts
                            //    (Detail.Insights.cs §6). This is the PRE-STUCK half of the control: once the hero collapses
                            //    the pinned band's word (§7) is the reachable one.
                            Children = [parts.Toolbar ?? new BoxEl { Height = VerticalLayout.ToolbarRowHeight }],
                        },
                    ],
                };
            }

            // ── the STUCK BAND: typography only, NO fill (the offset model — the rows are clipped under it) ──
            float heroH = parts.HeroHeight > 1f ? parts.HeroHeight : HeroBandHeight(spec, availW);
            float cd = VerticalLayout.CollapseDistance(heroH, VerticalLayout.BandFloor);

            string? byline = Text.Byline(id.OwnerName, id.Meta, id.Eyebrow);
            Element identityBlock = new BoxEl
            {
                Direction = 0, MinWidth = 0f, Shrink = 1f, Gap = Spacing.S, AlignItems = FlexAlign.Center,
                Children = byline is { Length: > 0 } ? [BandTitle(id.Title), BandByline(byline)] : [BandTitle(id.Title)],
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
            Element normalBand = Band(availW, parts.CompactLeft, lead, parts.BandActions);

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
            //    under `presentation` below) is live exactly when it is false. Never both, never neither — stated as
            //    Detail.InsightsSheet.BandToggleTakesInput / HeroToggleTakesInput, pinned by DetailInsightsSheetTests.
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

    /// <summary>The SLIM head's expanded presentation (<see cref="Config.SlimHead"/>): the head-top strip, then
    /// <c>[title row · gap]</c> outside Zune, the meta line, the gap to the bar, the bar - exactly
    /// <see cref="VerticalLayout.SlimHeadHeight"/>. The title block is ALWAYS mounted at its route-static height (56, or 0
    /// under the presented Zune style), so the nav-style change animates its height (<see cref="PageHead.Reflow"/>, a real
    /// layout tween the bar and rows below ride) while the title fades in place; the meta line is reserved (its shimmer is
    /// the same 16), so data arriving later fills the slot instead of moving anything.</summary>
    static Element SlimExpanded(Identity id, in HeroParts parts, bool zune, Action<RectF> measure)
    {
        Element titleBlock = new BoxEl
        {
            Key = "slim:title", Direction = 1, Shrink = 0f, MinWidth = 0f, ClipToBounds = true, Animate = PageHead.Reflow,
            Height = zune ? 0f : PageGeometry.TitleLine + PageGeometry.TitleToMeta,
            Children = zune
                ? Array.Empty<Element>()
                : new Element[]
                {
                    PageHead.TitleRow(id.Title, null) with
                        { Key = "slim:title-row", Enter = PageHead.FadeIn, Exit = PageHead.FadeOut, Transition = PageHead.FadeMotion },
                    Spacer(PageGeometry.TitleToMeta),
                },
        };
        Element meta = (id.MetaLoading || id.Meta is not { Length: > 0 }
            ? new BoxEl
            {
                Direction = 0, Height = PageGeometry.MetaLine, Shrink = 0f, MinWidth = 0f, ClipToBounds = true,
                Children = [MetaLine(id, width: float.NaN, maxWidth: float.NaN, maxLines: 1)],
            }
            : PageHead.MetaSlot(id.Meta)) with { Key = "slim:meta" };
        return new BoxEl
        {
            Direction = 1, Animate = HeroReflowMotion, OnBoundsChanged = measure,
            Padding = new Edges4(parts.CompactLeft, PageGeometry.HeadTop, parts.CompactLeft, VerticalLayout.ExpandedToolbarBottomPad),
            Children =
            [
                titleBlock,
                meta,
                Spacer(PageGeometry.HeadToViews),
                parts.Toolbar ?? new BoxEl { Height = VerticalLayout.ToolbarRowHeight },
            ],
        };
    }

    /// <summary>A keyed identity block: a position-only FLIP always, a fade-up entrance when it can arrive late.</summary>
    static BoxEl Block(string key, Element child, bool late = false) => new()
    {
        Key = key, Direction = 1, Layout = Shove, Enter = late ? FadeUp : (EnterExit?)null, Children = [child],
    };

    /// <summary>The ONE action row. A page that supplies the Play split's verbs (an album, a playlist) gets the SPLIT, then
    /// the LABELLED Shuffle (this arm has the width the rail lacks), the heart, Share and ⋯: Play split · Shuffle · heart ·
    /// share · ⋯, the Shuffle's label dropped (<paramref name="labelShuffle"/> false, the icon-only form of the same button)
    /// when the identity column cannot hold the labelled row on one line. Every other kind keeps W27's row: Play 32 + the 32-DIP satellites — Shuffle (when the page offers it) ·
    /// the heart (DROPPED, not disabled, when <c>Heart == None</c>) · Share · More.</summary>
    static Element HeroActions(VerticalSpec spec, Identity id, in Config cfg, FrameActions acts, bool labelShuffle)
    {
        bool split = acts.HasPlaySplit;
        Func<ColorF> accent = spec.Accent;
        var actions = new List<Element>(5)
        {
            split
                ? Controls.PlaySplitButton(id.Subject.Text, accent, spec.PlayAll ?? DefaultPlay(id.Subject),
                                           acts.AddToQueue!, acts.PlayNext!, acts.StartRadio!) with { Key = "vhero-play:" + id.Subject.Text }
                : PlayButton(accent, spec.PlayAll ?? DefaultPlay(id.Subject)) with { Key = "vhero-play" },
        };
        if (acts.Shuffle is { } shuffle)
            actions.Add(split
                ? Controls.SecondaryButton(Icons.Shuffle, Loc.Get(Strings.Detail.Shuffle), shuffle, labelShuffle) with { Key = "vhero-shuffle" }
                : Satellite(Icons.Shuffle, Loc.Get(Strings.Detail.Shuffle), shuffle) with { Key = "vhero-shuffle" });
        if (cfg.Heart != HeartMode.None)
        {
            // Keyed on the TARGET: SaveButton's uri freezes at mount. The heart reads the page accent through
            // Design.AccentCtx, which the frame host provides.
            string saveUri = SaveTargetOf(id).Text;
            string saveName = id.Title;
            actions.Add(Embed.Comp(() => new Controls.SaveButton
                { Uri = saveUri, Name = saveName, Glyph = 16f, Box = Controls.IconButtonSize })
                with { Key = "vhero-save:" + saveUri });
        }
        if (ShareActionFor(id) is { } share)
            actions.Add(split
                ? Controls.QuietIconButton(Icons.Share, Loc.Get(Strings.Menu.Share), share) with { Key = "vhero-share" }
                : Satellite(Icons.Share, Loc.Get(Strings.Menu.Share), share) with { Key = "vhero-share" });
        if (acts.More is { } more)
            actions.Add(MoreButton(more, Controls.IconButtonSize, 16f, round: false) with { Key = "vhero-more:" + id.Subject.Text });
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
                Size = 12f, LineHeight = 16f, Weight = 600, Color = Tok.TextSecondary,
                MaxWidth = contentW, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
            };
            // The owner opens their profile (#161) — a text link (accent on hover), inert when the owner has no uri.
            var profile = ProfileRoute.For(id.Owner, owner);
            if (profile.IsNone) return ownerText;
            return new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, MinWidth = 0f, MaxWidth = contentW,
                Role = AutomationRole.Hyperlink, Focusable = true, Cursor = CursorId.Hand, OnClick = () => Shell.GoTo(profile),
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
            Size = 12f, LineHeight = 16f, Color = Tok.TextSecondary,
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
    public static Element HeroSkeleton(VerticalSpec spec, float columnWidth, float compactLeft)
    {
        if (spec.Config.SlimHead) return SlimHeroSkeleton(compactLeft);
        float w = columnWidth > 0f ? columnWidth : VerticalLayout.FallbackW;
        bool rowFlow = VerticalLayout.RowFlow(w);
        float bw = VerticalLayout.BucketW(w);
        var f = FlagsOf(spec);
        var id = spec.Identity;
        float pad = VerticalLayout.HeroPadFor(bw, rowFlow);
        float gap = VerticalLayout.HeroGapFor(bw, rowFlow);
        float art = VerticalLayout.ArtworkFor(bw, rowFlow);
        float contentW = VerticalLayout.ContentWidthFor(bw, rowFlow);
        float descW = VerticalLayout.DescriptionWidthFor(bw, rowFlow);
        // The KNOWN title's plan (the loaded hero's own, so the swap does not jump), else the PESSIMISTIC null-title plan
        // (an empty Title is null to the estimator) — the one the pre-measure collapse height builds too.
        bool split = Skeleton.HasLabelledShuffle(id.Kind);
        int actionLines = VerticalLayout.ActionRowLinesFor(bw, rowFlow, split);
        var plan = VerticalLayout.TitleTypeFor(bw, rowFlow, id.Title,
            eyebrow: f.Eyebrow, attribution: f.Attribution, meta: f.Meta, pulse: f.Pulse, chart: f.Chart,
            actionLines: actionLines);
        float identityGap = VerticalLayout.IdentityGapFor(bw, rowFlow, plan,
            f.Eyebrow, f.Attribution, f.Meta, pulse: f.Pulse, chart: f.Chart, actionLines: actionLines);

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
        blocks.Add(SkeletonActionRow(split, VerticalLayout.LabelledShuffleFits(bw, rowFlow)));

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
                f.Eyebrow, f.Attribution, f.Meta, f.Description, pulse: f.Pulse, chart: f.Chart, actionLines: actionLines),
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

    /// <summary>The slim head's reserved band: the title bar (outside Zune), the meta bar and the bar's pills, at the heights
    /// <see cref="VerticalLayout.SlimHeadHeight"/> sums - so the swap to the loaded head moves nothing.</summary>
    static Element SlimHeroSkeleton(float compactLeft)
    {
        bool zune = Shell.Ui.PresentedNavStyle.Peek() == ShellNavStyle.Zune;
        var kids = new List<Element>(5);
        if (!zune)
        {
            kids.Add(new BoxEl
            {
                Direction = 0, Height = PageGeometry.TitleLine, Shrink = 0f, AlignItems = FlexAlign.Center,
                Children = [Bar(200f, 32f)],
            });
            kids.Add(Spacer(PageGeometry.TitleToMeta));
        }
        kids.Add(new BoxEl
        {
            Direction = 0, Height = PageGeometry.MetaLine, Shrink = 0f, AlignItems = FlexAlign.Center,
            Children = [Bar(150f, 12f)],
        });
        kids.Add(Spacer(PageGeometry.HeadToViews));
        kids.Add(new BoxEl
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
                        ToolbarPill(ButtonRules.PlaySplitWidthNominal), ToolbarPill(Skeleton.ShuffleButtonWidth),
                        new BoxEl { Grow = 1f, Height = 1f },
                        ToolbarPill(Track.CommandBarLayout.SearchIconWidth),
                    ],
                },
            ],
        });
        return new BoxEl
        {
            Key = "detail-skeleton:hero", Direction = 1,
            MinHeight = VerticalLayout.SlimHeadHeight(zune),
            Padding = new Edges4(compactLeft, PageGeometry.HeadTop, compactLeft, VerticalLayout.ExpandedToolbarBottomPad),
            Children = kids.ToArray(),
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

    /// <summary>The loaded row's shape, member for member, wrapping against the same identity column the same way:
    /// [Play split 121] · [Shuffle, labelled 96 or icon-only 32 by the SAME width rule as the loaded hero] · heart · share · ⋯
    /// for the kinds that carry the split (<see cref="Skeleton.HasLabelledShuffle"/>), else [Play 120] and the 32-DIP
    /// satellites. It wraps against the identity column (stretched, so the width is definite) exactly as the loaded row does.</summary>
    static Element SkeletonActionRow(bool split, bool labelledShuffle)
    {
        var kids = new Element[1 + Skeleton.SatelliteCount];
        kids[0] = new BoxEl
        {
            Width = Skeleton.PrimaryWidthFor(plainPlay: !split), Height = Controls.ButtonHeight, Shrink = 0f,
            Corners = Radii.ControlAll,
        };
        int next = 1;
        if (split)
            kids[next++] = new BoxEl
            {
                Width = labelledShuffle ? Skeleton.ShuffleButtonWidth : Controls.IconButtonSize,
                Height = Controls.ButtonHeight, Shrink = 0f, Corners = Radii.ControlAll,
            };
        for (int i = next; i < kids.Length; i++)
            kids[i] = new BoxEl
            {
                Width = Controls.IconButtonSize, Height = Controls.IconButtonSize, Shrink = 0f, Corners = Radii.ControlAll,
            };
        return new BoxEl
        {
            Direction = 0, Gap = VerticalLayout.ActionRowGap, Wrap = true, AlignItems = FlexAlign.Center, Justify = FlexJustify.Start,
            Margin = new Edges4(0f, Spacing.XS, 0f, 0f),
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

    // ══ 5. THE BAND HELPERS (the detail band and the artist band are ONE band) ═══════════════════════════════════════
    //
    // ONE BASELINE. The title (600, primary), a subtle 1x16 divider, the tabs (400, ink plus the shared accent underline at one
    // fixed y, never a weight change) and the trailing text actions (Controls.TextAction) each sit in a slot of BandLayout.ItemHeight
    // (32) and centre the band's ONE 20-DIP line (BandLayout.TextLine), so their baselines are the same line. BandCluster builds
    // the row; the Zune band's row 2 (Sidebar.UI.Zune.cs) calls the SAME builder at rowH = 32, so the in-page band and row 2 share
    // text and underline geometry. The pivot used to be a text-plus-underline column centred in 32, which put its word 3 DIP above
    // the title's. Under Zune the artist, profile, episode and show pages publish this band into row 2 and do not compose it
    // (the floor placement is Detail.BandLayout.FloorLatch).

    /// <summary>The identity row: 56 tall, gutter-padded, UNPAINTED (no fill, no edge — the caller places the one
    /// hairline), still hit-testable so its actions work and it swallows wheel/click meant for the rows behind it. A
    /// <see cref="BandCluster"/> of the identity and the actions.</summary>
    public static Element Band(float width, float gutter, Element identity, Element actions)
        => Band(width, gutter, BandCluster(identity, null, actions, BandLayout.Height));

    /// <summary>The artist arm's row (0.2.9 ContextBand.Row): the cluster's children (<see cref="BandCluster"/>), same geometry as
    /// the detail band. No flex gap: the cluster carries its own spacers.</summary>
    public static BoxEl Band(float width, float gutter, Element[] children) => new BoxEl
    {
        Direction = 0, Width = width, Height = BandLayout.Height, Padding = new Edges4(gutter, 0f, gutter, 0f),
        AlignItems = FlexAlign.Center, HitTestVisible = true, Children = children,
    };

    /// <summary>One group's slot: a row of <c>min(ItemHeight, rowH)</c>, its child centred, so the title, the tabs and the
    /// actions all centre their 20-DIP line in the same box. <paramref name="shrink"/> is 1 only for the title lane.</summary>
    public static Element BandSlot(Element child, float rowH, float shrink = 0f) => new BoxEl
    {
        Direction = 0, Height = MathF.Min(BandLayout.ItemHeight, rowH), AlignItems = FlexAlign.Center,
        Shrink = shrink, MinWidth = 0f, Children = [child],
    };

    /// <summary>The 1 x 16 divider between the title and the tabs.</summary>
    public static Element BandDivider(Prop<ColorF>? ink = null) => new BoxEl
    {
        Width = 1f, Height = BandLayout.DividerH, Shrink = 0f, Fill = ink ?? Tok.StrokeDividerDefault, HitTestVisible = false,
    };

    /// <summary>A band action: the shared <see cref="Controls.TextAction"/> at the band's item height and waist.
    /// <paramref name="ink"/> (resolved by a band that sits over the artist bleed) binds the plain word's ink; null = today's tokens.</summary>
    public static BoxEl BandAction(string label, Action? onClick, bool primary = false, Controls.TextActionInk? ink = null)
        => Controls.TextAction(label, onClick, primary, height: BandLayout.ItemHeight, padX: BandLayout.ActionPadX, ink: ink);

    /// <summary>The band's cluster, ONE builder for the in-page band (<paramref name="rowH"/> = 56) and the Zune band's row 2
    /// (32): [title slot] · [divider · tabs, elastic] · [actions slot]. The title and the actions never drop; the tabs are
    /// the only elastic lane. With no tabs a grow spacer carries the actions to the end. The row has no flex gap: the divider
    /// air and the minimum title-to-actions gap are explicit spacers.</summary>
    /// <param name="dividerInk">The divider's fill when the cluster sits over the artist bleed (row 2): a bound ink the Zune band resolves
    /// (the words carry their own). Null = today's divider.</param>
    public static Element[] BandCluster(Element title, Element? pivots, Element? actions, float rowH, Prop<ColorF>? dividerInk = null)
    {
        var kids = new List<Element>(7) { BandSlot(title, rowH, shrink: 1f) };
        if (pivots is not null)
        {
            kids.Add(Spacer(BandLayout.DividerGap));
            kids.Add(BandDivider(dividerInk));
            kids.Add(Spacer(BandLayout.DividerGap));
            kids.Add(new BoxEl
            {
                Direction = 0, Grow = 1f, Basis = 0f, MinWidth = 0f, Height = rowH, AlignItems = FlexAlign.Center,
                Children = [pivots],
            });
        }
        else
        {
            kids.Add(new BoxEl { Grow = 1f, Basis = 0f, MinWidth = 0f, Height = 1f, HitTestVisible = false });
        }
        if (actions is not null)
        {
            kids.Add(Spacer(BandLayout.ClusterGap));
            kids.Add(BandSlot(actions, rowH));
        }
        return [.. kids];
    }

    /// <summary>The band title: BodyStrong 14/600 on the band's 20-DIP line, primary, one line, ellipsised — never wraps,
    /// never drops.</summary>
    public static Element BandTitle(string title) => Ui.BodyStrong(title) with
    {
        LineHeight = BandLayout.TextLine, Weight = 600, Color = Tok.TextPrimary,
        MinWidth = 0f, MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis,
    };

    /// <summary>The band byline: Caption 12 on the same 20-DIP line as the title, tertiary, one line — context for the title,
    /// not a competing label. It joins the title's line (<c>[title, S, byline]</c>) and ellipsises first.</summary>
    public static Element BandByline(string byline, Func<ColorF>? ink = null) => new BoxEl
    {
        Direction = 0, MinWidth = 0f, Shrink = 1f, AlignItems = FlexAlign.Center,
        Children =
        [
            Ui.Caption(byline) with
            {
                LineHeight = BandLayout.TextLine, Color = ink is null ? Tok.TextTertiary : Prop.Of(ink),   // ink: see Shell.Ui.ChromeInkMix
                MinWidth = 0f, MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis,
            },
        ],
    };

    /// <summary>The band's ONE lower edge, as a real laid-out row child — placed on the LAST stuck stratum only.</summary>
    public static Element BandHairline() => new BoxEl
    {
        Height = BandLayout.HairlineHeight, AlignSelf = FlexAlign.Stretch,
        Fill = Tok.StrokeDividerDefault, HitTestVisible = false,
    };

    /// <summary>The band's middle cluster (artist arm): the page's sections as text links, a 2-DIP accent underline under
    /// the active one — ALWAYS mounted, switching colour over 167 ms. <paramref name="active"/> is the caller's scroll-spy
    /// answer; the section set is re-pushed (it grows after mount), the signal instance freezes at mount.</summary>
    public static Element Pivot(IReadOnlyList<(string Label, Action OnClick)> sections, IReadSignal<int> active,
                                Func<ColorF> accent, bool enabled = true)
        => Embed.Comp(new PivotProps(sections, active, accent, enabled), static () => new PivotHost());

    /// <summary><paramref name="Enabled"/> false takes the tabs out of the keyboard order (a disabled node is not collected as a
    /// focus stop) while they stay painted: the episode's row keeps them visible as it eases away under Zune.</summary>
    sealed record PivotProps(IReadOnlyList<(string Label, Action OnClick)> Sections, IReadSignal<int> Active, Func<ColorF> Accent,
                             bool Enabled = true)
    {
        public bool Equals(PivotProps? o)
        {
            if (ReferenceEquals(this, o)) return true;
            if (o is null || !ReferenceEquals(Active, o.Active) || Enabled != o.Enabled || Sections.Count != o.Sections.Count) return false;
            for (int i = 0; i < Sections.Count; i++)
                if (!string.Equals(Sections[i].Label, o.Sections[i].Label, StringComparison.Ordinal)) return false;
            return true;
        }

        public override int GetHashCode() => HashCode.Combine(Sections.Count, Active);
    }

    sealed class PivotHost : Component, IPropsHost
    {
        /// <summary>A pivot is a glance-and-aim affordance; past this many words it is a menu.</summary>
        const int MaxItems = 16;

        PivotProps? _latest;
        readonly Signal<PivotProps?> _props = new(null);
        NodeHandle[] _tabNodes = [];
        Action<NodeHandle>[] _tabRealized = [];
        Action[] _tabClicks = [];
        Func<ColorF>[] _tabFills = [];
        NodeHandle _viewport;
        bool _seeded;
        int _revealIndex = -1;
        readonly Action<NodeHandle> _captureViewport;
        readonly Action _reveal;

        public PivotHost()
        {
            _captureViewport = h => _viewport = h;
            _reveal = RevealActive;
        }

        public void ApplyProps(object props)
        {
            _latest = (PivotProps)props;
            _props.Value = _latest;
        }

        public override Element Render()
        {
            _ = _props.Value;
            var p = _latest!;
            int shown = Math.Min(p.Sections.Count, MaxItems);
            EnsureSlots(shown);
            int active = p.Active.Value;                           // re-renders only on a boundary crossing
            int current = BandLayout.PivotCurrent(active, shown);
            _revealIndex = current;
            UseLayoutEffect(_reveal, DepKey.From(current, shown));
            if (shown == 0) return new BoxEl { Width = 0f, Height = 0f, HitTestVisible = false };

            var kids = new Element[shown];
            for (int i = 0; i < shown; i++)
                kids[i] = PivotLink(p.Sections[i].Label, i == current, p.Enabled, _tabFills[i], _tabClicks[i], _tabRealized[i]);
            return new ScrollEl
            {
                Horizontal = true, ContentSized = true,
                Grow = 1f, Basis = 0f, MinWidth = 0f, Height = BandLayout.Height,
                SuppressScrollBar = true, AutoEdgeFade = true, EdgeCues = ScrollEdgeCues.None,
                OnRealized = _captureViewport,
                Content = new BoxEl
                {
                    Direction = 0, Gap = BandLayout.PivotGap, Shrink = 0f, AlignItems = FlexAlign.Center,
                    Children = kids,
                },
            };
        }

        void EnsureSlots(int count)
        {
            if (_tabNodes.Length == count) return;
            int old = _tabNodes.Length;
            Array.Resize(ref _tabNodes, count);
            Array.Resize(ref _tabRealized, count);
            Array.Resize(ref _tabClicks, count);
            Array.Resize(ref _tabFills, count);
            for (int i = old; i < count; i++)
            {
                int index = i;
                _tabRealized[i] = h => _tabNodes[index] = h;
                _tabClicks[i] = () =>
                {
                    if (_latest is { } live && (uint)index < (uint)live.Sections.Count) live.Sections[index].OnClick();
                };
                // The underline's bound fill: the accent while this link is active, else transparent. Reads the active
                // signal and the accent inside the bind, so both changes cross-fade without a re-render.
                _tabFills[i] = () =>
                {
                    if (_latest is not { } live) return ColorF.Transparent;
                    int shown = Math.Min(live.Sections.Count, MaxItems);
                    int current = BandLayout.PivotCurrent(live.Active.Value, shown);
                    return index == current ? live.Accent() : ColorF.Transparent;
                };
            }
            _seeded = false;
        }

        void RevealActive()
        {
            int i = _revealIndex;
            if ((uint)i >= (uint)_tabNodes.Length || _tabNodes[i].IsNull || _viewport.IsNull || Context.Scene is not { } scene) return;
            scene.BringIntoView(_viewport, _tabNodes[i], float.NaN, _seeded && !Design.Reduced ? ScrollMove.Glide : ScrollMove.Immediate, Spacing.S);
            _seeded = true;
        }

        /// <summary>One link: the hover boundary is the LINK's own box, so the word under the pointer lights alone. A ZSTACK of
        /// <see cref="BandLayout.ItemHeight"/>: the word centred on the band's 20-DIP line (the same line as the title and
        /// the actions), the active mark an OVERLAY at <see cref="BandLayout.UnderlineY"/>. The weight is a CONSTANT 400 in both
        /// states (active = primary ink plus the mark), so a tab never changes width while the scroll spy moves and a title
        /// (600) never looks like a tab.</summary>
        static Element PivotLink(string label, bool isActive, bool enabled, Func<ColorF> fill, Action go, Action<NodeHandle> realized) => new BoxEl
        {
            ZStack = true, Shrink = 0f,
            Height = BandLayout.ItemHeight,
            Padding = new Edges4(BandLayout.PivotPadX, 0f, BandLayout.PivotPadX, 0f),
            Corners = Radii.ControlAll,
            Role = AutomationRole.Tab, Focusable = true, IsEnabled = enabled, Cursor = CursorId.Hand, OnClick = go, OnRealized = realized,
            Children =
            [
                new BoxEl
                {
                    Direction = 1, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, HitTestVisible = false,
                    Children =
                    [
                        new TextEl(label)
                        {
                            Size = Controls.TextActionSize, LineHeight = BandLayout.TextLine, Weight = PivotWeight,
                            Color = isActive ? Tok.TextPrimary : Tok.TextSecondary,
                            HoverColor = Tok.TextPrimary,
                            MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis,
                        },
                    ],
                },
                new BoxEl
                {
                    Height = BandLayout.UnderlineHeight, AlignSelf = FlexAlign.Stretch,
                    Margin = new Edges4(0f, BandLayout.UnderlineY, 0f, 0f),
                    Fill = fill,
                    BrushTransitionMs = AccentTransitionMs,
                    HitTestVisible = false,
                },
            ],
        };

        /// <summary>A tab's weight, constant in both states (see <see cref="PivotLink"/>).</summary>
        const ushort PivotWeight = 400;
    }
}
