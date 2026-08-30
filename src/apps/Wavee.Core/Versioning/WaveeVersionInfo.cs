using System.Collections.Generic;

namespace Wavee.Core;

/// <summary>Everything the app knows about its own build, parsed once from assembly metadata.
/// <para>Pure: no I/O, no reflection of its own (the caller hands in the already-read
/// <c>AssemblyMetadataAttribute</c> pairs), so it is unit-tested directly.</para></summary>
/// <param name="SemVer">"0.2.0" | "0.4.0-beta.2" | "0.2.0-dev" — the informational version with build metadata stripped.</param>
/// <param name="Core">"0.2.0" — SemVer without its pre-release suffix.</param>
/// <param name="Beta">2 for <c>-beta.2</c>, else null.</param>
/// <param name="Quad">"0.2.0.17" — the MSIX identity version; "" for an unstamped (dev) build.</param>
/// <param name="Codename">"Breaker" — the per-MINOR codename; "" if unstamped.</param>
/// <param name="Channel">"stable" | "beta" | "dev".</param>
/// <param name="Commit">"d4227b3" or "".</param>
/// <param name="BuildDate">ISO-8601 UTC or "".</param>
/// <param name="FeedRelease">The rolling GitHub release that carries the .appinstaller feed — build-time metadata
/// (stamped by <c>pack-wavee-msix.ps1 -FeedRelease</c>) so a test package can poll a scratch feed with no env var and
/// no runtime switch. Defaults to "wavee-stable".</param>
/// <param name="UpdateBaseUrl">Where the release assets that carry the feed live — build-time metadata
/// (<c>pack-wavee-msix.ps1 -UpdateBaseUrl</c>) so a local end-to-end test can pack a package that polls
/// <c>http://127.0.0.1:8099/</c> with no env var and no runtime branch. Always ends in "/".
/// Defaults to <see cref="DefaultUpdateBaseUrl"/> (the GitHub releases download root).</param>
public sealed record WaveeVersionInfo(
    string SemVer,
    string Core,
    int? Beta,
    string Quad,
    string Codename,
    string Channel,
    string Commit,
    string BuildDate,
    string FeedRelease = "wavee-stable",
    string UpdateBaseUrl = WaveeVersionInfo.DefaultUpdateBaseUrl)
{
    /// <summary>The shipping feed root: GitHub's release-asset download prefix for this repo. The
    /// <c>&lt;release&gt;/&lt;asset&gt;</c> tail is appended by whoever needs it (the updater's .appinstaller, the
    /// release-notes store's document + index).</summary>
    public const string DefaultUpdateBaseUrl = "https://github.com/christosk92/WaveeMusic/releases/download/";

    /// <summary>Trim; empty → <see cref="DefaultUpdateBaseUrl"/>; guarantee the trailing slash (every caller
    /// concatenates a relative tail onto it). Shared by <see cref="Parse"/> and <c>ReleaseNotesStore</c> so the
    /// normalization lives in exactly one place.</summary>
    public static string NormalizeUpdateBaseUrl(string? raw)
    {
        string s = raw?.Trim() ?? "";
        if (s.Length == 0) return DefaultUpdateBaseUrl;
        return s.EndsWith('/') ? s : s + "/";
    }

    /// <summary>A build that Windows never installed: no MSIX quad, or an explicitly "dev" channel. Such a build must
    /// never see an update prompt (there is nothing for the feed to be newer than).</summary>
    public bool IsDev => Channel == "dev" || Quad.Length == 0;

    /// <summary>The product name as the UI says it: <c>Wavee 0.2.0 “Breaker”</c> (a dev build shows its raw semver).
    /// <para>An UNSTAMPED codename drops the quotes entirely rather than printing <c>Wavee 0.3.0 “”</c> — a packaged
    /// build whose <c>Codename</c> metadata was never written is a real case (a scratch pack, a hand-built MSIX) and
    /// empty quotes read as a bug in the app rather than a gap in the build stamp. The app renders a LOCALIZED form of
    /// this same shape (<c>update.about.display*</c>); this stays the pure, culture-free fallback used by crash headers
    /// and copied diagnostics.</para></summary>
    public string Display
    {
        get
        {
            if (IsDev) return $"Wavee {SemVer}";
            string named = Codename is { Length: > 0 } name ? $"Wavee {Core} “{name}”" : $"Wavee {Core}";
            return Beta is int b ? $"{named} · Beta {b}" : named;
        }
    }

    /// <summary>RFC 9110 product token — ThirdParty/GitHub client only, never the Spotify-facing UA.</summary>
    public string UserAgent(string os, string arch) => $"Wavee/{Core} (build {Quad}; {Channel}; {os}; {arch})";

    /// <summary>The one-line build stamp for About / crash headers / copied diagnostics.</summary>
    public string OneLine(string os, string arch) => $"{Display} · build {Quad} · {Commit} · {BuildDate} · {arch}";

    /// <summary>The value written to <c>app.lastRunVersion</c> (the quad; a dev build writes its semver so it never "updates").</summary>
    public string LastRunKey => IsDev ? SemVer : Quad;

    /// <summary>Parses <c>AssemblyInformationalVersionAttribute</c> + the <c>AssemblyMetadata</c> pairs stamped by
    /// <c>Wavee.csproj</c>. Never throws: anything missing degrades to a dev build.</summary>
    public static WaveeVersionInfo Parse(string? informational, IReadOnlyDictionary<string, string> metadata)
    {
        string inf = string.IsNullOrWhiteSpace(informational) ? "dev" : informational.Trim();
        int plus = inf.IndexOf('+');
        string semver = plus > 0 ? inf[..plus] : inf;
        int dash = semver.IndexOf('-');
        string core = dash > 0 ? semver[..dash] : semver;
        int? beta = null;
        if (dash > 0 && semver.AsSpan(dash + 1).StartsWith("beta.") && int.TryParse(semver.AsSpan(dash + 6), out int b)) beta = b;

        string Get(string k) => metadata is not null && metadata.TryGetValue(k, out var v) && v is not null ? v : "";
        string channel = Get("Channel");
        if (channel.Length == 0) channel = "dev";
        string feed = Get("FeedRelease");
        if (feed.Length == 0) feed = "wavee-stable";
        string baseUrl = NormalizeUpdateBaseUrl(Get("UpdateBaseUrl"));
        return new(semver, core, beta, Get("PackageVersion"), Get("Codename"), channel, Get("Commit"), Get("BuildDate"),
                   feed, baseUrl);
    }
}
