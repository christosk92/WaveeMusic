// ── Playback/LocalLink.Identity.cs ──────────────────────────────────────────────────────────────────────────────────
// which file each kept decision is about: a file identity beside every LocalId `locallinks.tsv` names, so a decision never lands
// on another file (#240, LocalId safety)
//
// Role: SHELL (UI thread: the check runs at the load and when a scan goes idle; HighestIdIn runs on the index's load thread)
// Spec: C:\WAVEE\fgpu\handoff\240-0dm-suggestions.md §1 "changing the index format reassigns LocalIds", option A0
//
// THE RISK. The links, renames, Play-by-default choices and picture choices are keyed by LocalId. An index that started over (a corrupt
// bin, a format this build could not read, an older build) would hand the ids out again from 1, and every decision would silently
// attach to another file. Three guards, outermost first:
//   1. LocalIndex never hands an id out twice: its load starts at the high-water mark kept outside the bin and above the highest id
//      this file names (`HighestIdIn`, read before the load).
//   2. Every id this file names carries its file's identity (an `I` record). At the load, a decision whose id now belongs to a file
//      that does not match is dropped (`local.link.identity … dropped=N`). A file matches by its file id (same volume), by its full path,
//      or by name + size + write time (a copy to another volume keeps those; the index pairs such a move by them too).
//   3. A decision whose id is no longer in the index (the index started over and gave the file a new id) is kept, and moves to the
//      file at its recorded path once a scan has indexed it (`rebound=N`).
// A row written by an older build has no identity: it keeps working, and takes its file's identity at the first check (`backfilled`).
// An older build skips `I` records and writes the file without them; the next check backfills them again.
//
// THE RECORD.  I  local-id  volume-serial(hex)  file-id(hex)  size  write-ticks  full-path

using System.Globalization;
using System.Text;

namespace Wavee;

