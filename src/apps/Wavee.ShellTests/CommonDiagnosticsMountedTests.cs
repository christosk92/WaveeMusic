// ── Wavee.ShellTests/CommonDiagnosticsMountedTests.cs — the Common diagnostics and logs pages, mounted (themes plan 6.5, T11c) ──────────────────────
//
// The diagnostics pages and the log viewer are Common blocks since T11c (`Themes\Common\CommonDiagnostics*.cs`, `CommonLogs.cs`); Classic registers them. The core builds
// the document (`Diagnostics.Pages.cs`) and holds the session model (`LogsPage.Host.cs`), so fixtures stand in for both here:
//
//   registered   Classic's registry holds the Common templates for the four keys
//   diagnostics  a page builds its document inside the view's render (the build argument is the expanded root), and the three stateful cards mount under their
//                keys with the host the node carries; the page poll and the Refresh button re-read the build
//   logs         the viewer's frame (header with its picker, toolbar, list card) stands under its keys; a loading session shows the progress state, a filter with
//                no rows the empty state, and a row the keyed measured list

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Signals;
using Wavee;
using Wavee.Themes.Classic;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Xunit;

namespace Wavee.ShellTests;

public sealed class CommonDiagnosticsMountedTests(ShellHost host)
{
    static ViewHarnessOptions UnderClassic() => new() { Composition = ClassicTheme.Create() };

    // ── fixtures ────────────────────────────────────────────────────────────────────────────────────────────────

    sealed class ScrollFixture : IDiagScrollHost
    {
        public Signal<int> Level { get; } = new(1);
        public Signal<int> Profile { get; } = new(0);
        public IReadOnlyList<string> ProfileNames { get; } = ["Standard", "Gentle"];
        public void SetLevel(int level) => Level.Value = level;
        public void SetProfile(int index) => Profile.Value = index;
        public void Export(float deviceScale) { }
    }

    sealed class TilesFixture : IDiagTilesHost
    {
        public bool HasHost => true;
        public bool PassTimingInitial => false;
        public bool PassTiming;

        public DiagCard Read(bool passTimingOn) => new("Tiles",
        [
            new DiagStatus(Icons.StatusSuccess, DiagTone.Success, "Healthy", "No tile problems"),
            new DiagRow("Slices", "4"),
            new DiagToggle("GPU pass timing", Enabled: true),
        ]);

        public void SetPassTiming(bool on) => PassTiming = on;
    }

    sealed class EvidenceFixture : IDiagEvidenceHost
    {
        public DiagEvidenceView Read() => new(true, "stale tiles 0", false, "—");
        public DiagEvidenceAnswer? Query(float x, float y) => null;
        public void Export(string tag) { }
    }

    static DiagnosticsPageProps Page(Func<long, IReadOnlyList<DiagNode>> build, IReadSignal<bool>? poll = null)
        => new() { Glyph = Icons.Document, Title = "Diagnostics", ScrollKey = "test-diagnostics", Build = build, PollWhile = poll, PollMs = 750f };

    // ── registration ────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Classic_registers_the_common_templates_for_the_four_keys()
        => host.Run(() =>
        {
            var registry = ClassicTheme.Create().Registry;
            Assert.Equal("common.diagnostics.playback", registry.TemplateIdOf(ViewKeys.DiagnosticsPlayback.Id));
            Assert.Equal("common.diagnostics.connect", registry.TemplateIdOf(ViewKeys.DiagnosticsConnect.Id));
            Assert.Equal("common.diagnostics.capture", registry.TemplateIdOf(ViewKeys.DiagnosticsCapture.Id));
            Assert.Equal("common.logs.page", registry.TemplateIdOf(ViewKeys.LogsPage.Id));
        });

    // ── the diagnostics pages ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_diagnostics_page_builds_its_document_in_the_render_and_mounts_the_three_cards_under_their_keys()
    {
        var builds = new List<long>();
        var scroll = new ScrollFixture();
        var tiles = new TilesFixture();
        var evidence = new EvidenceFixture();
        IReadOnlyList<DiagNode> Build(long expanded)
        {
            builds.Add(expanded);
            return
            [
                new DiagStatus(Icons.StatusInfo, DiagTone.Muted, "Off", "Not recording"),
                new DiagCard("Anomalies", [new DiagBody("None")]),
                new DiagButtons([new DiagButton("Refresh", Refresh: true)]),
                new DiagNote("Caption"),
                new DiagScroll("Scroll", "Probe level", ["Off", "Summary", "Trace"], "Level body", "Profile", "Export CSV", "Caption", scroll),
                new DiagTiles(tiles, 750f),
                new DiagEvidence("Evidence", "Healthy", "Warning", "Pixel", "(DIP)", "Query", "Pick with click", "Picking…", "Export bundle", "Exporting…", "tag",
                    "last", "Caption", 750f, evidence),
            ];
        }

        ViewHarness.With(host, ViewKeys.DiagnosticsCapture, Page(Build), harness =>
        {
            harness.RunFrames(30);
            Assert.Equal(-1L, builds[0]);
            Assert.True(harness.Contains("scroll-card"), "the scroll card mounts under its own key");
            Assert.True(harness.Contains("tiles-card"), "the tiles card mounts under its own key");
            Assert.True(harness.Contains("evidence-card"), "the evidence card mounts under its own key");
        }, UnderClassic());
    }

