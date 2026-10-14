// ── Wavee.ShellTests/ReleaseNotesMountedTests.cs — the release-notes views, mounted (themes plan 6.1, T11c) ───────────────────────────────
//
// The What's-new page, the highlight viewer and the after-update plate are the structure's since T11c: the core hosts (`ReleaseNotes.PageHost`, the viewer's and the
// plate's overlays) decide and load; Classic registers the three keys and draws them. What only a mounted tree can say:
//
//   the registered templates   Classic is the template of `Pages.WhatsNew`, `ReleaseNotes.Viewer` and `ReleaseNotes.AfterUpdatePlate`
//   the page's states          a loading or empty page draws no rail; a loaded page draws the release rail (one keyed row per release) and the highlight card
//   the viewer                 the veil's plate keeps its STABLE chrome (close, previous, next), the pager has one pip per highlight and the slide is keyed by its id
//   the plate                  the after-update plate draws its highlight cards
//
// The decisions (the gate, the range, the rail markers, the stepping rule, the plate-cap arithmetic) are `Wavee.Tests\ReleaseNotes*Tests.cs`.

using Wavee;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Themes.Classic;
using Xunit;

namespace Wavee.ShellTests;

public sealed class ReleaseNotesMountedTests(ShellHost host)
{
    static ViewHarnessOptions UnderClassic() => new() { Composition = ClassicTheme.Create() };

    static ReleaseNotes.ReleaseNotesDocument Doc(string version, params string[] highlightIds)
    {
        var doc = new ReleaseNotes.ReleaseNotesDocument
        {
            Version = version, Name = "Crest", Tagline = "A fixture release.", Date = "2026-10-01",
            Sections = [new ReleaseNotes.ReleaseSection { Kind = "added", Items = [new ReleaseNotes.ReleaseItem { Id = "i1", Text = "Something **new**." }] }],
            Highlights = highlightIds.Select(id => new ReleaseNotes.ReleaseHighlight { Id = id, Title = "Title " + id, Body = "Body of " + id }).ToArray(),
        };
        doc.Normalize();
        return doc;
    }

    static ReleaseNotes.HighlightItem[] Items(ReleaseNotes.ReleaseNotesDocument doc)
        => doc.Highlights.Select(h => new ReleaseNotes.HighlightItem(h, doc, null)).ToArray();

    static WhatsNewPageProps PageProps(WhatsNewState state, ReleaseNotes.ReleaseNotesView? view = null) => new()
    {
        State = state, View = view, OnlyLatest = false, RunningVersion = "0.3.0",
        ShowOnlyLatest = static () => { }, CopyLink = static _ => { }, OpenHighlight = static _ => { },
        OpenUrl = static _ => { }, OpenStoreListing = static () => { }, OpenRelease = static _ => { },
    };

    static ReleaseNotes.ReleaseNotesView LoadedView()
    {
        var doc = Doc("0.3.0", "h1", "h2");
        var older = Doc("0.2.9");
        var index = new ReleaseNotes.ReleaseNotesIndex
        {
            Releases =
            [
                new ReleaseNotes.ReleaseNotesIndexEntry { Version = "0.3.0", Name = "Crest", Date = "2026-10-01" },
                new ReleaseNotes.ReleaseNotesIndexEntry { Version = "0.2.9", Name = "Dune", Date = "2026-09-01" },
            ],
        };
        return new ReleaseNotes.ReleaseNotesView([new ReleaseNotes.ReleaseEntry(doc, null, IsYou: true, IsUnread: false)], Items(doc), index, "0.3.0", "0.2.9");
    }

    static HighlightViewerProps ViewerProps(ReleaseNotes.HighlightItem[] items) => new()
    {
        Items = items, Initial = 0, Close = static () => { }, TryItFor = static _ => null,
        OpenUrl = static _ => { }, OpenStoreListing = static () => { },
    };

