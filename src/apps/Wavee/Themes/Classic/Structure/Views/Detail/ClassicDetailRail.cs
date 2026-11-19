// ── Themes/Classic/Structure/Views/Detail/ClassicDetailRail.cs — Classic's detail rail and collapsed strip (themes plan 6.1, T9a) ──
//
// From `Entities\Detail.UI.cs` §4 (the rail family): the rail's frame, its loaded column and its loading twin (one skeleton boundary), the
// collapsed 96-DIP identity strip, and the row bodies they share with the hero (the meta line, the chart caption,
// the default owner row, the billed-artist line). Moved verbatim with the pictures; what DECIDES stays core: the row presence (`RailLayout`,
// `Skeleton.RailPlanFor`), the title rule (`VerticalLayout.RailTitleTypeFor`), the rung that picks the title cap and the description clamp
// (the host), the accent, the play-all cell and every page slot builder.
//
// The page-facing helpers (`Satellite`, `MoreButton`, `ArtistLine` and the share / save targets) STAY in `Detail`: the pages, the library
// panes and the artist page build their own pieces from them (T10 moves them with those pages). This file calls them through `Detail`. The keyed
// rail rows (`Row` / `LateRow` / `ReleaseRow`), the eyebrow run and the billed-artist line box moved to the shared kit (`ClassicDetailKit.cs`, T9b),
// T9c dropped `Detail.PlayButton`, and the notice strip moved to `ClassicDetailSeam.cs`.
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
using Wavee.Theming;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;
using Config = Wavee.Detail.Config;
using FrameActions = Wavee.Detail.FrameActions;
using FrameSlots = Wavee.Detail.FrameSlots;
using FrameSpec = Wavee.Detail.FrameSpec;
using Identity = Wavee.Detail.Identity;
using RailArtistLine = Wavee.Detail.RailArtistLine;
using RailArtistMode = Wavee.Detail.RailArtistMode;
using RailLayout = Wavee.Detail.RailLayout;
using RailSlotSet = Wavee.Detail.RailSlotSet;
using Skeleton = Wavee.Detail.Skeleton;
using Text = Wavee.Detail.Text;
using VerticalLayout = Wavee.Detail.VerticalLayout;

namespace Wavee.Themes.Classic;

/// <summary>Classic's detail frame: the scaffold (<see cref="Template"/>), the rail, the collapsed strip, the hero and its loading band
/// (this class is split over <c>ClassicDetailFrame.cs</c>, <c>ClassicDetailRail.cs</c> and <c>ClassicDetailHero.cs</c>).</summary>
internal static partial class ClassicDetailFrame
{
    // The rail's own pictures' numbers (Rules\DetailLayout.cs); its padding and row gap are the window tier's (`ClassicDetailTier.Rail`, Tiers.Detail.cs).
    const float RailFabSize = RailLayout.FabSize;                     // 32 (Workstream B: was 40)
    const int RailCoverDecodePx = ClassicDetailLayout.CoverDecodePx;  // the shelf card's bucket — a warm texture on arrival
    const string MetaShimmerText = ClassicDetailLayout.MetaShimmerText;   // the shimmer SHAPE only; never painted as text

    /// <summary><c>DetailRail.CoverEdge</c>: the rail cover fills the column less the tier's side padding, floored at 80.</summary>
    static float RailCoverEdge(float railW, ClassicDetailRail rail) => MathF.Max(ClassicDetailLayout.RailCoverMin, rail.ContentWidth(railW));

    // ── the templates ──

