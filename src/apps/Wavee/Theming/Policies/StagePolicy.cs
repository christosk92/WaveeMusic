// ── Theming/Policies/StagePolicy.cs — what a structure decides about the fullscreen stage (themes plan 5.5, 3.2) ──────
//
// The stage's allocator (`Stage.Layout.Resolve`) classifies its (W, H) into one of four layout classes; the classification is
// core, the edges are the structure's: three engine `TierLadder`s (TC3, plan 3.1's "stage aspect classes", moved out of the
// allocator's constants into the structure's `Rules\Tiers.Stage.cs`), each edge with its own hold, resolved against the band the
// previous class sat in, so a drag across an edge flips once per crossing. Beside them, the per-WINDOW-tier numbers the allocator
// budgets from (the transport card's height and gutter, the lyrics pane's type range): the stage fills the window, so its tier is
// the window's. There is no default FACE here: the face is Bloom in every theme until the user picks one (owner NQ3).

using FluentGpu.Foundation;
using Wavee.Theming.Contracts;

namespace Wavee.Theming;

/// <summary>The edges between the stage's layout classes, as three ladders. The class is decided in this order: a stage whose
/// width / height ratio is in the <see cref="Ratio"/> ladder's Portrait band is Portrait at every width; otherwise a stage under the
/// <see cref="Width"/> ladder's edge is Compact when it is landscape (ratio at least 1) and Portrait when it is not; otherwise a landscape stage (ratio at least 1) in the <see cref="Height"/> ladder's
/// short band is Compact; otherwise the Ratio ladder's top band is Ultrawide and its middle one Desktop. Each ladder is resolved
/// against the band the PREVIOUS class sat in, so its edge holds.</summary>
/// <param name="Width">One edge: band 0 (under it) is the narrow Compact stage, band 1 is wide.</param>
/// <param name="Height">One edge: band 0 is the short landscape stage (Compact), band 1 is tall enough for the wide classes.</param>
/// <param name="Ratio">Two edges on width / height: band 0 Portrait, band 1 Desktop, band 2 Ultrawide.</param>
public sealed record AspectThresholds(TierLadder Width, TierLadder Height, TierLadder Ratio)
{
    /// <summary>The bands of the three ladders.</summary>
    public const int NarrowBand = 0, ShortBand = 0, PortraitBand = 0, DesktopBand = 1, UltrawideBand = 2;

    /// <summary>The shell before themes, what Wavee Classic declares (<c>ClassicStageTiers.Aspect</c>; `ClassicPoliciesTests` pins the
    /// two equal): Compact under 600 wide, a landscape stage Compact at or below 460 tall (back at 484), Portrait at or below the ratio
    /// 0.80 (back at 0.85), Ultrawide from 2.00 (back at 1.95 or below).</summary>
    public static AspectThresholds Default { get; } = new(
        TierLadder.Of(new TierEdge(600f)),
        TierLadder.Of(AtOrBelow(460f, leaveAt: 484f)),
        TierLadder.Of(AtOrBelow(0.80f, leaveAt: 0.85f), From(2.00f, leaveAtOrBelow: 1.95f)));

    /// <summary>An edge whose LOWER band includes <paramref name="atOrBelow"/> and is left once the value reaches
    /// <paramref name="leaveAt"/>. A ladder edge is "at or above <c>At</c>" for the band above it, so the edge sits one float above the
    /// inclusive value; the widening hold is exact (Sterbenz: the two values are within a factor of two of each other).</summary>
    public static TierEdge AtOrBelow(float atOrBelow, float leaveAt)
    {
        float at = MathF.BitIncrement(atOrBelow);
        return new TierEdge(at, NarrowHold: 0f, WidenHold: MathF.Max(0f, leaveAt - at));
    }

    /// <summary>An edge entered at <paramref name="from"/> and left once the value falls to <paramref name="leaveAtOrBelow"/> or
    /// below: the band above holds while the value stays above <paramref name="leaveAtOrBelow"/>.</summary>
    public static TierEdge From(float from, float leaveAtOrBelow)
        => new(from, NarrowHold: MathF.Max(0f, from - MathF.BitIncrement(leaveAtOrBelow)), WidenHold: 0f);

