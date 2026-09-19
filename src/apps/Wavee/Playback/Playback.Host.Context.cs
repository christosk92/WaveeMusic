// ── Playback/Playback.Host.Context.cs ────────────────────────────────────────────────────────────────────────────────
// Playing a context: the context resolve and its start, rows a caller holds, radio, the queue reorder, a controller's
// add-to-queue, the launch restore, and the play report (registration, resume points and their local progress mirror,
// the play log), plus where an episode load starts (the row's progress, podcast plan §5.8). The inbound Connect
// intake (play / transfer / set_queue / update_context, in arrival order) is `Playback.Host.Remote.cs`; autoplay and its
// pages are `Playback.Host.Autoplay.cs`.
//
// Role: SHELL
// Owner: G
// Wave: gap batch B3 (+ B3c: the kept user queue, PlayRows, the station path, the resolved-start slot, the uid book;
//       + R4-1: context paging, the registration's playback id on the wire; the scope-switch rebind these writes call
//       first lives in `Playback.Host.Wire.cs`)
// Budget: 600 lines
// Spec: gap register G-070, G-071 (host half), G-074, G-075, G-076, G-078, G-079, G-080, G-241, G-242
//
// A NAMED PARTIAL OF `Playback.Host.cs`, declared when the loop passed 30 % over its 800-line budget. The same SHELL
// rules: the UI thread owns the queue and the tables (C1), blocking work runs on an api thread (`Spotify.Api.Run`) and
// comes back through `ToUi` or `Post` (C9), and every decision it applies is a pure rule in `Playback.Transitions.cs`
// (`RemotePlan`, `QueueUid`, `ShuffleOrder`, `RestorePoint`) rather than restated here.
//
// A CONTEXT LOAD, ONE PATH FOR THREE INTAKES:
//
//   PlayContext(uri | "spotify:station:…")  ─┐
//   StartRadio(seed), nothing live here      │   (seed → its inspiredby-mix playlist first; a LIVE deck parks it
//   RemoteLoadArrived(play)                  ├─▶ instead — §3a)
//   RemoteLoadArrived(transfer)             ─┘   rows (embedded, or ContextResolve on an api thread) ─▶ StartContext:
//                                                   history ≤ 50 │ now playing │ kept user queue (play) or the
//                                                   transfer's queue │ next up (shuffled?)
//                                                   ─▶ Queue.Replace ─▶ ShuffleOrdered · Repeat · PlayFrom(cause, paused, from)
//
//   PlayRows(rows, start) / PlayContext(a playable) ─▶ RemotePlan.Layout (the same shape, rows the caller holds)
//                                                   ─▶ Queue.Replace ─▶ Entities.Publish ─▶ Play
//
// A CONTEXT BIGGER THAN ONE PAGE (G-242): the resolve's next_page_url is kept with the context (`s_pageUrl`) and the
// reducer is told (`ContextPages`); its run-out asks for the page (`fx.Page` → RequestPage) before autoplay, the rows are
// appended as Context NextUp, and a station — which autoplay refuses — pages for as long as its pages name a next one.
//
// THE USER'S QUEUE SURVIVES A NEW CONTEXT (0.2.9 `SetContext(keepUserQueue: true)`): every play keeps the queued rows
// still waiting and lands them straight after the new deck row (`RemotePlan.KeptQueue`); only a transfer replaces them
// with the remote's own.
//
// THE AUTOPLAY TAIL SURVIVES THE SAME CONTEXT (`RemotePlan.SameContext` / `KeptAutoplay`): a play that lands the context
// already playing keeps the autoplay rows after the new context run and keeps the held autoplay page, so the reducer's
// Refill does not ask autoplay for the same context on every track change. A different context drops both.
//
// Every intake is sequence-numbered: a resolve answering after a newer load (or a takeover) is dropped, and its pooled
// buffer goes back either way.

using System.Buffers;
using System.Security.Cryptography;

using FluentGpu.Foundation;
using FluentGpu.Signals;

using ClusterBuffer = Wavee.Spotify.Decode.ClusterBuffer;
using ClusterTrack = Wavee.Spotify.Decode.ClusterTrack;
using RemoteCmd = Wavee.Spotify.Decode.RemoteCmd;
using RemoteLoad = Wavee.Spotify.Decode.RemoteLoad;
using RepeatMode = Wavee.Spotify.Decode.RepeatMode;

namespace Wavee;

public static partial class Playback
{
    // ── 1. the seams the shell installs ────────────────────────────────────────────────────────────────────────────

    /// <summary>A row's audio began — one call per registration, however many reloads it lives through (G-079). Owner
    /// I's `Shell.Host.cs` installs <c>(track, context, unixMs) =&gt; PlayLog.Append(...)</c>; null records nothing.</summary>
    public static Action<EntityId, EntityId, long>? PlayStarted { get; set; }

    /// <summary>Persist the deck for the next launch (G-078). Owner I's session document installs it; it is called on
    /// every snapshot-worthy transition (play, pause, seek on a parked deck, volume, sign-out) and must debounce its own
    /// write. <see cref="RestorePointNow"/> is the exit tail's final read.</summary>
    public static Action<RestorePoint>? PersistDeck { get; set; }

    /// <summary>The deck as a restore point, NOW (UI thread) — the exit tail's last write.</summary>
    public static RestorePoint RestorePointNow() => RestorePoint.Of(in s_state, FrameNowMs());

    /// <summary>Bumped by every context load — local, inbound or a held row set — so a resolve answering after a newer one
    /// is dropped. The inbound load's intake lives in `Playback.Host.Remote.cs`.</summary>
    static long s_contextSeq;

    // ── 3. playing a context (G-070) ───────────────────────────────────────────────────────────────────────────────

    /// <summary>Play a CONTEXT — an album, a playlist, an artist, a show, the collection, a station — from its head, from
    /// <paramref name="startAt"/> when named, at <paramref name="fromMs"/>. UI thread. A playable uri plays as a one-row
    /// context at once (<see cref="PlayRows"/>, which keeps the user's queue); anything else is resolved on an api thread
    /// first. Owner Q's <c>Queue.InstallUi</c> installs it as <c>Shell.OnPlayContext</c> and the Play verb.</summary>
    public static void PlayContext(EntityId context, EntityId startAt = default, int fromMs = -1)
    {
        if (context.IsEmpty || Entities.Current is null) return;
        // Another device owns playback: the click is a COMMAND to it, never a resolve here (StartContext would drop the
        // load anyway once it saw the foreign owner, and the listener saw nothing happen — 2026-09-16). The same desktop
        // `play` envelope a row click forwards from the reducer (DoPlay → ForwardPlay), sent straight from the host
        // because there is no row to post yet.
        if (s_state.Owner == Owner.Foreign && s_state.Own.Device != 0)
        {
            string target = Devices.IdOf(s_state.Own.Device);
            if (target.Length == 0) { Log.Warn("playback", "play dropped: the owner is not in the roster"); return; }
            string contextUri = context.Text, trackUri = startAt.IsEmpty ? "" : startAt.Text;
            bool shuffle = s_state.Shuffle;
            Log.Info("playback", "play forwarded to the owner: context=" + contextUri + " track=" + trackUri);
            Spotify.Api.Run(() => Spotify.Connect.PlayContext(target, contextUri, trackUri, shuffle, CancellationToken.None, fromMs));
            return;
        }
        if (context.IsPlayable)
        {
            EntityRef row = Entities.Ref(context);
            if (row.IsNone) return;
            Span<EntityRef> one = [row];
            PlayRows(one, 0, context, fromMs);
            return;
        }

        ClusterBuffer buffer = ClusterBuffer.Rent();
        var load = default(RemoteLoad);
        load.Kind = RemoteCmd.Play;
        load.SkipToIndex = -1;
        load.SeekToMs = Math.Max(-1, fromMs);
        load.Shuffle = -1;
        load.Repeat = -1;
        load.Speed = 1.0;
        Span<byte> text = stackalloc byte[512];
        load.ContextUri = buffer.AddText(text[..context.Format(text)]);
        if (!startAt.IsEmpty) load.SkipToUri = buffer.AddText(text[..startAt.Format(text)]);
        ResolveContext(++s_contextSeq, context.Text, load, buffer, ClaimCause.UserPlay);
    }

    /// <summary>Play a context named by its uri TEXT — a station (<c>spotify:station:track:…</c>), a radio, any context
    /// uri that is not a catalog row of its own. The text becomes a text-form <see cref="EntityId"/> (kind Unknown,
    /// provider Spotify), which is what <see cref="State.Context"/> and the PUT body carry, and from there the resolve, the
    /// queue build and autoplay's refusal of an infinite context are exactly the ones every other context takes. UI thread
    /// (the parse interns).</summary>
    public static void PlayContext(string contextUri, EntityId startAt = default, int fromMs = -1)
    {
        if (string.IsNullOrEmpty(contextUri) || Entities.Current is null) return;
        PlayContext(EntityId.Parse(contextUri.AsSpan()), startAt, fromMs);
    }

