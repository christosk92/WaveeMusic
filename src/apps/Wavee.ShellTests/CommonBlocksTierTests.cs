// ── Wavee.ShellTests/CommonBlocksTierTests.cs — the Common blocks at the plan's window sizes (themes plan 3.5, TC10) ─────────────────
//
// `Themes\Common\` holds the views every theme shares and no theme draws a board for them (owner Q18), so each block reads the CONTENT tier and the height tier and nothing else
// (`Themes\Common\Rules\Tiers.cs`; the pure records are `Wavee.Tests\CommonTiersTests`). What only a mounted tree can say, for EVERY block at 300x640 (the floor), 400x860 (the Compact
// board), 300x330 (the minimum height), 1440x960, 2560x1440 and 3840x2160, and at the page heights a 330-tall window leaves a Classic (210) and a Zune (126) page:
//
//   no page-level horizontal overflow   nothing laid out left of 0 or right of the window, except the content of a HORIZONTAL scroller (a block never needs one: the chip rail and
//                                       the Short toolbar are the only two, and they are viewports)
//   the record in use                   the tier the view read is the harness's, in the first frame and after more frames, and the geometry follows its record: the column stays
//                                       under its max width, the Customizer's rows are the record's height
//   Compact targets                     every button, link, check, switch, segmented item, chip and picker is at least 40 x 40
//   inside the page                     at 210 tall nothing is laid out below the window except the content of a vertical scroller; at 126 only the regions' order is asserted
//   one crossing                        a window resized across a tier re-lays-out the block in the new tier's record, with no overflow
//
// The harness mounts the view alone, so the content tier is the window's width (no nav pane) on the INSTALLED Classic ladder. The tests never name a Classic edge: they read the tier
// the harness published and assert the record for THAT tier, so they hold whichever band a width lands in.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Scene;
using FluentGpu.Signals;
using Wavee;
using Wavee.HomeUi;
using Wavee.Sdk;
using Wavee.Themes.Classic;
using Wavee.Themes.Common;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Xunit;

namespace Wavee.ShellTests;

public sealed class CommonBlocksTierTests(ShellHost host)
{
    // ── the sizes ─────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The plan's window sizes (3.5) the Common blocks are held to, then the page heights a 330-tall window leaves a Classic (210) and a Zune (126) page.</summary>
    static readonly (float W, float H)[] Sizes =
    [
        (300f, 640f), (400f, 860f), (300f, 330f), (1440f, 960f), (2560f, 1440f), (3840f, 2160f), (300f, 210f), (300f, 126f),
    ];

    static ViewHarnessOptions At(float w, float h) => new() { Composition = ClassicTheme.Create(), WidthDip = w, HeightDip = h };

    delegate void MountAt(ShellHost host, ViewHarnessOptions options, Action<ViewHarness> body);

    static readonly Dictionary<string, MountAt> Blocks = new()
    {
        ["diagnostics"] = (h, o, body) => ViewHarness.With(h, ViewKeys.DiagnosticsCapture, DiagnosticsPage(), body, o),
        ["logs"] = (h, o, body) => ViewHarness.With(h, ViewKeys.LogsPage, new LogsPageProps { Host = new LogsFixture(LogEntries()) }, body, o),
        ["history"] = (h, o, body) => ViewHarness.With(h, ViewKeys.HistoryPage, HistoryPage(), body, o),
        ["customize"] = (h, o, body) => ViewHarness.With(h, ViewKeys.HomeCustomize, CustomizePage(), body, o),
        ["module"] = (h, o, body) => ViewHarness.With(h, ViewKeys.ModulePage, ModulePage(), body, o),
        ["notfound"] = (h, o, body) => ViewHarness.With(h, ViewKeys.ShellContentState, ContentStateProps.ForNotFound("test:missing", "", static () => { }), body, o),
        ["fps"] = (h, o, body) => ViewHarness.With(h, ViewKeys.ShellFps, new FpsProps { Shown = static () => true }, body, o),
    };

