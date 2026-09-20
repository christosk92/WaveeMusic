using System.Linq;
using Wavee.Core.ReleaseNotes;
using Xunit;

namespace Wavee.Tests;

// The CHANGELOG → ReleaseSection[] parser.
//
// The fixture below is NOT a copy of the shipping CHANGELOG.md — it is a hand-cut SAMPLE carrying one instance of each
// shape that file uses (an undated heading, a bold-opener bullet, a continuation line, inline code, a heading whose
// title is not its kind token, the link-reference footer). A verbatim copy pinned the changelog's CONTENT rather than
// the parser's behaviour: every release edit broke green tests that had nothing to do with parsing, and the fix was
// always to re-copy the text rather than to think about it. If the real file grows a shape this sample does not
// cover, add THAT SHAPE here — not another paragraph of prose.
//
// Nothing here reads a production file: a test that reads .md or .cs off disk pins the file, not the behaviour.
public class ChangelogParserTests
{
    // ── the sample, end to end ───────────────────────────────────────────────────────────────

    [Fact]
    public void ASampleOfEveryShape_ParsesIntoItsSectionsInOrder()
    {
        var releases = ChangelogParser.Parse(Sample);
        var rel = Assert.Single(releases);

        Assert.Equal("0.2.0", rel.Version);
        Assert.Equal("unreleased", rel.Date);                       // the release script dates it
        Assert.Equal(new[] { "added", "changed", "fixed", "removed", "known" }, rel.Sections.Select(s => s.Kind));
        Assert.Equal(new[] { 2, 1, 1, 1, 3 }, rel.Sections.Select(s => s.Items.Length));
    }

    [Fact]
    public void BoldOpeners_AreNotMistakenForScopes()
    {
        var rel = Assert.Single(ChangelogParser.Parse(Sample));
        var added = rel.Sections.Single(s => s.Kind == "added");

        // "- **Developer mode** — …" opens with emphasis, not a "Scope:" label. The markdown must survive intact.
        Assert.Null(added.Items[0].Scope);
        Assert.StartsWith("**Developer mode** —", added.Items[0].Text);
        Assert.All(rel.Sections.SelectMany(s => s.Items), i => Assert.Null(i.Scope));
    }

    [Fact]
    public void ContinuationLines_JoinTheBullet_WithASingleSpace()
    {
        var rel = Assert.Single(ChangelogParser.Parse(Sample));
        var added = rel.Sections.Single(s => s.Kind == "added");

        Assert.Equal(
            "**Developer mode** — an explicit Settings toggle that gates the diagnostic surfaces. Off by default, so a normal "
            + "install no longer exposes developer-only tooling.",
            added.Items[0].Text);
    }

    [Fact]
    public void ItemIds_AreStableWithinTheirSection()
    {
        var rel = Assert.Single(ChangelogParser.Parse(Sample));
        var known = rel.Sections.Single(s => s.Kind == "known");
        Assert.Equal(new[] { "known-0", "known-1", "known-2" }, known.Items.Select(i => i.Id));
    }

    [Fact]
    public void Find_PicksTheNamedRelease_AndNothingElse()
    {
        Assert.NotNull(ChangelogParser.Find(Sample, "0.2.0"));
        Assert.Null(ChangelogParser.Find(Sample, "0.3.0"));
        Assert.Null(ChangelogParser.Find(Sample, "0.2.0.1"));    // the quad is not a changelog key
    }

    // ── headings, dates, kinds ──────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void DatedAndUndatedHeadings_BothParse_NewestFirstAsWritten()
    {
        const string Md = """
## [0.3.0] - 2026-09-01

### Added

- One.

## [0.2.0] - unreleased

### Fixed

- Two.

## [0.1.0]

### Added

- Three.
""";
        var releases = ChangelogParser.Parse(Md);
        Assert.Equal(3, releases.Count);
        Assert.Equal("2026-09-01", releases[0].Date);
        Assert.Equal("unreleased", releases[1].Date);
        Assert.Null(releases[2].Date);
        Assert.Equal(new[] { "0.3.0", "0.2.0", "0.1.0" }, releases.Select(r => r.Version));
    }

    [Fact]
    public void EveryKeepAChangelogHeading_MapsToItsKind()
    {
        const string Md = """
## [1.0.0] - 2026-01-01

### Added
- a
### Changed
- b
### Deprecated
- c
### Removed
- d
### Fixed
- e
### Security
- f
### Known limitations
- g
### Something we do not know
- h
""";
        var rel = Assert.Single(ChangelogParser.Parse(Md));
        Assert.Equal(new[] { "added", "changed", "deprecated", "removed", "fixed", "security", "known" },
                     rel.Sections.Select(s => s.Kind));
        Assert.DoesNotContain(rel.Sections.SelectMany(s => s.Items), i => i.Text == "h");   // unknown heading is skipped
    }

