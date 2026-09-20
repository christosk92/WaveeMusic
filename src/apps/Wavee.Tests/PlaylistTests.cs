// ── Wavee.Tests/PlaylistTests.cs — the playlist row's columns, its unknown-defaults and its derived facts ─────────
//
// Wave 1's gate for Entities/Playlist.cs (plan §5). Everything here runs against a bare `new PlaylistTable()` or a
// pure static: no `Entities.Boot`, no `Scope`, no sqlite (D17). That is not a limitation of the tests — the rules
// worth pinning are the ones that DECIDE something, and each of them was extracted to a pure function precisely so it
// could be pinned here rather than reproduced by a page (CLAUDE.md: "extract the decision into an engine-free pure
// class and unit-test that").
//
// Two families were added on 2026-09-12 with the packed identity
// (docs/plans/wavee/wavee-0.3-entity-identity-memory.md, option 2):
//
//   • THE FOLD (defect 4). `spotify:user:<u>:playlist:<gid>` and `spotify:playlist:<gid>` used to be two rows for one
//     playlist. The facts below pin ONE row, the canonical round trip, and the boundary — a fixture id that is not a
//     22-character gid takes the text form and is NOT folded.
//
//   • THE LIFETIME (defect 1). Nothing in `Entities/` ref-counted a `StringId` before that date, and a never-AddRef'd
//     id is PERMANENT (the engine's `StringTable.cs:26`), so a trim freed the columns and leaked the text. The facts
//     below observe the engine's own contract instead of a counter: the LAST release removes the map entry and ids
//     are never reused, so re-interning the same content afterwards mints a DIFFERENT id. Every string they use is
//     unique to its fact, because the interner is process-wide and this collection is what serialises the tests.

using FluentGpu.Foundation;
using Wavee;
using Xunit;

namespace Wavee.Tests;

[Collection(EntitiesCollection.Name)]
public class PlaylistTests
{
    static StringId Uri(string s) => Entities.Strings.Intern(s);

    /// <summary>A real-shaped gid: 22 base62 characters, distinct per <paramref name="seed"/> (the same helper
    /// <c>EntitiesTests</c> uses — a fixture id like <c>1a2b</c> would take the text form and prove nothing about the
    /// fold).</summary>
    static string Gid(int seed)
    {
        Span<char> buf = stackalloc char[Base62.GidChars];
        Base62.Encode(new UInt128((ulong)seed * 0x9E37_79B9_7F4A_7C15UL, (ulong)seed * 0xC2B2_AE3D_27D4_EB4FUL + 7), buf);
        return new string(buf);
    }

    // ── the three column-existence facts (ch 06 §7, 0.2.9 DetailPage.cs:501-531) ─────────────────────────────────────

    [Fact]
    public void One_adder_is_not_an_added_by_column_and_two_are()
    {
        var one = default(PlaylistFacts);
        one.Add(addedBy: 7, addedAt: 0, durationMs: 1000, isEpisode: false, hasVideo: false);
        one.Add(addedBy: 7, addedAt: 0, durationMs: 1000, isEpisode: false, hasVideo: false);
        Assert.False(one.ManyAdders);
        Assert.Equal(0u, one.Flags & (uint)PlaylistFlags.HasAddedBy);

        var two = default(PlaylistFacts);
        two.Add(addedBy: 7, addedAt: 0, durationMs: 0, isEpisode: false, hasVideo: false);
        two.Add(addedBy: 9, addedAt: 0, durationMs: 0, isEpisode: false, hasVideo: false);
        Assert.True(two.ManyAdders);
        Assert.Equal((uint)PlaylistFlags.HasAddedBy, two.Flags & (uint)PlaylistFlags.HasAddedBy);
    }

    [Fact]
    public void An_unknown_adder_never_counts_towards_distinctness()
    {
        // Slot 0 is "none" everywhere (P3), so a row whose added-by has not resolved to a user row must not make the
        // Added-by column appear — 0.2.9 counted the raw ids and a missing one was a distinct value.
        var facts = default(PlaylistFacts);
        facts.Add(addedBy: 0, addedAt: 0, durationMs: 0, isEpisode: false, hasVideo: false);
        facts.Add(addedBy: 4, addedAt: 0, durationMs: 0, isEpisode: false, hasVideo: false);
        facts.Add(addedBy: 0, addedAt: 0, durationMs: 0, isEpisode: false, hasVideo: false);
        Assert.False(facts.ManyAdders);
    }

    [Fact]
    public void Any_date_and_any_video_arm_their_columns_and_durations_sum()
    {
        var facts = default(PlaylistFacts);
        facts.Add(addedBy: 1, addedAt: 0, durationMs: 200_000, isEpisode: false, hasVideo: false);
        facts.Add(addedBy: 1, addedAt: 1_700_000, durationMs: 100_000, isEpisode: false, hasVideo: true);

        Assert.True(facts.AnyDate);
        Assert.True(facts.AnyVideo);
        Assert.Equal(300_000L, facts.DurationMs);
        uint flags = facts.Flags;
        Assert.Equal((uint)PlaylistFlags.HasDateAdded, flags & (uint)PlaylistFlags.HasDateAdded);
        Assert.Equal((uint)PlaylistFlags.HasVideo, flags & (uint)PlaylistFlags.HasVideo);
    }