    public static TheoryData<string, float, float> Plan()
    {
        var data = new TheoryData<string, float, float>();
        foreach (var block in Blocks.Keys)
            foreach (var (w, h) in Sizes)
                data.Add(block, w, h);
        return data;
    }

    // ── the claim, per block per size ─────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(Plan))]
    public void A_block_fits_its_window_in_the_tier_it_reads(string block, float width, float height)
        => Blocks[block](host, At(width, height), harness =>
        {
            var first = harness.Facts.Peek().Tiers;
            harness.RunFrames(40);
            var tiers = harness.Facts.Peek().Tiers;
            Assert.Equal(first.Content, tiers.Content);   // the tier is the first frame's, and it does not change in the frames after

            AssertNoHorizontalOverflow(harness, width, block);
            AssertRecordInUse(block, harness, tiers);
            if (tiers.Content == WidthTier.Compact) AssertTargets(harness, block);
            if (height >= PageHeightFloor) AssertInsideThePage(harness, height, block);
            AssertRegionsDoNotOverlap(harness, block);
        });

    /// <summary>The smallest page height at which a block must lay nothing out below the window (a Classic page at 330 tall; a Zune page at 126 keeps only the regions' order).</summary>
    const float PageHeightFloor = 210f;

    // ── one crossing ──────────────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("history")]
    [InlineData("logs")]
    [InlineData("customize")]
    [InlineData("diagnostics")]
    [InlineData("module")]
    public void A_window_resized_across_a_tier_re_lays_the_block_out_in_the_new_tiers_record(string block)
        => Blocks[block](host, At(400f, 860f), harness =>
        {
            harness.RunFrames(20);
            var narrow = harness.Facts.Peek().Tiers;
            AssertNoHorizontalOverflow(harness, 400f, block);

            harness.Resize(1440f, 960f);
            harness.RunFrames(30);
            var wide = harness.Facts.Peek().Tiers;
            Assert.NotEqual(narrow.Content, wide.Content);
            AssertNoHorizontalOverflow(harness, 1440f, block);
            AssertRecordInUse(block, harness, wide);

            harness.Resize(400f, 860f);
            harness.RunFrames(30);
            Assert.Equal(narrow.Content, harness.Facts.Peek().Tiers.Content);
            AssertNoHorizontalOverflow(harness, 400f, block);
            AssertRecordInUse(block, harness, harness.Facts.Peek().Tiers);
            if (block != "module") AssertTargets(harness, block);   // (the module fixture's cards and shelf rows are engine surfaces: asserted by the plan sizes)
        });

    // ── the record in use ─────────────────────────────────────────────────────────────────────────────────────────────────

