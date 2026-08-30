using System.Net;
using System.Text;
using System.Text.Json;
using Wavee;
using Wavee.Core.ReleaseNotes;
using Wavee.Tests;
using Wavee.Tests.Modules;
using Xunit;

// The release-notes store (App/ReleaseNotesStore.cs): the embedded → cache → release-asset ladder, the rolling index,
// and the unauthenticated issue-state budget. Every rung is driven over a scripted transport and a temp folder — no
// network, no %LOCALAPPDATA%.
public class ReleaseNotesStoreTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "wavee-notes-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch (Exception) { }
    }

    // ── the ladder ──────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Embedded_WinsAndCostsNoRequest()
    {
        var store = Build(out var http, embedded: Doc("0.3.0", "Crest"));

        var doc = await store.GetAsync("0.3.0", CancellationToken.None);

        Assert.Equal("Crest", doc!.Name);
        Assert.Empty(http.Requests);
        Assert.Equal("embedded", store.DiagnosticsSnapshot().LastSource);
    }

    [Fact]
    public async Task Embedded_IsNeverHandedBackForAnotherVersion()
    {
        // The copy beside the exe is THIS build's notes. Serving it for 0.4.0 would show 0.3.0's highlights under a
        // 0.4.0 heading — a silent lie, and exactly the bug a "just fall back to something" ladder would ship.
        var store = Build(out var http, embedded: Doc("0.3.0", "Crest"));
        Remote(http, "0.4.0", Doc("0.4.0", "Drift"));

        var doc = await store.GetAsync("0.4.0", CancellationToken.None);

        Assert.Equal("Drift", doc!.Name);
        Assert.Single(http.Requests);
    }

    [Fact]
    public async Task Cache_IsUsedBeforeTheNetwork()
    {
        var store = Build(out var http);
        WriteCache("0.2.0", Doc("0.2.0", "Breaker"));

        var doc = await store.GetAsync("0.2.0", CancellationToken.None);

        Assert.Equal("Breaker", doc!.Name);
        Assert.Empty(http.Requests);
        Assert.Equal("cache", store.DiagnosticsSnapshot().LastSource);
    }

    [Fact]
    public async Task Remote_IsFetchedOnceAndThenCached()
    {
        var store = Build(out var http);
        Remote(http, "0.4.0", Doc("0.4.0", "Drift"));

        var first = await store.GetAsync("0.4.0", CancellationToken.None);
        var second = await store.GetAsync("0.4.0", CancellationToken.None);

        Assert.Equal("Drift", first!.Name);
        Assert.Equal("Drift", second!.Name);
        Assert.Single(http.Requests);       // the second call came off disk
        Assert.True(File.Exists(Path.Combine(_root, "cache", "whatsnew", "0.4.0", "whatsnew.json")));
    }

    [Fact]
    public async Task Remote_MissingIsNullNotAThrow()
    {
        var store = Build(out var http);
        http.OnUrl("whatsnew.json", HttpStatusCode.NotFound, "nope");

        Assert.Null(await store.GetAsync("9.9.9", CancellationToken.None));
        Assert.Equal("none", store.DiagnosticsSnapshot().LastSource);
    }

    [Fact]
    public async Task Remote_MalformedJsonIsNullNotAThrow()
    {
        var store = Build(out var http);
        http.OnUrl("whatsnew.json", HttpStatusCode.OK, "{ not json", "application/json");

        Assert.Null(await store.GetAsync("0.4.0", CancellationToken.None));
    }

    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("0.4.0/../../x")]
    [InlineData("")]
    public async Task Version_IsWhitelistedBeforeItBecomesAPathOrAUrl(string version)
    {
        var store = Build(out var http);
        Assert.Null(await store.GetAsync(version, CancellationToken.None));
        Assert.Empty(http.Requests);
    }

    // ── the rolling index ───────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RefreshIndex_PublishesAndCaches()
    {
        var store = Build(out var http, feedRelease: "wavee-stable-test");
        Index(http, ("0.4.0", "0.4.0.9", "Drift"), ("0.3.0", "0.3.0.7", "Crest"));

        await store.RefreshIndexAsync(CancellationToken.None);

        Assert.Equal("Crest", store.IndexSnapshot()!.Find("0.3.0.7")!.Name);
        Assert.Contains("/wavee-stable-test/whatsnew-index.json", http.Requests[0].Url, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(_root, "cache", "whatsnew", "whatsnew-index.json")));
    }

    [Fact]
    public async Task RefreshIndex_FailureKeepsTheIndexWeAlreadyHad()
    {
        var store = Build(out var http);
        Index(http, ("0.3.0", "0.3.0.7", "Crest"));
        await store.RefreshIndexAsync(CancellationToken.None);

        var offline = Build(out var down, cacheOnly: true);
        down.OnUrl("whatsnew-index.json", HttpStatusCode.ServiceUnavailable, "down");
        await offline.RefreshIndexAsync(CancellationToken.None);

        // A laptop that woke up offline keeps its rail: the cached index is what PeekIndexAsync answers with.
        Assert.Equal("Crest", (await offline.PeekIndexAsync(CancellationToken.None))!.Find("0.3.0")!.Name);
    }

    [Fact]
    public async Task PeekIndex_CostsNoNetwork()
    {
        var store = Build(out var http);
        Assert.Null(await store.PeekIndexAsync(CancellationToken.None));
        Assert.Empty(http.Requests);
    }

    [Fact]
    public async Task Prefetch_IsSingleFlightPerTarget()
    {
        // Both the check that found the update and the scheduler that drove it ask for the notes; neither should have
        // to know whether the other already did.
        var store = Build(out var http);
        Index(http, ("0.4.0", "0.4.0.9", "Drift"));
        Remote(http, "0.4.0", Doc("0.4.0", "Drift"));

        await store.PrefetchAsync("0.4.0.9", CancellationToken.None);
        int after = http.Requests.Count;
        await store.PrefetchAsync("0.4.0.9", CancellationToken.None);

        Assert.Equal(2, after);                     // index + document
        Assert.Equal(after, http.Requests.Count);   // the repeat spent nothing
    }

    // ── the issue-state budget ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task IssueStates_AreCappedPerPageOpen()
    {
        // 60 unauthenticated requests per hour per IP is the whole allowance; a document with 25 references must not
        // spend half of it in one page open.
        var store = Build(out var http);
        Issues(http, HttpStatusCode.OK);

        await store.RefreshIssueStatesAsync(DocWithIssues(25), CancellationToken.None);

        Assert.Equal(20, http.Requests.Count);
    }

    [Fact]
    public async Task IssueStates_AreMergedIntoTheCacheAndPersisted()
    {
        var store = Build(out var http);
        Issues(http, HttpStatusCode.OK);

        var cache = await store.RefreshIssueStatesAsync(DocWithIssues(3), CancellationToken.None);

        var state = cache.Lookup(IssueStateCache.Key("christosk92/WaveeMusic", 1));
        Assert.Equal("closed", state!.State);
        Assert.Equal("completed", state.StateReason);
        Assert.True(File.Exists(Path.Combine(_root, "cache", "whatsnew", "issues.json")));
    }

    [Fact]
    public async Task IssueStates_FreshEntriesAreNotRefetched()
    {
        var store = Build(out var http);
        Issues(http, HttpStatusCode.OK);
        await store.RefreshIssueStatesAsync(DocWithIssues(3), CancellationToken.None);
        int spent = http.Requests.Count;

        // A second store reads the SAME on-disk cache: within the TTL nothing is worth asking again.
        var again = Build(out var http2, cacheOnly: true);
        Issues(http2, HttpStatusCode.OK);
        await again.RefreshIssueStatesAsync(DocWithIssues(3), CancellationToken.None);

        Assert.Equal(3, spent);
        Assert.Empty(http2.Requests);
    }

    [Fact]
    public async Task IssueStates_StopOnTheFirst403()
    {
        // GitHub answers 403 when the per-IP allowance is gone. Continuing would spend nothing but latency and would
        // keep the page spinning; the chips simply render what the release tool baked in.
        var store = Build(out var http);
        Issues(http, HttpStatusCode.Forbidden);

        await store.RefreshIssueStatesAsync(DocWithIssues(10), CancellationToken.None);

        Assert.Single(http.Requests);
    }

    [Fact]
    public async Task IssueStates_StopWhenTheRateLimitHeaderHitsZero()
    {
        var store = Build(out var http);
        http.OnUrl("api.github.com", HttpStatusCode.OK,
            "{\"state\":\"open\",\"title\":\"t\"}", "application/json", ("x-ratelimit-remaining", "0"));

        await store.RefreshIssueStatesAsync(DocWithIssues(10), CancellationToken.None);

        Assert.Single(http.Requests);
        Assert.Equal(0, store.DiagnosticsSnapshot().RateLimitRemaining);
    }

    [Fact]
    public async Task IssueStates_ADocumentWithNoIssuesSpendsNothing()
    {
        var store = Build(out var http);
        await store.RefreshIssueStatesAsync(Doc("0.3.0", "Crest"), CancellationToken.None);
        Assert.Empty(http.Requests);
    }

    // ── media ───────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void MediaPath_PrefersTheEmbeddedFileThenTheVersionCache()
    {
        var doc = Doc("0.3.0", "Crest");
        var store = Build(out _, embedded: doc);
        string embeddedDir = Path.Combine(_root, "embedded");
        Directory.CreateDirectory(Path.Combine(embeddedDir, "media"));
        File.WriteAllText(Path.Combine(embeddedDir, "media", "hero.webp"), "x");

        Assert.Equal(Path.Combine(embeddedDir, "media", "hero.webp"), store.MediaPath(doc, "media/hero.webp"));
        Assert.Equal(Path.Combine(_root, "cache", "whatsnew", "0.3.0", "media", "other.webp"),
            store.MediaPath(doc, "media/other.webp"));
    }

    [Theory]
    [InlineData("../secrets.txt")]
    [InlineData("media/../../secrets.txt")]
    // An ABSOLUTE path is the escape that needs no "..": Path.Combine DISCARDS the root it was handed the moment the
    // second argument is rooted, so "C:/Windows/notepad.exe" resolved to exactly that and the poster band would have
    // rendered whatever it pointed at.
    [InlineData("C:/Windows/System32/notepad.exe")]
    [InlineData("C:\\Windows\\System32\\notepad.exe")]
    [InlineData("/etc/passwd")]
    [InlineData("//server/share/x.webp")]
    // A drive or stream qualifier does the same thing on Windows even mid-string ("a:b" is an alternate data stream).
    [InlineData("media:hero.webp")]
    [InlineData("d:hero.webp")]
    public void MediaPath_RefusesToEscapeItsFolders(string src)
        => Assert.Equal("", Build(out _).MediaPath(Doc("0.3.0", "Crest"), src));

    [Fact]
    public void MediaPath_AlwaysResolvesUnderOneOfItsTwoRoots()
    {
        // The containment proof, independent of the shape-based refusals above: whatever comes back must start with a
        // root this store owns. A rule that only rejects the shapes we thought of is a rule that ages badly.
        var store = Build(out _);
        string got = store.MediaPath(Doc("0.3.0", "Crest"), "media/hero.webp");

        Assert.StartsWith(Path.GetFullPath(Path.Combine(_root, "cache", "whatsnew", "0.3.0")), got,
                          StringComparison.OrdinalIgnoreCase);
    }

    // ── the version is a path segment ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("0.3.0")]
    [InlineData("0.4.0-beta.2")]
    [InlineData("1.0.0")]
    public void IsSafeVersion_AcceptsRealVersions(string v)
        => Assert.True(ReleaseNotesStore.IsSafeVersion(v));

    [Theory]
    [InlineData(".")]          // the cache root itself
    [InlineData("..")]         // its PARENT — both passed the old digits/dots/dashes whitelist untouched
    [InlineData("...")]
    [InlineData("-rf")]        // switch-shaped
    [InlineData(".hidden")]
    [InlineData("v0.3.0")]     // a leading 'v' is not how the cache names a folder
    [InlineData("")]
    [InlineData(null)]
    [InlineData("0.3.0/../..")]
    [InlineData("0.3.0\\x")]
    [InlineData("C:")]
    public void IsSafeVersion_RejectsAnythingThatIsNotAVersion(string? v)
        => Assert.False(ReleaseNotesStore.IsSafeVersion(v));

    [Theory]
    [InlineData(".")]
    [InlineData("..")]
    public async Task Get_RefusesADotSegmentAsAVersion_WithoutTouchingTheNetwork(string version)
    {
        var store = Build(out var http);
        Assert.Null(await store.GetAsync(version, CancellationToken.None));
        Assert.Empty(http.Requests);
    }

    // ── the stamped download root ───────────────────────────────────────────────────────────────────────────────
    // The store fetches the document and the index from the SAME base URL the updater builds its feed from
    // (AppVersion.Info.UpdateBaseUrl, assembly metadata). A build packed against a loopback feed that still read its
    // notes off GitHub would be a package whose "What's new" silently disagrees with the version it just installed —
    // and, in the local end-to-end test, the one outbound request that makes the run not-offline.

    [Fact]
    public async Task ADocument_IsFetchedFromTheStampedRoot()
    {
        var store = Build(out var http, releasesRoot: "http://127.0.0.1:8099/");
        Remote(http, "0.4.0", Doc("0.4.0", "Drift"));

        var doc = await store.GetAsync("0.4.0", CancellationToken.None);

        Assert.Equal("Drift", doc!.Name);
        Assert.Single(http.Requests);
        Assert.Equal("http://127.0.0.1:8099/wavee-v0.4.0/whatsnew.json", http.Requests[0].Url);
    }

    [Fact]
    public async Task TheIndex_IsFetchedFromTheStampedRoot()
    {
        var store = Build(out var http, feedRelease: "wavee-local", releasesRoot: "http://127.0.0.1:8099");
        Index(http, ("0.4.0", "0.4.0.29", "Drift"));

        await store.RefreshIndexAsync(CancellationToken.None);

        // The missing trailing slash is repaired by NormalizeUpdateBaseUrl, not by string surgery at each call site.
        Assert.Single(http.Requests);
        Assert.Equal("http://127.0.0.1:8099/wavee-local/whatsnew-index.json", http.Requests[0].Url);
        Assert.Equal("0.4.0", store.IndexSnapshot()!.Releases[0].Version);
    }

    [Fact]
    public async Task WithNoStamp_TheStoreStillReadsGitHub()
    {
        var store = Build(out var http);
        Remote(http, "0.4.0", Doc("0.4.0", "Drift"));

        await store.GetAsync("0.4.0", CancellationToken.None);

        Assert.Single(http.Requests);
        Assert.Equal("https://github.com/christosk92/WaveeMusic/releases/download/wavee-v0.4.0/whatsnew.json",
                     http.Requests[0].Url);
    }

    // ── harness ─────────────────────────────────────────────────────────────────────────────────────────────────

    ReleaseNotesStore Build(out ScriptedHttpHandler http, ReleaseNotesDocument? embedded = null,
        string feedRelease = "wavee-stable", bool cacheOnly = false, string? releasesRoot = null)
    {
        http = new ScriptedHttpHandler();
        string embeddedDir = Path.Combine(_root, "embedded");
        Directory.CreateDirectory(embeddedDir);
        if (embedded is not null && !cacheOnly)
            File.WriteAllBytes(Path.Combine(embeddedDir, "whatsnew.json"), Bytes(embedded));
        return new ReleaseNotesStore(new HttpClient(http), _root, feedRelease, new CapturingWaveeLog(), embeddedDir,
            releasesRoot);
    }

    void WriteCache(string semver, ReleaseNotesDocument doc)
    {
        string dir = Path.Combine(_root, "cache", "whatsnew", semver);
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "whatsnew.json"), Bytes(doc));
    }

    static void Remote(ScriptedHttpHandler http, string semver, ReleaseNotesDocument doc)
        => http.OnUrl("wavee-v" + semver + "/whatsnew.json", HttpStatusCode.OK,
            Encoding.UTF8.GetString(Bytes(doc)), "application/json");

    static void Index(ScriptedHttpHandler http, params (string Version, string Quad, string Name)[] releases)
    {
        var index = new ReleaseNotesIndex
        {
            Releases = releases.Select(r => new ReleaseNotesIndexEntry
            { Version = r.Version, PackageVersion = r.Quad, Name = r.Name, Date = "2026-08-01", Channel = "stable" }).ToArray(),
        };
        http.OnUrl("whatsnew-index.json", HttpStatusCode.OK,
            JsonSerializer.Serialize(index, ReleaseNotesJsonContext.Default.ReleaseNotesIndex), "application/json");
    }

    static void Issues(ScriptedHttpHandler http, HttpStatusCode status)
        => http.OnUrl("api.github.com", status,
            status == HttpStatusCode.OK
                ? "{\"state\":\"closed\",\"state_reason\":\"completed\",\"title\":\"Docked video\"}"
                : "{\"message\":\"rate limit exceeded\"}",
            "application/json");

    static byte[] Bytes(ReleaseNotesDocument doc)
        => JsonSerializer.SerializeToUtf8Bytes(doc, ReleaseNotesJsonContext.Default.ReleaseNotesDocument);

    static ReleaseNotesDocument Doc(string version, string name) => new()
    {
        Version = version,
        PackageVersion = version + ".1",
        Name = name,
        Tagline = "a tagline",
        Date = "2026-08-01",
    };

    static ReleaseNotesDocument DocWithIssues(int count)
    {
        var items = new List<ReleaseItem>(count);
        for (int i = 1; i <= count; i++)
            items.Add(new ReleaseItem
            {
                Id = "i" + i,
                Text = "Something " + i,
                Issues = [new ReleaseIssue { Repo = "christosk92/WaveeMusic", Number = i, State = "open" }],
            });
        var doc = Doc("0.3.0", "Crest");
        doc.Sections = [new ReleaseSection { Kind = "fixed", Items = items.ToArray() }];
        return doc;
    }
}
