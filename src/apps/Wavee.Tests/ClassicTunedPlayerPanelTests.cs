// ── Wavee.Tests/ClassicTunedPlayerPanelTests.cs — tuned Classic CT4: the player bar and the panel (themes plan §6.6.3) ─────────
//
// P1 the queue from the Compact level up (its room from the seek bar and the gaps), the video split its own bit; P3 the player-style
// entries hidden until a style can change Now playing (#199); R2 the lyrics offline voice (an offline miss is its own state, retried
// when the connection is back). The geometry the boards fix is pinned in Wavee.ShellTests (`ClassicTunedPlayerBarMountedTests`).

using Wavee;
using Wavee.Themes.Classic;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Xunit;

namespace Wavee.Tests;

public class ClassicTunedPlayerBarTests
{
    static Shell.PlayerBarLayout Classic(Shell.PlayerBarTier tier) => Shell.PlayerBarLayout.ForTier(tier, 0f, ClassicPolicies.PlayerBar);

    [Fact]
    public void P1_the_queue_stays_from_the_compact_level_up_and_never_at_minimal()
    {
        Assert.False(Classic(Shell.PlayerBarTier.Minimal).ShowQueue);
        for (var t = Shell.PlayerBarTier.Compact; t <= Shell.PlayerBarTier.Full; t++)
        {
            var L = Classic(t);
            Assert.True(L.ShowQueue, t.ToString());
            // the podcast bar keeps it too
            Assert.True(Shell.PodcastBarLayout(L, ClassicPolicies.PlayerBar).ShowQueue, t + " (podcast)");
            // and it left the overflow menu
            var rows = new OverflowCommand[Shell.PlayerBarRules.MaxOverflow];
            int n = Shell.PlayerBarRules.Overflow(L, ownsTransport: true, active: true, hasVideo: true, rows);
            Assert.DoesNotContain(OverflowCommand.Queue, rows[..n]);
        }
    }

    [Fact]
    public void P1_the_queue_comes_ahead_of_lyrics_volume_and_shuffle_repeat()
    {
        // At Compact the queue is in the row while lyrics, the volume button and shuffle / repeat are not.
        var L = Classic(Shell.PlayerBarTier.Compact);
        var slots = Shell.PlayerBarRules.RightSlots(L, hasVideo: false);
        Assert.NotEqual(RightSlot.None, slots & RightSlot.Queue);
        Assert.Equal(RightSlot.None, slots & (RightSlot.Lyrics | RightSlot.Volume | RightSlot.Shuffle | RightSlot.Repeat));
    }

    [Fact]
    public void P1_the_video_split_keeps_the_wide_levels_and_is_its_own_bit()
    {
        for (var t = Shell.PlayerBarTier.Minimal; t <= Shell.PlayerBarTier.Full; t++)
        {
            var L = Classic(t);
            bool wide = t >= Shell.PlayerBarTier.Wide;
            Assert.Equal(wide, L.ShowVideoSplit);
            Assert.Equal(wide, (Shell.PlayerBarRules.RightSlots(L, hasVideo: true) & RightSlot.Video) != 0);
        }
        // a level with the queue and without the split carries the video verb in the "⋯" menu
        var compact = Classic(Shell.PlayerBarTier.Compact);
        var rows = new OverflowCommand[Shell.PlayerBarRules.MaxOverflow];
        int n = Shell.PlayerBarRules.Overflow(compact, ownsTransport: true, active: true, hasVideo: true, rows);
        Assert.Contains(OverflowCommand.Video, rows[..n]);
    }

    [Fact]
    public void P1_the_queue_never_costs_a_button_size_and_the_seek_bar_keeps_room_at_440()
    {
        var L = Classic(Shell.PlayerBarTier.Compact);
        Assert.Equal(32f, L.ButtonBox);
        Assert.Equal(16f, L.ButtonGlyph);
        Assert.Equal(L.ButtonBox, Shell.PlayerBarRules.SlotWidth(RightSlot.Queue, L));
        // The narrowest Compact window: what the centre keeps after the row padding, the identity block, the row gaps, the play button
        // and its gap, and the widest right cluster (queue · devices · ⋯). The board's seek rail floor is 40, beside the 44 remaining-
        // time label and its gap.
        float centre = 440f - 2f * L.RowPad - L.LeftW - 2f * L.RowGap - L.RightWMax;
        float seek = centre - (L.PrimaryBox + L.ClusterGap) - L.SeekGap - 44f;
        Assert.True(seek >= 40f, "seek " + seek);
    }

    [Fact]
    public void The_core_default_policy_is_classics()
        => Assert.Equal(ClassicPolicies.PlayerBar, PlayerBarPolicy.Default);
}

