// ── Platform/NtDirectory.cs ─────────────────────────────────────────────────────────────────────────────────────────
// the local index's directory walker: one NtQueryDirectoryFile call lists a whole folder's names, sizes, times, attributes
// and file ids without opening a single file
//
// Role: PLATFORM (scan threads only; never the UI or audio thread)
// Spec: C:\WAVEE\fgpu\handoff\request-plans\240-local-playlist-files-implementation.md §3.6 "Enumeration";
//       C:\WAVEE\fgpu\handoff\localindex-research\windows-filesystem.md §1, §4, §5, §7
//
// WHY NOT FileSystemEnumerable. .NET makes the same kernel call with a 4 KB buffer and `FileFullDirectoryInformation`, and
// drops the file id and ChangeTime, both of which the index keys on (a move keeps its id; a tag editor that restores the
// write time still moves ChangeTime). Here: `FileIdExtdDirectoryInformation` (128-bit id, ChangeTime, reparse tag) into a
// pooled 64 KB buffer, with a per-volume fallback to `FileIdBothDirectoryInformation` (64-bit id; the reparse tag rides in
// EaSize) where a file system or an SMB server refuses the class. Subdirectories open relative to their parent's handle
// (`OBJECT_ATTRIBUTES.RootDirectory`), so no full path is parsed per folder.
//
// WHAT IT NEVER DOES. Open a file to learn that it did not change; follow a reparse-point DIRECTORY (junctions and symlinks:
// cycles); open a cloud placeholder (`IsCloudOnly`: opening one downloads it). Reparse-point FILES are listed: OneDrive
// placeholders are reparse points.
//
// TIMES are DateTime ticks (UTC, 100 ns since 0001-01-01), converted from the NT FILETIME the listing carries; 0 = the
// volume did not report it (ChangeTime on some FAT/exFAT and SMB servers).

using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Wavee;

/// <summary>One child of a listed directory (see the file header).</summary>
public readonly record struct NtEntry(string Name, uint Attributes, long Size, long WriteTicks, long ChangeTicks, UInt128 FileId, uint ReparseTag)
{
    public bool IsDirectory => (Attributes & NtDirectory.AttrDirectory) != 0;
    public bool IsReparsePoint => (Attributes & NtDirectory.AttrReparsePoint) != 0;
    public bool IsCloudOnly => NtDirectory.IsCloudOnly(Attributes);
}

/// <summary>How a root's medium is read (plan D14): directory queries and tag reads in flight per medium.</summary>
public enum NtMedium : byte { Unknown, Ssd, Hdd, Usb, Network }

/// <summary>What the scanner needs to know about the volume a root lives on.</summary>
public readonly record struct NtVolume(uint Serial, string FileSystem, uint Flags, NtMedium Medium)
{
    /// <summary>NTFS and ReFS keep a file's id across a rename or a move on the volume; FAT, exFAT and SMB do not reliably.</summary>
    public bool StableIds => Medium != NtMedium.Network && FileSystem is "NTFS" or "ReFS";
    /// <summary>The volume can keep a change journal (NTFS; ReFS's V3 records are read too). By file system, not by
    /// FILE_SUPPORTS_USN_JOURNAL: Windows 11 NTFS was seen not to report that bit. The journal query is the real probe.</summary>
    public bool SupportsJournal => Medium != NtMedium.Network && FileSystem is "NTFS" or "ReFS";
    /// <summary>Directory queries in flight (D14): SSD 8, HDD 2, USB 3, SMB 6.</summary>
    public int ListWorkers => Medium switch { NtMedium.Hdd => 2, NtMedium.Usb => 3, NtMedium.Network => 6, _ => 8 };
    /// <summary>Tag reads in flight (D14): SSD 12, HDD 1, USB 3, SMB 12.</summary>
    public int ReadWorkers => Medium switch { NtMedium.Hdd => 1, NtMedium.Usb => 3, NtMedium.Network => 12, _ => 12 };
}

/// <summary>The NT directory walker and the few handle calls the scanner makes (see the file header). Thread-safe; every
/// method is a plain call on the calling (scan) thread.</summary>
public static partial class NtDirectory
{
    public const uint AttrReadOnly = 0x1, AttrHidden = 0x2, AttrSystem = 0x4, AttrDirectory = 0x10, AttrReparsePoint = 0x400,
        AttrOffline = 0x1000, AttrRecallOnOpen = 0x0004_0000, AttrPinned = 0x0008_0000, AttrUnpinned = 0x0010_0000,
        AttrRecallOnDataAccess = 0x0040_0000;

