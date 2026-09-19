using System;
using System.Text;
using System.Text.Json;
using Wavee.Core.ReleaseNotes;
using Xunit;

namespace Wavee.Tests.ReleaseNotes;

/// <summary>
/// <see cref="ReleaseCommits"/> — the `commits.json` reader and the cross-check that attaches a commit to every
/// changelog item whose issue/PR it names, fills <see cref="ReleaseNotesDocument.UnlinkedCommits"/>, and reports
/// the two ways the CHANGELOG and the git range can disagree. Pure: no file system, no network.
/// </summary>
public sealed class ReleaseCommitsTests
{
    // 40-hex, so callers can slice a 7-char short SHA the same way the real tool does.
    const string Sha1 = "a1b2c3d4e5f6a7b8c9d0e1f2a3b4c5d6e7f8a9b0"; // short: a1b2c3d
    const string Sha2 = "9f8e7d6c5b4a392817069f5a4b3c2d1e0f9e8d76"; // short: 9f8e7d6
    const string Repo = ChangelogParser.WaveeRepo;
    const string Range = "wavee-v0.2.5..HEAD";

    static byte[] Json(string s) => Encoding.UTF8.GetBytes(s);

    // ── Parse ───────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Parse_ReadsTheScriptShape()
    {
        var commits = ReleaseCommits.Parse(Json($$"""
        [
          { "sha": "{{Sha1}}", "short": "a1b2c3d", "subject": "audio: fix xrun logging (#48)", "issues": [48], "prs": [] },
          { "sha": "{{Sha2}}", "short": "9f8e7d6", "subject": "docs: nothing to link", "issues": [], "prs": [52] }
        ]
        """));

        Assert.Equal(2, commits.Length);
        Assert.Equal(Sha1, commits[0].Sha);
        Assert.Equal("a1b2c3d", commits[0].Short);
        Assert.Equal("audio: fix xrun logging (#48)", commits[0].Subject);
        Assert.Equal(new[] { 48 }, commits[0].Issues);
        Assert.Empty(commits[0].Prs);
        Assert.Equal(new[] { 52 }, commits[1].Prs);
        Assert.Empty(commits[1].Issues);
    }

    [Fact]
    public void Parse_RepairsNulls_AndDerivesShortFromSha()
    {
        var commits = ReleaseCommits.Parse(Json($$"""
        [
          null,
          { "sha": "{{Sha1}}", "short": null, "subject": null, "issues": null, "prs": null }
        ]
        """));

        // The null array element is dropped, not surfaced.
        var c = Assert.Single(commits);
        Assert.Equal(Sha1, c.Sha);
        Assert.Equal("a1b2c3d", c.Short);   // derived from Sha since "short" was null
        Assert.Equal("", c.Subject);
        Assert.Empty(c.Issues);
        Assert.Empty(c.Prs);
    }

    [Fact]
    public void Parse_DerivesShortFromSha_WhenShortIsAnEmptyString()
    {
        var commits = ReleaseCommits.Parse(Json($$"""[ { "sha": "{{Sha1}}", "short": "" } ]"""));
        Assert.Equal("a1b2c3d", Assert.Single(commits).Short);
    }

    [Fact]
    public void Parse_RejectsACommitWithoutASha()
    {
        Assert.Throws<JsonException>(() => ReleaseCommits.Parse(Json("""[ { "subject": "no sha here" } ]""")));
    }

    [Fact]
    public void Parse_RejectsANonArray()
    {
        Assert.Throws<JsonException>(() => ReleaseCommits.Parse(Json("""{ "sha": "not an array" }""")));
    }

    [Fact]
    public void Parse_OfEmptyArray_IsEmpty()
    {
        Assert.Empty(ReleaseCommits.Parse(Json("[]")));
    }

    // ── Link — fixtures ─────────────────────────────────────────────────────────────────────────────────────

    static ReleaseCommit Commit(string sha, string subject, int[]? issues = null, int[]? prs = null) => new()
    {
        Sha = sha,
        Short = sha[..7],
        Subject = subject,
        Issues = issues ?? [],
        Prs = prs ?? [],
    };

    static ReleaseItem Item(int? issue = null, int? pr = null, string repo = Repo, bool issueIsPr = false) => new()
    {
        Id = "added-0",
        Text = "Some fix.",
        Issues = issue is { } n ? [new ReleaseIssue { Repo = repo, Number = n, Title = "T" + n, IsPullRequest = issueIsPr }] : [],
        Prs = pr is { } p ? [new ReleasePr { Repo = repo, Number = p, Title = "PR" + p }] : [],
    };

    static ReleaseNotesDocument Doc(params ReleaseItem[] items) => new()
    {
        Version = "0.2.6",
        Sections = [new ReleaseSection { Kind = "fixed", Items = items }],
    };

    // ── Link — attaching ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Link_AttachesACommitToEveryItemCitingOneOfItsIssues()
    {
        var doc = Doc(Item(issue: 48), Item(issue: 41));
        var fixes48 = Commit(Sha1, "fix: xrun logging", issues: [48]);
        var fixes41 = Commit(Sha2, "fix: audio output", issues: [41]);

        var errors = ReleaseCommits.Link(doc, [fixes48, fixes41], Repo, Range);

        Assert.Empty(errors);
        var items = doc.Sections[0].Items;
        Assert.Same(fixes48, Assert.Single(items[0].Commits));
        Assert.Same(fixes41, Assert.Single(items[1].Commits));
    }

