// ── Playback/Playback.Dj.Narration.cs ────────────────────────────────────────────────────────────────────────────────
// AI DJ narration as playable queue items (plan-dj D2): the welcome, intro, jump and outro clips become rows that play in
// sequence with the music, each through `SourceRoute.Tts` (Spotify.DjTts).
//
// Role: SHELL (UI thread: the queue writes, the tables, the prefetch); `NarrationSkipTarget` and `PlainText` are PURE over what
// they are handed
// Spec: docs/plans/wavee/dj-implementation.md §1.4 (the prefetch rule), §1.5 (the items and the queue rules), §1.8 (the controls),
// §2.2 (the narration item), §2.3 (the prefetch scheduler)
//
// THE ROWS. A narration is a queue row of provider `QueueProvider.Narration` whose playable identity is its clip's
// (`wavee:tts:<key>`, `DjTts.IdOf`; the key is the fulfil body's hash, so it is known before anything is fetched). Its track row is
// staged here once, at Authority.Full with its identity, availability and video groups stated (the narration's title, artist and
// image, no artists edge, no album, no video), so nothing in the catalogue asks Spotify for it; its EntityProvider is Tts, which no
// fetch transport answers for. One `Spoken` per (slot, the track row it belongs to) keeps what the row is: the slot, the track, the
// display keys, the SSML, the clip key, whether the clip was asked for, its fate.
//
//   welcome  belongs to the first segment's first track  (wire: spotify:media:0000000000000000000000, narration/welcome, uid list_intro_item)
//   intro    belongs to its segment's first track        (spotify:media:<that track's id>, narration/intro)
//   jump     belongs to its segment's first track        (narration/jump: prefetched, never queued here; "Let DJ pick" is the next lane's)
//   outro    belongs to its segment's last track         (spotify:media:<that track's id>, narration/outro)
//
// THE PLANNER (`PlanNarration`, once per drain, before the queue follow; a no-op while neither the deck nor the queue moved). Only
// the NEAREST boundary carries items: from the deck's segment (a narration's is its track's), the outro right after the segment's
// last track and the next segment's INTRO right before its first track (the jump clip is only for "Let DJ pick"). A start puts the
// welcome on the deck before the first track (`StartContext` → `NarrationAtStart`), and the planner then lays the first intro
// between them. A narration the deck has passed is REMOVED, never history; a clip that failed is dropped from the plan. The deck's
// row never moves: rows before it shift only when a passed narration leaves, and the cursor follows (`Input.Recursor`).
//
// THE PREFETCH (§1.4). The welcome and the first intro are ensured at once on resolve; afterwards, about 5 s after each item starts,
// every narration still unasked up to and including the next boundary (every narration row ahead: only that boundary has any)
// and the next segment's jump clip, so it is on disk before the button is pressed. A narration the deck reaches unasked is asked
// at once. The TTS client logs a dropped clip itself (`[dj] narration dropped why=`); this side only acts on it.

using System.Globalization;
using System.Text;

using FluentGpu.Signals;

namespace Wavee;

public static partial class Playback
{
    /// <summary>An AI DJ narration is the current item (<see cref="State.Narrating"/>): the surfaces suppress its progress (no seek handle,
    /// no times) and refuse what the reducer refuses. Published once per drain.</summary>
    public static readonly Signal<bool> Narrating = new(false) { DebugName = "playback.narrating" };

    /// <summary>The reducer's tail for Next / Previous during a narration (<see cref="Dj.NarrationSkipTarget"/>): the deck lands on the
    /// row at <paramref name="at"/> from its start, as any advance lands.</summary>
    static void LandPastNarration(ref State s, in Input i, ref Effects fx, int at, PlayReason why)
    {
        QueueCursor cursor = Queue.CursorOf(at);
        EntityRef row = Queue.RefAt(at);
        ReportEnd(ref s, ref fx, why, s.Position(i.NowMs));
        PutOnDeck(ref s, in i, row, row.Id, cursor, DeckKind(in s, row), 0, paused: false);
        s.StartReason = why;
        EmitLoad(ref s, ref fx, LoadOrigin.Advance, paused: false, explicitPosition: true);
        fx.Snapshot = true;
    }

