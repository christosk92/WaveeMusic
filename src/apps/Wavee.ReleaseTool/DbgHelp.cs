using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace Wavee.ReleaseTool;

/// <summary>Thrown for any DbgHelp failure (SymInitializeW / SymLoadModuleExW / SymGetModuleInfoW64 / SymEnumSymbolsW).</summary>
sealed class DbgHelpException : Exception
{
    public DbgHelpException(string message) : base(message) { }
}

/// <summary>
/// The DbgHelp-backed function-symbol enumerator behind <c>symbol-map</c>: loads a PDB against its exe with
/// <c>SymInitializeW</c>/<c>SymLoadModuleExW</c>, cross-checks the loaded PDB's own GUID/age against what
/// <see cref="PeReader"/> read from the exe's debug directory (so a stale or wrong <c>--pdb</c> fails loudly instead
/// of silently shipping an empty or mismatched map), then walks every symbol with <c>SymEnumSymbolsW</c> and keeps
/// only <c>SymTagFunction</c> (5) entries as <c>(rva, size, name)</c>.
/// </summary>
/// <remarks>
/// AOT-safe by construction: <c>LibraryImport</c> (source-generated marshalling, no reflection), an
/// <c>[UnmanagedCallersOnly]</c> callback reached only via a native function pointer (never invoked from managed
/// code, so the source generator never needs a reverse-P/Invoke delegate), and <c>Marshal.Read*</c>/<c>PtrToStringUni</c>
/// for the parts of <c>SYMBOL_INFOW</c> this needs — the struct itself is owned and filled by dbghelp.dll for the
/// duration of the callback, so there is nothing to marshal in, only fixed-offset reads out.
/// </remarks>
static class DbgHelp
{
    const uint SymoptUndname = 0x00000002;
    const uint SymoptDeferredLoads = 0x00000004;
    const uint SymTagFunction = 5;
    const ulong DefaultModuleBase = 0x400000;

    // SYMBOL_INFOW field offsets (default/Sequential layout, 8-byte-aligned ULONG64 fields — see the file header of
    // Crash.SymbolMap.cs for the .symmap layout this feeds; this table is the DbgHelp side of the same contract).
    const int OffSize = 28;
    const int OffModBase = 32;
    const int OffAddress = 56;
    const int OffTag = 72;
    const int OffNameLen = 76;
    const int OffName = 84;

    public static List<Crash.SymbolMap.Entry> EnumerateFunctionSymbols(
        string exePath, string pdbPath, PeReader.DebugInfo exeDebug,
        out Guid loadedPdbGuid, out uint loadedPdbAge, out string loadedPdbPath)
    {
        nint hProcess = NativeMethods.GetCurrentProcess();
        // NO deferred loads: with SYMOPT_DEFERRED_LOADS the module's PDB is not opened until a symbol is queried, and
        // SymGetModuleInfoW64 then reports an all-zero PdbSig70 (observed 2026-09-24 against a real publish pair) —
        // the GUID/age cross-check below would reject every valid map. Load eagerly; the tool runs once per release.
        NativeMethods.SymSetOptions(SymoptUndname);

        string? symPath = Path.GetDirectoryName(Path.GetFullPath(pdbPath));
        if (!NativeMethods.SymInitializeW(hProcess, symPath, false))
            throw new DbgHelpException("SymInitializeW failed for search path '" + symPath + "' (Win32 error " + Marshal.GetLastWin32Error() + ")");

        try
        {
            ulong baseAddr = NativeMethods.SymLoadModuleExW(hProcess, 0, exePath, null, DefaultModuleBase, 0, 0, 0);
            if (baseAddr == 0)
                throw new DbgHelpException("SymLoadModuleExW('" + exePath + "') failed (Win32 error " + Marshal.GetLastWin32Error() +
                    "); pdb search path was '" + symPath + "'");

            unsafe
            {
                var info = default(ImagehlpModuleW64);
                info.SizeOfStruct = (uint)sizeof(ImagehlpModuleW64);
                if (!NativeMethods.SymGetModuleInfoW64(hProcess, baseAddr, &info))
                    throw new DbgHelpException("SymGetModuleInfoW64 failed (Win32 error " + Marshal.GetLastWin32Error() + ")");
                loadedPdbGuid = info.PdbSig70;
                loadedPdbAge = info.PdbAge;
                loadedPdbPath = new string(info.LoadedPdbName);   // a local struct is already fixed: the buffer decays to char* directly
            }

            var entries = new List<Crash.SymbolMap.Entry>(4096);
            GCHandle handle = GCHandle.Alloc(entries);
            try
            {
                unsafe
                {
                    delegate* unmanaged<nint, uint, nint, int> callback = &SymEnumCallback;
                    if (!NativeMethods.SymEnumSymbolsW(hProcess, baseAddr, "*", callback, GCHandle.ToIntPtr(handle)))
                        throw new DbgHelpException("SymEnumSymbolsW failed (Win32 error " + Marshal.GetLastWin32Error() + ")");
                }
            }
            finally { handle.Free(); }

            return entries;
        }
        finally
        {
            NativeMethods.SymUnloadModule64(hProcess, DefaultModuleBase);
            NativeMethods.SymCleanup(hProcess);
        }
    }

