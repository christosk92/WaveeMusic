using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Wavee.Backend.Wiring;
using Xunit;

namespace Wavee.Tests.Wiring;

// ── The go-live seam roster (hydration-facade-design.md §2.6) ────────────────────────────────────────────────────────
// `Services.LiveSeams` IS `LiveSeams.All`; the roster lives under Backend/ precisely so it can be pinned here (Services.cs
// drags the whole engine in and is not source-included by this project). What these guard is the roster's own integrity —
// a duplicated or blank name makes AssertCovers quietly weaker, and a const that is not in `All` is a seam nothing checks.
// The install SITES are checked at run time by `wiring.AssertCovers(Services.LiveSeams)` at the end of go-live.
public class ServicesLiveSeamsTests
{
    [Fact]
    public void Roster_HasNoDuplicates()
    {
        var dupes = LiveSeams.All.GroupBy(n => n, StringComparer.Ordinal)
                                 .Where(g => g.Count() > 1).Select(g => g.Key).ToArray();
        Assert.Empty(dupes);
    }

    [Fact]
    public void Roster_HasNoBlankNames()
        => Assert.DoesNotContain(LiveSeams.All, string.IsNullOrWhiteSpace);

    [Fact]
    public void Roster_IsNotEmpty_AndCoversTheSeamsGoOfflineUsedToResetByHand()
    {
        Assert.NotEmpty(LiveSeams.All);
        // The five Services.GoLive facades plus the four seams metadata-entry-points-inventory.md §8.2 #18 found had NO
        // teardown at all. If any of these leaves the roster, the drift this whole mechanism exists to stop is back.
        foreach (var required in new[]
                 {
                     LiveSeams.Player, LiveSeams.Devices, LiveSeams.Session, LiveSeams.Connectivity, LiveSeams.Lyrics,
                     LiveSeams.AlbumEnrichment, LiveSeams.PlaybackResolveVideoSource,
                     LiveSeams.PlaybackRepublishConnectState, LiveSeams.CoverColorFiller,
                     // …plus the runtime-status feed, which was a bare `audio.Status.Changed +=` with no seam name and
                     // no inverse: the handler died with the stack but the STATUS stayed on the bridge, so a logout
                     // left the "set up playback runtime" banner offering to provision for a dead session.
                     LiveSeams.PlaybackRuntimeStatus,
                 })
            Assert.Contains(required, LiveSeams.All);
    }

    // P4-C: the user-profile SERVICE is gone (an Owner is a store entity written by UserHydration), so its seam must be
    // gone from the roster too — a name left behind would make AssertCovers demand an install that no longer exists and
    // fail every go-live.
    [Fact]
    public void UserProfilesSeam_IsGone()
        => Assert.DoesNotContain("UserProfiles", LiveSeams.All);

    // Every public const on LiveSeams must BE in All — a name declared but left off the roster is a seam AssertCovers
    // can never notice going missing, which is the silent half of the original bug. (Reflection is fine here: the test
    // assembly is JIT, only the app is NativeAOT.)
    [Fact]
    public void EveryDeclaredSeamName_IsOnTheRoster()
    {
        var declared = typeof(LiveSeams)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToArray();

        Assert.NotEmpty(declared);
        var roster = new HashSet<string>(LiveSeams.All, StringComparer.Ordinal);
        var orphans = declared.Where(n => !roster.Contains(n)).ToArray();
        Assert.Empty(orphans);
        Assert.Equal(declared.Length, LiveSeams.All.Length);
    }
}