    static void AssertRecordInUse(string block, ViewHarness harness, TierFacts tiers)
    {
        var scene = harness.Shell.Host.Scene;
        switch (block)
        {
            case "diagnostics":
            {
                var layout = CommonDiagnosticsLayout.For(tiers);
                var scroll = ShellProbe.ScrollUnder(harness.Shell, scene.Root);
                Assert.False(scroll.IsNull, "the page's one scroll");
                var column = scene.FirstChild(scene.FirstChild(scroll));   // the scroll's content is a row holding the one capped column
                Assert.True(scene.AbsoluteRect(column).W <= layout.MaxWidth + 0.5f, "the column stops at the record's max width: " + Chain(harness, column));
                break;
            }
            case "logs":
            {
                var layout = CommonLogsLayout.For(tiers);
                Assert.True(scene.AbsoluteRect(harness.Node("logs-column")).W <= layout.MaxWidth + 0.5f, "the column stops at the record's max width");
                var header = harness.Node("logs-header");
                var title = scene.AbsoluteRect(scene.FirstChild(header));
                Assert.Equal(layout.Gutter, title.X, 0.5f);   // the head's side gutter is the record's
                Assert.Equal(layout.StackHeader && !layout.BareHeader, harness.Contains("logs:cluster"));   // the picker cluster under the title, or beside it
                Assert.Equal(layout.ShowCrumbs, CrumbsStand(harness));
                var first = harness.FindPrefix("logs:row:");
                Assert.NotEmpty(first);
                float rowHeight = scene.AbsoluteRect(scene.FirstChild(first[0].Node)).H;
                Assert.True(rowHeight >= (layout.TwoLineRows ? layout.Target : 0f) - 0.5f, "a row is at least the record's height: " + rowHeight);
                break;
            }
            case "history":
            {
                var layout = CommonHistoryLayout.For(tiers);
                var scroll = ShellProbe.ScrollUnder(harness.Shell, scene.Root);
                Assert.False(scroll.IsNull, "the page's one scroll");
                Assert.True(scene.AbsoluteRect(scene.FirstChild(scroll)).W <= layout.MaxWidth + 0.5f, "the column stops at the record's max width");
                // Short and Compact: the head scrolls with the list, so the scroll's content holds the head and the list; otherwise only the list.
                Assert.Equal(layout.HeaderScrolls ? 2 : 1, ChildCount(scene, scene.FirstChild(scroll)));
                break;
            }
            case "customize":
            {
                var layout = CommonCustomizeLayout.For(tiers);
                Assert.True(scene.AbsoluteRect(scene.FirstChild(harness.Node("home-customize"))).W <= layout.MaxWidth + 0.5f, "the column stops at the record's max width");
                foreach (var entry in CustomizeDoc.Zones)
                    Assert.Equal(layout.RowHeight, scene.AbsoluteRect(harness.Node(entry.Kind)).H, 0.5f);
                if (layout.Scrolls) Assert.False(ShellProbe.ScrollUnder(harness.Shell, scene.Root).IsNull, "the head and the list scroll as one page");
                break;
            }
            case "module":
            {
                var layout = CommonModuleLayout.For(tiers);
                var scroll = ShellProbe.ScrollUnder(harness.Shell, scene.Root);
                Assert.False(scroll.IsNull, "the page's one scroll");
                var content = scene.FirstChild(scroll);
                var column = scene.FirstChild(content);
                Assert.True(scene.AbsoluteRect(column).W <= layout.MaxWidth + 0.5f, "the column stops at the record's max width");
                Assert.Equal(layout.Gutter, scene.AbsoluteRect(column).X, 0.5f);
                break;
            }
            case "notfound":
            {
                var page = harness.Node("test:missing");
                var rect = scene.AbsoluteRect(page);
                Assert.Equal(3, ChildCount(scene, page));   // the glyph, the line, the action
                for (var child = scene.FirstChild(page); !child.IsNull; child = scene.NextSibling(child))
                {
                    var r = scene.AbsoluteRect(child);
                    Assert.Equal(rect.X + rect.W / 2f, r.X + r.W / 2f, 1f);   // centred across the page
                }
                break;
            }
            case "fps":
                Assert.True(harness.Subtree(scene.Root).Count() > 1, "the pill mounts");
                break;
        }
    }

    static int ChildCount(SceneStore scene, NodeHandle node)
    {
        int count = 0;
        for (var child = scene.FirstChild(node); !child.IsNull; child = scene.NextSibling(child)) count++;
        return count;
    }

    static bool CrumbsStand(ViewHarness harness)
    {
        // The crumbs are the title lane's first child: a lane with two children has them.
        var scene = harness.Shell.Host.Scene;
        return ChildCount(scene, harness.Node("logs:title")) > 1;
    }

