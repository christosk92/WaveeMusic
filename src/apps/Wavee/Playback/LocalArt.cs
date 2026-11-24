// ── Playback/LocalArt.cs ────────────────────────────────────────────────────────────────────────────────────────────
// album art for local files: the embedded front cover or a folder image, extracted on first display into the art cache and
// handed to every surface as a plain file:/// uri
//
// Role: SHELL (CoverUri/AlbumCoverUri: any thread, a dictionary probe under a lock; the extraction: one background worker)
// Spec: C:\WAVEE\fgpu\handoff\request-plans\240-local-playlist-files-implementation.md §3.6a, D8;
//       240-p1-contract.md revision 2 (Ogg base64 covers); 240-p2-contract.md (the seam: CoverUri, AlbumCoverUri, Epoch)
//
// AT SCAN nothing is read: an entry carries its cover's location (`LocalCover`), a folder the image names its listing saw.
// RESOLUTION, per file: the embedded front cover -> a folder image by fixed name (cover, folder, front, album, the album
// title; .jpg .jpeg .png .webp) -> the parent folder's, when this one is a disc folder (CD1, Disc 2, Disk 3) -> the only
// image in the folder -> none. No candidate is ever decoded to choose.
// ON FIRST DISPLAY `CoverUri` answers null and queues the file (duplicates merged, the newest request first). The worker
// reads the picture bytes once (unsynchronised ID3 and Ogg base64 decoded; an Ogg packet that spans pages has its page
// headers skipped), hashes them (xxHash3-64) and writes them once to Cache\art\<hash>.<ext> WITHOUT re-encoding; tracks of
// one album share the file. A folder image is used where it lies (its own file:/// uri). `Epoch` then moves (at most four
// times a second) and a bound surface reads `CoverUri` again. "None" and "failed" are remembered per (LocalId, size, write
// time), so a row never retries per frame; a changed file has new times and is looked at again.
// A VIDEO's cover (#240 local video) is its own embedded cover, else a STILL of it (`VideoFrame`: about 10 % in, at most 960 wide, in the
// video's own aspect), written once to Cache\art\<hash>.jpg where the hash names the file (path, size, write time): a later session finds
// it there and decodes nothing. Never a folder image: a video beside an album's cover.jpg is not that album. A dropped file (no LocalId)
// asks by its path (`FileCoverUri`); when the still lands its row is re-stated with it (`Playlist.StageLocalImages`).

using System.Buffers;
using System.Globalization;
using System.IO.Hashing;
using FluentGpu.Signals;
using Microsoft.Win32.SafeHandles;

namespace Wavee;

/// <summary>Local album art (see the file header).</summary>
public static class LocalArt
{
    /// <summary>Moves (UI thread, at most four times a second) when covers land: bind to it and read the uris again.</summary>
    public static readonly Signal<int> Epoch = new(0);

    static readonly string[] s_names = ["cover", "folder", "front", "album"];
    static readonly string[] s_exts = [".jpg", ".jpeg", ".png", ".webp"];
    const int MaxCoverBytes = 24 * 1024 * 1024;

    readonly record struct Memo(long Size, long Write, string? Uri);

    static readonly Lock s_gate = new();
    static readonly Dictionary<long, Memo> s_memo = new();
    static readonly HashSet<long> s_queued = new();
    static readonly Stack<long> s_work = new();
    static readonly AutoResetEvent s_wake = new(false);
    static Thread? s_worker;
    static int s_postPending;
    static Timer? s_post;
    static string? s_cacheDir;
    static readonly List<long> s_landed = new();   // covers found since the last UI post: their rows are re-stated with them
    static Dictionary<string, long[]>? s_albums;
    // The dropped files' covers, by full path (they have no LocalId): the same memo, queue and landing, keyed by the path.
    static readonly Dictionary<string, Memo> s_fileMemo = new(StringComparer.OrdinalIgnoreCase);
    static readonly HashSet<string> s_fileQueued = new(StringComparer.OrdinalIgnoreCase);
    static readonly Stack<string> s_fileWork = new();
    static readonly List<(string Path, string Uri)> s_fileLanded = new();
    static int s_albumsVersion = -1;

    /// <summary>The art cache folder (`Cache\art` beside the local index). Set once at start; defaults under the local folder.</summary>
    public static string CacheDir => s_cacheDir ??= Path.Combine(Platform.LocalFolder, "Wavee", "Cache", "art");

    /// <summary>Point the cache somewhere else (the inspector's fixtures use a temp folder).</summary>
    public static void UseCacheDir(string dir) => s_cacheDir = dir;