    [Fact]
    public void Mixed_is_only_true_when_the_membership_is_actually_mixed()
    {
        // "48 songs · 3 episodes" is the MIXED arm; a podcast-only playlist is not mixed, it is a list of episodes,
        // and 0.2.9's `episodes > 0` test rendered "0 songs · 3 episodes" for it (ch 06 §7).
        var onlyEpisodes = default(PlaylistFacts);
        onlyEpisodes.Add(1, 0, 0, isEpisode: true, hasVideo: false);
        onlyEpisodes.Add(1, 0, 0, isEpisode: true, hasVideo: false);
        Assert.Equal(0u, onlyEpisodes.Flags & (uint)PlaylistFlags.Mixed);
        Assert.Equal(2, onlyEpisodes.Episodes);

        var mixed = default(PlaylistFacts);
        mixed.Add(1, 0, 0, isEpisode: false, hasVideo: false);
        mixed.Add(1, 0, 0, isEpisode: true, hasVideo: false);
        Assert.Equal((uint)PlaylistFlags.Mixed, mixed.Flags & (uint)PlaylistFlags.Mixed);
    }

    // ── the flag merge (the tombstone latch) ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_later_header_can_never_un_delete_a_playlist()
    {
        uint current = (uint)PlaylistFlags.DeletedByOwner | (uint)PlaylistFlags.Public;
        // A full header that claims the list is alive and public, and says it speaks about both bits.
        uint merged = PlaylistTable.MergeFlags(current, incoming: (uint)PlaylistFlags.Public,
            mask: (uint)(PlaylistFlags.DeletedByOwner | PlaylistFlags.Public));
        Assert.Equal((uint)PlaylistFlags.DeletedByOwner, merged & (uint)PlaylistFlags.DeletedByOwner);
    }

    [Fact]
    public void An_answer_only_clears_the_bits_its_mask_claims()
    {
        uint current = (uint)PlaylistFlags.Public | (uint)PlaylistFlags.GenericTitle;
        // A rootlist row knows nothing about visibility: it must not clear Public by omission.
        uint merged = PlaylistTable.MergeFlags(current, incoming: 0, mask: (uint)PlaylistFlags.GenericTitle);
        Assert.Equal((uint)PlaylistFlags.Public, merged);
    }

    [Fact]
    public void A_wire_answer_can_neither_set_nor_clear_the_apps_own_bits()
    {
        uint current = (uint)PlaylistFlags.CreatePending | (uint)PlaylistFlags.HasAddedBy;
        uint merged = PlaylistTable.MergeFlags(current,
            incoming: (uint)PlaylistFlags.HasVideo,                       // the server does not decide our columns
            mask: (uint)(PlaylistFlags.CreatePending | PlaylistFlags.HasAddedBy | PlaylistFlags.HasVideo));
        Assert.Equal(current, merged);
    }

    // ── the unknown-defaults, which differ per capability on purpose ─────────────────────────────────────────────────

    [Fact]
    public void An_unknown_capability_block_is_viewable_and_not_editable()
    {
        Assert.True(Playlist.CanViewOf(knowsCaps: false, PlaylistCaps.None));      // never "revoked"
        Assert.False(Playlist.EditableOf(knowsCaps: false, PlaylistCaps.CanEditItems));
        Assert.False(Playlist.EditableMetadataOf(knowsCaps: false, PlaylistCaps.CanEditMetadata));
    }

    [Fact]
    public void A_known_block_is_taken_at_its_word_in_both_directions()
    {
        Assert.False(Playlist.CanViewOf(knowsCaps: true, PlaylistCaps.None));
        Assert.True(Playlist.CanViewOf(knowsCaps: true, PlaylistCaps.CanView));
        Assert.True(Playlist.EditableOf(knowsCaps: true, PlaylistCaps.CanView | PlaylistCaps.CanEditItems));
        Assert.False(Playlist.EditableOf(knowsCaps: true, PlaylistCaps.CanEditMetadata));
    }

    [Fact]
    public void Visibility_is_public_until_permission_base_says_otherwise()
    {
        Assert.True(Playlist.IsPublicOf(knowsVisibility: false, flags: 0));
        Assert.False(Playlist.IsPublicOf(knowsVisibility: true, flags: 0));
        Assert.True(Playlist.IsPublicOf(knowsVisibility: true, flags: (uint)PlaylistFlags.Public));
    }

    // ── the list state (0.2.9 PlaylistListState, ported) ─────────────────────────────────────────────────────────────

    [Fact]
    public void An_unknown_membership_shimmers_and_a_known_empty_one_says_so()
    {
        Assert.True(Playlist.IsLoading(membershipKnown: false, total: 0));
        Assert.Equal(PlaylistRowsState.Loading, Playlist.RowsStateOf(false, 0, 0));
        Assert.Equal(PlaylistRowsState.Empty, Playlist.RowsStateOf(true, 0, 0));
    }

    [Fact]
    public void Rows_are_proof_whatever_the_flag_says()
    {
        Assert.False(Playlist.IsLoading(membershipKnown: false, total: 12));
        Assert.Equal(PlaylistRowsState.Rows, Playlist.RowsStateOf(false, 12, 12));
        Assert.Equal(PlaylistRowsState.NoMatch, Playlist.RowsStateOf(true, 12, 0));
    }

    [Theory]
    [InlineData(PlaylistRowsState.Loading, "Loading")]
    [InlineData(PlaylistRowsState.Empty, "Empty")]
    [InlineData(PlaylistRowsState.NoMatch, "NoMatch")]
    [InlineData(PlaylistRowsState.Rows, "Rows")]
    public void The_diagnostics_spelling_is_pinned(PlaylistRowsState state, string name)
        => Assert.Equal(name, Playlist.NameOf(state));