    // ── scopes ──────────────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("- **Player:** Docked video keeps playing.", "Player", "Docked video keeps playing.")]
    [InlineData("- **Player**: Docked video keeps playing.", "Player", "Docked video keeps playing.")]
    [InlineData("- Player: Docked video keeps playing.", "Player", "Docked video keeps playing.")]
    [InlineData("- Queue and lists: reorder by drag.", "Queue and lists", "reorder by drag.")]
    public void LeadingScopeLabels_AreLifted(string bullet, string scope, string text)
    {
        var item = OneItem(bullet);
        Assert.Equal(scope, item.Scope);
        Assert.Equal(text, item.Text);
    }

    [Theory]
    [InlineData("- **Developer mode** — a Settings toggle.")]
    [InlineData("- lowercase: not a scope.")]
    [InlineData("- A scope label that is far too long to be one: nope.")]
    [InlineData("- Player:no space after the colon.")]
    [InlineData("- Setup › Local playback showed a stray chip row.")]
    public void NonScopes_LeaveTheTextAlone(string bullet)
    {
        var item = OneItem(bullet);
        Assert.Null(item.Scope);
        Assert.Equal(bullet[2..], item.Text);
    }

    // ── trailing reference groups ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TrailingRefGroup_BecomesIssuesAndPrs_AndLeavesTheText()
    {
        var item = OneItem("- Docked video keeps playing while you browse. (#412, !430, #388)");

        Assert.Equal("Docked video keeps playing while you browse.", item.Text);
        Assert.Equal(new[] { 412, 388 }, item.Issues.Select(i => i.Number));
        Assert.Equal(new[] { 430 }, item.Prs.Select(p => p.Number));
        Assert.All(item.Issues, i => Assert.Equal("christosk92/WaveeMusic", i.Repo));
        Assert.All(item.Prs, p => Assert.Equal("christosk92/WaveeMusic", p.Repo));
    }

    [Fact]
    public void TheDefaultRepo_IsOverridable()
    {
        var rel = ChangelogParser.Parse("## [1.0.0] - 2026-01-01\n\n### Fixed\n\n- Thing. (#7)\n", "acme/widgets");
        var item = Assert.Single(Assert.Single(rel).Sections[0].Items);
        Assert.Equal("acme/widgets", Assert.Single(item.Issues).Repo);
    }

    [Fact]
    public void MidSentenceReferences_StayInTheText()
    {
        var item = OneItem("- Reverted the change from (#412) because it regressed playback.");
        Assert.Empty(item.Issues);
        Assert.Equal("Reverted the change from (#412) because it regressed playback.", item.Text);
    }

    // ── robustness ──────────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("# Changelog\n\nNothing here yet.\n")]
    [InlineData("### Added\n\n- An orphan bullet with no release heading.\n")]
    public void NoReleases_IsEmptyNotAnError(string md)
    {
        Assert.Empty(ChangelogParser.Parse(md));
        Assert.Null(ChangelogParser.Find(md, "0.1.0"));
    }

    [Fact]
    public void CrLfAndLf_ParseIdentically()
    {
        const string Lf = "## [1.0.0] - 2026-01-01\n\n### Added\n\n- One thing\n  spanning two lines.\n";
        var a = Assert.Single(ChangelogParser.Parse(Lf));
        var b = Assert.Single(ChangelogParser.Parse(Lf.Replace("\n", "\r\n")));
        Assert.Equal(a.Sections[0].Items[0].Text, b.Sections[0].Items[0].Text);
        Assert.Equal("One thing spanning two lines.", a.Sections[0].Items[0].Text);
    }

    static ReleaseItem OneItem(string bullet)
    {
        var rel = Assert.Single(ChangelogParser.Parse("## [1.0.0] - 2026-01-01\n\n### Added\n\n" + bullet + "\n"));
        return Assert.Single(Assert.Single(rel.Sections).Items);
    }

    // One instance of each shape the shipping CHANGELOG.md uses — deliberately NOT a copy of it (see the file header).
    const string Sample = """
# Changelog

All notable changes to **Wavee** are documented here.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.2.0] - unreleased

### Added

- **Developer mode** — an explicit Settings toggle that gates the diagnostic surfaces. Off by default, so a normal
  install no longer exposes developer-only tooling.
- **Start on login** — an opt-in setting to launch Wavee when Windows starts.

### Changed

- **Image cache moved under `%LOCALAPPDATA%\Wavee\cache`**, joining the rest of the app's per-user state in one place.

### Fixed

- **Fabricated listening history on fresh installs.** A fresh install now starts empty.

### Removed

- **Environment-variable switches.** Behaviour is configured in Settings, never by an env var a shipped build honours.

### Known limitations

- No dedicated **episode** or **profile** pages.
- No **system tray** integration.
- **FLAC seek** is not implemented.

[0.2.0]: https://github.com/christosk92/WaveeMusic/releases/tag/wavee-v0.2.0
""";
}