    [Fact]
    public void Classic_is_the_registered_template_of_the_three_release_notes_keys()
        => host.Run(() =>
        {
            Assert.Equal("classic.release-notes.page", ThemeHost.Registry.TemplateIdOf(ViewKeys.WhatsNewPage.Id));
            Assert.Equal("classic.release-notes.viewer", ThemeHost.Registry.TemplateIdOf(ViewKeys.ReleaseNotesViewer.Id));
            Assert.Equal("classic.release-notes.after-update-plate", ThemeHost.Registry.TemplateIdOf(ViewKeys.ReleaseNotesAfterUpdatePlate.Id));
        });

    [Fact]
    public void A_loading_page_draws_no_rail()
        => ViewHarness.With(host, ViewKeys.WhatsNewPage, PageProps(WhatsNewState.Loading), harness =>
        {
            harness.RunFrames(10);
            Assert.Empty(harness.FindPrefix("rail:"));
        }, UnderClassic());

    [Fact]
    public void An_empty_page_draws_no_rail()
        => ViewHarness.With(host, ViewKeys.WhatsNewPage, PageProps(WhatsNewState.Empty), harness =>
        {
            harness.RunFrames(10);
            Assert.Empty(harness.FindPrefix("rail:"));
        }, UnderClassic());

    [Fact]
    public void A_loaded_page_draws_one_rail_row_per_release_and_the_highlight_cards()
        => ViewHarness.With(host, ViewKeys.WhatsNewPage, PageProps(WhatsNewState.Loaded, LoadedView()), harness =>
        {
            harness.RunFrames(30);
            // The rail's rows come from `Flow.For` keyed by version, and that key is the one the row's node carries (the row's own "rail:" key is
            // the one the For replaces: unchanged from the old page).
            Assert.True(harness.Contains("0.3.0"), "the running release's rail row");
            Assert.True(harness.Contains("0.2.9"), "the older release's rail row");
            Assert.True(harness.Contains("hl:0.3.0:h1"), "the first highlight card is keyed by its slide id");
            Assert.True(harness.Contains("hl:0.3.0:h2"), "the second highlight card is keyed by its slide id");
        }, UnderClassic());

    [Fact]
    public void The_viewer_keeps_its_chrome_and_pages_one_pip_per_highlight()
    {
        var items = Items(Doc("0.3.0", "h1", "h2", "h3"));
        ViewHarness.With(host, ViewKeys.ReleaseNotesViewer, ViewerProps(items), harness =>
        {
            harness.RunFrames(60);
            Assert.True(harness.Contains("hv:close"));
            Assert.True(harness.Contains("hv:prev"), "the previous circle stays on every slide (it dims at an end, it never leaves)");
            Assert.True(harness.Contains("hv:next"));
            Assert.Equal(3, harness.FindPrefix("pip:").Count);
            Assert.True(harness.Contains("hv:0.3.0:h1:img"), "the live slide's band layer is keyed by the slide id");
        }, UnderClassic());
    }

    [Fact]
    public void A_one_highlight_viewer_has_no_pager()
    {
        var items = Items(Doc("0.3.0", "h1"));
        ViewHarness.With(host, ViewKeys.ReleaseNotesViewer, ViewerProps(items), harness =>
        {
            harness.RunFrames(60);
            Assert.True(harness.Contains("hv:close"));
            Assert.Empty(harness.FindPrefix("pip:"));
        }, UnderClassic());
    }

    [Fact]
    public void The_after_update_plate_draws_its_highlight_cards()
    {
        var doc = Doc("0.3.0", "h1", "h2");
        var props = new AfterUpdatePlateProps
        {
            VersionRange = "0.2.9 → 0.3.0.1", VersionText = "Wavee 0.3.0", Tagline = doc.Tagline, Cards = Items(doc),
            OpenCard = static _ => { }, SetDontShowAgain = static _ => { }, ShowFull = static () => { }, Close = static () => { },
            OpenStoreListing = static () => { },
        };
        ViewHarness.With(host, ViewKeys.ReleaseNotesAfterUpdatePlate, props, harness =>
        {
            harness.RunFrames(30);
            Assert.True(harness.Contains("dlg-hl:h1"));
            Assert.True(harness.Contains("dlg-hl:h2"));
        }, UnderClassic());
    }
}
