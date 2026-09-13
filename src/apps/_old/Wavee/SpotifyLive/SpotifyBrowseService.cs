using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend;
using Wavee.Core;

namespace Wavee.SpotifyLive;

/// <summary>The live Browse adapter over Pathfinder (<c>browseAll</c> / <c>browsePage</c> / <c>browseSection</c>).
///
/// Every request shape here is wire-exact against <c>browe.saz</c> (729 sessions). Two things the contract makes easy
/// to get wrong, both encoded below:
///  • <c>browseAll</c> takes <c>sectionPagination.limit = 99</c> — the categories all live in ONE section, so a
///    default limit silently truncates the directory to the first handful.
///  • <c>pagePagination</c> and <c>browseSection</c> are independent paging axes (sections vs items within a section).
///
/// Reads ride <see cref="PathfinderResource"/>, so repeats inside the TTL never touch the network and a category page
/// re-opened from the directory is instant.</summary>
public sealed class SpotifyBrowseService : IBrowseService
{
    const string DesktopIntegration = "INTEGRATION_DESKTOP";

    readonly PathfinderResource _pf;
    readonly WaveeLogger _log;

    public SpotifyBrowseService(PathfinderResource pf, WaveeLogger log = default)
    {
        _pf = pf;
        _log = log;
    }

    public async Task<IReadOnlyList<BrowseCategory>> GetCategoriesAsync(CancellationToken ct = default)
    {
        try
        {
            using var doc = await _pf.UseQueryAsync(PathfinderOps.BrowseAll, PathfinderOps.BrowseAllHash,
                w =>
                {
                    WritePagination(w, "pagePagination", 0, 10);
                    // 99, not a default: browseAll returns all ~70 categories inside ONE section, so a small
                    // sectionPagination limit truncates the directory rather than paging it.
                    WritePagination(w, "sectionPagination", 0, 99);
                    w.WriteString("browseEndUserIntegration", DesktopIntegration);
                }, PathfinderClient.Platform.Desktop, ct).ConfigureAwait(false);

            if (doc is null)
            {
                _log.Info("browse: browseAll returned no document");
                return Array.Empty<BrowseCategory>();
            }
            var categories = SpotifyBrowseMapper.Categories(doc.RootElement);
            // Success telemetry, not just failure telemetry: with only failure logs, "browseAll worked" and
            // "browseAll was never called" are indistinguishable in the log — which is exactly the ambiguity a stuck
            // shimmer produces.
            _log.Event(WaveeLogLevel.Info, "browse.all.ok", "browseAll categories parsed",
                fields: [WaveeLogField.Of("count", categories.Count)]);
            if (categories.Count == 0) _log.Info("browse: browseAll parsed 0 categories");
            return categories;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _log.Info("browse: browseAll failed: " + ex.Message);
            return Array.Empty<BrowseCategory>();
        }
    }

    public async Task<BrowsePageModel?> GetPageAsync(string pageUri, int sectionOffset = 0, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(pageUri)) return null;
        try
        {
            using var doc = await _pf.UseQueryAsync(PathfinderOps.BrowsePage, PathfinderOps.BrowsePageHash,
                w =>
                {
                    WritePagination(w, "pagePagination", sectionOffset, 10);
                    WritePagination(w, "sectionPagination", 0, 10);
                    w.WriteString("uri", pageUri);
                    w.WriteString("browseEndUserIntegration", DesktopIntegration);
                    w.WriteBoolean("includeEpisodeContentRatingsV2", true);
                }, PathfinderClient.Platform.Desktop, ct).ConfigureAwait(false);

            if (doc is null)
            {
                _log.Info("browse: browsePage " + pageUri + " returned no document");
                return null;
            }

            var page = SpotifyBrowseMapper.Page(doc.RootElement, pageUri);
            // Success telemetry, mirroring browse.all.ok for the same reason: without it, "the category page loaded"
            // and "the category page was never fetched" look identical in the log, and a stuck shimmer is exactly the
            // report that cannot be told apart from those two without it.
            _log.Event(WaveeLogLevel.Info, "browse.page.ok", "browsePage parsed", pageUri,
                fields: [WaveeLogField.Of("sections", page.Sections.Count)]);
            // A header-less, section-less 200 is a REAL server response (observed on one captured page), not a parse
            // failure. Log it so an unexpectedly blank category is diagnosable, then let the UI show its empty state.
            if (page.IsEmpty) _log.Info("browse: page " + pageUri + " resolved empty (200 with no header/sections)");
            return page;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _log.Info("browse: browsePage " + pageUri + " failed: " + ex.Message);
            return null;
        }
    }

    public async Task<BrowseSection?> GetSectionAsync(string sectionUri, int offset, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(sectionUri)) return null;
        try
        {
            using var doc = await _pf.UseQueryAsync(PathfinderOps.BrowseSection, PathfinderOps.BrowseSectionHash,
                w =>
                {
                    WritePagination(w, "pagination", offset, 20);
                    w.WriteString("uri", sectionUri);
                    w.WriteString("browseEndUserIntegration", DesktopIntegration);
                    w.WriteBoolean("includeEpisodeContentRatingsV2", true);
                }, PathfinderClient.Platform.Desktop, ct).ConfigureAwait(false);

            return doc is null ? null : SpotifyBrowseMapper.SectionPage(doc.RootElement);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _log.Info("browse: browseSection " + sectionUri + " failed: " + ex.Message);
            return null;
        }
    }

    static void WritePagination(Utf8JsonWriter w, string name, int offset, int limit)
    {
        w.WritePropertyName(name);
        w.WriteStartObject();
        w.WriteNumber("offset", offset);
        w.WriteNumber("limit", limit);
        w.WriteEndObject();
    }
}

/// <summary>Switchable browse seam: the UI holds this for the whole session and go-live/GoOffline swap the inner
/// implementation, so pages never re-resolve a service or hold a stale reference across a login change.</summary>
public sealed class SwitchableBrowseService : IBrowseService
{
    volatile IBrowseService _inner = NullBrowseService.Instance;

    public void SetInner(IBrowseService inner) => _inner = inner ?? NullBrowseService.Instance;
    public void Reset() => _inner = NullBrowseService.Instance;

    public Task<IReadOnlyList<BrowseCategory>> GetCategoriesAsync(CancellationToken ct = default)
        => _inner.GetCategoriesAsync(ct);

    public Task<BrowsePageModel?> GetPageAsync(string pageUri, int sectionOffset = 0, CancellationToken ct = default)
        => _inner.GetPageAsync(pageUri, sectionOffset, ct);

    public Task<BrowseSection?> GetSectionAsync(string sectionUri, int offset, CancellationToken ct = default)
        => _inner.GetSectionAsync(sectionUri, offset, ct);
}