    public static partial class Dj
    {
        /// <summary>Which narration a row is (plan-dj §1.5).</summary>
        public enum Slot : byte { Welcome, Intro, Jump, Outro }

        /// <summary>The welcome's wire uri (the official player's; every other narration is <c>spotify:media:&lt;the track's id&gt;</c>).</summary>
        public const string WelcomeMediaUri = "spotify:media:0000000000000000000000";

        /// <summary>The welcome row's uid on the wire.</summary>
        public const string WelcomeUid = "list_intro_item";

        /// <summary>How long after an item starts the next boundary's narrations are asked for (§1.4: "about 5 s").</summary>
        public const int PrefetchDelayMs = 5_000;

        /// <summary>One narration: the row it is (or would be), the track it belongs to, what it says and shows, its clip.</summary>
        public sealed class Spoken
        {
            public Slot Slot;
            /// <summary>Its queue row's item id (a jump clip is prefetched, never queued here: still minted, never in the queue).</summary>
            public ulong ItemId;
            /// <summary>The track row it belongs to (the welcome: the first segment's first track).</summary>
            public ulong BelongsTo;
            /// <summary>That track's identity (what Previous restarts; the media uri's id).</summary>
            public EntityId Track;
            public string Segment = "";
            public int Page;
            /// <summary>The narration keys (<c>narration.&lt;slot&gt;.*</c>) by suffix: <c>ssml</c>, <c>title</c>, <c>artist</c>,
            /// <c>album_title</c>, <c>image</c>, <c>decision_id</c>, … (what Connect publishes as the item's metadata).</summary>
            public Dictionary<string, string> Keys = new(StringComparer.Ordinal);
            public Spotify.DjTts.Narration Request;
            /// <summary>The clip's key (the fulfil body's hash) and its playable identity <c>wavee:tts:&lt;key&gt;</c>.</summary>
            public string Key = "";
            public EntityId Id;
            /// <summary>The packed queue target of <see cref="Id"/>'s track row.</summary>
            public int Packed;
            /// <summary>The clip was asked for (<see cref="Spotify.DjTts.Ensure(in Spotify.DjTts.Narration)"/>), when (frame ms), and why.</summary>
            public bool Ensured;
            public long EnsuredAtMs = -1;
            public string EnsuredWhy = "";
            /// <summary>The clip failed: the row is dropped and never planned again.</summary>
            public bool Dropped;
            /// <summary>The clip's length once its body ended (−1 before).</summary>
            public long DurationMs = -1;

            public string Ssml => Keys.TryGetValue("ssml", out string? v) ? v : "";
            public string Title => Keys.TryGetValue("title", out string? v) ? v : "";
            public string Artist => Keys.TryGetValue("artist", out string? v) ? v : "";
            public string AlbumTitle => Keys.TryGetValue("album_title", out string? v) ? v : "";
            public string Image => Keys.TryGetValue("image", out string? v) ? v : "";
            /// <summary>The wire's provider (<c>narration/&lt;slot&gt;</c>).</summary>
            public string Provider => "narration/" + SlotName(Slot);
            /// <summary>The wire's item uri: the welcome's fixed one, else <c>spotify:media:</c> + the track's id (§1.5).</summary>
            public string MediaUri => Slot == Slot.Welcome ? WelcomeMediaUri
                : "spotify:media:" + (Track.IsEmpty ? "" : EntityUri.IdOf(Track.Text).ToString());
            /// <summary>The spoken words as plain text: the SSML's tags gone, an <c>&lt;entity&gt;</c>'s name kept (the caption lane's).</summary>
            public string Caption => PlainText(Ssml);
        }

        public static string SlotName(Slot slot) => slot switch
        {
            Slot.Welcome => "welcome",
            Slot.Intro => "intro",
            Slot.Jump => "jump",
            _ => "outro",
        };

        // ── the narration book (UI thread) ────────────────────────────────────────────────────────────────────────

        static readonly Dictionary<(Slot, ulong), Spoken> s_spoken = new();
        static readonly Dictionary<ulong, Spoken> s_rowSpoken = new();
        static EntityId s_narrContext;
        static bool s_settledHooked;