    /// <summary>The registered template of <see cref="ViewKeys.DetailRail"/>: the expanded rail, one <c>SkelRegionEl</c> pending on the header
    /// (<see cref="Identity.HeaderPending"/>), whose shimmer is derived from a column of the SAME rows the loaded rail lays out
    /// (<see cref="RailSkeletonColumn"/> mirrors <see cref="RailColumn"/> row for row), cross-dissolving once — the table's contract (FadeOnly, no
    /// resize tween). The frame (layer fill + scroller) sits OUTSIDE the boundary so the fill never blinks. The content thunk closes over this
    /// render's spec; a later push re-renders the frame and the region refreshes its Ready branch in place, so the rail keeps following the header
    /// (meta, description, a rename) after the reveal.</summary>
    public static Element RailTemplate(DetailRailProps p, in ViewContext ctx)
    {
        // A docked aside holds About this release (C-Album-Huge, contract 1.21): the rail leaves its release row out, loaded and shimmering alike.
        var spec = ctx.Hooks.UseContext(Detail.AsideDocked) && p.Spec.Slots.ReleasePanel is not null
            ? p.Spec with { Slots = p.Spec.Slots with { ReleasePanel = null } }
            : p.Spec;
        // The window tier's padding, row gap and CTA break (C-Album-Small): a crossing re-renders the rail and its rows shove into place.
        var rail = ClassicDetailTiers.Of(ctx.Tier()).Rail;
        return RailFrame(p.RailWidth, new SkelRegionEl(
            Pending: p.Pending, Failed: s_false,
            Content: () => RailColumn(spec, p.Actions, p.RailWidth, rail, p.TitleSize, p.DescriptionLines, p.Accent, p.Play, p.Shown),
            ShimmerSource: () => RailSkeletonColumn(spec.Identity, spec.Config, p.Actions, p.RailWidth, rail, p.TitleSize, p.DescriptionLines),
            OnFailed: null, Reveal: SkelReveal.FadeOnly, Style: Design.Skeleton, Group: null, SmoothResize: false));
    }

    /// <summary>The registered template of <see cref="ViewKeys.DetailCompactRail"/>: the collapsed strip's identity (cover + title) behind the same
    /// boundary; the expand chevron stays real.</summary>
    public static Element CompactRailTemplate(DetailCompactRailProps p, in ViewContext ctx)
    {
        float cover = CompactCover(p.StripWidth);
        return CompactRailStrip(p.Spec.Identity.Title, p.StripWidth, p.Expand, new SkelRegionEl(
            Pending: p.Pending, Failed: s_false,
            Content: () => CompactRailIdentity(p.Spec.Identity, p.Spec.Slots, cover, p.Expand),
            ShimmerSource: () => CompactRailSkeleton(p.Spec.Identity.CoverUrl, cover),
            OnFailed: null, Reveal: SkelReveal.FadeOnly, Style: Design.Skeleton, Group: null, SmoothResize: false));
    }

    // ── the rail family ──