    /// <summary>file:/// uri of the cached cover for this file, or null (not extracted yet: queued; or none). Any thread.</summary>
    public static string? CoverUri(long localId)
    {
        if (!LocalLibrary.Index.TryGet(localId, out LocalEntry? e)) return null;
        lock (s_gate)
        {
            if (s_memo.TryGetValue(localId, out Memo m) && m.Size == e.Size && m.Write == e.WriteTicks) return m.Uri;
            if (s_queued.Add(localId)) s_work.Push(localId);
        }
        EnsureWorker();
        s_wake.Set();
        return null;
    }

    /// <summary>The cover already found for this file, or null (none yet, or none at all). A PEEK: never queues. Any thread.</summary>
    public static string? Known(long localId)
    {
        if (!LocalLibrary.Index.TryGet(localId, out LocalEntry? e)) return null;
        lock (s_gate) return s_memo.TryGetValue(localId, out Memo m) && m.Size == e.Size && m.Write == e.WriteTicks ? m.Uri : null;
    }

    /// <summary>A surface is SHOWING this track with no cover (the row's art lane, the player bar): ask for its file's cover, the "first display"
    /// above. When it lands the row is re-stated with it (<see cref="LocalLibrary.StageCovers"/>). A cover found earlier that the row does not carry
    /// yet is handed over on the next UI turn (never from inside the render that asked). Cheap for any other row: a flag and an empty image id.
    /// UI thread.</summary>
    public static void Want(Track t)
    {
        if (!t.IsValid || !t.IsLocal || !t.ImageId.IsEmpty) return;
        long id = LocalLibrary.LocalIdOf(t.Id);
        if (id == 0)
        {
            // A dropped video (no index entry): its still by its path.
            if (!t.IsLocalVideo || Playlist.LocalPathOf(t.Id) is not { } path || FileCoverUri(path) is not { } uri) return;
            lock (s_gate) { if (!s_fileLanded.Contains((path, uri))) s_fileLanded.Add((path, uri)); }
            NoteLanded();
            return;
        }
        if (CoverUri(id) is null) return;
        lock (s_gate) { if (!s_landed.Contains(id)) s_landed.Add(id); }
        NoteLanded();
    }

    /// <summary>file:/// uri of a dropped file's cover (its embedded cover, or a video's still), or null (not extracted yet: queued; or
    /// none). Memoised per path for the session; the still itself is on disk across sessions. Any thread.</summary>
    public static string? FileCoverUri(string fullPath)
    {
        lock (s_gate)
        {
            if (s_fileMemo.TryGetValue(fullPath, out Memo m)) return m.Uri;
            if (s_fileQueued.Add(fullPath)) s_fileWork.Push(fullPath);
        }
        EnsureWorker();
        s_wake.Set();
        return null;
    }

    /// <summary>The album's cover: the first track of (album artist or first artist, album) with art, same rules. Any thread.</summary>
    public static string? AlbumCoverUri(string albumArtistOrArtist, string album)
    {
        foreach (long id in AlbumIds(albumArtistOrArtist, album))
        {
            string? uri = CoverUri(id);
            if (uri is not null) return uri;
            lock (s_gate) { if (s_memo.TryGetValue(id, out Memo m) && m.Uri is null) continue; }   // known: no art; try the next track
            break;   // the first track without a verdict is now queued; the next epoch answers
        }
        return null;
    }

    static string AlbumKey(string artist, string album) => artist.ToUpperInvariant() + "\u001f" + album.ToUpperInvariant();

    static long[] AlbumIds(string artist, string album)
    {
        int version = LocalLibrary.Index.Version;
        Dictionary<string, long[]>? albums;
        lock (s_gate) albums = s_albumsVersion == version ? s_albums : null;
        if (albums is null)
        {
            var build = new Dictionary<string, List<long>>(StringComparer.Ordinal);
            foreach (LocalEntry e in LocalLibrary.Index.Snapshot())
            {
                if (e.Tags.Kind != LocalKind.Audio || e.Tags.Album.Length == 0) continue;
                string a = e.Tags.AlbumArtist.Length > 0 ? e.Tags.AlbumArtist : e.Tags.FirstArtist;
                string key = AlbumKey(a, e.Tags.Album);
                if (!build.TryGetValue(key, out List<long>? list)) build[key] = list = new List<long>();
                list.Add(e.LocalId);
            }
            albums = new Dictionary<string, long[]>(build.Count, StringComparer.Ordinal);
            foreach (var p in build) albums[p.Key] = p.Value.ToArray();
            lock (s_gate) { s_albums = albums; s_albumsVersion = version; }
        }
        return albums.TryGetValue(AlbumKey(artist, album), out long[]? ids) ? ids : [];
    }

