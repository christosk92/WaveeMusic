using System.Text.Json;
using Wavee.Core.ReleaseNotes;
using Xunit;

namespace Wavee.Tests;

// The whatsnew.json wire. It is written by Wavee.ReleaseTool and read by a shipped app that may be OLDER than the
// document it is reading, so two properties matter more than any single field: camelCase names are stable, and unknown
// members are ignored rather than fatal. Everything goes through the source-generated context (NativeAOT: no reflection).
public class ReleaseNotesJsonTests
{
    static ReleaseNotesDocument Read(string json)
        => JsonSerializer.Deserialize(json, ReleaseNotesJsonContext.Default.ReleaseNotesDocument)!;

    static string Write(ReleaseNotesDocument doc)
        => JsonSerializer.Serialize(doc, ReleaseNotesJsonContext.Default.ReleaseNotesDocument);

    [Fact]
    public void TheSchemaExample_Deserializes()
    {
        var doc = Read(Example);

        Assert.Equal(1, doc.Schema);
        Assert.Equal("wavee", doc.Product);
        Assert.Equal("0.3.0", doc.Version);
        Assert.Equal("0.3.0.1047", doc.PackageVersion);
        Assert.Equal("Breaker", doc.Name);
        Assert.Equal("2026-08-27", doc.Date);
        Assert.Equal("stable", doc.Channel);
        Assert.Equal("10.0.19041.0", doc.MinOs);
        Assert.Equal(new[] { "x64", "arm64" }, doc.Arch);
        Assert.Equal("https://github.com/christosk92/WaveeMusic/releases/tag/wavee-v0.3.0", doc.Links.Release);

        var highlight = Assert.Single(doc.Highlights);
        Assert.Equal("synced-lyrics-overlay", highlight.Id);
        Assert.Equal("wavee://now-playing?lyrics=1", highlight.DeepLink);
        Assert.Equal(new[] { 412 }, highlight.Issues);
        Assert.NotNull(highlight.Media);
        Assert.Equal("video", highlight.Media!.Kind);
        Assert.Equal("media/lyrics.mp4", highlight.Media.Src);
        Assert.Equal("media/lyrics.webp", highlight.Media.Poster);
        Assert.Equal(1200, highlight.Media.Width);
        Assert.Equal(512000, highlight.Media.Bytes);

        var section = Assert.Single(doc.Sections);
        Assert.Equal("added", section.Kind);
        var item = Assert.Single(section.Items);
        Assert.Equal("s1", item.Id);
        Assert.StartsWith("**Developer mode**", item.Text);
        var issue = Assert.Single(item.Issues);
        Assert.Equal("christosk92/WaveeMusic", issue.Repo);
        Assert.Equal(388, issue.Number);
        Assert.Equal("closed", issue.State);
        Assert.Equal("completed", issue.StateReason);
        Assert.False(issue.IsPullRequest);
        Assert.Equal(401, Assert.Single(item.Prs).Number);
        Assert.Equal("ChristosKarapasias", Assert.Single(item.Contributors).Login);

        Assert.Equal("breaking", Assert.Single(doc.Notices).Kind);
        Assert.True(Assert.Single(doc.Contributors).FirstTime);
        Assert.Equal("2026-08-27T14:03:11Z", doc.GeneratedAt);
        Assert.Equal("media/lyrics.webp", Assert.Single(doc.Media).Src);
    }

    [Fact]
    public void UnknownMembers_AreIgnored()
    {
        var doc = Read("""
{ "schema": 2, "product": "wavee", "version": "9.9.9", "somethingFromTheFuture": { "a": [1, 2] }, "highlights": [ { "title": "T", "surprise": true } ] }
""");
        Assert.Equal(2, doc.Schema);
        Assert.Equal("9.9.9", doc.Version);
        Assert.Equal("T", Assert.Single(doc.Highlights).Title);
    }

    [Fact]
    public void AnEmptyDocument_KeepsItsDefaults()
    {
        var doc = Read("{}");
        Assert.Equal(1, doc.Schema);
        Assert.Equal("wavee", doc.Product);
        Assert.Equal("stable", doc.Channel);
        Assert.Empty(doc.Sections);
        Assert.Empty(doc.Highlights);
        Assert.NotNull(doc.Links);
    }

