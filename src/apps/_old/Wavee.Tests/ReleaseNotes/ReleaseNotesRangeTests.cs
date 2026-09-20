using System.Linq;
using Wavee.Core;
using Wavee.Core.ReleaseNotes;
using Xunit;

namespace Wavee.Tests;

// "Since you last looked" — which releases stack on the What's new page. Getting this wrong is silently bad in both
// directions: too many and a returning user reads notes they already read, too few and a release disappears forever
// (nothing else ever shows it again).
public class ReleaseNotesRangeTests
{
    static ReleaseNotesIndex Index(params ReleaseNotesIndexEntry[] entries)
        => new() { Releases = entries };

    static ReleaseNotesIndexEntry E(string version, string quad, string channel = "stable")
        => new() { Version = version, PackageVersion = quad, Name = "N" + version, Date = "2026-01-01", Channel = channel };

    static readonly ReleaseNotesIndex Stable = Index(
        E("0.4.0", "0.4.0.30"),
        E("0.3.0", "0.3.0.22"),
        E("0.2.1", "0.2.1.18"),
        E("0.2.0", "0.2.0.17"),
        E("0.1.0", "0.1.0.4"));

    static string[] Versions(ReleaseNotesIndexEntry[] entries) => entries.Select(e => e.Version).ToArray();

    [Fact]
    public void TheRangeIsHalfOpenBelow_AndClosedAbove()
    {
        var got = ReleaseNotesRange.Between("0.2.0", "0.4.0", Stable, "stable");
        Assert.Equal(new[] { "0.4.0", "0.3.0", "0.2.1" }, Versions(got));   // 0.2.0 was read; 0.4.0 is the one being shown
    }

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
    {
        var got = ReleaseNotesRange.Between(lastSeen, "0.3.0", Stable, "stable");
        Assert.Equal(new[] { "0.3.0" }, Versions(got));
    }

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
    {
        Assert.Equal(new[] { "0.3.0" }, Versions(ReleaseNotesRange.Between("0.3.0", "0.3.0", Stable, "stable")));
    }

    // ── the ordering bug this range was made unreachable by ──────────────────────────────────────────
    // The page marks the notes SEEN on open (ReleaseNotesLastSeen := the running version). For a while it did that
    // BEFORE its loader read the setting back, so Between() was always handed (running, running) — the pair the first
    // assertion below describes. The "since you last looked" banner and every unread dot were then unreachable by
    // construction, and nothing failed: the page rendered perfectly, just always as a single release. lastSeen is read
    // once, ahead of the load, and carried in.

    [Fact]
    public void AdvancingLastSeenBeforeReadingIt_CollapsesTheWholeStack()
    {
        var stacked = ReleaseNotesRange.Between("0.1.0", "0.4.0", Stable, "stable");
        var collapsed = ReleaseNotesRange.Between("0.4.0", "0.4.0", Stable, "stable");

        Assert.Equal(new[] { "0.4.0", "0.3.0", "0.2.1", "0.2.0" }, Versions(stacked));
        Assert.Equal(new[] { "0.4.0" }, Versions(collapsed));
    }

    [Fact]
    public void TheUnreadRule_IsTheSameComparison_AndGoesQuietOnceLastSeenCatchesUp()
    {
        // The rail's dot: newer than lastSeen. Same lastSeen value, same answer as the stack above — which is why the
        // page has to hand BOTH of them the value it read before advancing the setting.
        Assert.True(AppUpdateVersion.IsNewer("0.4.0", "0.1.0"));
        Assert.True(AppUpdateVersion.IsNewer("0.2.1", "0.1.0"));
        Assert.False(AppUpdateVersion.IsNewer("0.4.0", "0.4.0"));
        Assert.False(AppUpdateVersion.IsNewer("0.2.1", "0.4.0"));
    }

    [Fact]
    public void AnUpdatedQuad_IsAcceptedAsTheLastSeenKey()
    {
        // The updater records app.lastRunVersion as the MSIX quad; the page hands that straight in.
        Assert.Equal(new[] { "0.3.0", "0.2.1" }, Versions(ReleaseNotesRange.Between("0.2.0.17", "0.3.0", Stable, "stable")));
        Assert.Equal(new[] { "0.3.0" }, Versions(ReleaseNotesRange.Between("0.2.1.18", "0.3.0", Stable, "stable")));
    }

    [Fact]
    public void TheCurrentReleaseCanBeNamedByItsQuad()
    {
        Assert.Equal(new[] { "0.3.0", "0.2.1" }, Versions(ReleaseNotesRange.Between("0.2.0", "0.3.0.22", Stable, "stable")));
    }

    // ── channels ────────────────────────────────────────────────────────────────────────────────────────────────────

    static readonly ReleaseNotesIndex Mixed = Index(
        E("0.4.0", "0.4.0.30"),
        E("0.4.0-beta.2", "0.4.0.29", "beta"),
        E("0.4.0-beta.1", "0.4.0.28", "beta"),
        E("0.3.0", "0.3.0.22"));

    [Fact]
    public void StableChannel_SkipsPrereleases()
        => Assert.Equal(new[] { "0.4.0" }, Versions(ReleaseNotesRange.Between("0.3.0", "0.4.0", Mixed, "stable")));

    [Fact]
    public void BetaChannel_StacksPrereleases_BelowTheirRelease()
        => Assert.Equal(new[] { "0.4.0", "0.4.0-beta.2", "0.4.0-beta.1" },
                        Versions(ReleaseNotesRange.Between("0.3.0", "0.4.0", Mixed, "beta")));

    [Fact]
    public void APrereleaseCanBeTheCurrentRelease_OnEitherChannel()
    {
        Assert.Equal(new[] { "0.4.0-beta.2", "0.4.0-beta.1" },
                     Versions(ReleaseNotesRange.Between("0.3.0", "0.4.0-beta.2", Mixed, "beta")));
        Assert.Equal(new[] { "0.4.0-beta.2" },
                     Versions(ReleaseNotesRange.Between("0.3.0", "0.4.0-beta.2", Mixed, "stable")));
    }
}
