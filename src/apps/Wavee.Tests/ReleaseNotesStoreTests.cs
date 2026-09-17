// ── Wavee.Tests/ReleaseNotesStoreTests.cs — the release-notes store and the page / plate loaders (G-201) ────────────────
//
// Ported from 0.2.9's `Wavee.Tests/ReleaseNotesStoreTests.cs` against `ReleaseNotes.Store` (Screens/ReleaseNotes.Host.cs):
// the embedded → cache → release-asset ladder, the rolling index, the unauthenticated issue-state budget, MediaPath's
// containment and the stamped download root. The loader facts are new: they pin ch 28 §0.10/§0.11 (one whole view or
// nothing; an explicit version means that version only; the stack is everything since lastSeen) and §9.4 (a)'s payload
// (the plate's document + its visible cards, or nothing). Every rung runs over a scripted transport and a temp folder —
// no network, no %LOCALAPPDATA%. The store logs through the static `Log` ring, hence the Platform collection.

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Wavee;
using Xunit;
using static Wavee.ReleaseNotes;

namespace Wavee.Tests;

[Collection(PlatformCollection.Name)]
public sealed class ReleaseNotesStoreTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "wavee-notes-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch (Exception) { }
    }

    // ── the ladder ──────────────────────────────────────────────────────────────────────────────────────────────────

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
        // The copy beside the exe is THIS build's notes: serving it for 0.4.0 is a silent lie.
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

    [Fact]
    public async Task ADocumentNormalizes_OnTheWayOut()
    {
        // Every document the store hands out went through Normalize: no surface guards against "sections": null.
        var store = Build(out var http);
        http.OnUrl("wavee-v0.4.0/whatsnew.json", HttpStatusCode.OK, """{ "version": "0.4.0", "sections": null, "highlights": [null] }""", "application/json");

        var doc = await store.GetAsync("0.4.0", CancellationToken.None);

        Assert.Empty(doc!.Sections);
        Assert.Empty(doc.Highlights);
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

    // ── the rolling index ───────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RefreshIndex_PublishesAndCaches()
    {
        var store = Build(out var http, feedRelease: "wavee-stable-test");
        Index(http, ("0.4.0", "0.4.0.9", "Drift"), ("0.3.0", "0.3.0.7", "Crest"));

        await store.RefreshIndexAsync(CancellationToken.None);

        Assert.Equal("Crest", store.IndexSnapshot()!.Find("0.3.0.7")!.Name);
        Assert.Contains("/wavee-stable-test/whatsnew-index.json", http.Requests[0], StringComparison.Ordinal);
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

        // A laptop that woke up offline keeps its rail: the cached index is what the peek answers with.
        Assert.Equal("Crest", (await offline.PeekIndexAsync(CancellationToken.None))!.Find("0.3.0")!.Name);
        Assert.Equal("Crest", offline.PeekIndex()!.Find("0.3.0.7")!.Name);   // the updater's synchronous seam, same answer
    }

    [Fact]
    public async Task PeekIndex_CostsNoNetwork()
    {
        var store = Build(out var http);
        Assert.Null(await store.PeekIndexAsync(CancellationToken.None));
        Assert.Null(store.PeekIndex());
        Assert.Empty(http.Requests);
    }

    [Fact]
    public async Task Prefetch_IsSingleFlightPerTarget()
    {
        var store = Build(out var http);
        Index(http, ("0.4.0", "0.4.0.9", "Drift"));
        Remote(http, "0.4.0", Doc("0.4.0", "Drift"));

        await store.PrefetchAsync("0.4.0.9", CancellationToken.None);
        int after = http.Requests.Count;
        await store.PrefetchAsync("0.4.0.9", CancellationToken.None);

        Assert.Equal(2, after);                     // index + document
        Assert.Equal(after, http.Requests.Count);   // the repeat spent nothing
    }

    // ── the issue-state budget ──────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task IssueStates_AreCappedPerPageOpen()
    {
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
        var store = Build(out var http);
        Issues(http, HttpStatusCode.Forbidden);

        await store.RefreshIssueStatesAsync(DocWithIssues(10), CancellationToken.None);

        Assert.Single(http.Requests);
    }

    [Fact]
    public async Task IssueStates_StopWhenTheRateLimitHeaderHitsZero()
    {
        var store = Build(out var http);
        http.OnUrl("api.github.com", HttpStatusCode.OK, "{\"state\":\"open\",\"title\":\"t\"}", "application/json", ("x-ratelimit-remaining", "0"));

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

    // ── media ───────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void MediaPath_PrefersTheEmbeddedFileThenTheVersionCache()
    {
        var doc = Doc("0.3.0", "Crest");
        var store = Build(out _, embedded: doc);
        string embeddedDir = Path.Combine(_root, "embedded");
        Directory.CreateDirectory(Path.Combine(embeddedDir, "media"));
        File.WriteAllText(Path.Combine(embeddedDir, "media", "hero.webp"), "x");

        Assert.Equal(Path.Combine(embeddedDir, "media", "hero.webp"), store.MediaPath(doc, "media/hero.webp"));
        Assert.Equal(Path.Combine(_root, "cache", "whatsnew", "0.3.0", "media", "other.webp"), store.MediaPath(doc, "media/other.webp"));
    }

    [Theory]
    [InlineData("../secrets.txt")]
    [InlineData("media/../../secrets.txt")]
    [InlineData("C:/Windows/System32/notepad.exe")]   // rooted: Path.Combine DISCARDS the root it was handed
    [InlineData("C:\\Windows\\System32\\notepad.exe")]
    [InlineData("/etc/passwd")]
    [InlineData("//server/share/x.webp")]
    [InlineData("media:hero.webp")]                   // a drive / stream qualifier, even mid-string
    [InlineData("d:hero.webp")]
    public void MediaPath_RefusesToEscapeItsFolders(string src)
        => Assert.Equal("", Build(out _).MediaPath(Doc("0.3.0", "Crest"), src));

    [Fact]
    public void MediaPath_AlwaysResolvesUnderOneOfItsTwoRoots()
    {
        string got = Build(out _).MediaPath(Doc("0.3.0", "Crest"), "media/hero.webp");
        Assert.StartsWith(Path.GetFullPath(Path.Combine(_root, "cache", "whatsnew", "0.3.0")), got, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("0.3.0")]
    [InlineData("0.4.0-beta.2")]
    [InlineData("1.0.0")]
    public void IsSafeVersion_AcceptsRealVersions(string v) => Assert.True(ReleaseNotes.Store.IsSafeVersion(v));

    [Theory]
    [InlineData(".")]          // the cache root itself
    [InlineData("..")]         // its PARENT
    [InlineData("...")]
    [InlineData("-rf")]        // switch-shaped
    [InlineData(".hidden")]
    [InlineData("v0.3.0")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("0.3.0/../..")]
    [InlineData("0.3.0\\x")]
    [InlineData("C:")]
    public void IsSafeVersion_RejectsAnythingThatIsNotAVersion(string? v) => Assert.False(ReleaseNotes.Store.IsSafeVersion(v));

    [Theory]
    [InlineData(".")]
    [InlineData("..")]
    public async Task Get_RefusesADotSegmentAsAVersion_WithoutTouchingTheNetwork(string version)
    {
        var store = Build(out var http);
        Assert.Null(await store.GetAsync(version, CancellationToken.None));
        Assert.Empty(http.Requests);
    }

    // ── the stamped download root: the notes ride the SAME base URL as the update feed ──────────────────────────────

    [Fact]
    public async Task ADocument_IsFetchedFromTheStampedRoot()
    {
        var store = Build(out var http, releasesRoot: "http://127.0.0.1:8099/");
        Remote(http, "0.4.0", Doc("0.4.0", "Drift"));

        Assert.Equal("Drift", (await store.GetAsync("0.4.0", CancellationToken.None))!.Name);
        Assert.Single(http.Requests);
        Assert.Equal("http://127.0.0.1:8099/wavee-v0.4.0/whatsnew.json", http.Requests[0]);
    }

    [Fact]
    public async Task TheIndex_IsFetchedFromTheStampedRoot()
    {
        var store = Build(out var http, feedRelease: "wavee-local", releasesRoot: "http://127.0.0.1:8099");
        Index(http, ("0.4.0", "0.4.0.29", "Drift"));

        await store.RefreshIndexAsync(CancellationToken.None);

        Assert.Single(http.Requests);   // the missing trailing slash is repaired once, not by string surgery per call
        Assert.Equal("http://127.0.0.1:8099/wavee-local/whatsnew-index.json", http.Requests[0]);
        Assert.Equal("0.4.0", store.IndexSnapshot()!.Releases[0].Version);
    }

    [Fact]
    public async Task WithNoStamp_TheStoreStillReadsGitHub()
    {
        var store = Build(out var http);
        Remote(http, "0.4.0", Doc("0.4.0", "Drift"));

        await store.GetAsync("0.4.0", CancellationToken.None);

        Assert.Single(http.Requests);
        Assert.Equal("https://github.com/christosk92/WaveeMusic/releases/download/wavee-v0.4.0/whatsnew.json", http.Requests[0]);
    }

    // ── the page loader (ch 28 §0.10, §0.11) ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheDefaultDestination_StacksEverythingSinceLastSeen_NewestFirst()
    {
        var store = Build(out var http, embedded: Doc("0.4.0", "Drift", "d1", "d2"));
        Index(http, ("0.4.0", "0.4.0.9", "Drift"), ("0.3.0", "0.3.0.7", "Crest"), ("0.2.0", "0.2.0.3", "Breaker"));
        Remote(http, "0.3.0", Doc("0.3.0", "Crest", "c1", "c2"));

        var view = await LoadViewAsync(store, versionArg: null, lastSeen: "0.2.0", new RunningBuild("0.4.0", "stable", false), CancellationToken.None);

        Assert.NotNull(view);
        Assert.Equal(new[] { "0.4.0", "0.3.0" }, view!.Releases.Select(r => r.Doc.Version));
        Assert.True(view.Releases[0].IsYou);
        Assert.False(view.Releases[1].IsYou);
        Assert.All(view.Releases, r => Assert.True(r.IsUnread));
        Assert.Equal("0.4.0", view.SelectedVersion);
        Assert.Equal("0.2.0", view.LastSeen);                                     // carried out for the rail's dots
        Assert.Equal(new[] { "d1", "d2", "c1" }, view.MergedHighlights.Select(h => h.Highlight.Id));   // merged, capped at 3
        Assert.NotNull(view.Index);
    }

    [Fact]
    public async Task AnExplicitVersion_MeansThatVersionOnly()
    {
        var store = Build(out var http, embedded: Doc("0.4.0", "Drift"));
        Index(http, ("0.4.0", "0.4.0.9", "Drift"), ("0.3.0", "0.3.0.7", "Crest"), ("0.2.0", "0.2.0.3", "Breaker"));
        Remote(http, "0.3.0", Doc("0.3.0", "Crest"));

        var view = await LoadViewAsync(store, "0.3.0", lastSeen: "0.2.0", new RunningBuild("0.4.0", "stable", false), CancellationToken.None);

        var only = Assert.Single(view!.Releases);
        Assert.Equal("0.3.0", only.Doc.Version);
        Assert.False(only.IsYou);
        Assert.Equal("0.3.0", view.SelectedVersion);
    }

    [Fact]
    public async Task WithNoIndex_TheRunningDocumentStillLoads_AndReadsAsLatest()
    {
        var store = Build(out var http, embedded: Doc("0.4.0", "Drift"));
        http.OnUrl("whatsnew-index.json", HttpStatusCode.ServiceUnavailable, "down");

        var view = await LoadViewAsync(store, null, lastSeen: "0.2.0", new RunningBuild("0.4.0", "stable", false), CancellationToken.None);

        Assert.Equal("0.4.0", Assert.Single(view!.Releases).Doc.Version);
        Assert.Null(view.Index);                                                  // the rail hides
        Assert.True(IsLatest(view.Index, view.Releases[0].Doc.Version));          // parity 81
    }

    [Fact]
    public async Task NothingReachable_IsNull_WhichThePageRendersAsTheEmptyState()
    {
        var store = Build(out _);
        Assert.Null(await LoadViewAsync(store, "9.9.9", "", new RunningBuild("0.4.0", "stable", false), CancellationToken.None));
    }

    [Fact]
    public async Task TheIssueStateRepublish_EnrichesTheNewestDocumentOnly()
    {
        var store = Build(out var http, embedded: DocWithIssues(2));
        Index(http, ("0.3.0", "0.3.0.7", "Crest"), ("0.2.0", "0.2.0.3", "Breaker"));
        Remote(http, "0.2.0", Doc("0.2.0", "Breaker"));
        Issues(http, HttpStatusCode.OK);

        var view = await LoadViewAsync(store, null, lastSeen: "0.1.0", new RunningBuild("0.3.0", "stable", false), CancellationToken.None);
        var enriched = await WithIssueStatesAsync(store, view!, CancellationToken.None);

        Assert.NotNull(enriched.Releases[0].IssueStates);
        Assert.Null(enriched.Releases[1].IssueStates);
        Assert.Null(view!.Releases[0].IssueStates);                               // the first publish is never mutated
        Assert.Same(view.MergedHighlights, enriched.MergedHighlights);             // posters are not re-resolved
    }

    // ── the after-update plate's payload (§9.4 option a) ────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheSummary_IsTheRunningDocument_WithItsVisibleCards_AndTheirPosters()
    {
        var doc = Doc("0.4.0", "Drift", "a", "b", "c");
        doc.Highlights = [new ReleaseHighlight { Id = "store", Kind = "store" }, .. doc.Highlights];
        doc.Highlights[1].Media = new ReleaseMediaRef { Kind = "image", Src = "media/a.webp" };
        doc.Highlights[2].Media = new ReleaseMediaRef { Kind = "video", Src = "media/b.mp4", Poster = "media/b.webp" };
        var store = Build(out var http, embedded: doc);
        string media = Path.Combine(_root, "embedded", "media");
        Directory.CreateDirectory(media);
        File.WriteAllText(Path.Combine(media, "a.webp"), "x");
        File.WriteAllText(Path.Combine(media, "b.webp"), "x");

        var payload = await LoadSummaryAsync(store, new RunningBuild("0.4.0", "store", IsStore: true), CancellationToken.None);

        Assert.NotNull(payload);
        Assert.Equal("0.4.0", payload!.Value.Doc.Version);
        Assert.Equal(new[] { "a", "b", "c" }, payload.Value.Cards.Select(c => c.Highlight.Id));   // a Store install hides the store card
        Assert.Equal(Path.Combine(media, "a.webp"), payload.Value.Cards[0].Poster);
        Assert.Equal(Path.Combine(media, "b.webp"), payload.Value.Cards[1].Poster);               // a video names its still
        Assert.Null(payload.Value.Cards[2].Poster);                                                // no media: a tinted band
        Assert.Empty(http.Requests);
    }

    [Fact]
    public async Task TheSummary_ResolvesToNothing_WithNoStoreOrNoDocument()
    {
        Assert.Null(await LoadSummaryAsync(null, new RunningBuild("0.4.0", "stable", false), CancellationToken.None));
        var store = Build(out _);
        Assert.Null(await LoadSummaryAsync(store, new RunningBuild("0.4.0", "stable", false), CancellationToken.None));
    }

    // ── harness ─────────────────────────────────────────────────────────────────────────────────────────────────────

    ReleaseNotes.Store Build(out NotesHttp http, ReleaseNotesDocument? embedded = null, string feedRelease = "wavee-stable",
        bool cacheOnly = false, string? releasesRoot = null)
    {
        http = new NotesHttp();
        string embeddedDir = Path.Combine(_root, "embedded");
        Directory.CreateDirectory(embeddedDir);
        if (embedded is not null && !cacheOnly) File.WriteAllBytes(Path.Combine(embeddedDir, "whatsnew.json"), Bytes(embedded));
        return new ReleaseNotes.Store(new HttpClient(http), _root, feedRelease, embeddedDir, releasesRoot);
    }

    void WriteCache(string semver, ReleaseNotesDocument doc)
    {
        string dir = Path.Combine(_root, "cache", "whatsnew", semver);
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "whatsnew.json"), Bytes(doc));
    }

    static void Remote(NotesHttp http, string semver, ReleaseNotesDocument doc)
        => http.OnUrl("wavee-v" + semver + "/whatsnew.json", HttpStatusCode.OK, Encoding.UTF8.GetString(Bytes(doc)), "application/json");

    static void Index(NotesHttp http, params (string Version, string Quad, string Name)[] releases)
    {
        var index = new ReleaseNotesIndex
        {
            Releases = releases.Select(r => new ReleaseNotesIndexEntry { Version = r.Version, PackageVersion = r.Quad, Name = r.Name, Date = "2026-08-01", Channel = "stable" }).ToArray(),
        };
        http.OnUrl("whatsnew-index.json", HttpStatusCode.OK, JsonSerializer.Serialize(index, ReleaseNotesJsonContext.Default.ReleaseNotesIndex), "application/json");
    }

    static void Issues(NotesHttp http, HttpStatusCode status)
        => http.OnUrl("api.github.com", status,
            status == HttpStatusCode.OK ? "{\"state\":\"closed\",\"state_reason\":\"completed\",\"title\":\"Docked video\"}" : "{\"message\":\"rate limit exceeded\"}",
            "application/json");

    static byte[] Bytes(ReleaseNotesDocument doc) => JsonSerializer.SerializeToUtf8Bytes(doc, ReleaseNotesJsonContext.Default.ReleaseNotesDocument);

    static ReleaseNotesDocument Doc(string version, string name, params string[] highlightIds) => new()
    {
        Version = version,
        PackageVersion = version + ".1",
        Name = name,
        Tagline = "a tagline",
        Date = "2026-08-01",
        Highlights = highlightIds.Select(id => new ReleaseHighlight { Id = id, Title = id, Kind = "new" }).ToArray(),
    };

    static ReleaseNotesDocument DocWithIssues(int count)
    {
        var items = new List<ReleaseItem>(count);
        for (int i = 1; i <= count; i++)
            items.Add(new ReleaseItem { Id = "i" + i, Text = "Something " + i, Issues = [new ReleaseIssue { Repo = "christosk92/WaveeMusic", Number = i, State = "open" }] });
        var doc = Doc("0.3.0", "Crest");
        doc.Sections = [new ReleaseSection { Kind = "fixed", Items = items.ToArray() }];
        return doc;
    }
}

/// <summary>A scripted transport: first rule whose url substring matches answers; anything else is a 404. Records every
/// requested url, in order.</summary>
public sealed class NotesHttp : HttpMessageHandler
{
    readonly List<(string UrlContains, HttpStatusCode Status, string Body, string? ContentType, (string Name, string Value)[] Headers)> _rules = [];
    readonly Lock _gate = new();

    public List<string> Requests { get; } = [];

    public void OnUrl(string urlContains, HttpStatusCode status, string body, string? contentType = null, params (string Name, string Value)[] headers)
        => _rules.Add((urlContains, status, body, contentType, headers));

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string url = request.RequestUri?.ToString() ?? "";
        lock (_gate) Requests.Add(url);
        foreach (var rule in _rules)
        {
            if (!url.Contains(rule.UrlContains, StringComparison.Ordinal)) continue;
            var response = new HttpResponseMessage(rule.Status) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(rule.Body)) };
            if (rule.ContentType is { } type) response.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(type);
            foreach (var (name, value) in rule.Headers) response.Headers.TryAddWithoutValidation(name, value);
            return Task.FromResult(response);
        }
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new ByteArrayContent([]) });
    }
}
