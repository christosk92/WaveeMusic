// ── Screens/Diagnostics.Tiles.cs — the Diagnostics "Tiles" card (retained-tile census + GPU pass timing) ──────────────
//
// The engine's retained-tile census (`FluentGpu.Render.Tiles.TileCensus`) is ALWAYS-ON — read straight off
// `AppHost.LastTileCensus` (thread-safe) with no probe to arm. `ExposedMissing` (a visible tile that composited
// nothing), `DegradedSlices` and `CoverageClamps` must be 0; the card's verdict flags a nonzero one the same way the
// Runtime page flags a broken local-playback search (`Status`, Diagnostics.UI.cs).
//
// GPU PASS TIMING IS A RUNTIME TOGGLE, NOT A SETTING: `AppHost.GpuPassTimingEnabled` is a session measurement knob
// (CLAUDE.md's "no environment-variable switches" — this is the runtime-settable replacement for one) flipped from
// this card only, never persisted, and reset to off on every launch. While on, `AppHost.CopyGpuPassTimeline` returns
// the most recently retired frame's pass-granular breakdown into a preallocated buffer — zero-alloc per poll.
//
// The decisions — MiB/percent formatting, the health verdict, which invalidation reasons to show, the per-pass rows
// and the per-kind aggregate — are `TileDiagRules` (pure, tested); this file owns the engine calls and the card.

using System.Globalization;
using FluentGpu;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Render.Tiles;
using FluentGpu.Rhi;
using FluentGpu.Signals;

namespace Wavee;

/// <summary>The Tiles card's pure decisions: MiB/percent formatting, the health verdict, the nonzero invalidation
/// reasons, and the GPU pass timeline's per-pass rows and per-kind aggregate.</summary>
public static class TileDiagRules
{
    const long BytesPerMiB = 1024L * 1024L;

    /// <summary>Whether this turn's census should read as a warning: an exposed-missing tile, a STALE tile (valid pixels
    /// the current stream no longer describes — evidence-diagnostics §A.1), a degraded slice, a scroll-coverage clamp, or
    /// resident bytes past budget are all real correctness/perf problems, never noise.</summary>
    public static bool IsWarning(TileCensus c)
        => c.ExposedMissing > 0 || c.StaleTiles > 0 || c.DegradedSlices > 0 || c.CoverageClamps > 0 || OverBudget(c);

    public static bool OverBudget(TileCensus c) => c.BudgetBytes > 0 && c.ResidentBytes > c.BudgetBytes;

    /// <summary><paramref name="bytes"/> as "12.3 MiB", one decimal, invariant.</summary>
    public static string FormatMiB(long bytes) => (bytes / (double)BytesPerMiB).ToString("0.0", CultureInfo.InvariantCulture) + " MiB";

    /// <summary>Resident bytes as a percent of budget, clamped to 0..100 for a bar/verdict read; a zero or negative
    /// budget (not yet published) reads as 0, never a divide-by-zero.</summary>
    public static int BudgetPercentClamped(long residentBytes, long budgetBytes)
    {
        if (budgetBytes <= 0) return 0;
        double pct = residentBytes * 100.0 / budgetBytes;
        return pct < 0 ? 0 : pct > 100 ? 100 : (int)Math.Round(pct, MidpointRounding.AwayFromZero);
    }

    /// <summary>A millisecond value formatted "0.00", invariant — the same precision for a single pass and an
    /// aggregated sum.</summary>
    public static string FormatMs(float ms) => ms.ToString("0.00", CultureInfo.InvariantCulture);

    // ── invalidation reasons ─────────────────────────────────────────────────────────────────────────────────────

    public enum ReasonKind
    {
        NoTexture, Content, PrimCount, ValidRectChanged, ScaleChanged, SliceGeometry, BackgroundOrTheme, Evicted, Degraded,
    }

    public readonly record struct ReasonRow(ReasonKind Reason, int Count);

