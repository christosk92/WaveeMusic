// ── Wavee.Tests/SpotifyAudioTests.cs — the format ladder and the AES-CTR stream ───────────────────────────────────
//
// Wave 2's gate for `Spotify/Spotify.Audio.cs`. Two things are worth a test here and the rest is a socket:
//
//   THE LADDER, because it is the difference between "the user's 320 kbps setting is honoured" and "whatever the
//   catalogue happened to list first". It is a pure fold over a span of formats, so every rung and every fallback is
//   one line of assertion — and the fallback RULE (a missing rung falls to the nearest LOWER one, never the higher)
//   is a promise about the user's bandwidth that nothing else in the app states.
//
//   THE CTR MATH, because it is what makes a seek possible at all. A CTR keystream is position-addressable: block n's
//   keystream is AES-ECB(iv + n) regardless of what was read before it, and if that is wrong then a scrub produces
//   noise ten seconds after the bug was written. The facts below pin the property rather than a captured blob: the
//   transform is its own inverse, decrypting a RANGE at its true offset equals decrypting the whole file and slicing
//   it, and a block-straddling range is not special-cased wrong. The key-validation gate is checked against a buffer
//   this test encrypted itself, which is the only vector that can exist without shipping copyrighted audio.

using Wavee;
using Xunit;
using Md = Wavee.Protocol.Metadata;
using Af = Wavee.Protocol.Audiofiles;
using Google.Protobuf;

namespace Wavee.Tests;

public class SpotifyAudioLadderTests
{
    [Theory]
    [InlineData(Spotify.Audio.Format.OggVorbis96, 0)]
    [InlineData(Spotify.Audio.Format.OggVorbis160, 1)]
    [InlineData(Spotify.Audio.Format.Mp3, 1)]
    [InlineData(Spotify.Audio.Format.OggVorbis320, 2)]
    [InlineData(Spotify.Audio.Format.Flac, 3)]
    [InlineData(Spotify.Audio.Format.Flac24, 3)]
    [InlineData(Spotify.Audio.Format.Unknown, -1)]
    public void Every_format_sits_on_a_named_bandwidth_rung(Spotify.Audio.Format format, int rung)
        => Assert.Equal(rung, Spotify.Audio.Rung(format));

    [Theory]
    [InlineData(Md.AudioFile.Types.Format.OggVorbis96, Spotify.Audio.Format.OggVorbis96)]
    [InlineData(Md.AudioFile.Types.Format.OggVorbis160, Spotify.Audio.Format.OggVorbis160)]
    [InlineData(Md.AudioFile.Types.Format.OggVorbis320, Spotify.Audio.Format.OggVorbis320)]
    [InlineData(Md.AudioFile.Types.Format.Mp3320, Spotify.Audio.Format.Mp3)]
    [InlineData(Md.AudioFile.Types.Format.FlacFlac, Spotify.Audio.Format.Flac)]
    [InlineData(Md.AudioFile.Types.Format.FlacFlac24Bit, Spotify.Audio.Format.Flac24)]
    public void The_wire_formats_we_can_decode_map_across(Md.AudioFile.Types.Format wire, Spotify.Audio.Format ours)
        => Assert.Equal(ours, Spotify.Audio.FormatOf(wire));

    /// <summary>The AAC rungs are in the proto and are deliberately not carried: nothing in the app can decode them,
    /// and a format that maps to something we cannot open is a track that fails at the decoder instead of at the
    /// ladder.</summary>
    [Theory]
    [InlineData(Md.AudioFile.Types.Format.Aac24)]
    [InlineData(Md.AudioFile.Types.Format.XheAac24)]
    public void The_aac_rungs_are_not_on_the_ladder(Md.AudioFile.Types.Format wire)
        => Assert.Equal(Spotify.Audio.Format.Unknown, Spotify.Audio.FormatOf(wire));

    /// <summary>THE lossless layout difference, and the one that is invisible until it ships: Ogg and MP3 sit behind
    /// Spotify's 167-byte header, a FLAC does not — it is a raw `fLaC` stream from byte 0 (FLAC plan §1.1; librespot
    /// #1583; go-librespot opens FLAC at 0). Skipping 167 bytes of a FLAC hands the decoder the middle of STREAMINFO,
    /// which fails its magic check on every lossless open.</summary>
    [Theory]
    [InlineData(Spotify.Audio.Format.OggVorbis96, 0xa7)]
    [InlineData(Spotify.Audio.Format.OggVorbis160, 0xa7)]
    [InlineData(Spotify.Audio.Format.OggVorbis320, 0xa7)]
    [InlineData(Spotify.Audio.Format.Mp3, 0xa7)]
    [InlineData(Spotify.Audio.Format.Flac, 0)]
    [InlineData(Spotify.Audio.Format.Flac24, 0)]
    public void The_stream_skips_the_spotify_header_for_every_format_except_flac(Spotify.Audio.Format format, int skip)
        => Assert.Equal(skip, Spotify.Audio.HeaderBytesFor(format));

    /// <summary>The audio-key service does not serve lossless file ids, and its refusals are ACCOUNT-WIDE and
    /// latching — so asking it once for a FLAC would cost every later Ogg open its fast key path for the rest of the
    /// session. The eligibility is a function of the format, decided before anything is asked.</summary>
    [Theory]
    [InlineData(Spotify.Audio.Format.OggVorbis320, true)]
    [InlineData(Spotify.Audio.Format.Mp3, true)]
    [InlineData(Spotify.Audio.Format.Flac, false)]
    [InlineData(Spotify.Audio.Format.Flac24, false)]
    public void Only_the_lossy_formats_may_ask_the_ap_for_a_key(Spotify.Audio.Format format, bool eligible)
        => Assert.Equal(eligible, Spotify.Audio.ApEligible(format));

    static readonly Spotify.Audio.Format[] FullLadder =
    [
        Spotify.Audio.Format.OggVorbis96,
        Spotify.Audio.Format.OggVorbis160,
        Spotify.Audio.Format.OggVorbis320,
    ];

