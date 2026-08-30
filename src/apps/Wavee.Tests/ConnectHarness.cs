using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend;
using Wavee.Core;

namespace Wavee.Tests;

// Shared Connect test harness: build real dealer WS frame JSON (message + request shapes), gzip/base64 helpers, a tiny
// observer, and a GC-allocation gate for the ingest/projection hot path. Grows across Stages A→I.
static class ConnectHarness
{
    public static byte[] Gzip(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionMode.Compress, leaveOpen: true)) gz.Write(data, 0, data.Length);
        return ms.ToArray();
    }

    public static string B64(byte[] data) => Convert.ToBase64String(data);

    /// <summary>A dealer MESSAGE frame: one (optionally gzipped) payload + headers (where the Spotify-Connection-Id lives).</summary>
    public static byte[] MessageFrame(string uri, byte[] payload, bool gzip = false, (string, string)[]? headers = null)
    {
        var buf = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buf))
        {
            w.WriteStartObject();
            w.WriteString("type", "message");
            w.WriteString("uri", uri);
            w.WriteStartObject("headers");
            if (gzip) w.WriteString("Transfer-Encoding", "gzip");
            if (headers != null) foreach (var (k, v) in headers) w.WriteString(k, v);
            w.WriteEndObject();
            w.WriteStartArray("payloads");
            w.WriteStringValue(B64(gzip ? Gzip(payload) : payload));
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return buf.WrittenSpan.ToArray();
    }

    /// <summary>A dealer REQUEST frame: key + message_ident + a SINGULAR payload {compressed: base64(gzip(json))}.</summary>
    public static byte[] RequestFrame(string key, string ident, string commandJson, (string, string)[]? headers = null)
    {
        var buf = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buf))
        {
            w.WriteStartObject();
            w.WriteString("type", "request");
            w.WriteString("key", key);
            w.WriteString("message_ident", ident);
            if (headers != null) { w.WriteStartObject("headers"); foreach (var (k, v) in headers) w.WriteString(k, v); w.WriteEndObject(); }
            w.WriteStartObject("payload");
            w.WriteString("compressed", B64(Gzip(Encoding.UTF8.GetBytes(commandJson))));
            w.WriteEndObject();
            w.WriteEndObject();
        }
        return buf.WrittenSpan.ToArray();
    }

    /// <summary>GC allocation delta (bytes) for a hot-path action, after a warm-up pass. The Connect ingest gate budgets
    /// against this (bounded, not zero — the dealer/projection layer is the event-driven app tier, outside frame phases 6-13).</summary>
    public static long AllocDelta(Action action, int iters = 1)
    {
        action();   // JIT warm-up + first-touch allocations excluded
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < iters; i++) action();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    public static IObserver<T> Obs<T>(Action<T> onNext) => new Inline<T>(onNext);

    sealed class Inline<T>(Action<T> onNext) : IObserver<T>
    {
        public void OnCompleted() { }
        public void OnError(Exception error) { }
        public void OnNext(T value) => onNext(value);
    }
}

/// <summary>A test IContextResolver: any context uri resolves to a fixed track list (uid "uid{i}", 60 s each); skip_to is
/// honored (uid→uri→index). Stands in for LiveContextResolver in the headless controller tests.</summary>
public sealed class FakeContextResolver : IContextResolver
{
    readonly QueuedTrack[] _tracks;

    /// <summary>What <see cref="ResolveRadioSeedAsync"/> returns (the resolved radio playlist uri) — null = no radio.
    /// Set by the StartRadioAsync controller tests.</summary>
    public string? RadioSeedResult;

    /// <summary>The continuation url ResolveAsync reports (null = single page, the default). The restore-heal tests set it
    /// with <see cref="NextPage"/> to prove paging/station continuation survives a recovery.</summary>
    public string? NextPageUrlResult;

    /// <summary>Whether ResolveAsync reports an infinite (station/radio) context.</summary>
    public bool IsInfiniteResult;

    /// <summary>Resolves a continuation url → a page. Null (default) yields <see cref="ContextPage.Empty"/>.</summary>
    public Func<string, ContextPage>? NextPage;

