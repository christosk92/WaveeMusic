// ── Themes/Classic/Structure/Views/Detail/ClassicDetailRail.cs — Classic's detail rail, collapsed strip and show header (themes plan 6.1, T9a) ──
//
// From `Entities\Detail.UI.cs` §4 (the rail family): the rail's frame, its loaded column and its loading twin (one skeleton boundary), the
// collapsed 96-DIP identity strip, the vertical show header, and the row bodies they share with the hero (the meta line, the chart caption,
// the default owner row, the billed-artist line). Moved verbatim with the pictures; what DECIDES stays core: the row presence (`RailLayout`,
// `Skeleton.RailPlanFor`), the title rule (`VerticalLayout.RailTitleTypeFor`), the rung that picks the title cap and the description clamp
// (the host), the accent, the play-all cell and every page slot builder.
//
// The page-facing helpers (`Detail.PlayButton`, `RailSatellite*`, `Satellite`, `MoreButton`, `ArtistLine` and the share / save targets) STAY in
// `Detail`: the pages, the library panes and the artist page build their own pieces from them (T9c / T10 move them with those pages). This file calls
// them through `Detail`. The keyed rail rows (`Row` / `LateRow` / `ReleaseRow`), the eyebrow run and the billed-artist line box moved to the shared kit
// (`ClassicDetailKit.cs`, T9b), and the notice strip to `ClassicDetailSeam.cs`.
//
// NAME NOTE: `Text`, `Skeleton` and `Config` are nested `Detail` classes; the aliases below keep their short names here.

using System.Globalization;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;
using Config = Wavee.Detail.Config;
using FrameActions = Wavee.Detail.FrameActions;
using FrameSlots = Wavee.Detail.FrameSlots;
using FrameSpec = Wavee.Detail.FrameSpec;
using Identity = Wavee.Detail.Identity;
using RailArtistLine = Wavee.Detail.RailArtistLine;
using RailArtistMode = Wavee.Detail.RailArtistMode;
using RailBadgeRow = Wavee.Detail.RailBadgeRow;
using RailLayout = Wavee.Detail.RailLayout;
using RailSlotSet = Wavee.Detail.RailSlotSet;
using Skeleton = Wavee.Detail.Skeleton;
using Text = Wavee.Detail.Text;
using VerticalLayout = Wavee.Detail.VerticalLayout;

namespace Wavee.Themes.Classic;

/// <summary>Classic's detail frame: the scaffold (<see cref="Template"/>), the rail, the collapsed strip, the show header, the hero and its loading band
/// (this class is split over <c>ClassicDetailFrame.cs</c>, <c>ClassicDetailRail.cs</c> and <c>ClassicDetailHero.cs</c>).</summary>
internal static partial class ClassicDetailFrame
{
    // The rail's own pictures' numbers (Rules\DetailLayout.cs).
    static float RailSidePadL => Spacing.L;
    static float RailSidePadR => Spacing.S;   // 16 / 8
    const float RailGap = RailLayout.Gap;                             // 14 — the row model's (Detail.cs §8b)
    const float RailFabSize = RailLayout.FabSize;                     // 32 (Workstream B: was 40)
    const int RailCoverDecodePx = ClassicDetailLayout.CoverDecodePx;  // the shelf card's bucket — a warm texture on arrival
    const string MetaShimmerText = ClassicDetailLayout.MetaShimmerText;   // the shimmer SHAPE only; never painted as text

    /// <summary><c>DetailRail.CoverEdge</c>: the rail cover fills the column less its side padding, floored at 80.</summary>
    static float RailCoverEdge(float railW) => MathF.Max(ClassicDetailLayout.RailCoverMin, railW - RailSidePadL - RailSidePadR);

    // ── the templates ──

    /// <summary>The registered template of <see cref="ViewKeys.DetailRail"/>: the expanded rail, one <c>SkelRegionEl</c> pending on the header
    /// (<see cref="Identity.HeaderPending"/>), whose shimmer is derived from a column of the SAME rows the loaded rail lays out
    /// (<see cref="RailSkeletonColumn"/> mirrors <see cref="RailColumn"/> row for row), cross-dissolving once — the table's contract (FadeOnly, no
    /// resize tween). The frame (layer fill + scroller) sits OUTSIDE the boundary so the fill never blinks. The content thunk closes over this
    /// render's spec; a later push re-renders the frame and the region refreshes its Ready branch in place, so the rail keeps following the header
    /// (meta, description, a rename) after the reveal.</summary>
    public static Element RailTemplate(DetailRailProps p, in ViewContext ctx)
        => RailFrame(p.Spec.Identity.Kind, p.RailWidth, new SkelRegionEl(
            Pending: p.Pending, Failed: s_false,
            Content: () => RailColumn(p.Spec, p.Actions, p.RailWidth, p.TitleSize, p.DescriptionLines, p.Accent, p.Play),
            ShimmerSource: () => RailSkeletonColumn(p.Spec, p.RailWidth, p.TitleSize, p.DescriptionLines),
            OnFailed: null, Reveal: SkelReveal.FadeOnly, Style: SkeletonStyle.Default, Group: null, SmoothResize: false));

    /// <summary>The registered template of <see cref="ViewKeys.DetailCompactRail"/>: the collapsed strip's identity (cover + title) behind the same
    /// boundary; the expand chevron stays real.</summary>
    public static Element CompactRailTemplate(DetailCompactRailProps p, in ViewContext ctx)
    {
        float cover = CompactCover(p.StripWidth);
        return CompactRailStrip(p.Spec, p.StripWidth, p.Expand, new SkelRegionEl(
            Pending: p.Pending, Failed: s_false,
            Content: () => CompactRailIdentity(p.Spec, cover, p.Expand),
            ShimmerSource: () => CompactRailSkeleton(p.Spec, cover),
            OnFailed: null, Reveal: SkelReveal.FadeOnly, Style: SkeletonStyle.Default, Group: null, SmoothResize: false));
    }

