// ── Playback/LocalScan.Journal.cs ───────────────────────────────────────────────────────────────────────────────────
// step 2 of the scan: catch up through the NTFS change journal without admin rights, so a start-up with nothing changed
// lists no folder at all
//
// Role: CORE (the scan thread)
// Spec: C:\WAVEE\fgpu\handoff\request-plans\240-local-playlist-files-implementation.md §3.6 step 2;
//       C:\WAVEE\fgpu\handoff\localindex-research\windows-filesystem.md §2
//
// THE CALL. `FSCTL_READ_UNPRIVILEGED_USN_JOURNAL` (0x903AB, Windows 10 1607+) on the volume opened with FILE_TRAVERSE only
// (`\\.\C:`), next to `FSCTL_QUERY_USN_JOURNAL`. Neither needs admin. Records carry the file's and its parent's ids, never a
// name — the index already holds every folder's id, so a record's parent id is all it takes to mark that folder dirty. The
// control code is undocumented, so every failure (refused, no journal, a new journal id, purged records, more records than a
// walk would cost) answers null and the caller walks the root instead: the walk is always correct.

using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Wavee;

public sealed partial class LocalScan
{
    const uint FsctlQueryUsnJournal = 0x0009_00F4, FsctlReadUnprivilegedUsnJournal = 0x0009_03AB;
    /// <summary>Past this many records a walk (fingerprints skip unchanged folders) is the cheaper catch-up.</summary>
    const int JournalRecordCap = 400_000;
    /// <summary>The read input this Windows accepts: V1 (48 bytes, V2 and V3 records), else V0 (40 bytes, V2 records).</summary>
    static uint s_journalInputBytes = 48;

