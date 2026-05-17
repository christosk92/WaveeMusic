using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wavee.UI.Contracts;

namespace Wavee.UI.Services.Artists;

/// <summary>
/// Constants extracted from <c>ArtistViewModel</c>. The capping rule is
/// shared across the artist detail page (capped grids + "See all" tiles) and
/// the dedicated discography page (paginates without capping); keeping the
/// values centralised here avoids the two pages drifting.
/// </summary>
public static class DiscographyPaginationConstants
{
    /// <summary>
    /// Threshold above which the Albums / Singles grids on the artist page
    /// switch to capped projections plus a "See all N" tile. Prolific
    /// artists (legacy labels, VA / soundtrack accounts) can have 200-500
    /// releases per group; rendering all of them on the artist page hammers
    /// the composition tree and dwarfs every other section.
    /// </summary>
    public const int CapThreshold = 100;

    /// <summary>Cap applied to capped projections when the group total
    /// exceeds <see cref="CapThreshold"/>.</summary>
    public const int CardCap = 30;

    /// <summary>Standard discography fetch page size. The artist overview
    /// returns the first page directly; subsequent pages are fetched via
    /// <see cref="IArtistService.GetDiscographyPageAsync"/> at this size.</summary>
    public const int PageSize = 20;

    /// <summary>Maximum number of placeholder rows to seed per group on
    /// initial load. Keeps the shimmer skeleton bounded — a 500-release
    /// artist doesn't paint 500 placeholders before the second page lands.</summary>
    public const int MaxPlaceholders = 20;
}

/// <summary>
/// A fetched discography page anchored to its absolute offset in the parent
/// list. The caller pairs each (Offset, Items) with its enclosing group
/// placeholder so VMs can patch loaded items into their pre-allocated slots
/// instead of appending duplicates.
/// </summary>
public sealed record DiscographyPage(int Offset, IReadOnlyList<ArtistReleaseResult> Items);

/// <summary>
/// Result of fetching a single discography group. <see cref="Pages"/> is
/// empty when the group already had everything (caller should noop) or
/// when the fetch hit an error before any page returned.
/// </summary>
public sealed record DiscographyGroupFetch(
    string Type,
    string PlaceholderPrefix,
    IReadOnlyList<DiscographyPage> Pages,
    bool Failed);

/// <summary>
/// Framework-neutral pagination helper for an artist's discography. Owns the
/// page-boundary math, the cap/see-all threshold rule, and the
/// <see cref="IArtistService.GetDiscographyPageAsync"/> fetch loop. Stateless;
/// every method takes its inputs as arguments and returns a value. The
/// ArtistViewModel still owns the bound collections + the dispatcher; this
/// service exists so the math is unit-testable without booting the VM.
/// </summary>
public sealed class DiscographyPaginationService
{
    private readonly IArtistService _artistService;

    public DiscographyPaginationService(IArtistService artistService)
    {
        _artistService = artistService;
    }

    /// <summary>
    /// True when the supplied group total crosses
    /// <see cref="DiscographyPaginationConstants.CapThreshold"/> and the
    /// artist page should cap the grid + render the "See all" tile.
    /// </summary>
    public static bool ShouldCap(int totalCount)
        => totalCount > DiscographyPaginationConstants.CapThreshold;

    /// <summary>
    /// Number of items to take when capping is in effect. Returns
    /// <see cref="int.MaxValue"/> when the group is under the threshold so
    /// callers can use a single <c>Take(cap)</c> expression for both paths.
    /// </summary>
    public static int ResolveCap(int totalCount)
        => ShouldCap(totalCount)
            ? DiscographyPaginationConstants.CardCap
            : int.MaxValue;

    /// <summary>
    /// Number of placeholder rows to seed for a group given the already
    /// loaded count + the reported total. Bounded by
    /// <see cref="DiscographyPaginationConstants.MaxPlaceholders"/> so the
    /// shimmer skeleton stays small regardless of the artist's catalogue.
    /// </summary>
    public static int CountPlaceholderSlots(int alreadyLoaded, int totalCount)
    {
        var remaining = totalCount - alreadyLoaded;
        if (remaining <= 0) return 0;
        return Math.Min(remaining, DiscographyPaginationConstants.MaxPlaceholders);
    }

    /// <summary>
    /// Build the stable placeholder id for the i-th slot in a group, e.g.
    /// <c>album-ph-42</c>. The same scheme is used by the VM when it
    /// pre-allocates placeholder LazyReleaseItems on the initial load — so
    /// downstream <c>Populate</c> calls can look them up by id.
    /// </summary>
    public static string PlaceholderId(string placeholderPrefix, int index)
        => $"{placeholderPrefix}-{index}";

    /// <summary>
    /// Pull every remaining page for the given group. Stops when the
    /// per-page result is empty (Spotify sometimes reports totalCount larger
    /// than the actual page chain returns), cancellation fires, or all
    /// pages have been fetched. Returns a result with <c>Failed=true</c> on
    /// any exception other than <see cref="OperationCanceledException"/>.
    /// </summary>
    public async Task<DiscographyGroupFetch> FetchRemainingGroupAsync(
        string artistUri,
        string type,
        string placeholderPrefix,
        int alreadyLoaded,
        int totalCount,
        CancellationToken ct)
    {
        var pages = new List<DiscographyPage>();
        try
        {
            var offset = alreadyLoaded;
            while (offset < totalCount)
            {
                ct.ThrowIfCancellationRequested();
                var page = await _artistService
                    .GetDiscographyPageAsync(artistUri, type, offset, DiscographyPaginationConstants.PageSize, ct)
                    .ConfigureAwait(false);
                if (page.Count == 0) break;
                pages.Add(new DiscographyPage(offset, page));
                offset += page.Count;
            }

            return new DiscographyGroupFetch(type, placeholderPrefix, pages, Failed: false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return new DiscographyGroupFetch(type, placeholderPrefix, pages, Failed: true);
        }
    }

    /// <summary>
    /// Pull the remaining pages for every group that's not already complete,
    /// in parallel. Returns one <see cref="DiscographyGroupFetch"/> per
    /// requested group; groups already complete are skipped (no entry in
    /// the result). Throws <see cref="OperationCanceledException"/> if
    /// <paramref name="ct"/> fires.
    /// </summary>
    public async Task<IReadOnlyList<DiscographyGroupFetch>> FetchRemainingDiscographyAsync(
        string artistUri,
        int albumsLoaded, int albumsTotal,
        int singlesLoaded, int singlesTotal,
        int compilationsLoaded, int compilationsTotal,
        CancellationToken ct)
    {
        var tasks = new List<Task<DiscographyGroupFetch>>();

        if (albumsLoaded < albumsTotal)
            tasks.Add(FetchRemainingGroupAsync(artistUri, "ALBUM", "album-ph", albumsLoaded, albumsTotal, ct));

        if (singlesLoaded < singlesTotal)
            tasks.Add(FetchRemainingGroupAsync(artistUri, "SINGLE", "single-ph", singlesLoaded, singlesTotal, ct));

        if (compilationsLoaded < compilationsTotal)
            tasks.Add(FetchRemainingGroupAsync(artistUri, "COMPILATION", "comp-ph", compilationsLoaded, compilationsTotal, ct));

        if (tasks.Count == 0)
            return Array.Empty<DiscographyGroupFetch>();

        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        return results;
    }
}
