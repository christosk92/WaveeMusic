using System.Collections.Concurrent;
using System.Text;
using Google.Protobuf;
using Md = Wavee.Protocol.Metadata;
using Pl = Wavee.Protocol.Playlist;
using Xunit;

namespace Wavee.Tests;

[Collection(EntitiesCollection.Name)]
public sealed class AudiobookClassificationTests : IDisposable
{
    readonly string _path = Path.Combine(Path.GetTempPath(), "wavee-audiobook-" + Guid.NewGuid().ToString("N") + ".db");
    readonly ConcurrentQueue<Action> _posted = new();
    static byte[] Gid(byte seed) => Enumerable.Range(0, 16).Select(i => (byte)(seed + i)).ToArray();
    static EntityId Id(EntityKind kind, byte seed) => EntityId.ForGid(kind, Gid(seed));
    static Show Book => Entities.Show(Id(EntityKind.Show, 10));
    static Episode Chapter => Entities.Episode(Id(EntityKind.Episode, 40));

    public AudiobookClassificationTests()
    {
        Fetch.Reset(); Store.Shutdown(); Store.Post = a => _posted.Enqueue(a);
        Store.Register(new ShowShape()); Store.Register(new EpisodeShape()); Store.Use(_path);
        Entities.Boot(CatalogScope.Fake()); Store.Flush();
    }

    public void Dispose()
    {
        Fetch.Reset(); Store.Shutdown(); Store.Use(null); Store.Post = static a => a();
        foreach (string suffix in new[] { "", "-wal", "-shm" })
            try { File.Delete(_path + suffix); } catch (IOException) { }
    }

    static Md.Show ShowWire(bool? audiobook = null) => new()
    {
        Gid = ByteString.CopyFrom(Gid(10)), Name = "The Manager's Path", Publisher = "Camille Fournier",
        IsAudiobook = audiobook ?? false,
    };

    static Staging ChapterWire(bool chapter = true)
    {
        // themanagerpath.saz raw/401_s.txt: all 19 EpisodeV4 entries explicitly set field96=1;
        // nested Show contains identity only, and parent ShowV4 responses are304 without a payload.
        var show = new Md.Show { Gid = ByteString.CopyFrom(Gid(10)), Name = "The Manager's Path" };
        var wire = new Md.Episode
        {
            Gid = ByteString.CopyFrom(Gid(40)), Name = "Track 1", Duration = 120_000,
            Show = show, IsAudiobookChapter = chapter, Explicit = true,
        };
        var staged = Staging.Rent(); Spotify.Decode.EpisodeV4(wire.ToByteArray(), staged); return staged;
    }

    static Staging ShowBatch(bool audiobook)
    {
        var staged = Staging.Rent(); Spotify.Decode.ShowV4(ShowWire(audiobook).ToByteArray(), staged); return staged;
    }

    [Fact]
    public void Direct_show_flag_is_a_facts_bit_and_round_trips_without_chapters()
    {
        var stage = ShowBatch(true);
        TestScope.CommitAndPublish(stage);
        Assert.True(Book.IsAudiobook);
        Assert.True(Book.Flags.HasFlag(ShowFlags.Audiobook));
        Assert.True(Book.Knows(ShowFields.Facts));
        var persisted = ShowBatch(true); Assert.True(Store.WriteBehind(persisted)); Store.Flush();
        var table = Entities.Current.Shows; table.FreeSlot(Book.Slot);
        int cold = table.Slot(Id(EntityKind.Show, 10));
        Assert.True(Store.Read(Entities.Current, table, new[] { cold }, (uint)ShowFields.All, FetchPriority.Visible));
        Store.Flush(); while (_posted.TryDequeue(out var action)) action();
        Assert.True(new Show(cold).IsAudiobook);
        Assert.True(new Show(cold).Flags.HasFlag(ShowFlags.Audiobook));
    }