    // ── 3a. "Start radio" (G-251, D34 — 0.2.9 `PlaybackController.StartRadioAsync`) ──────────────────────────────────
    //
    //   StartRadio(seed) ─▶ api thread: RadioSeed(seed) → mediaItems[0].uri ─▶ ContextResolve(playlist) into ONE buffer
    //                    ─▶ UI thread, LandRadio:
    //                         no radio / empty / superseded        ─▶ RadioOutcome(Started: false)      → "Couldn't start radio"
    //                         RemotePlan.RadioParks == false        ─▶ Resolved(…) → the one context path → plays row 0 now
    //                         a track is live on this deck (PARK)  ─▶ ParkRadio: history │ deck (untouched) │ kept queue │
    //                                                                  radio rows as Context NextUp (the deck's own recording
    //                                                                  dropped) ─▶ Queue.Replace ─▶ SwitchContext (no Play,
    //                                                                  no load) ─▶ ContextPages follows the playlist's pages
    //
    // The park never touches the audio host: the deck row keeps its slot, cursor index, epoch and registration, and the
    // reducer's next-row arm simply reads the rewritten rows behind it. `done` is invoked on the UI thread exactly once.

    /// <summary>What "Start radio" did (G-251), for the toast the UI raises: <see cref="Started"/> with the radio
    /// <see cref="Playlist"/> (its page is the toast's "Open playlist" action), <see cref="Parked"/> when the current
    /// track was left to finish first; <c>Started == false</c> is "Couldn't start radio". <see cref="Name"/> is a display
    /// name a caller who knows one may fill in (the host does not know the playlist's title).</summary>
    public readonly record struct RadioOutcome(bool Started, EntityId Playlist, string? Name, bool Parked);

    /// <summary><c>ActionServices.StartRadio</c>: song radio or artist radio from <paramref name="seed"/>, 0.2.9's way.
    /// The seed resolves to its REAL radio playlist (<c>inspiredby-mix</c>, <see cref="Spotify.Api.RadioSeed"/>), the
    /// playlist resolves like any other context, and then: nothing live on this deck → it plays from its first row
    /// through the one context path; a track playing here → it is PARKED behind that track, which finishes untouched,
    /// and becomes the context (<see cref="RemotePlan.RadioParks"/>, <see cref="RemotePlan.RadioAfterCurrent"/>). A
    /// seed with no radio, a refused resolve or an empty playlist changes nothing. <paramref name="done"/> gets the
    /// outcome on the UI thread, exactly once. UI thread.</summary>
    public static void StartRadio(EntityUri seed, Action<RadioOutcome>? done = null)
    {
        string seedUri = RemotePlan.RadioSeedUri(seed.Id);
        if (seedUri.Length == 0 || Entities.Current is null)
        {
            Log.Info("playback", "radio: the seed has no radio (kind=" + seed.Kind + ")");
            done?.Invoke(default);
            return;
        }
        long seq = s_contextSeq;                           // a context loaded after this click wins over the radio
        ClusterBuffer buffer = ClusterBuffer.Rent();
        bool queued = Spotify.Api.Run(() =>
        {
            string playlist = "", pageUrl = "";
            int start = buffer.TrackCount, count = 0;
            var load = default(RemoteLoad);
            load.Kind = RemoteCmd.Play;
            load.SkipToIndex = -1;
            load.Shuffle = -1;
            load.Repeat = -1;
            load.Speed = 1.0;
            try
            {
                Spotify.Api.Result seedResult = Spotify.Api.RadioSeed(seedUri, CancellationToken.None);
                if (seedResult.Ok && seedResult.Body.Length > 0) playlist = Spotify.Decode.RadioPlaylistUri(seedResult.Bytes) ?? "";
                else Log.Warn("playback", "radio: seed refused (" + seedResult.Status + ") " + seedUri);
                if (playlist.Length > 0)
                {
                    load.ContextUri = buffer.AddText(System.Text.Encoding.UTF8.GetBytes(playlist));
                    Spotify.Api.Result result = Spotify.Api.ContextResolve(playlist, CancellationToken.None);
                    if (result.Ok && result.Body.Length > 0)
                    {
                        Spotify.Decode.ContextPage page = Spotify.Decode.ContextResolve(result.Bytes, buffer);
                        start = page.TrackStart;
                        count = page.TrackCount;
                        if (!page.NextPageUrl.IsEmpty) pageUrl = System.Text.Encoding.UTF8.GetString(buffer.Utf8(page.NextPageUrl));
                    }
                    else Log.Warn("playback", "radio: playlist resolve refused (" + result.Status + ") " + playlist);
                }
            }
            catch (Exception ex) { Log.Warn("playback", "radio: resolve failed", ex); }
            ToUi(() => LandRadio(seq, seedUri, playlist, in load, buffer, start, count, pageUrl, done));
        });
        if (queued) return;
        Log.Warn("playback", "radio: refused: the api queue is full");
        ClusterBuffer.Return(buffer);
        done?.Invoke(default);
    }

    /// <summary>UI thread: the radio resolved (or did not). Decides park-or-play on the deck AS IT IS NOW — not as it was
    /// at the click — hands the buffer to the context path's slot when it plays at once, and returns it otherwise.</summary>
    static void LandRadio(long seq, string seedUri, string playlist, in RemoteLoad load, ClusterBuffer buffer,
        int start, int count, string pageUrl, Action<RadioOutcome>? done)
    {
        bool owned = true, started = false, parked = false;
        EntityId playlistId = default;
        try
        {
            if (playlist.Length == 0 || count == 0 || Entities.Current is null)
            {
                Log.Info("playback", "radio: unavailable for " + seedUri + (playlist.Length == 0 ? " (no playlist)" : " (empty playlist)"));
                return;
            }
            if (seq != s_contextSeq) { Log.Info("playback", "radio: dropped, a newer context loaded meanwhile"); return; }
            playlistId = EntityId.Parse(playlist.AsSpan());
            Rebind();                                      // the deck and queue this decides on must be this scope's (G-241)
            int deck = s_state.Cursor.Index;
            if (Queue.RefAt(deck) != s_state.Current) deck = Queue.Divider(Queue.Rows, -1);
            bool deckLive = deck >= 0 && Queue.RefAt(deck) == s_state.Current;
            if (!RemotePlan.RadioParks(in s_state, deckLive))
            {
                // Nothing live here: the resolved playlist takes the one context path and plays from row 0 at once. The
                // slot owns the buffer from here (a newer resolve displaces and returns it; StartResolved returns it).
                Resolved(++s_contextSeq, in load, buffer, start, count, ClaimCause.UserPlay, pageUrl);
                owned = false;
                // Honest toast: while another device owns playback StartContext drops the load (as PlayContext does), so
                // nothing starts here — the outcome says so instead of announcing a radio that never plays.
                started = s_state.Owner != Owner.Foreign;
                Log.Info("playback", "radio: playing " + playlist + " now (rows=" + count + ", pages=" + (pageUrl.Length > 0 ? "more" : "one") + ")");
                return;
            }
            started = parked = ParkRadio(playlistId, buffer, start, count, pageUrl, deck);
            if (!started) Log.Info("playback", "radio: could not park " + playlist + " behind the deck");
        }
        finally
        {
            if (owned) ClusterBuffer.Return(buffer);
            done?.Invoke(new RadioOutcome(started, playlistId, null, parked));
        }
    }