    /// <summary>Only the reasons this turn actually hit, in the census's own field order — a zero reason is noise on
    /// a page meant to be read at a glance.</summary>
    public static IReadOnlyList<ReasonRow> ReasonRows(TileCensus c)
    {
        ReadOnlySpan<(ReasonKind Reason, int Count)> all =
        [
            (ReasonKind.NoTexture, c.NoTexture),
            (ReasonKind.Content, c.Content),
            (ReasonKind.PrimCount, c.PrimCount),
            (ReasonKind.ValidRectChanged, c.ValidRectChanged),
            (ReasonKind.ScaleChanged, c.ScaleChanged),
            (ReasonKind.SliceGeometry, c.SliceGeometry),
            (ReasonKind.BackgroundOrTheme, c.BackgroundOrTheme),
            (ReasonKind.Evicted, c.EvictedReason),
            (ReasonKind.Degraded, c.Degraded),
        ];
        var rows = new List<ReasonRow>(all.Length);
        foreach (var (reason, count) in all)
            if (count > 0) rows.Add(new ReasonRow(reason, count));
        return rows;
    }

    // ── GPU pass timeline ────────────────────────────────────────────────────────────────────────────────────────

    public readonly record struct PassRow(GpuPassKind Kind, int TargetWidthPx, int TargetHeightPx, string Ms);

    /// <summary>One row per interval the timeline copied this poll, in recorded (chronological) order.</summary>
    public static IReadOnlyList<PassRow> PassRows(ReadOnlySpan<GpuPassTiming> passes)
    {
        var rows = new List<PassRow>(passes.Length);
        foreach (var p in passes) rows.Add(new PassRow(p.Kind, p.TargetWidthPx, p.TargetHeightPx, FormatMs(p.Ms)));
        return rows;
    }

    public readonly record struct AggregateRow(GpuPassKind Kind, int Count, string TotalMs);

    /// <summary>Per-kind sums of <paramref name="passes"/>, heaviest kind first — the shape that answers "what is the
    /// frame actually spending its GPU time on."</summary>
    public static IReadOnlyList<AggregateRow> AggregatePasses(ReadOnlySpan<GpuPassTiming> passes)
    {
        var sums = new Dictionary<GpuPassKind, (float Ms, int Count)>();
        foreach (var p in passes)
        {
            sums.TryGetValue(p.Kind, out var cur);
            sums[p.Kind] = (cur.Ms + p.Ms, cur.Count + 1);
        }
        var entries = new List<(GpuPassKind Kind, float Ms, int Count)>(sums.Count);
        foreach (var kv in sums) entries.Add((kv.Key, kv.Value.Ms, kv.Value.Count));
        entries.Sort(static (a, b) => b.Ms.CompareTo(a.Ms));

        var rows = new List<AggregateRow>(entries.Count);
        foreach (var e in entries) rows.Add(new AggregateRow(e.Kind, e.Count, FormatMs(e.Ms)));
        return rows;
    }
}

public static partial class Diagnostics
{
    /// <summary>The Diagnostics "Tiles" card: the always-on retained-tile census, its invalidation reasons, and the
    /// GPU pass timing toggle + breakdown. Every number is read straight off the engine each poll; the card holds no
    /// state but the toggle and its own refresh tick.</summary>
    sealed class TileCardView : Component
    {
        // Preallocated once — CopyGpuPassTimeline is zero-alloc into this buffer every poll while the toggle is on.
        static readonly GpuPassTiming[] s_passBuffer = new GpuPassTiming[64];

        // Seeded from the host: the timeline may already be on (`--fg gpu-timing`, a probe), and the toggle must say so.
        readonly Signal<bool> _passTiming = new(Probe.Host?.GpuPassTimingEnabled ?? false);
        readonly Signal<int> _refresh = new(0);

        public override Element Render()
        {
            _ = _refresh.Value;
            bool passTimingOn = _passTiming.Value;

            // One 750 ms poll drives both halves: the always-on census read is cheap (a lock + struct copy), and the
            // pass-timeline copy below only actually runs while the toggle is on.
            UseInterval(() => _refresh.Value = _refresh.Peek() + 1, 750f, enabled: true);

            var host = Probe.Host;
            var census = host?.LastTileCensus ?? default;

            var rows = new List<Element>(8) { Verdict(census) };
            rows.AddRange(CensusRows(census));

            var reasons = TileDiagRules.ReasonRows(census);
            rows.Add(Separator(2f));
            rows.Add(new TextEl(Loc.Get(Strings.Diagnostics.Tiles.ReasonsTitle)) { Size = 12f, Weight = 600, Color = Tok.TextPrimary });
            rows.Add(reasons.Count == 0 ? Body(Loc.Get(Strings.Diagnostics.Tiles.NoReasons)) : ReasonsBlock(reasons));

            rows.Add(Separator(2f));
            rows.Add(Labeled(Loc.Get(Strings.Diagnostics.Tiles.GpuPassTiming),
                ToggleSwitch.Create(_passTiming, OnToggle, isEnabled: host is not null)));
            rows.Add(Caption(Loc.Get(Strings.Diagnostics.Tiles.GpuPassTimingCaption)));

            if (passTimingOn && host is not null) rows.AddRange(PassTimingRows(host));

            rows.Add(Caption(Loc.Get(Strings.Diagnostics.Tiles.Caption)));

            return Card(Loc.Get(Strings.Diagnostics.Tiles.Title), rows);
        }

