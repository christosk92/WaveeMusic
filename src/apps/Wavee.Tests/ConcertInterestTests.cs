// ── Wavee.Tests/ConcertInterestTests.cs — the concert "interested" mark, a local set (contract 1.46) ──────────────────────
// Pure: the set is a string blob.

using Xunit;

namespace Wavee.Tests;

public class ConcertInterestTests
{
    [Fact]
    public void Toggle_marks_newest_first_and_unmarks()
    {
        string blob = ConcertInterest.Toggle("", "spotify:concert:a", out bool on);
        Assert.True(on);
        blob = ConcertInterest.Toggle(blob, "spotify:concert:b", out on);
        Assert.True(on);
        Assert.Equal(["spotify:concert:b", "spotify:concert:a"], ConcertInterest.Parse(blob));
        Assert.True(ConcertInterest.Contains(blob, "spotify:concert:a"));
        blob = ConcertInterest.Toggle(blob, "spotify:concert:a", out on);
        Assert.False(on);
        Assert.False(ConcertInterest.Contains(blob, "spotify:concert:a"));
        Assert.Equal(["spotify:concert:b"], ConcertInterest.Parse(blob));
    }

    [Fact]
    public void The_set_is_capped_and_a_blank_uri_changes_nothing()
    {
        string blob = "";
        for (int i = 0; i < ConcertInterest.Cap + 20; i++) blob = ConcertInterest.Toggle(blob, "spotify:concert:" + i, out _);
        var all = ConcertInterest.Parse(blob);
        Assert.Equal(ConcertInterest.Cap, all.Length);
        Assert.Equal("spotify:concert:" + (ConcertInterest.Cap + 19), all[0]);
        Assert.Equal(blob, ConcertInterest.Toggle(blob, "", out bool on));
        Assert.False(on);
    }
}
