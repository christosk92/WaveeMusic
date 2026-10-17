// ── Wavee.Tests/CommonDiagnosticsContractTests.cs — the Common diagnostics and logs pages' published keys and registration (themes plan 6.5, T11c) ──────
//
// The diagnostics pages (playback runtime, Connect, realtime capture) and the log viewer are Common blocks since T11c: the core builds the document (`Diagnostics.Pages.cs`) or
// holds the session model (`LogsPage.Host.cs`) and one Common view draws it for every theme. What is pure about the contract: the published ids (a published id never
// changes), Classic registering the blocks under the Common template ids with a complete registry, and the document records comparing by value where the keyed widgets
// below them rely on it (a widget node with the same texts and the same host is an equal props record, so an unchanged page render skips the widget). The pages'
// content is the existing core facts (`DiagnosticsCoreTests`, `TileDiagRulesTests`, `ScrollDiagRulesTests`, `LogsPageRulesTests`); the mounted facts are
// `Wavee.ShellTests\CommonDiagnosticsMountedTests.cs`.

using Wavee.Themes.Classic;
using Wavee.Theming.Contracts;
using Xunit;

namespace Wavee.Tests;

public class CommonDiagnosticsContractTests
{
    [Fact]
    public void The_published_ids_are_the_plans()
    {
        Assert.Equal("Pages.Diagnostics.Playback", ViewKeys.DiagnosticsPlayback.Id);
        Assert.Equal("Pages.Diagnostics.Connect", ViewKeys.DiagnosticsConnect.Id);
        Assert.Equal("Pages.Diagnostics.Capture", ViewKeys.DiagnosticsCapture.Id);
        Assert.Equal("Pages.Logs", ViewKeys.LogsPage.Id);
    }

    [Fact]
    public void Classic_registers_the_common_diagnostics_and_logs_blocks_under_the_common_template_ids()
    {
        var classic = ClassicTheme.Create();
        Assert.Equal("common.diagnostics.playback", classic.Registry.TemplateIdOf(ViewKeys.DiagnosticsPlayback.Id));
        Assert.Equal("common.diagnostics.connect", classic.Registry.TemplateIdOf(ViewKeys.DiagnosticsConnect.Id));
        Assert.Equal("common.diagnostics.capture", classic.Registry.TemplateIdOf(ViewKeys.DiagnosticsCapture.Id));
        Assert.Equal("common.logs.page", classic.Registry.TemplateIdOf(ViewKeys.LogsPage.Id));
        Assert.Empty(classic.Validate());
    }

    [Fact]
    public void A_row_is_narrow_unless_the_page_asks_for_the_wide_label_column()
    {
        Assert.False(new DiagRow("Outcome", "Ready").Wide);
        Assert.True(new DiagRow("State", "Us", Wide: true).Wide);
    }

    [Fact]
    public void A_button_does_nothing_until_it_is_given_a_verb_and_refresh_is_its_own_flag()
    {
        var plain = new DiagButton("Open");
        Assert.False(plain.Accent);
        Assert.Null(plain.Click);
        Assert.Null(plain.CopyText);
        Assert.False(plain.Refresh);
        Assert.True(new DiagButton("Refresh", Refresh: true).Refresh);
    }

    [Fact]
    public void A_widget_node_with_the_same_texts_and_host_is_an_equal_props_record()
    {
        var host = new TilesHost();
        Assert.Equal(new DiagTiles(host, 750f), new DiagTiles(host, 750f));
        Assert.NotEqual(new DiagTiles(host, 750f), new DiagTiles(new TilesHost(), 750f));
        Assert.Equal(new DiagToggle("GPU pass timing", true), new DiagToggle("GPU pass timing", true));
    }

    sealed class TilesHost : IDiagTilesHost
    {
        public bool HasHost => false;
        public bool PassTimingInitial => false;
        public DiagCard Read(bool passTimingOn) => new("Tiles", []);
        public void SetPassTiming(bool on) { }
    }
}