        void OnToggle(bool on)
        {
            _passTiming.Value = on;
            var host = Probe.Host;
            if (host is not null) host.GpuPassTimingEnabled = on;
        }

        // ── the census block ─────────────────────────────────────────────────────────────────────────────────────

        static Element Verdict(TileCensus c) => TileDiagRules.IsWarning(c)
            ? Status(Icons.StatusWarning, Tok.SystemFillCaution, Loc.Get(Strings.Diagnostics.Tiles.WarningTitle), Loc.Get(Strings.Diagnostics.Tiles.WarningBody))
            : Status(Icons.StatusSuccess, Tok.SystemFillSuccess, Loc.Get(Strings.Diagnostics.Tiles.HealthyTitle), Loc.Get(Strings.Diagnostics.Tiles.HealthyBody));

        static IEnumerable<Element> CensusRows(TileCensus c)
        {
            var inv = CultureInfo.InvariantCulture;
            yield return Row(Loc.Get(Strings.Diagnostics.Tiles.Slices), c.Slices.ToString(inv));
            yield return Row(Loc.Get(Strings.Diagnostics.Tiles.LiveTiles), c.LiveTiles.ToString(inv));
            yield return Row(Loc.Get(Strings.Diagnostics.Tiles.ResidentTiles), c.ResidentTiles.ToString(inv));
            yield return Row(Loc.Get(Strings.Diagnostics.Tiles.Resident),
                Strings.Diagnostics.Tiles.ResidentValue(TileDiagRules.FormatMiB(c.ResidentBytes),
                    TileDiagRules.BudgetPercentClamped(c.ResidentBytes, c.BudgetBytes).ToString(inv)));
            yield return Row(Loc.Get(Strings.Diagnostics.Tiles.Scheduled), c.Scheduled.ToString(inv));
            yield return Row(Loc.Get(Strings.Diagnostics.Tiles.Rastered), c.Rastered.ToString(inv));
            yield return Row(Loc.Get(Strings.Diagnostics.Tiles.Evicted), c.Evicted.ToString(inv));
            yield return Row(Loc.Get(Strings.Diagnostics.Tiles.Items), c.Items.ToString(inv));
            yield return Row(Loc.Get(Strings.Diagnostics.Tiles.DegradedSlices), c.DegradedSlices.ToString(inv));
            yield return Row(Loc.Get(Strings.Diagnostics.Tiles.ExposedMissing), c.ExposedMissing.ToString(inv));
            yield return Row(Loc.Get(Strings.Diagnostics.Tiles.StaleTiles), c.StaleTiles.ToString(inv));
            yield return Row(Loc.Get(Strings.Diagnostics.Tiles.CoverageClamps), c.CoverageClamps.ToString(inv));
        }

        // ── invalidation reasons ─────────────────────────────────────────────────────────────────────────────────

        static Element ReasonsBlock(IReadOnlyList<TileDiagRules.ReasonRow> reasons)
        {
            var kids = new Element[reasons.Count];
            for (int i = 0; i < reasons.Count; i++)
            {
                var r = reasons[i];
                kids[i] = Row(ReasonLabel(r.Reason), r.Count.ToString(CultureInfo.InvariantCulture));
            }
            return new BoxEl { Direction = 1, Gap = 2f, Children = kids };
        }

