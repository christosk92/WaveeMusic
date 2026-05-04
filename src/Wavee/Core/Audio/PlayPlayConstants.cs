namespace Wavee.Core.Audio;

/// <summary>
/// Constants pinned to <c>Spotify.dll</c> v1.2.88.483 (x86_64). Every virtual
/// address below is bound to that exact build — a different Spotify version
/// will silently land in unrelated code, so the SHA-256 in
/// <see cref="SpotifyClientSha256"/> must match before the helper is started.
/// Sourced from <c>cycyrild/another-unplayplay</c> (<c>src/unplayplay/consts.py</c>).
/// </summary>
public static class PlayPlayConstants
{
    /// <summary>Required Spotify desktop version.</summary>
    public const string SpotifyClientVersion = "1.2.88.483";

    /// <summary>SHA-256 of <c>Spotify.dll</c> v1.2.88.483 (x86_64).</summary>
    public static readonly byte[] SpotifyClientSha256 =
    [
        0x9C, 0xAF, 0xE1, 0xCA, 0xD1, 0x76, 0x02, 0x44,
        0x85, 0xF8, 0x84, 0x0B, 0x72, 0xF6, 0x74, 0x7D,
        0x5B, 0x87, 0x88, 0x5B, 0x04, 0x23, 0xB1, 0xDF,
        0x00, 0x5A, 0xDF, 0x08, 0x8E, 0xF8, 0x0C, 0xE8,
    ];

    /// <summary>
    /// Initial value written to the VM's init buffer before
    /// <c>vm_object_transform</c> is invoked. Captured during runtime by upstream.
    /// 16 bytes.
    /// </summary>
    public static readonly byte[] VmInitValue =
    [
        0xAA, 0x0D, 0xFA, 0xC6, 0x77, 0x6A, 0x22, 0x8C,
        0x1C, 0x49, 0xCF, 0x2C, 0x6B, 0x65, 0xE6, 0x09,
    ];

    public static class Mem
    {
        public const ulong PageSize = 0x1000;

        public const ulong StackAddr = 0x1000000;
        public const ulong StackSize = 0x200000;

        public const ulong HeapAddr = 0x2000000;
        public const ulong HeapSize = 0x200000;

        public const ulong TebAddr = 0x3000000;

        public const ulong ExitAddr = 0x4000000;
    }

    /// <summary>Image base recorded in the dumped Spotify.dll PE.</summary>
    public const ulong AnalysisBase = 0x0000000180000000UL;

    public static class RtFunctions
    {
        public const ulong VmRuntimeInitVa = 0x00000001803E42AC;
        public const ulong VmObjectTransformVa = 0x00000001803E62B8;
        public const ulong CxxThrowExceptionVa = 0x000000018165DD68;
    }

    public static class RtData
    {
        public const ulong RuntimeContextVa = 0x0000000181786FE0;
    }

    public static class AesKeyHook
    {
        /// <summary>RIP at which RDX points to the freshly-derived 16-byte AES key.</summary>
        public const ulong TriggerRip = 0x00000001804149FC;
    }

    public static class RtHooks
    {
        public const ulong MtxLockVa = 0x0000000181642340;
        public const ulong CndWaitVa = 0x00000001816434E4;
        public const ulong MtxUnlockVa = 0x000000018164236C;
        public const ulong MallocVa = 0x000000018166D9B0;
    }

    public static class VmHooks
    {
        public const ulong FillRandomBytesVa = 0x0000000180434B04;
    }

    public static class EmulatorSizes
    {
        public const int VmObject = 144;
        public const int RtContext = 16;
        public const int DerivedKey = 24;
        public const int ObfuscatedKey = 16;
        public const int InitValue = 16;
        public const int ContentId = 16;
        public const int Key = 16;
    }
}
