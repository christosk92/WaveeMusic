// ── Wavee.Tests/QueueModelTests.cs — the queue as one core model (themes plan C7, X42) ─────────────────────────────────
// (Shell/QueueModel.cs, Shell/QueueVerbs.cs over `Queue.Rules.cs`.) Pure: the projection takes the queue's payload and a row
// resolver as arguments, so nothing here needs an entity scope, a playback model or a surface.
//
//   THE PROJECTION   the three sections' counts, the rows with their section and position, the autoplay preview, the two
//                    "nothing here" facts; a viewed (remote) queue ignores the local cursor.
//   THE PAGING       "Show more" is one counter per section on a surface, or one for all on the stage's continuous list.
//   THE VERBS        a row is re-located by its item id in the LIVE queue; the menu's edits need an owned queue and an
//                    upcoming row with a stable id; autoplay is one flag written through one door.

using Wavee.Theming.Contracts;
using Xunit;

using RepeatMode = Wavee.Spotify.Decode.RepeatMode;

namespace Wavee.Tests;

public class QueueModelProjectionTests
{
    // 0 history · 1 now playing · 2,3 queued by the user · 4,5 the context's continuation · 6 the autoplay radio
    static readonly QueueEdge[] Edges =
    [
        new(1, (byte)QueueProvider.Context, (byte)QueueBucket.History),
        new(2, (byte)QueueProvider.Context, (byte)QueueBucket.NowPlaying),
        new(3, (byte)QueueProvider.Queue, (byte)QueueBucket.UserQueue),
        new(4, (byte)QueueProvider.Queue, (byte)QueueBucket.UserQueue),
        new(5, (byte)QueueProvider.Context, (byte)QueueBucket.NextUp),
        new(6, (byte)QueueProvider.Context, (byte)QueueBucket.NextUp),
        new(7, (byte)QueueProvider.Autoplay, (byte)QueueBucket.NextUp),
    ];

    static EntityRef RefAt(int i) => new(EntityKind.Track, i + 10);

    static QueueProps Project(int cursor = 1, bool viewer = false, bool autoplay = true, QueueEdge[]? edges = null)
        => Shell.QueueModel.Project(edges ?? Edges, RefAt, cursor, viewer, autoplay, shuffle: false, RepeatMode.Off,
            RefAt(1), playingFrom: null);

    [Fact]
    public void The_counts_split_the_upcoming_rows_by_section()
    {
        var props = Project();
        Assert.Equal(new QueueCounts(2, 2, 1), props.Counts);
        Assert.Equal(2, props.NextInQueue);
        Assert.Equal(2, props.NextUp);
        Assert.Equal(5, props.Counts.Total);
    }

    [Fact]
    public void The_rows_are_listed_section_by_section_with_their_position_in_it()
    {
        var rows = Project().Rows;
        Assert.Equal(new[] { 2, 3, 4, 5, 6 }, rows.Select(r => r.Index));
        Assert.Equal(new[] { 3UL, 4UL, 5UL, 6UL, 7UL }, rows.Select(r => r.ItemId));
        Assert.Equal(new[] { QueueSection.Queue, QueueSection.Queue, QueueSection.NextUp, QueueSection.NextUp, QueueSection.Autoplay },
            rows.Select(r => r.Section));
        Assert.Equal(new[] { 0, 1, 0, 1, 0 }, rows.Select(r => r.Position));
        Assert.Equal(RefAt(4), rows[2].Ref);
    }

    [Fact]
    public void A_section_row_is_found_by_its_position_in_the_list()
    {
        var props = Project();
        Assert.True(props.TryRow(QueueSection.Queue, 1, out var queued));
        Assert.Equal(4UL, queued.ItemId);
        Assert.True(props.TryRow(QueueSection.NextUp, 0, out var next));
        Assert.Equal(5UL, next.ItemId);
        Assert.True(props.TryRow(QueueSection.Autoplay, 0, out var radio));
        Assert.Equal(7UL, radio.ItemId);
        Assert.Equal(RefAt(6), radio.Ref);
    }