        /// <summary>The narration a queue row is, or null (a track, a queued row, no row).</summary>
        public static Spoken? NarrationOf(ulong itemId) => itemId != 0 && s_rowSpoken.TryGetValue(itemId, out Spoken? sp) ? sp : null;

        /// <summary>Is the queue row <paramref name="itemId"/> a narration?</summary>
        public static bool IsNarration(ulong itemId) => itemId != 0 && s_rowSpoken.ContainsKey(itemId);

        /// <summary>The DJ book's row a queue row belongs to: a DJ track's own, a narration's track's (a narration belongs to its segment),
        /// else null. What the lookahead, the refill and the jump read so they see through narration rows.</summary>
        public static Row? SegmentRowOf(ulong itemId)
            => RowOf(itemId) ?? (NarrationOf(itemId) is { } sp ? RowOf(sp.BelongsTo) : null);

        /// <summary>Every narration the planner made this session (queued, passed, prefetched or dropped).</summary>
        public static IEnumerable<Spoken> AllSpoken => s_spoken.Values;

        /// <summary>The spoken words of the narration on the deck, as plain text ("" while a track plays): the caption lane's seam.</summary>
        public static readonly Signal<string> Caption = new("") { DebugName = "dj.caption" };

        /// <summary>The playable identity is a narration clip's (<see cref="EntityProvider.Tts"/>)?</summary>
        public static bool IsNarrationId(EntityId id) => id.Provider == EntityProvider.Tts;

        /// <summary>The track row <paramref name="row"/> is a narration clip's (UI reads: the credits line, the heart, the drag).</summary>
        public static bool IsNarrationRow(EntityRef row)
            => row.Kind == EntityKind.Track && Entities.Current is { } scope && (uint)row.Slot < (uint)scope.Tracks.Count
               && scope.Tracks.Id[row.Slot].Provider == EntityProvider.Tts;

        /// <summary>Forget the session's narrations (another context plays).</summary>
        internal static void ForgetNarration()
        {
            s_spoken.Clear();
            s_rowSpoken.Clear();
            s_narrContext = default;
            s_deckItem = 0;
            s_nextJump = 0;
            s_prefetchLog.Clear();
            s_timeline.Clear();
            Caption.Value = "";
        }

        /// <summary>Where Next and Previous land while the narration at <paramref name="cursor"/> is current (§1.8, inferred from
        /// <c>skipping_*_past_track=resume</c>): Next on the first playable track row after it (a clip that plays alone: talk-over, D3, will
        /// skip its target too); Previous on the track it belongs to, from its start. −1: the plain walk. Reads the live tables.</summary>
        public static int NarrationSkipTarget(ReadOnlySpan<QueueEdge> rows, int cursor, bool forward)
        {
            if ((uint)cursor >= (uint)rows.Length || NarrationOf(rows[cursor].ItemId) is not { } sp) return -1;
            if (!forward) return Queue.IndexOfItem(rows, sp.BelongsTo);
            for (int i = Queue.NextIndex(rows, cursor); i >= 0; i = Queue.NextIndex(rows, i))
                if (rows[i].Provider != (byte)QueueProvider.Narration && RowPlayable(Queue.RefAt(i))) return i;
            return -1;
        }

        // ── the keys → a narration ────────────────────────────────────────────────────────────────────────────────

        /// <summary>The narration a slot's keys name, or null when they carry no SSML.</summary>
        static Spoken? Make(Slot slot, ulong belongsTo, IReadOnlyDictionary<string, string> keys, Row? row)
        {
            if (!keys.TryGetValue("ssml", out string? ssml) || ssml.Length == 0) return null;
            var sp = new Spoken { Slot = slot, BelongsTo = belongsTo, Segment = row?.Segment ?? "", Page = row?.Page ?? 0 };
            foreach (KeyValuePair<string, string> k in keys) sp.Keys[k.Key] = k.Value;
            sp.Request = new Spotify.DjTts.Narration(ssml,
                Voice: keys.TryGetValue("voice", out string? voice) && voice.Length > 0 ? voice : "VOICE1",
                Provider: keys.TryGetValue("tts_provider", out string? provider) && provider.Length > 0 ? provider : "SONANTIC_FAST",
                SampleRate: keys.TryGetValue("sample_rate", out string? rate) && int.TryParse(rate, NumberStyles.Integer, CultureInfo.InvariantCulture, out int r) && r > 0 ? r : 44_100,
                LoudnessLufs: Float(keys, "loudness", Spotify.DjTts.DefaultLoudnessLufs),
                TruePeakDb: Float(keys, "true_peak", Spotify.DjTts.DefaultTruePeakDb));
            sp.Key = Spotify.DjTts.KeyOf(Spotify.DjTts.FulfilBody(in sp.Request));
            sp.Id = Spotify.DjTts.IdOf(sp.Key);
            sp.ItemId = slot == Slot.Welcome ? s_uids.ItemIdOf(Encoding.UTF8.GetBytes(WelcomeUid)) : Queue.MintItemIds(1);
            if (sp.ItemId == 0) sp.ItemId = Queue.MintItemIds(1);
            StageRow(sp);
            EntityRef track = Entities.Ref(sp.Id);
            sp.Packed = Queue.Pack(track);
            return sp;
        }

