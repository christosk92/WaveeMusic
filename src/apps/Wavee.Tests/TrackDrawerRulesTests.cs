// ── Wavee.Tests/TrackDrawerRulesTests.cs — the expanded-row drawer's pure decisions ────────────────────────────────
//
// Wave 5's gate for `Track.DrawerRules` (Entities/Track.Drawer.cs, stream B of WP-5.M). Everything the drawer decides
// that is not layout lives there so it can be pinned here without an engine:
//
//   THE RESERVED VIDEO ROW (ch 01 §9 trap 8): a catalogue video reserves the row only while the versions relation is
//   UNANSWERED; an answer (or a failure) that names no video reserves nothing — and the relation's own video, then the
//   kind-99 counterpart, always win.
//   THE VERSION ORDER (TrackVersionsPanel.cs:114-119): the first video, then every alternate audio in wire order.
//   THE FORMAT LADDER (ch 01 DATA GAP 14): one label per wire format, the radio row's three-space join, best-first
//   ordering with ties in wire order, decodability that agrees with the audio ladder's own `FormatOf`, and the rung an
//   override asks the playback ladder for.
//   THE OVERRIDE MAP's persisted codec: tolerant parse, canonical serialize, newest-last set/clear, the cap.
//   THE CREDITS SHEET (TrackCreditsDialog.cs:74): heading-else-role grouping in first-appearance order and the sheet's
//   three states.
//
// Pure: no scope, no tables, no loop — so no EntitiesCollection.

using Xunit;
using Md = Wavee.Protocol.Metadata;
using Rules = Wavee.Track.DrawerRules;
using Overrides = Wavee.Track.DrawerRules.FormatOverrides;
using FactKind = Wavee.Track.FactKind;

namespace Wavee.Tests;

public class TrackDrawerRulesTests
{
    // ── the reserved music-video row ──

    [Theory]
    [InlineData(true, EdgeState.Unknown, false, false, Rules.VideoRow.Reserved)]
    [InlineData(true, EdgeState.Partial, false, false, Rules.VideoRow.None)]
    [InlineData(true, EdgeState.Complete, false, false, Rules.VideoRow.None)]
    [InlineData(true, EdgeState.Failed, false, false, Rules.VideoRow.None)]
    [InlineData(false, EdgeState.Unknown, false, false, Rules.VideoRow.None)]
    [InlineData(true, EdgeState.Complete, true, false, Rules.VideoRow.Edge)]
    [InlineData(false, EdgeState.Complete, true, true, Rules.VideoRow.Edge)]
    [InlineData(true, EdgeState.Unknown, false, true, Rules.VideoRow.Counterpart)]
    [InlineData(false, EdgeState.Complete, false, true, Rules.VideoRow.Counterpart)]
    public void The_video_row_is_reserved_only_while_the_relation_is_unanswered(
        bool hasVideo, EdgeState versions, bool edgeHasVideo, bool counterpart, Rules.VideoRow expected)
        => Assert.Equal(expected, Rules.VideoRowFor(hasVideo, versions, edgeHasVideo, counterpart));

    [Fact]
    public void The_reserved_row_carries_the_real_rows_geometry_and_key()
    {
        Assert.Equal(51f, Rules.RowH);                                 // 43 thumb + 4 above + 4 below (ch 04 item 68)
        Assert.Equal(7f, Rules.RailX);                                 // RowMetrics.RailOffset: where the table's indent subtracts
        Assert.Equal("v:video", Rules.VideoRowKey);
        Assert.Equal(76f, Rules.VideoThumbW);
        Assert.Equal(43f, Rules.VideoThumbH);
    }

    // ── version order ──

    [Fact]
    public void Versions_list_the_first_video_then_every_alternate_audio_in_wire_order()
    {
        VersionEdge[] kinds =
        [
            new(TrackVersionKind.Audio), new(TrackVersionKind.Video), new(TrackVersionKind.Audio), new(TrackVersionKind.Video),
        ];
        Span<int> into = stackalloc int[8];
        int n = Rules.VersionOrder(kinds, into);
        Assert.Equal(new[] { 1, 0, 2 }, into[..n].ToArray());
    }

    [Fact]
    public void Version_order_never_writes_past_its_buffer_and_an_empty_relation_lists_nothing()
    {
        VersionEdge[] kinds = [new(TrackVersionKind.Audio), new(TrackVersionKind.Audio), new(TrackVersionKind.Audio)];
        Span<int> two = stackalloc int[2];
        Assert.Equal(2, Rules.VersionOrder(kinds, two));
        Assert.Equal(0, Rules.VersionOrder(ReadOnlySpan<VersionEdge>.Empty, two));
    }

    // ── the format ladder ──

