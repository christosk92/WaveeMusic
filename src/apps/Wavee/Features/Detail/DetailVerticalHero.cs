using System;
using System.Collections.Generic;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Core;
using Wavee.Features.Detail;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

// ── THE UNIFIED DETAIL HERO ───────────────────────────────────────────────────────────────────────────────────────
//
// ONE composition, left-aligned at every width: artwork · eyebrow · title · accent rule · attribution · meta · action
// row · description. Apple Music's bones, the app's own voice — every part of it is a component the rest of Wavee
// already ships (DetailRail's eyebrow run and artwork treatment, Surfaces.AccentRule, WaveeCta.Play, SaveButton,
// PlaylistInlineEdit.ShareButton, RichText.Expandable), so the hero cannot say anything in a register no other surface
// speaks.
//
// WHAT IT REPLACED. This file used to carry THREE hero variants selected by a second breakpoint ladder: an IMMERSIVE
// arm (full-bleed square cover, a black copy-contrast scrim, white on-media ink, a raw Display run at −28 tracking
// sized by the TITLE'S LENGTH, hand-rolled white-alpha "glass" circles, a floating 60-DIP utility strip, a 44-DIP art
// token, copy indented to x=80), a COMPACT arm (a 96/64-DIP thumbnail row on surface ink with an accent 36×92 Play),
// and a SIDE-BY-SIDE arm that auto mode could never reach (it needed ≥560 DIP of column inside a layout that only
// exists below 540). The arms shared almost nothing — different ink, different type, different button grammar,
// different geometry — so a resize did not adjust the hero, it swapped it for a different design. All three are gone.
//
// WHAT IS LEFT IS A REFLOW, not a variant: below DetailVerticalLayout.RowFlowEnterW the artwork stacks above the
// identity column; at or above it the two sit side by side, TOP-aligned — the cover beside the title. Same elements,
// same order, same ink, same tokens. The ONLY thing width still decides is size (artwork edge, title rung, description
// cap, padding).
//
// INK. The page now sits on ONE opaque art-derived plane (WaveePalette.PageTone via CoverPaletteLeaves.PageTonePlane)
// whose lightness is CLAMPED per theme by construction — so the standard Tok ink tokens are correct on it in both
// themes and there is no on-media ladder anywhere on this surface. Every on-media ink read, every hand-mixed
// white-alpha plate and every media scrim is deleted rather than re-tuned.
//
// Built per-render from live values (BuildHeader's pattern → the hero re-derives on every re-render, so no frozen-prop
// hazard for the plain elements; Embed.Comp children freeze exactly as BuildHeader's do).
static class DetailVerticalHero
{
    static readonly LayoutTransition HeroGeometryMotion = new(
        TransitionChannels.Bounds,
        TransitionDynamics.Tween(280f, Easing.SmoothOut),
        SizeMode.ScaleCorrect);

    static readonly LayoutTransition HeroReflowMotion = new(
        TransitionChannels.Position | TransitionChannels.Size,
        TransitionDynamics.Tween(280f, Easing.SmoothOut),
        SizeMode.Reveal);

    /// <summary>Satellite action edge — row 1 of the icon-button geometry table (32 × 32, <c>Radii.Control</c>). The
    /// hero's secondary actions are ordinary icon buttons on an ordinary surface, which is exactly what that row is
    /// for; the circles they replaced were FAB geometry, and a FAB is for floating over media.</summary>
    const float SatelliteSize = WaveeCta.IconButtonSize;