        static float Float(IReadOnlyDictionary<string, string> keys, string key, float fallback)
            => keys.TryGetValue(key, out string? v) && float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out float f) && float.IsFinite(f) ? f : fallback;

        /// <summary>The narration of <paramref name="slot"/> belonging to the track row <paramref name="belongsTo"/>, made once.</summary>
        static Spoken? For(Slot slot, ulong belongsTo, EntityId track, IReadOnlyDictionary<string, string>? keys, Row? row)
        {
            if (belongsTo == 0 || keys is null) return null;
            if (s_spoken.TryGetValue((slot, belongsTo), out Spoken? held)) return held;
            if (Make(slot, belongsTo, keys, row) is not { } sp) return null;
            sp.Track = track;
            s_spoken[(slot, belongsTo)] = sp;
            if (slot != Slot.Jump) s_rowSpoken[sp.ItemId] = sp;
            return sp;
        }

        /// <summary>The clip's track row: its title, artist and image from the narration keys, its length once known, no artists edge, no
        /// album, no video, available. Staged at Authority.Full: nothing else answers for a <c>wavee:tts:</c> row.</summary>
        static void StageRow(Spoken sp)
        {
            if (Entities.Current is not { } scope) return;
            var staging = Staging.Rent();
            staging.Epoch = scope.Epoch;
            ref var row = ref staging.Tracks.RowFor(new StagedId(staging.AddText(Encoding.UTF8.GetBytes(sp.Id.Text))), Authority.Full,
                (uint)(TrackFields.Identity | TrackFields.Availability | TrackFields.Video));
            string title = sp.Title.Length > 0 ? sp.Title : sp.Slot == Slot.Welcome ? "Welcome" : "Up next";
            row.Title = staging.AddText(Encoding.UTF8.GetBytes(title));
            if (sp.Artist.Length > 0) row.ArtistLine = staging.AddText(Encoding.UTF8.GetBytes(sp.Artist));
            if (sp.Image.Length > 0) row.Image = staging.AddText(Encoding.UTF8.GetBytes(sp.Image));
            row.DurationMs = sp.DurationMs > 0 ? (int)Math.Min(sp.DurationMs, int.MaxValue) : 0;
            row.Flags = 0;
            Entities.Commit(staging);
            Staging.Return(staging);
            Entities.Publish();
        }

        // ── the start (StartContext) ──────────────────────────────────────────────────────────────────────────────