    /// <summary>The registered template of <see cref="ViewKeys.DetailShowHeader"/>.</summary>
    public static Element ShowHeaderTemplate(DetailShowHeaderProps p, in ViewContext ctx)
        => ShowHeaderCore(p.Spec, p.Actions, p.Accent, p.Play, p.PageWidth);

    // ── the rail family ──

    /// <summary>The rail's frame: the layer fill and, the LAST resort once the text has given, the rail's own scroller.
    /// The Liked arm differs in exactly ONE property — no layer fill (ch 03 §9). Holds the loaded column, or the skeleton
    /// boundary that swaps to it (<see cref="RailTemplate"/>) — outside the boundary, so the fill never blinks.</summary>
    static Element RailFrame(DetailKind kind, float railW, Element body) => new BoxEl
    {
        Direction = 1, Width = railW, Shrink = 0f, ClipToBounds = true,
        Fill = kind == DetailKind.Liked ? ColorF.Transparent : Tok.FillLayerDefault,
        Children = [ScrollView(body) with { Grow = 1f, Shrink = 1f, MinHeight = 0f, Width = railW }],
    };

    /// <summary>The page-subject cover's gesture, from the shared surface's ownership rule (<see cref="SurfaceRules"/>). The
    /// cover stays its own tree — it is the page's SUBJECT, not an item: it keeps its morph and its shadow — but what makes
    /// a box invokable is ONE rule everywhere: a clickable cover is a Button and a tab stop (Enter / Space invoke through
    /// the engine's click) with the bordered focus margin and the hand; a display-only cover carries none of them. Applied
    /// by the rail, the show header and the vertical hero alike.</summary>
    static BoxEl ClickableCover(BoxEl cover, Action? click)
    {
        var mode = SurfaceRules.Ownership(inSlot: false, hasClick: click is not null);
        return !mode.OwnsClick ? cover : cover with
        {
            OnClick = click, Role = mode.Role, Focusable = mode.OwnsFocus,
            FocusVisualMargin = Design.FocusInsetBordered, Cursor = SurfaceRules.Cursor(in mode),
        };
    }

