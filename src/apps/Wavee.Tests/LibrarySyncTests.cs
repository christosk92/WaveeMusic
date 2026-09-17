// ── Wavee.Tests/LibrarySyncTests.cs — the library delta sync's drift ledger (gap-fix "library delta sync") ─────────
//
// `LibrarySyncLedger` (Spotify/Spotify.Api.Library.cs) is the pure decision surface behind stage 2's collection-v2
// delta and stage 1's own truncated-walk guard: no Staging, no live tables, no network — every branch is a value in,
// a value out. `Spotify.Decode.LibraryDelta` and `Spotify.Api.SyncTokenOf` are the two raw-byte folds its callers feed
// off, built with the generated `Col.*` messages exactly like `SpotifyApiTests`' own `NextPageToken` test.
//
// The truncated-walk test comes FIRST, per the task's own house rule: the failure mode is silent data loss (0.2.10's
// `CollectionSnapshotLedger` production bug — a truncated snapshot once deleted a user's newest likes and then stored
// the current server revision, so no later delta ever re-shipped them), and that is the one this file must not skip.

using Google.Protobuf;
using System.Linq;
using Wavee;
using Xunit;
using Col = Wavee.Protocol.Collection;

namespace Wavee.Tests;

public class LibrarySyncTests
{
    // ── 1. the truncated walk: never trusted, never a removal ───────────────────────────────────────────────────────

    [Fact]
    public void A_walk_smaller_than_the_ledgers_stored_count_is_never_trustworthy()
    {
        var stored = new LedgerEntry("old-token", 500);

        // The walk came back far short of what was last verified: never trust it to ADVANCE the ledger...
        Assert.False(LibrarySyncLedger.WalkIsTrustworthy(walkedCount: 10, stored));

        // ...which is exactly what forces the NEXT sync back to a full walk instead of a delta: the ledger is left
        // untouched, so the live list (now at 10) no longer matches the stored baseline (500) either, and nothing
        // can build an incremental apply off it. A truncated walk can never masquerade as 490 removals.
        Assert.False(LibrarySyncLedger.BaselineMatches(currentLiveCount: 10, stored));
    }

    [Fact]
    public void A_walk_that_matches_or_beats_the_stored_count_is_trustworthy()
    {
        var stored = new LedgerEntry("old-token", 500);
        Assert.True(LibrarySyncLedger.WalkIsTrustworthy(walkedCount: 500, stored));
        Assert.True(LibrarySyncLedger.WalkIsTrustworthy(walkedCount: 612, stored));
    }

    [Fact]
    public void A_walk_with_nothing_stored_yet_is_always_trustworthy()
        => Assert.True(LibrarySyncLedger.WalkIsTrustworthy(walkedCount: 0, LedgerEntry.Empty));

    // ── 2. delta_update_possible == false falls back to a full walk ─────────────────────────────────────────────────

    [Fact]
    public void Delta_update_not_possible_falls_back_to_a_full_walk()
    {
        var response = new Col.DeltaResponse { DeltaUpdatePossible = false, SyncToken = "server-said-no" };
        response.Items.Add(new Col.CollectionItem { Uri = "spotify:track:a", AddedAt = 1 });

        var items = new List<CollectionDeltaItem>();
        bool possible = Spotify.Decode.LibraryDelta(response.ToByteArray(), items, out string token);

        Assert.False(possible);
        Assert.Single(items);                                  // the items still parse — the CALLER must not use them
        Assert.False(LibrarySyncLedger.CanApplyDelta(possible, token));
    }

    // ── 3. a missing or unparseable token falls back to a full walk ─────────────────────────────────────────────────

    [Fact]
    public void A_delta_answer_with_no_sync_token_falls_back_even_when_possible_is_true()
    {
        var response = new Col.DeltaResponse { DeltaUpdatePossible = true };   // no SyncToken set at all

        var items = new List<CollectionDeltaItem>();
        bool possible = Spotify.Decode.LibraryDelta(response.ToByteArray(), items, out string token);

        Assert.True(possible);
        Assert.Equal("", token);
        Assert.False(LibrarySyncLedger.CanApplyDelta(possible, token));
    }

    [Fact]
    public void A_missing_or_unparseable_stored_token_never_starts_a_delta()
    {
        Assert.True(LibrarySyncLedger.Decode(null).IsEmpty);
        Assert.True(LibrarySyncLedger.Decode("").IsEmpty);
        Assert.True(LibrarySyncLedger.Decode("not-a-ledger-value").IsEmpty);     // no separator to split on
        Assert.True(LibrarySyncLedger.Decode("-3|some-token").IsEmpty);         // a negative count
        Assert.True(LibrarySyncLedger.Decode("abc|some-token").IsEmpty);        // a non-numeric count
    }