    static void EnsureWorker()
    {
        if (s_worker is not null) return;
        lock (s_gate)
        {
            if (s_worker is not null) return;
            s_worker = new Thread(Work) { IsBackground = true, Name = "Wavee.LocalArt", Priority = ThreadPriority.BelowNormal };
            s_worker.Start();
        }
    }

    static void Work()
    {
        while (true)
        {
            long id = 0;
            string? file = null;
            lock (s_gate)
            {
                if (s_work.Count > 0) { id = s_work.Pop(); s_queued.Remove(id); }
                else if (s_fileWork.Count > 0) { file = s_fileWork.Pop(); s_fileQueued.Remove(file); }
            }
            if (file is not null) { WorkFile(file); continue; }
            if (id == 0) { s_wake.WaitOne(); continue; }
            if (!LocalLibrary.Index.TryGet(id, out LocalEntry? e)) continue;
            string? uri = null;
            try { uri = Resolve(e); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or ArgumentException)
            {
                Log.Info("local", "local.art failed file=" + Path.GetFileName(e.RelativePath) + " (" + ex.GetType().Name + ")");
            }
            lock (s_gate)
            {
                s_memo[id] = new Memo(e.Size, e.WriteTicks, uri);
                if (uri is not null) s_landed.Add(id);
            }
            if (uri is not null) NoteLanded();
        }
    }