    /// <summary>Counts ResolveAsync calls (the recovery-heal tests assert the background resolve happened).</summary>
    public int ResolveCalls;

    /// <summary>Counts <see cref="ResolveAutoplayAsync"/> calls. Autoplay is a SPOTIFY station: a context outside its
    /// catalog (a playback module's link, a local folder) has none, so asking is a guaranteed 400 + a warning at every
    /// end-of-context. The module-playable tests assert this stays at 0.</summary>
    public int AutoplayCalls;

    public FakeContextResolver(params string[] uris)
    {
        _tracks = new QueuedTrack[uris.Length];
        for (int i = 0; i < uris.Length; i++) _tracks[i] = new QueuedTrack(Trk(uris[i]), "uid" + i);
    }

    public Task<ResolvedContext> ResolveAsync(ContextSpec spec, CancellationToken ct = default)
    {
        Interlocked.Increment(ref ResolveCalls);
        IReadOnlyList<QueuedTrack> tracks = _tracks;
        if (spec.EmbeddedPages is { Count: > 0 } pages)   // a sorted/custom-ordered page sent inline wins over the fixed list
        {
            var arr = new QueuedTrack[pages.Count];
            for (int i = 0; i < pages.Count; i++)
                arr[i] = new QueuedTrack(Trk(pages[i].Uri), pages[i].Uid, pages[i].Provider, pages[i].Metadata);
            tracks = arr;
        }
        int start = ContextResolve.ResolveStartIndex(tracks, spec);
        return Task.FromResult(new ResolvedContext(tracks, start, null, NextPageUrlResult, IsInfiniteResult));
    }

    public Task<ContextPage> LoadMoreAsync(string nextPageUrl, CancellationToken ct = default)
        => Task.FromResult(NextPage?.Invoke(nextPageUrl) ?? ContextPage.Empty);

    public Task<ResolvedContext> ResolveAutoplayAsync(string contextUri, IReadOnlyList<string> recentTrackUris, CancellationToken ct = default)
    {
        Interlocked.Increment(ref AutoplayCalls);
        return Task.FromResult(ResolvedContext.Empty);
    }

    public Task<ResolvedContext> ResolveAutopodcastAsync(string contextUri, IReadOnlyList<string> recentEpisodeUris, CancellationToken ct = default)
        => Task.FromResult(ResolvedContext.Empty);

    public Task<string?> ResolveRadioSeedAsync(string seedUri, CancellationToken ct = default) => Task.FromResult(RadioSeedResult);

    /// <summary>When true, <see cref="HydrateAsync"/> answers with the URI-ONLY placeholder <c>LiveContextResolver</c>
    /// mints for a uri no catalogue owns (title == uri, no artists, no art, duration 0). That is the exact shape a
    /// restored playback-module playable used to come back as, so the owner-hydration tests can prove both halves: the
    /// module answering, and the module declining and this row surviving.</summary>
    public bool HydrateAsPlaceholder;

    public Task<IReadOnlyList<QueuedTrack>> HydrateAsync(IReadOnlyList<QueuedRef> refs, CancellationToken ct = default)
    {
        var arr = new QueuedTrack[refs.Count];
        for (int i = 0; i < refs.Count; i++)
            arr[i] = new QueuedTrack(HydrateAsPlaceholder ? Placeholder(refs[i].Uri) : Trk(refs[i].Uri),
                refs[i].Uid, refs[i].Provider, refs[i].Metadata);
        return Task.FromResult<IReadOnlyList<QueuedTrack>>(arr);
    }

    static Track Trk(string uri) => new(uri[(uri.LastIndexOf(':') + 1)..], uri, "T:" + uri,
        Array.Empty<ArtistRef>(), new AlbumRef("", "", ""), 60000, false, null);

    static Track Placeholder(string uri) => new(uri[(uri.LastIndexOf(':') + 1)..], uri, uri,
        Array.Empty<ArtistRef>(), new AlbumRef("", "", ""), 0, false, null);
}
