using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Wavee.Core.Audio;

namespace Wavee.AudioHost.PlayPlay;

/// <summary>
/// Direct-call PlayPlay AES-key unwrapper. Loads Spotify.dll v1.2.88.483 (x86_64)
/// via LoadLibrary, computes the pinned-RVA addresses of <c>vm_runtime_init</c>
/// and <c>vm_object_transform</c>, and calls them as ordinary C function
/// pointers. No CPU emulator. The host process must be x86_64 — Spotify.dll is
/// x86_64 — which is why <c>Wavee.AudioHost.csproj</c> pins
/// <c>RuntimeIdentifier=win-x64</c>. On ARM64 Windows the OS's x64 emulation
/// runs this process; the WinUI process stays ARM64-native.
/// </summary>
/// <remarks>
/// <para>Memory layout (per cycyrild/another-unplayplay):</para>
/// <list type="bullet">
///   <item><c>vm_obj</c> — 144 bytes, opaque cipher state</item>
///   <item><c>rt_context</c> — 16 bytes (8 zero, then runtime_context_va LE)</item>
///   <item><c>init_value</c> — 16 bytes <see cref="PlayPlayConstants.VmInitValue"/></item>
///   <item><c>obf_key</c> — 16 bytes per request</item>
///   <item><c>derived_key</c> — 24 bytes scratch</item>
/// </list>
/// <para>The AES key never lands in <c>derived_key</c> directly; it only
/// exists in memory at <c>[RDX]</c> at exactly RIP=<see cref="PlayPlayConstants.AesKeyHook.TriggerRip"/>.
/// We patch a single <c>0xCC</c> there, install a vectored exception handler,
/// and copy the 16 bytes out on the breakpoint.</para>
/// <para>This class is NOT thread-safe — derivations must be serialised by
/// the caller. The dispatcher in <c>AudioHostService</c> already uses one
/// pipe-message-at-a-time semantics so a single instance is fine.</para>
/// </remarks>
public sealed unsafe class PlayPlayKeyEmulator : IDisposable
{
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadLibraryW(string lpFileName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr AddVectoredExceptionHandler(uint first, IntPtr handler);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr RemoveVectoredExceptionHandler(IntPtr handler);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool VirtualProtect(IntPtr lpAddress, UIntPtr dwSize, uint flNewProtect, out uint lpflOldProtect);

    private const uint PAGE_EXECUTE_READWRITE = 0x40;
    private const uint EXCEPTION_BREAKPOINT = 0x80000003;
    private const int EXCEPTION_CONTINUE_EXECUTION = -1;
    private const int EXCEPTION_CONTINUE_SEARCH = 0;

    private const int CONTEXT_RDX_OFFSET = 0x88;
    private const int CONTEXT_RIP_OFFSET = 0xF8;

    private readonly ILogger _logger;

    private readonly IntPtr _moduleBase;
    private readonly byte[] _vmObjSnapshot;
    private readonly IntPtr _vmObj, _rtContext, _initValue, _obfKey, _derivedKey;

    private readonly delegate* unmanaged[Cdecl]<IntPtr, IntPtr, ulong, void> _vmRuntimeInit;
    private readonly delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, void> _vmObjectTransform;

    private readonly IntPtr _aesKeyTriggerRip;
    private byte _origTriggerByte;
    private readonly byte[] _capturedKey = new byte[16];
    private bool _capturedFlag;

    private delegate int VectoredHandlerDelegate(IntPtr exceptionPointers);
    private readonly VectoredHandlerDelegate _handlerDelegate;
    private readonly IntPtr _handlerHandle;

    private bool _disposed;

    /// <summary>
    /// Loads <paramref name="spotifyDllPath"/>, verifies its SHA-256 against
    /// <see cref="PlayPlayConstants.SpotifyClientSha256"/>, hot-patches
    /// <c>fill_random_bytes</c>, installs the int3 hook at TRIGGER_RIP, and
    /// runs <c>vm_runtime_init</c> once to capture a vm_obj snapshot for reuse.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// LoadLibrary failed, SHA-256 mismatch, or VirtualProtect denied write
    /// access to the patch target.
    /// </exception>
    /// <exception cref="FileNotFoundException">DLL path doesn't exist.</exception>
    public PlayPlayKeyEmulator(string spotifyDllPath, ILogger logger)
    {
        _logger = logger;

        if (!File.Exists(spotifyDllPath))
            throw new FileNotFoundException($"Spotify.dll not found at {spotifyDllPath}", spotifyDllPath);

        var dllBytes = File.ReadAllBytes(spotifyDllPath);
        var actualHash = Convert.ToHexString(SHA256.HashData(dllBytes));
        var expectedHash = Convert.ToHexString(PlayPlayConstants.SpotifyClientSha256);
        if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Spotify.dll SHA-256 mismatch. Expected {expectedHash}, got {actualHash}. " +
                "PlayPlayConstants are pinned to v1.2.88.483; refusing to execute a different binary.");
        }

