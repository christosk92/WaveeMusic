// ── Screens/Diagnostics.Capture.UI.cs — the "Realtime capture" Diagnostics page (unit 6) ───────────────────────────
//
// docs/plans/wavee/realtime-capture-implementation.md §5.5 (the wireframe), §8 unit 6. Routed exactly like
// RuntimePage/ConnectPage (Diagnostics.UI.cs's Install(), `Shell.RouteKind.CaptureDiagnostics`, Shell.cs): developer-
// only, no material tint. Reads Diagnostics/Capture.Recent.cs's pure classes over CaptureRecentStore's snapshot —
// NEVER the disk (§3.1's crash-safety story is a disk concern; the live view is a memory-only, header-only one,
// §5.5: "bodies stay opaque to the in-app view by design").
//
// ZERO COST WHEN OFF: this page (and its 750 ms poll) exists only while the route is mounted — the route itself IS
// the subscription, exactly like the log viewer's own tail (LogsPage.UI.cs's LogsPageView). CaptureRecentStore
// only ever grows while a writer is installed (RealtimeCaptureHost.Apply installs the tee on the 0→1 transition
// only, Diagnostics/Capture.Host.cs), so an idle store costs one empty-array snapshot per poll.
//
// MASTER-DETAIL, NO NAVIGATION: "View tree" expands a root'S causal tree IN PLACE, directly beneath its own row —
// the same re-skin library-rework-implementation.md's §0 table asks for, never a second route.
//
// THE SCROLL CARD rides at the foot of this page (Diagnostics.Scroll.cs): the engine scroll probe's level, the feel
// profile and the CSV export — independent of the capture toggle. THE TILES CARD rides right after it
// (Diagnostics.Tiles.cs): the always-on retained-tile census and the GPU pass timing toggle — also independent of
// the capture toggle.
//
// PROPS FREEZE AT MOUNT: the page has no props to freeze (a plain route), and every list here is rebuilt fresh each
// render off a `Signal`-gated poll — no child here holds a value that must survive a re-render unchanged.

using System.Globalization;
using FluentGpu;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;

namespace Wavee;

public static partial class Diagnostics
{
    /// <summary>The `capture-diagnostics` page.</summary>
    // MOUNT POINT (stage B contract) — registered by Install() (Diagnostics.UI.cs)
    public static Element CapturePage() => Embed.Comp(static () => new CapturePageView());

    /// <summary>"What did the user do, and everything that happened because of it" — read straight, no navigation.
    /// Re-renders on its own 750 ms poll (only while capture is actually on — off, the interval is disabled and the
    /// snapshot is a fixed empty array) and on the capture toggle's own signal.</summary>
    sealed class CapturePageView : Component
    {
        const int MaxRoots = 20;
        const int MaxAnomalies = 10;

        readonly Signal<int> _refresh = new(0);
        readonly Signal<long> _expandedRoot = new(-1);

        public override Element Render()
        {
            bool on = RealtimeCaptureHost.Enabled.Value;
            _ = _refresh.Value;
            _ = _expandedRoot.Value;

            // The live poll: enabled only while capture itself is on — off, nothing is being written to the store,
            // so polling would just repaint the same empty snapshot every 750 ms for no reason.
            UseInterval(() => _refresh.Value = _refresh.Peek() + 1, 750f, enabled: on);

            var snapshot = CaptureRecentStore.Instance.Snapshot();
            var roots = CaptureRootGrouping.RecentRoots(snapshot, MaxRoots);
            var anomalies = CaptureAnomalyScanner.Scan(snapshot);

            var body = new List<Element>(6)
            {
                StatusCard(on),
                AnomaliesCard(anomalies),
                RootsCard(roots, snapshot),
                FooterRow(),
                Caption(Loc.Get(Strings.Diagnostics.Capture.Caption)),
                // The developer page's other always-available instruments: the engine scroll probe (Diagnostics.Scroll.cs)
                // and the retained-tile census + GPU pass timing (Diagnostics.Tiles.cs).
                Embed.Comp(static () => new ScrollCardView()) with { Key = "scroll-card" },
                Embed.Comp(static () => new TileCardView()) with { Key = "tiles-card" },
                // The evidence card (Diagnostics.Evidence.cs): the stale-tile / exposed-missing / scratch invariants, the
                // pixel query over the latest composite, and the evidence bundle export.
                Embed.Comp(static () => new EvidenceCardView()) with { Key = "evidence-card" },
            };
            return PageFrame(Loc.Get(Strings.Nav.CaptureDiagnostics), "capture-diagnostics", body);
        }

