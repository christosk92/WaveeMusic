// ── Themes/Common/CommonDiagnostics.Widgets.cs — the diagnostics pages' stateful cards (themes plan 6.5, T11c) ─────────────────────────
//
// From `Screens\Diagnostics.UI.cs` (`PlaybackHealthView`, `PodcastWireCheckView`), `Diagnostics.Scroll.cs` (`ScrollCardView`),
// `Diagnostics.Tiles.cs` (`TileCardView`) and `Diagnostics.Evidence.cs` (`EvidenceCardView`): the five cards that own a poll, a toggle, a
// picker or text boxes. Each is a component the page embeds for its `DiagNode`; what it reads and does reaches it through the node's
// host (a core object), so a component never names the engine host, the playback core or the network.

using System.Globalization;
using FluentGpu;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Common;

internal static partial class CommonDiagnostics
{
    static Element Labeled(string label, Element control) => new BoxEl
    {
        Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center,
        Children = [new TextEl(label) { Size = CommonDiagnosticsRules.TextSize, Color = Tok.TextSecondary, Width = CommonDiagnosticsRules.RuntimeLabelWidth, Shrink = 0f }, control],
    };

    // ══ THE PLAYBACK HEALTH CARD (playback-smoothness-implementation.md §4.14; #167) ════════════════════════════════
    //
    // "Is playback healthy right now?" without opening a log. One lock-free metrics read per second (the node's `Read`), so it costs the page
    // nothing while nothing changes. A row whose fact does not exist yet (no live session, no decode yet, no seek yet) prints the page's usual "—".

    /// <summary>Self-refreshing: it owns a 1 Hz <c>UseInterval</c> over the node's read, so the page's own Refresh button is not needed for it.</summary>
    sealed class HealthCard : Component
    {
        readonly Signal<int> _tick = new(0);

        public override Element Render()
        {
            var node = UseProps<DiagHealth>();
            UseInterval(() => _tick.Value = _tick.Peek() + 1, node.PollMs, enabled: true);
            _ = _tick.Value;
            return CardOf(node.Read(), Env.None);
        }
    }

    // ══ THE PODCAST WIRE CHECK (podcast-show-rework-implementation.md §6.0; owner D1) ══════════════════════════════
    //
    // A Diagnostics click that fires the podcast rework's pathfinder ops + the transcript GET, ONCE each, and lists each one's status/bytes/root-found.
    // All additive: a new card on the existing runtime-diagnostics page, no restructuring.

    /// <summary>The two uri fields, the Run button (disabled while running or signed out), the "Save captures" toggle and the result list. Prefills
    /// BOTH fields once, at mount, from the now-playing episode/show if one is playing (never overwrites a field the user already typed into). The
    /// network walk itself runs on an api worker thread; this component only awaits it and marshals the result back with <c>UsePost</c>,
    /// same as every other background action on this page.</summary>
    sealed class WireCheckCard : Component
    {
        readonly Signal<string> _showUri = new("");
        readonly Signal<string> _episodeUri = new("");
        readonly Signal<bool> _saveCaptures = new(false);
        readonly Signal<bool> _running = new(false);
        readonly Signal<int> _resultVersion = new(0);
        IReadOnlyList<DiagWireRow>? _results;

        public override Element Render()
        {
            var node = UseProps<DiagWireCheck>();
            var host = node.Host;
            var post = UsePost();
            _ = _resultVersion.Value;
            bool live = host.Live;
            bool running = _running.Value;

            UseEffect(() =>
            {
                if (_showUri.Peek().Length > 0 || _episodeUri.Peek().Length > 0) return;
                var (show, episode) = host.Prefill();
                if (episode.Length == 0) return;
                _episodeUri.Value = episode;
                if (show.Length > 0) _showUri.Value = show;
            }, DepKey.Empty);

            var rows = new List<Element>(6)
            {
                Body(node.Body),
                TextBox.Create(_showUri, null, new TextBox.TextBoxOptions
                    { Header = node.ShowUriLabel, Width = CommonDiagnosticsRules.WireBoxWidth }),
                TextBox.Create(_episodeUri, null, new TextBox.TextBoxOptions
                    { Header = node.EpisodeUriLabel, Width = CommonDiagnosticsRules.WireBoxWidth }),
                CheckBox.Create(node.SaveCapturesLabel, _saveCaptures),
                new BoxEl
                {
                    Direction = 0, Gap = Spacing.S,
                    Children =
                    [
                        Button.Accent(running ? node.RunningLabel : node.RunLabel, () => Run(host, post), isEnabled: live && !running),
                    ],
                },
            };
            if (_results is { Count: > 0 } results) rows.Add(ResultsTable(node, results));
            return Card(node.Title, rows);
        }

