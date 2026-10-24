// ── Themes/Classic/Structure/Views/Detail/ClassicDetailHero.cs — Classic's vertical hero and its loading band (themes plan 6.1, T9a) ──────────────
//
// From `Entities\Detail.UI.Hero.cs`: THE VERTICAL HERO SYSTEM (ch 03 §0.3, §0.6-§0.8, §0.14-§0.18; W6-W9, W12-W14, W27-W28) — the expanded
// presentation and the 56-DIP context band with its scroll binds and input handoff (`HeroHost`), the hero's actions and attribution, and
// `HeroIdentitySkeleton`, the identity's own loading state (the hero shimmers in place; the list has no loading band, audit B7 r2).
//
// WHO CALLS WHAT. The TABLE (`Track.Table.cs`) owns the vertical list: it calls `Detail.Hero(spec, parts)` for item 0 (a plain box that the table
// pins with `Sticky(0)` and collapses with `Collapse`, around the `Detail.Hero` slot this file's `HeroTemplate` draws) and reads the hero's style
// from `StyleKeys.DetailHero` (`HeroStyle`: the list insets beside the rail and their motion; no loading band, the identity shimmers in place). This file never scrolls anything: the expanded presentation binds TransY/Opacity to the NEAREST scroller, the band reveals over the
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
using RailBadgeRow = Wavee.Detail.RailBadgeRow;
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

    /// <summary>The hero's loading band, the list's inset beside the columns form's rail and its ease, as the style the table reads
    /// (<see cref="StyleKeys.DetailHero"/>).</summary>
    public static readonly DetailHeroStyle HeroStyle = new() { ListInsets = ListInsets, InsetMotion = ClassicTierMotion.SizeReflow };

    /// <summary>The registered template of <see cref="ViewKeys.DetailHero"/>: the table's item 0 at every width (audit B9), ONE tree for both of the page's
    /// forms. The HERO form (the narrow page): the expanded presentation (artwork · eyebrow · title · rule · attribution · meta · pulse · chart · actions ·
    /// description, then the toolbar) and the 56-DIP context band, scroll-bound. The COLUMNS form: the rail (the same cover, eyebrow, title, meta and actions
    /// over the rail's own rows, in its own scroller) hanging below the list bar beside the rows, or the collapsed strip in its place; a page with no rail
    /// (Liked Songs) shows its head over the bar. The hero owns its flow hysteresis, so it is a small component; the props gate it on data (the spec) and on
    /// the reference of the table's pieces.</summary>
    public static Element HeroTemplate(DetailHeroProps p, in ViewContext ctx)
        => Embed.Comp(p, static () => new HeroHost());

    // ══ 1a. THE LIST'S INSET (the columns form's rail) ═════════════════════════════════════════════════════════════════════

    /// <summary>What the columns form's rail takes beside the rows: the expanded rail, or the collapsed strip and its grip's slot; a page with no rail keeps
    /// its list the head's inset inside the page gutter on both sides (its plates start 8 before the head's words, tuned Classic D16). The hero form has
    /// none. The aside is the frame's (beside the table), so the list's trail is the frame's too.</summary>
    static DetailListInsets ListInsets(DetailInsetQuery q)
    {
        if (q.Form != DetailForm.Columns) return default;
        if (q.Columns?.TwoColumn is { } tc) return new DetailListInsets(RailLead(tc), 0f);
        if (q.Columns?.Head is not null)
        {
            float inset = ClassicDetailLayout.HeadListInset(ClassicTiers.PageOf(q.Tiers).Gutter);
            return new DetailListInsets(inset, inset);
        }
        return default;
    }

    /// <summary>The rail's whole slot beside the rows: the expanded rail's live width, or the collapsed strip with its grip's slot.</summary>
    internal static float RailLead(DetailTwoColumnProps tc) => tc.Collapsed ? tc.CompactWidth + tc.CollapsedGripWidth : tc.RailWidth;

    /// <summary>The head's nominal height on a page with no rail (24 + 52 + 4 + 16 + 4): where the frame's insights sheet starts under it.</summary>
    internal static float HeadHeight => ClassicTiers.HeadTop + ClassicDetailLayout.HeadTitleLine + ClassicTiers.HeadMetaGap + ClassicDetailLayout.HeadMetaLine
                                       + ClassicDetailLayout.HeadPadBottom;

    // ══ 1b. THE HERO ═══════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The hero host: the one tree of both forms, and the hero form's stuck band.</summary>
    sealed class HeroHost : Component, IPropsHost
    {
        DetailHeroProps? _latest;
        readonly Signal<DetailHeroProps?> _props = new(null);
        readonly Action<RectF> _measure;
        readonly Action<RectF> _measureHead;

        // hysteresis state — plain fields, written in render (not signals: nothing re-renders off them). The TITLE keeps
        // none: its plan is a pure function of (bucketed width, flow, title), so it cannot disagree with its own lines.
        bool _rowFlow, _flowInit, _boundsSeen;
        float _lastMeasuredH;

        public HeroHost()
        {
            _measure = Measure;
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
            if (r.H <= 1f) return;
            _boundsSeen = true;
            if (_latest?.Parts.Form == DetailForm.Columns) return;
            Report(r.H);
        }

        /// <summary>The columns form's measure: the list head's foot in the body, the PINNED head the chrome stands under (the rail hangs below it). It lands
        /// at its final place at once, while the body around it eases, so the chrome and the rows never wait on the ease.</summary>
        void MeasureHead(RectF r)
        {
            if (_latest?.Parts.Form != DetailForm.Columns) return;
            Report(r.Y + r.H);
        }

        void Report(float h)
        {
            if (h <= 1f) return;
            // Reported again whenever the table's own value disagrees (it keeps one per form), not only on a change here.
            if (MathF.Abs(h - _lastMeasuredH) <= 1f && MathF.Abs(h - (_latest?.Parts.HeroHeight ?? 0f)) <= 1f) return;
            _lastMeasuredH = h;
            _latest?.Parts.OnHeroMeasured?.Invoke(h);             // the table republishes its collapse height (hero form) or its pinned line (columns form)
        }

        public override Element Render()
        {
            _ = _props.Value;
            // The tiers this hero is drawn in (its root's facts, the signal its table reads), never the process's last window.
            var tiers = global::Wavee.Theming.TierContext.DrawnIn(UseContext(global::Wavee.Theming.ThemeFactsContext.Signal)).Value.Tiers;
            // A docked aside holds About this release (C-Album-Huge, contract 1.21): the rail leaves its release row out.
            bool asideDocked = UseContext(Detail.AsideDocked);
            var seen = UseRef(false);                              // a swap piece fades in only on a re-arrangement, never with the page's own entrance
            var swapIn = ClassicTierMotion.SwapEnter(seen.Value);
            seen.Value = true;
            var p = _latest!;
            var spec = p.Spec;
            var parts = p.Parts;
            var id = spec.Identity;
            bool columns = parts.Form == DetailForm.Columns;
            var tc = parts.Columns?.TwoColumn;
            bool railForm = columns && tc is not null;
            bool head = columns && tc is null;

            bool compactCanHit = parts.CompactInteractive.Value && !columns;   // subscribe: the stuck edge moves input ownership (the hero form's)

            // ── the hero form's flow off the raw width (hysteretic) ──
            float availW = parts.ColumnWidth > 0f ? parts.ColumnWidth : VerticalLayout.FallbackW;
            _rowFlow = VerticalLayout.RowFlow(availW, _rowFlow, _flowInit);
            if (parts.ColumnWidth > 0f) _flowInit = true;

            // ── the body: [the identity slot | the list head], a row at the columns form (the rail hangs beside the bar), stacked otherwise ──
            float gutter = ClassicTiers.PageOf(tiers).Gutter;
            Element identity = Identity(p, tiers, columns, asideDocked, swapIn, _rowFlow, _boundsSeen, gutter);
            Element listHead = new BoxEl
            {
                // Laid out at its final place and size from the commit (the slot beside it lands at once), so the list bar inside fits once; its picture
                // slides and reveals there.
                Key = "hero:list-head", Direction = 1, Grow = railForm ? 1f : 0f, Shrink = 0f, Basis = railForm ? 0f : float.NaN, MinWidth = 0f,
                Layout = ClassicTierMotion.Reflow, OnBoundsChanged = _measureHead,
                Children = [ToolbarBlock(parts, columns, railForm, head, gutter)],
            };
            // Its height eases through real layout (the list head moves beside the rail or under the identity; a late row, a re-flow): the rows under the
            // hero ride it instead of jumping. Clipped while it eases in the hero form (the identity is revealed as it grows); the columns form's rail
            // hangs outside it on purpose and keeps painting.
            Element expanded = new BoxEl
            {
                Key = "hero:body", Direction = (byte)(railForm ? 0 : 1), AlignItems = railForm ? FlexAlign.Start : FlexAlign.Stretch, MinWidth = 0f,
                Animate = railForm ? ClassicTierMotion.HangingReflow : ClassicTierMotion.SizeReflow, OnBoundsChanged = _measure,
                Children = [identity, listHead],
            };

            // ── the STUCK BAND (the hero form's): typography only, NO fill (the offset model — the rows are clipped under it) ──
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
            // The band exists in both forms (one tree) and is drawn only by the hero form's collapse: hidden, inert and unbound at the columns form.
            BoxEl compact = new()
            {
                Key = "hero:stuck-band", ZStack = true, Width = availW, Height = BandLayout.Height,
                HitTestVisible = compactCanHit, HitTestPassThrough = true, Visible = !columns,
                Children = [bandContent],
            };
            // …and the scrolled-away hero stops eating clicks at the same edge.
            // It rides the scroll back out of the pinned hero (0 → −cd over the collapse) and fades over the tail of it.
            BoxEl presentation = ZStack(expanded) with { Direction = 1, HitTestVisible = !compactCanHit };
            if (!columns)
            {
                compact = compact.Reveal(VerticalLayout.CompactRevealStart(cd), cd - VerticalLayout.CompactRevealStart(cd), Design.Reduced ? 0f : Spacing.XS);
                presentation = presentation.Parallax(0.0, cd, 0f, -cd).Fade(VerticalLayout.ExpandedFadeStart(cd), cd, 1f, 0f);
            }
            // The band hangs from a 0-tall slot: it never floors the item's height (the columns form's head is the 52 list bar; a crossing to the hero form
            // eases the item from there instead of stepping to the band's 56 first).
            return ZStack(presentation, new BoxEl { Key = "hero:band-slot", Direction = 1, Height = 0f, MinWidth = 0f, Children = [compact] }) with { Direction = 1 };
        }

        /// <summary>The list bar's block: the hero form's (the rows' gutter either side, 8 over and 4 under the bar), the rail form's (8 over the bar, the
        /// rows' plate inset at its end: the two-column list bar's) or a head page's (the head's list inset either side). Spacers, so the bar glides
        /// between them.</summary>
        static Element ToolbarBlock(Detail.HeroParts parts, bool columns, bool railForm, bool head, float gutter)
        {
            float headInset = ClassicDetailLayout.HeadListInset(gutter);
            float lead = railForm ? 0f : head ? headInset : parts.CompactLeft;
            float trail = railForm ? Track.RowMetrics.RowInset : head ? headInset + Track.RowMetrics.RowInset : parts.CompactLeft;
            return new BoxEl
            {
                Key = "hero:toolbar", Direction = 1, MinWidth = 0f,
                Children =
                [
                    Air("hero:toolbar-top", VerticalLayout.ExpandedToolbarTopPad),
                    new BoxEl
                    {
                        Direction = 0, MinWidth = 0f,
                        Children =
                        [
                            Gutter("hero:toolbar-lead", lead),
                            // The table's list bar. On a facts page it carries the insights toggle itself (tuned Classic I1) — the PRE-STUCK half of the
                            // control: once the hero collapses the pinned band's word (§7) is the reachable one.
                            new BoxEl
                            {
                                Direction = 1, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f,
                                Children = [parts.Toolbar ?? new BoxEl { Height = VerticalLayout.ToolbarRowHeight }],
                            },
                            Gutter("hero:toolbar-trail", trail),
                        ],
                    },
                    Air("hero:toolbar-bottom", columns ? 0f : VerticalLayout.ExpandedToolbarBottomPad),
                ],
            };
        }
    }

    /// <summary>A run of vertical air whose height a crossing changes: a spacer that eases its height (a padding cannot ease). Empty space on a skeleton.</summary>
    static Element Air(string key, float height)
        => new BoxEl { Key = key, Height = height, MinHeight = 0f, Shrink = 0f, HitTestVisible = false, Layout = ClassicTierMotion.SizeReflow }.Skeletonized(false);

    /// <summary>A run of horizontal air whose width a crossing changes: a spacer that eases its width.</summary>
    static Element Gutter(string key, float width)
        => new BoxEl { Key = key, Width = width, MinWidth = 0f, Shrink = 0f, HitTestVisible = false, Layout = ClassicTierMotion.SizeReflow }.Skeletonized(false);

    // ══ 1c. THE IDENTITY (one tree: the hero form's artwork and rows, the rail, a head) ═══════════════════════════════════════

    /// <summary>The identity's slot in the hero: at the hero form the full-width padded block (the artwork beside or over the rows, the description under
    /// them); at the columns form the rail, laid out 0 tall at the rail's live width so it hangs below the list bar beside the rows (the table insets them
    /// by exactly that, <see cref="ListInsets"/>), in a box as tall as the list's viewport with its own scroller, or the collapsed strip in its place; on a
    /// page with no rail the head (the title and the meta line). ONE tree: the cover, the eyebrow, the title, the meta, the actions and the description are
    /// the same keyed nodes in every form, re-arranged (the cover's bounds tween, every row glides); what one form draws and the other does not (the rule
    /// and the hero's artist run; the rail's owner, artists, courtesy rows and release) enters and leaves on the declared swap fade.</summary>
    static Element Identity(DetailHeroProps p, global::Wavee.Theming.TierFacts tiers, bool columns, bool asideDocked, EnterExit? swapIn, bool rowFlow,
        bool boundsSeen, float gutter)
    {
        var spec = p.Spec;
        var parts = p.Parts;
        var id = spec.Identity;
        var cfg = spec.Config;
        var acts = spec.Actions;
        var slots = spec.Slots;
        var tc = parts.Columns?.TwoColumn;
        var rail = tc?.Rail;
        bool railForm = columns && tc is not null;
        bool collapsed = railForm && tc!.Collapsed;
        bool head = columns && tc is null;
        var detailTier = ClassicDetailTiers.Of(tiers);
        var railRec = detailTier.Rail;
        ColorF accent = spec.Accent();
        var f = Detail.FlagsOf(spec);

        // ── the hero form's geometry, off ONE bucketed width (D49: the skeleton and the collapse height read the same) ──
        float availW = parts.ColumnWidth > 0f ? parts.ColumnWidth : VerticalLayout.FallbackW;
        float bw = VerticalLayout.BucketW(availW);
        float pad = VerticalLayout.HeroPadFor(bw, rowFlow);
        float gap = VerticalLayout.HeroGapFor(bw, rowFlow);
        float art = VerticalLayout.ArtworkFor(bw, rowFlow);
        float contentW = VerticalLayout.ContentWidthFor(bw, rowFlow);
        var plan = VerticalLayout.TitleTypeFor(bw, rowFlow, id.Title,
            eyebrow: f.Eyebrow, attribution: f.Attribution, meta: f.Meta, pulse: f.Pulse, chart: f.Chart);
        float identityGap = VerticalLayout.IdentityGapFor(bw, rowFlow, plan,
            f.Eyebrow, f.Attribution, f.Meta, pulse: f.Pulse, chart: f.Chart);

        // ── the rail's (the columns form): its live width, its cover (the column less the tier's side padding), the rung's title cap and clamp ──
        float railW = tc?.RailWidth ?? 0f;
        float railCover = RailCoverEdge(railW, railRec);
        float titleCap = rail?.TitleSize ?? 0f;
        int railDescLines = rail?.DescriptionLines ?? 0;
        Element[]? satellites = railForm ? slots.Satellites?.Invoke() : null;
        var presence = PresenceOf(slots, satellites?.Length ?? 0);
        bool blurb = (id.EditableMetadata && slots.Description is not null) || id.DescriptionHtml is { Length: > 0 };
        bool heart = cfg.Heart != HeartMode.None;
        var rr = RailLayout.RowsFor(id.Kind, cfg.Badges, id.Eyebrow.Length > 0, id.OwnerName is { Length: > 0 },
            id.Artists is { Count: > 0 }, id.Meta is { Length: > 0 } || id.MetaLoading, ClassicDetailKit.FixedActionCount(id, heart, acts), blurb, railDescLines,
            presence);

        var rows = new List<Element>(16);

        // ── the lead: the eyebrow (the rail shows it for TypeYear only, W27; the hero wherever the page has one) ──
        if (railForm ? rr.Eyebrow : !head && f.Eyebrow)
            rows.Add(Late("hero-eyebrow", railForm && rr.Badges == RailBadgeRow.BesideEyebrow && slots.Badges is { } badgesBeside
                ? EyebrowWithBadges(id.Eyebrow, badgesBeside(), railCover)
                : ClassicDetailKit.EyebrowRun(id.Eyebrow) with { Width = railForm ? railCover : float.NaN }, swapIn));
        // The rail's owner row, before the title. A page's own attribution builder is the hero's attribution too: one node that moves (after the rule at
        // the hero form); the default owner block is the rail's alone (the hero's default is the owner run).
        bool pageAttribution = slots.Attribution is not null && id.Kind != DetailKind.Album;
        if (railForm && rr.Owner)
        {
            if (RailLayout.LeadsWithAttribution(id.Kind, presence) && slots.Attribution is { } showLink) rows.Add(Late("rail:lead", showLink(railCover), swapIn));
            else if (pageAttribution) rows.Add(Late("hero-attribution", slots.Attribution!(railCover), swapIn));
            else rows.Add(Late("rail:owner", OwnerBlock(id, railCover), swapIn));
        }
        if (railForm && rr.Badges == RailBadgeRow.OwnRow && slots.Badges is { } badgesOwn)
            rows.Add(Late("rail:badges", badgesOwn(), swapIn) with { MinHeight = RailLayout.BadgeHeight });

        // ── the title: ONE node. The hero's plan (one size per bucketed width), the rail's (the rung is its cap), or the head's display face in its fixed
        //    52 line box; drawn exactly, wrapping freely, ellipsised only past the pathological cap ──
        Element title;
        if (head)
            title = R.Type.SurfaceDisplay(id.Title) with { MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f };
        else
        {
            var titlePlan = railForm ? VerticalLayout.RailTitleTypeFor(railCover, titleCap, id.Title) : plan;
            float titleW = railForm ? railCover : plan.WrapWidth;
            title = slots.Title?.Invoke(titlePlan.Size, float.NaN)
                ?? R.Type.DetailHero(id.Title) with
                {
                    Size = titlePlan.Size, Weight = 600,
                    // CLEARED: the natural line box of the size drawn (the plan's LineHeight is that same number).
                    LineHeight = float.NaN,
                    Width = titleW, MaxWidth = titleW,
                    // WRAPS freely: the cap is the pathological-length guard, never the plan's line count.
                    Wrap = TextWrap.WrapWholeWords, MaxLines = VerticalLayout.TitleLineCap,
                    Trim = TextTrim.CharacterEllipsis, Color = Tok.TextPrimary,
                };
        }
        rows.Add(Block("hero-title", new BoxEl
        {
            Direction = 1, MinWidth = 0f, Height = head ? ClassicDetailLayout.HeadTitleLine : float.NaN, Children = [title],
        }));

        // ── the hero form's rule and artist run (the rail's artists are its own line, after the title) ──
        if (!columns)
        {
            rows.Add(Swap("hero-rule", Controls.AccentRule(accent), swapIn));
            if (f.Attribution) rows.Add(Late("hero-attribution", HeroAttribution(id, slots, contentW, accent), swapIn));
        }
        if (railForm && rr.Artists)
            rows.Add(Late("rail:artists", slots.Attribution?.Invoke(railCover)
                ?? (id.Artists is { Count: > 0 } billed ? BilledArtists(billed, railCover) : new BoxEl()), swapIn));
        if (railForm && rr.Rating && slots.Rating is { } rating)
            rows.Add(Late("rail:rating", rating(railCover), swapIn) with { MinHeight = RailLayout.RatingHeight });

        // ── the meta: ONE node in every form. While a find or a filter narrows the list it reads "3 of 48 songs · 11 min (filtered)" (tuned Classic D4) —
        //    its own component, so a keystroke re-renders one line ──
        if (railForm ? rr.Meta : head || f.Meta)
        {
            float metaW = railForm ? railCover : float.NaN;
            float metaMax = railForm || head ? float.NaN : contentW;
            int metaLines = head ? 1 : 2;
            var shown = rail?.Shown ?? parts.Columns?.Head?.Shown;
            rows.Add(Late("hero-meta", new BoxEl
            {
                Direction = 1, MinWidth = 0f, Height = head ? ClassicDetailLayout.HeadMetaLine : float.NaN,
                Children =
                [
                    shown is { } count
                        ? Embed.Comp(new RailMetaProps(id, metaW, metaMax, metaLines), () => new RailMetaLine(count))
                        : MetaLine(id, metaW, metaMax, metaLines),
                ],
            }, swapIn));
        }
        if (railForm && rr.Badges == RailBadgeRow.AfterMeta && slots.Badges is { } badgesLate)
            rows.Add(Late("rail:badges", badgesLate(), swapIn) with { MinHeight = RailLayout.BadgeHeight });
        if (railForm && rr.Ledger && slots.Ledger is { } ledger)
            rows.Add(Late("rail:ledger", ledger(railCover), swapIn) with { MinHeight = RailLayout.LedgerHeight });

        // ── the status rows: the daylist's pulse, the chart's news (one node each in both forms) ──
        if ((railForm ? id.DaylistExpiresAtMs > 0 : !head && f.Pulse) && slots.Pulse is { } pulse) rows.Add(Late("hero-pulse", pulse(), swapIn));
        if (railForm ? id.ChartNewEntries > 0 : !head && f.Chart) rows.Add(Late("hero-chart", ChartCaption(id, slots), swapIn));

        // ── the actions: ONE row in both forms (the Play split, then Shuffle · the heart · Share · More in one wrap, the window tier's targets); the rail
        //    breaks after the split where its tier says (C-Album-Small), and a page's own primary and satellites lead the rail's (W1, W4) ──
        if (!head)
        {
            float target = detailTier.Target;
            Action play = spec.PlayAll ?? rail?.Play ?? DetailVerbs.PlayContext(id.Subject);
            Element primary = railForm && slots.Primary is { } primaryOf ? primaryOf(spec.Accent) : ClassicDetailKit.PlaySplit(id.Subject, spec.Accent, play, target);
            float topMargin = railForm ? RailLayout.CtaTopMargin : Spacing.XS;
            rows.Add(Block("hero-actions", satellites is not null
                ? SatelliteCta("vhero-actions", primary, satellites, topMargin)
                : ClassicDetailKit.ActionRow("vhero-actions", primary,
                    ClassicDetailKit.FixedActions("vhero-", id, heart, acts, spec.Accent, MathF.Max(RailLayout.FabSize, target)),
                    topMargin, railForm && railRec.BreakAfterPrimary ? railCover : 0f)));
        }

        // ── the rail's courtesy rows: topics, about, the countdown, About this release (the band under the rows holds the last two at the hero form) ──
        if (railForm)
        {
            if (rr.Topics && slots.Topics is { } topics) rows.Add(Late("rail:topics", topics(railCover), swapIn));
            if (slots.About is { } about) rows.Add(Late("rail:about", about(railCover), swapIn));
            if (id.UpcomingAtUnixSeconds > 0 && slots.PreRelease is { } pre) rows.Add(Late("rail:prerelease", pre(), swapIn));
            // Release facts arrive after the frame: the late panel keeps out of the rows' shove (ClassicDetailKit.ReleaseRow).
            if (!asideDocked && cfg.Badges == BadgeStyle.TypeYear && slots.ReleasePanel is { } release)
                rows.Add(ClassicDetailKit.ReleaseRow("rail:release", release(false)) with
                {
                    Enter = swapIn, Exit = ClassicTierMotion.SwapFade, Transition = ClassicTierMotion.SwapFadeMotion,
                });
        }

        // ── the description: one node, under the artwork row (the hero form: 13 at the hero's clamp; the rail: 12 at the rung's) ──
        Element? description = null;
        if (railForm ? railDescLines > 0 : !head)
        {
            float descW = railForm ? railCover : VerticalLayout.DescriptionWidthFor(bw, rowFlow);
            int descLines = railForm ? railDescLines : VerticalLayout.DescriptionMaxLines(rowFlow);
            float descSize = railForm ? RailDescriptionSize : HeroDescriptionSize;
            if (id.EditableMetadata && slots.Description is { } editDescription)
                description = editDescription(descW);
            else if (id.DescriptionHtml is { Length: > 0 } html)
                // Clamped with the inline "… More" the engine shows only when the body overflows, and the whole text on hover while collapsed (fullTextTip).
                description = Controls.ExpandableRichText(html, descSize, Tok.TextSecondary, accent, descW, descLines,
                                                          id.Subject.Text, DetailVerbs.NavigateKey, fullTextTip: true);
        }

        // ── the artwork row: the cover BESIDE the rows (the hero's row flow) or OVER them (the stacked hero, the rail); a head has no cover. The page's
        //    ANCHOR: no entrance, no morph — its bounds tween from the top-left, so its corner stays pinned to the title's top across a re-arrangement ──
        float coverEdge = railForm ? railCover : art;
        int decodePx = railForm ? RailCoverDecodePx : VerticalLayout.ArtworkDecodePx(art, boundsSeen);
        var rowKids = new List<Element>(2);
        if (!head)
            rowKids.Add(ClickableCover(new BoxEl
            {
                Key = "hero:cover",
                Width = coverEdge, Height = coverEdge, Shrink = 0f,
                Corners = CornerRadius4.All(Radii.Card), Shadow = Elevation.Card, ClipToBounds = true,
                Animate = HeroGeometryMotion, TransformOriginX = 0f, TransformOriginY = 0f,
                Enter = swapIn, Exit = ClassicTierMotion.SwapFade,
                Draggable = acts.CoverDrag is { } drag ? Drag.Source(drag) : null,
                Children = [slots.Cover?.Invoke(coverEdge)
                    ?? Controls.Artwork(id.CoverUrl, coverEdge, coverEdge, Radii.Card, decodePx: decodePx, saturation: 1.18f)],
            }, acts.CoverClick));
        bool besideCover = !columns && rowFlow;
        // AlignItems Stretch (+ a definite width when stacked) is load-bearing: the action row WRAPS, and a wrap needs a definite width to wrap against.
        rowKids.Add(new BoxEl
        {
            Key = "hero:rows", Direction = 1, AlignItems = FlexAlign.Stretch, MinWidth = 0f,
            Gap = railForm ? railRec.Gap : head ? ClassicTiers.HeadMetaGap : identityGap,
            Width = railForm ? railCover : head || besideCover ? float.NaN : contentW,
            Grow = besideCover ? 1f : 0f, Basis = besideCover ? 0f : float.NaN,
            MinHeight = columns ? 0f : VerticalLayout.IdentityMinHeightFor(bw, rowFlow),
            Layout = HeroReflowMotion,
            Children = rows.ToArray(),
        });
        // Stacked ↔ row ↔ the rail's column is the SAME children in the same order: the re-arrangement animates as one gesture.
        Element artRow = new BoxEl
        {
            Key = "hero:art-row", Direction = (byte)(besideCover ? 0 : 1), Gap = railForm ? railRec.Gap : head ? 0f : gap,
            AlignItems = head ? FlexAlign.Stretch : FlexAlign.Start, MinWidth = 0f,
            Animate = HeroReflowMotion,
            Children = rowKids.ToArray(),
        };

        Element column = new BoxEl
        {
            Key = "hero:column", Direction = 1, MinWidth = 0f, Width = railForm ? railW : float.NaN, Shrink = railForm ? 0f : 1f,
            Padding = railForm ? railRec.Padding
                : head ? new Edges4(gutter, ClassicTiers.HeadTop, gutter, ClassicDetailLayout.HeadPadBottom)
                : new Edges4(pad, pad, pad, VerticalLayout.HeroBottomPad),
            Animate = HeroReflowMotion,
            Children = description is null
                ? [artRow]
                : [artRow, Late("hero-description", description, swapIn) with
                    {
                        Margin = new Edges4(0f, railForm ? railRec.Gap : VerticalLayout.DescriptionGapFor(rowFlow), 0f, 0f),
                    }],
        };
        // The identity's loading state: the header shimmers it whole until it has answered, cross-dissolving once (the rail's rows at the columns form, the
        // hero's at the hero form). The hero list mounts at once (no loading band, audit B7 r2), so this is the head's one loading state, and the swap EASES
        // its height (SmoothResize): rows the data brings (a second title line, the description) open with the chrome and the rows riding them, never a
        // jump. One boundary in every form, so the column never re-parents.
        Func<bool> pending = rail?.Pending ?? s_false;
        Element region = new SkelRegionEl(
            Pending: pending, Failed: s_false,
            Content: () => column,
            ShimmerSource: railForm
                ? () => RailSkeletonColumn(id, cfg, slots, acts, railW, railRec, titleCap, railDescLines)
                : () => HeroIdentitySkeleton(spec, availW, rowFlow, tiers),
            OnFailed: null, Reveal: SkelReveal.FadeOnly, Style: SkeletonStyle.Default, Group: null, SmoothResize: true);

        // The rail's own scroller (the last resort once its text has given): as tall as the list's viewport at the columns form, so what the rail holds past
        // the fold stays reachable; at the hero form it sizes to the column (never scrolls: the list carries the hero). The collapsed strip stands in its
        // place while the rail is collapsed (its fill is the frame's ground, as the rail's is).
        Element body;
        if (collapsed)
        {
            float stripCover = CompactCover(tc!.CompactWidth);
            Action expand = tc.CompactRail?.Expand ?? s_noop;
            body = CompactRailStrip(id.Title, tc.CompactWidth, expand, new SkelRegionEl(
                Pending: tc.CompactRail?.Pending ?? s_false, Failed: s_false,
                Content: () => CompactRailIdentity(id, slots, stripCover, expand),
                ShimmerSource: () => CompactRailSkeleton(id.CoverUrl, stripCover),
                OnFailed: null, Reveal: SkelReveal.FadeOnly, Style: SkeletonStyle.Default, Group: null, SmoothResize: false), fill: false)
                with { Key = "hero:strip", Enter = swapIn, Exit = ClassicTierMotion.SwapFade };
        }
        else
            body = Ui.ScrollView(region) with
            {
                Key = "hero:identity-scroll", ContentSized = true, Shrink = 1f, MinHeight = 0f, Grow = railForm ? 1f : 0f,
                ScrollKey = "hero:identity:" + id.Subject.Text,
            };
        var viewportH = parts.ViewportHeight;
        Prop<float> frameH = railForm && viewportH is not null ? Prop.Of(() => (float)viewportH.Value) : float.NaN;
        return new BoxEl
        {
            // The slot's size lands at once (the list head beside it then lays out at its final width from the commit, so the list bar fits once); its rows
            // glide inside it.
            Key = "hero:rail", Direction = 0, Shrink = 0f, MinWidth = 0f, Layout = ClassicTierMotion.Shove,
            Width = railForm ? RailLead(tc!) : float.NaN, Height = railForm ? 0f : float.NaN,
            Children =
            [
                new BoxEl
                {
                    // CLIPS at the columns form (bug B): the cover takes an explicit square of the rail's edge; while a split settles a width that has not caught
                    // up must not paint over the list's lanes. The resist cue fades it (paint-bound: the cue never re-renders the rail).
                    Key = "hero:rail-frame", Direction = 1, MinWidth = 0f, MinHeight = 0f,
                    Width = railForm ? (collapsed ? tc!.CompactWidth : railW) : float.NaN,
                    Grow = railForm ? 0f : 1f, Shrink = railForm ? 0f : 1f, Basis = railForm ? float.NaN : 0f,
                    Height = frameH, ClipToBounds = railForm,
                    Opacity = railForm ? tc!.RailOpacity : 1f,
                    Children = [body],
                },
                // The collapsed grip's 20-DIP slot (the grip itself is the frame's seam layer, over the list, in both arms).
                new BoxEl { Key = "hero:grip-slot", Width = collapsed ? tc!.CollapsedGripWidth : 0f, Shrink = 0f, HitTestVisible = false },
            ],
        };
    }

    /// <summary>The description's size: the hero's 13, the rail's 12.</summary>
    const float HeroDescriptionSize = 13f, RailDescriptionSize = 12f;

    static readonly Action s_noop = static () => { };

    /// <summary>A keyed identity row present in both forms: the row shove, its height eased where the form re-measures it.</summary>
    static BoxEl Block(string key, Element child) => new()
    {
        Key = key, Direction = 1, MinWidth = 0f, Layout = ClassicDetailKit.GrowShove, Children = [child],
    };

    /// <summary>A keyed identity row that can arrive late (it fades up), and that one form may not draw (it leaves on the swap fade; a re-arrangement brings
    /// it in on the same fade).</summary>
    static BoxEl Late(string key, Element child, EnterExit? swapIn) => Block(key, child) with
    {
        Enter = swapIn ?? ClassicDetailKit.FadeUp, Exit = ClassicTierMotion.SwapFade,
    };

    /// <summary>A keyed row of one form only that arrives with its page (the hero's rule): it enters and leaves on the declared swap fade, with no entrance
    /// of its own when the page first draws it.</summary>
    static BoxEl Swap(string key, Element child, EnterExit? swapIn) => Block(key, child) with { Enter = swapIn, Exit = ClassicTierMotion.SwapFade };

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
        // No names yet (a head still loading holds its kind's artists line, Detail.FlagsOf): the line's box, empty, so the names fade into it (audit B7).
        if (artists is not { Count: > 0 }) return new BoxEl { Height = ClassicDetailLayout.AttributionLine };
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

    // ══ 4. THE IDENTITY, LOADING ═══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The hero form's identity, loading: the padded [artwork | rows] block and the description's lines, the loaded identity's own boxes at its
    /// sizes (the hero's own pending region's shimmer source while the page's header has not answered).</summary>
    static Element HeroIdentitySkeleton(VerticalSpec spec, float w, bool rowFlow, global::Wavee.Theming.TierFacts tiers)
    {
        float bw = VerticalLayout.BucketW(w);
        var f = Detail.FlagsOf(spec);
        var id = spec.Identity;
        float pad = VerticalLayout.HeroPadFor(bw, rowFlow);
        float gap = VerticalLayout.HeroGapFor(bw, rowFlow);
        float art = VerticalLayout.ArtworkFor(bw, rowFlow);
        float contentW = VerticalLayout.ContentWidthFor(bw, rowFlow);
        float descW = VerticalLayout.DescriptionWidthFor(bw, rowFlow);
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
        // A list's attribution is its owner block, reserved at the block's own height (the 24 portrait and the name in ClassicPlaylistLayout.OwnerRowHeight:
        // the 16-DIP text bar it shimmered as left the actions 16 high, audit B7 r2); any other kind's is the one text line.
        if (f.Attribution)
            blocks.Add(id.Kind == DetailKind.Playlist && spec.Slots.Attribution is not null
                ? new BoxEl
                {
                    Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, Height = ClassicPlaylistLayout.OwnerRowHeight,
                    Children =
                    [
                        new BoxEl { Width = ClassicPlaylistLayout.OwnerAvatar, Height = ClassicPlaylistLayout.OwnerAvatar, Shrink = 0f,
                                    Corners = CornerRadius4.All(ClassicPlaylistLayout.OwnerAvatar / 2f) },
                        Bar(Skeleton.BarWidth(contentW, Skeleton.AttributionFraction), VerticalLayout.AttributionRowHeight),
                    ],
                }
                : Bar(Skeleton.BarWidth(contentW, Skeleton.AttributionFraction), VerticalLayout.AttributionRowHeight));
        if (f.Meta) blocks.Add(Bar(Skeleton.BarWidth(contentW, Skeleton.MetaFraction), VerticalLayout.MetaRowHeight));
        if (f.Pulse) blocks.Add(Bar(Skeleton.BarWidth(contentW, Skeleton.PulseFraction), VerticalLayout.PulseRowHeight));
        if (f.Chart) blocks.Add(Bar(Skeleton.BarWidth(contentW, Skeleton.ChartFraction), VerticalLayout.ChartRowHeight));
        blocks.Add(SkeletonActionRow(spec, ClassicDetailTiers.Of(tiers).Target));

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
            Direction = 1, Padding = new Edges4(pad, pad, pad, VerticalLayout.HeroBottomPad),
            Children = f.Description
                ? [hero, Lines(descW, VerticalLayout.DescriptionLineHeight,
                      VerticalLayout.DescriptionMaxLines(rowFlow), Skeleton.DescriptionLastLineFraction) with
                      { Margin = new Edges4(0f, VerticalLayout.DescriptionGapFor(rowFlow), 0f, 0f) }]
                : [hero],
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

}