        // ── the status line ──────────────────────────────────────────────────────────────────────────────────────

        static Element StatusCard(bool on) => Status(
            on ? Icons.StatusSuccess : Icons.StatusInfo,
            on ? Tok.SystemFillSuccess : Tok.TextTertiary,
            Loc.Get(on ? Strings.Diagnostics.Capture.Recording : Strings.Diagnostics.Capture.Off),
            Loc.Get(on ? Strings.Diagnostics.Capture.RecordingBody : Strings.Diagnostics.Capture.OffBody));

        // ── the Anomalies card (§5.5: capped at 10, newest first) ───────────────────────────────────────────────

        static Element AnomaliesCard(IReadOnlyList<CaptureAnomaly> anomalies)
        {
            string title = Loc.Get(Strings.Diagnostics.Capture.Anomalies);
            if (anomalies.Count == 0) return Card(title, Body(Loc.Get(Strings.Diagnostics.Capture.NoAnomalies)));

            int shown = Math.Min(anomalies.Count, MaxAnomalies);
            var rows = new List<Element>(shown * 2);
            for (int i = 0; i < shown; i++)
            {
                if (i > 0) rows.Add(Separator(4f));
                rows.Add(AnomalyRow(anomalies[i]));
            }
            return Card(title, rows);
        }

        static Element AnomalyRow(CaptureAnomaly a) => new BoxEl
        {
            Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center,
            Children =
            [
                InfoBadge.Dot(InfoBadgeSeverity.Critical),
                new TextEl(AnomalyLabel(a.Kind)) { Size = 12f, Weight = 600, Color = Tok.TextPrimary, Width = 120f, Shrink = 0f },
                new TextEl(a.Summary ?? "") { Size = 12f, Color = Tok.TextSecondary, Grow = 1f, MinWidth = 0f, Trim = TextTrim.CharacterEllipsis },
                new TextEl("#" + a.RootId.ToString(CultureInfo.InvariantCulture)) { Size = 11f, Color = Tok.TextTertiary, FontFamily = "Cascadia Code", Shrink = 0f },
            ],
        };

        static string AnomalyLabel(CaptureAnomalyKind kind) => Loc.Get(kind switch
        {
            CaptureAnomalyKind.EchoMissing => Strings.Diagnostics.Capture.EchoMissing,
            CaptureAnomalyKind.NonSuccessStatus => Strings.Diagnostics.Capture.NonSuccessStatus,
            CaptureAnomalyKind.DecodeFailed => Strings.Diagnostics.Capture.DecodeFailed,
            _ => Strings.Diagnostics.Capture.FrameIgnored,
        });

        // ── the recent causal roots ("last stories" — §5.5) ─────────────────────────────────────────────────────

        Element RootsCard(IReadOnlyList<CaptureRootSummary> roots, CaptureEvent[] snapshot)
        {
            string title = Loc.Get(Strings.Diagnostics.Capture.RecentRoots);
            if (roots.Count == 0) return Card(title, Body(Loc.Get(Strings.Diagnostics.Capture.NoRoots)));

            var rows = new List<Element>(roots.Count * 2);
            foreach (var r in roots)
            {
                if (rows.Count > 0) rows.Add(Separator(4f));
                rows.Add(RootRow(r));
                if (_expandedRoot.Value == r.RootId) rows.Add(TreeView(r.RootId, snapshot));
            }
            return Card(title, rows);
        }

