// ── Playback/Playback.Transitions.cs ─────────────────────────────────────────────────────────────────────────────────
// The reducer's transition arms: the gapless hand-off and the next-row arm, the device reload, the video host switch
// and its recovery, the launch restore, remote volume and queue writes, autoplay, the play report — plus the pure
// planners the host feeds them (the inbound load, the queue uid, the shuffle order) and the pure host gates
// (MediaSwitch, SeekGate, GaplessJoinClock, DeviceRecoveryPlan), moved here from Playback.cs §4
//
// Role: CORE
// Owner: G
// Wave: gap batch B3 (+ B3c: the queue follow gate, the repeat-context wrap row, the kept user queue, the radio station,
//       the Connect intake order, a controller's queue splice, the uid book; + R4-1: context paging, the queue forward to
//       a foreign owner, the superseding inbound load, update_context's reshuffle, the queue announce)
// Budget: 1,000 lines
// Spec: gap register G-070/071/073/074/075/076/078/079/080/100/108/112/140/141/142/143, G-242/244/245/246/248;
//       decisions D4 and D13; WP-5.Q's asks (follow after a drain, `Queue.WrapIndex`, radio through the one context path,
//       0.2.9's keep-the-queue)
//
// A NAMED PARTIAL OF `Playback.cs`, declared when the reducer passed 30 % over its 1,790-line budget. The same CORE
// contract as the parent file, and nothing looser: no clock (every input carries its frame stamp), no I/O, no
// allocation after warm-up, no LINQ and no closures, UI thread only (C1). Every arm here mutates `State` in place and
// fills effect SLOTS; `Playback.Host.cs` executes them. The only table reads are the ones `Queue` already makes plus
// a row's flag word (`KindOfRow`) and an episode row's progress (`EpisodeStartOf`), all UI-thread column loads.
//
// THE TRACK BOUNDARY, AS ONE PICTURE (D4 — the pump's butt-join is kept; the reducer follows it):
//
//   load A ──▶ Prefetch(next)            warm only: head, mirrors, key — no ring, no decoder
//   pump: EndingSoon(A) ──▶ PrepareNext(B)  the full open, inside fade + 8 s
//   queue/shuffle/repeat changes the next row ──▶ PrepareNext(B′) (the pump replaces the slot) or CancelPrepared
//   pump: HandedOff(A, B) ──▶ cursor → B, epoch n → n+1, NO Load, Adopt(n → n+1)   the voice is already live
//   pump: Ended(A) with nothing prepared ──▶ Advance: the HARD-CUT fallback, a Load in the same drain
//
// A hand-off naming a row the queue no longer puts next is the wrong track in the listener's ear: the reducer hard-cuts
// to the truth instead of labelling the wrong one.

using FluentGpu.Foundation;

using RemoteCmd = Wavee.Spotify.Decode.RemoteCmd;
using RepeatMode = Wavee.Spotify.Decode.RepeatMode;

namespace Wavee;

public static partial class Playback
{
    // ── 1. the play report (G-076 registration, G-079 play log) ────────────────────────────────────────────────────

    /// <summary>What a Step reports to play registration. Flags, because a hand-off both ENDS one registration and
    /// STARTS the next in one Step; the host consumes them in declaration order (a seek and a pause belong to the
    /// registration that is closing, the start to the one opening).</summary>
    [Flags]
    public enum PlayEvents : byte { None = 0, Seeked = 1, Paused = 2, Resumed = 4, Ended = 8, Started = 16 }

    /// <summary>Why a registration opened or closed — gabo's <c>reason_start</c> / <c>reason_end</c> words
    /// (<see cref="ReasonText"/>).</summary>
    public enum PlayReason : byte { None, ClickRow, TrackDone, ForwardButton, BackButton, PlayButton, Remote, EndPlay, TrackError, Logout }

    /// <summary>The report slot. Unlike every other slot it is a SEQUENCE, not a latest-wins value — a registration
    /// that ended must be closed before the next one opens — so the host drains it after EVERY Step, not once per
    /// drain, and <see cref="Effects.Any"/> deliberately does not read it.</summary>
    public struct PlayReport
    {
        public PlayEvents Events;
        /// <summary>The row whose registration closed, and the one that opened.</summary>
        public EntityId EndedId, StartedId;
        /// <summary>The context the opening registration plays from.</summary>
        public EntityId Context;
        public PlayReason EndReason, StartReason;
        public int EndPosMs, StartPosMs, SeekFromMs, SeekToMs, PausePosMs, ResumePosMs;
        /// <summary>The opening row's duration as the deck knows it (0 = not yet stated).</summary>
        public int DurationMs;
    }

    /// <summary>The wire word for a reason (gabo's vocabulary, 0.2.9's <c>RawCoreStreamProjection</c>).</summary>
    public static string ReasonText(PlayReason reason) => reason switch
    {
        PlayReason.ClickRow => "clickrow",
        PlayReason.TrackDone => "trackdone",
        PlayReason.ForwardButton => "fwdbtn",
        PlayReason.BackButton => "backbtn",
        PlayReason.PlayButton => "playbtn",
        PlayReason.Remote => "remote",
        PlayReason.EndPlay => "endplay",
        PlayReason.TrackError => "trackerror",
        PlayReason.Logout => "logout",
        _ => "",
    };

    /// <summary>Where autoplay stands for the current context (G-080). <see cref="Requested"/>: asked, the context has
    /// not run out yet; <see cref="Waiting"/>: the context ran out and the deck waits for the answer;
    /// <see cref="Exhausted"/>: the host declined or found nothing, so the next run-out simply ends;
    /// <see cref="Deferred"/>: the host could not ask (offline, or the session refused), so the next
    /// <see cref="InputKind.SessionOnline"/> asks again.</summary>
    public enum AutoplayPhase : byte { None, Requested, Waiting, Exhausted, Deferred }

    // ── 2. the launch restore point (G-078) ────────────────────────────────────────────────────────────────────────

    /// <summary>One persisted queue row: its identity and its edge (item id, provider, bucket), never a slot.</summary>
    public readonly record struct RestoreRow(EntityId Id, QueueEdge Edge);

    /// <summary>What the shell persists to put the deck back at launch: identities and numbers, never a slot (slots do
    /// not survive a process). Restored PAUSED, AUDIO-FIRST and WITHOUT CLAIMING (<see cref="Input.Restore"/>).
    /// <para><paramref name="Rows"/> is the queue itself, in reading order (history · now-playing · user queue · next
    /// up), when the document carried it; <c>null</c> for a point the reducer's state produced (<see cref="Of"/>) or a
    /// document written before rows were persisted — the HOST attaches rows, and <see cref="CursorIndex"/> indexes
    /// THEM when they are present. The array compares by reference, so a persisted point is compared through the
    /// document's shape (<c>Shell.SessionDeck.Same</c>), never through record equality.</para></summary>
    public readonly record struct RestorePoint(
        EntityId Track, EntityId Context, int CursorIndex, int PositionMs, int DurationMs, bool Shuffle, RepeatMode Repeat,
        RestoreRow[]? Rows = null)
    {
        public bool IsEmpty => Track.IsEmpty;
        /// <summary>The document carried the queue's rows, not just the deck.</summary>
        public bool HasRows => Rows is { Length: > 0 };

        /// <summary>The point a state persists. Empty unless the deck is OURS to restore: a foreign device's mirrored
        /// row (or the one it left behind) is not this device's session and must not come back as one.</summary>
        public static RestorePoint Of(in State s, long nowMs)
            => !s.HasCurrent || !s.CurrentId.IsPlayable || !Ownership.ShowsLocalNowPlaying(in s.Own, hasLocalSession: true)
                ? default
                : new(s.CurrentId, s.Context, s.Cursor.Index, s.Position(nowMs), s.DurationMs, s.Shuffle, s.Repeat);
    }

    // ── 3. the inbound load planner (G-071 host half, G-070) ───────────────────────────────────────────────────────

    /// <summary>The pure decisions behind turning a controller's <c>play</c> / <c>transfer</c> (or a local "play this
    /// context") into a queue and a start: which row, from where, paused or not. The host owns the table writes; this
    /// owns the rules, so each is one assertion.</summary>
    public static class RemotePlan
    {
        /// <summary>How many already-played context rows the queue keeps before the start row (Previous has somewhere
        /// to go; a 10,000-row playlist started at row 9,000 does not become 9,000 history rows).</summary>
        public const int HistoryCap = 50;

        /// <summary>The most rows one context load lands in the queue. The rest is a resolve's next page, which is the
        /// context queue's job (owner Q), not this planner's.</summary>
        public const int MaxRows = 500;

        /// <summary>The row an inbound load starts at: the uid, then the uri, then the index — 0.2.9's order. -1 when
        /// none of the three names a row in <paramref name="tracks"/>.</summary>
        public static int StartIndex(Spotify.Decode.ClusterBuffer text, ReadOnlySpan<Spotify.Decode.ClusterTrack> tracks,
            TextRef uid, TextRef uri, int index)
        {
            if (!uid.IsEmpty)
            {
                ReadOnlySpan<byte> want = text.Utf8(uid);
                for (int k = 0; k < tracks.Length; k++)
                    if (!tracks[k].Uid.IsEmpty && text.Utf8(tracks[k].Uid).SequenceEqual(want)) return k;
            }
            if (!uri.IsEmpty)
            {
                ReadOnlySpan<byte> want = text.Utf8(uri);
                for (int k = 0; k < tracks.Length; k++)
                    if (text.Utf8(tracks[k].Uri).SequenceEqual(want)) return k;
            }
            return (uint)index < (uint)tracks.Length ? index : -1;
        }

        /// <summary>Is the load asked to start paused? A play says so itself (<c>initially_paused</c>); a transfer
        /// restores the remote's own pause unless <c>restore_paused: "kill"</c> forces play.</summary>
        public static bool StartsPaused(in Spotify.Decode.RemoteLoad load)
            => load.Kind == RemoteCmd.Transfer ? load.HasPlayback && load.Paused && !load.ForcePlay : load.InitiallyPaused;

        /// <summary>Where the load starts, in ms. A transfer resumes the remote's position, EXTRAPOLATED by the time
        /// since its stamp when it was playing and asked for it (<c>restore_position: "extrapolate"</c>) — without that
        /// a transfer replays the seconds the handshake took. A play starts at its <c>seek_to</c>, else 0.</summary>
        public static long StartPositionMs(in Spotify.Decode.RemoteLoad load, long unixNowMs)
        {
            if (load.Kind != RemoteCmd.Transfer) return Math.Max(0, load.SeekToMs);
            if (!load.HasPlayback) return 0;
            long at = Math.Max(0, load.PositionAsOfMs);
            if (load.Extrapolate && !StartsPaused(in load) && load.TimestampMs > 0 && unixNowMs > load.TimestampMs)
                at += (long)((unixNowMs - load.TimestampMs) * (load.Speed > 0 ? load.Speed : 1.0));
            return at;
        }

