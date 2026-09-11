namespace Wavee.Features.Detail;

/// <summary>
/// ArtistPopular's chart-row geometry tier (artwork size / duration-cell visibility / subtitle stacking), decided
/// from the shelf's fitted column width with the SAME resize hysteresis <see cref="DetailLayoutBreakpoints"/>
/// already established for the detail page: narrow immediately — the safe direction, the row always still fits —
/// and re-admit the wider reading only once the width clears the breakpoint by <see cref="HysteresisDip"/> — the
/// risky direction, since it assumes room that a few more px of shelf remeasurement might take back. Pure static,
/// no FluentGpu reference: source-included by Wavee.Tests (ArtistPopularLayoutTests) per "no source-text tests".
///
/// WHY THIS EXISTS (S2 audit finding #8, evidence t_cputh_tt/tt2, t_klaas_a/thumbs): ArtistPopular used to fold a
/// bare `cellW >= threshold` straight into the mounted chart row's KEY. A purely cosmetic width change — the
/// extended-tracks fetch landing, a 1↔2 column crossing, a page-count flip that adds the header pager — reported a
/// new cellW, crossed a threshold, and produced a different key, so the row was destroyed and rebuilt with a
/// DIFFERENT string: the play-count FORMAT flipped ("7,650,335" ↔ "7.7M", "1.21B" ↔ "1,209,321,047"), and — because
/// `fullPlays` (cellW ≥ 300) and `stackSub` (cellW < 340) were independently thresholded — a cellW in [300, 340)
/// was BOTH full-digit AND unstacked, so the long number and the "feat. X" credit landed on the same line and the
/// number (never allowed to shrink) shoved the name out ("feat. +11,209,321,047 plays"). The remount cascade across
/// up to ten simultaneously-realized rows is also what cost the "blank right column" frame.
///
/// The fix has two halves:
///  1. HYSTERESIS (this type): the geometry tier only flips when cellW clears a breakpoint by more than the dip,
///     so shelf remeasurement jitter around 220/200/340 can't chatter.
///  2. ONE FORMAT: the play-count format is no longer part of the tier, or width-derived, AT ALL — ArtistPopular's
///     row builder always renders the compact form and puts the exact count in a tooltip, so no width can ever
///     squeeze the name again (removing the dangerous "full digits, not stacked" overlap outright rather than
///     tuning its thresholds).
/// ArtistPopular.cs feeds <see cref="Decide"/>'s result into a re-pushed component PROP, never a Key, so a tier
/// change now updates the mounted row in place instead of remounting it.
/// </summary>
public static class ArtistPopularLayout
{
    /// <summary>Same dip DetailLayoutBreakpoints uses: wide enough to absorb sub-pixel shelf remeasurement, far
    /// short of the 100s-of-px move a genuine 1↔2 column crossing makes (which still lands correctly on the very
    /// first <see cref="Decide"/> call after it — see the multi-step-jump case in the tests).</summary>
    public const float HysteresisDip = 24f;

    public const float ArtBreakW = 220f;       // < this: 40px art (Modern only — Classic art is fixed at 40px)
    public const float DurationBreakW = 200f;  // < this: the duration cell is dropped
    public const float StackBreakW = 340f;     // < this: the subtitle stacks onto its own 3rd line (Modern only)

    /// <summary>The geometry a chart row renders at. Deliberately does NOT carry the play-count format — see the
    /// type doc: that is now a width-independent constant that lives in ArtistPopular's row builder, not here.</summary>
    public readonly record struct Tier(float Art, bool ShowDuration, bool StackSub)
    {
        public static Tier Classic(bool showDuration) => new(40f, showDuration, false);
    }

    /// <summary>Bare thresholds, no memory of a previous decision — what a fresh chart (nothing decided yet)
    /// resolves to, and what <see cref="Decide"/> falls back on when <c>previous</c> is null.</summary>
    public static Tier NominalFor(float cellW, bool classic) => classic
        ? Tier.Classic(cellW >= DurationBreakW)
        : new Tier(cellW >= ArtBreakW ? 44f : 40f, cellW >= DurationBreakW, cellW < StackBreakW);

    /// <summary>The stateful decision ArtistPopular.Card re-runs on every cellW the shelf reports.
    /// <paramref name="previous"/> is null only for the row's very first decision — the mount case — which takes
    /// <see cref="NominalFor"/> outright (mirrors DetailLayoutBreakpoints.TierFor's own <c>initialized:false</c>
    /// rule). Every later call narrows a breakpoint's reading the instant cellW drops below it, and widens only
    /// once cellW clears it by more than <see cref="HysteresisDip"/>, so a shelf remeasurement landing a few px
    /// either side of 220/200/340 cannot flip the tier back and forth. A classic/modern toggle should pass
    /// <c>previous: null</c> (ArtistPopular resets its stored tier on that transition) rather than let a forced
    /// Classic reading (art pinned to 40, never stacked) contaminate the hysteresis memory for Modern.</summary>
    public static Tier Decide(float cellW, bool classic, Tier? previous)
    {
        if (previous is not { } p) return NominalFor(cellW, classic);
        bool showDuration = Admit(cellW, DurationBreakW, p.ShowDuration);
        if (classic) return Tier.Classic(showDuration);
        float art = Admit(cellW, ArtBreakW, p.Art >= 44f) ? 44f : 40f;
        bool stackSub = !Admit(cellW, StackBreakW, !p.StackSub);
        return new Tier(art, showDuration, stackSub);
    }

    // True while cellW still qualifies for the WIDE reading of one breakpoint (more artwork, the duration cell,
    // an unstacked subtitle): narrows the instant cellW drops below the bare breakpoint, but only (re)admits the
    // wide reading once cellW clears it by HysteresisDip.
    static bool Admit(float cellW, float breakpoint, bool currentlyWide)
        => cellW >= (currentlyWide ? breakpoint : breakpoint + HysteresisDip);
}
