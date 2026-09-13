using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using FluentGpu.Media;
using FluentGpu.WindowsApi.Media.PlayReady;

namespace Wavee.SpotifyLive;

/// <summary>
/// Parses Spotify's v9 video manifest JSON (<c>/manifests/v9/json/sources/{manifestId}/options/supports_drm</c>) into a
/// native-descriptor-friendly model. IMPROVEMENT over the WaveeMusic reference: it does NOT synthesise a DASH MPD (that
/// round-trip existed only to feed WinRT AdaptiveMediaSource, which FluentGpu does not use). Instead it produces the
/// segment addressing the in-process CENC source needs directly (init URL + <c>base + prefix + &lt;timestamp&gt; + suffix</c>
/// with a TIMESTAMP STRIDE), plus PlayReady init data (PSSH/PRO) and the byte-swapped PlayReady key id.
/// <para>Pure C# (System.Text.Json + span parsing), TerraFX-free, headless-unit-testable. It reports which DRM systems
/// the manifest advertises so the caller can gate: FluentGpu ships PlayReady only (no Widevine lane).</para>
/// </summary>
sealed class SpotifyVideoManifest
{
    public string EncodingId { get; init; } = "";
    public int SegmentLengthSeconds { get; init; } = 4;
    public long DurationMs { get; init; }

    /// <summary>True when a <c>playready</c> encryption entry with an mp4/H.264 profile is present — the FluentGpu DRM lane.</summary>
    public bool HasPlayReadyMp4 { get; init; }
    /// <summary>True when the manifest advertises a <c>widevine</c> entry (NOT playable here — reported for the go/no-go).</summary>
    public bool HasWidevine { get; init; }

    // ── selected mp4/PlayReady video profile (a single conservative ≤480p representation) ──
    public int ProfileId { get; init; }
    public string VideoCodec { get; init; } = "";
    public int Width { get; init; }
    public int Height { get; init; }
    /// <summary>Every compatible PlayReady/H.264 rung, ordered from lowest to highest quality.</summary>
    public IReadOnlyList<SpotifyVideoManifestProfile> VideoProfiles { get; init; } = Array.Empty<SpotifyVideoManifestProfile>();

    // ── segment addressing for the native CENC source (base + prefix + <startTs + i*strideSeconds> + suffix) ──
    public string InitUrl { get; init; } = "";
    public string SegmentBaseUrl { get; init; } = "";
    public string SegmentPrefix { get; init; } = "";
    public string SegmentSuffix { get; init; } = "";
    public int SegmentStrideSeconds { get; init; } = 4;   // Spotify names segments by absolute time; step == segment length
    public int SegmentCount { get; init; }

    // ── selected mp4/PlayReady AUDIO profile: the VIDEO'S OWN soundtrack ──────────────────────────────────────────────
    // A music video is its own edit — intros, spoken pre/post-roll, a different arrangement — so its audio is NOT the
    // plain song's audio track and cannot be substituted for it. The manifest does carry it: the audio profiles sit in
    // the same `profiles` array, under the same PlayReady encryption index, addressed by the same templates via their own
    // profile_id, and (verified on real manifests) under the SAME content key as the video — so playing it needs no
    // additional licence work at all. This parser used to drop every audio profile on the floor, which is the whole
    // reason a music video played silently.
    public int AudioProfileId { get; init; }
    /// <summary>The selected audio profile's codec string, e.g. <c>mp4a.40.2</c> (AAC-LC). Empty ⇒ no usable audio.</summary>
    public string AudioCodec { get; init; } = "";
    public int AudioBitrate { get; init; }
    public string AudioInitUrl { get; init; } = "";
    public string AudioSegmentBaseUrl { get; init; } = "";
    public string AudioSegmentPrefix { get; init; } = "";
    public string AudioSegmentSuffix { get; init; } = "";
    /// <summary>The audio profile's own CENC key id (hyphenated GUID). Equal to <see cref="CencKid"/> on real Spotify
    /// manifests — that equality is what makes the already-acquired video licence cover the audio too.</summary>
    public string? AudioCencKid { get; init; }
    /// <summary>Every compatible AAC representation. Opus/WebM is intentionally excluded.</summary>
    public IReadOnlyList<SpotifyVideoManifestProfile> AudioProfiles { get; init; } = Array.Empty<SpotifyVideoManifestProfile>();