        /// <summary>The wire's provider word → the queue's provenance. Missing or unknown reads as the context.</summary>
        public static QueueProvider ProviderOf(ReadOnlySpan<byte> provider)
            => provider.SequenceEqual("queue"u8) ? QueueProvider.Queue
             : provider.SequenceEqual("autoplay"u8) ? QueueProvider.Autoplay
             : QueueProvider.Context;

        /// <summary>Does a context uri name something autoplay can continue? Albums, playlists, artists, shows and the
        /// collection do; an infinite context (a station, a radio, autoplay itself) pages instead. A bare track or
        /// episode "context" — a card's single-row play — continues too: Spotify's own clients keep going with the
        /// seed's track/episode station (<see cref="StationUri"/> shows the shape; the request itself sends the bare
        /// uri as <c>context_uri</c> and the server resolves the station server-side, G-080).</summary>
        public static bool AutoplayContinues(ReadOnlySpan<byte> contextUri)
            => contextUri.Length > 0
               && contextUri.IndexOf(":station:"u8) < 0 && contextUri.IndexOf(":radio:"u8) < 0
               && contextUri.IndexOf(":autoplay"u8) < 0
               && (contextUri.IndexOf(":album:"u8) >= 0 || contextUri.IndexOf(":playlist:"u8) >= 0
                   || contextUri.IndexOf(":artist:"u8) >= 0 || contextUri.IndexOf(":show:"u8) >= 0
                   || contextUri.IndexOf(":collection"u8) >= 0 || contextUri.IndexOf(":track:"u8) >= 0
                   || contextUri.IndexOf(":episode:"u8) >= 0);

        /// <summary>The station a "Start radio" seed plays — song radio <c>spotify:station:track:&lt;id&gt;</c>, artist
        /// radio <c>spotify:station:artist:&lt;id&gt;</c> — resolved like any other context. "" for a seed with no Spotify
        /// station (an episode, a local file, a module row, a text-form id). Cold: one string per user action.</summary>
        public static string StationUri(EntityId seed)
            => seed.Provider == EntityProvider.Spotify && seed.Form == EntityForm.Gid && !seed.IsPrerelease
               && (seed.Kind is EntityKind.Track or EntityKind.Artist)
                ? "spotify:station:" + seed.Text.Substring("spotify:".Length)
                : "";

        /// <summary>The seed uri "Start radio" resolves through <c>inspiredby-mix</c> (G-251): the literal
        /// <c>spotify:track:&lt;id&gt;</c> (song radio) or <c>spotify:artist:&lt;id&gt;</c> (artist radio). "" for a seed
        /// with no Spotify radio (an episode, a local file, a module row, a text-form id) — the same seeds
        /// <see cref="StationUri"/> refuses. Cold: one string per user action.</summary>
        public static string RadioSeedUri(EntityId seed)
            => seed.Provider == EntityProvider.Spotify && seed.Form == EntityForm.Gid && !seed.IsPrerelease
               && (seed.Kind is EntityKind.Track or EntityKind.Artist)
                ? seed.Text
                : "";

        /// <summary>Does a resolved radio PARK behind the deck (0.2.9 <c>StartRadioAsync</c>: the current track keeps
        /// playing, the radio becomes what follows it and the context it plays from) or PLAY at once from its first row?
        /// It parks only while THIS device has a row on the deck that is live in a host — playing, paused or loading, and
        /// not parked after an end or a restore — and <paramref name="deckIsLiveRow"/> says the queue still holds that
        /// row (there is nothing to lay the radio behind otherwise). A foreign owner, an idle deck and an ended queue all
        /// play the radio through the one context path instead. PURE.</summary>
        public static bool RadioParks(in State s, bool deckIsLiveRow)
            => s.RoutesLocal && s.HasCurrent && !s.Parked && deckIsLiveRow
               && s.Phase is Phase.Playing or Phase.Paused or Phase.Loading;

        /// <summary>Lay a radio BEHIND the deck (G-251): the live rows up to and including <paramref name="deck"/> are kept
        /// (history re-bucketed, the deck row NowPlaying — the reducer's cursor still names the row it is on, so nothing
        /// reloads), then the user's still-waiting <see cref="KeptQueue"/> rows (0.2.9's user queue drained before its
        /// context), then the radio's rows as Context NextUp — EXCEPT a radio row that is the deck's own recording: song
        /// radio leads with its seed, and the seed is what is playing, so it would play twice in a row. Rows past the
        /// output are dropped and an unrepresentable target (0) is skipped. Answers the rows written, with
        /// <paramref name="dropped"/> the radio rows left out for being the deck; −1, with nothing written, when the deck
        /// is not a live row or does not fit. PURE.</summary>
        public static int RadioAfterCurrent(ReadOnlySpan<int> livePacked, ReadOnlySpan<QueueEdge> liveRows, int deck,
            ReadOnlySpan<int> keptPacked, ReadOnlySpan<QueueEdge> keptRows,
            ReadOnlySpan<int> radioPacked, ReadOnlySpan<QueueEdge> radioRows,
            Span<int> packedOut, Span<QueueEdge> rowsOut, out int dropped)
        {
            dropped = 0;
            int cap = Math.Min(packedOut.Length, rowsOut.Length);
            if ((uint)deck >= (uint)liveRows.Length || deck >= livePacked.Length || deck >= cap || livePacked[deck] <= 0) return -1;
            int n = 0;
            for (int k = 0; k < deck; k++)
                Put(livePacked[k], liveRows[k] with { Bucket = (byte)QueueBucket.History }, packedOut, rowsOut, ref n);
            int current = livePacked[deck];
            Put(current, liveRows[deck] with { Bucket = (byte)QueueBucket.NowPlaying }, packedOut, rowsOut, ref n);
            for (int k = 0; k < keptPacked.Length && k < keptRows.Length && n < cap; k++)
                Put(keptPacked[k], keptRows[k] with { Bucket = (byte)QueueBucket.UserQueue }, packedOut, rowsOut, ref n);
            for (int k = 0; k < radioPacked.Length && k < radioRows.Length && n < cap; k++)
            {
                if (radioPacked[k] == current) { dropped++; continue; }
                Put(radioPacked[k], radioRows[k] with { Bucket = (byte)QueueBucket.NextUp }, packedOut, rowsOut, ref n);
            }
            return n;
        }

        /// <summary>The user's queued rows a NEW CONTEXT keeps (0.2.9 <c>PlaybackSession.SetContext</c>, whose
        /// <c>keepUserQueue</c> is true for every local and inbound play): each row still WAITING in the user queue — after
        /// the deck, "Next in queue" by provenance (<see cref="Queue.SectionOf"/>) — in its order, with its item id and
        /// provider, re-bucketed UserQueue. They land straight after the new deck row and ahead of the new context's
        /// continuation, because 0.2.9's user queue drained before its context did. A queued row already played (history,
        /// or the deck row itself), a context row and an autoplay row do not survive. A TRANSFER keeps none: it brings the
        /// remote's own queue (0.2.9 <c>SetTransferredContext(clearUserQueue: true)</c>), so the host never asks.
        /// <para>The deck is read off the queue's OWN buckets (<see cref="Queue.Divider"/> without a cursor): the host
        /// follows the queue to the reducer's cursor at the end of every drain and writes a consistent run with every load,
        /// so the NowPlaying row is the deck even while a play the host just posted has not been folded yet. Returns the
        /// rows written, capped by the shorter output.</para></summary>
        public static int KeptQueue(ReadOnlySpan<int> packed, ReadOnlySpan<QueueEdge> rows, Span<int> packedOut, Span<QueueEdge> rowsOut)
        {
            int divider = Queue.Divider(rows, -1);
            int cap = Math.Min(packedOut.Length, rowsOut.Length), n = 0;
            for (int i = divider + 1; i < rows.Length && i < packed.Length && n < cap; i++)
            {
                if (packed[i] <= 0 || !Queue.IsUpcoming(rows, divider, i) || Queue.SectionOf(in rows[i]) != QueueSection.Queue) continue;
                packedOut[n] = packed[i];
                rowsOut[n] = rows[i] with { Bucket = (byte)QueueBucket.UserQueue };
                n++;
            }
            return n;
        }

        /// <summary>A play that lands the context that is ALREADY playing: the same id, not a radio parked behind the deck
        /// (that path never comes here) and not an empty id. The autoplay tail survives such a play; any other drops it.</summary>
        public static bool SameContext(EntityId landing, EntityId current)
            => !landing.IsEmpty && landing.Equals(current);

        /// <summary>The autoplay rows a play KEEPS: when <paramref name="sameContext"/>, every still-upcoming row the
        /// Autoplay provider appended, in its order, re-bucketed NextUp — they land after the new context run, where the
        /// reducer's Refill finds them and does not ask for the same context's autoplay again (seven asks in 15 s, each
        /// after a track change, 2026-09-17). A different context, a transfer or a radio keeps none. Returns the rows
        /// written, capped by the shorter output. PURE.</summary>
        public static int KeptAutoplay(ReadOnlySpan<int> packed, ReadOnlySpan<QueueEdge> rows, bool sameContext, Span<int> packedOut, Span<QueueEdge> rowsOut)
        {
            if (!sameContext) return 0;
            int divider = Queue.Divider(rows, -1);
            int cap = Math.Min(packedOut.Length, rowsOut.Length), n = 0;
            for (int i = divider + 1; i < rows.Length && i < packed.Length && n < cap; i++)
            {
                if (packed[i] <= 0 || !Queue.IsUpcoming(rows, divider, i) || Queue.SectionOf(in rows[i]) != QueueSection.Autoplay) continue;
                packedOut[n] = packed[i];
                rowsOut[n] = rows[i] with { Bucket = (byte)QueueBucket.NextUp };
                n++;
            }
            return n;
        }