    // ── the table (columns, groups, the authority ladder) ────────────────────────────────────────────────────────────

    [Fact]
    public void Every_column_grows_with_the_table()
    {
        var t = new PlaylistTable();
        int slot = 0;
        for (int i = 0; i < 40; i++) slot = t.Alloc(Uri($"spotify:playlist:{i}"));

        // Writing the last column of the last row is the only honest check that GrowColumns lists them all.
        t.DurationMs[slot] = 123;
        t.TuningRevision[slot] = 7;
        t.EpisodeCount[slot] = 3;
        Assert.Equal(123L, t.DurationMs[slot]);
        Assert.Equal(40, t.LiveCount);
    }

    [Fact]
    public void A_thin_answer_fills_a_group_nobody_filled_and_never_overwrites_a_full_one()
    {
        var t = new PlaylistTable();
        int slot = t.Alloc(Uri("spotify:playlist:37i9"));

        // A full header lands identity. Through SetText, which is the ONLY sanctioned write to a text column: it
        // AddRefs the incoming id and releases the one it overwrites (defect 1).
        Assert.True(t.Accepts(slot, (uint)PlaylistFields.Identity, Authority.Full, in t.IdentityAuthority));
        t.SetText(ref t.Title, slot, Uri("Discover Weekly"));
        t.Applied(slot, (uint)PlaylistFields.Identity, Authority.Full, ref t.IdentityAuthority);

        // A rootlist row (Thin) may not degrade it…
        Assert.False(t.Accepts(slot, (uint)PlaylistFields.Identity, Authority.Thin, in t.IdentityAuthority));
        // …but the same Thin answer may fill the SAVES group, which nobody has filled (D16).
        Assert.True(t.Accepts(slot, (uint)PlaylistFields.Saves, Authority.Thin, in t.ExtrasAuthority));

        Assert.True(t.Knows(slot, (uint)PlaylistFields.Identity));
        Assert.False(t.Knows(slot, (uint)PlaylistFields.Capabilities));
    }

    [Fact]
    public void The_seed_writes_below_every_wire_answer()
    {
        var t = new PlaylistTable();
        int slot = t.Alloc(Uri("fake:playlist:pl7"));
        t.Applied(slot, (uint)PlaylistFields.Identity, Authority.Seed, ref t.IdentityAuthority);

        Assert.True(t.Accepts(slot, (uint)PlaylistFields.Identity, Authority.Thin, in t.IdentityAuthority));
        Assert.False(t.Accepts(slot, (uint)PlaylistFields.Identity, Authority.None, in t.IdentityAuthority));
    }

    // ── the local-files surface (plan §9.5) ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_local_files_uri_parses_as_a_local_playlist_with_no_special_case()
    {
        var provider = EntityUri.ProviderOf(Playlist.LocalFilesUri.AsSpan(), out var kind);
        Assert.Equal(EntityProvider.Local, provider);
        Assert.Equal(EntityKind.Playlist, kind);
    }

    // ── defect 4: two spellings, ONE playlist row ────────────────────────────────────────────────────────

    [Fact]
    public void The_user_namespaced_spelling_and_the_bare_one_are_the_same_row()
    {
        // 0.2.9 interned the two spellings to two `StringId`s and allocated TWO rows for ONE playlist: two headers,
        // two membership edges, two fetches, and whichever one the sidebar held was the one that did not get the
        // answer (doc §4.4). The id is the uri's TRAILING segment, so both decode to the same gid.
        var t = new PlaylistTable();
        string gid = Gid(101);
        int bare = t.Slot($"spotify:playlist:{gid}".AsSpan());
        int namespaced = t.Slot($"spotify:user:christos:playlist:{gid}".AsSpan());

        Assert.Equal(bare, namespaced);
        Assert.Equal(1, t.LiveCount);
        Assert.Equal(EntityForm.Gid, t.Id[bare].Form);
        Assert.Equal(EntityKind.Playlist, t.Id[bare].Kind);
        Assert.Equal(EntityProvider.Spotify, t.Id[bare].Provider);
    }

    [Fact]
    public void The_fold_survives_whichever_spelling_arrives_first()
    {
        // The order matters as much as the fold: the namespaced form is what Home and recents carried in 0.2.9, so it
        // is frequently the one that allocates the row, and the bare form has to find it.
        var t = new PlaylistTable();
        string gid = Gid(102);
        int first = t.Slot($"spotify:user:someone:playlist:{gid}".AsSpan());
        Assert.Equal(first, t.Slot($"spotify:playlist:{gid}".AsSpan()));
        Assert.True(t.TryGetSlot($"spotify:playlist:{gid}".AsSpan(), out int found));
        Assert.Equal(first, found);
        Assert.Equal(1, t.LiveCount);
    }

    [Fact]
    public void A_round_trip_answers_the_canonical_spelling()
    {
        // Formatting a gid-form id reproduces `spotify:playlist:<gid>` whichever spelling built it. That is the
        // decision written down (file header item 5): Connect and deep links accept the canonical form.
        var t = new PlaylistTable();
        string gid = Gid(103);
        int slot = t.Slot($"spotify:user:christos:playlist:{gid}".AsSpan());
        Assert.Equal($"spotify:playlist:{gid}", t.Id[slot].Text);
    }

