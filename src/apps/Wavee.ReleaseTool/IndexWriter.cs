using System;
using System.IO;
using System.Text.Json;
using Wavee.Core.ReleaseNotes;

namespace Wavee.ReleaseTool;

/// <summary>
/// Writes <c>whatsnew-index.json</c> — this release prepended to the previous index (the copy the release script
/// downloaded from the rolling feed release), newest first, capped at
/// <see cref="ReleaseNotesValidation.MaxIndexEntries"/>.
/// </summary>
static class IndexWriter
{
    public static void Write(string outDir, string? previousIndexPath, ReleaseNotesDocument doc)
    {
        ReleaseNotesIndex? previous = null;
        if (previousIndexPath is { Length: > 0 } path && File.Exists(path))
        {
            try
            {
                previous = JsonSerializer.Deserialize(File.ReadAllBytes(path), ReleaseNotesJsonContext.Default.ReleaseNotesIndex);
            }
            catch (JsonException ex)
            {
                // NOT a warning. The index is the cumulative history of every release and the release script
                // uploads it with --clobber, so quietly "starting fresh" would replace the published history with
                // a one-entry file - and the old entries exist nowhere else. Fail and let the operator fix the
                // download; omitting --previous-index is the legitimate way to say "there is no history yet".
                throw new IOException("--previous-index is not valid JSON: " + path + " (" + ex.Message + "). "
                                      + "It is the cumulative release history and would be clobbered by a one-entry index.");
            }
            // A "releases": null on the wire needs no guard here: MergeIndex reads it as `previous?.Releases is { }`.
            if (previous is null) throw new IOException("--previous-index deserialized to nothing: " + path);
        }
        else if (previousIndexPath is { Length: > 0 } absent)
        {
            throw new IOException("--previous-index not found: " + absent
                                  + ". Omit the option to start a fresh index; do not pass a path that is not there.");
        }

        var merged = ReleaseNotesValidation.MergeIndex(previous, doc);
        Directory.CreateDirectory(outDir);
        File.WriteAllBytes(Path.Combine(outDir, "whatsnew-index.json"),
            JsonSerializer.SerializeToUtf8Bytes(merged, ReleaseNotesJsonContext.Default.ReleaseNotesIndex));
    }
}
