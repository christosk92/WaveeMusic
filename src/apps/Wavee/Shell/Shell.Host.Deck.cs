// ── Shell/Shell.Host.Deck.cs ─────────────────────────────────────────────────────────────────────────────────────────
// the play log and the session document meet playback: PlayStarted → play-log.json, PersistDeck → session.json's deck,
// the launch restore and the exit capture (gap batch B3b)
//
// Role: SHELL
// Owner: I
// Wave: gap batch B3b
// Budget: 260 lines
// Spec: gap register G-078 (launch restore), G-079 (the play log's caller), G-217 (the session document's playback
//       section) — a named partial of Shell.Host.cs, declared when the composition root's second half passed 30 % over
//       its 1,000-line budget
//
// THE THREE SEAMS B3 LEFT, INSTALLED ONCE BY `Shell.Run` (after `Session.Load` and `PlayLog.Load`, before the loop):
//
//   Playback.PlayStarted(track, context, unixMs) ─▶ RecordPlay ─▶ PlayLog.Append (+ the row's album and billed artists,
//                                                   whatever the catalog already holds — the recency sidecar's stamps)
//   Playback.PersistDeck(point)                  ─▶ DeckDto (+ the live queue's rows) ─▶ SessionDeck.ToDto
//                                                   ─▶ Session.CaptureDeck (equality-gated on the SHAPE; the
//                                                   document's own 2 s debounce is the write's)
//   session.json's deck                          ─▶ SessionDeck.FromDto ─▶ Playback.Restore   (paused, never claiming;
//                                                   the rows laid back directly when the document carries them)
//   the exit tail                                ─▶ Playback.RestorePointNow ─▶ DeckDto ─▶ Session.CaptureDeck, then
//                                                   Session.Flush
//
// THE QUEUE TRAVELS WITH THE DECK. A restore that only knew the current track and its context re-resolved the context
// to rebuild the queue — and an autoplay row, a hand-queued row, anything whose context had run out came back alone
// ("when I restart the app the queue is also gone", 2026-09-16). The document now carries the rows themselves, capped
// at `SessionDeck.MaxRows` around the deck (`SelectRows`), and `Playback.Restore` lays them back before any seed.
//
// `--fake` neither restores nor persists the deck: its catalog is a seed with no network behind it, so a real session's
// row would not play there, and a seeded row must not come back as the next real launch's deck. The play log records
// under `--fake` exactly as History does.
//
// Every DECISION about the document's shape is `SessionDeck`'s, which is pure and unit-tested (ShellDeckTests).

using System.Buffers;

using RepeatMode = Wavee.Spotify.Decode.RepeatMode;

namespace Wavee;

public static partial class Shell
{
    /// <summary>How many billed artists one play stamps in the recency sidecar. A track rarely bills more; the cap keeps
    /// the stamp buffer on the stack.</summary>
    public const int PlayLogArtists = 8;

    /// <summary>Install the play log and the deck document into playback, then put the persisted deck back. UI thread,
    /// once, before the first frame (the restore's posts wait for the root's poster).</summary>
    static void AttachPlayback()
    {
        Playback.PlayStarted = static (track, context, unixMs) => RecordPlay(track, context, unixMs);
        if (Platform.Args.Fake) return;
        Playback.RestorePoint restore = SessionDeck.FromDto(Session.DeckSection);
        // No seed is needed for the first EMPTY point (a takeover, a sign-out) to clear the section: `CaptureDeck` gates
        // on the document's shape, and null over a present section is a change.
        Playback.PersistDeck = static point => OnPersistDeck(point);
        if (restore.IsEmpty) return;
        Playback.Restore(in restore);
        Log.Info("session", "deck restored paused at " + restore.PositionMs + " ms (cursor " + restore.CursorIndex
            + ", rows " + (restore.Rows?.Length ?? 0) + ")");
    }

    /// <summary><c>Playback.PersistDeck</c>: UI thread, on every snapshot-worthy transition. The ONE equality gate is
    /// <c>Session.CaptureDeck</c>'s, on the produced shape (rows included): a point carrying a rows array cannot be
    /// compared by record equality. The session document's own debounce coalesces the writes.</summary>
    static void OnPersistDeck(Playback.RestorePoint point) => Session.CaptureDeck(DeckDto(in point));

