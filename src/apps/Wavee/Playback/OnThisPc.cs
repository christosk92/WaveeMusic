// ── Playback/OnThisPc.cs ────────────────────────────────────────────────────────────────────────────────────────────
// "on this PC": the row fact that a track's audio is fully cached on this computer, at the quality it would play at now,
// with its decryption key saved
//
// Role: SHELL
// Spec: docs/themes/plan/technical-plan.md §6.3 (ZC2), owner decisions Q29 / NQ4
//
// WHAT IT IS, AND WHAT IT IS NOT. Owner Q29: there are no downloads, so a theme that draws a "downloaded"-style mark (Zune's
// ↓) draws it for a track that is cached instead, worded "on this PC" (loc `track.onThisPc`, distinct from the Settings ›
// Storage header). Owner NQ4: it is an INDICATOR only. Nothing here opens, plays or prefers anything: there is no offline
// playback path, and an open still runs the whole ladder (metadata, CDN, key) exactly as before. The fact is three ANDs:
//
//   1. the file   `AudioFileMap` knows which file id the track plays from at the CURRENT preferred quality
//                 (`Spotify.Audio.PreferredQuality`: the setting, the metered cap, the build's lossless ability);
//   2. the bytes  `ChunkDiskCache.IsComplete(fileId)`: every chunk of that file is committed (the cache's in-memory index);
//   3. the key    `Spotify.Audio.KeyStore.Contains(fileId)`: a key for that file is saved. Only presence is asked.
//
// Classic ignores the fact (no visual change); it rides `Track.RowState.OnThisPc`, which every row surface already reads.
//
// THREADING AND COST. `Of` is UI-thread only and allocation-free in steady state: a signal read, a quality stamp compare, the
// map probe (under its lock), the cache's index probe and a UI-owned dictionary of key answers. The key store is sqlite, so a
// key question is never asked on the UI thread: the first time a COMPLETE file is seen its question is queued to a
// background job, which answers through `Playback.ToUi`; until then the row reads false (one flip to true, never a flicker).
// Everything that can change an answer bumps the one `Changed` signal through a coalesced post: a new map entry (the ladder,
// api threads), a completion edge or an eviction (the cache's writer and trim threads), a key write, forget or clear (the
// key store's writer), a quality change (seen by `Of` itself). The key answers are dropped whenever `KeyStore.Version` moves.

using FluentGpu.Signals;
using Wavee.Sdk.Streams;

namespace Wavee;

public static partial class Playback
{
    /// <summary>The "on this PC" row fact (see the file header). UI thread for <see cref="Of"/>; <see cref="Record"/> from
    /// any thread.</summary>
    public static class OnThisPc
    {
        /// <summary>The row indicator's label key: <c>Loc.Get(OnThisPc.LabelKey)</c> reads "On this PC".</summary>
        public const string LabelKey = Strings.Track.OnThisPc;

        static readonly AudioFileMap s_map = new();
        static OnThisPcFacts? s_facts;
        static string? s_path;
        static Timer? s_saveTimer;
        static int s_savePending;

        /// <summary>The process-wide map (diagnostics and tests).</summary>
        public static AudioFileMap Map => s_map;

        /// <summary>The live facts, or null while detached (<c>--fake</c>, headless, a test that never called <see cref="Use"/>).</summary>
        public static OnThisPcFacts? Facts => Volatile.Read(ref s_facts);

        /// <summary>App.Main, after <c>KeyStore.Use</c> and never under <c>--fake</c>: records where the map lives, wires the
        /// facts to the shared cache, the key store and the UI poster, and loads the map off-thread. Opens nothing on the
        /// calling thread.</summary>
        public static void Use(string path)
        {
            s_path = path;
            s_saveTimer ??= new Timer(static _ => SaveNow(), null, Timeout.Infinite, Timeout.Infinite);
            var facts = new OnThisPcFacts(s_map, static () => Spotify.Audio.DiskCache.IfCreated, KeyPresent,
                static () => Spotify.Audio.KeyStore.Version, CurrentQuality, static a => ToUi(a), RunInBackground);
            Volatile.Write(ref s_facts, facts);
            Spotify.Audio.KeyStore.Changed += facts.NoteKeysChanged;
            var loader = new Thread(static state => LoadAndWatch((OnThisPcFacts)state!))
            {
                IsBackground = true,
                Name = "Wavee.OnThisPc.Load",
                Priority = ThreadPriority.BelowNormal,
            };
            loader.Start(facts);
        }

        /// <summary>The loader: read the map, then — only when it holds anything a row could show — make sure the shared
        /// cache exists and start its index sweep, so a fresh launch shows the fact before anything plays.</summary>
        static void LoadAndWatch(OnThisPcFacts facts)
        {
            try
            {
                int read = s_path is { } path ? s_map.Load(path) : 0;
                if (read > 0) facts.NoteMapChanged();
                if (s_map.Count > 0 && Spotify.Audio.DiskCache.Shared is { } cache) facts.Watch(cache);
                Log.Info("audio", "audio.onthispc.load entries=" + read);
            }
            catch (Exception ex) { Log.Warn("audio", "audio.onthispc.load faulted (" + ex.GetType().Name + ")"); }
        }