    /// <summary>The rail's loaded column: cover · eyebrow (+ a show's badges) / owner / an episode's show link · title ·
    /// attribution · rating · meta · an episode's badges · ledger · daylist · chart · CTA (primary + the fixed group or the
    /// page's satellites) · topics · prerelease · release panel · description · liked facts. The identity rows are
    /// <see cref="RailLayout.RowsFor"/>'s decisions; <see cref="RailSkeletonColumn"/> reserves the same rows
    /// (<see cref="Skeleton.RailPlanFor"/>) in the same order at the same widths, so the swap is a dissolve and never a
    /// reflow. With none of the podcast slots declared the column is exactly the pre-podcast one.</summary>
    static Element RailColumn(FrameSpec spec, FrameActions acts, float railW, float titleSize,
                              int descMaxLines, Func<ColorF> accent, Action play)
    {
        var id = spec.Identity;
        var cfg = spec.Config;
        var slots = spec.Slots;
        float cover = RailCoverEdge(railW);
        ColorF accentColor = accent();
        var kids = new List<Element>(16);

        // The CTA's pieces first: how many FABs it carries is a row decision (it sizes the CTA's lines) as well as its
        // children. A page's Satellites replace the fixed group; no Shuffle either way (the command bar's, W27).
        Element[]? satellites = slots.Satellites?.Invoke();
        List<Element>? fabs = satellites is null ? RailFabs(id, cfg.Heart != HeartMode.None, acts, accent) : null;
        var presence = PresenceOf(slots, satellites?.Length ?? 0);
        bool lead = RailLayout.LeadsWithAttribution(id.Kind, presence);
        bool blurb = (id.EditableMetadata && slots.Description is not null) || id.DescriptionHtml is { Length: > 0 };
        var rows = RailLayout.RowsFor(id.Kind, cfg.Badges, id.Eyebrow.Length > 0, id.OwnerName is { Length: > 0 },
            id.Artists is { Count: > 0 }, id.Meta is { Length: > 0 } || id.MetaLoading, fabs?.Count ?? 0, blurb, descMaxLines,
            presence);

        // Cover: the column's ANCHOR — keyed, never animated. The drag source sits on the FRAMING box so an editable
        // cover's own file-drop target inside it is untouched.
        kids.Add(ClickableCover(new BoxEl
        {
            Key = "rail:cover",
            Width = cover, Height = cover, Corners = CornerRadius4.All(Radii.Card),
            Shadow = Elevation.Card, ClipToBounds = true,
            Draggable = acts.CoverDrag is { } drag ? Drag.Source(drag) : null,
            Children = [slots.Cover?.Invoke(cover)
                ?? Controls.Artwork(id.CoverUrl, cover, cover, Radii.Card, decodePx: RailCoverDecodePx, saturation: 1.18f)],
        }, acts.CoverClick));

        // The lead. The eyebrow asymmetry (W27): the rail shows it for TypeYear only (a show's badges share its line);
        // OwnerRow gets the owner block instead; an EPISODE is led by its show link (its Attribution slot, W4).
        if (rows.Eyebrow)
            kids.Add(ClassicDetailKit.LateRow("rail:eyebrow", rows.Badges == RailBadgeRow.BesideEyebrow && slots.Badges is { } badgesBeside
                ? EyebrowWithBadges(id.Eyebrow, badgesBeside(), cover)
                : ClassicDetailKit.EyebrowRun(id.Eyebrow) with { Width = cover }));
        if (rows.Owner)
            kids.Add(lead && slots.Attribution is { } showLink
                ? ClassicDetailKit.LateRow("rail:lead", showLink(cover))
                : ClassicDetailKit.LateRow("rail:owner", slots.Attribution?.Invoke(cover) ?? OwnerBlock(id, cover)));
        if (rows.Badges == RailBadgeRow.OwnRow && slots.Badges is { } badgesOwn)
            kids.Add(SlotRow("rail:badges", badgesOwn(), RailLayout.BadgeHeight));

        // The title: ONE size from the title rule (the rung is its cap), drawn exactly — no engine auto-fit under an
        // authored line box (that pairing kept a 36/52 box around 18-DIP glyphs) — wrapping freely in its natural line
        // box, ellipsised only past TitleLineCap.
        var titlePlan = VerticalLayout.RailTitleTypeFor(cover, titleSize, id.Title);
        kids.Add(ClassicDetailKit.Row("rail:title", slots.Title?.Invoke(titlePlan.Size, float.NaN)
            ?? R.Type.DetailHero(id.Title) with
            {
                Size = titlePlan.Size, Weight = 600, Width = cover, LineHeight = float.NaN,
                Wrap = TextWrap.WrapWholeWords, MaxLines = VerticalLayout.TitleLineCap, Trim = TextTrim.CharacterEllipsis,
            }));

        // Attribution: an album's billed artists; a podcast's Attribution slot (a show's publisher line).
        if (rows.Artists)
            kids.Add(ClassicDetailKit.LateRow("rail:artists", slots.Attribution?.Invoke(cover)
                ?? (id.Artists is { Count: > 0 } billed ? BilledArtists(billed, cover) : new BoxEl())));
        if (rows.Rating && slots.Rating is { } rating) kids.Add(SlotRow("rail:rating", rating(cover), RailLayout.RatingHeight));

        // Meta: playlist / Liked / the PODCAST family (a show has no facts panel; 0.2.9 built its publisher line and never
        // drew it, ch 09 §9.4; an episode states its date · length).
        if (rows.Meta) kids.Add(ClassicDetailKit.LateRow("rail:meta", MetaRow(id, cover, maxLines: 2)));
        if (rows.Badges == RailBadgeRow.AfterMeta && slots.Badges is { } badgesLate)
            kids.Add(SlotRow("rail:badges", badgesLate(), RailLayout.BadgeHeight));
        if (rows.Ledger && slots.Ledger is { } ledger) kids.Add(SlotRow("rail:ledger", ledger(cover), RailLayout.LedgerHeight));

        if (id.DaylistExpiresAtMs > 0 && slots.Pulse is { } pulse) kids.Add(ClassicDetailKit.LateRow("rail:daylist", pulse()));
        if (id.ChartNewEntries > 0) kids.Add(ClassicDetailKit.LateRow("rail:chart", ChartCaption(id, slots)));

        // CTA cluster: the primary (the page's, else Play), then the fixed FAB GROUP that wraps as a unit — or the page's
        // satellites, wrapping one by one.
        Element primary = slots.Primary is { } primaryOf ? primaryOf(accent) : Controls.ContextPlayButton(spec.Identity.Subject.Text, accent, play);
        kids.Add(satellites is not null
            ? SatelliteCta("rail:cta", primary, satellites, RailLayout.CtaTopMargin)
            : new BoxEl
            {
                Key = "rail:cta", Layout = ClassicDetailKit.Shove,
                Direction = 0, Wrap = true, Gap = RailLayout.CtaGap, AlignItems = FlexAlign.Center,
                Margin = new Edges4(0f, RailLayout.CtaTopMargin, 0f, 0f),
                Children =
                [
                    primary,
                    new BoxEl { Direction = 0, Gap = RailLayout.FabGap, AlignItems = FlexAlign.Center, Children = fabs!.ToArray() },
                ],
            });

        if (rows.Topics && slots.Topics is { } topics) kids.Add(ClassicDetailKit.LateRow("rail:topics", topics(cover)));

        // The short About block (podcast-episode-peek §1): its own late row, because the description lands after the
        // header facts do — an empty body renders an empty box and reserves nothing.
        if (slots.About is { } about) kids.Add(ClassicDetailKit.LateRow("rail:about", about(cover)));

        if (id.UpcomingAtUnixSeconds > 0 && slots.PreRelease is { } pre) kids.Add(ClassicDetailKit.LateRow("rail:prerelease", pre()));

        if (cfg.Badges == BadgeStyle.TypeYear && slots.ReleasePanel is { } release)
            kids.Add(ClassicDetailKit.ReleaseRow("rail:release", release(false)));

        if (descMaxLines > 0)
        {
            if (id.EditableMetadata && slots.Description is { } editDescription)
                kids.Add(ClassicDetailKit.LateRow("rail:desc", editDescription(cover)));
            else if (id.DescriptionHtml is { Length: > 0 } html)
                // Clamped with the inline "… More" the engine shows only when the body overflows (a bare "…" left the
                // rest unreachable), and the whole text on hover while collapsed (fullTextTip).
                kids.Add(ClassicDetailKit.LateRow("rail:desc", Controls.ExpandableRichText(html, 12f, Tok.TextSecondary, accentColor, cover,
                    descMaxLines, id.Subject.Text, DetailVerbs.NavigateKey, fullTextTip: true)));
        }

        // Row, not LateRow: the facts panel owns its own entrance.
        if (slots.LikedFacts is { } facts) kids.Add(ClassicDetailKit.Row("rail:likedfacts", facts(cover)));

        return RailColumnBox(railW, kids.ToArray());
    }

    static Element RailColumnBox(float railW, Element[] rows) => new BoxEl
    {
        Direction = 1, Gap = RailGap, Width = railW, Shrink = 0f,
        Padding = new Edges4(RailSidePadL, RailLayout.PadTop, RailSidePadR, RailLayout.PadBottom),
        Children = rows,
    };

