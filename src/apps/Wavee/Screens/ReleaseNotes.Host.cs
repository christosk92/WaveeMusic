// ── Screens/ReleaseNotes.Host.cs ───────────────────────────────────────────────────────────────────────────────────
// the release-notes store (embedded → cache → release-asset ladder, MediaPath, the rolling index as a Volatile field,
// the budgeted issue states), the LOADERS that assemble a whole page view / the after-update plate's payload off the
// UI thread, and the process-wide store + GitHub pool + the updater's two notes seams. The links, the date format, the
// version sentence's shape, the after-update gate and the small render decisions the page, rail, chips and avatars
// take moved to `ReleaseNotes.cs` (CORE, batch R2) — they touch no engine, network or disk. The few that need an app
// type stay HERE (G-265), because `Wavee.ReleaseTool` compiles `ReleaseNotes.cs` + `ReleaseNotes.Model.cs` by name and
// has none of them: `AfterUpdateGate.Consume` (settings + `Platform.Keys`), `RailMarkerFor` (`Notify`),
// `KindPillLocKey` / `SectionTitleLocKey` (the generated `Strings.*` constants) and `SlideId` (`HighlightItem`). The
// launch arming that WRITES pendingFrom is `Update.LaunchVersion.Arm` (Platform/Update.Host.cs); this file only
// consumes it.
//
// Role: SHELL
// Owner: R
// Wave: 6
// Budget: 520 lines
// Spec: ch 28 §9.5
//
// ENGINE-FREE BY CONSTRUCTION (BCL + the CORE records + `Platform.Settings`/`Log` + generated loc-key CONSTANTS): the
// store test drives the REAL class over a scripted HttpMessageHandler and a temp folder (ch 28 §8, the
// ReleaseNotesStore row). Nothing here throws into a caller: a missing document is null, a failed fetch is a log line
// in category "whatsnew".

using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Wavee;

public static partial class ReleaseNotes
{
    // ══ THE VIEW MODEL + THE LOADERS (ch 28 §0.10: one whole view or nothing) ═══════════════════════════════════════

    /// <summary>One release as the page renders it: the document, the live issue states over it, the two rail facts.</summary>
    public sealed record ReleaseEntry(ReleaseNotesDocument Doc, IssueStateCache? IssueStates, bool IsYou, bool IsUnread);

    /// <summary>Everything the page needs, assembled OFF the UI thread and published in one write.</summary>
    public sealed record ReleaseNotesView(ReleaseEntry[] Releases, HighlightItem[] MergedHighlights, ReleaseNotesIndex? Index,
        string SelectedVersion, string LastSeen);

    /// <summary>A highlight ready to render, its poster already resolved to a real path (or null) by the LOADER.</summary>
    public sealed record HighlightItem(ReleaseHighlight Highlight, ReleaseNotesDocument Doc, string? Poster);

    // ══ THE APP-BOUND DECISIONS (pure, but they name app types the ReleaseTool cherry-pick does not have — G-265) ═══════

    /// <summary>The strip's key recipe, mirrored by the viewer's slides: doc version + highlight id, the index as a last resort.</summary>
    public static string SlideId(HighlightItem item, int index)
        => item.Doc.Version + ":" + (item.Highlight.Id is { Length: > 0 } id ? id : index.ToString(CultureInfo.InvariantCulture));

    /// <summary>Exclusive markers: the running build is YOU and never a dot; the SELECTED row is never unread.</summary>
    public static RailMarker RailMarkerFor(string version, string? selected, string running, string lastSeen)
    {
        if (string.Equals(version, running, StringComparison.Ordinal)) return RailMarker.You;
        bool selectedRow = string.Equals(version, selected, StringComparison.Ordinal);
        return !selectedRow && Notify.AppUpdateVersion.IsNewer(version, lastSeen) ? RailMarker.Unread : RailMarker.None;
    }

    /// <summary>store → Microsoft Store · rebuilt · improved · EVERYTHING ELSE (an unknown kind too) → New.</summary>
    public static string KindPillLocKey(string? kind, bool store) => store ? Strings.WhatsNew.Kind.Store : kind switch
    {
        "rebuilt" => Strings.WhatsNew.Kind.Rebuilt,
        "improved" => Strings.WhatsNew.Kind.Improved,
        _ => Strings.WhatsNew.Kind.New,
    };

