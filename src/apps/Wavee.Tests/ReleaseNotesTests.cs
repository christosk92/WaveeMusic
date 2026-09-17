// ── Wavee.Tests/ReleaseNotesTests.cs — the release-notes CORE (G-201) and the page's pure rules ─────────────────────────
//
// Ported from 0.2.9's `Wavee.Tests/ReleaseNotes/{ChangelogParserTests, MarkdownLiteTests, ReleaseNotesRangeTests,
// HighlightVisibilityTests, HighlightCardMetricsTests, HighlightViewerLayoutTests, HighlightViewerMotionTests,
// IssueStateBudgetTests}.cs` against `Screens/ReleaseNotes.cs` (the CORE nested under `Wavee.ReleaseNotes`), plus the new
// facts for the page's pure decisions (links, date, version sentence, after-update gate, rail markers, chips, avatars, the
// highlight merge) — most in `ReleaseNotes.cs`, the few that name an app type (`AfterUpdateGate.Consume`, `RailMarkerFor`,
// the two loc-key maps, `SlideId`) in `ReleaseNotes.Host.cs`, G-265. The wire/validation half is `ReleaseNotesValidationTests.cs`; the store
// and the loaders are `ReleaseNotesStoreTests.cs`. Pure: no engine loop, no network, no profile folder.

using Wavee;
using Xunit;
using static Wavee.ReleaseNotes;

namespace Wavee.Tests;

// ── ChangelogParser ──────────────────────────────────────────────────────────────────────────────────────────────────
// The fixture is a hand-cut SAMPLE carrying one instance of each shape the shipping CHANGELOG.md uses — NOT a copy of it
// (a verbatim copy pinned the changelog's content, not the parser). Nothing here reads a production file.
public class ChangelogParserTests
{
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

    // One instance of each shape the shipping CHANGELOG.md uses — deliberately NOT a copy of it (see the header).
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

// ── MarkdownLite ─────────────────────────────────────────────────────────────────────────────────────────────────────
// Every case is a rendering decision the user sees; the tokenizer is the one component that must NEVER throw.
public class MarkdownLiteTests
{
    static InlineToken[] T(string s) => MarkdownLite.Tokenize(s);
    static string Plain(string s) => string.Concat(T(s).Select(t => t.Text));

    [Fact]
    public void Empty_YieldsNothing()
    {
        Assert.Empty(MarkdownLite.Tokenize(""));
        Assert.Empty(MarkdownLite.Tokenize(null!));
    }

    [Fact]
    public void PlainProse_IsOneTextRun()
    {
        var only = Assert.Single(T("Just a sentence with no markup at all."));
        Assert.Equal(InlineKind.Text, only.Kind);
        Assert.Equal("Just a sentence with no markup at all.", only.Text);
    }

    [Fact]
    public void Bold_Code_AndEmphasis_Split()
    {
        var tokens = T("**Docked video** rides in `MediaPlayerElement`, *finally*.");

        Assert.Equal(InlineKind.Bold, tokens[0].Kind);
        Assert.Equal("Docked video", tokens[0].Text);
        Assert.Equal(InlineKind.Text, tokens[1].Kind);
        Assert.Equal(" rides in ", tokens[1].Text);
        Assert.Equal(InlineKind.Code, tokens[2].Kind);
        Assert.Equal("MediaPlayerElement", tokens[2].Text);
        Assert.Equal(InlineKind.Bold, tokens[4].Kind);      // single-asterisk emphasis renders as weight
        Assert.Equal("finally", tokens[4].Text);
    }

    [Theory]
    [InlineData("**bold with no close")]
    [InlineData("`code with no close")]
    [InlineData("[link with no target")]
    [InlineData("*")]
    [InlineData("**")]
    [InlineData("]([)")]
    public void UnbalancedMarkers_FallThroughAsText(string s)
    {
        Assert.All(T(s), t => Assert.Equal(InlineKind.Text, t.Kind));
        Assert.Equal(s, Plain(s));
    }

    [Fact]
    public void BackslashEscapes_TheNextCharacter()
    {
        var only = Assert.Single(T(@"\#412 is not a reference"));
        Assert.Equal(InlineKind.Text, only.Kind);
        Assert.Equal("#412 is not a reference", only.Text);
    }

    [Fact]
    public void MarkdownLink_CarriesTextAndTarget()
    {
        var link = Assert.Single(T("See the [release notes](https://example.com/notes) for more."), t => t.Kind == InlineKind.Link);
        Assert.Equal("release notes", link.Text);
        Assert.Equal("https://example.com/notes", link.Target);
    }

    [Fact]
    public void BareUrl_BecomesItsOwnTarget()
    {
        var url = Assert.Single(T("Docs live at https://example.com/a/b, honest."), t => t.Kind == InlineKind.Url);
        Assert.Equal("https://example.com/a/b", url.Text);
        Assert.Equal("https://example.com/a/b", url.Target);   // the trailing comma is prose, not the URL
    }

    [Fact]
    public void BareIssueRef_Lifts()
    {
        var tokens = T("Fixed in #412 at last.");
        var issue = Assert.Single(tokens, t => t.Kind == InlineKind.Issue);
        Assert.Equal("#412", issue.Text);
        Assert.Equal(412, issue.Number);
        Assert.Null(issue.Repo);
        Assert.Equal("Fixed in ", tokens[0].Text);
        Assert.Equal(" at last.", tokens[2].Text);
    }

    [Fact]
    public void BangRef_IsAPullRequest()
    {
        var pr = Assert.Single(T("Landed as !430."), t => t.Kind == InlineKind.Pr);
        Assert.Equal("!430", pr.Text);
        Assert.Equal(430, pr.Number);
    }

    [Fact]
    public void RefGroup_InParentheses_Lifts_AndKeepsTheSeparators()
    {
        var tokens = T("(#12, !34)");
        Assert.Equal(InlineKind.Text, tokens[0].Kind);
        Assert.Equal("(", tokens[0].Text);
        Assert.Equal(InlineKind.Issue, tokens[1].Kind);
        Assert.Equal(12, tokens[1].Number);
        Assert.Equal(", ", tokens[2].Text);
        Assert.Equal(InlineKind.Pr, tokens[3].Kind);
        Assert.Equal(34, tokens[3].Number);
        Assert.Equal(")", tokens[4].Text);
    }