    [Theory]
    [InlineData(Spotify.Audio.Quality.Normal96, 0)]
    [InlineData(Spotify.Audio.Quality.High160, 1)]
    [InlineData(Spotify.Audio.Quality.VeryHigh320, 2)]
    public void A_complete_ladder_gives_the_user_exactly_the_rung_they_chose(Spotify.Audio.Quality quality, int index)
        => Assert.Equal(index, Spotify.Audio.PickRung(FullLadder, quality));

    /// <summary>An account without lossless asking for lossless gets the top OGG rung, not a refusal.</summary>
    [Fact]
    public void Lossless_over_an_ogg_only_ladder_behaves_like_very_high()
        => Assert.Equal(2, Spotify.Audio.PickRung(FullLadder, Spotify.Audio.Quality.Lossless));

    /// <summary>THE fallback rule: when the chosen rung is absent, take the nearest LOWER one. Never spend bandwidth
    /// the user said not to spend.</summary>
    [Fact]
    public void A_missing_rung_falls_to_the_nearest_lower_one()
    {
        Spotify.Audio.Format[] noMiddle = [Spotify.Audio.Format.OggVorbis96, Spotify.Audio.Format.OggVorbis320];
        Assert.Equal(0, Spotify.Audio.PickRung(noMiddle, Spotify.Audio.Quality.High160));
    }

    /// <summary>…and only when there is nothing lower does it go up, because something must play.</summary>
    [Fact]
    public void With_nothing_lower_it_goes_up_rather_than_refusing_to_play()
    {
        Spotify.Audio.Format[] onlyTop = [Spotify.Audio.Format.OggVorbis320];
        Assert.Equal(0, Spotify.Audio.PickRung(onlyTop, Spotify.Audio.Quality.Normal96));
    }

    [Fact]
    public void A_list_with_nothing_playable_on_it_picks_nothing()
    {
        Spotify.Audio.Format[] junk = [Spotify.Audio.Format.Unknown, Spotify.Audio.Format.Unknown];
        Assert.Equal(-1, Spotify.Audio.PickRung(junk, Spotify.Audio.Quality.High160));
        Assert.Equal(-1, Spotify.Audio.PickRung([], Spotify.Audio.Quality.High160));
    }

    // ── the ladder over a real TRACK_V4 payload ──────────────────────────────────────────────────────────────────────

    static Md.AudioFile File(Md.AudioFile.Types.Format format, byte marker)
    {
        var id = new byte[20];
        id[0] = marker;
        return new Md.AudioFile { FileId = ByteString.CopyFrom(id), Format = format };
    }

    /// <summary>The market the unrestricted ladder facts run in; no fixture above restricts it.</summary>
    const string Market = "NL";

    static Md.Track Track(byte gidMarker, params Md.AudioFile[] files)
    {
        var gid = new byte[16];
        gid[0] = gidMarker;
        var track = new Md.Track { Gid = ByteString.CopyFrom(gid), Duration = 210_000 };
        track.File.AddRange(files);
        return track;
    }

    [Fact]
    public void The_chosen_file_id_is_the_rung_the_setting_names()
    {
        Md.Track track = Track(1,
            File(Md.AudioFile.Types.Format.OggVorbis96, 0x96),
            File(Md.AudioFile.Types.Format.OggVorbis160, 0x60),
            File(Md.AudioFile.Types.Format.OggVorbis320, 0x20));

        Spotify.Audio.FileChoice high = Spotify.Audio.Choose(track, null, Spotify.Audio.Quality.VeryHigh320, 0, Market);
        Spotify.Audio.FileChoice low = Spotify.Audio.Choose(track, null, Spotify.Audio.Quality.Normal96, 0, Market);

        Assert.Equal((byte)0x20, high.FileId[0]);
        Assert.Equal(Spotify.Audio.Format.OggVorbis320, high.Fmt);
        Assert.Equal((byte)0x96, low.FileId[0]);
        Assert.Equal(Spotify.Audio.Format.OggVorbis96, low.Fmt);
        Assert.Equal(210_000L, high.DurationMs);
    }

    /// <summary>A track with no files of its own falls through to the first alternative that has some — and the audio
    /// key is then asked for THAT alternative's gid, because the key is bound to the (file, track) pair and the
    /// alternative is a different row on the wire even though the user thinks it is the same song.</summary>
    [Fact]
    public void An_empty_track_falls_through_to_an_alternative_and_carries_its_gid()
    {
        Md.Track main = Track(0x11);
        Md.Track alternative = Track(0x22, File(Md.AudioFile.Types.Format.OggVorbis160, 0x60));
        main.Alternative.Add(alternative);

        Spotify.Audio.FileChoice choice = Spotify.Audio.Choose(main, null, Spotify.Audio.Quality.High160, 0, Market);

        Assert.True(choice.Ok);
        Assert.Equal((byte)0x60, choice.FileId[0]);
        Assert.Equal((byte)0x22, choice.TrackGid[0]);
    }

    /// <summary>RELINKING's other half. A relinked id often keeps its OLD file[] listed — but restricted in the session's
    /// market. Picking that file handed the key service the old gid, which refused (NoKey → Unavailable) where the
    /// unrestricted alternative would have played. The market gate skips the track's own files and takes the
    /// alternative's file AND gid.</summary>
    [Fact]
    public void A_track_restricted_in_the_market_yields_to_its_unrestricted_alternative()
    {
        Md.Track main = Track(0x11, File(Md.AudioFile.Types.Format.OggVorbis160, 0x16));
        main.Restriction.Add(new Md.Restriction { CountriesForbidden = "SEGB" });
        Md.Track alternative = Track(0x22, File(Md.AudioFile.Types.Format.OggVorbis160, 0x60));
        main.Alternative.Add(alternative);

        Spotify.Audio.FileChoice choice = Spotify.Audio.Choose(main, null, Spotify.Audio.Quality.High160, 0, "SE");

        Assert.True(choice.Ok);
        Assert.Equal((byte)0x60, choice.FileId[0]);
        Assert.Equal((byte)0x22, choice.TrackGid[0]);

        // Elsewhere the same payload plays its own file under its own gid: the restriction is a gate, not a verdict.
        Spotify.Audio.FileChoice home = Spotify.Audio.Choose(main, null, Spotify.Audio.Quality.High160, 0, "NL");
        Assert.Equal((byte)0x16, home.FileId[0]);
        Assert.Equal((byte)0x11, home.TrackGid[0]);
    }