    /// <summary>The rail's FIXED FAB group — the heart (a Save / Follow kind), Share, and the ⋯ every kind but the album
    /// carries. What a page's <see cref="FrameSlots.Satellites"/> replace.</summary>
    static List<Element> RailFabs(Identity id, bool heart, FrameActions acts, Func<ColorF> accent)
    {
        var fabs = new List<Element>(3);
        if (heart)
        {
            string saveUri = Detail.SaveTargetOf(id).Text;
            string saveName = id.Title;
            fabs.Add(Embed.Comp(() => new Controls.SaveButton { Uri = saveUri, Name = saveName, Glyph = 16f, Box = RailFabSize, Accent = accent })
                with { Key = "save:" + saveUri });
        }
        if (Detail.ShareActionFor(id) is { } share)
            fabs.Add(Controls.Named(Detail.Fab(Icons.Share, share), Loc.Get(Strings.Menu.Share)));
        // ch 05 parity 15: an album's rail has no ⋯ — its overflow is the vertical hero's alone.
        if (acts.More is { } more && id.Kind != DetailKind.Album)
            fabs.Add(Detail.MoreButton(more, RailFabSize, 16f, round: true) with { Key = "rail:more" });
        return fabs;
    }

    /// <summary>The SATELLITES arm of a CTA cluster (rail and vertical header): the primary, then each satellite, in ONE
    /// wrap 8 apart both ways — the prototype's <c>.cta</c> (W1, W4).</summary>
    static Element SatelliteCta(string key, Element primary, Element[] satellites, float topMargin)
    {
        var kids = new Element[1 + satellites.Length];
        kids[0] = primary;
        Array.Copy(satellites, 0, kids, 1, satellites.Length);
        return new BoxEl
        {
            Key = key, Layout = ClassicDetailKit.Shove,
            Direction = 0, Wrap = true, Gap = RailLayout.SatelliteGap, AlignItems = FlexAlign.Center,
            Margin = new Edges4(0f, topMargin, 0f, 0f),
            Children = kids,
        };
    }

    /// <summary>A single-line podcast slot row: a <see cref="Detail.LateRow"/> held at least at its nominal height
    /// (<see cref="RailLayout"/>), so a body shorter than its reservation never pulls the rows under it up on reveal.</summary>
    static BoxEl SlotRow(string key, Element child, float nominal) => ClassicDetailKit.LateRow(key, child) with { MinHeight = nominal };

    /// <summary>A show's eyebrow line: the eyebrow run, then the page's badge row in the width the run leaves (a wrapping
    /// chip row wraps inside it, never under the eyebrow). NaN <paramref name="width"/> = the vertical header's column.</summary>
    static Element EyebrowWithBadges(string eyebrow, Element badges, float width) => new BoxEl
    {
        Direction = 0, Gap = RailLayout.BadgeGap, AlignItems = FlexAlign.Start, Width = width, MinWidth = 0f,
        Children =
        [
            ClassicDetailKit.EyebrowRun(eyebrow) with { Shrink = 0f },
            new BoxEl { Direction = 1, Grow = 1f, Basis = 0f, Shrink = 1f, MinWidth = 0f, Children = [badges] },
        ],
    };

    /// <summary>The rail-row presence a page's slots declare — the input <see cref="RailLayout.RowsFor"/> and
    /// <see cref="Skeleton.RailPlanFor"/> share. <paramref name="satelliteCount"/> is the length the Satellites builder
    /// returned (0 when the slot is absent).</summary>
    static RailSlotSet PresenceOf(FrameSlots s, int satelliteCount) => new(
        Attribution: s.Attribution is not null, Badges: s.Badges is not null, Rating: s.Rating is not null,
        Ledger: s.Ledger is not null, Topics: s.Topics is not null, Satellites: s.Satellites is not null,
        SatelliteCount: s.Satellites is null ? 0 : satelliteCount);

