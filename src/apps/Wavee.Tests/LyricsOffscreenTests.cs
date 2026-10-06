// ── Wavee.Tests/LyricsOffscreenTests.cs — rows no pixel of which can show (Lyrics.Offscreen) ────────────────────────
//
// The lyrics surface keeps rows nobody can see out of the depth-of-field blur and the hand-off cascade. These are the
// rules it runs on, as values: the visibility of a row's sweep, the σ its node carries, the geometry that forces a
// re-judgement, and the cascade flush that defers a hidden row's writes and never loses one.

using Wavee;
using Xunit;

namespace Wavee.Tests;

public sealed class LyricsOffscreenTests
{
    const float RowH = 80f, Viewport = 596f, Sigma = 6.5f;
    static readonly float Reach = Lyrics.Offscreen.Reach(Sigma, 3f);

    /// <summary>Rows whose rest tops are given, each <see cref="RowH"/> tall, in a viewport of a given height.</summary>
    struct Rows(float[] tops, float viewportH) : Lyrics.Offscreen.IRowSweep
    {
        public readonly bool MayShow(int index, float comp) => Lyrics.Offscreen.RowMayShow(tops[index], RowH, comp, Reach, viewportH);
    }

    static float Shown(float top, float viewportH, float comp = 0f, bool gate = true)
        => Lyrics.Offscreen.ShownSigma(Sigma, gate, Lyrics.Offscreen.RowMayShow(top, RowH, comp, Reach, viewportH));

    [Fact]
    public void ARowPastTheViewportByMoreThanItsReach_CarriesNoBlur_AndOneWithinIt_Does()
    {
        Assert.Equal(0f, Shown(Viewport + Reach + 1f, Viewport));
        Assert.Equal(Sigma, Shown(Viewport + Reach - 1f, Viewport));
        Assert.Equal(0f, Shown(-RowH - Reach - 1f, Viewport));
        Assert.Equal(Sigma, Shown(-RowH - Reach + 1f, Viewport));
        // the gate closed (a user scroll, a moving viewport): every row carries its σ
        Assert.Equal(Sigma, Shown(5000f, Viewport, gate: false));
        // a focused row (σ 0) is never "hidden"
        Assert.Equal(0f, Lyrics.Offscreen.ShownSigma(0f, true, false));
    }

    [Fact]
    public void AResizeAfterLanding_IsANewGeometry_AndBringsTheRowBack()
    {
        float top = 700f;   // below a 596-DIP viewport, past its reach
        var before = new Lyrics.Offscreen.Geometry(Viewport, 420f, 0.0, 3000f, 0.0, false);
        Assert.Equal(0f, Shown(top, before.ViewportH));
        var after = before with { ViewportH = 900f };
        Assert.NotEqual(before, after);                 // the watcher re-judges
        Assert.Equal(Sigma, Shown(top, after.ViewportH)); // and the row carries its blur again
        Assert.True(Lyrics.Offscreen.ExactSigmaWrite(landed: false, hide: false, wasHidden: true));
        // a width change alone (a re-wrap) and an extent change are new geometries too
        Assert.NotEqual(before, before with { ViewportW = 500f });
        Assert.NotEqual(before, before with { ContentH = 3100f });
    }

    [Fact]
    public void TheRailOpenGrowth_ReJudgesEveryFrame_UntilTheLastRowShows()
    {
        float top = 700f;
        var g = new Lyrics.Offscreen.Geometry(0f, 420f, 0.0, 3000f, 0.0, false);
        bool shownAtEnd = false;
        for (float h = 0f; h <= 900f; h += 100f)
        {
            var next = g with { ViewportH = h };
            Assert.True(h == 0f || next != g);
            g = next;
            shownAtEnd = Shown(top, h) == Sigma;
        }
        Assert.True(shownAtEnd);
        Assert.Equal(0f, Shown(top, 300f));   // mid-growth it was still hidden
    }

    [Fact]
    public void OnTheArmFrame_TheSweepCounts_NotJustTheRestRect()
    {
        // rests below the viewport but is displaced up into it by the compensating translate
        Assert.True(Lyrics.Offscreen.RowMayShow(700f, RowH, -200f, Reach, Viewport));
        // rests above it and travels down through it
        Assert.True(Lyrics.Offscreen.RowMayShow(-300f, RowH, 350f, Reach, Viewport));
        // travels entirely below it
        Assert.False(Lyrics.Offscreen.RowMayShow(800f, RowH, 200f, Reach, Viewport));
    }