    /// <summary>An alternative that is itself ruled out of the market is skipped for the next one; a track whose every
    /// candidate is ruled out is the same named fault as one with no files at all.</summary>
    [Fact]
    public void Restricted_alternatives_are_skipped_and_a_wholly_restricted_track_is_a_named_fault()
    {
        Md.Track main = Track(0x11);
        Md.Track blocked = Track(0x22, File(Md.AudioFile.Types.Format.OggVorbis160, 0x60));
        blocked.Restriction.Add(new Md.Restriction { CountriesAllowed = "USGB" });
        Md.Track open = Track(0x33, File(Md.AudioFile.Types.Format.OggVorbis160, 0x61));
        main.Alternative.Add(blocked);
        main.Alternative.Add(open);

        Spotify.Audio.FileChoice choice = Spotify.Audio.Choose(main, null, Spotify.Audio.Quality.High160, 0, "SE");
        Assert.Equal((byte)0x61, choice.FileId[0]);
        Assert.Equal((byte)0x33, choice.TrackGid[0]);

        main.Alternative.RemoveAt(1);
        Spotify.Audio.FileChoice none = Spotify.Audio.Choose(main, null, Spotify.Audio.Quality.High160, 0, "SE");
        Assert.False(none.Ok);
        Assert.Equal(Spotify.Audio.Fault.NoFile, none.Fault);
    }

    /// <summary>The country gate over the wire's 2-char chunks (`lean_metadata.proto`'s note): an empty
    /// <c>countries_allowed</c> is an empty WHITELIST — no gate — never "allowed nowhere".</summary>
    [Theory]
    [InlineData("", "SE", false, true)]         // empty allowed list = playable
    [InlineData("USGB", "SE", false, false)]    // allowed list without the market = blocked
    [InlineData("USSEGB", "SE", false, true)]   // allowed list with the market = playable
    [InlineData("usse", "SE", false, true)]     // case is not a verdict
    [InlineData("SEGB", "SE", true, false)]     // forbidden list containing the market = blocked
    [InlineData("USGB", "SE", true, true)]      // forbidden list without the market = playable
    [InlineData("", "SE", true, true)]          // empty forbidden list forbids nobody
    [InlineData("USGB", "", false, true)]       // no market yet = nothing to evaluate = playable
    public void The_country_gate_reads_two_char_chunks(string list, string market, bool forbidden, bool expected)
        => Assert.Equal(expected, Spotify.Audio.CountryAllowed(list, market, forbidden));

    /// <summary>`Allowed` over the whole restriction list: only a country rule can rule the market out — the catalogue and
    /// type fields decide nothing, and a track with no restriction is admitted everywhere.</summary>
    [Fact]
    public void Allowed_is_false_only_when_a_country_rule_names_the_market_out()
    {
        Md.Track plain = Track(0x01);
        Assert.True(Spotify.Audio.Allowed(plain, "SE"));

        Md.Track catalogueOnly = Track(0x02);
        catalogueOnly.Restriction.Add(new Md.Restriction
        {
            Catalogue = { Md.Restriction.Types.Catalogue.Subscription }, Type = Md.Restriction.Types.Type.Streaming,
        });
        Assert.True(Spotify.Audio.Allowed(catalogueOnly, "SE"));

        Md.Track emptyWhitelist = Track(0x03);
        emptyWhitelist.Restriction.Add(new Md.Restriction { CountriesAllowed = "" });
        Assert.True(Spotify.Audio.Allowed(emptyWhitelist, "SE"));

        Md.Track whitelisted = Track(0x04);
        whitelisted.Restriction.Add(new Md.Restriction { CountriesAllowed = "USGB" });
        Assert.False(Spotify.Audio.Allowed(whitelisted, "SE"));
        Assert.True(Spotify.Audio.Allowed(whitelisted, "GB"));

        Md.Track forbidden = Track(0x05);
        forbidden.Restriction.Add(new Md.Restriction { CountriesForbidden = "SEGB" });
        Assert.False(Spotify.Audio.Allowed(forbidden, "SE"));
        Assert.True(Spotify.Audio.Allowed(forbidden, "NL"));
    }

    [Fact]
    public void A_track_with_nothing_playable_anywhere_is_a_named_fault()
    {
        Spotify.Audio.FileChoice choice = Spotify.Audio.Choose(Track(0x33), null, Spotify.Audio.Quality.High160, 0, Market);

        Assert.False(choice.Ok);
        Assert.Equal(Spotify.Audio.Fault.NoFile, choice.Fault);
    }

    /// <summary>FLAC wins when the account returned it AND the setting asked for it — and the key still uses the
    /// TRACK's gid, because a FLAC is an alternative encoding rather than an alternative track.</summary>
    [Fact]
    public void Lossless_prefers_twenty_four_bit_and_keeps_the_tracks_own_gid()
    {
        Md.Track track = Track(0x44, File(Md.AudioFile.Types.Format.OggVorbis320, 0x20));
        var lossless = new Af.AudioFilesExtensionResponse();
        lossless.Files.Add(new Af.ExtendedAudioFile { File = File(Md.AudioFile.Types.Format.FlacFlac, 0xF1) });
        lossless.Files.Add(new Af.ExtendedAudioFile { File = File(Md.AudioFile.Types.Format.FlacFlac24Bit, 0xF2) });
        lossless.DefaultFileNormalizationParams = new Af.NormalizationParams { LoudnessDb = -8f, TruePeakDb = -3f };

        Spotify.Audio.FileChoice choice = Spotify.Audio.Choose(track, lossless, Spotify.Audio.Quality.Lossless, 0, Market);

        Assert.Equal(Spotify.Audio.Format.Flac24, choice.Fmt);
        Assert.Equal((byte)0xF2, choice.FileId[0]);
        Assert.Equal((byte)0x44, choice.TrackGid[0]);
        // The catalogue's true peak travels with the gain, LINEAR, so the adapter's gain cap can apply it.
        Assert.Equal(Spotify.Audio.PeakLinear(-3f), choice.Peak);
        Assert.True(choice.Peak > 0.7f && choice.Peak < 0.71f);
    }

