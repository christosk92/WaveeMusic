using Google.Protobuf;
using Wavee;
using Xunit;
using Md = Wavee.Protocol.Metadata;

namespace Wavee.Tests;

[Collection(EntitiesCollection.Name)]
public sealed class ShowIdentityTests
{
    static readonly byte[] ShowGid = Enumerable.Range(1, 16).Select(i => (byte)i).ToArray();
    static EntityId Id => EntityId.ForGid(EntityKind.Show, ShowGid);
    static Show Subject => Entities.Show(Id);

    static void Stage(Authority authority, ShowFields known, string title = "", string image = "", string publisher = "")
    {
        var s = Staging.Rent();
        ref var row = ref s.Shows.RowFor(Id, authority, (uint)known);
        row.Title = s.Text(title); row.Image = s.Text(image); row.Publisher = s.Text(publisher);
        TestScope.CommitAndPublish(s);
    }

    static void Mention(bool cover = false)
    {
        var show = new Md.Show { Gid = ByteString.CopyFrom(ShowGid), Name = "Mention title" };
        if (cover) show.CoverImage = new Md.ImageGroup
        {
            Image = { new Md.Image { FileId = ByteString.CopyFrom(new byte[20]), Size = Md.Image.Types.Size.Default } },
        };
        var episode = new Md.Episode { Gid = ByteString.CopyFrom(ShowGid), Name = "Episode", Show = show };
        var s = Staging.Rent();
        Spotify.Decode.EpisodeV4(episode.ToByteArray(), s);
        TestScope.CommitAndPublish(s);
    }

    [Fact]
    public void ParentMentionDoesNotEraseExistingLibraryArtworkOrPublisher()
    {
        TestScope.Fresh();
        Stage(Authority.Thin, ShowFields.Identity, "Library title", "https://example.test/show.jpg", "Publisher");
        Mention();
        Assert.Equal("Mention title", Subject.Title);
        Assert.Equal("https://example.test/show.jpg", Entities.Strings.Resolve(Subject.ImageId));
        Assert.Equal("Publisher", Entities.Strings.Resolve(Subject.PublisherId));
        Assert.True(Subject.Knows(ShowFields.Identity));
    }

    [Fact]
    public void ThinParentLeavesAbsentIdentityFieldsDemandable()
    {
        TestScope.Fresh();
        Mention();
        Assert.True(Subject.Knows(ShowFields.Title));
        Assert.False(Subject.Knows(ShowFields.Image));
        Assert.False(Subject.Knows(ShowFields.Publisher));
        Stage(Authority.Full, ShowFields.Identity, "Full title", "https://example.test/full.jpg", "Publisher");
        Assert.True(Subject.Knows(ShowFields.Identity));
        Assert.Equal("https://example.test/full.jpg", Entities.Strings.Resolve(Subject.ImageId));
    }

    [Fact]
    public void OlderThinRowsClaimingIdentityDoNotSealMissingColumns()
    {
        TestScope.Fresh();
        Stage(Authority.Thin, ShowFields.Identity, "Cached title");
        Assert.True(Subject.Knows(ShowFields.Title));
        Assert.False(Subject.Knows(ShowFields.Image));
        Assert.False(Subject.Knows(ShowFields.Publisher));
    }

    [Fact]
    public void FillingAnImageHoleCannotDowngradeAnAuthoritativeTitle()
    {
        TestScope.Fresh();
        Stage(Authority.Full, ShowFields.Title, "Full title");
        Stage(Authority.Thin, ShowFields.Identity, "Thin title", "https://example.test/card.jpg", "Publisher");
        Assert.Equal("Full title", Subject.Title);
        Assert.Equal("https://example.test/card.jpg", Entities.Strings.Resolve(Subject.ImageId));
        Assert.Equal("Publisher", Entities.Strings.Resolve(Subject.PublisherId));
    }

    [Fact]
    public void FullIdentityAnswerCanConfirmNoArtworkOrPublisher()
    {
        TestScope.Fresh();
        Stage(Authority.Full, ShowFields.Identity, "Title");
        Assert.True(Subject.Knows(ShowFields.Identity));
        Assert.Equal("", Entities.Strings.Resolve(Subject.ImageId));
        Assert.Equal("", Entities.Strings.Resolve(Subject.PublisherId));
    }

