using Wavee.Sdk;

namespace Wavee.Module.Radio;

/// <summary>
/// The whole executable. <see cref="ModuleRunner"/> owns stdio, framing, JSON-RPC ids, cancellation and shutdown;
/// without <c>--wavee-module</c> it exposes the <c>match</c> / <c>resolve</c> CLI used for manual testing.
/// </summary>
internal static class Program
{
    private static Task<int> Main(string[] args) => ModuleRunner.RunAsync<RadioModule>(args);
}
