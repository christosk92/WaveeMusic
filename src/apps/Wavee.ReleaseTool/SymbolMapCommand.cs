using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace Wavee.ReleaseTool;

/// <summary>
/// <c>symbol-map</c> — reads a <c>Wavee.exe</c> + its matching <c>Wavee.pdb</c> and writes the binary <c>.symmap</c>
/// the crash-ingest Worker resolves report RVAs against (crash &amp; diagnostics plan §I "Symbol map (.symmap) binary
/// format"). Invoked by <c>ops\release\wavee-release.ps1</c>'s <c>symbols</c> phase, one call per architecture.
/// </summary>
static class SymbolMapCommand
{
    public static int Run(Args a)
    {
        var bad = a.UnknownKeys("pdb", "exe", "out");
        if (bad.Count > 0)
        {
            Console.Error.WriteLine("error: unknown option(s): " + string.Join(", ", bad));
            Program.PrintUsage();
            return Validator.ExitUsage;
        }

        var missing = new List<string>();
        string pdbPath = a.Require("pdb", missing);
        string exePath = a.Require("exe", missing);
        string outPath = a.Require("out", missing);
        if (missing.Count > 0)
        {
            Console.Error.WriteLine("error: symbol-map needs " + string.Join(" ", missing));
            Program.PrintUsage();
            return Validator.ExitUsage;
        }

        if (!File.Exists(exePath)) { Console.Error.WriteLine("error: exe not found: " + exePath); return Validator.ExitUsage; }
        if (!File.Exists(pdbPath)) { Console.Error.WriteLine("error: pdb not found: " + pdbPath); return Validator.ExitUsage; }

        PeReader.DebugInfo exeDebug;
        try
        {
            exeDebug = PeReader.ReadDebugInfo(exePath);
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine("error: could not read the debug directory of " + exePath + ": " + ex.Message);
            return Validator.ExitInvalid;
        }

        List<Crash.SymbolMap.Entry> entries;
        try
        {
            entries = DbgHelp.EnumerateFunctionSymbols(exePath, pdbPath, exeDebug,
                out Guid loadedPdbGuid, out uint loadedPdbAge, out string loadedPdbPath);

            if (loadedPdbGuid != exeDebug.PdbGuid || loadedPdbAge != exeDebug.Age)
            {
                Console.Error.WriteLine("error: " + pdbPath + " does not match " + exePath + ":");
                Console.Error.WriteLine("  exe debug directory : " + FormatDebugId(exeDebug.PdbGuid, exeDebug.Age));
                Console.Error.WriteLine("  loaded pdb          : " + FormatDebugId(loadedPdbGuid, loadedPdbAge) +
                    (loadedPdbPath.Length > 0 ? " (" + loadedPdbPath + ")" : " (none loaded)"));
                Console.Error.WriteLine("  a stale or wrong --pdb produces a map the Worker can never resolve a real report against - rebuild or point --pdb at the exact Wavee.pdb this Wavee.exe was linked from.");
                return Validator.ExitInvalid;
            }
        }
        catch (DbgHelpException ex)
        {
            Console.Error.WriteLine("error: " + ex.Message);
            return Validator.ExitInvalid;
        }

        if (entries.Count == 0)
            Console.Error.WriteLine("warn: DbgHelp enumerated zero SymTagFunction symbols - the map will resolve nothing");

        string outFull = Path.GetFullPath(outPath);
        string? outDir = Path.GetDirectoryName(outFull);
        if (!string.IsNullOrEmpty(outDir)) Directory.CreateDirectory(outDir);

        using (var fs = new FileStream(outFull, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            Crash.SymbolMap.Write(fs, entries, exeDebug.PdbGuid, exeDebug.Age, exeDebug.SizeOfImage);
        }

        long bytesWritten = new FileInfo(outFull).Length;
        Console.Out.WriteLine(entries.Count.ToString(CultureInfo.InvariantCulture) + " symbol(s) -> " + outFull +
            " (" + bytesWritten.ToString(CultureInfo.InvariantCulture) + " bytes, debug id " +
            FormatDebugId(exeDebug.PdbGuid, exeDebug.Age) + ")");
        return Validator.ExitOk;
    }

    /// <summary>"GUID-age", upper-case GUID without braces, matching <c>PeDebugId.TryRead</c>'s string form (plan §I).</summary>
    static string FormatDebugId(Guid guid, uint age) =>
        guid.ToString("D", CultureInfo.InvariantCulture).ToUpperInvariant() + "-" + age.ToString(CultureInfo.InvariantCulture);
}
