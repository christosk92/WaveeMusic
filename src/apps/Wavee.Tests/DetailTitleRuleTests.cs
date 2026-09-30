// ── Wavee.Tests/DetailTitleRuleTests.cs — the detail title's ONE trim rule, the rail's title plan, and the rail width a
// render composes ──────────────────────────────────────────────────────────────────────────────────────────────────────
//
// The owner's report (2026-09-30): "throwback pop 2000s vibes wednesday evening" drew as three lines ending
// "wednesday eveni…" in the narrow rail, with a line box far too tall for its glyphs, and flickered — the full title for a
// frame or two, then the trimmed one. Root causes: a 3-line MaxLines under an engine auto-fit (TextEl.MinSize 18) that
// bottomed out in the 164-DIP cover of the mode-2 rail, an authored 36/52 line box kept around the shrunken glyphs, and
// the pre-measure frames composing the rail at the WIDE arm's width. These facts pin the replacement: one size per
// (measure, title), drawn exactly, wrapping freely, ellipsised only past TitleLineCap — and a rail that composes the width
// it will rest at before the re-resting effect has run.
//
// Engine-free: only the pure `Detail.VerticalLayout` / `Detail.RailPolicy` rules (CLAUDE.md "no source-text tests").

using Xunit;
using RailPolicy = Wavee.Detail.RailPolicy;
using VerticalLayout = Wavee.Detail.VerticalLayout;

namespace Wavee.Tests;

public class DetailTitleRuleTests
{
    const string OwnersTitle = "throwback pop 2000s vibes wednesday evening";

    static readonly string[] NormalTitles =
    [
        "Chill",
        OwnersTitle,
        "Discover Weekly",
        "Random Access Memories",
        "Can This Love Be Translated? (Soundtrack from the Netflix Series)",
        "Here's some throwback pop, 2010s, high-spirited, summer camp, nostalgia, birthday",
        "スローなブギにしてくれ深夜高速道路のミックス",
    ];

    /// <summary>A title that is a paragraph, not a name — the only kind the cap may ellipsise.</summary>
    const string Pathological =
        "This playlist name is not a name at all but a whole paragraph that someone pasted into the title field, " +
        "running on sentence after sentence about the songs inside it, the summer they were collected in, the friends " +
        "who suggested them, and the long drive home that none of the passengers wanted to end, until it simply stops.";

    static float Cover(float railW) => MathF.Max(80f, railW - 24f);   // RailCoverEdge: the rail less its 16 + 8 side pads

    // ── the rail ─────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Every rail width a grip can reach (180-480) at both window rungs: a normal title never reaches the cap —
    /// so the engine never ellipsises it — and its size stays between the floor and the rung.</summary>
    [Fact]
    public void Rail_NormalTitle_IsNeverTrimmed_AtAnyRailWidth()
    {
        foreach (float rung in new[] { 28f, 40f })
            for (float railW = RailPolicy.MinWidth; railW <= RailPolicy.MaxWidth; railW += 1f)
                foreach (string title in NormalTitles)
                {
                    var plan = VerticalLayout.RailTitleTypeFor(Cover(railW), rung, title);
                    Assert.True(plan.Lines < VerticalLayout.TitleLineCap,
                        $"'{title}' reaches the trim cap ({plan.Lines} lines at {plan.Size}) in a {railW}-DIP rail at rung {rung}");
                    Assert.InRange(plan.Size, VerticalLayout.TitleSizeFloor, rung);
                }
    }

    /// <summary>The owner's exact case: the mode-2 rail at rest (188 → a 164-DIP cover), the short-window rung (28). The
    /// title keeps every word (no cap reached) in the natural line box of the size it is drawn at — never the 36-DIP box
    /// of the 28 rung around smaller glyphs.</summary>
    [Fact]
    public void Rail_OwnersTitle_AtTheNarrowRestRail_WrapsInItsOwnLineBox()
    {
        float cover = Cover(RailPolicy.NarrowRestWidth);
        Assert.Equal(164f, cover);
        var plan = VerticalLayout.RailTitleTypeFor(cover, 28f, OwnersTitle);
        Assert.True(plan.Lines >= 3 && plan.Lines < VerticalLayout.TitleLineCap, $"{plan.Lines} lines");
        Assert.Equal(VerticalLayout.NaturalLineHeightFor(plan.Size), plan.LineHeight);
        Assert.True(plan.LineHeight < 36f, $"line box {plan.LineHeight} is the 28 rung's, not the drawn {plan.Size}'s");
        Assert.Equal(VerticalLayout.TitleLinesAt(plan.Size, cover, VerticalLayout.TitleAdvanceEm(OwnersTitle)), plan.Lines);
    }