    /// <summary>PARK the resolved radio behind the deck row at <paramref name="deck"/> (0.2.9
    /// <c>SwitchContextAfterCurrent</c>): the queue becomes history │ the deck row, untouched │ the user's still-waiting
    /// queued rows │ the radio as Context NextUp minus the deck's own recording (<see cref="RemotePlan.RadioAfterCurrent"/>),
    /// shuffled behind the deck when shuffle is on; the reducer is told the context changed under its row
    /// (<see cref="Input.SwitchContext"/> — no Play, no load) and that the radio's pages are what the run-out follows
    /// (<see cref="Input.ContextPages"/>). Moves the context sequence on, so an older resolve or page answering later is
    /// dropped. False, with nothing written, when the layout refused the deck.</summary>
    static bool ParkRadio(EntityId playlist, ClusterBuffer buffer, int start, int count, string pageUrl, int deck)
    {
        ReadOnlySpan<ClusterTrack> tracks = buffer.Tracks(start, count);
        int live = Math.Max(1, Queue.Count);
        int radioCap = Math.Min(count, RemotePlan.MaxRows);
        int cap = deck + 1 + live + radioCap;
        int[] keptPacked = ArrayPool<int>.Shared.Rent(live);
        QueueEdge[] keptRows = ArrayPool<QueueEdge>.Shared.Rent(live);
        EntityRef[] radioRefs = ArrayPool<EntityRef>.Shared.Rent(radioCap);
        QueueEdge[] radioRows = ArrayPool<QueueEdge>.Shared.Rent(radioCap);
        int[] radioPacked = ArrayPool<int>.Shared.Rent(radioCap);
        int[] packed = ArrayPool<int>.Shared.Rent(cap);
        QueueEdge[] rows = ArrayPool<QueueEdge>.Shared.Rent(cap);
        try
        {
            int kept = RemotePlan.KeptQueue(Queue.PackedRefs, Queue.Rows, keptPacked, keptRows);
            int r = 0;
            for (int k = 0; k < tracks.Length && r < radioCap; k++)
                AddRow(buffer, in tracks[k], QueueBucket.NextUp, radioRefs, radioRows, ref r);
            Queue.Pack(radioRefs.AsSpan(0, r), radioPacked);
            int n = RemotePlan.RadioAfterCurrent(Queue.PackedRefs, Queue.Rows, deck, keptPacked.AsSpan(0, kept), keptRows.AsSpan(0, kept),
                radioPacked.AsSpan(0, r), radioRows.AsSpan(0, r), packed.AsSpan(0, cap), rows.AsSpan(0, cap), out int dropped);
            if (n < 0) return false;
            ++s_contextSeq;
            s_orderRefs = null;
            s_orderRows = null;
            FillQueueIds(playlist, packed.AsSpan(0, n), rows.AsSpan(0, n));
            if (s_state.Shuffle) Reorder(packed.AsSpan(0, n), rows.AsSpan(0, n), deck, shuffle: true);
            Queue.Replace(packed.AsSpan(0, n), rows.AsSpan(0, n));
            s_uids.Retain(Queue.Rows);
            Entities.Publish();                            // the rail answers the click in its own frame (PlayRows' rule)
            long now = FrameNowMs();
            Apply(Input.SwitchContext(playlist, Queue.CursorOf(deck), now));
            s_pageContext = playlist;
            s_pageUrl = pageUrl;
            ForgetAutoplayPage();
            Apply(Input.ContextPages(playlist, pageUrl.Length > 0, now));
            Log.Info("playback", "radio: parked " + playlist.Text + " behind the deck (radio rows=" + (r - dropped) + ", dropped=" + dropped
                + ", kept=" + kept + ", queue=" + n + ", pages=" + (pageUrl.Length > 0 ? "more" : "one") + ")");
            return true;
        }
        finally
        {
            ArrayPool<int>.Shared.Return(keptPacked);
            ArrayPool<QueueEdge>.Shared.Return(keptRows);
            ArrayPool<EntityRef>.Shared.Return(radioRefs);
            ArrayPool<QueueEdge>.Shared.Return(radioRows);
            ArrayPool<int>.Shared.Return(radioPacked);
            ArrayPool<int>.Shared.Return(packed);
            ArrayPool<QueueEdge>.Shared.Return(rows);
        }
    }

    /// <summary>Play rows the caller already HOLDS as the context — a page's visible (sorted, filtered) order, a menu's one
    /// track — from <paramref name="start"/>, at <paramref name="fromMs"/>. UI thread. The queue is laid out by
    /// <see cref="RemotePlan.Layout"/> (history, the deck, the user's still-waiting queued rows, the rest as next up, the
    /// next-up run shuffled when shuffle is on), published at once so the queue surfaces answer the click in its own frame,
    /// and one Play is posted. A context resolve still in flight is superseded.</summary>
    public static void PlayRows(ReadOnlySpan<EntityRef> rows, int start, EntityId context, int fromMs = -1)
    {
        if (Entities.Current is null || (uint)start >= (uint)rows.Length || rows[start].IsNone) return;
        Rebind();                                          // the queue this play keeps from must be this scope's (G-241)
        int existing = Math.Max(1, Queue.Count);
        int[] targets = ArrayPool<int>.Shared.Rent(rows.Length);
        int[] keptTargets = ArrayPool<int>.Shared.Rent(existing);
        QueueEdge[] keptRows = ArrayPool<QueueEdge>.Shared.Rent(existing);
        int[] autoTargets = ArrayPool<int>.Shared.Rent(existing);
        QueueEdge[] autoRows = ArrayPool<QueueEdge>.Shared.Rent(existing);
        int[] packed = ArrayPool<int>.Shared.Rent(RemotePlan.MaxRows);
        QueueEdge[] laid = ArrayPool<QueueEdge>.Shared.Rent(RemotePlan.MaxRows);
        try
        {
            Queue.Pack(rows, targets);
            // The same context again keeps its autoplay tail (and the held autoplay page), so the reducer does not re-ask.
            bool sameContext = RemotePlan.SameContext(context, s_state.Context);
            int kept = RemotePlan.KeptQueue(Queue.PackedRefs, Queue.Rows, keptTargets, keptRows);
            int auto = RemotePlan.KeptAutoplay(Queue.PackedRefs, Queue.Rows, sameContext, autoTargets, autoRows);
            int n = RemotePlan.Layout(targets.AsSpan(0, rows.Length), start, keptTargets.AsSpan(0, kept), keptRows.AsSpan(0, kept),
                autoTargets.AsSpan(0, auto), autoRows.AsSpan(0, auto),
                packed.AsSpan(0, RemotePlan.MaxRows), laid.AsSpan(0, RemotePlan.MaxRows), out int deck);
            if (deck < 0) return;
            ++s_contextSeq;
            s_orderRefs = null;
            s_orderRows = null;
            FillQueueIds(context, packed.AsSpan(0, n), laid.AsSpan(0, n));
            if (s_state.Shuffle) Reorder(packed.AsSpan(0, n), laid.AsSpan(0, n), deck, shuffle: true);
            Queue.Replace(packed.AsSpan(0, n), laid.AsSpan(0, n));
            Entities.Publish();
            EntityRef row = rows[start];
            long now = FrameNowMs();
            Post(Input.PlayFrom(row, row.Id, context, Queue.CursorOf(deck), PlayableKind.Audio, Math.Max(0, fromMs), now,
                ClaimCause.UserPlay, paused: false, explicitPosition: fromMs >= 0));
            s_pageUrl = "";                                // the caller's rows ARE the context: nothing to page (G-242)
            if (!sameContext) ForgetAutoplayPage();
            Post(Input.ContextPages(context, morePages: false, now));
        }
        finally
        {
            ArrayPool<int>.Shared.Return(targets);
            ArrayPool<int>.Shared.Return(keptTargets);
            ArrayPool<QueueEdge>.Shared.Return(keptRows);
            ArrayPool<int>.Shared.Return(autoTargets);
            ArrayPool<QueueEdge>.Shared.Return(autoRows);
            ArrayPool<int>.Shared.Return(packed);
            ArrayPool<QueueEdge>.Shared.Return(laid);
        }
    }

    /// <summary>Append the user's still-waiting queued rows of the queue about to be replaced (<see cref="RemotePlan.KeptQueue"/>)
    /// to a context build at <paramref name="n"/>, up to <paramref name="cap"/>.</summary>
    static void AddKeptQueue(EntityRef[] refs, QueueEdge[] rows, ref int n, int cap) => AddKept(refs, rows, ref n, cap, autoplay: false);

    /// <summary>Append the autoplay tail a same-context play keeps (<see cref="RemotePlan.KeptAutoplay"/>) after the context run.</summary>
    static void AddKeptAutoplay(EntityRef[] refs, QueueEdge[] rows, ref int n, int cap) => AddKept(refs, rows, ref n, cap, autoplay: true);

    static void AddKept(EntityRef[] refs, QueueEdge[] rows, ref int n, int cap, bool autoplay)
    {
        int count = Queue.Count;
        if (count == 0 || n >= cap) return;
        int[] packed = ArrayPool<int>.Shared.Rent(count);
        QueueEdge[] kept = ArrayPool<QueueEdge>.Shared.Rent(count);
        try
        {
            Span<int> room = packed.AsSpan(0, Math.Min(count, cap - n));
            int k = autoplay
                ? RemotePlan.KeptAutoplay(Queue.PackedRefs, Queue.Rows, sameContext: true, room, kept)
                : RemotePlan.KeptQueue(Queue.PackedRefs, Queue.Rows, room, kept);
            for (int i = 0; i < k; i++)
            {
                refs[n] = Queue.Unpack(packed[i]);
                rows[n] = kept[i];
                n++;
            }
        }
        finally
        {
            ArrayPool<int>.Shared.Return(packed);
            ArrayPool<QueueEdge>.Shared.Return(kept);
        }
    }

    /// <summary>Resolve <paramref name="uri"/> on an api thread into the same buffer, then hand the rows — and the next
    /// page's url, when the context has one (G-242) — to the next drain (<see cref="Resolved"/>). The buffer is returned
    /// exactly once, whichever way it ends.</summary>
    static void ResolveContext(long seq, string uri, RemoteLoad load, ClusterBuffer buffer, ClaimCause cause)
    {
        bool queued = Spotify.Api.Run(() =>
        {
            int start = buffer.TrackCount, count = 0;
            string pageUrl = "";
            try
            {
                Spotify.Api.Result result = Spotify.Api.ContextResolve(uri, CancellationToken.None);
                if (result.Ok && result.Body.Length > 0)
                {
                    Spotify.Decode.ContextPage page = Spotify.Decode.ContextResolve(result.Bytes, buffer);
                    start = page.TrackStart;
                    count = page.TrackCount;
                    load.ResolvedMetadata = page.Metadata;
                    if (!page.NextPageUrl.IsEmpty) pageUrl = System.Text.Encoding.UTF8.GetString(buffer.Utf8(page.NextPageUrl));
                }
                else Log.Warn("playback", "context resolve refused (" + result.Status + ")");
            }
            catch (Exception ex) { Log.Warn("playback", "context resolve failed", ex); }
            ToUi(() => Resolved(seq, in load, buffer, start, count, cause, pageUrl));
        });
        if (queued) return;
        Log.Warn("playback", "context resolve refused: the api queue is full");
        ClusterBuffer.Return(buffer);
        ReleaseHold(seq);
    }

