using System;
using System.IO;
using System.Text.Json;
using Wavee.SpotifyLive;
using Xunit;

namespace Wavee.Tests;

/// <summary>
/// The v9 video-manifest parser, driven by a REAL Spotify manifest captured with
/// <c>Wavee.exe --spotify-video-manifest spotify:track:&lt;id&gt;</c> and scrubbed of its one tokenised (subtitle) URL.
/// Using the real thing is the point: the reason a music video played silently was a belief that the manifest carried no
/// audio, and only the actual bytes could settle it. The fixture has 19 profiles — including TWO PlayReady-indexed
/// audio profiles, one AAC and one Opus — which is exactly the shape the selection rules have to survive.
/// </summary>
public class SpotifyVideoManifestTests
{
    static string FixturePath => Path.Combine(AppContext.BaseDirectory, "Fixtures", "spotify-video-manifest-v9.json");

    static SpotifyVideoManifest Real() => SpotifyVideoManifest.FromJson(File.ReadAllText(FixturePath));

    /// <summary>The fixture is a REAL capture, and a real capture arrives with signed CDN grants in its segment templates
    /// (<c>token</c>/<c>fauth</c>/<c>token_ak</c>/<c>token_cf</c>). They are scrubbed to placeholders — the URL SHAPE is
    /// what the parser must handle, not the grant. This guards the next person who refreshes the fixture.</summary>
    [Fact]
    public void Fixture_IsScrubbedOfSignedCdnGrants()
    {
        string raw = File.ReadAllText(FixturePath);
        Assert.DoesNotContain("fauth=eyJ", raw, StringComparison.Ordinal);   // a signed JWT
        Assert.DoesNotContain("hmac%3D", raw, StringComparison.Ordinal);     // an Akamai token
        Assert.Contains("token=SCRUBBED", raw, StringComparison.Ordinal);    // …but the query shape is still exercised
    }

    // ── the video side (regression guard: M2 must not disturb it) ────────────────────────────────────────────────────

    [Fact]
    public void RealManifest_SelectsAConservativePlayReadyVideoProfile()
    {
        var m = Real();
        Assert.True(m.HasPlayReadyMp4);
        Assert.StartsWith("avc1", m.VideoCodec, StringComparison.OrdinalIgnoreCase);
        Assert.InRange(m.Height, 1, 480);           // the conservative ≤480p cap
        Assert.True(m.SegmentCount > 0);
        Assert.Equal(4, m.SegmentStrideSeconds);    // Spotify names segments by absolute time, stepping by segment_length
        Assert.Contains("/profiles/" + m.ProfileId + "/", m.InitUrl, StringComparison.Ordinal);
    }

    [Fact]
    public void RealManifest_ExposesEveryH264Rung_InAscendingOrder()
    {
        var m = Real();
        Assert.Collection(m.VideoProfiles,
            p => Assert.Equal(180, p.Height),
            p => Assert.Equal(240, p.Height),
            p => Assert.Equal(320, p.Height),
            p => Assert.Equal(480, p.Height),
            p => Assert.Equal(720, p.Height),
            p => Assert.Equal(1080, p.Height));
        Assert.Equal(480, m.Height); // catalog expansion must not make startup more aggressive
    }