    [Fact]
    public void EmbeddedShowCoverIsHydratedWhenWireCarriesIt()
    {
        TestScope.Fresh();
        Mention(cover: true);
        Assert.True(Subject.Knows(ShowFields.Image));
        Assert.NotEqual("", Entities.Strings.Resolve(Subject.ImageId));
        Assert.False(Subject.Knows(ShowFields.Publisher));
    }

    // ── D2 §4.2: the producer mask on `ShowV4` itself (not just `ThinShow`'s mention) ────────────────────────────────

    /// <summary>report 2b: a followed show re-answered by `ShowV4` without `cover_image` on the wire must not blank
    /// the artwork a prior Full answer already sealed — the whole point of the producer mask (claim only the columns
    /// this payload actually carried, never the whole Identity group because the row happens to be a Full decode).</summary>
    [Fact]
    public void FullShowV4AnswerWithoutCoverImageKeepsTheOldArtwork()
    {
        TestScope.Fresh();
        var s0 = Staging.Rent();
        Spotify.Decode.ShowV4(new Md.Show
        {
            Gid = ByteString.CopyFrom(ShowGid), Name = "SOLVED", Publisher = "Focus Studios",
            CoverImage = new Md.ImageGroup { Image = { new Md.Image { FileId = ByteString.CopyFrom(new byte[20]), Size = Md.Image.Types.Size.Default } } },
        }.ToByteArray(), s0);
        TestScope.CommitAndPublish(s0);
        Assert.True(Subject.Knows(ShowFields.Image));
        string sealedImage = Entities.Strings.Resolve(Subject.ImageId);
        Assert.NotEqual("", sealedImage);

        // A LATER Full `ShowV4` answer for the same show that omits `cover_image` (0.2.9's cross-show art defect,
        // report 2b: "SOLVED" wore "The Manager's Path" art after exactly this kind of re-answer).
        var s1 = Staging.Rent();
        Spotify.Decode.ShowV4(new Md.Show { Gid = ByteString.CopyFrom(ShowGid), Name = "SOLVED", Publisher = "Focus Studios" }.ToByteArray(), s1);
        TestScope.CommitAndPublish(s1);

        Assert.Equal(sealedImage, Entities.Strings.Resolve(Subject.ImageId));
        Assert.True(Subject.Knows(ShowFields.Image));
    }

    /// <summary>D2 §4.2's mismatch guard: the envelope's asked uri and the payload's own gid disagreeing is a wire
    /// defect (a misrouted or stale batch answer), never a legitimate second identity — the row is dropped, not
    /// sealed under the asked uri.</summary>
    [Fact]
    public void ShowV4PayloadGidMismatchedAgainstTheAskedUriIsDropped()
    {
        TestScope.Fresh();
        var askedId = EntityId.ForGid(EntityKind.Show, Enumerable.Repeat((byte)9, 16).ToArray());
        var payloadGid = Enumerable.Repeat((byte)7, 16).ToArray(); // a DIFFERENT show than the envelope asked for

        var s = Staging.Rent();
        byte[] askedUri = System.Text.Encoding.UTF8.GetBytes(askedId.Text);
        Spotify.Decode.ShowV4(new Md.Show { Gid = ByteString.CopyFrom(payloadGid), Name = "Wrong Show" }.ToByteArray(), askedUri, s);
        TestScope.CommitAndPublish(s);

        var asked = new Show(Entities.Current.Shows.Slot(askedId));
        Assert.False(asked.Knows(ShowFields.Title));
        var payload = new Show(Entities.Current.Shows.Slot(EntityId.ForGid(EntityKind.Show, payloadGid)));
        Assert.False(payload.Knows(ShowFields.Title));
    }

    /// <summary>An unenveloped decode (a direct fixture call, `entityUri` empty) has nothing to compare against and
    /// must not be treated as a mismatch.</summary>
    [Fact]
    public void ShowV4WithNoEnvelopeIsNeverTreatedAsAMismatch()
    {
        TestScope.Fresh();
        Mention();
        var s = Staging.Rent();
        Spotify.Decode.ShowV4(new Md.Show { Gid = ByteString.CopyFrom(ShowGid), Name = "Full title", Publisher = "Publisher" }.ToByteArray(), s);
        TestScope.CommitAndPublish(s);
        Assert.Equal("Full title", Subject.Title);
    }

