// ── Screens/Diagnostics.Evidence.cs — the Diagnostics "Evidence" card (evidence-diagnostics §B) ────────────────────────
//
// ┌ Evidence ─────────────────────────────────────────────────────────────┐
// │ ● Healthy   stale tiles 0 · exposed missing 0 · scratch refused 0      │
// │ Pixel  x [ 612 ] y [ 71 ]  (DIP)   [ Query ]   [ Pick with click ]    │
// │  #  kind    node                        α     f1     f2    tile  raster │
// │ [ Export bundle ]  tag [ band-ramp-16 ]       last: logs\evidence\…    │
// └────────────────────────────────────────────────────────────────────────┘
//
// The live invariants (the tile census's StaleTiles / ExposedMissing, the device's refused scratch leases, the stale
// turns since launch), a pixel query over the latest composite ("what composited here, with which feather, from which
// tile rastered when"), and the evidence bundle export (EvidenceBundle.cs). Decisions and formats are EvidenceReport
// (pure, tested); this file owns the engine calls and the card's node; the card is the Common view's (`CommonDiagnostics.Widgets.cs`). Rides the capture-diagnostics page after the Tiles card.

using System.Globalization;
using FluentGpu;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Render.Evidence;
using FluentGpu.Render.Tiles;
using FluentGpu.Rhi;
using FluentGpu.Signals;
using Wavee.Theming.Contracts;

namespace Wavee;

public static partial class Diagnostics
{
    static readonly EvidenceHost s_evidenceHost = new();

    /// <summary>The Diagnostics "Evidence" card's node: the texts and the host. The Common view (`CommonDiagnostics.Widgets.cs`) owns the pixel boxes,
    /// the pick-with-click observer and the poll.</summary>
    static DiagNode EvidenceNode() => new DiagEvidence(
        Loc.Get(Strings.Diagnostics.Evidence.Title), Loc.Get(Strings.Diagnostics.Evidence.HealthyTitle), Loc.Get(Strings.Diagnostics.Evidence.WarningTitle),
        Loc.Get(Strings.Diagnostics.Evidence.Pixel), Loc.Get(Strings.Diagnostics.Evidence.Dip), Loc.Get(Strings.Diagnostics.Evidence.Query),
        Loc.Get(Strings.Diagnostics.Evidence.Pick), Loc.Get(Strings.Diagnostics.Evidence.Picking),
        Loc.Get(Strings.Diagnostics.Evidence.Export), Loc.Get(Strings.Diagnostics.Evidence.Exporting), Loc.Get(Strings.Diagnostics.Evidence.Tag),
        Loc.Get(Strings.Diagnostics.Evidence.Last), Loc.Get(Strings.Diagnostics.Evidence.Caption), CapturePollMs, s_evidenceHost);

    sealed class EvidenceHost : IDiagEvidenceHost
    {
        /// <summary>The live invariants (the tile census's StaleTiles / ExposedMissing, the device's refused scratch leases, the stale
        /// turns since launch).</summary>
        public DiagEvidenceView Read()
        {
            var host = Probe.Host;
            TileCensus census = host?.LastTileCensus ?? default;
            int refused = host is not null && host.TryGetDeviceCounters(out GpuFrameCounters d) ? d.ScratchRefused : 0;
            var inv = CultureInfo.InvariantCulture;
            string facts = Strings.Diagnostics.Evidence.Facts(census.StaleTiles.ToString(inv), census.ExposedMissing.ToString(inv),
                refused.ToString(inv), TileInvariants.StaleTurns.ToString(inv));
            return new DiagEvidenceView(EvidenceReport.IsHealthy(census, refused), facts, Evidence.Busy, OrDash(Evidence.LastFolder));
        }

        public DiagEvidenceAnswer? Query(float x, float y)
        {
            var host = Probe.Host;
            if (host is null) return null;
            PixelHit[] hits = Evidence.QueryPixel((int)MathF.Floor(x), (int)MathF.Floor(y), dip: true, writeNow: false, out CompositeFrameHeader frame);
            var names = new Dictionary<(int, uint), string>();
            string Name(int index, uint gen)
            {
                if (!names.TryGetValue((index, gen), out var v)) names[(index, gen)] = v = host.DescribeNode(index, gen);
                return v;
            }
            return new DiagEvidenceAnswer(EvidenceReport.PixelRows(hits, Name),
                Strings.Diagnostics.Evidence.Queried(x.ToString("0.#", CultureInfo.InvariantCulture),
                    y.ToString("0.#", CultureInfo.InvariantCulture), frame.Frame.ToString(CultureInfo.InvariantCulture),
                    hits.Length.ToString(CultureInfo.InvariantCulture)));
        }

        public void Export(string tag) => Evidence.RequestBundle(tag);
    }
}
