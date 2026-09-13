using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Wavee.Backend;
using Wavee.Backend.Audio;
using Wavee.Backend.Spotify;
using Af = Wavee.Protocol.Audiofiles;
using M = Wavee.Protocol.Metadata;
using S = Wavee.Protocol.Storage;
using Xm = Wavee.Protocol.ExtendedMetadata;

namespace Wavee.SpotifyLive.Audio;

/// <summary>
/// Opt-in diagnostic probe for alternate Spotify playback routes. It never changes selected playback format; it only logs
/// what the account/catalog exposes: MP3/AAC/FLAC/Ogg candidates, whether their CDN prefix is clear or encrypted-looking,
/// preview MP3 availability, and music-video DASH DRM manifest shape.
/// </summary>
public sealed class AudioFormatProbe
{
    const int PrefixBytes = 512;

    readonly ITransport _transport;
    readonly IHttpExchange _http;
    readonly Func<string, Xm.ExtensionKind, CancellationToken, Task<ByteString?>> _fetchExtension;
    readonly WaveeLogger _log;

    public AudioFormatProbe(
        ITransport transport,
        IHttpExchange http,
        Func<string, Xm.ExtensionKind, CancellationToken, Task<ByteString?>> fetchExtension,
        WaveeLogger log = default)
    {
        _transport = transport;
        _http = http;
        _fetchExtension = fetchExtension;
        _log = log;
    }