        /// <summary>Lay a context the caller already HOLDS (a page's visible order, one track) out as the queue, in reading
        /// order: up to <see cref="HistoryCap"/> rows before <paramref name="start"/> as history, the start row on the deck,
        /// the <see cref="KeptQueue"/> rows, the rest of the context as next up, then the <see cref="KeptAutoplay"/> tail —
        /// at most <see cref="MaxRows"/> and the outputs' length. An unrepresentable packed target (0) is skipped. Answers
        /// the rows written and the deck row's index in <paramref name="deck"/>; −1, with nothing written, when the start
        /// row itself is not representable.</summary>
        public static int Layout(ReadOnlySpan<int> context, int start, ReadOnlySpan<int> keptPacked, ReadOnlySpan<QueueEdge> keptRows,
            ReadOnlySpan<int> tailPacked, ReadOnlySpan<QueueEdge> tailRows, Span<int> packedOut, Span<QueueEdge> rowsOut, out int deck)
        {
            deck = -1;
            int cap = Math.Min(Math.Min(packedOut.Length, rowsOut.Length), MaxRows);
            if ((uint)start >= (uint)context.Length || context[start] <= 0 || cap == 0) return 0;
            int n = 0;
            for (int k = Math.Max(0, start - HistoryCap); k < start && n < cap - 1; k++)
                Put(context[k], ContextRow(QueueBucket.History), packedOut, rowsOut, ref n);
            deck = n;
            Put(context[start], ContextRow(QueueBucket.NowPlaying), packedOut, rowsOut, ref n);
            for (int k = 0; k < keptPacked.Length && k < keptRows.Length && n < cap; k++)
                Put(keptPacked[k], keptRows[k], packedOut, rowsOut, ref n);
            for (int k = start + 1; k < context.Length && n < cap; k++)
                Put(context[k], ContextRow(QueueBucket.NextUp), packedOut, rowsOut, ref n);
            for (int k = 0; k < tailPacked.Length && k < tailRows.Length && n < cap; k++)
                Put(tailPacked[k], tailRows[k], packedOut, rowsOut, ref n);
            return n;
        }

        /// <summary>A context page's <c>next_page_url</c> → the spclient path that answers it (G-242), the way librespot's
        /// <c>get_next_page</c> reads it: an <c>hm://</c> ident is the same path on spclient, an <c>https://</c> url loses its
        /// host, a bare path is kept. "" for no url. Cold: one string per page.</summary>
        public static string PageRoute(string url)
        {
            if (string.IsNullOrEmpty(url)) return "";
            if (url.StartsWith("hm://", StringComparison.Ordinal)) return "/" + url["hm://".Length..].TrimStart('/');
            if (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                int slash = url.IndexOf('/', "https://".Length);
                return slash < 0 ? "" : url[slash..];
            }
            return url[0] == '/' ? url : "/" + url;
        }

        static QueueEdge ContextRow(QueueBucket bucket) => new(0, (byte)QueueProvider.Context, (byte)bucket);

        static void Put(int target, QueueEdge row, Span<int> packed, Span<QueueEdge> rows, ref int n)
        {
            if (target <= 0) return;
            packed[n] = target;
            rows[n] = row;
            n++;
        }
    }

    /// <summary>The server-minted queue item id (<c>ProvidedTrack.uid</c>) packed into <see cref="QueueEdge.ItemId"/>
    /// and back (G-075). A 16-lowercase-hex uid (the autoplay tail's shape) is exactly 64 bits and packs; anything else
    /// answers 0 here, so a round trip is always EXACT — and <see cref="UidBook"/> keeps the shapes that do not pack.</summary>
    public static class QueueUid
    {
        public const int Chars = 16;

        public static ulong ItemIdOf(ReadOnlySpan<byte> uid)
        {
            if (uid.Length != Chars) return 0;
            ulong v = 0;
            for (int i = 0; i < Chars; i++)
            {
                int c = uid[i];
                int nibble = c >= '0' && c <= '9' ? c - '0' : c >= 'a' && c <= 'f' ? c - 'a' + 10 : -1;
                if (nibble < 0) return 0;
                v = (v << 4) | (uint)nibble;
            }
            return v;
        }

        /// <summary>Write the uid back; 0 characters for "no uid" or a buffer under <see cref="Chars"/>.</summary>
        public static int Format(ulong itemId, Span<char> into)
        {
            if (itemId == 0 || into.Length < Chars) return 0;
            for (int i = Chars - 1; i >= 0; i--)
            {
                into[i] = "0123456789abcdef"[(int)(itemId & 0xF)];
                itemId >>= 4;
            }
            return Chars;
        }
    }

    /// <summary>The order of a shuffled "next up" run (G-080). Pure over spans, seeded by the caller (the core reads no
    /// clock and no RNG). Owner Q's Wave-5 <c>QueueOrder</c> in <c>Entities/Queue.cs</c> is the eventual home of this
    /// rule; until it lands the host shuffles through here.</summary>
    public static class ShuffleOrder
    {
        /// <summary>Fisher–Yates over <paramref name="order"/>, driven by a splitmix64 stream from
        /// <paramref name="seed"/>: the same seed always yields the same order, and nothing is allocated.</summary>
        public static void Permute(Span<int> order, ulong seed)
        {
            ulong x = seed;
            for (int i = order.Length - 1; i > 0; i--)
            {
                x += 0x9E3779B97F4A7C15UL;
                ulong z = x;
                z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
                z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
                z ^= z >> 31;
                int j = (int)(z % (ulong)(i + 1));
                (order[i], order[j]) = (order[j], order[i]);
            }
        }

        /// <summary>Put the rows still ahead of the cursor back into the context's own order, in place: every saved
        /// row still present moves to the front in saved order, and a row the saved order never had (queued since the
        /// shuffle) keeps its place after them. Matches on the packed row AND its item id, because the same recording
        /// may sit in a context twice.</summary>
        public static void Unshuffle(ReadOnlySpan<int> savedRefs, ReadOnlySpan<QueueEdge> savedRows, Span<int> refs, Span<QueueEdge> rows)
        {
            int w = 0;
            for (int k = 0; k < savedRefs.Length && w < refs.Length; k++)
            {
                for (int j = w; j < refs.Length; j++)
                {
                    if (refs[j] != savedRefs[k] || rows[j].ItemId != savedRows[k].ItemId) continue;
                    (refs[w], refs[j]) = (refs[j], refs[w]);
                    (rows[w], rows[j]) = (rows[j], rows[w]);
                    w++;
                    break;
                }
            }
        }
    }

    // ── 3b. the Connect intake: the uid book, the controller's queue splice, arrival order (B3c) ────────────────────

    /// <summary>Every queue row's server uid, kept EXACT whatever its shape (G-075). A 16-hex uid packs into the item id
    /// itself (<see cref="QueueUid"/>); a context row's 20-hex uid does not fit 64 bits and a user-queue row's short
    /// <c>q2</c> is not hex (both captured, <c>set-queue-52.json</c>), so those get a locally minted item id and their text
    /// is kept here — and every shape echoes back in PutState as the controller sent it. The same text answers the same
    /// item id, so a controller re-sending its queue keeps the rows' identities. UI thread only; <see cref="Retain"/> after
    /// each queue write keeps it to the live rows. Allocates one string per new booked uid, never on a lookup.</summary>
    public sealed class UidBook
    {
        /// <summary>The longest uid kept; a longer one is not a uid and answers 0 ("no uid").</summary>
        public const int MaxChars = 64;

        readonly Dictionary<ulong, string> _text = new();
        readonly Dictionary<ulong, ulong> _byHash = new();

        /// <summary>How many uids are kept as text.</summary>
        public int Count => _text.Count;

        /// <summary>The item id a row with <paramref name="uid"/> is stored under: the packed value for a 16-hex uid, the
        /// id already booked for the same text, else a freshly minted one (never 0). 0 for an empty or oversized uid.</summary>
        public ulong ItemIdOf(ReadOnlySpan<byte> uid)
        {
            if (uid.IsEmpty || uid.Length > MaxChars) return 0;
            ulong packed = QueueUid.ItemIdOf(uid);
            if (packed != 0 && !_text.ContainsKey(packed)) return packed;
            ulong hash = Hash(uid);
            if (_byHash.TryGetValue(hash, out ulong known) && _text.TryGetValue(known, out string? same) && Same(same, uid))
                return known;
            ulong minted = Queue.MintItemIds(1);
            _text[minted] = System.Text.Encoding.UTF8.GetString(uid);
            _byHash[hash] = minted;                          // a colliding hash re-points; the older id keeps its text
            return minted;
        }

        /// <summary>Write the uid <paramref name="itemId"/> was stored from: its booked text, else the packed 16-hex form.
        /// 0 characters for "no uid" or a buffer too short.</summary>
        public int Format(ulong itemId, Span<char> into)
        {
            if (itemId == 0) return 0;
            if (!_text.TryGetValue(itemId, out string? text)) return QueueUid.Format(itemId, into);
            if (text.Length > into.Length) return 0;
            text.AsSpan().CopyTo(into);
            return text.Length;
        }

        /// <summary>The uid <paramref name="itemId"/> was BOOKED from, as the controller spelled it, or null when it was not
        /// booked (a packed 16-hex uid, a locally minted id, 0). No allocation: the booked string itself (G-240's window).</summary>
        public string? TextOf(ulong itemId) => itemId != 0 && _text.TryGetValue(itemId, out string? text) ? text : null;

        /// <summary>Forget every booked uid no live row carries any more.</summary>
        public void Retain(ReadOnlySpan<QueueEdge> live)
        {
            // Removal while enumerating is defined for Dictionary (it never invalidates the enumerator).
            foreach (KeyValuePair<ulong, string> entry in _text)
            {
                bool present = false;
                for (int i = 0; i < live.Length && !present; i++) present = live[i].ItemId == entry.Key;
                if (present) continue;
                _text.Remove(entry.Key);
                ulong hash = Hash(entry.Value);
                if (_byHash.TryGetValue(hash, out ulong id) && id == entry.Key) _byHash.Remove(hash);
            }
        }

        public void Clear()
        {
            _text.Clear();
            _byHash.Clear();
        }

        static bool Same(string text, ReadOnlySpan<byte> utf8)
        {
            if (text.Length != utf8.Length) return false;
            for (int i = 0; i < utf8.Length; i++) if (text[i] != utf8[i]) return false;
            return true;
        }

        // FNV-1a over the uid's characters; a uid is ASCII, so the byte and char forms hash alike.
        static ulong Hash(ReadOnlySpan<byte> utf8)
        {
            ulong h = 14695981039346656037UL;
            for (int i = 0; i < utf8.Length; i++) { h ^= utf8[i]; h *= 1099511628211UL; }
            return h;
        }

        static ulong Hash(string text)
        {
            ulong h = 14695981039346656037UL;
            for (int i = 0; i < text.Length; i++) { h ^= (byte)text[i]; h *= 1099511628211UL; }
            return h;
        }
    }