    public static string SectionTitleLocKey(string? kind) => kind switch
    {
        "added" => Strings.WhatsNew.Section.Added,
        "changed" => Strings.WhatsNew.Section.Changed,
        "fixed" => Strings.WhatsNew.Section.Fixed,
        "removed" => Strings.WhatsNew.Section.Removed,
        "deprecated" => Strings.WhatsNew.Section.Deprecated,
        "security" => Strings.WhatsNew.Section.Security,
        _ => Strings.WhatsNew.Section.Known,
    };

    public static partial class AfterUpdateGate
    {
        /// <summary>§9.4 option (a), the deliberate change: the one-shot is consumed only when the plate is about to OPEN.
        /// A load that resolved to nothing (no store, no document, a throw) opens nothing and leaves the key ARMED for the
        /// next launch — 0.2.9 cleared it first and then sat on an empty plate forever.</summary>
        /// <returns>True when the caller should open the plate now.</returns>
        public static bool Consume(IAppSettings settings, bool opening)
        {
            if (opening) settings.Set(Platform.Keys.ReleaseNotesPendingFrom, "");
            return opening;
        }
    }

    /// <summary>embedded → cache → asset for ONE release (an explicit arg) or the whole skipped range (no arg), with
    /// <paramref name="lastSeen"/> read by the caller BEFORE it advanced the setting (§0.11). Null = nothing to show.
    /// Cancellation throws; everything else is absorbed by the store.</summary>
    public static async Task<ReleaseNotesView?> LoadViewAsync(Store store, string? versionArg, string lastSeen, RunningBuild me, CancellationToken ct)
    {
        string selected = versionArg is { Length: > 0 } v ? v : me.Core;
        await store.RefreshIndexAsync(ct).ConfigureAwait(false);   // a nicety: on failure the rail hides, the document loads
        var index = store.IndexSnapshot();

        var wanted = new List<string>(4);
        if (versionArg is { Length: > 0 }) wanted.Add(selected);      // the rail / a deep link means THAT version only
        else
        {
            if (index is not null)
                foreach (var e in ReleaseNotesRange.Between(lastSeen, me.Core, index, me.Channel))
                    if (e?.Version is { Length: > 0 } ev) wanted.Add(ev);
            if (wanted.Count == 0) wanted.Add(selected);
        }

        var entries = new List<ReleaseEntry>(wanted.Count);
        foreach (string want in wanted)
        {
            ct.ThrowIfCancellationRequested();
            if (await store.GetAsync(want, ct).ConfigureAwait(false) is not { } doc) continue;
            entries.Add(new ReleaseEntry(doc, null, string.Equals(doc.Version, me.Core, StringComparison.Ordinal),
                Notify.AppUpdateVersion.IsNewer(doc.Version, lastSeen)));
        }
        ct.ThrowIfCancellationRequested();
        if (entries.Count == 0) return null;
        return new ReleaseNotesView(entries.ToArray(), MergeHighlights(entries, me.IsStore, store), index,
            entries[0].Doc.Version is { Length: > 0 } dv ? dv : selected, lastSeen);
    }

    /// <summary>The one deliberate second publish: live issue states over the NEWEST document only (the budget is per open).</summary>
    public static async Task<ReleaseNotesView> WithIssueStatesAsync(Store store, ReleaseNotesView view, CancellationToken ct)
    {
        var states = await store.RefreshIssueStatesAsync(view.Releases[0].Doc, ct).ConfigureAwait(false);
        var releases = (ReleaseEntry[])view.Releases.Clone();
        releases[0] = releases[0] with { IssueStates = states };
        return view with { Releases = releases };
    }

    /// <summary>Highlights MERGE across the stack, newest first, capped at <see cref="HighlightMax"/> VISIBLE cards.</summary>
    public static HighlightItem[] MergeHighlights(IReadOnlyList<ReleaseEntry> entries, bool isStoreInstall, Store? store)
    {
        var merged = new List<HighlightItem>(HighlightMax);
        foreach (var e in entries)
            foreach (var h in e.Doc.Highlights)
            {
                if (merged.Count >= HighlightMax) return merged.ToArray();
                if (HighlightVisibility.IsVisible(h, isStoreInstall)) merged.Add(new HighlightItem(h, e.Doc, ResolvePoster(h, store, e.Doc)));
            }
        return merged.ToArray();
    }