    static long s_resolvedSeq;
    static RemoteLoad s_resolvedLoad;
    static ClusterBuffer? s_resolvedBuffer;
    static int s_resolvedStart, s_resolvedCount;
    static ClaimCause s_resolvedCause;
    static string s_resolvedPageUrl = "";

    /// <summary>UI thread: a resolve answered. Its start is folded at the TOP of the next drain (<see cref="StartResolved"/>),
    /// never from this callback — a Connect verb held back behind an inbound load (`Playback.Host.Remote.cs`) must land
    /// after the load's Play, and inputs posted from here would reach the reducer after the mailbox. One slot: the newer
    /// load wins, and the buffer it displaces goes back.</summary>
    static void Resolved(long seq, in RemoteLoad load, ClusterBuffer buffer, int start, int count, ClaimCause cause, string pageUrl)
    {
        if (s_resolvedBuffer is { } waiting)
        {
            if (s_resolvedSeq > seq) { ClusterBuffer.Return(buffer); return; }
            ClusterBuffer.Return(waiting);
        }
        s_resolvedSeq = seq;
        s_resolvedLoad = load;
        s_resolvedBuffer = buffer;
        s_resolvedStart = start;
        s_resolvedCount = count;
        s_resolvedCause = cause;
        s_resolvedPageUrl = pageUrl;
        RequestDrain();
    }

    /// <summary>Inside the drain, first: start the resolved context (its inputs step inline) and release the hold its
    /// inbound load put on the mailbox.</summary>
    static void StartResolved(long now)
    {
        if (s_resolvedBuffer is not { } buffer) return;
        s_resolvedBuffer = null;
        long seq = s_resolvedSeq;
        RemoteLoad load = s_resolvedLoad;
        s_resolvedLoad = default;
        ReleaseHold(seq);
        string pageUrl = s_resolvedPageUrl;
        s_resolvedPageUrl = "";
        try { StartContext(seq, in load, buffer, s_resolvedStart, s_resolvedCount, s_resolvedCause, now, pageUrl); }
        finally { ClusterBuffer.Return(buffer); }
    }

    /// <summary>UI thread: the rows are known — build the queue in reading order and fold the start. Dropped when a newer
    /// context load superseded this one, or another device took playback meanwhile. <paramref name="pageUrl"/> is the
    /// context's next page, kept for its run-out (G-242).</summary>
    static void StartContext(long seq, in RemoteLoad load, ClusterBuffer buffer, int contextStart, int contextCount,
        ClaimCause cause, long now, string pageUrl = "")
    {
        if (seq != s_contextSeq || Entities.Current is null) return;
        if (s_state.Owner == Owner.Foreign) return;
        CaptureOrigin(in load, buffer);

        ReadOnlySpan<ClusterTrack> context = buffer.Tracks(contextStart, contextCount);
        bool transfer = load.Kind == RemoteCmd.Transfer;
        EntityId contextId = load.ContextUri.IsEmpty ? default : EntityId.Parse(buffer.Utf8(load.ContextUri));
        // The same context again keeps its autoplay tail (and the held autoplay page); a transfer brings the remote's queue.
        bool sameContext = !transfer && RemotePlan.SameContext(contextId, s_state.Context);
        int start = transfer
            ? (load.HasCurrent ? RemotePlan.StartIndex(buffer, context, load.CurrentUid.IsEmpty ? load.Current.Uid : load.CurrentUid, load.Current.Uri, -1) : -1)
            : RemotePlan.StartIndex(buffer, context, load.SkipToUid, load.SkipToUri, load.SkipToIndex);
        if (start < 0 && (!transfer || (!load.HasCurrent && load.AlwaysPlaySomething)) && context.Length > 0) start = 0;

        int cap = RemotePlan.MaxRows;
        EntityRef[] refs = ArrayPool<EntityRef>.Shared.Rent(cap);
        QueueEdge[] rows = ArrayPool<QueueEdge>.Shared.Rent(cap);
        try
        {
            int n = 0;
            if (start > 0)
                for (int k = Math.Max(0, start - RemotePlan.HistoryCap); k < start; k++)
                    AddRow(buffer, in context[k], QueueBucket.History, refs, rows, ref n);
            int at = n;
            if (start >= 0) AddRow(buffer, in context[start], QueueBucket.NowPlaying, refs, rows, ref n);
            else if (transfer && load.HasCurrent) AddRow(buffer, in load.Current, QueueBucket.NowPlaying, refs, rows, ref n);
            if (n == at)
            {
                Log.Info("playback", "context load had nothing to play (rows=" + context.Length + ")");
                return;
            }
            // A play keeps the user's still-waiting queue right after the new deck row; a transfer brings the remote's.
            if (!transfer) AddKeptQueue(refs, rows, ref n, cap);
            else
            {
                ReadOnlySpan<ClusterTrack> queued = buffer.Tracks(load.TrackStart, load.TrackCount);
                for (int k = 0; k < queued.Length && n < cap; k++)
                {
                    // A remote playing out of its user queue carries the current row at the queue's head.
                    if (k == 0 && load.IsPlayingQueue && buffer.Utf8(queued[k].Uri).SequenceEqual(buffer.Utf8(load.Current.Uri))) continue;
                    AddRow(buffer, in queued[k], QueueBucket.UserQueue, refs, rows, ref n);
                }
            }
            for (int k = start + 1; k < context.Length && n < cap; k++)
                AddRow(buffer, in context[k], QueueBucket.NextUp, refs, rows, ref n);
            if (sameContext) AddKeptAutoplay(refs, rows, ref n, cap);

            bool shuffle = load.Shuffle >= 0 ? load.Shuffle == 1 : s_state.Shuffle;
            int[] packed = ArrayPool<int>.Shared.Rent(n);
            try
            {
                Queue.Pack(refs.AsSpan(0, n), packed);
                s_orderRefs = null;
                s_orderRows = null;
                FillQueueIds(contextId, packed.AsSpan(0, n), rows.AsSpan(0, n));
                if (shuffle) Reorder(packed.AsSpan(0, n), rows.AsSpan(0, n), at, shuffle: true);
                Queue.Replace(packed.AsSpan(0, n), rows.AsSpan(0, n));
                s_uids.Retain(Queue.Rows);
            }
            finally { ArrayPool<int>.Shared.Return(packed); }

            EntityRef row = refs[at];
            long from = RemotePlan.StartPositionMs(in load, UnixNowMs());
            Apply(Input.ShuffleOrdered(shuffle));
            if (load.Repeat >= 0) Apply(Input.Repeat((RepeatMode)load.Repeat));
            Apply(Input.PlayFrom(row, row.Id, contextId, Queue.CursorOf(at), PlayableKind.Audio,
                (int)Math.Min(from, int.MaxValue), now, cause, RemotePlan.StartsPaused(in load), explicitPosition: load.SeekToMs >= 0));
            s_pageContext = contextId;
            s_pageUrl = pageUrl;
            if (!sameContext) ForgetAutoplayPage();
            Apply(Input.ContextPages(contextId, pageUrl.Length > 0, now));   // after the Play: it names the new context
            Log.Info("playback", "context load rows=" + n + " start=" + at + " shuffle=" + (shuffle ? 1 : 0)
                + " cause=" + cause + " pages=" + (pageUrl.Length > 0 ? "more" : "one"));
        }
        finally
        {
            ArrayPool<EntityRef>.Shared.Return(refs);
            ArrayPool<QueueEdge>.Shared.Return(rows);
        }
    }

    /// <summary>One wire row → a queue row: its identity (allocating the catalog row), its uid kept exactly
    /// (<see cref="UidBook"/>, G-075) and its provenance. A row with no playable identity — a page marker, a delimiter —
    /// is skipped rather than queued as a hole.</summary>
    static void FillQueueIds(EntityId context, ReadOnlySpan<int> packed, Span<QueueEdge> rows)
    {
        Dictionary<int, ulong>? canonical = null;
        var scope = Entities.Current;
        if (scope is not null && context.Kind == EntityKind.Show && scope.Shows.TryGetSlot(context, out int show))
        {
            var targets = scope.Edges.ShowEpisodes.Targets(show);
            var payload = scope.Edges.ShowEpisodes.Payload(show);
            canonical = new Dictionary<int, ulong>();
            for (int k = 0; k < targets.Length && k < payload.Length; k++)
                if (!payload[k].ItemId.IsEmpty)
                    canonical[targets[k]] = s_uids.ItemIdOf(System.Text.Encoding.UTF8.GetBytes(Entities.Strings.Resolve(payload[k].ItemId)));
        }
        for (int k = 0; k < rows.Length; k++)
        {
            if (rows[k].ItemId != 0) continue;
            var row = Queue.Unpack(packed[k]);
            ulong item = row.Kind == EntityKind.Episode && canonical is not null && canonical.TryGetValue(row.Slot, out ulong held)
                ? held : Queue.MintItemIds(1);
            rows[k] = rows[k] with { ItemId = item };
        }
    }