    // ── 4. a clean delta applies adds and removes ────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_clean_delta_decodes_adds_and_removes_and_applies_through_the_live_relation()
    {
        var response = new Col.DeltaResponse { DeltaUpdatePossible = true, SyncToken = "tok-2" };
        response.Items.Add(new Col.CollectionItem { Uri = "spotify:track:newlyLiked", AddedAt = 500, IsRemoved = false });
        response.Items.Add(new Col.CollectionItem { Uri = "spotify:track:unliked", IsRemoved = true });

        var items = new List<CollectionDeltaItem>();
        bool possible = Spotify.Decode.LibraryDelta(response.ToByteArray(), items, out string token);
        Assert.True(possible);
        Assert.Equal("tok-2", token);
        Assert.Equal(2, items.Count);
        Assert.Contains(items, i => i.Uri == "spotify:track:newlyLiked" && !i.Removed && i.AddedAt == 500);
        Assert.Contains(items, i => i.Uri == "spotify:track:unliked" && i.Removed);

        // The apply itself, through the SAME commit primitives an optimistic write uses (Entities/Edges.cs) — a fresh
        // table standing in for the live relation, at a baseline of two existing members.
        const int Me = 1, ExistingLiked = 10, ToRemove = 20, ToAdd = 30;
        var table = new EdgeTable<LibraryEdge>();
        table.ReplaceRun(Me, [ExistingLiked, ToRemove], [new LibraryEdge(1, 0), new LibraryEdge(2, 0)]);
        int baselineCount = table.Count(Me);

        foreach (var item in items)
        {
            int slot = item.Uri == "spotify:track:unliked" ? ToRemove : ToAdd;
            if (item.Removed) table.Remove(Me, slot);
            else table.Insert(Me, slot, new LibraryEdge(item.AddedAt, 0), at: 0);
        }

        Assert.Equal(baselineCount, table.Count(Me));          // one out, one in: the count is unchanged
        Assert.True(table.Contains(Me, ExistingLiked));
        Assert.True(table.Contains(Me, ToAdd));
        Assert.False(table.Contains(Me, ToRemove));

        int added = items.Count(i => !i.Removed), removed = items.Count(i => i.Removed);
        Assert.True(LibrarySyncLedger.PostApplyMatches(baselineCount, table.Count(Me), added, removed));
    }

    // ── 5. a post-apply count mismatch discards and re-walks ─────────────────────────────────────────────────────────

    [Fact]
    public void A_post_apply_count_mismatch_is_discarded_rather_than_persisted()
    {
        // The delta said one add, one remove (net zero) — but the "remove" named something the live list had
        // already lost (a duplicate confirmation, a prior drift): applying it is a no-op, so the resulting count
        // does not reconcile with what the delta's own items implied.
        const int baselineCount = 40;
        const int actualNewCountAfterANoOpRemove = 41;         // only the add landed; the remove found nothing to take out

        Assert.False(LibrarySyncLedger.PostApplyMatches(baselineCount, actualNewCountAfterANoOpRemove, added: 1, removed: 1));

        // A clean apply, by contrast, reconciles exactly and is the only case that may persist the new token.
        Assert.True(LibrarySyncLedger.PostApplyMatches(baselineCount, actualNewCount: baselineCount, added: 1, removed: 1));
    }

    // ── 6. SyncTokenOf reads field 3, not NextPageToken's field 2 ────────────────────────────────────────────────────

    [Fact]
    public void SyncTokenOf_reads_field_three_not_the_next_page_token()
    {
        var page = new Col.PageResponse { NextPageToken = "next-page-cursor", SyncToken = "verified-sync-7" };
        page.Items.Add(new Col.CollectionItem { Uri = "spotify:track:a", AddedAt = 1 });

        Assert.Equal("verified-sync-7", Spotify.Api.SyncTokenOf(page.ToByteArray()));
        Assert.Equal("next-page-cursor", Spotify.Api.NextPageToken(page.ToByteArray()));      // a DIFFERENT field

        var noSyncToken = new Col.PageResponse { NextPageToken = "still-paging" };
        Assert.Equal("", Spotify.Api.SyncTokenOf(noSyncToken.ToByteArray()));                 // absent, not garbage
    }

    // ── the ledger's own wire format: what Store.MetaSet/MetaGet actually persists ───────────────────────────────────

    [Fact]
    public void The_ledger_encode_decode_round_trips_token_and_count()
    {
        string stored = LibrarySyncLedger.Encode("abc123", 4200);
        var entry = LibrarySyncLedger.Decode(stored);

        Assert.False(entry.IsEmpty);
        Assert.Equal("abc123", entry.SyncToken);
        Assert.Equal(4200, entry.ItemCount);
    }

    [Fact]
    public void A_token_containing_the_separator_still_round_trips()
    {
        // The token is whatever the server hands back; only the COUNT is parsed as a number, so a token with its own
        // pipe character must not confuse the split (Encode/Decode split on the FIRST separator only).
        string stored = LibrarySyncLedger.Encode("weird|token|with|pipes", 7);
        var entry = LibrarySyncLedger.Decode(stored);

        Assert.False(entry.IsEmpty);
        Assert.Equal(7, entry.ItemCount);
        Assert.Equal("weird|token|with|pipes", entry.SyncToken);
    }

    [Fact]
    public void CollectionMetaKey_keys_liked_and_saved_albums_independently()
    {
        // Liked and SavedAlbums share the wire set "collection" (one walk pays for both), but each relation has its
        // own item count — sharing a meta key would let one relation's baseline silently stand in for the other's.
        string liked = Spotify.Api.CollectionMetaKey("wavee-test", LibraryEdgeKind.Liked);
        string savedAlbums = Spotify.Api.CollectionMetaKey("wavee-test", LibraryEdgeKind.SavedAlbums);

        Assert.NotEqual(liked, savedAlbums);
        Assert.Equal(liked, Spotify.Api.CollectionMetaKey("wavee-test", LibraryEdgeKind.Liked));
    }
}