    /// <summary>The rail's LOADING column — the shimmer source of <see cref="RailTemplate"/>, derived by the engine into
    /// bars: the same rows as <see cref="RailColumn"/> (<see cref="Skeleton.RailPlanFor"/> decides which, per kind and
    /// badge style and the rail slots the page declared) at the same widths, gap and padding — its nominal height is
    /// <see cref="RailLayout.HeightOf"/> of that plan. Unfilled boxes; a cover already known (a shelf card's 256 bucket)
    /// is painted for real and exempted from the deriver, as the hero's skeleton does.</summary>
    static Element RailSkeletonColumn(FrameSpec spec, float railW, float titleSize, int descMaxLines)
    {
        var id = spec.Identity;
        var cfg = spec.Config;
        var slots = spec.Slots;
        float cover = RailCoverEdge(railW);
        // Satellites are COUNTED (the builder only makes elements; they are dropped here) so the CTA wraps as the reveal will.
        int satelliteCount = slots.Satellites is { } satellitesOf ? satellitesOf().Length : 0;
        var plan = Skeleton.RailPlanFor(id.Kind, cfg.Badges, cfg.Heart != HeartMode.None, descMaxLines,
                                        PresenceOf(slots, satelliteCount));
        var kids = new List<Element>(12) { SkeletonCover(id.CoverUrl, cover) };

        if (plan.Eyebrow)
        {
            Element eyebrow = Bar(Skeleton.BarWidth(cover, Skeleton.EyebrowFraction), VerticalLayout.EyebrowRowHeight);
            kids.Add(plan.Badges == RailBadgeRow.BesideEyebrow
                ? new BoxEl
                {
                    Direction = 0, Gap = RailLayout.BadgeGap, AlignItems = FlexAlign.Start, MaxWidth = cover,
                    Children = [eyebrow, Bar(Skeleton.BadgeBarWidth, RailLayout.BadgeHeight)],
                }
                : eyebrow);
        }
        if (plan.Owner)
            kids.Add(new BoxEl
            {
                Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, MaxWidth = cover,
                Children =
                [
                    new BoxEl { Width = Skeleton.OwnerAvatar, Height = Skeleton.OwnerAvatar, Shrink = 0f, Corners = CornerRadius4.All(Skeleton.OwnerAvatar / 2f) },
                    Bar(Skeleton.BarWidth(cover - Skeleton.OwnerAvatar - Spacing.S, Skeleton.AttributionFraction), VerticalLayout.AttributionRowHeight),
                ],
            });
        if (plan.Badges == RailBadgeRow.OwnRow) kids.Add(Bar(Skeleton.BadgeBarWidth, RailLayout.BadgeHeight));
        // The title block: a KNOWN title's own plan (the rule the loaded rail draws, so the dissolve does not reflow it);
        // none yet ⇒ the reserved two lines in the rung's natural line box.
        if (id.Title is { Length: > 0 } knownTitle)
        {
            var titlePlan = VerticalLayout.RailTitleTypeFor(cover, titleSize, knownTitle);
            kids.Add(Lines(cover, titlePlan.LineHeight, titlePlan.Lines, Skeleton.TitleLastLineFraction));
        }
        else
            kids.Add(Lines(cover, VerticalLayout.NaturalLineHeightFor(titleSize), plan.TitleLines, Skeleton.TitleLastLineFraction));
        if (plan.Artists) kids.Add(Bar(Skeleton.BarWidth(cover, Skeleton.AttributionFraction),
            plan.ArtistFaces ? RailArtistLine.ReservedHeight(cover) : VerticalLayout.AttributionRowHeight));
        if (plan.Rating) kids.Add(Bar(Skeleton.BarWidth(cover, Skeleton.RatingFraction), RailLayout.RatingHeight));
        if (plan.Meta) kids.Add(Bar(Skeleton.BarWidth(cover, Skeleton.MetaFraction), VerticalLayout.MetaRowHeight));
        if (plan.Badges == RailBadgeRow.AfterMeta) kids.Add(Bar(Skeleton.BadgeBarWidth, RailLayout.BadgeHeight));
        if (plan.Ledger)
            kids.Add(new BoxEl
            {
                Direction = 1, Gap = RailLayout.LedgerGap, Width = cover,
                Children =
                [
                    Bar(cover, RailLayout.LedgerBarHeight),
                    Bar(Skeleton.BarWidth(cover, Skeleton.LedgerLineFraction), RailLayout.LedgerLineHeight),
                ],
            });

        float fabEdge = plan.Satellites ? RailLayout.SatelliteSize : RailFabSize;
        var fabs = new Element[Math.Max(0, plan.Fabs)];
        for (int i = 0; i < fabs.Length; i++)
            fabs[i] = new BoxEl { Width = fabEdge, Height = fabEdge, Shrink = 0f, Corners = Radii.ControlAll };
        Element pill = new BoxEl { Width = Skeleton.PlayButtonWidth, Height = Controls.ButtonHeight, Shrink = 0f, Corners = Radii.ControlAll };
        kids.Add(plan.Satellites
            ? SatelliteCta("skel:cta", pill, fabs, RailLayout.CtaTopMargin)
            : new BoxEl
            {
                Direction = 0, Wrap = true, Gap = RailLayout.CtaGap, AlignItems = FlexAlign.Center,
                Margin = new Edges4(0f, RailLayout.CtaTopMargin, 0f, 0f),
                Children =
                [
                    pill,
                    new BoxEl { Direction = 0, Gap = RailLayout.FabGap, AlignItems = FlexAlign.Center, Children = fabs },
                ],
            });
        if (plan.Topics)
        {
            var topicLines = new Element[RailLayout.TopicLines];
            for (int i = 0; i < topicLines.Length; i++)
                topicLines[i] = Bar(Skeleton.LineWidth(cover, i, RailLayout.TopicLines, Skeleton.DescriptionLastLineFraction),
                                    RailLayout.TopicLineHeight);
            kids.Add(new BoxEl { Direction = 1, Gap = RailLayout.TopicLineGap, Width = cover, Children = topicLines });
        }
        if (plan.DescriptionLines > 0)
            kids.Add(Lines(cover, VerticalLayout.DescriptionLineHeight, plan.DescriptionLines, Skeleton.DescriptionLastLineFraction));

        return RailColumnBox(railW, kids.ToArray());
    }

    /// <summary>The skeleton's cover slot — the rail's, the collapsed strip's and the vertical hero's alike (the page
    /// SUBJECT's own shape, never a media card's, so it is not a <c>Controls.Surface</c> seed): the REAL preview when a url
    /// is already known, exempted from the deriver with a self-override; else the reserved card-cornered square the
    /// deriver paints as a bar.</summary>
    static Element SkeletonCover(string? coverUrl, float edge)
    {
        if (coverUrl is { Length: > 0 } url)
        {
            Element preview = Controls.Artwork(url, edge, edge, Radii.Card, decodePx: RailCoverDecodePx, saturation: 1.18f);
            return preview.Skel(preview);
        }
        return new BoxEl { Width = edge, Height = edge, Shrink = 0f, Corners = CornerRadius4.All(Radii.Card) };
    }

    /// <summary>The collapsed rail (WP-κ): a 96-DIP identity strip — cover, two-line title, chevron. The WHOLE cover and
    /// the chevron both expand (no second hit target inside 48-80 DIP).</summary>
    static float CompactCover(float stripW) => MathF.Max(ClassicDetailLayout.CompactCoverMin, stripW - Spacing.S - Spacing.S);

    /// <summary>The strip's loading identity: the reserved cover square (or the known preview) over two short title
    /// runs — what <see cref="CompactRailIdentity"/> lays out, unfilled.</summary>
    static Element CompactRailSkeleton(FrameSpec spec, float cover) => new BoxEl
    {
        Direction = 1, Gap = Spacing.S, Width = cover,
        Children = [SkeletonCover(spec.Identity.CoverUrl, cover), Lines(cover, 16f, Skeleton.RailTitleLines, Skeleton.TitleLastLineFraction)],
    };