    /// <summary>A controller's rows spliced in BEHIND the deck (<c>set_queue</c>, <c>update_context</c> — G-074): the live
    /// rows up to and including <paramref name="deck"/> are kept (history and the current row, re-bucketed as such, so the
    /// reducer's cursor still names the row it is on and nothing reloads), and everything after is replaced by the given
    /// rows in reading order — user-queued rows (provider Queue) first, then the continuation, each in the order given.
    /// Rows past the output are dropped and an unrepresentable target (0) is skipped. Answers the rows written; −1, with
    /// nothing written, when the deck is not a live row or does not fit. PURE.</summary>
    public static int SpliceQueue(ReadOnlySpan<int> livePacked, ReadOnlySpan<QueueEdge> liveRows, int deck,
        ReadOnlySpan<int> nextPacked, ReadOnlySpan<QueueEdge> nextRows, Span<int> packedOut, Span<QueueEdge> rowsOut)
    {
        int cap = Math.Min(packedOut.Length, rowsOut.Length);
        if ((uint)deck >= (uint)liveRows.Length || deck >= livePacked.Length || deck >= cap) return -1;
        for (int i = 0; i <= deck; i++)
        {
            packedOut[i] = livePacked[i];
            rowsOut[i] = liveRows[i] with { Bucket = (byte)(i < deck ? QueueBucket.History : QueueBucket.NowPlaying) };
        }
        int n = deck + 1;
        for (int pass = 0; pass < 2; pass++)
            for (int k = 0; k < nextPacked.Length && k < nextRows.Length && n < cap; k++)
            {
                bool user = nextRows[k].Provider == (byte)QueueProvider.Queue;
                if (nextPacked[k] <= 0 || user != (pass == 0)) continue;
                packedOut[n] = nextPacked[k];
                rowsOut[n] = nextRows[k] with { Bucket = (byte)(user ? QueueBucket.UserQueue : QueueBucket.NextUp) };
                n++;
            }
        return n;
    }

    /// <summary>What a host-side Connect intake carries.</summary>
    public enum IntakeKind : byte { None, Load, Queue }

    /// <summary>One Connect intake the glue hands the host rather than the mailbox: a <c>play</c> / <c>transfer</c> body
    /// (<see cref="Load"/>) or a <c>set_queue</c> / <c>update_context</c> body (<see cref="Rows"/>), with the command that
    /// carried it and the pooled buffer its text and rows live in. <see cref="Ahead"/> is how many mailbox items it still
    /// waits behind.</summary>
    public struct Intake
    {
        public IntakeKind Kind;
        public Spotify.Decode.RemoteCommand Command;
        public Spotify.Decode.RemoteLoad Load;
        public Spotify.Decode.RemoteQueue Rows;
        public Spotify.Decode.ClusterBuffer? Buffer;
        public uint Epoch;
        public int Ahead;
    }

    /// <summary>ARRIVAL ORDER between the Connect mailbox and the host's own intakes (B3c). The glue fills both from one
    /// dealer thread, and a phone's "play, then pause" must reach the reducer in that order — draining the mailbox first
    /// applied the pause to the deck before the play and left the play playing. So an intake records how many mailbox items
    /// were already WAITING when it arrived (read under the host's gate, so no drain dequeue is in flight), the drain counts
    /// every item it takes, and the intake is taken once that many have gone — or when the mailbox is empty. A newer load
    /// supersedes an older one still waiting (its claim is repeated by the newer one); the queue is bounded and drops its
    /// oldest. An item the mailbox itself dropped on overflow is not counted, which can only delay an intake. PURE: the caller
    /// holds the gate; <paramref name="release"/> gets every buffer this lets go of.</summary>
    public sealed class IntakeQueue(Action<Spotify.Decode.ClusterBuffer> release)
    {
        /// <summary>How many intakes wait at most; the oldest goes first.</summary>
        public const int Depth = 8;

        readonly Intake[] _items = new Intake[Depth];
        int _head, _count;

        public int Count => _count;

        /// <summary>Is an inbound play / transfer waiting? A newer load supersedes one whose resolve is still in flight
        /// (G-244, <see cref="SupersedesHold"/>).</summary>
        public bool HasLoad
        {
            get
            {
                for (int i = 0; i < _count; i++) if (_items[(_head + i) % Depth].Kind == IntakeKind.Load) return true;
                return false;
            }
        }

        /// <summary>An intake arrived with <paramref name="pending"/> mailbox items already waiting.</summary>
        public void Arrive(in Intake intake, int pending)
        {
            if (_count == Depth)
            {
                Release(ref _items[_head]);
                _head = (_head + 1) % Depth;
                _count--;
            }
            ref Intake slot = ref _items[(_head + _count) % Depth];
            slot = intake;
            slot.Ahead = Math.Max(0, pending);
            _count++;
        }

        /// <summary>The drain took one mailbox item: every waiting intake is one item closer.</summary>
        public void Taken()
        {
            for (int i = 0; i < _count; i++) _items[(_head + i) % Depth].Ahead--;
        }

        /// <summary>The oldest intake, when it is due before the next mailbox item — or at all, once the mailbox is empty.</summary>
        public bool TryTake(bool mailboxEmpty, out Intake intake)
        {
            if (_count == 0 || (!mailboxEmpty && _items[_head].Ahead > 0)) { intake = default; return false; }
            intake = _items[_head];
            _items[_head] = default;
            _head = (_head + 1) % Depth;
            _count--;
            return true;
        }

        /// <summary>Let every waiting intake go.</summary>
        public void Clear()
        {
            for (int i = 0; i < _count; i++) Release(ref _items[(_head + i) % Depth]);
            _head = 0;
            _count = 0;
        }

        void Release(ref Intake item)
        {
            if (item.Buffer is { } buffer) release(buffer);
            item = default;
        }
    }

    /// <summary>May the drain take mailbox items and later intakes? Not while an inbound load it already took is still
    /// resolving its context (<paramref name="holdSeq"/> is the context load in flight), up to <paramref name="holdUntilMs"/>:
    /// a verb that followed the load must land on the load, not on the deck before it. A load superseded meanwhile (a local
    /// play moved <paramref name="contextSeq"/> on) holds nothing. PURE.</summary>
    public static bool HoldsMailbox(long holdSeq, long contextSeq, long nowMs, long holdUntilMs)
        => holdSeq != 0 && holdSeq == contextSeq && nowMs < holdUntilMs;

    /// <summary>The longest an inbound context resolve holds the Connect mailbox back.</summary>
    public const int InboundHoldMs = 3_000;

    /// <summary>Does a NEWER inbound play / transfer end the hold an older one's resolve put on the mailbox (G-244)? It
    /// does, whenever the hold is live and a load is waiting: the older start is superseded (the host moves the context
    /// sequence on, so its <c>StartContext</c> drops), and the items that arrived between the two loads fold in arrival
    /// order — before the newer load, on the deck it replaces — instead of waiting out the 3 s and then letting the older
    /// load play briefly. PURE.</summary>
    public static bool SupersedesHold(long holdSeq, long contextSeq, long nowMs, long holdUntilMs, bool loadWaiting)
        => loadWaiting && HoldsMailbox(holdSeq, contextSeq, nowMs, holdUntilMs);

    /// <summary>Does a controller's body that just replaced the rows behind the deck get SHUFFLED while shuffle is on
    /// (G-245)? <c>update_context</c> yes — it restates the context in its own order, and the saved unshuffle order becomes
    /// that order. <c>set_queue</c> no — the controller sends the order it wants played, already shuffled or not. PURE.</summary>
    public static bool ReshufflesAfter(RemoteCmd kind, bool shuffling) => shuffling && kind == RemoteCmd.UpdateContext;

    // ── 4. which host a row plays on (G-141) ───────────────────────────────────────────────────────────────────────

    /// <summary>The kind a row loads as: the video host when the placement wants video AND the row carries one, the
    /// audio host for a local file and everything else (<see cref="MediaSwitch.KindOf"/>). A row with no slot — a
    /// foreign device's track not fetched yet — is audio.</summary>
    public static PlayableKind KindOfRow(EntityRef row, bool videoWanted)
    {
        if (row.IsNone || Entities.Current is null) return PlayableKind.Audio;
        if (row.Kind == EntityKind.Episode)
            return videoWanted && (new Episode(row.Slot).Flags & EpisodeFlags.Video) != 0 ? PlayableKind.Video : PlayableKind.Audio;
        if (row.Kind != EntityKind.Track) return PlayableKind.Audio;
        TrackTable t = Entities.Current.Tracks;
        if ((uint)row.Slot >= (uint)t.Count) return PlayableKind.Audio;
        uint flags = t.Flags[row.Slot];
        bool local = (flags & (uint)TrackFlags.Local) != 0 || t.Id[row.Slot].Provider == EntityProvider.Local;
        return MediaSwitch.KindOf(videoWanted && (flags & (uint)TrackFlags.VideoMask) != 0, local);
    }

    // ── 4b. which rows the deck may land on ────────────────────────────────────────────────────────────────────────

    /// <summary>May the deck land on <paramref name="row"/>? False for no row at all, and for a track the catalog has
    /// RULED unavailable with no release instant (<see cref="Track.Unplayable()"/> — withdrawn, region-locked, gone):
    /// its load can only fail, and a natural end that lands there strands the session on "3:33 / 0:00". An unruled
    /// row, a row with no slot yet (a foreign device's track), a slot from a retired scope and every episode are
    /// playable — a verdict nobody gave is not a verdict, and refusing to play what we merely have not asked about
    /// would empty a cold queue.</summary>
    public static bool RowPlayable(EntityRef row)
    {
        if (row.IsNone) return false;
        if (Entities.Current is null) return true;
        if (Spotify.Library.IsBanned(row.Id)) return false;
        if (row.Kind == EntityKind.Episode) return (new Episode(row.Slot).Flags & EpisodeFlags.Unplayable) == 0;
        if (row.Kind != EntityKind.Track) return true;
        var t = new Track(row.Slot);
        return !t.IsValid || !t.Unplayable();
    }

    /// <summary>The live queue's rows answered through <see cref="RowPlayable"/> — what the reducer hands
    /// <see cref="Queue.NextPlayable"/>. Stateless, so <c>default</c> is the instance.</summary>
    readonly struct LiveRows : Queue.IPlayableRows
    {
        public bool IsPlayable(int index) => RowPlayable(Queue.RefAt(index));
    }

    // ── 5. the next row: prefetch at load, prepare in the endgame (G-112) ──────────────────────────────────────────

