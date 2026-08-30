using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Wavee.Backend.Metadata;
using Wavee.Core;
using Xm = Wavee.Protocol.ExtendedMetadata;

namespace Wavee.Backend.Hydration;

// ── The DISPLAY-ONLY extension read path (design §2.5) ───────────────────────────────────────────────────────────────
// Traits decorate ROWS and go through TraitPipeline. This is the other half: extension payloads a DRAWER shows — track
// credits (186), a pre-release header (138), the expand drawer's 99/98/5/237, a user profile (15). They never write the
// store, so they need a different cache: the answer people re-open is the PARSED object, not the bytes.
//
// The four services this replaces each had their own dictionary, their own in-flight guard (or none) and their own
// idea of what a 404 meant. Three rules, once:
//   • the parsed answer is cached INCLUDING null — "this track has no credits" is an answer, and re-parsing 40 KB of
//     protobuf to rediscover it on every drawer open was the measurable half of the waste;
//   • concurrent opens share ONE load. The TCS slot is published BEFORE the load starts and removed in a finally with
//     the value-matching TryRemove overload, so a load that completes synchronously cannot strand its own slot (the
//     bug shape that wedges a key for the session);
//   • a nav-away must never cancel a load somebody else is waiting on: the load runs on CancellationToken.None and the
//     CALLER's token only detaches that caller's await (WaitAsync).
// And the negative memo is SHARED with TraitPipeline — a "no" learned here stops the row pass re-asking, and vice-versa.

/// <summary>Per-read knobs. <see cref="Revalidate"/> forces a conditional round trip (the expand drawer's refresh):
/// the etag cache is marked stale first, so the request carries the etag and a 304 keeps the cached answer.</summary>
public readonly record struct ReadOptions(bool Revalidate = false);

/// <summary>Display-only extension reads. Every arm stamps the surface's <c>client-feature-id</c>.</summary>
public interface IExtensionReader
{
    /// <summary>One (uri, kind) → the parsed answer, or null when the entity has no such extension.</summary>
    Task<T?> ReadAsync<T>(string uri, Xm.ExtensionKind kind, Func<ByteString, T?> parse, TraitSurface surface,
                          CancellationToken ct = default, ReadOptions options = default) where T : class;

    /// <summary>Many uris, ONE kind, one POST per <see cref="MetadataChunking.MaxEntitiesPerRequest"/>. Only entities
    /// with a non-null answer appear in the result.</summary>
    Task<IReadOnlyDictionary<string, T>> ReadManyAsync<T>(IReadOnlyList<string> uris, Xm.ExtensionKind kind,
                                                          Func<ByteString, T?> parse, TraitSurface surface,
                                                          CancellationToken ct = default) where T : class;

    /// <summary>Multi-KIND raw read — the expand drawer asks 99/98/5/237 for one track and wants the bytes, not a
    /// parsed shape. No parsed cache (the caller owns the decode); the etag cache still answers what it holds.</summary>
    Task<IReadOnlyDictionary<(string Uri, Xm.ExtensionKind Kind), CachedExtension>> ReadRawAsync(
        IReadOnlyList<(string Uri, Xm.ExtensionKind Kind)> reqs, TraitSurface surface,
        CancellationToken ct = default, ReadOptions options = default);

    /// <summary>Publish an already-known answer under a key. The pre-release path resolves ONE payload that answers for
    /// two uris (the prerelease uri and the album it becomes), and seeding the second is what stops the second open
    /// paying for a request that cannot tell it anything new.</summary>
    void Seed<T>(string uri, Xm.ExtensionKind kind, T? answer) where T : class;
}

/// <inheritdoc cref="IExtensionReader"/>
public sealed class ExtensionReader : IExtensionReader
{
    readonly ExtensionEtagCache _cache;
    readonly NegativeMemo _negatives;
    readonly WaveeLogger _log;
    readonly BoundedLru<(string Uri, Xm.ExtensionKind Kind), object?> _answers;
    readonly ConcurrentDictionary<(string Uri, Xm.ExtensionKind Kind), TaskCompletionSource<object?>> _inFlight = new();

