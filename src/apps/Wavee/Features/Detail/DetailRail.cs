using System;
using System.Collections.Generic;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;
using FluentGpu.Localization;
using Wavee.Core;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

// The fixed-width left metadata rail (album / single / playlist) in its own vertical scroller. Stack order:
// cover → eyebrow → big hero title → owner/artist row → meta line → CTA cluster → tools/actions → description.
// Every clamped run gets an EXPLICIT Width (= cover edge) so a long title/owner never widens the rail (MediaCard's
// discipline). The cover edge is a constant per config (RailWidth − side padding), so no SizeChanged hack is needed.
static class DetailRail
{
    const float SidePadL = Spacing.L;   // 16
    const float SidePadR = Spacing.S;   // 8
    const float FabSize = 40f;
    // Decode the rail/header cover at the SAME size the Home shelf card uses (MediaCard's ShelfDecodePx, 256) so a Hero
    // fly hands the cover off to the SAME cached texture — pixel-identical, with NO fresh first-visit cover decode (the
    // cold connected-animation spike). Displayed larger (the ~300px rail cover) is a slight, imperceptible upscale.
    const int HeroCoverDecodePx = 256;

    // ── THE PREVIEW → FULL MODEL SWAP: stable row identity, then calm motion ──────────────────────────────────────
    //
    // A detail page renders TWICE per open. The nav hand-off supplies a PARTIAL preview model (cover · title · artist)
    // so the page can paint on the click frame; the full model lands a moment later. The rows below are CONDITIONAL —
    // the eyebrow, the owner block, the artist face pile, the meta line, the daylist strip and the description exist
    // only once the full model is in — so the second render INSERTS rows into the middle of the column.
    //
    // The reconciler matches UNKEYED siblings by POSITION + TYPE (Reconciler.ReconcileChildrenCore). Unkeyed, that
    // insert used to UPDATE the title's TextEl into the newly-first eyebrow run and mount a second TextEl below it for
    // the title: the hero visibly relabelled itself for a frame and every row under it jumped. So every structural row
    // carries a STABLE key — constant across preview→full, and deliberately free of model text (the `save:` /
    // `daylist:` / `prerelease:` keys elsewhere in this file are the opposite case: they encode identity BECAUSE those
    // components must remount when it changes).
    //
    // Keys alone fix the relabel; the two specs below calm the shove that is left. The engine bakes Enter/Exit/Layout
    // for BoxEl ONLY (Reconciler, `case BoxEl`) — on a bare TextEl/ComponentEl they are silently dropped — which is
    // why the non-box rows go through the wrapper helpers rather than carrying the fields themselves.
    //
    // Reduced motion is NOT branched here (the canon rule, see WaveeMotion.cs): the structural seed consults
    // ReducedMotionPolicy.KeepFade centrally — the fade survives, the travel snaps.

    /// <summary>A row that arrives with the FULL model fades up from nothing instead of blinking into place. Folded
    /// into <see cref="Shove"/> by the reconciler (SynthesizeDeclarative merges Element.Enter into Element.Layout), so
    /// the entrance and the FLIP share one set of dynamics and cannot drift apart.</summary>
    internal static readonly EnterExit FadeUp = new(Opacity: 0f, Active: true);

    /// <summary>Position-only FLIP: a row pushed down by a late-arriving sibling travels from its old origin instead of
    /// teleporting. Position only — the rows own their sizes, and animating those would re-wrap live text.</summary>
    internal static readonly LayoutTransition Shove = new(
        TransitionChannels.Position, TransitionDynamics.Tween(Expressive.Fast, Easing.SmoothOut));

    /// <summary>A keyed rail row that exists in BOTH models: it only ever gets pushed, so it takes the FLIP alone.
    /// The wrapper is a plain <c>Direction = 1</c> box, whose default <c>AlignItems = Stretch</c> hands the child the
    /// same content width it had as a direct child of the rail column — geometry is unchanged.</summary>
    static Element Row(string key, Element child) => new BoxEl
    {
        Key = key, Direction = 1, Layout = Shove, Children = [child],
    };

    /// <summary>A keyed rail row that can arrive LATE (full-model-only): fades up on insert, FLIPs on later shoves.</summary>
    static Element LateRow(string key, Element child) => new BoxEl
    {
        Key = key, Direction = 1, Layout = Shove, Enter = FadeUp, Children = [child],
    };

    public static float CoverEdge(float railW) => MathF.Max(80f, railW - SidePadL - SidePadR);

