using System;

namespace Wavee.Analyzers.Baselines;

/// <summary>Slash-normalised path helpers shared by the baseline matching and the layering rules. No file IO and no
/// <c>System.IO.Path</c>: analyzers must stay pure (RS1035) and the inputs are strings from trees and AdditionalFiles.</summary>
internal static class BaselinePaths
{
    /// <summary>Backslashes to forward slashes, trimmed.</summary>
    public static string Normalize(string path) => path.Replace('\\', '/').Trim();

    /// <summary>Normalises a baseline entry path: forward slashes, no leading <c>./</c> or <c>/</c>.</summary>
    public static string NormalizeEntry(string path)
    {
        string p = Normalize(path);
        while (p.StartsWith("./", StringComparison.Ordinal))
            p = p.Substring(2);
        return p.TrimStart('/');
    }

    /// <summary>True when <paramref name="path"/> equals <paramref name="suffix"/> or ends with it on a segment boundary
    /// (<c>Shell/F.cs</c> matches <c>C:/repo/Shell/F.cs</c>, <c>hell/F.cs</c> does not). Case-insensitive (Windows).</summary>
    public static bool EndsWithPath(string path, string suffix)
    {
        if (suffix.Length == 0 || path.Length < suffix.Length)
            return false;
        if (!path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            return false;
        return path.Length == suffix.Length || path[path.Length - suffix.Length - 1] == '/';
    }

    /// <summary>The last segment of a normalised path.</summary>
    public static string FileName(string normalizedPath)
    {
        int i = normalizedPath.LastIndexOf('/');
        return i < 0 ? normalizedPath : normalizedPath.Substring(i + 1);
    }

    /// <summary>The segment before the last one of a normalised path, or empty.</summary>
    public static string ParentSegment(string normalizedPath)
    {
        int last = normalizedPath.LastIndexOf('/');
        if (last <= 0)
            return "";
        int prev = normalizedPath.LastIndexOf('/', last - 1);
        return normalizedPath.Substring(prev + 1, last - prev - 1);
    }
}
