// ── Wavee/Diagnostics/LocalScan.Probe.cs — `Wavee.exe --local-scan <folder> [--runs N] [--change-test] [--keep]` ─────────
//
// The scan probe of #240 (plan §3.6 "Targets", §5): it runs the REAL scanner on a folder of music, read-only, against a
// private index in a temp folder (never the app's index, never a file inside the music folder), and prints the numbers the
// targets are judged by:
//   cold      an empty index: every folder listed, every file read (the OS file cache may be warm: a truly cold cache needs a
//             reboot or a drive that was not read since);
//   persist   the compaction into localindex.bin (ms, bytes) and the load a start-up does (ms);
//   warm      nothing changed: one listing per folder, every folder skipped by fingerprint (best and median of --runs);
//   startup   the start-up path: the USN journal catch-up where the volume allows it, else a warm walk;
//   checks    README "checks before building" 1 (the unprivileged USN journal from this process) and 3 (ChangeTime on every
//             FAT/exFAT volume present: reported, zero, or a mirror of the write time).
// With --change-test it copies a few of the folder's files into the temp folder (the music folder is never written) and
// measures, through the real watcher and debounce, how long a touch, a rename, a move between folders, a delete and a
// tag editor's safe-save take to land in the index, and whether each kept its LocalId.
//
// NO LOGIN, NO WINDOW: a windowless arm of `Probe.TryRunCliArm`. Output goes to the parent console and to the log (`local`).
// The inspector verb `localscan` runs the same runner in memory (Diagnostics.Inspect.LocalScan.cs).

using System.Diagnostics;
using System.Globalization;

namespace Wavee;

public static partial class Diagnostics
{
    public static partial class Probe
    {
        /// <summary>`--local-scan &lt;folder&gt;`: run, print, log. Exit 0 when the scans ran, 2 on a usage error.</summary>
        static int LocalScanArm(string[] args)
        {
            AttachParentConsole();
            int at = Array.IndexOf(args, "--local-scan");
            if (at + 1 >= args.Length || args[at + 1].StartsWith("--", StringComparison.Ordinal))
            {
                Console.Error.WriteLine("usage: Wavee.exe --local-scan <folder> [--runs N] [--change-test] [--keep]");
                return 2;
            }
            int runs = 3;
            int r = Array.IndexOf(args, "--runs");
            if (r >= 0 && r + 1 < args.Length) int.TryParse(args[r + 1], NumberStyles.None, CultureInfo.InvariantCulture, out runs);
            string work = Path.Combine(Path.GetTempPath(), "wavee-localscan-" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
            try
            {
                LocalScanProbe.Run(args[at + 1], work, Math.Clamp(runs, 1, 20), Array.IndexOf(args, "--change-test") >= 0, line =>
                {
                    Console.Out.WriteLine(line);
                    Log.Info("local", "probe " + line);
                });
            }
            finally
            {
                if (Array.IndexOf(args, "--keep") < 0)
                    try { if (Directory.Exists(work)) Directory.Delete(work, recursive: true); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Console.Error.WriteLine("could not delete " + work); }
            }
            return 0;
        }
    }
}

/// <summary>The scan probe's runner (see the file header). The CLI arm and the inspector verb both call it.</summary>
public static class LocalScanProbe
{
    /// <summary>Run the probe on <paramref name="folder"/>. <paramref name="workDir"/>: where the private index is persisted
    /// (null: memory only, no persist step, no change test). Every line goes to <paramref name="line"/>.</summary>
    public static void Run(string folder, string? workDir, int runs, bool changeTest, Action<string> line)
    {
        var c = CultureInfo.InvariantCulture;
        folder = LocalLibrary.Normalize(folder);
        // A network folder is probed first (the scanner's own probe, F4): a dead share answers in ~3 s instead of blocking a minute.
        if (NtDirectory.IsNetworkPath(folder))
        {
            var reachWatch = Stopwatch.StartNew();
            LocalRootState reach = LocalScan.ProbeReach(folder);
            line($"reach state={reach} ms={reachWatch.ElapsedMilliseconds}");
            if (reach is not (LocalRootState.Online or LocalRootState.Waking or LocalRootState.Slow)) { line("error: the folder cannot be reached (" + reach + ")"); return; }
        }
        if (!Directory.Exists(folder)) { line("error: not a folder"); return; }
        NtDirectory.ExposePlaceholders();
        using (var probeRoot = NtDirectory.OpenDirectory(null, folder, out _))
            if (probeRoot is not null) { NtVolume v = NtDirectory.VolumeOf(probeRoot, folder); line($"volume fs={v.FileSystem} flags=0x{v.Flags:X8} stableIds={(v.StableIds ? 1 : 0)} journal={(v.SupportsJournal ? 1 : 0)}"); }
        line($"medium={NtDirectory.MediumOf(folder)} network={(NtDirectory.IsNetworkPath(folder) ? 1 : 0)} elevated={(Environment.IsPrivilegedProcess ? 1 : 0)} os={Environment.OSVersion.Version}");
        line("check1 " + LocalScan.JournalCheck(folder));
        foreach (string check in ChangeTimeChecks()) line("check3 " + check);
        using (IDisposable? arrivals = LocalScan.WatchVolumeArrivals(static () => { }))
            line("volume-arrivals registered=" + (arrivals is null ? 0 : 1));

        // cold: an empty index
        string? bin = workDir is null ? null : Path.Combine(workDir, LocalIndex.FileName);
        if (workDir is not null) Directory.CreateDirectory(workDir);
        var index = new LocalIndex();
        if (bin is not null) index.Load(bin);
        var scan = new LocalScan(index);
        LocalScanStats cold = scan.ScanRoot(folder, startup: false, dirty: null);
        line("cold " + cold.Summary());
        line(string.Create(c, $"index entries={index.Count} unplayable={index.UnplayableCount} missing={index.MissingCount} cloud={index.CloudOnlyCount} video={index.VideoCount} folders={index.Folders().Length} logBytes={index.LogBytes}"));
        foreach (string l in Census(index)) line(l);
        if (cold.Offline) return;

        // persist: compaction + the start-up load
        if (bin is not null)
        {
            var clock = Stopwatch.StartNew();
            index.Compact();
            double compactMs = clock.Elapsed.TotalMilliseconds;
            long bytes = new FileInfo(bin).Length;
            index.Detach();
            index = null!;
            scan = null!;
            // Each measure in a method of its own: a Debug build keeps a method's locals alive to its end.
            long recordsBytes = MeasureRecords(bin);
            (long indexBytes, double loadMs, int loaded) = MeasureLoad(bin);
            index = new LocalIndex();
            index.Load(bin);
            line(string.Create(c, $"persist compactMs={compactMs:0.0} binBytes={bytes} loadMs={loadMs:0.0} loaded={loaded} indexMB={indexBytes / 1048576.0:0.0} snapshotMB={recordsBytes / 1048576.0:0.0}"));
            scan = new LocalScan(index);
        }

        // warm: nothing changed
        var warm = new List<double>();
        for (int i = 0; i < runs; i++)
        {
            LocalScanStats s = scan.ScanRoot(folder, startup: false, dirty: null);
            warm.Add(s.TotalMs);
            if (i == 0) line("warm " + s.Summary());
        }
        warm.Sort();
        line(string.Create(c, $"warm runs={runs} bestMs={warm[0]:0.0} medianMs={warm[warm.Count / 2]:0.0}"));

        // start-up: the journal catch-up where the volume allows it
        LocalScanStats startup = scan.ScanRoot(folder, startup: true, dirty: null);
        line("startup " + startup.Summary());

        if (NtDirectory.IsNetworkPath(folder)) line("network-reader " + NetworkReaderCheck(index));
        if (changeTest && workDir is not null) ChangeTest(index, folder, workDir, line);
        index.Detach();
    }

