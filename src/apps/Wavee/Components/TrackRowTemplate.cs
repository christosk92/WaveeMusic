using System;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Core;
using Wavee.Core.Catalog;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

/// <summary>The live bound track-row cell template (Operation ultra-fast GPU engine, P5 slice 4 —
/// <c>docs/plans/wavee/operation-ultra-fast-app-progress.md</c>): renders a <see cref="RowPresentation"/> through
/// <see cref="BoundItemScope{T}"/>'s typed channels instead of rebuilding the element tree per render. Runs ONCE
/// per persistent virtualized slot; every per-row value below is a <c>Prop&lt;T&gt;</c> or a rare <c>ShowWhen</c>
/// branch, never a closure over a frozen field. <c>BoundRowContent.Render</c> is the call site.
/// <para>Cell-by-cell parity source: <c>Components/TrackRow.cs</c>'s <c>Grid</c> — every cell here must render
/// IDENTICALLY (zebra/hover/selection skin, the like pop, the marquee, chart glyphs, badges are all preserved by
/// the surrounding <c>BoundRowSkin</c>/<c>ExpandableRowSlot</c>).</para></summary>
internal static class TrackRowTemplate
{
    internal static Element Build(BoundItemScope<RowPresentation> item, TrackList.RowShape shape, RowHandlers h)
    {
        var set = shape.Set;
        var cells = new System.Collections.Generic.List<Element>(shape.Tracks.Length);
        void Add(string key, Element cell) => cells.Add(cell with { Key = key });

        Add(TrackRow.CellKey.Num, TrackRow.CenterCell(NumberCell(item, h)));

        if (set.Heart) Add(TrackRow.CellKey.Heart, TrackRow.CenterCell(HeartCell(item, h)));

        if (set.Thumb) Add(TrackRow.CellKey.Art, TrackRow.CenterCell(ArtCell(item, shape)));

        // showAlbumInMeta: STATIC per shape (mirrors TrackRow.Grid's own `showAlbumInMeta: !set.Classic &&
        // showListMetadata && !set.Album` local, minus the per-row showListMetadata factor — RowSpanLayout.Metadata
        // ANDs that in per row from RowPresentation.ShowListMetadata).
        bool showAlbumInMeta = !set.Classic && !set.Album;
        Add(TrackRow.CellKey.Title, TitleCell(item, h, set, showAlbumInMeta));

        if (set.Artist) Add(TrackRow.CellKey.Artist, TrackRow.LeftCell(ArtistCell(item, h, set)));
        if (set.Album) Add(TrackRow.CellKey.Album, TrackRow.LeftCell(AlbumCell(item, h)));
        if (set.By) Add(TrackRow.CellKey.By, ByCell(item, set));
        if (set.Date) Add(TrackRow.CellKey.Date, TrackRow.LeftCell(DateCell(item, set)));
        if (set.Plays) Add(TrackRow.CellKey.Plays, TrackRow.EndCell(PlaysCell(item, set)));
        if (TrackRow.ShowTempo(set)) Add(TrackRow.CellKey.Tempo, TrackRow.EndCell(TempoCell(item, set)));
        Add(TrackRow.CellKey.Duration, TrackRow.EndCell(DurationCell(item, set)));
        if (set.Video) Add(TrackRow.CellKey.Video, TrackRow.CenterCell(VideoMoreCell(item)));
        if (set.Actions) Add(TrackRow.CellKey.More, MoreCell(item));
        if (set.Expand) Add(TrackRow.CellKey.Expand, ExpandCell(item, h));

        float padX = TrackRow.PadXFor(set.Tier);
        return new GridEl
        {
            // shape.RowH, NEVER the TrackRow.RowHeight constant: the row height is DENSITY-keyed (modern 40/48/56/64,
            // Classic 36/40/44/48 — DetailTrackTableRules.RowHeightFor) and it is the number the whole reservation
            // chain is built from (MeasuredStackVirtualLayout's per-row estimate, the row skin's MinHeight, the header
            // ladder, the collapsed extent ToggleExpanded hands CorrectMeasuredExtent). Pinning the grid at 48 made
            // every Classic row and every Compact row MEASURE a height the list had not reserved, so the content
            // extent drifted as rows realized and an off-window collapse corrected to the wrong band. Matches the
            // eager path (TrackRow.Grid takes `rowH` and passes it straight through).
            Columns = shape.Tracks, ColGap = TrackRow.ColGapFor(set.Tier), RowHeight = shape.RowH, Grow = 1f,
            Padding = new Edges4(set.Classic ? padX : padX - TrackRow.RowInset, 0f,
                                 set.Classic ? padX : padX - TrackRow.RowInset, 0f),
            Children = cells.ToArray(),
        };
    }