    /// <summary>A dropped file's cover: read its tags, then the embedded cover or a video's still.</summary>
    static void WorkFile(string path)
    {
        string? uri = null;
        long size = 0, write = 0;
        try
        {
            var info = new FileInfo(path);
            if (info.Exists)
            {
                size = info.Length;
                write = info.LastWriteTimeUtc.Ticks;
                if (LocalTags.TryRead(path, out _, out LocalTagSet tags, out _) || tags.Kind == LocalKind.Video)
                {
                    if (tags.Cover.Exists && ReadEmbedded(path, tags.Cover) is { } bytes) uri = Store(bytes);
                    else if (tags.Kind == LocalKind.Video) uri = Still(path, size, write);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or ArgumentException)
        {
            Log.Info("local", "local.art failed file=" + Path.GetFileName(path) + " (" + ex.GetType().Name + ")");
        }
        lock (s_gate)
        {
            s_fileMemo[path] = new Memo(size, write, uri);
            if (uri is not null) s_fileLanded.Add((path, uri));
        }
        if (uri is not null) NoteLanded();
    }

    static void NoteLanded()
    {
        if (Interlocked.Exchange(ref s_postPending, 1) != 0) return;
        s_post ??= new Timer(static _ => Playback.ToUi(static () =>
        {
            Volatile.Write(ref s_postPending, 0);
            long[] landed;
            (string Path, string Uri)[] files;
            lock (s_gate)
            {
                landed = s_landed.ToArray(); s_landed.Clear();
                files = s_fileLanded.ToArray(); s_fileLanded.Clear();
            }
            if (landed.Length > 0) LocalLibrary.StageCovers(landed);
            if (files.Length > 0) Playlist.StageLocalImages(files);
            Epoch.Value++;
        }), null, Timeout.Infinite, Timeout.Infinite);
        s_post.Change(250, Timeout.Infinite);
    }

    // ── resolution ───────────────────────────────────────────────────────────────────────────────────────────────────

    static string? Resolve(LocalEntry e)
    {
        string? full = LocalLibrary.Index.FullPathOf(e);
        if (full is null) return null;
        if (e.Tags.Cover.Exists && e.State == LocalState.Present && ReadEmbedded(full, e.Tags.Cover) is { } bytes)
            return Store(bytes);
        if (e.Tags.Kind == LocalKind.Video) return e.State == LocalState.Present ? Still(full, e.Size, e.WriteTicks) : null;
        return FolderImage(e, full);
    }

    /// <summary>A video's still (<see cref="VideoFrame"/>), stored once as Cache\art\&lt;hash&gt;.jpg, the hash naming the file (its path,
    /// size and write time): a later session finds the file there and decodes nothing; a changed video names a new one.</summary>
    static string? Still(string fullPath, long size, long write)
    {
        string dir = CacheDir;
        string name = fullPath.ToUpperInvariant() + "|" + size.ToString(CultureInfo.InvariantCulture) + "|" + write.ToString(CultureInfo.InvariantCulture);
        string path = Path.Combine(dir, XxHash3.HashToUInt64(System.Runtime.InteropServices.MemoryMarshal.AsBytes(name.AsSpan())).ToString("x16", CultureInfo.InvariantCulture) + ".jpg");
        if (File.Exists(path)) return FileUri(path);
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        if (VideoFrame.Grab(fullPath) is not { } jpeg)
        {
            Log.Info("local", "local.art still failed file=" + Path.GetFileName(fullPath) + " why=" + VideoFrame.LastFailure);
            return null;
        }
        Directory.CreateDirectory(dir);
        string tmp = path + "." + Environment.ProcessId + ".tmp";
        File.WriteAllBytes(tmp, jpeg);
        File.Move(tmp, path, overwrite: true);
        Log.Info("local", "local.art still file=" + Path.GetFileName(fullPath) + " bytes=" + jpeg.Length
            + " ms=" + (int)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        return FileUri(path);
    }

    /// <summary>A folder image by fixed name, the album title, the parent's for a disc folder, else the only image.</summary>
    static string? FolderImage(LocalEntry e, string fullPath)
    {
        string folderRel = LocalIndex.FolderOf(e.RelativePath);
        string folderFull = Path.GetDirectoryName(fullPath) ?? "";
        if (Pick(e.RootId, folderRel, folderFull, e.Tags.Album) is { } uri) return uri;
        string leaf = Path.GetFileName(folderRel);
        if (folderRel.Length > 0 && IsDiscFolder(leaf))
        {
            string parentRel = LocalIndex.FolderOf(folderRel);
            if (Pick(e.RootId, parentRel, Path.GetDirectoryName(folderFull) ?? "", e.Tags.Album) is { } parentUri) return parentUri;
        }
        return LocalLibrary.Index.TryGetFolder(e.RootId, folderRel, out LocalFolder? f) && f.Images.Length == 1
            ? FileUri(Path.Combine(folderFull, f.Images[0])) : null;
    }

    static string? Pick(int rootId, string folderRel, string folderFull, string album)
    {
        if (!LocalLibrary.Index.TryGetFolder(rootId, folderRel, out LocalFolder? f) || f.Images.Length == 0) return null;
        foreach (string name in s_names)
            foreach (string image in f.Images)
                if (Path.GetFileNameWithoutExtension(image).Equals(name, StringComparison.OrdinalIgnoreCase) && HasImageExt(image))
                    return FileUri(Path.Combine(folderFull, image));
        if (album.Length > 0)
            foreach (string image in f.Images)
                if (Path.GetFileNameWithoutExtension(image).Equals(album, StringComparison.OrdinalIgnoreCase) && HasImageExt(image))
                    return FileUri(Path.Combine(folderFull, image));
        return null;
    }

    static bool HasImageExt(string name)
    {
        string ext = Path.GetExtension(name);
        foreach (string e in s_exts) if (ext.Equals(e, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>CD1, CD 2, Disc 2, Disk 3, Disc1 (case-insensitive).</summary>
    public static bool IsDiscFolder(string name)
    {
        ReadOnlySpan<char> s = name.AsSpan().Trim();
        foreach (string p in (string[])["cd", "disc", "disk"])
        {
            if (!s.StartsWith(p, StringComparison.OrdinalIgnoreCase)) continue;
            ReadOnlySpan<char> rest = s[p.Length..].TrimStart();
            return rest.Length is > 0 and <= 3 && int.TryParse(rest, NumberStyles.None, CultureInfo.InvariantCulture, out _);
        }
        return false;
    }

    static string FileUri(string path) => new Uri(path).AbsoluteUri;

    // ── the picture bytes ────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>One ranged read of the picture, decoded to the image bytes. Null when it cannot be read.</summary>
    static byte[]? ReadEmbedded(string path, LocalCover cover)
    {
        if (cover.Length <= 0 || cover.Length > MaxCoverBytes) return null;
        using SafeFileHandle file = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        long length = RandomAccess.GetLength(file);
        if (cover.Offset < 0 || cover.Offset >= length) return null;
        // Base64 inside Ogg pages: the range can hold page headers; read some slack past the packet bytes.
        int want = cover.Encoding == LocalCoverEncoding.Base64 ? (int)Math.Min(length - cover.Offset, cover.Length + cover.Length / 64 + 4096) : (int)Math.Min(length - cover.Offset, cover.Length);
        byte[] raw = new byte[want];
        int got = RandomAccess.Read(file, raw, cover.Offset);
        if (got <= 0) return null;
        return cover.Encoding switch
        {
            LocalCoverEncoding.Raw => got == raw.Length ? raw : raw[..got],
            LocalCoverEncoding.Unsync => Resync(raw.AsSpan(0, got)),
            LocalCoverEncoding.Base64 => FlacPicture(Base64Packet(raw.AsSpan(0, got), cover.Length)),
            _ => null,
        };
    }

    /// <summary>ID3 unsynchronisation undone: FF 00 -> FF.</summary>
    static byte[] Resync(ReadOnlySpan<byte> s)
    {
        var o = new byte[s.Length];
        int n = 0;
        for (int i = 0; i < s.Length; i++)
        {
            o[n++] = s[i];
            if (s[i] == 0xFF && i + 1 < s.Length && s[i + 1] == 0x00) i++;
        }
        return o[..n];
    }

    /// <summary>The base64 text of an Ogg METADATA_BLOCK_PICTURE, decoded: <paramref name="packetBytes"/> packet bytes from the
    /// start, skipping any Ogg page header inside ("OggS" followed by version 0, which no base64 text holds).</summary>
    static byte[]? Base64Packet(ReadOnlySpan<byte> s, int packetBytes)
    {
        byte[] text = ArrayPool<byte>.Shared.Rent(packetBytes);
        try
        {
            int n = 0, i = 0;
            while (i < s.Length && n < packetBytes)
            {
                if (s[i] == (byte)'O' && i + 27 <= s.Length && s[i + 1] == (byte)'g' && s[i + 2] == (byte)'g' && s[i + 3] == (byte)'S' && s[i + 4] == 0)
                {
                    int segments = s[i + 26];
                    i += 27 + segments;
                    continue;
                }
                text[n++] = s[i++];
            }
            int len = n;
            while (len > 0 && text[len - 1] is (byte)'\r' or (byte)'\n' or (byte)' ') len--;
            var outBuf = new byte[len / 4 * 3 + 3];
            return System.Buffers.Text.Base64.DecodeFromUtf8(text.AsSpan(0, len), outBuf, out _, out int written) == OperationStatus.Done
                ? outBuf[..written] : null;
        }
        finally { ArrayPool<byte>.Shared.Return(text); }
    }

    /// <summary>A FLAC PICTURE block's image data: type, mime, description, sizes, then the data.</summary>
    static byte[]? FlacPicture(byte[]? block)
    {
        if (block is null || block.Length < 32) return null;
        int at = 4;
        int mimeLen = BE(block, at); at += 4 + mimeLen;
        if (at + 4 > block.Length) return null;
        int descLen = BE(block, at); at += 4 + descLen + 16;
        if (at + 4 > block.Length) return null;
        int dataLen = BE(block, at); at += 4;
        return dataLen > 0 && at + dataLen <= block.Length ? block[at..(at + dataLen)] : null;

        static int BE(byte[] b, int i) => (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(i));
    }

    /// <summary>Write the image once to the cache under its content hash (no re-encode); its file:/// uri.</summary>
    static string? Store(byte[] bytes)
    {
        string? ext = ExtOf(bytes);
        if (ext is null) return null;
        string dir = CacheDir;
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, XxHash3.HashToUInt64(bytes).ToString("x16", CultureInfo.InvariantCulture) + ext);
        if (!File.Exists(path))
        {
            string tmp = path + "." + Environment.ProcessId + ".tmp";
            File.WriteAllBytes(tmp, bytes);
            File.Move(tmp, path, overwrite: true);
        }
        return FileUri(path);
    }

    /// <summary>The file extension by the image's own magic bytes, or null when it is not an image this app shows.</summary>
    static string? ExtOf(ReadOnlySpan<byte> b)
    {
        if (b.Length > 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return ".jpg";
        if (b.Length > 8 && b[0] == 0x89 && b[1] == (byte)'P' && b[2] == (byte)'N' && b[3] == (byte)'G') return ".png";
        if (b.Length > 12 && b[0] == (byte)'R' && b[1] == (byte)'I' && b[2] == (byte)'F' && b[3] == (byte)'F' && b[8] == (byte)'W' && b[9] == (byte)'E' && b[10] == (byte)'B' && b[11] == (byte)'P') return ".webp";
        if (b.Length > 6 && b[0] == (byte)'G' && b[1] == (byte)'I' && b[2] == (byte)'F') return ".gif";
        if (b.Length > 2 && b[0] == (byte)'B' && b[1] == (byte)'M') return ".bmp";
        return null;
    }

}