    // ── D2 §4.2: the one-time repair of a row an old decoder sealed wrong (`ShowIdentityRepair.Demote`) ─────────────

    [Fact]
    public void Demote_FullIdentityMissingArtworkOrPublisherBecomesThin()
    {
        Assert.Equal(Authority.Thin, ShowIdentityRepair.Demote(Authority.Full, hasImage: false, hasPublisher: true));
        Assert.Equal(Authority.Thin, ShowIdentityRepair.Demote(Authority.Full, hasImage: true, hasPublisher: false));
        Assert.Equal(Authority.Thin, ShowIdentityRepair.Demote(Authority.Full, hasImage: false, hasPublisher: false));
    }

    [Fact]
    public void Demote_FullIdentityWithBothColumnsPresentStaysFull()
        => Assert.Equal(Authority.Full, ShowIdentityRepair.Demote(Authority.Full, hasImage: true, hasPublisher: true));

    [Fact]
    public void Demote_AlreadyThinIsUnaffectedEitherWay()
    {
        Assert.Equal(Authority.Thin, ShowIdentityRepair.Demote(Authority.Thin, hasImage: false, hasPublisher: false));
        Assert.Equal(Authority.Thin, ShowIdentityRepair.Demote(Authority.Thin, hasImage: true, hasPublisher: true));
    }

    /// <summary>The repair wired into `ShowShape.Load` end to end: a row written to disk by the OLD decoder (Full,
    /// claiming Identity whole, with an empty publisher) comes back Thin on a cold read, so a real answer can fill
    /// the hole instead of `Table.Accepts` refusing it forever.</summary>
    [Fact]
    public void OldlySealedFullRowWithEmptyPublisherIsDemotedOnLoad()
    {
        string path = Path.Combine(Path.GetTempPath(), "wavee-show-identity-repair-" + Guid.NewGuid().ToString("N") + ".db");
        var posted = new System.Collections.Concurrent.ConcurrentQueue<Action>();
        Staging OldSealedRow()
        {
            var s = Staging.Rent();
            ref var row = ref s.Shows.RowFor(Id, Authority.Full, (uint)ShowFields.Identity);
            row.Title = s.Text("Old title");
            row.Image = s.Text("https://example.test/old.jpg");
            row.Publisher = default; // never rode the old wire — exactly the defect Load's demotion repairs
            return s;
        }
        try
        {
            Fetch.Reset(); Store.Shutdown(); Store.Post = a => posted.Enqueue(a);
            Store.Register(new ShowShape()); Store.Use(path);
            Entities.Boot(CatalogScope.Fake()); Store.Flush();

            TestScope.CommitAndPublish(OldSealedRow());              // creates the live slot
            Assert.True(Store.WriteBehind(OldSealedRow()));          // a SEPARATE staging: persists it to disk
            Store.Flush();

            var t = Entities.Current.Shows;
            t.FreeSlot(Subject.Slot);
            int cold = t.Slot(Id);
            Assert.True(Store.Read(Entities.Current, t, new[] { cold }, (uint)ShowFields.All, FetchPriority.Visible));
            Store.Flush();
            while (posted.TryDequeue(out var action)) action();

            // A Thin correction must be ACCEPTED — refused forever is exactly the old defect (`Table.Accepts`).
            var fix = Staging.Rent();
            ref var fixedRow = ref fix.Shows.RowFor(Id, Authority.Thin, (uint)ShowFields.Publisher);
            fixedRow.Publisher = fix.Text("Repaired Publisher");
            Entities.Commit(fix); Entities.Publish(); Staging.Return(fix);
            Assert.Equal("Repaired Publisher", Entities.Strings.Resolve(new Show(cold).PublisherId));
        }
        finally
        {
            Fetch.Reset(); Store.Shutdown(); Store.Use(null); Store.Post = static a => a();
            foreach (string suffix in new[] { "", "-wal", "-shm" })
                try { File.Delete(path + suffix); } catch (IOException) { }
        }
    }
}