    [Fact]
    public void A_position_past_a_sections_end_has_no_row()
    {
        var props = Project();
        Assert.False(props.TryRow(QueueSection.Queue, 2, out _));
        Assert.False(props.TryRow(QueueSection.NextUp, -1, out _));
        Assert.False(props.TryRow(QueueSection.Autoplay, 1, out _));
    }

    [Fact]
    public void Autoplay_rows_have_no_row_to_find_while_the_toggle_is_off()
    {
        var off = Project(autoplay: false);
        Assert.False(off.TryRow(QueueSection.Autoplay, 0, out _));
        Assert.True(off.TryRow(QueueSection.NextUp, 1, out var last));
        Assert.Equal(6UL, last.ItemId);
    }

    [Fact]
    public void Autoplay_rows_are_listed_only_while_the_toggle_is_on_but_always_counted()
    {
        var off = Project(autoplay: false);
        Assert.Equal(4, off.Rows.Length);
        Assert.DoesNotContain(off.Rows, r => r.Section == QueueSection.Autoplay);
        Assert.Equal(1, off.Counts.Autoplay);
        Assert.False(off.Autoplay.Enabled);
        Assert.Equal(1, off.Autoplay.Count);
    }

    [Fact]
    public void The_autoplay_preview_is_the_first_row_the_radio_would_play()
    {
        Assert.Equal(RefAt(6), Project().Autoplay.Preview);
        Assert.Equal(RefAt(6), Project(autoplay: false).Autoplay.Preview);
    }

    [Fact]
    public void No_autoplay_rows_means_no_preview()
    {
        QueueEdge[] edges = Edges[..6];
        Assert.True(Project(edges: edges).Autoplay.Preview.IsNone);
        Assert.Equal(0, Project(edges: edges).Counts.Autoplay);
    }

    [Fact]
    public void A_local_queue_divides_at_the_cursor_and_a_viewed_one_at_its_now_playing_row()
    {
        // Cursor 0: the NowPlaying row is "upcoming" to a local session that has not played it, so it joins the continuation.
        Assert.Equal(3, Project(cursor: 0).Counts.NextUp);
        // A viewed (remote) mirror ignores the local cursor entirely.
        var viewed = Project(cursor: 0, viewer: true);
        Assert.Equal(2, viewed.Counts.NextUp);
        Assert.True(viewed.Viewer);
    }

    [Fact]
    public void Rows_are_removable_on_an_owned_queue_only_and_drag_as_the_row_rules_say()
    {
        var owned = Project().Rows[0];
        Assert.True(owned.Removable);
        Assert.Equal(QueueRowRules.RowDrags(viewer: false), owned.Draggable);

        var viewed = Project(viewer: true).Rows[0];
        Assert.False(viewed.Removable);
        Assert.Equal(QueueRowRules.RowDrags(viewer: true), viewed.Draggable);
    }

    [Fact]
    public void An_empty_queue_is_empty_and_has_nothing_to_preview()
    {
        var props = Project(cursor: -1, edges: []);
        Assert.Empty(props.Rows);
        Assert.Equal(default(QueueCounts), props.Counts);
        Assert.True(props.Empty);
        Assert.True(props.Autoplay.Preview.IsNone);
    }

    [Fact]
    public void The_toggles_and_the_context_ride_along_unchanged()
    {
        var from = new QueuePlayingFrom("Road Trip", default);
        var props = Shell.QueueModel.Project(Edges, RefAt, 1, viewer: false, autoplayEnabled: true, shuffle: true, RepeatMode.Track,
            RefAt(1), from);
        Assert.True(props.Shuffle);
        Assert.Equal(RepeatMode.Track, props.Repeat);
        Assert.Equal(RefAt(1), props.NowPlaying);
        Assert.Equal(from, props.PlayingFrom);
    }