        /// <summary>A lexicon session starts on its first track (<paramref name="first"/>, its row item <paramref name="firstItem"/>): the
        /// session's narrations start over, the welcome is made and asked for at once with the first segment's intro (§1.4), and the welcome
        /// is answered for the caller to put on the deck before the first track (null: the session states no welcome).</summary>
        internal static Spoken? AtStart(EntityId context, Resolve resolve, ulong firstItem, EntityId first, long now)
        {
            ForgetNarration();
            s_narrContext = context;
            if (!s_settledHooked) { s_settledHooked = true; Spotify.DjTts.Settled += OnSettled; }
            var welcomeKeys = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, string> k in resolve.Facts.Metadata)
                if (k.Key.StartsWith("narration.welcome.", StringComparison.Ordinal)) welcomeKeys[k.Key["narration.welcome.".Length..]] = k.Value;
            Row? firstRow = RowOf(firstItem);
            Spoken? welcome = For(Slot.Welcome, firstItem, first, welcomeKeys, firstRow);
            if (welcome is not null) Ask(welcome, "resolve", now);
            if (firstRow?.Intro is { } intro && For(Slot.Intro, firstItem, first, intro.Keys, firstRow) is { } firstIntro) Ask(firstIntro, "resolve", now);
            return welcome;
        }

        // ── the plan (once per drain) ─────────────────────────────────────────────────────────────────────────────

        /// <summary>One narration the plan wants in the queue, emitted in front of the old row <see cref="Before"/> (the row count: at the end).</summary>
        internal readonly record struct Want(Spoken Spoken, int Before);

        /// <summary>The narrations the NEAREST boundary carries for a deck at <paramref name="deck"/> (§1.5): the deck's segment's outro
        /// right after its last track, the next segment's intro right before its first track, and (the deck is the welcome) the deck's
        /// own segment's intro before its first track. A dropped clip is never wanted. Writes the next segment's first row to
        /// <paramref name="nextFirst"/> (0: not loaded yet) for the jump prefetch.</summary>
        internal static int Wanted(ReadOnlySpan<QueueEdge> rows, int deck, Span<Want> into, out ulong nextFirst)
        {
            nextFirst = 0;
            if ((uint)deck >= (uint)rows.Length) return 0;
            ulong deckItem = rows[deck].ItemId;
            int scanFrom = deck, segmentFirst = -1;
            Row? deckRow;
            if (NarrationOf(deckItem) is { } sp)
            {
                int b = Queue.IndexOfItem(rows, sp.BelongsTo);
                deckRow = RowOf(sp.BelongsTo);
                if (b < 0 || deckRow is null) return 0;
                scanFrom = b;
                if (sp.Slot == Slot.Welcome) segmentFirst = b;
            }
            else deckRow = RowOf(deckItem);
            if (deckRow is null) return 0;
            string segment = deckRow.Segment;
            int last = -1, next = -1;
            for (int i = scanFrom; i < rows.Length; i++)
            {
                if (rows[i].Provider == (byte)QueueProvider.Narration || RowOf(rows[i].ItemId) is not { } r) continue;
                if (r.Segment == segment) last = i;
                else { next = i; break; }
            }
            int n = 0;
            if (segmentFirst >= 0 && RowOf(rows[segmentFirst].ItemId) is { Intro: { } intro } firstRow
                && For(Slot.Intro, rows[segmentFirst].ItemId, Queue.RefAt(segmentFirst).Id, intro.Keys, firstRow) is { Dropped: false } i0)
                into[n++] = new Want(i0, segmentFirst);
            if (last >= 0 && RowOf(rows[last].ItemId) is { Outro: { } outro } lastRow
                && For(Slot.Outro, rows[last].ItemId, Queue.RefAt(last).Id, outro.Keys, lastRow) is { Dropped: false } o)
            {
                int before = last + 1;
                while (before < rows.Length && (rows[before].Bucket == (byte)QueueBucket.UserQueue || rows[before].Provider == (byte)QueueProvider.Narration)) before++;
                into[n++] = new Want(o, before);
            }
            if (next >= 0 && RowOf(rows[next].ItemId) is { } nextRow)
            {
                nextFirst = rows[next].ItemId;
                if (nextRow.Intro is { } nextIntro
                    && For(Slot.Intro, rows[next].ItemId, Queue.RefAt(next).Id, nextIntro.Keys, nextRow) is { Dropped: false } i1)
                    into[n++] = new Want(i1, next);
            }
            return n;
        }

        // ── the prefetch and the timeline ─────────────────────────────────────────────────────────────────────────

        /// <summary>One prefetch decision, for the inspector: when (frame ms), why (<c>resolve</c>, <c>item+5s</c>, <c>reached</c>), what.</summary>
        public readonly record struct PrefetchEntry(long AtMs, string Why, Slot Slot, string Segment, string Key, string ClipBefore);

        /// <summary>One item on the deck: what it was, when it started and ended (frame ms; −1 while it plays), the length it had.</summary>
        public sealed class TimelineEntry
        {
            public string Item = "";
            public long StartMs, EndMs = -1, DurationMs;
            public ulong ItemId;
        }

        static readonly List<PrefetchEntry> s_prefetchLog = new();
        static readonly List<TimelineEntry> s_timeline = new();
        const int LogCap = 128;

        public static IReadOnlyList<PrefetchEntry> PrefetchLog => s_prefetchLog;
        public static IReadOnlyList<TimelineEntry> Timeline => s_timeline;

        static ulong s_deckItem, s_nextJump;
        static long s_itemStartMs = -1;
        static bool s_itemPrefetched;

        /// <summary>The next segment's first track (whose jump clip is prefetched), or 0.</summary>
        public static ulong NextJumpTarget => s_nextJump;

        static void Ask(Spoken sp, string why, long now)
        {
            if (sp.Ensured || sp.Dropped) return;
            string before = Spotify.DjTts.TryGet(sp.Key, out Spotify.DjTts.ClipInfo held) ? held.State.ToString() : "none";
            string key = Spotify.DjTts.Ensure(in sp.Request);
            sp.Ensured = true;
            sp.EnsuredAtMs = now;
            sp.EnsuredWhy = why;
            if (s_prefetchLog.Count >= LogCap) s_prefetchLog.RemoveAt(0);
            s_prefetchLog.Add(new PrefetchEntry(now, why, sp.Slot, sp.Segment, key, before));
            Log.Info("dj", "narration.prefetch why=" + why + " slot=" + SlotName(sp.Slot) + " segment=" + sp.Segment + " key=" + key + " clip=" + before);
        }

        /// <summary>The deck moved or time passed: the timeline, the caption, the prefetch rule (§1.4). UI thread, once per drain.</summary>
        internal static void Tick(ReadOnlySpan<QueueEdge> rows, int deck, Phase phase, int durationMs, long now)
        {
            ulong item = (uint)deck < (uint)rows.Length ? rows[deck].ItemId : 0;
            if (item != s_deckItem)
            {
                if (s_timeline.Count > 0 && s_timeline[^1].EndMs < 0) s_timeline[^1].EndMs = now;
                s_deckItem = item;
                s_itemStartMs = -1;
                s_itemPrefetched = false;
                Spoken? sp = NarrationOf(item);
                Caption.Value = sp?.Caption ?? "";
                if (item != 0)
                {
                    if (s_timeline.Count >= LogCap) s_timeline.RemoveAt(0);
                    s_timeline.Add(new TimelineEntry { Item = Label(rows[deck].ItemId, Queue.RefAt(deck)), StartMs = now, ItemId = item });
                }
                if (sp is not null) Ask(sp, "reached", now);
            }
            if (durationMs > 0 && s_timeline.Count > 0 && s_timeline[^1].ItemId == item) s_timeline[^1].DurationMs = durationMs;
            if (s_itemStartMs < 0 && phase == Phase.Playing) s_itemStartMs = now;
            if (s_itemPrefetched || s_itemStartMs < 0 || now - s_itemStartMs < PrefetchDelayMs) return;
            s_itemPrefetched = true;
            for (int i = deck + 1; i < rows.Length; i++)
                if (NarrationOf(rows[i].ItemId) is { } ahead) Ask(ahead, "item+5s", now);
            if (s_nextJump != 0 && RowOf(s_nextJump) is { Jump: { } jump } target
                && For(Slot.Jump, s_nextJump, TrackOfItem(rows, s_nextJump), jump.Keys, target) is { } jumpClip)
                Ask(jumpClip, "item+5s", now);
        }

        static EntityId TrackOfItem(ReadOnlySpan<QueueEdge> rows, ulong itemId)
        {
            int at = Queue.IndexOfItem(rows, itemId);
            return at < 0 ? default : Queue.RefAt(at).Id;
        }

        /// <summary>A row as the timeline names it: <c>welcome</c>, <c>intro:&lt;segment&gt;</c>, <c>outro:&lt;segment&gt;</c>, or the track's id.</summary>
        public static string Label(ulong itemId, EntityRef row)
        {
            if (NarrationOf(itemId) is { } sp) return sp.Slot == Slot.Welcome ? "welcome" : SlotName(sp.Slot) + ":" + sp.Segment;
            string id = row.Id.IsEmpty ? "?" : EntityUri.IdOf(row.Id.Text).ToString();
            return RowOf(itemId) is { } r ? id + "@" + r.Segment : id;
        }

        /// <summary>The planner's last next-segment first row (written by <see cref="PlanNarration"/>).</summary>
        internal static void NoteNextJump(ulong item) => s_nextJump = item;

        /// <summary>A clip settled (UI thread): Ready → its length is the row's and, on the deck, the reducer's (a streamed clip opened with no
        /// length); Failed → the row leaves the plan, and a clip on the deck is skipped (the music goes on). The drop itself is the TTS
        /// client's line.</summary>
        static void OnSettled(Spotify.DjTts.ClipInfo info)
        {
            foreach (Spoken sp in s_spoken.Values)
            {
                if (!string.Equals(sp.Key, info.Key, StringComparison.Ordinal)) continue;
                if (info.State == Spotify.DjTts.ClipState.Failed) sp.Dropped = true;
                else if (info.State == Spotify.DjTts.ClipState.Ready && info.DurationMs > 0 && sp.DurationMs != info.DurationMs)
                {
                    sp.DurationMs = info.DurationMs;
                    StageRow(sp);
                }
            }
            NarrationSettled(info);
        }

        // ── SSML → plain text (PURE) ──────────────────────────────────────────────────────────────────────────────

        /// <summary>The SSML's words: every tag gone (an <c>&lt;entity&gt;</c>'s name kept, it is the tag's text), the five XML escapes
        /// undone, white space folded to single spaces. PURE.</summary>
        public static string PlainText(string ssml)
        {
            if (string.IsNullOrEmpty(ssml)) return "";
            var sb = new StringBuilder(ssml.Length);
            bool inTag = false, space = false;
            for (int i = 0; i < ssml.Length; i++)
            {
                char c = ssml[i];
                if (inTag) { if (c == '>') inTag = false; continue; }
                if (c == '<') { inTag = true; continue; }
                if (c == '&')
                {
                    int end = ssml.IndexOf(';', i);
                    if (end > i && end - i <= 6)
                    {
                        string? decoded = ssml.AsSpan(i, end - i + 1) switch
                        {
                            "&amp;" => "&",
                            "&lt;" => "<",
                            "&gt;" => ">",
                            "&quot;" => "\"",
                            "&apos;" => "'",
                            _ => null,
                        };
                        if (decoded is not null) { Append(decoded); i = end; continue; }
                    }
                }
                if (char.IsWhiteSpace(c)) { space = sb.Length > 0; continue; }
                Append(c.ToString());
            }
            return sb.ToString();

            void Append(string text)
            {
                if (space) { sb.Append(' '); space = false; }
                sb.Append(text);
            }
        }
    }

    // ── the planner's host half (the drain) ───────────────────────────────────────────────────────────────────────────

    static readonly Dj.Want[] s_wanted = new Dj.Want[3];
    static uint s_narrPlanVersion;
    static int s_narrPlanDeck = -1;
    static Dj.Session? s_narrSession;

    /// <summary>The DJ planner (see the banner), once per drain before the queue follow. Rebuilds the narration rows when the deck or the
    /// queue moved; then the prefetch tick.</summary>
    static void PlanNarration(long now)
    {
        Dj.Session? session = Dj.Current;
        if (session is null)
        {
            if (s_narrSession is not null) { Dj.ForgetNarration(); s_narrSession = null; s_narrPlanDeck = -1; }
            return;
        }
        if (Entities.Current is null || !s_state.RoutesLocal || s_state.Cursor.IsNone || !s_state.Context.Equals(session.Context)) return;
        int deck = s_state.Cursor.Index;
        if (!FollowsQueue(in s_state, Queue.RefAt(deck))) return;
        if (!ReferenceEquals(session, s_narrSession)) { s_narrSession = session; s_narrPlanVersion = 0; s_narrPlanDeck = -1; }
        if (Queue.Version != s_narrPlanVersion || deck != s_narrPlanDeck) deck = RelayNarration(deck, now);
        Dj.Tick(Queue.Rows, deck, s_state.Phase, s_state.DurationMs, now);
    }

    /// <summary>Lay the wanted narrations around the deck (<see cref="Dj.Wanted"/>), dropping every other narration row but the deck's.
    /// Answers the deck's index afterwards.</summary>
    static int RelayNarration(int deck, long now)
    {
        ReadOnlySpan<QueueEdge> rows = Queue.Rows;
        ReadOnlySpan<int> packed = Queue.PackedRefs;
        int n = rows.Length;
        Dj.Want[] wanted = s_wanted;
        int w = Dj.Wanted(rows, deck, wanted, out ulong nextFirst);
        Dj.NoteNextJump(nextFirst);
        ulong deckItem = rows[deck].ItemId;
        int cap = n + w;
        int[] outPacked = System.Buffers.ArrayPool<int>.Shared.Rent(Math.Max(1, cap));
        QueueEdge[] outRows = System.Buffers.ArrayPool<QueueEdge>.Shared.Rent(Math.Max(1, cap));
        try
        {
            int m = 0, newDeck = deck, removedBeforeContext = 0, contextStart = Queue.ContextStart;
            for (int i = 0; i <= n; i++)
            {
                for (int k = 0; k < w; k++)
                {
                    if (wanted[k].Before != i || wanted[k].Spoken.ItemId == deckItem || wanted[k].Spoken.Packed == 0) continue;
                    outPacked[m] = wanted[k].Spoken.Packed;
                    outRows[m] = new QueueEdge(wanted[k].Spoken.ItemId, (byte)QueueProvider.Narration, (byte)QueueBucket.NextUp);
                    m++;
                }
                if (i == n) break;
                if (rows[i].Provider == (byte)QueueProvider.Narration && i != deck)
                {
                    if (i < contextStart) removedBeforeContext++;
                    continue;
                }
                if (i == deck) newDeck = m;
                outPacked[m] = packed[i];
                outRows[m] = rows[i];
                m++;
            }
            bool changed = m != n;
            for (int i = 0; i < m && !changed; i++) changed = outPacked[i] != packed[i] || outRows[i] != rows[i];
            if (changed)
            {
                Queue.Replace(outPacked.AsSpan(0, m), outRows.AsSpan(0, m), Math.Max(0, contextStart - removedBeforeContext));
                if (newDeck != deck) StepOne(Input.Recursor(Queue.CursorOf(newDeck), now));
                WatchQueue(now);
            }
            s_narrPlanVersion = Queue.Version;
            s_narrPlanDeck = newDeck;
            return newDeck;
        }
        finally
        {
            System.Buffers.ArrayPool<int>.Shared.Return(outPacked);
            System.Buffers.ArrayPool<QueueEdge>.Shared.Return(outRows);
        }
    }

    /// <summary>A clip settled: replan (a failed one leaves), skip a failed clip on the deck, and push a streamed clip's length to the
    /// reducer once it is known (it opened with none). UI thread.</summary>
    static void NarrationSettled(Spotify.DjTts.ClipInfo info)
    {
        s_narrPlanVersion = 0;
        long now = FrameNowMs();
        ReadOnlySpan<QueueEdge> rows = Queue.Rows;
        int deck = s_state.Cursor.Index;
        Dj.Spoken? onDeck = (uint)deck < (uint)rows.Length ? Dj.NarrationOf(rows[deck].ItemId) : null;
        if (onDeck is not null && string.Equals(onDeck.Key, info.Key, StringComparison.Ordinal) && s_state.CurrentId.Equals(onDeck.Id))
        {
            if (info.State == Spotify.DjTts.ClipState.Failed)
            {
                Log.Info("dj", "narration.skip slot=" + Dj.SlotName(onDeck.Slot) + " key=" + info.Key + ": its clip failed on the deck, the music goes on");
                Post(Input.Next(now));
                return;
            }
            if (info.State == Spotify.DjTts.ClipState.Ready && info.DurationMs > 0 && s_state.DurationMs != info.DurationMs)
            {
                Log.Info("dj", "narration.duration key=" + info.Key + " durMs=" + info.DurationMs + " (pushed: the clip opened with no length)");
                Post(Input.Duration((int)Math.Min(info.DurationMs, int.MaxValue), s_state.LoadEpoch));
            }
        }
        RequestDrain();
    }
}
