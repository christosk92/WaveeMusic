// ── Wavee.Tests/PlaylistHeaderRulesTests.cs — the playlist header's two pure rules ───────────────────────────────────
//
// NEW in 0.3, both written against reported bugs:
//
//  1. `Playlist.PageRules.MetaArmFor` — the meta line ("N songs · 2 hr 59 min") used to shimmer forever on an
//     editorial playlist. `Detail.MetaRow` paints a skeleton region whose Pending is hard-wired true and whose Failed
//     is hard-wired false, so ONLY the page can end that state — and the page read the membership edge's raw `State`,
//     which never answers Failed. A list whose membership ask failed, or was answered with nothing, therefore stayed
//     "loading" for the life of the page. The count cannot be demanded instead: `PlaylistFields.TrackCount` is named
//     by no route in `FetchRoutes` (only the length-bearing membership decoder fills it), so the loading state IS the
//     membership's and must read the membership's failure. Three arms, no fourth.
//
//  2. `OptimisticSwitch` — the "Collaborative playlist" toggle flipped back on its own. The settle's membership
//     re-read re-decodes the list attributes at Authority.Full and can land the PRE-write flag (it raced the write on
//     the server side). The switch holds the user's intent until an authoritative answer agrees with it, and reverts
//     exactly ONCE, on a refusal.
//
// Plus the eyebrow the same header reads for a playlist's privacy/collaborative state — the string that changed under
// the open flyout. Every expected string is built from the same `Loc.Get(Strings.*)` the rule makes, never a literal.

using FluentGpu.Localization;
using Xunit;
using Arm = Wavee.Playlist.MetaArm;
using Rules = Wavee.Playlist.PageRules;
using Text = Wavee.Detail.Text;

namespace Wavee.Tests;

public class PlaylistHeaderRulesTests
{
    // ── 1. the meta line's arm ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The open's revalidation hold wins over everything: the count and the duration ARE the list's, so they
    /// wait with it (and that hold carries its own budget, so it always ends).</summary>
    [Theory]
    [InlineData(EdgeState.Unknown, false, 0)]
    [InlineData(EdgeState.Complete, true, 40)]
    [InlineData(EdgeState.Failed, true, 40)]
    public void MetaArm_AHeldList_Shimmers(EdgeState readiness, bool countKnown, int rows)
        => Assert.Equal(Arm.Loading, Rules.MetaArmFor(holding: true, readiness, countKnown, rows));

    /// <summary>Nobody has answered the membership YET and the page has no count of its own: the ask is out (the
    /// page's own `ListOpen.Open`) and its answer — landing, failing or answering with nothing — moves the
    /// readiness off Unknown. THIS is the only shimmer a playlist header may show.</summary>
    [Fact]
    public void MetaArm_UnknownWithNothingToState_Shimmers()
        => Assert.Equal(Arm.Loading, Rules.MetaArmFor(holding: false, EdgeState.Unknown, countKnown: false, residentRows: 0));

    /// <summary>THE BUG. The membership ask failed, or was answered with nothing (`MarkUnanswered`'s NoRoute) — which
    /// only `EdgeTableBase.Readiness` ever reports — and no count can be stated. The row LEAVES the hero rather than
    /// shimmer at a fact that will not arrive.</summary>
    [Fact]
    public void MetaArm_FailedWithNothingToState_IsAbsent()
        => Assert.Equal(Arm.Absent, Rules.MetaArmFor(holding: false, EdgeState.Failed, countKnown: false, residentRows: 0));

    /// <summary>A failure the page can talk over: the header's own length is a count, so the line is written from it
    /// (without the duration segment, which needs the rows).</summary>
    [Fact]
    public void MetaArm_FailedButTheCountIsKnown_IsText()
        => Assert.Equal(Arm.Text, Rules.MetaArmFor(holding: false, EdgeState.Failed, countKnown: true, residentRows: 0));