    [Theory]
    [InlineData(0, "OGG 96")]
    [InlineData(1, "OGG 160")]
    [InlineData(2, "OGG 320")]
    [InlineData(3, "MP3 256")]
    [InlineData(4, "MP3 320")]
    [InlineData(5, "MP3 160")]
    [InlineData(6, "MP3 96")]
    [InlineData(7, "MP3 160")]
    [InlineData(8, "AAC 24")]
    [InlineData(9, "AAC 48")]
    [InlineData(16, "FLAC")]
    [InlineData(18, "xHE-AAC 24")]
    [InlineData(19, "xHE-AAC 16")]
    [InlineData(20, "xHE-AAC 12")]
    [InlineData(22, "FLAC 24-bit")]
    [InlineData(42, "Format 42")]
    public void Every_wire_format_has_a_label_and_an_unknown_one_still_renders(int formatId, string label)
        => Assert.Equal(label, Rules.FormatLabel((byte)formatId));

    [Theory]
    [InlineData(Md.AudioFile.Types.Format.OggVorbis96)]
    [InlineData(Md.AudioFile.Types.Format.OggVorbis160)]
    [InlineData(Md.AudioFile.Types.Format.OggVorbis320)]
    [InlineData(Md.AudioFile.Types.Format.FlacFlac)]
    [InlineData(Md.AudioFile.Types.Format.FlacFlac24Bit)]
    public void The_ladder_label_agrees_with_the_playback_badge(Md.AudioFile.Types.Format wire)
        => Assert.Equal(Playback.Audio.LabelFor(Spotify.Audio.FormatOf(wire), 0, 0), Rules.FormatLabel((byte)wire));

    /// <summary>A rung this build cannot open renders DISABLED, never hidden — and "can open" is exactly the audio ladder's
    /// own mapping, so the menu can never offer a format the open path would refuse.</summary>
    [Fact]
    public void Decodable_is_exactly_what_the_audio_ladder_can_open()
    {
        for (int id = 0; id < 32; id++)
            Assert.Equal(Spotify.Audio.FormatOf((Md.AudioFile.Types.Format)id) != Spotify.Audio.Format.Unknown,
                         Rules.Decodable((byte)id));
    }

    [Fact]
    public void A_radio_row_joins_label_and_bitrate_with_three_spaces_and_omits_an_unstated_bitrate()
    {
        Assert.Equal("OGG 320   320 kbps", Rules.RadioLabel(2, 320));
        Assert.Equal("FLAC", Rules.RadioLabel(16, 0));
        Assert.Equal("96 kbps", Rules.KbpsLabel(96));
        Assert.Equal("", Rules.KbpsLabel(0));
    }

    [Fact]
    public void The_ladder_reads_best_first_with_unstated_bitrates_last_and_ties_in_wire_order()
    {
        FormatEdge[] ladder = [new(0, 96), new(16, 0), new(2, 320), new(9, 48), new(1, 160), new(4, 320)];
        Span<int> into = stackalloc int[8];
        int n = Rules.LadderOrder(ladder, into);
        Assert.Equal(new[] { 2, 5, 4, 0, 3, 1 }, into[..n].ToArray());
    }

    [Theory]
    [InlineData(0, Spotify.Audio.Quality.Normal96)]
    [InlineData(1, Spotify.Audio.Quality.High160)]
    [InlineData(2, Spotify.Audio.Quality.VeryHigh320)]
    [InlineData(3, Spotify.Audio.Quality.VeryHigh320)]
    [InlineData(4, Spotify.Audio.Quality.VeryHigh320)]
    [InlineData(5, Spotify.Audio.Quality.High160)]
    [InlineData(6, Spotify.Audio.Quality.Normal96)]
    [InlineData(16, Spotify.Audio.Quality.Lossless)]
    [InlineData(22, Spotify.Audio.Quality.Lossless)]
    public void An_override_asks_the_playback_ladder_for_its_bandwidth_rung(int formatId, Spotify.Audio.Quality rung)
        => Assert.Equal(rung, Rules.QualityOf((byte)formatId));

    [Theory]
    [InlineData(Md.AudioFile.Types.Format.OggVorbis96)]
    [InlineData(Md.AudioFile.Types.Format.OggVorbis160)]
    [InlineData(Md.AudioFile.Types.Format.OggVorbis320)]
    [InlineData(Md.AudioFile.Types.Format.FlacFlac)]
    [InlineData(Md.AudioFile.Types.Format.FlacFlac24Bit)]
    public void The_override_rung_agrees_with_the_ladders_own_rung_for_every_single_rung_format(Md.AudioFile.Types.Format wire)
        => Assert.Equal(Spotify.Audio.Rung(Spotify.Audio.FormatOf(wire)), (int)Rules.QualityOf((byte)wire));

    // ── the persisted override map ──

    [Fact]
    public void An_empty_setting_parses_to_an_empty_map()
    {
        Assert.Empty(Overrides.Parse(null));
        Assert.Empty(Overrides.Parse(""));
    }

    [Fact]
    public void The_map_round_trips_uris_that_contain_colons()
    {
        const string text = "spotify:track:aaa=2;spotify:track:bbb=16";
        var map = Overrides.Parse(text);
        Assert.Equal(2, map.Count);
        Assert.Equal((byte)2, Overrides.Find(map, "spotify:track:aaa"));
        Assert.Equal((byte)16, Overrides.Find(map, "spotify:track:bbb"));
        Assert.Null(Overrides.Find(map, "spotify:track:ccc"));
        Assert.Equal(text, Overrides.Serialize(map));
    }