    /// <summary>A lossless payload that arrived while the user is on 320 does not hijack the pick.</summary>
    [Fact]
    public void Lossless_is_ignored_unless_the_setting_asked_for_it()
    {
        Md.Track track = Track(0x55, File(Md.AudioFile.Types.Format.OggVorbis320, 0x20));
        var lossless = new Af.AudioFilesExtensionResponse();
        lossless.Files.Add(new Af.ExtendedAudioFile { File = File(Md.AudioFile.Types.Format.FlacFlac, 0xF1) });

        Spotify.Audio.FileChoice choice = Spotify.Audio.Choose(track, lossless, Spotify.Audio.Quality.VeryHigh320, 0, Market);

        Assert.Equal(Spotify.Audio.Format.OggVorbis320, choice.Fmt);
    }

    // ── the CDN route: a file id is only half the address ───────────────────────────────────────────────────────────
    //
    // Storage-resolve SIGNS a url, and it signs one per (format, file id) pair. The route without the format in it is
    // therefore a route that guesses, and it guesses Ogg: a lossless id resolved through v1 came back with a perfectly
    // well-formed mirror set whose host had no such object, so every body range 404'd at byte 0 while the head service
    // — which IS keyed by the id alone — served 80 KiB of the same id and made the track look like it was playing.
    // The three facts below are what that bug needed and did not have.

    /// <summary>The numbers are the WIRE's (`metadata.proto` AudioFile.Format) and they are what goes in the path. They
    /// are pinned as literals because a renumbering would not break a build — it would 404 every body.</summary>
    [Theory]
    [InlineData(Md.AudioFile.Types.Format.OggVorbis96, 0)]
    [InlineData(Md.AudioFile.Types.Format.OggVorbis160, 1)]
    [InlineData(Md.AudioFile.Types.Format.OggVorbis320, 2)]
    [InlineData(Md.AudioFile.Types.Format.FlacFlac, 16)]
    [InlineData(Md.AudioFile.Types.Format.FlacFlac24Bit, 22)]
    public void The_wire_format_numbers_are_what_the_cdn_route_carries(Md.AudioFile.Types.Format wire, int number)
        => Assert.Equal(number, (int)wire);

    /// <summary>…and the route is v2, with that number as a segment of its own and `?product=0` behind the id — the
    /// captured desktop form, and go-librespot's. `Spotify.Build` is pure, so the whole address is one assertion.</summary>
    [Theory]
    [InlineData(Md.AudioFile.Types.Format.OggVorbis320, "/storage-resolve/v2/files/audio/interactive/2/deadbeef?product=0")]
    [InlineData(Md.AudioFile.Types.Format.FlacFlac, "/storage-resolve/v2/files/audio/interactive/16/deadbeef?product=0")]
    [InlineData(Md.AudioFile.Types.Format.FlacFlac24Bit, "/storage-resolve/v2/files/audio/interactive/22/deadbeef?product=0")]
    public void Storage_resolve_names_the_format_in_its_own_path_segment(Md.AudioFile.Types.Format wire, string expected)
        => Assert.Equal(expected, StorageResolvePath("deadbeef", wire));

    static string StorageResolvePath(string fileIdHex, Md.AudioFile.Types.Format wire)
    {
        var session = default(Spotify.Session);
        var args = new Spotify.RequestArgs { Id = fileIdHex, Number = (long)wire };
        Span<char> path = stackalloc char[512];
        var request = Spotify.Build(session, Spotify.RequestKind.StorageResolve, args, path);
        return new string(request.Path);
    }

    /// <summary>The choice carries the CATALOGUE's format beside ours, because ours is lossy on purpose — `FormatOf`
    /// collapses the four MP3 rungs into one, since no decoder cares which — and a choice holding only that could not
    /// reconstruct the number the route needs. Lossless is where the difference stops being a rounding: a FLAC that
    /// resolves as an Ogg is a 404, not a lower bitrate.</summary>
    [Fact]
    public void A_lossless_choice_carries_the_flac_wire_format_and_not_a_collapsed_one()
    {
        Md.Track track = Track(0x44, File(Md.AudioFile.Types.Format.OggVorbis320, 0x20));
        var lossless = new Af.AudioFilesExtensionResponse();
        lossless.Files.Add(new Af.ExtendedAudioFile { File = File(Md.AudioFile.Types.Format.FlacFlac, 0xF1) });

        Spotify.Audio.FileChoice flac = Spotify.Audio.Choose(track, lossless, Spotify.Audio.Quality.Lossless, 0, Market);
        Assert.Equal(Md.AudioFile.Types.Format.FlacFlac, flac.Wire);
        // …and it reaches the CDN that way: `Ref` is the pair storage-resolve is asked with.
        Assert.Equal(Md.AudioFile.Types.Format.FlacFlac, flac.Ref.Wire);
        Assert.Equal(flac.FileIdHex, flac.Ref.Hex);

        // 24-bit is its own number, and the Ogg rung of the very same track is its own again: one file id, one format.
        lossless.Files.Add(new Af.ExtendedAudioFile { File = File(Md.AudioFile.Types.Format.FlacFlac24Bit, 0xF2) });
        Assert.Equal(Md.AudioFile.Types.Format.FlacFlac24Bit,
            Spotify.Audio.Choose(track, lossless, Spotify.Audio.Quality.Lossless, 0, Market).Wire);
        Assert.Equal(Md.AudioFile.Types.Format.OggVorbis320,
            Spotify.Audio.Choose(track, null, Spotify.Audio.Quality.VeryHigh320, 0, Market).Wire);
    }

    /// <summary>The collapse the wire format survives, on the rung where ours cannot tell: an MP3 320 file is
    /// `Format.Mp3` to the decoder and MP3_320 (4) to the CDN, so the route asks for the object that exists rather
    /// than for MP3_256's.</summary>
    [Fact]
    public void An_mp3_choice_keeps_the_rung_our_own_format_collapses()
    {
        Md.Track track = Track(0x66, File(Md.AudioFile.Types.Format.Mp3320, 0x32));

        Spotify.Audio.FileChoice choice = Spotify.Audio.Choose(track, null, Spotify.Audio.Quality.VeryHigh320, 0, Market);

        Assert.Equal(Spotify.Audio.Format.Mp3, choice.Fmt);
        Assert.Equal(Md.AudioFile.Types.Format.Mp3320, choice.Wire);
        Assert.Equal(4, (int)choice.Wire);
    }

