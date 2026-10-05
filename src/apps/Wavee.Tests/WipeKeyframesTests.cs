using Wavee;
using Xunit;

namespace Wavee.Tests;

/// <summary>The render-thread karaoke wipe (Lyrics.Wipe.SplitKeyframes): the keyframes the view seeds must BE the split the
/// UI used to write every frame — ComputeSplit, plus the lead while 0 &lt; split &lt; 1 — at every instant of the line; and
/// the halo lanes (GlowMoving / NextGlowEdgeMs) must say exactly when the UI still has per-frame work.</summary>
public class WipeKeyframesTests
{
    // "city " short, "lights " short, "falling" HELD (800 ms ≥ HeldGlowMinMs)
    static readonly Lyrics.Line Line = Lyr.Line(1000, "city lights falling",
        Lyr.S(1000, 1400, "city "), Lyr.S(1500, 2000, "lights "), Lyr.S(2100, 2900, "falling"));

    static float Expected(long t, bool lead)
    {
        float v = Lyrics.Wipe.ComputeSplit(Line, t);
        return lead && v > 0f && v < 1f ? Math.Clamp(v + Lyrics.Wipe.LeadFrac, 0f, 1f) : v;
    }

    static float Sample(FluentGpu.Animation.Keyframe[] keys, long now, long end, long t)
    {
        float p = (float)(t - now) / (end - now);
        if (p <= keys[0].Offset) return keys[0].Value;
        for (int i = 1; i < keys.Length; i++)
        {
            if (p > keys[i].Offset) continue;
            float span = keys[i].Offset - keys[i - 1].Offset;
            float f = span <= 0f ? 1f : (p - keys[i - 1].Offset) / span;
            return keys[i - 1].Value + (keys[i].Value - keys[i - 1].Value) * f;
        }
        return keys[^1].Value;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_keyframes_reproduce_the_split_at_every_instant(bool lead)
    {
        const long now = 500;
        var keys = Lyrics.Wipe.SplitKeyframes(Line, now, lead, out long end);
        Assert.NotNull(keys);
        Assert.Equal(2900, end);
        Assert.Equal(1f, keys![^1].Value);
        for (long t = now; t <= end; t += 7)
        {
            if (lead && t > 1000 && t <= 1001) continue;   // the lead's 1 ms step out of 0
            Assert.True(MathF.Abs(Sample(keys, now, end, t) - Expected(t, lead)) < 1e-4f, $"t={t}: {Sample(keys, now, end, t)} vs {Expected(t, lead)}");
        }
    }

    [Fact]
    public void A_mid_line_seed_starts_at_the_split_of_now_and_a_settled_line_has_nothing_left()
    {
        var keys = Lyrics.Wipe.SplitKeyframes(Line, 1700, lead: true, out long end);
        Assert.NotNull(keys);
        Assert.Equal(0f, keys![0].Offset);
        Assert.True(MathF.Abs(keys[0].Value - Expected(1700, lead: true)) < 1e-6f);
        Assert.Null(Lyrics.Wipe.SplitKeyframes(Line, 2900, lead: true, out _));
        Assert.Null(Lyrics.Wipe.SplitKeyframes(Line, 4000, lead: true, out _));
    }

    [Fact]
    public void The_halo_moves_only_while_a_held_syllable_ramps_in_or_melts_out()
    {
        const long lineEnd = 2900;
        Assert.False(Lyrics.Wipe.GlowMoving(Line, 1200, lineEnd));   // a short syllable never glows
        Assert.False(Lyrics.Wipe.GlowMoving(Line, 2050, lineEnd));   // between syllables
        Assert.True(Lyrics.Wipe.GlowMoving(Line, 2200, lineEnd));    // ramp-in (min(500, 800/2) = 400 ms)
        Assert.False(Lyrics.Wipe.GlowMoving(Line, 2550, lineEnd));   // plateau
        Assert.True(Lyrics.Wipe.GlowMoving(Line, 2700, lineEnd));    // melt-out (last 320 ms)
        Assert.Equal(2100, Lyrics.Wipe.NextGlowEdgeMs(Line, 1200, lineEnd));
        Assert.Equal(2580, Lyrics.Wipe.NextGlowEdgeMs(Line, 2550, lineEnd));
        Assert.Equal(Lyrics.MotionDemand.None, Lyrics.Wipe.NextGlowEdgeMs(Line, 2900, lineEnd));
    }
}
