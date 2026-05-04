using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Wavee.Core.Video;

/// <summary>
/// Parsed representation of Spotify's v9 video manifest JSON. Builds an
/// in-memory DASH MPD that AdaptiveMediaSource can consume directly.
/// </summary>
public sealed class SpotifyVideoManifest
{
    public string EncodingId { get; init; } = "";
    public int SegmentLength { get; init; } = 4;
    public long DurationMs { get; init; }
    public string InitTemplate { get; init; } = "";
    public string SegmentTemplate { get; init; } = "";
    public string BaseUrl { get; init; } = "";
    public string? DefaultKeyId { get; init; }
    public string? DefaultPlayReadyKeyId { get; init; }
    public string? PlayReadyLicenseServerEndpoint { get; init; }
    public byte[]? PlayReadyPsshBytes { get; init; }
    public byte[]? PlayReadyProBytes { get; init; }
    public IReadOnlyList<VideoProfile> VideoProfiles { get; init; } = Array.Empty<VideoProfile>();
    public AudioProfile? AudioProfile { get; init; }

    public static SpotifyVideoManifest FromJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return FromJson(doc.RootElement);
    }

    public static SpotifyVideoManifest FromJson(JsonElement root)
    {
        // Spotify v9 manifests have two shapes in the wild:
        // - contents[0] carries profiles/encryption/encoding data while the
        //   root carries templates and base URLs.
        // - older source-shaped payloads put everything under sources[0].
        var content = root;
        var templateHost = root;
        if (root.TryGetProperty("contents", out var contents) && contents.ValueKind == JsonValueKind.Array && contents.GetArrayLength() > 0)
        {
            content = contents[0];
        }
        if (root.TryGetProperty("sources", out var sources) && sources.GetArrayLength() > 0)
        {
            content = sources[0];
            templateHost = content;
        }

        string encodingId = "";
        if (content.TryGetProperty("encoding_id", out var eid)) encodingId = eid.GetString() ?? "";
        if (string.IsNullOrEmpty(encodingId) && content.TryGetProperty("media_id", out var mid))
            encodingId = mid.GetString() ?? "";

        int segLen = content.TryGetProperty("segment_length", out var sl) ? sl.GetInt32() : 4;

        long durMs = 0;
        if (content.TryGetProperty("duration", out var dur)) durMs = dur.GetInt64();
        else
        {
            var startMs = GetInt64(content, "start_time_millis") ?? GetInt64(root, "start_time_millis") ?? 0;
            var endMs = GetInt64(content, "end_time_millis") ?? GetInt64(root, "end_time_millis") ?? 0;
            if (endMs > startMs) durMs = endMs - startMs;
        }

        string initTpl = templateHost.TryGetProperty("initialization_template", out var it)
            ? it.GetString() ?? "" : "";
        string segTpl = templateHost.TryGetProperty("segment_template", out var st)
            ? st.GetString() ?? "" : "";

        string baseUrl = "";
        if (templateHost.TryGetProperty("base_urls", out var bus) && bus.GetArrayLength() > 0)
            baseUrl = bus[0].GetString() ?? "";

        byte[]? psshBytes = null;
        byte[]? proBytes = null;
        string? licenseServerEndpoint = null;
        int? playReadyEncryptionIndex = null;
        var encryptionHost = content.TryGetProperty("encryption_infos", out _) ? content : root;
        if (encryptionHost.TryGetProperty("encryption_infos", out var encInfos))
        {
            var index = 0;
            foreach (var ei in encInfos.EnumerateArray())
            {
                if (ei.TryGetProperty("key_system", out var ks)
                    && ks.GetString() == "playready"
                    && ei.TryGetProperty("encryption_data", out var ed))
                {
                    var b64 = ed.GetString();
                    if (!string.IsNullOrEmpty(b64))
                    {
                        psshBytes = Convert.FromBase64String(b64);
                        proBytes = ExtractProFromPssh(psshBytes);
                    }
                    playReadyEncryptionIndex = index;
                    if (ei.TryGetProperty("license_server_endpoint", out var lse))
                        licenseServerEndpoint = lse.GetString();
                    break;
                }
                index++;
            }
        }

        var videoProfiles = new List<VideoProfile>();
        AudioProfile? audioProfile = null;
        string? defaultKeyId = null;
        string? defaultPlayReadyKeyId = null;

        if (content.TryGetProperty("profiles", out var profiles))
        {
            foreach (var p in profiles.EnumerateArray())
            {
                int id = p.TryGetProperty("id", out var pid) ? pid.GetInt32() : 0;
                string fileType = p.TryGetProperty("file_type", out var ft) ? ft.GetString() ?? "" : "";
                if (fileType != "mp4") continue;
                if (!ProfileMatchesEncryptionIndex(p, playReadyEncryptionIndex)) continue;

                if (defaultKeyId is null
                    && p.TryGetProperty("key_id", out var keyIdProperty))
                {
                    var keyId = keyIdProperty.GetString();
                    defaultKeyId = FormatCencKeyId(keyId);
                    defaultPlayReadyKeyId = FormatPlayReadyKeyId(keyId);
                }

                if (p.TryGetProperty("video_codec", out var vc))
                {
                    int w = GetInt32(p, "video_width") ?? GetInt32(p, "width") ?? 0;
                    int h = GetInt32(p, "video_height") ?? GetInt32(p, "height") ?? 0;
                    int bw = GetInt32(p, "max_bitrate") ?? GetInt32(p, "bandwidth_estimate") ?? GetInt32(p, "video_bitrate") ?? 0;
                    videoProfiles.Add(new VideoProfile(id, vc.GetString() ?? "", w, h, bw));
                }
                else if (p.TryGetProperty("audio_codec", out var ac))
                {
                    int bw = GetInt32(p, "audio_bitrate") ?? GetInt32(p, "max_bitrate") ?? GetInt32(p, "bandwidth_estimate") ?? 160_000;
                    audioProfile = new AudioProfile(id, ac.GetString() ?? "", bw);
                }
            }
        }

        return new SpotifyVideoManifest
        {
            EncodingId = encodingId,
            SegmentLength = segLen,
            DurationMs = durMs,
            InitTemplate = initTpl,
            SegmentTemplate = segTpl,
            BaseUrl = baseUrl,
            DefaultKeyId = defaultKeyId,
            DefaultPlayReadyKeyId = defaultPlayReadyKeyId,
            PlayReadyLicenseServerEndpoint = licenseServerEndpoint,
            PlayReadyPsshBytes = psshBytes,
            PlayReadyProBytes = proBytes,
            VideoProfiles = videoProfiles,
            AudioProfile = audioProfile,
        };
    }

    public Uri BuildInitializationUri(int profileId)
        => new(BuildInitializationUrl(profileId), UriKind.Absolute);

    private string BuildInitializationUrl(int profileId)
        => BaseUrl
           + InitTemplate
               .Replace("{{profile_id}}", profileId.ToString(CultureInfo.InvariantCulture))
               .Replace("{{file_type}}", "mp4");

    private string BuildInitializationTemplateUrl()
        => BaseUrl
           + InitTemplate
               .Replace("{{profile_id}}", "$RepresentationID$")
               .Replace("{{file_type}}", "mp4");

    private string BuildMediaTemplateUrl()
        => BaseUrl
           + SegmentTemplate
               .Replace("{{profile_id}}", "$RepresentationID$")
               .Replace("{{segment_timestamp}}", "$Time$")
               .Replace("{{file_type}}", "mp4");

    private static string? FormatCencKeyId(string? base64KeyId)
    {
        if (string.IsNullOrWhiteSpace(base64KeyId)) return null;
        try
        {
            var bytes = Convert.FromBase64String(base64KeyId);
            if (bytes.Length != 16) return null;
            return string.Create(36, bytes, static (span, kid) =>
            {
                const string hex = "0123456789abcdef";
                int output = 0;
                for (int i = 0; i < kid.Length; i++)
                {
                    if (i is 4 or 6 or 8 or 10)
                        span[output++] = '-';
                    span[output++] = hex[kid[i] >> 4];
                    span[output++] = hex[kid[i] & 0x0F];
                }
            });
        }
        catch
        {
            return null;
        }
    }

    private static string? FormatPlayReadyKeyId(string? base64KeyId)
    {
        if (string.IsNullOrWhiteSpace(base64KeyId)) return null;
        try
        {
            var cencKid = Convert.FromBase64String(base64KeyId);
            if (cencKid.Length != 16) return null;

            var playReadyKid = new byte[16];
            playReadyKid[0] = cencKid[3];
            playReadyKid[1] = cencKid[2];
            playReadyKid[2] = cencKid[1];
            playReadyKid[3] = cencKid[0];
            playReadyKid[4] = cencKid[5];
            playReadyKid[5] = cencKid[4];
            playReadyKid[6] = cencKid[7];
            playReadyKid[7] = cencKid[6];
            cencKid.AsSpan(8, 8).CopyTo(playReadyKid.AsSpan(8));
            return Convert.ToBase64String(playReadyKid);
        }
        catch
        {
            return null;
        }
    }

    private static int? GetInt32(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var property) && property.TryGetInt32(out var value)
            ? value
            : null;

    private static long? GetInt64(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var property) && property.TryGetInt64(out var value)
            ? value
            : null;

    private static bool ProfileMatchesEncryptionIndex(JsonElement profile, int? encryptionIndex)
    {
        if (encryptionIndex is null) return true;
        if (!profile.TryGetProperty("encryption_indices", out var indices)
            || indices.ValueKind != JsonValueKind.Array)
        {
            return !profile.TryGetProperty("encryption_index", out var index)
                   || !index.TryGetInt32(out var value)
                   || value == encryptionIndex.Value;
        }

        foreach (var index in indices.EnumerateArray())
        {
            if (index.TryGetInt32(out var value) && value == encryptionIndex.Value)
                return true;
        }

        return false;
    }

    private static byte[]? ExtractProFromPssh(byte[] pssh)
    {
        // PSSH v0: size(4) + "pssh"(4) + version/flags(4) + systemId(16) + dataLen(4) + data(PRO)
        // PSSH v1 inserts KID_count + KIDs before dataLen.
        if (pssh.Length < 32) return null;
        try
        {
            if (pssh[4] != (byte)'p' || pssh[5] != (byte)'s' || pssh[6] != (byte)'s' || pssh[7] != (byte)'h')
                return null;

            var version = pssh[8];
            var offset = 28;
            if (version > 0)
            {
                if (pssh.Length < offset + 4) return null;
                var kidCount = BinaryPrimitives.ReadInt32BigEndian(pssh.AsSpan(offset, 4));
                offset += 4;
                if (kidCount < 0 || pssh.Length < offset + kidCount * 16 + 4) return null;
                offset += kidCount * 16;
            }

            int proLen = BinaryPrimitives.ReadInt32BigEndian(pssh.AsSpan(offset, 4));
            offset += 4;
            if (proLen <= 0 || offset + proLen > pssh.Length) return null;
            var pro = new byte[proLen];
            pssh.AsSpan(offset, proLen).CopyTo(pro);
            return LooksLikePlayReadyObject(pro) ? pro : null;
        }
        catch { return null; }
    }

    private static bool LooksLikePlayReadyObject(byte[] pro)
    {
        if (pro.Length < 6) return false;
        var objectLength = BinaryPrimitives.ReadInt32LittleEndian(pro.AsSpan(0, 4));
        return objectLength == pro.Length;
    }

    /// <summary>
    /// Synthesises a DASH MPD from the manifest data. The MPD is suitable for
    /// passing to AdaptiveMediaSource.CreateFromStreamAsync with PlayReady DRM.
    /// </summary>
    public string BuildDashMpd(
        IReadOnlyDictionary<int, Mp4InitSegmentProtectionData>? initProtectionByProfileId = null)
    {
        if (DurationMs <= 0)
            throw new InvalidOperationException("DurationMs must be > 0 before building DASH MPD.");

        double durationS = DurationMs / 1000.0;
        string durationText = durationS.ToString("0.###", CultureInfo.InvariantCulture);
        int totalSegments = (int)Math.Ceiling(durationS / SegmentLength);

        // Substitute Spotify template variables with DASH template variables.
        // {{profile_id}} → $RepresentationID$, {{segment_timestamp}} → $Time$,
        // {{file_type}} → "mp4" (we only include MP4 profiles).
        string initUrl = BuildInitializationTemplateUrl();
        string mediaUrl = BuildMediaTemplateUrl();

        string proBase64 = PlayReadyProBytes != null
            ? Convert.ToBase64String(PlayReadyProBytes) : "";
        string psshBase64 = PlayReadyPsshBytes != null
            ? Convert.ToBase64String(PlayReadyPsshBytes) : "";

        var sb = new StringBuilder(4096);
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
        sb.Append("<MPD xmlns=\"urn:mpeg:dash:schema:mpd:2011\"");
        sb.Append(" xmlns:mspr=\"urn:microsoft:playready\"");
        sb.Append(" xmlns:cenc=\"urn:mpeg:cenc:2013\"");
        sb.Append(" profiles=\"urn:mpeg:dash:profile:isoff-live:2011\"");
        sb.Append(" type=\"static\"");
        sb.Append($" mediaPresentationDuration=\"PT{durationText}S\"");
        sb.AppendLine(" minBufferTime=\"PT4S\">");
        sb.AppendLine($"  <Period duration=\"PT{durationText}S\">");

        var dashVideoProfiles = SelectDashVideoProfiles(VideoProfiles);
        if (dashVideoProfiles.Count == 0)
            throw new InvalidOperationException("No MP4 video profile is available for DASH playback.");

        AppendAdaptationSet(sb, id: 1, contentType: "video", mimeType: "video/mp4",
            initUrl, mediaUrl, SegmentLength, totalSegments, DefaultKeyId, DefaultPlayReadyKeyId, psshBase64, proBase64,
            GetProtectionData(initProtectionByProfileId, dashVideoProfiles[0].Id),
            representations: BuildVideoRepresentations(dashVideoProfiles));

        if (AudioProfile != null)
        {
            AppendAdaptationSet(sb, id: 2, contentType: "audio", mimeType: "audio/mp4",
                initUrl, mediaUrl, SegmentLength, totalSegments, DefaultKeyId, DefaultPlayReadyKeyId, psshBase64, proBase64,
                GetProtectionData(initProtectionByProfileId, AudioProfile.Id),
                representations: BuildAudioRepresentation(AudioProfile));
        }

        sb.AppendLine("  </Period>");
        sb.AppendLine("</MPD>");
        return sb.ToString();
    }

    public IReadOnlyList<VideoProfile> GetDashVideoProfilesForDiagnostics()
        => SelectDashVideoProfiles(VideoProfiles);

    private static Mp4InitSegmentProtectionData? GetProtectionData(
        IReadOnlyDictionary<int, Mp4InitSegmentProtectionData>? initProtectionByProfileId,
        int profileId)
        => initProtectionByProfileId is not null
           && initProtectionByProfileId.TryGetValue(profileId, out var protectionData)
           && protectionData.HasAnySignal
            ? protectionData
            : null;

    private static string BuildVideoRepresentations(IReadOnlyList<VideoProfile> profiles)
    {
        var sb = new StringBuilder();
        foreach (var vp in profiles)
        {
            sb.AppendLine(
                $"      <Representation id=\"{vp.Id}\" codecs=\"{vp.VideoCodec}\"" +
                $" width=\"{vp.Width}\" height=\"{vp.Height}\" bandwidth=\"{vp.Bandwidth}\"/>");
        }
        return sb.ToString();
    }

    private static IReadOnlyList<VideoProfile> SelectDashVideoProfiles(IReadOnlyList<VideoProfile> profiles)
    {
        if (profiles.Count <= 1) return profiles;

        // Spotify's web player selects one MSE profile at a time. Windows
        // AdaptiveMediaSource is stricter about adaptive sets that mix AVC
        // levels/resolutions, so start with one conservative representation.
        VideoProfile? selected = null;
        foreach (var profile in profiles)
        {
            if (profile.Width <= 0 || profile.Height <= 0) continue;
            if (profile.Height > 480) continue;
            if (selected is null || profile.Height > selected.Height || (profile.Height == selected.Height && profile.Bandwidth > selected.Bandwidth))
                selected = profile;
        }

        if (selected is null)
        {
            foreach (var profile in profiles)
            {
                if (selected is null || profile.Bandwidth < selected.Bandwidth)
                    selected = profile;
            }
        }

        return selected is null ? Array.Empty<VideoProfile>() : new[] { selected };
    }

    private static string BuildAudioRepresentation(AudioProfile ap)
        => $"      <Representation id=\"{ap.Id}\" codecs=\"{ap.AudioCodec}\" bandwidth=\"{ap.Bandwidth}\"/>\n";

    private static void AppendAdaptationSet(
        StringBuilder sb, int id, string contentType, string mimeType,
        string initUrl, string mediaUrl,
        int segmentLength, int totalSegments, string? defaultKeyId, string? defaultPlayReadyKeyId, string psshBase64, string proBase64,
        Mp4InitSegmentProtectionData? initProtection,
        string representations)
    {
        defaultKeyId = initProtection?.DefaultKeyId ?? defaultKeyId;
        defaultPlayReadyKeyId = initProtection?.DefaultPlayReadyKeyId ?? defaultPlayReadyKeyId;
        if (initProtection?.PlayReadyPsshBytes is { Length: > 0 } initPssh)
            psshBase64 = Convert.ToBase64String(initPssh);
        if (initProtection?.PlayReadyProBytes is { Length: > 0 } initPro)
            proBase64 = Convert.ToBase64String(initPro);
        var ivSize = initProtection?.DefaultPerSampleIvSize ?? 8;

        sb.AppendLine($"    <AdaptationSet id=\"{id}\" contentType=\"{contentType}\"" +
                      $" mimeType=\"{mimeType}\" segmentAlignment=\"true\" startWithSAP=\"1\">");

        if (!string.IsNullOrEmpty(defaultKeyId))
        {
            sb.AppendLine("      <ContentProtection schemeIdUri=\"urn:mpeg:dash:mp4protection:2011\"" +
                          $" value=\"cenc\" cenc:default_KID=\"{defaultKeyId}\"/>");
        }

        if (!string.IsNullOrEmpty(proBase64) || !string.IsNullOrEmpty(psshBase64))
        {
            sb.AppendLine("      <ContentProtection schemeIdUri=\"urn:uuid:9a04f079-9840-4286-ab92-e65be0885f95\" value=\"MSPR 2.0\">");
            if (!string.IsNullOrEmpty(defaultPlayReadyKeyId))
            {
                sb.AppendLine("        <mspr:IsEncrypted>1</mspr:IsEncrypted>");
                sb.AppendLine($"        <mspr:IV_size>{ivSize}</mspr:IV_size>");
                sb.AppendLine($"        <mspr:kid>{defaultPlayReadyKeyId}</mspr:kid>");
            }
            if (!string.IsNullOrEmpty(psshBase64))
                sb.AppendLine($"        <cenc:pssh>{psshBase64}</cenc:pssh>");
            if (!string.IsNullOrEmpty(proBase64))
                sb.AppendLine($"        <mspr:pro>{proBase64}</mspr:pro>");
            sb.AppendLine("      </ContentProtection>");
        }

        if (contentType == "audio")
        {
            sb.AppendLine("      <AudioChannelConfiguration schemeIdUri=\"urn:mpeg:dash:23003:3:audio_channel_configuration:2011\" value=\"2\"/>");
        }

        string safeInitUrl = EscapeXmlAttribute(initUrl);
        string safeMediaUrl = EscapeXmlAttribute(mediaUrl);

        sb.AppendLine($"      <SegmentTemplate timescale=\"1\"");
        sb.AppendLine($"        initialization=\"{safeInitUrl}\"");
        sb.AppendLine($"        media=\"{safeMediaUrl}\">");
        sb.AppendLine($"        <SegmentTimeline>");
        sb.AppendLine($"          <S t=\"0\" d=\"{segmentLength}\" r=\"{totalSegments - 1}\"/>");
        sb.AppendLine($"        </SegmentTimeline>");
        sb.AppendLine("      </SegmentTemplate>");
        sb.Append(representations);
        sb.AppendLine("    </AdaptationSet>");
    }

    private static string EscapeXmlAttribute(string value)
        => value
            .Replace("&", "&amp;")
            .Replace("\"", "&quot;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;");
}

public sealed record VideoProfile(int Id, string VideoCodec, int Width, int Height, int Bandwidth);
public sealed record AudioProfile(int Id, string AudioCodec, int Bandwidth);