    /// <summary>The attribute bits that mean "opening this file would download it" (§3.6 "Cloud files").</summary>
    public const uint CloudMask = AttrOffline | AttrRecallOnOpen | AttrRecallOnDataAccess;

    public static bool IsCloudOnly(uint attributes) => (attributes & CloudMask) != 0;

    const int BufferBytes = 64 * 1024;
    const int ClassIdBoth = 37, ClassIdExtd = 60;
    const int StatusNoMoreFiles = unchecked((int)0x8000_0006), StatusNoSuchFile = unchecked((int)0xC000_000F),
        StatusInvalidInfoClass = unchecked((int)0xC000_0003), StatusInvalidParameter = unchecked((int)0xC000_000D),
        StatusNotSupported = unchecked((int)0xC000_00BB), StatusNotImplemented = unchecked((int)0xC000_0002);
    const long FileTimeToTicks = 504_911_232_000_000_000L;

    /// <summary>Volume serials whose file system refused <c>FileIdExtdDirectoryInformation</c> (the per-volume fallback).</summary>
    static readonly ConcurrentDictionary<uint, byte> s_noExtd = new();

    // ── opening ──────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Open a directory for listing: a full Win32 path when <paramref name="parent"/> is null, else
    /// <paramref name="name"/> relative to the parent's handle (one name, or a relative path with backslashes). Null on
    /// failure with the NTSTATUS in <paramref name="status"/>. Never follows a reparse point below a root.</summary>
    public static SafeFileHandle? OpenDirectory(SafeFileHandle? parent, string name, out int status)
    {
        const uint access = 0x1 /*LIST_DIRECTORY*/ | 0x20 /*TRAVERSE*/ | 0x80 /*READ_ATTRIBUTES*/ | 0x0010_0000 /*SYNCHRONIZE*/;
        uint options = 0x1 /*DIRECTORY_FILE*/ | 0x20 /*SYNCHRONOUS_IO_NONALERT*/ | 0x4000 /*OPEN_FOR_BACKUP_INTENT*/;
        if (parent is not null) options |= 0x0020_0000;   // FILE_OPEN_REPARSE_POINT: a child is never followed
        return Open(parent, parent is null ? ToNtPath(name) : name, access, options, out status);
    }

    /// <summary>Open a file for a header read relative to its directory's handle (no read-ahead: a header is 64 KB at most).
    /// Never call it for a cloud placeholder.</summary>
    public static SafeFileHandle? OpenFileForRead(SafeFileHandle directory, string name, out int status)
    {
        const uint access = 0x1 /*READ_DATA*/ | 0x80 /*READ_ATTRIBUTES*/ | 0x0010_0000 /*SYNCHRONIZE*/;
        const uint options = 0x40 /*NON_DIRECTORY_FILE*/ | 0x20 /*SYNCHRONOUS_IO_NONALERT*/ | 0x800 /*RANDOM_ACCESS*/;
        return Open(directory, name, access, options, out status);
    }

    static unsafe SafeFileHandle? Open(SafeFileHandle? parent, string name, uint access, uint options, out int status)
    {
        bool added = false;
        try
        {
            parent?.DangerousAddRef(ref added);
            fixed (char* p = name)
            {
                var text = new UnicodeString { Length = (ushort)(name.Length * 2), MaximumLength = (ushort)(name.Length * 2), Buffer = p };
                var attributes = new ObjectAttributes
                {
                    Length = (uint)sizeof(ObjectAttributes),
                    RootDirectory = parent is null ? 0 : parent.DangerousGetHandle(),
                    ObjectName = &text,
                    Attributes = 0x40,   // OBJ_CASE_INSENSITIVE
                };
                IoStatusBlock io;
                nint handle;
                status = NtCreateFile(&handle, access, &attributes, &io, null, 0, 0x7 /*share all*/, 0x1 /*FILE_OPEN*/, options, null, 0);
                return status >= 0 ? new SafeFileHandle(handle, ownsHandle: true) : null;
            }
        }
        finally
        {
            if (added) parent!.DangerousRelease();
        }
    }

