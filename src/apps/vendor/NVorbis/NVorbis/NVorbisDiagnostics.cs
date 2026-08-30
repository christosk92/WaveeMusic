using System;

namespace NVorbis
{
    /// <summary>
    /// Static logging sink for vendored NVorbis. Set <see cref="Trace"/> from the
    /// host (e.g. AudioHostService) to route NVorbis diagnostic lines through the
    /// host's ILogger. Keeps NVorbis dependency-free (no MS.Extensions.Logging) while
    /// allowing structured logging at the boundary.
    /// </summary>
    public static class NVorbisDiagnostics
    {
        /// <summary>
        /// When non-null, NVorbis emits trace lines through this delegate. When null,
        /// trace calls are no-ops (zero allocation, single null check).
        /// </summary>
        public static Action<string> Trace { get; set; }

        internal static bool IsEnabled => Trace != null;

        internal static void Log(string message)
        {
            var sink = Trace;
            if (sink != null) sink(message);
        }
    }
}
