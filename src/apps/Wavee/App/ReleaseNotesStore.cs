using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Core.ReleaseNotes;

namespace Wavee;

/// <summary>What the diagnostics page shows about the release-notes pipeline: where the document came from, where the
/// cache lives, when we last talked to GitHub and what the REST budget said.</summary>
/// <param name="CacheRoot">The per-version cache folder root (<c>%LOCALAPPDATA%\Wavee\cache\whatsnew</c>).</param>
/// <param name="EmbeddedRoot">The folder beside the exe that carries THIS build's own notes + media.</param>
/// <param name="FeedRelease">The rolling release the index is fetched from (build-time metadata).</param>
/// <param name="LastSource">Where the last <see cref="ReleaseNotesStore.GetAsync"/> resolved from: embedded / cache / remote / none.</param>
/// <param name="LastFetchUtc">The last network attempt, or null when this process has made none.</param>
/// <param name="LastFetchUrl">The URL of that attempt.</param>
/// <param name="LastFetchStatus">Its HTTP status text, or the exception message.</param>
/// <param name="RateLimitRemaining">The last <c>x-ratelimit-remaining</c> seen on an api.github.com reply, or -1.</param>
/// <param name="RateLimitReset">The last <c>x-ratelimit-reset</c> seen (unix seconds), or 0.</param>
/// <param name="IssueRequestsThisSession">How many issue lookups this process has actually spent.</param>
public readonly record struct ReleaseNotesDiagnostics(
    string CacheRoot,
    string EmbeddedRoot,
    string FeedRelease,
    string LastSource,
    DateTimeOffset? LastFetchUtc,
    string LastFetchUrl,
    string LastFetchStatus,
    int RateLimitRemaining,
    long RateLimitReset,
    int IssueRequestsThisSession);

/// <summary>
/// The release-notes ("What's new") document store: ONE ladder — <b>embedded → cache → release asset</b> — behind
/// every surface that shows notes (the page, the after-update plate, the update toast's highlight line).
/// <para>
/// The ladder is not a fallback chain bolted on for robustness; each rung answers a different question. The
/// <b>embedded</b> copy (<c>Assets/whatsnew/whatsnew.json</c>, laid down beside the exe by the pack script) is the
/// notes for the version that is running — always present, always offline, never wrong. The <b>cache</b>
/// (<c>%LOCALAPPDATA%\Wavee\cache\whatsnew\&lt;semver&gt;\</c>) holds notes for OTHER versions the user has looked at
/// (the "since you last looked" stack, and the pre-fetched notes for an update that has been offered). The
/// <b>release asset</b> is the only rung that costs a request, and it is a plain asset GET — not the REST API — so it
/// spends no rate-limit budget at all.
/// </para>
/// <para>
/// Nothing here throws. A missing document is <see langword="null"/> and the caller shows what it has; a failed fetch
/// is a log line in category <c>whatsnew</c>. The one budgeted surface is <see cref="RefreshIssueStatesAsync"/>, which
/// DOES spend the unauthenticated REST allowance (60/hour/IP) — see the budget constants below.
/// </para>
/// </summary>
public sealed class ReleaseNotesStore
{
    const string ApiRoot = "https://api.github.com/repos/";
    const string LogCategory = "whatsnew";
    const string DocName = "whatsnew.json";
    const string IndexName = "whatsnew-index.json";

    /// <summary>How long a completed <see cref="PrefetchAsync"/> for a given target suppresses a repeat.</summary>
    const long PrefetchWindowMs = 5 * 60 * 1000;

    /// <summary>The issue-chip rate-limit policy — WHICH keys to fetch and WHEN to give up. Pure and unit-tested in
    /// <see cref="IssueStateBudget"/>; this class owns only the HTTP half. There is deliberately no second copy of the
    /// numbers here: a store that re-derived "20 per open, 24 h TTL, stop on 403" inline is exactly the drift the
    /// single-owner rule exists to prevent.</summary>
    static readonly IssueStateBudget s_issueBudget = new();