        /// <summary>App exit tail: write the map if it changed since the last save. Any thread.</summary>
        public static void Shutdown()
        {
            s_saveTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            if (Interlocked.Exchange(ref s_savePending, 0) != 0 && s_path is { } path) s_map.Save(path);
        }

        /// <summary>The ladder resolved <paramref name="uri"/> at <paramref name="quality"/> (the <c>Spotify.Audio.Quality</c>
        /// rung) to <paramref name="fileIdHex"/> (<c>Spotify.Audio.ChooseFor</c>). Any thread; never blocks: the save is
        /// debounced onto a timer.</summary>
        public static void Record(string uri, byte quality, string fileIdHex)
        {
            bool changed = s_map.Record(uri, quality, fileIdHex);
            if (changed) Volatile.Read(ref s_facts)?.NoteMapChanged();
            if (s_path is not null && Interlocked.Exchange(ref s_savePending, 1) == 0) s_saveTimer?.Change(3_000, Timeout.Infinite);
        }

        static void SaveNow()
        {
            if (Interlocked.Exchange(ref s_savePending, 0) == 0 || s_path is not { } path) return;
            if (!s_map.Save(path)) Log.Warn("audio", "audio.onthispc.save failed (kept in memory; retried on the next change)");
        }

        /// <summary>Is <paramref name="id"/>'s audio on this PC (see the file header)? UI THREAD; a subscribing read of the
        /// facts' <see cref="OnThisPcFacts.Changed"/>. False while detached.</summary>
        public static bool Of(EntityId id) => Volatile.Read(ref s_facts) is { } facts && facts.Of(id);

        /// <summary>The key question, on a background thread: the hex id as bytes, then the store's presence check.</summary>
        static bool KeyPresent(string fileIdHex)
        {
            Span<byte> id = stackalloc byte[64];
            if (fileIdHex.Length > id.Length * 2) return false;
            if (Convert.FromHexString(fileIdHex, id, out _, out int written) != System.Buffers.OperationStatus.Done) return false;
            return Spotify.Audio.KeyStore.Contains(id[..written]);
        }

        static void RunInBackground(Action work) => ThreadPool.UnsafeQueueUserWorkItem(static w => w(), work, preferLocal: false);

        // The preferred quality, recomputed only when something it reads moved: any settings write, the connection cost,
        // the build's lossless ability. UI thread (Of's caller).
        static uint s_qualityEpoch = uint.MaxValue;
        static FluentGpu.WindowsApi.Network.NetworkCost s_qualityCost;
        static bool s_qualityDerive;
        static byte s_quality;

        static byte CurrentQuality()
        {
            uint epoch = Platform.SettingsEpoch;
            var cost = Platform.Network.Current;
            bool derive = Spotify.Audio.CanDerive;
            if (epoch != s_qualityEpoch || derive != s_qualityDerive || !cost.Equals(s_qualityCost))
            {
                s_qualityEpoch = epoch;
                s_qualityCost = cost;
                s_qualityDerive = derive;
                s_quality = (byte)Spotify.Audio.PreferredQuality();
            }
            return s_quality;
        }
    }

    /// <summary>The "on this PC" facts over injected seams: the map, the cache, the key question, the key store's version,
    /// the preferred quality, the UI poster and a background runner (<see cref="OnThisPc.Use"/> wires the live ones; a test
    /// passes its own). <see cref="Of"/> is UI-thread only; the <c>Note*</c> members and <see cref="Watch"/> are any thread.</summary>
    public sealed class OnThisPcFacts
    {
        enum KeyState : byte { Pending, Yes, No }

        readonly record struct KeyFact(KeyState State, long Version);

        readonly AudioFileMap _map;
        readonly Func<ChunkDiskCache?> _cache;
        readonly Func<string, bool> _keyPresent;
        readonly Func<long> _keyVersion;
        readonly Func<byte> _quality;
        readonly Action<Action> _post;
        readonly Action<Action> _background;
        readonly Action _bump, _runAsks;
        readonly Action<string?> _onCompletion;

        // UI thread.
        readonly Dictionary<string, KeyFact> _keys = new(StringComparer.OrdinalIgnoreCase);
        long _keysVersion = long.MinValue;
        int _lastQuality = -1;

        // Any thread.
        readonly Lock _askGate = new();
        readonly List<string> _asks = new();
        int _askScheduled;
        int _bumpPosted;
        ChunkDiskCache? _watched;

        /// <param name="map">The (track, quality) → file id map.</param>
        /// <param name="cache">The audio cache, or null while none exists (never created by this call).</param>
        /// <param name="keyPresent">Is a key saved for this hex file id? Called on a background thread only.</param>
        /// <param name="keyVersion">Moves whenever <paramref name="keyPresent"/>'s answers may have changed. Any thread.</param>
        /// <param name="quality">The rung a play would ask for now (UI thread).</param>
        /// <param name="post">Run an action on the UI thread.</param>
        /// <param name="background">Run an action off the UI thread.</param>
        public OnThisPcFacts(AudioFileMap map, Func<ChunkDiskCache?> cache, Func<string, bool> keyPresent, Func<long> keyVersion,
            Func<byte> quality, Action<Action> post, Action<Action> background)
        {
            _map = map;
            _cache = cache;
            _keyPresent = keyPresent;
            _keyVersion = keyVersion;
            _quality = quality;
            _post = post;
            _background = background;
            _bump = DoBump;
            _runAsks = RunAsks;
            _onCompletion = _ => Bump();
        }