    static readonly IReadOnlyDictionary<(string Uri, Xm.ExtensionKind Kind), CachedExtension> NoRaw =
        new Dictionary<(string, Xm.ExtensionKind), CachedExtension>();

    public ExtensionReader(ExtensionEtagCache cache, NegativeMemo negatives, WaveeLogger log = default, int parsedCap = 1024)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _negatives = negatives ?? throw new ArgumentNullException(nameof(negatives));
        _log = log;
        _answers = new BoundedLru<(string, Xm.ExtensionKind), object?>(parsedCap);
    }

    /// <summary>Loads currently in flight — a diagnostic, and the assertion that a completed read left no slot behind.</summary>
    public int InFlight => _inFlight.Count;

    /// <summary>Parsed answers held (including cached nulls).</summary>
    public int Cached => _answers.Count;

    public Task<T?> ReadAsync<T>(string uri, Xm.ExtensionKind kind, Func<ByteString, T?> parse, TraitSurface surface,
                                 CancellationToken ct = default, ReadOptions options = default) where T : class
    {
        ArgumentNullException.ThrowIfNull(parse);
        if (string.IsNullOrEmpty(uri) || kind == Xm.ExtensionKind.UnknownExtension) return Task.FromResult<T?>(null);

        var key = (uri, kind);
        if (options.Revalidate)
        {
            // MarkStale FIRST so the refetch is CONDITIONAL: the etag rides the request and a 304 costs no body.
            _cache.MarkStale(uri, kind);
            _answers.Remove(key);
        }
        else
        {
            if (_answers.TryGet(key, out var cached)) return Task.FromResult(cached as T);
            if (_negatives.Contains(uri, kind)) return Task.FromResult<T?>(null);
        }
        return LoadAsync(key, parse, surface, ct);
    }

    async Task<T?> LoadAsync<T>((string Uri, Xm.ExtensionKind Kind) key, Func<ByteString, T?> parse,
                                TraitSurface surface, CancellationToken ct) where T : class
    {
        // Publish the slot BEFORE the load. Whoever wins GetOrAdd owns the fetch; everyone else awaits the same task.
        var slot = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var existing = _inFlight.GetOrAdd(key, slot);
        if (!ReferenceEquals(existing, slot))
            return await existing.Task.WaitAsync(ct).ConfigureAwait(false) as T;

        _ = FillAsync(key, parse, slot, surface);
        // The owner waits through the SAME awaiter as everyone else, so its own cancellation detaches it without
        // cancelling a load the other waiters still need.
        return await slot.Task.WaitAsync(ct).ConfigureAwait(false) as T;
    }

    async Task FillAsync<T>((string Uri, Xm.ExtensionKind Kind) key, Func<ByteString, T?> parse,
                            TaskCompletionSource<object?> slot, TraitSurface surface) where T : class
    {
        object? answer = null;
        try
        {
            // CancellationToken.None on purpose (see the header): the shared load outlives any one caller's navigation.
            var payload = await _cache.GetPayloadAsync(key.Uri, key.Kind, CancellationToken.None, surface.ClientFeatureId())
                                      .ConfigureAwait(false);
            if (payload is { IsEmpty: false })
            {
                try { answer = parse(payload); }
                catch (Exception ex)
                {
                    // Undecodable is a NULL ANSWER, not a failure: the bytes are what they are, and re-fetching them
                    // will hand us the same bytes again.
                    _log.Event(WaveeLogLevel.Warning, "extensions.parse.fail", "extension parse failed", ex: ex,
                        fields: [WaveeLogField.Of("kind", key.Kind.ToString()), WaveeLogField.Of("uri", key.Uri)]);
                    answer = null;
                }
            }
            // 404 / empty / undecodable — cache the null AND memoize it, so neither this reader nor the row pipeline
            // asks again this session.
            if (answer is null) _negatives.Add(key.Uri, key.Kind);
            _answers.Set(key, answer);
        }
        catch (Exception ex)
        {
            // A TRANSPORT failure caches nothing and memoizes nothing — "the network was down" is not "no such
            // extension", and the next open must be free to retry.
            _log.Event(WaveeLogLevel.Warning, "extensions.read.fail", "extension read failed", ex: ex,
                fields: [WaveeLogField.Of("kind", key.Kind.ToString()), WaveeLogField.Of("uri", key.Uri)]);
            answer = null;
        }
        finally
        {
            // The value-matching overload: only ever remove OUR slot, and always — a load that completed synchronously
            // must not leave a resolved slot parked under the key forever.
            _inFlight.TryRemove(new KeyValuePair<(string Uri, Xm.ExtensionKind Kind), TaskCompletionSource<object?>>(key, slot));
            slot.TrySetResult(answer);
        }
    }

    public async Task<IReadOnlyDictionary<string, T>> ReadManyAsync<T>(IReadOnlyList<string> uris, Xm.ExtensionKind kind,
                                                                       Func<ByteString, T?> parse, TraitSurface surface,
                                                                       CancellationToken ct = default) where T : class
    {
        ArgumentNullException.ThrowIfNull(parse);
        var result = new Dictionary<string, T>(StringComparer.Ordinal);
        if (uris is null || uris.Count == 0 || kind == Xm.ExtensionKind.UnknownExtension) return result;

        List<(string Uri, Xm.ExtensionKind Kind)>? misses = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < uris.Count; i++)
        {
            string uri = uris[i];
            if (string.IsNullOrEmpty(uri) || !seen.Add(uri)) continue;
            var key = (uri, kind);
            if (_answers.TryGet(key, out var cached)) { if (cached is T hit) result[uri] = hit; continue; }
            if (_negatives.Contains(uri, kind)) continue;
            (misses ??= new List<(string, Xm.ExtensionKind)>()).Add((uri, kind));
        }
        if (misses is null) return result;

        string? clientFeatureId = surface.ClientFeatureId();
        for (int start = 0; start < misses.Count; start += MetadataChunking.MaxEntitiesPerRequest)
        {
            int count = Math.Min(MetadataChunking.MaxEntitiesPerRequest, misses.Count - start);
            var page = misses.GetRange(start, count);
            IReadOnlyDictionary<(string Uri, Xm.ExtensionKind Kind), CachedExtension> values;
            try { values = await _cache.GetAsync(page, ct, clientFeatureId).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _log.Event(WaveeLogLevel.Warning, "extensions.read.fail", "extension batch read failed", ex: ex,
                    fields: [WaveeLogField.Of("kind", kind.ToString()), WaveeLogField.Of("uris", count)]);
                return result;   // nothing cached, nothing memoized — the next pass retries the whole page
            }

            foreach (var (uri, _) in page)
            {
                // Absent from the response is NOT an outcome (the same rule the etag cache enforces) — leave it
                // unmemoized so the next pass asks again.
                if (!values.TryGetValue((uri, kind), out var cachedExt)) continue;
                object? answer = null;
                if (!cachedExt.Missing && cachedExt.Payload is { IsEmpty: false } payload)
                {
                    try { answer = parse(payload); }
                    catch (Exception ex)
                    {
                        _log.Event(WaveeLogLevel.Warning, "extensions.parse.fail", "extension parse failed", ex: ex,
                            fields: [WaveeLogField.Of("kind", kind.ToString()), WaveeLogField.Of("uri", uri)]);
                    }
                }
                if (answer is null) _negatives.Add(uri, kind);
                _answers.Set((uri, kind), answer);
                if (answer is T typed) result[uri] = typed;
            }
        }
        return result;
    }

    public async Task<IReadOnlyDictionary<(string Uri, Xm.ExtensionKind Kind), CachedExtension>> ReadRawAsync(
        IReadOnlyList<(string Uri, Xm.ExtensionKind Kind)> reqs, TraitSurface surface,
        CancellationToken ct = default, ReadOptions options = default)
    {
        if (reqs is null || reqs.Count == 0) return NoRaw;

        // Group the kinds under their uri: MetadataChunking.ExtensionRanges only flushes a chunk on a uri boundary, so
        // an interleaved list would split one entity across two POSTs.
        var grouped = GroupByUri(reqs);
        if (options.Revalidate)
            foreach (var (uri, kind) in grouped) _cache.MarkStale(uri, kind);

        try { return await _cache.GetAsync(grouped, ct, surface.ClientFeatureId()).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _log.Event(WaveeLogLevel.Warning, "extensions.readraw.fail", "raw extension read failed", ex: ex,
                fields: [WaveeLogField.Of("reqs", grouped.Count)]);
            return NoRaw;
        }
    }

    static List<(string Uri, Xm.ExtensionKind Kind)> GroupByUri(IReadOnlyList<(string Uri, Xm.ExtensionKind Kind)> reqs)
    {
        var order = new List<string>();
        var byUri = new Dictionary<string, List<Xm.ExtensionKind>>(StringComparer.Ordinal);
        for (int i = 0; i < reqs.Count; i++)
        {
            var (uri, kind) = reqs[i];
            if (string.IsNullOrEmpty(uri) || kind == Xm.ExtensionKind.UnknownExtension) continue;
            if (!byUri.TryGetValue(uri, out var kinds)) { byUri[uri] = kinds = new List<Xm.ExtensionKind>(4); order.Add(uri); }
            if (!kinds.Contains(kind)) kinds.Add(kind);
        }
        var flat = new List<(string, Xm.ExtensionKind)>(reqs.Count);
        foreach (string uri in order)
            foreach (var kind in byUri[uri])
                flat.Add((uri, kind));
        return flat;
    }

    public void Seed<T>(string uri, Xm.ExtensionKind kind, T? answer) where T : class
    {
        if (string.IsNullOrEmpty(uri) || kind == Xm.ExtensionKind.UnknownExtension) return;
        // Only the parsed answer is published — a seed is somebody else's knowledge, not a wire outcome, so it never
        // writes the negative memo (which the etag cache and the trait pipeline both trust).
        _answers.Set((uri, kind), answer);
    }
}