public static partial class LocalLink
{
    /// <summary>What a kept decision's file was: enough to tell it from another file that took its LocalId.</summary>
    sealed record FileIdentity(uint Volume, UInt128 FileId, long Size, long WriteTicks, string Path)
    {
        public bool Matches(FileIdentity now)
        {
            if (FileId != 0 && now.FileId != 0 && Volume == now.Volume && FileId == now.FileId) return true;
            if (string.Equals(Path, now.Path, StringComparison.OrdinalIgnoreCase)) return true;
            return Size == now.Size && WriteTicks != 0 && WriteTicks == now.WriteTicks
                   && string.Equals(System.IO.Path.GetFileName(Path), System.IO.Path.GetFileName(now.Path), StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>LocalId → its file's identity as last seen.</summary>
    static readonly Dictionary<long, FileIdentity> s_identity = new();
    static int s_identityChecked = -1;

    static FileIdentity? IdentityNow(long localId)
        => LocalLibrary.Index.TryGet(localId, out LocalEntry? e)
            ? new FileIdentity(e.VolumeSerial, e.FileId, e.Size, e.WriteTicks, LocalLibrary.Index.FullPathOf(e) ?? "")
            : null;

    /// <summary>Every LocalId a kept decision names: a link, a song group, a rename, a song's default version, a picture choice.</summary>
    static HashSet<long> ReferencedIds()
    {
        var ids = new HashSet<long>();
        foreach (long id in s_links.Keys) ids.Add(id);
        foreach (string key in s_decided.Keys) if (SongIdOf(key) is var id and > 0) ids.Add(id);
        foreach (long id in s_renames.Keys) ids.Add(id);
        foreach (long id in s_defaults.Values) if (id > 0) ids.Add(id);
        foreach (long id in s_thumbs.Keys) ids.Add(id);
        return ids;
    }

    static long SongIdOf(string key)
        => key.StartsWith("song:", StringComparison.Ordinal) && long.TryParse(key.AsSpan(5), NumberStyles.None, CultureInfo.InvariantCulture, out long id) ? id : 0;

    /// <summary>The check (see the file header): at the load (<paramref name="load"/>: a mismatch drops the decision) and whenever a scan
    /// has gone idle since the index last moved (identities follow their files; a decision whose id left the index moves to the file at its
    /// recorded path). Saves when anything changed. UI thread.</summary>
    public static void CheckIdentities(bool load = false)
    {
        if (!s_loaded || s_memoryOnly) return;
        int version = LocalLibrary.Index.Version;
        if (!load && version == s_identityChecked) return;
        if (!load) s_identityChecked = version;   // the load's pass leaves the idle pass to run once after it
        int kept = 0, dropped = 0, backfilled = 0, orphans = 0, rebound = 0;
        bool changed = false;
        foreach (long id in ReferencedIds())
        {
            FileIdentity? now = IdentityNow(id);
            s_identity.TryGetValue(id, out FileIdentity? was);
            if (now is not null)
            {
                if (was is null) { s_identity[id] = now; backfilled++; changed = true; continue; }
                // At the load the index may have started over: a file that is not the one decided about lost its id's decisions. Later the
                // index's own pairing (D13) is the judge: a LocalId never moves to another file while the app runs.
                if (load && !was.Matches(now)) { Forget(id); dropped++; changed = true; continue; }
                if (was != now) { s_identity[id] = now; changed = true; }
                kept++;
                continue;
            }
            if (was is null) { orphans++; continue; }
            if (!load && LocalLibrary.Index.TryGetByPath(was.Path, out LocalEntry? at) && at.LocalId != id && !IsReferenced(at.LocalId))
            {
                Move(id, at.LocalId);
                s_identity.Remove(id);
                s_identity[at.LocalId] = new FileIdentity(at.VolumeSerial, at.FileId, at.Size, at.WriteTicks, LocalLibrary.Index.FullPathOf(at) ?? "");
                rebound++;
                changed = true;
                continue;
            }
            orphans++;
        }
        // Identities of ids no decision names any more go.
        if (s_identity.Count > 0)
        {
            var named = ReferencedIds();
            var gone = new List<long>();
            foreach (long id in s_identity.Keys) if (!named.Contains(id)) gone.Add(id);
            foreach (long id in gone) s_identity.Remove(id);
            changed |= gone.Count > 0;
        }
        if (load || dropped + backfilled + rebound > 0)
            Log.Info("local.link", "local.link.identity " + (load ? "load" : "idle") + " kept=" + kept + " dropped=" + dropped + " backfilled=" + backfilled
                + " rebound=" + rebound + " orphans=" + orphans);
        if (!changed) return;
        if (dropped + rebound > 0) Publish();
        Save();
    }

    static bool IsReferenced(long id)
        => s_links.ContainsKey(id) || s_renames.ContainsKey(id) || s_thumbs.ContainsKey(id) || s_decided.ContainsKey(SongKey(id)) || s_defaults.ContainsValue(id);

    /// <summary>Drop every decision about <paramref name="id"/> (its file is not the one they were made for).</summary>
    static void Forget(long id)
    {
        s_links.Remove(id);
        s_linkGroup.Remove(id);
        s_renames.Remove(id);
        s_thumbs.Remove(id);
        s_identity.Remove(id);
        string song = SongKey(id);
        s_decided.Remove(song);
        s_groups.Remove(song);
        foreach (var (uri, file) in s_defaults.ToArray()) if (file == id) s_defaults.Remove(uri);
    }

    /// <summary>Move every decision about <paramref name="from"/> to <paramref name="to"/> (the same file under a new id).</summary>
    static void Move(long from, long to)
    {
        if (s_links.Remove(from, out LinkTrack? link)) s_links[to] = link;
        if (s_renames.Remove(from, out LocalRename? rename)) s_renames[to] = rename;
        if (s_thumbs.Remove(from, out long thumb)) s_thumbs[to] = thumb;
        string fromSong = SongKey(from), toSong = SongKey(to);
        if (s_decided.Remove(fromSong, out Decided? d)) s_decided[toSong] = d;
        s_groups.Remove(fromSong);
        if (s_linkGroup.Remove(from, out string? group)) s_linkGroup[to] = group == fromSong ? toSong : group;
        foreach (var (uri, file) in s_defaults.ToArray()) if (file == from) s_defaults[uri] = to;
    }

    static bool ReadIdentity(string[] f)
    {
        if (f[0] != "I" || f.Length < 7 || !long.TryParse(f[1], NumberStyles.None, CultureInfo.InvariantCulture, out long id)) return f[0] == "I";
        if (!uint.TryParse(f[2], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint volume)
            || !UInt128.TryParse(f[3], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out UInt128 fileId)
            || !long.TryParse(f[4], NumberStyles.None, CultureInfo.InvariantCulture, out long size)
            || !long.TryParse(f[5], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long write)) return true;
        s_identity[id] = new FileIdentity(volume, fileId, size, write, f[6]);
        return true;
    }

    static void WriteIdentities(StringBuilder sb, Func<string, string> clean)
    {
        foreach (long id in ReferencedIds())
        {
            // The file as it is now when the index holds it; else as it was last seen (an id waiting to be rebound).
            FileIdentity? i = IdentityNow(id) ?? (s_identity.TryGetValue(id, out FileIdentity? was) ? was : null);
            if (i is null) continue;
            s_identity[id] = i;
            sb.Append('I').Append('\t').Append(id.ToString(CultureInfo.InvariantCulture)).Append('\t').Append(i.Volume.ToString("x", CultureInfo.InvariantCulture))
              .Append('\t').Append(i.FileId.ToString("x", CultureInfo.InvariantCulture)).Append('\t').Append(i.Size.ToString(CultureInfo.InvariantCulture))
              .Append('\t').Append(i.WriteTicks.ToString(CultureInfo.InvariantCulture)).Append('\t').Append(clean(i.Path)).Append('\n');
        }
    }

    /// <summary>The highest LocalId <c>locallinks.tsv</c> under <paramref name="cacheDir"/> names (0: none), so the index never hands one of
    /// them out again. Read on the index's load thread, before the index loads; never throws.</summary>
    public static long HighestIdIn(string? cacheDir)
    {
        if (cacheDir is null) return 0;
        long max = 0;
        try
        {
            string path = Path.Combine(cacheDir, "locallinks.tsv");
            if (!File.Exists(path)) return 0;
            foreach (string line in File.ReadLines(path, Encoding.UTF8))
            {
                string[] f = line.Split('\t');
                if (f.Length < 2) continue;
                string field = f[0] switch { "L" or "R" or "T" or "I" => f[1], "D" when f.Length >= 3 => f[2], "G" => f[1].StartsWith("song:", StringComparison.Ordinal) ? f[1][5..] : "", _ => "" };
                if (long.TryParse(field, NumberStyles.None, CultureInfo.InvariantCulture, out long id) && id > max) max = id;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Warn("local.link", "local.link.highest unreadable", ex); }
        return max;
    }
}
