using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Core;
using Wavee.Features.Detail;

namespace Wavee;

/// <summary>
/// The stage's RIGHT region: two panes cross-faded IN PLACE, with the pane pivot in the band along its bottom edge.
///
/// <para><b>Both panes are always mounted.</b> The switch is an OPACITY cross-fade (<c>MotionTok.ControlNormal</c> =
/// the WinUI 250 ms rung; reduced motion snaps through the token's own policy, never through a branch here) and
/// <c>HitTestVisible</c> follows the active one — which excludes the whole inactive subtree from hit testing, so the
/// invisible pane cannot eat a click. Conditionally MOUNTING the panes instead would tear down and rebuild
/// <c>LyricsView</c>'s measured document (and the queue's reorder lane) on every flip, and would change the hook shape
/// of this component between renders, which is the reconciler crash the animation canon exists to prevent.</para>
///
/// <para><b>The pivot band carries no veil.</b> It used to, back when the scrim flipped with the theme but the ink did
/// not, so every region of on-media ink needed its own dark box. Scrim and ink now flip together (<c>StageInk</c>) and
/// the scrim's own bottom deepening resolves under this band across a feather hundreds of DIP long — so the pivot row
/// is just a row. The one
/// local shade left on this side is the QUEUE pane's (<c>StageChrome.PaneShade</c>), which is genuinely local: it is
/// mounted and cross-faded with the pane whose hover glass needs a floor, and it comes up out of ZERO on its left edge.</para>
/// </summary>
sealed class StagePanes : Component
{
    // No layout/viewport signal is held any more: the reading column is MEASURED (see LyricsColumn), so this component
    // has nothing left to predict and nothing to re-solve on resize.
    public override Element Render()
    {
        var ui = UseContext(ShellUi.Slot);
        var b = UseContext(PlaybackBridge.Slot);
        int pane = StagePane.Current.Value;                 // subscribe → the cross-fade re-renders exactly this component
        bool lyrics = pane == StagePane.Lyrics;
        var accent = StageChrome.AccentFor(b?.CurrentTrack.Value);

        return new BoxEl
        {
            Direction = 1, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f,
            Children =
            [
                new BoxEl
                {
                    Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, ZStack = true, ClipToBounds = true,
                    Children =
                    [
                        new BoxEl
                        {
                            Key = "pane:lyrics",
                            AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch,
                            Opacity = lyrics ? 1f : 0f, Transition = MotionTok.ControlNormal,
                            HitTestVisible = lyrics,
                            Children = [LyricsColumn(ui)],
                        },
                        new BoxEl
                        {
                            Key = "pane:queue",
                            AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch,
                            Opacity = lyrics ? 0f : 1f, Transition = MotionTok.ControlNormal,
                            HitTestVisible = !lyrics,
                            Gradient = StageChrome.PaneShade(),
                            Children = [Embed.Comp(() => new StageQueuePane())],
                        },
                    ],
                },
                Pivot(lyrics, accent),
            ],
        };
    }