    public static Element Build(DetailModel m, DetailConfig cfg, DetailHandlers h, Loadable<DetailModel> full,
                                bool rowFlow, float availW, bool widthMeasured,
                                float compactLeft, float collapseDistance,
                                IReadSignal<bool> compactInteractive,
                                IReadSignal<bool> searchExpanded, IReadSignal<bool> selectionCommandsVisible,
                                Element toolbar, Element compactSearch, Element compactActions, Element compactSelection,
                                ActionServices? acts = null)
    {
        bool editable = PlaylistInlineEdit.EditableMetadata(m) && m.ContextUri is { Length: > 0 };
        bool compactCanHit = compactInteractive.Value;

        // Bucket the available width to 8 DIP before deriving geometry, so the InlineEdit facades' width-folding keys
        // (title/description) don't churn a remount on every sub-pixel resize frame.
        float viewportW = availW > 0f ? availW : DetailVerticalLayout.FallbackW;
        float bw = DetailVerticalLayout.BucketW(viewportW);
        float heroPad = DetailVerticalLayout.HeroPadFor(bw);
        float heroGap = DetailVerticalLayout.HeroGapFor(bw);
        float artSize = DetailVerticalLayout.ArtworkFor(bw, rowFlow);
        float contentW = DetailVerticalLayout.ContentWidthFor(bw, rowFlow);
        float titleSize = DetailVerticalLayout.TitleSizeFor(bw);
        float titleLineHeight = DetailVerticalLayout.TitleLineHeightFor(titleSize);
        int descLines = DetailVerticalLayout.DescriptionMaxLines(rowFlow);
        // Unmeasured (widthMeasured false — availW is a page-width ESTIMATE, not a real bounds callback yet): 256, the
        // SAME bucket the grid tiles / DetailRail hero use, so the very first frame is a cache hit instead of a guess
        // off geometry that is itself only an estimate. See DetailVerticalLayout.ArtworkDecodePx(float,bool).
        int heroDecodePx = DetailVerticalLayout.ArtworkDecodePx(artSize, widthMeasured);

        // DIAGNOSTIC ONLY (see DetailCoverTrace): the narrow/Hero arm is the one that flashes, and this is where the
        // cover URL and the decode bucket are chosen. `artSize`/`heroDecodePx` moving between two consecutive lines for
        // the same route is H2 (a new decode key after the first width measure); `cover` changing is H1.
        if (DetailCoverTrace.On)
            WaveeLog.Instance.Debug("detail", "cover", "hero-build",
                WaveeLogField.Of("arm", "vertical"),
                WaveeLogField.Of("ctx", m.ContextUri),
                WaveeLogField.Of("editable", editable),
                WaveeLogField.Of("rowFlow", rowFlow),
                WaveeLogField.Of("availW", availW),
                WaveeLogField.Of("bucketW", bw),
                WaveeLogField.Of("artSize", artSize),
                WaveeLogField.Of("decodePx", heroDecodePx),
                WaveeLogField.Of("cover", DetailCoverTrace.Id(m.Cover)),
                WaveeLogField.Of("state", ((LoadState)full.State.Peek()).ToString()),
                WaveeLogField.Of("fullCover", DetailCoverTrace.Id(full.Value.Peek()?.Cover)),
                WaveeLogField.Of("morphKey", m.MorphKey));

        // ── artwork ────────────────────────────────────────────────────────────────────────────────────────────
        // The rail's treatment verbatim: card corners, the card elevation, and Apple's 1.18 oversaturation. It is the
        // page's anchor, so it does NOT take the entrance cascade below — only the copy arrives in sequence.
        Element artworkBox = new BoxEl
        {
            Width = artSize, Height = artSize, Shrink = 0f,
            HitTestVisible = true,
            Corners = CornerRadius4.All(Radii.Card),
            Shadow = Elevation.Card,
            ClipToBounds = true,
            Animate = HeroGeometryMotion,
            TransformOriginX = 0f, TransformOriginY = 0f,
            // The cover drags the whole entity this page is about. On the framing box, not on the editable
            // cover inside it, so that cover's FILE drop target is untouched (see WaveeDetailDrag.Hero).
            Draggable = WaveeDetailDrag.Hero(m, acts),
            Children =
            [
                // The rail's three arms verbatim (DetailRail.Build): editable playlist cover, the Liked collection's
                // dynamic treatment with its style picker, static art. morphKey stays null here — this hero does not
                // participate in the connected fly, exactly as before.
                editable
                    ? PlaylistInlineEdit.Cover(full, artSize, Radii.Card, shadow: true,
                        morphKey: null, decodePx: heroDecodePx, saturation: 1.18f)
                    : DetailRail.IsDynamicLikedCover(m)
                        ? LikedCoverPicker.Cover(artSize, Radii.Card, morphKey: null)
                        : DetailRail.HeroArtwork(m, artSize, Radii.Card, connected: false,
                            saturation: 1.18f, morphKey: null, decodePx: heroDecodePx)
            ],
        };

        // ── the identity column ────────────────────────────────────────────────────────────────────────────────
        // Each block is keyed so a late arrival (a description that lands with the full model) inserts itself without
        // shifting the reconciler's view of its siblings. No WaveeEntrance here: ContentHost already slides the page
        // (Fluent/Zune page language). A second rise+blur cascade on the identity column is what made first-open
        // dizzy and made Back feel like a different animation.
        //
        // The key is only half of it. The page renders TWICE per open — once from the PARTIAL nav preview model
        // (cover/title/artist), then again when the full model lands — and the eyebrow, attribution, meta line, daylist
        // pulse and description are FULL-MODEL-ONLY. So `late: true` rows fade up from nothing on insert (never a blink
        // into place) and EVERY row FLIPs from its old origin when a late sibling pushes it down. Both specs are shared
        // with the two-column rail (DetailRail.FadeUp / DetailRail.Shove) so the two layouts arrive with one gesture,
        // and both are authored on THIS wrapper box because the reconciler bakes Enter/Exit/Layout for BoxEl only.
        // Reduced motion is not branched here — the structural seed's KeepFade policy keeps the fade, snaps the travel.
        var infoKids = new List<Element>(8);
        void Add(string key, Element? e, bool late = false)
        {
            if (e is null) return;
            infoKids.Add(new BoxEl
            {
                Key = key, Direction = 1, HitTestVisible = true,
                Layout = DetailRail.Shove,
                Enter = late ? DetailRail.FadeUp : null,
                Children = [e],
            });
        }

        // The eyebrow STRING and RUN both come from DetailRail (EyebrowText / EyebrowRun) — the rail, the narrow
        // header and this hero must never word or style the same release two ways across a layout cross.
        string eyebrow = DetailRail.EyebrowText(m, cfg);
        Add("hero-eyebrow", eyebrow.Length > 0 ? DetailRail.EyebrowRun(eyebrow) : null, late: true);

        // The title is a RUNG of the type ramp in the DISPLAY face — the face is what keeps a page hero from reading
        // as a UI label, and the ramp is what keeps every album's hero opening at the same typographic weight. No
        // CharSpacing override: the alias publishes the tracking with the size and the line height.
        Element title = editable
            ? PlaylistInlineEdit.Title(full, contentW, titleSize, displayFace: true, lineHeight: titleLineHeight)
            : WaveeType.DetailHero(m.Title) with
            {
                Size = titleSize, MinSize = 18f, Weight = 600, LineHeight = titleLineHeight,
                Width = contentW, MaxWidth = contentW,
                Wrap = TextWrap.WrapWholeWords, MaxLines = 2, Trim = TextTrim.CharacterEllipsis,
                Color = Tok.TextPrimary,
            };
        Add("hero-title", title);

        // The app's section ornament, same recipe as every artist-page header: a 20×2 accent rule UNDER the title.
        Add("hero-rule", Surfaces.AccentRule(h.Accent));

        Add("hero-attribution", Attribution(m, h, contentW, full), late: true);

        if (m.MetaLine is { Length: > 0 })
            Add("hero-meta", WaveeType.TrackMeta(m.MetaLine) with
            {
                MaxWidth = contentW, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
            }, late: true);

        // Daylist rollover countdown — hero scale, between the meta line and the action row (the Home hero's slot).
        // Null-safe: Add skips it for every non-daylist model, exactly like the optional blocks above.
        Add("hero-pulse", DetailRail.DaylistCard(m, h, compact: false), late: true);

        // Chart-playlist "N new entries · <date>" caption — same slot shape as the daylist pulse above, null off every
        // non-chart model.
        Add("hero-chart", DetailRail.ChartCaption(m), late: true);

        // ── the action row ─────────────────────────────────────────────────────────────────────────────────────
        // The accent Play capsule LEADS (the same WaveeCta.Play builder and the same artwork accent the two-column
        // rail uses), then the quiet 32-DIP satellites. One grammar, no plates over media, nothing hand-rolled.
        // Keyed for the same reason the rows above are: the heart below is CONDITIONAL, so its arrival shifts every
        // satellite after it and position matching would remount the share button behind it.
        var actions = new List<Element>(5)
        {
            WaveeCta.Play(h.Accent, h.PlayAll) with { Key = "vhero-play" },
            Satellite(Icons.Shuffle, Loc.Get(Strings.Detail.Shuffle), h.Shuffle) with { Key = "vhero-shuffle" },
        };
        // Same heart-target swap as the two-column rail: a full pre-release is saved against its PRERELEASE entity, so
        // swap the target rather than adding a second heart, and key on the target because SaveButton's uri freezes at
        // mount. Falls back to the album uri whenever the link is absent (offline, unresolved, or already released).
        if ((m.PreReleaseUri ?? m.ContextUri) is { Length: > 0 } saveUri && cfg.Heart != HeartMode.None)
            actions.Add(Embed.Comp(() => new SaveButton(saveUri, 16f, SatelliteSize, m.Title) { Accent = () => h.Accent })
                with { Key = $"vhero-save:{saveUri}" });
        actions.Add(PlaylistInlineEdit.ShareButton(full, SatelliteSize) with { Key = "vhero-share" });
        actions.Add(Embed.Comp(() => new DetailHeroMoreButton(full, cfg, h, SatelliteSize))
            with { Key = $"vhero-more:{m.ContextUri}" });
        Add("hero-actions", new BoxEl
        {
            Direction = 0, Gap = Spacing.S, Wrap = true,
            AlignItems = FlexAlign.Center, Justify = FlexJustify.Start,
            Margin = new Edges4(0f, Spacing.XS, 0f, 0f),
            Children = actions.ToArray(),
        });

        Element? description = null;
        if (editable)
            description = PlaylistInlineEdit.Description(full, contentW, descLines, h);
        else if (m.Description is { Length: > 0 })
            description = RichText.Expandable(m.Description, 13f, Tok.TextSecondary, h.Accent,
                contentW, descLines, m.ContextUri ?? m.Title,
                u => { if (RichText.RouteForUri(u) is { } k) h.Go(k, null); });
        Add("hero-description", description, late: true);

        // ── WHERE THE FACTS CARDS WENT ─────────────────────────────────────────────────────────────────────────────
        // The metadata bento (this week's adds + the 12-week strip, the tempo curve, the top-artists ranking) used to
        // be the last block of THIS column, straight under the description. It is the same panel and the same cards —
        // it just is not composed here any more. The hero is the page's OPENING, and three stacked analytics cards in
        // it push the first track below the fold on every window the hero system runs at: the page then opens on
        // charts about the songs rather than on the songs. So in the hero system the panel is the page's FOOTER
        // instead — one virtual-list slot after the last row (TrackList.VerticalFactsFooter /
        // DetailVerticalLayout.ItemRole's Footer). The two-column/rail arm still renders it in the rail, unchanged and
        // for the same reason: there the cards sit BESIDE the tracks, so they cost the tracks no vertical space.

        // AlignItems = Stretch (plus an explicit width in stacked flow) is load-bearing, not tidiness: the action row
        // is a WRAPPING flex row, and a wrap needs a DEFINITE width to wrap against. Left to its intrinsic size it
        // measures as one unwrapped line and simply overflows the column at phone widths.
        Element identity = new BoxEl
        {
            Direction = 1, Gap = Spacing.XS, AlignItems = FlexAlign.Stretch,
            Width = rowFlow ? float.NaN : contentW,
            Grow = rowFlow ? 1f : 0f, Basis = rowFlow ? 0f : float.NaN, MinWidth = 0f,
            Children = infoKids.ToArray(),
        };

        // Stacked ↔ row is the SAME two children in the same order — only the axis changes, so the reflow animates as
        // one gesture instead of tearing the subtree down. Cross alignment is Start in BOTH arms: the row arm used to
        // bottom-align the pair (cover edge on the action row's baseline), which reads well only while the identity
        // column is about as tall as the art — and it is not: it carries the description (and, until the facts bento
        // moved to the page footer, three analytics cards under it), so on a wide hero page the 240-DIP cover sank
        // hundreds of DIP below the title to the band's bottom-left corner and read as a detached, misplaced square.
        // The cover belongs beside the title, and Start keeps it there whatever the column below grows to.
        Element hero = new BoxEl
        {
            Direction = rowFlow ? (byte)0 : (byte)1,
            Gap = heroGap,
            AlignItems = FlexAlign.Start,
            Animate = HeroReflowMotion,
            Children = [artworkBox, identity],
        };

        Element expanded = new BoxEl
        {
            Direction = 1,
            Animate = HeroReflowMotion,
            Children =
            [
                new BoxEl
                {
                    Direction = 1,
                    Padding = new Edges4(heroPad, heroPad, heroPad, DetailVerticalLayout.HeroBottomPad),
                    Animate = HeroReflowMotion,
                    Children = [hero],
                },
                new BoxEl
                {
                    Direction = 1,
                    Padding = new Edges4(compactLeft, DetailVerticalLayout.ExpandedToolbarTopPad,
                        compactLeft, DetailVerticalLayout.ExpandedToolbarBottomPad),
                    Children = [toolbar],
                },
            ],
        };

        // ── THE STUCK BAND: the shared text-chrome context bar (ContextBand) ─────────────────────────────────────
        // Typography only, and — since the offset model landed — NO MATERIAL: the band is an unpainted omission and
        // the rows are clipped at its lower edge (DetailVerticalLayout.StickyClipInset) rather than sliding under it,
        // so what shows behind this text is the page's own art-derived tone plane and its blurred backdrop. The band
        // inherits the record's colour for free, and there is no opaque constant left to read as a black slab on a
        // dark wallpaper. Its lower stratum is the tracklist's own column header (pinned directly under this by
        // VerticalChromeRoot), which carries the band's single hairline.

        // The byline is owner AND the meta line where both exist — "Spotify · 50 songs, 3 hr 12 min" — because the
        // band has a full row of width where the old capsule had a 480-DIP cap it was already ellipsizing inside.
        string compactMeta =
            m.OwnerName is { Length: > 0 } bandOwner && m.MetaLine is { Length: > 0 } bandMeta
                ? bandOwner + " · " + bandMeta
                : m.OwnerName ?? m.MetaLine ?? eyebrow;
        Element compactIdentityBlock = new BoxEl
        {
            Direction = 1, MinWidth = 0f, Shrink = 1f, Gap = 0f,
            Children = compactMeta is { Length: > 0 }
                ? [ContextBand.Title(m.Title), ContextBand.Byline(compactMeta)]
                : [ContextBand.Title(m.Title)],
        };
        Element compactSearchHost = new BoxEl
        {
            Shrink = 0f,
            Children = [compactSearch],
        };
        // The expanded search field takes the TITLE's place, not the actions' — the actions are what the band is for,
        // and a field that pushed them off-row would make Find a trap. The two live in ONE zero-gap slot so the
        // hidden arm cannot leave a cluster gap behind and shift the visible one.
        Element compactLeadSlot = new BoxEl
        {
            Direction = 0, MinWidth = 0f, Shrink = 1f, Gap = 0f, AlignItems = FlexAlign.Center,
            Children =
            [
                Flow.Show(() => !searchExpanded.Value, compactIdentityBlock),
                Flow.Show(() => searchExpanded.Value, compactSearchHost),
            ],
        };
        Element normalCompactIdentity = ContextBand.Row(viewportW, compactLeft,
            [
                compactLeadSlot,
                new BoxEl { Grow = 1f, Basis = 0f, MinWidth = 0f, Height = 1f, HitTestVisible = false },
                compactActions,
            ]);
        // The selection arm swaps the band's CONTENT (a batch command bar) for the same 56 DIP. Unpainted like the
        // normal arm — it is the same band in another mode, not a plate that appears on top of one.
        Element selectionCompactIdentity = new BoxEl
        {
            Direction = 1,
            Width = viewportW,
            Height = DetailVerticalLayout.CompactIdentityHeight,
            Padding = new Edges4(compactLeft, 4f, compactLeft, 4f),
            Justify = FlexJustify.Center,
            HitTestVisible = true,
            Children = [compactSelection],
        };
        Element compactIdentityContent = new BoxEl
        {
            ZStack = true,
            Width = viewportW,
            Height = DetailVerticalLayout.CompactIdentityHeight,
            Children =
            [
                Flow.Show(() => !selectionCommandsVisible.Value, normalCompactIdentity),
                Flow.Show(() => selectionCommandsVisible.Value, selectionCompactIdentity),
            ],
        };
        Element compactIdentity = new BoxEl
        {
            ZStack = true, Width = viewportW, Height = DetailVerticalLayout.CompactIdentityHeight,
            HitTestVisible = compactCanHit, HitTestPassThrough = true,
            Children =
            [
                compactIdentityContent,
            ],
        }.Reveal(DetailVerticalLayout.CompactRevealStart(collapseDistance), collapseDistance, Spacing.XS);

        Element expandedPresentation = ZStack(expanded) with
        {
            Direction = 1,
            HitTestVisible = !compactCanHit,
            ScrollBinds =
            [
                new() { From = ScrollChannel.Offset, To = BindSink.TransY,
                    Range = ScrollRange.Px(0f, collapseDistance),
                    OutStart = 0f, OutEnd = -collapseDistance, Ease = Easing.Linear },
                new() { From = ScrollChannel.Offset, To = BindSink.Opacity,
                    Range = ScrollRange.Px(DetailVerticalLayout.ExpandedFadeStart(collapseDistance), collapseDistance),
                    OutStart = 1f, OutEnd = 0f, Ease = Easing.Linear },
            ],
        };
        return ZStack(expandedPresentation, compactIdentity) with { Direction = 1 };
    }