    // ── # cell ────────────────────────────────────────────────────────────────────────────────────────────────
    // Same rest/transport ZStack as TrackRow.NumberCell, rebuilt as bound presence channels instead of a per-render
    // ternary: the "rest" slot mounts number+chart+star+eq+spinner as SIBLINGS, each individually presence-gated so
    // at most one (or number+chart together, exactly like the eager cell) is ever visible — no remount on a
    // playback-state flip, only a Visible flip (P1's presence channel).
    static Element NumberCell(BoundItemScope<RowPresentation> item, RowHandlers h)
    {
        Element number = Caption("") with
        {
            Text = item.Number(p => p.DisplayIndex + 1),
            Color = Tok.TextTertiary,
            Visible = item.Show(p => !p.State.IsBuffering && !p.State.IsNow && !p.State.IsTop),
        };
        Element chart = new TextEl("") with
        {
            Text = item.Text(p => TrackRowGlyphs.ChartText(p.Track.Chart)),
            Size = 8f, LineHeight = 12f, Weight = 700,
            Color = item.Color(p => ChartGlyphColor(p.Track.Chart)),
            Visible = item.Show(p => !p.State.IsBuffering && !p.State.IsNow && !p.State.IsTop
                                      && TrackRowGlyphs.ChartText(p.Track.Chart).Length > 0),
        };
        Element star = Icon(Icons.FavoriteStarFill, 11f, Tok.AccentTextPrimary) with
        {
            Visible = item.Show(p => !p.State.IsBuffering && !p.State.IsNow && p.State.IsTop),
        };
        Element restSlot = new BoxEl
        {
            Grow = 1f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, HoverOpacity = 0f,
            Children =
            [
                new BoxEl { Direction = 0, Gap = 2f, AlignItems = FlexAlign.Center, Children = [number, chart] },
                star,
                item.ShowWhen(p => p.State.IsNow && !p.State.IsBuffering,
                    () => WaveeEqualizer.Of(item.Signal(p => p.State.IsPlaying), static () => Tok.AccentTextPrimary)),
                item.ShowWhen(p => p.State.IsBuffering, TrackRow.Spinner),
            ],
        };
        Element transportGlyph = Icon(Icons.Play, 12f, Tok.TextPrimary) with
        {
            Text = item.Text(p => p.State.IsNow && p.State.IsPlaying ? Icons.Pause : Icons.Play),
            Color = item.Color(p => p.State.IsNow ? Tok.AccentTextPrimary : Tok.TextPrimary),
        };
        Element transportSlot = new BoxEl
        {
            Grow = 1f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Opacity = 0f, HoverOpacity = 1f,
            // A track the server says is not out yet gets no click target — matches TrackRow.Grid's
            // `notYetOut ? null : onPlay` (a hover play that would do nothing). Skeleton rows are non-interactive too.
            Visible = item.Show(p => !p.Track.IsNotYetOut() && !p.IsSkeleton),
            Cursor = CursorId.Hand,
            OnClick = item.Invoke(p => h.Play(p.DisplayIndex)),
            Children =
            [
                new BoxEl
                {
                    Width = 24f, Height = 24f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                    PressScale = WaveeMotion.ScaleEmphatic.Press,
                    Children = [transportGlyph],
                },
            ],
        };
        return new BoxEl { ZStack = true, Children = [restSlot, transportSlot] };
    }

    static ColorF ChartGlyphColor(ChartEntry? chart) => chart?.Status switch
    {
        ChartEntryStatus.Down => Tok.SystemFillCritical,
        _ => Tok.SystemFillSuccess,
    };

