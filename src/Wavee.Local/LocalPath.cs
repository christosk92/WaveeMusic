namespace Wavee.Local;

internal static class LocalPath
{
    /// <summary>
    /// Canonical form for an absolute path: full-path resolved, drive letter
    /// lowercased on Windows, separators canonical. Case below the drive letter
    /// is preserved (NTFS is case-preserving; SMB shares may be case-sensitive).
    /// </summary>
    public static string Normalize(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var full = System.IO.Path.GetFullPath(path);
        if (OperatingSystem.IsWindows() && full.Length >= 2 && full[1] == ':')
        {
            full = char.ToLowerInvariant(full[0]) + full.Substring(1);
        }
        return full.Replace(System.IO.Path.AltDirectorySeparatorChar, System.IO.Path.DirectorySeparatorChar);
    }
}