    /// <summary>The exit tail's last read of the deck, before the session document's flush writes it. Fail-soft: a fault
    /// here must never cost the flush that follows.</summary>
    static void CaptureDeckForExit()
    {
        if (Platform.Args.Fake || Playback.PersistDeck is null) return;
        try { Session.CaptureDeck(DeckDto(Playback.RestorePointNow())); }
        catch (Exception ex) { Log.Warn("session", "the deck could not be captured at exit; the last debounced one stands", ex); }
    }

    /// <summary>The section for a point PLUS the live queue's rows (G-078). UI THREAD — the queue's spans are the
    /// session-bound table's. The rows' identities are read into a pooled buffer once, so the pure shape rule never
    /// touches a table; an empty queue or a catalog that has not booted writes the deck alone.</summary>
    static SessionDeckDto? DeckDto(in Playback.RestorePoint point)
    {
        if (point.IsEmpty || !point.Track.IsPlayable) return null;
        ReadOnlySpan<int> packed = Entities.Current is null ? default : Queue.PackedRefs;
        ReadOnlySpan<QueueEdge> rows = Entities.Current is null ? default : Queue.Rows;
        int n = Math.Min(packed.Length, rows.Length);
        if (n == 0) return SessionDeck.ToDto(in point);
        EntityId[] ids = ArrayPool<EntityId>.Shared.Rent(n);
        try
        {
            for (int i = 0; i < n; i++) ids[i] = Queue.Unpack(packed[i]).Id;
            ReadOnlySpan<EntityId> idSpan = ids.AsSpan(0, n);
            int deck = SessionDeck.DeckIndex(idSpan, rows[..n], point.Track, point.CursorIndex);
            return SessionDeck.ToDto(in point, idSpan, rows[..n], deck);
        }
        finally { ArrayPool<EntityId>.Shared.Return(ids); }
    }

    /// <summary>A row's audio began (G-079): one play-log entry, stamping its album and billed artists in the recency
    /// sidecar when the catalog already knows them. UI THREAD — playback's drain runs the play report. A row whose
    /// metadata has not landed records the track and context alone.</summary>
    public static void RecordPlay(EntityId track, EntityId context, long unixMs)
    {
        if (!track.IsPlayable) return;
        EntityUri album = default;
        Span<EntityUri> artists = stackalloc EntityUri[PlayLogArtists];
        int count = 0;
        if (track.Kind == EntityKind.Track && Entities.Current is not null)
        {
            Track row = Entities.Track(track);
            album = row.Album.Uri;
            ReadOnlySpan<int> slots = row.ArtistSlots;
            for (int i = 0; i < slots.Length && count < artists.Length; i++)
                if (slots[i] > 0) artists[count++] = new Artist(slots[i]).Uri;
        }
        PlayLog.Append(new EntityUri(track), new EntityUri(context), unixMs, album: album, artists: artists[..count]);
    }

    /// <summary>The deck section's shape, both ways. PURE — no table, no clock, no interner for a catalog uri.</summary>
    public static class SessionDeck
    {
        /// <summary>The most queue rows the document carries. A 10,000-row context is the context's to re-page; what
        /// comes back on a restart is what the panel would show around the deck.</summary>
        public const int MaxRows = 200;
        /// <summary>How many already-played rows immediately before the deck the document keeps, so Previous has
        /// somewhere to go without the whole history riding along.</summary>
        public const int HistoryKeep = 10;

        /// <summary>A restore point → the section, or null (clear it) for an empty point. The deck alone, no rows.</summary>
        public static SessionDeckDto? ToDto(in Playback.RestorePoint point) => ToDto(in point, default, default, -1);