    // ── ♥ cell ────────────────────────────────────────────────────────────────────────────────────────────────
    // Both glyphs always mounted (shape-stable), presence-gated by Saved: no per-row LikeEdge/UseRef bookkeeping —
    // the P1 true-edge Enter seed (Element.Enter, seeded on a false→true Visible flip) IS the pop, and P3's
    // SuppressBoundTransitions snaps it instead of animating on a recycle.
    static readonly EnterExit HeartPop = new(Sx: 0.25f, Sy: 0.25f, Opacity: 0f, Active: true, Blur: Expressive.BlurSmall);

    static Element HeartCell(BoundItemScope<RowPresentation> item, RowHandlers h)
    {
        Element outline = Icon(Icons.Heart, 14f, Tok.TextTertiary) with
        {
            Visible = item.Show(p => !p.State.Saved),
        };
        Element filled = Icon(Icons.HeartFill, 14f, Tok.AccentTextPrimary) with
        {
            Visible = item.Show(p => p.State.Saved),
            Enter = HeartPop,
        };
        return new BoxEl
        {
            Width = TrackRow.HeartCol, Height = TrackRow.HeartCol, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Corners = Radii.Circle(TrackRow.HeartCol),
            Cursor = CursorId.Hand, OnClick = item.Invoke(h.ToggleLike),
            BlocksDragArm = true,
            Children = [outline, filled],
        }.Interactive(Interaction.Subtle);
    }

    // ── art cell ──────────────────────────────────────────────────────────────────────────────────────────────
    // The placeholder tile is ALWAYS mounted under the image (Surfaces.Artwork's own pattern) rather than a
    // load-state branch — a bound Fill, so a recycled slot's placeholder repaints instantly to the new row's tint
    // while the new cover decodes, with no remount. The image is likewise always-present (P3: an ImageEl's decode
    // target is template-static — shape.Art, not per-row — so density changes are a template remount via the
    // existing tier-keyed list remount, never a per-row concern).
    static Element ArtCell(BoundItemScope<RowPresentation> item, TrackList.RowShape shape)
    {
        float edge = shape.Art;
        Element tile = new BoxEl
        {
            Width = edge, Height = edge, Corners = CornerRadius4.All(Radii.Control),
            Fill = item.Color(p => Surfaces.PlaceholderFor(ImageSource.UrlFor(p.Track.Image, false))),
        };
        Element image = new ImageEl
        {
            Source = item.Image(p => ImageSource.UrlFor(p.Track.Image, false)),
            Width = edge, Height = edge, Corners = CornerRadius4.All(Radii.Control),
            Fit = ImageFit.Cover, DecodePx = edge * 2f,
        };
        return new BoxEl { ZStack = true, Children = [tile, image] };
    }