        void Run(IDiagWireHost host, Action<Action> post)
        {
            string showUri = _showUri.Peek();
            string episodeUri = _episodeUri.Peek();
            bool save = _saveCaptures.Peek();
            _running.Value = true;
            _ = InvokeAsync(host, post, showUri, episodeUri, save);
        }

        async Task InvokeAsync(IDiagWireHost host, Action<Action> post, string showUri, string episodeUri, bool save)
        {
            var results = await host.Run(showUri, episodeUri, save).ConfigureAwait(false);
            post(() =>
            {
                _results = results;
                _resultVersion.Value = _resultVersion.Peek() + 1;
                _running.Value = false;
                if (save && results.Count > 0) host.Saved();
            });
        }

        static Element ResultsTable(DiagWireCheck node, IReadOnlyList<DiagWireRow> results)
        {
            var rows = new List<Element>(results.Count + 2)
            {
                new BoxEl
                {
                    Direction = 0, Gap = Spacing.S,
                    Children =
                    [
                        R.Type.MicroMeta(node.ColOp) with { Weight = 600, Color = Tok.TextTertiary, Width = CommonDiagnosticsRules.WireOpWidth },
                        R.Type.MicroMeta(node.ColStatus) with { Weight = 600, Color = Tok.TextTertiary, Width = CommonDiagnosticsRules.WireStatusWidth },
                        R.Type.MicroMeta(node.ColBytes) with { Weight = 600, Color = Tok.TextTertiary, Width = CommonDiagnosticsRules.WireBytesWidth },
                        R.Type.MicroMeta(node.ColRoot) with { Weight = 600, Color = Tok.TextTertiary, Grow = 1f },
                    ],
                },
                Separator(DiagGap.Hairline),
            };
            foreach (var r in results)
            {
                rows.Add(new BoxEl
                {
                    Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Start,
                    Children =
                    [
                        new TextEl(r.Op) { Size = CommonDiagnosticsRules.TextSize, Color = Tok.TextPrimary, Width = CommonDiagnosticsRules.WireOpWidth, Wrap = TextWrap.Wrap },
                        new TextEl(r.Status) { Size = CommonDiagnosticsRules.TextSize, Color = Tok.TextSecondary, Width = CommonDiagnosticsRules.WireStatusWidth },
                        new TextEl(r.Bytes) { Size = CommonDiagnosticsRules.TextSize, Color = Tok.TextSecondary, Width = CommonDiagnosticsRules.WireBytesWidth },
                        new TextEl(r.Root) { Size = CommonDiagnosticsRules.TextSize, Color = r.RootFound ? Tok.TextPrimary : Tok.SystemFillCritical, Grow = 1f, Wrap = TextWrap.Wrap },
                    ],
                });
            }
            return new BoxEl { Direction = 1, Gap = CommonDiagnosticsRules.WireTableGap, Children = rows.ToArray() };
        }
    }

    // ══ THE SCROLL CARD ═════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The Diagnostics "Scroll" card: the probe level (Off · Summary · Trace), the feel profile, and the CSV export. Every control reads
    /// and writes the node's host; the card holds no state of its own.</summary>
    sealed class ScrollCard : Component
    {
        SegmentedItem[]? _levelItems;