    /// <summary>Whether a usable (AAC, PlayReady-indexed, addressable) audio representation was selected.</summary>
    public bool HasAudio => AudioCodec.Length > 0 && AudioInitUrl.Length > 0;

    // ── PlayReady protection ──
    public byte[]? Pssh { get; init; }
    public byte[]? Pro { get; init; }
    public string? CencKid { get; init; }            // hyphenated GUID
    public string? PlayReadyKid { get; init; }        // base64, first-8-bytes byte-swapped
    public string? LicenseServerEndpoint { get; init; }

    public static SpotifyVideoManifest FromJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return FromJson(doc.RootElement);
    }

    public static SpotifyVideoManifest FromJson(JsonElement root)
    {
        var (content, templateHost) = ResolveHosts(root);

        string encodingId = Str(content, "encoding_id") ?? Str(content, "media_id") ?? "";
        int segLen = Int(content, "segment_length") ?? 4;
        if (segLen <= 0) segLen = 4;

        long durMs = Long(content, "duration") ?? 0;
        if (durMs <= 0)
        {
            long start = Long(content, "start_time_millis") ?? Long(root, "start_time_millis") ?? 0;
            long end = Long(content, "end_time_millis") ?? Long(root, "end_time_millis") ?? 0;
            if (end > start) durMs = end - start;
        }

        string initTpl = Str(templateHost, "initialization_template") ?? "";
        string segTpl = Str(templateHost, "segment_template") ?? "";
        string baseUrl = templateHost.TryGetProperty("base_urls", out var bus) && bus.ValueKind == JsonValueKind.Array && bus.GetArrayLength() > 0
            ? bus[0].GetString() ?? "" : "";

        // ── encryption_infos: locate the playready entry (its index gates profile selection) + note widevine presence ──
        byte[]? pssh = null, pro = null;
        string? licenseEndpoint = null;
        int? playReadyIndex = null;
        bool hasWidevine = false;
        var encHost = content.TryGetProperty("encryption_infos", out _) ? content : root;
        if (encHost.TryGetProperty("encryption_infos", out var encInfos) && encInfos.ValueKind == JsonValueKind.Array)
        {
            int index = 0;
            foreach (var ei in encInfos.EnumerateArray())
            {
                string? ks = Str(ei, "key_system");
                if (string.Equals(ks, "widevine", StringComparison.Ordinal)) hasWidevine = true;
                if (string.Equals(ks, "playready", StringComparison.Ordinal) && playReadyIndex is null)
                {
                    playReadyIndex = index;
                    if (Str(ei, "encryption_data") is { Length: > 0 } b64)
                    {
                        try { pssh = Convert.FromBase64String(b64); pro = ExtractProFromPssh(pssh); } catch { pssh = null; }
                    }
                    licenseEndpoint = Str(ei, "license_server_endpoint");
                }
                index++;
            }
        }

        // ── profiles: mp4 profiles matching the playready encryption index — a conservative ≤480p VIDEO representation
        // and, separately, the AAC AUDIO representation that is the video's own soundtrack ──
        int profileId = 0; string vcodec = ""; int w = 0, h = 0;
        string? cencKid = null, prKid = null;
        bool hasPlayReadyMp4 = false;
        SpotifyVideoManifestProfile? bestAudio = null;
        var videoProfiles = new List<SpotifyVideoManifestProfile>();
        var audioProfiles = new List<SpotifyVideoManifestProfile>();
        string? audioKid = null;
        if (content.TryGetProperty("profiles", out var profiles) && profiles.ValueKind == JsonValueKind.Array)
        {
            SpotifyVideoManifestProfile? best = null;
            foreach (var p in profiles.EnumerateArray())
            {
                if (!string.Equals(Str(p, "file_type"), "mp4", StringComparison.Ordinal)) continue;
                if (!ProfileMatchesEncryptionIndex(p, playReadyIndex)) continue;
                int pid = Int(p, "id") ?? 0;

                if (Str(p, "video_codec") is { Length: > 0 } vc && IsH264(vc))
                {
                    hasPlayReadyMp4 = true;
                    string? profileKid = FormatCencKeyId(Str(p, "key_id"));
                    cencKid ??= profileKid;
                    prKid ??= FormatPlayReadyKeyId(Str(p, "key_id"));

                    int pw = Int(p, "video_width") ?? Int(p, "width") ?? 0;
                    int ph = Int(p, "video_height") ?? Int(p, "height") ?? 0;
                    int bw = Int(p, "max_bitrate") ?? Int(p, "bandwidth_estimate") ?? Int(p, "video_bitrate") ?? 0;
                    var cand = new SpotifyVideoManifestProfile(pid, vc, pw, ph, bw, profileKid);
                    videoProfiles.Add(cand);
                    best = ChooseConservative(best, cand);
                    continue;
                }

                // Audio-only profile. It must be AAC: real manifests also advertise an OPUS profile under the SAME
                // PlayReady index, and Opus is not decodable by the protected Media Foundation pipeline we feed — picking
                // it would produce a session that fails deep inside the CDM rather than a clear "no audio".
                if (Str(p, "audio_codec") is { Length: > 0 } ac && IsAac(ac))
                {
                    int abw = Int(p, "audio_bitrate") ?? Int(p, "max_bitrate") ?? Int(p, "bandwidth_estimate") ?? 0;
                    var cand = new SpotifyVideoManifestProfile(pid, ac, 0, 0, abw, FormatCencKeyId(Str(p, "key_id")));
                    audioProfiles.Add(cand);
                    if (bestAudio is null || cand.Bandwidth > bestAudio.Bandwidth)
                    {
                        bestAudio = cand;
                        audioKid = FormatCencKeyId(Str(p, "key_id"));
                    }
                }
            }
            videoProfiles.Sort(CompareProfiles);
            audioProfiles.Sort(CompareProfiles);
            if (best is { } sel) { profileId = sel.Id; vcodec = sel.Codec; w = sel.Width; h = sel.Height; }
        }

        // ── segment addressing: substitute profile_id + file_type, split at {{segment_timestamp}} ──
        // The templates are per-PROFILE, so the audio representation is addressed by the exact same pair with its own
        // profile_id — which is why adding the video's soundtrack needs no new manifest plumbing, only its profile.
        string initUrl = "", segBase = "", segPrefix = "", segSuffix = "";
        string audioInit = "", audioBase = "", audioPrefix = "", audioSuffix = "";
        int segCount = 0;
        bool addressable = baseUrl.Length > 0 && initTpl.Length > 0 && segTpl.Length > 0;
        if (addressable && hasPlayReadyMp4)
        {
            (initUrl, segBase, segPrefix, segSuffix) = BuildAddressing(baseUrl, initTpl, segTpl, profileId);
            double durS = durMs / 1000.0;
            segCount = durS > 0 ? (int)Math.Ceiling(durS / segLen) : 0;
            if (bestAudio is { } aud)
                (audioInit, audioBase, audioPrefix, audioSuffix) = BuildAddressing(baseUrl, initTpl, segTpl, aud.Id);
        }

        return new SpotifyVideoManifest
        {
            EncodingId = encodingId,
            SegmentLengthSeconds = segLen,
            DurationMs = durMs,
            HasPlayReadyMp4 = hasPlayReadyMp4,
            HasWidevine = hasWidevine,
            ProfileId = profileId,
            VideoCodec = vcodec,
            Width = w,
            Height = h,
            VideoProfiles = videoProfiles.ToArray(),
            InitUrl = initUrl,
            SegmentBaseUrl = segBase,
            SegmentPrefix = segPrefix,
            SegmentSuffix = segSuffix,
            SegmentStrideSeconds = segLen,
            SegmentCount = segCount,
            AudioProfileId = bestAudio?.Id ?? 0,
            AudioCodec = audioInit.Length > 0 ? bestAudio?.Codec ?? "" : "",
            AudioBitrate = bestAudio?.Bandwidth ?? 0,
            AudioInitUrl = audioInit,
            AudioSegmentBaseUrl = audioBase,
            AudioSegmentPrefix = audioPrefix,
            AudioSegmentSuffix = audioSuffix,
            AudioCencKid = audioInit.Length > 0 ? audioKid : null,
            AudioProfiles = audioProfiles.ToArray(),
            Pssh = pssh,
            Pro = pro,
            CencKid = cencKid,
            PlayReadyKid = prKid,
            LicenseServerEndpoint = licenseEndpoint,
        };
    }

    /// <summary>Project the selected mp4/PlayReady profile into the engine's <see cref="DashSourceDescriptor"/> for the
    /// native CENC source: StartNumber 0 + a timestamp stride (Spotify names segments by absolute time), the split
    /// init/segment addressing, and the PSSH. Null when no playable PlayReady mp4 profile / segment addressing resolved.</summary>
    public DashSourceDescriptor? ToDashDescriptor()
    {
        if (!HasPlayReadyMp4 || string.IsNullOrEmpty(InitUrl) || SegmentCount <= 0) return null;
        var tracks = new List<ProtectedTrackDescriptor>(HasAudio ? 2 : 1);
        var videoRepresentations = new List<ProtectedRepresentationDescriptor>(VideoProfiles.Count);
        for (int i = 0; i < VideoProfiles.Count; i++)
        {
            var p = VideoProfiles[i];
            var a = BuildAddressingForProfile(p.Id);
            if (a.Init.Length == 0 || a.Base.Length == 0) continue;
            videoRepresentations.Add(new ProtectedRepresentationDescriptor
            {
                Id = p.Id.ToString(CultureInfo.InvariantCulture),
                Quality = new QualityVariant(
                    p.Id.ToString(CultureInfo.InvariantCulture), p.Bandwidth, new SizeI(p.Width, p.Height), 0,
                    new MediaContentType(Container.Mp4, CodecId.H264, CodecId.None),
                    Label: p.Height > 0 ? p.Height.ToString(CultureInfo.InvariantCulture) + "p" : null),
                InitUrl = a.Init,
                SegmentBaseUrl = a.Base,
                SegmentPrefix = a.Prefix,
                SegmentSuffix = a.Suffix,
                StartNumber = 0,
                SegmentCount = SegmentCount,
                SegmentStride = SegmentStrideSeconds,
                DefaultKid = p.CencKid,
            });
        }
        if (videoRepresentations.Count > 0)
            tracks.Add(new ProtectedTrackDescriptor
            {
                Id = 1,
                Kind = TrackKind.Video,
                Label = "Video",
                IsDefault = true,
                Representations = videoRepresentations,
            });

        if (HasAudio)
        {
            var audioRepresentations = new List<ProtectedRepresentationDescriptor>(AudioProfiles.Count);
            for (int i = 0; i < AudioProfiles.Count; i++)
            {
                var p = AudioProfiles[i];
                var a = BuildAddressingForProfile(p.Id);
                if (a.Init.Length == 0 || a.Base.Length == 0) continue;
                audioRepresentations.Add(new ProtectedRepresentationDescriptor
                {
                    Id = p.Id.ToString(CultureInfo.InvariantCulture),
                    Quality = new QualityVariant(
                        p.Id.ToString(CultureInfo.InvariantCulture), p.Bandwidth, SizeI.Zero, 0,
                        new MediaContentType(Container.Mp4, CodecId.None, CodecId.Aac), Label: "AAC"),
                    InitUrl = a.Init,
                    SegmentBaseUrl = a.Base,
                    SegmentPrefix = a.Prefix,
                    SegmentSuffix = a.Suffix,
                    StartNumber = 0,
                    SegmentCount = SegmentCount,
                    SegmentStride = SegmentStrideSeconds,
                    DefaultKid = p.CencKid,
                });
            }
            if (audioRepresentations.Count > 0)
                tracks.Add(new ProtectedTrackDescriptor
                {
                    Id = 2,
                    Kind = TrackKind.Audio,
                    Label = "Main",
                    Role = TrackRole.Main,
                    IsDefault = true,
                    Representations = audioRepresentations,
                });
        }

        return new DashSourceDescriptor
        {
            Catalog = new ProtectedAdaptiveCatalog { Tracks = tracks },
            InitUrl = InitUrl,
            SegmentBaseUrl = SegmentBaseUrl,
            SegmentPrefix = SegmentPrefix,
            SegmentSuffix = SegmentSuffix,
            StartNumber = 0,                          // Spotify's first segment timestamp is 0
            SegmentCount = SegmentCount,
            SegmentStride = SegmentStrideSeconds,      // segment names step by the segment length (seconds)
            Pssh = Pssh ?? System.Array.Empty<byte>(),
            DefaultKid = CencKid,
            Codecs = VideoCodec,
            // The video's OWN soundtrack, addressed by the same templates under the same content key. Null/empty when the
            // manifest offers no AAC audio under the PlayReady index — the native side then plays video only, exactly as
            // it does today, rather than failing.
            AudioInitUrl = HasAudio ? AudioInitUrl : null,
            AudioSegmentBaseUrl = HasAudio ? AudioSegmentBaseUrl : null,
            AudioSegmentPrefix = HasAudio ? AudioSegmentPrefix : null,
            AudioSegmentSuffix = HasAudio ? AudioSegmentSuffix : null,
            AudioCodecs = HasAudio ? AudioCodec : null,
        };
    }

    (string Init, string Base, string Prefix, string Suffix) BuildAddressingForProfile(int profileId)
    {
        if (profileId == ProfileId) return (InitUrl, SegmentBaseUrl, SegmentPrefix, SegmentSuffix);
        if (profileId == AudioProfileId) return (AudioInitUrl, AudioSegmentBaseUrl, AudioSegmentPrefix, AudioSegmentSuffix);

        // All Spotify v9 representations use the same URL template. Replace only the explicit profile lane in the
        // already-resolved URLs so signed query parameters remain byte-for-byte intact.
        string selected = "/profiles/" + ProfileId.ToString(CultureInfo.InvariantCulture) + "/";
        string replacement = "/profiles/" + profileId.ToString(CultureInfo.InvariantCulture) + "/";
        return (
            InitUrl.Replace(selected, replacement, StringComparison.Ordinal),
            SegmentBaseUrl.Replace(selected, replacement, StringComparison.Ordinal),
            SegmentPrefix.Replace(selected, replacement, StringComparison.Ordinal),
            SegmentSuffix);
    }

    /// <summary>Locate the two elements a v9 manifest splits itself across — the ONE definition, shared with the
    /// <c>--spotify-video-manifest</c> dump so the probe inspects exactly what this parser inspects. Two shapes exist:
    /// <c>contents[0]</c> carries profiles/encryption while the root carries templates/base URLs, or everything sits under
    /// <c>sources[0]</c>. Mirrors the reference's host resolution.</summary>
    internal static (JsonElement Content, JsonElement TemplateHost) ResolveHosts(JsonElement root)
    {
        var content = root;
        var templateHost = root;
        if (root.TryGetProperty("contents", out var contents) && contents.ValueKind == JsonValueKind.Array && contents.GetArrayLength() > 0)
            content = contents[0];
        if (root.TryGetProperty("sources", out var sources) && sources.ValueKind == JsonValueKind.Array && sources.GetArrayLength() > 0)
        { content = sources[0]; templateHost = content; }
        return (content, templateHost);
    }

    /// <summary>Substitute one profile into the init + segment templates and split the media URL at the timestamp token
    /// into the <c>base | prefix | suffix</c> triple the native CENC source concatenates. Shared by the video and audio
    /// representations — same templates, different <c>profile_id</c>.</summary>
    static (string Init, string Base, string Prefix, string Suffix) BuildAddressing(
        string baseUrl, string initTpl, string segTpl, int profileId)
    {
        const string tokenName = "{{segment_timestamp}}";
        string init = baseUrl + Subst(initTpl, profileId);
        string media = baseUrl + Subst(segTpl, profileId);
        int tok = media.IndexOf(tokenName, StringComparison.Ordinal);
        if (tok < 0) return (init, "", "", "");
        string before = media[..tok];
        string suffix = media[(tok + tokenName.Length)..];
        int slash = before.LastIndexOf('/');
        return slash >= 0
            ? (init, before[..(slash + 1)], before[(slash + 1)..], suffix)
            : (init, "", before, suffix);
    }

    // AAC only (mp4a.40.x / "aac"): the protected MF pipeline we hand the stream to decodes AAC, and a manifest that also
    // offers Opus under the same PlayReady index must not silently win the pick.
    static bool IsAac(string codec)
        => codec.StartsWith("mp4a", StringComparison.OrdinalIgnoreCase)
        || codec.StartsWith("aac", StringComparison.OrdinalIgnoreCase);

    static bool IsH264(string codec)
        => codec.StartsWith("avc1", StringComparison.OrdinalIgnoreCase)
        || codec.StartsWith("avc3", StringComparison.OrdinalIgnoreCase)
        || codec.Equals("h264", StringComparison.OrdinalIgnoreCase);

    static int CompareProfiles(SpotifyVideoManifestProfile left, SpotifyVideoManifestProfile right)
    {
        int height = left.Height.CompareTo(right.Height);
        return height != 0 ? height : left.Bandwidth.CompareTo(right.Bandwidth);
    }

    static string Subst(string template, int profileId) => template
        .Replace("{{profile_id}}", profileId.ToString(CultureInfo.InvariantCulture))
        .Replace("{{file_type}}", "mp4");

    static SpotifyVideoManifestProfile? ChooseConservative(SpotifyVideoManifestProfile? best, SpotifyVideoManifestProfile cand)
    {
        if (cand.Width <= 0 || cand.Height <= 0) return best ?? cand;   // keep something even if unsized
        if (cand.Height > 480) return best;                             // start ≤480p (native H.264 conservative)
        if (best is null) return cand;
        if (cand.Height > best.Height || (cand.Height == best.Height && cand.Bandwidth > best.Bandwidth)) return cand;
        return best;
    }

    static bool ProfileMatchesEncryptionIndex(JsonElement profile, int? encryptionIndex)
    {
        if (encryptionIndex is null) return true;
        if (profile.TryGetProperty("encryption_indices", out var indices) && indices.ValueKind == JsonValueKind.Array)
        {
            foreach (var i in indices.EnumerateArray())
                if (i.TryGetInt32(out var v) && v == encryptionIndex.Value) return true;
            return false;
        }
        if (profile.TryGetProperty("encryption_index", out var one) && one.TryGetInt32(out var ov)) return ov == encryptionIndex.Value;
        return true;   // no per-profile index → assume it applies
    }

    // CENC KID: hyphenated GUID string (big-endian display order).
    internal static string? FormatCencKeyId(string? base64KeyId)
    {
        if (string.IsNullOrWhiteSpace(base64KeyId)) return null;
        try
        {
            var b = Convert.FromBase64String(base64KeyId);
            if (b.Length != 16) return null;
            return string.Create(36, b, static (span, kid) =>
            {
                const string hex = "0123456789abcdef";
                int o = 0;
                for (int i = 0; i < kid.Length; i++)
                {
                    if (i is 4 or 6 or 8 or 10) span[o++] = '-';
                    span[o++] = hex[kid[i] >> 4];
                    span[o++] = hex[kid[i] & 0x0F];
                }
            });
        }
        catch { return null; }
    }

    // PlayReady KID: byte-swap the first 8 bytes of the 16-byte CENC KID (mixed-endian GUID), then base64. A wrong swap
    // silently yields no license — ported byte-for-byte from the proven reference.
    internal static string? FormatPlayReadyKeyId(string? base64KeyId)
    {
        if (string.IsNullOrWhiteSpace(base64KeyId)) return null;
        try
        {
            var c = Convert.FromBase64String(base64KeyId);
            if (c.Length != 16) return null;
            var pr = new byte[16];
            pr[0] = c[3]; pr[1] = c[2]; pr[2] = c[1]; pr[3] = c[0];
            pr[4] = c[5]; pr[5] = c[4];
            pr[6] = c[7]; pr[7] = c[6];
            c.AsSpan(8, 8).CopyTo(pr.AsSpan(8));
            return Convert.ToBase64String(pr);
        }
        catch { return null; }
    }

    // Extract the PlayReady Object (PRO) from a CENC PSSH box (v0: …systemId(16)+dataLen(4)+PRO; v1 inserts KID list).
    internal static byte[]? ExtractProFromPssh(byte[] pssh)
    {
        if (pssh.Length < 32) return null;
        try
        {
            if (pssh[4] != (byte)'p' || pssh[5] != (byte)'s' || pssh[6] != (byte)'s' || pssh[7] != (byte)'h') return null;
            byte version = pssh[8];
            int offset = 28;
            if (version > 0)
            {
                if (pssh.Length < offset + 4) return null;
                int kidCount = BinaryPrimitives.ReadInt32BigEndian(pssh.AsSpan(offset, 4));
                offset += 4;
                if (kidCount < 0 || pssh.Length < offset + kidCount * 16 + 4) return null;
                offset += kidCount * 16;
            }
            int proLen = BinaryPrimitives.ReadInt32BigEndian(pssh.AsSpan(offset, 4));
            offset += 4;
            if (proLen <= 0 || offset + proLen > pssh.Length) return null;
            var pro = new byte[proLen];
            pssh.AsSpan(offset, proLen).CopyTo(pro);
            // sanity: a PRO's first 4 bytes are its little-endian total length
            return pro.Length >= 6 && BinaryPrimitives.ReadInt32LittleEndian(pro.AsSpan(0, 4)) == pro.Length ? pro : null;
        }
        catch { return null; }
    }

    static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    static int? Int(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.TryGetInt32(out var i) ? i : null;
    static long? Long(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.TryGetInt64(out var l) ? l : null;
}

sealed record SpotifyVideoManifestProfile(int Id, string Codec, int Width, int Height, int Bandwidth, string? CencKid);