    internal static Element HeroArtwork(DetailModel m, float size, float radius = Radii.Card, bool connected = true,
                                        float saturation = 1f, string? morphKey = null,
                                        int decodePx = HeroCoverDecodePx, bool preferLargest = false) =>
        LikedSongsArtwork.IsLikedUri(m.ContextUri) && m.Cover is null
            ? LikedSongsArtwork.Cover(size, radius, morphKey ?? (connected ? m.MorphKey : null))
            : Surfaces.Artwork(m.Cover, m.Title.GetHashCode() & 0x7fffffff, size, size, radius,
                morphKey ?? (connected ? m.MorphKey : null), decodePx: decodePx, saturation: saturation,
                preferLargest: preferLargest);

    /// <summary>Whether this page's cover is the Liked collection's — i.e. the slot the DYNAMIC treatment
    /// (<see cref="LikedSongsArtwork.Dynamic"/>) owns instead of the bundled PNG. <c>Cover is null</c> is the router,
    /// exactly as it is in <see cref="HeroArtwork"/> above (<c>DetailPage.MapLiked</c> sets it null on purpose).
    ///
    /// <para>The branch lives at the CALL SITES rather than inside <see cref="HeroArtwork"/> for one deliberate
    /// reason: <c>DetailTracks.PreviewHeroArt</c> also routes through <see cref="HeroArtwork"/>, and the loading
    /// skeleton must keep painting the stock PNG (E2). The skeleton deriver walks a STATIC element tree, and a
    /// hook-bearing component inside it is untested territory; the liked skeleton is cold-open-only, so a one-frame
    /// stock→treatment cross on the first ever open is honest where a blank hero would not be.</para></summary>
    internal static bool IsDynamicLikedCover(DetailModel m)
        => LikedSongsArtwork.IsLikedUri(m.ContextUri) && m.Cover is null;

