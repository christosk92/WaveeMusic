// ── Wavee.Tests/VideoOverrideMirrorTests.cs — the attached video's BIT (G-220, 2026-09-18) ──────────────────────────
//
// THE DEFECT. `Track.HasVideo` is `(Flags & (HasVideo | VideoOverride)) != 0`, and it is what the player bar's video
// slot, the immersive rail, the availability fold and the album page's indicator all read. `Video.Overrides.Changed`
// had NO production subscriber, and the commit's Video arm only PRESERVES a `VideoOverride` bit already on the row —
// so nothing ever ORIGINATED it. An attached file PLAYED (the resolver's tier 1 asks the roster by uri) while no
// button, badge or row indicator ever appeared for it.
//
// THE FIX, in two halves, and both are pinned below:
//   · `Video.OverrideMirror` (Shell/Video.Overrides.Mirror.cs) subscribes `Changed` and writes the bit, the
//     `LocalVideo` column and one `Bump` + `Publish` for the row that EXISTS now;
//   · `CommitTracks` (Entities/Track.cs) probes the mirror's id index for every staged row, so a row that lands AFTER
//     the attach — a page mounted later, or a restart, where the device-wide roster loads long before any track slot
//     exists — lights too.
//
// WHAT IS DELIBERATELY NOT HERE. The fold's surface effects (`Video.State`) are `VideoStateTests`' — one class per
// process-wide static, the rule this suite's `VideoSurfaceRulesTests` header states — so this class touches the
// roster and the tables and never the placement machine. The one thing worth saying about it: the mirror folds
// through `State.FoldForTrack(..., boundary: false)`, whose upgrade is DEFERRED, and it applies neither of
// `OverrideMutation.Plan`'s reveal/force legs — attaching a file lights the BUTTON and plays nothing.
//
// The mirror is INSTALLED and UNINSTALLED by the fixture, the way the composition root installs it, so the facts run
// against the real subscription rather than a hand-called method — the missing subscription being the whole defect.

using System.IO;
using Wavee;
using Xunit;

namespace Wavee.Tests;

[Collection(EntitiesCollection.Name)]
public sealed class VideoOverrideMirrorTests : IDisposable
{
    // A TEXT-form uri on purpose: a roster key is a uri STRING, and `spotify:track:<22 base62>` is the gid form while
    // anything else takes the interned-text form. Both must line up with the row's own `EntityId`, so one fact below
    // uses a real gid too.
    const string One = "spotify:track:mirrorone";
    const string Two = "spotify:track:mirrortwo";
    const string Path1 = @"C:\clips\one.mp4";
    const string Path2 = @"C:\clips\two.mp4";

    readonly MemoryAppSettings _settings = new();

    public VideoOverrideMirrorTests()
    {
        TestScope.Fresh();
        Video.Overrides.FileExists = static _ => true;
        Video.Overrides.DirectoryExists = static _ => true;
        Video.Overrides.Attach(_settings);      // the roster first…
        Video.InstallMirror();                  // …then its mirror, exactly the order `App.cs` installs them in
    }

    public void Dispose()
    {
        Video.OverrideMirror.Uninstall();
        Video.Overrides.Attach(null);
        Video.Overrides.FileExists = File.Exists;
        Video.Overrides.DirectoryExists = Directory.Exists;
    }

    static EntityId Id(string uri) => EntityId.Parse(uri);

    /// <summary>Stage one track row the way a decoder does and commit it through a drain.</summary>
    static Track Stage(EntityId id, string title, TrackFlags flags = TrackFlags.None,
                       TrackFields known = TrackFields.Identity)
    {
        var s = Staging.Rent();
        ref var row = ref s.Tracks.RowFor(id, Authority.Full, (uint)known);
        row.Title = s.Text(title);
        row.Flags = (uint)flags;
        TestScope.CommitAndPublish(s);
        return Entities.Track(id);
    }

    // ── the roster mutation reaches a row that already exists ────────────────────────────────────────────────────────

    [Fact]
    public void An_attach_lights_the_bit_every_surface_reads_and_records_the_path()
    {
        var track = Stage(Id(One), "Has No Video Yet");
        Assert.False(track.HasVideo);
        Assert.True(track.LocalVideoId.IsEmpty);

        Assert.Equal(Video.OverrideMutationKind.Attach, Video.Overrides.Attach(One, Path1, Path1, 100));

        Assert.True(track.HasVideo);                                               // the one probe the whole app takes
        Assert.Equal((uint)TrackFlags.VideoOverride, track.FlagBits & (uint)TrackFlags.VideoOverride);
        Assert.Equal(Path1, Entities.Strings.Resolve(track.LocalVideoId));
        Assert.True(Video.OverrideMirror.Has(Id(One)));
        int indexed = Video.OverrideMirror.Count;
        Assert.Equal(1, indexed);
    }

    [Fact]
    public void A_replace_repoints_the_path_and_keeps_the_bit()
    {
        var track = Stage(Id(One), "Replaced");
        Video.Overrides.Attach(One, Path1, Path1, 100);
        Assert.Equal(Video.OverrideMutationKind.Replace, Video.Overrides.Attach(One, Path2, Path2, 200));

        Assert.True(track.HasVideo);
        Assert.Equal(Path2, Entities.Strings.Resolve(track.LocalVideoId));
    }