    static void AddRow(ClusterBuffer buffer, in ClusterTrack track, QueueBucket bucket, EntityRef[] refs, QueueEdge[] rows, ref int n)
    {
        if (n >= refs.Length) return;
        ReadOnlySpan<byte> uri = buffer.Utf8(track.Uri);
        if (uri.IsEmpty) return;
        EntityId id = EntityId.Parse(uri);
        if (!id.IsPlayable) return;
        if (bucket == QueueBucket.NextUp && (Spotify.Library.IsBanned(id)
            || (!track.ArtistUri.IsEmpty && Spotify.Library.IsBanned(id.Text,
                [System.Text.Encoding.UTF8.GetString(buffer.Utf8(track.ArtistUri))])))) return;
        EntityRef row = Entities.Ref(id);
        if (row.IsNone) return;
        QueueProvider provider = bucket == QueueBucket.UserQueue ? QueueProvider.Queue : RemotePlan.ProviderOf(buffer.Utf8(track.Provider));
        refs[n] = row;
        rows[n] = new QueueEdge(s_uids.ItemIdOf(buffer.Utf8(track.Uid)), (byte)provider, (byte)bucket);
        n++;
    }

    // ── 4. the queue writes a Step asks for (G-074, G-080) ─────────────────────────────────────────────────────────

    /// <summary>The context's own order of the rows a shuffle moved, kept to put them back.</summary>
    static int[]? s_orderRefs;
    static QueueEdge[]? s_orderRows;

    /// <summary>A controller's <c>add_to_queue</c>. The queue's version bump re-arms the next row on the next drain.</summary>
    static void EnqueueRemote(EntityId id)
    {
        if (!id.IsPlayable || Entities.Current is null) return;
        EntityRef row = Entities.Ref(id);
        if (row.IsNone) return;
        Queue.Enqueue(row);
        RequestDrain();
    }

    /// <summary>Shuffle, or un-shuffle, the rows ahead of the cursor in the live queue.</summary>
    static void ReorderQueue(bool shuffle)
    {
        if (Entities.Current is null) return;
        int count = Queue.Count;
        if (count == 0) { s_orderRefs = null; s_orderRows = null; return; }
        int[] packed = ArrayPool<int>.Shared.Rent(count);
        QueueEdge[] rows = ArrayPool<QueueEdge>.Shared.Rent(count);
        try
        {
            Queue.PackedRefs.CopyTo(packed);
            Queue.Rows.CopyTo(rows);
            if (!Reorder(packed.AsSpan(0, count), rows.AsSpan(0, count), s_state.Cursor.Index, shuffle)) return;
            Queue.Replace(packed.AsSpan(0, count), rows.AsSpan(0, count));
            RequestDrain();
        }
        finally
        {
            ArrayPool<int>.Shared.Return(packed);
            ArrayPool<QueueEdge>.Shared.Return(rows);
        }
    }

    /// <summary>Reorder the NextUp run after <paramref name="cursorIndex"/> in place: shuffle it (remembering the
    /// context's order) or restore that order (<see cref="ShuffleOrder.Unshuffle"/>). The user queue is never moved.
    /// False when nothing changed.</summary>
    static bool Reorder(Span<int> packed, Span<QueueEdge> rows, int cursorIndex, bool shuffle)
    {
        int start = Math.Max(0, cursorIndex + 1);
        while (start < rows.Length && rows[start].Bucket != (byte)QueueBucket.NextUp) start++;
        int length = rows.Length - start;                 // NextUp is the last bucket (Queue.Rank)
        Span<int> runRefs = packed.Slice(start, length);
        Span<QueueEdge> runRows = rows.Slice(start, length);
        if (!shuffle)
        {
            if (s_orderRefs is null || s_orderRows is null) return false;
            ShuffleOrder.Unshuffle(s_orderRefs, s_orderRows, runRefs, runRows);
            s_orderRefs = null;
            s_orderRows = null;
            return true;
        }
        if (length < 2) return false;
        s_orderRefs = runRefs.ToArray();
        s_orderRows = runRows.ToArray();
        int[] order = ArrayPool<int>.Shared.Rent(length);
        try
        {
            for (int k = 0; k < length; k++) order[k] = k;
            ShuffleOrder.Permute(order.AsSpan(0, length), (ulong)System.Diagnostics.Stopwatch.GetTimestamp());
            for (int k = 0; k < length; k++)
            {
                runRefs[k] = s_orderRefs[order[k]];
                runRows[k] = s_orderRows[order[k]];
            }
        }
        finally { ArrayPool<int>.Shared.Return(order); }
        return true;
    }

    // ── 5a. context paging (G-242) ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>The next page of the context on the deck, as its last page named it; "" when it had none.</summary>
    static string s_pageUrl = "";
    static EntityId s_pageContext;

    /// <summary>The context is running out and its next page is known: GET it on an api thread (the url's spclient path,
    /// <see cref="RemotePlan.PageRoute"/>), append its rows as Context NextUp, keep ITS next url, and answer with
    /// <see cref="Input.Paged"/>. A newer context load while the page is out drops the answer.</summary>
    static void RequestPage(EntityId context)
    {
        long now = FrameNowMs();
        string path = context.Equals(s_pageContext) ? RemotePlan.PageRoute(s_pageUrl) : "";
        if (path.Length == 0 || Entities.Current is null) { Post(Input.Paged(context, 0, morePages: false, now)); return; }
        long seq = s_contextSeq;
        bool queued = Spotify.Api.Run(() =>
        {
            ClusterBuffer buffer = ClusterBuffer.Rent();
            FetchPage(path, buffer, "context page", out int start, out int count, out string next);
            ToUi(() =>
            {
                try
                {
                    int appended = seq == s_contextSeq ? AppendRows(context, buffer, start, count, QueueProvider.Context) : 0;
                    if (seq == s_contextSeq && context.Equals(s_pageContext)) s_pageUrl = appended > 0 ? next : "";
                    Log.Info("playback", "context page appended " + appended + " row(s), " + (next.Length > 0 ? "more follow" : "the last"));
                    Post(Input.Paged(context, appended, morePages: appended > 0 && next.Length > 0, FrameNowMs()));
                }
                finally { ClusterBuffer.Return(buffer); }
            });
        });
        if (!queued) Post(Input.Paged(context, 0, morePages: false, now));
    }

    /// <summary>GET one context-resolve page (a context's or an autoplay answer's next_page_url) on an api thread and
    /// decode it into <paramref name="buffer"/>: the rows' span and the page's own next url ("" when it is the last).
    /// A refused or failed page is logged as <paramref name="what"/> and answers no rows; the caller decides what that
    /// means.</summary>
    static void FetchPage(string path, ClusterBuffer buffer, string what, out int start, out int count, out string next)
    {
        start = 0;
        count = 0;
        next = "";
        try
        {
            var args = new Spotify.RequestArgs
            {
                Path = path,
                Host = Spotify.ApiHost.Spclient,
                Verb = Spotify.Verb.Get,
                Headers = Spotify.HeaderSet.Bearer | Spotify.HeaderSet.ClientToken | Spotify.HeaderSet.Identity
                          | Spotify.HeaderSet.AcceptLanguage | Spotify.HeaderSet.AcceptJson,
            };
            Spotify.Api.Result result = Spotify.Api.Send(Spotify.RequestKind.Custom, args, CancellationToken.None);
            if (result.Ok && result.Body.Length > 0)
            {
                Spotify.Decode.ContextPage page = Spotify.Decode.ContextResolve(result.Bytes, buffer);
                start = page.TrackStart;
                count = page.TrackCount;
                if (!page.NextPageUrl.IsEmpty) next = System.Text.Encoding.UTF8.GetString(buffer.Utf8(page.NextPageUrl));
            }
            else Log.Warn("playback", what + " refused (" + result.Status + ")");
        }
        catch (Exception ex) { Log.Warn("playback", what + " failed", ex); }
    }

    // ── 5b. the seed (bug I): a real current row must not sit next to an Unknown queue ────────────────────────────
    //
    // `Playback.Current` can go real with no `Queue.Replace` ever having run: a restored session (below), or a
    // mirrored remote cluster (`Playback.Host.Remote.cs`'s `SeedQueueFromCluster`) — the queue rail has no way to
    // tell "nobody has answered yet" from "this session genuinely has nothing queued" except `EdgeState.Unknown`
    // staying Unknown, and both callers landed here funnel through `Queue.DecideSeed` (the pure decision) and
    // `Queue.Replace` (the one write) — never a second path into the table.

    /// <summary>Separate from <see cref="s_contextSeq"/>: a background seed answers a different question than a real
    /// context load (Play) does, so the two are never compared against each other. The guard a landing seed checks is
    /// <c>Queue.State</c> — a real Play (or an earlier seed) already answering "what plays next" — not a race with
    /// this counter.</summary>
    static long s_seedSeq;