    /// <summary>Any answered membership is a count — INCLUDING a Complete list with no rows: "this playlist has no
    /// tracks" is a real, renderable answer (ch 03 §7), not a pending state.</summary>
    [Theory]
    [InlineData(EdgeState.Complete, 0)]
    [InlineData(EdgeState.Complete, 40)]
    [InlineData(EdgeState.Partial, 100)]
    public void MetaArm_AnAnsweredMembership_IsText(EdgeState readiness, int rows)
        => Assert.Equal(Arm.Text, Rules.MetaArmFor(holding: false, readiness, countKnown: false, rows));

    /// <summary>Resident rows are a count even before the edge says Complete (a page restored from disk mid-read).</summary>
    [Fact]
    public void MetaArm_ResidentRows_AreACount()
        => Assert.Equal(Arm.Text, Rules.MetaArmFor(holding: false, EdgeState.Unknown, countKnown: false, residentRows: 12));

    /// <summary>The header's own count releases the line without the membership at all — the whole point, since
    /// `PlaylistFields.TrackCount` has no route of its own to wait on.</summary>
    [Fact]
    public void MetaArm_TheHeadersCount_ReleasesTheLine()
        => Assert.Equal(Arm.Text, Rules.MetaArmFor(holding: false, EdgeState.Unknown, countKnown: true, residentRows: 0));

    /// <summary>Whatever the facts, the answer is never a shimmer that no further fact can end: an arm that says
    /// Loading is one where an ask is out (Unknown) or a budgeted hold is on.</summary>
    [Theory]
    [InlineData(EdgeState.Unknown)]
    [InlineData(EdgeState.Partial)]
    [InlineData(EdgeState.Complete)]
    [InlineData(EdgeState.Failed)]
    public void MetaArm_LoadingOnlyEverMeansUnknown(EdgeState readiness)
    {
        for (int mask = 0; mask < 4; mask++)
        {
            var arm = Rules.MetaArmFor(holding: false, readiness, countKnown: (mask & 1) != 0, residentRows: (mask & 2) != 0 ? 7 : 0);
            if (arm == Arm.Loading) Assert.Equal(EdgeState.Unknown, readiness);
        }
    }

    // ── 2. the optimistic switch ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Settled: the switch is the model, nothing held, nothing busy.</summary>
    [Fact]
    public void Switch_Settled_IsTheModel()
    {
        Assert.False(OptimisticSwitch.At(false).Shown);
        Assert.True(OptimisticSwitch.At(true).Shown);
        Assert.False(OptimisticSwitch.At(true).Busy);
    }

    /// <summary>The click paints the intent AT ONCE and marks the row busy — before any write has gone out.</summary>
    [Fact]
    public void Switch_Flip_PaintsTheIntentImmediately()
    {
        var s = OptimisticSwitch.At(false).Flip(true);
        Assert.True(s.Shown);
        Assert.True(s.Busy);
    }

    /// <summary>THE BUG. A publication that still carries the pre-write value — the settle's own membership re-read,
    /// a dealer echo of the older head — is a READ that raced the write, not the write's answer. It is recorded and
    /// NOT painted.</summary>
    [Fact]
    public void Switch_AStaleReadUnderAHeldIntent_DoesNotFlipItBack()
    {
        var s = OptimisticSwitch.At(false).Flip(true).Observe(false);
        Assert.True(s.Shown);
        s = s.Answered(ok: true, model: false).Observe(false);
        Assert.True(s.Shown);
    }

    /// <summary>The model catching up SETTLES the hold: from there the switch simply mirrors the model again.</summary>
    [Fact]
    public void Switch_TheModelCatchingUp_SettlesTheHold()
    {
        var s = OptimisticSwitch.At(false).Flip(true).Answered(ok: true, model: false);
        Assert.True(s.Holding);
        s = s.Observe(true);
        Assert.False(s.Holding);
        Assert.True(s.Shown);
        Assert.False(s.Busy);
        Assert.False(s.Observe(false).Shown);      // no hold left: a real later change is honoured at once
    }