    /// <summary>The row a NATURAL end continues into, and its cursor: the queue's next PLAYABLE row (a ruled-dead row is
    /// stepped past, so the pump never prepares a stream that cannot open — the same walk <c>Advance</c> runs), the wrap
    /// row under repeat-context (<see cref="Queue.WrapIndex"/> — the first row the context itself provided; a context
    /// with a page still to fetch continues into that page instead, G-242), nothing under repeat-track (the same row
    /// reloads; there is nothing to hand off) or at the end.</summary>
    static EntityRef NaturalNext(in State s, out QueueCursor cursor)
    {
        cursor = QueueCursor.None;
        if (s.Cursor.IsNone || s.Repeat == RepeatMode.Track) return default;
        int next = Queue.NextPlayable(Queue.Rows, default(LiveRows), s.Cursor.Index, forward: true,
                                      wrap: s.Repeat == RepeatMode.Context && !s.MorePages);
        if (next < 0) return default;
        cursor = Queue.CursorOf(next);
        return Queue.RefAt(next);
    }

    /// <summary>Re-arm the pump's idea of the next row. Called on every load, on the endgame signal and whenever the
    /// queue, the shuffle or the repeat mode may have changed what comes next. Idempotent: the same row in the same arm
    /// state emits nothing. A spliceable next row (audio after audio) is PREFETCHED until the endgame opens, then
    /// PREPARED; a row that stops being next cancels the prepared slot.</summary>
    static void ArmNext(ref State s, ref Effects fx)
    {
        QueueCursor nextCursor = QueueCursor.None;
        EntityRef next = s.RoutesLocal && s.HasCurrent && s.Phase is Phase.Loading or Phase.Playing or Phase.Paused
            ? NaturalNext(in s, out nextCursor)
            : default;
        EntityId id = !next.IsNone && s.Kind == PlayableKind.Audio && KindOfRow(next, s.VideoWanted) == PlayableKind.Audio
            ? next.Id
            : default;
        // The row after the next one is warmed too, forward only (a wrap row's successor is never past the deck row).
        EntityRef next2 = default;
        EntityId id2 = default;
        bool podcast = s.CurrentId.Kind == EntityKind.Episode;
        if (podcast && !s.EndingSoon) id = default;
        if (!podcast && !id.IsEmpty)
        {
            int at = Queue.NextPlayable(Queue.Rows, default(LiveRows), nextCursor.Index, forward: true, wrap: false);
            if (at >= 0 && at != s.Cursor.Index)
            {
                EntityRef row = Queue.RefAt(at);
                if (KindOfRow(row, s.VideoWanted) == PlayableKind.Audio) { next2 = row; id2 = row.Id; }
            }
        }
        // A parked deck (a restore, a stop) warms what follows it but prepares nothing: there is no voice to join.
        bool prepare = !podcast && s.EndingSoon && !s.Parked && !id.IsEmpty;
        bool next2Changed = !id2.Equals(s.Next2Id);
        if (id.Equals(s.NextId) && prepare == s.NextArmed && !next2Changed) return;

        bool wasArmed = s.NextArmed;
        s.NextId = id;
        s.NextArmed = prepare;
        s.Next2Id = id2;
        fx.Prefetch = false;
        fx.Prefetch2 = false;
        if (!id2.IsEmpty && next2Changed)
        {
            fx.Prefetch2 = true;
            fx.Prefetch2Row = next2;
            fx.Prefetch2Id = id2;
        }
        if (id.IsEmpty || !prepare)
        {
            fx.PrepareNext = false;
            if (wasArmed) fx.CancelPrepared = true;
            if (id.IsEmpty) return;
            fx.Prefetch = true;
            fx.PrefetchRow = next;
            fx.PrefetchId = id;
            return;
        }
        // The pump replaces a slot holding another id, so a re-prepare needs no cancel in front of it.
        fx.CancelPrepared = false;
        fx.PrepareNext = true;
        fx.NextRow = next;
        fx.NextId = id;
    }

    /// <summary>The pump's endgame window opened for this load. Prepare the next row now — and, when the context is
    /// about to run out with nothing after it, ask for more EARLY — its next page when the host holds one (G-242), else
    /// autoplay — so the first new row can be prepared and joined gaplessly instead of cut in after a silence.</summary>
    static void DoEndingSoon(ref State s, ref Effects fx)
    {
        if (s.EndingSoon || !s.RoutesLocal) return;
        s.EndingSoon = true;
        // Under repeat-context a context still paging asks for its page first (NaturalNext does not wrap past it).
        if ((s.Repeat == RepeatMode.Off || (s.Repeat == RepeatMode.Context && s.MorePages))
            && s.Autoplay == AutoplayPhase.None && !s.Context.IsEmpty
            && NaturalNext(in s, out _).IsNone)
        {
            s.Autoplay = AutoplayPhase.Requested;
            AskForMore(ref s, ref fx);
        }
        ArmNext(ref s, ref fx);
    }

    /// <summary>The context is running out: its next page when the host holds one, the autoplay answer's next page when
    /// the host holds that, autoplay otherwise (G-242).</summary>
    static void AskForMore(ref State s, ref Effects fx)
    {
        if (s.MorePages) { fx.Page = true; fx.PageContext = s.Context; return; }
        if (s.AutoplayPages) { fx.AutoplayPage = true; fx.AutoplayPageContext = s.Context; return; }
        fx.Autoplay = true;
        fx.AutoplayContext = s.Context;
    }

    /// <summary>A new context on the deck: every "more rows" fact belongs to the old one.</summary>
    static void ResetRefill(ref State s)
    {
        s.Autoplay = AutoplayPhase.None;
        s.MorePages = false;
        s.PagesKnown = false;
        s.AutoplayPages = false;
    }

    /// <summary>The queue changed under the deck (an add, a reorder, an autoplay append, a context edit, a follow). While
    /// the deck is ours the controllers are told: their queue view is our PUT's next_tracks, and 0.2.9's publisher announced
    /// every queue change the same way (G-240). The announce slot coalesces with whatever else the drain changed.</summary>
    static void DoQueueChanged(ref State s, ref Effects fx)
    {
        ArmNext(ref s, ref fx);
        CheckRefill(ref s, ref fx);
        if (s.Own.Kind == Owner.Us && s.HasCurrent) Announce(ref s, ref fx, PublishReason.PlayerStateChanged);
    }

    /// <summary>The host holds (or stopped holding) a next page for the context on the deck (G-242). From here the refill
    /// rule decides (<see cref="CheckRefill"/>): a context that landed complete asks autoplay at once, one that landed
    /// with a page and little ahead of the deck pages at once, and the rest wait until the run is nearly consumed.
    /// <see cref="DoEndingSoon"/> stays the fallback for a run that reaches the endgame with nothing asked.</summary>
    static void DoContextPages(ref State s, in Input i, ref Effects fx)
    {
        if (!i.Context.Equals(s.Context)) return;
        s.MorePages = i.IntArg != 0;
        s.PagesKnown = true;
        CheckRefill(ref s, ref fx);
    }

    /// <summary>The deck's CONTEXT changes under the row it is on, with NO load (G-251): "Start radio" while a track plays
    /// resolves the radio playlist, lays it behind the current row (<see cref="RemotePlan.RadioAfterCurrent"/>) and makes
    /// it the context that row is now playing from. The row, its audio, its position, its epoch and its registration are
    /// untouched — only what follows it and what "Playing from" names change. The autoplay / paging bookkeeping starts
    /// over for the new context exactly as a Play into it would (<see cref="DoPlay"/>), the next row is re-armed off the
    /// rewritten queue, controllers are told, and the restore point follows. A deck that had run out and was WAITING for
    /// the old context's answer advances into the radio at once — that answer names a context no longer on the deck and
    /// would otherwise strand it. Refused while another device owns playback or the deck is empty (nothing to park
    /// behind); a cursor that no longer names the deck row keeps the old one.</summary>
    static void DoSwitchContext(ref State s, in Input i, ref Effects fx)
    {
        if (!s.RoutesLocal || !s.HasCurrent || i.Context.IsEmpty) return;
        if (!i.Cursor.IsNone && Entities.Current is not null && Queue.RefAt(in i.Cursor) == s.Current) s.Cursor = i.Cursor;
        bool waiting = s.Autoplay == AutoplayPhase.Waiting;
        ResetRefill(ref s);
        s.Context = i.Context;
        if (waiting) Advance(ref s, in i, ref fx, forward: true, PlayReason.TrackDone);
        else ArmNext(ref s, ref fx);
        if (s.Own.Kind == Owner.Us) Announce(ref s, ref fx, PublishReason.PlayerStateChanged);
        fx.Snapshot = true;
    }

    /// <summary>A context page answered (G-242): rows were appended as Context NextUp, or none were. A deck waiting at the
    /// end advances into them; a page that brought nothing ends the paging, and the run-out then asks autoplay — or, under
    /// repeat-context, wraps to the head.</summary>
    static void DoPaged(ref State s, in Input i, ref Effects fx)
    {
        if (!i.Context.Equals(s.Context)) return;
        bool waiting = s.Autoplay == AutoplayPhase.Waiting;
        s.MorePages = i.IntArg > 0 && i.LongArg != 0;
        s.Autoplay = AutoplayPhase.None;
        if (i.IntArg <= 0)
        {
            if (!waiting) { CheckRefill(ref s, ref fx); return; }
            if (s.Repeat == RepeatMode.Context) Advance(ref s, in i, ref fx, forward: true, PlayReason.TrackDone);
            else EndOfContext(ref s, in i, ref fx, PlayReason.TrackDone);
            return;
        }
        if (waiting) Advance(ref s, in i, ref fx, forward: true, PlayReason.TrackDone);
        else { ArmNext(ref s, ref fx); CheckRefill(ref s, ref fx); }
    }

    /// <summary>The user queued rows while another device owns playback (G-248): a controller's queue edit is a command to
    /// the owner, never a write to our stale local queue. One row appended is <c>add_to_queue</c>; several, or "play next",
    /// is a <c>set_queue</c> of the owner's own queue with the rows spliced in. A deck that is ours (or nobody's) is the
    /// local queue's, and the caller already wrote it: nothing is forwarded.</summary>
    static void DoQueueToOwner(ref State s, in Input i, ref Effects fx)
    {
        if (i.IntArg <= 0) return;
        bool next = (i.LongArg & 1) != 0;
        long run = ((i.LongArg >> 1) << 32) | (uint)i.IntArg;          // the staged run: offset high, count low
        Forward(ref s, i.IntArg == 1 && !next ? RemoteCmd.AddToQueue : RemoteCmd.SetQueue, ref fx, run, next);
    }