    /// <summary>The strip's identity: the cover (a hit target that expands) and the two-line title.</summary>
    static Element CompactRailIdentity(FrameSpec spec, float cover, Action expand)
    {
        var id = spec.Identity;
        Element coverHit = new BoxEl
        {
            Key = "compact:cover",
            Width = cover, Height = cover, Corners = CornerRadius4.All(Radii.Card),
            Shadow = Elevation.Card, ClipToBounds = true, Shrink = 0f,
            Role = AutomationRole.Button, Cursor = CursorId.Hand, OnClick = expand,
            Children = [spec.Slots.CompactCover?.Invoke(cover)
                ?? Controls.Artwork(id.CoverUrl, cover, cover, Radii.Card, decodePx: RailCoverDecodePx, saturation: 1.18f)],
        };
        Element title = new BoxEl
        {
            Key = "compact:title", Width = cover, Direction = 1,
            Children =
            [
                new TextEl(id.Title)
                {
                    Size = ClassicDetailLayout.CompactTitleSize, LineHeight = ClassicDetailLayout.CompactTitleLine, Weight = 600, Color = Tok.TextPrimary,
                    Width = cover, MinWidth = 0f, MaxLines = 2, Trim = TextTrim.CharacterEllipsis,
                    Wrap = TextWrap.WrapWholeWords,
                },
            ],
        };
        return new BoxEl { Key = "compact:identity", Direction = 1, Gap = Spacing.S, Width = cover, Children = [coverHit, title] };
    }

    /// <summary>The 96-DIP strip around its identity (loaded, or the skeleton boundary that swaps to it): the layer fill,
    /// the spacer and the expand chevron — the chevron is chrome and stays real while the identity loads.</summary>
    static Element CompactRailStrip(FrameSpec spec, float stripW, Action expand, Element identity)
    {
        float pad = Spacing.S;
        float cover = CompactCover(stripW);
        Element expandHit = new BoxEl
        {
            Key = "compact:expand",
            Width = cover, Height = ClassicDetailLayout.CompactExpandHeight, Shrink = 0f,
            AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Corners = Radii.ControlAll, HoverFill = Tok.FillSubtleSecondary,
            Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand, OnClick = expand,
            Children = [Icon(Icons.ChevronRight, 14f, Tok.TextSecondary)],
        };
        Element strip = new BoxEl
        {
            Key = "detail-rail-compact",
            Direction = 1, Width = stripW, Shrink = 0f, Gap = Spacing.S,
            Padding = new Edges4(pad, Spacing.L, pad, Spacing.L),
            Fill = Tok.FillLayerDefault, ClipToBounds = true,
            Children =
            [
                identity,
                new BoxEl { Key = "compact:spacer", Grow = 1f, MinHeight = 0f },
                expandHit,
            ],
        };
        return ToolTip.Wrap(strip, spec.Identity.Title);
    }