    /// <summary>An episode with an external url skips the key and the CDN entirely: it is a plain MP3 on somebody
    /// else's host.</summary>
    [Fact]
    public void An_episode_with_an_external_url_is_a_plain_mp3()
    {
        var gid = new byte[16];
        gid[0] = 0x77;
        var episode = new Md.Episode
        {
            Gid = ByteString.CopyFrom(gid),
            Duration = 1_800_000,
            ExternalUrl = "https://example.invalid/episode.mp3",
        };

        Spotify.Audio.FileChoice choice = Spotify.Audio.Choose(episode, Spotify.Audio.Quality.High160, 0);

        Assert.True(choice.Ok);
        Assert.Equal(Spotify.Audio.Format.Mp3, choice.Fmt);
        Assert.Equal("https://example.invalid/episode.mp3", choice.ExternalUrl);
        Assert.Equal(1_800_000L, choice.DurationMs);
    }

    // ── gap batch B4: what a failure means, the lossless fallback, whose gain a body opens with ─────────────────────────
    //
    // …and the third way a lossless open fails, which none of the rules below could see when they were written: the
    // ladder, the mirrors and the key can ALL succeed and the body still never arrive, because the CDN refuses every
    // range. The clear head hides it — the track starts, plays half a second and goes silent — so the three facts about
    // a refused body (it falls back like a refused key, it is decided before the head runs out, and the pump does not
    // wait ninety seconds for it) are pinned here beside the rules they extend.

    /// <summary>G-038: a TRACK_V4 read that never reached a server, a 5xx or a 429 is the network being unlucky — retryable —
    /// and only an answer that says "nothing here" is the terminal Restricted.</summary>
    [Theory]
    [InlineData(0, 0, false, Spotify.Audio.Fault.Network)]          // transport: DNS, socket, timeout
    [InlineData(401, 0, false, Spotify.Audio.Fault.Network)]        // survived the token refresh
    [InlineData(429, 0, false, Spotify.Audio.Fault.Network)]
    [InlineData(503, 0, false, Spotify.Audio.Fault.Network)]
    [InlineData(404, 0, false, Spotify.Audio.Fault.Restricted)]
    [InlineData(200, 0, false, Spotify.Audio.Fault.Restricted)]     // answered, with no entry for the track
    [InlineData(200, 404, false, Spotify.Audio.Fault.Restricted)]
    [InlineData(200, 503, false, Spotify.Audio.Fault.Network)]      // the entry itself failed upstream
    [InlineData(200, 200, true, Spotify.Audio.Fault.None)]
    public void A_metadata_failure_is_a_network_fault_unless_the_answer_says_nothing_is_there(int http, int entity,
        bool hasPayload, Spotify.Audio.Fault expected)
        => Assert.Equal(expected, Spotify.Audio.MetadataFault(http, entity, hasPayload));

    /// <summary>D7, G-101: a refused lossless key plays the Ogg 320 rung; nothing else falls back.</summary>
    [Theory]
    [InlineData(Spotify.Audio.Format.Flac, Spotify.Audio.Fault.NoDeriver, true)]
    [InlineData(Spotify.Audio.Format.Flac24, Spotify.Audio.Fault.NoKey, true)]
    [InlineData(Spotify.Audio.Format.Flac24, Spotify.Audio.Fault.Network, false)]   // a network fault is retried, not downgraded
    [InlineData(Spotify.Audio.Format.OggVorbis320, Spotify.Audio.Fault.NoDeriver, false)]
    [InlineData(Spotify.Audio.Format.Flac, Spotify.Audio.Fault.None, false)]
    public void Only_a_lossless_file_whose_key_is_refused_falls_back(Spotify.Audio.Format format, Spotify.Audio.Fault fault,
        bool fallsBack)
        => Assert.Equal(fallsBack, Spotify.Audio.LosslessFallback.Decide(format, fault));

    /// <summary>…and so does a lossless file whose BODY was refused. The key is only one of the two ways a lossless
    /// open can hold nothing: a FLAC opened cleanly off its clear head, every CDN mirror then refused every range, and
    /// the track played half a second and went silent for 57 s. The 320 rung is a different file id on a different
    /// mirror set, so the demotion is the one move left; a non-lossless rung has nothing below it to fall to, and a
    /// network fault is still a retry rather than a downgrade.</summary>
    [Theory]
    [InlineData(Spotify.Audio.Format.Flac, true)]
    [InlineData(Spotify.Audio.Format.Flac24, true)]
    [InlineData(Spotify.Audio.Format.OggVorbis320, false)]
    [InlineData(Spotify.Audio.Format.OggVorbis160, false)]
    [InlineData(Spotify.Audio.Format.Mp3, false)]
    public void A_refused_body_falls_back_exactly_where_a_refused_key_does(Spotify.Audio.Format format, bool fallsBack)
        => Assert.Equal(fallsBack, Spotify.Audio.LosslessFallback.Decide(format, Spotify.Audio.Fault.Refused));

    /// <summary>THE FIRST-BODY DEADLINE. The clear head serves the opening ~0.6 s of a FLAC whether or not the body
    /// ever starts, so "it is playing" is not evidence that it will keep playing. A lossless open that has landed zero
    /// BODY bytes is demoted the moment its mirrors refuse, and otherwise once the bound passes; one landed byte ends
    /// the question for good, and a non-lossless rung is never demoted by this rule at all.</summary>
    [Theory]
    [InlineData(Spotify.Audio.Format.Flac, 0L, true, 40L, true)]              // refused at once: the ~1 s downgrade
    [InlineData(Spotify.Audio.Format.Flac24, 0L, false, 3_000L, true)]        // never refused, never arrived: the bound
    [InlineData(Spotify.Audio.Format.Flac, 0L, false, 2_999L, false)]         // still inside the bound: keep waiting
    [InlineData(Spotify.Audio.Format.Flac, 65_536L, true, 9_000L, false)]     // bytes landed: a starve, not a dead rung
    [InlineData(Spotify.Audio.Format.Flac24, 1L, false, 90_000L, false)]      // one byte is enough to disprove it
    [InlineData(Spotify.Audio.Format.OggVorbis320, 0L, true, 9_000L, false)]  // nothing below 320 to demote to
    public void A_lossless_open_that_never_got_a_body_byte_is_demoted(Spotify.Audio.Format format, long bodyBytes,
        bool refusing, long waitedMs, bool demotes)
        => Assert.Equal(demotes, Spotify.Audio.LosslessFallback.DemoteForNoBody(format, bodyBytes, refusing, waitedMs));