        /// <summary>Bumped (UI thread) whenever any row's answer may have changed. <see cref="Of"/> subscribes to it.</summary>
        public Signal<uint> Changed { get; } = new(0u);

        /// <summary>Is <paramref name="id"/>'s audio on this PC: the file at the current quality is known, complete in the
        /// cache, and its key is saved? UI THREAD; subscribing; allocation-free once the file's key answer is known.</summary>
        public bool Of(EntityId id)
        {
            _ = Changed.Value;   // subscribe: every edge below is announced through it
            byte quality = _quality();
            if (quality != _lastQuality)
            {
                if (_lastQuality >= 0) Bump();   // the setting or the connection moved: every row asks again
                _lastQuality = quality;
            }
            if (id.IsEmpty || !_map.TryGet(id, quality, out string? fileIdHex)) return false;
            if (_cache() is not { } cache) return false;
            if (!ReferenceEquals(Volatile.Read(ref _watched), cache)) Watch(cache);
            return cache.IsComplete(fileIdHex) && KeyPresent(fileIdHex);
        }

        /// <summary>Follow <paramref name="cache"/>'s completion edges and start its index sweep. Idempotent per cache.</summary>
        public void Watch(ChunkDiskCache cache)
        {
            ChunkDiskCache? prior = Interlocked.Exchange(ref _watched, cache);
            if (ReferenceEquals(prior, cache)) return;
            if (prior is not null) prior.CompletionChanged -= _onCompletion;
            cache.CompletionChanged += _onCompletion;
            cache.EnsureIndex();
            Bump();
        }

        /// <summary>A map entry was added or changed. Any thread.</summary>
        public void NoteMapChanged() => Bump();

        /// <summary>The key store committed a write or a forget, or was cleared. Any thread.</summary>
        public void NoteKeysChanged() => Bump();

        // ── the key answers (UI thread) ──────────────────────────────────────────────────────────────────────────────

        bool KeyPresent(string fileIdHex)
        {
            long version = _keyVersion();
            if (version != _keysVersion)
            {
                _keys.Clear();
                _keysVersion = version;
            }
            if (_keys.TryGetValue(fileIdHex, out KeyFact fact)) return fact.State == KeyState.Yes;
            _keys[fileIdHex] = new KeyFact(KeyState.Pending, version);
            Ask(fileIdHex);
            // An inline runner (a test) has answered already; the live one answers through a post and a bump.
            return _keys.TryGetValue(fileIdHex, out fact) && fact.State == KeyState.Yes;
        }

        void Ask(string fileIdHex)
        {
            lock (_askGate) _asks.Add(fileIdHex);
            if (Interlocked.Exchange(ref _askScheduled, 1) == 0) _background(_runAsks);
        }

        /// <summary>Background: answer every queued key question, a batch at a time, and post the answers to the UI.</summary>
        void RunAsks()
        {
            while (true)
            {
                string[] batch;
                lock (_askGate)
                {
                    if (_asks.Count == 0)
                    {
                        Volatile.Write(ref _askScheduled, 0);
                        return;
                    }
                    batch = _asks.ToArray();
                    _asks.Clear();
                }
                long version = _keyVersion();   // read BEFORE asking: a change after it moves the version and the answers are dropped
                var answers = new bool[batch.Length];
                for (int i = 0; i < batch.Length; i++)
                {
                    try { answers[i] = _keyPresent(batch[i]); }
                    catch (Exception ex) { answers[i] = false; Log.Warn("audio", "audio.onthispc.key faulted (" + ex.GetType().Name + ")"); }
                }
                _post(() => Apply(batch, answers, version));
            }
        }

        /// <summary>UI thread: store the answers that are still the question asked (same key-store version), and announce a yes.</summary>
        void Apply(string[] batch, bool[] answers, long version)
        {
            bool yes = false;
            for (int i = 0; i < batch.Length; i++)
            {
                if (!_keys.TryGetValue(batch[i], out KeyFact fact) || fact.State != KeyState.Pending || fact.Version != version) continue;
                _keys[batch[i]] = new KeyFact(answers[i] ? KeyState.Yes : KeyState.No, version);
                yes |= answers[i];
            }
            if (yes) Changed.Value = Changed.Peek() + 1u;
        }

        // ── the coalesced announcement ───────────────────────────────────────────────────────────────────────────────

        void Bump()
        {
            if (Interlocked.Exchange(ref _bumpPosted, 1) == 0) _post(_bump);
        }

        void DoBump()
        {
            Volatile.Write(ref _bumpPosted, 0);
            Changed.Value = Changed.Peek() + 1u;
        }
    }
}