    /// <summary>A Win32 path as the NT object manager reads it (<c>C:\Music</c> → <c>\??\C:\Music</c>, <c>\\nas\m</c> →
    /// <c>\??\UNC\nas\m</c>). A drive root keeps its backslash (without it the name is the volume, not its root folder).</summary>
    public static string ToNtPath(string path)
    {
        string full = Path.GetFullPath(path);
        if (full.StartsWith(@"\\?\UNC\", StringComparison.Ordinal)) return @"\??\UNC\" + full[8..].TrimEnd('\\');
        if (full.StartsWith(@"\\?\", StringComparison.Ordinal) || full.StartsWith(@"\\.\", StringComparison.Ordinal)) return @"\??\" + full[4..];
        if (full.StartsWith(@"\\", StringComparison.Ordinal)) return @"\??\UNC\" + full[2..].TrimEnd('\\');
        return @"\??\" + (full.Length > 3 ? full.TrimEnd('\\') : full);
    }

    // ── listing ──────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>List every child of <paramref name="directory"/> ("." and ".." left out) into <paramref name="into"/>
    /// (cleared first). <paramref name="volumeSerial"/> keys the per-volume class fallback. False with the NTSTATUS on a
    /// failed query; the entries read before a mid-listing failure are discarded by the caller (the listing failed).</summary>
    public static unsafe bool List(SafeFileHandle directory, uint volumeSerial, List<NtEntry> into, out int status)
    {
        into.Clear();
        bool extd = !s_noExtd.ContainsKey(volumeSerial);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferBytes);
        bool added = false;
        try
        {
            directory.DangerousAddRef(ref added);
            nint handle = directory.DangerousGetHandle();
            bool restart = true;
            fixed (byte* b = buffer)
            {
                while (true)
                {
                    IoStatusBlock io;
                    status = NtQueryDirectoryFile(handle, 0, 0, 0, &io, b, BufferBytes, extd ? ClassIdExtd : ClassIdBoth, 0, null, restart ? (byte)1 : (byte)0);
                    if (status is StatusNoMoreFiles or StatusNoSuchFile) { status = 0; return true; }
                    if (status < 0 && extd && restart && status is StatusInvalidInfoClass or StatusInvalidParameter or StatusNotSupported or StatusNotImplemented)
                    {
                        s_noExtd.TryAdd(volumeSerial, 0);   // this volume answers only the 64-bit class: remember it, list again
                        extd = false;
                        continue;
                    }
                    if (status < 0) { into.Clear(); return false; }
                    restart = false;
                    Parse(b, extd, into);
                }
            }
        }
        finally
        {
            if (added) directory.DangerousRelease();
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    static unsafe void Parse(byte* b, bool extd, List<NtEntry> into)
    {
        int at = 0;
        while (true)
        {
            byte* e = b + at;
            uint next = *(uint*)e;
            long write = *(long*)(e + 24), change = *(long*)(e + 32), size = *(long*)(e + 40);
            uint attributes = *(uint*)(e + 56);
            int nameBytes = *(int*)(e + 60);
            uint tag;
            UInt128 id;
            char* name;
            if (extd)
            {
                tag = *(uint*)(e + 68);
                id = new UInt128(*(ulong*)(e + 80), *(ulong*)(e + 72));
                name = (char*)(e + 88);
            }
            else
            {
                tag = (attributes & AttrReparsePoint) != 0 ? *(uint*)(e + 64) : 0u;   // EaSize carries the reparse tag
                id = new UInt128(0, *(ulong*)(e + 96));
                name = (char*)(e + 104);
            }
            int chars = nameBytes / 2;
            if (!(chars == 1 && name[0] == '.') && !(chars == 2 && name[0] == '.' && name[1] == '.'))
                into.Add(new NtEntry(new string(name, 0, chars), attributes, size, Ticks(write), Ticks(change), id, tag));
            if (next == 0) return;
            at += (int)next;
        }
    }

    static long Ticks(long fileTime) => fileTime > 0 ? fileTime + FileTimeToTicks : 0;

    /// <summary>The listing rules of §3.6: is this child a folder the walk goes into? Not the recycle bin, not System Volume
    /// Information, not hidden or system, never a reparse point (junctions and symlinks are not followed).</summary>
    public static bool WalksInto(in NtEntry entry)
        => entry.IsDirectory && !entry.IsReparsePoint && (entry.Attributes & (AttrHidden | AttrSystem)) == 0
           && !entry.Name.Equals("$RECYCLE.BIN", StringComparison.OrdinalIgnoreCase)
           && !entry.Name.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase);

    // ── the volume ───────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The volume behind an open root handle and the medium of <paramref name="rootPath"/> (D14). Never throws.</summary>
    public static unsafe NtVolume VolumeOf(SafeFileHandle root, string rootPath)
    {
        uint serial = 0, flags = 0, maxComponent;
        string fileSystem = "";
        char* fs = stackalloc char[64];
        if (GetVolumeInformationByHandleW(root, null, 0, &serial, &maxComponent, &flags, fs, 64) != 0)
            fileSystem = new string(fs);
        return new NtVolume(serial, fileSystem, flags, MediumOf(rootPath));
    }

    /// <summary>The 128-bit id of an open file or directory (the root folder's own id: its listing gives its children's).</summary>
    public static unsafe UInt128 FileIdOf(SafeFileHandle handle)
    {
        byte* info = stackalloc byte[24];   // FILE_ID_INFO: ULONGLONG VolumeSerialNumber; FILE_ID_128 FileId
        if (GetFileInformationByHandleEx(handle, 18 /*FileIdInfo*/, info, 24) == 0) return 0;
        return new UInt128(*(ulong*)(info + 16), *(ulong*)(info + 8));
    }

    /// <summary>Is <paramref name="path"/> on a network share (a UNC path or a mapped remote drive)?</summary>
    public static bool IsNetworkPath(string path)
    {
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) return true;
        if (path.StartsWith(@"\\", StringComparison.Ordinal) && !path.StartsWith(@"\\?\", StringComparison.Ordinal) && !path.StartsWith(@"\\.\", StringComparison.Ordinal)) return true;
        string? root = Path.GetPathRoot(path);
        return root is { Length: > 0 } && GetDriveTypeW(root) == 4 /*DRIVE_REMOTE*/;
    }

    /// <summary>SSD, HDD, USB or network (D14): UNC and remote drives are network; removable drives and fixed drives on a USB
    /// bus are USB; the rest by the storage stack's seek-penalty answer (unknown reads as SSD).</summary>
    public static unsafe NtMedium MediumOf(string path)
    {
        if (IsNetworkPath(path)) return NtMedium.Network;
        string? root = Path.GetPathRoot(Path.GetFullPath(path));
        if (root is not { Length: >= 2 } || root[1] != ':') return NtMedium.Unknown;
        uint type = GetDriveTypeW(root);
        if (type == 2 /*REMOVABLE*/) return NtMedium.Usb;
        // `\\.\X:` opened with no access rights: enough for IOCTL_STORAGE_QUERY_PROPERTY, and no admin needed.
        using SafeFileHandle device = CreateFileW(@"\\.\" + root[..2], 0, 0x3, 0, 3 /*OPEN_EXISTING*/, 0, 0);
        if (device.IsInvalid) return NtMedium.Unknown;
        byte* query = stackalloc byte[12];
        byte* answer = stackalloc byte[256];
        *(uint*)query = 0;          // StorageDeviceProperty
        *(uint*)(query + 4) = 0;    // PropertyStandardQuery
        uint got;
        if (DeviceIoControl(device, 0x002D_1400, query, 12, answer, 256, &got, 0) != 0 && got >= 32 && *(uint*)(answer + 28) == 7 /*BusTypeUsb*/)
            return NtMedium.Usb;
        *(uint*)query = 7;          // StorageDeviceSeekPenaltyProperty
        if (DeviceIoControl(device, 0x002D_1400, query, 12, answer, 256, &got, 0) != 0 && got >= 9)
            return answer[8] != 0 ? NtMedium.Hdd : NtMedium.Ssd;
        return NtMedium.Ssd;
    }

    // ── cloud placeholders, moves outside the folders, closing ──────────────────────────────────────────────────────

    static int s_placeholderMode;

    /// <summary>Once per process: <c>PHCM_EXPOSE_PLACEHOLDERS</c>, so OneDrive placeholders show their real attributes (the
    /// cloud bits) to the listing. It does not stop hydration: the scanner still never opens such a file.</summary>
    public static void ExposePlaceholders()
    {
        if (Interlocked.Exchange(ref s_placeholderMode, 1) != 0) return;
        try { RtlSetProcessPlaceholderCompatibilityMode(2); }
        catch (EntryPointNotFoundException) { }   // before Windows 10 1803: placeholders are already exposed to this app
    }

    /// <summary>Where a file with <paramref name="fileId"/> on the volume of <paramref name="volumeHint"/> lives now (NTFS/ReFS):
    /// <c>OpenFileById</c> with attribute access only (never hydrates), then <c>GetFinalPathNameByHandleW</c>. Null when no
    /// such file exists (deleted) or the call is refused. No admin rights, no scan.</summary>
    public static unsafe string? PathOfFileId(SafeFileHandle volumeHint, UInt128 fileId)
    {
        byte* descriptor = stackalloc byte[24];   // FILE_ID_DESCRIPTOR: DWORD dwSize; FILE_ID_TYPE Type; FILE_ID_128 ExtendedFileId
        *(uint*)descriptor = 24;
        *(uint*)(descriptor + 4) = 2;             // ExtendedFileIdType
        *(ulong*)(descriptor + 8) = (ulong)fileId;
        *(ulong*)(descriptor + 16) = (ulong)(fileId >> 64);
        using SafeFileHandle file = OpenFileById(volumeHint, descriptor, 0x80 /*READ_ATTRIBUTES*/, 0x7, 0, 0x0200_0000 /*BACKUP_SEMANTICS*/ | 0x0020_0000 /*OPEN_REPARSE_POINT*/);
        if (file.IsInvalid) return null;
        char* text = stackalloc char[1024];
        uint n = GetFinalPathNameByHandleW(file, text, 1024, 0 /*FILE_NAME_NORMALIZED | VOLUME_NAME_DOS*/);
        if (n == 0 || n >= 1024) return null;
        string path = new(text, 0, (int)n);
        if (path.StartsWith(@"\\?\UNC\", StringComparison.Ordinal)) return @"\\" + path[8..];
        return path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;
    }

    static readonly BlockingCollection<SafeHandle> s_closing = new(new ConcurrentQueue<SafeHandle>());
    static int s_closers;

    /// <summary>Close a handle on a small background pool: an antivirus filter can make <c>CloseHandle</c> cost 1-10 ms, which
    /// would otherwise sit on a scan worker (research §7).</summary>
    public static void CloseLater(SafeHandle? handle)
    {
        if (handle is null) return;
        if (Interlocked.CompareExchange(ref s_closers, 2, 0) == 0)
        {
            for (int i = 0; i < 2; i++)
                new Thread(static () => { foreach (SafeHandle h in s_closing.GetConsumingEnumerable()) h.Dispose(); })
                { IsBackground = true, Name = "Wavee.LocalScan.Close", Priority = ThreadPriority.BelowNormal }.Start();
        }
        s_closing.Add(handle);
    }

    // ── interop ──────────────────────────────────────────────────────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    unsafe struct UnicodeString { public ushort Length; public ushort MaximumLength; public char* Buffer; }

    [StructLayout(LayoutKind.Sequential)]
    unsafe struct ObjectAttributes
    {
        public uint Length;
        public nint RootDirectory;
        public UnicodeString* ObjectName;
        public uint Attributes;
        public nint SecurityDescriptor;
        public nint SecurityQualityOfService;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct IoStatusBlock { public nint Status; public nint Information; }

    [LibraryImport("ntdll.dll")]
    private static unsafe partial int NtCreateFile(nint* handle, uint access, ObjectAttributes* attributes, IoStatusBlock* io, long* allocationSize,
        uint fileAttributes, uint share, uint disposition, uint options, void* ea, uint eaLength);

    [LibraryImport("ntdll.dll")]
    private static unsafe partial int NtQueryDirectoryFile(nint handle, nint ev, nint apc, nint apcContext, IoStatusBlock* io, void* buffer, uint length,
        int infoClass, byte returnSingle, UnicodeString* fileName, byte restartScan);

    [LibraryImport("ntdll.dll")]
    private static partial sbyte RtlSetProcessPlaceholderCompatibilityMode(sbyte mode);

    [LibraryImport("ntdll.dll")]
    internal static partial uint RtlNtStatusToDosError(int status);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static unsafe partial int GetVolumeInformationByHandleW(SafeFileHandle file, char* name, uint nameSize, uint* serial, uint* maxComponent,
        uint* flags, char* fileSystem, uint fileSystemSize);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static unsafe partial int GetFileInformationByHandleEx(SafeFileHandle file, int infoClass, void* info, uint size);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint GetDriveTypeW(string root);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial SafeFileHandle CreateFileW(string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static unsafe partial int DeviceIoControl(SafeFileHandle device, uint code, void* input, uint inputSize, void* output, uint outputSize,
        uint* returned, nint overlapped);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static unsafe partial SafeFileHandle OpenFileById(SafeFileHandle volumeHint, void* descriptor, uint access, uint share, nint security, uint flags);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static unsafe partial uint GetFinalPathNameByHandleW(SafeFileHandle file, char* path, uint size, uint flags);
}