        var module = LoadLibraryW(spotifyDllPath);
        if (module == IntPtr.Zero)
        {
            var err = Marshal.GetLastWin32Error();
            throw new InvalidOperationException($"LoadLibrary failed for {spotifyDllPath} (Win32 error {err})");
        }
        _moduleBase = module;

        _vmRuntimeInit = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, ulong, void>)
            Rebase(PlayPlayConstants.RtFunctions.VmRuntimeInitVa);
        _vmObjectTransform = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, void>)
            Rebase(PlayPlayConstants.RtFunctions.VmObjectTransformVa);
        _aesKeyTriggerRip = Rebase(PlayPlayConstants.AesKeyHook.TriggerRip);

        // Stub fill_random_bytes so derivation is deterministic. Without this,
        // every call would produce a different AES key.
        NoopFillRandomBytes();

        // Save trigger byte and patch with int3. The vectored handler will
        // restore + rewind RIP on each fire.
        _origTriggerByte = Marshal.ReadByte(_aesKeyTriggerRip);
        WriteCode(_aesKeyTriggerRip, [0xCC]);

        _handlerDelegate = OnVectoredException;
        var handlerPtr = Marshal.GetFunctionPointerForDelegate(_handlerDelegate);
        _handlerHandle = AddVectoredExceptionHandler(1, handlerPtr);
        if (_handlerHandle == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                $"AddVectoredExceptionHandler failed (Win32 {Marshal.GetLastWin32Error()})");
        }

        _vmObj = Marshal.AllocHGlobal(PlayPlayConstants.EmulatorSizes.VmObject);
        _rtContext = Marshal.AllocHGlobal(PlayPlayConstants.EmulatorSizes.RtContext);
        _initValue = Marshal.AllocHGlobal(PlayPlayConstants.EmulatorSizes.InitValue);
        _obfKey = Marshal.AllocHGlobal(PlayPlayConstants.EmulatorSizes.ObfuscatedKey);
        _derivedKey = Marshal.AllocHGlobal(PlayPlayConstants.EmulatorSizes.DerivedKey);

        Span<byte> rtBytes = stackalloc byte[PlayPlayConstants.EmulatorSizes.RtContext];
        rtBytes.Clear();
        var ctxVa = (long)Rebase(PlayPlayConstants.RtData.RuntimeContextVa);
        BinaryPrimitives.WriteInt64LittleEndian(rtBytes[8..], ctxVa);
        Marshal.Copy(rtBytes.ToArray(), 0, _rtContext, rtBytes.Length);

        Marshal.Copy(PlayPlayConstants.VmInitValue, 0, _initValue, PlayPlayConstants.EmulatorSizes.InitValue);

        _vmRuntimeInit(_vmObj, _rtContext, 1);

        _vmObjSnapshot = new byte[PlayPlayConstants.EmulatorSizes.VmObject];
        Marshal.Copy(_vmObj, _vmObjSnapshot, 0, _vmObjSnapshot.Length);

        _logger.LogInformation(
            "PlayPlayKeyEmulator initialised (Spotify.dll v{Version} at {Module:X})",
            PlayPlayConstants.SpotifyClientVersion, _moduleBase.ToInt64());
    }

    /// <summary>
    /// Derives a 16-byte AES audio-decryption key from a 16-byte obfuscated
    /// key. <paramref name="contentId16"/> is currently unused by Spotify's
    /// PlayPlay v5 cipher but is kept on the API surface in case a future
    /// version reintroduces it.
    /// </summary>
    public byte[] DeriveAesKey(ReadOnlySpan<byte> obfuscatedKey16, ReadOnlySpan<byte> contentId16)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (obfuscatedKey16.Length != 16) throw new ArgumentException("obfuscated key must be 16 bytes", nameof(obfuscatedKey16));
        if (contentId16.Length != 16) throw new ArgumentException("content id must be 16 bytes", nameof(contentId16));

        Marshal.Copy(_vmObjSnapshot, 0, _vmObj, _vmObjSnapshot.Length);
        var obfArr = obfuscatedKey16.ToArray();
        Marshal.Copy(obfArr, 0, _obfKey, 16);

        // Re-patch with int3 (the previous derive's vectored handler restored
        // the original byte) and reset capture state.
        WriteCode(_aesKeyTriggerRip, [0xCC]);
        _capturedFlag = false;
        Array.Clear(_capturedKey);

        _vmObjectTransform(_vmObj, _obfKey, _derivedKey, _initValue);

        if (!_capturedFlag)
            throw new InvalidOperationException(
                "AES key capture did not fire — TRIGGER_RIP never reached. Spotify.dll RVAs may have drifted.");

        var aes = new byte[PlayPlayConstants.EmulatorSizes.Key];
        Buffer.BlockCopy(_capturedKey, 0, aes, 0, aes.Length);
        return aes;
    }

    private void NoopFillRandomBytes()
    {
        var addr = Rebase(PlayPlayConstants.VmHooks.FillRandomBytesVa);
        ReadOnlySpan<byte> noop = [0x31, 0xC0, 0xC3]; // xor eax, eax; ret
        WriteCode(addr, noop);
    }

    private static void WriteCode(IntPtr addr, ReadOnlySpan<byte> bytes)
    {
        if (!VirtualProtect(addr, (UIntPtr)bytes.Length, PAGE_EXECUTE_READWRITE, out var oldProt))
            throw new InvalidOperationException(
                $"VirtualProtect failed at {addr.ToInt64():X} (Win32 {Marshal.GetLastWin32Error()})");
        for (int i = 0; i < bytes.Length; i++)
            Marshal.WriteByte(addr, i, bytes[i]);
        VirtualProtect(addr, (UIntPtr)bytes.Length, oldProt, out _);
    }

    private int OnVectoredException(IntPtr ep)
    {
        var rec = Marshal.ReadIntPtr(ep, 0);
        var ctx = Marshal.ReadIntPtr(ep, IntPtr.Size);

        var code = (uint)Marshal.ReadInt32(rec, 0);
        if (code != EXCEPTION_BREAKPOINT) return EXCEPTION_CONTINUE_SEARCH;

        var faultAddr = Marshal.ReadIntPtr(rec, 16);
        if (faultAddr != _aesKeyTriggerRip) return EXCEPTION_CONTINUE_SEARCH;

        var rdx = (IntPtr)Marshal.ReadInt64(ctx, CONTEXT_RDX_OFFSET);
        Marshal.Copy(rdx, _capturedKey, 0, 16);
        _capturedFlag = true;

        WriteCode(_aesKeyTriggerRip, [_origTriggerByte]);
        var rip = Marshal.ReadInt64(ctx, CONTEXT_RIP_OFFSET);
        Marshal.WriteInt64(ctx, CONTEXT_RIP_OFFSET, rip - 1);

        return EXCEPTION_CONTINUE_EXECUTION;
    }

    private IntPtr Rebase(ulong va)
    {
        var rva = va - PlayPlayConstants.AnalysisBase;
        return (IntPtr)((ulong)_moduleBase + rva);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Best-effort: remove the vectored handler before freeing scratch
        // memory, so a stray int3 from anywhere else can't fire OnVectoredException
        // and read freed pointers.
        if (_handlerHandle != IntPtr.Zero)
            RemoveVectoredExceptionHandler(_handlerHandle);

        // Restore the original byte at TRIGGER_RIP. Spotify.dll stays loaded
        // — there's no FreeLibrary — but at least the patched function is back
        // to its original instruction.
        try { WriteCode(_aesKeyTriggerRip, [_origTriggerByte]); } catch { }

        Marshal.FreeHGlobal(_vmObj);
        Marshal.FreeHGlobal(_rtContext);
        Marshal.FreeHGlobal(_initValue);
        Marshal.FreeHGlobal(_obfKey);
        Marshal.FreeHGlobal(_derivedKey);
    }
}
