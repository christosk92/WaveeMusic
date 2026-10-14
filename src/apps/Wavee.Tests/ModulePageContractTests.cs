// ── Wavee.Tests/ModulePageContractTests.cs — the module page's published key and its Common block's registration (themes plan 6.5, T10b3) ──
//
// The module page is a Common block since T10b3 (`Themes\Common\CommonModulePage.cs`): written once, registered by every structure. `Modules.PageHost`
// keeps the route parse, the load, the claim and the verbs. What is pure about the contract: the id (a published id never changes), Classic registering
// the key under the Common template id with a complete registry, and the props never comparing equal (the host builds them on every render, so a
// data-equal record must not let the view skip the render). The route grammar and the watch projection are `ModulePagesTests` and `WatchPageModelTests`.

using FluentGpu.Signals;
using Wavee.Sdk;
using Wavee.Themes.Classic;
using Wavee.Theming.Contracts;
using Xunit;

namespace Wavee.Tests;

public class ModulePageContractTests
{
    static ModulePageProps Props(Loadable<ModulePageDoc> loadable) => new()
    {
        PageUri = "u", ModuleId = "m", Parsed = true, ScrollKey = "k", Loadable = loadable, Retry = static () => { },
    };

    [Fact]
    public void The_published_id_is_the_plans()
        => Assert.Equal("Pages.Module", ViewKeys.ModulePage.Id);

    [Fact]
    public void Classic_registers_the_module_page_as_the_common_block()
    {
        var classic = ClassicTheme.Create();
        Assert.Equal("common.module.page", classic.Registry.TemplateIdOf(ViewKeys.ModulePage.Id));
        Assert.Empty(classic.Validate());
    }

    [Fact]
    public void Props_never_compare_equal_to_another_instance()
    {
        var loadable = Loadable<ModulePageDoc>.Pending();
        var a = Props(loadable);
        var b = Props(loadable);
        Assert.NotEqual(a, b);
    }
}