    /// <summary>What the readers made of the files: formats, playable, estimated lengths, durations, the reasons for the
    /// unplayable ones (reason text only: never a path).</summary>
    static IEnumerable<string> Census(LocalIndex index)
    {
        var formats = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var why = new Dictionary<string, int>(StringComparer.Ordinal);
        var durations = new List<int>();
        int playable = 0, estimated = 0, tagged = 0, covers = 0;
        foreach (LocalEntry e in index.Snapshot())
        {
            if (e.Tags.Kind != LocalKind.Audio) continue;
            string f = e.Format.ToString();
            formats[f] = formats.GetValueOrDefault(f) + 1;
            if (e.Playable) playable++;
            else why[e.Why] = why.GetValueOrDefault(e.Why) + 1;
            if (e.Tags.DurationEstimated) estimated++;
            if (e.Tags.Title.Length > 0) tagged++;
            if (e.Tags.Cover.Exists) covers++;
            if (e.Tags.DurationMs > 0) durations.Add(e.Tags.DurationMs);
        }
        durations.Sort();
        yield return FormattableString.Invariant($"census audio={index.Count} playable={playable} tagged={tagged} covers={covers} estimatedLength={estimated} withLength={durations.Count}")
            + (durations.Count > 0 ? FormattableString.Invariant($" lengthMs min={durations[0]} median={durations[durations.Count / 2]} max={durations[^1]}") : "")
            + " formats=" + string.Join(",", formats.Select(static p => p.Key + ":" + p.Value.ToString(CultureInfo.InvariantCulture)));
        foreach (var w in why.OrderByDescending(static p => p.Value).Take(5)) yield return "census unplayable " + w.Value + "x why=\"" + w.Key + "\"";
    }