    /// <summary>The after-update plate's payload — the running build's document and its visible cards — or null when it
    /// resolved to nothing. The plate opens only once this has landed (§9.4 option (a)).</summary>
    public static async Task<(ReleaseNotesDocument Doc, HighlightItem[] Cards)?> LoadSummaryAsync(Store? store, RunningBuild me, CancellationToken ct)
    {
        if (store is null || await store.GetAsync(me.Core, ct).ConfigureAwait(false) is not { } doc) return null;
        var visible = HighlightVisibility.SelectVisible(doc.Highlights, me.IsStore, HighlightMax);
        var cards = new HighlightItem[visible.Count];
        for (int i = 0; i < visible.Count; i++) cards[i] = new HighlightItem(visible[i], doc, ResolvePoster(visible[i], store, doc));
        return (doc, cards);
    }

    /// <summary>A highlight's poster on disk, or null. LOADER-only: <c>File.Exists</c> per card per render is UI-thread I/O.</summary>
    public static string? ResolvePoster(ReleaseHighlight? h, Store? store, ReleaseNotesDocument? doc)
    {
        if (store is null || doc is null || h?.Media is not { } m) return null;
        string? src = string.Equals(m.Kind, "video", StringComparison.OrdinalIgnoreCase) ? m.Poster : m.Src;   // a video names its still
        if (string.IsNullOrEmpty(src)) return null;
        try { string path = store.MediaPath(doc, src); return path.Length > 0 && File.Exists(path) ? path : null; }
        catch (Exception) { return null; }
    }

    // ══ THE PROCESS-WIDE STORE ═══════════════════════════════════════════════════════════════════════════════════════

    static Store? s_notes;
    static HttpClient? s_github;

    /// <summary>The store every notes surface reads (the page, the plate, the updater's prefetch), built on first read from
    /// the build stamp — the notes ride the SAME download root as the update feed. Any thread.</summary>
    public static Store Notes
    {
        get
        {
            if (Volatile.Read(ref s_notes) is { } live) return live;
            var me = Platform.Version;
            var built = new Store(GitHubHttp, Platform.LocalFolder, me.FeedRelease, releasesRoot: me.UpdateBaseUrl);
            return Interlocked.CompareExchange(ref s_notes, built, null) ?? built;
        }
    }

    /// <summary>Replace the store (a harness, a loopback feed). The composition root does not need to call it.</summary>
    public static void UseStore(Store store) => Volatile.Write(ref s_notes, store);

    /// <summary>Fill the seams other owners left for this store: the updater's two notes seams
    /// (<c>Update.Host.PeekIndexEntry</c> / <c>PrefetchNotes</c>, read through static forwarders at call time, so the order
    /// against <c>Update.Host.Start</c> does not matter) and the diagnostics page's pipeline receipts.</summary>
    static void InstallSeams()
    {
        Update.Host.PeekIndexEntry ??= static quad => Notes.PeekIndex()?.Find(quad);
        Update.Host.PrefetchNotes ??= static quad => _ = Task.Run(() => Notes.PrefetchAsync(quad, CancellationToken.None));
        Diagnostics.NotesReportSource ??= static () =>
        {
            var d = Notes.DiagnosticsSnapshot();
            return new Diagnostics.NotesReport(d.LastSource, d.CacheRoot, d.EmbeddedRoot, d.FeedRelease, d.LastFetchUtc, d.LastFetchUrl,
                d.LastFetchStatus, d.IssueRequestsThisSession, d.RateLimitRemaining);
        };
    }