    // ── the lyrics pane: the SAME LyricsView the rail mounts, on the stage's INK ─────────────────────────────────────
    // Its behaviour is untouched — the blur-distance treatment, the wipe, click-to-seek, all of it. Two things are
    // passed in: the visibility gate (which parks the 16 ms ticker while the QUEUE pane is up — two panes are mounted,
    // and exactly one of them may be ticking), and `onMedia`, which swaps the view's whole ink ladder from the PAGE's
    // theme rungs onto the STAGE's (StageInk, via LyricsInk). Both follow the theme; what differs is which ground they
    // are solved against, because the stage owns its own veil. The rail keeps the default — it is on a panel.
    // THE READING COLUMN IS MEASURED, NEVER PREDICTED. It used to author its own Width from a viewport FORMULA —
    // `viewportW − StageLayout.LayoutWidth − ColumnGutter`, clamped to ColumnMaxW — which is a second, private copy of
    // the band's arithmetic. Any disagreement between that copy and the real layout lands as a column authored WIDER
    // than the pane it sits in; the column shrinks (Shrink = 1, MinWidth = 0) flush to the window edge while the lyric
    // rows inside it keep the width they were measured at (Shrink = 0, Trim.None by design), and the text clips
    // mid-word. The identity's grow leak was one such disagreement — but the class of bug is the formula itself, so
    // the formula is gone: the column now GROWS into whatever the pane actually gives it, capped by MaxWidth, centred
    // by the wrapper, with the gutter spelled as real padding. FlexLayout re-measures a grown row child at its FINAL
    // main size, so the rows can never carry a pre-shrink width.
    Element LyricsColumn(ShellUi? ui) => new BoxEl
    {
        Direction = 0, Grow = 1f, Shrink = 1f, MinHeight = 0f, MinWidth = 0f,
        // LEFT-ANCHORED, not centred. Centring is what converted a pane region wider than its slot into text pushed
        // OFF-SCREEN — the column sat at x=181 in a 1062-wide pane and ran past the window edge. Left-anchored, the
        // worst an over-wide pane can do is leave empty air on the right: visually wrong, never unreadable. It is also
        // the reference composition — the reading column begins a fixed distance after the identity column rather than
        // floating in the middle of whatever room is left.
        Justify = FlexJustify.Start, AlignItems = FlexAlign.Stretch,
        // The gutter is now the column's TRAILING air only — the leading air is the band's RegionGapW, so a
        // half-gutter here would double-count it. The pivot band's own height stays reserved at the bottom so the last
        // lyric line clears the "Lyrics · Queue" row exactly the way the queue pane reserves it.
        Padding = new Edges4(0f, 0f, ImmersiveLyricsSurface.ColumnGutter, StageChrome.PivotBandH),
        Children =
        [
            new BoxEl
            {
                OnBoundsChanged = ImmersiveLyricsSurface.RectProbe("lyricscolumn"),
                Direction = 1, Grow = 1f, Shrink = 1f, MinHeight = 0f, MinWidth = 0f,
                MaxWidth = ImmersiveLyricsSurface.ColumnMaxW,
                Children =
                [
                    Embed.Comp(() => new LyricsView(large: true, onMedia: true, visible: () =>
                        (ui is null || ui.ImmersiveLyrics.Value) && StagePane.Current.Value == StagePane.Lyrics)),
                ],
            },
        ],
    };

    // ── the pane pivot: "Lyrics · Queue", bottom-right of the pane region ────────────────────────────────────────────

    static Element Pivot(bool lyrics, ColorF accent) => new BoxEl
    {
        OnBoundsChanged = ImmersiveLyricsSurface.RectProbe("pivot"),
        Direction = 0, Height = StageChrome.PivotBandH, Shrink = 0f,
        AlignItems = FlexAlign.End, Justify = FlexJustify.End, Gap = ContextBandLayout.PivotGap,
        Padding = new Edges4(Spacing.XXL, 0f, Spacing.XXL, Spacing.L),
        Children =
        [
            StageChrome.PivotLink(Loc.Get(Strings.Player.Lyrics), lyrics, accent,
                () => StagePane.Current.Value = StagePane.Lyrics),
            StageChrome.PivotLink(Loc.Get(Strings.Player.Queue), !lyrics, accent,
                () => StagePane.Current.Value = StagePane.Queue),
        ],
    };
}

/// <summary>
/// The stage's QUEUE pane — the rail's queue model on the stage's material.
///
/// <para>Everything behind it is the existing machinery: <c>PlaybackBridge.Queue</c> and its
/// <c>QueueBucket</c>/<c>QueueProvider</c> split, <c>QueueOrder.Move</c>/<c>Remove</c> for the optimistic snapshot, the
/// player's <c>MoveQueueItemAsync</c>/<c>RemoveQueueItemAsync</c>/<c>SkipToQueueItemAsync</c> commands,
/// <c>Reorderable</c> for the one section whose order is the user's, and <c>Menus.QueueEntry</c> for the row menu. What
/// changed is the SKIN: one continuous 56-DIP list on the on-media ladder, a "Playing next · from {context}" header
/// over the app's 20 × 2 accent rule, and hover GLASS instead of the rail's row-hover plate.</para>
///
/// <para><b>The ∞ Autoplay row is real.</b> It reads and writes <c>WaveeSettings.AutoplayEnabled</c> and bumps
/// <c>PlaybackPrefs</c>, which is the same seam the Settings → Playback toggle and the rail's queue pill drive, and
/// which <c>LiveSessionHost</c> binds to <c>PlaybackController.AutoplayEnabled</c> as a late-bound lambda — so a flip
/// here lands on the very next continuation decision. It is not a per-account Spotify option (nothing writes
/// <c>options.autoplay</c> on the Connect wire) and it does not truncate an in-flight station; both are properties of
/// the existing seam, not of this row.</para>
/// </summary>
sealed class StageQueuePane : Component
{
    const float RowH = 56f;
    const float RowArt = 38f;
    const float TimeW = 44f;
    const float GripW = 24f;
    const int PageSize = 100;