    // ── 5b-i. the boot-time seed's online wait ─────────────────────────────────────────────────────────────────────
    //
    // A restore's context resolve asked before the session adopts its catalog scope answers 401 (the bug: it used to
    // land the bare row on that, the same as a genuinely empty context, which then blocked `Queue.DecideSeed` from
    // ever re-seeding once the session came online). The wait uses the SAME idiom `Spotify.Library`'s `WatchSession`
    // and `Entities.Home.Host`'s `WatchSession` use for "do X once, on the next transition into Online": a private
    // `ReactiveRuntime` with one `Effect` tracking `Spotify.Status`, flushed through this file's own `ToUi` — the
    // signal can be written off the UI thread, same as every other seam here (C9).

    static ReactiveRuntime? s_seedRuntime;
    static Effect? s_seedOnlineWatch;
    static bool s_seedFlushPosted;
    static bool s_seedArmed;
    static EntityId s_seedArmedContext, s_seedArmedCurrent;
    static readonly Action s_seedFlush = static () => { s_seedFlushPosted = false; s_seedRuntime?.Flush(); };

    static void RequestSeedFlush()
    {
        if (s_seedFlushPosted) return;
        s_seedFlushPosted = true;
        ToUi(s_seedFlush);
    }

    /// <summary>Arm the seed's context resolve to run once more on the session's next transition into Online, and
    /// leave the queue <see cref="EdgeState.Unknown"/> (the skeleton) until then. UI thread; idempotent — a second arm
    /// before the first fires just replaces which (context, current row) pair the retry targets.</summary>
    static void ArmSeedRetry(EntityId context, EntityId currentId)
    {
        s_seedArmed = true;
        s_seedArmedContext = context;
        s_seedArmedCurrent = currentId;
        if (s_seedRuntime is null)
        {
            s_seedRuntime = new ReactiveRuntime { FrameRequested = RequestSeedFlush };
            s_seedOnlineWatch = new Effect(s_seedRuntime, WatchSeedOnline);
        }
    }

    /// <summary>Drop an outstanding arm without firing it — the cluster seed (<c>Playback.Host.Remote.cs</c>'s
    /// <c>SeedQueueFromCluster</c>) or a real Play answered "what plays next" before the session came online. Safe to
    /// call when nothing is armed.</summary>
    static void CancelArmedSeedRetry()
    {
        if (!s_seedArmed) return;
        s_seedArmed = false;
        DisposeSeedWatch();
    }

    static void DisposeSeedWatch()
    {
        s_seedOnlineWatch?.Dispose();
        s_seedOnlineWatch = null;
        s_seedRuntime = null;
        s_seedFlushPosted = false;
    }

    /// <summary>Tracked: re-runs whenever <see cref="Spotify.Status"/> moves. One-shot — the watch is torn down the
    /// moment it fires, armed or not, so a reconnect's later transitions never fire it twice.</summary>
    static void WatchSeedOnline()
    {
        Spotify.SessionPhase phase = Spotify.Status.Value;
        if (phase != Spotify.SessionPhase.Online) return;
        bool armed = s_seedArmed;
        s_seedArmed = false;
        DisposeSeedWatch();
        if (armed) FireSeedRetry();
    }

    /// <summary>The armed retry's turn: re-decide from scratch rather than blindly resuming the stale request — a real
    /// Play, or the cluster seed, may have already answered "what plays next" while this waited
    /// (<see cref="Queue.DecideSeed"/> refuses outright once <see cref="Queue.State"/> left <see cref="EdgeState.Unknown"/>),
    /// or the current row itself may have moved on. Mirrors <see cref="Restore"/>'s own shape exactly, including its
    /// bare-row fallback when the resolve cannot even be sent (the api queue is full again).</summary>
    static void FireSeedRetry()
    {
        if (Entities.Current is null || !s_state.CurrentId.Equals(s_seedArmedCurrent)) return;
        bool hasContext = !s_seedArmedContext.IsEmpty && !s_seedArmedContext.Equals(s_seedArmedCurrent);
        if (Queue.DecideSeed(Queue.State, hasCurrent: true, hasClusterTracks: false, hasContext) != Queue.SeedSource.Context)
        {
            Log.Info("playback", "queue seed retry skipped: the queue already answered what plays next");
            return;
        }
        EntityRef row = Entities.Ref(s_seedArmedCurrent);
        if (row.IsNone) return;
        Log.Info("playback", "queue seed retrying now that the session is online");
        if (!ResolveSeedContext(++s_seedSeq, s_seedArmedContext, s_seedArmedCurrent))
        {
            SeedCurrentRow(row);
            s_state.Cursor = Queue.CursorOf(0);
            Log.Info("playback", "queue seeded the bare row: the retry's own api queue was full");
            RequestDrain();
        }
    }

    /// <summary>The bare current row alone (<see cref="Queue.SeedSource.CurrentOnly"/>): one row, no history, no next
    /// up, item id 0 (nothing this session queued itself) — the shape both the restore and the cluster seed fall back
    /// to when there is nothing richer to build from.</summary>
    static void SeedCurrentRow(EntityRef row)
    {
        if (row.IsNone) return;
        Span<EntityRef> refs = [row];
        Span<QueueEdge> rows = [new QueueEdge(Queue.MintItemIds(1), (byte)QueueProvider.Context, (byte)QueueBucket.NowPlaying)];
        Queue.Replace(refs, rows);
    }

    /// <summary>Resolve <paramref name="context"/> on an api thread PURELY TO SEED the queue: the exact same
    /// <c>ContextResolve</c> call <see cref="ResolveContext"/> makes for a Play, landed by
    /// <see cref="LandSeedContext"/> instead of <see cref="StartContext"/> — no claim, no load, no registration, just
    /// rows. Answers whether it actually armed the resolve (false — the api queue was full — leaves the buffer
    /// returned and the decision to the caller, which still has its own bare-row fallback); NO SYNC-OVER-ASYNC, so a
    /// caller that gets true must wait for <see cref="LandSeedContext"/>, never block here.</summary>
    static bool ResolveSeedContext(long seq, EntityId context, EntityId currentId)
    {
        ClusterBuffer buffer = ClusterBuffer.Rent();
        string uri = context.Text;
        bool queued = Spotify.Api.Run(() =>
        {
            int start = 0, count = 0, status = 0;
            try
            {
                Spotify.Api.Result result = Spotify.Api.ContextResolve(uri, CancellationToken.None);
                status = result.Status;
                if (result.Ok && result.Body.Length > 0)
                {
                    Spotify.Decode.ContextPage page = Spotify.Decode.ContextResolve(result.Bytes, buffer);
                    start = page.TrackStart;
                    count = page.TrackCount;
                }
                else if (!result.Ok) Log.Warn("playback", "queue seed resolve refused (" + status + ")");
            }
            catch (Exception ex) { status = 0; Log.Warn("playback", "queue seed resolve failed", ex); }
            ToUi(() =>
            {
                try { LandSeedContext(seq, context, currentId, buffer, start, count, status); }
                finally { ClusterBuffer.Return(buffer); }
            });
        });
        if (!queued)
        {
            Log.Warn("playback", "queue seed resolve refused: the api queue is full");
            ClusterBuffer.Return(buffer);
        }
        return queued;
    }