    [Fact]
    public void Link_AttachesTheSameCommitToEveryMatchingItem_DedupedBySha()
    {
        var doc = Doc(Item(issue: 48), Item(pr: 48));
        var commit = Commit(Sha1, "fix: shared ref", issues: [48], prs: [48]);

        ReleaseCommits.Link(doc, [commit, commit], Repo, Range);

        var items = doc.Sections[0].Items;
        Assert.Single(items[0].Commits);
        Assert.Single(items[1].Commits);
    }

    [Fact]
    public void Link_TreatsAPrCitedWithHash_AsLinked()
    {
        // The CHANGELOG cites "#52" as an issue-shaped ref (per the trailing-group grammar), but the commit only
        // names it as a PR (squash suffix / !N). ReleaseCommits.Link still treats that as satisfied.
        var doc = Doc(Item(issue: 52));
        var commit = Commit(Sha1, "chore: squash (#52)", issues: [], prs: [52]);

        var errors = ReleaseCommits.Link(doc, [commit], Repo, Range);

        Assert.Empty(errors);
        Assert.Same(commit, Assert.Single(doc.Sections[0].Items[0].Commits));
    }

    [Fact]
    public void Link_DoesNotDemandTheChangelogCitePrs()
    {
        // A commit whose only ref is a PR number is not itself a "closing keyword" issue, so its absence from any
        // item's Issues does not produce a mismatch — it simply attaches wherever an item's Prs names it (or is
        // otherwise unlinked; see below).
        var doc = Doc(Item(pr: 52));
        var commit = Commit(Sha1, "chore: squash (#52)", issues: [], prs: [52]);

        var errors = ReleaseCommits.Link(doc, [commit], Repo, Range);

        Assert.Empty(errors);
        Assert.Same(commit, Assert.Single(doc.Sections[0].Items[0].Commits));
    }

    [Fact]
    public void Link_IgnoresIssuesOfAnotherRepo()
    {
        var doc = Doc(Item(issue: 48, repo: "someone/else"));
        var commit = Commit(Sha1, "fix: xrun logging", issues: [48]);

        var errors = ReleaseCommits.Link(doc, [commit], Repo, Range);

        // Not attached to the foreign-repo item; the commit is unlinked for THIS repo's purposes.
        Assert.Empty(doc.Sections[0].Items[0].Commits);
        Assert.Same(commit, Assert.Single(doc.UnlinkedCommits));
        Assert.Contains(errors, e => e.Contains("#48") && e.Contains("does not cite it"));
    }

    [Fact]
    public void Link_CollectsCommitsCitingNothing_AsUnlinked_InOrder()
    {
        var doc = Doc(Item(issue: 48));
        var fixes48 = Commit(Sha1, "fix: xrun logging", issues: [48]);
        var chore = Commit(Sha2, "chore: bump deps");

        ReleaseCommits.Link(doc, [chore, fixes48], Repo, Range);

        Assert.Equal([chore], doc.UnlinkedCommits);
    }

    // ── Link — mismatches ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Link_ReportsAnIssueFixedInGitButAbsentFromTheChangelog()
    {
        var doc = Doc(Item());   // the entry cites nothing, so the only mismatch is git's #48
        var commit = Commit(Sha1, "fix: xrun logging", issues: [48]);

        var errors = ReleaseCommits.Link(doc, [commit], Repo, Range);

        Assert.Equal(
            ["issue #48 is fixed by a1b2c3d \"fix: xrun logging\" but the CHANGELOG [0.2.6] entry does not cite it"],
            errors);
    }

    [Fact]
    public void Link_ReportsAChangelogIssueNoCommitFixes()
    {
        var doc = Doc(Item(issue: 48));

        var errors = ReleaseCommits.Link(doc, [], Repo, Range);

        Assert.Equal(
            [$"CHANGELOG cites #48 but no commit in {Range} carries \"Fixes #48\""],
            errors);
    }

    [Fact]
    public void Link_ListsEveryMismatchOnce_AscendingByIssue()
    {
        var doc = Doc(Item(issue: 90), Item(issue: 10));
        var commit41 = Commit(Sha1, "fix: 41", issues: [41]);
        var commit20 = Commit(Sha2, "fix: 20", issues: [20]);

        var errors = ReleaseCommits.Link(doc, [commit41, commit20], Repo, Range);

        // One line per issue number, globally ascending — missing-in-git and missing-in-changelog interleave by
        // number rather than sorting as two separate groups: 10, 20, 41, 90.
        Assert.Equal(
        [
            $"CHANGELOG cites #10 but no commit in {Range} carries \"Fixes #10\"",
            "issue #20 is fixed by 9f8e7d6 \"fix: 20\" but the CHANGELOG [0.2.6] entry does not cite it",
            "issue #41 is fixed by a1b2c3d \"fix: 41\" but the CHANGELOG [0.2.6] entry does not cite it",
            $"CHANGELOG cites #90 but no commit in {Range} carries \"Fixes #90\"",
        ], errors);
    }

    [Fact]
    public void Link_StillAttaches_WhenItReportsErrors()
    {
        // --allow-unlinked ships what it has: attaching and reporting are independent, not either/or.
        var doc = Doc(Item(issue: 48));
        var commit = Commit(Sha1, "fix: xrun logging", issues: [48, 41]);

        var errors = ReleaseCommits.Link(doc, [commit], Repo, Range);

        Assert.NotEmpty(errors);
        Assert.Same(commit, Assert.Single(doc.Sections[0].Items[0].Commits));
    }
}
