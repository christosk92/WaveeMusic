// ── Wavee.ShellTests/ModulePageMountedTests.cs — the module page's view, mounted (themes plan 6.5, T10b3) ───────────────────────
//
// The module page is the Common block `CommonModulePage` since T10b3; `Modules.PageHost` keeps the route parse, the load, the claim of the stage playable
// and the verbs (`Wavee.Tests` pins the pure ones: `ModulePagesTests`, `WatchPageModelTests`, `ModulePageContractTests`). What only a mounted tree can say:
//
//   registered   Classic registers the Common block under `Pages.Module`
//   not parsed   a route that is not a module page draws the error vacancy and no stage slot
//   pending      the stage slot is an EMPTY keyed box (a non-watch document), outside the scroller
//   failed       the error arm mounts the failed body (keyed `module-failed`) and Retry is the host's verb
//   ready        the entity reading is the keyed `module-entity` column

using FluentGpu.Signals;
using Wavee;
using Wavee.Sdk;
using Wavee.Themes.Classic;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Xunit;

namespace Wavee.ShellTests;

public sealed class ModulePageMountedTests(ShellHost host)
{
    static ViewHarnessOptions UnderClassic() => new() { Composition = ClassicTheme.Create() };

    static ModulePageProps Props(Loadable<ModulePageDoc> loadable, bool parsed = true, Action? retry = null) => new()
    {
        PageUri = "fixture",
        ModuleId = parsed ? "fixture-module" : "",
        Parsed = parsed,
        RouteArg = "Fixture",
        ScrollKey = "test:module-page:fixture",
        Loadable = loadable,
        Retry = retry ?? (static () => { }),
    };

    static ModulePageDoc Doc() => new(ModulePageDoc.CurrentVersion, ModulePageDoc.TemplateEntity,
        new PageHero("Fixture title", null, null, null, null, false), [], [], null);

    [Fact]
    public void Classic_registers_the_module_page_as_the_common_block()
        => host.Run(() => Assert.Equal("common.module.page", ThemeHost.Registry.TemplateIdOf(ViewKeys.ModulePage.Id)));

    [Fact]
    public void A_route_that_is_not_a_module_page_draws_no_stage_slot()
        => ViewHarness.With(host, ViewKeys.ModulePage, Props(Loadable<ModulePageDoc>.Pending(Doc()), parsed: false),
            harness =>
            {
                harness.RunFrames(30);
                Assert.False(harness.Contains("module-stage-slot"), "the error vacancy replaces the page");
                Assert.False(harness.Contains("module-entity"));
            }, UnderClassic());

    [Fact]
    public void A_pending_page_keeps_an_empty_stage_slot_outside_the_scroller()
        => ViewHarness.With(host, ViewKeys.ModulePage, Props(Loadable<ModulePageDoc>.Pending(Doc())),
            harness =>
            {
                harness.RunFrames(30);
                Assert.True(harness.Contains("module-stage-slot"), "the stage slot is always there, empty for a non-watch document");
                Assert.False(harness.Contains("module-failed"));
            }, UnderClassic());

    [Fact]
    public void A_failed_page_mounts_the_failed_body()
        => ViewHarness.With(host,
            ViewKeys.ModulePage,
            Props(Loadable<ModulePageDoc>.Failed(new ModuleException(ModuleErrorCode.Unsupported, "No module host is running."))),
            harness =>
            {
                harness.RunFrames(30);
                Assert.True(harness.Contains("module-stage-slot"));
                Assert.True(harness.Contains("module-failed"), "the failed arm carries the vacancy with Retry");
            }, UnderClassic());

    [Fact]
    public void A_ready_entity_page_is_the_keyed_entity_column()
        => ViewHarness.With(host, ViewKeys.ModulePage, Props(Loadable<ModulePageDoc>.Ready(Doc())),
            harness =>
            {
                harness.RunFrames(60);
                Assert.True(harness.Contains("module-entity"));
                Assert.True(harness.Contains("module-hero"), "the entity template always wears a hero");
            }, UnderClassic());
}
