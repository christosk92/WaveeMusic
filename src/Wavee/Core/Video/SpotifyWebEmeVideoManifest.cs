using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wavee.Core.Video;

/// <summary>
/// Parsed representation of Spotify's WebM/Widevine video manifest shape used
/// by the WebView2 EME player.
/// </summary>
public sealed record SpotifyWebEmeVideoManifest(
    int VideoProfileId,
    int AudioProfileId,
    long DurationMs,
    int SegmentLength,
    string? LicenseServerEndpoint,
    IReadOnlyList<int> SegmentTimes,
    SpotifyWebEmeTrackConfig Video,
    SpotifyWebEmeTrackConfig Audio,
    [property: JsonIgnore] IReadOnlyList<SpotifyWebEmeTrackConfig> VideoTracks)
{
    public static SpotifyWebEmeVideoManifest FromJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var content = root;
        var templateHost = root;

        if (root.TryGetProperty("contents", out var contents)
            && contents.ValueKind == JsonValueKind.Array
            && contents.GetArrayLength() > 0)
        {
            content = contents[0];
        }

        if (root.TryGetProperty("sources", out var sources)
            && sources.ValueKind == JsonValueKind.Array
            && sources.GetArrayLength() > 0)
        {
            content = sources[0];
            templateHost = content;
        }

        var segmentLength = GetInt32(content, "segment_length") ?? 4;
        if (segmentLength <= 0)
            segmentLength = 4;
        var durationMs = GetDurationMs(root, content);
        if (durationMs <= 0)
            throw new InvalidOperationException("Spotify video manifest did not include a valid duration.");

        var initTemplate = GetString(templateHost, "initialization_template")
            ?? throw new InvalidOperationException("Spotify video manifest did not include initialization_template.");
        var segmentTemplate = GetString(templateHost, "segment_template")
            ?? throw new InvalidOperationException("Spotify video manifest did not include segment_template.");
        var baseUrl = "";
        if (templateHost.TryGetProperty("base_urls", out var baseUrls)
            && baseUrls.ValueKind == JsonValueKind.Array
            && baseUrls.GetArrayLength() > 0)
        {
            baseUrl = baseUrls[0].GetString() ?? "";
        }

        int? widevineEncryptionIndex = null;
        string? licenseEndpoint = null;
        var encryptionHost = content.TryGetProperty("encryption_infos", out _) ? content : root;
        if (encryptionHost.TryGetProperty("encryption_infos", out var encryptionInfos)
            && encryptionInfos.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var encryptionInfo in encryptionInfos.EnumerateArray())
            {
                if (string.Equals(GetString(encryptionInfo, "key_system"), "widevine", StringComparison.OrdinalIgnoreCase))
                {
                    widevineEncryptionIndex = index;
                    licenseEndpoint = GetString(encryptionInfo, "license_server_endpoint");
                    break;
                }

                index++;
            }
        }

        var videoCandidates = new List<WebProfile>();
        WebProfile? selectedAudio = null;
        if (content.TryGetProperty("profiles", out var profiles)
            && profiles.ValueKind == JsonValueKind.Array)
        {
            foreach (var profile in profiles.EnumerateArray())
            {
                if (!string.Equals(GetString(profile, "file_type"), "webm", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!ProfileMatchesEncryptionIndex(profile, widevineEncryptionIndex))
                    continue;

                var id = GetInt32(profile, "id") ?? 0;
                if (id <= 0)
                    continue;

                var videoCodec = GetString(profile, "video_codec");
                if (!string.IsNullOrWhiteSpace(videoCodec))
                {
                    var width = GetInt32(profile, "video_width") ?? GetInt32(profile, "width") ?? 0;
                    var height = GetInt32(profile, "video_height") ?? GetInt32(profile, "height") ?? 0;
                    var bandwidth = GetInt32(profile, "max_bitrate") ?? GetInt32(profile, "bandwidth_estimate") ?? 0;
                    var candidate = new WebProfile(id, videoCodec, width, height, bandwidth);
                    videoCandidates.Add(candidate);

                    continue;
                }

                var audioCodec = GetString(profile, "audio_codec");
                if (!string.IsNullOrWhiteSpace(audioCodec))
                {
                    var bandwidth = GetInt32(profile, "max_bitrate") ?? GetInt32(profile, "bandwidth_estimate") ?? 0;
                    var candidate = new WebProfile(id, audioCodec, 0, 0, bandwidth);
                    if (selectedAudio is null || candidate.Bandwidth > selectedAudio.Bandwidth)
                        selectedAudio = candidate;
                }
            }
        }

        if (videoCandidates.Count == 0)
            throw new InvalidOperationException("Spotify video manifest did not include a Widevine WebM video profile.");
        if (selectedAudio is null)
            throw new InvalidOperationException("Spotify video manifest did not include a Widevine WebM audio profile.");

        videoCandidates.Sort((left, right) =>
        {
            var height = right.Height.CompareTo(left.Height);
            if (height != 0) return height;
            var bandwidth = right.Bandwidth.CompareTo(left.Bandwidth);
            if (bandwidth != 0) return bandwidth;
            return left.Id.CompareTo(right.Id);
        });

        var selectedVideo = videoCandidates[0];

        var totalSegments = (int)Math.Ceiling(durationMs / 1000d / segmentLength);
        var segmentTimes = new List<int>(totalSegments);
        for (var i = 0; i < totalSegments; i++)
            segmentTimes.Add(i * segmentLength);

        var video = BuildTrack(baseUrl, initTemplate, segmentTemplate, selectedVideo, segmentTimes, isVideo: true);
        var videoTracks = new List<SpotifyWebEmeTrackConfig>(videoCandidates.Count);
        foreach (var candidate in videoCandidates)
            videoTracks.Add(BuildTrack(baseUrl, initTemplate, segmentTemplate, candidate, segmentTimes, isVideo: true));

        var audio = BuildTrack(baseUrl, initTemplate, segmentTemplate, selectedAudio, segmentTimes, isVideo: false);
        return new SpotifyWebEmeVideoManifest(
            selectedVideo.Id,
            selectedAudio.Id,
            durationMs,
            segmentLength,
            licenseEndpoint,
            segmentTimes,
            video,
            audio,
            videoTracks);
    }

    public static string DescribeManifestForLog(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var content = root;

            if (root.TryGetProperty("contents", out var contents)
                && contents.ValueKind == JsonValueKind.Array
                && contents.GetArrayLength() > 0)
            {
                content = contents[0];
            }

            if (root.TryGetProperty("sources", out var sources)
                && sources.ValueKind == JsonValueKind.Array
                && sources.GetArrayLength() > 0)
            {
                content = sources[0];
            }

            var sb = new StringBuilder();
            sb.Append("durationMs=").Append(GetDurationMs(root, content));
            sb.Append("; segmentLength=").Append(GetInt32(content, "segment_length") ?? 0);
            sb.Append("; encryption=[");
            AppendEncryptionInfo(sb, content, root);
            sb.Append("]; profiles=[");
            AppendProfileInfo(sb, content);
            sb.Append(']');
            return sb.ToString();
        }
        catch (Exception ex)
        {
            return $"manifest diagnostics unavailable: {ex.Message}; bytes={json.Length}";
        }
    }

    private static SpotifyWebEmeTrackConfig BuildTrack(
        string baseUrl,
        string initTemplate,
        string segmentTemplate,
        WebProfile profile,
        IReadOnlyList<int> segmentTimes,
        bool isVideo)
    {
        var initUrl = baseUrl + initTemplate
            .Replace("{{profile_id}}", profile.Id.ToString(CultureInfo.InvariantCulture))
            .Replace("{{file_type}}", "webm");

        var segmentUrls = new List<string>(segmentTimes.Count);
        foreach (var time in segmentTimes)
        {
            segmentUrls.Add(baseUrl + segmentTemplate
                .Replace("{{profile_id}}", profile.Id.ToString(CultureInfo.InvariantCulture))
                .Replace("{{segment_timestamp}}", time.ToString(CultureInfo.InvariantCulture))
                .Replace("{{file_type}}", "webm"));
        }

        var contentType = isVideo
            ? $"video/webm; codecs=\"{NormalizeWebCodec(profile.Codec)}\""
            : $"audio/webm; codecs=\"{NormalizeWebCodec(profile.Codec)}\"";

        return new SpotifyWebEmeTrackConfig(
            profile.Id,
            profile.Codec,
            profile.Width,
            profile.Height,
            profile.Bandwidth,
            BuildTrackLabel(profile, isVideo),
            contentType,
            initUrl,
            segmentUrls);
    }

    private static string BuildTrackLabel(WebProfile profile, bool isVideo)
    {
        if (isVideo && profile.Height > 0)
        {
            return profile.Bandwidth > 0
                ? $"{profile.Height}p - {FormatBitrate(profile.Bandwidth)}"
                : $"{profile.Height}p";
        }

        if (profile.Bandwidth > 0)
            return $"{FormatBitrate(profile.Bandwidth)} audio";

        return profile.Codec;
    }

    private static string FormatBitrate(int bitsPerSecond)
    {
        if (bitsPerSecond >= 1_000_000)
            return $"{(bitsPerSecond / 1_000_000d).ToString("0.#", CultureInfo.InvariantCulture)} Mbps";

        if (bitsPerSecond >= 1000)
            return $"{(bitsPerSecond / 1000d).ToString("0", CultureInfo.InvariantCulture)} kbps";

        return $"{bitsPerSecond.ToString(CultureInfo.InvariantCulture)} bps";
    }

    private static string NormalizeWebCodec(string codec)
        => codec.Equals("vp9", StringComparison.OrdinalIgnoreCase) ? "vp9" : codec;

    private static long GetDurationMs(JsonElement root, JsonElement content)
    {
        if (content.TryGetProperty("duration", out var duration) && duration.TryGetInt64(out var durationMs))
            return durationMs;

        var startMs = GetInt64(content, "start_time_millis") ?? GetInt64(root, "start_time_millis") ?? 0;
        var endMs = GetInt64(content, "end_time_millis") ?? GetInt64(root, "end_time_millis") ?? 0;
        return endMs > startMs ? endMs - startMs : 0;
    }

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

    private static string? GetString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var property) ? property.GetString() : null;

    private static int? GetInt32(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var property) && property.TryGetInt32(out var value)
            ? value
            : null;

    private static long? GetInt64(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var property) && property.TryGetInt64(out var value)
            ? value
            : null;

    private static void AppendEncryptionInfo(StringBuilder sb, JsonElement content, JsonElement root)
    {
        var encryptionHost = content.TryGetProperty("encryption_infos", out _) ? content : root;
        if (!encryptionHost.TryGetProperty("encryption_infos", out var encryptionInfos)
            || encryptionInfos.ValueKind != JsonValueKind.Array)
        {
            sb.Append("<none>");
            return;
        }

        var index = 0;
        var wrote = false;
        foreach (var encryptionInfo in encryptionInfos.EnumerateArray())
        {
            if (wrote) sb.Append(", ");
            wrote = true;
            sb.Append(index)
              .Append(':')
              .Append(GetString(encryptionInfo, "key_system") ?? "<unknown>")
              .Append(":license=")
              .Append(string.IsNullOrWhiteSpace(GetString(encryptionInfo, "license_server_endpoint")) ? "<none>" : "<set>");
            index++;
        }

        if (!wrote) sb.Append("<empty>");
    }

    private static void AppendProfileInfo(StringBuilder sb, JsonElement content)
    {
        if (!content.TryGetProperty("profiles", out var profiles)
            || profiles.ValueKind != JsonValueKind.Array)
        {
            sb.Append("<none>");
            return;
        }

        var wrote = false;
        foreach (var profile in profiles.EnumerateArray())
        {
            if (wrote) sb.Append(", ");
            wrote = true;
            sb.Append(GetInt32(profile, "id") ?? 0)
              .Append(':')
              .Append(GetString(profile, "file_type") ?? "<type>")
              .Append(':')
              .Append(GetString(profile, "video_codec") ?? GetString(profile, "audio_codec") ?? "<codec>")
              .Append(':')
              .Append(GetInt32(profile, "video_width") ?? GetInt32(profile, "width") ?? 0)
              .Append('x')
              .Append(GetInt32(profile, "video_height") ?? GetInt32(profile, "height") ?? 0)
              .Append(":enc=");

            if (profile.TryGetProperty("encryption_index", out var encryptionIndex)
                && encryptionIndex.TryGetInt32(out var index))
            {
                sb.Append(index);
            }
            else if (profile.TryGetProperty("encryption_indices", out var encryptionIndices)
                     && encryptionIndices.ValueKind == JsonValueKind.Array)
            {
                sb.Append('[');
                var wroteIndex = false;
                foreach (var item in encryptionIndices.EnumerateArray())
                {
                    if (!item.TryGetInt32(out var value)) continue;
                    if (wroteIndex) sb.Append(',');
                    wroteIndex = true;
                    sb.Append(value);
                }
                sb.Append(']');
            }
            else
            {
                sb.Append("<none>");
            }
        }

        if (!wrote) sb.Append("<empty>");
    }

    private sealed record WebProfile(int Id, string Codec, int Width, int Height, int Bandwidth);
}

public sealed record SpotifyWebEmeTrackConfig(
    int ProfileId,
    string Codec,
    int Width,
    int Height,
    int Bandwidth,
    string Label,
    string ContentType,
    string InitUrl,
    IReadOnlyList<string> SegmentUrls);