    [Fact]
    public void An_id_that_is_not_a_gid_is_not_folded_and_the_boundary_is_deliberate()
    {
        // It is the GID that folds, not the parser. `1a2b` is not 22 base62 characters, so both spellings take the
        // TEXT form and two different strings are two different rows — pinned so the boundary is a decision rather
        // than a surprise the first fixture trips over.
        var t = new PlaylistTable();
        int bare = t.Slot("spotify:playlist:1a2b".AsSpan());
        int namespaced = t.Slot("spotify:user:christos:playlist:1a2b".AsSpan());

        Assert.NotEqual(bare, namespaced);
        Assert.Equal(EntityForm.Text, t.Id[bare].Form);
        Assert.Equal(EntityKind.Playlist, t.Id[bare].Kind);
    }

    [Fact]
    public void A_playlist_row_is_found_by_the_gid_the_wire_hands_us()
    {
        // The 3 ns door: a decoder holds 16 raw bytes, never a uri. It must reach the SAME row the uri text allocated.
        var t = new PlaylistTable();
        string gid = Gid(104);
        int viaText = t.Slot($"spotify:playlist:{gid}".AsSpan());

        Span<byte> raw = stackalloc byte[Base62.GidBytes];
        Assert.True(Base62.TryDecode(gid.AsSpan(), out UInt128 value));
        Base62.WriteBytes(value, raw);

        Assert.Equal(viaText, t.Slot(EntityKind.Playlist, raw));
        Assert.Equal(1, t.LiveCount);
    }

    // ── defect 1: the row owns its text, and gives it back ─────────────────────────────────────────────

    [Fact]
    public void A_freed_row_hands_its_title_back_to_the_interner()
    {
        var t = new PlaylistTable();
        int slot = t.Alloc(EntityId.Parse($"spotify:playlist:{Gid(105)}".AsSpan()));
        StringId title = Uri("PlaylistTests/freed/title");
        t.SetText(ref t.Title, slot, title);

        t.FreeSlot(slot);

        // The last release dropped the map entry, and ids are never reused: the same content interns to a NEW id.
        Assert.NotEqual(title, Uri("PlaylistTests/freed/title"));
    }

    [Fact]
    public void Overwriting_a_text_column_releases_the_string_it_replaced()
    {
        // The half that is easy to forget: a header re-answered a hundred times must own ONE title at the end of it,
        // not a hundred. `SetText` AddRefs the incoming id BEFORE releasing the outgoing one, so writing the same id
        // twice is a no-op rather than a reclaim-then-resurrect.
        var t = new PlaylistTable();
        int slot = t.Alloc(EntityId.Parse($"spotify:playlist:{Gid(106)}".AsSpan()));
        StringId first = Uri("PlaylistTests/overwrite/first");
        t.SetText(ref t.Title, slot, first);
        t.SetText(ref t.Title, slot, Uri("PlaylistTests/overwrite/second"));

        Assert.NotEqual(first, Uri("PlaylistTests/overwrite/first"));
        Assert.Equal("PlaylistTests/overwrite/second", Entities.Strings.Resolve(t.Title[slot]));

        // …and re-writing the SAME id keeps it alive.
        StringId same = t.Title[slot];
        t.SetText(ref t.Title, slot, same);
        Assert.Equal("PlaylistTests/overwrite/second", Entities.Strings.Resolve(t.Title[slot]));
    }

    [Fact]
    public void Every_text_column_of_a_playlist_row_is_listed_in_release_text()
    {
        // THE gate for the mechanical half of defect 1: a column missing from `PlaylistTable.ReleaseText` leaks its
        // text for the life of the process, and nothing else in the suite would notice. One assertion per column.
        var t = new PlaylistTable();
        int slot = t.Alloc(EntityId.Parse($"spotify:playlist:{Gid(107)}".AsSpan()));

        string[] names =
        [
            "PlaylistTests/release/title", "PlaylistTests/release/description", "PlaylistTests/release/image",
            "PlaylistTests/release/permission", "PlaylistTests/release/header", "PlaylistTests/release/generic",
            "PlaylistTests/release/rank", "PlaylistTests/release/tuning", "PlaylistTests/release/revision",
        ];
        StringId[] ids = new StringId[names.Length];
        for (int i = 0; i < names.Length; i++) ids[i] = Uri(names[i]);

        t.SetText(ref t.Title, slot, ids[0]);
        t.SetText(ref t.Description, slot, ids[1]);
        t.SetText(ref t.Image, slot, ids[2]);
        t.SetText(ref t.PermissionRevision, slot, ids[3]);
        t.SetText(ref t.HeaderImage, slot, ids[4]);
        t.SetText(ref t.GenericTitle, slot, ids[5]);
        t.SetText(ref t.ChartRankType, slot, ids[6]);
        t.SetText(ref t.TuningSelected, slot, ids[7]);
        t.SetText(ref t.Revision, slot, ids[8]);              // Wave 5 (owner O): the membership revision

        t.ReleaseAllText();                                   // what a retired scope does (Entities.Switch, D9)

        for (int i = 0; i < names.Length; i++)
            Assert.NotEqual(ids[i], Uri(names[i]));
        Assert.False(t.Id[slot].IsEmpty);                     // a GID identity owns no text and stays: Playback.Rebind reads it (G-241)
        Assert.Equal(0, t.TextRows);
    }

