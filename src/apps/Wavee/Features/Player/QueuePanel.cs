using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Input;
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
    // The NON-ITEM slots' resting extents. Headers and "Show more" rows live INSIDE the reorderable column (see
    // QueueSlots), so the reorder geometry has to know exactly how tall they are — which is why they carry an explicit
    // Height rather than sizing to content: the extent the slot math samples IS the extent the layout arranges.
    const float HeaderRowH = 20f;                                            // eyebrow row: the Clear pill's 16 + XXS×2
    const float HeaderExtent = Spacing.M + HeaderRowH + Spacing.XS;          // "Next in queue" / "Next up"
    const float HeaderSubExtent = HeaderExtent + Spacing.XXS + 16f;          // "Autoplay" + its one-line hint
    const float MoreRowH = 40f;
    const float MoreExtent = MoreRowH + Spacing.XS + Spacing.XXS;            // the row + its authored margins

    // Visual page counts per section (instance-lived; reset when the playback context changes).
    readonly Signal<int> _queuePages = new(1);
    readonly Signal<int> _upPages = new(1);
    readonly Signal<int> _autoPages = new(1);
    readonly SwipeGroup _swipeGroup = new();

    // ONE Reorderable over the WHOLE upcoming list — "Next in queue", "Next up" and "Autoplay" together, headers and
    // all (QueueSlots flattens them; QueueMovePlan decides a drop). A move is SECTION-LOCAL: the model keeps every row
    // inside its own provider section (PlaybackSession.MoveItem, pinned by QueueSessionTests), so a row dropped into
    // another section is refused with one caption rather than guessed at. The previous shape — a Reorderable around the
    // user queue alone and two blanket "Can't reorder" refusers below it — left a single queued track with NO legal
    // target at all (its lane was one row tall), and the drag then landed on whatever playlist surface was nearest.
    // Plain keyed rows are exactly what Reorderable's live projection wants (LiveProject moves the dragged row through
    // the keyed diff and FLIP animates its neighbours), which is why this panel's non-virtualized design gets premiere
    // reorder for free while the virtualized lists need ItemsView's InsertionOptions.
    readonly Reorderable _reorder = new(WaveeDragKinds.Resource)
    {
        ItemExtent = RowExtent,
        Spacing = 0f,
        DragStyle = new DragVisualStyle { Lift = DragLift.Stationary, Opacity = Drag.SourceDimOpacity },
        // A queue row released OUTSIDE the list commits nothing: no playlist surface accepts a queue drag any more
        // (WaveeResourceDragPayload.FromQueue), so such a release is simply a cancel — and the travel that got the
        // pointer there must not be read as a reorder intent either.
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

        // The upcoming list as reorder SLOTS — headers, realized rows and "Show more" rows in render order. Built from
        // this render's rows, so the geometry the reorder samples and the children the column arranges are one list.
        // Signals are read here (not peeked): a page reveal must re-render the slots it adds.
        var slots = QueueSlots.Build(
            userQueue, QueueSlots.Realized(userQueue.Count, _queuePages.Value, PageSize),
            ctxUp, QueueSlots.Realized(ctxUp.Count, _upPages.Value, PageSize),
            autoUp, QueueSlots.Realized(autoUp.Count, _autoPages.Value, PageSize),
            autoplay, headers: true);

        // Fresh closures every render: they read THIS render's rows, and the panel re-renders on every queue push.
        ConfigureReorder(b, acts, display, slots, userQueue, ctxUp, autoUp);

        var content = new List<Element>(4);
        if (source is { Length: > 0 })
            content.Add(PlayingFrom(source, sourceHref, go));
        if (track is { } t)
            content.Add(NowPlayingCard(b, lib, t, go, classic));
        if (slots.Count > 0)
            // The one insertion + reorder LANE: every upcoming slot. Grow is dropped (the wrapper defaults to filling
            // the free space of the scroll column, which would push a short list's tail to the bottom). The wrapper
            // holds ONLY the slots — no crumb, no card, no padding — because the reorder assumes slot 0's resting start
            // is the wrapper's own origin.
            content.Add((BoxEl)_reorder.List(Upcoming(slots, userQueue, ctxUp, autoUp, b, lib, go, display,
                                                      removable: !viewer, acts, menuOverlay, showTrackArtwork, classic,
                                                      reorder: viewer ? null : _reorder))
                with { Grow = 0f, Key = "lane:upcoming" });
        if (track is null && userQueue.Count == 0 && ctxUp.Count == 0)
            content.Add(EmptyState.Compact(Loc.Get(Strings.Player.NothingPlaying)));

        Element body = new BoxEl
        {
            Direction = 1, MinHeight = 0f,
            Padding = new Edges4(0f, 0f, 0f, 14f),
            Children = content.ToArray(),
        };
        // With NOTHING upcoming there is no lane to aim at, and a drop that lands nowhere is the "cannot drop in this
        // mode" failure this campaign exists to kill. The same Reorderable then mounts around the whole panel body:
        // item count 0 ⇒ every position resolves to slot 0, which is the play-next insert.
        if (slots.Count == 0)
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

    // ── drag & drop: one reorderable + insertable lane over every upcoming slot ───────────────────────────────────────

    /// <summary>Point the <see cref="Reorderable"/> at THIS render's slots. Everything here is a fresh closure on
    /// purpose: the panel re-renders on every queue push, and a delegate captured at mount would move the wrong row.</summary>
    void ConfigureReorder(PlaybackBridge b, ActionServices? acts, Signal<IReadOnlyList<QueueEntry>> display,
                          List<QueueSlot> slots, List<QueueEntry> userQueue, List<QueueEntry> ctxUp, List<QueueEntry> autoUp)
    {
        ReleaseStrandedLift(_reorder);
        _reorder.Scene = Context.Scene;
        _reorder.RequestRender = Context.RequestRerender;
        _reorder.ItemCount = slots.Count;
        // Variable extents: a header and a "Show more" row are slots too, and the slot math has to know how tall each
        // one rests at — otherwise every boundary below a header would sit one header-height off.
        _reorder.ExtentOf = i => (uint)i < (uint)slots.Count ? ExtentOf(slots[i]) : RowExtent;
        _reorder.ItemOf = i => (uint)i < (uint)slots.Count && slots[i].Entry is { } e
            ? WaveeResourceDragPayload.ForQueueRow(e)
            : null;
        _reorder.OnReorder = (from, to) => CommitMove(b, display, slots, userQueue, ctxUp, autoUp, from, to);
        // A foreign deposit lands in the USER QUEUE at the boundary the insertion line marked, mapped through the flat
        // slot list (a drop below the queue appends to it; slot 0 is play-next).
        _reorder.OnCrossCommit = (payload, _, _, _, slot) =>
            InsertAtSlot(b, acts, payload, QueueMovePlan.InsertIndex(slots, slot));
        // A viewer's queue belongs to the active device: an insert forwards as a set_queue rewrite, so drops still
        // work — only the local reorder path is withheld (MoveQueueItemAsync is a no-op while another device is active).
        _reorder.CanAcceptForeign = static p => WaveeResourceDrop.CanDepositTracks(p);
        _reorder.ForeignRefusalCaption = static p => WaveeResourceDrag.Unwrap(p) is { } r
            ? Loc.Get(r.FromQueue
                // A queue row from the OTHER queue surface (the stage pane vs this rail) is a reorder there, never an
                // insert here — the same reading every playlist target gives it.
                ? Strings.Drag.ReorderHint
                // Locked decision: an artist has no single obvious track set, so it is refused rather than guessed at.
                : r.Kind == WaveeResourceKind.Artist ? Strings.Drag.CantAddArtist : Strings.Drag.NothingToAdd)
            : null;
        _reorder.ForeignCaption = static (_, _) => Loc.Get(Strings.Drag.AddToQueue);
    }

    /// <summary>A pointer lift whose gesture ENDED without telling the list. The queue re-renders on every server push,
    /// and a push that re-keys the lifted row (a set_queue rewrite re-minting ids) frees its node mid-gesture; the
    /// engine keeps the session alive on the chip (a Stationary lift tolerates its source dying — <c>DragDropContext.
    /// PruneDead</c>) but can no longer deliver the node's own <c>OnDragCompleted</c>/<c>OnDragCanceled</c> at release
    /// (those columns died with it). The <see cref="Reorderable"/> then stays lifted forever: <c>ItemAt</c> keeps
    /// projecting the dead gesture's dwell slot, and the panel shows a row where the server never put it. A lift with
    /// no live drag session behind it is exactly that strand — drop it here, on the render that would otherwise paint
    /// the stale projection. The keyboard lift has no pointer session by design and is left alone.</summary>
    internal static void ReleaseStrandedLift(Reorderable reorder)
    {
        if (!reorder.IsLifted || reorder.IsKeyboardLifted) return;
        var drag = InputHooks.Current.Default.GetDragState?.Invoke() ?? default;
        if (!drag.Active) reorder.Core.Cancel();
    }

    static float ExtentOf(in QueueSlot slot) => slot.Kind switch
    {
        QueueSlotKind.Header => slot.Section == QueueSection.Autoplay ? HeaderSubExtent : HeaderExtent,
        QueueSlotKind.More => MoreExtent,
        _ => RowExtent,
    };

    /// <summary>The reorder COMMIT: a flat <c>(from, to)</c> from the one lane becomes a section-local move, or a
    /// refusal. The decision is <see cref="QueueMovePlan.For"/> (pure, tested); this only routes its verdict.
    /// <para>A refusal is TOLD, not swallowed: the list's own gesture is always accepted by its drop target (a same-list
    /// lift has no refuser to walk to), so the only place a cross-section drop can be explained is after the release —
    /// the same one-line informational toast <c>WaveeResourceDrop.MoveRootlist</c> shows for an illegal filing.</para></summary>
    internal static void CommitMove(PlaybackBridge b, Signal<IReadOnlyList<QueueEntry>> display, List<QueueSlot> slots,
                                    List<QueueEntry> userQueue, List<QueueEntry> ctxUp, List<QueueEntry> autoUp, int from, int to)
    {
        var plan = QueueMovePlan.For(slots, from, to);
        switch (plan.Kind)
        {
            case QueueMoveKind.Move:
                MoveInSection(b, display, SectionRows(plan.Section, userQueue, ctxUp, autoUp), plan.FromPos, plan.ToPos);
                break;
            case QueueMoveKind.Refused:
                Toast.Show(Loc.Get(Strings.Drag.CantMoveAcrossSections),
                           new ToastOptions { Severity = InfoBarSeverity.Informational });
                break;
        }
    }

    internal static List<QueueEntry> SectionRows(QueueSection section, List<QueueEntry> userQueue, List<QueueEntry> ctxUp,
                                                 List<QueueEntry> autoUp)
        => section switch
        {
            QueueSection.Queue => userQueue,
            QueueSection.NextUp => ctxUp,
            _ => autoUp,
        };

    /// <summary>Move a row inside its own section — the ONE path behind both the drag reorder and the context menu's
    /// ±1 verbs, on this rail AND on the stage's queue pane (<c>StageQueuePane</c> shares these statics rather than
    /// carrying a second copy). The optimistic snapshot mirrors the session op exactly (remove + insert at the
    /// post-removal index), which for a ±1 move is the same result the old swap produced.</summary>
    internal static void MoveInSection(PlaybackBridge b, Signal<IReadOnlyList<QueueEntry>> display,
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
    internal static void InsertAtSlot(PlaybackBridge b, ActionServices? acts, object? payload, int slot)
    {
        // CanCopyTracks is false for a QUEUE row (a reorder, never a deposit), so a row from the other queue surface
        // can never be re-inserted here as a duplicate.
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

        // The eyebrow row is pinned to the Clear pill's height whether or not a pill is present, so a header is the same
        // extent in every section — the reorder samples HeaderExtent for it (HeaderSubExtent with the hint line).
        var kids = new List<Element>(2)
        {
            new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S, MinHeight = HeaderRowH, Children = top.ToArray() },
        };
        if (sub is { Length: > 0 })
            kids.Add(new TextEl(sub) { Size = 12f, LineHeight = 16f, Color = Tok.TextTertiary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis });

        return new BoxEl
        {
            Key = "hdr:" + title,
            Direction = 1, Gap = Spacing.XXS,
            Height = sub is { Length: > 0 } ? HeaderSubExtent : HeaderExtent,
            Padding = new Edges4(Spacing.S, Spacing.M, Spacing.S, Spacing.XS),
            Layout = LayoutTransition.Slide,
            // A header is a slot the reorder geometry knows about, never a handle: a press inside it must not arm the
            // list's drag (there is nothing to lift), and the lane's own foreign-insert target still sees through it.
            BlocksDragArm = true,
            Children = kids.ToArray(),
        };
    }

    // ── The upcoming COLUMN: every slot as a plain keyed child — the reconciler's keyed diff + Enter/Exit/FLIP does all
    // the motion. Rendered through the live PROJECTION: mid-drag the dragged row occupies the slot it would land in and
    // the keyed diff + FLIP glide its neighbours (headers included) out of the way — the WinUI part-to-make-room motion
    // this panel's plain keyed rows can express natively. Visual pagination only: PageSize rows per revealed page, then
    // an explicit full-width "Show more (N left)" row — the UNDERLYING queue is untouched; every hidden track is one
    // click away and the remaining count is always visible. ──
    Element Upcoming(List<QueueSlot> slots, List<QueueEntry> userQueue, List<QueueEntry> ctxUp, List<QueueEntry> autoUp,
                     PlaybackBridge b, LibraryBridge? lib, Action<string, string?>? go,
                     Signal<IReadOnlyList<QueueEntry>> display, bool removable, ActionServices? acts,
                     IOverlayService? menuOverlay, bool showTrackArtwork, bool classic, Reorderable? reorder)
    {
        var kids = new List<Element>(slots.Count);
        for (int i = 0; i < slots.Count; i++)
        {
            int item = reorder is { } ro ? ro.ItemAt(i) : i;
            if ((uint)item >= (uint)slots.Count) item = i;
            var slot = slots[item];
            // The WHOLE section list, not the realized prefix: the header's count and the ±1 menu verbs bound against
            // everything the section holds, while the slots cover only what is on screen.
            var section = SectionRows(slot.Section, userQueue, ctxUp, autoUp);
            switch (slot.Kind)
            {
                case QueueSlotKind.Header:
                    kids.Add(slot.Section switch
                    {
                        QueueSection.Queue => SectionHeader(Loc.Get(Strings.Player.NextInQueue), section.Count,
                            removable ? () => ClearUserQueue(b, display) : null),
                        QueueSection.NextUp => SectionHeader(Loc.Get(Strings.Player.NextUp), section.Count, null),
                        _ => SectionHeader(Loc.Get(Strings.Player.Autoplay), section.Count, null,
                            sub: Loc.Get(Strings.Player.AutoplayHint)),
                    });
                    break;
                case QueueSlotKind.More:
                {
                    var pages = PagesOf(slot.Section);
                    int shown = QueueSlots.Realized(section.Count, pages.Peek(), PageSize);
                    kids.Add(ShowMore(Tag(slot.Section), Math.Min(PageSize, section.Count - shown), section.Count - shown,
                        () => pages.Value = pages.Peek() + 1));
                    break;
                }
                default:
                {
                    var entry = slot.Entry!;
                    var row = QueueRow(b, lib, go, display, entry, slot.Pos, section, removable,
                                       dim: slot.Section == QueueSection.Autoplay,
                                       acts, menuOverlay, _swipeGroup, reorder is null, showTrackArtwork, classic);
                    // Direction 1 on the wrapper: its single child must stretch across the WIDTH (a row-direction
                    // wrapper would size the row to its content and collapse the hover plate to the text).
                    kids.Add(reorder is { } r
                        ? (BoxEl)r.Item(item, row, key: RowKey(entry)) with { Direction = 1 }
                        : row);
                    break;
                }
            }
        }
        return new BoxEl { Key = "upcoming", Direction = 1, Children = kids.ToArray() };
    }

    Signal<int> PagesOf(QueueSection section) => section switch
    {
        QueueSection.Queue => _queuePages,
        QueueSection.NextUp => _upPages,
        _ => _autoPages,
    };

    internal static string Tag(QueueSection section) => section switch
    {
        QueueSection.Queue => "q",
        QueueSection.NextUp => "u",
        _ => "a",
    };

    // Full-width load-more affordance: "⌄ Show next 100 · 63 more" — the pagination is explicit, never a silent cut.
    // Its Height is authored (with the margins, MoreExtent) because it is a reorder slot — see QueueSlots.
    static Element ShowMore(string sectionTag, int nextPage, int remaining, Action more) => new BoxEl
    {
        Key = "more:" + sectionTag,
        Direction = 0, Height = MoreRowH, Gap = Spacing.S, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        BlocksDragArm = true,
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
            // already unwraps); a viewer's row (remote device active — no local reorder) drags itself. Either way the
            // payload is the QUEUE row (ForQueueRow): a reorder gesture that no playlist surface may take as a copy.
            Draggable = ownDrag
                ? Drag.Source(WaveeDragKinds.Resource, () => WaveeResourceDragPayload.ForQueueRow(entry))
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