    [Fact]
    public void Playing_from_needs_a_name()
    {
        Assert.Null(Shell.QueueModel.PlayingFromOf(null, default));
        Assert.Null(Shell.QueueModel.PlayingFromOf("", default));
        Assert.Equal(new QueuePlayingFrom("Liked Songs", default), Shell.QueueModel.PlayingFromOf("Liked Songs", default));
    }

    [Fact]
    public void Nothing_playing_is_no_now_playing_row_and_nothing_queued_or_continuing_and_autoplay_alone_does_not_count()
    {
        Assert.True(Shell.QueueModel.ShowsNothingPlaying(true, new QueueCounts(0, 0, 9)));
        Assert.False(Shell.QueueModel.ShowsNothingPlaying(false, new QueueCounts(0, 0, 0)));
        Assert.False(Shell.QueueModel.ShowsNothingPlaying(true, new QueueCounts(1, 0, 0)));
        Assert.False(Shell.QueueModel.ShowsNothingPlaying(true, new QueueCounts(0, 1, 0)));
    }

    [Fact]
    public void Empty_means_nothing_in_any_section_autoplay_included()
    {
        Assert.True(Shell.QueueModel.IsEmpty(new QueueCounts(0, 0, 0)));
        Assert.False(Shell.QueueModel.IsEmpty(new QueueCounts(0, 0, 1)));
    }

    [Fact]
    public void The_counts_answer_per_section()
    {
        var counts = new QueueCounts(1, 2, 3);
        Assert.Equal(1, counts.Of(QueueSection.Queue));
        Assert.Equal(2, counts.Of(QueueSection.NextUp));
        Assert.Equal(3, counts.Of(QueueSection.Autoplay));
    }
}

public class QueuePagesTests
{
    [Fact]
    public void Each_section_pages_on_its_own_on_the_rail()
    {
        var pages = new Shell.QueuePages();
        pages.ShowMore(QueueSection.NextUp);
        pages.ShowMore(QueueSection.NextUp);
        Assert.Equal(1, pages.Of(QueueSection.Queue).Peek());
        Assert.Equal(3, pages.Of(QueueSection.NextUp).Peek());
        Assert.Equal(1, pages.Of(QueueSection.Autoplay).Peek());
    }

    [Fact]
    public void The_stages_continuous_list_pages_every_section_together()
    {
        var pages = new Shell.QueuePages(unified: true);
        pages.ShowMore(QueueSection.Autoplay);
        Assert.Equal(2, pages.Of(QueueSection.Queue).Peek());
        Assert.Equal(2, pages.Of(QueueSection.NextUp).Peek());
        Assert.Same(pages.Of(QueueSection.Queue), pages.Of(QueueSection.Autoplay));
    }

    [Fact]
    public void A_context_change_returns_every_section_to_page_one()
    {
        var rail = new Shell.QueuePages();
        rail.ShowMore(QueueSection.Queue);
        rail.ShowMore(QueueSection.Autoplay);
        rail.Reset();
        Assert.Equal(1, rail.Of(QueueSection.Queue).Peek());
        Assert.Equal(1, rail.Of(QueueSection.Autoplay).Peek());

        var stage = new Shell.QueuePages(unified: true);
        stage.ShowMore(QueueSection.Queue);
        stage.Reset();
        Assert.Equal(1, stage.Of(QueueSection.NextUp).Peek());
    }

    [Fact]
    public void The_verb_pages_the_surface_it_was_built_over()
    {
        var pages = new Shell.QueuePages();
        IQueueVerbs verbs = new Shell.QueueVerbs(pages);      // by the interface: the contract, not just the class
        verbs.ShowMore(QueueSection.Queue);
        Assert.Equal(2, pages.Of(QueueSection.Queue).Peek());
    }
}

public class QueueVerbRulesTests
{
    static readonly EntityRef A = new(EntityKind.Track, 1);
    static readonly EntityRef B = new(EntityKind.Track, 2);