    [Fact]
    public void Captured_chapter_fact_classifies_thin_parent_without_claiming_or_overwriting_show_facts()
    {
        TestScope.CommitAndPublish(ChapterWire());
        Assert.True(Chapter.IsAudiobookChapter);
        Assert.True(Book.IsAudiobook);
        Assert.False(Book.Knows(ShowFields.Facts));
        Assert.Equal(ShowFlags.None, Book.Flags);
        Assert.Empty(Book.EpisodeSlots.ToArray()); // works before membership is loaded

        var full = Staging.Rent();
        var wire = ShowWire(false); wire.Explicit = true; wire.MediaType = Md.Show.Types.MediaType.Video;
        wire.ConsumptionOrder = Md.Show.Types.ConsumptionOrder.Sequential; wire.TrailerUri = "spotify:episode:trailer";
        Spotify.Decode.ShowV4(wire.ToByteArray(), full); TestScope.CommitAndPublish(full);
        TestScope.CommitAndPublish(ChapterWire());
        Assert.True(Book.IsAudiobook);
        Assert.Equal(ShowFlags.Explicit | ShowFlags.Video, Book.Flags);
        Assert.Equal(ConsumptionOrder.Sequential, Book.Order);
        Assert.Equal("spotify:episode:trailer", Entities.Strings.Resolve(Book.TrailerId));
    }

    [Fact]
    public void Similar_titles_and_plain_episodes_never_classify_a_book()
    {
        TestScope.CommitAndPublish(ShowBatch(false));
        TestScope.CommitAndPublish(ChapterWire(false));
        Assert.False(Chapter.IsAudiobookChapter);
        Assert.False(Book.IsAudiobook);
        TestScope.CommitAndPublish(ChapterWire());
        Assert.True(Book.IsAudiobook);
        TestScope.CommitAndPublish(ChapterWire(false)); // authoritative correction
        Assert.False(Book.IsAudiobook);
    }

    [Fact]
    public void A_chapter_never_classifies_an_unrelated_show_or_survives_a_scope_reset()
    {
        TestScope.CommitAndPublish(ChapterWire());
        var unrelated = Entities.Show(Id(EntityKind.Show, 11));
        Assert.False(unrelated.IsAudiobook);
        Assert.True(Book.IsAudiobook);
        Entities.Boot(CatalogScope.Fake());
        Assert.False(Book.IsAudiobook);
    }

    // ── D2 §4.3: the show's OWN list-header attributes classify it before `ShowV4` ever answers ───────────────────────

    static Pl.FormatListAttribute Attr(string key, string value) => new() { Key = key, Value = value };

    /// <summary>The official capture (plan §4.3, ledger row 10): `GET playlist/v2/show/{id}` on The Manager's Path
    /// carries `is_audiobook=true` / `autoplay_candidate=false` as list-header attributes — proof this route can
    /// classify a book even when `ShowV4`'s own field 89 answers a cached 304 with nothing behind it.</summary>
    [Fact]
    public void Header_attributes_classify_the_book_and_flag_no_autoplay_before_any_ShowV4_answer()
    {
        var content = new Pl.SelectedListContent
        {
            Length = 0,
            Attributes = new Pl.ListAttributes
            {
                FormatAttributes =
                {
                    Attr("is_audiobook", "true"),
                    Attr("autoplay_candidate", "false"),
                    Attr("end_of_list_action", "STOP"),   // present on the wire, not read by this decoder
                },
            },
        };
        var s = Staging.Rent();
        Spotify.Decode.ShowHeaderAttributes(content.ToByteArray(), Encoding.UTF8.GetBytes(Id(EntityKind.Show, 10).Text), s);
        TestScope.CommitAndPublish(s);

        Assert.True(Book.IsAudiobook);
        Assert.True(Book.Flags.HasFlag(ShowFlags.Audiobook));
        Assert.True(Book.Flags.HasFlag(ShowFlags.NoAutoplay));
        // Outside the Facts ladder: this route never claims the whole group, so a page must not treat it as settled.
        Assert.False(Book.Knows(ShowFields.Facts));
    }