    /// <summary>The rail's frame: the layer fill and, the LAST resort once the text has given, the rail's own scroller. Holds the
    /// loaded column, or the skeleton boundary that swaps to it (<see cref="RailTemplate"/>) — outside the boundary, so the fill
    /// never blinks. (Liked Songs, whose rail had no fill, has no rail: tuned Classic D16.)
    /// <para>It GROWS to the column's height rather than leaning on the row's stretch: the frame mounts it through the view slot's
    /// two component anchors, and a nested anchor is a default column, so the row's cross-axis stretch reaches only the outer
    /// anchor. Without its own grow the frame measured its content — a scroller with no minimum height, so 0 — and the whole rail
    /// was clipped away (the blank rail).</para></summary>
    static Element RailFrame(float railW, Element body) => new BoxEl
    {
        Direction = 1, Width = railW, Shrink = 0f, Grow = 1f, MinHeight = 0f, ClipToBounds = true,
        Fill = Tok.FillLayerDefault,
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

    /// <summary>The rail's loaded column: cover · eyebrow / owner · title · attribution · meta · daylist · chart · CTA (the Play split
    /// and the fixed group) · prerelease · release panel · description (the facts bento is the insights sheet's, I1). The identity rows are
    /// <see cref="RailLayout.RowsFor"/>'s decisions; <see cref="RailSkeletonColumn"/> reserves the same rows
    /// (<see cref="Skeleton.RailPlanFor"/>) in the same order at the same widths, so the swap is a dissolve and never a
    /// reflow.</summary>
    static Element RailColumn(FrameSpec spec, FrameActions acts, float railW, ClassicDetailRail rail, float titleSize,
                              int descMaxLines, Func<ColorF> accent, Action play, IReadSignal<Detail.ListCount>? shown)
    {
        var id = spec.Identity;
        var cfg = spec.Config;
        var slots = spec.Slots;
        float cover = RailCoverEdge(railW, rail);
        ColorF accentColor = accent();
        var kids = new List<Element>(16);

        // The CTA's pieces first: how many actions it carries is a row decision (it sizes the CTA's lines) as well as its
        // children (tuned Classic D1: Shuffle · heart · Share · ⋯).
        List<Element> fabs = ClassicDetailKit.FixedActions("rail:", id, cfg.Heart != HeartMode.None, acts, accent);
        var presence = PresenceOf(slots);
        bool blurb = (id.EditableMetadata && slots.Description is not null) || id.DescriptionHtml is { Length: > 0 };
        var rows = RailLayout.RowsFor(cfg.Badges, id.Eyebrow.Length > 0, id.OwnerName is { Length: > 0 },
            id.Artists is { Count: > 0 }, id.Meta is { Length: > 0 } || id.MetaLoading, fabs.Count, blurb, descMaxLines,
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

        // The lead. The eyebrow asymmetry (W27): the rail shows it for TypeYear only; OwnerRow gets the owner block instead.
        if (rows.Eyebrow)
            kids.Add(ClassicDetailKit.LateRow("rail:eyebrow", ClassicDetailKit.EyebrowRun(id.Eyebrow) with { Width = cover }));
        if (rows.Owner)
            kids.Add(ClassicDetailKit.LateRow("rail:owner", slots.Attribution?.Invoke(cover) ?? OwnerBlock(id, cover)));

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

        // Attribution: an album's billed artists (or its Attribution slot).
        if (rows.Artists)
            kids.Add(ClassicDetailKit.LateRow("rail:artists", slots.Attribution?.Invoke(cover)
                ?? (id.Artists is { Count: > 0 } billed ? BilledArtists(billed, cover) : new BoxEl())));

        // Meta: playlist / Liked. While a find or a filter narrows the list it reads "3 of 48 songs · 11 min (filtered)"
        // (tuned Classic D4, D-10) — its own component, so a keystroke re-renders one line.
        if (rows.Meta)
            kids.Add(ClassicDetailKit.LateRow("rail:meta", shown is null
                ? MetaRow(id, cover, maxLines: 2)
                : Embed.Comp(new RailMetaProps(id, cover), () => new RailMetaLine(shown))));

        if (id.DaylistExpiresAtMs > 0 && slots.Pulse is { } pulse) kids.Add(ClassicDetailKit.LateRow("rail:daylist", pulse()));
        if (id.ChartNewEntries > 0) kids.Add(ClassicDetailKit.LateRow("rail:chart", ChartCaption(id, slots)));

        // CTA cluster: the Play split (D1/D2), then the fixed actions, all wrapping one by one.
        Element primary = ClassicDetailKit.PlaySplit(id.Subject, accent, play);
        kids.Add(ClassicDetailKit.ActionRow("rail:cta", primary, fabs, RailLayout.CtaTopMargin, rail.BreakAfterPrimary ? cover : 0f));

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

        return RailColumnBox(railW, rail, kids.ToArray());
    }

    static Element RailColumnBox(float railW, ClassicDetailRail rail, Element[] rows) => new BoxEl
    {
        Direction = 1, Gap = rail.Gap, Width = railW, Shrink = 0f,
        Padding = rail.Padding,   // Standard 24 / 8 / 24 / 16 (tuned Classic F3); Small 16 / 12 / 16 / 12 (C-Album-Small)
        Children = rows,
    };

    /// <summary>The rail-row presence a page's slots declare — <see cref="RailLayout.RowsFor"/>'s input.</summary>
    static RailSlotSet PresenceOf(FrameSlots s) => new(Attribution: s.Attribution is not null);

    /// <summary>The rail's LOADING column — the shimmer source of <see cref="RailTemplate"/>, derived by the engine into
    /// bars: the same rows as <see cref="RailColumn"/> (<see cref="Skeleton.RailPlanFor"/> decides which, per kind and
    /// badge style) at the same widths, gap and padding — its nominal height is
    /// <see cref="RailLayout.HeightOf"/> of that plan. Unfilled boxes; a cover already known (a shelf card's 256 bucket)
    /// is painted for real and exempted from the deriver, as the hero's skeleton does. The CTA reserves exactly the buttons
    /// <see cref="ClassicDetailKit.FixedActions"/> will lay out (<see cref="ClassicDetailKit.FixedActionCount"/>, tuned Classic X2): a page with no
    /// web link (a local album or playlist) gets no Share square, one without a heart or a menu none for those.</summary>
    static Element RailSkeletonColumn(Identity id, in Config cfg, FrameActions acts, float railW, ClassicDetailRail rail, float titleSize,
        int descMaxLines)
    {
        float cover = RailCoverEdge(railW, rail);
        var plan = Skeleton.RailPlanFor(id.Kind, cfg.Badges, ClassicDetailKit.FixedActionCount(id, cfg.Heart != HeartMode.None, acts), descMaxLines);
        var kids = new List<Element>(12) { SkeletonCover(id.CoverUrl, cover) };

        if (plan.Eyebrow) kids.Add(Bar(Skeleton.BarWidth(cover, Skeleton.EyebrowFraction), VerticalLayout.EyebrowRowHeight));
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
        if (plan.Meta) kids.Add(Bar(Skeleton.BarWidth(cover, Skeleton.MetaFraction), VerticalLayout.MetaRowHeight));

        var fabs = new Element[Math.Max(0, plan.Fabs)];
        for (int i = 0; i < fabs.Length; i++)
            fabs[i] = new BoxEl { Width = RailFabSize, Height = RailFabSize, Shrink = 0f, Corners = Radii.ControlAll };
        // The primary's shape: the Play split with its trailing margin (RailLayout.CtaHeight's).
        Element pill = new BoxEl
        {
            Width = RailLayout.PlaySplitWidth, Height = Controls.ButtonHeight, Shrink = 0f, Corners = Radii.ControlAll,
            Margin = new Edges4(0f, 0f, RailLayout.SplitTrail, 0f),
        };
        kids.Add(ClassicDetailKit.ActionRow("skel:cta", pill, fabs, RailLayout.CtaTopMargin, rail.BreakAfterPrimary ? cover : 0f));
        if (plan.DescriptionLines > 0)
            kids.Add(Lines(cover, VerticalLayout.DescriptionLineHeight, plan.DescriptionLines, Skeleton.DescriptionLastLineFraction));

        return RailColumnBox(railW, rail, kids.ToArray());
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
    static Element CompactRailSkeleton(string? coverUrl, float cover) => new BoxEl
    {
        Direction = 1, Gap = Spacing.S, Width = cover,
        Children = [SkeletonCover(coverUrl, cover), Lines(cover, 16f, Skeleton.RailTitleLines, Skeleton.TitleLastLineFraction)],
    };

    /// <summary>The strip's identity: the cover (a hit target that expands) and the two-line title.</summary>
    static Element CompactRailIdentity(Identity id, FrameSlots slots, float cover, Action expand)
    {
        Element coverHit = new BoxEl
        {
            Key = "compact:cover",
            Width = cover, Height = cover, Corners = CornerRadius4.All(Radii.Card),
            Shadow = Elevation.Card, ClipToBounds = true, Shrink = 0f,
            Role = AutomationRole.Button, Cursor = CursorId.Hand, OnClick = expand,
            Children = [slots.CompactCover?.Invoke(cover)
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
    /// the spacer and the expand chevron — the chevron is chrome and stays real while the identity loads. A one-tree page's frame paints the rail's
    /// fill under the strip itself (<paramref name="fill"/> false: one ground for the rail and the strip).</summary>
    static Element CompactRailStrip(string title, float stripW, Action expand, Element identity, bool fill = true)
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
            Fill = fill ? Tok.FillLayerDefault : ColorF.Transparent, ClipToBounds = true,
            Children =
            [
                identity,
                new BoxEl { Key = "compact:spacer", Grow = 1f, MinHeight = 0f },
                expandHit,
            ],
        };
        // Grown to the column's height for the same reason as RailFrame (the slot's nested anchor is a column): the spacer can then
        // push the expand chevron to the strip's foot.
        return ToolTip.Wrap(strip, title, grow: 1f);
    }

    // ── shared row bodies (rail, hero) ──

    /// <summary>The "N songs · 2 hr 59 min" caption — or, while the count is still loading, a shimmer bar of that
    /// caption's SHAPE in the same slot ("0 songs · 1 min" is a count the page does not have).</summary>
    static Element MetaRow(Identity id, float width, int maxLines)
        => MetaLine(id, width, maxWidth: float.NaN, maxLines);

    sealed record RailMetaProps(Identity Id, float Width, float MaxWidth = float.NaN, int Lines = 2);

    /// <summary>The rail's count line (tuned Classic D4): the page's meta, or — while the list's find or filter narrows it — "3 of 48 songs ·
    /// 11 min" and the quiet "(filtered)" mark (<see cref="Detail.Text.FilteredMeta"/>), in the same slot at the same width.</summary>
    sealed class RailMetaLine(IReadSignal<Detail.ListCount> shown) : Component
    {
        public override Element Render()
        {
            var p = UseProps<RailMetaProps>();
            var count = shown.Value;
            if (Detail.Text.FilteredMeta(in count) is not { } filtered) return MetaLine(p.Id, p.Width, maxWidth: p.MaxWidth, maxLines: p.Lines);
            return new BoxEl
            {
                Direction = 0, Wrap = true, Width = p.Width, MaxWidth = p.MaxWidth, Gap = Spacing.XS, AlignItems = FlexAlign.Center,
                Children =
                [
                    R.Type.TrackMeta(filtered.Count) with { MaxLines = 1, Trim = TextTrim.CharacterEllipsis, Shrink = 1f, MinWidth = 0f },
                    R.Type.TrackMeta(filtered.Mark) with { MaxLines = 1, Color = Tok.TextTertiary, Shrink = 0f },
                ],
            };
        }
    }

    /// <summary>The meta caption: ONE TrackMeta run ("2 songs · 6 min"), whole-word wrapped to
    /// <paramref name="maxLines"/> — or its loading shimmer. An album's year is not on it (the eyebrow and the release
    /// panel carry it).
    /// <para>The line FILLS ONCE (the owner's recording, 2026-11-15: "50 songs · 293.7K saves", then "· 2 hr 42 min" appended a frame later): while
    /// a part of it is still on its way (the count held with the list, <c>MetaLoading</c>; the duration or the owner, <c>MetaSettling</c>) it stays on
    /// its shimmer, then the whole line crossfades in over it. ONE region in both states, so the swap is the region's cross-dissolve, never a cut.</para></summary>
    static Element MetaLine(Identity id, float width, float maxWidth, int maxLines)
    {
        bool ready = !id.MetaLoading && !id.MetaSettling && id.Meta is { Length: > 0 };
        Func<Element> content = s_metaShimmer;
        if (ready)
        {
            var line = R.Type.TrackMeta(id.Meta!) with
            {
                MaxLines = maxLines, Wrap = TextWrap.WrapWholeWords, Trim = TextTrim.CharacterEllipsis,
            };
            if (!float.IsNaN(width)) line = line with { Width = width };
            if (!float.IsNaN(maxWidth)) line = line with { MaxWidth = maxWidth };
            content = () => line;
        }
        return new SkelRegionEl(
            Pending: ready ? s_false : s_true, Failed: s_false, Content: content, ShimmerSource: s_metaShimmer, OnFailed: null,
            Reveal: SkelReveal.FadeOnly, Style: Design.Skeleton, Group: null, SmoothResize: true);
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
                var opened = overlay.Open(() => anchor.Value, () => MenuFlyout.Create(MenuItems(artists), () => handle.Value?.Close(), size: OverlayFit.MenuSize),
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