    [Fact]
    public void A_row_with_an_item_id_is_found_by_it_wherever_the_queue_moved_it()
    {
        // The render saw it at index 3; the queue shifted and it now sits at 7.
        Assert.Equal(7, Shell.QueueVerbs.Locate(42, 3, A, id => id == 42 ? 7 : -1, _ => default));
    }

    [Fact]
    public void A_row_that_left_the_queue_is_gone()
        => Assert.Equal(-1, Shell.QueueVerbs.Locate(42, 3, A, _ => -1, _ => A));

    [Fact]
    public void A_row_without_an_item_id_is_found_by_its_captured_index_while_it_still_holds_the_same_ref()
    {
        Assert.Equal(3, Shell.QueueVerbs.Locate(0, 3, A, _ => -1, i => i == 3 ? A : B));
        Assert.Equal(-1, Shell.QueueVerbs.Locate(0, 3, A, _ => -1, _ => B));            // something else sits there now
        Assert.Equal(-1, Shell.QueueVerbs.Locate(0, 3, default, _ => -1, _ => default)); // no row to match
    }

    [Fact]
    public void The_menu_offers_edits_only_on_an_owned_queue_for_an_upcoming_row_with_a_stable_id()
    {
        Assert.True(Shell.QueueVerbs.RowMenuEditable(viewer: false, itemId: 5, positionInSection: 0));
        Assert.False(Shell.QueueVerbs.RowMenuEditable(viewer: true, itemId: 5, positionInSection: 0));
        Assert.False(Shell.QueueVerbs.RowMenuEditable(viewer: false, itemId: 0, positionInSection: 0));
        Assert.False(Shell.QueueVerbs.RowMenuEditable(viewer: false, itemId: 5, positionInSection: -1));
    }

    [Fact]
    public void Move_up_and_move_down_are_legal_only_inside_the_section()
    {
        Assert.False(Shell.QueueVerbs.CanMoveUp(0));
        Assert.True(Shell.QueueVerbs.CanMoveUp(1));
        Assert.True(Shell.QueueVerbs.CanMoveDown(0, 2));
        Assert.False(Shell.QueueVerbs.CanMoveDown(1, 2));
        Assert.False(Shell.QueueVerbs.CanMoveDown(0, 1));       // a one-row section can move nowhere
    }

    [Fact]
    public void Each_section_has_its_own_name_in_the_menu_header()
    {
        Assert.Equal(Strings.Player.QueueSectionNext, Shell.QueueVerbs.SectionNameKey(QueueSection.Queue));
        Assert.Equal(Strings.Player.QueueSectionNextUp, Shell.QueueVerbs.SectionNameKey(QueueSection.NextUp));
        Assert.Equal(Strings.Player.QueueSectionAutoplay, Shell.QueueVerbs.SectionNameKey(QueueSection.Autoplay));
    }
}

[Collection(PlatformCollection.Name)]
public sealed class QueueAutoplayVerbTests : IDisposable
{
    readonly MemoryAppSettings _store = new();

    public QueueAutoplayVerbTests() => Platform.UseSettings(_store);
    public void Dispose() => Platform.UseSettings(null);

    [Fact]
    public void Autoplay_is_on_by_default()
        => Assert.True(Shell.QueueModel.AutoplayEnabled());

    [Fact]
    public void Setting_autoplay_writes_the_one_key()
    {
        Shell.QueueVerbs.Default.SetAutoplay(false);
        Assert.False(Shell.QueueModel.AutoplayEnabled());
        Assert.False(_store.Get(Platform.Keys.AutoplayEnabled));
        Shell.QueueVerbs.Default.SetAutoplay(true);
        Assert.True(Shell.QueueModel.AutoplayEnabled());
    }

    [Fact]
    public void Toggling_flips_whatever_is_stored()
    {
        Shell.QueueVerbs.Default.ToggleAutoplay();
        Assert.False(Shell.QueueModel.AutoplayEnabled());
        Shell.QueueVerbs.Default.ToggleAutoplay();
        Assert.True(Shell.QueueModel.AutoplayEnabled());
    }
}