    [Fact]
    public void RoundTrip_IsStable_AndCamelCased()
    {
        var once = Write(Read(Example));

        Assert.Contains("\"packageVersion\":", once);
        Assert.Contains("\"minOs\":", once);
        Assert.Contains("\"deepLink\":", once);
        Assert.Contains("\"stateReason\":", once);
        Assert.Contains("\"isPullRequest\":", once);
        Assert.Contains("\"generatedAt\":", once);
        Assert.DoesNotContain("\"PackageVersion\":", once);

        Assert.Equal(once, Write(Read(once)));
    }

    [Fact]
    public void Nulls_AreOmitted_NotWrittenAsNull()
    {
        string json = Write(new ReleaseNotesDocument
        {
            Version = "1.0.0",
            Highlights = [new ReleaseHighlight { Id = "h", Title = "T", Media = null, DeepLink = null }],
            Sections = [new ReleaseSection { Kind = "fixed", Items = [new ReleaseItem { Id = "fixed-0", Text = "x", Scope = null }] }],
        });

        Assert.DoesNotContain("null", json);
        Assert.DoesNotContain("\"deepLink\"", json);
        Assert.DoesNotContain("\"scope\"", json);
    }

    // ── commit linkage (ReleaseCommit) ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AnOldDocumentWithoutCommits_ReadsAsEmptyArrays()
    {
        // Example predates the commit-linkage fields entirely — no "commits" or "unlinkedCommits" anywhere in it.
        var doc = Read(Example);
        Assert.Empty(doc.UnlinkedCommits);
        Assert.Empty(Assert.Single(Assert.Single(doc.Sections).Items).Commits);
    }

    [Fact]
    public void Commits_RoundTrip_CamelCased()
    {
        var doc = new ReleaseNotesDocument
        {
            Version = "0.2.6",
            UnlinkedCommits =
            [
                new ReleaseCommit { Sha = "a1b2c3d4e5f6a7b8c9d0e1f2a3b4c5d6e7f8a9b0", Short = "a1b2c3d", Subject = "chore: bump" },
            ],
            Sections =
            [
                new ReleaseSection
                {
                    Kind = "fixed",
                    Items =
                    [
                        new ReleaseItem
                        {
                            Id = "fixed-0",
                            Text = "x",
                            Commits = [new ReleaseCommit { Sha = "9f8e7d6c5b4a392817069f5a4b3c2d1e0f9e8d76", Short = "9f8e7d6", Subject = "fix: y", Issues = [412] }],
                        },
                    ],
                },
            ],
        };

        string once = Write(doc);
        Assert.Contains("\"commits\":", once);
        Assert.Contains("\"unlinkedCommits\":", once);
        Assert.Contains("\"short\":", once);
        Assert.Equal(once, Write(Read(once)));
    }

    [Fact]
    public void AnOldIndexEntryWithoutIssues_ReadsAsEmpty()
    {
        const string Json = """
{ "schema": 1, "product": "wavee", "releases": [ { "version": "0.2.5", "packageVersion": "0.2.5.6", "name": "Old", "date": "2026-08-01", "channel": "stable" } ] }
""";
        var index = JsonSerializer.Deserialize(Json, ReleaseNotesJsonContext.Default.ReleaseNotesIndex)!;
        Assert.Empty(index.Releases[0].Issues);
    }