    [Fact]
    public void A_detach_clears_the_bit_and_the_path()
    {
        var track = Stage(Id(One), "Detached");
        Video.Overrides.Attach(One, Path1, Path1, 100);
        Assert.True(track.HasVideo);

        Assert.True(Video.Overrides.Remove(One));

        Assert.False(track.HasVideo);
        Assert.Equal(0u, track.FlagBits & (uint)TrackFlags.VideoOverride);
        Assert.True(track.LocalVideoId.IsEmpty);
        Assert.False(Video.OverrideMirror.Has(Id(One)));
    }

    [Fact]
    public void A_detach_leaves_the_catalogues_own_music_video_alone()
    {
        // The two planes are separate bits for exactly this reason (`TrackFlags.VideoMask`): detaching the user's file
        // must not un-say kind 99. `HasVideo` therefore stays true while the OVERRIDE bit goes.
        var track = Stage(Id(Two), "Kind 99 Too", TrackFlags.HasVideo, TrackFields.Identity | TrackFields.Video);
        Video.Overrides.Attach(Two, Path1, Path1, 100);
        Assert.True(Video.Overrides.Remove(Two));

        Assert.True(track.HasVideo);
        Assert.Equal(0u, track.FlagBits & (uint)TrackFlags.VideoOverride);
    }

    // ── and the row that lands afterwards ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_row_committed_after_the_attach_carries_the_bit()
    {
        Video.Overrides.Attach(One, Path1, Path1, 100);
        // The attach must NOT have minted a row: an attachment is curated by URI, and a phantom `TrackTable` row for
        // a playable no page has mentioned is what a `Staging.Slot`-based mirror would have produced.
        Assert.False(Entities.Current.Tracks.TryGetSlot(Id(One), out _));

        var track = Stage(Id(One), "Landed Later");

        Assert.True(track.HasVideo);
        Assert.Equal(Path1, Entities.Strings.Resolve(track.LocalVideoId));
    }

    [Fact]
    public void A_roster_loaded_from_the_store_lights_a_row_with_no_Changed_event_at_all()
    {
        // THE RESTART. `Overrides.Attach(settings)` loads the roster and bumps the epoch but raises no `Changed`, and
        // at boot no track slot exists yet either. The index is rebuilt from the epoch, which is what makes the
        // commit's probe answer for a row staged minutes later.
        _settings.Set(Video.Overrides.StoreKey, One + "\t100\t" + Path1 + "\t" + Path1);
        Video.Overrides.Attach(_settings);
        int loaded = Video.Overrides.Count, indexed = Video.OverrideMirror.Count;
        Assert.Equal(1, loaded);
        Assert.Equal(1, indexed);

        var track = Stage(Id(One), "After A Restart");

        Assert.True(track.HasVideo);
        Assert.Equal(Path1, Entities.Strings.Resolve(track.LocalVideoId));
    }

    [Fact]
    public void A_gid_form_uri_lines_up_with_the_rows_own_identity()
    {
        // The production shape: a catalogue uri parses to the GID form with no intern at all, so the index's key and
        // `Table.Id[slot]` must be the same 24 bytes. A base62 tail that is 22 characters is what makes it the gid form.
        var gid = new byte[16];
        for (int i = 0; i < 16; i++) gid[i] = (byte)(i + 7);
        var id = EntityId.ForGid(EntityKind.Track, gid);
        Span<char> buf = stackalloc char[EntityId.MaxGidTextChars];
        string uri = new(buf[..id.Format(buf)]);

        var track = Stage(id, "A Real Gid");
        Video.Overrides.Attach(uri, Path2, Path2, 300);

        Assert.True(track.HasVideo);
        Assert.Equal(Path2, Entities.Strings.Resolve(track.LocalVideoId));
    }

    // ── what the mirror must NOT do ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_bit_is_not_an_answer_for_the_Video_group()
    {
        // `Bump(slot)`, never `Bump(slot, TrackFields.Video)`. Sealing the group's Known bit would tell `Fetch.NeedOf`
        // the kind-99 association is settled — the catalogue video would never be asked for again — and would make
        // `Album`'s `Knows(TrackFields.Video)` readiness gate read true for a row nobody answered for.
        var track = Stage(Id(One), "Curation Is Not An Answer");
        Video.Overrides.Attach(One, Path1, Path1, 100);

        Assert.True(track.HasVideo);
        Assert.False(track.Knows(TrackFields.Video));
    }

    [Fact]
    public void With_no_roster_attached_nothing_is_mirrored_and_nothing_throws()
    {
        // The feature's kill switch: with no settings store `Overrides.Present` is false, the menu never offers the
        // verb (`OverrideUx.MenuFor` takes exactly that bool) and the index is empty — so the commit's probe returns
        // before it hashes anything and no row is ever touched.
        Video.Overrides.Attach(null);
        Assert.False(Video.Overrides.Present);
        int indexed = Video.OverrideMirror.Count;
        Assert.Equal(0, indexed);

        var track = Stage(Id(Two), "No Roster At All");

        Assert.False(track.HasVideo);
        Assert.True(track.LocalVideoId.IsEmpty);
        Assert.False(Video.OverrideMirror.Has(Id(Two)));
    }
}
