using System;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend;
using Wavee.Backend.Spotify;
using Wavee.Core;

namespace Wavee.SpotifyLive;

/// <summary>The live Home "Show all" adapter over Pathfinder's <c>homeSection</c> operation.
///
/// Home's section URIs (<c>spotify:section:…</c>) belong to the HOME document, not to Browse. This surface used to page
/// them through <c>browseSection</c> on an inferred contract; <c>homeSection</c> is the operation the desktop client
/// actually issues for the same gesture (all_home.saz session 480), and it shares one persisted document with
/// <c>home</c> — see <see cref="PathfinderOps.HomeSection"/>.
///
/// The variable shape is capture-exact and NOT interchangeable with <c>home</c>'s: this one sends <c>uri</c> +
/// <c>sectionItemsOffset</c> and NO <c>facet</c>; <c>home</c> sends <c>facet</c> and neither of the other two.
///
/// A stale persisted hash answers HTTP 400, which <see cref="PathfinderClient"/> logs and turns into a null document —
/// which becomes a null here and a visible failure on the page. That is deliberate: there is no <c>browseSection</c>
/// fallback, because a silent fallback to the wrong endpoint is what hid this defect in the first place.</summary>
public sealed class SpotifyHomeSectionService : IHomeSectionSource
{
    const string DesktopIntegration = "INTEGRATION_DESKTOP";
    /// <summary>The captured page size. The desktop client asks for 20 per "Show all" page; <c>home</c>'s own inline
    /// sections use a much smaller limit, so the two must not share a constant.</summary>
    const int PageLimit = 20;

    readonly PathfinderResource _pf;
    readonly WaveeLogger _log;

    public SpotifyHomeSectionService(PathfinderResource pf, WaveeLogger log = default)
    {
        _pf = pf;
        _log = log;
    }

    public async Task<HomeSectionPageResult?> GetHomeSectionAsync(string sectionUri, int offset,
                                                                 CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(sectionUri)) return null;
        try
        {
            // The real local zone, as IANA — exactly as WriteHomeVariables does. The zone drives the time-of-day
            // shelves, so asking for a section of the feed in a different zone than the feed itself is asking for
            // someone else's afternoon.
            string tz = SpotifyTimeZone.LocalIana;
            using var doc = await _pf.UseQueryAsync(PathfinderOps.HomeSection, PathfinderOps.HomeSectionHash,
                w =>
                {
                    w.WriteString("uri", sectionUri);
                    w.WriteString("homeEndUserIntegration", DesktopIntegration);
                    w.WriteString("timeZone", tz);
                    w.WriteString("sp_t", "");
                    w.WriteNumber("sectionItemsOffset", offset);
                    w.WriteNumber("sectionItemsLimit", PageLimit);
                    w.WriteBoolean("includeEpisodeContentRatingsV2", true);
                }, PathfinderClient.Platform.Desktop, ct).ConfigureAwait(false);

            if (doc is null)
            {
                _log.Info("home: homeSection " + sectionUri + " returned no document");
                return null;
            }

            var page = SpotifyHomeComposer.SectionPage(doc.RootElement);
            if (page is null)
            {
                _log.Info("home: homeSection " + sectionUri + " carried no section");
                return null;
            }
            // Success telemetry, mirroring browse.all.ok/browse.page.ok: without it, "the section paged" and "the
            // section was never fetched" look identical in the log, which is precisely the pair a dead "Show all"
            // button cannot be told apart from.
            _log.Event(WaveeLogLevel.Info, "home.section.ok", "homeSection page parsed", sectionUri,
                fields: [WaveeLogField.Of("offset", offset), WaveeLogField.Of("items", page.Section.RawItemCount),
                         WaveeLogField.Of("total", page.Section.TotalCount),
                         WaveeLogField.Of("nextOffset", page.NextOffset ?? -1)]);
            return page;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _log.Info("home: homeSection " + sectionUri + " failed: " + ex.Message);
            return null;
        }
    }
}