    /// <summary>Does the host put the queue's buckets back under the reducer's cursor after a drain
    /// (<see cref="Queue.Follow"/>)? The reducer only MOVES the cursor; the surfaces that read buckets — the now-playing
    /// "Next up", the sidebar queue feed, the rail — would otherwise show the rows of the deck before the advance. Only
    /// while the deck is LOCAL and the queue still holds the deck's row at the cursor (<paramref name="atCursor"/>): a
    /// foreign owner's list is never re-bucketed under a stale local cursor, and a queue replaced by a play the reducer
    /// has not folded yet is left as it was written.</summary>
    public static bool FollowsQueue(in State s, EntityRef atCursor)
        => s.RoutesLocal && !s.Cursor.IsNone && !s.Current.IsNone && atCursor == s.Current;

    // ── 6. the gapless hand-off (G-100, D4) ────────────────────────────────────────────────────────────────────────

    /// <summary>The pump joined the prepared row. The voice is already live, so there is NO Load: the cursor moves, the
    /// epoch bumps, and the pump is handed the new epoch through <see cref="Effects.Adopt"/> so its next position tick
    /// is not dropped as stale. <c>Ended</c> with nothing prepared stays the hard-cut fallback (<see cref="DoEnded"/>).</summary>
    static void DoHandedOff(ref State s, in Input i, ref Effects fx)
    {
        if (!s.RoutesLocal) return;
        EntityRef next = NaturalNext(in s, out QueueCursor cursor);
        if (next.IsNone || !next.Id.Equals(i.Id) || KindOfRow(next, s.VideoWanted) != PlayableKind.Audio)
        {
            // The listener hears a row the queue no longer puts next (an edit, a shuffle or a repeat change raced the
            // prepare). Hard-cut to the truth: the Load replaces the joined voice.
            s.HasBeenPlayingForMs += Math.Max(0, i.NowMs - s.PosQpc);
            Advance(ref s, in i, ref fx, forward: true, PlayReason.TrackDone);
            return;
        }

        uint from = s.LoadEpoch;
        s.HasBeenPlayingForMs += Math.Max(0, i.NowMs - s.PosQpc);
        ReportEnd(ref s, ref fx, PlayReason.TrackDone, s.DurationMs > 0 ? s.DurationMs : s.Position(i.NowMs));
        PutOnDeck(ref s, in i, next, i.Id, cursor, PlayableKind.Audio, (int)Math.Clamp(i.LongArg, 0, int.MaxValue), paused: false);
        s.Phase = Phase.Playing;                          // audible already: this is not a load
        s.StartReason = PlayReason.TrackDone;
        Bump(ref s);
        s.LoadEpoch = s.Epoch;
        s.LoadWhy = LoadOrigin.Advance;                   // the deck's own move: a dead joined row is skipped, not parked
        s.AutoSkips = 0;                                  // the joined row is audible: the dead-row run, if any, is over
        s.EndingSoon = false;                             // the prepared slot IS the live voice now
        s.NextId = default;
        s.NextArmed = false;
        fx.Adopt = true;
        fx.AdoptFrom = from;
        fx.AdoptTo = s.Epoch;
        ReportStart(ref s, ref fx, PlayReason.TrackDone, s.PosMs);
        ArmNext(ref s, ref fx);
        Announce(ref s, ref fx, PublishReason.PlayerStateChanged);
        fx.Smtc = true;
        fx.SmtcEpoch = s.Epoch;
        fx.Snapshot = true;
    }

    // ── 7. the load tails every transition shares ──────────────────────────────────────────────────────────────────

    /// <summary>Put a row on the deck with a fresh transport: what every load writes before it asks a host for
    /// anything. The caller decides the context, the claim and the reason.</summary>
    static void PutOnDeck(ref State s, in Input i, EntityRef row, EntityId id, QueueCursor cursor, PlayableKind kind,
        int fromMs, bool paused)
    {
        s.Current = row;
        s.CurrentId = id;
        s.Kind = kind;
        s.Cursor = cursor;
        s.Phase = paused ? Phase.Paused : Phase.Loading;
        s.Buffering = false;
        s.Error = Fault.None;
        s.Recovery = RecoveryKind.None;
        s.PosMs = Math.Max(0, fromMs);
        s.PosQpc = i.NowMs;
        s.DurationMs = 0;
        s.StreamFormat = StringId.Empty;
        s.Live = LiveWindow.None;
        s.Edge = LiveEdgeState.AtEdge;
        s.TunedInAtMs = 0;
        s.VideoRetries = 0;
    }

    /// <summary>THE load tail: bump, mark the new load epoch, fill the Load slot, re-arm the next row, announce, and
    /// refresh the card. The pump's prepared slot dies with the session a Load replaces, so the arm resets first.
    /// <para>An EPISODE a Claim or an Advance starts "from the top" starts where it was left (<see cref="EpisodeStartOf"/>)
    /// — decided HERE, on the deck, so the position is the published one from the first frame: a paused load (a paused
    /// transfer, a restored context) paints 12:34 before any audio exists, where the host-side resolution painted 0:00
    /// until playback began. The host opens the stream at the same number (<see cref="Effects.LoadFromMs"/>). A reload
    /// (device, media kind, video recovery) is not a start and keeps its own position, 0 included.</para></summary>
    static void EmitLoad(ref State s, ref Effects fx, LoadOrigin why, bool paused, bool explicitPosition = false)
    {
        if (!explicitPosition && s.PosMs == 0 && why is LoadOrigin.Claim or LoadOrigin.Advance) s.PosMs = EpisodeStartOf(s.CurrentId);
        Bump(ref s);
        s.LoadEpoch = s.Epoch;
        s.LoadWhy = why;
        s.Parked = false;
        s.EndingSoon = false;
        s.NextId = default;
        s.NextArmed = false;
        fx.Load = true;
        fx.LoadRow = s.Current;
        fx.LoadId = s.CurrentId;
        fx.LoadKind = s.Kind;
        fx.LoadEpoch = s.Epoch;
        fx.LoadFromMs = s.PosMs;
        fx.LoadWhy = why;
        fx.LoadPaused = paused;
        fx.Adopt = false;                                 // a load supersedes any adoption earlier in this drain
        fx.CancelPrepared = false;
        ArmNext(ref s, ref fx);
        CheckRefill(ref s, ref fx);
        Announce(ref s, ref fx, PublishReason.PlayerStateChanged);
        fx.Smtc = true;
        fx.SmtcEpoch = s.Epoch;
    }

    /// <summary>Where an episode load that named no position starts (G-076, podcast plan §5.8, D-8): the row's own
    /// progress — hydrated at login, mirrored by this player, set by a mark — through
    /// <see cref="EpisodeProgress.ResumeFromMs"/>, so a finished episode starts over and an unknown one at 0 (§6.1: no
    /// state, never a guess). A TABLE READ on the UI thread, never a wire one (0.3 used to ask herodotus per load and
    /// seek once the answer came, after audio had begun). 0 for anything that is not a resident episode row, and without
    /// a scope.</summary>
    public static int EpisodeStartOf(EntityId id)
    {
        if (id.Kind != EntityKind.Episode || Entities.Current is null) return 0;
        EpisodeTable t = Entities.Current.Episodes;
        if (!t.TryGetSlot(id, out int slot)) return 0;
        return new Episode(slot).Completed ? 0 : EpisodeProgress.ResumeFromMs(t.Knows(slot, (uint)EpisodeFields.Progress), t.ProgressMs[slot], t.DurationMs[slot]);
    }

    /// <summary>Reload the row on the deck on <paramref name="kind"/>'s host at the current position, play intent kept:
    /// the device-format recovery, the audio ⇄ video switch and the video retry. The registration stays open — the
    /// listener is still on the same row.</summary>
    static void ReloadHost(ref State s, in Input i, ref Effects fx, PlayableKind kind, LoadOrigin why)
    {
        bool paused = s.Phase == Phase.Paused;
        int at = s.Position(i.NowMs);
        int duration = s.DurationMs;
        byte retries = s.VideoRetries;
        PutOnDeck(ref s, in i, s.Current, s.CurrentId, s.Cursor, kind, at, paused);
        s.DurationMs = duration;                          // the same row: its stated duration still holds
        s.VideoRetries = retries;
        EmitLoad(ref s, ref fx, why, paused);
    }

    /// <summary>The output device changed format under a graph that cannot render on it (G-108): reload at the
    /// position, intent kept. 0.2.9 soft-reloaded here; 0.3 sat "Playing" in silence.</summary>
    static void DoDeviceReload(ref State s, in Input i, ref Effects fx)
    {
        if (!s.RoutesLocal || !s.HasCurrent || s.Parked || s.Phase is Phase.Idle or Phase.Ended) return;
        ReloadHost(ref s, in i, ref fx, s.Kind, LoadOrigin.DeviceReload);
    }

    // ── 8. video: the placement fold and the recovery (G-140, G-141, G-142, G-143) ─────────────────────────────────

    /// <summary>How many times a failing video load is re-resolved before the placement is demoted to audio.</summary>
    public const int VideoRetryBudget = 1;

    /// <summary>The placement turned video on or off (owner K posts it on the <c>IsActive</c> edge). A row with a video
    /// switches hosts at the CARRIED position; a row without one only records the wish, so the next row with a video
    /// opens on the video host. Idle, parked or foreign decks just remember it.</summary>
    static void DoVideoPlacement(ref State s, in Input i, ref Effects fx)
    {
        bool wanted = i.IntArg != 0;
        if (s.VideoWanted != wanted) { s.VideoWanted = wanted; s.VideoRetries = 0; }   // a repeated "on" re-decides the row's kind (a lit badge's click)
        if (!s.HasCurrent) return;
        PlayableKind kind = KindOfRow(s.Current, wanted);
        if (kind == s.Kind) return;
        if (!s.RoutesLocal || s.Parked || s.Phase is Phase.Idle or Phase.Ended) { s.Kind = kind; return; }
        ReloadHost(ref s, in i, ref fx, kind, LoadOrigin.MediaKindRefresh);
    }

    /// <summary>A video load failed (<paramref name="retry"/>) or found no source at all (not a retry). One re-resolve
    /// is spent first; then the placement is demoted and the row reloads on the audio host at the carried position,
    /// and the shell is told so it can say why (<see cref="Effects.VideoDemoted"/>). 0.3 used to fold the fault as a
    /// dead track.</summary>
    static void DoVideoFault(ref State s, in Input i, ref Effects fx, bool retry, Fault fault)
    {
        if (retry && s.VideoRetries < VideoRetryBudget)
        {
            s.VideoRetries++;
            ReloadHost(ref s, in i, ref fx, PlayableKind.Video, LoadOrigin.VideoRecovery);
            return;
        }
        s.VideoWanted = false;
        fx.VideoDemoted = true;
        fx.VideoDemotedWhy = fault == Fault.None ? Fault.Unavailable : fault;
        ReloadHost(ref s, in i, ref fx, KindOfRow(s.Current, videoWanted: false), LoadOrigin.VideoRecovery);
        s.VideoRetries = 0;
    }

