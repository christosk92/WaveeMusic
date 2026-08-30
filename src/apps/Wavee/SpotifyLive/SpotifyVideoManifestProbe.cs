using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using Wavee.Backend;
using Wavee.Backend.Metadata;

namespace Wavee.SpotifyLive;

// LIVE RAW video-manifest dump. It exists to settle ONE factual question with data instead of reasoning: does the v9
// manifest of a music video carry an AUDIO representation next to the video one? SpotifyVideoManifest DELIBERATELY drops
// audio-only profiles (its `video_codec` filter), so the parsed model cannot answer it — this probe prints the profiles
// array AS SERVED, plus a single VERDICT line, and writes the untouched body under %LOCALAPPDATA%\Wavee\diag so the
// evidence is inspectable afterwards.
//
// The chain is the app's own, not a re-implementation: SpotifyLiveSpclient.ConnectAsync (stored credential) →
// SpotifyVideoManifestResolver.ResolveManifestIdAsync (TrackV4 OriginalVideo[0].Gid, else the VIDEO_ASSOCIATIONS counterpart) →
// SpotifyVideoResolver.ResolveManifestJsonAsync (the one route + xpui Origin/Referer definition). Needs creds + network,
// so the USER runs it: `--spotify-video-manifest [spotify:track:...]`.
public static class SpotifyVideoManifestProbe
{
    /// <summary>Distinct exit code for "this track has no music video at all" — not a failure (1), just nothing to dump.</summary>
    public const int NoVideoExitCode = 3;

    public static async Task<int> RunAsync(string uri, WaveeLogger log, CancellationToken ct, string language = "en")
    {
        // Fail-soft by contract: a probe must report, never throw into Program.Main.
        try { return await ProbeAsync(uri, log, ct, language).ConfigureAwait(false); }
        catch (Exception ex)
        {
            log.Info("video-manifest probe failed: " + ex.GetType().Name + ": " + ex.Message);
            return 1;
        }
    }

    static async Task<int> ProbeAsync(string uri, WaveeLogger log, CancellationToken ct, string language)
    {
        var live = await SpotifyLiveSpclient.ConnectAsync(log, ct, language: language).ConfigureAwait(false);
        if (live is null) return 1;

        // The metadata chain wired exactly like SpotifyMetadataProbe (a one-shot InMemoryStore — the probe persists
        // nothing), plus the app's own spclient transport. The dealer socket is never Start()ed: LiveDealerTransport.Request
        // is plain spclient HTTP under the bearer + client-token middleware, which is all the manifest GET needs.
        var store = new InMemoryStore();
        var source = new ExtendedMetadataSource(live.Pipeline, () => live.BaseUrl, () => live.Session);
        using var transport = new LiveDealerTransport(Array.Empty<string>(), live.TokenProvider, live.Pipeline,
            () => live.BaseUrl, log, forceRefreshToken: live.ForceTokenProvider);
        var video = new SpotifyVideoManifestResolver(source, store, log);

        log.Info("Resolving the music-video manifest id for " + uri + " ...");
        var (manifestId, idSource) = await video.ResolveManifestIdAsync(uri, ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(manifestId))
        {
            log.Info("  NO VIDEO: " + uri + " has no TrackV4 OriginalVideo, and no VIDEO_ASSOCIATIONS counterpart that has one.");
            log.Info("  Pass a track that carries a music video: --spotify-video-manifest spotify:track:<id>");
            return NoVideoExitCode;
        }
        log.Info("  manifest id=" + manifestId + " (resolved via " + idSource + ")");

        string route = SpotifyVideoResolver.ManifestRoute(manifestId);
        var (status, json) = await SpotifyVideoResolver.ResolveManifestJsonAsync(transport, manifestId, ct).ConfigureAwait(false);
        log.Info("GET " + route + " -> status=" + status.ToString(CultureInfo.InvariantCulture)
                 + " bodyLen=" + json.Length.ToString(CultureInfo.InvariantCulture) + (status == 0 ? " (the request never completed)" : ""));
        if (json.Length == 0) { log.Info("  empty body - nothing to report."); return 1; }
        Dump(manifestId, json, log);
        return Report(json, log);
    }

