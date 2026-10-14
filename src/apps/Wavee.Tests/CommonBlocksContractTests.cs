// ── Wavee.Tests/CommonBlocksContractTests.cs — the Common blocks' and the dialog bodies' published keys (themes plan 6.0 / 6.5, T11c) ────────────
//
// T11c writes the utility pages every theme shares into `Themes\Common\` (history, the Home customizer, the frame-rate overlay, diagnostics, logs) and gives
// Classic the feedback and crash dialogs' bodies. What is pure about the contract: the published ids (an id never changes), Classic registering each key
// under its own template id with a complete registry (the Common blocks arrive through one call, so each id names its block), and the history model's
// empty values being safe to read.

using Wavee.Themes.Classic;
using Wavee.Theming.Contracts;
using Xunit;

namespace Wavee.Tests;

public class CommonBlocksContractTests
{
    [Fact]
    public void The_published_ids_are_the_plans()
    {
        Assert.Equal("Pages.History", ViewKeys.HistoryPage.Id);
        Assert.Equal("Dialog.Feedback", ViewKeys.DialogFeedback.Id);
        Assert.Equal("Dialog.Crash", ViewKeys.DialogCrash.Id);
        Assert.Equal("Pages.HomeCustomize", ViewKeys.HomeCustomize.Id);
        Assert.Equal("Shell.Fps", ViewKeys.ShellFps.Id);
    }

    [Fact]
    public void Classic_registers_the_common_blocks_and_the_dialog_bodies_with_a_complete_registry()
    {
        var classic = ClassicTheme.Create();
        Assert.Equal("common.history", classic.Registry.TemplateIdOf(ViewKeys.HistoryPage.Id));
        Assert.Equal("common.customize", classic.Registry.TemplateIdOf(ViewKeys.HomeCustomize.Id));
        Assert.Equal("common.fps", classic.Registry.TemplateIdOf(ViewKeys.ShellFps.Id));
        Assert.Equal("classic.dialog.feedback", classic.Registry.TemplateIdOf(ViewKeys.DialogFeedback.Id));
        Assert.Equal("classic.dialog.crash", classic.Registry.TemplateIdOf(ViewKeys.DialogCrash.Id));
        Assert.Empty(classic.Validate());
    }

    [Fact]
    public void An_empty_history_model_reads_without_a_null()
    {
        var model = new HistoryPageModel { TotalVisits = 0, UniqueRoutes = 0, Body = HistoryBodyKind.Empty, Sections = [] };
        Assert.Equal("", model.EmptyTitle);
        Assert.Equal("", model.EmptyCaption);
        Assert.False(model.EmptyIsNoMatch);
        Assert.Empty(model.Sections);
    }
}