    /// <summary>The current row's video facts GAINED something the connect-state service has not been told (a badge
    /// landed after the track-change announce): one extra PutState, and only while we own playback.</summary>
    static void DoVideoOffer(ref State s, in Input i, ref Effects fx)
    {
        if (s.Own.Kind != Owner.Us || !i.Id.Equals(s.CurrentId)) return;
        Announce(ref s, ref fx, PublishReason.PlayerStateChanged);
    }

    // ── 9. restore, remote volume, autoplay ────────────────────────────────────────────────────────────────────────

    /// <summary>Put the last session back on the deck at launch (G-078): PAUSED, PARKED (no host holds it until the
    /// first Resume loads it at the saved position), AUDIO-FIRST and WITHOUT CLAIMING. Refused over a live deck and
    /// while another device owns playback (<see cref="Ownership.AllowsLoad"/>).</summary>
    static void DoRestore(ref State s, in Input i, ref Effects fx)
    {
        if (s.HasCurrent || i.Id.IsEmpty || !i.Id.IsPlayable) return;
        if (!Ownership.AllowsLoad(in s.Own, LoadOrigin.Restore, paused: true)) return;
        s.Current = i.Row;
        s.CurrentId = i.Id;
        s.Context = i.Context;
        s.Cursor = i.Cursor;
        s.Kind = KindOfRow(i.Row, videoWanted: false);
        s.Phase = Phase.Paused;
        s.Parked = true;
        s.PosMs = Math.Max(0, i.IntArg);
        s.PosQpc = i.NowMs;
        s.DurationMs = (int)Math.Clamp(i.LongArg, 0, int.MaxValue);
        s.StartReason = PlayReason.PlayButton;
        Bump(ref s);
        if (!i.Id.IsEmpty && !RowNamesId(i.Row, i.Id)) { fx.Fetch = true; fx.FetchId = i.Id; fx.FetchEpoch = s.Epoch; }
        ArmNext(ref s, ref fx);
        CheckRefill(ref s, ref fx);
        fx.Smtc = true;
        fx.SmtcEpoch = s.Epoch;
    }

    /// <summary>A controller set THIS device's volume (<c>connect/volume</c>, G-073). It is ours whoever owns playback,
    /// so it never forwards anywhere — not echoed back — and the PutState it causes is attributed to the sender's
    /// message id so its slider does not fight an unattributed update.</summary>
    static void DoRemoteVolume(ref State s, in Input i, ref Effects fx)
    {
        int wire = Math.Clamp(i.IntArg, 0, MaxWireVolume);
        float v = wire / (float)MaxWireVolume;
        if (i.LongArg != 0)
        {
            s.LastCommandMessageId = (uint)i.LongArg;
            s.LastCommandAtMs = i.NowMs;                  // aged like a player command's (G-246)
            s.LastCommandSender = 0;                      // connect/volume names no sender
        }
        if (Math.Abs(v - s.Volume) * MaxWireVolume < 1f) return;
        s.Volume = v;
        Bump(ref s);
        fx.Volume = true;
        fx.VolumeValue = v;
        fx.VolumeEpoch = s.Epoch;
        Announce(ref s, ref fx, PublishReason.VolumeChanged);
        fx.Snapshot = true;
    }

    /// <summary>The autoplay answer for <see cref="Input.Context"/> (G-080): rows were appended as
    /// <see cref="QueueProvider.Autoplay"/> NextUp, or none were. A deck waiting at the end advances into them. An
    /// answer the host could not get (offline, refused by the session) is <see cref="AutoplayPhase.Deferred"/>, not
    /// exhausted: the next <see cref="InputKind.SessionOnline"/> asks again.</summary>
    static void DoAutoplayed(ref State s, in Input i, ref Effects fx)
    {
        if (!i.Context.Equals(s.Context)) return;
        bool waiting = s.Autoplay == AutoplayPhase.Waiting;
        bool morePages = (i.LongArg & Input.AutoplayMorePagesBit) != 0;
        bool retry = (i.LongArg & Input.AutoplayRetryBit) != 0;
        s.AutoplayPages = i.IntArg > 0 && morePages;
        if (i.IntArg <= 0)
        {
            s.Autoplay = retry ? AutoplayPhase.Deferred : AutoplayPhase.Exhausted;
            if (waiting) EndOfQueue(ref s, in i, ref fx, PlayReason.TrackDone);
            return;
        }
        s.Autoplay = AutoplayPhase.None;                  // the run grew: the refill rule decides the next ask
        if (waiting) Advance(ref s, in i, ref fx, forward: true, PlayReason.TrackDone);
        else { ArmNext(ref s, ref fx); CheckRefill(ref s, ref fx); }
    }

    /// <summary>An autoplay page answered: rows were appended as <see cref="QueueProvider.Autoplay"/> NextUp, or none
    /// were. A deck waiting at the end advances into them; an empty page ends the paging and the run-out (or the refill
    /// rule) asks autoplay afresh.</summary>
    static void DoAutoplayPaged(ref State s, in Input i, ref Effects fx)
    {
        if (!i.Context.Equals(s.Context)) return;
        bool waiting = s.Autoplay == AutoplayPhase.Waiting;
        s.AutoplayPages = i.IntArg > 0 && i.LongArg != 0;
        s.Autoplay = AutoplayPhase.None;
        if (i.IntArg <= 0)
        {
            if (!waiting) { CheckRefill(ref s, ref fx); return; }
            EndOfContext(ref s, in i, ref fx, PlayReason.TrackDone);
            return;
        }
        if (waiting) Advance(ref s, in i, ref fx, forward: true, PlayReason.TrackDone);
        else { ArmNext(ref s, ref fx); CheckRefill(ref s, ref fx); }
    }

    /// <summary>The session came online. A deferred autoplay ask is asked again, and the prefetches the pump dropped
    /// while offline are re-issued; a prepared row stays prepared.</summary>
    static void DoSessionOnline(ref State s, ref Effects fx)
    {
        if (s.Autoplay == AutoplayPhase.Deferred) s.Autoplay = AutoplayPhase.None;
        if (!s.NextArmed) s.NextId = default;
        s.Next2Id = default;
        ArmNext(ref s, ref fx);
        CheckRefill(ref s, ref fx);
    }

    /// <summary>The context ran out (a natural end or a forward click past the last row). Its next page (G-242) or
    /// autoplay is asked once per run-out and the deck waits for the answer; a declined or empty autoplay answer — or a
    /// context autoplay cannot continue — ends the phase.</summary>
    static void EndOfContext(ref State s, in Input i, ref Effects fx, PlayReason why)
    {
        // Exhausted with an autoplay page still held pages it; deferred (offline) ends here and SessionOnline asks again.
        bool exhausted = s.Autoplay == AutoplayPhase.Exhausted && !s.MorePages && !s.AutoplayPages;
        if (exhausted || s.Autoplay == AutoplayPhase.Deferred || s.Context.IsEmpty) { EndOfQueue(ref s, in i, ref fx, why); return; }
        if (s.Autoplay == AutoplayPhase.Waiting) return;
        if (s.Autoplay is AutoplayPhase.None or AutoplayPhase.Exhausted) AskForMore(ref s, ref fx);
        s.Autoplay = AutoplayPhase.Waiting;
        ReportEnd(ref s, ref fx, why, why == PlayReason.TrackDone && s.DurationMs > 0 ? s.DurationMs : s.Position(i.NowMs));
        s.PosMs = s.Position(i.NowMs);
        s.PosQpc = i.NowMs;
        s.Phase = Phase.Loading;
        s.Buffering = false;
        s.EndingSoon = false;
        s.NextId = default;
        s.NextArmed = false;
        Bump(ref s);
        s.LoadEpoch = s.Epoch;
        fx.Load = false;
        fx.Stop = true;
        fx.StopWhy = StopReason.EndOfQueue;
        fx.TransportEpoch = s.Epoch;
        fx.Smtc = true;
        fx.SmtcEpoch = s.Epoch;
    }

    // ── 10. the report helpers (G-076) ─────────────────────────────────────────────────────────────────────────────

    static void ReportStart(ref State s, ref Effects fx, PlayReason why, int positionMs)
    {
        if (s.ReportOpen || s.CurrentId.IsEmpty) return;
        s.ReportOpen = true;
        fx.Play.Events |= PlayEvents.Started;
        fx.Play.StartedId = s.CurrentId;
        fx.Play.Context = s.Context;
        fx.Play.StartReason = why == PlayReason.None ? PlayReason.ClickRow : why;
        fx.Play.StartPosMs = Math.Max(0, positionMs);
        fx.Play.DurationMs = s.DurationMs;
    }

    static void ReportEnd(ref State s, ref Effects fx, PlayReason why, int positionMs)
    {
        if (!s.ReportOpen) return;
        s.ReportOpen = false;
        fx.Play.Events |= PlayEvents.Ended;
        fx.Play.EndedId = s.CurrentId;
        fx.Play.EndReason = why;
        fx.Play.EndPosMs = Math.Max(0, positionMs);
    }

    static void ReportPaused(ref State s, ref Effects fx, int positionMs)
    {
        if (!s.ReportOpen) return;
        fx.Play.Events |= PlayEvents.Paused;
        fx.Play.PausePosMs = Math.Max(0, positionMs);
    }

    static void ReportResumed(ref State s, ref Effects fx, int positionMs)
    {
        if (!s.ReportOpen) return;
        fx.Play.Events |= PlayEvents.Resumed;
        fx.Play.ResumePosMs = Math.Max(0, positionMs);
    }

    static void ReportSeeked(ref State s, ref Effects fx, int fromMs, int toMs)
    {
        if (!s.ReportOpen) return;
        fx.Play.Events |= PlayEvents.Seeked;
        fx.Play.SeekFromMs = Math.Max(0, fromMs);
        fx.Play.SeekToMs = Math.Max(0, toMs);
    }

    // ── 11. the pure host gates (ported from _old/Wavee/App + SpotifyLive/Audio; moved from Playback.cs §4) ───────

