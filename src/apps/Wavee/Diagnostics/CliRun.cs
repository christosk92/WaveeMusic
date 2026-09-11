using System;

namespace Wavee;

// Headless-CLI probe detection. ONE source of truth for "is this launch a probe, not the GUI" — Program.cs uses it to
// decide whether to echo the log to the attached console (in Release builds a probe otherwise prints nothing; see
// WaveeLog.Instance.SetEcho in Program.cs). Ordinal-EQUALS only: never prefix-match (`--spotify-collection` must not
// swallow an unrelated `--spotify-collectionx`, and a probe flag must not accidentally match a substring of another arg).
public static class CliRun
{
    // Append-only: every headless probe arm in Program.cs, in the order they appear there.
    public static readonly string[] ProbeFlags =
    [
        "--backend-selftest",
        "--qr-dump",
        "--spotify-login",
        "--spotify-metadata",
        "--spotify-video-manifest",
        "--spotify-video-traits",
        "--spotify-playlist",
        "--spotify-rootlist",
        "--spotify-collection",
        "--spotify-sync",
        "--connect-live",
    ];

    public static bool IsHeadless(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
            for (int j = 0; j < ProbeFlags.Length; j++)
                if (string.Equals(args[i], ProbeFlags[j], StringComparison.Ordinal))
                    return true;
        return false;
    }

    public static string? FlagOf(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
            for (int j = 0; j < ProbeFlags.Length; j++)
                if (string.Equals(args[i], ProbeFlags[j], StringComparison.Ordinal))
                    return args[i];
        return null;
    }
}