    [Fact]
    public void A_malformed_entry_is_skipped_and_a_repeated_uri_keeps_its_last_value_as_the_newest()
    {
        var map = Overrides.Parse("junk;=3;spotify:track:a=;spotify:track:b=999;spotify:track:c=7;;spotify:track:d=1;spotify:track:c=9");
        Assert.Equal("spotify:track:d=1;spotify:track:c=9", Overrides.Serialize(map));
    }

    [Fact]
    public void Setting_appends_as_newest_resetting_the_same_value_changes_nothing_and_clearing_removes()
    {
        var map = new List<KeyValuePair<string, byte>>();
        Assert.True(Overrides.Put(map, "spotify:track:a", 2));
        Assert.False(Overrides.Put(map, "spotify:track:a", 2));
        Assert.True(Overrides.Put(map, "spotify:track:b", 1));
        Assert.True(Overrides.Put(map, "spotify:track:a", 2));          // re-chosen: moves to the newest end
        Assert.Equal("spotify:track:b=1;spotify:track:a=2", Overrides.Serialize(map));
        Assert.True(Overrides.Put(map, "spotify:track:a", null));
        Assert.False(Overrides.Put(map, "spotify:track:zzz", null));
        Assert.Equal("spotify:track:b=1", Overrides.Serialize(map));
    }

    [Theory]
    [InlineData("")]
    [InlineData("spotify:track:a;b")]
    [InlineData("spotify:track:a=b")]
    public void A_uri_the_codec_cannot_carry_is_refused(string uri)
    {
        var map = new List<KeyValuePair<string, byte>>();
        Assert.False(Overrides.Put(map, uri, 2));
        Assert.Empty(map);
    }

    [Fact]
    public void Past_the_cap_the_oldest_choice_drops_first()
    {
        var map = new List<KeyValuePair<string, byte>>();
        Overrides.Put(map, "a", 1, cap: 3);
        Overrides.Put(map, "b", 2, cap: 3);
        Overrides.Put(map, "c", 3, cap: 3);
        Overrides.Put(map, "d", 4, cap: 3);
        Assert.Equal("b=2;c=3;d=4", Overrides.Serialize(map));
        Assert.Equal(256, Rules.MaxOverrides);
    }

    // ── the credits sheet ──

    [Theory]
    [InlineData("Performers", "Vocals", "Performers")]
    [InlineData(null, "Producer", "Producer")]
    [InlineData("  ", "Mixing Engineer", "Mixing Engineer")]
    [InlineData(null, null, "")]
    public void A_credit_files_under_its_heading_else_its_role(string? group, string? role, string key)
        => Assert.Equal(key, Rules.CreditGroupKey(group, role));

    [Fact]
    public void Credits_group_in_first_appearance_order_with_members_in_wire_order()
    {
        string[] keys = ["Performers", "Writers", "Performers", "", "Writers", "Producers"];
        Span<int> order = stackalloc int[keys.Length];
        int n = Rules.CreditOrder(keys, order);
        Assert.Equal(new[] { 0, 2, 1, 4, 3, 5 }, order[..n].ToArray());
        bool[] starts = new bool[n];
        for (int k = 0; k < n; k++) starts[k] = Rules.StartsGroup(keys, order, k);
        Assert.Equal(new[] { true, false, true, false, true, true }, starts);
    }

    [Theory]
    [InlineData(EdgeState.Unknown, 0, Rules.CreditsView.Loading)]
    [InlineData(EdgeState.Unknown, 3, Rules.CreditsView.Loading)]
    [InlineData(EdgeState.Partial, 2, Rules.CreditsView.List)]
    [InlineData(EdgeState.Complete, 4, Rules.CreditsView.List)]
    [InlineData(EdgeState.Complete, 0, Rules.CreditsView.Empty)]
    [InlineData(EdgeState.Failed, 0, Rules.CreditsView.Empty)]
    public void The_sheet_shows_bars_until_answered_then_rows_or_the_empty_line(EdgeState readiness, int count, Rules.CreditsView view)
        => Assert.Equal(view, Rules.CreditsViewFor(readiness, count));

    // ── the facts strip and the waveform ──

    [Fact]
    public void Only_the_two_dates_and_the_person_take_a_lead_in_label()
    {
        foreach (var kind in Enum.GetValues<FactKind>())
            Assert.Equal(kind is FactKind.Added or FactKind.Released or FactKind.AddedBy, Rules.NeedsLabel(kind));
    }

    [Fact]
    public void Waveform_magnitudes_scale_to_unit_peaks_within_the_buffer()
    {
        byte[] magnitudes = [0, 255, 51, 102];
        Span<float> peaks = stackalloc float[3];
        Assert.Equal(3, Rules.Peaks(magnitudes, peaks));
        Assert.Equal(0f, peaks[0]);
        Assert.Equal(1f, peaks[1]);
        Assert.Equal(0.2f, peaks[2], 5);
    }
}