    readonly HttpClient _github;
    readonly string _cacheRoot;
    readonly string _embeddedRoot;
    readonly string _feedRelease;
    /// <summary>The download root every release asset hangs off, ending in "/" — GitHub's by default, a loopback HTTP
    /// server when this build was packed for the local end-to-end update test. Build-time metadata, never a runtime
    /// switch: the ONE base URL both this store and the updater's feed are built from.</summary>
    readonly string _releasesRoot;
    readonly IWaveeLog _log;
    readonly object _gate = new();

    string _lastSource = "none";
    DateTimeOffset? _lastFetchUtc;
    string _lastFetchUrl = "";
    string _lastFetchStatus = "";
    int _rateLimitRemaining = -1;
    long _rateLimitReset;
    int _issueRequests;
    string? _lastPrefetchTarget;
    long _lastPrefetchMs;

    /// <summary>The process-wide store, published by the composition root's construction of it. The updater and the
    /// shell reach it here for the same reason they reach the updater that way: both are app-scoped by contract and
    /// some call sites (a background check, a deep link) cannot see the service bag. Null until <c>Services</c> is built.</summary>
    public static ReleaseNotesStore? Instance { get; private set; }

    /// <summary>The rolling release index (every published version, newest first), or null before the first refresh.
    /// <para>A plain volatile field behind <see cref="IndexSnapshot"/>, NOT a <c>Signal</c>: it is written on whatever
    /// thread the refresh completed on (an HTTP continuation, a prefetch off the update scheduler) and nothing ever
    /// subscribed to it — every reader is a page load that already runs off the UI thread and publishes its own view
    /// in one write. A signal written from a pool thread would be a reactive-graph write with no poster behind it.</para></summary>
    ReleaseNotesIndex? _index;

    /// <summary>The index we currently hold, or null. Safe to call from any thread.</summary>
    public ReleaseNotesIndex? IndexSnapshot() => Volatile.Read(ref _index);

    /// <param name="github">The GitHub pool (product user-agent + <c>application/vnd.github+json</c> already set).</param>
    /// <param name="appDataRoot"><c>%LOCALAPPDATA%\Wavee</c> — the cache is a folder under it.</param>
    /// <param name="feedRelease">The rolling release the index lives on (build-time metadata, never a runtime switch).</param>
    /// <param name="log">The app log; everything here is best-effort and logs rather than throws.</param>
    /// <param name="embeddedRoot">Where THIS build's own notes were laid down. Defaults to <c>Assets/whatsnew</c>
    /// beside the exe, which is where <c>pack-wavee-msix.ps1 -NotesDir</c> copies them.</param>
    /// <param name="releasesRoot">The release-asset download root this build was stamped with
    /// (<c>AppVersion.Info.UpdateBaseUrl</c>). Null/empty ⇒ <c>WaveeVersionInfo.DefaultUpdateBaseUrl</c>; a
    /// missing trailing slash is repaired. The document and the index are fetched from under it, so a package packed
    /// against a loopback feed reads its notes from the same server as its update feed.</param>
    public ReleaseNotesStore(HttpClient github, string appDataRoot, string feedRelease, IWaveeLog log,
        string? embeddedRoot = null, string? releasesRoot = null)
    {
        ArgumentNullException.ThrowIfNull(github);
        ArgumentNullException.ThrowIfNull(log);
        _github = github;
        _log = log;
        _cacheRoot = Path.Combine(string.IsNullOrEmpty(appDataRoot) ? "" : appDataRoot, "cache", "whatsnew");
        _embeddedRoot = embeddedRoot ?? Path.Combine(AppContext.BaseDirectory, "Assets", "whatsnew");
        _feedRelease = string.IsNullOrWhiteSpace(feedRelease) ? "wavee-stable" : feedRelease.Trim();
        _releasesRoot = Wavee.Core.WaveeVersionInfo.NormalizeUpdateBaseUrl(releasesRoot);
        Instance = this;
    }

    /// <summary>The folder this build's own notes + media were published into (beside the exe).</summary>
    public string EmbeddedRoot => _embeddedRoot;

    /// <summary>The per-version cache root.</summary>
    public string CacheRoot => _cacheRoot;

    // ── the document ladder ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The notes for <paramref name="semver"/>: embedded (this build only) → cache → release asset.
    /// Never throws; null means the document exists nowhere we can reach.</summary>
    public async Task<ReleaseNotesDocument?> GetAsync(string semver, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(semver)) return null;
        semver = semver.Trim();
        if (!IsSafeVersion(semver)) return null;

