// The one test that spawns a REAL module process — the end-to-end proof that the launch shape (ProcessStartInfo, the
// stdio protocol channel, the job object) works against a module built from this repo. Skipped when no bundled module
// is deployed next to the test binary, which is the normal state of a unit-test run: the csproj's module copy only
// populates the APP's output. Ported from 0.2.9 Wavee.Tests/Modules/ModuleIntegrationTests.cs.

using Xunit;
using static Wavee.Modules;

namespace Wavee.Tests;

[Trait("Category", "Integration")]
public class ModuleIntegrationTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static ModuleCatalog DeployedCatalog()
        => ModuleCatalog.Discover(Path.Combine(AppContext.BaseDirectory, "modules"), Path.Combine(AppContext.BaseDirectory, "no-user-store"));

    [Fact]
    public async Task ARealModuleProcess_CompletesTheHandshake()
    {
        ModuleCatalog catalog = DeployedCatalog();
        if (catalog.Modules.Count == 0)
        {
            Assert.Skip("no bundled playback module is deployed next to the test binary");
            return;
        }

        InstalledModule module = catalog.Modules[0];
        using var host = new ModuleHost(catalog, hostVersion: "1.0.0-test", locale: "en-US", startIdleTimer: false);
        ModuleProcess process = host.Process(module);

        // Any request forces the lazy start + the handshake; a typed refusal is a completed round trip.
        try { await host.MatchAsync("https://example.test/nothing-claims-this", module.Id, Ct); }
        catch (Wavee.Sdk.ModuleException) { }

        Assert.Equal(ModuleProcessState.Ready, process.State);
        Assert.NotNull(process.ProcessId);
        Assert.True(process.NegotiatedProtocol >= ModuleCatalog.MinProtocol);

        await process.StopAsync("test", Ct);
        Assert.Equal(ModuleProcessState.Stopped, process.State);
    }
}