    [Fact]
    public void QualifiedRef_TakesTheRepoPrefixBackOffTheTextRun()
    {
        var tokens = T("Tracked as christosk92/WaveeMusic#412 upstream.");

        Assert.Equal(InlineKind.Text, tokens[0].Kind);
        Assert.Equal("Tracked as ", tokens[0].Text);            // the prefix is NOT left dangling in the text run
        Assert.Equal(InlineKind.Issue, tokens[1].Kind);
        Assert.Equal("christosk92/WaveeMusic#412", tokens[1].Text);
        Assert.Equal("christosk92/WaveeMusic", tokens[1].Repo);
        Assert.Equal(412, tokens[1].Number);
        Assert.Equal(" upstream.", tokens[2].Text);
    }

    [Theory]
    [InlineData("C# is a language")]              // '#' not preceded by whitespace/'('/','
    [InlineData("C#14 shipped")]                  // ditto, even with digits behind it
    [InlineData("Wow!5 things")]                  // '!' mid-word is punctuation
    [InlineData("Nothing to see # here")]         // no digits
    [InlineData("#abc is not a number")]
    [InlineData("#12abc is not a reference")]     // a reference does not run into a word
    [InlineData("a/b/c#3 is not a repo slug")]    // two slashes
    public void NonReferences_StayText(string s)
    {
        Assert.DoesNotContain(T(s), t => t.Kind is InlineKind.Issue or InlineKind.Pr);
        Assert.Equal(s, Plain(s));
    }

    [Fact]
    public void Mention_KeepsTheAtForDisplay_AndTheLoginAsTarget()
    {
        var tokens = T("Thanks @ChristosKarapasias-1 for the fix.");
        var mention = Assert.Single(tokens, t => t.Kind == InlineKind.Mention);
        Assert.Equal("@ChristosKarapasias-1", mention.Text);
        Assert.Equal("ChristosKarapasias-1", mention.Target);
        Assert.Equal(" for the fix.", tokens[^1].Text);
    }

    [Theory]
    [InlineData("mail me at name@example.com")]   // '@' preceded by a letter is not a mention
    [InlineData("@ alone")]                       // nothing to mention
    public void NonMentions_StayText(string s)
    {
        Assert.DoesNotContain(T(s), t => t.Kind == InlineKind.Mention);
        Assert.Equal(s, Plain(s));
    }

    [Fact]
    public void TokenizingIsTotal_OverAWholeChangelogBullet()
    {
        const string Bullet =
            "**Developer mode** — an explicit Settings toggle that gates the diagnostic surfaces. See " +
            "christosk92/WaveeMusic#388 and !401, thanks @someone, docs at https://example.com/dev.";

        var tokens = T(Bullet);
        Assert.Contains(tokens, t => t.Kind == InlineKind.Bold);
        Assert.Contains(tokens, t => t.Kind == InlineKind.Issue && t.Repo == "christosk92/WaveeMusic");
        Assert.Contains(tokens, t => t.Kind == InlineKind.Pr && t.Number == 401);
        Assert.Contains(tokens, t => t.Kind == InlineKind.Mention && t.Target == "someone");
        Assert.Contains(tokens, t => t.Kind == InlineKind.Url);
        Assert.All(tokens, t => Assert.False(string.IsNullOrEmpty(t.Text)));
    }

    [Fact]
    public void Bold_TokenizesItsInside_SoCodeInsideBoldIsCode()
    {
        // The shipping changelog writes "**dismissed with `Esc`,**": the code span must survive inside the bold run.
        var t = MarkdownLite.Tokenize("**dismissed with `Esc`,** dropping");
        Assert.Equal(InlineKind.Bold, t[0].Kind); Assert.Equal("dismissed with ", t[0].Text);
        Assert.Equal(InlineKind.Code, t[1].Kind); Assert.Equal("Esc", t[1].Text); Assert.True(t[1].Bold);
        Assert.Equal(InlineKind.Bold, t[2].Kind); Assert.Equal(",", t[2].Text);
        Assert.Equal(InlineKind.Text, t[3].Kind); Assert.Equal(" dropping", t[3].Text); Assert.False(t[3].Bold);
    }
}

// ── ReleaseNotesRange ("since you last looked") ─────────────────────────────────────────────────────────────────────
// Wrong in either direction is silently bad: too many and a user re-reads notes, too few and a release vanishes forever.
public class ReleaseNotesRangeTests
{
    static ReleaseNotesIndex Index(params ReleaseNotesIndexEntry[] entries) => new() { Releases = entries };

    static ReleaseNotesIndexEntry E(string version, string quad, string channel = "stable")
        => new() { Version = version, PackageVersion = quad, Name = "N" + version, Date = "2026-01-01", Channel = channel };

    static readonly ReleaseNotesIndex Stable = Index(
        E("0.4.0", "0.4.0.30"), E("0.3.0", "0.3.0.22"), E("0.2.1", "0.2.1.18"), E("0.2.0", "0.2.0.17"), E("0.1.0", "0.1.0.4"));

    static string[] Versions(ReleaseNotesIndexEntry[] entries) => entries.Select(e => e.Version).ToArray();

    [Fact]
    public void TheRangeIsHalfOpenBelow_AndClosedAbove()
        => Assert.Equal(new[] { "0.4.0", "0.3.0", "0.2.1" }, Versions(ReleaseNotesRange.Between("0.2.0", "0.4.0", Stable, "stable")));

