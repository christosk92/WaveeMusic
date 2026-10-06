// ── Shell/Stage.Host.cs ────────────────────────────────────────────────────────────────────────────────────────────
// Stage.Diagnostics — the structured log events (stage.enter/exit, viz.lease, viz.analysis, viz.pick) and the snapshot
// the Diagnostics page's "Fullscreen & visualizer" card reads
//
// Role: SHELL
// Owner: K
// Wave: 7
// Budget: 120 lines
// Spec: docs/plans/wavee/fullscreen-flagship-implementation.md §4.10b, §4.14

using FluentGpu.Signals;

namespace Wavee;

public static partial class Stage
{
    /// <summary>The Rail.NpvDiagnostics shape (Rail.cs:313-338): `Note…` writers that log and bump one version signal.</summary>
    public static class Diagnostics
    {
        public const string Category = "stage";
        public static readonly Signal<int> Version = new(0);
        public static bool IsOpen { get; private set; }
        public static Mode LastMode { get; private set; }
        public static Visualizer.Tier LastTier { get; private set; }
        public static Visualizer.Source LastSource { get; private set; }
        public static Visualizer.Kind LastKind { get; private set; }
        public static VizLayout LastLayout { get; private set; }
        public static long EnteredAtMs { get; private set; }

        static void Bump() => Version.Value = Version.Peek() + 1;

        public static void NoteEnter(string cause)
        {
            IsOpen = true; EnteredAtMs = Design.FrameTime.NowMs;
            Log.Event(WaveeLogLevel.Info, Category, "stage.enter", "fullscreen stage opened via " + cause,
                fields: [WaveeLogField.Of("cause", cause), WaveeLogField.Of("mode", LastMode.ToString()), WaveeLogField.Of("kind", LastKind.ToString())]);
            Bump();
        }

        public static void NoteExit(string cause)
        {
            IsOpen = false;
            long dwellMs = EnteredAtMs == 0 ? 0 : Design.FrameTime.NowMs - EnteredAtMs;
            Log.Event(WaveeLogLevel.Info, Category, "stage.exit", "fullscreen stage closed via " + cause, elapsedMs: dwellMs,
                fields: [WaveeLogField.Of("cause", cause), WaveeLogField.Of("mode", LastMode.ToString())]);
            Bump();
        }

        public static void NoteMode(Mode mode)
        {
            if (LastMode == mode) return;
            LastMode = mode;
            Log.Event(WaveeLogLevel.Debug, Category, "stage.mode", "stage mode " + mode, fields: [WaveeLogField.Of("mode", mode.ToString())]);
            Bump();
        }

        /// <summary>The EFFECTIVE layout changed (a pick, a [ / ] step, an artist without a header, Compact): gated like the mode.</summary>
        public static void NoteLayout(VizLayout layout)
        {
            if (LastLayout == layout) return;
            LastLayout = layout;
            Log.Event(WaveeLogLevel.Debug, Category, "stage.layout", "stage layout " + layout, fields: [WaveeLogField.Of("layout", layout.ToString())]);
            Bump();
        }

        public static void NotePick(Visualizer.Kind kind)
        {
            LastKind = kind;
            Log.Event(WaveeLogLevel.Info, Category, "viz.pick", "visualizer " + kind, fields: [WaveeLogField.Of("kind", kind.ToString())]);
            Bump();
        }

        public static void NoteLease(Visualizer.Tier tier)
        {
            if (LastTier == tier) return;
            LastTier = tier;
            Log.Event(WaveeLogLevel.Debug, Category, "viz.lease", "visualizer lease " + tier, fields: [WaveeLogField.Of("tier", tier.ToString())]);
            Bump();
        }

        public static void NoteSource(Visualizer.Source source)
        {
            if (LastSource == source) return;
            LastSource = source;
            Log.Event(WaveeLogLevel.Debug, Category, "viz.analysis", "visualizer data source " + source, fields: [WaveeLogField.Of("source", source.ToString())]);
            Bump();
        }
    }
}
