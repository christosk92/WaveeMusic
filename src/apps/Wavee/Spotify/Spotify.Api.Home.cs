// ── Spotify/Spotify.Api.Home.cs ─────────────────────────────────────────────────────────────────────────────────────
// the omnibar's suggestion source (G-191, owner P's half): searchSuggestions → Shell.Omnibar.Suggestions
//
// Role: SHELL (the request blocks on an api thread) + the UI-thread decode of its answer
// Owner: P (Wave 5, stream P3)
// Wave: 5
// Budget: 90 lines (the WP-5 "Spotify.Api.<Area>.cs" row)
// Spec: ch 13 §7 (the omnibar's source), ch 18 W15 (the seam), gap register G-191 (P part), G-045 (suggestions)
//
// THE SEAM IS OWNER I's (`Shell.Omnibar.Source`, Shell.cs §14): a `Func<string, CancellationToken, Task<Suggestions>>`
// the masthead awaits and posts back itself. This file is the function. It does NOT touch the entity tables — a
// suggestion popup is a launcher, not catalogue data — so there is no Staging, no commit and no epoch.
//
// TWO THREADS, ONE HOP EACH. The request runs on an api thread (`Api.Run`, the one pool; a full queue FAULTS the task,
// C8). The DECODE runs on the UI thread (`Spotify.Post`): a rich row's `EntityUri.Parse` interns a text-form identity
// (a genre page, a profile), and the string table is UI-thread-only (C1). The answer body is an owned `byte[]`, so it
// crosses the hop without a copy.
//
// Install it once at boot (App.cs, a shared-file patch in the WP-5.P report): `Shell.Omnibar.Source = Spotify.Api.SuggestAsync;`
// — `--fake` leaves it unset and the popup answers from the navigation log.

namespace Wavee;

public static partial class Spotify
{
    public static partial class Api
    {
        /// <summary>The omnibar's as-you-type source (G-191): one <c>searchSuggestions</c> request for the trimmed
        /// <paramref name="query"/> on an api thread, decoded on the UI thread by
        /// <see cref="Decode.SearchSuggestions"/>. A blank query answers <see cref="Shell.Omnibar.Suggestions.Empty"/>
        /// without a request; a cancelled token cancels the task; a non-2xx answer FAULTS it (the popup's retry row).</summary>
        public static Task<Shell.Omnibar.Suggestions> SuggestAsync(string query, CancellationToken ct)
        {
            string term = query?.Trim() ?? "";
            if (term.Length == 0) return Task.FromResult(Shell.Omnibar.Suggestions.Empty);
            if (ct.IsCancellationRequested) return Task.FromCanceled<Shell.Omnibar.Suggestions>(ct);

            var done = new TaskCompletionSource<Shell.Omnibar.Suggestions>(TaskCreationOptions.RunContinuationsAsynchronously);
            bool queued = Run(() =>
            {
                Result result;
                try { result = Search(SearchFacet.Suggestions, term, 0, 30, ct); }
                catch (Exception ex)
                {
                    done.TrySetException(ex);
                    return;
                }
                if (ct.IsCancellationRequested) { done.TrySetCanceled(ct); return; }
                if (!result.Ok)
                {
                    done.TrySetException(new HttpRequestException("searchSuggestions answered " + result.Status));
                    return;
                }
                byte[] body = result.Body;
                Spotify.Post(() => LandSuggestions(body, ct, done));
            });
            if (!queued) done.TrySetException(new InvalidOperationException("api queue full (" + QueueDepth + ")"));
            return done.Task;
        }

        /// <summary>UI thread: decode the answer into the popup's value (a malformed body is an empty answer).</summary>
        static void LandSuggestions(byte[] body, CancellationToken ct, TaskCompletionSource<Shell.Omnibar.Suggestions> done)
        {
            if (ct.IsCancellationRequested) { done.TrySetCanceled(ct); return; }
            try { done.TrySetResult(Decode.SearchSuggestions(body)); }
            catch (Exception ex) { done.TrySetException(ex); }
        }
    }
}