        static string ReasonLabel(TileDiagRules.ReasonKind reason) => Loc.Get(reason switch
        {
            TileDiagRules.ReasonKind.NoTexture => Strings.Diagnostics.Tiles.ReasonNoTexture,
            TileDiagRules.ReasonKind.Content => Strings.Diagnostics.Tiles.ReasonContent,
            TileDiagRules.ReasonKind.PrimCount => Strings.Diagnostics.Tiles.ReasonPrimCount,
            TileDiagRules.ReasonKind.ValidRectChanged => Strings.Diagnostics.Tiles.ReasonValidRectChanged,
            TileDiagRules.ReasonKind.ScaleChanged => Strings.Diagnostics.Tiles.ReasonScaleChanged,
            TileDiagRules.ReasonKind.SliceGeometry => Strings.Diagnostics.Tiles.ReasonSliceGeometry,
            TileDiagRules.ReasonKind.BackgroundOrTheme => Strings.Diagnostics.Tiles.ReasonBackgroundOrTheme,
            TileDiagRules.ReasonKind.Evicted => Strings.Diagnostics.Tiles.ReasonEvicted,
            _ => Strings.Diagnostics.Tiles.ReasonDegraded,
        });

        // ── GPU pass timing breakdown (toggle on only) ───────────────────────────────────────────────────────────

        static IEnumerable<Element> PassTimingRows(FluentGpu.Hosting.AppHost host)
        {
            int n = host.CopyGpuPassTimeline(s_passBuffer, out var summary);
            var passes = s_passBuffer.AsSpan(0, n);

            var results = new List<Element> { Separator(2f) };
            if (n == 0)
            {
                results.Add(Body(Loc.Get(Strings.Diagnostics.Tiles.NoPasses)));
                return results;
            }

            var inv = CultureInfo.InvariantCulture;
            results.Add(new TextEl(Strings.Diagnostics.Tiles.FrameSummary(TileDiagRules.FormatMs(summary.WholeMs),
                summary.PassCount.ToString(inv), summary.PassesDropped.ToString(inv)))
            { Size = 12f, Color = Tok.TextSecondary });

            results.Add(new TextEl(Loc.Get(Strings.Diagnostics.Tiles.AggregateTitle)) { Size = 12f, Weight = 600, Color = Tok.TextPrimary });
            foreach (var agg in TileDiagRules.AggregatePasses(passes))
                results.Add(Row(PassKindLabel(agg.Kind), agg.TotalMs + " ms  ·  " + Strings.Diagnostics.Tiles.AggregateCount(agg.Count)));

            results.Add(new TextEl(Loc.Get(Strings.Diagnostics.Tiles.PassesTitle)) { Size = 12f, Weight = 600, Color = Tok.TextPrimary });
            foreach (var p in TileDiagRules.PassRows(passes)) results.Add(PassRow(p));

            return results;
        }

        static Element PassRow(TileDiagRules.PassRow p) => new BoxEl
        {
            Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center,
            Children =
            [
                new TextEl(PassKindLabel(p.Kind)) { Size = 12f, Color = Tok.TextPrimary, Width = 120f, Shrink = 0f },
                new TextEl(p.TargetWidthPx.ToString(CultureInfo.InvariantCulture) + " × " + p.TargetHeightPx.ToString(CultureInfo.InvariantCulture))
                    { Size = 12f, Color = Tok.TextSecondary, FontFamily = "Cascadia Code", Width = 100f, Shrink = 0f },
                new TextEl(p.Ms + " ms") { Size = 12f, Color = Tok.TextSecondary, FontFamily = "Cascadia Code" },
            ],
        };

        static string PassKindLabel(GpuPassKind kind) => Loc.Get(kind switch
        {
            GpuPassKind.Uploads => Strings.Diagnostics.Tiles.PassUploads,
            GpuPassKind.BakedBlur => Strings.Diagnostics.Tiles.PassBakedBlur,
            GpuPassKind.Clear => Strings.Diagnostics.Tiles.PassClear,
            GpuPassKind.Scene => Strings.Diagnostics.Tiles.PassScene,
            GpuPassKind.GlyphBand => Strings.Diagnostics.Tiles.PassGlyphBand,
            GpuPassKind.TileRaster => Strings.Diagnostics.Tiles.PassTileRaster,
            GpuPassKind.Offscreen => Strings.Diagnostics.Tiles.PassOffscreen,
            _ => Strings.Diagnostics.Tiles.PassComposite,
        });

        static Element Labeled(string label, Element control) => new BoxEl
        {
            Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center,
            Children = [new TextEl(label) { Size = 12f, Color = Tok.TextSecondary, Width = RuntimeLabelWidth, Shrink = 0f }, control],
        };
    }
}