        Element RootRow(CaptureRootSummary r)
        {
            bool expanded = _expandedRoot.Value == r.RootId;
            long rootId = r.RootId;
            string label = r.Root.Kind + (string.IsNullOrEmpty(r.Root.Fields.A) ? "" : "  " + r.Root.Fields.A);
            return new BoxEl
            {
                Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center,
                Children =
                [
                    RootDot(r.Status),
                    new TextEl(LogView.FormatTime(r.Root.UnixMs, LocalOffset)) { Size = 12f, Color = Tok.TextSecondary, FontFamily = "Cascadia Code", Width = 92f, Shrink = 0f },
                    new TextEl(label) { Size = 12f, Weight = 600, Color = Tok.TextPrimary, Grow = 1f, MinWidth = 0f, Trim = TextTrim.CharacterEllipsis },
                    new TextEl(Strings.Diagnostics.Capture.EffectCount(r.EffectCount)) { Size = 12f, Color = Tok.TextTertiary, Shrink = 0f },
                    HyperlinkButton.Create(Loc.Get(expanded ? Strings.Diagnostics.Capture.HideTree : Strings.Diagnostics.Capture.ViewTree),
                        () => _expandedRoot.Value = expanded ? -1 : rootId),
                ],
            };
        }

        static Element RootDot(CaptureRootStatus status) => InfoBadge.Dot(status switch
        {
            CaptureRootStatus.Red => InfoBadgeSeverity.Critical,
            CaptureRootStatus.Amber => InfoBadgeSeverity.Caution,
            _ => InfoBadgeSeverity.Success,
        });

        /// <summary>The expanded tree, indented under its own root row — input → decisions → requests → echoes →
        /// UI outcomes → errors, in causal + time order (§2.5). Header fields only; no payload is ever read here.</summary>
        static Element TreeView(long rootId, CaptureEvent[] snapshot)
        {
            var underRoot = new List<CaptureEvent>();
            foreach (var e in snapshot)
                if (e.RootId == rootId) underRoot.Add(e);

            var tree = CaptureTreeBuilder.Build(rootId, underRoot);
            if (tree.Count == 0) return new BoxEl
            {
                Padding = new Edges4(24f, 2f, 0f, 6f),
                Children = [Body(Loc.Get(Strings.Diagnostics.Capture.EmptyTree))],
            };

            var rows = new Element[tree.Count];
            for (int i = 0; i < tree.Count; i++) rows[i] = TreeRow(tree[i]);
            return new BoxEl { Direction = 1, Gap = 2f, Padding = new Edges4(24f, 2f, 0f, 8f), Children = rows };
        }

        static Element TreeRow(CaptureTreeRow row)
        {
            var e = row.Event;
            string suffix = e.Phase == CapturePhase.End ? " ✓" : "";
            string label = e.Kind + suffix + (string.IsNullOrEmpty(e.Fields.A) ? "" : "  " + e.Fields.A)
                + (string.IsNullOrEmpty(e.Fields.B) ? "" : " → " + e.Fields.B);
            return new BoxEl
            {
                Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, Margin = new Edges4(row.Depth * 16f, 0f, 0f, 0f),
                Children =
                [
                    new TextEl("└─") { Size = 12f, Color = Tok.TextTertiary, FontFamily = "Cascadia Code", Shrink = 0f },
                    new TextEl(LogView.FormatTime(e.UnixMs, LocalOffset)) { Size = 11f, Color = Tok.TextTertiary, FontFamily = "Cascadia Code", Width = 82f, Shrink = 0f },
                    new TextEl(label) { Size = 12f, Color = Tok.TextPrimary, Grow = 1f, MinWidth = 0f, Trim = TextTrim.CharacterEllipsis },
                ],
            };
        }

        // ── the footer: the two doors out (folder / re-poll) ────────────────────────────────────────────────────

        Element FooterRow() => new BoxEl
        {
            Direction = 0, Gap = Spacing.S,
            Children =
            [
                Button.Standard(Loc.Get(Strings.Settings.Privacy.Tools.OpenFolder),
                    static () => OpenFolder(Path.Combine(Platform.LogFolder, "capture"))),
                Button.Standard(Loc.Get(Strings.Settings.Diagnostics.Refresh), () => _refresh.Value = _refresh.Peek() + 1),
            ],
        };
    }
}
