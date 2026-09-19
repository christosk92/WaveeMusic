// ── Entities/Fetch.Baselines.cs — SHELL: the replay baseline, snapshotted at send (a named partial of Fetch.cs) ──────
//
// Role: SHELL (UI thread) · Owner: L2 · Wave: D3 · Budget: 80 lines
// Spec: docs/plans/wavee/cache-integrity-and-playlist-diff-implementation.md §3.3 (the replay arm)
//
// THE BASELINE A /diff IS REPLAYED OVER. A held revision makes a list's network ask the `/diff`
// (`FillRevisions`), and since wave D3 a `/diff` that answers with OPS is replayed rather than re-read in full
// (Spotify.Api.Library.cs, `ListReplay`). The replay runs on an API worker thread, which may never read a live table
// (C1: `FetchProvider.Start`'s contract) — so everything it replays over crosses in the batch: the list the held
// revision describes, snapshotted HERE, on the UI thread, at send, as text (`Store.SnapshotList`).
//
//     Send (UI) ─ FillRevisions: batch.Revisions[i] = the held revision
//               ─ FillBaselines: batch.Baselines[i] = the settled list that revision describes, or null
//     provider (API thread) ─ /diff → ListReplay.Decide(answer, held, baseline) → stage the replayed whole list
//
// A baseline is offered ONLY beside a held revision, and only for a settled whole (Complete, every row, no optimistic
// row — `ListWrite.IsSettled`): ops replayed over a window or over unconfirmed rows describe a list nobody has, and a
// list stored from them is poisoned for ever (plan §7). No baseline is never an error — the provider reads the list in
// full, exactly as before D3. Recycle clears the column with the batch (Fetch.cs), so a pooled batch pins no list.
//
// Rules: C1 (UI thread; the snapshot resolves text here so the provider needs no interner), P4 (one snapshot per
// parent per send — a list is one request per parent on every route there is).

namespace Wavee;

public static partial class Fetch
{
    /// <summary>UI THREAD, inside <see cref="Send"/> right after <see cref="FillRevisions"/>: for a
    /// <see cref="FetchEdge.PlaylistTracks"/> or <see cref="FetchEdge.Rootlist"/> batch, each parent whose held revision
    /// was sent (<see cref="FetchBatch.Revisions"/> non-null) gets its list as a replay baseline
    /// (<see cref="Store.SnapshotList"/> — null when the list is not a settled whole). Every other parent, and every other
    /// relation, gets null: the provider then reads a list that moved in full.</summary>
    static void FillBaselines(FetchBatch batch, FetchEdge edge, int take)
    {
        ListRow[]?[] baselines = batch.Baselines;
        int n = Math.Min(take, Math.Min(baselines.Length, batch.Revisions.Length));
        Array.Clear(baselines, 0, n);
        EdgeRelation relation = edge switch
        {
            FetchEdge.PlaylistTracks => EdgeRelation.PlaylistTracks,
            FetchEdge.ShowEpisodes => EdgeRelation.ShowEpisodes,
            FetchEdge.Rootlist => EdgeRelation.Rootlist,
            _ => EdgeRelation.None,
        };
        if (relation == EdgeRelation.None) return;
        Scope scope = Entities.Current;
        for (int i = 0; i < n; i++)
            if (batch.Revisions[i] is not null) baselines[i] = Store.SnapshotList(scope, relation, batch.Slots[i]);
    }
}