    /// <summary>The <c>PSYM_ENUMERATESYMBOLS_CALLBACKW</c> DbgHelp invokes per symbol. Reached only via a native
    /// function pointer — never call this from managed code. Must never let an exception cross back into
    /// dbghelp.dll: any failure here is swallowed and the symbol is skipped rather than aborting the whole enumeration.</summary>
    [UnmanagedCallersOnly]
    static int SymEnumCallback(nint symInfo, uint symbolSize, nint userContext)
    {
        try
        {
            uint tag = unchecked((uint)Marshal.ReadInt32(symInfo, OffTag));
            if (tag != SymTagFunction) return 1;

            ulong moduleBase = unchecked((ulong)Marshal.ReadInt64(symInfo, OffModBase));
            ulong address = unchecked((ulong)Marshal.ReadInt64(symInfo, OffAddress));
            uint size = unchecked((uint)Marshal.ReadInt32(symInfo, OffSize));
            uint nameLen = unchecked((uint)Marshal.ReadInt32(symInfo, OffNameLen));

            if (address < moduleBase) return 1;
            ulong rva64 = address - moduleBase;
            if (rva64 > uint.MaxValue || nameLen == 0) return 1;

            string? name = Marshal.PtrToStringUni(symInfo + OffName, (int)nameLen);
            if (string.IsNullOrEmpty(name)) return 1;

            if (GCHandle.FromIntPtr(userContext).Target is List<Crash.SymbolMap.Entry> list)
                list.Add(new Crash.SymbolMap.Entry((uint)rva64, size, name));
        }
        catch
        {
            // A managed exception must never propagate back into dbghelp.dll's call stack; drop the symbol instead.
        }
        return 1; // continue enumeration
    }
}

/// <summary>The subset of <c>IMAGEHLP_MODULEW64</c> this tool reads: sized exactly like the native struct (default
/// Sequential layout matches the Windows SDK header's field order/alignment) so <c>sizeof(ImagehlpModuleW64)</c> is
/// the correct <c>SizeOfStruct</c> DbgHelp requires.</summary>
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal unsafe struct ImagehlpModuleW64
{
    public uint SizeOfStruct;
    public ulong BaseOfImage;
    public uint ImageSize;
    public uint TimeDateStamp;
    public uint CheckSum;
    public uint NumSyms;
    public int SymType;
    public fixed char ModuleName[32];
    public fixed char ImageName[256];
    public fixed char LoadedImageName[256];
    public fixed char LoadedPdbName[256];
    public uint CVSig;
    public fixed char CVData[780]; // MAX_PATH * 3
    public uint PdbSig;
    public Guid PdbSig70;
    public uint PdbAge;
    public int PdbUnmatched;
    public int DbgUnmatched;
    public int LineNumbers;
    public int GlobalSymbols;
    public int TypeInfo;
    public int SourceIndexed;
    public int Publics;
    public uint MachineType;
    public uint Reserved;
}

internal static partial class NativeMethods
{
    [LibraryImport("kernel32.dll")]
    internal static partial nint GetCurrentProcess();

    [LibraryImport("dbghelp.dll")]
    internal static partial uint SymSetOptions(uint symOptions);

    [LibraryImport("dbghelp.dll", EntryPoint = "SymInitializeW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SymInitializeW(nint hProcess, string? userSearchPath, [MarshalAs(UnmanagedType.Bool)] bool fInvadeProcess);

    [LibraryImport("dbghelp.dll", EntryPoint = "SymLoadModuleExW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial ulong SymLoadModuleExW(nint hProcess, nint hFile, string imageName, string? moduleName,
        ulong baseOfDll, uint dllSize, nint data, uint flags);

    [LibraryImport("dbghelp.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool SymGetModuleInfoW64(nint hProcess, ulong qwAddr, ImagehlpModuleW64* moduleInfo);

    [LibraryImport("dbghelp.dll", EntryPoint = "SymEnumSymbolsW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool SymEnumSymbolsW(nint hProcess, ulong baseOfDll, string? mask,
        delegate* unmanaged<nint, uint, nint, int> callback, nint userContext);

    [LibraryImport("dbghelp.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SymUnloadModule64(nint hProcess, ulong baseOfDll);

    [LibraryImport("dbghelp.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SymCleanup(nint hProcess);
}