    // ── title cell ────────────────────────────────────────────────────────────────────────────────────────────
    static Element TitleCell(BoundItemScope<RowPresentation> item, RowHandlers h, ColumnSet set, bool showAlbumInMeta)
    {
        Element plainTitle = new TextEl("") with
        {
            Text = item.Text(p => p.Track.Title),
            Size = 14f, Weight = 600,
            Color = item.Color(p => p.State.IsNow ? Tok.AccentTextPrimary : Tok.TextPrimary),
            Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
            // Presence, not ShowWhen: the plain title is the COMMON case (every row but the one now playing without
            // a disabled marquee) and flips often as playback moves row to row — a real mount/unmount per flip would
            // defeat shape stability for the whole list's steady state.
            Visible = item.Show(p => !(p.State.IsNow && !p.MarqueeDisabled) && p.TitleState == TrackTitleState.Ready),
        };
        Element marquee = item.ShowWhen(p => p.State.IsNow && !p.MarqueeDisabled && p.TitleState == TrackTitleState.Ready,
            () => Marquee.Of(item.Text(p => p.Track.Title), new Marquee.Style { FontSize = 14f, Weight = 600, Foreground = Tok.AccentTextPrimary }));
        // The NOT-READY title line — one boundary covering every non-Ready state, deliberately.
        //
        // Two things are true at once here. (1) A row whose title is still LOADING must shimmer, not render the
        // terminal "Track details unavailable + Retry" block: that block is an ANSWER, and the gate used to be
        // `!= Ready`, which showed it for Loading too — so on a freshly opened album ten of thirteen rows rendered a
        // dead end with a Retry button for as long as their extended-metadata batch was in flight. (2) The fix cannot
        // be a FOURTH sibling: as the note below this records, a Show boundary is a real layout child even when its
        // predicate is false, and adding one made the title cell measure taller than the row skin the grid reserved —
        // rows grew, unevenly, and the zebra band no longer matched the row pitch.
        //
        // So the state fork lives INSIDE the single boundary, on presence-gated siblings (Element.Visible, which does
        // collapse out of flow — the same pair the Plays cell uses). The title line keeps exactly its three children.
        Element retry = item.ShowWhen(p => p.TitleState != TrackTitleState.Ready, () => new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S,
            Children =
            [
                new BoxEl
                {
                    Width = 168f, Height = 13f, Corners = CornerRadius4.All(4f), Fill = Tok.FillControlSecondary,
                    Visible = item.Show(p => p.TitleState == TrackTitleState.Loading),
                },
                new TextEl("") with
                {
                    Text = item.Text(p => Loc.Get(p.TitleState == TrackTitleState.Offline
                        ? Strings.Detail.TrackDetailsOffline : Strings.Detail.TrackDetailsUnavailable)),
                    Size = 14f, Color = Tok.TextSecondary, Grow = 1f,
                    Visible = item.Show(p => p.TitleState is TrackTitleState.Unavailable or TrackTitleState.Offline),
                },
                Button.Create(Loc.Get(Strings.Common.Retry), h.RetryMetadata, ButtonAppearance.Subtle, ControlSize.Small)
                    with { Visible = item.Show(p => p.TitleState is TrackTitleState.Unavailable or TrackTitleState.Offline) },
            ],
        });
        Element metaLine = MetaLine(item, h, set, showAlbumInMeta);
        // ONE title-line slot holding the three mutually-exclusive title variants, so the column below has exactly the
        // TWO rungs it means to space — the title line and the metadata line — and pays `Gap` once between them.
        //
        // Not cosmetic bookkeeping: a `Flow.Show` boundary (item.ShowWhen) is a REAL layout child even when its
        // predicate is false. It is not collapsed (only Element.Visible collapses a node out of flow —
        // FlexLayout.FirstVisibleChild reads the Collapsed aux bit), so it measures 0×0 AND still consumes a gap slot
        // (`usedMain += li.Gap * (n - 1)` counts it). With marquee + retry as direct siblings the common row therefore
        // paid THREE XXS gaps between "Style" and "Taylor Swift · 1989 (D…" — 6 DIP where the eager TrackRow.Grid
        // title column pays 2 — and the inflated title cell pushed the row's own content height with it.
        // Gap = 0 inside, so the two 0-tall Show boundaries in here cost nothing.
        Element titleLine = new BoxEl
        {
            Direction = 1, Gap = 0f, MinWidth = 0f,
            Children = [plainTitle, marquee, retry],
        };
        Element titleCol = new BoxEl
        {
            Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Gap = Spacing.XXS,
            Opacity = item.Opacity(p => p.Track.IsNotYetOut() ? 0.45f : 1f),
            Children = [titleLine, metaLine],
        };
        return new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, MinWidth = 0f, ClipToBounds = true, Children = [titleCol] };
    }

    // ── metadata subline (artist/album/episode-badge/explicit-badge under the title) ────────────────────────────
    // ALWAYS mounted (Visible-gated, never a structural flip) — TrackRow.MetadataLine's three optional children
    // (episode eyebrow, explicit badge, the artist+album span run) become three ALWAYS-mounted siblings here, each
    // individually presence-gated exactly like the number cell's rest-slot siblings (slice 2's own pattern).
    static Element MetaLine(BoundItemScope<RowPresentation> item, RowHandlers h, ColumnSet set, bool showAlbumInMeta)
    {
        bool classic = set.Classic;
        Element eyebrow = WaveeType.Eyebrow(Loc.Get(Strings.Detail.Badge.Episode)) with
        {
            Color = Tok.TextTertiary, Shrink = 0f, MaxLines = 1,
            Visible = item.Show(p => !classic && EntityUri.KindOf(p.Track.Uri) == EntityKind.Episode),
        };
        Element badge = TrackRow.ExplicitBadge() with
        {
            Visible = item.Show(p => !classic && p.ShowListMetadata && p.Track.IsExplicit),
        };
        Element spans = new SpanTextEl(item.Spans((p, b) => Meta(p, b, showAlbumInMeta))) with
        {
            Size = 12f, LineHeight = 16f, Color = Tok.TextSecondary, Wrap = TextWrap.NoWrap,
            Trim = TextTrim.CharacterEllipsis, MaxLines = 1, Grow = 1f, Basis = 0f, MinWidth = 0f,
            OnSpanClick = item.InvokeSpan((p, i) => GoToMetaSpan(p, i, showAlbumInMeta, h)),
        };
        return new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = 4f, MinWidth = 0f, ClipToBounds = true,
            Visible = item.Show(p => !classic && (p.ShowTrackArtist || (showAlbumInMeta && p.ShowListMetadata)
                                                   || (p.ShowListMetadata && p.Track.IsExplicit))),
            Children = [eyebrow, badge, spans],
        };
    }

    /// <summary>Fills the metadata line's <see cref="SpanBuffer"/> from <see cref="RowSpanLayout.Metadata"/> — the
    /// ONE layout <see cref="GoToMetaSpan"/> resolves clicks against, so the two can never disagree about what span
    /// index N means. Separator spans carry no link (no <c>IsLink</c>): <c>InputDispatcher</c> never routes a click
    /// on one to <see cref="SpanTextEl.OnSpanClick"/>.</summary>
    static void Meta(RowPresentation p, SpanBuffer b, bool showAlbumInMeta)
    {
        var slots = RowSpanLayout.Metadata(p, showAlbumInMeta);
        for (int i = 0; i < slots.Count; i++)
            b.Add(new TextSpan(slots[i].Text, IsLink: slots[i].Kind != MetaSpanKind.Separator));
    }

    static void GoToMetaSpan(RowPresentation p, int spanIndex, bool showAlbumInMeta, RowHandlers h)
    {
        var slots = RowSpanLayout.Metadata(p, showAlbumInMeta);
        if ((uint)spanIndex >= (uint)slots.Count) return;
        var slot = slots[spanIndex];
        string? route = slot.Kind switch
        {
            MetaSpanKind.Artist => RouteForRef(slot.Uri),
            MetaSpanKind.Album => RichText.RouteForUri(slot.Uri),
            _ => null,
        };
        if (route is not null) h.Go(route, slot.Name);
    }

    static string RouteForRef(string uri) => RichText.RouteForUri(uri) ?? ("artist:" + uri);

    // ── artist cell (Classic's dedicated lane, ColumnSet.Artist) ────────────────────────────────────────────────
    static Element ArtistCell(BoundItemScope<RowPresentation> item, RowHandlers h, ColumnSet set)
    {
        float size = set.Classic ? 14f : 12f, lineHeight = set.Classic ? 20f : 16f;
        return new SpanTextEl(item.Spans((p, b) =>
        {
            // Color rides per-span (SpanTextEl's own Color is the static paragraph base, not a bound channel) —
            // classicNow (set.Classic is static, State.IsNow is per-row) decides the whole run's ink together.
            ColorF ink = set.Classic && p.State.IsNow ? Tok.AccentTextPrimary : Tok.TextSecondary;
            var slots = RowSpanLayout.Artists(p.Track.Artists);
            for (int i = 0; i < slots.Count; i++)
                b.Add(new TextSpan(slots[i].Text, Color: ink, IsLink: slots[i].Kind == MetaSpanKind.Artist));
        })) with
        {
            Size = size, LineHeight = lineHeight,
            Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MaxLines = 1, MinWidth = 0f,
            OnSpanClick = item.InvokeSpan((p, i) =>
            {
                var slots = RowSpanLayout.Artists(p.Track.Artists);
                if ((uint)i < (uint)slots.Count && slots[i].Kind == MetaSpanKind.Artist)
                    h.Go(RouteForRef(slots[i].Uri), slots[i].Name);
            }),
        };
    }

    // ── album cell (ColumnSet.Album) ─────────────────────────────────────────────────────────────────────────────
    // One span, bound: TrackRow.AlbumLink's Dash-when-unnamed rule, and a route the moment the ref resolves even
    // before the name hydrates (RichText.RouteForUri decides on the URI, not the name).
    static Element AlbumCell(BoundItemScope<RowPresentation> item, RowHandlers h) => new SpanTextEl(item.Spans((p, b) =>
    {
        var album = p.Track.Album;
        bool named = album.Name.Length > 0;
        b.Add(new TextSpan(named ? album.Name : TrackRow.Dash, Color: named ? Tok.TextSecondary : Tok.TextTertiary,
            IsLink: RichText.RouteForUri(album.Uri) is not null));
    })) with
    {
        Size = 12f, LineHeight = 16f,
        Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MaxLines = 1,
        Grow = 1f, Basis = 0f, MinWidth = 0f,
        OnSpanClick = item.InvokeSpan((p, _) =>
        {
            var album = p.Track.Album;
            if (RichText.RouteForUri(album.Uri) is { } route) h.Go(route, album.Name.Length > 0 ? album.Name : null);
        }),
    };

    // ── added-by cell (ColumnSet.By) ─────────────────────────────────────────────────────────────────────────────
    // Visible on the raw membership id (Track.AddedBy — always present when there IS an added-by, resolved profile
    // or not); the LABEL prefers the resolved profile's name, falling back to the raw id exactly like the eager
    // TrackRow.AddedByCell — RowPresentation.AddedBy is only the resolved Owner (slice 1), so both fields are read.
    static Element ByCell(BoundItemScope<RowPresentation> item, ColumnSet set)
    {
        Prop<bool> visible = item.Show(p => !string.IsNullOrEmpty(p.Track.AddedBy));
        Prop<string> label = item.Text(p => p.AddedBy?.Name is { Length: > 0 } n ? n : p.Track.AddedBy ?? "");
        if (set.Classic)
            return new TextEl("") with
            {
                Text = label, Size = 14f, LineHeight = 20f,
                Color = item.Color(p => p.State.IsNow ? Tok.AccentTextPrimary : Tok.TextSecondary),
                Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                Grow = 1f, Basis = 0f, MinWidth = 0f, Visible = visible,
            };
        Element avatar = PersonPicture.Bound(label, item.Image(p => p.AddedBy?.Avatar?.Url), Spacing.XXL);
        Element caption = Caption("") with
        {
            Text = label, Color = Tok.TextSecondary, Grow = 1f, Basis = 0f, MinWidth = 0f,
            MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
        };
        return new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Start, Gap = Spacing.S,
            MinWidth = 0f, ClipToBounds = true, Visible = visible,
            Children = [avatar, caption],
        };
    }

    // ── date-added cell (ColumnSet.Date) ─────────────────────────────────────────────────────────────────────────
    static readonly FormatCache<int> DateCache = new();

    static string DateAddedLabelForKey(int dayKey) => dayKey < 0
        ? ""
        : DetailFormat.DateAddedLabel(new DateTimeOffset(DateTime.UnixEpoch.AddDays(dayKey), TimeSpan.Zero));

    static Element DateCell(BoundItemScope<RowPresentation> item, ColumnSet set) => new TextEl("") with
    {
        Text = item.Text(p => p.AddedDayKey, DateCache, DateAddedLabelForKey),
        Size = set.Classic ? 14f : 12f, LineHeight = set.Classic ? 20f : 16f,
        Color = item.Color(p => set.Classic && p.State.IsNow ? Tok.AccentTextPrimary : Tok.TextSecondary),
        Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
        Grow = 1f, Basis = 0f, MinWidth = 0f,
    };

    // ── plays cell (ColumnSet.Plays) ─────────────────────────────────────────────────────────────────────────────
    // A not-yet-out track states Absent (dash) regardless of what the query layer resolved, exactly like
    // TrackRow.Grid's own `notYetOut ? TrackFactState.Absent : playsState` override.
    static TrackFactState EffectivePlaysState(RowPresentation p) => p.Track.IsNotYetOut() ? TrackFactState.Absent : p.PlaysState;

    static readonly FormatCache<(long Count, byte State)> PlaysCache = new();

    static string PlaysLabelFor((long Count, byte State) key) => (TrackFactState)key.State switch
    {
        TrackFactState.Present => TrackRow.PlaysLabel(key.Count),
        TrackFactState.Pending => "000",
        _ => TrackRow.Dash,
    };

    static string? PlaysTipFor(TrackFactState state) => state switch
    {
        TrackFactState.Offline => Loc.Get(Strings.Detail.TrackDetailsOffline),
        TrackFactState.Failed => Loc.Get(Strings.Detail.TrackDetailsUnavailable),
        _ => null,
    };

    static Element PlaysCell(BoundItemScope<RowPresentation> item, ColumnSet set)
    {
        Prop<ColorF> ink = item.Color(p => set.Classic && p.State.IsNow ? Tok.AccentTextPrimary : Tok.TextTertiary);
        Element leaf = new TextEl("") with
        {
            Text = item.Text(p => (p.Track.PlayCount, (byte)EffectivePlaysState(p)), PlaysCache, PlaysLabelFor),
            Size = set.Classic ? 14f : 12f, LineHeight = set.Classic ? 20f : 16f, Color = ink,
            Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, Shrink = 0f,
            Visible = item.Show(p => EffectivePlaysState(p) != TrackFactState.Pending),
        };
        // The Loadable-driven SkelRegionEl shimmer (TrackRow.PlayCountCell's `.Pending(...)`) needs a per-row
        // Loadable<T> instance this template has no use for — a bound row states Pending as a VALUE
        // (RowPresentation.PlaysState) already, so the placeholder is a plain presence-gated bar instead.
        Element shimmer = new BoxEl
        {
            Width = 28f, Height = 12f, Corners = CornerRadius4.All(4f), Fill = Tok.FillControlSecondary,
            Visible = item.Show(p => EffectivePlaysState(p) == TrackFactState.Pending),
        };
        Prop<string?> tip = Prop.Of(() => PlaysTipFor(EffectivePlaysState(item.Item.Value)));
        return ToolTip.Wrap(new BoxEl { ZStack = true, Children = [leaf, shimmer] }, tip);
    }

    // ── tempo/key cell (ColumnSet.Tempo, ShowTempo gate) ─────────────────────────────────────────────────────────
    static readonly FormatCache<int> BpmCache = new();

    static Element TempoCell(BoundItemScope<RowPresentation> item, ColumnSet set)
    {
        Prop<ColorF> ink = item.Color(p => set.Classic && p.State.IsNow ? Tok.AccentTextPrimary : Tok.TextSecondary);
        Prop<ColorF> tertiaryInk = item.Color(p => set.Classic && p.State.IsNow ? Tok.AccentTextPrimary : Tok.TextTertiary);
        Element swatch = new BoxEl
        {
            Width = 6f, Height = 6f, Corners = CornerRadius4.All(1.5f), Opacity = 0.85f, AlignSelf = FlexAlign.Center,
            Fill = item.Color(p => WaveePalette.DataDotInk(p.Track.CamelotColor ?? 0u, Tok.Theme)),
            Visible = item.Show(p => p.Track.CamelotColor is not null),
        };
        Element bpm = new TextEl("") with
        {
            Text = item.Text(p => (int)Math.Round((p.Track.TempoBpm ?? 0d) * 10d), BpmCache, static k => TrackExpandedFacts.Bpm(k / 10d)),
            Size = set.Classic ? 14f : 12f, LineHeight = set.Classic ? 20f : 16f, Color = ink,
            Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
        };
        Prop<bool> keyVisible = item.Show(p => TrackExpandedFacts.KeyLabel(p.Track.CamelotCode, p.Track.MusicalKey) is { Length: > 0 });
        Element dot = new TextEl("·") with
        {
            Size = set.Classic ? 14f : 12f, LineHeight = set.Classic ? 20f : 16f, Color = tertiaryInk,
            Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, Visible = keyVisible,
        };
        Element key = new TextEl("") with
        {
            Text = item.Text(p => TrackExpandedFacts.KeyLabel(p.Track.CamelotCode, p.Track.MusicalKey) ?? ""),
            Size = set.Classic ? 14f : 12f, LineHeight = set.Classic ? 20f : 16f, Color = tertiaryInk,
            Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, Visible = keyVisible,
        };
        return new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.XS,
            Visible = item.Show(p => (p.Track.TempoBpm ?? 0d) > 0d),
            Children = [swatch, bpm, dot, key],
        };
    }

    // ── duration cell (always present) ───────────────────────────────────────────────────────────────────────────
    static readonly FormatCache<long> AvailableAtCache = new();

    static Element DurationCell(BoundItemScope<RowPresentation> item, ColumnSet set)
    {
        var scopeItem = item.Item;
        Prop<string> text = Prop.Of(() =>
        {
            var p = scopeItem.Value;
            // A track whose identity has not landed has no duration either, and DurationMmSs(0) renders that as a
            // confident "0:00" — a wrong ANSWER sitting next to a title that is still loading. A dash states the same
            // thing honestly and, unlike a shimmer sibling, changes nothing about this cell's shape (see TitleCell:
            // an extra layout child here grows the row).
            if (p.IsSkeleton) return TrackRow.Dash;
            if (!p.Track.IsNotYetOut()) return FormatCache.DurationMmSs(p.Track.DurationMs);
            return p.Track.AvailableAt is { } live && live > DateTimeOffset.UtcNow
                ? AvailableAtCache.Get(live.ToUnixTimeSeconds(), static s => DetailFormat.ShortDate(DateTimeOffset.FromUnixTimeSeconds(s)))
                : TrackRow.Dash;
        });
        return new TextEl("") with
        {
            Text = text, Size = set.Classic ? 14f : 12f, LineHeight = set.Classic ? 20f : 16f,
            Color = item.Color(p => set.Classic && p.State.IsNow ? Tok.AccentTextPrimary
                                     : p.Track.IsNotYetOut() ? Tok.TextTertiary : Tok.TextSecondary),
            Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
        };
    }

    // ── video/more lane (ColumnSet.Video) ────────────────────────────────────────────────────────────────────────
    // ClickRequestsContext/Cursor/HoverOpacity stay STATIC (mount-time only, not a bound Prop channel per the
    // engine's Element shape) — a skeleton row's more-glyph therefore keeps its hover affordance mounted even though
    // Opacity gates it to 0 at rest; the same static-vs-bound split slice 2's number cell already accepted for
    // Cursor/OnClick on its transport layer.
    static Element VideoMoreCell(BoundItemScope<RowPresentation> item)
    {
        Element film = Icon(Icons.Movie, 13f, Tok.TextTertiary) with { Visible = item.Show(p => p.HasVideo) };
        Element rest = new BoxEl { Grow = 1f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, HoverOpacity = 0f, Children = [film] };
        Element more = new BoxEl
        {
            Grow = 1f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Opacity = item.Opacity(p => !p.HasVideo ? TrackRow.MoreRestOpacity : 0f),
            HoverOpacity = 1f, Cursor = CursorId.Hand, ClickRequestsContext = true, Role = AutomationRole.Button,
            BlocksDragArm = true,
            Children = [Icon(Icons.More, 16f, Tok.TextSecondary)],
        };
        return new BoxEl { ZStack = true, Children = [rest, more] };
    }

    // ── dedicated "…" overflow cell (ColumnSet.Actions, when Video is off) ───────────────────────────────────────
    static Element MoreCell(BoundItemScope<RowPresentation> item)
    {
        Element button = new BoxEl
        {
            Width = 28f, Height = 28f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Corners = Radii.Circle(28f), HoverScale = WaveeMotion.ScaleEmphatic.Hover, PressScale = WaveeMotion.ScaleEmphatic.Press,
            Cursor = CursorId.Hand, ClickRequestsContext = true, Role = AutomationRole.Button, BlocksDragArm = true,
            Children = [Icon(Icons.More, 16f, Tok.TextSecondary)],
        }.Interactive(Interaction.Subtle);
        return new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Opacity = item.Opacity(p => p.IsSkeleton ? 0f : TrackRow.MoreRestOpacity),
            HoverOpacity = 1f,
            Children = [button],
        };
    }

    // ── expand chevron (ColumnSet.Expand) ────────────────────────────────────────────────────────────────────────
    static Element ExpandCell(BoundItemScope<RowPresentation> item, RowHandlers h) => new BoxEl
    {
        Width = Spacing.XXL, Height = Spacing.XXL, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Corners = Radii.ControlAll, HoverFill = Tok.FillControlSecondary,
        Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand,
        FocusVisualMargin = new Edges4(1f, 1f, 1f, 1f),
        OnClick = item.Invoke(h.ToggleExpanded),
        BlocksDragArm = true,
        Children =
        [
            Icon(Icons.ChevronRight, 12f, Tok.TextSecondary) with
            {
                Text = item.Text(p => p.IsExpanded ? Icons.ChevronDown : Icons.ChevronRight),
                Color = item.Color(p => p.IsExpanded ? Tok.AccentTextPrimary : Tok.TextSecondary),
            },
        ],
    };
}