    // The side rail: the cover STRETCHES to fill the column width (a big hero — the image is NEVER shrunk for height).
    // The height fit comes from the TEXT — titleSize (the shell lowers it on a short rail; auto-fits down to 18px) and
    // the description's line cap (descMaxLines) — and only then the rail's own scrollbar (last resort).
    public static Element Build(DetailModel m, DetailConfig cfg, DetailHandlers h, float railW, float titleSize, float titleLineHeight, int descMaxLines, Loadable<DetailModel> modelSource, ActionServices? acts = null)
    {
        float cover = CoverEdge(railW);
        var kids = new List<Element>(10);

        // Cover — click-to-change when metadata is editable; static art otherwise.
        bool editable = PlaylistInlineEdit.EditableMetadata(m) && m.ContextUri is { Length: > 0 };
        // DIAGNOSTIC ONLY (see DetailCoverTrace): the WIDE two-column arm — the one that does NOT flash. Its lines are
        // the control group for the vertical hero's; the decodePx here is the HeroArtwork default (HeroCoverDecodePx).
        if (DetailCoverTrace.On)
            WaveeLog.Instance.Debug("detail", "cover", "rail-build",
                WaveeLogField.Of("arm", "rail"),
                WaveeLogField.Of("ctx", m.ContextUri),
                WaveeLogField.Of("editable", editable),
                WaveeLogField.Of("railW", railW),
                WaveeLogField.Of("coverEdge", cover),
                WaveeLogField.Of("decodePx", HeroCoverDecodePx),
                WaveeLogField.Of("saturation", 1.18),
                WaveeLogField.Of("cover", DetailCoverTrace.Id(m.Cover)),
                WaveeLogField.Of("state", ((LoadState)modelSource.State.Peek()).ToString()),
                WaveeLogField.Of("morphKey", m.MorphKey));
        // No motion on the cover: it is the column's ANCHOR (row 0), so nothing can ever push it and an entrance on the
        // page's largest object is exactly the flash this pass removes. Key only.
        kids.Add(new BoxEl
        {
            Key = "rail:cover",
            Width = cover, Height = cover, Corners = CornerRadius4.All(Radii.Card),
            Shadow = Elevation.Card, ClipToBounds = true,
            // The cover drags the whole entity. On the FRAMING box, not on the editable cover inside it, so the
            // file-drop target that cover owns stays untouched (see WaveeDetailDrag.Hero).
            Draggable = WaveeDetailDrag.Hero(m, acts),
            // Three arms, in precedence order: an editable playlist cover, the LIKED collection's dynamic treatment
            // with its style picker, and every other page's static art. Only the detail surfaces carry the picker —
            // the Home/shelf/sidebar liked cards get the bare Dynamic (see LikedCoverPicker's note).
            Children =
            [
                editable ? PlaylistInlineEdit.Cover(modelSource, cover, saturation: 1.18f)
                : IsDynamicLikedCover(m) ? LikedCoverPicker.Cover(cover, Radii.Card, m.MorphKey)
                : HeroArtwork(m, cover, saturation: 1.18f),
            ],
        });

        // Identity eyebrow — the type/year fact as ONE tracked-out run, occupying exactly the row the type/year pills
        // held (nothing below it moves). Same treatment as the vertical hero's eyebrow, from the same helper, so the
        // two layouts state the release kind identically. Playlists keep the owner/collaborator block instead.
        if (cfg.Badges == BadgeStyle.TypeYear)
        {
            if (EyebrowText(m, cfg) is { Length: > 0 } eyebrow)
                kids.Add(LateRow("rail:eyebrow", EyebrowRun(eyebrow) with { Width = cover }));
        }
        else if (cfg.Badges == BadgeStyle.OwnerRow && m.OwnerName is { Length: > 0 })
        {
            kids.Add(LateRow("rail:owner", PlaylistOwnerBlock(m, cover, modelSource)));
        }

        // Hero title — the page's Title/TitleLarge rung, AUTO-FITTING to the cover width in ≤3 LINES down to 18px. The
        // shell picks the rung from the window height (Title 28/36 or TitleLarge 40/52) and hands the paired line height
        // in with it; `float.NaN` here used to mean "whatever the font's natural box is", which is exactly the metric the
        // ramp exists to pin. Weight is the ramp's 600, not the old 900 — PageHero IS Ui.Title, and a 900 override made
        // this the only 900 in the app.
        // The title exists in BOTH models — it is the run that must never be rewritten, only moved.
        kids.Add(Row("rail:title", editable
            ? PlaylistInlineEdit.Title(modelSource, cover, titleSize, displayFace: true, lineHeight: titleLineHeight, maxLines: 3)
            : WaveeType.DetailHero(m.Title) with
            {
                Size = titleSize, MinSize = 18f, Weight = 600, Width = cover, LineHeight = titleLineHeight,
                Wrap = TextWrap.WrapWholeWords, MaxLines = 3, Trim = TextTrim.CharacterEllipsis,
            }));

        // Billed-artist row (album/single): a STACKED artist face-pile (overlapping avatars + "+N" of the distinct album
        // artists + the billed name) when the album carries artist avatars; else the plain clickable artist names.
        if (cfg.Badges == BadgeStyle.TypeYear && m.Artists.Count > 0)
            kids.Add(LateRow("rail:artists", Embed.Comp(new ArtistFacePile.Props(m.Artists, m.AlbumArtists, m.Tracks, cover, h), () => new ArtistFacePile())));

        // Meta line — albums surface Songs/Length/Released as the bento facts panel below, so an inline line would just
        // duplicate it; only non-album surfaces (playlists / liked) show it here.
        if (cfg.Badges != BadgeStyle.TypeYear && m.MetaLine is { Length: > 0 })
            kids.Add(LateRow("rail:meta", WaveeType.TrackMeta(m.MetaLine) with { Width = cover, MaxLines = 2, Trim = TextTrim.CharacterEllipsis }));

        // Daylist rollover countdown — the same flip strip the Home hero mounts, at rail scale. The card keeps its own
        // remount-on-rollover key INSIDE this row's stable slot: the two keys answer different questions.
        if (DaylistCard(m, h, compact: true) is { } daylist) kids.Add(LateRow("rail:daylist", daylist));

        // Chart-playlist "N new entries · <date>" caption — a plain static row, unlike the daylist countdown above.
        if (ChartCaption(m) is { } chartCaption) kids.Add(LateRow("rail:chart", chartCaption));

        // CTA cluster: Play pill + a GROUP of shuffle/heart/share FABs. Wrap=true → at a wide rail they're one line; at a
        // narrow rail the FAB group wraps to the next line AS A UNIT (Play above, the three FABs together below) instead
        // of orphaning a single FAB on its own line.
        kids.Add(new BoxEl
        {
            // Already a box, so it carries the key + the FLIP itself rather than gaining a wrapper. It exists in both
            // models and is pushed by every late row above it.
            Key = "rail:cta", Layout = Shove,
            Direction = 0, Wrap = true, Gap = Spacing.M, AlignItems = FlexAlign.Center,
            Margin = new Edges4(0f, Spacing.XS, 0f, 0f),
            Children =
            [
                PlayPill(h.Accent, h.PlayAll),
                new BoxEl
                {
                    Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center,
                    Children =
                    [
                        // Shuffle now lives in the track-list command bar; the rail keeps just the hero Play + save/share.
                        // A full pre-release has no album to save yet; the entity the collection write accepts is the
                        // prerelease. Swap the heart's TARGET rather than adding a second heart. Falls back to the album
                        // uri whenever the link is absent (offline, unresolved, or already released). Key = the target:
                        // SaveButton's uri freezes at mount.
                        (m.PreReleaseUri ?? m.ContextUri) is { Length: > 0 } saveUri
                            ? Embed.Comp(() => new SaveButton(saveUri, 16f, FabSize, m.Title) { Accent = () => h.Accent }) with { Key = "save:" + saveUri }
                            : Fab(Icons.Heart, () => { }),
                        PlaylistInlineEdit.ShareButton(modelSource),
                        PlaylistInlineEdit.OwnerMenu(modelSource, h),
                    ],
                },
            ],
        });

        if (PreReleaseCard(m, h) is { } countdown) kids.Add(LateRow("rail:prerelease", countdown));

        if (cfg.Badges == BadgeStyle.TypeYear && AlbumTrailing.HasReleasePanel(m))
            kids.Add(Row("rail:release", AlbumTrailing.ReleasePanel(m, h, outerPadding: false)));

        // Description / release blurb — an HTML fragment (links to artists/playlists, bold): parse → rich spans (links
        // accent + clickable via h.Go, bold rendered, entities decoded). Trimmed to descMaxLines (shell lowers it when short).
        if (descMaxLines > 0 && (editable || m.Description is { Length: > 0 }))
            kids.Add(LateRow("rail:desc", editable
                ? PlaylistInlineEdit.Description(modelSource, cover, descMaxLines, h)
                : RichText.Of(m.Description!, 12f, Tok.TextSecondary, h.Accent, cover, descMaxLines,
                    u => { if (RichText.RouteForUri(u) is { } k) h.Go(k, null); })));

        // Liked Songs' facts bento — the one page whose rail is otherwise mostly dead space below the CTA cluster.
        // `Row`, not `LateRow`: the panel owns its own entrance (AlbumTrailing.ReleasePanel's documented contract, and
        // the same call shape three rows above). `LikedFacts.Has` is the honesty gate — an empty or unstamped library
        // mounts NOTHING rather than a stack of empty cards (E1).
        if (LikedFacts.Has(m, cfg.Badges))
            kids.Add(Row("rail:likedfacts", LikedFacts.Panel(m, h, outerPadding: false)));

        var rail = new BoxEl
        {
            Direction = 1, Gap = 14f, Width = railW, Shrink = 0f,
            Padding = new Edges4(SidePadL, Spacing.XXL, SidePadR, Spacing.XXL),
            Children = kids.ToArray(),
        };
        // Own vertical scroller (hidden bar by default) — the LAST resort once the TEXT has shrunk and it still overflows
        // (the image stays full-width; the text gave first).
        //
        // Liked Songs takes the SAME scroller as everything else, minus the layer fill (below): its rail now carries the
        // facts bento and is genuinely taller than the shell. The arm this replaces was `Grow = 0, Shrink = 0` with no
        // `MinHeight = 0` — a scroller that cannot be given a height smaller than its content, i.e. one that grows the
        // rail instead of scrolling inside it. That was invisible while the liked rail was short; it is a clipped stack
        // the moment it is not.
        if (LikedSongsArtwork.IsLikedUri(m.ContextUri))
            return new BoxEl
            {
                // Fill DELIBERATELY unset — see the note on the general arm below: Liked keeps its established
                // unlayered treatment, and that is the ONLY difference between the two arms.
                Direction = 1, Width = railW, Shrink = 0f, ClipToBounds = true,
                Children =
                [
                    ScrollView(rail) with { Grow = 1f, Shrink = 1f, MinHeight = 0f, Width = railW },
                ],
            };

        // Match LibraryPage.NavPanel exactly: the contextual left column recedes on FillLayerDefault while the detail
        // rows remain on the base content surface. Liked Songs intentionally keeps its established unlayered treatment.
        return new BoxEl
        {
            Direction = 1, Width = railW, Shrink = 0f,
            ClipToBounds = true, Fill = Tok.FillLayerDefault,
            Children =
            [
                ScrollView(rail) with { Grow = 1f, Shrink = 1f, MinHeight = 0f, Width = railW },
            ],
        };
    }