    /// <summary>A short title takes the rung itself, in one line; the rung is a CAP, never exceeded.</summary>
    [Theory]
    [InlineData(28f)]
    [InlineData(40f)]
    public void Rail_ShortTitle_TakesTheRung(float rung)
    {
        var plan = VerticalLayout.RailTitleTypeFor(Cover(240f), rung, "Chill");
        Assert.Equal(rung, plan.Size);
        Assert.Equal(1, plan.Lines);
        Assert.Equal(VerticalLayout.NaturalLineHeightFor(rung), plan.LineHeight);
    }

    /// <summary>The rail keeps a title in its three preferred lines by stepping the size down; a size above the floor
    /// never needs more lines than that budget.</summary>
    [Fact]
    public void Rail_SolvedPlan_HoldsItsPreferredLines()
    {
        foreach (float rung in new[] { 28f, 40f })
            for (float railW = RailPolicy.MinWidth; railW <= RailPolicy.MaxWidth; railW += 4f)
                foreach (string title in NormalTitles)
                {
                    var plan = VerticalLayout.RailTitleTypeFor(Cover(railW), rung, title);
                    if (plan.Size > VerticalLayout.TitleSizeFloor)
                        Assert.True(plan.Lines <= VerticalLayout.RailTitleLinesPreferred,
                            $"'{title}' at {plan.Size} needs {plan.Lines} lines in a {railW} rail");
                }
    }

    // ── the hero ─────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The vertical hero, every width, both flows, with a playlist's tallest chrome: a normal title never
    /// reaches the cap. At a phone-width stacked hero the owner's title WRAPS to several lines at the floor — the old plan
    /// claimed one line there and the engine trimmed the rest.</summary>
    [Fact]
    public void Hero_NormalTitle_IsNeverTrimmed_AtAnyWidth()
    {
        for (float w = 240f; w <= 1400f; w += 1f)
            foreach (bool row in new[] { false, true })
                foreach (string title in NormalTitles)
                {
                    var plan = VerticalLayout.TitleTypeFor(VerticalLayout.BucketW(w), row, title,
                        eyebrow: true, attribution: true, meta: true, pulse: true);
                    Assert.True(plan.Lines < VerticalLayout.TitleLineCap,
                        $"'{title}' reaches the trim cap ({plan.Lines} lines at {plan.Size}) at w={w} row={row}");
                }

        var phone = VerticalLayout.TitleTypeFor(240f, rowFlow: false, OwnersTitle, eyebrow: true, attribution: true, meta: true);
        Assert.Equal(VerticalLayout.TitleSizeFloor, phone.Size);
        Assert.True(phone.Lines >= 2, $"the phone-width plan claims {phone.Lines} line(s) for a title that cannot fit one");
    }

    /// <summary>Only a pathological, paragraph-length title reaches the cap — the one place the engine ellipsises.</summary>
    [Fact]
    public void PathologicalTitle_ReachesTheCap_AtTheNarrowestMeasure()
    {
        var rail = VerticalLayout.RailTitleTypeFor(Cover(RailPolicy.MinWidth), 28f, Pathological);
        Assert.Equal(VerticalLayout.TitleSizeFloor, rail.Size);
        Assert.Equal(VerticalLayout.TitleLineCap, rail.Lines);
        Assert.Equal(VerticalLayout.TitleLineCap, VerticalLayout.TitleLinesAt(20f, 156f, VerticalLayout.TitleAdvanceEm(Pathological)));
    }

    /// <summary>Over the size GRID (the only sizes a plan draws) a bigger size never spends fewer lines.</summary>
    [Fact]
    public void TitleLinesAt_IsOneForAnEmptyRun_AndMonotoneOverTheGrid()
    {
        Assert.Equal(1, VerticalLayout.TitleLinesAt(96f, 200f, 0f));
        float adv = VerticalLayout.TitleAdvanceEm(OwnersTitle);
        int prev = 1;
        for (float s = VerticalLayout.TitleSizeFloor; s <= VerticalLayout.TitleSizeCap; s += VerticalLayout.TitleSnapStep(s))
        {
            int lines = VerticalLayout.TitleLinesAt(s, 300f, adv);
            Assert.True(lines >= prev, $"lines fell from {prev} to {lines} at size {s}");
            Assert.InRange(lines, 1, VerticalLayout.TitleLineCap);
            prev = lines;
        }
    }

