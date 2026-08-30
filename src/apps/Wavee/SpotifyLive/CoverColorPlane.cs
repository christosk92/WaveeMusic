using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Signals;
using Wavee.Core;

namespace Wavee.SpotifyLive;

/// <summary>
/// THE cover-colour plane: one durable, IMAGE-KEYED table of the colour roles Spotify extracts from a cover, and the
/// only place the app resolves an art colour from.
///
/// Why image-keyed and not entity-keyed: the colours are a property of the cover, not of the row that happens to show
/// it. Extension kind 179 VISUAL_IDENTITY_TRAIT proves it by shipping the image URLs and the colour set in ONE payload
/// — so a track's payload also tints its album's grid card, and a colour is available before a single image byte
/// arrives. Keying by entity (the old <c>Track.Tint</c> / <c>Album.Tint</c> / <c>Palette</c> fields) forced every
/// surface to fetch, store and thread its own copy, which is why grids painted grey while track lists painted colour.
///
/// Two feeds, ONE role set (they return the same five roles):
///   • kind 179 — free, rides batches the app already makes, but DARK ONLY (its three schemes are elevation levels
///     base/darker/darkest, not light-vs-dark — see visual_identity_trait.proto).
///   • getDynamicColorsByUris — the universal filler, keyed by <c>spotify:image:&lt;id&gt;</c> (exactly this table's
///     key), returning dark AND light. Demand-driven: <see cref="TryGetTint"/> enqueues on a miss, so RENDERING the
///     art is the request and no surface has to remember to prefetch.
///
/// Threading: reads are lock-guarded and allocation-free (span lookups) because they sit on the render path; writes
/// happen on background completions and bump <see cref="Epoch"/> ONCE PER BATCH through the UI post, never per entity.
/// </summary>
public sealed class CoverColorPlane
{
    static CoverColorPlane? _current;

    /// <summary>The ambient plane every art slot reads. Lazily real so <c>Surfaces</c> never needs a context lookup on
    /// the render path; with no <see cref="Filler"/> installed (logged out, offline, tests) it simply serves whatever
    /// the persisted table holds and every miss falls back to the neutral tile.</summary>
    public static CoverColorPlane Current => _current ??= new CoverColorPlane();

    /// <summary>Swap in a plane (tests: a temp path + a fake clock).</summary>
    public static void Install(CoverColorPlane plane) => _current = plane;

    /// <summary>The five colour roles Spotify grades per cover — the shape BOTH kind 179 (<c>ColorScheme</c>) and
    /// <c>getDynamicColorsByUris</c> return. Opaque ARGB, framework-neutral (mapped to ColorF at the UI boundary).</summary>
    public readonly record struct Scheme(uint BackgroundBase, uint BackgroundTintedBase, uint TextBase,
                                         uint TextSubdued, uint TextBrightAccent)
    {
        public bool IsEmpty => BackgroundBase == 0;
    }

    static readonly TimeSpan HitTtl = TimeSpan.FromDays(180);   // a cover's colours never change — persist for ~half a year
    static readonly TimeSpan MissTtl = TimeSpan.FromDays(7);    // a colourless cover: don't re-ask every launch, but do recover
    const int FlushDebounceMs = 1500;
    const int PumpDebounceMs = 120;    // coalesce a grid realize (dozens of misses in one frame) into ONE batch
    const int BatchCap = 50;           // uris per getDynamicColorsByUris request
    const int MaxQueue = 512;

    readonly string _path;
    readonly object _gate = new();
    readonly Dictionary<string, Entry> _map = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _queued = new(StringComparer.OrdinalIgnoreCase);
    readonly Queue<string> _pending = new();
    readonly Dictionary<string, Signal<int>> _watch = new(StringComparer.OrdinalIgnoreCase);
    readonly Func<long> _nowUnix;
    readonly Signal<int> _epoch = new(0);
    Action<Action> _post = static a => a();
    bool _loaded, _dirty, _flushScheduled, _pumpScheduled, _pumping;

    readonly record struct Entry(Scheme Dark, Scheme Light, bool HasLight, bool BestFitIsLight, bool Negative, long Ts);

    /// <summary>Resolves a batch of image ids to their graded colours (index-parallel with the input; null = the server
    /// had none). Installed by the live session; null ⇒ cache-only (offline, logged out, tests).</summary>
    public Func<IReadOnlyList<string>, CancellationToken, Task<IReadOnlyList<GradedColors?>>>? Filler { get; set; }

    /// <summary>One image's graded colours as a feed hands them over.</summary>
    public readonly record struct GradedColors(Scheme Dark, Scheme? Light, bool BestFitIsLight);