    /// <summary>A refused demotion does not SUPPRESS a later lossless attempt the way a missing key does, and that is
    /// the difference between a diagnosis and a dead end. A key that cannot be had is deterministic — this build has no
    /// deriver, or this account has no entitlement — so the 320 answer is sticky and no later play of the track pays
    /// for the same verdict twice. A refused body is a server saying no this minute on an account that DOES have
    /// lossless: keeping that for half an hour would quietly cost the user lossless on a track that is fine, and would
    /// make the bug unreproducible — the retry meant to gather evidence would be served the cached rung and never reach
    /// a mirror, so not one `audio.mirror` or `audio.resolve` line would be printed.</summary>
    [Theory]
    [InlineData(Spotify.Audio.Fault.NoDeriver, true)]
    [InlineData(Spotify.Audio.Fault.NoKey, true)]
    [InlineData(Spotify.Audio.Fault.Refused, false)]
    public void Only_a_deterministic_lossless_failure_is_remembered(Spotify.Audio.Fault fault, bool sticky)
        => Assert.Equal(sticky ? Spotify.Audio.ChoiceTtlMs : Spotify.Audio.RefusedChoiceTtlMs,
                        Spotify.Audio.LosslessFallback.RememberFor(fault));

    /// <summary>…and "not remembered" has to hold on the clock, not only in the mapping: a refused demotion is
    /// SUPPRESSION — seconds, enough that a retry loop cannot hammer a dead url set — and never memory.</summary>
    [Fact]
    public void A_refused_demotion_is_suppression_and_not_memory()
    {
        long refused = Spotify.Audio.LosslessFallback.RememberFor(Spotify.Audio.Fault.Refused);
        Assert.InRange(refused, 1_000L, 10_000L);
        Assert.True(refused * 100 < Spotify.Audio.ChoiceTtlMs,
            $"a refused demotion held for {refused} ms is memory, not suppression");
    }

    /// <summary>D5's patience, and the one fact it was missing. Ninety seconds is right for a link that is SLOW —
    /// bytes are coming, just not yet — and wrong for a mirror set that answered a non-2xx to every range, because an
    /// answer does not change by being waited on. A refused body still shows "Reconnecting" first (a re-resolve really
    /// can fix expired urls) and then fails in seconds; a slow one keeps the full budget.</summary>
    [Theory]
    [InlineData(0L, false, Playback.Audio.StarvePolicy.Verdict.Flowing)]
    [InlineData(0L, true, Playback.Audio.StarvePolicy.Verdict.Flowing)]      // refused, but nothing has waited yet
    [InlineData(1_500L, true, Playback.Audio.StarvePolicy.Verdict.Recovering)]
    [InlineData(5_999L, true, Playback.Audio.StarvePolicy.Verdict.Recovering)]
    [InlineData(6_000L, true, Playback.Audio.StarvePolicy.Verdict.Failed)]   // one re-resolve cycle, then it is over
    [InlineData(6_000L, false, Playback.Audio.StarvePolicy.Verdict.Recovering)]
    [InlineData(89_999L, false, Playback.Audio.StarvePolicy.Verdict.Recovering)]
    [InlineData(90_000L, false, Playback.Audio.StarvePolicy.Verdict.Failed)]
    public void A_refused_body_fails_fast_where_a_slow_one_keeps_the_full_budget(long stallMs, bool refused,
        Playback.Audio.StarvePolicy.Verdict expected)
        => Assert.Equal(expected, Playback.Audio.StarvePolicy.Decide(stallMs, refused));

    /// <summary>D7: a build that cannot derive a lossless key never asks for the lossless rung.</summary>
    [Fact]
    public void The_lossless_rung_is_asked_for_only_by_a_build_that_can_derive_its_key()
    {
        Assert.Equal(Spotify.Audio.Quality.VeryHigh320,
            Spotify.Audio.LosslessFallback.Effective(Spotify.Audio.Quality.Lossless, canDerive: false));
        Assert.Equal(Spotify.Audio.Quality.Lossless,
            Spotify.Audio.LosslessFallback.Effective(Spotify.Audio.Quality.Lossless, canDerive: true));
        Assert.Equal(Spotify.Audio.Quality.High160,
            Spotify.Audio.LosslessFallback.Effective(Spotify.Audio.Quality.High160, canDerive: false));
    }

    /// <summary>G-105 / G-106: the catalogue's figure first; else ONLY an Ogg body's header (gain at 144, linear peak at
    /// 148). A FLAC's byte 144 is STREAMINFO/SEEKTABLE data — read as a float it was up to +30 dB.</summary>
    [Fact]
    public void A_body_opens_with_the_catalogue_gain_else_the_ogg_header_and_never_a_flac_s_bytes()
    {
        var header = new byte[160];
        BitConverter.TryWriteBytes(header.AsSpan(144), -3.5f);
        BitConverter.TryWriteBytes(header.AsSpan(148), 0.9f);

        Assert.Equal((-3.5f, 0.9f), Spotify.Audio.GainFor(Spotify.Audio.Format.OggVorbis320, 0f, 0f, header));
        Assert.Equal((0f, 0f), Spotify.Audio.GainFor(Spotify.Audio.Format.Flac, 0f, 0f, header));
        Assert.Equal((0f, 0f), Spotify.Audio.GainFor(Spotify.Audio.Format.Mp3, 0f, 0f, header));
        Assert.Equal((-6f, 0.7f), Spotify.Audio.GainFor(Spotify.Audio.Format.Flac24, -6f, 0.7f, header));
        Assert.Equal((0f, 0f), Spotify.Audio.GainFor(Spotify.Audio.Format.OggVorbis160, 0f, 0f, ReadOnlySpan<byte>.Empty));

        BitConverter.TryWriteBytes(header.AsSpan(144), 99f);                // garbage bytes, not a gain
        Assert.Equal(0f, Spotify.Audio.GainFor(Spotify.Audio.Format.OggVorbis96, 0f, 0f, header).GainDb);
    }

