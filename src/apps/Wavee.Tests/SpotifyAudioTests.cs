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

        Spotify.Audio.FileChoice high = Spotify.Audio.Choose(track, null, Spotify.Audio.Quality.VeryHigh320, 0);
        Spotify.Audio.FileChoice low = Spotify.Audio.Choose(track, null, Spotify.Audio.Quality.Normal96, 0);

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

        Spotify.Audio.FileChoice choice = Spotify.Audio.Choose(main, null, Spotify.Audio.Quality.High160, 0);

        Assert.True(choice.Ok);
        Assert.Equal((byte)0x60, choice.FileId[0]);
        Assert.Equal((byte)0x22, choice.TrackGid[0]);
    }

    [Fact]
    public void A_track_with_nothing_playable_anywhere_is_a_named_fault()
    {
        Spotify.Audio.FileChoice choice = Spotify.Audio.Choose(Track(0x33), null, Spotify.Audio.Quality.High160, 0);

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

        Spotify.Audio.FileChoice choice = Spotify.Audio.Choose(track, lossless, Spotify.Audio.Quality.Lossless, 0);

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

        Spotify.Audio.FileChoice choice = Spotify.Audio.Choose(track, lossless, Spotify.Audio.Quality.VeryHigh320, 0);

        Assert.Equal(Spotify.Audio.Format.OggVorbis320, choice.Fmt);
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