    [Fact]
    public void RealManifest_ProjectsStableQualityIdsAndSignedAddresses()
    {
        var d = Real().ToDashDescriptor();
        Assert.NotNull(d?.Catalog);
        var video = Assert.Single(d!.Catalog!.Tracks, t => t.Kind == FluentGpu.Media.TrackKind.Video);
        Assert.Equal(6, video.Representations.Count);
        for (int i = 0; i < video.Representations.Count; i++)
        {
            var rep = video.Representations[i];
            Assert.Equal(rep.Id, rep.Quality.Id);
            Assert.Contains("/profiles/" + rep.Id + "/", rep.InitUrl, StringComparison.Ordinal);
            Assert.Contains("?token=", rep.SegmentSuffix, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void RealManifest_CarriesPlayReadyInitDataAndBothKeyIdForms()
    {
        var m = Real();
        Assert.NotNull(m.Pssh);
        Assert.NotNull(m.Pro);                      // the PlayReady Object, extracted from the PSSH
        Assert.NotNull(m.CencKid);
        Assert.NotNull(m.PlayReadyKid);
        Assert.Matches("^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$", m.CencKid!);
    }

    // ── the audio side (M2) ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void RealManifest_SelectsTheVideosOwnAacSoundtrack()
    {
        var m = Real();
        Assert.True(m.HasAudio, "the real manifest DOES carry the video's own audio — that is the whole premise of M2");
        Assert.Equal("mp4a.40.2", m.AudioCodec);    // AAC-LC
        Assert.True(m.AudioBitrate > 0);
    }

    [Fact]
    public void RealManifest_NeverSelectsOpus_EvenThoughItSitsUnderTheSamePlayReadyIndex()
    {
        // The fixture offers an Opus profile with the same encryption indices as the AAC one. Opus is not decodable by
        // the protected Media Foundation pipeline, and picking it would fail deep inside the CDM rather than cleanly.
        using var doc = JsonDocument.Parse(File.ReadAllText(FixturePath));
        var (content, _) = SpotifyVideoManifest.ResolveHosts(doc.RootElement);
        bool opusOffered = false;
        foreach (var p in content.GetProperty("profiles").EnumerateArray())
            if (p.TryGetProperty("audio_codec", out var ac) && ac.GetString() is { } c
                && c.Contains("opus", StringComparison.OrdinalIgnoreCase))
                opusOffered = true;
        Assert.True(opusOffered, "fixture no longer exercises the Opus trap — pick a manifest that does");

        Assert.DoesNotContain("opus", Real().AudioCodec, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RealManifest_AudioIsAddressedByItsOwnProfileId_OnTheSameTemplates()
    {
        var m = Real();
        Assert.NotEqual(m.ProfileId, m.AudioProfileId);
        Assert.Contains("/profiles/" + m.AudioProfileId + "/", m.AudioInitUrl, StringComparison.Ordinal);
        Assert.Contains("/profiles/" + m.AudioProfileId + "/", m.AudioSegmentPrefix + m.AudioSegmentBaseUrl, StringComparison.Ordinal);
        // Same template family, same file type, same suffix shape as the video — only the profile differs. The suffix
        // carries the CDN's signed query, so it must survive the split verbatim (a mangled query = 403 on every segment).
        Assert.Equal(m.SegmentSuffix, m.AudioSegmentSuffix);
        Assert.Contains("/inits/mp4?", m.AudioInitUrl, StringComparison.Ordinal);
        Assert.Contains("?token=", m.AudioSegmentSuffix, StringComparison.Ordinal);
    }

    [Fact]
    public void RealManifest_AudioSharesTheVideosContentKey_SoNoExtraLicenceIsNeeded()
    {
        // This equality is why playing the soundtrack needs no additional DRM work at all: the licence already acquired
        // (and reported USABLE) for the video covers the audio stream too.
        var m = Real();
        Assert.Equal(m.CencKid, m.AudioCencKid);
    }

    [Fact]
    public void RealManifest_ProjectsAudioOntoTheNativeDescriptor()
    {
        var d = Real().ToDashDescriptor();
        Assert.NotNull(d);
        Assert.False(string.IsNullOrEmpty(d!.AudioInitUrl));
        Assert.False(string.IsNullOrEmpty(d.AudioSegmentSuffix));
        Assert.Equal("mp4a.40.2", d.AudioCodecs);
        // The two representations ride the SAME segment grid — that is what lets one stride/count drive both.
        Assert.True(d.SegmentCount > 0);
        Assert.Equal(4, d.SegmentStride);
        Assert.Equal(0, d.StartNumber);
    }

    // ── degradation: no audio must never become a failure ────────────────────────────────────────────────────────────

    [Fact]
    public void ManifestWithoutAudioProfiles_StillPlaysVideoOnly()
    {
        // Strip every audio profile from the real manifest: the video side must be untouched and the audio side simply
        // absent — a manifest with no usable soundtrack has to degrade, never fail.
        string stripped = StripAudioProfiles(File.ReadAllText(FixturePath));
        var m = SpotifyVideoManifest.FromJson(stripped);

        Assert.True(m.HasPlayReadyMp4);
        Assert.False(m.HasAudio);
        Assert.Equal("", m.AudioCodec);
        Assert.Null(m.AudioCencKid);

        var d = m.ToDashDescriptor();
        Assert.NotNull(d);
        Assert.Null(d!.AudioInitUrl);
        Assert.Null(d.AudioCodecs);
        Assert.Equal(Real().InitUrl, d.InitUrl);   // the video pick is unchanged by the audio arm
    }

    [Fact]
    public void OpusOnlyManifest_ReportsNoAudio_RatherThanPickingAnUndecodableStream()
    {
        string aacless = StripAacProfiles(File.ReadAllText(FixturePath));
        var m = SpotifyVideoManifest.FromJson(aacless);
        Assert.True(m.HasPlayReadyMp4);
        Assert.False(m.HasAudio);
    }

    // Rewrite the fixture JSON keeping only profiles that satisfy <paramref name="keep"/>.
    static string FilterProfiles(string json, Func<JsonElement, bool> keep)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            foreach (var prop in root.EnumerateObject())
            {
                if (prop.Name != "contents") { prop.WriteTo(w); continue; }
                w.WritePropertyName("contents");
                w.WriteStartArray();
                foreach (var content in prop.Value.EnumerateArray())
                {
                    w.WriteStartObject();
                    foreach (var cp in content.EnumerateObject())
                    {
                        if (cp.Name != "profiles") { cp.WriteTo(w); continue; }
                        w.WritePropertyName("profiles");
                        w.WriteStartArray();
                        foreach (var profile in cp.Value.EnumerateArray())
                            if (keep(profile)) profile.WriteTo(w);
                        w.WriteEndArray();
                    }
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }
            w.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    static string StripAudioProfiles(string json)
        => FilterProfiles(json, p => !p.TryGetProperty("audio_codec", out var ac) || ac.ValueKind != JsonValueKind.String);

    static string StripAacProfiles(string json)
        => FilterProfiles(json, p => !p.TryGetProperty("audio_codec", out var ac) || ac.ValueKind != JsonValueKind.String
                                     || !ac.GetString()!.StartsWith("mp4a", StringComparison.OrdinalIgnoreCase));
}