    /// <summary>Everything wrong with the thresholds, empty when they can classify a stage.</summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        if (Width is null || Height is null || Ratio is null)
        {
            problems.Add("Stage.Aspect needs its width, height and ratio ladders");
            return problems;
        }
        if (Width.EdgeCount != 1 || !Width.EdgeAt(0).IsPresent) problems.Add("Stage.Aspect.Width must have exactly one edge");
        if (Height.EdgeCount != 1 || !Height.EdgeAt(0).IsPresent) problems.Add("Stage.Aspect.Height must have exactly one edge");
        if (Ratio.EdgeCount != 2 || !Ratio.EdgeAt(0).IsPresent || !Ratio.EdgeAt(1).IsPresent) problems.Add("Stage.Aspect.Ratio must have exactly two edges (Portrait | Desktop | Ultrawide)");
        if (problems.Count > 0) return problems;
        if (Width.EdgeAt(0).At < 0f || Height.EdgeAt(0).At < 0f || Ratio.EdgeAt(0).At < 0f) problems.Add("Stage.Aspect: no edge may lie below zero");
        // Portrait is left (widening) before Ultrawide is left (narrowing): the two held bands never overlap.
        TierEdge portrait = Ratio.EdgeAt(0), ultra = Ratio.EdgeAt(1);
        if (portrait.At + portrait.WidenHold >= ultra.At - ultra.NarrowHold) problems.Add("Stage.Aspect: the Portrait and Ultrawide ratio bands must not overlap");
        return problems;
    }

    /// <summary>Equal when the three ladders have the same edges (a ladder is a class: compared edge by edge, not by reference), so an
    /// unchanged policy republished is not a change.</summary>
    public bool Equals(AspectThresholds? other)
        => other is not null && Same(Width, other.Width) && Same(Height, other.Height) && Same(Ratio, other.Ratio);

    /// <summary>The hash of the three ladders' edges.</summary>
    public override int GetHashCode() => HashCode.Combine(Hash(Width), Hash(Height), Hash(Ratio));

    static bool Same(TierLadder? a, TierLadder? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null || a.EdgeCount != b.EdgeCount || a.Aspect != b.Aspect) return false;
        for (int i = 0; i < a.EdgeCount; i++)
        {
            TierEdge x = a.EdgeAt(i), y = b.EdgeAt(i);
            if (x.IsPresent != y.IsPresent || (x.IsPresent && x != y)) return false;
        }
        return true;
    }

    static int Hash(TierLadder? ladder)
    {
        if (ladder is null) return 0;
        var h = new HashCode();
        for (int i = 0; i < ladder.EdgeCount; i++) h.Add(ladder.EdgeAt(i).IsPresent ? ladder.EdgeAt(i).At : -1f);
        return h.ToHashCode();
    }
}

/// <summary>What the stage's allocator budgets from in one WINDOW tier (the stage fills the window).</summary>
/// <param name="TransportH">The transport card's height on the wide classes (Desktop, Ultrawide, Portrait); the Compact class keeps its
/// own short card.</param>
/// <param name="TransportInset">The transport card's gutter to the window's edges.</param>
/// <param name="LyricsTypeMin">The lyrics pane's smallest type size.</param>
/// <param name="LyricsTypeMax">The lyrics pane's largest type size.</param>
public sealed record StageTierMetrics(float TransportH, float TransportInset, float LyricsTypeMin, float LyricsTypeMax)
{
    /// <summary>The tuned Standard stage: the 112 card at the 24 gutter, the authored 36 grown up to 64 on a big stage.</summary>
    public static StageTierMetrics Default { get; } = new(112f, 24f, 36f, 64f);

    /// <summary>The docked gallery runs down to the transport card's gutter at the stage's foot and the card yields its width beside it
    /// (C-Stage-Huge); otherwise the gallery stops above the full-width card. Appended (contract 1.22).</summary>
    public bool GalleryToFoot { get; init; }

    /// <summary>Lyrics mode docks the Up next column at this width on the right, beside the lyrics, above the transport card
    /// (C-Stage-Huge4K); 0: none. Appended (contract 1.22).</summary>
    public float UpNextW { get; init; }

    /// <summary>Lyrics mode draws the visualizer bars under the cover's title column, this tall (C-Stage-Huge4K); 0: none. Appended
    /// (contract 1.22).</summary>
    public float ArtBarsH { get; init; }

    /// <summary>The identity is the now-playing CARD (the small cover and one title line in a card under the top bar) in Visualizer mode, and
    /// in every mode on a Portrait stage (C-Stage-Compact, C-Lyrics-Compact). Appended (contract 1.22).</summary>
    public bool IdentityCard { get; init; }

    /// <summary>The gallery opens only when it is asked for during the stage's life (the face chip, the options footer, G), never from the
    /// stored flag alone (C-Stage-Compact draws none). Appended (contract 1.22).</summary>
    public bool GalleryOnDemand { get; init; }

    /// <summary>Everything wrong with the metrics of <paramref name="tier"/>.</summary>
    public IReadOnlyList<string> Validate(string tier)
    {
        var problems = new List<string>();
        if (!float.IsFinite(TransportH) || TransportH < 80f) problems.Add($"Stage.{tier}.TransportH must hold the seek row and the play disc (at least 80, was {TransportH})");
        if (!float.IsFinite(TransportInset) || TransportInset < 0f) problems.Add($"Stage.{tier}.TransportInset must be zero or more (was {TransportInset})");
        if (!float.IsFinite(LyricsTypeMin) || LyricsTypeMin <= 0f) problems.Add($"Stage.{tier}.LyricsTypeMin must be positive (was {LyricsTypeMin})");
        if (!float.IsFinite(LyricsTypeMax) || LyricsTypeMax < LyricsTypeMin) problems.Add($"Stage.{tier}.LyricsTypeMax must not be below LyricsTypeMin");
        if (!float.IsFinite(UpNextW) || UpNextW < 0f) problems.Add($"Stage.{tier}.UpNextW must be zero or more (was {UpNextW})");
        if (!float.IsFinite(ArtBarsH) || ArtBarsH < 0f) problems.Add($"Stage.{tier}.ArtBarsH must be zero or more (was {ArtBarsH})");
        return problems;
    }
}

