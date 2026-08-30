using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Core;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

// The right-rail QUEUE panel — ground-up rework on the WaveeMusic shape (C:\WAVEE\WaveeMusic QueueControl), which the
// old anchor-list design never got right:
//
//   • The CURRENT track is a pinned, bordered NOW-PLAYING CARD above the sections — it is NEVER a row inside a list,
//     so there is no anchor index, no scroll pinning, no bucket-shift bookkeeping, and no way for it to render as a
//     dimmed history row.
//   • NO history section. The queue is forward-looking only: "Next in queue" (user queue + Clear) → "Next up"
//     (context, with the "Playing from …" breadcrumb above the card) → "Autoplay" (dimmed tail, when enabled).
//   • Sections are PLAIN KEYED ROWS inside one ScrollEl — no virtualization, no bound-slot recycling. A queue is at
//     most a few hundred cheap rows; realizing them keyed means the reconciler's Enter/Exit/FLIP transitions animate
//     every change natively: the consumed row exits, the rest slide up, the card cross-fades to the new track. The
//     old index-bound recycled list could never express motion (slots rebind in place) and its recycled subtrees were
//     the source of the frozen-bind/wrong-track corruption.
sealed class QueuePanel : Component
{
    const float QueueArt = 34f;
    const float RowExtent = 44f;   // the queue row's resting main-axis extent (its MinHeight) — the reorder slot pitch
    const int PageSize = 100;   // rows REALIZED per visual page — the underlying queue is never truncated; the section
                                // paginates visually with an explicit "Show more (N left)" affordance per page.

    // Visual page counts per section (instance-lived; reset when the playback context changes).
    readonly Signal<int> _queuePages = new(1);
    readonly Signal<int> _upPages = new(1);
    readonly Signal<int> _autoPages = new(1);
    readonly SwipeGroup _swipeGroup = new();

    // "Next in queue" is the one REORDERABLE + INSERTABLE section (locked decision: the user queue is the only part of
    // the queue whose order is the user's to choose — a context continuation and the autoplay tail are the source's).
    // Plain keyed rows are exactly what Reorderable's live projection wants (LiveProject moves the dragged row through
    // the keyed diff and FLIP animates its neighbours), which is why this panel's non-virtualized design gets premiere
    // reorder for free while the virtualized lists need ItemsView's InsertionOptions.
    readonly Reorderable _reorder = new(WaveeDragKinds.Resource)
    {
        ItemExtent = RowExtent,
        Spacing = 0f,
        DragStyle = new DragVisualStyle { Lift = DragLift.Stationary, Opacity = Drag.SourceDimOpacity },
        // A queue row dragged OUT lands on the surface it was dropped on (a playlist, the player bar) and leaves the
        // queue alone — without this the downward travel to reach that surface would also commit a local reorder.
        RequireDropOnList = true,
    };

