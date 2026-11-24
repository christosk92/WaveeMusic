// ── Playback/LocalLibrary.Network.cs ────────────────────────────────────────────────────────────────────────────────
// network folders (NAS, SMB shares, mapped drives): stored as the network path, shown as the letter the user knows, signed in
// to by Windows, and never touched from the UI or audio thread while unreachable
//
// Role: SHELL (UI thread for the verbs; the sign-in dialog and the probes run on threads of their own)
// Spec: C:\WAVEE\fgpu\handoff\request-plans\240-local-playlist-files-implementation.md §3.16 (D17, D18); 240-local-files-ux.md S13
//
// ADDING. A folder picked on a mapped letter (`Z:\Music`) is stored as its UNC path (`\\nas\share\Music`,
// `WNetGetUniversalNameW`), so it survives a remapped letter and works from an elevated or packaged process where mapped letters
// are not visible. `DisplayOf` shows it back the way the user knows it: "Z:\Music (\\nas\share\Music)" while the letter maps there.
// SIGNING IN is Windows' job: Wavee never stores NAS credentials. `SignIn` opens the standard Windows credential dialog
// (`WNetAddConnection3W` with CONNECT_INTERACTIVE | CONNECT_PROMPT), which can remember them in Credential Manager, then walks.
// REACHABILITY. `IsUnreachable(path)` answers from the folder's last probe (no I/O): the play path refuses a song on an offline
// share at once instead of blocking the audio pump for the redirector's minute-long timeout.

using System.Runtime.InteropServices;

namespace Wavee;

public static partial class LocalLibrary
{
    /// <summary>A path as a music folder is stored: a mapped network letter becomes its UNC path; anything else is unchanged.</summary>
    public static unsafe string ToStoredPath(string path)
    {
        if (path.Length < 3 || path[1] != ':' || !NtDirectory.IsNetworkPath(path)) return path;
        uint size = 2048;
        byte* buffer = stackalloc byte[(int)size];
        // UNIVERSAL_NAME_INFO_LEVEL: the buffer starts with an LPWSTR to the UNC text, which sits later in the same buffer.
        if (WNetGetUniversalNameW(path, 1, buffer, ref size) != 0) return path;
        char* unc = *(char**)buffer;
        return unc is null ? path : new string(unc).TrimEnd('\\');
    }

    static readonly Lock s_displayGate = new();
    static (long At, Dictionary<string, string> Map) s_letters;

    /// <summary>The folder as the user knows it: "Z:\Music (\\nas\share\Music)" while a mapped letter points at its share, else
    /// the folder itself. UI thread (cached 30 s; one WNetGetConnection per network letter).</summary>
    public static string DisplayOf(string folder)
    {
        if (!folder.StartsWith(@"\\", StringComparison.Ordinal)) return folder;
        Dictionary<string, string> letters = MappedLetters();
        foreach ((string letter, string remote) in letters)
        {
            if (folder.Equals(remote, StringComparison.OrdinalIgnoreCase)) return letter + @"\ (" + folder + ")";
            if (folder.StartsWith(remote + @"\", StringComparison.OrdinalIgnoreCase))
                return letter + folder[remote.Length..] + " (" + folder + ")";
        }
        return folder;
    }

    static unsafe Dictionary<string, string> MappedLetters()
    {
        lock (s_displayGate)
        {
            if (s_letters.Map is not null && Environment.TickCount64 - s_letters.At < 30_000) return s_letters.Map;
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            char* remote = stackalloc char[1024];
            foreach (DriveInfo d in DriveInfo.GetDrives())
            {
                if (d.DriveType != DriveType.Network) continue;
                string letter = d.Name[..2];
                uint len = 1024;
                if (WNetGetConnectionW(letter, remote, ref len) == 0) map[letter] = new string(remote).TrimEnd('\\');
            }
            s_letters = (Environment.TickCount64, map);
            return map;
        }
    }

    /// <summary>The share part of a UNC folder (`\\nas\share\Music` -> `\\nas\share`), or null.</summary>
    public static string? ShareOf(string folder)
    {
        if (!folder.StartsWith(@"\\", StringComparison.Ordinal)) return null;
        int server = folder.IndexOf('\\', 2);
        if (server < 0) return null;
        int share = folder.IndexOf('\\', server + 1);
        return share < 0 ? folder : folder[..share];
    }

    /// <summary>"Sign in to \\nas" (S13): the Windows credential dialog for the folder's share, then a walk. Returns at once.</summary>
    public static void SignIn(string folder)
    {
        string? share = ShareOf(folder);
        if (share is null) return;
        new Thread(() =>
        {
            uint result = AddConnection(share);
            Log.Info("local", "local.signin result=" + result);
            if (result == 0 && !s_fixture) s_scan.RequestFull(folder);
        })
        { IsBackground = true, Name = "Wavee.LocalLibrary.SignIn" }.Start();
    }

    static unsafe uint AddConnection(string share)
    {
        fixed (char* remote = share)
        {
            var resource = new NetResource { Type = 1 /*RESOURCETYPE_DISK*/, RemoteName = remote };
            return WNetAddConnection3W(0, &resource, null, null, 0x8 /*CONNECT_INTERACTIVE*/ | 0x10 /*CONNECT_PROMPT*/);
        }
    }

    /// <summary>Is this file on a network folder whose last probe found it offline or needing a sign-in? No I/O: the play path asks
    /// it before touching the file, so a dead share is refused at once instead of blocking for the redirector's timeout.</summary>
    public static bool IsUnreachable(string path)
    {
        if (!NtDirectory.IsNetworkPath(path)) return false;
        foreach (string f in Folders)
            if (path.StartsWith(f, StringComparison.OrdinalIgnoreCase) && (path.Length == f.Length || path[f.Length] == '\\'))
                return StateOf(f) is LocalRootState.Offline or LocalRootState.SignInNeeded;
        return false;
    }

    [StructLayout(LayoutKind.Sequential)]
    unsafe struct NetResource
    {
        public uint Scope, Type, DisplayType, Usage;
        public char* LocalName, RemoteName, Comment, Provider;
    }

    [LibraryImport("mpr.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static unsafe partial uint WNetGetUniversalNameW(string localPath, uint infoLevel, void* buffer, ref uint bufferSize);

    [LibraryImport("mpr.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static unsafe partial uint WNetGetConnectionW(string localName, char* remoteName, ref uint length);

    [LibraryImport("mpr.dll")]
    private static unsafe partial uint WNetAddConnection3W(nint owner, NetResource* resource, char* password, char* userName, uint flags);
}