    /// <summary>UI thread: the seed resolve answered (or was refused). Dropped when the queue already answered "what
    /// plays next" some other way while it was in flight (Queue.State is the guard, not <paramref name="seq"/> against
    /// a real Play — <see cref="Queue.DecideSeed"/> already refuses a non-Unknown queue) or the current row moved on.
    ///
    /// <para>A resolve that came back with NO tracks at all is not automatically "the context is empty": an
    /// authentication refusal (401/403 — the boot-time bug, asked before the session adopts its catalog scope) looks
    /// exactly like an empty context unless the status is checked (<see cref="Queue.SeedRetryOn"/>). Only that refusal
    /// arms a retry for the next Online transition and leaves the rail on its skeleton a little longer; every other
    /// empty answer lands the bare row rather than the skeleton forever, and sets the reducer's cursor directly
    /// (<see cref="Rebind"/>'s own pattern): nothing else relocates a <see cref="QueueCursor.None"/> once the queue
    /// changes under it.</para></summary>
    static void LandSeedContext(long seq, EntityId context, EntityId currentId, ClusterBuffer buffer, int start, int count, int status)
    {
        if (seq != s_seedSeq || Entities.Current is null || Queue.State != EdgeState.Unknown
            || !s_state.CurrentId.Equals(currentId))
            return;
        EntityRef current = Entities.Ref(currentId);
        if (current.IsNone) return;

        if (count == 0 && Queue.SeedRetryOn(status) == Queue.SeedRetryDecision.RetryOnline)
        {
            Log.Info("playback", "queue seed resolve refused (" + status + "): arming a retry for the next session online");
            ArmSeedRetry(context, currentId);
            return;
        }

        ReadOnlySpan<ClusterTrack> ctxTracks = buffer.Tracks(start, count);
        Span<byte> text = stackalloc byte[512];
        TextRef uriRef = buffer.AddText(text[..currentId.Format(text)]);
        int at = count > 0 ? RemotePlan.StartIndex(buffer, ctxTracks, default, uriRef, -1) : -1;
        int deck = 0;
        if (at < 0) SeedCurrentRow(current);
        else
        {
            int cap = RemotePlan.MaxRows;
            EntityRef[] refs = ArrayPool<EntityRef>.Shared.Rent(cap);
            QueueEdge[] rows = ArrayPool<QueueEdge>.Shared.Rent(cap);
            try
            {
                int n = 0;
                if (at > 0)
                    for (int k = Math.Max(0, at - RemotePlan.HistoryCap); k < at; k++)
                        AddRow(buffer, in ctxTracks[k], QueueBucket.History, refs, rows, ref n);
                deck = n;
                AddRow(buffer, in ctxTracks[at], QueueBucket.NowPlaying, refs, rows, ref n);
                if (n == deck) { SeedCurrentRow(current); deck = 0; }
                else
                {
                    for (int k = at + 1; k < ctxTracks.Length && n < cap; k++)
                        AddRow(buffer, in ctxTracks[k], QueueBucket.NextUp, refs, rows, ref n);
                    int[] packed = ArrayPool<int>.Shared.Rent(n);
                    try
                    {
                        Queue.Pack(refs.AsSpan(0, n), packed);
                        FillQueueIds(context, packed.AsSpan(0, n), rows.AsSpan(0, n));
                        Queue.Replace(packed.AsSpan(0, n), rows.AsSpan(0, n));
                    }
                    finally { ArrayPool<int>.Shared.Return(packed); }
                    s_uids.Retain(Queue.Rows);
                }
            }
            finally
            {
                ArrayPool<EntityRef>.Shared.Return(refs);
                ArrayPool<QueueEdge>.Shared.Return(rows);
            }
        }
        s_state.Cursor = Queue.CursorOf(deck);
        Log.Info("playback", "queue seeded from a local context resolve, deck=" + deck);
        RequestDrain();
    }

    // ── 6. the launch restore (G-078) ──────────────────────────────────────────────────────────────────────────────

    /// <summary>Put the persisted deck back, once at boot, after <c>Entities.Boot</c> (UI thread). Paused, parked, audio
    /// first and never claiming (<see cref="Input.Restore"/>): the first Resume loads it at the saved position. An empty
    /// queue is seeded (<see cref="Queue.DecideSeed"/>): a context distinct from the row itself is resolved in the
    /// background (armed, never awaited — <see cref="ResolveSeedContext"/> lands through <see cref="LandSeedContext"/>
    /// later, well after this call's own <c>Post</c> below has been folded, so its direct cursor write never races
    /// it) — UNLESS the session has not reached <c>SessionPhase.Online</c> yet (the catalog scope this resolve needs
    /// adopts there): asking now would just answer 401, so the resolve is deferred to the session's next Online
    /// transition instead (<see cref="ArmSeedRetry"/>), and the queue stays <see cref="EdgeState.Unknown"/> (the
    /// skeleton) rather than landing a bare row that then blocks the richer seed forever. Anything else falls back to
    /// the one row now, so Next and Previous have a cursor to walk immediately.
    /// <para>BUT FIRST THE DOCUMENT'S OWN ROWS: when the session document carried the queue (<see cref="RestorePoint.Rows"/>),
    /// they are laid back directly (<see cref="LayPersistedRows"/>) and no seed is decided at all — the queue leaves
    /// <see cref="EdgeState.Unknown"/> here, which is <see cref="Queue.DecideSeed"/>'s guard. That is what brings an
    /// autoplay queue, or one whose context ran out, back after a restart; the context seed alone could not.</para></summary>
    public static void Restore(in RestorePoint point)
    {
        if (point.IsEmpty || !point.Track.IsPlayable || Entities.Current is null) return;
        Rebind();                                          // a deck from a retired scope is carried before this one lands
        EntityRef row = Entities.Ref(point.Track);
        if (row.IsNone) return;
        QueueCursor cursor = Queue.Count == 0 && point.Rows is { Length: > 0 } persisted
            ? LayPersistedRows(persisted, point.Track, point.CursorIndex)
            : QueueCursor.None;
        if (cursor.IsNone)
            cursor = point.CursorIndex >= 0 && Queue.RefAt(point.CursorIndex) == row
                ? Queue.CursorOf(point.CursorIndex)
                : QueueCursor.None;
        if (cursor.IsNone && Queue.Count == 0)
        {
            bool hasContext = !point.Context.IsEmpty && !point.Context.Equals(point.Track);
            Queue.SeedSource source = Queue.DecideSeed(Queue.State, hasCurrent: true, hasClusterTracks: false, hasContext);
            bool seeding;
            if (source == Queue.SeedSource.Context && !Spotify.Current.IsOnline)
            {
                Log.Info("playback", "queue seed deferred: the session is not online yet, arming a retry");
                ArmSeedRetry(point.Context, point.Track);
                seeding = true;                            // stay Unknown (the skeleton), not CurrentOnly
            }
            else seeding = source == Queue.SeedSource.Context && ResolveSeedContext(++s_seedSeq, point.Context, point.Track);
            if (!seeding)
            {
                SeedCurrentRow(row);
                Entities.Publish();                        // boot is no posted drain: publish the seeded row now
                cursor = Queue.CursorOf(0);
            }
        }
        long now = FrameNowMs();
        Post(Input.ShuffleOrdered(point.Shuffle));
        Post(Input.Repeat(point.Repeat));
        Post(Input.Restore(row, point.Track, point.Context, cursor, point.PositionMs, point.DurationMs, now));
        // A restored context has no page held (the document keeps rows, not urls): saying so lets the refill rule ask
        // autoplay now instead of at the last row's endgame. Offline the ask defers to the session watch.
        s_pageContext = point.Context;
        s_pageUrl = "";
        ForgetAutoplayPage();
        Post(Input.ContextPages(point.Context, morePages: false, now));
    }

    /// <summary>How many rows around the deck the restore asks the catalog for at playback priority; the rest of the
    /// document's rows go at prefetch priority, so the panel's first screen has titles before the tail does.</summary>
    const int RestoreHotRows = 30;

    /// <summary>Lay the session document's queue rows back (G-078), in their persisted reading order, and answer the
    /// deck's cursor. Each row is re-slotted by identity (<c>Entities.Ref</c> allocates an unseen one); a row whose kind
    /// has no table is dropped. The deck is the now-playing row that names <paramref name="track"/>, else the persisted
    /// cursor when it names the track. NOTHING IS WRITTEN — <see cref="QueueCursor.None"/>, the caller's seed path —
    /// when no row is the deck or the rows are not in bucket order (a hand-edited document): the queue must stay
    /// <see cref="EdgeState.Unknown"/> for <see cref="Queue.DecideSeed"/> to take over. UI thread, at boot.</summary>
    static QueueCursor LayPersistedRows(RestoreRow[] persisted, EntityId track, int cursorHint)
    {
        int n = persisted.Length;
        EntityRef[] refs = ArrayPool<EntityRef>.Shared.Rent(n);
        QueueEdge[] edges = ArrayPool<QueueEdge>.Shared.Rent(n);
        try
        {
            int count = 0, deck = -1, hinted = -1;
            for (int i = 0; i < n; i++)
            {
                RestoreRow r = persisted[i];
                if (!r.Id.IsPlayable) continue;
                EntityRef row = Entities.Ref(r.Id);
                if (row.IsNone) continue;
                if (r.Id.Equals(track))
                {
                    if (deck < 0 && r.Edge.Bucket == (byte)QueueBucket.NowPlaying) deck = count;
                    if (i == cursorHint) hinted = count;
                }
                refs[count] = row;
                edges[count] = r.Edge;
                count++;
            }
            if (deck < 0) deck = hinted;
            if (count == 0 || deck < 0 || !Queue.IsOrdered(edges.AsSpan(0, count)))
            {
                Log.Info("playback", "the session document's queue rows were not laid: rows=" + count + " deck=" + deck);
                return QueueCursor.None;
            }
            Queue.Replace(refs.AsSpan(0, count), edges.AsSpan(0, count));
            s_uids.Retain(Queue.Rows);
            Entities.Publish();                            // boot is no posted drain: publish the laid rows now
            EnsureRestoredIdentities(refs.AsSpan(0, count), deck);
            Log.Info("playback", "queue restored from the session document rows=" + count + " deck=" + deck);
            return Queue.CursorOf(deck);
        }
        finally
        {
            ArrayPool<EntityRef>.Shared.Return(refs);
            ArrayPool<QueueEdge>.Shared.Return(edges);
        }
    }