    public override Element Render()
    {
        var b = UseContext(PlaybackBridge.Slot);
        var lib = UseContext(LibraryBridge.Slot);
        var svc = UseContext(Services.Slot);
        var go = UseContext(HistoryStore.NavCtx);
        var acts = UseContext(ActionServices.Slot);     // queue-row context menus (Menus.QueueEntry)
        var menuOverlay = UseContext(Overlay.Service);

        // Peek, never Value: UseSignal consumes `initial` on the MOUNT pass only, so reading .Value here bought nothing
        // but a live component→Queue dependency on every render — and with the effect below ALSO writing `display`, one
        // queue push re-rendered the panel TWICE (the census's QueuePanel×2 ⇒ SwipeControlCore×100). The effect is the
        // single intended path from the bridge into this panel.
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
        var uiLogSig = UseRef<string?>(null);
        // New context ⇒ collapse the visual pagination back to the first page of each section.
        UseEffect(() => { _queuePages.Value = 1; _upPages.Value = 1; _autoPages.Value = 1; }, ctxUri);

        if (b is null) return new BoxEl();

        void ToggleAutoplay()
        {
            if (svc is null) return;
            svc.Settings.Set(WaveeSettings.AutoplayEnabled, !autoplay);
            PlaybackPrefs.Bump();
        }

        var track = b.CurrentTrack.Value;
        // These are accent-FILLED chrome pills, not a wash: use the same lifted/saturation-floored cover role as every
        // media CTA. The raw grading role can be deliberately dark and made an enabled pill read disabled.
        var accent = Surfaces.ChromeSchemeFor(b.CurrentTrack.Value?.Image?.Url) is { } p
            ? WaveePalette.ChromeAccent(p)
            : Tok.AccentDefault;

        // ── bucket split: forward-looking only (History and the NowPlaying entry are NOT rows here) ──
        var queue = display.Value;
        var userQueue = new List<QueueEntry>();
        var ctxUp = new List<QueueEntry>();
        var autoUp = new List<QueueEntry>();
        string? curUri = track?.Uri;
        foreach (var e in queue)
        {
            if (curUri is { Length: > 0 } && e.Track.Uri == curUri) continue;   // never show the current track as a row
            switch (e.Bucket)
            {
                case QueueBucket.UserQueue: userQueue.Add(e); break;
                case QueueBucket.NextUp: (e.Provider == QueueProvider.Autoplay ? autoUp : ctxUp).Add(e); break;
            }
        }

        // Copy-paste diagnostics: what the panel actually shows. Diff against queue.snapshot / bridge.ui.push-state.
        var sigRef = uiLogSig.Value;
        Backend.PlaybackBucketDiagnostics.UiIfChanged(ref sigRef, "queue.panel.rows",
            PanelDump(curUri, userQueue, ctxUp, autoUp, autoplay));
        uiLogSig.Value = sigRef;

        bool viewer = PlayerBarContent.RemoteDevice(b) is not null;
        bool artworkHidden = AppearancePrefs.TrackArtworkHidden(svc?.Settings);
        bool classic = (svc?.Settings.Get(WaveeSettings.TrackRowStyle) ?? 0) == 1;
        // Classic owns identity chrome, not just density: like the detail table it suppresses cell artwork even when
        // the independent generic artwork preference is off. TrackArtworkHidden's Epoch read makes both settings live.
        bool showTrackArtwork = !classic && !artworkHidden;
        string? source = ctxName.Value.Value is { Length: > 0 } rn ? rn : ImmediateContextName(ctxUri);
        string? sourceHref = source is { Length: > 0 } ? RichText.RouteForUri(ctxUri) : null;

        // Fresh closures every render: they read THIS render's rows, and the panel re-renders on every queue push.
        ConfigureReorder(b, acts, display, userQueue);

        var content = new List<Element>(8);
        if (source is { Length: > 0 })
            content.Add(PlayingFrom(source, sourceHref, go));
        if (track is { } t)
            content.Add(NowPlayingCard(b, lib, t, go, classic));
        if (userQueue.Count > 0)
        {
            content.Add(SectionHeader(Loc.Get(Strings.Player.NextInQueue), userQueue.Count,
                viewer ? null : () => ClearUserQueue(b, display)));
            // The one insertion LANE: the user queue's own rows. Grow is dropped (the wrapper defaults to filling the
            // free space of the scroll column, which would push every later section to the bottom of a short queue).
            content.Add((BoxEl)_reorder.List(
                Rows("q", userQueue, b, lib, go, display, removable: !viewer, dim: false, _queuePages, acts, menuOverlay,
                     _swipeGroup, showTrackArtwork, classic, reorder: viewer ? null : _reorder))
                with { Grow = 0f, Key = "lane:q" });
        }
        if (ctxUp.Count > 0)
        {
            content.Add(SectionHeader(Loc.Get(Strings.Player.NextUp), ctxUp.Count, null));
            content.Add(RefusingLane("u", Loc.Get(Strings.Drag.CantReorderNextUp),
                Rows("u", ctxUp, b, lib, go, display, removable: !viewer, dim: false, _upPages, acts, menuOverlay,
                    _swipeGroup, showTrackArtwork, classic)));
        }
        if (autoplay && autoUp.Count > 0)
        {
            content.Add(SectionHeader(Loc.Get(Strings.Player.Autoplay), autoUp.Count, null, sub: Loc.Get(Strings.Player.AutoplayHint)));
            content.Add(RefusingLane("a", Loc.Get(Strings.Drag.CantReorderAutoplay),
                Rows("a", autoUp, b, lib, go, display, removable: !viewer, dim: true, _autoPages, acts, menuOverlay,
                    _swipeGroup, showTrackArtwork, classic)));
        }
        if (track is null && userQueue.Count == 0 && ctxUp.Count == 0)
            content.Add(EmptyState.Compact(Loc.Get(Strings.Player.NothingPlaying)));

        Element body = new BoxEl
        {
            Direction = 1, MinHeight = 0f,
            Padding = new Edges4(0f, 0f, 0f, 14f),
            Children = content.ToArray(),
        };
        // With NO user queue there is no lane to aim at, and a drop that lands nowhere is the "cannot drop in this
        // mode" failure this campaign exists to kill. The same Reorderable then mounts around the whole panel body:
        // item count 0 ⇒ every position resolves to slot 0, which is the play-next insert.
        if (userQueue.Count == 0)
            body = (BoxEl)_reorder.List(body) with { Grow = 0f, Key = "lane:empty" };

        return new BoxEl
        {
            Direction = 1, Grow = 1f, MinHeight = 0f, ClipToBounds = true,
            Padding = new Edges4(14f, 4f, 14f, 0f),
            Children =
            [
                Pills(b, accent, autoplay, ToggleAutoplay),
                new ScrollEl
                {
                    Grow = 1f, MinHeight = 0f,
                    AutoEdgeFade = true,
                    ScrollKey = "queuepanel",
                    OnScrollGeometryChanged = (g => _swipeGroup.AnyOpen ? BitConverter.SingleToInt32Bits(g.OffsetY) : 0L, _ => _swipeGroup.Close()),
                    Content = body,
                },
            ],
        };
    }

    // ── drag & drop: the user queue is the one reorderable + insertable section ───────────────────────────────────────