    [Fact]
    public void A_text_form_identity_is_released_with_its_row()
    {
        // A `wavee:playlist:*` row is the TEXT form — it keeps a string, and that string is the 158 B/row the gid
        // form removes and the text form must therefore hand back (doc §1.2, §6 "not solved").
        var t = new PlaylistTable();
        StringId uri = Uri("wavee:playlist:PlaylistTests-session-one");
        int slot = t.Alloc(uri);
        Assert.Equal(1, t.TextRows);

        t.FreeSlot(slot);

        Assert.Equal(0, t.TextRows);
        Assert.NotEqual(uri, Uri("wavee:playlist:PlaylistTests-session-one"));
    }

    [Fact]
    public void A_gid_row_keeps_no_uri_text_at_all()
    {
        // The whole point of the packed identity: 10,000 catalog rows used to cost 158 B of interned uri each
        // (doc §1.2). A gid row has no string anywhere in the process.
        var t = new PlaylistTable();
        for (int i = 0; i < 16; i++) t.Slot($"spotify:playlist:{Gid(200 + i)}".AsSpan());
        Assert.Equal(16, t.LiveCount);
        Assert.Equal(0, t.TextRows);
        Assert.Equal(16, t.IndexedRows);
    }

    // ══ Wave 5 (owner O, WP-5.O stream A): the edit gate, the commit-time facts, tuning, recs, collaborators ═══════════

    static Playlist Row(int seed) => Entities.Playlist(EntityId.Parse($"spotify:playlist:{Gid(seed)}".AsSpan()));

    static void Stage(int seed, PlaylistCaps caps, uint known = (uint)(PlaylistFields.Identity | PlaylistFields.Capabilities),
                      uint flags = 0, uint mask = 0, string? owner = null)
    {
        var s = Staging.Rent();
        ref var row = ref s.Playlists.RowFor(new StagedId(EntityId.Parse($"spotify:playlist:{Gid(seed)}".AsSpan())), Authority.Full, known);
        row.Title = s.Text("PlaylistTests/w5/" + seed);
        row.Caps = (byte)caps;
        row.Flags = flags;
        row.FlagsMask = mask;
        if (owner is not null) row.OwnerUri = new StagedId(s.Text(owner));
        TestScope.CommitAndPublish(s);
    }

    [Fact]
    public void A_notice_takes_every_edit_affordance_away_in_the_same_read()
    {
        // ch 06 §0.2: the trio. A playlist deleted under the reader keeps the capabilities it loaded with.
        TestScope.Fresh();
        Stage(300, PlaylistCaps.CanView | PlaylistCaps.CanEditItems | PlaylistCaps.CanEditMetadata);
        var p = Row(300);
        Assert.True(p.Editable && p.EditableMetadata && p.Live);

        Stage(300, PlaylistCaps.CanView | PlaylistCaps.CanEditItems | PlaylistCaps.CanEditMetadata,
              flags: (uint)PlaylistFlags.DeletedByOwner, mask: (uint)PlaylistFlags.DeletedByOwner);
        Assert.Equal(DetailNotice.Deleted, p.Notice);
        Assert.False(p.Editable);
        Assert.False(p.EditableMetadata);
        Assert.False(p.Live);
    }

    [Fact]
    public void The_notice_column_is_written_at_commit_by_the_sticky_rule()
    {
        TestScope.Fresh();
        // Known caps, no view, not the owner ⇒ revoked.
        Stage(301, PlaylistCaps.None);
        Assert.Equal(DetailNotice.AccessRevoked, Row(301).Notice);
        // A thin header (no capability block in THIS answer, but the row still knows its caps) keeps the verdict.
        Stage(301, PlaylistCaps.None, known: (uint)PlaylistFields.Identity);
        Assert.Equal(DetailNotice.AccessRevoked, Row(301).Notice);
        // …and a later block that grants view clears it.
        Stage(301, PlaylistCaps.CanView);
        Assert.Equal(DetailNotice.None, Row(301).Notice);

        // An owner is never revoked from their own list (administration implies ownership, 0.2.9 CapabilitiesOf).
        Stage(302, PlaylistCaps.CanAdministratePermissions);
        Assert.True(Row(302).IsOwner);
        Assert.Equal(DetailNotice.None, Row(302).Notice);

        // CreateFailed is terminal: no later answer relabels it.
        var failed = Row(303);
        failed.MarkCreatePending();
        failed.SettleCreate(ok: false);
        Stage(303, PlaylistCaps.CanView | PlaylistCaps.CanEditItems);
        Assert.Equal(DetailNotice.CreateFailed, Row(303).Notice);
    }

    [Fact]
    public void Edits_are_live_only_for_a_signed_in_spotify_account()
    {
        Assert.True(Playlist.SpotifyEditsLiveOf(accountScope: true, online: true));
        Assert.False(Playlist.SpotifyEditsLiveOf(accountScope: true, online: false));
        Assert.False(Playlist.SpotifyEditsLiveOf(accountScope: false, online: true));   // --fake: item 58
    }

    [Fact]
    public void ApplyTuning_replaces_the_whole_list_and_owns_its_strings()
    {
        TestScope.Fresh();
        var p = Row(310);
        StringId id = Uri("PlaylistTests/tune/choice"), label = Uri("PlaylistTests/tune/label"), selected = Uri("PlaylistTests/tune/selected");
        p.ApplyTuning([new TuningEdge(id, label, (byte)TuningOptionKind.Choice), new TuningEdge(Uri("PlaylistTests/tune/reset"), default, (byte)TuningOptionKind.Reset)],
                      selected, 42);

        Assert.True(p.Knows(PlaylistFields.Tuning));
        Assert.Equal(2, p.TuningOptions.Length);
        Assert.Equal(42u, p.TuningRevision);
        Assert.Equal("PlaylistTests/tune/selected", Entities.Strings.Resolve(p.TuningSelectedId));
        Assert.True(p.TuningCurrent);                          // no membership revision held ⇒ never a mismatch

        p.ApplyTuning(ReadOnlySpan<TuningEdge>.Empty, StringId.Empty, 0);
        Assert.True(p.TuningOptions.IsEmpty);
        Assert.Equal(EdgeState.Complete, Entities.Current.Edges.PlaylistTuning.State(p.Slot));
        // The replaced options were released: their last reference is gone, so the content re-interns to a NEW id.
        Assert.NotEqual(id, Uri("PlaylistTests/tune/choice"));
        Assert.NotEqual(label, Uri("PlaylistTests/tune/label"));
    }