    // Write the body EXACTLY as served (evidence, not a re-serialization) next to the app's other local diagnostics.
    static void Dump(string manifestId, string json, WaveeLogger log)
    {
        try
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Wavee", "diag");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "video-manifest-" + manifestId + ".json");
            File.WriteAllText(path, json);
            log.Info("  raw JSON (byte-for-byte as served) -> " + path);
        }
        catch (Exception ex) { log.Info("  raw JSON dump failed: " + ex.Message + " (the report below still stands)"); }
    }

    // Print the manifest in the order the audio question needs: profiles (an audio-only one is unmistakable) →
    // encryption_infos → the shared templates/addressing → durations → the VERDICT.
    static int Report(string json, WaveeLogger log)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (Exception ex) { log.Info("  manifest is not valid JSON: " + ex.Message + " (the dumped body is the evidence)"); return 1; }

        using (doc)
        {
            var root = doc.RootElement;
            var (content, templates) = SpotifyVideoManifest.ResolveHosts(root);   // the parser's own shape resolution

            var audioOnly = new List<Profile>();
            var muxed = new List<Profile>();
            int total = 0;

            log.Info("profiles:");
            if (!content.TryGetProperty("profiles", out var profiles) || profiles.ValueKind != JsonValueKind.Array)
            {
                log.Info("  <no profiles array>");
            }
            else
            {
                foreach (var p in profiles.EnumerateArray())
                {
                    string id = Str(p, "id") ?? Int(p, "id")?.ToString(CultureInfo.InvariantCulture) ?? "-";
                    string vcodec = Str(p, "video_codec") ?? "-";
                    string acodec = Str(p, "audio_codec") ?? "-";
                    int w = Int(p, "video_width") ?? Int(p, "width") ?? 0;
                    int h = Int(p, "video_height") ?? Int(p, "height") ?? 0;
                    string enc = EncryptionIndices(p);
                    log.Info("  [" + total.ToString(CultureInfo.InvariantCulture) + "] id=" + id
                             + " file_type=" + (Str(p, "file_type") ?? "-")
                             + " video_codec=" + vcodec + " audio_codec=" + acodec
                             + " " + w.ToString(CultureInfo.InvariantCulture) + "x" + h.ToString(CultureInfo.InvariantCulture)
                             + " " + Bitrate(p) + " enc=" + enc
                             + " key_id=" + (Str(p, "key_id") is { Length: > 0 } ? "<set>" : "-"));

                    var entry = new Profile(id, acodec, enc);
                    if (vcodec == "-" && acodec != "-") audioOnly.Add(entry);
                    else if (vcodec != "-" && acodec != "-") muxed.Add(entry);
                    total++;
                }
                if (total == 0) log.Info("  <empty profiles array>");
            }

            log.Info("encryption_infos:");
            var encHost = content.TryGetProperty("encryption_infos", out _) ? content : root;
            if (!encHost.TryGetProperty("encryption_infos", out var infos) || infos.ValueKind != JsonValueKind.Array)
            {
                log.Info("  <none>");
            }
            else
            {
                int i = 0;
                foreach (var ei in infos.EnumerateArray())
                {
                    log.Info("  [" + i.ToString(CultureInfo.InvariantCulture) + "] key_system=" + (Str(ei, "key_system") ?? "-")
                             + " license_server_endpoint=" + (Str(ei, "license_server_endpoint") ?? "-")
                             + " encryption_data=" + (Str(ei, "encryption_data") is { Length: > 0 } d
                                 ? d.Length.ToString(CultureInfo.InvariantCulture) + " base64 chars" : "-"));
                    i++;
                }
                if (i == 0) log.Info("  <empty encryption_infos array>");
            }

            // One template set serves EVERY profile — {{profile_id}} is the only per-representation substitution, which is
            // exactly what decides whether an audio profile is addressable without new plumbing.
            log.Info("templates + addressing (shared; a profile is addressed by substituting its own {{profile_id}}):");
            if (BaseUrls(templates, content, root) is { Count: > 0 } urls)
            {
                for (int u = 0; u < urls.Count; u++)
                    log.Info("  base_urls[" + u.ToString(CultureInfo.InvariantCulture) + "]=" + urls[u]);
            }
            else
            {
                log.Info("  base_urls=<none>");
            }
            log.Info("  initialization_template=" + (Str2(templates, content, "initialization_template") ?? "-"));
            log.Info("  segment_template=" + (Str2(templates, content, "segment_template") ?? "-"));
            log.Info("  segment_length=" + Num(Long2(content, templates, "segment_length")));
            log.Info("  duration=" + Num(Long2(content, root, "duration"))
                     + " start_time_millis=" + Num(Long2(content, root, "start_time_millis"))
                     + " end_time_millis=" + Num(Long2(content, root, "end_time_millis")));
            log.Info("  encoding_id=" + (Str2(content, root, "encoding_id") ?? "-")
                     + " media_id=" + (Str2(content, root, "media_id") ?? "-"));

            if (audioOnly.Count > 0)
                log.Info("VERDICT: audio profile(s) FOUND: ids=[" + Join(audioOnly, x => x.Id) + "] codecs=["
                         + Join(audioOnly, x => x.Codec) + "] encIdx=[" + Join(audioOnly, x => x.Enc) + "]");
            else if (muxed.Count > 0)
                log.Info("VERDICT: NO audio-only profile, but " + muxed.Count.ToString(CultureInfo.InvariantCulture)
                         + " profile(s) declare audio_codec ALONGSIDE video (muxed): ids=[" + Join(muxed, x => x.Id)
                         + "] codecs=[" + Join(muxed, x => x.Codec) + "] encIdx=[" + Join(muxed, x => x.Enc) + "]");
            else
                log.Info("VERDICT: NO audio profile in this manifest (profiles=" + total.ToString(CultureInfo.InvariantCulture)
                         + "; none declares an audio_codec)");
        }
        return 0;
    }

    readonly record struct Profile(string Id, string Codec, string Enc);

    static string Join(List<Profile> items, Func<Profile, string> pick) => string.Join(",", items.Select(pick));

    static readonly string[] BitrateNames = { "max_bitrate", "bandwidth_estimate", "video_bitrate", "audio_bitrate", "bitrate" };

    // Whichever bitrate-ish field this manifest actually uses, printed under ITS OWN name and raw text — nothing is
    // silently normalized, and a fractional value still shows up.
    static string Bitrate(JsonElement p)
    {
        foreach (var name in BitrateNames)
            if (p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number) return name + "=" + v.GetRawText();
        return "bitrate=-";
    }

    static string EncryptionIndices(JsonElement p)
    {
        if (p.TryGetProperty("encryption_indices", out var many) && many.ValueKind == JsonValueKind.Array)
        {
            var parts = new List<string>();
            foreach (var e in many.EnumerateArray())
                parts.Add(e.TryGetInt32(out var v) ? v.ToString(CultureInfo.InvariantCulture) : e.ToString());
            return "[" + string.Join(",", parts) + "]";
        }
        if (p.TryGetProperty("encryption_index", out var one) && one.TryGetInt32(out var ov))
            return ov.ToString(CultureInfo.InvariantCulture);
        return "-";
    }

    static List<string> BaseUrls(params JsonElement[] hosts)
    {
        var urls = new List<string>();
        foreach (var host in hosts)
        {
            if (!host.TryGetProperty("base_urls", out var bus) || bus.ValueKind != JsonValueKind.Array) continue;
            foreach (var u in bus.EnumerateArray())
                if (u.ValueKind == JsonValueKind.String && u.GetString() is { Length: > 0 } s) urls.Add(s);
            if (urls.Count > 0) return urls;
        }
        return urls;
    }

    static string Num(long? v) => v?.ToString(CultureInfo.InvariantCulture) ?? "-";
    static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    static int? Int(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.TryGetInt32(out var i) ? i : null;
    static long? Long(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.TryGetInt64(out var l) ? l : null;
    // The v9 shapes disagree on which element carries a field, so fall back instead of printing a misleading "-".
    static string? Str2(JsonElement a, JsonElement b, string name) => Str(a, name) ?? Str(b, name);
    static long? Long2(JsonElement a, JsonElement b, string name) => Long(a, name) ?? Long(b, name);
}
