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
// (pure, tested); this file owns the engine calls and the card. Rides the capture-diagnostics page after the Tiles card.

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

namespace Wavee;

public static partial class Diagnostics
{
    sealed class EvidenceCardView : Component
    {
        readonly Signal<string> _x = new("0");
        readonly Signal<string> _y = new("0");
        readonly Signal<string> _tag = new("card");
        readonly Signal<int> _refresh = new(0);
        readonly Signal<IReadOnlyList<EvidencePixelRow>> _rows = new(Array.Empty<EvidencePixelRow>());
        readonly Signal<string> _queried = new("");
        InputHooks? _hooks;
        Action<Point2>? _pick;

        public override Element Render()
        {
            _ = _refresh.Value;
            _hooks = UseContext(InputHooks.Current);
            UseInterval(() => _refresh.Value = _refresh.Peek() + 1, 750f, enabled: true);
            UseEffect(() => () => StopPick(), DepKey.Empty);

            var host = Probe.Host;
            TileCensus census = host?.LastTileCensus ?? default;
            int refused = host is not null && host.TryGetDeviceCounters(out GpuFrameCounters d) ? d.ScratchRefused : 0;
            var inv = CultureInfo.InvariantCulture;
            string facts = Strings.Diagnostics.Evidence.Facts(census.StaleTiles.ToString(inv), census.ExposedMissing.ToString(inv),
                refused.ToString(inv), TileInvariants.StaleTurns.ToString(inv));
            bool healthy = EvidenceReport.IsHealthy(census, refused);

            var rows = new List<Element>(12)
            {
                healthy
                    ? Status(Icons.StatusSuccess, Tok.SystemFillSuccess, Loc.Get(Strings.Diagnostics.Evidence.HealthyTitle), facts)
                    : Status(Icons.StatusWarning, Tok.SystemFillCaution, Loc.Get(Strings.Diagnostics.Evidence.WarningTitle), facts),
                new BoxEl
                {
                    Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center,
                    Children =
                    [
                        new TextEl(Loc.Get(Strings.Diagnostics.Evidence.Pixel)) { Size = 12f, Color = Tok.TextSecondary, Width = 48f, Shrink = 0f },
                        TextBox.Create(_x, null, new TextBox.TextBoxOptions { Header = "x", Width = 80f }),
                        TextBox.Create(_y, null, new TextBox.TextBoxOptions { Header = "y", Width = 80f }),
                        new TextEl(Loc.Get(Strings.Diagnostics.Evidence.Dip)) { Size = 12f, Color = Tok.TextTertiary, Shrink = 0f },
                        Button.Standard(Loc.Get(Strings.Diagnostics.Evidence.Query), Query),
                        Button.Standard(Loc.Get(_pick is null ? Strings.Diagnostics.Evidence.Pick : Strings.Diagnostics.Evidence.Picking), TogglePick),
                    ],
                },
            };
            if (_queried.Value.Length > 0) rows.Add(Caption(_queried.Value));
            var hits = _rows.Value;
            if (hits.Count > 0)
            {
                rows.Add(HitRow("#", "kind", "node", "α", "f1", "f2", "tile", "raster", header: true, stale: false));
                foreach (var r in hits) rows.Add(HitRow(r.Index, r.Kind, r.Node, r.Alpha, r.Feather1, r.Feather2, r.Tile, r.Raster, header: false, stale: r.Stale));
            }
            rows.Add(Separator(2f));
            rows.Add(new BoxEl
            {
                Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.End,
                Children =
                [
                    Button.Standard(Loc.Get(Evidence.Busy ? Strings.Diagnostics.Evidence.Exporting : Strings.Diagnostics.Evidence.Export), Export),
                    TextBox.Create(_tag, null, new TextBox.TextBoxOptions { Header = Loc.Get(Strings.Diagnostics.Evidence.Tag), Width = 200f }),
                ],
            });
            rows.Add(Row(Loc.Get(Strings.Diagnostics.Evidence.Last), Evidence.LastFolder));
            rows.Add(Caption(Loc.Get(Strings.Diagnostics.Evidence.Caption)));
            return Card(Loc.Get(Strings.Diagnostics.Evidence.Title), rows);
        }

        static Element HitRow(string index, string kind, string node, string alpha, string f1, string f2, string tile, string raster,
            bool header, bool stale)
        {
            ColorF c = header ? Tok.TextSecondary : stale ? Tok.SystemFillCritical : Tok.TextPrimary;
            TextEl Cell(string s, float w) => new(s) { Size = 12f, Color = c, Width = w, Shrink = 0f, FontFamily = "Cascadia Code", Trim = TextTrim.CharacterEllipsis };
            return new BoxEl
            {
                Direction = 0, Gap = Spacing.XS,
                Children =
                [
                    Cell(index, 28f), Cell(kind, 64f), new TextEl(node) { Size = 12f, Color = c, Grow = 1f, MinWidth = 0f, FontFamily = "Cascadia Code", Trim = TextTrim.CharacterEllipsis },
                    Cell(alpha, 44f), Cell(f1, 44f), Cell(f2, 44f), Cell(tile, 56f), Cell(stale ? raster + "*" : raster, 72f),
                ],
            };
        }

        void Query()
        {
            if (!float.TryParse(_x.Peek(), NumberStyles.Float, CultureInfo.InvariantCulture, out float x)
                || !float.TryParse(_y.Peek(), NumberStyles.Float, CultureInfo.InvariantCulture, out float y)) return;
            RunQuery(x, y);
        }

        void RunQuery(float x, float y)
        {
            var host = Probe.Host;
            if (host is null) return;
            PixelHit[] hits = Evidence.QueryPixel((int)MathF.Floor(x), (int)MathF.Floor(y), dip: true, writeNow: false, out CompositeFrameHeader frame);
            var names = new Dictionary<(int, uint), string>();
            string Name(int index, uint gen)
            {
                if (!names.TryGetValue((index, gen), out var v)) names[(index, gen)] = v = host.DescribeNode(index, gen);
                return v;
            }
            _rows.Value = EvidenceReport.PixelRows(hits, Name);
            _queried.Value = Strings.Diagnostics.Evidence.Queried(x.ToString("0.#", CultureInfo.InvariantCulture),
                y.ToString("0.#", CultureInfo.InvariantCulture), frame.Frame.ToString(CultureInfo.InvariantCulture),
                hits.Length.ToString(CultureInfo.InvariantCulture));
        }

        void TogglePick()
        {
            if (_pick is not null) { StopPick(); _refresh.Value = _refresh.Peek() + 1; return; }
            if (_hooks is null) return;
            _pick = OnPick;
            _hooks.PointerDownObserved += _pick;
            _refresh.Value = _refresh.Peek() + 1;
        }

        void OnPick(Point2 p)
        {
            StopPick();
            _x.Value = p.X.ToString("0.#", CultureInfo.InvariantCulture);
            _y.Value = p.Y.ToString("0.#", CultureInfo.InvariantCulture);
            RunQuery(p.X, p.Y);
        }

        void StopPick()
        {
            if (_pick is not null && _hooks is not null) _hooks.PointerDownObserved -= _pick;
            _pick = null;
        }

        void Export()
        {
            Evidence.RequestBundle(_tag.Peek());
            _refresh.Value = _refresh.Peek() + 1;
        }
    }
}