    /// <summary>A quiet 32-DIP secondary action: transparent at rest, <c>FillSubtleSecondary</c> under the pointer, on
    /// the control ladder's 4-DIP radius. Row 1 of <see cref="WaveeCta"/>'s icon-button geometry table — the standard
    /// icon affordance on a normal surface, which is what the hero now is.</summary>
    static Element Satellite(string glyph, string label, Action onClick)
    {
        BoxEl button = new()
        {
            Direction = 0, Width = SatelliteSize, Height = SatelliteSize, Shrink = 0f,
            AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Corners = Radii.ControlAll,
            HoverFill = Tok.FillSubtleSecondary,
            PressedFill = Tok.FillSubtleTertiary,
            BrushTransitionMs = WaveeMotion.Faster,
            Cursor = CursorId.Hand, Focusable = true, Role = AutomationRole.Button, OnClick = onClick,
            HoverScale = WaveeMotion.ScaleStandard.Hover, PressScale = WaveeMotion.ScaleStandard.Press,
            Children = [Icon(glyph, 14f, Tok.TextSecondary)],
        };
        return ToolTip.Wrap(button, label);
    }

    static Element? Attribution(DetailModel m, DetailHandlers h, float maxWidth, Loadable<DetailModel>? full = null)
    {
        // Collaborative playlists get the stacked-avatar facepile here too — the rail renders it via PlaylistOwnerBlock,
        // and the hero system replaced the rail at narrow widths, so dropping it here silently dropped the collaborator
        // overlays at every width (user report 2026-07-23). Same predicate as the rail.
        if (DetailRail.ShowCollaborators(m))
            return Embed.Comp(() => new CollaboratorFacePile(m, maxWidth, full));
        if (m.OwnerName is { Length: > 0 } owner)
            return new TextEl(owner)
            {
                Size = 12f, Weight = 600,
                Color = Tok.TextSecondary,
                MaxWidth = maxWidth, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
            };
        if (m.Artists.Count == 0) return null;

        var spans = new TextSpan[m.Artists.Count * 2 - 1];
        int at = 0;
        for (int i = 0; i < m.Artists.Count; i++)
        {
            if (i > 0) spans[at++] = new TextSpan(", ");
            var artist = m.Artists[i];
            spans[at++] = new TextSpan(artist.Name, Weight: 600, Color: h.Accent,
                OnClick: () => h.Go("artist:" + artist.Uri, artist.Name));
        }
        return new SpanTextEl(spans)
        {
            Size = 12f,
            Color = Tok.TextSecondary,
            MaxWidth = maxWidth,
            Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
        };
    }
}