    // ── the index ───────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheIndex_RoundTrips_AndFindsByEitherKey()
    {
        const string Json = """
{ "schema": 1, "product": "wavee", "releases": [
  { "version": "0.3.0", "packageVersion": "0.3.0.22", "name": "Crest", "date": "2026-08-27", "channel": "stable" },
  { "version": "0.2.0", "packageVersion": "0.2.0.17", "name": "Breaker", "date": "2026-07-01", "channel": "stable" } ] }
""";
        var index = JsonSerializer.Deserialize(Json, ReleaseNotesJsonContext.Default.ReleaseNotesIndex)!;

        Assert.Equal(2, index.Releases.Length);
        Assert.Equal("Crest", index.Find("0.3.0")!.Name);
        Assert.Equal("Crest", index.Find("0.3.0.22")!.Name);
        Assert.Equal("Breaker", index.Find("0.2.0.17")!.Name);
        Assert.Null(index.Find("0.9.9"));
        Assert.Null(index.Find(""));

        string written = JsonSerializer.Serialize(index, ReleaseNotesJsonContext.Default.ReleaseNotesIndex);
        Assert.Equal("Crest", JsonSerializer.Deserialize(written, ReleaseNotesJsonContext.Default.ReleaseNotesIndex)!.Find("0.3.0")!.Name);
    }

    // ── the issue-state cache ───────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheIssueCache_RoundTrips_WithItsKeysVerbatim()
    {
        var cache = new IssueStateCache();
        cache.Set("christosk92/WaveeMusic#412", new IssueState { State = "closed", StateReason = "not_planned", Title = "Docked video", FetchedAtMs = 1234 });

        string json = JsonSerializer.Serialize(cache, ReleaseNotesJsonContext.Default.IssueStateCache);
        Assert.Contains("christosk92/WaveeMusic#412", json);      // dictionary keys are NOT camel-cased

        var back = JsonSerializer.Deserialize(json, ReleaseNotesJsonContext.Default.IssueStateCache)!;
        var entry = back.Lookup("christosk92/WaveeMusic#412");
        Assert.NotNull(entry);
        Assert.Equal("closed", entry!.State);
        Assert.Equal("not_planned", entry.StateReason);
        Assert.Equal(1234, entry.FetchedAtMs);
    }

    // The dossier's schema example (§4.3), with its JSONC comments removed and one member the app has never heard of.
    const string Example = """
{
  "schema": 1,
  "product": "wavee",
  "version": "0.3.0",
  "packageVersion": "0.3.0.1047",
  "name": "Breaker",
  "tagline": "Lyrics that follow you, and a lighter first run.",
  "date": "2026-08-27",
  "channel": "stable",
  "lang": "en",
  "minOs": "10.0.19041.0",
  "arch": ["x64", "arm64"],
  "links": {
    "release":   "https://github.com/christosk92/WaveeMusic/releases/tag/wavee-v0.3.0",
    "changelog": "https://github.com/christosk92/WaveeMusic/blob/wavee-v0.3.0/CHANGELOG.md",
    "compare":   "https://github.com/christosk92/WaveeMusic/compare/wavee-v0.2.0...wavee-v0.3.0"
  },
  "highlights": [
    {
      "id": "synced-lyrics-overlay",
      "title": "Synced lyrics, anywhere",
      "body": "Lyrics ride along in the **mini player** and full-screen view. Press `L` to toggle.",
      "media": { "kind": "video", "src": "media/lyrics.mp4", "poster": "media/lyrics.webp",
                 "alt": "Lyrics in the mini player", "width": 1200, "height": 675, "bytes": 512000 },
      "deepLink": "wavee://now-playing?lyrics=1",
      "issues": [412]
    }
  ],
  "sections": [
    { "kind": "added",
      "items": [
        { "id": "s1",
          "text": "**Developer mode** - a Settings toggle that gates the diagnostic surfaces.",
          "issues": [ { "repo": "christosk92/WaveeMusic", "number": 388, "title": "Hide dev tools by default",
                        "state": "closed", "stateReason": "completed", "isPullRequest": false } ],
          "prs":    [ { "repo": "christosk92/WaveeMusic", "number": 401, "title": "feat: developer mode" } ],
          "contributors": [ { "login": "ChristosKarapasias", "firstTime": false } ] } ] }
  ],
  "notices": [ { "kind": "breaking", "text": "Environment-variable switches are gone; use Settings." } ],
  "contributors": [ { "login": "someone", "firstTime": true } ],
  "generatedAt": "2026-08-27T14:03:11Z",
  "media": [ { "src": "media/lyrics.webp", "bytes": 91234, "sha256": "abc123" } ],
  "somethingTheAppHasNeverHeardOf": { "nested": [1, 2, 3] }
}
""";
}