    /// <summary>W25: the VERTICAL show header — the third composition, never the hero system. Cover 140 at saturation
    /// 1.0 (the one arm without the 1.18 boost), the PageHero title, Play + heart + a bare Share FAB, no toolbar. The
    /// podcast slots land as the prototype's narrow pane does: badges / rating beside the cover in the info column, the
    /// ledger and the CTA (a page's primary + satellites) full width under it, no topics and no blurb. The rows are the
    /// rail's own decision (<see cref="RailLayout.RowsFor"/>); every slot width here is NaN (fill the column).</summary>
    static Element ShowHeaderCore(FrameSpec spec, FrameActions acts, Func<ColorF> accent, Action play, float pageWidth)
    {
        const float coverSize = ClassicDetailLayout.ShowCover;
        // The info column's measure: the page less the header's two insets, the cover and the gap between. An unmeasured
        // page (NaN) keeps the old 600 DIP nominal.
        float infoW = float.IsFinite(pageWidth) ? MathF.Max(0f, pageWidth - 3f * Spacing.L - coverSize) : ClassicDetailLayout.ShowInfoWidth;
        var id = spec.Identity;
        var cfg = spec.Config;
        var slots = spec.Slots;
        Element[]? satellites = slots.Satellites?.Invoke();
        var presence = PresenceOf(slots, satellites?.Length ?? 0);
        bool lead = RailLayout.LeadsWithAttribution(id.Kind, presence);
        var rows = RailLayout.RowsFor(id.Kind, cfg.Badges, id.Eyebrow.Length > 0, id.OwnerName is { Length: > 0 },
            id.Artists is { Count: > 0 }, id.Meta is { Length: > 0 } || id.MetaLoading, fabs: 0, description: false,
            descriptionMaxLines: 0, presence);
        var info = new List<Element>(6);

        // The info column already clamps (Grow / Basis 0), so the eyebrow run needs no explicit width here.
        if (rows.Eyebrow)
            info.Add(ClassicDetailKit.LateRow("hdr:eyebrow", rows.Badges == RailBadgeRow.BesideEyebrow && slots.Badges is { } badgesBeside
                ? EyebrowWithBadges(id.Eyebrow, badgesBeside(), float.NaN)
                : ClassicDetailKit.EyebrowRun(id.Eyebrow)));
        if (rows.Owner)
            info.Add(lead && slots.Attribution is { } showLink
                ? ClassicDetailKit.LateRow("hdr:lead", showLink(ClassicDetailLayout.ShowInfoWidth))
                : ClassicDetailKit.LateRow("hdr:owner", slots.Attribution?.Invoke(ClassicDetailLayout.ShowInfoWidth) ?? OwnerBlock(id, ClassicDetailLayout.ShowInfoWidth)));
        if (rows.Badges == RailBadgeRow.OwnRow && slots.Badges is { } badgesOwn)
            info.Add(SlotRow("hdr:badges", badgesOwn(), RailLayout.BadgeHeight));

        info.Add(ClassicDetailKit.Row("hdr:title", slots.Title?.Invoke(ClassicDetailLayout.ShowTitleSize, ClassicDetailLayout.ShowTitleLine)
            ?? R.Type.PageHero(id.Title) with
            {
                Size = ClassicDetailLayout.ShowTitleSize, LineHeight = ClassicDetailLayout.ShowTitleLine, Weight = 600,
                // The detail frame's ONE trim rule: a title wraps, ellipsised only past the pathological cap.
                Wrap = TextWrap.WrapWholeWords, MaxLines = VerticalLayout.TitleLineCap, Trim = TextTrim.CharacterEllipsis,
            }));
        if (rows.Artists)
            info.Add(ClassicDetailKit.LateRow("hdr:artists", slots.Attribution?.Invoke(infoW)
                ?? (id.Artists is { Count: > 0 } billed ? BilledArtists(billed, infoW) : new BoxEl())));
        if (rows.Rating && slots.Rating is { } rating) info.Add(SlotRow("hdr:rating", rating(float.NaN), RailLayout.RatingHeight));
        if (rows.Meta) info.Add(ClassicDetailKit.LateRow("hdr:meta", MetaRow(id, float.NaN, maxLines: 1)));
        if (rows.Badges == RailBadgeRow.AfterMeta && slots.Badges is { } badgesLate)
            info.Add(SlotRow("hdr:badges", badgesLate(), RailLayout.BadgeHeight));

        var coverRow = new BoxEl
        {
            Key = "hdr:cover",
            Direction = 0, Gap = Spacing.L, AlignItems = FlexAlign.Center,   // centred: balanced, never a wedge
            Children =
            [
                ClickableCover(new BoxEl
                {
                    Width = coverSize, Height = coverSize, Corners = CornerRadius4.All(Radii.Card),
                    Shadow = Elevation.Card, ClipToBounds = true, Shrink = 0f,
                    Draggable = acts.CoverDrag is { } drag ? Drag.Source(drag) : null,
                    Children = [slots.Cover?.Invoke(coverSize)
                        ?? Controls.Artwork(id.CoverUrl, coverSize, coverSize, Radii.Card, decodePx: RailCoverDecodePx)],
                }, acts.CoverClick),
                new BoxEl { Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Gap = Spacing.XS, Children = info.ToArray() },
            ],
        };

        // The one fixed CTA cluster that differs: no More, a bare Share FAB. A page's primary replaces Play; its
        // satellites replace the heart + Share and wrap one by one.
        Element primary = slots.Primary is { } primaryOf ? primaryOf(accent) : Controls.ContextPlayButton(spec.Identity.Subject.Text, accent, play);
        Element ctaRow;
        if (satellites is not null)
            ctaRow = SatelliteCta("hdr:play", primary, satellites, topMargin: 0f);
        else
        {
            var cta = new List<Element>(3) { primary };
            if (cfg.Heart != HeartMode.None)
            {
                string saveUri = Detail.SaveTargetOf(id).Text;
                string saveName = id.Title;
                cta.Add(Embed.Comp(() => new Controls.SaveButton { Uri = saveUri, Name = saveName, Glyph = 16f, Box = RailFabSize, Accent = accent })
                    with { Key = "save:" + saveUri });
            }
            if (Detail.ShareActionFor(id) is { } share) cta.Add(Controls.Named(Detail.Fab(Icons.Share, share), Loc.Get(Strings.Menu.Share)));
            ctaRow = new BoxEl
            {
                Key = "hdr:play", Layout = ClassicDetailKit.Shove,
                Direction = 0, Gap = Spacing.M, AlignItems = FlexAlign.Center, Wrap = true,
                Children = cta.ToArray(),
            };
        }

        var headerKids = new List<Element>(5) { coverRow };
        // The ledger spans the header (the narrow pane's full-basis row), above the CTA.
        if (rows.Ledger && slots.Ledger is { } ledger) headerKids.Add(SlotRow("hdr:ledger", ledger(float.NaN), RailLayout.LedgerHeight));
        headerKids.Add(ctaRow);
        if (id.UpcomingAtUnixSeconds > 0 && slots.PreRelease is { } pre) headerKids.Add(ClassicDetailKit.LateRow("hdr:prerelease", pre()));
        if (cfg.Badges == BadgeStyle.TypeYear && slots.ReleasePanel is { } release) headerKids.Add(ClassicDetailKit.ReleaseRow("hdr:release", release(false)));

        return new BoxEl
        {
            Direction = 1, Gap = Spacing.M, Shrink = 0f,
            Padding = new Edges4(Spacing.L, Spacing.L, Spacing.L, Spacing.S),
            Children = headerKids.ToArray(),
        };
    }

    // ── shared row bodies (rail, show header, hero) ──

    /// <summary>The "N songs · 2 hr 59 min" caption — or, while the count is still loading, a shimmer bar of that
    /// caption's SHAPE in the same slot ("0 songs · 1 min" is a count the page does not have).</summary>
    static Element MetaRow(Identity id, float width, int maxLines)
        => MetaLine(id, width, maxWidth: float.NaN, maxLines);

    /// <summary>The meta caption: ONE TrackMeta run ("2 songs · 6 min"), whole-word wrapped to
    /// <paramref name="maxLines"/> — or its loading shimmer. An album's year is not on it (the eyebrow and the release
    /// panel carry it).</summary>
    static Element MetaLine(Identity id, float width, float maxWidth, int maxLines)
    {
        if (!id.MetaLoading && id.Meta is { Length: > 0 } meta)
        {
            var line = R.Type.TrackMeta(meta) with
            {
                MaxLines = maxLines, Wrap = TextWrap.WrapWholeWords, Trim = TextTrim.CharacterEllipsis,
            };
            if (!float.IsNaN(width)) line = line with { Width = width };
            if (!float.IsNaN(maxWidth)) line = line with { MaxWidth = maxWidth };
            return line;
        }
        return new SkelRegionEl(
            Pending: s_true, Failed: s_false, Content: s_metaShimmer, ShimmerSource: null, OnFailed: null,
            Reveal: SkelReveal.FadeOnly, Style: SkeletonStyle.Default, Group: null, SmoothResize: false);
    }