    /// <summary>README check 3: on every FAT/exFAT volume present, does a listing report ChangeTime (zero, equal to the
    /// write time, or its own value)? Read-only: one listing of the volume's root folder.</summary>
    static IEnumerable<string> ChangeTimeChecks()
    {
        var found = new List<string>();
        foreach (DriveInfo d in DriveInfo.GetDrives())
        {
            string format;
            try { if (!d.IsReady) continue; format = d.DriveFormat; }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            if (!format.Contains("FAT", StringComparison.OrdinalIgnoreCase)) continue;
            using var root = NtDirectory.OpenDirectory(null, d.RootDirectory.FullName, out int status);
            if (root is null) { found.Add($"{format} {d.DriveType} open failed status=0x{status:X8}"); continue; }
            NtVolume volume = NtDirectory.VolumeOf(root, d.RootDirectory.FullName);
            var list = new List<NtEntry>();
            if (!NtDirectory.List(root, volume.Serial, list, out status)) { found.Add($"{format} list failed status=0x{status:X8}"); continue; }
            int zero = 0, mirror = 0, own = 0, idZero = 0;
            foreach (NtEntry e in list)
            {
                if (e.ChangeTicks == 0) zero++;
                else if (e.ChangeTicks == e.WriteTicks) mirror++;
                else own++;
                if (e.FileId == 0) idZero++;
            }
            found.Add($"{format} {d.DriveType} medium={volume.Medium} entries={list.Count} changeZero={zero} changeMirrorsWrite={mirror} changeOwn={own} fileIdZero={idZero}");
        }
        if (found.Count == 0) found.Add("no FAT or exFAT volume present: not checked");
        return found;
    }

    /// <summary>Copy a few files into the work folder and time how a touch, a rename, a move, a delete and a safe-save land in
    /// the index through the real watcher. The music folder is only read.</summary>
    static void ChangeTest(LocalIndex source, string folder, string workDir, Action<string> line)
    {
        var picks = new List<string>();
        foreach (LocalEntry e in source.Snapshot())
            if (e.State == LocalState.Present && e.Tags.Kind == LocalKind.Audio && e.Size < 64L * 1024 * 1024 && source.FullPathOf(e) is { } p) { picks.Add(p); if (picks.Count == 8) break; }
        if (picks.Count < 5) { line("change-test skipped: fewer than 5 audio files"); return; }
        string lib = Path.Combine(workDir, "lib");
        Directory.CreateDirectory(Path.Combine(lib, "A"));
        Directory.CreateDirectory(Path.Combine(lib, "B"));
        var copies = new List<string>();
        for (int i = 0; i < picks.Count; i++)
        {
            string to = Path.Combine(lib, i % 2 == 0 ? "A" : "B", Path.GetFileName(picks[i]));
            if (copies.Contains(to, StringComparer.OrdinalIgnoreCase)) continue;
            File.Copy(picks[i], to);
            copies.Add(to);
        }
        var index = new LocalIndex();
        var scan = new LocalScan(index);
        LocalScanStats first = scan.ScanRoot(lib, startup: false, dirty: null);
        line("change-test seed " + first.Summary());
        scan.Watch(lib);
        Thread.Sleep(300);   // the watch is armed
        try
        {
            string touched = copies[0];
            long id0 = Id(index, touched);
            File.SetLastWriteTimeUtc(touched, DateTime.UtcNow.AddSeconds(-5));
            long w0 = File.GetLastWriteTimeUtc(touched).Ticks;
            line(Timed("touch", () => index.TryGetByPath(touched, out LocalEntry? e) && e.WriteTicks == w0, () => Id(index, touched) == id0));

            string renamedFrom = copies[1], renamedTo = Path.Combine(Path.GetDirectoryName(renamedFrom)!, "renamed " + Path.GetFileName(renamedFrom));
            long id1 = Id(index, renamedFrom);
            File.Move(renamedFrom, renamedTo);
            line(Timed("rename", () => index.TryGetByPath(renamedTo, out _), () => Id(index, renamedTo) == id1));

            string movedFrom = copies[2], movedTo = Path.Combine(lib, Path.GetFileName(Path.GetDirectoryName(movedFrom)) == "A" ? "B" : "A", "moved " + Path.GetFileName(movedFrom));
            long id2 = Id(index, movedFrom);
            File.Move(movedFrom, movedTo);
            line(Timed("move", () => index.TryGetByPath(movedTo, out _), () => Id(index, movedTo) == id2));

            string deleted = copies[3];
            long id3 = Id(index, deleted);
            File.Delete(deleted);
            line(Timed("delete", () => index.TryGet(id3, out LocalEntry? e) && e.State == LocalState.Missing, () => true));

            string saved = copies[4], temp = saved + ".tmp";
            long id4 = Id(index, saved);
            File.Copy(saved, temp);
            File.SetLastWriteTimeUtc(temp, DateTime.UtcNow.AddSeconds(-9));
            File.Move(temp, saved, overwrite: true);   // a tag editor's safe-save: the path gets a new file id
            long w4 = File.GetLastWriteTimeUtc(saved).Ticks;
            line(Timed("safe-save", () => index.TryGetByPath(saved, out LocalEntry? e) && e.WriteTicks == w4, () => Id(index, saved) == id4));
        }
        finally
        {
            scan.Unwatch(lib);
        }
    }

