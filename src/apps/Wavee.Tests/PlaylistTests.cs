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
            "PlaylistTests/release/rank", "PlaylistTests/release/tuning",
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

        t.ReleaseAllText();                                   // what a retired scope does (Entities.Switch, D9)

        for (int i = 0; i < names.Length; i++)
            Assert.NotEqual(ids[i], Uri(names[i]));
        Assert.True(t.Id[slot].IsEmpty);                      // the identity went back too
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
}