        public override Element Render()
        {
            var node = UseProps<DiagScroll>();
            var host = node.Host;
            if (_levelItems is null)   // the launch locale is fixed for the process
            {
                var items = new SegmentedItem[node.LevelLabels.Count];
                for (int i = 0; i < items.Length; i++) items[i] = new SegmentedItem(node.LevelLabels[i]);
                _levelItems = items;
            }
            return Card(node.Title,
                Labeled(node.ProbeLevelLabel, Segmented.Create(_levelItems, host.Level, onChange: host.SetLevel)),
                Body(node.LevelBody),
                Labeled(node.ProfileLabel,
                    ComboBox.Create(host.ProfileNames, host.Profile, width: CommonDiagnosticsRules.ProfileComboWidth, onChange: host.SetProfile)),
                new BoxEl
                {
                    Direction = 0, Gap = Spacing.S,
                    Children = [Button.Standard(node.ExportLabel, () => host.Export(Context.Scene?.DeviceScale ?? 1f))],
                },
                Caption(node.Caption));
        }
    }

    // ══ THE TILES CARD ══════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The Diagnostics "Tiles" card: the always-on retained-tile census, its invalidation reasons, and the GPU pass timing toggle +
    /// breakdown. Every number is read straight off the engine each poll (the node's host); the card holds no state but the toggle and its own
    /// refresh tick.</summary>
    sealed class TileCard : Component
    {
        Signal<bool>? _passTiming;
        readonly Signal<int> _refresh = new(0);
        IDiagTilesHost? _host;
        Action<bool>? _onToggle;

        public override Element Render()
        {
            var node = UseProps<DiagTiles>();
            var host = _host = node.Host;
            // Seeded from the host: the timeline may already be on (`--fg gpu-timing`, a probe), and the toggle must say so.
            var passTiming = _passTiming ??= new Signal<bool>(host.PassTimingInitial);
            _ = _refresh.Value;
            bool passTimingOn = passTiming.Value;

            // One poll drives both halves: the always-on census read is cheap (a lock + struct copy), and the
            // pass-timeline copy only actually runs while the toggle is on.
            UseInterval(() => _refresh.Value = _refresh.Peek() + 1, node.PollMs, enabled: true);

            var env = new Env(null, null, null, t => Labeled(t.Label, ToggleSwitch.Create(passTiming, _onToggle ??= OnToggle, isEnabled: t.Enabled)));
            return CardOf(host.Read(passTimingOn), env);
        }

        void OnToggle(bool on)
        {
            _passTiming!.Value = on;
            _host!.SetPassTiming(on);
        }
    }

    // ══ THE EVIDENCE CARD (evidence-diagnostics §B) ═════════════════════════════════════════════════════════════════
    //
    // ┌ Evidence ─────────────────────────────────────────────────────────────┐
    // │ ● Healthy   stale tiles 0 · exposed missing 0 · scratch refused 0      │
    // │ Pixel  x [ 612 ] y [ 71 ]  (DIP)   [ Query ]   [ Pick with click ]    │
    // │  #  kind    node                        α     f1     f2    tile  raster │
    // │ [ Export bundle ]  tag [ band-ramp-16 ]       last: logs\evidence\…    │
    // └────────────────────────────────────────────────────────────────────────┘
    //
    // The live invariants, a pixel query over the latest composite ("what composited here, with which feather, from which tile rastered when") and
    // the evidence bundle export. Decisions and formats are the core's (the node's host); this component owns the boxes, the pick-with-click
    // observer and the card.

    sealed class EvidenceCard : Component
    {
        readonly Signal<string> _x = new("0");
        readonly Signal<string> _y = new("0");
        readonly Signal<string> _tag = new("card");
        readonly Signal<int> _refresh = new(0);
        readonly Signal<IReadOnlyList<EvidencePixelRow>> _rows = new(Array.Empty<EvidencePixelRow>());
        readonly Signal<string> _queried = new("");
        InputHooks? _hooks;
        Action<Point2>? _pick;
        IDiagEvidenceHost _host = null!;