    readonly Signal<int> _pages = new(1);

    readonly Reorderable _reorder = new(WaveeDragKinds.Resource)
    {
        ItemExtent = RowH,
        Spacing = 0f,
        DragStyle = new DragVisualStyle { Lift = DragLift.Stationary, Opacity = Drag.SourceDimOpacity },
        RequireDropOnList = true,
    };

    public override Element Render()
    {
        var b = UseContext(PlaybackBridge.Slot);
        var lib = UseContext(LibraryBridge.Slot);
        var svc = UseContext(Services.Slot);
        var go = UseContext(HistoryStore.NavCtx);
        var acts = UseContext(ActionServices.Slot);
        var menuOverlay = UseContext(Overlay.Service);

        var serverQueue = b?.Queue.Peek() ?? Array.Empty<QueueEntry>();
        var display = UseSignal<IReadOnlyList<QueueEntry>>(serverQueue);
        UseSignalEffect(() =>
        {
            if (b is null) return;
            display.Value = b.Queue.Value;
        });

        var (autoplay, setAutoplay) = UseState(svc?.Settings.Get(WaveeSettings.AutoplayEnabled) ?? true);
        int prefsEpoch = PlaybackPrefs.Epoch.Value;
        UseEffect(() => setAutoplay(svc?.Settings.Get(WaveeSettings.AutoplayEnabled) ?? true), prefsEpoch);

        string ctxUri = b?.CurrentContext.Value ?? "";
        var ctxName = UseResource(ct => ResolveContextNameAsync(svc, ctxUri, ct), (string?)null, ctxUri).Loadable;
        UseEffect(() => { _pages.Value = 1; }, ctxUri);

        if (b is null) return new BoxEl();

        var track = b.CurrentTrack.Value;
        var accent = StageChrome.AccentFor(track);

        // Forward-looking only, exactly like the rail panel: the user queue, then the context continuation, then the
        // autoplay tail (dimmed, and only while the toggle is on).
        var queue = display.Value;
        var userQueue = new List<QueueEntry>();
        var ctxUp = new List<QueueEntry>();
        var autoUp = new List<QueueEntry>();
        string? curUri = track?.Uri;
        foreach (var e in queue)
        {
            if (curUri is { Length: > 0 } && e.Track.Uri == curUri) continue;
            switch (e.Bucket)
            {
                case QueueBucket.UserQueue: userQueue.Add(e); break;
                case QueueBucket.NextUp: (e.Provider == QueueProvider.Autoplay ? autoUp : ctxUp).Add(e); break;
            }
        }

        bool viewer = PlayerBarContent.RemoteDevice(b) is not null;
        bool showTrackArtwork = !AppearancePrefs.TrackArtworkHidden(svc?.Settings);
        string? source = ctxName.Value.Value is { Length: > 0 } rn ? rn : ImmediateContextName(ctxUri);

        ConfigureReorder(b, acts, display, userQueue);

        var content = new List<Element>(6)
        {
            AutoplayRow(autoplay, accent, () =>
            {
                if (svc is null) return;
                svc.Settings.Set(WaveeSettings.AutoplayEnabled, !autoplay);
                PlaybackPrefs.Bump();
            }),
        };

        if (userQueue.Count > 0)
            content.Add((BoxEl)_reorder.List(
                Rows("q", userQueue, b, lib, go, display, removable: !viewer, dim: false, acts, menuOverlay,
                     showTrackArtwork, reorder: viewer ? null : _reorder))
                with { Grow = 0f, Key = "stagelane:q" });
        if (ctxUp.Count > 0)
            content.Add(Rows("u", ctxUp, b, lib, go, display, removable: !viewer, dim: false, acts, menuOverlay,
                showTrackArtwork));
        if (autoplay && autoUp.Count > 0)
            content.Add(Rows("a", autoUp, b, lib, go, display, removable: !viewer, dim: true, acts, menuOverlay,
                showTrackArtwork));
        if (userQueue.Count == 0 && ctxUp.Count == 0 && autoUp.Count == 0)
            content.Add(new BoxEl
            {
                Padding = new Edges4(0f, Spacing.XXL, 0f, 0f),
                Children = [new TextEl(Loc.Get(Strings.Player.QueueEmpty))
                {
                    Size = 14f, LineHeight = 20f, Color = StageInk.InkTertiary,
                }],
            });

        Element body = new BoxEl
        {
            Direction = 1, MinHeight = 0f,
            Padding = new Edges4(0f, 0f, 0f, StageChrome.PivotBandH),
            Children = content.ToArray(),
        };
        // With no user queue there is no lane to aim at, and a drop that lands nowhere is the silent failure the drag
        // campaign exists to kill: item count 0 ⇒ every position resolves to slot 0, i.e. the play-next insert.
        if (userQueue.Count == 0)
            body = (BoxEl)_reorder.List(body) with { Grow = 0f, Key = "stagelane:empty" };

        return new BoxEl
        {
            Direction = 1, Grow = 1f, MinHeight = 0f, MinWidth = 0f, ClipToBounds = true,
            Padding = new Edges4(Spacing.XXL, Spacing.XXL, Spacing.XXL, 0f),
            Children =
            [
                Header(source, accent),
                new ScrollEl
                {
                    Grow = 1f, MinHeight = 0f,
                    AutoEdgeFade = true,
                    ScrollKey = "stagequeue",
                    Content = body,
                },
            ],
        };
    }

