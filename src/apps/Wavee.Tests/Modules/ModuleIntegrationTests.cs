using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend.Modules;
using Wavee.Sdk;
using Xunit;

namespace Wavee.Tests.Modules;

/// <summary>
/// The one test that spawns a REAL module process — the end-to-end proof that the launch shape (ProcessStartInfo,
/// the stdio protocol channel, the environment, the job object) actually works against a module built from this repo.
/// It is skipped when no bundled module is present next to the test binary, which is the normal state of a unit-test
/// run: <c>CopyBundledModules</c> only populates the APP's output.
/// </summary>
[Trait("Category", "Integration")]
public class ModuleIntegrationTests
{
    static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    /// <summary>The first bundled module found next to the test binary, or null when none is deployed.</summary>
    static InstalledModule? FirstBundled()
    {
        ModuleCatalog catalog = ModuleCatalog.Discover(
            Path.Combine(AppContext.BaseDirectory, "modules"), Path.Combine(AppContext.BaseDirectory, "nope"));
        return catalog.Modules.Count > 0 ? catalog.Modules[0] : null;
    }

    [Fact]
    public async Task ARealModuleProcess_CompletesTheHandshake()
    {
        if (FirstBundled() is not { } module)
        {
            Assert.Skip("no bundled playback module is deployed next to the test binary");
            return;
        }

        var catalog = ModuleCatalog.Discover(Path.Combine(AppContext.BaseDirectory, "modules"),
            Path.Combine(AppContext.BaseDirectory, "nope"));
        using var host = new ModuleHost(catalog, default, null, null, null, "1.0.0-test", "en-US",
            startIdleTimer: false);
        ModuleProcess process = host.Process(module);

        // Any request forces the lazy start + the module/initialize handshake. A module that declares `match` answers
        // (or typed-refuses) a match; either way the process reached Ready, which is what this test is about.
        try
        {
            await host.MatchAsync("https://example.test/nothing-claims-this", module.Id, Ct);
        }
        catch (ModuleException)
        {
            // A typed refusal is a completed round trip.
        }

        Assert.Equal(ModuleProcessState.Ready, process.State);
        Assert.NotNull(process.ProcessId);
        Assert.True(process.NegotiatedProtocol >= ModuleCatalog.MinProtocol);

        await process.StopAsync("test", Ct);
        Assert.Equal(ModuleProcessState.Stopped, process.State);
    }
}
