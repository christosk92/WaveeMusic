namespace Wavee.Core.Audio;

// PlayPlay is Spotify property. Wavee follows strict guidelines (proper
// user-agents, playback events, no abuse). Tracks are chunked per user request
// and not stored to disk. PlayPlay keys are only cached in obfuscated form and
// still require the same algorithm to read. The actual cipher constants are not
// bundled in the public repository — this stub keeps the rest of the codebase
// compiling and the runtime feature simply stays disabled.
public static class PlayPlayConstants
{
    public const string SpotifyClientVersion = "0.0.0";
    public static readonly byte[] SpotifyClientSha256 = new byte[32];
    public static readonly byte[] VmInitValue = new byte[16];
    public const ulong AnalysisBase = 0;

    public static class EmulatorSizes
    {
        public const int VmObject = 144;
        public const int RtContext = 16;
        public const int InitValue = 16;
        public const int ObfuscatedKey = 16;
        public const int DerivedKey = 24;
        public const int Key = 16;
        public const int ContentId = 16;
    }

    public static class RtFunctions
    {
        public const ulong VmRuntimeInitVa = 0;
        public const ulong VmObjectTransformVa = 0;
    }

    public static class RtData
    {
        public const ulong RuntimeContextVa = 0;
    }

    public static class VmHooks
    {
        public const ulong FillRandomBytesVa = 0;
    }

    public static class AesKeyHook
    {
        public const ulong TriggerRip = 0;
    }
}