    public CoverColorPlane(string? path = null, Func<long>? nowUnix = null)
    {
        _path = path ?? DefaultPath();
        _nowUnix = nowUnix ?? (() => DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    }

    /// <summary>Bumped once per landed batch. Subscribe from ART TILES only — they are the leaves that are waiting for
    /// a colour, so a batch repaints exactly them. A page that wants its wash to appear should watch its ONE cover with
    /// <see cref="Watch"/> instead: a page-wide subscription to this would re-render the whole page (and every shelf in
    /// it) every time a scrolling grid finished another batch.</summary>
    public IReadSignal<int> Epoch => _epoch;

    /// <summary>A signal that changes only when THIS image is graded — for page chrome (hero wash, accent bar, the
    /// shell material tint), which depends on one cover rather than on every colour in flight. Read it at page scope and the wash
    /// lands the moment its own cover resolves, with no coupling to unrelated batches.</summary>
    public IReadSignal<int> Watch(string? url)
    {
        var key = KeySpan(url);
        if (key.IsEmpty) return _epoch;
        lock (_gate)
        {
            var lookup = _watch.GetAlternateLookup<ReadOnlySpan<char>>();
            if (lookup.TryGetValue(key, out var sig)) return sig;
            var created = new Signal<int>(0);
            _watch[key.ToString()] = created;
            return created;
        }
    }

    /// <summary>Marshal background completions onto the UI thread. Call once from a mount effect with <c>UsePost()</c>.
    /// Also warms the persisted table off-thread (see <see cref="Prewarm"/>) — startup is the last moment before the
    /// first frame at which the disk read is free.</summary>
    public void Activate(Action<Action> post)
    {
        _post = post ?? (static a => a());
        Prewarm();
    }

    /// <summary>Parse the persisted table on a background thread so the FIRST render-path read is already a dictionary
    /// lookup. Without this the cold load rode <see cref="EnsureLoadedLocked"/> on whichever caller happened to be first
    /// — in practice the always-mounted player bar's art tile, whose <see cref="TryGetTint"/> then paid a synchronous
    /// <c>File.ReadAllBytes</c> + <c>JsonDocument.Parse</c> INSIDE <c>Render()</c> (RenderBudget flagged 100-325ms
    /// one-shot renders of <c>PlayerBarContent</c>). Idempotent and race-free by construction: the work is the same
    /// <see cref="EnsureLoadedLocked"/> under the same <c>_gate</c>, so a UI-thread read that beats this task simply
    /// does the load itself (the old behaviour, now the rare fallback rather than the norm) and this task then finds
    /// <c>_loaded</c> set and returns.</summary>
    public void Prewarm() => _ = Task.Run(() => { lock (_gate) EnsureLoadedLocked(); });

    public static string DefaultPath()
    {
        try { return Path.Combine(FluentGpu.WindowsApi.Storage.AppDataStore.ForUnpackaged("Wavee", "Wavee").CacheFolder, "cover-colors.json"); }
        catch { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Wavee", "Cache", "cover-colors.json"); }
    }

    // ── keys ────────────────────────────────────────────────────────────────────────────────────────────────────
    // The image-id / artwork-identity rules (40-hex Spotify id = 16-char size marker + 24-char art tail) are OWNED by
    // Wavee.Core's ImageSource (ImageIdSpan / ArtIdentityOf) — the same rule the detail hero's cover latch compares on —
    // so the colour cache and the cover latch can never disagree about "same art". These accessors delegate; the
    // COLOUR identity is the size-independent tail (one entry serves the row thumbnail and the hero), while a FETCH needs
    // the full id, because getDynamicColorsByUris takes `spotify:image:<full id>`.

    const int ImageIdLength = 40;                       // the provider's full id, the shape CanGrade accepts (== ImageSource's)
    const string ImageUriPrefix = "spotify:image:";      // the getDynamicColorsByUris token form

    /// <summary>The full image id (the segment after <c>/image/</c>, or the token after <c>spotify:image:</c>) — what
    /// the filler asks about. A SLICE of the input (the hot read path must not mint a string per art slot per render).</summary>
    public static ReadOnlySpan<char> IdSpan(ReadOnlySpan<char> url) => ImageSource.ImageIdSpan(url);

    /// <summary>The colour identity of a cover URL: the size-independent tail of its image id, so every pre-sized URL
    /// of one artwork shares a single entry. Ids that are not Spotify's 40-char form key on themselves.</summary>
    public static ReadOnlySpan<char> KeySpan(ReadOnlySpan<char> url) => ImageSource.ArtIdentityOf(IdSpan(url));

    /// <summary>The size-independent part of an image id (see <see cref="KeySpan"/>).</summary>
    public static ReadOnlySpan<char> IdentityOf(ReadOnlySpan<char> imageId) => ImageSource.ArtIdentityOf(imageId);

    public static string KeyForUrl(string? url) => string.IsNullOrEmpty(url) ? "" : KeySpan(url).ToString();

    /// <summary>Whether <paramref name="url"/> carries the provider's full 40-hex image id and can therefore be sent to
    /// <c>getDynamicColorsByUris</c>. Playlist mosaics and custom-cover URLs still key the local plane (a kind-179
    /// visual-identity payload may have seeded them), but a render-path miss for those URLs cannot be filled by the
    /// dynamic-colour endpoint. Callers that own richer context can use this to choose a stable, gradeable fallback
    /// instead of waiting forever on an impossible request.</summary>
    public static bool CanGrade(string? url)
    {
        if (string.IsNullOrEmpty(url)) return false;
        var id = IdSpan(url);
        if (id.Length != ImageIdLength) return false;
        for (int i = 0; i < id.Length; i++)
        {
            char c = id[i];
            if (!((uint)(c - '0') <= 9u
                || (uint)(c - 'a') <= 5u
                || (uint)(c - 'A') <= 5u))
                return false;
        }
        return true;
    }

    /// <summary>The <c>spotify:image:&lt;id&gt;</c> form <c>getDynamicColorsByUris</c> takes — it does NOT accept the
    /// https URL that <c>fetchExtractedColors</c> did.</summary>
    public static string ImageUri(string key) => ImageUriPrefix + key;

    // ── the render-path read ────────────────────────────────────────────────────────────────────────────────────
    /// <summary>The art placeholder colour for a cover URL, or false when unknown (⇒ caller paints the neutral tile).
    /// A miss ENQUEUES the image for the filler, which is what makes every art slot in the app self-resolving.
    /// Allocation-free on a hit: the key is a span into <paramref name="url"/>.</summary>
    public bool TryGetTint(ReadOnlySpan<char> url, bool lightTheme, out uint argb)
    {
        argb = 0;
        var id = IdSpan(url);
        var key = IdentityOf(id);
        if (key.IsEmpty) return false;

        lock (_gate)
        {
            EnsureLoadedLocked();
            var lookup = _map.GetAlternateLookup<ReadOnlySpan<char>>();
            if (lookup.TryGetValue(key, out var e))
            {
                long age = _nowUnix() - e.Ts;
                bool fresh = age <= (long)(e.Negative ? MissTtl : HitTtl).TotalSeconds;
                if (fresh)
                {
                    if (e.Negative) return false;
                    // Light theme needs a LIGHT scheme. Kind 179 only ever ships dark treatments, so an entry that has
                    // never been graded by the filler must fall back to the neutral tile rather than drop a dark slab
                    // onto a pale page — and it stays queued so the filler can complete it.
                    if (lightTheme && !e.HasLight) { EnqueueLocked(key, id); return false; }
                    argb = lightTheme ? e.Light.BackgroundBase : e.Dark.BackgroundBase;
                    return argb != 0;
                }
            }
            EnqueueLocked(key, id);
            return false;
        }
    }

    /// <summary>The full graded roles for a cover (page washes, accents), or null when the plane has nothing fresh.
    /// Same demand-driven fill as <see cref="TryGetTint"/>.</summary>
    public Scheme? TryGetScheme(string? url, bool lightTheme)
    {
        if (string.IsNullOrEmpty(url)) return null;
        lock (_gate)
        {
            EnsureLoadedLocked();
            var id = IdSpan(url);
            var key = IdentityOf(id);
            var lookup = _map.GetAlternateLookup<ReadOnlySpan<char>>();
            if (lookup.TryGetValue(key, out var e) && !e.Negative
                && _nowUnix() - e.Ts <= (long)HitTtl.TotalSeconds)
            {
                if (!lightTheme) return e.Dark;
                if (e.HasLight) return e.Light;
            }
            EnqueueLocked(key, id);
            return null;
        }
    }

    /// <summary>Does this cover already carry a fresh DARK grading? A pure probe — unlike <see cref="TryGetTint"/> and
    /// <see cref="TryGetScheme"/> it never enqueues the image for the filler, because its caller is not rendering
    /// anything: it is the kind-179 trait projector asking "has this entity been through here?" before a request is
    /// planned. Enqueuing from a planning question would turn every warm page into a getDynamicColorsByUris batch.
    ///
    /// <para>Dark specifically: kind 179 only ever ships dark treatments, so a dark entry (however it was graded) is
    /// exactly what a 179 answer would produce, and a negative is NOT one — a cover the colour server declined can
    /// still get a 179 payload.</para></summary>
    public bool HasFreshDark(string? url)
    {
        if (string.IsNullOrEmpty(url)) return false;
        lock (_gate)
        {
            EnsureLoadedLocked();
            var key = KeySpan(url);
            if (key.IsEmpty) return false;
            var lookup = _map.GetAlternateLookup<ReadOnlySpan<char>>();
            return lookup.TryGetValue(key, out var e) && !e.Negative && !e.Dark.IsEmpty
                && _nowUnix() - e.Ts <= (long)HitTtl.TotalSeconds;
        }
    }

    // ── feeds ───────────────────────────────────────────────────────────────────────────────────────────────────
    /// <summary>A dark-only grading (extension kind 179 / a GraphQL <c>visualIdentity</c> node). Never downgrades an
    /// entry the filler has already graded with a light scheme.</summary>
    public void SetDark(string? url, Scheme dark)
    {
        if (dark.IsEmpty) return;
        string key = KeyForUrl(url);   // the size-independent identity: one 179 payload tints every size of that cover
        if (key.Length == 0) return;
        lock (_gate)
        {
            EnsureLoadedLocked();
            if (_map.TryGetValue(key, out var cur) && cur.HasLight)
            {
                _map[key] = cur with { Dark = dark, Ts = _nowUnix() };   // keep the graded light half
            }
            else
            {
                _map[key] = new Entry(dark, default, HasLight: false, BestFitIsLight: false, Negative: false, _nowUnix());
            }
            _dirty = true;
            ScheduleFlushLocked();
        }
    }

    /// <summary>A full grading from the filler (dark + light + bestFit), or a negative result for a cover the server
    /// has no colours for. Takes the image id it was requested with and files it under the size-independent identity,
    /// so the answer serves every size of that artwork.</summary>
    public void SetGraded(string imageId, GradedColors? colors)
    {
        if (imageId.Length == 0) return;
        string key = IdentityOf(imageId).ToString();
        lock (_gate)
        {
            EnsureLoadedLocked();
            _map[key] = colors is { } c
                ? new Entry(c.Dark, c.Light ?? default, c.Light is not null, c.BestFitIsLight, Negative: false, _nowUnix())
                : new Entry(default, default, false, false, Negative: true, _nowUnix());
            _dirty = true;
            ScheduleFlushLocked();
        }
    }

    // ── demand-driven fill ──────────────────────────────────────────────────────────────────────────────────────
    /// <summary>Queue an artwork for grading. Deduped by colour IDENTITY (so ten sizes of one cover ask once) but
    /// queued as the FULL image id, which is what <c>spotify:image:</c> needs.</summary>
    void EnqueueLocked(ReadOnlySpan<char> key, ReadOnlySpan<char> imageId)
    {
        if (Filler is null || key.IsEmpty || imageId.IsEmpty || _pending.Count >= MaxQueue) return;
        if (_queued.GetAlternateLookup<ReadOnlySpan<char>>().Contains(key)) return;
        _queued.Add(key.ToString());          // the ONLY allocations on the read path, once per artwork per run
        _pending.Enqueue(imageId.ToString());
        SchedulePumpLocked();
    }

    void SchedulePumpLocked()
    {
        if (_pumpScheduled || _pumping) return;
        _pumpScheduled = true;
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(PumpDebounceMs).ConfigureAwait(false); } catch { }
            lock (_gate) { _pumpScheduled = false; }
            await PumpAsync().ConfigureAwait(false);
        });
    }

    async Task PumpAsync()
    {
        lock (_gate)
        {
            if (_pumping) return;
            _pumping = true;
        }
        try
        {
            while (true)
            {
                var batch = new List<string>(BatchCap);
                lock (_gate)
                {
                    while (batch.Count < BatchCap && _pending.Count > 0) batch.Add(_pending.Dequeue());
                }
                if (batch.Count == 0 || Filler is not { } filler) return;

                // The batch carries full image ids (what the wire wants); dedupe/watch/store all key on the identity.
                IReadOnlyList<GradedColors?> graded;
                try { graded = await filler(batch, CancellationToken.None).ConfigureAwait(false); }
                catch
                {
                    // A failed batch must not poison the images: forget they were queued so a later render retries.
                    lock (_gate) foreach (var id in batch) _queued.Remove(IdentityOf(id).ToString());
                    return;
                }

                var watched = new List<Signal<int>>();
                for (int i = 0; i < batch.Count; i++)
                {
                    var colors = i < graded.Count ? graded[i] : null;
                    SetGraded(batch[i], colors);
                    string key = IdentityOf(batch[i]).ToString();
                    lock (_gate)
                    {
                        _queued.Remove(key);
                        if (colors is not null && _watch.TryGetValue(key, out var sig)) watched.Add(sig);
                    }
                }

                // ONE bump per landed batch, on the UI thread: a grid realize repaints its visible tiles once. Pages
                // that watch a single cover are woken through their own signal, so an unrelated batch never touches them.
                _post(() =>
                {
                    _epoch.Value = _epoch.Peek() + 1;
                    foreach (var sig in watched) sig.Value = sig.Peek() + 1;
                });
            }
        }
        finally { lock (_gate) _pumping = false; }
    }

    // ── persistence ─────────────────────────────────────────────────────────────────────────────────────────────
    void EnsureLoadedLocked()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            if (!File.Exists(_path)) return;
            using var doc = JsonDocument.Parse(File.ReadAllBytes(_path));
            if (!doc.RootElement.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Object) return;
            foreach (var prop in entries.EnumerateObject())
            {
                var v = prop.Value;
                bool neg = v.TryGetProperty("neg", out var n) && n.ValueKind == JsonValueKind.True;
                bool hasLight = v.TryGetProperty("l", out var lightNode) && lightNode.ValueKind == JsonValueKind.Array;
                _map[prop.Name] = new Entry(
                    ReadScheme(v, "d"), hasLight ? ReadScheme(v, "l") : default, hasLight,
                    v.TryGetProperty("bfl", out var b) && b.ValueKind == JsonValueKind.True, neg,
                    v.TryGetProperty("ts", out var t) && t.TryGetInt64(out var ts) ? ts : 0);
            }
        }
        catch { /* a corrupt/partial cache is non-fatal — start empty and re-populate */ }

        static Scheme ReadScheme(JsonElement e, string name)
        {
            if (!e.TryGetProperty(name, out var a) || a.ValueKind != JsonValueKind.Array || a.GetArrayLength() < 5) return default;
            return new Scheme(U(a[0]), U(a[1]), U(a[2]), U(a[3]), U(a[4]));
            static uint U(JsonElement x) => x.TryGetUInt32(out var u) ? u : 0;
        }
    }

    void ScheduleFlushLocked()
    {
        if (_flushScheduled) return;
        _flushScheduled = true;
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(FlushDebounceMs).ConfigureAwait(false); } catch { }
            Flush();
        });
    }

    /// <summary>Write the table to disk (atomic temp-then-replace). Best-effort — a failed write just means a refetch.</summary>
    public void Flush()
    {
        KeyValuePair<string, Entry>[] snapshot;
        lock (_gate)
        {
            _flushScheduled = false;
            if (!_dirty) return;
            _dirty = false;
            snapshot = new KeyValuePair<string, Entry>[_map.Count];
            int i = 0;
            foreach (var kv in _map) snapshot[i++] = kv;
        }

        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms))
            {
                w.WriteStartObject();
                w.WriteNumber("version", 1);
                w.WritePropertyName("entries");
                w.WriteStartObject();
                foreach (var (key, e) in snapshot)
                {
                    w.WritePropertyName(key);
                    w.WriteStartObject();
                    if (e.Negative) w.WriteBoolean("neg", true);
                    else
                    {
                        WriteScheme(w, "d", e.Dark);
                        if (e.HasLight) WriteScheme(w, "l", e.Light);
                        if (e.BestFitIsLight) w.WriteBoolean("bfl", true);
                    }
                    w.WriteNumber("ts", e.Ts);
                    w.WriteEndObject();
                }
                w.WriteEndObject();
                w.WriteEndObject();
            }
            var tmp = _path + ".tmp";
            File.WriteAllBytes(tmp, ms.ToArray());
            File.Move(tmp, _path, overwrite: true);
        }
        catch { /* best-effort persistence */ }

        static void WriteScheme(Utf8JsonWriter w, string name, in Scheme s)
        {
            w.WritePropertyName(name);
            w.WriteStartArray();
            w.WriteNumberValue(s.BackgroundBase);
            w.WriteNumberValue(s.BackgroundTintedBase);
            w.WriteNumberValue(s.TextBase);
            w.WriteNumberValue(s.TextSubdued);
            w.WriteNumberValue(s.TextBrightAccent);
            w.WriteEndArray();
        }
    }
}