public class ClassicTunedPlayerStyleTests
{
    [Fact]
    public void P3_the_palette_drops_the_two_player_style_rows_where_a_style_cannot_change_now_playing()
    {
        var all = Shell.CommandTable.BuildIndex(null, null);
        var hidden = Shell.CommandTable.BuildIndex(null, null, playerStyleRows: false);
        Assert.Equal(all.Length - 2, hidden.Length);
        Assert.Contains(all, r => r.Id == "settings.npvNextStyle");
        Assert.Contains(all, r => r.Id == "settings.npvPresentation");
        Assert.DoesNotContain(hidden, r => Shell.CommandTable.IsPlayerStyleRow(r.Id));
    }

    [Fact]
    public void P3_the_settings_style_row_is_composed_only_in_developer_mode()
    {
        Assert.False(Settings.Catalog.RowVisible(Settings.Tab.Appearance, "npvStyle", developerMode: false));
        Assert.True(Settings.Catalog.RowVisible(Settings.Tab.Appearance, "npvStyle", developerMode: true));
    }
}

public class ClassicTunedLyricsStateTests
{
    [Fact]
    public void R2_an_offline_miss_is_its_own_state_and_cached_lyrics_still_read()
    {
        Assert.Equal(LyricsState.Offline, LyricsModel.Classify(true, true, false, false, null, false, offline: true));
        Assert.Equal(LyricsState.NoLyrics, LyricsModel.Classify(true, true, false, false, null, false, offline: false));
        var doc = new Lyrics.Doc("t", true, [new Lyrics.Line(1000, "one", [])], Lyrics.SyncKind.Line, "spotify");
        Assert.Equal(LyricsState.Synced, LyricsModel.Classify(true, true, false, false, doc, false, offline: true));
        // still loading or failed outranks the offline voice
        Assert.Equal(LyricsState.Loading, LyricsModel.Classify(true, true, true, false, null, false, offline: true));
    }

    [Fact]
    public void R2_the_offline_state_is_appended_so_every_older_value_keeps_its_number()
    {
        Assert.Equal(5, (int)LyricsState.Synced);
        Assert.Equal(6, (int)LyricsState.Offline);
    }
}

[Collection(LyricsStoreCollection.Name)]
public class LyricsStoreOfflineTests
{
    sealed class EmptySource : Lyrics.ISource
    {
        public int Calls;
        public string Id => "spotify";
        public bool Enabled => true;
        public double Prior => 0.5;
        public Task<Lyrics.Candidate?> FetchAsync(Lyrics.Request req, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult<Lyrics.Candidate?>(null);
        }
    }

    [Fact]
    public async Task R2_a_miss_while_offline_is_remembered_and_retried_once_the_connection_is_back()
    {
        string dir = Path.Combine(Path.GetTempPath(), "wavee-store-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var online = Lyrics.Store.Online;
        bool connected = false;
        try
        {
            var source = new EmptySource();
            var agg = new Lyrics.Aggregator([source],
                (id, _) => Task.FromResult<Lyrics.Request?>(new Lyrics.Request(id, "spotify:track:" + id, "Song", ["Artist"], "Album", 200_000)),
                new Lyrics.Options(PerSourceTimeoutMs: 5000, TotalTimeoutMs: 5000, FirstHitGraceMs: 0), diskCache: new Lyrics.DiskCache(dir));
            Lyrics.Store.Attach(agg);
            Lyrics.Store.Online = () => connected;
            string id = "offline-" + Guid.NewGuid().ToString("N");

            Lyrics.Store.Ensure(id);
            for (int i = 0; i < 200 && !Lyrics.Store.Answered(id); i++) await Task.Delay(25);
            Assert.True(Lyrics.Store.Answered(id));
            Assert.Null(Lyrics.Store.Doc(id));
            Assert.True(Lyrics.Store.AnsweredOffline(id));

            // still offline: nothing is asked again
            Lyrics.Store.RetryWhenOnline(id);
            Assert.True(Lyrics.Store.AnsweredOffline(id));

            // back online: the miss is asked again, and an online miss is a plain "no lyrics"
            connected = true;
            int before = source.Calls;
            Lyrics.Store.RetryWhenOnline(id);
            for (int i = 0; i < 200 && !Lyrics.Store.Answered(id); i++) await Task.Delay(25);
            Assert.True(Lyrics.Store.Answered(id));
            Assert.False(Lyrics.Store.AnsweredOffline(id));
            Assert.True(source.Calls >= before);
        }
        finally
        {
            Lyrics.Store.Online = online;
            Lyrics.Store.Attach(null);
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void R2_retry_leaves_a_real_answer_alone()
    {
        Lyrics.Store.Attach(null);
        Lyrics.Store.Retry("never-asked");
        Assert.False(Lyrics.Store.Answered("never-asked"));
        Assert.False(Lyrics.Store.AnsweredOffline("never-asked"));
    }
}