    public static AudioFormatProbe? FromEnvironment(
        ITransport transport,
        IHttpExchange http,
        Func<string, Xm.ExtensionKind, CancellationToken, Task<ByteString?>> fetchExtension,
        WaveeLogger log = default)
    {
        var flag = Environment.GetEnvironmentVariable("WAVEE_AUDIO_FORMAT_PROBE");
        if (!string.Equals(flag, "1", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(flag, "true", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(flag, "yes", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return new AudioFormatProbe(transport, http, fetchExtension, log);
    }

    public async Task ProbeAsync(string trackUri, M.Track track, Af.AudioFilesExtensionResponse? audioFiles, CancellationToken ct)
    {
        _log.Info($"probe {trackUri}: starting audio-format/CDN/DRM probe");
        var candidates = CollectAudioCandidates(track, audioFiles);
        if (candidates.Count == 0)
        {
            _log.Info($"probe {trackUri}: no audio file candidates in TRACK_V4/AUDIO_FILES");
        }
        else
        {
            _log.Info($"probe {trackUri}: candidates {DescribeCandidates(candidates)}");
            foreach (var c in candidates)
                await ProbeAudioCandidateAsync(c, ct).ConfigureAwait(false);
        }

        await ProbePreviewsAsync(track, ct).ConfigureAwait(false);
        await ProbeVideoDrmAsync(trackUri, track, ct).ConfigureAwait(false);
        _log.Info($"probe {trackUri}: done");
    }

    async Task ProbeAudioCandidateAsync(AudioCandidate c, CancellationToken ct)
    {
        var fileIdHex = Convert.ToHexStringLower(c.FileId);
        var route = "/storage-resolve/files/audio/interactive/" + fileIdHex;
        Resp sr;
        try
        {
            sr = await _transport.Request(Channel.Spclient, route, default, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Info($"probe audio {c.Label}: storage-resolve threw {ex.Message}");
            return;
        }

        if (!sr.Ok)
        {
            _log.Info($"probe audio {c.Label}: storage-resolve status={sr.Status}");
            return;
        }

        S.StorageResolveResponse parsed;
        try { parsed = S.StorageResolveResponse.Parser.ParseFrom(sr.Body); }
        catch (Exception ex)
        {
            _log.Info($"probe audio {c.Label}: storage-resolve parse failed ({ex.Message})");
            return;
        }

        if (parsed.Result == S.StorageResolveResponse.Types.Result.Restricted)
        {
            _log.Info($"probe audio {c.Label}: storage-resolve RESTRICTED");
            return;
        }
        if (parsed.Cdnurl.Count == 0)
        {
            _log.Info($"probe audio {c.Label}: storage-resolve returned no CDN URLs");
            return;
        }

        var first = parsed.Cdnurl[0];
        var prefix = await FetchPrefixAsync(first, ct).ConfigureAwait(false);
        var sniff = DescribePrefix(prefix);
        _log.Info($"probe audio {c.Label}: cdn={parsed.Cdnurl.Count} prefix={sniff} first={HexPrefix(prefix, 24)}");
    }

    async Task ProbePreviewsAsync(M.Track track, CancellationToken ct)
    {
        if (track.Preview.Count == 0) return;
        foreach (var p in track.Preview)
        {
            if (p.FileId.Length == 0) continue;
            var id = Convert.ToHexStringLower(p.FileId.Span);
            var url = "https://p.scdn.co/mp3-preview/" + id;
            var prefix = await FetchPrefixAsync(url, ct).ConfigureAwait(false);
            _log.Info($"probe preview {id} {p.Format}: prefix={DescribePrefix(prefix)} first={HexPrefix(prefix, 24)}");
        }
    }

    async Task ProbeVideoDrmAsync(string trackUri, M.Track track, CancellationToken ct)
    {
        var manifestIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddOriginalVideoManifestIds(track, manifestIds);

        try
        {
            var assocPayload = await _fetchExtension(trackUri, Xm.ExtensionKind.VideoAssociations, ct).ConfigureAwait(false);
            if (assocPayload is { Length: > 0 })
            {
                var assoc = Xm.VideoAssociations.Parser.ParseFrom(assocPayload);
                var linked = assoc.Association?.AssociatedUri;
                var files = assoc.Association?.Files?.File.Count ?? 0;
                _log.Info($"probe video {trackUri}: VIDEO_ASSOCIATIONS linked={linked ?? "<none>"} files={files}");

                if (!string.IsNullOrWhiteSpace(linked))
                {
                    var videoTrackPayload = await _fetchExtension(linked, Xm.ExtensionKind.TrackV4, ct).ConfigureAwait(false);
                    if (videoTrackPayload is { Length: > 0 })
                    {
                        var videoTrack = M.Track.Parser.ParseFrom(videoTrackPayload);
                        AddOriginalVideoManifestIds(videoTrack, manifestIds);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Info($"probe video {trackUri}: VIDEO_ASSOCIATIONS probe failed ({ex.Message})");
        }

        if (manifestIds.Count == 0)
        {
            _log.Info($"probe video {trackUri}: no music-video manifest id discovered");
            return;
        }

        foreach (var manifestId in manifestIds)
            await ProbeVideoManifestAsync(manifestId, ct).ConfigureAwait(false);
    }

    async Task ProbeVideoManifestAsync(string manifestId, CancellationToken ct)
    {
        var route = "/manifests/v9/json/sources/" + manifestId + "/options/supports_drm";
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Accept"] = "*/*",
            ["Origin"] = "https://xpui.app.spotify.com",
            ["Referer"] = "https://xpui.app.spotify.com/",
        };

        Resp resp;
        try
        {
            resp = await _transport.Request(Channel.Spclient, route, default, ct, method: "GET", headers: headers).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Info($"probe video manifest {manifestId}: GET threw {ex.Message}");
            return;
        }

        if (!resp.Ok)
        {
            _log.Info($"probe video manifest {manifestId}: status={resp.Status}");
            return;
        }

        var json = Encoding.UTF8.GetString(resp.Body);
        _log.Info($"probe video manifest {manifestId}: {DescribeVideoManifest(json)}");
    }

    async Task<byte[]> FetchPrefixAsync(string url, CancellationToken ct)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Range"] = "bytes=0-" + (PrefixBytes - 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Accept"] = "*/*",
        };
        using var resp = await _http.SendAsync(new HttpReq("GET", url, headers), ct).ConfigureAwait(false);
        if (resp.Status is < 200 or >= 300)
            return Encoding.ASCII.GetBytes("HTTP " + resp.Status.ToString(System.Globalization.CultureInfo.InvariantCulture));

        var buf = new byte[PrefixBytes];
        var read = 0;
        while (read < buf.Length)
        {
            var n = await resp.Body.ReadAsync(buf.AsMemory(read, buf.Length - read), ct).ConfigureAwait(false);
            if (n <= 0) break;
            read += n;
        }
        Array.Resize(ref buf, read);
        return buf;
    }

    internal static IReadOnlyList<AudioCandidate> CollectAudioCandidates(M.Track track, Af.AudioFilesExtensionResponse? audioFiles)
    {
        var list = new List<AudioCandidate>();
        AddFiles(list, "track", track.File);

        var altIndex = 0;
        foreach (var alt in track.Alternative)
            AddFiles(list, "alt" + altIndex++, alt.File);

        if (audioFiles is not null)
        {
            var ix = 0;
            foreach (var ef in audioFiles.Files)
            {
                if (ef.File is { FileId.Length: > 0 } file)
                    list.Add(new AudioCandidate("audio_files" + ix++, file.Format, file.FileId.ToByteArray()));
            }
        }

        return Dedup(list);
    }

    static void AddFiles(List<AudioCandidate> list, string source, IEnumerable<M.AudioFile> files)
    {
        var ix = 0;
        foreach (var f in files)
        {
            if (f.FileId.Length == 0) continue;
            list.Add(new AudioCandidate(source + ix++, f.Format, f.FileId.ToByteArray()));
        }
    }

    static IReadOnlyList<AudioCandidate> Dedup(List<AudioCandidate> input)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var output = new List<AudioCandidate>(input.Count);
        foreach (var c in input)
        {
            var key = Convert.ToHexString(c.FileId) + ":" + c.Format;
            if (seen.Add(key)) output.Add(c);
        }
        return output;
    }

    static string DescribeCandidates(IReadOnlyList<AudioCandidate> candidates)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < candidates.Count; i++)
        {
            if (i > 0) sb.Append(", ");
            var c = candidates[i];
            sb.Append(c.Source).Append(':').Append(c.Format).Append(':').Append(Convert.ToHexStringLower(c.FileId));
        }
        return sb.ToString();
    }

    static void AddOriginalVideoManifestIds(M.Track track, HashSet<string> manifestIds)
    {
        foreach (var v in track.OriginalVideo)
            if (v.Gid.Length > 0)
                manifestIds.Add(Convert.ToHexStringLower(v.Gid.Span));
    }

    internal static string DescribePrefix(ReadOnlySpan<byte> prefix)
    {
        if (prefix.Length == 0) return "empty";
        if (prefix.Length >= 5 && prefix[0] == (byte)'H' && prefix[1] == (byte)'T' && prefix[2] == (byte)'T' && prefix[3] == (byte)'P')
            return Encoding.ASCII.GetString(prefix);
        if (prefix.Length >= 3 && prefix[0] == (byte)'I' && prefix[1] == (byte)'D' && prefix[2] == (byte)'3')
            return "clear-mp3:id3";
        if (prefix.Length >= 2 && prefix[0] == 0xFF && (prefix[1] & 0xE0) == 0xE0)
            return "clear-mp3:frame-sync";
        if (HasMagic(prefix, "OggS"u8, 0))
            return "clear-ogg:offset0";
        if (HasMagic(prefix, "OggS"u8, SpotifyAesCtr.SpotifyHeaderSize))
            return "clear-ogg:offset0xa7";
        if (HasMagic(prefix, "fLaC"u8, 0))
            return "clear-flac:offset0";
        if (HasMagic(prefix, "fLaC"u8, SpotifyAesCtr.SpotifyHeaderSize))
            return "clear-flac:offset0xa7";
        if (prefix.Length >= 12 && prefix[4] == (byte)'f' && prefix[5] == (byte)'t' && prefix[6] == (byte)'y' && prefix[7] == (byte)'p')
            return "clear-mp4/iso-bmff:ftyp";
        return "encrypted-or-unknown";
    }

    static bool HasMagic(ReadOnlySpan<byte> bytes, ReadOnlySpan<byte> magic, int offset)
        => bytes.Length >= offset + magic.Length && bytes.Slice(offset, magic.Length).SequenceEqual(magic);

    static string HexPrefix(ReadOnlySpan<byte> bytes, int count)
    {
        var n = Math.Min(bytes.Length, count);
        return n == 0 ? "<empty>" : Convert.ToHexStringLower(bytes[..n]);
    }

    internal static string DescribeVideoManifest(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var content = root;
            if (root.TryGetProperty("contents", out var contents)
                && contents.ValueKind == JsonValueKind.Array
                && contents.GetArrayLength() > 0)
                content = contents[0];
            if (root.TryGetProperty("sources", out var sources)
                && sources.ValueKind == JsonValueKind.Array
                && sources.GetArrayLength() > 0)
                content = sources[0];

            var sb = new StringBuilder();
            sb.Append("drm=");
            AppendEncryptionInfo(sb, content, root);
            sb.Append("; profiles=");
            AppendProfileInfo(sb, content);
            return sb.ToString();
        }
        catch (Exception ex)
        {
            return "manifest parse failed: " + ex.Message;
        }
    }

    static void AppendEncryptionInfo(StringBuilder sb, JsonElement content, JsonElement root)
    {
        var host = content.TryGetProperty("encryption_infos", out _) ? content : root;
        if (!host.TryGetProperty("encryption_infos", out var infos) || infos.ValueKind != JsonValueKind.Array)
        {
            sb.Append("<none>");
            return;
        }

        var index = 0;
        var wrote = false;
        foreach (var info in infos.EnumerateArray())
        {
            if (wrote) sb.Append(", ");
            wrote = true;
            sb.Append(index++)
              .Append(':')
              .Append(GetString(info, "key_system") ?? "<unknown>")
              .Append(":license=")
              .Append(string.IsNullOrWhiteSpace(GetString(info, "license_server_endpoint")) ? "<none>" : "<set>");
        }
        if (!wrote) sb.Append("<empty>");
    }

    static void AppendProfileInfo(StringBuilder sb, JsonElement content)
    {
        if (!content.TryGetProperty("profiles", out var profiles) || profiles.ValueKind != JsonValueKind.Array)
        {
            sb.Append("<none>");
            return;
        }

        var wrote = false;
        foreach (var p in profiles.EnumerateArray())
        {
            if (wrote) sb.Append(", ");
            wrote = true;
            sb.Append(GetInt32(p, "id") ?? 0)
              .Append(':')
              .Append(GetString(p, "file_type") ?? "<type>")
              .Append(':')
              .Append(GetString(p, "video_codec") ?? GetString(p, "audio_codec") ?? "<codec>")
              .Append(":enc=");
            AppendEncryptionIndex(sb, p);
        }
        if (!wrote) sb.Append("<empty>");
    }

    static void AppendEncryptionIndex(StringBuilder sb, JsonElement p)
    {
        if (p.TryGetProperty("encryption_index", out var index) && index.TryGetInt32(out var one))
        {
            sb.Append(one);
            return;
        }

        if (p.TryGetProperty("encryption_indices", out var many) && many.ValueKind == JsonValueKind.Array)
        {
            sb.Append('[');
            var wrote = false;
            foreach (var e in many.EnumerateArray())
            {
                if (!e.TryGetInt32(out var v)) continue;
                if (wrote) sb.Append(',');
                wrote = true;
                sb.Append(v);
            }
            sb.Append(']');
            return;
        }

        sb.Append("<none>");
    }

    static string? GetString(JsonElement e, string name) => e.TryGetProperty(name, out var v) ? v.GetString() : null;
    static int? GetInt32(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.TryGetInt32(out var i) ? i : null;

    internal readonly record struct AudioCandidate(string Source, M.AudioFile.Types.Format Format, byte[] FileId)
    {
        public string Label => Source + ":" + Format + ":" + Convert.ToHexStringLower(FileId);
    }
}