    /// <summary>The volume handle for a root on a drive letter (<c>\\.\C:</c>, FILE_TRAVERSE only); null elsewhere.</summary>
    static SafeFileHandle? OpenVolume(string rootPath)
    {
        string full = Path.GetFullPath(rootPath);
        if (full.Length < 2 || full[1] != ':' || !char.IsAsciiLetter(full[0])) return null;
        SafeFileHandle h = NtDirectory.CreateFileW(@"\\.\" + full[..2], 0x20 /*FILE_TRAVERSE*/, 0x7, 0, 3 /*OPEN_EXISTING*/, 0, 0);
        if (!h.IsInvalid) return h;
        h.Dispose();
        return null;
    }

    /// <summary>The volume's journal: its id, its first and next USN, its lowest valid USN. False when there is none or the
    /// query is refused (with the Win32 error in the last out).</summary>
    public static unsafe bool TryQueryJournal(string rootPath, out ulong journalId, out long firstUsn, out long nextUsn, out int error)
    {
        journalId = 0; firstUsn = nextUsn = 0; error = 0;
        using SafeFileHandle? volume = OpenVolume(rootPath);
        if (volume is null) { error = Marshal.GetLastPInvokeError(); return false; }
        byte* data = stackalloc byte[80];   // USN_JOURNAL_DATA_V2 (V0 is the first 56 bytes)
        uint got;
        if (NtDirectory.DeviceIoControl(volume, FsctlQueryUsnJournal, null, 0, data, 80, &got, 0) == 0 || got < 56)
        {
            error = Marshal.GetLastPInvokeError();
            return false;
        }
        journalId = *(ulong*)data;
        firstUsn = Math.Max(*(long*)(data + 8), *(long*)(data + 24));   // FirstUsn, LowestValidUsn
        nextUsn = *(long*)(data + 16);
        return true;
    }

    /// <summary>Read the journal from the root's stored cursor to <paramref name="toUsn"/> and turn every record's parent id
    /// into a dirty folder of this root. Null: walk instead.</summary>
    HashSet<string>? JournalDirty(string rootPath, LocalRoot root, ulong journalId, long toUsn, LocalScanStats stats)
    {
        var parents = new HashSet<UInt128>();
        if (!TryReadJournal(rootPath, journalId, root.NextUsn, toUsn, parents, out int records, out int error))
        {
            stats.Journal = "read-error-" + error.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return null;
        }
        stats.JournalRecords = records;
        stats.Journal = "read-ok";
        var dirty = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (parents.Count == 0) return dirty;
        foreach (LocalFolder f in _index.FoldersOf(root.RootId))
            if (parents.Contains(f.FileId)) dirty.Add(f.RelativePath);
        return dirty;
    }

    /// <summary>Every record between two USNs: the parent ids (and the ids of changed directories) into
    /// <paramref name="parents"/>. False when the read is refused, the window is gone or the records pass the cap.</summary>
    public static unsafe bool TryReadJournal(string rootPath, ulong journalId, long fromUsn, long toUsn, HashSet<UInt128> parents, out int records, out int error)
    {
        records = 0; error = 0;
        if (fromUsn >= toUsn) return true;
        using SafeFileHandle? volume = OpenVolume(rootPath);
        if (volume is null) { error = Marshal.GetLastPInvokeError(); return false; }
        const int bufferBytes = 64 * 1024;
        byte* buffer = (byte*)NativeMemory.Alloc(bufferBytes);
        try
        {
            byte* input = stackalloc byte[48];   // READ_USN_JOURNAL_DATA_V1 (V0 = its first 40 bytes)
            long usn = fromUsn;
            uint inputBytes = Volatile.Read(ref s_journalInputBytes);
            while (usn < toUsn)
            {
                new Span<byte>(input, 48).Clear();
                *(long*)input = usn;                    // StartUsn
                *(uint*)(input + 8) = 0xFFFF_FFFF;      // ReasonMask: everything
                *(ulong*)(input + 32) = journalId;      // UsnJournalID: a restamped journal fails the call
                *(ushort*)(input + 40) = 2;             // MinMajorVersion (V1 only)
                *(ushort*)(input + 42) = 3;             // MaxMajorVersion (ReFS writes V3)
                uint got;
                if (NtDirectory.DeviceIoControl(volume, FsctlReadUnprivilegedUsnJournal, input, inputBytes, buffer, bufferBytes, &got, 0) == 0)
                {
                    error = Marshal.GetLastPInvokeError();   // 1181 entry deleted, 1179 not active, 1178 deleting, 1 refused
                    if (error == 87 && inputBytes != 40) { inputBytes = 40; Volatile.Write(ref s_journalInputBytes, 40); continue; }   // V0 input
                    return false;
                }
                if (got <= 8) break;
                long next = *(long*)buffer;
                int at = 8;
                while (at + 8 <= got)
                {
                    byte* r = buffer + at;
                    uint length = *(uint*)r;
                    if (length == 0 || at + length > got) break;
                    ushort major = *(ushort*)(r + 4);
                    long recordUsn;
                    if (major == 2)
                    {
                        parents.Add(new UInt128(0, *(ulong*)(r + 16)));
                        if ((*(uint*)(r + 52) & NtDirectory.AttrDirectory) != 0) parents.Add(new UInt128(0, *(ulong*)(r + 8)));
                        recordUsn = *(long*)(r + 24);
                    }
                    else if (major == 3)
                    {
                        parents.Add(new UInt128(*(ulong*)(r + 32), *(ulong*)(r + 24)));
                        if ((*(uint*)(r + 68) & NtDirectory.AttrDirectory) != 0) parents.Add(new UInt128(*(ulong*)(r + 16), *(ulong*)(r + 8)));
                        recordUsn = *(long*)(r + 40);
                    }
                    else recordUsn = usn;
                    if (recordUsn >= toUsn) return true;   // later changes are read next time from the cursor stored now
                    if (++records > JournalRecordCap) { error = -1; return false; }   // -1: past the cap (the read itself works)
                    at += (int)length;
                }
                if (next <= usn) break;
                usn = next;
            }
            return true;
        }
        finally
        {
            NativeMemory.Free(buffer);
        }
    }

    /// <summary>README check 1, as data: can this process read the journal of <paramref name="rootPath"/>'s volume without
    /// admin rights? One line for the probe.</summary>
    public static string JournalCheck(string rootPath)
    {
        if (!TryQueryJournal(rootPath, out ulong id, out long first, out long next, out int error))
            return "journal query refused error=" + error;
        var parents = new HashSet<UInt128>();
        long from = first;   // from the journal's first record (a USN inside a record is refused); capped at JournalRecordCap
        var clock = System.Diagnostics.Stopwatch.StartNew();
        bool ok = TryReadJournal(rootPath, id, from, next, parents, out int records, out int readError);
        return ok
            ? $"journal query ok read ok records={records} parents={parents.Count} ms={clock.Elapsed.TotalMilliseconds:0.0} elevated={(Environment.IsPrivilegedProcess ? 1 : 0)}"
            : readError == -1 ? $"journal query ok read ok (stopped at the cap) records={records} ms={clock.Elapsed.TotalMilliseconds:0.0} elevated={(Environment.IsPrivilegedProcess ? 1 : 0)}"
            : $"journal query ok read refused error={readError} input={s_journalInputBytes} elevated={(Environment.IsPrivilegedProcess ? 1 : 0)}";
    }
}