    [Fact]
    public void NewestFirst_EvenWhenTheIndexIsOutOfOrder()
    {
        var shuffled = Index(E("0.2.1", "0.2.1.18"), E("0.4.0", "0.4.0.30"), E("0.3.0", "0.3.0.22"), E("0.2.0", "0.2.0.17"));
        Assert.Equal(new[] { "0.4.0", "0.3.0", "0.2.1" }, Versions(ReleaseNotesRange.Between("0.2.0", "0.4.0", shuffled, "stable")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("dev")]
    [InlineData("not-a-version")]
    public void NoUsableLastSeen_ShowsOnlyTheCurrentRelease(string lastSeen)
        => Assert.Equal(new[] { "0.3.0" }, Versions(ReleaseNotesRange.Between(lastSeen, "0.3.0", Stable, "stable")));

    [Fact]
    public void AnUnknownCurrentRelease_YieldsNothing()
    {
        Assert.Empty(ReleaseNotesRange.Between("0.2.0", "9.9.9", Stable, "stable"));
        Assert.Empty(ReleaseNotesRange.Between("0.2.0", "", Stable, "stable"));
    }

    [Fact]
    public void AnEmptyIndex_YieldsNothing()
    {
        Assert.Empty(ReleaseNotesRange.Between("0.2.0", "0.3.0", new ReleaseNotesIndex(), "stable"));
        Assert.Empty(ReleaseNotesRange.Between("0.2.0", "0.3.0", null!, "stable"));
    }

    [Fact]
    public void AlreadyCurrent_IsJustTheCurrentRelease()
        => Assert.Equal(new[] { "0.3.0" }, Versions(ReleaseNotesRange.Between("0.3.0", "0.3.0", Stable, "stable")));

    // The page once advanced lastSeen BEFORE its loader read it, so Between() always got (running, running): the banner
    // and every unread dot were unreachable by construction and nothing failed. lastSeen is read first and carried in.
    [Fact]
    public void AdvancingLastSeenBeforeReadingIt_CollapsesTheWholeStack()
    {
        Assert.Equal(new[] { "0.4.0", "0.3.0", "0.2.1", "0.2.0" }, Versions(ReleaseNotesRange.Between("0.1.0", "0.4.0", Stable, "stable")));
        Assert.Equal(new[] { "0.4.0" }, Versions(ReleaseNotesRange.Between("0.4.0", "0.4.0", Stable, "stable")));
    }

    [Fact]
    public void TheUnreadRule_IsTheSameComparison_AndGoesQuietOnceLastSeenCatchesUp()
    {
        Assert.True(Notify.AppUpdateVersion.IsNewer("0.4.0", "0.1.0"));
        Assert.True(Notify.AppUpdateVersion.IsNewer("0.2.1", "0.1.0"));
        Assert.False(Notify.AppUpdateVersion.IsNewer("0.4.0", "0.4.0"));
        Assert.False(Notify.AppUpdateVersion.IsNewer("0.2.1", "0.4.0"));
    }

    [Fact]
    public void AnUpdatedQuad_IsAcceptedAsTheLastSeenKey()
    {
        Assert.Equal(new[] { "0.3.0", "0.2.1" }, Versions(ReleaseNotesRange.Between("0.2.0.17", "0.3.0", Stable, "stable")));
        Assert.Equal(new[] { "0.3.0" }, Versions(ReleaseNotesRange.Between("0.2.1.18", "0.3.0", Stable, "stable")));
    }

    [Fact]
    public void TheCurrentReleaseCanBeNamedByItsQuad()
        => Assert.Equal(new[] { "0.3.0", "0.2.1" }, Versions(ReleaseNotesRange.Between("0.2.0", "0.3.0.22", Stable, "stable")));

    static readonly ReleaseNotesIndex Mixed = Index(
        E("0.4.0", "0.4.0.30"), E("0.4.0-beta.2", "0.4.0.29", "beta"), E("0.4.0-beta.1", "0.4.0.28", "beta"), E("0.3.0", "0.3.0.22"));

    [Fact]
    public void StableChannel_SkipsPrereleases()
        => Assert.Equal(new[] { "0.4.0" }, Versions(ReleaseNotesRange.Between("0.3.0", "0.4.0", Mixed, "stable")));

    [Fact]
    public void BetaChannel_StacksPrereleases_BelowTheirRelease()
        => Assert.Equal(new[] { "0.4.0", "0.4.0-beta.2", "0.4.0-beta.1" }, Versions(ReleaseNotesRange.Between("0.3.0", "0.4.0", Mixed, "beta")));

    [Fact]
    public void APrereleaseCanBeTheCurrentRelease_OnEitherChannel()
    {
        Assert.Equal(new[] { "0.4.0-beta.2", "0.4.0-beta.1" }, Versions(ReleaseNotesRange.Between("0.3.0", "0.4.0-beta.2", Mixed, "beta")));
        Assert.Equal(new[] { "0.4.0-beta.2" }, Versions(ReleaseNotesRange.Between("0.3.0", "0.4.0-beta.2", Mixed, "stable")));
    }
}

// ── HighlightVisibility ─────────────────────────────────────────────────────────────────────────────────────────────
// A Store install never sees the "store" announcement — and a hidden store card must FREE its slot, not shrink the strip.
public class HighlightVisibilityTests
{
    static ReleaseHighlight H(string kind, string id = "h") => new() { Id = id, Title = "T", Body = "B", Kind = kind };

    [Fact]
    public void AStoreHighlight_IsHiddenOnAStoreInstall() => Assert.False(HighlightVisibility.IsVisible(H("store"), isStoreInstall: true));

    [Fact]
    public void AStoreHighlight_IsVisibleOnAFeedInstall() => Assert.True(HighlightVisibility.IsVisible(H("store"), isStoreInstall: false));

    [Theory]
    [InlineData("new")]
    [InlineData("improved")]
    [InlineData("rebuilt")]
    [InlineData("fixed")]
    [InlineData("added")]
    [InlineData("")]
    [InlineData("some-future-kind")]
    public void EveryOtherKind_IsVisibleEverywhere(string kind)
    {
        Assert.True(HighlightVisibility.IsVisible(H(kind), isStoreInstall: false));
        Assert.True(HighlightVisibility.IsVisible(H(kind), isStoreInstall: true));
        Assert.False(HighlightVisibility.IsStore(H(kind)));
    }

    [Theory]
    [InlineData("store")]
    [InlineData("Store")]
    [InlineData("STORE")]
    public void TheStoreKind_IsCaseInsensitive(string kind)
    {
        Assert.True(HighlightVisibility.IsStore(H(kind)));
        Assert.False(HighlightVisibility.IsVisible(H(kind), isStoreInstall: true));
    }

    [Fact]
    public void ANullHighlight_IsNeitherStoreNorVisible()
    {
        Assert.False(HighlightVisibility.IsStore(null));
        Assert.False(HighlightVisibility.IsVisible(null, isStoreInstall: false));
        Assert.False(HighlightVisibility.IsVisible(null, isStoreInstall: true));
    }

    [Fact]
    public void OnAStoreInstall_AHiddenStoreCard_FreesItsSlot()
    {
        var doc = new[] { H("store", "store"), H("added", "a"), H("fixed", "b"), H("new", "c") };
        Assert.Equal(new[] { "a", "b", "c" }, HighlightVisibility.SelectVisible(doc, isStoreInstall: true, max: 3).Select(h => h.Id));
    }

    [Fact]
    public void OnAFeedInstall_TheStoreCard_TakesASlotLikeAnyOther()
    {
        var doc = new[] { H("store", "store"), H("added", "a"), H("fixed", "b"), H("new", "c") };
        Assert.Equal(new[] { "store", "a", "b" }, HighlightVisibility.SelectVisible(doc, isStoreInstall: false, max: 3).Select(h => h.Id));
    }

    [Fact]
    public void AStoreOnlyDocument_RendersNoCardsOnAStoreInstall()
        => Assert.Empty(HighlightVisibility.SelectVisible(new[] { H("store") }, isStoreInstall: true, max: 3));

    [Fact]
    public void SelectVisible_DropsNullElements_AndToleratesNullAndEmptyInput()
    {
        Assert.Empty(HighlightVisibility.SelectVisible(null, isStoreInstall: false, max: 3));
        Assert.Empty(HighlightVisibility.SelectVisible(new ReleaseHighlight?[] { null }, isStoreInstall: false, max: 3));
        Assert.Empty(HighlightVisibility.SelectVisible(new[] { H("added") }, isStoreInstall: false, max: 0));
    }
}

// ── HighlightCardMetrics ────────────────────────────────────────────────────────────────────────────────────────────
// The after-update plate RENDERS from these constants, so the 620-DIP cap asserted here is a real gate.
public class HighlightCardMetricsTests
{
    [Theory]
    [InlineData(3, false, 2, 505f)]
    [InlineData(3, true, 2, 525f)]
    [InlineData(2, false, 2, 568f)]
    [InlineData(2, true, 2, 588f)]
    [InlineData(1, false, 2, 583f)]
    [InlineData(1, true, 2, 603f)]
    [InlineData(3, false, 1, 486f)]
    public void DialogHeight_MatchesTheBudgetTable(int cardCount, bool store, int taglineLines, float expected)
        => Assert.Equal(expected, HighlightCardMetrics.DialogHeight(cardCount, store, taglineLines));

    [Theory]
    [InlineData(1, false, 132f)]
    [InlineData(2, false, 150f)]
    [InlineData(1, true, 152f)]
    [InlineData(2, true, 170f)]
    public void TextBlockHeight_MatchesTheArithmetic(int titleLines, bool store, float expected)
        => Assert.Equal(expected, HighlightCardMetrics.TextBlockHeight(titleLines, store));

    [Theory]
    [InlineData(3, 216f)]
    [InlineData(2, 329f)]
    [InlineData(1, 356f)]
    public void DialogCardWidth_SplitsTheInnerPlateEvenlyAndCapsTheLoneCard(int cardCount, float expected)
        => Assert.Equal(expected, HighlightCardMetrics.DialogCardWidth(cardCount));

    [Theory]
    [InlineData(216f, 122f)]
    [InlineData(329f, 185f)]
    [InlineData(356f, 200f)]
    [InlineData(420f, 236f)]
    public void BandHeight_IsTheRounded16By9Band(float cardWidth, float expected)
        => Assert.Equal(expected, HighlightCardMetrics.BandHeight(cardWidth));

    [Theory]
    [InlineData(51f)]
    [InlineData(68f)]
    [InlineData(68.5f)]
    public void Overflows_IsFalseAtOrBelowFourLines(float naturalHeight) => Assert.False(HighlightCardMetrics.Overflows(naturalHeight));

    [Theory]
    [InlineData(85f)]
    [InlineData(102f)]
    public void Overflows_IsTrueAboveFourLines(float naturalHeight) => Assert.True(HighlightCardMetrics.Overflows(naturalHeight));

    [Fact]
    public void DialogHeight_NeverExceedsThePlateCap_ForEveryRenderableShape()
    {
        for (int cards = 1; cards <= 3; cards++)
            foreach (bool store in new[] { false, true })
                for (int taglineLines = 1; taglineLines <= 2; taglineLines++)
                {
                    float height = HighlightCardMetrics.DialogHeight(cards, store, taglineLines);
                    Assert.True(height <= HighlightCardMetrics.PlateMaxHeight,
                        $"cards={cards} store={store} taglineLines={taglineLines} → {height} > {HighlightCardMetrics.PlateMaxHeight}");
                }
    }
}

// ── HighlightViewerLayout ───────────────────────────────────────────────────────────────────────────────────────────
public class HighlightViewerLayoutTests
{
    [Theory]
    [InlineData(1440f, 900f, 960f)]
    [InlineData(1100f, 700f, 604f)]
    [InlineData(900f, 600f, 427f)]
    [InlineData(500f, 420f, 320f)]
    [InlineData(320f, 600f, 224f)]   // the window edge WINS over the floor
    public void PlateWidth_MatchesTheClampLadder(float vpW, float vpH, float expected)
        => Assert.Equal(expected, HighlightViewerLayout.PlateWidth(vpW, vpH));

    [Fact]
    public void ImageHeight_IsThe16By9BandWhenAPosterExists()
    {
        Assert.Equal(540f, HighlightViewerLayout.ImageHeight(960f, hasPoster: true));
        Assert.Equal(240f, HighlightViewerLayout.ImageHeight(427f, hasPoster: true));
    }

    // Issue #89 L4: a no-poster slide keeps the SAME w·9/16 band (tinted, not smaller).
    [Theory]
    [InlineData(960f)]
    [InlineData(427f)]
    [InlineData(320f)]
    public void ImageHeight_IsTheSame16By9BandWithOrWithoutAPoster(float plateWidth)
        => Assert.Equal(HighlightViewerLayout.ImageHeight(plateWidth, hasPoster: true), HighlightViewerLayout.ImageHeight(plateWidth, hasPoster: false));

    [Fact]
    public void Chrome_FitsInsideTheSmallestBand()
        => Assert.True(HighlightViewerLayout.ChromeInset * 2f + HighlightViewerLayout.ChromeCircle
                       <= HighlightViewerLayout.ImageHeight(HighlightViewerLayout.PlateMinWidth, hasPoster: false));

    [Theory]
    [InlineData(0, 3, HighlightNavKey.Previous, 0, HighlightSlideDirection.None)]
    [InlineData(1, 3, HighlightNavKey.Previous, 0, HighlightSlideDirection.Back)]
    [InlineData(1, 3, HighlightNavKey.Next, 2, HighlightSlideDirection.Forward)]
    [InlineData(2, 3, HighlightNavKey.Next, 2, HighlightSlideDirection.None)]
    [InlineData(2, 3, HighlightNavKey.First, 0, HighlightSlideDirection.Back)]
    [InlineData(0, 3, HighlightNavKey.First, 0, HighlightSlideDirection.None)]
    [InlineData(0, 3, HighlightNavKey.Last, 2, HighlightSlideDirection.Forward)]
    [InlineData(2, 3, HighlightNavKey.Last, 2, HighlightSlideDirection.None)]
    public void Step_IsClampedAndNeverWraps(int current, int count, HighlightNavKey key, int expectedIndex, HighlightSlideDirection expectedDirection)
    {
        var step = HighlightViewerLayout.Step(current, count, key);
        Assert.Equal(expectedIndex, step.Index);
        Assert.Equal(expectedDirection, step.Direction);
    }

    [Theory]
    [InlineData(HighlightNavKey.Previous)]
    [InlineData(HighlightNavKey.Next)]
    [InlineData(HighlightNavKey.First)]
    [InlineData(HighlightNavKey.Last)]
    public void Step_WithASingleItem_IsAlwaysNone(HighlightNavKey key)
    {
        var step = HighlightViewerLayout.Step(0, 1, key);
        Assert.Equal(0, step.Index);
        Assert.Equal(HighlightSlideDirection.None, step.Direction);
    }

    [Theory]
    [InlineData(1, 3, 5, 3, HighlightSlideDirection.Forward)]
    [InlineData(3, 1, 5, 1, HighlightSlideDirection.Back)]
    [InlineData(2, 2, 5, 2, HighlightSlideDirection.None)]
    public void StepTo_DirectionIsTheSignOfTheMove(int current, int target, int count, int expectedIndex, HighlightSlideDirection expectedDirection)
    {
        var step = HighlightViewerLayout.StepTo(current, target, count);
        Assert.Equal(expectedIndex, step.Index);
        Assert.Equal(expectedDirection, step.Direction);
    }

    [Fact]
    public void StepTo_WithASingleItem_IsAlwaysNone()
    {
        var step = HighlightViewerLayout.StepTo(0, 0, 1);
        Assert.Equal(0, step.Index);
        Assert.Equal(HighlightSlideDirection.None, step.Direction);
    }
}

// ── HighlightViewerMotion (UI file, engine-typed, but headless-safe: it builds records, nothing runs) ─────────────────
// The engine seeds an orphan's exit from the spec it MOUNTED with, so every recipe must exit the same way.
public class HighlightViewerMotionTests
{
    [Fact]
    public void SlideDistance_MatchesTheEngineEntranceOffset()
        => Assert.Equal(FluentGpu.Dsl.Motion.EntranceOffsetPx, HighlightViewerMotion.SlideDistance);

    [Fact]
    public void SlideForward_EntersFromPositive24()
    {
        var t = HighlightViewerMotion.SlideForward;
        Assert.Equal(HighlightViewerMotion.SlideDistance, t.Enter.Dx);
        Assert.Equal(0f, t.Enter.Opacity);
        Assert.True(t.Enter.Active);
    }

    [Fact]
    public void SlideBack_EntersFromNegative24()
    {
        var t = HighlightViewerMotion.SlideBack;
        Assert.Equal(-HighlightViewerMotion.SlideDistance, t.Enter.Dx);
        Assert.Equal(0f, t.Enter.Opacity);
        Assert.True(t.Enter.Active);
    }

    [Fact]
    public void ExitOnly_HasNoEntrance() => Assert.False(HighlightViewerMotion.ExitOnly.Enter.Active);

    [Fact]
    public void EveryRecipe_ExitsInPlace()
    {
        foreach (var t in new[] { HighlightViewerMotion.SlideForward, HighlightViewerMotion.SlideBack, HighlightViewerMotion.ExitOnly })
        {
            Assert.Equal(0f, t.Exit.Dx);
            Assert.Equal(0f, t.Exit.Opacity);
            Assert.True(t.Exit.Active);
        }
    }

    [Theory]
    [InlineData(HighlightSlideDirection.Forward)]
    [InlineData(HighlightSlideDirection.Back)]
    public void For_DirectionalSteps_EnterIsActive(HighlightSlideDirection direction)
        => Assert.True(HighlightViewerMotion.For(direction).Enter.Active);

    [Fact]
    public void For_None_EnterIsNotActive() => Assert.False(HighlightViewerMotion.For(HighlightSlideDirection.None).Enter.Active);

    [Fact]
    public void For_MapsEachDirectionToItsRecipe()
    {
        Assert.Equal(HighlightViewerMotion.SlideForward, HighlightViewerMotion.For(HighlightSlideDirection.Forward));
        Assert.Equal(HighlightViewerMotion.SlideBack, HighlightViewerMotion.For(HighlightSlideDirection.Back));
        Assert.Equal(HighlightViewerMotion.ExitOnly, HighlightViewerMotion.For(HighlightSlideDirection.None));
    }
}

// ── IssueStateBudget / IssueStateCache ──────────────────────────────────────────────────────────────────────────────
// Wavee ships no token: 60 unauthenticated REST requests an hour for the whole app.
public class IssueStateBudgetTests
{
    const string Repo = "christosk92/WaveeMusic";
    const long Now = 1_700_000_000_000;

    static IssueStateCache Cache(params (int Number, long FetchedAtMs)[] entries)
    {
        var cache = new IssueStateCache();
        foreach (var (number, fetched) in entries)
            cache.Set(IssueStateCache.Key(Repo, number), new IssueState { State = "closed", StateReason = "completed", Title = "t", FetchedAtMs = fetched });
        return cache;
    }

    static string[] Keys(params int[] numbers) => numbers.Select(n => IssueStateCache.Key(Repo, n)).ToArray();

    [Fact]
    public void TheKeyIsRepoHashNumber() => Assert.Equal("christosk92/WaveeMusic#412", IssueStateCache.Key(Repo, 412));

    [Fact]
    public void AnEmptyCache_PlansEverything_InInputOrder()
        => Assert.Equal(Keys(3, 1, 2), new IssueStateBudget().Plan(Keys(3, 1, 2), new IssueStateCache(), Now));

    [Fact]
    public void DuplicatesCollapse() => Assert.Equal(Keys(7, 8), new IssueStateBudget().Plan(Keys(7, 7, 8, 7), new IssueStateCache(), Now));

    [Fact]
    public void FreshEntriesAreNotRefetched_StaleOnesAre()
    {
        var cache = Cache((1, Now - 1_000), (2, Now - IssueStateBudget.OneDayMs - 1));
        Assert.Equal(Keys(2, 3), new IssueStateBudget().Plan(Keys(1, 2, 3), cache, Now));
    }

    [Fact]
    public void TheTtlBoundary_IsExclusive()
    {
        var budget = new IssueStateBudget(ttlMs: 1000);
        Assert.Equal(Keys(1), budget.Plan(Keys(1), Cache((1, Now - 1000)), Now));   // exactly at the TTL → stale
        Assert.Empty(budget.Plan(Keys(1), Cache((1, Now - 999)), Now));
    }

    [Fact]
    public void AFutureTimestamp_CountsAsFresh_NotAsAStorm()
        => Assert.Empty(new IssueStateBudget().Plan(Keys(1), Cache((1, Now + 60_000)), Now));

    [Fact]
    public void ThePlanIsCapped()
    {
        var many = Enumerable.Range(1, 100).Select(n => IssueStateCache.Key(Repo, n)).ToArray();
        Assert.Equal(20, new IssueStateBudget().Plan(many, new IssueStateCache(), Now).Length);
        Assert.Equal(3, new IssueStateBudget(maxPerOpen: 3).Plan(many, new IssueStateCache(), Now).Length);
        Assert.Empty(new IssueStateBudget(maxPerOpen: 0).Plan(many, new IssueStateCache(), Now));
    }

    [Fact]
    public void EmptyAndNullInputs_AreEmptyPlans()
    {
        var budget = new IssueStateBudget();
        Assert.Empty(budget.Plan([], new IssueStateCache(), Now));
        Assert.Empty(budget.Plan(null!, new IssueStateCache(), Now));
        Assert.Empty(budget.Plan(["", null!], new IssueStateCache(), Now));
    }

    [Theory]
    [InlineData(403, null)]
    [InlineData(429, null)]
    [InlineData(200, "0")]
    [InlineData(304, "0")]
    [InlineData(200, " 0 ")]
    public void StopConditions(int status, string? remaining) => Assert.True(new IssueStateBudget().ShouldStop(status, remaining));

    [Theory]
    [InlineData(200, "59")]
    [InlineData(200, null)]
    [InlineData(200, "")]
    [InlineData(404, "42")]
    [InlineData(500, "42")]
    [InlineData(200, "nonsense")]
    public void NonStopConditions(int status, string? remaining) => Assert.False(new IssueStateBudget().ShouldStop(status, remaining));

    [Fact]
    public void Lookup_ResolvesADocumentIssueReference()
    {
        var cache = Cache((412, Now));
        var live = cache.Lookup(new ReleaseIssue { Repo = Repo, Number = 412, State = "open" });
        Assert.NotNull(live);
        Assert.Equal("closed", live!.State);
        Assert.Equal("completed", live.StateReason);
        Assert.Null(cache.Lookup(new ReleaseIssue { Repo = Repo, Number = 999 }));
        Assert.Null(cache.Lookup(""));
    }
}

// ── the page's pure rules (ReleaseNotes.Host.cs PURE region — belongs in ReleaseNotes.cs) ─────────────────────────────
public class ReleaseNotesPageRulesTests
{
    // ── links + date ──

    [Fact]
    public void IssueUrls_DefaultTheRepo_AndSplitIssuesFromPulls()
    {
        Assert.Equal("https://github.com/christosk92/WaveeMusic/issues/412", Links.IssueUrl(null, 412, pr: false));
        Assert.Equal("https://github.com/christosk92/WaveeMusic/pull/430", Links.IssueUrl("", 430, pr: true));
        Assert.Equal("https://github.com/acme/widgets/issues/7", Links.IssueUrl("acme/widgets", 7, pr: false));
    }

    [Fact]
    public void TheReleaseTagUrl_IsTheWaveeTagPage()
    {
        Assert.Equal("https://github.com/christosk92/WaveeMusic/releases/tag/wavee-v0.3.0", Links.ReleaseTagUrl("0.3.0"));
        Assert.Equal("https://github.com/christosk92/WaveeMusic/releases", Links.ReleasesUrl);
    }

    [Fact]
    public void TheStoreId_FallsBackOnlyWhenUnstamped()
    {
        Assert.Equal("9ABC", Links.StoreIdOrFallback("9ABC"));
        Assert.Equal(Links.FallbackStoreId, Links.StoreIdOrFallback(""));
        Assert.Equal(Links.FallbackStoreId, Links.StoreIdOrFallback(null));
    }

    [Fact]
    public void ADate_IsFormatted_AnUnparseableOne_IsEchoed_AndBlankIsEmpty()
    {
        Assert.Equal("29 Aug 2026", Links.Date("2026-08-29", "d MMM yyyy"));
        Assert.Equal("sometime in spring", Links.Date("sometime in spring", "d MMM yyyy"));   // echoed, never blanked (parity 82)
        Assert.Equal("", Links.Date("   ", "d MMM yyyy"));
        Assert.Equal("", Links.Date(null, "d MMM yyyy"));
    }

    // ── the version sentence (0.2.9 AppVersionDisplay.Of, untested until now) ──

    [Fact]
    public void TheVersionSentence_HasThreeShapes_AndADevBuildNamesItsSemVer()
    {
        Assert.Equal(new VersionDisplay(VersionDisplayShape.Codename, "0.2.9", "Breaker", 0), VersionDisplay.Of("0.2.9", "0.2.9", false, "Breaker", null));
        Assert.Equal(new VersionDisplay(VersionDisplayShape.Bare, "0.2.9", "", 0), VersionDisplay.Of("0.2.9", "0.2.9", false, "", null));
        Assert.Equal(new VersionDisplay(VersionDisplayShape.Beta, "0.2.9", "Breaker", 3), VersionDisplay.Of("0.2.9-beta.3", "0.2.9", false, "Breaker", 3));
        Assert.Equal("0.3.0-dev", VersionDisplay.Of("0.3.0-dev", "0.3.0", isDev: true, null, null).Version);
        Assert.Equal(VersionDisplayShape.Bare, VersionDisplay.Of("0.3.0", "0.3.0", false, null, 2).Shape);   // no codename: bare, even on a beta
    }

    // ── the after-update gate (§7's four gates) + §9.4 (a) ──

    [Theory]
    [InlineData("", true, false, false, AfterUpdateVerdict.NothingPending)]
    [InlineData(null, true, false, false, AfterUpdateVerdict.NothingPending)]
    [InlineData("0.2.8.4", false, true, true, AfterUpdateVerdict.AutoShowOff)]          // order: autoShow before the wizard
    [InlineData("0.2.8.4", true, true, true, AfterUpdateVerdict.WizardDeferred)]        // the wizard before the crash notice
    [InlineData("0.2.8.4", true, false, true, AfterUpdateVerdict.CrashNoticeDeferred)]
    [InlineData("0.2.8.4", true, false, false, AfterUpdateVerdict.Open)]
    public void TheGates_AreEvaluatedInOrder(string? pending, bool autoShow, bool wizard, bool crash, AfterUpdateVerdict expected)
        => Assert.Equal(expected, AfterUpdateGate.Decide(pending, autoShow, wizard, crash));

    [Fact]
    public void ALoadThatResolvedToNothing_LeavesTheKeyArmed_AndOpeningConsumesIt()
    {
        var settings = new MemoryAppSettings();
        settings.Set(Platform.Keys.ReleaseNotesPendingFrom, "0.2.8.4");

        Assert.False(AfterUpdateGate.Consume(settings, opening: false));
        Assert.Equal("0.2.8.4", settings.Get(Platform.Keys.ReleaseNotesPendingFrom));   // the next launch tries again

        Assert.True(AfterUpdateGate.Consume(settings, opening: true));
        Assert.Equal("", settings.Get(Platform.Keys.ReleaseNotesPendingFrom));          // one shot, before the plate renders
    }

    // ── unread, latest, the rail ──

    [Fact]
    public void TheAboutDot_IsLitUntilTheRunningNotesAreOpened()
    {
        Assert.True(ReleaseNotes.NotesUnread("", "0.3.0"));
        Assert.True(ReleaseNotes.NotesUnread("0.2.9", "0.3.0"));
        Assert.False(ReleaseNotes.NotesUnread("0.3.0", "0.3.0"));
    }

    [Fact]
    public void Latest_IsTheIndexHead_AndEveryOfflineLoad()
    {
        var index = new ReleaseNotesIndex { Releases = [new() { Version = "0.3.0" }, new() { Version = "0.2.9" }] };
        Assert.True(IsLatest(index, "0.3.0"));
        Assert.False(IsLatest(index, "0.2.9"));
        Assert.True(IsLatest(null, "0.2.9"));                    // parity 81: no index ⇒ [Latest] still shows
        Assert.True(IsLatest(new ReleaseNotesIndex(), "0.2.9"));
    }

    [Theory]
    [InlineData("0.3.0", "0.2.8", "0.3.0", "0.1.0", RailMarker.You)]       // the running build is YOU, never a dot
    [InlineData("0.2.9", "0.2.9", "0.3.0", "0.1.0", RailMarker.None)]      // the SELECTED row is never unread
    [InlineData("0.2.9", "0.3.0", "0.3.0", "0.1.0", RailMarker.Unread)]
    [InlineData("0.2.9", "0.3.0", "0.3.0", "0.2.9", RailMarker.None)]      // already looked at
    public void RailMarkers_AreExclusive_AndSelectionAware(string version, string selected, string running, string lastSeen, RailMarker expected)
        => Assert.Equal(expected, RailMarkerFor(version, selected, running, lastSeen));

    [Fact]
    public void RailRows_KeepOneRowPerVersion_NewestWins_AndSurviveNulls()
    {
        var index = new ReleaseNotesIndex
        {
            Releases = [new() { Version = "0.3.0", PackageVersion = "0.3.0.9" }, null!, new() { Version = "0.3.0", PackageVersion = "0.3.0.8" }, new() { Version = "0.2.9" }],
        };
        var rows = RailRows(index);
        Assert.Equal(new[] { "0.3.0", "0.2.9" }, rows.Select(r => r.Version));
        Assert.Equal("0.3.0.9", rows[0].PackageVersion);
        Assert.Empty(RailRows(null));
    }

    [Fact]
    public void TheRailSubtitle_AndTheDivider_PrintWhatTheEntryCarries()
    {
        Assert.Equal("Breaker  ·  11 Sep 2026", RailSubtitle("Breaker", "11 Sep 2026"));
        Assert.Equal("Breaker", RailSubtitle("Breaker", ""));
        Assert.Equal("11 Sep 2026", RailSubtitle("", "11 Sep 2026"));
        Assert.Equal("", RailSubtitle(null, ""));
        Assert.Equal("0.2.8  Crest  ·  29 Aug 2026", DividerLabel("0.2.8", "Crest", "29 Aug 2026"));
        Assert.Equal("0.2.8", DividerLabel("0.2.8", "", ""));
    }

    // ── notices, pills, sections, chips, avatars, the fold ──

    [Theory]
    [InlineData("breaking", true)]
    [InlineData("WARNING", true)]
    [InlineData("info", false)]
    [InlineData(null, false)]
    public void Notices_BreakingAndWarningAreWarnings(string? kind, bool warning) => Assert.Equal(warning, NoticeIsWarning(kind));

    [Theory]
    [InlineData("rebuilt", false, "whatsNew.kind.rebuilt")]
    [InlineData("improved", false, "whatsNew.kind.improved")]
    [InlineData("new", false, "whatsNew.kind.new")]
    [InlineData("some-future-kind", false, "whatsNew.kind.new")]   // parity 73: NOT title-cased
    [InlineData(null, false, "whatsNew.kind.new")]
    [InlineData("rebuilt", true, "whatsNew.kind.store")]
    public void KindPills_MapEverythingUnknownToNew(string? kind, bool store, string key) => Assert.Equal(key, KindPillLocKey(kind, store));

    [Fact]
    public void SectionTitles_FallBackToKnownLimitations()
    {
        Assert.Equal("whatsNew.section.fixed", SectionTitleLocKey("fixed"));
        Assert.Equal("whatsNew.section.known", SectionTitleLocKey("something-else"));
    }

    [Fact]
    public void AChip_ShowsTheLiveState_ElseTheSnapshot_NeverNothing()
    {
        var snapshot = new ReleaseIssue { State = "closed", StateReason = "not_planned", Title = "Snapshot title" };
        Assert.Equal(ChipState.NotPlanned, IssueChipState(null, snapshot));
        Assert.Equal(ChipState.Open, IssueChipState(new IssueState { State = "open" }, snapshot));
        // A live state with no reason falls back to the snapshot's reason, half by half (0.2.9's `??` pair).
        Assert.Equal(ChipState.NotPlanned, IssueChipState(new IssueState { State = "closed", StateReason = null }, snapshot));
        Assert.Equal(ChipState.Closed, IssueChipState(new IssueState { State = "closed", StateReason = "completed" }, snapshot));

        Assert.Equal("Live title", IssueChipTip(new IssueState { Title = "Live title" }, snapshot));
        Assert.Equal("Snapshot title", IssueChipTip(new IssueState { Title = "" }, snapshot));
        Assert.Equal("", IssueChipTip(null, new ReleaseIssue()));   // no title anywhere ⇒ no tooltip
    }

    [Fact]
    public void Avatars_AreDeterministic_AndInitialsTakeTheLetterAfterTheFirstSeparator()
    {
        Assert.Equal(AvatarTintIndex("christosk92"), AvatarTintIndex("christosk92"));
        Assert.InRange(AvatarTintIndex("christosk92"), 0, 5);
        Assert.Equal(0, AvatarTintIndex(""));
        Assert.Equal("C", Initials("christosk92"));
        Assert.Equal("JD", Initials("jane-doe"));
        Assert.Equal("AB", Initials("a_bee"));
        Assert.Equal("X", Initials("x-"));                         // a trailing separator has no second letter
        Assert.Equal("?", Initials(""));
    }

    [Theory]
    [InlineData(14, false, 8)]
    [InlineData(14, true, 14)]
    [InlineData(8, false, 8)]
    [InlineData(3, false, 3)]
    public void ASection_FoldsAtEight_UntilShowAll(int count, bool expanded, int shown) => Assert.Equal(shown, ShownRows(count, expanded));

    // ── the highlight merge (the loader half) ──

    static ReleaseHighlight H(string id, string kind = "new") => new() { Id = id, Title = id, Kind = kind };

    static ReleaseEntry Entry(string version, params ReleaseHighlight[] highlights)
        => new(new ReleaseNotesDocument { Version = version, Highlights = highlights }, null, false, true);

    [Fact]
    public void Highlights_MergeAcrossTheStack_NewestFirst_CappedAtThreeVisible()
    {
        var entries = new[] { Entry("0.3.0", H("store", "store"), H("a")), Entry("0.2.9", H("b"), H("c"), H("d")) };

        Assert.Equal(new[] { "a", "b", "c" }, MergeHighlights(entries, isStoreInstall: true, store: null).Select(i => i.Highlight.Id));
        Assert.Equal(new[] { "store", "a", "b" }, MergeHighlights(entries, isStoreInstall: false, store: null).Select(i => i.Highlight.Id));
        Assert.All(MergeHighlights(entries, false, null), i => Assert.Null(i.Poster));   // no store ⇒ no poster, never a throw
    }

    [Fact]
    public void ASlideId_IsVersionPlusId_WithTheIndexAsTheLastResort()
    {
        var doc = new ReleaseNotesDocument { Version = "0.3.0" };
        Assert.Equal("0.3.0:logs", SlideId(new HighlightItem(new ReleaseHighlight { Id = "logs" }, doc, null), 2));
        Assert.Equal("0.3.0:2", SlideId(new HighlightItem(new ReleaseHighlight { Id = "" }, doc, null), 2));
    }
}