/// <summary>A plain bounded LRU. There was no reusable one in the app (the caches that exist are all
/// <c>Resource&lt;,&gt;</c>-backed freshness caches, which is the wrong shape here: these answers have no TTL, no etag
/// and no refetch — they are just parsed bytes worth keeping until the cap says otherwise).</summary>
sealed class BoundedLru<TKey, TValue> where TKey : notnull
{
    readonly int _cap;
    readonly Dictionary<TKey, LinkedListNode<KeyValuePair<TKey, TValue>>> _map;
    readonly LinkedList<KeyValuePair<TKey, TValue>> _order = new();
    readonly object _gate = new();

    public BoundedLru(int cap) => (_cap, _map) = (Math.Max(1, cap), new Dictionary<TKey, LinkedListNode<KeyValuePair<TKey, TValue>>>(Math.Max(1, cap)));

    public int Count { get { lock (_gate) return _map.Count; } }

    public bool TryGet(TKey key, out TValue value)
    {
        lock (_gate)
        {
            if (!_map.TryGetValue(key, out var node)) { value = default!; return false; }
            _order.Remove(node);
            _order.AddFirst(node);
            value = node.Value.Value;
            return true;
        }
    }

    public void Set(TKey key, TValue value)
    {
        lock (_gate)
        {
            if (_map.TryGetValue(key, out var node)) { _order.Remove(node); }
            else if (_map.Count >= _cap)
            {
                var oldest = _order.Last;
                if (oldest is not null) { _order.RemoveLast(); _map.Remove(oldest.Value.Key); }
            }
            _map[key] = _order.AddFirst(new KeyValuePair<TKey, TValue>(key, value));
        }
    }

    public void Remove(TKey key)
    {
        lock (_gate)
        {
            if (!_map.Remove(key, out var node)) return;
            _order.Remove(node);
        }
    }
}