    // ── the shared assertions ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Every live node under the root, skipping the children of a horizontal scroller (its content is meant to run past) and, when asked, of a vertical one.</summary>
    static IEnumerable<(NodeHandle Node, RectF Rect)> Walk(ViewHarness harness, bool intoVerticalScrollers)
    {
        var scene = harness.Shell.Host.Scene;
        var stack = new Stack<NodeHandle>();
        stack.Push(scene.Root);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (node.IsNull || !scene.IsLive(node)) continue;
            yield return (node, scene.AbsoluteRect(node));
            if (scene.TryGetScroll(node, out var scroll) && (scroll.Orientation == 1 || !intoVerticalScrollers)) continue;
            for (var child = scene.FirstChild(node); !child.IsNull; child = scene.NextSibling(child)) stack.Push(child);
        }
    }

    static void AssertNoHorizontalOverflow(ViewHarness harness, float width, string block)
    {
        var offenders = Walk(harness, intoVerticalScrollers: true)
            .Where(n => n.Rect.W > 0.5f && (n.Rect.X < -0.5f || n.Rect.X + n.Rect.W > width + 0.5f))
            .Select(n => Describe(harness, n.Node, n.Rect))
            .Take(8)
            .ToList();
        Assert.True(offenders.Count == 0, block + " at " + width + " wide has nodes outside the window: " + string.Join("; ", offenders)
            + " [" + Chain(harness, ShellProbe.ScrollUnder(harness.Shell, harness.Shell.Host.Scene.Root)) + "]");
    }

    static void AssertInsideThePage(ViewHarness harness, float height, string block)
    {
        var offenders = Walk(harness, intoVerticalScrollers: false)
            .Where(n => n.Rect.H > 0.5f && n.Rect.Y + n.Rect.H > height + 0.5f)
            .Select(n => Describe(harness, n.Node, n.Rect))
            .Take(8)
            .ToList();
        Assert.True(offenders.Count == 0, block + " at " + height + " tall lays out below the page: " + string.Join("; ", offenders));
    }

    static readonly AutomationRole[] TargetRoles =
    [
        AutomationRole.Button, AutomationRole.ToggleButton, AutomationRole.CheckBox, AutomationRole.RadioButton, AutomationRole.Hyperlink,
        AutomationRole.ToggleSwitch, AutomationRole.ComboBox, AutomationRole.Tab,
    ];

    /// <summary>A control's own small part inside a text field (the suggest box's 30 x 22 query and clear glyph buttons): the FIELD is the target, and it is 40 tall.</summary>
    static bool IsFieldPart(SceneStore scene, ViewHarness harness, NodeHandle node)
    {
        var up = node;
        for (int i = 0; i < 4; i++)
        {
            up = scene.Parent(up);
            if (up.IsNull) return false;
            if (harness.Subtree(up).Any(n => n != node && scene.Interaction(n).Role == AutomationRole.Text)) return true;
        }
        return false;
    }

    /// <summary>Compact: every control that can be pressed is at least 40 x 40 (the brief's rule). The glyph buttons inside a text field are the field's parts.</summary>
    static void AssertTargets(ViewHarness harness, string block)
    {
        var scene = harness.Shell.Host.Scene;
        var offenders = Walk(harness, intoVerticalScrollers: true)
            .Where(n => TargetRoles.Contains(scene.Interaction(n.Node).Role) && n.Rect.W > 0.5f && (n.Rect.W < 39.5f || n.Rect.H < 39.5f)
                && !(n.Rect.H <= 24.5f && n.Rect.W <= 32.5f && IsFieldPart(scene, harness, n.Node)))
            .Select(n => Describe(harness, n.Node, n.Rect))
            .Take(8)
            .ToList();
        Assert.True(offenders.Count == 0, block + " at Compact has targets under 40 x 40: " + string.Join("; ", offenders));
    }

    /// <summary>The top-level regions that carry keys never overlap vertically.</summary>
    static void AssertRegionsDoNotOverlap(ViewHarness harness, string block)
    {
        if (block != "logs") return;
        var scene = harness.Shell.Host.Scene;
        var regions = new[] { "logs-header", "logs-toolbar" }.Where(harness.Contains).Select(k => (Key: k, Rect: scene.AbsoluteRect(harness.Node(k)))).OrderBy(r => r.Rect.Y).ToArray();
        for (int i = 1; i < regions.Length; i++)
            Assert.True(regions[i].Rect.Y >= regions[i - 1].Rect.Y + regions[i - 1].Rect.H - 0.5f, regions[i - 1].Key + " and " + regions[i].Key + " overlap");
    }

    static string Describe(ViewHarness harness, NodeHandle node, RectF r)
    {
        var scene = harness.Shell.Host.Scene;
        string within = "";
        var up = node;
        for (int i = 0; i < 12 && within.Length == 0; i++)
        {
            up = scene.Parent(up);
            if (up.IsNull) break;
            within = scene.DebugKeyOf(up) ?? "";
        }
        return scene.Interaction(node).Role + "@" + r.X.ToString("0.#") + "," + r.Y.ToString("0.#") + " " + r.W.ToString("0.#") + "x" + r.H.ToString("0.#")
            + (within.Length > 0 ? " in " + within : "")
            + " ^" + string.Join(",", Enumerable.Range(0, 5).Aggregate((Parent: node, Widths: new List<string>()), (acc, _) =>
            {
                var p = acc.Parent.IsNull ? acc.Parent : scene.Parent(acc.Parent);
                if (!p.IsNull) acc.Widths.Add(scene.AbsoluteRect(p).W.ToString("0"));
                return (p, acc.Widths);
            }).Widths);
    }

    /// <summary>The node, then its first descendants down a few levels, as widths (a failure's evidence).</summary>
    static string Chain(ViewHarness harness, NodeHandle node)
    {
        var scene = harness.Shell.Host.Scene;
        var parts = new List<string>();
        for (int i = 0; i < 4 && !node.IsNull; i++)
        {
            parts.Add((scene.DebugKeyOf(node) ?? "-") + " " + scene.AbsoluteRect(node).W.ToString("0.#"));
            node = scene.FirstChild(node);
        }
        return string.Join(" > ", parts);
    }

    // ── fixtures ──────────────────────────────────────────────────────────────────────────────────────────────────────────

    // history

    static HistoryRowFacts HistoryRow(string title, bool reachable, int visits = 0)
        => new(title, Icons.Clock, "Page", "An argument long enough to need an ellipsis on a narrow window", "Yesterday 18:32", visits, false, reachable, static () => { }, static () => { });

    static HistoryPageProps HistoryPage()
    {
        var rows = new List<HistoryRowFacts>();
        for (int i = 0; i < 12; i++) rows.Add(HistoryRow("A visited page with a rather long title number " + i, reachable: i != 3, visits: i % 4));
        var model = new HistoryPageModel
        {
            TotalVisits = 4821, UniqueRoutes = 312, Body = HistoryBodyKind.DateGroups,
            Sections = [new HistorySection("Today", "12 visits", rows.ToArray()), new HistorySection("Yesterday", "3 visits", rows.Take(3).ToArray())],
        };
        return new HistoryPageProps { Version = new Signal<int>(0), Project = (_, _, _) => model };
    }

    // the Home customizer

    static readonly LayoutDoc CustomizeDoc = LayoutFile.DefaultDoc();

    static HomeCustomizeProps CustomizePage() => new()
    {
        Doc = CustomizeDoc,
        Reorder = new Reorderable("wavee.test-common-tiers-zone-layout") { Spacing = 0f, RequireDropOnList = true },
        Toggle = static _ => { },
        Move = static (_, _) => { },
        Reset = static () => { },
    };

    // the module page

    static ModulePageProps ModulePage()
    {
        var items = new[]
        {
            new PageItem("A related entity with a fairly long title", "A subtitle", null, null, "e1", null, null, false, null),
            new PageItem("Another related entity", "Subtitle", null, null, "e2", null, null, false, null),
        };
        var playables = new[]
        {
            new PageItem("A playable with a long title that has to be trimmed", "Subtitle", null, "p1", null, null, null, false, "12:34"),
            new PageItem("A live playable", null, null, "p2", null, null, null, true, null),
        };
        var links = new[]
        {
            new PageItem("The website", "example.invalid", null, null, null, "https://example.invalid/", null, false, null),
            new PageItem("Another", null, null, null, null, "https://example.invalid/b", null, false, null),
        };
        var doc = new ModulePageDoc(ModulePageDoc.CurrentVersion, ModulePageDoc.TemplateEntity,
            new PageHero("A module page whose title is long enough to wrap on a narrow window", "Eyebrow", "Subtitle", null, "A meta line", false),
            [
                new PageAction("open", PageAction.KindOpenUrl, "Open in the browser", null, "https://example.invalid/", true),
                new PageAction("more", PageAction.KindOpenUrl, "Another action", null, "https://example.invalid/more", false),
            ],
            [
                new PageSection(PageSection.KindText, "About", new string('x', 40) + " " + string.Join(" ", Enumerable.Repeat("prose that is long enough to need the More toggle", 12)), null, null),
                new PageSection(PageSection.KindFacts, "Facts", null, [["Subscribers", "1.2M"], ["Videos", "340"], ["Joined", "2011"]], null),
                new PageSection(PageSection.KindPlayables, "Videos", null, null, playables),
                new PageSection(PageSection.KindCards, "Related", null, null, items),
                new PageSection(PageSection.KindLinks, "Links", null, null, links),
            ], null);
        return new ModulePageProps
        {
            PageUri = "fixture", ModuleId = "fixture-module", Parsed = true, RouteArg = "Fixture", ScrollKey = "test:common-tiers:module",
            Loadable = Loadable<ModulePageDoc>.Ready(doc), Retry = static () => { },
        };
    }

    // the log viewer

    sealed class LogsFixture(WaveeLogEntry[]? entries) : ILogsHost
    {
        public Signal<int> Session { get; } = new(0);
        public IReadSignal<int> Refresh { get; } = new Signal<int>(0);
        public IReadSignal<int> SessionsRev { get; } = new Signal<int>(0);
        public void Bump() { }
        public long LogVersion => 0;
        public void RefreshSessions(Action<Action> post, bool force = false) { }
        public void EnsureSessionLoaded(Action<Action> post) { }
        public string[] SessionLabels() => ["This session (live)", "Yesterday 18:32", "Monday 09:10"];
        public string? PickerCaption() => "Reading the log folder";
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
        public (string Ring, string File) CaptureLevels() => ("Info", "Information and above");
    }

    static WaveeLogEntry[] LogEntries()
    {
        var entries = new WaveeLogEntry[24];
        for (int i = 0; i < entries.Length; i++)
            entries[i] = new WaveeLogEntry(i + 1, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + i, i % 7 == 0 ? WaveeLogLevel.Warning : WaveeLogLevel.Info,
                "playback.engine", "ev", "A log message long enough that it has to be trimmed on a narrow window " + i, null, 1, -1, null, null);
        return entries;
    }

    // the diagnostics pages

    sealed class WireFixture : IDiagWireHost
    {
        public bool Live => true;
        public (string ShowUri, string EpisodeUri) Prefill() => ("", "");
        public Task<IReadOnlyList<DiagWireRow>> Run(string showUri, string episodeUri, bool saveCaptures) => Task.FromResult<IReadOnlyList<DiagWireRow>>([]);
        public void Saved() { }
    }

    sealed class ScrollFixture : IDiagScrollHost
    {
        public Signal<int> Level { get; } = new(1);
        public Signal<int> Profile { get; } = new(0);
        public IReadOnlyList<string> ProfileNames { get; } = ["Standard", "Gentle", "Snappy"];
        public void SetLevel(int level) => Level.Value = level;
        public void SetProfile(int index) => Profile.Value = index;
        public void Export(float deviceScale) { }
    }

    sealed class TilesFixture : IDiagTilesHost
    {
        public bool HasHost => true;
        public bool PassTimingInitial => false;
        public DiagCard Read(bool passTimingOn) => new("Tiles",
        [
            new DiagStatus(Icons.StatusSuccess, DiagTone.Success, "Healthy", "No tile problems"),
            new DiagRow("Slices", "4"),
            new DiagToggle("GPU pass timing", Enabled: true),
            new DiagPassRow("Composite", "tile-target", "1.2 ms"),
        ]);
        public void SetPassTiming(bool on) { }
    }

    sealed class EvidenceFixture : IDiagEvidenceHost
    {
        public DiagEvidenceView Read() => new(true, "stale tiles 0 · exposed missing 0 · scratch refused 0", false, "—");
        public DiagEvidenceAnswer? Query(float x, float y) => null;
        public void Export(string tag) { }
    }

    static DiagnosticsPageProps DiagnosticsPage() => new()
    {
        Glyph = Icons.Document, Title = "Playback diagnostics", ScrollKey = "test:common-tiers:diagnostics", PollMs = 750f,
        Build = static expanded =>
        [
            new DiagStatus(Icons.StatusInfo, DiagTone.Muted, "Realtime capture is off", "Turn it on to record the next few seconds of input, decisions and requests."),
            new DiagCard("Runtime",
            [
                new DiagRow("Audio device", "Speakers (Realtek High Definition Audio) at 48000 Hz, shared mode, 10 ms"),
                new DiagRow("Connect", "A long value that has to wrap on a narrow window because it is a sentence", Wide: true),
                new DiagNamed("Modules", [new DiagChip("playback", true), new DiagChip("lyrics", false), new DiagChip("video", true)]),
                new DiagSeparator(DiagGap.Normal),
                new DiagButtons([new DiagButton("Refresh", Refresh: true), new DiagButton("Copy report", Accent: true), new DiagButton("Open the log folder")]),
                new DiagLink("Open the diagnostics folder", static () => { }, "Where the reports are written"),
            ]),
            new DiagCard("Anomalies", [new DiagAnomaly("late-echo", "The echo for request 42 arrived after the deadline of 800 ms", "r-123")]),
            new DiagCard("Recent roots",
            [
                new DiagRoot(7, DiagTone.Success, "12:34:56.789", "Play 'A track with a long name'", "9 effects", Expanded: true, "Hide",
                [
                    new DiagTreeRow(0, "+0 ms", "input: play"), new DiagTreeRow(1, "+2 ms", "decision: route to the playback core"),
                    new DiagTreeRow(2, "+9 ms", "request: resolve the track"), new DiagTreeRow(3, "+40 ms", "echo: state changed"),
                    new DiagTreeRow(4, "+41 ms", "outcome: the bar shows the track"), new DiagTreeRow(5, "+42 ms", "error: none"),
                ], "No tree"),
            ]),
            new DiagHealth(static () => new DiagCard("Playback health", [new DiagRow("Underruns", "0"), new DiagRow("Decode", "—")]), 1000f),
            new DiagWireCheck("Podcast wire check", "Fires the pathfinder ops once each.", "Show uri", "Episode uri", "Save captures", "Run", "Running…",
                "Operation", "Status", "Bytes", "Root", new WireFixture()),
            new DiagScroll("Scroll", "Probe level", ["Off", "Summary", "Trace"], "Summary records each frame's offset.", "Profile", "Export CSV", "Caption", new ScrollFixture()),
            new DiagTiles(new TilesFixture(), 750f),
            new DiagEvidence("Evidence", "Healthy", "Warning", "Pixel", "(DIP)", "Query", "Pick with click", "Picking…", "Export bundle", "Exporting…", "Tag", "Last", "Caption",
                750f, new EvidenceFixture()),
        ],
    };
}
