using System;
using System.IO;

namespace Wavee;

// Where the diagnostic probes (nav / resize / mem-soak / perf-bench) drop their CSV/JSON/PNG artifacts. Anchored to the
// WaveeLog file's directory (so artifacts sit beside the logs the same run produced) with a temp-dir fallback when no log
// file is configured. Created on demand; probes announce the resolved directory once via their logger.
internal static class ProbeArtifacts
{
    static string? _dir;

    public static string Dir
    {
        get
        {
            if (_dir is { } cached) return cached;
            string root;
            string? logDir = null;
            try { logDir = Path.GetDirectoryName(WaveeLog.Instance.FilePath ?? ""); } catch { }
            if (!string.IsNullOrEmpty(logDir))
                root = logDir!;
            else
                root = Path.Combine(Path.GetTempPath(), "wavee");
            string dir = Path.Combine(root, "artifacts");
            try { Directory.CreateDirectory(dir); } catch { }
            _dir = dir;
            return dir;
        }
    }

    public static string PathFor(string name) => Path.Combine(Dir, name);
}