    /// <summary>G-109: a podcast enclosure is a plain external body — no file id, no key, no resolve.</summary>
    [Fact]
    public void A_podcast_enclosure_is_an_external_mp3_choice()
    {
        Spotify.Audio.FileChoice choice = Spotify.Audio.ExternalChoice("https://podcast.example/ep1.mp3", 1_800_000);

        Assert.True(choice.Ok);
        Assert.Equal("https://podcast.example/ep1.mp3", choice.ExternalUrl);
        Assert.Equal(Spotify.Audio.Format.Mp3, choice.Fmt);
        Assert.Equal(1_800_000L, choice.DurationMs);
        Assert.Empty(choice.FileId);
        Assert.StartsWith("ext", choice.FileIdHex);
        Assert.Equal(choice.FileIdHex, Spotify.Audio.ExternalChoice("https://podcast.example/ep1.mp3", 0).FileIdHex);
    }

    /// <summary>The gain is capped by the true-peak headroom: a quiet track is not lifted past clipping.</summary>
    [Fact]
    public void The_normalization_gain_is_capped_by_the_true_peak_headroom()
    {
        // −8 LUFS is louder than the −14 target, so the gain is negative and the cap does not bite.
        Assert.Equal(-6f, Spotify.Audio.NormalizationGain(-8f, -3f), 3);
        // −24 LUFS would want +10 dB, but a true peak of −1 leaves 0 dB of headroom.
        Assert.Equal(0f, Spotify.Audio.NormalizationGain(-24f, -1f), 3);
    }
}

public class SpotifyAudioCtrTests
{
    static byte[] Key() => [0x01, 0x23, 0x45, 0x67, 0x89, 0xab, 0xcd, 0xef,
                            0xfe, 0xdc, 0xba, 0x98, 0x76, 0x54, 0x32, 0x10];

    static byte[] Plain(int length)
    {
        var bytes = new byte[length];
        for (int i = 0; i < length; i++) bytes[i] = (byte)(i * 7 + 3);
        return bytes;
    }

    [Fact]
    public void The_public_iv_is_the_captured_sixteen_bytes()
    {
        ReadOnlySpan<byte> expected =
        [
            0x72, 0xe0, 0x67, 0xfb, 0xdd, 0xcb, 0xcf, 0x77,
            0xeb, 0xe8, 0xbc, 0x64, 0x3f, 0x63, 0x0d, 0x93,
        ];
        Assert.True(Spotify.Audio.Ctr.PublicIv.SequenceEqual(expected));
        Assert.Equal(0xa7, Spotify.Audio.Ctr.HeaderBytes);
    }

    /// <summary>CTR is its own inverse: the same transform at the same offset takes the ciphertext back.</summary>
    [Fact]
    public void The_transform_is_its_own_inverse()
    {
        byte[] key = Key();
        byte[] plain = Plain(300);
        byte[] cipher = Spotify.Audio.Ctr.Decrypt(plain, key, 0);

        Assert.NotEqual(plain, cipher);
        Assert.Equal(plain, Spotify.Audio.Ctr.Decrypt(cipher, key, 0));
    }

    /// <summary>THE seek property, and the reason a ranged read works at all: decrypting a RANGE at its true file
    /// offset gives exactly the bytes the whole-file decrypt has at that offset. Get this wrong and a scrub produces
    /// noise ten seconds after the bug was written.</summary>
    [Theory]
    [InlineData(16, 32)]
    [InlineData(64, 16)]
    [InlineData(128, 64)]
    public void A_range_decrypted_at_its_offset_matches_the_whole_file(int offset, int length)
    {
        byte[] key = Key();
        byte[] cipher = Spotify.Audio.Ctr.Decrypt(Plain(512), key, 0);

        byte[] whole = Spotify.Audio.Ctr.Decrypt(cipher, key, 0);
        byte[] piece = Spotify.Audio.Ctr.Decrypt(cipher.AsSpan(offset, length), key, offset);

        Assert.Equal(whole.AsSpan(offset, length).ToArray(), piece);
    }

    /// <summary>A range that neither starts nor ends on a block boundary is the case a naive implementation gets
    /// wrong — it is also the case every 128 KB chunk boundary lands on once the header shift is applied.</summary>
    [Fact]
    public void A_range_that_straddles_blocks_matches_too()
    {
        byte[] key = Key();
        byte[] cipher = Spotify.Audio.Ctr.Decrypt(Plain(512), key, 0);

        byte[] whole = Spotify.Audio.Ctr.Decrypt(cipher, key, 0);
        byte[] piece = Spotify.Audio.Ctr.Decrypt(cipher.AsSpan(7, 30), key, 7);

        Assert.Equal(whole.AsSpan(7, 30).ToArray(), piece);
    }

    /// <summary>The key-validation gate: a Spotify file decrypts to `OggS` at 0xa7, and that is the cheap runtime
    /// proof that the key is the right one before a decoder spends a second failing on noise.</summary>
    [Fact]
    public void A_right_key_is_recognised_by_the_ogg_magic_at_the_header_offset()
    {
        byte[] key = Key();
        byte[] file = Plain(0x100);
        "OggS"u8.CopyTo(file.AsSpan(Spotify.Audio.Ctr.HeaderBytes));
        byte[] encrypted = Spotify.Audio.Ctr.Decrypt(file, key, 0);

        Assert.True(Spotify.Audio.Ctr.Validates(encrypted, key));
    }

    /// <summary>`Ctr_validates_a_flac_head` (FLAC plan §7.3). A Spotify FLAC has no header, so its magic is `fLaC`
    /// at offset 0 — and the clear head file is the proof vector for it, being byte-identical to a correctly
    /// decrypted first chunk.</summary>
    [Fact]
    public void Ctr_validates_a_flac_head()
    {
        byte[] key = Key();
        byte[] file = Plain(0x100);
        "fLaC"u8.CopyTo(file.AsSpan(0));
        byte[] encrypted = Spotify.Audio.Ctr.Decrypt(file, key, 0);

        Assert.True(Spotify.Audio.Ctr.Validates(encrypted, key));

        byte[] wrong = Key();
        wrong[0] ^= 0xFF;
        Assert.False(Spotify.Audio.Ctr.Validates(encrypted, wrong));
    }