    /// <summary>A partial header answer (only `is_audiobook` this time) must never clear a sibling bit a fuller
    /// `ShowV4` — or an earlier header read — already set: setting bits only, never clearing others.</summary>
    [Fact]
    public void Header_attributes_only_ever_set_bits_never_clear_a_sibling_one()
    {
        var first = Staging.Rent();
        Spotify.Decode.ShowHeaderAttributes(new Pl.SelectedListContent
        {
            Attributes = new Pl.ListAttributes { FormatAttributes = { Attr("autoplay_candidate", "false") } },
        }.ToByteArray(), Encoding.UTF8.GetBytes(Id(EntityKind.Show, 10).Text), first);
        TestScope.CommitAndPublish(first);
        Assert.True(Book.Flags.HasFlag(ShowFlags.NoAutoplay));
        Assert.False(Book.IsAudiobook);

        var second = Staging.Rent();
        Spotify.Decode.ShowHeaderAttributes(new Pl.SelectedListContent
        {
            Attributes = new Pl.ListAttributes { FormatAttributes = { Attr("is_audiobook", "true") } },
        }.ToByteArray(), Encoding.UTF8.GetBytes(Id(EntityKind.Show, 10).Text), second);
        TestScope.CommitAndPublish(second);

        Assert.True(Book.IsAudiobook);
        Assert.True(Book.Flags.HasFlag(ShowFlags.NoAutoplay)); // the first answer's fact must have survived
    }

    /// <summary>A subsequent whole-group `ShowV4` Facts answer is the AUTHORITATIVE decode and may legitimately
    /// correct a header guess (this book's `ShowV4` truthfully says `is_audiobook=false`) — only `NoAutoplay`, which
    /// rides outside `ShowFlags.FactsMask` entirely, is immune to a full-group replace.</summary>
    [Fact]
    public void A_full_ShowV4_facts_answer_can_correct_a_header_guess_but_never_touches_NoAutoplay()
    {
        var header = Staging.Rent();
        Spotify.Decode.ShowHeaderAttributes(new Pl.SelectedListContent
        {
            Attributes = new Pl.ListAttributes
            {
                FormatAttributes = { Attr("is_audiobook", "true"), Attr("autoplay_candidate", "false") },
            },
        }.ToByteArray(), Encoding.UTF8.GetBytes(Id(EntityKind.Show, 10).Text), header);
        TestScope.CommitAndPublish(header);
        Assert.True(Book.IsAudiobook);

        TestScope.CommitAndPublish(ShowBatch(false)); // the real decode disagrees
        Assert.False(Book.Flags.HasFlag(ShowFlags.Audiobook));
        Assert.True(Book.Flags.HasFlag(ShowFlags.NoAutoplay)); // untouched by the Facts group replace
    }

    [Fact]
    public void Partial_title_updates_and_disk_reload_preserve_chapter_classification()
    {
        TestScope.CommitAndPublish(ChapterWire());
        Assert.True(Store.WriteBehind(ChapterWire())); Store.Flush();
        Staging Partial()
        {
            var stage = Staging.Rent();
            ref var row = ref stage.Episodes.RowFor(Id(EntityKind.Episode, 40), Authority.Full, (uint)EpisodeFields.Title);
            row.Title = stage.Text("Chapter One"); return stage;
        }
        TestScope.CommitAndPublish(Partial());
        Assert.True(Chapter.IsAudiobookChapter);
        Assert.True(Store.WriteBehind(Partial())); Store.Flush();
        var episodes = Entities.Current.Episodes; episodes.FreeSlot(Chapter.Slot);
        int cold = episodes.Slot(Id(EntityKind.Episode, 40));
        Assert.True(Store.Read(Entities.Current, episodes, new[] { cold }, (uint)EpisodeFields.All, FetchPriority.Visible));
        Store.Flush(); while (_posted.TryDequeue(out var action)) action();
        Assert.True(new Episode(cold).IsAudiobookChapter);
        Assert.True(Book.IsAudiobook);
    }
}