    /// <summary>The GitHub pool: a product-token User-Agent (the REST API refuses a request without one) and the
    /// <c>application/vnd.github+json</c> accept header, set once. Shareable with the updater's feed reads.</summary>
    public static HttpClient GitHubHttp
    {
        get
        {
            if (Volatile.Read(ref s_github) is { } live) return live;
            var client = new HttpClient(new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(2), PooledConnectionIdleTimeout = TimeSpan.FromMinutes(1),
                MaxConnectionsPerServer = 4, AutomaticDecompression = DecompressionMethods.All,
            }) { Timeout = TimeSpan.FromSeconds(15) };
            string arch = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64";
            if (!client.DefaultRequestHeaders.UserAgent.TryParseAdd(Platform.Version.UserAgent(RuntimeInformation.OSDescription, arch)))
                client.DefaultRequestHeaders.UserAgent.TryParseAdd("Wavee");   // GitHub only requires SOME user-agent
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            return Interlocked.CompareExchange(ref s_github, client, null) ?? client;
        }
    }

    // ══ THE STORE ════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>What the diagnostics page shows about the pipeline (0.2.9 <c>ReleaseNotesDiagnostics</c>).</summary>
    public readonly record struct StoreDiagnostics(string CacheRoot, string EmbeddedRoot, string FeedRelease, string LastSource,
        DateTimeOffset? LastFetchUtc, string LastFetchUrl, string LastFetchStatus, int RateLimitRemaining, long RateLimitReset,
        int IssueRequestsThisSession);

    /// <summary>ONE ladder behind every surface that shows notes. Each rung answers a different question: EMBEDDED
    /// (<c>Assets/whatsnew</c> beside the exe) is the running build's notes, always offline and never wrong; the CACHE
    /// (<c>&lt;app data&gt;\cache\whatsnew\&lt;semver&gt;</c>) holds other versions the user looked at; the release ASSET
    /// is the only rung that costs a request — a plain asset GET that spends no REST budget. The one budgeted surface is
    /// <see cref="RefreshIssueStatesAsync"/> (60/hour/IP unauthenticated), whose policy is <see cref="IssueStateBudget"/>'s.</summary>
    public sealed class Store
    {
        const string ApiRoot = "https://api.github.com/repos/";
        const string LogCategory = "whatsnew";
        const string DocName = "whatsnew.json";
        const string IndexName = "whatsnew-index.json";
        const long PrefetchWindowMs = 5 * 60 * 1000;   // a completed prefetch for one target suppresses a repeat this long

        static readonly IssueStateBudget s_issueBudget = new();   // the numbers' single owner; never restated here

        readonly HttpClient _http;
        readonly string _cacheRoot, _embeddedRoot, _feedRelease, _releasesRoot;
        readonly Lock _gate = new();
        string _lastSource = "none", _lastFetchUrl = "", _lastFetchStatus = "";
        DateTimeOffset? _lastFetchUtc;
        int _rateLimitRemaining = -1, _issueRequests;
        long _rateLimitReset, _lastPrefetchMs;
        string? _lastPrefetchTarget;

        /// <summary>A plain volatile field, NOT a signal: written on whatever thread an HTTP continuation finished on, and
        /// every reader is a loader that publishes its own view in one write.</summary>
        ReleaseNotesIndex? _index;

        /// <param name="appDataRoot">The profile folder; the cache is <c>cache\whatsnew</c> under it.</param>
        /// <param name="feedRelease">The rolling release the index lives on (build-time metadata).</param>
        /// <param name="embeddedRoot">This build's own notes; default <c>Assets/whatsnew</c> beside the exe.</param>
        /// <param name="releasesRoot">The stamped download root; blank ⇒ GitHub's (<see cref="WaveeVersionInfo.NormalizeUpdateBaseUrl"/>).</param>
        public Store(HttpClient http, string appDataRoot, string feedRelease, string? embeddedRoot = null, string? releasesRoot = null)
        {
            ArgumentNullException.ThrowIfNull(http);
            _http = http;
            _cacheRoot = Path.Combine(appDataRoot ?? "", "cache", "whatsnew");
            _embeddedRoot = embeddedRoot ?? Path.Combine(AppContext.BaseDirectory, "Assets", "whatsnew");
            _feedRelease = string.IsNullOrWhiteSpace(feedRelease) ? "wavee-stable" : feedRelease.Trim();
            _releasesRoot = WaveeVersionInfo.NormalizeUpdateBaseUrl(releasesRoot);
        }

        public string EmbeddedRoot => _embeddedRoot;
        public string CacheRoot => _cacheRoot;

        /// <summary>The index we hold, or null. Any thread.</summary>
        public ReleaseNotesIndex? IndexSnapshot() => Volatile.Read(ref _index);

        /// <summary>The notes for <paramref name="semver"/>: embedded (this build only) → cache → release asset. Null = nowhere.</summary>
        public async Task<ReleaseNotesDocument?> GetAsync(string semver, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(semver)) return null;
            semver = semver.Trim();
            if (!IsSafeVersion(semver)) return null;

            // The embedded copy is THIS build's notes and nothing else: handing it back for another version is a silent lie.
            if (TryReadDocument(Path.Combine(_embeddedRoot, DocName)) is { } embedded && string.Equals(embedded.Version, semver, StringComparison.Ordinal))
            { Note("embedded"); return embedded; }

            string path = Path.Combine(_cacheRoot, semver, DocName);
            if (TryReadDocument(path) is { } cached) { Note("cache"); return cached; }

            byte[]? bytes = await GetBytesAsync(_releasesRoot + ReleaseNotesValidation.TagPrefix + semver + "/" + DocName, ct).ConfigureAwait(false);
            var doc = bytes is null ? null : Deserialize(bytes);
            if (doc is null) { Note("none"); return null; }
            TryWrite(path, bytes!);
            Note("remote");
            return doc;
        }

        /// <summary>The index we already have (memory, else the disk cache). NO network — the update check's hot path.</summary>
        public Task<ReleaseNotesIndex?> PeekIndexAsync(CancellationToken ct) => Task.FromResult(PeekIndex());

        /// <summary>The synchronous half of <see cref="PeekIndexAsync"/> (the updater's seam is synchronous).</summary>
        public ReleaseNotesIndex? PeekIndex()
        {
            if (IndexSnapshot() is { } live) return live;
            var cached = TryRead(Path.Combine(_cacheRoot, IndexName), ReleaseNotesJsonContext.Default.ReleaseNotesIndex);
            if (cached is not null) Volatile.Write(ref _index, cached);
            return cached;
        }

        /// <summary>Warm the notes for an offered update. Single-flight per target inside a five-minute window: both the
        /// check and the scheduler that drove it ask, and neither should know whether the other did.</summary>
        public async Task PrefetchAsync(string? targetQuadOrSemver, CancellationToken ct)
        {
            string key = targetQuadOrSemver ?? "";
            long now = Stopwatch.GetTimestamp() * 1000 / Stopwatch.Frequency;
            lock (_gate)
            {
                if (string.Equals(_lastPrefetchTarget, key, StringComparison.Ordinal) && now - _lastPrefetchMs < PrefetchWindowMs) return;
                _lastPrefetchTarget = key;
                _lastPrefetchMs = now;
            }
            await RefreshIndexAsync(ct).ConfigureAwait(false);
            if (!ct.IsCancellationRequested && IndexSnapshot()?.Find(key) is { Version.Length: > 0 } entry)
                _ = await GetAsync(entry.Version, ct).ConfigureAwait(false);
        }

        /// <summary>Re-read the rolling index into memory + the cache. A failure keeps whatever index we had.</summary>
        public async Task RefreshIndexAsync(CancellationToken ct)
        {
            byte[]? bytes = await GetBytesAsync(_releasesRoot + _feedRelease + "/" + IndexName, ct).ConfigureAwait(false);
            if (bytes is null) return;
            ReleaseNotesIndex? index;
            try { index = JsonSerializer.Deserialize(bytes, ReleaseNotesJsonContext.Default.ReleaseNotesIndex); }
            catch (Exception ex) { Log.Warn(LogCategory, "index parse failed", ex); return; }
            if (index is null) return;
            TryWrite(Path.Combine(_cacheRoot, IndexName), bytes);
            Volatile.Write(ref _index, index);
        }

        /// <summary>Refresh the GitHub state of the issues <paramref name="doc"/> references and return the merged cache. A
        /// stopped refresh is not an error — the chips render the tool's snapshot.</summary>
        public async Task<IssueStateCache> RefreshIssueStatesAsync(ReleaseNotesDocument doc, CancellationToken ct)
        {
            var cache = TryRead(Path.Combine(_cacheRoot, "issues.json"), ReleaseNotesJsonContext.Default.IssueStateCache) ?? new IssueStateCache();
            if (doc is null) return cache;
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            bool dirty = false;
            foreach (string key in s_issueBudget.Plan(IssueKeys(doc), cache, now))
            {
                if (ct.IsCancellationRequested) break;
                int hash = key.LastIndexOf('#');
                if (hash <= 0 || !int.TryParse(key.AsSpan(hash + 1), NumberStyles.None, CultureInfo.InvariantCulture, out int number)) continue;
                Interlocked.Increment(ref _issueRequests);
                var (state, stop) = await FetchIssueAsync(key[..hash], number, ct).ConfigureAwait(false);
                if (state is not null) { state.FetchedAtMs = now; cache.Set(key, state); dirty = true; }
                if (stop) break;
            }
            if (dirty) TryWrite(Path.Combine(_cacheRoot, "issues.json"), JsonSerializer.SerializeToUtf8Bytes(cache, ReleaseNotesJsonContext.Default.IssueStateCache));
            return cache;
        }

        async Task<(IssueState? State, bool Stop)> FetchIssueAsync(string repo, int number, CancellationToken ct)
        {
            string url = ApiRoot + repo + "/issues/" + number.ToString(CultureInfo.InvariantCulture);
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
                string? remaining = Observe(url, resp);
                bool stop = s_issueBudget.ShouldStop((int)resp.StatusCode, remaining);
                if (stop && (resp.StatusCode == HttpStatusCode.Forbidden || resp.StatusCode == (HttpStatusCode)429))
                {
                    Log.Info(LogCategory, "issue budget exhausted at " + url);
                    return (null, true);
                }
                if (!resp.IsSuccessStatusCode) return (null, stop);
                using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                using var json = await JsonDocument.ParseAsync(stream, default, ct).ConfigureAwait(false);
                var root = json.RootElement;
                return (new IssueState
                {
                    State = root.TryGetProperty("state", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() ?? "open" : "open",
                    StateReason = root.TryGetProperty("state_reason", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null,
                    Title = root.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() ?? "" : "",
                }, stop);
            }
            catch (OperationCanceledException) { return (null, true); }
            catch (Exception ex) { Failed(url, ex); Log.Warn(LogCategory, "issue fetch failed " + url, ex); return (null, false); }
        }

        static IEnumerable<string> IssueKeys(ReleaseNotesDocument doc)
        {
            foreach (var section in doc.Sections ?? [])
                foreach (var item in section?.Items ?? [])
                    foreach (var issue in item?.Issues ?? [])
                        if (issue is { Repo.Length: > 0, Number: > 0 }) yield return IssueStateCache.Key(issue.Repo, issue.Number);
        }

        /// <summary>A document-relative media reference → a local path, or "". The embedded copy wins; everything else
        /// resolves inside that version's cache folder, and the result is PROVEN to sit under its root.</summary>
        public string MediaPath(ReleaseNotesDocument doc, string src)
        {
            if (doc is null || string.IsNullOrWhiteSpace(src)) return "";
            if (src[0] == '/' || src[0] == '\\') return "";                      // an absolute reference, never media
            string rel = src.Replace('\\', '/');
            if (rel.Contains("..", StringComparison.Ordinal) || rel.Contains(':') || Path.IsPathRooted(rel)) return "";
            string relative = rel.Replace('/', Path.DirectorySeparatorChar);
            if (Contained(_embeddedRoot, relative) is { Length: > 0 } embedded && File.Exists(embedded)) return embedded;
            return IsSafeVersion(doc.Version) ? Contained(Path.Combine(_cacheRoot, doc.Version), relative) ?? "" : "";
        }

        static string? Contained(string root, string relative)
        {
            try
            {
                string full = Path.GetFullPath(Path.Combine(root, relative));
                string fullRoot = Path.GetFullPath(root);
                if (!fullRoot.EndsWith(Path.DirectorySeparatorChar)) fullRoot += Path.DirectorySeparatorChar;
                return full.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase) ? full : null;
            }
            catch (Exception) { return null; }
        }

        public StoreDiagnostics DiagnosticsSnapshot()
        {
            lock (_gate)
                return new StoreDiagnostics(_cacheRoot, _embeddedRoot, _feedRelease, _lastSource, _lastFetchUtc, _lastFetchUrl,
                    _lastFetchStatus, _rateLimitRemaining, _rateLimitReset, Volatile.Read(ref _issueRequests));
        }

        async Task<byte[]?> GetBytesAsync(string url, CancellationToken ct)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
                Observe(url, resp);
                if (resp.IsSuccessStatusCode) return await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
                Log.Info(LogCategory, "fetch " + (int)resp.StatusCode + " " + url);
                return null;
            }
            catch (OperationCanceledException) { return null; }
            catch (Exception ex) { Failed(url, ex); Log.Warn(LogCategory, "fetch failed " + url, ex); return null; }
        }

        /// <summary>Record the attempt + both rate-limit headers for diagnostics, and hand back the RAW remaining header —
        /// <see cref="IssueStateBudget.ShouldStop"/> parses it itself.</summary>
        string? Observe(string url, HttpResponseMessage resp)
        {
            string? remaining = resp.Headers.TryGetValues("x-ratelimit-remaining", out var rv) ? rv.FirstOrDefault() : null;
            string? reset = resp.Headers.TryGetValues("x-ratelimit-reset", out var sv) ? sv.FirstOrDefault() : null;
            lock (_gate)
            {
                _lastFetchUtc = DateTimeOffset.UtcNow;
                _lastFetchUrl = url;
                _lastFetchStatus = ((int)resp.StatusCode).ToString(CultureInfo.InvariantCulture);
                if (int.TryParse(remaining, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)) _rateLimitRemaining = n;
                if (long.TryParse(reset, NumberStyles.Integer, CultureInfo.InvariantCulture, out long r)) _rateLimitReset = r;
            }
            return remaining;
        }

        void Failed(string url, Exception ex) { lock (_gate) { _lastFetchUtc = DateTimeOffset.UtcNow; _lastFetchUrl = url; _lastFetchStatus = ex.Message; } }

        void Note(string source) { lock (_gate) _lastSource = source; }

        /// <summary>Read + deserialize + NORMALIZE: no surface downstream guards against a hand-authored <c>"sections": null</c>.</summary>
        ReleaseNotesDocument? TryReadDocument(string path)
        {
            try { return File.Exists(path) ? Deserialize(File.ReadAllBytes(path)) : null; }
            catch (Exception ex) { Log.Warn(LogCategory, "read failed " + path, ex); return null; }
        }

        static ReleaseNotesDocument? Deserialize(byte[] bytes)
        {
            try
            {
                var doc = JsonSerializer.Deserialize(bytes, ReleaseNotesJsonContext.Default.ReleaseNotesDocument);
                doc?.Normalize();
                return doc;
            }
            catch (Exception ex) { Log.Warn(LogCategory, "parse failed", ex); return null; }
        }

        static T? TryRead<T>(string path, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info) where T : class
        {
            try { return File.Exists(path) ? JsonSerializer.Deserialize(File.ReadAllBytes(path), info) : null; }
            catch (Exception ex) { Log.Warn(LogCategory, "cache read failed " + path, ex); return null; }
        }

        static void TryWrite(string path, byte[] bytes)
        {
            try
            {
                if (Path.GetDirectoryName(path) is { Length: > 0 } dir) Directory.CreateDirectory(dir);
                File.WriteAllBytes(path, bytes);
            }
            catch (Exception ex) { Log.Warn(LogCategory, "cache write failed " + path, ex); }
        }

        /// <summary>A version is a PATH SEGMENT and part of a URL, so it is whitelisted: a leading DIGIT (rejects "." and
        /// "..", hidden and switch-shaped names), then digits, letters, dots and dashes, at most 40.</summary>
        public static bool IsSafeVersion(string? v)
        {
            if (string.IsNullOrEmpty(v) || v.Length > 40 || !char.IsAsciiDigit(v[0])) return false;
            foreach (char c in v) if (!char.IsAsciiLetterOrDigit(c) && c != '.' && c != '-') return false;
            return true;
        }
    }
}