    /// <summary>Point the section's <see cref="Reorderable"/> at THIS render's rows. Everything here is a fresh closure
    /// on purpose: the panel re-renders on every queue push, and a delegate captured at mount would move the wrong row.</summary>
    void ConfigureReorder(PlaybackBridge b, ActionServices? acts, Signal<IReadOnlyList<QueueEntry>> display,
                          List<QueueEntry> userQueue)
    {
        // Slot math spans the REALIZED rows only (the section paginates at PageSize) — the rows the user can see are
        // the rows they can aim between.
        int shown = Math.Min(userQueue.Count, Math.Max(1, _queuePages.Peek()) * PageSize);
        _reorder.Scene = Context.Scene;
        _reorder.RequestRender = Context.RequestRerender;
        _reorder.ItemCount = shown;
        _reorder.ItemOf = slot => (uint)slot < (uint)userQueue.Count
            ? WaveeResourceDragPayload.ForTrack(userQueue[slot].Track)
            : null;
        _reorder.OnReorder = (from, to) => MoveInSection(b, display, userQueue, from, to);
        _reorder.OnCrossCommit = (payload, _, _, _, slot) => InsertAtSlot(b, acts, payload, slot);
        // A viewer's queue belongs to the active device: an insert forwards as a set_queue rewrite, so drops still
        // work — only the local reorder path is withheld (MoveQueueItemAsync is a no-op while another device is active).
        _reorder.CanAcceptForeign = static p => WaveeResourceDrop.CanDepositTracks(p);
        _reorder.ForeignRefusalCaption = static p => WaveeResourceDrag.Unwrap(p) is { } r
            // Locked decision: an artist has no single obvious track set, so it is refused rather than guessed at.
            ? Loc.Get(r.Kind == WaveeResourceKind.Artist ? Strings.Drag.CantAddArtist : Strings.Drag.NothingToAdd)
            : null;
        _reorder.ForeignCaption = static (_, _) => Loc.Get(Strings.Drag.AddToQueue);
    }

    /// <summary>A section whose order is NOT the user's to change (a context continuation, the autoplay tail). It is a
    /// kind-matched REFUSER, not empty space: without it a drag over these rows is silent, and silence is what "drag &amp;
    /// drop is broken" is made of.</summary>
    static Element RefusingLane(string tag, string why, Element rows) => new BoxEl
    {
        Key = "lane:" + tag,
        Direction = 1,
        DropTarget = Drop.Target<WaveeResourceDragPayload>(WaveeDragKinds.Resource,
            accepts: static _ => false,
            refusalCaption: _ => why),
        Children = [rows],
    };

    /// <summary>Move a row inside its own section — the ONE path behind both the drag reorder and the context menu's
    /// ±1 verbs. The optimistic snapshot mirrors the session op exactly (remove + insert at the post-removal index),
    /// which for a ±1 move is the same result the old swap produced.</summary>
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

    /// <summary>Deposit a foreign payload into the user queue at the slot the insertion line marked. Albums/playlists
    /// resolve cold (never during the drag), and the batch cap is surfaced rather than silently applied.</summary>
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

    // ── "Playing from {source}" breadcrumb — borderless single line above the card (WaveeMusic ContextCard). ──
    static Element PlayingFrom(string source, string? href, Action<string, string?>? go) => new BoxEl
    {
        Key = "qp:ctx",
        Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S, MinHeight = 28f,
        Padding = new Edges4(Spacing.S, Spacing.XXS, Spacing.S, Spacing.S),
        Corners = Radii.ControlAll,
        HoverFill = href is { Length: > 0 } ? WaveeColors.RowHover : ColorF.Transparent,
        Cursor = href is { Length: > 0 } ? CursorId.Hand : CursorId.Arrow,
        OnClick = href is { Length: > 0 } && go is not null ? () => go(href, source) : null,
        Children =
        [
            new SpanTextEl(
            [
                new TextSpan(Strings.Player.PlayingFrom(""), Color: Tok.TextSecondary),
                new TextSpan(source, Color: Tok.TextPrimary, Weight: 700),
            ]) { Size = 12f, LineHeight = 16f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, Grow = 1f, MinWidth = 0f },
            new TextEl(Icons.ChevronRightMed) { Size = 12f, FontFamily = Theme.IconFont, Color = Tok.TextSecondary },
        ],
    };