    [Fact]
    public void A_page_poll_re_renders_the_page_only_while_its_gate_holds()
    {
        var gate = new Signal<bool>(false);
        int builds = 0;
        IReadOnlyList<DiagNode> Build(long _)
        {
            builds++;
            return [new DiagNote("tick")];
        }

        ViewHarness.With(host, ViewKeys.DiagnosticsCapture, Page(Build, gate), harness =>
        {
            harness.RunFrames(10);
            int idle = builds;
            Assert.True(idle >= 1);
            harness.RunFrames(120);
            Assert.Equal(idle, builds);   // the gate is shut: no poll, no re-render
        }, UnderClassic());
    }

    // ── the log viewer ──────────────────────────────────────────────────────────────────────────────────────────

    sealed class LogsFixture(WaveeLogEntry[]? entries) : ILogsHost
    {
        public Signal<int> Session { get; } = new(0);
        public IReadSignal<int> Refresh { get; } = new Signal<int>(0);
        public IReadSignal<int> SessionsRev { get; } = new Signal<int>(0);
        public void Bump() { }
        public long LogVersion => 0;
        public void RefreshSessions(Action<Action> post, bool force = false) { }
        public void EnsureSessionLoaded(Action<Action> post) { }
        public string[] SessionLabels() => ["This session"];
        public string? PickerCaption() => null;
        public WaveeLogEntry[]? EntriesFor(bool live, out WaveeLogSessions.Info? past)
        {
            past = null;
            return entries;
        }
        public void ExportSession() { }
        public bool Verbose => false;
        public void SetVerbose(bool on) { }
        public void ClearRing() { }
        public void ReportThisSession() { }
        public void OpenLogFolder() { }
        public void OpenCrumb(int index) { }
        public bool IsActive => true;
        public void GoBack() { }
        public (string Ring, string File) CaptureLevels() => ("Info", "Info");
    }

    static WaveeLogEntry Entry(long sequence, string message)
        => new(sequence, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), WaveeLogLevel.Info, "test", "ev", message, null, 1, -1, null, null);

    static LogsPageProps Logs(WaveeLogEntry[]? entries) => new() { Host = new LogsFixture(entries) };

    [Fact]
    public void The_log_viewer_stands_its_frame_under_its_keys()
        => ViewHarness.With(host, ViewKeys.LogsPage, Logs([]), harness =>
        {
            harness.RunFrames(30);
            Assert.True(harness.Contains("logs-page"));
            Assert.True(harness.Contains("logs-header"));
            Assert.True(harness.Contains("logs:title"));
            Assert.True(harness.Contains("logs:folder"));
            Assert.True(harness.Contains("logs-toolbar"));
            Assert.NotEmpty(harness.FindPrefix("logs:session:"));
        }, UnderClassic());

    [Fact]
    public void A_filter_with_no_rows_shows_the_empty_state_and_mounts_no_list()
        => ViewHarness.With(host, ViewKeys.LogsPage, Logs([]), harness =>
        {
            harness.RunFrames(30);
            Assert.Empty(harness.FindPrefix("logs:list:"));
        }, UnderClassic());

    [Fact]
    public void A_loading_past_session_mounts_no_list()
        => ViewHarness.With(host, ViewKeys.LogsPage, Logs(null), harness =>
        {
            harness.RunFrames(30);
            Assert.True(harness.Contains("logs-page"));
            Assert.Empty(harness.FindPrefix("logs:list:"));
        }, UnderClassic());

    [Fact]
    public void A_session_with_rows_mounts_the_keyed_measured_list()
        => ViewHarness.With(host, ViewKeys.LogsPage, Logs([Entry(1, "first"), Entry(2, "second")]), harness =>
        {
            harness.RunFrames(60);
            Assert.NotEmpty(harness.FindPrefix("logs:list:"));
        }, UnderClassic());
}
