// ── Spotify/Spotify.Api.Album.cs ───────────────────────────────────────────────────────────────────────────────────
// the album page's requests: the pre-save resolve (kind 138), and the three pathfinder answers the album edges need
//
// Role: SHELL (each call blocks on an api thread) + the UI-thread landing of the pre-save resolve
// Owner: M (Wave 5, stream C of WP-5.M)
// Wave: 5
// Budget: 160 lines (the WP-5 "Spotify.Api.<Area>.cs" row; no plan §2 line of its own)
// Spec: ch 05 §6 (CTA cluster), §7 (the pre-save row, "who asks for what"), DATA GAPS D4/D5/D8 · gap register G-045,
//   G-089 (the pre-save half), G-232 (the album half)
//
// THE PRE-SAVE RESOLVE (`Controls.PreSave`). A card knows the ALBUM; the collection write only accepts the
// `spotify:prerelease:` entity. One hop: kind 138 through the ONE metadata door (`Api.PreRelease`, the same
// `Extension` → `Decode.ExtendedMetadata` path every trait takes) on an api thread, the staged answer committed on the
// UI thread through `Spotify.Post` exactly as `Fetch.Answer` commits one, and the verdict read off the columns by the
// CORE rule (`Album.Upcoming.PreSaveTarget`) — so a stale link (cached up to 30 days) never pre-saves a record that
// shipped last week. No second cache, no service class: the columns are the cache.
//
// THE EDGE ANSWERS (called from the provider's `AnswerQuery` / `AnswerEdge` arms, shared-file patches in the WP-5.M
// report). `getAlbum` answers at its page offset (`Decode.AlbumAnswer`); `queryAlbumMerch` lands the merch run;
// `similarAlbumsBasedOnThisTrack` is seeded by a TRACK while the relation hangs off the ALBUM — the provider cannot read
// the tables, so the planner resolves the seed on the UI thread (`Album.SimilarSeedUri`) into the batch's per-parent
// string (`FetchBatch.Revisions`) and this arm only reads it. No seed (the tracklist has not landed) is answered WITHOUT
// a request, which the edge door records as a vacancy.
//
// NAMES. Inside `Api`, `Album` and `Track` are METHODS (`Api.Album`, `Api.Track`), so the handle types are spelled
// `global::Wavee.Album` here.

using System.Text;

namespace Wavee;

public static partial class Spotify
{
    public static partial class Api
    {
        // ── the pre-save resolve ─────────────────────────────────────────────────────────────────────────────────────

        /// <summary><c>Controls.PreSave</c>: an album or prerelease uri → the <c>spotify:prerelease:</c> uri a pre-save
        /// addresses, or null when the release is out or nothing resolves. Runs kind 138 through the one metadata door on
        /// an api thread, commits the answer on the UI thread, then reads the column. Call it from the UI thread (it
        /// captures the scope epoch the answer belongs to).</summary>
        public static Task<string?> ResolvePreRelease(string uri, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(uri) || EntityUri.KindOf(uri.AsSpan()) != EntityKind.Album)
                return Task.FromResult<string?>(null);
            if (ct.IsCancellationRequested) return Task.FromCanceled<string?>(ct);

            var done = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            uint epoch = Entities.Current.Epoch;
            bool queued = Run(() =>
            {
                Staging s = Staging.Rent();
                s.Epoch = epoch;
                int status;
                try { status = PreRelease(uri, s, ct); }
                catch (Exception ex)
                {
                    Staging.Return(s);
                    done.TrySetException(ex);
                    return;
                }
                Spotify.Post(() => LandPreRelease(s, status, uri, ct, done));
            });
            if (!queued) done.TrySetException(new InvalidOperationException("api queue full (" + QueueDepth + ")"));
            return done.Task;
        }

        /// <summary>UI thread: commit the kind-138 answer (a non-answer commits nothing), hand the staging to the store's
        /// write-behind like any fetch answer, and read the verdict off the columns.</summary>
        static void LandPreRelease(Staging s, int status, string uri, CancellationToken ct, TaskCompletionSource<string?> done)
        {
            bool owned = false;
            try
            {
                if (status is >= 200 and < 300)
                {
                    Entities.Commit(s);                                // drops the batch whole if the scope moved (C7)
                    Entities.Publish();
                    owned = Store.WriteBehind(s);
                }
                if (ct.IsCancellationRequested) { done.TrySetCanceled(ct); return; }
                var target = global::Wavee.Album.Upcoming.PreSaveTarget(EntityUri.Parse(uri.AsSpan()),
                                                                        Store.ToUnix(Entities.Now));
                done.TrySetResult(target.IsValid ? target.Text : null);
            }
            catch (Exception ex)
            {
                done.TrySetException(ex);
            }
            finally
            {
                if (!owned) Staging.Return(s);
            }
        }

        // ── the similar-albums edge ──────────────────────────────────────────────────────────────────────────────────
        // (`getAlbum` at any offset and `queryAlbumMerch` answer through `AnswerQuery`'s own arms — `Decode.AlbumAnswer`,
        // `Decode.AlbumMerch` — because they need nothing the arm does not already have.)

        /// <summary><c>similarAlbumsBasedOnThisTrack</c> for one album, seeded by the track the planner resolved into the
        /// batch (<paramref name="seedTrackUri"/>). No seed → no request: the edge door records the album as unanswered
        /// for the scope, so the page asks only once its tracklist is in hand.</summary>
        static void SimilarAlbumsEdge(string albumUri, string? seedTrackUri, Staging s, ref FetchOutcome outcome)
        {
            if (seedTrackUri is not { Length: > 0 }) return;
            Result result = SimilarAlbums(seedTrackUri, global::Wavee.Album.PageRules.SimilarLimit, CancellationToken.None);
            if (result.Ok) Decode.SimilarAlbums(result.Bytes, Encoding.UTF8.GetBytes(albumUri), s);
            outcome.Note(in result);
        }
    }
}