    [Fact]
    public void Recommendations_are_a_whole_relation_write()
    {
        TestScope.Fresh();
        var p = Row(311);
        Assert.Equal(EdgeState.Unknown, p.RecommendationsState);
        int a = Entities.Track(EntityId.Parse($"spotify:track:{Gid(312)}".AsSpan())).Slot;
        int b = Entities.Track(EntityId.Parse($"spotify:track:{Gid(313)}".AsSpan())).Slot;
        p.ApplyRecommendations([a, b]);
        Assert.Equal([a, b], p.RecommendationSlots.ToArray());
        Assert.Equal(EdgeState.Complete, p.RecommendationsState);
        p.ApplyRecommendations([]);
        Assert.True(p.RecommendationSlots.IsEmpty);
    }

    [Fact]
    public void Collaborators_are_the_owner_then_distinct_adders_in_first_seen_order()
    {
        TestScope.Fresh();
        Stage(320, PlaylistCaps.CanView, owner: "spotify:user:PlaylistTests-owner");
        int owner = Entities.User(EntityId.Parse("spotify:user:PlaylistTests-owner".AsSpan())).Slot;
        int mia = Entities.User(EntityId.Parse("spotify:user:PlaylistTests-mia".AsSpan())).Slot;
        int alex = Entities.User(EntityId.Parse("spotify:user:PlaylistTests-alex".AsSpan())).Slot;
        var p = Row(320);
        int t = Entities.Track(EntityId.Parse($"spotify:track:{Gid(321)}".AsSpan())).Slot;
        p.ApplyMembership([t, t, t, t],
            [new PlaylistTrackEdge(default, 1, mia, 0, 0, 0, 0), new PlaylistTrackEdge(default, 2, owner, 0, 0, 0, 0),
             new PlaylistTrackEdge(default, 3, alex, 0, 0, 0, 0), new PlaylistTrackEdge(default, 4, mia, 0, 0, 0, 0)]);

        Span<int> into = stackalloc int[8];
        int n = p.CollaboratorSlots(into);
        Assert.Equal([owner, mia, alex], into[..n].ToArray());
        Assert.Equal(2, p.CollaboratorSlots(into[..2]));      // bounded by the caller's buffer
    }

    [Fact]
    public void Refold_derives_the_column_facts_from_the_rows_and_writes_only_when_they_moved()
    {
        TestScope.Fresh();
        var p = Row(330);
        var s = Staging.Rent();
        ref var track = ref s.Tracks.RowFor(new StagedId(EntityId.Parse($"spotify:track:{Gid(331)}".AsSpan())), Authority.Full, (uint)TrackFields.Duration);
        track.DurationMs = 200_000;
        TestScope.CommitAndPublish(s);
        int t = Entities.Track(EntityId.Parse($"spotify:track:{Gid(331)}".AsSpan())).Slot;
        p.ApplyMembership([t, t], [new PlaylistTrackEdge(default, 1_700_000_000, 7, 0, 0, 0, 0), new PlaylistTrackEdge(default, 0, 9, 0, 0, 0, 0)]);

        Assert.True(p.Refold());
        Assert.True(p.HasDateAddedColumn);
        Assert.True(p.HasAddedByColumn);
        Assert.Equal(400_000, p.DurationMs);
        uint version = p.Version;
        Assert.False(p.Refold());                              // nothing moved: no write, no bump, no publish loop
        Assert.Equal(version, p.Version);
    }

    [Fact]
    public void The_revision_hash_is_stable_and_empty_is_zero()
    {
        Assert.Equal(0u, Playlist.RevisionHash(""));
        Assert.Equal(Playlist.RevisionHash("7,abcdef"), Playlist.RevisionHash("7,abcdef"));
        Assert.NotEqual(Playlist.RevisionHash("7,abcdef"), Playlist.RevisionHash("8,abcdef"));
    }

    // ══ W3-A3 (the value gates): the page stamp, the page rules, the facts fold ═══════════════════════════════════════

    [Theory]
    [InlineData(false, true, true, true, EdgeState.Unknown, true)]     // live edit path: the section mounts before the edge answers
    [InlineData(false, true, true, false, EdgeState.Complete, true)]   // signed out, but a landed batch stays
    [InlineData(false, true, true, false, EdgeState.Unknown, false)]   // signed out and unanswered (item 58: --fake)
    [InlineData(true, true, true, true, EdgeState.Complete, false)]    // Local Files never recommends
    [InlineData(false, false, true, true, EdgeState.Complete, false)]  // a read-only playlist has nothing to add to
    [InlineData(false, true, false, true, EdgeState.Complete, false)]  // not Spotify's
    public void Recommendations_mount_for_an_editable_spotify_playlist_with_a_live_path_or_a_landed_edge(
        bool local, bool editable, bool spotify, bool editsLive, EdgeState recommendations, bool expected)
        => Assert.Equal(expected, Playlist.PageRules.ShowsRecommendations(local, editable, spotify, editsLive, recommendations));