    // Compact identity strip (WP-κ): cover + truncated title + expand control. Collapse no longer vanishes the rail —
    // playlists especially need the title when art is weak/generic. Same HeroArtwork decode path as the full rail so
    // the cover texture stays warm across the detent. expand restores the full rail (persisted by the caller).
    public static Element BuildCompact(DetailModel m, float stripW, Action expand)
    {
        const float pad = Spacing.S;   // 8 — tighter than the full rail; cover still fills the strip
        float cover = MathF.Max(48f, stripW - pad - pad);
        // The compact strip carries NO conditional row — cover + title + spacer + chevron, always, and both facts live
        // in the preview model — so it needs no late-arrival motion. It is keyed anyway so a future conditional row
        // cannot reintroduce the position-matching rewrite this file's keying note describes.
        Element coverHit = new BoxEl
        {
            Key = "compact:cover",
            Width = cover, Height = cover, Corners = CornerRadius4.All(Radii.Card),
            Shadow = Elevation.Card, ClipToBounds = true, Shrink = 0f, OnClick = expand,
            // The dynamic liked cover, but NO picker: the whole compact cover is the expand gesture, and a second hit
            // target inside 48–80 DIP would be a coin flip. LikedCoverArt's own <140 collapse decides what it paints
            // there (the flat 2x2 collection mosaic, or the stock PNG below four distinct covers).
            Children = [IsDynamicLikedCover(m)
                ? LikedSongsArtwork.Dynamic(cover, Radii.Card, m.MorphKey)
                : HeroArtwork(m, cover, saturation: 1.18f)],
        };
        Element title = new TextEl(m.Title)
        {
            Key = "compact:title",
            Size = 12f, Weight = 600, Color = Tok.TextPrimary,
            Width = cover, MinWidth = 0f, MaxLines = 2, Trim = TextTrim.CharacterEllipsis,
            Wrap = TextWrap.WrapWholeWords,
        };
        Element expandHit = new BoxEl
        {
            Key = "compact:expand",
            Width = cover, Height = 28f, Shrink = 0f,
            AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Corners = CornerRadius4.All(Radii.Control),
            HoverFill = Tok.FillSubtleSecondary, OnClick = expand,
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
                coverHit,
                title,
                new BoxEl { Key = "compact:spacer", Grow = 1f, MinHeight = 0f },   // push the chevron to the foot
                expandHit,
            ],
        };
        return ToolTip.Wrap(strip, m.Title);
    }

    // The header for the VERTICAL (narrow) layout, fixed above the scrolling track list. The cover sits on the LEFT with
    // the metadata (badges/owner, title, artist, meta) BESIDE it (filling the width to the right of the art); the PLAY
    // cluster + the context actions (copy-to-playlist / add-to-queue) stack full-width below. Center-aligned so the cover
    // and the text block balance (only a small symmetric gap, never a big wedge under the cover). The title wraps to
    // ≤3 lines (no truncation). The list's own command bar follows below (in the track list chrome). Drops the description.
    public static Element BuildHeader(DetailModel m, DetailConfig cfg, DetailHandlers h, Loadable<DetailModel> modelSource, bool includeReleasePanel = true, ActionServices? acts = null)
    {
        const float coverSz = 140f;
        var info = new List<Element>(4);

        // Same preview→full insert hazard as the two-column rail (see the keying note at the top of this file): the
        // eyebrow / owner / face pile / meta rows are full-model-only, so every info row is keyed and the wrappers
        // stretch to the info column exactly as the bare runs did.
        if (cfg.Badges == BadgeStyle.TypeYear)
        {
            // The info column already clamps (Grow/Basis 0) — the run's MaxLines/ellipsis carries the rest, so it needs
            // no explicit Width here (unlike the fixed-cover-edge rail above).
            if (EyebrowText(m, cfg) is { Length: > 0 } eyebrow) info.Add(LateRow("hdr:eyebrow", EyebrowRun(eyebrow)));
        }
        else if (cfg.Badges == BadgeStyle.OwnerRow && m.OwnerName is { Length: > 0 })
        {
            info.Add(LateRow("hdr:owner", PlaylistOwnerBlock(m, 600f, modelSource)));
        }

        bool editable = PlaylistInlineEdit.EditableMetadata(m) && m.ContextUri is { Length: > 0 };
        // DIAGNOSTIC ONLY (see DetailCoverTrace): the OTHER narrow arm (the fixed 140-DIP header above the track list),
        // whose cover size is a constant — so a decodePx that never moves here while the hero's does is H2.
        if (DetailCoverTrace.On)
            WaveeLog.Instance.Debug("detail", "cover", "header-build",
                WaveeLogField.Of("arm", "header"),
                WaveeLogField.Of("ctx", m.ContextUri),
                WaveeLogField.Of("editable", editable),
                WaveeLogField.Of("coverEdge", coverSz),
                WaveeLogField.Of("decodePx", HeroCoverDecodePx),
                WaveeLogField.Of("saturation", 1.0),
                WaveeLogField.Of("cover", DetailCoverTrace.Id(m.Cover)),
                WaveeLogField.Of("state", ((LoadState)modelSource.State.Peek()).ToString()),
                WaveeLogField.Of("morphKey", m.MorphKey));

        // Title cross-stretches to the info column's (Grow) width → wraps to it; ≤3 lines avoids truncation.
        info.Add(Row("hdr:title", editable
            // Title (28/36/600) — the same rung and weight as the side rail's hero above. Was a 900 override.
            ? PlaylistInlineEdit.Title(modelSource, 600f, 28f, lineHeight: 36f)
            : WaveeType.PageHero(m.Title) with { Size = 28f, LineHeight = 36f, Weight = 600, Wrap = TextWrap.WrapWholeWords, MaxLines = 3, Trim = TextTrim.CharacterEllipsis }));
        if (cfg.Badges == BadgeStyle.TypeYear && m.Artists.Count > 0)
            info.Add(LateRow("hdr:artists", Embed.Comp(new ArtistFacePile.Props(m.Artists, m.AlbumArtists, m.Tracks, 600f, h), () => new ArtistFacePile())));
        if (cfg.Badges != BadgeStyle.TypeYear && m.MetaLine is { Length: > 0 })
            info.Add(LateRow("hdr:meta", WaveeType.TrackMeta(m.MetaLine) with { MaxLines = 1, Trim = TextTrim.CharacterEllipsis }));

        var coverRow = new BoxEl
        {
            Key = "hdr:cover",   // the anchor row: keyed for stable identity, never animated (see the note above)
            Direction = 0, Gap = Spacing.L, AlignItems = FlexAlign.Center,   // center → balanced (no big wedge)
            Children =
            [
                new BoxEl
                {
                    Width = coverSz, Height = coverSz, Corners = CornerRadius4.All(Radii.Card),
                    Shadow = Elevation.Card, ClipToBounds = true,
                    Draggable = WaveeDetailDrag.Hero(m, acts),
                    // The rail's three arms verbatim (the narrow layout is a detail surface too, so it keeps the
                    // picker); the header cover is 140, which is exactly LikedCoverArt's treatment floor.
                    Children =
                    [
                        editable ? PlaylistInlineEdit.Cover(modelSource, coverSz)
                        : IsDynamicLikedCover(m) ? LikedCoverPicker.Cover(coverSz, Radii.Card, m.MorphKey)
                        : HeroArtwork(m, coverSz),
                    ],
                },
                new BoxEl { Direction = 1, Grow = 1f, Basis = 0f, Gap = Spacing.XS, Children = info.ToArray() },
            ],
        };

        // PlayRow is already a box, so it takes the key + FLIP directly; the two trailing cards are full-model-only.
        var headerKids = new List<Element>(4) { coverRow, PlayRow(h, m) with { Key = "hdr:play", Layout = Shove } };
        if (PreReleaseCard(m, h) is { } countdown) headerKids.Add(LateRow("hdr:prerelease", countdown));
        if (includeReleasePanel && cfg.Badges == BadgeStyle.TypeYear && AlbumTrailing.HasReleasePanel(m))
            headerKids.Add(Row("hdr:release", AlbumTrailing.ReleasePanel(m, h, outerPadding: false)));

        return new BoxEl
        {
            Direction = 1, Gap = Spacing.M, Shrink = 0f,
            Padding = new Edges4(Spacing.L, Spacing.L, Spacing.L, Spacing.S),
            Children = headerKids.ToArray(),
        };
    }

    /// <summary>The upcoming-release countdown, or null when nothing about this release is still ahead of us.
    ///
    /// The gate is <see cref="DetailModel.UpcomingAt"/>, NOT <c>IsPreRelease</c>: an album can be genuinely upcoming in
    /// three different wire shapes and only one of them sets that flag (a declared <c>preReleaseEndDateTime</c>; a
    /// partly-released album whose remaining rows carry a future live timestamp; a plain future release date with no
    /// prerelease marking at all). <see cref="PreReleaseDerivation"/> owns the precedence between them, so the card
    /// fires for all three and — because each rung is wall-clock checked — for none of them once the record is out.
    ///
    /// A pre-release with no derivable instant still renders nothing: a countdown to an unknown moment is not a
    /// countdown, and the rest of the page already says the album is upcoming.
    ///
    /// Shared by all three mount sites (the two-column rail, the vertical header, and the vertical trailing body) so
    /// the card cannot exist in one layout and be missing in another across a resize.</summary>
    internal static Element? PreReleaseCard(DetailModel m, DetailHandlers h)
        => m.UpcomingAt is { } end
            ? Embed.Comp(() => new PreReleaseCountdown { ReleaseAt = end, Accent = () => h.Accent })
                with { Key = "prerelease:" + m.ContextUri + ":" + end.UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture) }
            : null;

    /// <summary>The daylist rollover countdown, or null when this playlist carries no window. Shared by the rail
    /// (compact) and the vertical hero (hero scale). Key = the window, so a rollover remounts with fresh frozen props;
    /// the accent is a thunk because the art-derived palette lands after mount (PreReleaseCard's contract).</summary>
    internal static Element? DaylistCard(DetailModel m, DetailHandlers h, bool compact)
        => m.ExpiresAtMs > 0
            ? Embed.Comp(() => new FlipCountdown { ExpiresAtMs = m.ExpiresAtMs, Accent = () => h.Accent, Compact = compact })
                with { Key = "daylist:" + m.ContextUri + ":" + m.ExpiresAtMs.ToString(System.Globalization.CultureInfo.InvariantCulture) }
            : null;

    /// <summary>The chart-playlist "N new entries · <c>MMM d</c>" caption, or null off a non-chart playlist (or a chart
    /// header reporting no new entries). A plain static caption — unlike <see cref="DaylistCard"/> it states a fact as
    /// of the last load rather than counting down live, so it needs no Component/remount key of its own.</summary>
    internal static Element? ChartCaption(DetailModel m)
        => m.ChartNewEntries > 0
            ? Caption(Strings.Detail.ChartNewEntries(m.ChartNewEntries, DetailFormat.ChartUpdatedDateLabel(m.ChartUpdatedAtMs))) with
            {
                Color = Tok.TextSecondary,
            }
            : null;

    // The play cluster for the vertical (narrow) header: Play pill + shuffle / save / share, wrapping as a unit. The list
    // view controls (filter / sort / row size) now live in the track list's own command bar, so this row carries none.
    static Element PlayRow(DetailHandlers h, DetailModel m) => new BoxEl
    {
        Direction = 0, Gap = Spacing.M, AlignItems = FlexAlign.Center, Wrap = true,
        Children =
        [
            PlayPill(h.Accent, h.PlayAll),
            // Shuffle lives in the track-list command bar now (see DetailTracks.Toolbar).
            // Same heart-target swap as the two-column rail above (see the comment there): a full pre-release is saved
            // against its prerelease entity, and the key is the target because SaveButton's uri freezes at mount.
            (m.PreReleaseUri ?? m.ContextUri) is { Length: > 0 } saveUri
                ? Embed.Comp(() => new SaveButton(saveUri, 16f, FabSize, m.Title) { Accent = () => h.Accent }) with { Key = "save:" + saveUri }
                : Fab(Icons.Heart, () => { }),
            Fab(Icons.Share, () => { if (m.ShareUrl is { Length: > 0 } url) InputHooks.Current.Default.OpenUri?.Invoke(url); }),
        ],
    };

    // The billed-artist control: a stacked face-pile (the album's primary artists' avatars, overlapping, capped at 3) +
    // a "+N" badge folding in the rest of the DISTINCT artists across the album's tracks + the billed name. Clickable to
    // the lead artist. Falls back to the plain artist names when the album carries no artist avatars.
    static Element BilledArtists(DetailModel m, float cover, DetailHandlers h)
    {
        var detailed = m.AlbumArtists;
        if (detailed is not { Count: > 0 })
        {
            var only = m.Artists[0];
            return new BoxEl
            {
                Direction = 0, OnClick = () => h.Go("artist:" + only.Uri, only.Name),
                Children = [WaveeType.TrackTitle(DetailFormat.ArtistNames(m.Artists)) with { Width = cover, MaxLines = 1, Trim = TextTrim.CharacterEllipsis }],
            };
        }

        // Distinct artists across the album (primary ∪ all track artists) → the "+N" overflow count.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var a in detailed) seen.Add(a.Uri);
        foreach (var t in m.Tracks) foreach (var ar in t.Artists) if (ar.Uri.Length > 0) seen.Add(ar.Uri);

        int shown = Math.Min(detailed.Count, 3);
        int extra = seen.Count - shown;
        var lead = detailed[0];

        var pile = new List<Element>(shown + 1);
        for (int i = 0; i < shown; i++)
        {
            var a = detailed[i];
            pile.Add(new BoxEl
            {
                Width = 28f, Height = 28f, Shrink = 0f, Corners = CornerRadius4.All(14f), ClipToBounds = true,
                Margin = new Edges4(i == 0 ? 0f : -10f, 0f, 0f, 0f),   // overlap the stack
                Children = [Surfaces.Artwork(a.Image, a.Id.GetHashCode() & 0x7fffffff, 28f, 28f, 14f)],
            });
        }
        if (extra > 0)
            pile.Add(new BoxEl
            {
                Height = 28f, MinWidth = 34f, Shrink = 0f, Corners = CornerRadius4.All(14f), Fill = Tok.FillSubtleTertiary,
                Margin = new Edges4(-10f, 0f, 0f, 0f), Padding = new Edges4(11f, 0f, 8f, 0f),
                AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Children = [new TextEl("+" + extra) { Size = 11f, Weight = 700, Color = Tok.TextSecondary }],
            });

        return new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S, MaxWidth = cover,
            Corners = CornerRadius4.All(16f), Padding = new Edges4(2f, 2f, Spacing.S, 2f),
            HoverFill = Tok.FillSubtleSecondary, OnClick = () => h.Go("artist:" + lead.Uri, lead.Name),
            Children =
            [
                new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Shrink = 0f, Children = pile.ToArray() },
                new TextEl(DetailFormat.ArtistNames(m.Artists)) { Size = 14f, Weight = 700, Color = Tok.AccentTextPrimary, Grow = 1f, Basis = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
            ],
        };
    }

    internal static Element PlaylistOwnerBlock(DetailModel m, float cover, Loadable<DetailModel> full)
        => ShowCollaborators(m)
            ? Embed.Comp(() => new CollaboratorFacePile(m, cover, full))
            : PlaylistInlineEdit.OwnerRow(full, cover);

    internal static bool ShowCollaborators(DetailModel m)
        => m.Collaborators is { Count: > 0 } members && (m.Capabilities.IsCollaborative || members.Count >= 2);

    /// <summary>The identity EYEBROW string, for every detail header (this rail, the narrow header, the vertical hero).
    /// ONE composition so the same release can never be worded two ways across a layout cross: "ALBUM · 2019", the kind
    /// alone when the year is unknown (a show, an undated release), the year alone when the kind is, and "" when neither
    /// is known.</summary>
    internal static string EyebrowText(DetailModel m, DetailConfig cfg)
        => cfg.Badges switch
        {
            BadgeStyle.TypeYear => m.BadgeType is { Length: > 0 } type && m.Year is { Length: > 0 } year
                ? type + " · " + year
                : m.BadgeType ?? m.Year ?? "",
            // A playlist's eyebrow carries its ACCESS when there is something to carry. Both facts come from the store
            // header (seeded on open, flipped in place by a dealer permission push), so the line agrees with the access
            // flyout without either of them asking the server. Collaborative wins the single slot when both are true:
            // a collaborative playlist is private by default, so "Private" there is the less informative half.
            // A playlist that is public AND solo says nothing extra — that is the unremarkable default.
            BadgeStyle.OwnerRow => m.Capabilities.IsCollaborative ? Loc.Get(Strings.Nav.PlaylistCollaborative)
                : !m.IsPublic ? Loc.Get(Strings.Nav.PlaylistPrivate)
                : Loc.Get(Strings.Nav.Playlist),
            _ => Loc.Get(Strings.Nav.YourLibrary),
        };

    /// <summary>The eyebrow RUN — tertiary metadata on one line. Shared with the vertical hero: the type/year fact must
    /// look identical in both layouts, so the styling has exactly one definition — which is now
    /// <see cref="WaveeType.Eyebrow"/> plus this role's colour and its one-line clamp.</summary>
    internal static TextEl EyebrowRun(string text) => WaveeType.Eyebrow(text) with
    {
        Color = Tok.TextTertiary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
    };

    // The WaveeCta media pill on the cover-extracted accent (WaveeCta resolves the WCAG on-fill ink itself).
    static Element PlayPill(ColorF accent, Action onPlay)
        => WaveeCta.Play(accent, onPlay, Loc.Get(Strings.Detail.Play));

    static Element Fab(string glyph, Action onClick) => new BoxEl
    {
        Width = FabSize, Height = FabSize, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Corners = CornerRadius4.All(FabSize / 2f),
        HoverScale = WaveeMotion.ScaleEmphatic.Hover, PressScale = WaveeMotion.ScaleEmphatic.Press, OnClick = onClick,
        Children = [Icon(glyph, 16f, Tok.TextSecondary)],
    }.Interactive(Interaction.Subtle);

}
