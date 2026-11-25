// ── Playback/LocalLibrary.Reread.cs ─────────────────────────────────────────────────────────────────────────────────
// a new decoder set re-reads the files indexed as unplayable, once (#240, 0dm's "WAV first": the WAVs already indexed stay
// "Can't play" otherwise, because an unchanged file is never read again)
//
// Role: SHELL (a below-normal background thread after the index loads; the setting is written on the UI thread)
// Spec: C:\WAVEE\fgpu\handoff\240-0dm-suggestions.md §2 "The re-read"
//
// `DecoderSet` names what this build plays. It is kept in a setting (not the index: no format bump), and when the stored number is
// lower, every Present entry with Playable=false is read again (a header read each, LocalTags.TryRead, per folder batch) and
// committed with what this build makes of it. One line says what came of it: `local.reread unplayable n=… nowPlayable=…`. A file
// that could not be reached (an offline drive or share) leaves the setting where it was, so the next start tries again; a cloud-only
// placeholder is never opened (UX S7) and is left to the scan that reads it once it is local. The same start walks every folder
// (not the journal catch-up), so a file type the older build did not list at all (`.aifc`) is found too.

namespace Wavee;

public static partial class LocalLibrary
{
    /// <summary>What this build's decoders play, as a number that only rises: 1 = PCM in WAV and AIFF.</summary>
    public const int DecoderSet = 1;

    /// <summary>After the index loads (its load thread): re-read the unplayable entries when the decoder set rose. True when it rose:
    /// the caller then walks every folder instead of the journal catch-up, because a rise can add file types (`.aifc`) that the
    /// older build never listed; the journal reports no change for those folders, while a walk fingerprints them with this build's
    /// types, so a folder holding one differs and its new files are read.</summary>
    static bool RereadIfDecodersRose()
    {
        int stored = Platform.Settings.Get(Platform.Keys.MusicDecoderSet);
        if (stored >= DecoderSet) return false;
        new Thread(() => Reread(stored)) { IsBackground = true, Name = "Wavee.LocalLibrary.Reread", Priority = ThreadPriority.BelowNormal }.Start();
        return true;
    }

    static void Reread(int from)
    {
        var batches = new Dictionary<(int Root, string Folder), LocalScanBatch>();
        int n = 0, nowPlayable = 0, away = 0, failed = 0;
        foreach (LocalEntry e in s_index.Snapshot())
        {
            if (e.Playable || e.State != LocalState.Present) continue;
            if (s_index.FullPathOf(e) is not { } path) continue;
            if (IsUnreachable(path) || !File.Exists(path)) { away++; continue; }
            n++;
            if (!LocalTags.TryRead(path, out Spotify.Audio.Format format, out LocalTagSet tags, out string why)) { failed++; continue; }
            bool playable = format != Spotify.Audio.Format.Unknown;
            if (!playable && why == e.Why) continue;   // nothing changed for this file
            if (playable) nowPlayable++;
            var key = (e.RootId, LocalIndex.FolderOf(e.RelativePath));
            if (!batches.TryGetValue(key, out LocalScanBatch? batch)) batches[key] = batch = new LocalScanBatch(key.RootId, key.Item2);
            batch.Upserts.Add(e with { Format = format, Tags = tags, Playable = playable, Why = playable ? "" : why });   // its matching name stays
        }
        foreach (LocalScanBatch batch in batches.Values) s_index.Apply(batch);
        Log.Info("local", "local.reread unplayable n=" + n + " nowPlayable=" + nowPlayable + " failed=" + failed + " away=" + away + " decoders=" + from + "->" + DecoderSet);
        if (batches.Count > 0) NoteProgress();   // the counts, Needs attention and the rows follow
        if (away == 0) Playback.ToUi(static () => Platform.Settings.Set(Platform.Keys.MusicDecoderSet, DecoderSet));
    }
}