    [Fact]
    public void The_profile_and_slots_masks_key_on_every_input_and_nothing_else()
    {
        int baseline = Playlist.PageRules.ProfileMask(false, false, false, false, 0f);
        Assert.NotEqual(baseline, Playlist.PageRules.ProfileMask(true, false, false, false, 0f));
        Assert.NotEqual(baseline, Playlist.PageRules.ProfileMask(false, true, false, false, 0f));
        Assert.NotEqual(baseline, Playlist.PageRules.ProfileMask(false, false, true, false, 0f));
        Assert.NotEqual(baseline, Playlist.PageRules.ProfileMask(false, false, false, true, 0f));
        Assert.NotEqual(baseline, Playlist.PageRules.ProfileMask(false, false, false, false, User.LensExtent));
        // The four flags and the lens extent occupy disjoint bits: no two inputs can cancel each other out.
        Assert.Equal(1 | 2 | 4 | 8 | ((int)User.LensExtent << 4),
                     Playlist.PageRules.ProfileMask(true, true, true, true, User.LensExtent));
        Assert.Equal(baseline, Playlist.PageRules.ProfileMask(false, false, false, false, 0f));   // a pure function of its inputs

        Assert.Equal(0, Playlist.PageRules.SlotsMask(false, false, false));
        Assert.Equal(1, Playlist.PageRules.SlotsMask(true, false, false));
        Assert.Equal(2, Playlist.PageRules.SlotsMask(false, true, false));
        Assert.Equal(4, Playlist.PageRules.SlotsMask(false, false, true));
    }

    [Fact]
    public void The_page_stamp_moves_on_each_field_the_page_paints_and_on_nothing_else()
    {
        // The gate is the memo's value equality: two stamps over the same inputs must compare equal (the render is
        // skipped), and every field the page or its identity reads must move it.
        var a = new Playlist.PageStamp(1, 7, 3, EdgeState.Complete, 5, RowFold.Add(RowFold.Seed, 9), 11, 2, EdgeState.Unknown, 1, false, true, false, 0, false, 0, false);
        Assert.Equal(a, new Playlist.PageStamp(1, 7, 3, EdgeState.Complete, 5, RowFold.Add(RowFold.Seed, 9), 11, 2, EdgeState.Unknown, 1, false, true, false, 0, false, 0, false));
        Assert.NotEqual(a, a with { Row = 4 });                                    // a rename, a new cover, new caps
        Assert.NotEqual(a, a with { Membership = EdgeState.Partial });             // "durations known" arm of the meta
        Assert.NotEqual(a, a with { MembersEdge = 6 });                            // a row landed / moved
        Assert.NotEqual(a, a with { Members = RowFold.Add(RowFold.Seed, 10) });    // a member's duration arrived
        Assert.NotEqual(a, a with { Owner = 3 });                                  // the owner's name / avatar
        Assert.NotEqual(a, a with { Recommendations = EdgeState.Complete });       // the Recommended section's mount
        Assert.NotEqual(a, a with { Tuning = 2 });                                 // the Tune affordance
        Assert.NotEqual(a, a with { EditsLive = true });                           // the session came online
        Assert.NotEqual(a, a with { Holding = true });
        Assert.NotEqual(a, a with { Facts = false });                              // the bento's presence
        Assert.NotEqual(a, a with { Epoch = 2 });                                  // a scope switch
        Assert.NotEqual(a, a with { TrackCount = 12 });                            // IdentityOf's count
        Assert.NotEqual(a, a with { CountKnown = true });
        Assert.NotEqual(a, a with { Saves = 4 });
        Assert.NotEqual(a, a with { SavesKnown = true });
    }

    [Fact]
    public void The_facts_fold_of_a_playlist_source_moves_with_its_members_and_not_with_a_stranger()
    {
        TestScope.Fresh();
        var p = Row(340);
        int member = StageTrack(341, 200_000);
        int stranger = StageTrack(342, 100_000);
        p.ApplyMembership([member], [new PlaylistTrackEdge(default, 1_700_000_000, 0, 0, 0, 0, 0)]);
        var src = Track.TableSource.ForPlaylist(p);

        ulong before = User.FactsInputFold(src);
        Assert.Equal(before, User.FactsInputFold(src));                            // a pure function of the tables

        StageTrack(342, 110_000);                                                  // a stranger's row moves…
        Assert.Equal(before, User.FactsInputFold(src));                            // …and the fold does not

        StageTrack(341, 210_000);                                                  // a member's row moves
        ulong afterRow = User.FactsInputFold(src);
        Assert.NotEqual(before, afterRow);

        p.ApplyMembership([member], [new PlaylistTrackEdge(default, 1_700_000_100, 0, 0, 0, 0, 0)]);   // the added-at moves
        Assert.NotEqual(afterRow, User.FactsInputFold(src));

        var one = new User.FactsSettleGate(p.Uri, afterRow, 42);
        Assert.Equal(one, new User.FactsSettleGate(p.Uri, afterRow, 42));
        Assert.NotEqual(one, one with { HourTicks = 43 });                        // the week windows are drawn against the hour
        Assert.NotEqual(one, one with { Fold = before });
        _ = stranger;
    }

    // ══ the daylist edition gate: a later window outranks whoever wrote the earlier one ═══════════════════════════════