    /// <summary>D49 with a KNOWN title: the pre-measure / skeleton band is built from exactly the plan the loaded hero
    /// draws at that width (so the swap does not jump); without one it stays the pessimistic null-title band.</summary>
    [Fact]
    public void HeroBand_WithAKnownTitle_IsTheLoadedHerosOwnPlan()
    {
        for (float w = 240f; w <= 1400f; w += 8f)
            foreach (bool row in new[] { false, true })
            {
                var plan = VerticalLayout.TitleTypeFor(w, row, OwnersTitle, true, true, true, pulse: true);
                Assert.Equal(VerticalLayout.HeroBandHeight(w, row, plan, true, true, true, description: true, pulse: true),
                             VerticalLayout.HeroBandHeight(w, row, true, true, true, description: true, pulse: true,
                                                           title: OwnersTitle));
                var pessimistic = VerticalLayout.TitleTypeFor(w, row, title: null, true, true, true, pulse: true);
                Assert.Equal(VerticalLayout.HeroBandHeight(w, row, pessimistic, true, true, true, description: true, pulse: true),
                             VerticalLayout.HeroBandHeight(w, row, true, true, true, description: true, pulse: true));
                Assert.Equal(VerticalLayout.HeroBandHeight(w, row, true, true, true, description: true, pulse: true),
                             VerticalLayout.HeroBandHeight(w, row, true, true, true, description: true, pulse: true, title: ""));
            }
    }

    // ── the rail width a render composes (the flicker) ───────────────────────────────────────────────────────────────

    /// <summary>The frames the owner saw: the pre-measure render (the cell still rested for the wide arm, the seeded
    /// narrow mode, the viewport estimate's cap), the first measured render (the real cap, the effect not yet run) and
    /// the render after the rest — one width throughout, instead of the wide arm's 240 for two frames and then 188.</summary>
    [Fact]
    public void ComposedWidth_DoesNotMoveAcrossTheFirstFrames()
    {
        const RailScope scope = RailScope.Playlist;
        float stored = RailPolicy.DefaultWidthFor(scope);   // never dragged
        float ctorLive = RailPolicy.RestingWidth(stored, scope, RailPolicy.WideMode, RailPolicy.MaxWidth);
        Assert.Equal(stored, ctorLive);

        float preMeasure = RailPolicy.ComposedWidth(ctorLive, stored, scope, RailPolicy.WideMode, RailPolicy.MaxWidth,
                                                    mode: 2, maxWidth: 280f);
        float measured = RailPolicy.ComposedWidth(ctorLive, stored, scope, RailPolicy.WideMode, RailPolicy.MaxWidth,
                                                  mode: 2, maxWidth: 272f);
        float rested = RailPolicy.RestingWidth(stored, scope, 2, 272f);
        float afterRest = RailPolicy.ComposedWidth(rested, stored, scope, 2, 272f, mode: 2, maxWidth: 272f);

        Assert.Equal(RailPolicy.NarrowRestWidth, preMeasure);
        Assert.Equal(preMeasure, measured);
        Assert.Equal(preMeasure, afterRest);
    }

    /// <summary>Once rested for the render's (mode, cap), the LIVE width wins — a drag moves the rail — clamped on read.</summary>
    [Fact]
    public void ComposedWidth_OnceRested_IsTheLiveWidthClamped()
    {
        const RailScope scope = RailScope.Album;
        float stored = RailPolicy.DefaultWidthFor(scope);
        Assert.Equal(300f, RailPolicy.ComposedWidth(300f, stored, scope, 0, 480f, mode: 0, maxWidth: 480f));
        Assert.Equal(480f, RailPolicy.ComposedWidth(520f, stored, scope, 0, 480f, mode: 0, maxWidth: 480f));
        Assert.Equal(RailPolicy.MinWidth, RailPolicy.ComposedWidth(100f, stored, scope, 0, 480f, mode: 0, maxWidth: 480f));
        // A new cap the rest has not reached yet answers the rest's value, not the stale live width.
        Assert.Equal(RailPolicy.RestingWidth(stored, scope, 0, 300f),
                     RailPolicy.ComposedWidth(480f, stored, scope, 0, 480f, mode: 0, maxWidth: 300f));
    }
}
