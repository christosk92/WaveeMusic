using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace Wavee.Core.Audio;

// PlayPlay is Spotify property. The user's local Spotify.dll is matched by
// SHA-256 against PlayPlayConstants. Returns null when no acceptable copy is
// found — fallback stays disabled.
public static class SpotifyDllLocator
{
    public static string CachePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Wavee", "PlayPlay", "Spotify.dll");

    public static string? Locate(IEnumerable<string>? explicitCandidates = null, ILogger? logger = null)
    {
        var candidates = new List<string> { CachePath };

        var spotifyAppData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Spotify", "Spotify.dll");
        candidates.Add(spotifyAppData);

        if (explicitCandidates is not null)
            candidates.AddRange(explicitCandidates);

        Span<byte> hash = stackalloc byte[32];
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            if (!File.Exists(candidate)) continue;

            try
            {
                var bytes = File.ReadAllBytes(candidate);
                SHA256.HashData(bytes, hash);

                if (hash.SequenceEqual(PlayPlayConstants.SpotifyClientSha256))
                {
                    logger?.LogInformation(
                        "Located matching Spotify.dll v{Version} at {Path}",
                        PlayPlayConstants.SpotifyClientVersion, candidate);
                    return candidate;
                }

                logger?.LogDebug(
                    "Skipping Spotify.dll candidate {Path}: SHA-256 {Sha} (expected v{Version})",
                    candidate, Convert.ToHexString(hash), PlayPlayConstants.SpotifyClientVersion);
            }
            catch (Exception ex)
            {
                logger?.LogDebug(ex, "Failed to inspect Spotify.dll candidate {Path}", candidate);
            }
        }

        return null;
    }

    public static void CopyToCache(string sourcePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);

        var bytes = File.ReadAllBytes(sourcePath);
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(bytes, hash);

        if (!hash.SequenceEqual(PlayPlayConstants.SpotifyClientSha256))
        {
            throw new SpotifyBinaryMismatchException(
                expectedSha256: Convert.ToHexString(PlayPlayConstants.SpotifyClientSha256),
                actualSha256: Convert.ToHexString(hash));
        }

        var dest = CachePath;
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);

        // Atomic install: write to a temp file in the same directory, then
        // move-with-overwrite. Mirrors NativeLibraryProvisioner's pattern.
        var tmp = dest + ".tmp";
        File.WriteAllBytes(tmp, bytes);
        File.Move(tmp, dest, overwrite: true);
    }
}