    /// <summary>What a loaded index holds alive, and how long the load took.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    static (long Bytes, double Ms, int Loaded) MeasureLoad(string bin)
    {
        long before = LiveBytes();
        var clock = Stopwatch.StartNew();
        LocalIndex index = LoadIndex(bin, out int loaded);
        double ms = clock.Elapsed.TotalMilliseconds;
        long after = LiveBytes();
        index.Detach();
        GC.KeepAlive(index);
        return (after - before, ms, loaded);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    static LocalIndex LoadIndex(string bin, out int loaded)
    {
        var index = new LocalIndex();
        loaded = index.Load(bin);
        return index;
    }

    /// <summary>What a full <c>Snapshot()</c> (a record per file, as P2's restate takes it) holds alive on its own.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    static long MeasureRecords(string bin)
    {
        long before = LiveBytes();
        (LocalEntry[] Entries, LocalFolder[] Folders) records = LoadRecords(bin);
        long after = LiveBytes();
        GC.KeepAlive(records.Entries);
        GC.KeepAlive(records.Folders);
        return after - before;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    static (LocalEntry[], LocalFolder[]) LoadRecords(string bin)
    {
        LocalIndex index = LoadIndex(bin, out _);
        var records = (index.Snapshot(), index.Folders());
        index.Detach();
        return records;
    }

    /// <summary>Read the largest indexed file through <see cref="NetworkByteSource"/> (start to end, then three seeks: back to the
    /// start, the middle, near the end) and compare every byte with a plain read of the same file. One line for the probe.</summary>
    static string NetworkReaderCheck(LocalIndex index)
    {
        LocalEntry? pick = null;
        foreach (LocalEntry e in index.Snapshot()) if (e.State == LocalState.Present && (pick is null || e.Size > pick.Size)) pick = e;
        if (pick is null || index.FullPathOf(pick) is not { } path) return "skipped: no file";
        byte[] expected = File.ReadAllBytes(path);
        var source = new NetworkByteSource(path);
        var clock = Stopwatch.StartNew();
        if (!source.TryOpen(default)) return "open failed";
        try
        {
            byte[] got = new byte[expected.Length];
            int at = 0, n;
            while (at < got.Length && (n = source.Read(got.AsSpan(at, Math.Min(64 * 1024, got.Length - at)))) > 0) at += n;
            bool whole = at == expected.Length && got.AsSpan().SequenceEqual(expected);
            double fullMs = clock.Elapsed.TotalMilliseconds;
            int seeksOk = 0;
            foreach (long offset in (long[])[0, expected.Length / 2, Math.Max(0, expected.Length - 5000)])
            {
                source.Seek(offset);
                var buf = new byte[Math.Min(4096, expected.Length - (int)offset)];
                int r = 0;
                while (r < buf.Length && (n = source.Read(buf.AsSpan(r))) > 0) r += n;
                if (r == buf.Length && buf.AsSpan().SequenceEqual(expected.AsSpan((int)offset, buf.Length))) seeksOk++;
            }
            return string.Create(CultureInfo.InvariantCulture,
                $"bytes={expected.Length} identical={(whole ? 1 : 0)} seeks={seeksOk}/3 fullReadMs={fullMs:0.0} bufferingNow={NetworkByteSource.Buffering}");
        }
        finally { source.Close(); }
    }

    /// <summary>Live managed bytes after a compacting full collection (heap size less fragmentation): what the objects
    /// still reachable really take, which <c>GC.GetTotalMemory</c> does not say on a fragmented heap.</summary>
    static long LiveBytes()
    {
        for (int i = 0; i < 2; i++)
        {
            System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }
        GCMemoryInfo info = GC.GetGCMemoryInfo(GCKind.FullBlocking);
        return info.HeapSizeBytes - info.FragmentedBytes;
    }

    static long Id(LocalIndex index, string path) => index.TryGetByPath(path, out LocalEntry? e) ? e.LocalId : -1;

    static string Timed(string what, Func<bool> landed, Func<bool> keptId)
    {
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < 15_000)
        {
            if (landed()) return string.Create(CultureInfo.InvariantCulture, $"change-test {what} landedMs={clock.Elapsed.TotalMilliseconds:0} keptId={(keptId() ? 1 : 0)}");
            Thread.Sleep(10);
        }
        return "change-test " + what + " did not land within 15 s";
    }
}