    /// <summary>A prefix long enough for `fLaC` but too short for the Ogg offset is answered, not read past — the
    /// gate got a cheaper minimum length when FLAC joined it.</summary>
    [Fact]
    public void A_four_byte_prefix_is_enough_to_answer_for_flac_and_not_enough_to_lie_about_ogg()
    {
        byte[] key = Key();
        byte[] file = new byte[8];
        "fLaC"u8.CopyTo(file.AsSpan(0));

        Assert.True(Spotify.Audio.Ctr.Validates(Spotify.Audio.Ctr.Decrypt(file, key, 0), key));
        Assert.False(Spotify.Audio.Ctr.Validates(Spotify.Audio.Ctr.Decrypt(new byte[8], key, 0), key));
    }

    [Fact]
    public void A_wrong_key_is_refused_rather_than_handed_to_the_decoder()
    {
        byte[] key = Key();
        byte[] file = Plain(0x100);
        "OggS"u8.CopyTo(file.AsSpan(Spotify.Audio.Ctr.HeaderBytes));
        byte[] encrypted = Spotify.Audio.Ctr.Decrypt(file, key, 0);

        byte[] wrong = Key();
        wrong[0] ^= 0xFF;

        Assert.False(Spotify.Audio.Ctr.Validates(encrypted, wrong));
    }

    /// <summary>Shorter than the shortest magic (`fLaC`, four bytes) — answered without decrypting anything.</summary>
    [Fact]
    public void A_prefix_too_short_to_hold_the_magic_is_refused_rather_than_read_past()
        => Assert.False(Spotify.Audio.Ctr.Validates(new byte[3], Key()));

    [Fact]
    public void A_key_that_is_not_sixteen_bytes_is_rejected_loudly()
        => Assert.Throws<ArgumentException>(() => Spotify.Audio.Ctr.Decrypt(new byte[16], new byte[8], 0));

    /// <summary>The head file's normalization gain is a float at byte 144; a head too short to hold it is 0, not a
    /// read past the end.</summary>
    [Fact]
    public void The_head_gain_is_read_at_byte_one_hundred_and_forty_four()
    {
        var head = new byte[160];
        BitConverter.TryWriteBytes(head.AsSpan(144), -3.5f);

        Assert.Equal(-3.5f, Spotify.Audio.HeadGainDb(head), 3);
        Assert.Equal(0f, Spotify.Audio.HeadGainDb(new byte[100]), 3);
    }
}

public class SpotifyAudioSeamTests
{
    /// <summary>With no deriver installed — a public-only checkout — a refused key is a NAMED fault the UI can render,
    /// never a crash and never a silent stall. This is the whole of the PlayPlay contract on this side of the seam.</summary>
    [Fact]
    public void With_no_deriver_installed_the_build_says_so_rather_than_pretending()
    {
        Func<Spotify.Audio.KeyRequest, CancellationToken, byte[]?>? saved = Spotify.Audio.KeyDeriver;
        try
        {
            Spotify.Audio.KeyDeriver = null;
            Assert.False(Spotify.Audio.CanDerive);
        }
        finally { Spotify.Audio.KeyDeriver = saved; }
    }

    /// <summary>`Key_skips_the_ap_for_flac` (FLAC plan §7.3). A lossless key request goes STRAIGHT to the seam: the
    /// AP is never asked, and — the fact that matters — the account-wide latch is still clear afterwards, so the next
    /// Ogg track still has its fast key path. Nothing here can reach a socket: the ineligible arm does not call
    /// `RequestAudioKey` at all.</summary>
    [Fact]
    public void Key_skips_the_ap_for_flac()
    {
        Func<Spotify.Audio.KeyRequest, CancellationToken, byte[]?>? saved = Spotify.Audio.KeyDeriver;
        try
        {
            Spotify.Audio.ResetKeyLatch();
            Spotify.Audio.KeyDeriver = null;

            Span<byte> key = stackalloc byte[16];
            Spotify.Audio.Fault fault = Spotify.Audio.Key(
                "f1acf1acf1acf1ac", new byte[20], new byte[16], key, apEligible: false, default);

            // A public-only build says so honestly, and says nothing about the account's AP keys.
            Assert.Equal(Spotify.Audio.Fault.NoDeriver, fault);
            Assert.False(Spotify.Audio.ApKeysDisabled);

            // With a deriver installed the same call is answered by the seam, and the latch is STILL clear.
            var derived = new byte[16];
            derived[0] = 0x5A;
            Spotify.Audio.KeyDeriver = (_, _) => derived;

            fault = Spotify.Audio.Key("f1acf1acf1acf1ad", new byte[20], new byte[16], key, apEligible: false, default);

            Assert.Equal(Spotify.Audio.Fault.None, fault);
            Assert.Equal((byte)0x5A, key[0]);
            Assert.False(Spotify.Audio.ApKeysDisabled);
        }
        finally
        {
            Spotify.Audio.KeyDeriver = saved;
            Spotify.Audio.ResetKeyLatch();
        }
    }

    /// <summary>A deriver that IS installed is visible as such, and is handed the file id, its bytes and the track
    /// gid — and nothing else, so the private assembly binds to this one value and to no other type in the tree.</summary>
    [Fact]
    public void An_installed_deriver_is_handed_the_file_and_the_track_and_nothing_else()
    {
        Func<Spotify.Audio.KeyRequest, CancellationToken, byte[]?>? saved = Spotify.Audio.KeyDeriver;
        try
        {
            Spotify.Audio.KeyRequest seen = default;
            Spotify.Audio.KeyDeriver = (request, _) => { seen = request; return null; };
            Assert.True(Spotify.Audio.CanDerive);

            Spotify.Audio.KeyDeriver!(new Spotify.Audio.KeyRequest("abcd", new byte[20], new byte[16]), default);

            Assert.Equal("abcd", seen.FileIdHex);
            Assert.Equal(20, seen.FileId.Length);
            Assert.Equal(16, seen.TrackGid.Length);
        }
        finally { Spotify.Audio.KeyDeriver = saved; }
    }
}