    // ── header: "Playing next"  from {context}  +  the 20 × 2 accent rule ────────────────────────────────────────────

    static Element Header(string? source, ColorF accent) => new BoxEl
    {
        Direction = 1, Shrink = 0f, Gap = Spacing.S,
        Padding = new Edges4(0f, 0f, 0f, Spacing.M),
        Children =
        [
            new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S, MinWidth = 0f,
                Children = source is { Length: > 0 }
                    ?
                    [
                        new TextEl(Loc.Get(Strings.Player.PlayingNext))
                        {
                            Size = 20f, LineHeight = 28f, Weight = 600, Color = StageInk.Ink,
                            Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, Shrink = 0f,
                        },
                        new TextEl(Strings.Player.FromContext(source))
                        {
                            Size = 12f, LineHeight = 16f, Color = StageInk.InkTertiary,
                            Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                            MinWidth = 0f, Shrink = 1f,
                        },
                    ]
                    :
                    [
                        new TextEl(Loc.Get(Strings.Player.PlayingNext))
                        {
                            Size = 20f, LineHeight = 28f, Weight = 600, Color = StageInk.Ink,
                            Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, Shrink = 0f,
                        },
                    ],
            },
            StageChrome.SectionRule(accent),
        ],
    };

    // ── the ∞ Autoplay row ───────────────────────────────────────────────────────────────────────────────────────────

    static Element AutoplayRow(bool on, ColorF accent, Action toggle) => new BoxEl
    {
        Key = "stage:autoplay",
        Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M, MinHeight = 48f,
        Padding = new Edges4(Spacing.S, 0f, Spacing.S, 0f),
        Corners = Radii.ControlAll,
        Fill = StageInk.GlassRest, HoverFill = StageInk.GlassHover, PressedFill = StageInk.GlassPressed,
        BrushTransitionMs = WaveeMotion.Faster,
        Role = AutomationRole.CheckBox, Focusable = true, Cursor = CursorId.Hand, OnClick = toggle,
        Children =
        [
            new TextEl("∞")
            {
                Size = 17f, LineHeight = 22f, Weight = 600,
                Color = on ? accent : StageInk.InkTertiary,
                Width = 22f,
            },
            new BoxEl
            {
                Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Gap = Spacing.XXS,
                Children =
                [
                    new TextEl(Loc.Get(Strings.Player.Autoplay))
                    {
                        Size = 13f, LineHeight = 18f, Weight = 600,
                        Color = on ? StageInk.Ink : StageInk.InkSecondary,
                    },
                    new TextEl(Loc.Get(Strings.Player.AutoplayHint))
                    {
                        Size = 12f, LineHeight = 16f, Color = StageInk.InkTertiary,
                        Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                    },
                ],
            },
        ],
    };

    // ── rows ─────────────────────────────────────────────────────────────────────────────────────────────────────────

    Element Rows(string tag, List<QueueEntry> entries, PlaybackBridge b, LibraryBridge? lib,
                 Action<string, string?>? go, Signal<IReadOnlyList<QueueEntry>> display, bool removable, bool dim,
                 ActionServices? acts, IOverlayService? menuOverlay, bool showTrackArtwork,
                 Reorderable? reorder = null)
    {
        int n = Math.Min(entries.Count, Math.Max(1, _pages.Value) * PageSize);
        var kids = new List<Element>(n + 1);
        for (int i = 0; i < n; i++)
        {
            int item = reorder is { } ro ? ro.ItemAt(i) : i;
            if ((uint)item >= (uint)entries.Count) item = i;
            var row = Row(b, lib, go, display, entries[item], item, entries, removable, dim, acts, menuOverlay,
                          ownDrag: reorder is null, gripped: reorder is not null, showArtwork: showTrackArtwork);
            kids.Add(reorder is { } r
                ? (BoxEl)r.Item(item, row, key: RowKey(entries[item])) with { Direction = 1 }
                : row);
        }
        if (entries.Count > n)
            kids.Add(ShowMore(tag, entries.Count - n, () => _pages.Value = _pages.Peek() + 1));
        return new BoxEl { Key = "stagesec:" + tag, Direction = 1, Children = kids.ToArray() };
    }

    Element Row(PlaybackBridge b, LibraryBridge? lib, Action<string, string?>? go,
                Signal<IReadOnlyList<QueueEntry>> display, QueueEntry entry, int index, IReadOnlyList<QueueEntry> section,
                bool removable, bool dim, ActionServices? acts, IOverlayService? menuOverlay,
                bool ownDrag, bool gripped, bool showArtwork)
    {
        var t = entry.Track;
        int count = section.Count;

        void Remove()
        {
            _ = b.Player.RemoveQueueItemAsync(entry.ItemId);
            display.Value = QueueOrder.Remove(display.Peek(), entry);
        }

        bool canMove = removable && !entry.ItemId.IsNone;
        void Move(int delta)
        {
            if (!canMove) return;
            MoveInSection(b, display, section, index, index + delta);
        }

        Element[] artwork = showArtwork
            ?
            [
                new BoxEl
                {
                    Width = RowArt, Height = RowArt, Shrink = 0f, ZStack = true, ClipToBounds = true,
                    Corners = Radii.ControlAll,
                    Children =
                    [
                        Surfaces.Artwork(t.Image, t.Id.GetHashCode() & 0x7fffffff, RowArt, RowArt, Radii.Control, decodePx: 96),
                        NowPlayingOverlay.Create(t.Uri, () => PlayQueueEntry(b, entry), 26f, cover: true, RowArt, centered: true)
                            .Skeletonized(false),
                    ],
                },
            ]
            : Array.Empty<Element>();

        var row = new BoxEl
        {
            Key = RowKey(entry) + ":art=" + showArtwork,
            Draggable = ownDrag
                ? Drag.Source(WaveeDragKinds.Resource, () => WaveeResourceDragPayload.ForTrack(t))
                : null,
            Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M, MinHeight = RowH,
            Padding = new Edges4(Spacing.S, 0f, Spacing.S, 0f),
            Corners = Radii.ControlAll,
            Fill = StageInk.GlassRest,
            HoverFill = StageInk.GlassHover,
            PressedFill = StageInk.GlassPressed,
            BrushTransitionMs = WaveeMotion.Faster,
            PressScale = WaveeMotion.ScaleSubtle.Press,
            Opacity = dim ? 0.68f : 1f,
            Role = AutomationRole.Button, Cursor = CursorId.Hand, Focusable = true, AllowFocusOnInteraction = false,
            OnClick = () => PlayQueueEntry(b, entry),
            Enter = new EnterExit(Dy: 6f, Opacity: 0f, Active: true),
            Exit = new EnterExit(Dy: -4f, Opacity: 0f, Active: true),
            Layout = LayoutTransition.Slide,
            Children =
            [
                // The drag GRIP: hidden at rest, revealed on ROW hover (the recorder drives the fade off the nearest
                // interactive ancestor). It is an affordance, not a control — the whole row is the drag source.
                new BoxEl
                {
                    Width = GripW, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                    Opacity = 0f, HoverOpacity = gripped ? 1f : 0f, HitTestVisible = false,
                    Children = [new TextEl(Icons.GripperBar) { Size = 14f, FontFamily = Theme.IconFont, Color = StageInk.InkTertiary }],
                },
                ..artwork,
                new BoxEl
                {
                    Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Justify = FlexJustify.Center, Gap = 1f,
                    Children =
                    [
                        // The app's track-title rung (14 / 20 / 600). The stage deliberately does NOT re-mint the
                        // fractional 13.5 the token convergence removed.
                        new TextEl(t.Title)
                        {
                            Size = 14f, LineHeight = 20f, Weight = 600, Color = StageInk.Ink,
                            Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                        },
                        new TextEl(DetailFormat.ArtistNames(t.Artists))
                        {
                            Size = 12f, LineHeight = 16f, Color = StageInk.InkSecondary,
                            Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                        },
                    ],
                },
                // TABULAR by geometry, not by font feature (the text seam carries no numeric-style knob): a fixed
                // end-aligned slot, so every duration's colon lands on the same x.
                new BoxEl
                {
                    Width = TimeW, Shrink = 0f, Direction = 0, Justify = FlexJustify.End, AlignItems = FlexAlign.Center,
                    Children = [new TextEl(PlayerBarContent.Fmt(t.DurationMs))
                    {
                        Size = 12f, LineHeight = 16f, Color = StageInk.InkTertiary, Wrap = TextWrap.NoWrap,
                    }],
                },
                removable && !entry.ItemId.IsNone
                    ? new BoxEl
                    {
                        Opacity = 0f, HoverOpacity = 1f, Shrink = 0f, BlocksDragArm = true,
                        Children =
                        [
                            StageChrome.Glyph(Icons.ChromeClose, Remove, WaveeCta.IconButtonSize, 12f),
                        ],
                    }
                    : new BoxEl { Width = WaveeCta.IconButtonSize, Shrink = 0f },
            ],
        };

        // Right-click / Menu key: the SAME queue-entry menu the rail's rows raise — "Play now", the ±1 moves and
        // "Remove from queue" all reuse the closures above.
        if (acts is { } a && menuOverlay is { } svc)
            return row.WithContextMenu(svc, () => Menus.QueueEntry(
                a, entry, canMove ? (Action)Remove : null, () => PlayQueueEntry(b, entry),
                canMove && index > 0 ? () => Move(-1) : null,
                canMove && index + 1 < count ? () => Move(1) : null));
        return row;
    }

    static Element ShowMore(string tag, int remaining, Action more) => new BoxEl
    {
        Key = "stagemore:" + tag,
        Direction = 0, MinHeight = 40f, Gap = Spacing.S, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Margin = new Edges4(0f, Spacing.XS, 0f, Spacing.XXS),
        Corners = Radii.ControlAll,
        Fill = StageInk.GlassRest, HoverFill = StageInk.GlassHover, PressedFill = StageInk.GlassPressed,
        Role = AutomationRole.Button, Cursor = CursorId.Hand, Focusable = true, OnClick = more,
        Layout = LayoutTransition.Slide,
        Children =
        [
            new TextEl(Icons.ChevronDown) { Size = 12f, FontFamily = Theme.IconFont, Color = StageInk.InkSecondary },
            new TextEl($"·  {remaining}") { Size = 12f, LineHeight = 16f, Color = StageInk.InkTertiary },
        ],
    };

    // ── the drag/drop wiring: identical in mechanism to QueuePanel's, pointed at THIS render's rows ──────────────────

    void ConfigureReorder(PlaybackBridge b, ActionServices? acts, Signal<IReadOnlyList<QueueEntry>> display,
                          List<QueueEntry> userQueue)
    {
        int shown = Math.Min(userQueue.Count, Math.Max(1, _pages.Peek()) * PageSize);
        _reorder.Scene = Context.Scene;
        _reorder.RequestRender = Context.RequestRerender;
        _reorder.ItemCount = shown;
        _reorder.ItemOf = slot => (uint)slot < (uint)userQueue.Count
            ? WaveeResourceDragPayload.ForTrack(userQueue[slot].Track)
            : null;
        _reorder.OnReorder = (from, to) => MoveInSection(b, display, userQueue, from, to);
        _reorder.OnCrossCommit = (payload, _, _, _, slot) => InsertAtSlot(b, acts, payload, slot);
        _reorder.CanAcceptForeign = static p => WaveeResourceDrop.CanDepositTracks(p);
        _reorder.ForeignRefusalCaption = static p => WaveeResourceDrag.Unwrap(p) is { } r
            ? Loc.Get(r.Kind == WaveeResourceKind.Artist ? Strings.Drag.CantAddArtist : Strings.Drag.NothingToAdd)
            : null;
        _reorder.ForeignCaption = static (_, _) => Loc.Get(Strings.Drag.AddToQueue);
    }

    static void MoveInSection(PlaybackBridge b, Signal<IReadOnlyList<QueueEntry>> display,
                              IReadOnlyList<QueueEntry> section, int from, int to)
    {
        if ((uint)from >= (uint)section.Count) return;
        int at = Math.Clamp(to, 0, section.Count - 1);
        if (at == from) return;
        var entry = section[from];
        if (entry.ItemId.IsNone) return;
        _ = b.Player.MoveQueueItemAsync(entry.ItemId, at);
        display.Value = QueueOrder.Move(display.Peek(), section, from, at);
    }

    static void InsertAtSlot(PlaybackBridge b, ActionServices? acts, object? payload, int slot)
    {
        if (WaveeResourceDrag.Unwrap(payload) is not { CanCopyTracks: true } resource) return;
        _ = Run();

        async Task Run()
        {
            IReadOnlyList<Track> tracks;
            try { tracks = await resource.ResolveTracksAsync().ConfigureAwait(false); }
            catch { return; }
            int n = DetailQueueActions.InsertAt(b.Player, tracks, slot);
            if (n <= 0) return;
            int total = tracks.Count;
            acts?.Post?.Invoke(() => Toast.Show(
                n < total
                    ? Strings.Detail.AddedFirstToQueue(Strings.Detail.SongCount(n))
                    : Strings.Detail.AddedToQueue(Strings.Detail.SongCount(n)),
                new ToastOptions { Severity = InfoBarSeverity.Success }));
        }
    }

    static string RowKey(in QueueEntry e) => e.ItemId.IsNone ? "se" + e.EntryId : "si" + e.ItemId.Value;

    static void PlayQueueEntry(PlaybackBridge b, QueueEntry entry)
    {
        if (entry.ItemId.IsNone)
        {
            TrackRow.Invoke(b, entry.Track, () => b.Player.PlayTrackAsync(entry.Track));
            return;
        }
        TrackRow.Invoke(b, entry.Track, () => b.Player.SkipToQueueItemAsync(entry.ItemId));
    }

    static string? ImmediateContextName(string uri)
        // Both collection shapes (`spotify:collection:*` and `spotify:user:{id}:collection`) are ONE kind to the parser
        // (hydration-facade-design.md §1.1), which is what the `Contains(":collection")` probe was approximating.
        => uri.Length > 0 && EntityUri.KindOf(uri) == EntityKind.Collection
            ? Loc.Get(Strings.Player.LikedSongs)
            : null;

    static async Task<string?> ResolveContextNameAsync(Services? svc, string uri, CancellationToken ct)
    {
        if (svc is null || uri.Length == 0) return null;
        try
        {
            switch (EntityUri.KindOf(uri))   // the ONE parser decides which read answers the context name
            {
                case EntityKind.Collection: return Loc.Get(Strings.Player.LikedSongs);
                case EntityKind.Playlist: return (await svc.Library.GetPlaylistAsync(uri, HydrationLevel.Identity, ct).ConfigureAwait(false))?.Name;
                case EntityKind.Album: return (await svc.Library.GetAlbumAsync(uri, HydrationLevel.Identity, ct).ConfigureAwait(false))?.Name;
                case EntityKind.Artist: return (await svc.Library.GetArtistAsync(uri, HydrationLevel.Identity, ct).ConfigureAwait(false))?.Name;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { }
        return null;
    }
}