    /// <summary>Ask the catalog for the laid rows' identities so the panel paints titles, not skeletons: the deck and
    /// <see cref="RestoreHotRows"/> around it at playback priority, the rest at prefetch. One batched <c>Ensure</c> per
    /// priority for tracks, and one more per priority for episodes (D4: a restored episode row used to be asked one
    /// at a time — rare in a queue, but still a per-row POST).</summary>
    static void EnsureRestoredIdentities(ReadOnlySpan<EntityRef> rows, int deck)
    {
        int hotFirst = Math.Max(0, deck - RestoreHotRows / 6);
        int hotEnd = Math.Min(rows.Length, hotFirst + RestoreHotRows);
        Track[] tracks = ArrayPool<Track>.Shared.Rent(rows.Length);
        Episode[] episodes = ArrayPool<Episode>.Shared.Rent(rows.Length);
        try
        {
            for (int pass = 0; pass < 2; pass++)
            {
                bool hot = pass == 0;
                FetchPriority priority = hot ? FetchPriority.Playback : FetchPriority.Prefetch;
                int n = 0, m = 0;
                for (int i = 0; i < rows.Length; i++)
                {
                    if ((i >= hotFirst && i < hotEnd) != hot) continue;
                    EntityRef r = rows[i];
                    if (r.Kind == EntityKind.Track) tracks[n++] = new Track(r.Slot);
                    else if (r.Kind == EntityKind.Episode) episodes[m++] = new Episode(r.Slot);
                }
                if (n > 0) Entities.Ensure(tracks.AsSpan(0, n), TrackFields.Identity, priority);
                if (m > 0) Entities.Ensure(episodes.AsSpan(0, m), EpisodeFields.Identity, priority);
            }
        }
        finally
        {
            ArrayPool<Track>.Shared.Return(tracks);
            ArrayPool<Episode>.Shared.Return(episodes);
        }
    }

    // ── 7. the play report: registration, resume points, the play log (G-076, G-079) ───────────────────────────────

    static Spotify.Telemetry.Registration s_registration;
    static EntityId s_registeredId;

    /// <summary>Run one Step's report, in order: a seek, a pause and a resume belong to the registration that is open;
    /// an end closes it; a start opens the next. Only Spotify rows are registered; every row reaches the play log.
    /// Telemetry never fails playback.</summary>
    static void RunPlayReport(in PlayReport r)
    {
        try { Report(in r); }
        catch (Exception ex) { Log.Warn("playback", "play report failed", ex); }
    }

    static void Report(in PlayReport r)
    {
        long unix = UnixNowMs();
        bool open = s_registration.Open;
        bool episode = IsSpotifyEpisode(s_registeredId);

        if ((r.Events & PlayEvents.Seeked) != 0 && open) Spotify.Telemetry.Seeked(ref s_registration, r.SeekFromMs, r.SeekToMs);
        if ((r.Events & PlayEvents.Paused) != 0)
        {
            ArmProgressMirror(false);
            if (open) Spotify.Telemetry.Paused(ref s_registration, r.PausePosMs);
            if (episode) LeaveEpisode(s_registeredId, r.PausePosMs, unix, "pause");
        }
        if ((r.Events & PlayEvents.Resumed) != 0 && open)
        {
            Spotify.Telemetry.Resumed(ref s_registration, r.ResumePosMs);
            if (episode) ArmProgressMirror(true);
        }
        if ((r.Events & PlayEvents.Ended) != 0)
        {
            ArmProgressMirror(false);
            if (open) Spotify.Telemetry.Ended(ref s_registration, r.EndPosMs, ReasonText(r.EndReason));
            if (IsSpotifyEpisode(r.EndedId)) LeaveEpisode(r.EndedId, r.EndPosMs, unix, "end");
            s_registration = default;
            s_registeredId = default;
        }
        if ((r.Events & PlayEvents.Started) == 0) return;

        EntityId id = r.StartedId;
        PlayStarted?.Invoke(id, r.Context, unix);
        if (id.Provider != EntityProvider.Spotify || !id.IsPlayable) return;
        string uri = id.Text;
        Audio.Opened opened = Audio.PlayingOpened;
        s_registration = Spotify.Telemetry.Started(NewPlaybackIds(), uri, r.Context.IsEmpty ? "" : r.Context.Text,
            ProviderText(), ReasonText(r.StartReason), null, null, opened.BitrateKbps, opened.Label,
            r.DurationMs > 0 ? r.DurationMs : opened.DurationMs, r.StartPosMs);
        s_registeredId = id;
        if (s_state.ContentRate != 1f) Spotify.Telemetry.RateChanged(ref s_registration, r.StartPosMs, s_state.ContentRate);
        if (id.Kind == EntityKind.Episode)
        {
            ArmProgressMirror(true);
            // Where the episode's audio really began — its resume point, when the load named none (EmitLoad resolved it).
            if (r.StartPosMs > 0)
                Log.Info("podcast", "podcast.progress.resume fromMs=" + r.StartPosMs + " durationMs=" + r.DurationMs);
        }
        if (!Platform.Settings.Get(Platform.Keys.PrivateSession)) Spotify.Telemetry.PlayHistory(uri, unix);
    }

    static bool IsSpotifyEpisode(EntityId id) => id.Kind == EntityKind.Episode && id.Provider == EntityProvider.Spotify;

    /// <summary>The listener left an episode (a pause, an end, a skip): tell herodotus where, and mirror the SAME position
    /// at the SAME instant into the row at Local (podcast plan §5.8) — the row repaints now, the next launch reads it
    /// from disk, and a later hydrate recognises this device's own revision by that instant and keeps the row.</summary>
    static void LeaveEpisode(EntityId id, int positionMs, long unixMs, string why)
    {
        Spotify.Telemetry.ResumePoint(id.Text, positionMs, unixMs);
        Entities.MirrorEpisodeProgress(id, positionMs, unixMs);
        Log.Info("podcast", "podcast.progress.mirror why=" + why + " positionMs=" + positionMs);
    }

    // ── the local progress mirror's tick (P10, named: EpisodeProgress.MirrorIntervalMs) ──
    //
    // Herodotus hears this device on pause and end only, so without a tick a row would show the position the episode
    // STARTED at until the listener stopped. Armed by a Spotify episode's Started/Resumed report, disarmed by any
    // Paused/Ended one; the tick itself re-checks on the UI thread and disarms when the deck is no longer that episode,
    // playing. One Timer for the process, a cached tick delegate: nothing allocates per tick but the mirror's own
    // pooled staging and its write-behind job.

    static Timer? s_progressMirror;
    static readonly Action s_progressMirrorTick = ProgressMirrorTick;

    static void ArmProgressMirror(bool on)
    {
        if (!on)
        {
            s_progressMirror?.Change(Timeout.Infinite, Timeout.Infinite);
            return;
        }
        s_progressMirror ??= new Timer(static _ => ToUi(s_progressMirrorTick), null, Timeout.Infinite, Timeout.Infinite);
        s_progressMirror.Change(EpisodeProgress.MirrorIntervalMs, EpisodeProgress.MirrorIntervalMs);
    }

    /// <summary>UI THREAD (marshalled by <see cref="ToUi"/>): mirror the playing episode's position at Local.</summary>
    static void ProgressMirrorTick()
    {
        if (!s_registration.Open || !IsSpotifyEpisode(s_registeredId) || s_state.Phase != Phase.Playing
            || !s_state.CurrentId.Equals(s_registeredId))
        {
            ArmProgressMirror(false);
            return;
        }
        Entities.MirrorEpisodeProgress(s_registeredId, s_state.Position(FrameNowMs()), UnixNowMs());
    }

    /// <summary>The provenance word of the queue row the deck is on.</summary>
    static string ProviderText()
    {
        if (s_state.Cursor.IsNone || Entities.Current is null) return "context";
        ReadOnlySpan<QueueEdge> rows = Queue.Rows;
        int index = s_state.Cursor.Index;
        if ((uint)index >= (uint)rows.Length) return "context";
        return (QueueProvider)rows[index].Provider switch
        {
            QueueProvider.Queue => "queue",
            QueueProvider.Autoplay => "autoplay",
            _ => "context",
        };
    }

    /// <summary>The four ids a registration is keyed by, minted as the row's audio begins. The playback id is the one the
    /// connect-state PUT names the row by (<see cref="WireStarted"/>, G-240), as 0.2.9's was.</summary>
    static Spotify.Telemetry.PlaybackIds NewPlaybackIds()
    {
        byte[] playback = PlaybackIdBytes();
        byte[] stream = RandomNumberGenerator.GetBytes(16);
        return new Spotify.Telemetry.PlaybackIds(playback, stream, Convert.ToHexStringLower(playback),
            Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString(), Guid.NewGuid().ToString("N"));
    }


    // ── 8. test seam ───────────────────────────────────────────────────────────────────────────────────────────────

    static void ResetContextForTests()
    {
        if (s_resolvedBuffer is { } resolved) ClusterBuffer.Return(resolved);
        s_resolvedBuffer = null;
        s_resolvedLoad = default;
        s_resolvedSeq = 0;
        s_resolvedPageUrl = "";
        s_contextSeq = 0;
        s_seedSeq = 0;
        CancelArmedSeedRetry();
        s_seedArmedContext = default;
        s_seedArmedCurrent = default;
        s_orderRefs = null;
        s_orderRows = null;
        s_registration = default;
        s_registeredId = default;
        ArmProgressMirror(false);                        // a fact's episode must not tick into the next fact's scope
        s_pageUrl = "";
        s_pageContext = default;
        ForgetAutoplayPage();
    }
}
