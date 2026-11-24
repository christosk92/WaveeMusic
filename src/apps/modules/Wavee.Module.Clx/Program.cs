using Wavee.Sdk;

namespace Wavee.Module.Clx;

/// <summary>
/// The whole executable. <see cref="ModuleRunner"/> owns stdio, framing, JSON-RPC ids, cancellation and shutdown.
/// </summary>
internal static class Program
{
    private static Task<int> Main(string[] args) => ModuleRunner.RunAsync<ClxModule>(args);
}