    /// <summary>A write the server took while the model already agreed settles immediately.</summary>
    [Fact]
    public void Switch_AnAcceptedWriteTheModelAlreadyShows_Settles()
    {
        var s = OptimisticSwitch.At(false).Flip(true).Answered(ok: true, model: true);
        Assert.False(s.Holding);
        Assert.False(s.Busy);
        Assert.True(s.Shown);
    }

    /// <summary>A refusal reverts ONCE, to the model the edit host has already put back — and stays there: the revert
    /// is a settle, so nothing can bounce off it afterwards.</summary>
    [Fact]
    public void Switch_ARefusal_RevertsExactlyOnce()
    {
        var s = OptimisticSwitch.At(false).Flip(true).Answered(ok: false, model: false);
        Assert.False(s.Shown);
        Assert.False(s.Busy);
        Assert.False(s.Holding);
        Assert.False(s.Observe(false).Shown);
        Assert.True(s.Observe(true).Shown);        // another device turning it on is still honoured
    }

    /// <summary>Nothing held: every publication is the truth (the switch is not a latch, it is a hold).</summary>
    [Fact]
    public void Switch_WithNothingHeld_MirrorsTheModel()
    {
        var s = OptimisticSwitch.At(false);
        Assert.True(s.Observe(true).Shown);
        Assert.False(s.Observe(true).Observe(false).Shown);
    }

    /// <summary>A second flip while a write is out replaces the intent — the user's latest click is the one held.</summary>
    [Fact]
    public void Switch_ASecondFlip_ReplacesTheIntent()
    {
        var s = OptimisticSwitch.At(false).Flip(true).Flip(false);
        Assert.False(s.Shown);
        Assert.True(s.Busy);
        Assert.False(s.Observe(true).Shown);       // the in-between publication of the first write is not the answer
    }

    // ── 3. the eyebrow the same header reads ────────────────────────────────────────────────────────────────────────

    /// <summary>Collaborative wins the slot; then Private, but only once visibility is KNOWN (an editorial playlist,
    /// whose `/permission/base` nobody may read, must not read as "Private"); else the bare kind.</summary>
    [Theory]
    [InlineData(false, true, true, Strings.Nav.Playlist)]
    [InlineData(false, false, true, Strings.Nav.PlaylistPrivate)]
    [InlineData(false, false, false, Strings.Nav.Playlist)]
    [InlineData(true, false, true, Strings.Nav.PlaylistCollaborative)]
    [InlineData(true, true, true, Strings.Nav.PlaylistCollaborative)]
    [InlineData(true, false, false, Strings.Nav.PlaylistCollaborative)]
    public void Eyebrow_OwnerRow_ReadsCollaborativeThenPrivate(bool collaborative, bool isPublic, bool visibilityKnown, string key)
        => Assert.Equal(Loc.Get(key),
            Text.Eyebrow(DetailKind.Playlist, BadgeStyle.OwnerRow, AlbumKind.Album, 0, collaborative, isPublic, visibilityKnown));

    /// <summary>The pile replaces the owner row for a collaborative list with any member, or for two or more members —
    /// the swap the reserved owner-row height exists to absorb.</summary>
    [Theory]
    [InlineData(0, true, false)]
    [InlineData(1, false, false)]
    [InlineData(1, true, true)]
    [InlineData(2, false, true)]
    public void ShowCollaborators_IsTheArmSwap(int count, bool collaborative, bool expected)
        => Assert.Equal(expected, Text.ShowCollaborators(count, collaborative));

    // ── 3. owner demand ─────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void OwnerAsk_UnknownIdentity_IsEnsure()
        => Assert.Equal(Playlist.OwnerAsk.Ensure, Rules.OwnerAskFor(knownIdentity: false, Authority.None));

    [Fact]
    public void OwnerAsk_ThinNamedRow_IsInvalidate()
        => Assert.Equal(Playlist.OwnerAsk.Invalidate, Rules.OwnerAskFor(knownIdentity: true, Authority.Thin));

    [Fact]
    public void OwnerAsk_FullProfile_IsNone()
        => Assert.Equal(Playlist.OwnerAsk.None, Rules.OwnerAskFor(knownIdentity: true, Authority.Full));
}