/// <summary>The stage's policy.</summary>
/// <param name="Aspect">The layout-class edges.</param>
public sealed record StagePolicy(AspectThresholds Aspect)
{
    /// <summary>The shell before themes (what Wavee Classic declares, <c>ClassicStageTiers.Policy</c>): the boards' metrics per window
    /// tier (C-Stage-Compact / C-Lyrics-Compact, C-Stage-Small / C-Lyrics-Small, C-Lyrics-Huge / C-Stage-Huge4K). Under
    /// <see cref="TierPolicy.Standard"/> only <see cref="Metrics"/> is ever read.</summary>
    public static StagePolicy Default { get; } = new(AspectThresholds.Default)
    {
        Compact = new StageTierMetrics(TransportH: 104f, TransportInset: 16f, LyricsTypeMin: 26f, LyricsTypeMax: 26f) { IdentityCard = true, GalleryOnDemand = true },
        Small = new StageTierMetrics(TransportH: 96f, TransportInset: 16f, LyricsTypeMin: 30f, LyricsTypeMax: 36f),
        Huge = StageTierMetrics.Default with { LyricsTypeMax = 48f, GalleryToFoot = true },
        HugeWide = StageTierMetrics.Default with { LyricsTypeMax = 48f, GalleryToFoot = true, UpNextW = 520f, ArtBarsH = 140f },
        HugeWideRung = "detail.huge.wide",   // Classic's Huge 4K mark (ClassicDetailTiers.HugeWideRung); a policy without it reads Huge's at every Huge width
    };

    /// <summary>The metrics of every window tier that has none of its own (Standard).</summary>
    public StageTierMetrics Metrics { get; init; } = StageTierMetrics.Default;

    /// <summary>The metrics while the window is Compact; null keeps <see cref="Metrics"/>.</summary>
    public StageTierMetrics? Compact { get; init; }

    /// <summary>The metrics while the window is Small; null keeps <see cref="Metrics"/>.</summary>
    public StageTierMetrics? Small { get; init; }

    /// <summary>The metrics while the window is Huge or Huge 4K; null keeps <see cref="Metrics"/>.</summary>
    public StageTierMetrics? Huge { get; init; }

    /// <summary>The Huge band's wide twin (C-Stage-Huge4K: the Up next column beside the lyrics, the bars under the cover), read from
    /// <see cref="HugeWideRung"/> on; null: Huge's at every Huge width. Appended (contract 1.22).</summary>
    public StageTierMetrics? HugeWide { get; init; }

    /// <summary>The window-width rung of the structure's <see cref="TierPolicy.Rungs"/> from which a Huge window reads <see cref="HugeWide"/>.
    /// Appended (contract 1.22).</summary>
    public string? HugeWideRung { get; init; }

    /// <summary>The metrics in window tier <paramref name="window"/>.</summary>
    public StageTierMetrics MetricsFor(WidthTier window) => window switch
    {
        WidthTier.Compact => Compact ?? Metrics,
        WidthTier.Small => Small ?? Metrics,
        WidthTier.Huge or WidthTier.Huge4K => Huge ?? Metrics,
        _ => Metrics,
    };

    /// <summary>The metrics for the window tier of <paramref name="tiers"/>; <see cref="Metrics"/> where no frame resolved the tiers.</summary>
    public StageTierMetrics MetricsFor(in TierFacts tiers) => tiers.Policy is null ? Metrics : IsHugeWide(in tiers) ? HugeWide! : MetricsFor(tiers.Window);

    /// <summary>Is the facts' window a Huge one past <see cref="HugeWideRung"/> (and the policy has a <see cref="HugeWide"/>)?</summary>
    public bool IsHugeWide(in TierFacts tiers)
        => HugeWide is not null && HugeWideRung is { } rung && tiers.Policy is { } policy && tiers.Window >= WidthTier.Huge
           && policy.IndexOfRung(rung) >= 0 && tiers.Rung(rung);

    /// <summary>Everything wrong with the policy.</summary>
    public IReadOnlyList<string> Validate()
    {
        if (Aspect is null) return new[] { "Stage.Aspect is missing" };
        var problems = new List<string>(Aspect.Validate());
        if (Metrics is null) problems.Add("Stage.Metrics is missing"); else problems.AddRange(Metrics.Validate("Metrics"));
        if (Compact is { } compact) problems.AddRange(compact.Validate("Compact"));
        if (Small is { } small) problems.AddRange(small.Validate("Small"));
        if (Huge is { } huge) problems.AddRange(huge.Validate("Huge"));
        if (HugeWide is { } wide)
        {
            problems.AddRange(wide.Validate("HugeWide"));
            if (HugeWideRung is not { Length: > 0 }) problems.Add("Stage.HugeWide needs the HugeWideRung it is read from");
        }
        return problems;
    }
}