    // ── The pinned NOW-PLAYING CARD: a bordered card, keyed by track uri (a track change remounts it with an Enter
    // fade — the cross-fade the old anchor row could never do). The EQ overlay toggles play/pause. ──
    static Element NowPlayingCard(PlaybackBridge b, LibraryBridge? lib, Track t, Action<string, string?>? go, bool classic)
    {
        var st = TrackRow.StateOf(b, lib, t);
        Action? like = t.Uri.Length > 0 && lib is not null ? () => lib.ToggleSaved(t.Uri, t.Title) : null;
        var children = new List<Element>(3);
        if (!classic)
            children.Add(new BoxEl
            {
                Width = 44f, Height = 44f, Shrink = 0f, ZStack = true, ClipToBounds = true,
                Corners = Radii.ControlAll,
                Children =
                [
                    Surfaces.Artwork(t.Image, t.Id.GetHashCode() & 0x7fffffff, 44f, 44f, Radii.Control, decodePx: 96),
                    NowPlayingOverlay.Create(t.Uri, () => { }, 28f, cover: true, 44f, centered: true)
                        .Skeletonized(false),
                ],
            });
        children.Add(classic
            ? QueueIdentity(t, go, nowPlaying: true)
            : new BoxEl
            {
                Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Justify = FlexJustify.Center, Gap = 1f,
                Children =
                [
                    new TextEl(t.Title)
                    {
                        Size = 14f, LineHeight = 20f, Weight = 600, Color = Tok.AccentTextPrimary,
                        Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                    },
                    go is null
                        ? new TextEl(DetailFormat.ArtistNames(t.Artists))
                        { Size = 12f, LineHeight = 16f, Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f }
                        : TrackRow.ArtistLinks(t.Artists, (r, n) => go(r, n)),
                ],
            });
        children.Add(new BoxEl
        {
            Width = 30f, Height = classic ? RowExtent : 44f, Shrink = 0f,
            AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Children = [TrackRow.Heart(st.Saved, like, classic: classic)],
        });

        var body = new BoxEl
        {
            Direction = 0, Grow = 1f, MinWidth = 0f, AlignItems = FlexAlign.Center,
            Gap = classic ? Spacing.S : Spacing.L,
            MinHeight = classic ? RowExtent : 64f,
            Padding = classic ? new Edges4(Spacing.S, 0f, Spacing.XS, 0f) : Edges4.All(10f),
            Children = children.ToArray(),
        };

        return new BoxEl
        {
            Key = "np:" + t.Uri + ":classic=" + classic,
            ZStack = true, MinHeight = classic ? RowExtent : 64f, ClipToBounds = classic,
            Margin = classic ? Edges4.All(0f) : new Edges4(0f, 0f, 0f, 10f),
            Corners = classic ? CornerRadius4.All(Radii.None) : CornerRadius4.All(Radii.Card),
            Fill = classic ? ColorF.Transparent : Tok.FillCardDefault,
            BorderWidth = classic ? 0f : 1f,
            BorderColor = classic ? ColorF.Transparent : Tok.StrokeCardDefault,
            Enter = new EnterExit(Dy: 6f, Opacity: 0f, Active: true),
            Layout = LayoutTransition.Slide,
            Children = classic ? [body, ClassicHairline()] : [body],
        };
    }

    // ── Section header: caps title + optional count + optional Clear, optional hint sub-line. ──
    static Element SectionHeader(string title, int count, Action? clear, string? sub = null)
    {
        var top = new List<Element>(4)
        {
            WaveeType.Eyebrow(title) with
            {
                Color = Tok.TextTertiary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
            },
        };
        if (count >= 0) top.Add(new TextEl(count.ToString()) { Size = 12f, LineHeight = 16f, Weight = 600, Color = Tok.TextTertiary });
        top.Add(new BoxEl { Grow = 1f, MinWidth = 0f });
        if (clear is not null)
            top.Add(new BoxEl
            {
                Padding = new Edges4(Spacing.S, Spacing.XXS, Spacing.S, Spacing.XXS), Corners = Radii.ControlAll,
                HoverFill = WaveeColors.RowHover, PressedFill = WaveeColors.RowPressed,
                Role = AutomationRole.Button, Cursor = CursorId.Hand, Focusable = true, OnClick = clear,
                Children = [new TextEl(Loc.Get(Strings.Player.Clear)) { Size = 12f, LineHeight = 16f, Weight = 600, Color = Tok.TextSecondary, HoverColor = Tok.TextPrimary }],
            });

        var kids = new List<Element>(2) { new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S, Children = top.ToArray() } };
        if (sub is { Length: > 0 })
            kids.Add(new TextEl(sub) { Size = 12f, LineHeight = 16f, Color = Tok.TextTertiary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis });

        return new BoxEl
        {
            Key = "hdr:" + title,
            Direction = 1, Gap = Spacing.XXS,
            Padding = new Edges4(Spacing.S, Spacing.M, Spacing.S, Spacing.XS),
            Layout = LayoutTransition.Slide,
            Children = kids.ToArray(),
        };
    }

    // ── A section's keyed rows: plain children — the reconciler's keyed diff + Enter/Exit/FLIP does all the motion.
    // Visual pagination only: PageSize rows per revealed page, then an explicit full-width "Show more (N left)" row —
    // the UNDERLYING queue is untouched; every hidden track is one click away and the remaining count is always visible. ──
    static Element Rows(string sectionTag, List<QueueEntry> entries, PlaybackBridge b, LibraryBridge? lib,
        Action<string, string?>? go, Signal<IReadOnlyList<QueueEntry>> display, bool removable, bool dim, Signal<int> pages,
        ActionServices? acts = null, IOverlayService? menuOverlay = null, SwipeGroup? swipeGroup = null,
        bool showTrackArtwork = true, bool classic = false, Reorderable? reorder = null)
    {
        int n = Math.Min(entries.Count, Math.Max(1, pages.Value) * PageSize);
        var kids = new List<Element>(n + 1);
        for (int i = 0; i < n; i++)
        {
            // The reorderable section renders its rows through the live PROJECTION: mid-drag the dragged row occupies
            // the slot it would land in and the keyed diff + FLIP glide its neighbours out of the way (the WinUI
            // part-to-make-room motion this panel's plain keyed rows can express natively).
            int item = reorder is { } ro ? ro.ItemAt(i) : i;
            if ((uint)item >= (uint)entries.Count) item = i;
            var row = QueueRow(b, lib, go, display, entries[item], item, entries, removable, dim,
                               acts, menuOverlay, swipeGroup, reorder is null, showTrackArtwork, classic);
            // Direction 1 on the wrapper: its single child must stretch across the WIDTH (a row-direction wrapper
            // would size the row to its content and collapse the hover plate to the text).
            kids.Add(reorder is { } r
                ? (BoxEl)r.Item(item, row, key: RowKey(entries[item])) with { Direction = 1 }
                : row);
        }
        if (entries.Count > n)
            kids.Add(ShowMore(sectionTag, Math.Min(PageSize, entries.Count - n), entries.Count - n,
                () => pages.Value = pages.Peek() + 1));
        return new BoxEl { Key = "sec:" + sectionTag, Direction = 1, Children = kids.ToArray() };
    }

    // Full-width load-more affordance: "⌄ Show next 100 · 63 more" — the pagination is explicit, never a silent cut.
    static Element ShowMore(string sectionTag, int nextPage, int remaining, Action more) => new BoxEl
    {
        Key = "more:" + sectionTag,
        Direction = 0, MinHeight = 40f, Gap = Spacing.S, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Margin = new Edges4(0f, Spacing.XS, 0f, Spacing.XXS),
        Corners = Radii.ControlAll,
        Fill = Tok.FillCardSecondary,
        HoverFill = WaveeColors.RowHover, PressedFill = WaveeColors.RowPressed,
        Role = AutomationRole.Button, Cursor = CursorId.Hand, Focusable = true, OnClick = more,
        Layout = LayoutTransition.Slide,
        Children =
        [
            new TextEl(Icons.ChevronDown) { Size = 12f, FontFamily = Theme.IconFont, Color = Tok.TextSecondary },
            new TextEl($"Show next {nextPage}") { Size = 12f, LineHeight = 16f, Weight = 600, Color = Tok.TextPrimary },
            new TextEl($"·  {remaining} more") { Size = 12f, LineHeight = 16f, Color = Tok.TextTertiary },
        ],
    };

    static Element QueueRow(PlaybackBridge b, LibraryBridge? lib, Action<string, string?>? go,
        Signal<IReadOnlyList<QueueEntry>> display, QueueEntry entry, int bucketIndex, IReadOnlyList<QueueEntry> section,
        bool removable, bool dim,
        ActionServices? acts = null, IOverlayService? menuOverlay = null, SwipeGroup? swipeGroup = null,
        bool ownDrag = true, bool showArtwork = true, bool classic = false)
    {
        var t = entry.Track;
        int bucketCount = section.Count;
        var st = TrackRow.StateOf(b, lib, t);
        // A row this thin (ApplySetQueue's Synthetic fallback for a uri we didn't already hold, or a set_queue that
        // outran BumpQueueRevision's identity pass) has a bare spotify:track:… uri sitting in Title — never paint that.
        bool titleThin = HydrationLevels.TitleMissing(t.Title, t.Uri);
        Action? like = t.Uri.Length > 0 && lib is not null ? () => lib.ToggleSaved(t.Uri, t.Title) : null;

        void Remove()
        {
            _ = b.Player.RemoveQueueItemAsync(entry.ItemId);
            display.Value = QueueOrder.Remove(display.Peek(), entry);
        }

        bool canMove = removable && !entry.ItemId.IsNone;
        void Move(int delta)
        {
            if (!canMove) return;
            MoveInSection(b, display, section, bucketIndex, bucketIndex + delta);
        }

        Element[] artwork = showArtwork
            ?
            [
                new BoxEl
                {
                    Width = QueueArt, Height = QueueArt, Shrink = 0f, ZStack = true, ClipToBounds = true,
                    Corners = Radii.ControlAll,
                    Children =
                    [
                        Surfaces.Artwork(t.Image, t.Id.GetHashCode() & 0x7fffffff, QueueArt, QueueArt, Radii.Control, decodePx: 72),
                        NowPlayingOverlay.Create(t.Uri, () => PlayQueueEntry(b, entry), 26f, cover: true, QueueArt, centered: true)
                            .Skeletonized(false),
                    ],
                },
            ]
            : Array.Empty<Element>();

        var rowBody = new BoxEl
        {
            Direction = 0, Grow = 1f, MinWidth = 0f, AlignItems = FlexAlign.Center, Gap = Spacing.S,
            MinHeight = RowExtent,
            Padding = new Edges4(Spacing.S, 0f, Spacing.XS, 0f),
            Children =
            [
                new BoxEl
                {
                    Width = 26f, Height = RowExtent, Shrink = 0f,
                    AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                    BlocksDragArm = true,
                    Children = [TrackRow.Heart(st.Saved, like, classic: classic)],
                },
                ..artwork,
                classic
                    ? QueueIdentity(t, go, nowPlaying: st.IsNow)
                    : new BoxEl
                    {
                        Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Justify = FlexJustify.Center,
                        Gap = titleThin ? 4f : 0f,
                        Children = titleThin
                            ?
                            [
                                // The same title+subtitle two-bar shape SidebarSkeletons.Row draws for a pending list
                                // row — this row IS mounted (it has a real ItemId/slot), only its metadata isn't in yet.
                                new BoxEl { Width = 120f, Height = 14f, Corners = CornerRadius4.All(4f), Fill = Tok.FillSubtleSecondary },
                                new BoxEl { Width = 80f, Height = 11f, Corners = CornerRadius4.All(4f), Fill = Tok.FillSubtleSecondary },
                            ]
                            :
                            [
                                new TextEl(t.Title)
                                {
                                    Size = 14f, LineHeight = 20f, Weight = 600,
                                    Color = st.IsNow ? Tok.AccentTextPrimary : Tok.TextPrimary,
                                    Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                                },
                                go is null
                                    ? new TextEl(DetailFormat.ArtistNames(t.Artists))
                                    { Size = 12f, LineHeight = 16f, Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f }
                                    : TrackRow.ArtistLinks(t.Artists, (r, n) => go(r, n)),
                            ],
                    },
                // Hover-revealed "…" overflow beside the ✕ (kept): opens the SAME queue-entry menu the row shows on
                // right-click, anchored at the button — the engine's ClickRequestsContext re-enters the context-request
                // funnel here and the walk finds the row's OnContextRequested (the WithContextMenu attach below). Only
                // rendered when a menu is actually attachable. Row 1 of WaveeCta's icon-button geometry table - a
                // 32-square at the control radius. It used to be a 28 CIRCLE, which the table reserves for FABs on
                // media; nothing here is on media, and two round buttons in a flat rail read as a different control
                // family from the identical square ones in the toolbar right above them.
                acts is not null && menuOverlay is not null
                    ? new BoxEl
                    {
                        Opacity = 0f, HoverOpacity = 1f, Shrink = 0f, BlocksDragArm = true,
                        Children =
                        [
                            new BoxEl
                            {
                                Width = WaveeCta.IconButtonSize, Height = WaveeCta.IconButtonSize,
                                AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                                Corners = classic ? CornerRadius4.All(Radii.None) : Radii.ControlAll,
                                HoverFill = WaveeColors.RowPressed,
                                Role = AutomationRole.Button, Cursor = CursorId.Hand,
                                ClickRequestsContext = true,
                                Children = [new TextEl(Icons.More) { Size = 14f, FontFamily = Theme.IconFont, Color = Tok.TextTertiary, HoverColor = Tok.TextPrimary }],
                            },
                        ],
                    }
                    : new BoxEl { Width = 0f, Shrink = 0f },
                removable && !entry.ItemId.IsNone
                    ? new BoxEl
                    {
                        Width = WaveeCta.IconButtonSize, Height = WaveeCta.IconButtonSize, Shrink = 0f,
                        AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                        Corners = classic ? CornerRadius4.All(Radii.None) : Radii.ControlAll,
                        HoverFill = WaveeColors.RowPressed,
                        Role = AutomationRole.Button, Cursor = CursorId.Hand,
                        BlocksDragArm = true,
                        OnClick = Remove,
                        Children = [new TextEl(Icons.ChromeClose) { Size = 12f, FontFamily = Theme.IconFont, Color = Tok.TextTertiary, HoverColor = Tok.TextPrimary }],
                    }
                    : new BoxEl { Width = WaveeCta.IconButtonSize, Shrink = 0f },
            ],
        };

        var row = new BoxEl
        {
            Key = RowKey(entry) + ":art=" + showArtwork + ":classic=" + classic,
            // Rows the Reorderable owns get their drag source from it (payload = the ReorderPayload every Wavee target
            // already unwraps); every other row drags itself as a COPY — the queue is never mutated by a drag OUT.
            Draggable = ownDrag
                ? Drag.Source(WaveeDragKinds.Resource, () => WaveeResourceDragPayload.ForTrack(t))
                : null,
            ZStack = true, MinHeight = RowExtent, ClipToBounds = classic,
            Corners = classic ? CornerRadius4.All(Radii.None) : Radii.ControlAll,
            // NO ZEBRA. Classic adds only a hairline; both styles retain the shared hover/press depth.
            Fill = ColorF.Transparent,
            HoverFill = WaveeColors.RowHover,
            PressedFill = WaveeColors.RowPressed,
            PressScale = WaveeMotion.ScaleSubtle.Press,
            Opacity = dim ? 0.72f : 1f,
            Role = AutomationRole.Button, Cursor = CursorId.Hand, Focusable = true,
            OnClick = () => PlayQueueEntry(b, entry),
            Enter = new EnterExit(Dy: 6f, Opacity: 0f, Active: true),
            Exit = new EnterExit(Dy: -4f, Opacity: 0f, Active: true),
            Layout = LayoutTransition.Slide,
            Children = classic ? [rowBody, ClassicHairline()] : [rowBody],
        };
        bool canRemove = removable && !entry.ItemId.IsNone;
        // Right-click / long-press: the queue-entry menu. "Play now" = the row's own skip-in-place; "Remove from
        // queue" reuses the exact Remove() closure above (player call + optimistic display update) — a viewer (remote
        // device) gets it disabled, mirroring the hidden inline ✕.
        Element rowEl = row;
        if (acts is { } a && menuOverlay is { } menuSvc)
            rowEl = row.WithContextMenu(menuSvc, () => Menus.QueueEntry(
                a, entry, canRemove ? (Action)Remove : null, () => PlayQueueEntry(b, entry),
                canMove && bucketIndex > 0 ? () => Move(-1) : null,
                canMove && bucketIndex + 1 < bucketCount ? () => Move(1) : null));
        // Touch swipe-to-action (Phase D): swipe LEFT to remove (destructive red, reusing the Remove() closure via the
        // action target), swipe RIGHT to like. Eager KEYED rows ⇒ no resetKey (each entry mounts its own control; a
        // queue edit remounts by RowKey). The context menu is attached to the row BENEATH the wrapper, so the touch
        // long-press still finds the row's ContextBit ancestor.
        if (acts is { } sa)
        {
            var ctx = new ActionContext(ActionTarget.ForQueueEntry(entry, canRemove ? (Action)Remove : null), sa);
            rowEl = RowSwipe.Wrap(rowEl, ctx,
                group: swipeGroup,
                leading: TrackActions.ToggleLike,
                trailing: canRemove ? TrackActions.RemoveFromQueue : null);
        }
        return rowEl;
    }

    /// <summary>Classic's rail-width fallback: the dedicated Artist column folds into the same one-line identity run,
    /// exactly as the detail table does at its narrowest pressure tier. Artwork is absent and the full explicit word-mark
    /// remains pinned after the ellipsized identity.</summary>
    static Element QueueIdentity(Track t, Action<string, string?>? go, bool nowPlaying)
    {
        ColorF primary = nowPlaying ? Tok.AccentTextPrimary : Tok.TextPrimary;
        ColorF secondary = nowPlaying ? Tok.AccentTextPrimary : Tok.TextSecondary;

        Element identity;
        if (HydrationLevels.TitleMissing(t.Title, t.Uri))
        {
            // Classic folds title+artist into one span run — there is no separate slot to skeletonize, so the whole
            // identity becomes one placeholder bar (same fill as the modern row's two-bar shape above) instead of
            // laying spans over a bare uri.
            identity = new BoxEl { Width = 160f, Height = 14f, Corners = CornerRadius4.All(4f), Fill = Tok.FillSubtleSecondary };
        }
        else
        {
            var spans = new List<TextSpan>(t.Artists.Count * 2 + 2)
            {
                new(t.Title, Weight: 600, Color: primary),
            };
            if (t.Artists.Count > 0) spans.Add(new TextSpan("  ·  ", Color: secondary));
            for (int i = 0; i < t.Artists.Count; i++)
            {
                if (i > 0) spans.Add(new TextSpan(", ", Color: secondary));
                var artist = t.Artists[i];
                string route = RichText.RouteForUri(artist.Uri) ?? ("artist:" + artist.Uri);
                spans.Add(new TextSpan(artist.Name, Color: secondary,
                    OnClick: go is null ? null : () => go(route, artist.Name)));
            }

            identity = new SpanTextEl(spans.ToArray())
            {
                Size = 14f, LineHeight = 20f, Color = primary,
                Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MaxLines = 1,
                Shrink = 1f, MinWidth = 0f,
            };
        }

        var kids = new List<Element>(2)
        {
            new BoxEl
            {
                Grow = 1f, Basis = 0f, MinWidth = 0f, ClipToBounds = true,
                Children = [identity],
            },
        };
        if (t.IsExplicit) kids.Add(TrackRow.ClassicExplicitBadge(nowPlaying ? Tok.AccentTextPrimary : null));
        return new BoxEl
        {
            Direction = 0, Grow = 1f, Basis = 0f, MinWidth = 0f,
            AlignItems = FlexAlign.Center, Gap = Spacing.S, ClipToBounds = true,
            Children = kids.ToArray(),
        };
    }

    static Element ClassicHairline() => new BoxEl
    {
        Key = "classic-hairline",
        AlignSelf = FlexAlign.End, JustifySelf = FlexAlign.Stretch,
        Height = 1f, Fill = Prop.Of(static () => Tok.StrokeDividerDefault),
        HitTestVisible = false,
    };

    static string RowKey(in QueueEntry e) => e.ItemId.IsNone ? "e" + e.EntryId : "i" + e.ItemId.Value;

    static void ClearUserQueue(PlaybackBridge b, Signal<IReadOnlyList<QueueEntry>> display)
    {
        _ = b.Player.ClearQueueAsync();
        display.Value = display.Peek().Where(x => x.Bucket != QueueBucket.UserQueue).ToList();
    }

    static Element Pills(PlaybackBridge b, ColorF accent, bool autoplayOn, Action toggleAutoplay) => new BoxEl
    {
        Direction = 0, Gap = 8f, AlignItems = FlexAlign.Center,
        Padding = new Edges4(0f, 4f, 0f, 10f),
        Children =
        [
            SegmentPill(Loc.Get(Strings.Player.Shuffle), Icons.Shuffle, b.IsShuffle.Value,
                () => PlayerBarContent.ToggleShuffle(b), accent),
            SegmentPill(Loc.Get(Strings.Player.Repeat), b.Repeat.Value == RepeatMode.Track ? Icons.RepeatOne : Icons.RepeatAll,
                b.Repeat.Value != RepeatMode.Off, () => PlayerBarContent.CycleRepeat(b), accent),
            AutoplayPill(autoplayOn, toggleAutoplay, accent),
        ],
    };

    static Element AutoplayPill(bool on, Action click, ColorF accent) => new BoxEl
    {
        Direction = 0, Height = 32f, Gap = 6f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Padding = new Edges4(12f, 0f, 12f, 0f),
        Corners = Radii.PillAll,
        Fill = on ? accent : Tok.FillCardSecondary,
        HoverFill = on ? accent with { A = 0.88f } : Tok.FillSubtleSecondary,
        PressedFill = on ? accent with { A = 0.78f } : Tok.FillSubtleTertiary,
        Role = AutomationRole.Button, Cursor = CursorId.Hand, Focusable = true, OnClick = click,
        Children =
        [
            // The ∞ glyph rides the label's own rung (14/600) instead of an off-ramp 15/800.
            new TextEl("∞") { Size = 14f, LineHeight = 20f, Weight = 600, Color = on ? Tok.TextOnAccentPrimary : Tok.TextSecondary },
            new TextEl(Loc.Get(Strings.Player.Autoplay)) { Size = 12f, LineHeight = 16f, Weight = 600, Color = on ? Tok.TextOnAccentPrimary : Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
        ],
    };

    static Element SegmentPill(string label, string glyph, bool on, Action click, ColorF accent) => new BoxEl
    {
        Direction = 0, Height = 32f, Gap = 6f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Padding = new Edges4(12f, 0f, 12f, 0f),
        Corners = Radii.PillAll,
        Fill = on ? accent : Tok.FillCardSecondary,
        HoverFill = on ? accent with { A = 0.88f } : Tok.FillSubtleSecondary,
        PressedFill = on ? accent with { A = 0.78f } : Tok.FillSubtleTertiary,
        Role = AutomationRole.Button, Cursor = CursorId.Hand, Focusable = true, OnClick = click,
        Children =
        [
            new TextEl(glyph) { Size = 12f, FontFamily = Theme.IconFont, Color = on ? Tok.TextOnAccentPrimary : Tok.TextSecondary },
            new TextEl(label) { Size = 12f, LineHeight = 16f, Weight = 600, Color = on ? Tok.TextOnAccentPrimary : Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
        ],
    };

    static string? ImmediateContextName(string uri)
    {
        if (uri.Length == 0) return null;
        // Both collection shapes (`spotify:collection:*` and `spotify:user:{id}:collection`) are ONE kind to the parser
        // (hydration-facade-design.md §1.1), which is what the `Contains(":collection")` probe was approximating.
        if (EntityUri.KindOf(uri) == EntityKind.Collection) return Loc.Get(Strings.Player.LikedSongs);
        return null;
    }

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

    // Skip-in-place to the clicked row within the live session by its stable id (F1): a cursor move, never a rebuild.
    // The PlayTrackAsync fallback fires only when the row carries no stable id (ItemId.IsNone — a degenerate snapshot).
    static void PlayQueueEntry(PlaybackBridge b, QueueEntry entry)
    {
        if (entry.ItemId.IsNone)
        {
            TrackRow.Invoke(b, entry.Track, () => b.Player.PlayTrackAsync(entry.Track));
            return;
        }
        TrackRow.Invoke(b, entry.Track, () => b.Player.SkipToQueueItemAsync(entry.ItemId));
    }

    // The panel-side truth dump (queue.panel.rows): what the panel actually shows, per section, with row keys —
    // diff against queue.snapshot / bridge.ui.push-state to split bad DATA from bad RENDERING at a glance.
    static string PanelDump(string? currentUri, List<QueueEntry> userQueue, List<QueueEntry> ctxUp,
        List<QueueEntry> autoUp, bool autoplayOn)
    {
        var sb = new System.Text.StringBuilder(96 + (userQueue.Count + ctxUp.Count + autoUp.Count) * 40);
        sb.Append("card=").Append(currentUri ?? "-")
          .Append(" queue=").Append(userQueue.Count)
          .Append(" nextUp=").Append(ctxUp.Count)
          .Append(" autoplay=").Append(autoplayOn ? autoUp.Count.ToString() : "off")
          .Append(" rows=[");
        AppendSection(sb, "Q", userQueue);
        AppendSection(sb, "U", ctxUp);
        if (autoplayOn) AppendSection(sb, "A", autoUp);
        sb.Append(']');
        return sb.ToString();

        static void AppendSection(System.Text.StringBuilder sb, string tag, List<QueueEntry> list)
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (sb[^1] != '[') sb.Append("; ");
                sb.Append(tag).Append(i).Append(" key=").Append(RowKey(list[i]))
                  .Append(" \"").Append(list[i].Track.Title).Append('"');
            }
        }
    }
}

/// <summary>Cross-surface playback preference epoch (autoplay): bumped when <see cref="WaveeSettings.AutoplayEnabled"/>
/// changes from the Settings toggle, the queue-panel pill, or the autoplay footer, so every surface stays in sync.</summary>
static class PlaybackPrefs
{
    public static readonly Signal<int> Epoch = new(0);
    public static void Bump() => PlaybackPrefs.Epoch.Value = Epoch.Peek() + 1;
}
