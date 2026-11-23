// P1B-STUB: delete when rebased onto P1a
// ── Playback/LocalTags.P1bStub.cs ───────────────────────────────────────────────────────────────────────────────────
// stand-ins for P1a's `LocalTags` and `LocalMatch`, with exactly the public members of the shared contract
//
// Role: CORE (scan threads)
// Spec: C:\WAVEE\fgpu\handoff\request-plans\240-p1-contract.md (the two signatures P1b codes against)
//
// WHY. P1b (the index and the scanner) builds in parallel with P1a (the readers and the matcher). This file lets P1b compile
// and run against the contract until P1a lands; the rebase onto P1a deletes it whole. It is never the real thing:
//   - `TryRead` answers the file name as the title and `Format.Unknown` (so every read file is indexed as unplayable), but it
//     does the I/O a real reader does (one 64 KB head read and one 160-byte tail read on the handle) so the scan probe's
//     cold numbers carry the read cost.
//   - `LocalMatch` keys are `ToUpperInvariant` with whitespace collapsed; `Loose` keeps letters and digits only.

using System.Buffers;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Wavee;

// P1B-STUB: delete when rebased onto P1a
public static partial class LocalTags
{
    /// <summary>P1B-STUB. Opens <paramref name="path"/> and answers as <see cref="TryRead(SafeFileHandle, long, ReadOnlySpan{char}, out Spotify.Audio.Format, out LocalTagSet, out string)"/>.</summary>
    public static bool TryRead(string path, out Spotify.Audio.Format format, out LocalTagSet tags, out string why)
    {
        try
        {
            using SafeFileHandle file = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return TryRead(file, RandomAccess.GetLength(file), Path.GetFileName(path.AsSpan()), out format, out tags, out why);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            format = Spotify.Audio.Format.Unknown;
            tags = Empty(LocalKind.Audio);
            why = "Could not open the file (" + ex.GetType().Name + ")";
            return false;
        }
    }

    /// <summary>P1B-STUB. One 64 KB head read and one 160-byte tail read (the real reader's I/O shape), then the file name as the
    /// title and <c>Format.Unknown</c>.</summary>
    public static bool TryRead(SafeFileHandle file, long length, ReadOnlySpan<char> fileName,
                               out Spotify.Audio.Format format, out LocalTagSet tags, out string why)
    {
        format = Spotify.Audio.Format.Unknown;
        tags = Empty(IsVideoExtension(fileName) ? LocalKind.Video : LocalKind.Audio);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            int head = RandomAccess.Read(file, buffer.AsSpan(0, (int)Math.Min(length, 64 * 1024)), 0);
            if (length > 64 * 1024) RandomAccess.Read(file, buffer.AsSpan(0, 160), length - 160);
            why = head > 0 ? "No reader in this build yet (P1b stub)" : "The file is empty";
            return head > 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            why = "Could not read the file (" + ex.GetType().Name + ")";
            return false;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>P1B-STUB. The title tag, else the file name without its extension.</summary>
    public static string TitleOrFileName(in LocalTagSet tags, ReadOnlySpan<char> fileName)
        => tags.Title.Length > 0 ? tags.Title : Path.GetFileNameWithoutExtension(fileName).ToString();

    /// <summary>P1B-STUB. .mp3 .flac .ogg .oga .m4a .mp4 .m4v .aac .wav .opus .mkv .webm</summary>
    public static bool IsScanExtension(ReadOnlySpan<char> fileName)
    {
        ReadOnlySpan<char> ext = Path.GetExtension(fileName);
        return ext.Equals(".mp3", StringComparison.OrdinalIgnoreCase) || ext.Equals(".flac", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".ogg", StringComparison.OrdinalIgnoreCase) || ext.Equals(".oga", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".m4a", StringComparison.OrdinalIgnoreCase) || ext.Equals(".mp4", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".m4v", StringComparison.OrdinalIgnoreCase) || ext.Equals(".aac", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".wav", StringComparison.OrdinalIgnoreCase) || ext.Equals(".opus", StringComparison.OrdinalIgnoreCase)
            || IsVideoExtension(fileName);
    }

    static bool IsVideoExtension(ReadOnlySpan<char> fileName)
    {
        ReadOnlySpan<char> ext = Path.GetExtension(fileName);
        return ext.Equals(".mkv", StringComparison.OrdinalIgnoreCase) || ext.Equals(".webm", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".m4v", StringComparison.OrdinalIgnoreCase);
    }

    static LocalTagSet Empty(LocalKind kind)
        => new(kind, "", [], "", "", 0, 0, 0, 0, false, 0, LocalCover.None, 0, 0, true);
}

// P1B-STUB: delete when rebased onto P1a
public static class LocalMatch
{
    /// <summary>P1B-STUB. Trimmed, inner whitespace runs collapsed to one space, upper-invariant (no NFKC).</summary>
    public static string Exact(string text)
    {
        if (text.Length == 0) return "";
        var sb = new StringBuilder(text.Length);
        bool space = false;
        foreach (char c in text.AsSpan().Trim())
        {
            if (char.IsWhiteSpace(c)) { space = true; continue; }
            if (space) { sb.Append(' '); space = false; }
            sb.Append(char.ToUpperInvariant(c));
        }
        return sb.ToString();
    }

    /// <summary>P1B-STUB. <see cref="Exact"/>, then only letters and digits.</summary>
    public static string Loose(string text)
    {
        string exact = Exact(text);
        var sb = new StringBuilder(exact.Length);
        foreach (char c in exact)
            if (char.IsLetterOrDigit(c)) sb.Append(c);
        return sb.ToString();
    }

    public static string Key(string title, string artist) => title + "\u001f" + artist;

    public static bool DurationAgrees(int uriSeconds, int entryMs, int toleranceSeconds)
        => uriSeconds <= 0 || entryMs <= 0 || Math.Abs(entryMs / 1000 - uriSeconds) <= toleranceSeconds;

    /// <summary>P1B-STUB. Album equal (Exact) first, then the smallest duration difference, then lossless, then the higher
    /// bitrate, then the ordinal-smallest path.</summary>
    public static int Pick(ReadOnlySpan<LocalEntry> candidates, string album, int uriSeconds)
    {
        if (candidates.Length == 0) return -1;
        string albumKey = Exact(album);
        int best = 0;
        for (int i = 1; i < candidates.Length; i++)
            if (Better(candidates[i], candidates[best], albumKey, uriSeconds)) best = i;
        return best;

        static bool Better(LocalEntry a, LocalEntry b, string albumKey, int seconds)
        {
            bool aAlbum = albumKey.Length > 0 && Exact(a.Tags.Album) == albumKey, bAlbum = albumKey.Length > 0 && Exact(b.Tags.Album) == albumKey;
            if (aAlbum != bAlbum) return aAlbum;
            int aDiff = seconds > 0 ? Math.Abs(a.Tags.DurationMs / 1000 - seconds) : 0, bDiff = seconds > 0 ? Math.Abs(b.Tags.DurationMs / 1000 - seconds) : 0;
            if (aDiff != bDiff) return aDiff < bDiff;
            bool aLossless = a.Format is Spotify.Audio.Format.Flac or Spotify.Audio.Format.Flac24, bLossless = b.Format is Spotify.Audio.Format.Flac or Spotify.Audio.Format.Flac24;
            if (aLossless != bLossless) return aLossless;
            if (a.Tags.BitrateKbps != b.Tags.BitrateKbps) return a.Tags.BitrateKbps > b.Tags.BitrateKbps;
            return string.CompareOrdinal(a.RelativePath, b.RelativePath) < 0;
        }
    }
}