    [Fact]
    public void AHiddenRowsInFlightWrite_IsDeferred_AndItsLandingIsWrittenExactly()
    {
        var rows = new Rows([100f, 900f], Viewport);
        byte[] write = [Lyrics.Cascade.WriteMoving, Lyrics.Cascade.WriteMoving];
        float[] comp = [-20f, -20f];
        bool[] stale = new bool[2];
        byte[] outWrite = new byte[2];
        Assert.Equal(1, Lyrics.Offscreen.Flush(write, comp, stale, mayDefer: true, ref rows, outWrite));
        Assert.Equal(Lyrics.Offscreen.FlushMoving, outWrite[0]);
        Assert.Equal(Lyrics.Offscreen.FlushNone, outWrite[1]);
        Assert.True(stale[1]);

        // the next steps write nothing for it while it stays hidden
        write = [Lyrics.Cascade.WriteNone, Lyrics.Cascade.WriteMoving];
        Lyrics.Offscreen.Flush(write, comp, stale, mayDefer: true, ref rows, outWrite);
        Assert.Equal(Lyrics.Offscreen.FlushNone, outWrite[1]);

        // its landing: always written, exactly
        write = [Lyrics.Cascade.WriteNone, Lyrics.Cascade.WriteLanded];
        comp = [0f, 0f];
        Lyrics.Offscreen.Flush(write, comp, stale, mayDefer: true, ref rows, outWrite);
        Assert.Equal(Lyrics.Offscreen.FlushExact, outWrite[1]);
        Assert.False(stale[1]);
    }

    [Fact]
    public void AStaleRowThatCanShowAgain_CatchesUpExactly()
    {
        var hidden = new Rows([900f], Viewport);
        bool[] stale = new bool[1];
        byte[] outWrite = new byte[1];
        Lyrics.Offscreen.Flush([Lyrics.Cascade.WriteMoving], [-30f], stale, true, ref hidden, outWrite);
        Assert.True(stale[0]);
        // the viewport grew: the same row can show now, with no write of its own this step
        var visible = new Rows([900f], 1200f);
        Assert.Equal(1, Lyrics.Offscreen.Flush([Lyrics.Cascade.WriteNone], [-25f], stale, true, ref visible, outWrite));
        Assert.Equal(Lyrics.Offscreen.FlushExact, outWrite[0]);
        Assert.False(stale[0]);
    }

    [Fact]
    public void TheStepTheCascadeComesToRest_WritesEveryDeferredRow()
    {
        var rows = new Rows([900f, 1000f, 100f], Viewport);
        bool[] stale = [true, true, false];
        byte[] outWrite = new byte[3];
        // mayDefer = false: the cascade is not pending after this step
        int n = Lyrics.Offscreen.Flush([Lyrics.Cascade.WriteNone, Lyrics.Cascade.WriteNone, Lyrics.Cascade.WriteNone],
            [0.3f, 0.3f, 0f], stale, mayDefer: false, ref rows, outWrite);
        Assert.Equal(2, n);
        Assert.Equal(Lyrics.Offscreen.FlushExact, outWrite[0]);
        Assert.Equal(Lyrics.Offscreen.FlushExact, outWrite[1]);
        Assert.Equal(Lyrics.Offscreen.FlushNone, outWrite[2]);
        Assert.DoesNotContain(true, stale);
    }

    [Fact]
    public void ARowRealizedMidCascade_StartsClean_AndIsDeferredAgainOnlyWhileHidden()
    {
        // realization seeds the node at the model's translate and clears its stale bit (ReportDofNode)
        bool[] stale = [false];
        byte[] outWrite = new byte[1];
        var hidden = new Rows([900f], Viewport);
        Lyrics.Offscreen.Flush([Lyrics.Cascade.WriteMoving], [-40f], stale, true, ref hidden, outWrite);
        Assert.Equal(Lyrics.Offscreen.FlushNone, outWrite[0]);
        Assert.True(stale[0]);
        var shown = new Rows([500f], Viewport);
        stale[0] = false;   // realized again, now inside the viewport
        Lyrics.Offscreen.Flush([Lyrics.Cascade.WriteMoving], [-35f], stale, true, ref shown, outWrite);
        Assert.Equal(Lyrics.Offscreen.FlushMoving, outWrite[0]);
    }

    [Fact]
    public void AVirtualWindowReAnchor_MovesNoRow()
    {
        // the realized window re-bases by 1200 DIP: the rows' in-window offsets drop by the same amount
        float before = Lyrics.Offscreen.RestTop(1500f, 0.0, 1300.0);
        float after = Lyrics.Offscreen.RestTop(300f, 1200.0, 1300.0);
        Assert.Equal(before, after);
        var g = new Lyrics.Offscreen.Geometry(Viewport, 420f, 0.0, 3000f, 1300.0, false);
        Assert.NotEqual(g, g with { WindowOrigin = 1200.0 });   // still a re-judgement, with the same verdict
    }
}