        /// <summary>A restore point plus the live queue → the section. <paramref name="ids"/> and <paramref name="rows"/>
        /// are parallel and in reading order; <paramref name="deck"/> is the deck's index in them (<see cref="DeckIndex"/>)
        /// or -1. The rows written are <see cref="SelectRows"/>'s window in the same order, a row whose id does not play
        /// skipped, and the section's cursor re-based onto the rows it carries (-1 when the deck is not among them).
        /// No rows → the deck alone, exactly as before rows were persisted.</summary>
        public static SessionDeckDto? ToDto(in Playback.RestorePoint point, ReadOnlySpan<EntityId> ids,
            ReadOnlySpan<QueueEdge> rows, int deck)
        {
            if (point.IsEmpty || !point.Track.IsPlayable) return null;
            var dto = new SessionDeckDto
            {
                Track = point.Track.Text,
                Context = point.Context.IsEmpty ? null : point.Context.Text,
                CursorIndex = Math.Max(-1, point.CursorIndex),
                PositionMs = Math.Max(0, point.PositionMs),
                DurationMs = Math.Max(0, point.DurationMs),
                Shuffle = point.Shuffle,
                Repeat = (int)point.Repeat,
            };
            int n = Math.Min(ids.Length, rows.Length);
            if (n == 0) return dto;
            SelectRows(rows[..n], deck, MaxRows, out int first, out int count);
            int playable = 0;
            for (int i = first; i < first + count; i++) if (ids[i].IsPlayable) playable++;
            if (playable == 0) return dto;
            var written = new SessionQueueRowDto[playable];
            int w = 0, cursor = -1;
            for (int i = first; i < first + count; i++)
            {
                if (!ids[i].IsPlayable) continue;
                if (i == deck) cursor = w;
                written[w++] = new SessionQueueRowDto
                {
                    Uri = ids[i].Text, Bucket = rows[i].Bucket, Provider = rows[i].Provider, ItemId = rows[i].ItemId,
                };
            }
            dto.Rows = written;
            dto.CursorIndex = cursor;
            return dto;
        }

        /// <summary>The window of a queue the document keeps: at most <see cref="HistoryKeep"/> History rows immediately
        /// before the deck, the deck, then everything after it (every user-queue row first, by the bucket order) until
        /// <paramref name="cap"/> rows. A deck index that is not a row starts the window at the head.</summary>
        public static void SelectRows(ReadOnlySpan<QueueEdge> rows, int deck, int cap, out int first, out int count)
        {
            first = 0;
            count = 0;
            if (rows.Length == 0 || cap <= 0) return;
            if ((uint)deck < (uint)rows.Length)
            {
                first = deck;
                while (first > 0 && deck - first < HistoryKeep && rows[first - 1].Bucket == (byte)QueueBucket.History) first--;
            }
            count = Math.Min(rows.Length - first, cap);
        }

        /// <summary>Which row is the deck: the cursor when it names <paramref name="track"/> on the now-playing bucket,
        /// else the now-playing row that names it, else the cursor when it at least names the track, else -1.</summary>
        public static int DeckIndex(ReadOnlySpan<EntityId> ids, ReadOnlySpan<QueueEdge> rows, EntityId track, int cursor)
        {
            int n = Math.Min(ids.Length, rows.Length);
            bool cursorNames = (uint)cursor < (uint)n && ids[cursor].Equals(track);
            if (cursorNames && rows[cursor].Bucket == (byte)QueueBucket.NowPlaying) return cursor;
            for (int i = 0; i < n; i++)
                if (rows[i].Bucket == (byte)QueueBucket.NowPlaying && ids[i].Equals(track)) return i;
            return cursorNames ? cursor : -1;
        }

        /// <summary>The section → a restore point; empty for a missing section or one that names nothing playable. A
        /// position past a known duration is pulled back to it, and a repeat ordinal this build does not know is off.
        /// The rows come back in order with a garbled one dropped (and the cursor re-based past it); a document
        /// without rows restores with <c>Rows = null</c>, the context seed's case.</summary>
        public static Playback.RestorePoint FromDto(SessionDeckDto? dto)
        {
            if (dto is null || !TryUri(dto.Track, out EntityId track) || !track.IsPlayable) return default;
            EntityId context = TryUri(dto.Context, out EntityId parsed) ? parsed : default;
            int duration = Math.Max(0, dto.DurationMs);
            int position = Math.Max(0, dto.PositionMs);
            if (duration > 0 && position > duration) position = duration;
            RepeatMode repeat = dto.Repeat is >= (int)RepeatMode.Off and <= (int)RepeatMode.Track ? (RepeatMode)dto.Repeat : RepeatMode.Off;
            int cursor = Math.Max(-1, dto.CursorIndex);
            Playback.RestoreRow[]? rows = RowsOf(dto.Rows, ref cursor);
            return new Playback.RestorePoint(track, context, cursor, position, duration, dto.Shuffle, repeat, rows);
        }