    /// <summary>The PURE decision rules for the ONE current media's host swap: which kind a playable is, whether a
    /// change reloads the current host or swaps hosts, whether a crossfade is allowed across the boundary, what
    /// <c>track_player</c> Connect should report, and whether the outgoing host must be stopped first. Ported from
    /// <c>_old/Wavee/App/MediaSwitchLogic.cs</c>.
    ///
    /// <para>0.2.9's <c>HasVideoMetadata</c> / <c>StampVideoAssociation</c> are deliberately NOT ported: both took an
    /// <c>IReadOnlyDictionary&lt;string,string&gt;</c> of wire metadata, and 0.3's cluster decode folds that map to
    /// typed fields on the way in (<c>Spotify.Decode.ClusterTrack</c>) — re-introducing the dictionary here would
    /// re-introduce the per-row allocation the decode exists to remove.</para></summary>
    public static class MediaSwitch
    {
        /// <summary>Classify the current media into the ONE kind that selects its host. A video track is always
        /// <see cref="PlayableKind.Video"/> regardless of origin (video wins over local); otherwise a local file is
        /// <see cref="PlayableKind.LocalFile"/>; everything else is <see cref="PlayableKind.Audio"/>.</summary>
        public static PlayableKind KindOf(bool isVideoTrack, bool isLocalFile)
            => isVideoTrack ? PlayableKind.Video
             : isLocalFile ? PlayableKind.LocalFile
             : PlayableKind.Audio;

        /// <summary>What the current-media owner should do to honour a change from one playable to another.</summary>
        public enum SwitchAction : byte
        {
            /// <summary>Same kind → the host is unchanged; just re-load the new playable onto it.</summary>
            LoadOnCurrent,
            /// <summary>Different kind → stop the outgoing host, swap for the new kind's host, then load.</summary>
            SwapThenLoad,
        }

        /// <inheritdoc cref="SwitchAction"/>
        public static SwitchAction Decide(PlayableKind current, PlayableKind next)
            => current == next ? SwitchAction.LoadOnCurrent : SwitchAction.SwapThenLoad;

        /// <summary>Whether a crossfade / prepared-next transition is allowed across this boundary. Crossfade is an
        /// AUDIO-only, same-kind capability; every cross-kind boundary and every video boundary is a HARD CUT.</summary>
        public static bool AllowCrossfade(PlayableKind from, PlayableKind to)
            => from == to && from == PlayableKind.Audio;

        /// <summary>The Connect <c>track_player</c> metadata value: <c>"video"</c> for <see cref="PlayableKind.Video"/>,
        /// else <c>"audio"</c> (a local file plays through the audio host and therefore also reports audio).</summary>
        public static string TrackPlayer(PlayableKind kind) => kind == PlayableKind.Video ? "video" : "audio";

        /// <summary>Whether the outgoing host must be stopped BEFORE the new one starts. True on any kind change so
        /// two decoders never both output audio at once.</summary>
        public static bool ShouldStopOutgoingHost(PlayableKind current, PlayableKind next) => current != next;

        /// <summary>Whether the ONE current-media HOST INSTANCE actually changes. <see cref="PlayableKind.Audio"/> and
        /// <see cref="PlayableKind.LocalFile"/> share the SAME host — only a <see cref="PlayableKind.Video"/> boundary
        /// flips to (or away from) the video host, so an Audio↔LocalFile change stays a same-host reload and keeps the
        /// fast-start / prepared-next path untouched.</summary>
        public static bool HostChanges(PlayableKind current, PlayableKind next)
            => (current == PlayableKind.Video) != (next == PlayableKind.Video);
    }

    /// <summary>What the audio host does with a seek request, decided against the state of the serialized pump at the
    /// moment the seek op RUNS (never earlier — the answer depends on ops that ran before it).</summary>
    public enum SeekAdmission : byte
    {
        /// <summary>Hand the seek to the engine now: a session is open and its byte source can serve the target.</summary>
        ApplyNow,
        /// <summary>Park the target and apply it after the next body attach / session open — the engine's seek would
        /// otherwise block the pump waiting for bytes that only a LATER pump op can attach.</summary>
        Defer,
    }

    /// <summary>The pure decision behind the audio host's seek. It exists because of one deadlock: the controller
    /// enqueues <c>LoadFastStart</c> → <c>Seek(resumePositionMs)</c> → (later) <c>SupplyBody</c> onto ONE serialized
    /// pump, and the engine's seek holds its replacement gate until the decode producer has PCM at the target. A
    /// fast-start session owns only the ~80 KB clear head; a target beyond it makes the decoder block waiting for the
    /// body attach that is the very next op in the pump, BEHIND the seek. Nothing completes; the bar buffers forever.
    /// A launch restore at a saved position and a video→audio swap mid-track both take this path.</summary>
    public static class SeekGate
    {
        /// <param name="hasSession">An engine session is open (a deferred-open load has none until its body attaches).</param>
        /// <param name="sourceCanServeBeyondHead">The active byte source can read past its clear head: a Spotify stream
        /// with its body attached, or a source that never had a head/body split (local file, module stream).</param>
        public static SeekAdmission Decide(bool hasSession, bool sourceCanServeBeyondHead)
            => hasSession && sourceCanServeBeyondHead ? SeekAdmission.ApplyNow : SeekAdmission.Defer;

        /// <summary>The position the host should REPORT while a seek is parked: the parked TARGET. The session's own
        /// clock still reads the pre-seek position (0 for a fresh load), and publishing that would show 0:00 for a
        /// track the user resumed at 3:45. A negative <paramref name="pendingSeekMs"/> means nothing is parked.</summary>
        public static long ReportedPositionMs(long pendingSeekMs, long clockPositionMs)
            => pendingSeekMs >= 0 ? pendingSeekMs : clockPositionMs;
    }

    /// <summary>The buffering-bar-on-a-paused-restored-track fix. A launch-recovery restore loads the current track
    /// PAUSED so the bar can show it at its saved position; the host announced Prebuffering/Buffering anyway while
    /// attaching the head and body — work it does whether or not anyone asked to HEAR it — and with no Play() ever
    /// called the ticker that retires the flag never started. The indeterminate bar latched until the user pressed
    /// play.</summary>
    public static class PlayIntentGate
    {
        /// <summary>A load nobody asked to hear announces nothing: buffering while attaching is expected work, not a
        /// state the UI needs to show. Once there IS play intent every buffering signal is real.</summary>
        public static bool ShouldAnnounceBuffering(bool playIntent) => playIntent;
    }

    /// <summary>Frame-domain arithmetic for the gapless join, in ONE place. A session's sample clock counts frames
    /// consumed since THAT session was built (a seek rebases the position clock, never the sample clock; a
    /// device-format soft reload builds a NEW session at clock 0 and seeks it to the saved playhead). Every writer of
    /// the active track's natural-end frame therefore expresses it as "clock now + frames still to play", never as
    /// "frames from track start" — computing it track-absolute once at open is what scheduled a mid-track reopen's
    /// join hundreds of seconds into the future.</summary>
    public static class GaplessJoinClock
    {
        public static long MsToFrames(long ms, int rate) => ms * rate / 1000L;

        /// <summary>The active track's natural-end frame, on the session clock, given where the playhead is now.</summary>
        public static long JoinFrameFor(long sampleClockNow, long durationMs, long playheadMs, int rate)
            => sampleClockNow + MsToFrames(Math.Max(0L, durationMs - playheadMs), rate);

        /// <summary>Where to start the next voice: never in the past, and never further out than the active track's own
        /// remaining time + 100 ms — a stale estimate degrades to a ≤100 ms butt-join instead of a 164 s stall.</summary>
        public static long ScheduleJoin(long activeJoinFrame, long sampleClockNow, long remainingMs, int rate)
        {
            long join = Math.Max(activeJoinFrame, sampleClockNow);
            long bound = sampleClockNow + MsToFrames(Math.Max(0L, remainingMs), rate) + rate / 10;
            return Math.Min(join, bound);
        }

        /// <summary>A primed voice is only spliceable into a mixer running at the rate it was resampled for.</summary>
        public static bool PrimedSlotMatches(int primedMixRate, int sessionRate) => primedMixRate == sessionRate;

        /// <summary>May the join commit right now? Never into a session a soft reload may replace, and never while the
        /// reported playhead is a stale 0 — with a stale playhead "duration − position" reads as "the whole track
        /// remains" and the join is scheduled off a clock that is not this track's.</summary>
        public static bool CanCommit(bool clockStale, bool softReloading) => !clockStale && !softReloading;
    }

    /// <summary>What the host does with the live session after the engine swapped the output sink underneath it.</summary>
    public enum DeviceRecoveryAction : byte
    {
        /// <summary>Rate changed, body reopenable: build a NEW session/graph at the live rate, restore the playhead.</summary>
        ReopenNewGraph,
        /// <summary>Same rate, reopenable body: the existing graph can still render on the new sink.</summary>
        AdoptIntoExistingGraph,
        /// <summary>Same rate, body not reopenable: keep the session and make sure it is audible (a swap can leave the
        /// transport parked).</summary>
        KeepSession,
        /// <summary>Rate changed, body not reopenable: the session stays SILENT and the host cannot rebuild in place —
        /// surface an honest fault so the reducer records it and the bar offers Retry.</summary>
        ReloadThroughController,
    }

    /// <summary>The pure decision behind a device-format recovery. A rate-changed sink rebuild latches "requires graph
    /// rebuild" one-way — the mixer/decoder graph bound at prepare time cannot render on the new device and the
    /// session stays SILENT until a new graph exists — so "leave the old session playing" is only a benign no-op for a
    /// SAME-rate swap. Every early exit of the soft reload routes through here, so a rate change ends in an audible
    /// session or a Retry, never in silence until the next track.</summary>
    public static class DeviceRecoveryPlan
    {
        public static DeviceRecoveryAction Decide(bool requiresGraphRebuild, bool canReopen)
            => canReopen
                ? (requiresGraphRebuild ? DeviceRecoveryAction.ReopenNewGraph : DeviceRecoveryAction.AdoptIntoExistingGraph)
                : requiresGraphRebuild ? DeviceRecoveryAction.ReloadThroughController : DeviceRecoveryAction.KeepSession;
    }

    /// <summary>How an OS audio endpoint is NAMED in the picker. Pure strings, so the rule is pinned by a test rather
    /// than by whatever hardware the developer happens to have plugged in (ch 20 §9 asks for it here; the enumeration
    /// service itself stays SHELL, in owner H's <c>Playback.Audio.cs</c>).</summary>
    public static class AudioDeviceNaming
    {
        /// <summary>The short label: the endpoint's own description first, else the friendly name with its trailing
        /// " (adapter)" parenthetical stripped, else the raw name. Null / empty in, null out.</summary>
        public static string? Shorten(string? deviceDesc, string? friendlyName)
        {
            if (!string.IsNullOrWhiteSpace(deviceDesc)) return deviceDesc.Trim();
            if (string.IsNullOrWhiteSpace(friendlyName)) return friendlyName;
            string name = friendlyName.Trim();
            int open = name.LastIndexOf(" (", StringComparison.Ordinal);
            return open > 0 && name.EndsWith(')') ? name[..open] : name;
        }
    }
}