        public override Element Render()
        {
            var node = UseProps<DiagEvidence>();
            _host = node.Host;
            _ = _refresh.Value;
            _hooks = UseContext(InputHooks.Current);
            UseInterval(() => _refresh.Value = _refresh.Peek() + 1, node.PollMs, enabled: true);
            UseEffect(() => () => StopPick(), DepKey.Empty);

            var view = _host.Read();

            var rows = new List<Element>(12)
            {
                view.Healthy
                    ? Status(new DiagStatus(Icons.StatusSuccess, DiagTone.Success, node.HealthyTitle, view.Facts))
                    : Status(new DiagStatus(Icons.StatusWarning, DiagTone.Caution, node.WarningTitle, view.Facts)),
                new BoxEl
                {
                    Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center,
                    Children =
                    [
                        new TextEl(node.PixelLabel) { Size = CommonDiagnosticsRules.TextSize, Color = Tok.TextSecondary, Width = CommonDiagnosticsRules.PixelLabelWidth, Shrink = 0f },
                        TextBox.Create(_x, null, new TextBox.TextBoxOptions { Header = "x", Width = CommonDiagnosticsRules.PixelBoxWidth }),
                        TextBox.Create(_y, null, new TextBox.TextBoxOptions { Header = "y", Width = CommonDiagnosticsRules.PixelBoxWidth }),
                        new TextEl(node.DipLabel) { Size = CommonDiagnosticsRules.TextSize, Color = Tok.TextTertiary, Shrink = 0f },
                        Button.Standard(node.QueryLabel, Query),
                        Button.Standard(_pick is null ? node.PickLabel : node.PickingLabel, TogglePick),
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
            rows.Add(Separator(DiagGap.Hairline));
            rows.Add(new BoxEl
            {
                Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.End,
                Children =
                [
                    Button.Standard(view.Busy ? node.ExportingLabel : node.ExportLabel, Export),
                    TextBox.Create(_tag, null, new TextBox.TextBoxOptions { Header = node.TagLabel, Width = CommonDiagnosticsRules.TagBoxWidth }),
                ],
            });
            rows.Add(Row(node.LastLabel, view.LastFolder, CommonDiagnosticsRules.RuntimeLabelWidth));
            rows.Add(Caption(node.Caption));
            return Card(node.Title, rows);
        }

        static Element HitRow(string index, string kind, string node, string alpha, string f1, string f2, string tile, string raster,
            bool header, bool stale)
        {
            ColorF c = header ? Tok.TextSecondary : stale ? Tok.SystemFillCritical : Tok.TextPrimary;
            TextEl Cell(string s, float w) => new(s) { Size = CommonDiagnosticsRules.TextSize, Color = c, Width = w, Shrink = 0f, FontFamily = "Cascadia Code", Trim = TextTrim.CharacterEllipsis };
            return new BoxEl
            {
                Direction = 0, Gap = Spacing.XS,
                Children =
                [
                    Cell(index, CommonDiagnosticsRules.HitIndexWidth), Cell(kind, CommonDiagnosticsRules.HitKindWidth),
                    new TextEl(node) { Size = CommonDiagnosticsRules.TextSize, Color = c, Grow = 1f, MinWidth = 0f, FontFamily = "Cascadia Code", Trim = TextTrim.CharacterEllipsis },
                    Cell(alpha, CommonDiagnosticsRules.HitAlphaWidth), Cell(f1, CommonDiagnosticsRules.HitAlphaWidth), Cell(f2, CommonDiagnosticsRules.HitAlphaWidth),
                    Cell(tile, CommonDiagnosticsRules.HitTileWidth), Cell(stale ? raster + "*" : raster, CommonDiagnosticsRules.HitRasterWidth),
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
            if (_host.Query(x, y) is not { } answer) return;
            _rows.Value = answer.Rows;
            _queried.Value = answer.Queried;
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
            _host.Export(_tag.Peek());
            _refresh.Value = _refresh.Peek() + 1;
        }
    }
}