        /// <summary>The persisted rows → identities and edges, in order, capped at <see cref="MaxRows"/>. A row whose
        /// uri is garbled, does not play, or names a bucket this build does not know is dropped, and
        /// <paramref name="cursor"/> moves with the rows that remain (-1 when the cursor's own row was dropped).</summary>
        static Playback.RestoreRow[]? RowsOf(SessionQueueRowDto[]? rows, ref int cursor)
        {
            if (rows is null || rows.Length == 0) return null;
            int n = Math.Min(rows.Length, MaxRows);
            int kept = 0;
            for (int i = 0; i < n; i++) if (TryRow(rows[i], out _)) kept++;
            if (kept == 0) { cursor = -1; return null; }
            var result = new Playback.RestoreRow[kept];
            int w = 0, landed = -1;
            for (int i = 0; i < n; i++)
            {
                if (!TryRow(rows[i], out Playback.RestoreRow row)) continue;
                if (i == cursor) landed = w;
                result[w++] = row;
            }
            cursor = landed;
            return result;
        }

        static bool TryRow(SessionQueueRowDto? dto, out Playback.RestoreRow row)
        {
            row = default;
            if (dto is null || !TryUri(dto.Uri, out EntityId id) || !id.IsPlayable) return false;
            if (dto.Bucket is < (int)QueueBucket.NowPlaying or > (int)QueueBucket.History) return false;
            int provider = dto.Provider is >= (int)QueueProvider.Context and <= (int)QueueProvider.Autoplay
                ? dto.Provider : (int)QueueProvider.Context;
            row = new Playback.RestoreRow(id, new QueueEdge(dto.ItemId, (byte)provider, (byte)dto.Bucket));
            return true;
        }

        /// <summary>Would writing <paramref name="next"/> over <paramref name="current"/> change the document? The rows
        /// count: a queued, removed or reordered row is a change even when the deck stood still.</summary>
        public static bool Same(SessionDeckDto? current, SessionDeckDto? next)
        {
            if (current is null || next is null) return current is null && next is null;
            return string.Equals(current.Track, next.Track, StringComparison.Ordinal)
                && string.Equals(current.Context, next.Context, StringComparison.Ordinal)
                && current.CursorIndex == next.CursorIndex && current.PositionMs == next.PositionMs
                && current.DurationMs == next.DurationMs && current.Shuffle == next.Shuffle && current.Repeat == next.Repeat
                && SameRows(current.Rows, next.Rows);
        }

        static bool SameRows(SessionQueueRowDto[]? a, SessionQueueRowDto[]? b)
        {
            int na = a?.Length ?? 0, nb = b?.Length ?? 0;
            if (na != nb) return false;
            for (int i = 0; i < na; i++)
            {
                SessionQueueRowDto? x = a![i], y = b![i];
                if (x is null || y is null) { if ((x is null) != (y is null)) return false; continue; }
                if (!string.Equals(x.Uri, y.Uri, StringComparison.Ordinal)
                    || x.Bucket != y.Bucket || x.Provider != y.Provider || x.ItemId != y.ItemId) return false;
            }
            return true;
        }

        /// <summary>A persisted uri → its identity. A uri no provider owns, and a catalog-kind <c>spotify:</c> uri that is
        /// not a valid gid, are refused BEFORE any parse that interns, so a garbled document never becomes a row. Only an
        /// id that IS text (Liked Songs, a local file, a module track) takes the interning parse — at boot, UI thread.</summary>
        static bool TryUri(string? text, out EntityId id)
        {
            id = default;
            if (string.IsNullOrEmpty(text)) return false;
            EntityProvider provider = EntityUri.ProviderOf(text.AsSpan(), out EntityKind kind);
            if (provider == EntityProvider.None) return false;
            if (provider == EntityProvider.Spotify && EntityId.IsGidKind(kind)) return EntityId.TryParseGid(text.AsSpan(), out id);
            return EntityId.TryParse(text.AsSpan(), out id);
        }
    }
}