    /// <summary>Commit one header row the way the feed or PlaylistRead would: <paramref name="known"/> picks the groups,
    /// <paramref name="windowEndS"/> is the daylist window's end (0 = no window staged).</summary>
    static void StageEdition(int seed, Authority authority, uint known, string title, int windowEndS,
                             string header = "", string generic = "")
    {
        var s = Staging.Rent();
        ref var row = ref s.Playlists.RowFor(new StagedId(EntityId.Parse($"spotify:playlist:{Gid(seed)}".AsSpan())), authority, known);
        row.Title = s.Text(title);
        row.DaylistExpiresAt = windowEndS;
        row.DaylistCreatedAt = windowEndS == 0 ? 0 : windowEndS - 3600;
        row.Format = (byte)PlaylistFormat.Daylist;
        row.HeaderImage = s.Text(header);
        row.GenericTitle = s.Text(generic);
        TestScope.CommitAndPublish(s);
    }

    static string Title(Playlist p) => Entities.Strings.Resolve(p.TitleId);

    const uint IdentityAndDaylist = (uint)(PlaylistFields.Identity | PlaylistFields.Daylist);
    const uint EditionGroups = (uint)(PlaylistFields.Identity | PlaylistFields.Format | PlaylistFields.Daylist);

    [Fact]
    public void A_later_daylist_window_lets_a_thin_row_retitle_a_full_one()
    {
        TestScope.Fresh();
        StageEdition(320, Authority.Full, IdentityAndDaylist, "PlaylistTests/edition/T1", windowEndS: 1_000);
        var p = Row(320);
        Assert.Equal("PlaylistTests/edition/T1", Title(p));
        Assert.Equal(1_000, p.DaylistExpiresAt);

        // The feed's Thin row for the NEXT edition: a plain Thin write would lose to the Full one, the window wins.
        StageEdition(320, Authority.Thin, IdentityAndDaylist, "PlaylistTests/edition/T2", windowEndS: 2_000);
        Assert.Equal("PlaylistTests/edition/T2", Title(p));
        Assert.Equal(2_000, p.DaylistExpiresAt);
    }

    [Fact]
    public void An_earlier_or_windowless_thin_row_never_overwrites_the_held_edition()
    {
        // The held edition was written Full (PlaylistRead of W2), so the plain D16 rule applies to anything that
        // is not a NEWER window: Thin loses to Full.
        TestScope.Fresh();
        StageEdition(321, Authority.Full, IdentityAndDaylist, "PlaylistTests/edition/T2", windowEndS: 2_000);
        var p = Row(321);
        Assert.Equal("PlaylistTests/edition/T2", Title(p));

        // A stale card still carrying the OLD window: not a new edition, rejected.
        StageEdition(321, Authority.Thin, IdentityAndDaylist, "PlaylistTests/edition/T1-again", windowEndS: 1_000);
        Assert.Equal("PlaylistTests/edition/T2", Title(p));
        Assert.Equal(2_000, p.DaylistExpiresAt);

        // The held window but WITHOUT the Daylist bit: the row does not speak for the window, so no edition claim.
        StageEdition(321, Authority.Thin, (uint)PlaylistFields.Identity, "PlaylistTests/edition/T3", windowEndS: 2_000);
        Assert.Equal("PlaylistTests/edition/T2", Title(p));
    }

    [Fact]
    public void A_format_row_without_a_masthead_keeps_the_held_one_unless_it_is_a_new_edition()
    {
        TestScope.Fresh();
        StageEdition(322, Authority.Full, EditionGroups, "PlaylistTests/edition/T1", windowEndS: 1_000,
                     header: "PlaylistTests/edition/header1", generic: "PlaylistTests/edition/generic1");
        var p = Row(322);
        Assert.Equal("PlaylistTests/edition/header1", Entities.Strings.Resolve(p.HeaderImageId));
        Assert.Equal("PlaylistTests/edition/generic1", Entities.Strings.Resolve(p.GenericTitleId));

        // PlaylistRead's shape: the Format byte alone, no header, no generic title, no window. The masthead stays.
        StageEdition(322, Authority.Full, (uint)PlaylistFields.Format, "PlaylistTests/edition/unused", windowEndS: 0);
        Assert.Equal(PlaylistFormat.Daylist, p.Format);
        Assert.Equal("PlaylistTests/edition/header1", Entities.Strings.Resolve(p.HeaderImageId));
        Assert.Equal("PlaylistTests/edition/generic1", Entities.Strings.Resolve(p.GenericTitleId));

        // A new edition with no masthead clears both: the old one belongs to the wrong edition.
        StageEdition(322, Authority.Thin, EditionGroups, "PlaylistTests/edition/T2", windowEndS: 2_000);
        Assert.Equal("PlaylistTests/edition/T2", Title(p));
        Assert.True(p.HeaderImageId.IsEmpty);
        Assert.True(p.GenericTitleId.IsEmpty);
    }

    /// <summary>Commit one track row with a duration (a second call with a different duration bumps its version).</summary>
    static int StageTrack(int seed, int durationMs)
    {
        var s = Staging.Rent();
        ref var track = ref s.Tracks.RowFor(new StagedId(EntityId.Parse($"spotify:track:{Gid(seed)}".AsSpan())), Authority.Full, (uint)TrackFields.Duration);
        track.DurationMs = durationMs;
        TestScope.CommitAndPublish(s);
        return Entities.Track(EntityId.Parse($"spotify:track:{Gid(seed)}".AsSpan())).Slot;
    }
}