// The hero's unified overflow ("More") menu — a 32-DIP quiet icon button whose flyout is built lazily at open from the
// LIVE model: Add/Copy to playlist (the searchable picker) · Play next · Add to queue · (owner-only) Invite / Delete.
// Every item uses the new IconRef { Glyph, Font } form. Keyed per context at the call site so its frozen ctor args
// (cfg/h) stay coherent for THIS page.
sealed class DetailHeroMoreButton : Component
{
    readonly Loadable<DetailModel> _full;
    readonly DetailConfig _cfg;
    readonly DetailHandlers _h;
    readonly float _size;

    public DetailHeroMoreButton(Loadable<DetailModel> full, DetailConfig cfg, DetailHandlers h, float size)
    { _full = full; _cfg = cfg; _h = h; _size = size; }

    public override Element Render()
    {
        var overlay = UseContext(Overlay.Service);
        var lib = UseContext(LibraryBridge.Slot);
        var svc = UseContext(Services.Slot);
        var anchor = UseRef<NodeHandle>(default);
        var handle = UseRef<OverlayHandle?>(null);
        var pickerHandle = UseRef<OverlayHandle?>(null);
        var accessHandle = UseRef<OverlayHandle?>(null);

        void Toggle()
        {
            if (overlay is null) return;
            if (handle.Value is { IsOpen: true } open) { open.Close(); return; }
            var m = _full.Value.Peek();
            // Read-only contexts (followed playlists, Liked) COPY to a playlist; an editable playlist / album ADDS.
            bool copy = _cfg.Heart == HeartMode.Follow || LikedSongsArtwork.IsLikedUri(m.ContextUri);
            var items = new List<MenuFlyoutItem>
            {
                new(Loc.Get(copy ? Strings.Detail.CopyToPlaylist : Strings.Detail.AddToPlaylist),
                    new IconRef { Glyph = Icons.Add, Font = null },
                    Invoke: () => PlaylistPickerLauncher.OpenFlyout(overlay, () => anchor.Value, () => _full.Value.Peek().Tracks, pickerHandle)),
                new(Loc.Get(Strings.Detail.PlayNext), new IconRef { Glyph = WaveeIcons.PlayNext, Font = WaveeIcons.Font }, Invoke: _h.PlayNext),
                new(Loc.Get(Strings.Detail.AddToQueue), new IconRef { Glyph = WaveeIcons.PlayAfter, Font = WaveeIcons.Font }, Invoke: _h.AddToQueue),
            };
            // Owner-only Invite / Delete (capability-gated inside AppendOwnerItems), behind a separator.
            var ownerItems = new List<MenuFlyoutItem>();
            PlaylistInlineEdit.AppendOwnerItems(ownerItems, overlay, lib, svc, _full, _h, () => anchor.Value, accessHandle);
            if (ownerItems.Count > 0)
            {
                items.Add(MenuFlyoutItem.Separator);
                items.AddRange(ownerItems);
            }
            handle.Value = overlay.Open(
                () => anchor.Value,
                () => MenuFlyout.Create(items, () => handle.Value?.Close()),
                FlyoutPlacement.BottomEdgeAlignedRight,
                new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss) { ConstrainToRootBounds = false });
            handle.Value.ClosedAction = () => handle.Value = null;
        }

        BoxEl button = new()
        {
            Width = _size, Height = _size, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Corners = Radii.ControlAll,
            HoverScale = WaveeMotion.ScaleStandard.Hover, PressScale = WaveeMotion.ScaleStandard.Press,
            Cursor = CursorId.Hand, Role = AutomationRole.Button,
            OnClick = Toggle,
            OnRealized = h => anchor.Value = h,
            Children = [Icon(Icons.More, 16f, Tok.TextSecondary)],
        };
        return button.Interactive(Interaction.Subtle);
    }
}