        if (TryReadEmbedded(semver) is { } embedded) { Note("embedded"); return embedded; }

        string path = Path.Combine(_cacheRoot, semver, DocName);
        if (TryReadDocument(path) is { } cached) { Note("cache"); return cached; }

        string url = _releasesRoot + "wavee-v" + semver + "/" + DocName;
        byte[]? bytes = await GetBytesAsync(url, ct).ConfigureAwait(false);
        if (bytes is null) { Note("none"); return null; }

        var doc = Deserialize(bytes);
        if (doc is null) { Note("none"); return null; }
        TryWrite(path, bytes);
        Note("remote");
        return doc;
    }

    /// <summary>The index we already have — the signal, else the on-disk cache. Costs NO network: the update check
    /// calls this on its hot path to name the version it is offering, and a check must never wait on GitHub twice.</summary>
    public Task<ReleaseNotesIndex?> PeekIndexAsync(CancellationToken ct)
    {
        if (IndexSnapshot() is { } live) return Task.FromResult<ReleaseNotesIndex?>(live);
        var cached = TryReadIndexCache();
        if (cached is not null) Volatile.Write(ref _index, cached);
        return Task.FromResult(cached);
    }

    /// <summary>Best-effort warm-up after a check found an update: refresh the index, then pull the notes for the
    /// offered version so the after-update plate and the "What's new" page open instantly (and offline).
    /// The caller gates this on an unmetered link; nothing here re-checks that.</summary>
    public async Task PrefetchAsync(string? targetQuadOrSemver, CancellationToken ct)
    {
        // Single-flight per target: BOTH the check itself and the scheduler that drove it want the notes warm, and
        // neither should have to know whether the other already asked. A repeat for the same target inside the window
        // is a no-op rather than a second pair of GETs.
        string key = targetQuadOrSemver ?? "";
        long now = Environment.TickCount64;
        lock (_gate)
        {
            if (_lastPrefetchTarget is { } previous
                && string.Equals(previous, key, StringComparison.Ordinal)
                && now - _lastPrefetchMs < PrefetchWindowMs) return;
            _lastPrefetchTarget = key;
            _lastPrefetchMs = now;
        }
        await RefreshIndexAsync(ct).ConfigureAwait(false);
        if (ct.IsCancellationRequested) return;
        var entry = IndexSnapshot()?.Find(targetQuadOrSemver ?? "");
        if (entry is { Version.Length: > 0 }) _ = await GetAsync(entry.Version, ct).ConfigureAwait(false);
    }

    /// <summary>Re-read <c>whatsnew-index.json</c> from the rolling feed release into <see cref="Index"/> + the cache.
    /// A failed refresh leaves whatever index we already had — an offline laptop keeps its rail.</summary>
    public async Task RefreshIndexAsync(CancellationToken ct)
    {
        string url = _releasesRoot + _feedRelease + "/" + IndexName;
        byte[]? bytes = await GetBytesAsync(url, ct).ConfigureAwait(false);
        if (bytes is null) return;
        ReleaseNotesIndex? index;
        try { index = JsonSerializer.Deserialize(bytes, ReleaseNotesJsonContext.Default.ReleaseNotesIndex); }
        catch (Exception ex) { _log.Warn(LogCategory, "index parse failed", ex); return; }
        if (index is null) return;
        TryWrite(Path.Combine(_cacheRoot, IndexName), bytes);
        Volatile.Write(ref _index, index);
    }

    // ── live issue states (budgeted) ─────────────────────────────────────────────────────────────────────────────

    /// <summary>Refresh the GitHub state of the issues <paramref name="doc"/> references, and return the merged cache.
    /// <para>The policy is <see cref="IssueStateBudget"/>'s and nothing here restates it:
    /// <see cref="IssueStateBudget.Plan"/> picks the keys worth spending a request on (document order, de-duplicated,
    /// anything still fresh dropped, capped at its per-open maximum) and <see cref="IssueStateBudget.ShouldStop"/>
    /// decides when GitHub has told us to stop (403/429, or an <c>x-ratelimit-remaining</c> of zero). A stopped refresh
    /// is not an error — the chips simply render the state the release tool baked into the document.</para></summary>
    public async Task<IssueStateCache> RefreshIssueStatesAsync(ReleaseNotesDocument doc, CancellationToken ct)
    {
        var cache = TryReadIssueCache() ?? new IssueStateCache();
        if (doc is null) return cache;

        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        bool dirty = false;

        foreach (string key in s_issueBudget.Plan(EnumerateIssueKeys(doc), cache, now))
        {
            if (ct.IsCancellationRequested) break;
            if (!TryParseIssueKey(key, out string repo, out int number)) continue;

            Interlocked.Increment(ref _issueRequests);
            var (state, halt) = await FetchIssueAsync(repo, number, ct).ConfigureAwait(false);
            if (state is not null)
            {
                state.FetchedAtMs = now;
                cache.Set(key, state);
                dirty = true;
            }
            if (halt) break;
        }

        if (dirty) TryWriteIssueCache(cache);
        return cache;
    }

    /// <summary>One unauthenticated issue read. Returns the state (or null) plus whether the budget is now spent —
    /// a 403 or an exhausted <c>x-ratelimit-remaining</c> stops the whole pass rather than the next request.</summary>
    async Task<(IssueState? State, bool Stop)> FetchIssueAsync(string repo, int number, CancellationToken ct)
    {
        string url = ApiRoot + repo + "/issues/" + number.ToString(CultureInfo.InvariantCulture);
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            using var resp = await _github.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
            string? remainingHeader = ReadRateLimit(resp);
            lock (_gate)
            {
                _lastFetchUtc = DateTimeOffset.UtcNow;
                _lastFetchUrl = url;
                _lastFetchStatus = ((int)resp.StatusCode).ToString(CultureInfo.InvariantCulture);
            }

            bool stop = s_issueBudget.ShouldStop((int)resp.StatusCode, remainingHeader);
            if (stop && (resp.StatusCode == HttpStatusCode.Forbidden || resp.StatusCode == (HttpStatusCode)429))
            {
                _log.Info(LogCategory, "issue budget exhausted at " + url);
                return (null, true);
            }
            if (!resp.IsSuccessStatusCode) return (null, stop);

            using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var json = await JsonDocument.ParseAsync(stream, default, ct).ConfigureAwait(false);
            var root = json.RootElement;
            var state = new IssueState
            {
                State = root.TryGetProperty("state", out var s) && s.ValueKind == JsonValueKind.String ? (s.GetString() ?? "open") : "open",
                StateReason = root.TryGetProperty("state_reason", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null,
                Title = root.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? (t.GetString() ?? "") : "",
            };
            return (state, stop);
        }
        catch (OperationCanceledException) { return (null, true); }
        catch (Exception ex)
        {
            lock (_gate)
            {
                _lastFetchUtc = DateTimeOffset.UtcNow;
                _lastFetchUrl = url;
                _lastFetchStatus = ex.Message;
            }
            _log.Warn(LogCategory, "issue fetch failed " + url, ex);
            return (null, false);
        }
    }

    /// <summary>Every issue the document references, in document order, already as the cache/budget KEY (which is what
    /// <see cref="IssueStateBudget.Plan"/> takes). Still defensive about nulls even though
    /// <see cref="ReleaseNotesDocument.Normalize"/> has run on everything this store deserializes — the method is also
    /// reachable with a document a caller built by hand.</summary>
    static IEnumerable<string> EnumerateIssueKeys(ReleaseNotesDocument doc)
    {
        var sections = doc.Sections;
        if (sections is null) yield break;
        foreach (var section in sections)
        {
            var items = section is null ? null : section.Items;
            if (items is null) continue;
            foreach (var item in items)
            {
                var issues = item is null ? null : item.Issues;
                if (issues is null) continue;
                foreach (var issue in issues)
                    if (issue is { Repo.Length: > 0, Number: > 0 })
                        yield return IssueStateCache.Key(issue.Repo, issue.Number);
            }
        }
    }

    /// <summary>The inverse of <see cref="IssueStateCache.Key"/>: <c>owner/repo#123</c> back into its two halves, so a
    /// planned key can be turned into the one REST URL it stands for.</summary>
    static bool TryParseIssueKey(string key, out string repo, out int number)
    {
        repo = "";
        number = 0;
        if (string.IsNullOrEmpty(key)) return false;
        int hash = key.LastIndexOf('#');
        if (hash <= 0 || hash == key.Length - 1) return false;
        if (!int.TryParse(key.AsSpan(hash + 1), NumberStyles.None, CultureInfo.InvariantCulture, out number)) return false;
        repo = key[..hash];
        return repo.Length > 0 && number > 0;
    }

    // ── media ────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Resolve a document-relative media reference to a local path (what <c>ImageEl.Source</c> takes). The
    /// embedded copy wins for the running version; everything else resolves inside that version's cache folder.</summary>
    public string MediaPath(ReleaseNotesDocument doc, string src)
    {
        if (doc is null || string.IsNullOrWhiteSpace(src)) return "";
        // A leading separator is an ABSOLUTE reference ("/etc/passwd", "//server/share/x"), never a media file:
        // refuse it outright rather than trimming it into something that merely looks relative.
        if (src[0] == '/' || src[0] == '\\') return "";
        string rel = src.Replace('\\', '/');
        // Three independent refusals, because each defeats a different escape: ".."; an ABSOLUTE path
        // ("C:/Windows/notepad.exe", "/etc/passwd"), which Path.Combine honours by DISCARDING the root we handed it;
        // and a drive-or-stream qualifier (":"), which does the same on Windows even mid-string ("a:b" names an
        // alternate data stream).
        if (rel.Contains("..", StringComparison.Ordinal)) return "";
        if (rel.Contains(':')) return "";
        if (Path.IsPathRooted(rel)) return "";

        string relative = rel.Replace('/', Path.DirectorySeparatorChar);
        if (Contained(_embeddedRoot, relative) is { Length: > 0 } embedded && File.Exists(embedded)) return embedded;
        if (!IsSafeVersion(doc.Version)) return "";
        return Contained(Path.Combine(_cacheRoot, doc.Version), relative) ?? "";
    }

    /// <summary>Combine, then PROVE the result is still under <paramref name="root"/>. The three refusals above reject
    /// the shapes we know about; this rejects the ones we do not — the combined full path must start with the root's
    /// own full path or the answer is "no such media".</summary>
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

    // ── diagnostics ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Everything the playback/runtime diagnostics page needs to explain what the notes pipeline did.</summary>
    public ReleaseNotesDiagnostics DiagnosticsSnapshot()
    {
        lock (_gate)
        {
            return new ReleaseNotesDiagnostics(_cacheRoot, _embeddedRoot, _feedRelease, _lastSource,
                _lastFetchUtc, _lastFetchUrl, _lastFetchStatus, _rateLimitRemaining, _rateLimitReset,
                Volatile.Read(ref _issueRequests));
        }
    }

    // ── plumbing ─────────────────────────────────────────────────────────────────────────────────────────────────

    async Task<byte[]?> GetBytesAsync(string url, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            using var resp = await _github.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
            ReadRateLimit(resp);
            lock (_gate)
            {
                _lastFetchUtc = DateTimeOffset.UtcNow;
                _lastFetchUrl = url;
                _lastFetchStatus = ((int)resp.StatusCode).ToString(CultureInfo.InvariantCulture);
            }
            if (!resp.IsSuccessStatusCode)
            {
                _log.Info(LogCategory, "fetch " + (int)resp.StatusCode + " " + url);
                return null;
            }
            return await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            lock (_gate)
            {
                _lastFetchUtc = DateTimeOffset.UtcNow;
                _lastFetchUrl = url;
                _lastFetchStatus = ex.Message;
            }
            _log.Warn(LogCategory, "fetch failed " + url, ex);
            return null;
        }
    }

    /// <summary>Record the two rate-limit headers for the diagnostics page and hand the RAW
    /// <c>x-ratelimit-remaining</c> back, because that is what <see cref="IssueStateBudget.ShouldStop"/> takes — the
    /// budget parses it itself, so the "is the quota gone?" rule has exactly one owner.</summary>
    string? ReadRateLimit(HttpResponseMessage resp)
    {
        string? remainingHeader = null;
        try
        {
            if (resp.Headers.TryGetValues("x-ratelimit-remaining", out var remaining))
                foreach (var v in remaining) { remainingHeader = v; break; }
            string? resetHeader = null;
            if (resp.Headers.TryGetValues("x-ratelimit-reset", out var reset))
                foreach (var v in reset) { resetHeader = v; break; }

            lock (_gate)
            {
                if (remainingHeader is not null
                    && int.TryParse(remainingHeader, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n))
                    _rateLimitRemaining = n;
                if (resetHeader is not null
                    && long.TryParse(resetHeader, NumberStyles.Integer, CultureInfo.InvariantCulture, out long r))
                    _rateLimitReset = r;
            }
        }
        catch (Exception) { }
        return remainingHeader;
    }

    void Note(string source) { lock (_gate) { _lastSource = source; } }

    ReleaseNotesDocument? TryReadEmbedded(string semver)
    {
        var doc = TryReadDocument(Path.Combine(_embeddedRoot, DocName));
        // The embedded copy is THIS build's notes and nothing else: handing it back for another version would be a
        // silent lie ("here are 0.3's highlights" while showing 0.2's).
        return doc is not null && string.Equals(doc.Version, semver, StringComparison.Ordinal) ? doc : null;
    }

    ReleaseNotesDocument? TryReadDocument(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            return Deserialize(File.ReadAllBytes(path));
        }
        catch (Exception ex) { _log.Warn(LogCategory, "read failed " + path, ex); return null; }
    }

    /// <summary>Deserialize AND normalize. Every document this store hands out has been through
    /// <see cref="ReleaseNotesDocument.Normalize"/>, so no surface downstream has to guard against the
    /// <c>"sections": null</c> the hand-authored JSON is perfectly entitled to carry.</summary>
    ReleaseNotesDocument? Deserialize(byte[] bytes)
    {
        try
        {
            var doc = JsonSerializer.Deserialize(bytes, ReleaseNotesJsonContext.Default.ReleaseNotesDocument);
            doc?.Normalize();
            return doc;
        }
        catch (Exception ex) { _log.Warn(LogCategory, "parse failed", ex); return null; }
    }

    ReleaseNotesIndex? TryReadIndexCache()
    {
        try
        {
            string path = Path.Combine(_cacheRoot, IndexName);
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize(File.ReadAllBytes(path), ReleaseNotesJsonContext.Default.ReleaseNotesIndex);
        }
        catch (Exception ex) { _log.Warn(LogCategory, "index cache read failed", ex); return null; }
    }

    IssueStateCache? TryReadIssueCache()
    {
        try
        {
            string path = Path.Combine(_cacheRoot, "issues.json");
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize(File.ReadAllBytes(path), ReleaseNotesJsonContext.Default.IssueStateCache);
        }
        catch (Exception ex) { _log.Warn(LogCategory, "issue cache read failed", ex); return null; }
    }

    void TryWriteIssueCache(IssueStateCache cache)
    {
        try
        {
            TryWrite(Path.Combine(_cacheRoot, "issues.json"),
                JsonSerializer.SerializeToUtf8Bytes(cache, ReleaseNotesJsonContext.Default.IssueStateCache));
        }
        catch (Exception ex) { _log.Warn(LogCategory, "issue cache write failed", ex); }
    }

    void TryWrite(string path, byte[] bytes)
    {
        try
        {
            string? dir = Path.GetDirectoryName(path);
            if (dir is { Length: > 0 }) Directory.CreateDirectory(dir);
            File.WriteAllBytes(path, bytes);
        }
        catch (Exception ex) { _log.Warn(LogCategory, "cache write failed " + path, ex); }
    }

    /// <summary>A version string is used as a PATH SEGMENT and as part of a URL, so it is whitelisted rather than
    /// escaped: digits, dots and the pre-release alphabet only. Anything else resolves to nothing.</summary>
    internal static bool IsSafeVersion(string? v)
    {
        if (string.IsNullOrEmpty(v) || v.Length > 40) return false;
        // A version is used as a PATH SEGMENT, and "." and ".." pass the whitelist below while naming the cache root
        // and its PARENT. Requiring a leading DIGIT rejects both — plus every hidden-file and switch-shaped name —
        // and costs nothing a real semver would have wanted.
        if (!char.IsAsciiDigit(v[0])) return false;
        foreach (char c in v)
            if (!char.IsAsciiLetterOrDigit(c) && c != '.' && c != '-') return false;
        return true;
    }
}