    static readonly Func<bool> s_true = static () => true;
    static readonly Func<bool> s_false = static () => false;
    static readonly Func<Element> s_metaShimmer = static () => R.Type.TrackMeta(MetaShimmerText) with { MaxLines = 1 };

    /// <summary>The chart playlist's "N new entries · date" caption (W28) — the page's slot, else the default caption.</summary>
    static Element ChartCaption(Identity id, FrameSlots slots)
    {
        if (slots.Chart is { } chart) return chart();
        return Ui.Caption(DetailVerbs.ChartCaptionText(id)) with { Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis };
    }

    /// <summary>The default owner row (a playlist page supplies its inline-edit owner block through the Attribution
    /// slot): the collaborator pile when it applies — WIDTH-keyed, the one geometry-keyed remount — else the owner.</summary>
    static Element OwnerBlock(Identity id, float width)
    {
        if (id.Collaborators is { Count: > 0 } collaborators && Text.ShowCollaborators(collaborators.Count, id.Collaborative))
            return new BoxEl
            {
                Key = "pl-collab:" + ((int)width).ToString(CultureInfo.InvariantCulture),
                Direction = 0, AlignItems = FlexAlign.Center, MaxWidth = width,
                Children = [Controls.FacePile(collaborators)],
            };
        string owner = id.OwnerName ?? "";
        // The owner opens their profile (#161): the avatar + name run is the link (accent on hover), inert when the
        // page carries no owner uri (a local playlist's "On this device").
        Action? openProfile = DetailVerbs.OpenOwner(id.Owner, owner);
        bool go = openProfile is not null;
        return new BoxEl
        {
            Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, MaxWidth = width,
            Role = go ? AutomationRole.Button : AutomationRole.None, Focusable = go,
            Cursor = go ? CursorId.Hand : null, OnClick = openProfile,
            Children =
            [
                PersonPicture.Create("", ClassicDetailLayout.OwnerAvatar, displayName: owner, imageSourcePath: id.OwnerImageUrl),
                new TextEl(owner)
                {
                    Size = ClassicDetailLayout.AttributionSize, LineHeight = ClassicDetailLayout.AttributionLine, Weight = 600, Color = Tok.TextSecondary,
                    HoverColor = go ? Tok.AccentTextPrimary : default, BrushTransitionMs = Design.Motion.Faster,
                    MinWidth = 0f, Shrink = 1f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                },
            ],
        };
    }

    /// <summary>The default billed-artist line, for a page that declares no Attribution slot: the identity's artists
    /// under the rail's width pressure. ONE trigger holds it all: it opens the every-artist menu, or goes straight to the
    /// artist when there is only one.</summary>
    static Element BilledArtists(IReadOnlyList<Controls.Face> artists, float width)
        => Embed.Comp(new RailArtistLineProps(artists, width), static () => new RailArtistLineView())
            with { Key = "rail:artist-line" };

    sealed record RailArtistLineProps(IReadOnlyList<Controls.Face> Artists, float Width);

    /// <summary>Hosts <see cref="ClassicDetailKit.ArtistLineBox"/> for the default line: <see cref="RailArtistLine.Mode"/> needs the
    /// PREVIOUS mode for its hysteresis, so the mode is a plain field of the component.</summary>
    sealed class RailArtistLineView : Component
    {
        static readonly Func<Element> s_none = static () => new BoxEl();

        RailArtistMode? _mode;

        public override Element Render()
        {
            var p = UseProps<RailArtistLineProps>();
            var overlay = UseContext(Overlay.Service);
            var anchor = UseRef<NodeHandle>(default);
            var handle = UseRef<OverlayHandle?>(null);
            var artists = p.Artists;
            if (artists.Count == 0) return s_none();

            var mode = RailArtistLine.Mode(p.Width, _mode);
            _mode = mode;

            bool menu = artists.Count > 1;
            var names = new System.Text.StringBuilder(64);
            var faces = new List<Controls.Face>(artists.Count);
            for (int i = 0; i < artists.Count; i++)
            {
                if (i > 0) names.Append(", ");
                names.Append(artists[i].Name);
                faces.Add(new Controls.Face(artists[i].Name, artists[i].ImageUrl));   // no click of its own: the line is the trigger
            }

            void Toggle()
            {
                if (Controls.IsNullOverlay(overlay)) return;
                if (handle.Value is { IsOpen: true } open) { open.Close(); return; }
                var opened = overlay.Open(() => anchor.Value, () => MenuFlyout.Create(MenuItems(artists), () => handle.Value?.Close()),
                    FlyoutPlacement.BottomEdgeAlignedLeft,
                    new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss) { ConstrainToRootBounds = false });
                handle.Value = opened;
                opened.ClosedAction = () => handle.Value = null;
            }

            return ClassicDetailKit.ArtistLineBox(mode, new ArtistLineSpec(
                faces, names.ToString(), artists.Count, RailArtistLine.PlusN(artists.Count, RailArtistLine.WideFaces), p.Width,
                Loc.Get(Strings.Detail.Rail.ArtistsMenu), menu, menu ? Toggle : artists[0].OnClick,
                menu ? e => { if (e.KeyCode is Keys.Down or Keys.F4) { Toggle(); e.Handled = true; } } : null,
                n => anchor.Value = n));
        }

        static MenuFlyoutItem[] MenuItems(IReadOnlyList<Controls.Face> artists)
        {
            var items = new MenuFlyoutItem[artists.Count];
            for (int i = 0; i < items.Length; i++)
                items[i] = new MenuFlyoutItem(artists[i].Name, Icons.Contact, artists[i].OnClick is not null, artists[i].OnClick);
            return items;
        }
    }
}
