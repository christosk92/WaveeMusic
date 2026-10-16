// ── Wavee.ShellTests/TierLayoutTests.ReleaseNotes.cs — Classic's What's-new page, viewer and after-update plate at every tier size (themes plan 3.5; TC8) ───
//
// TWO mounts. The REAL frame on the What's-new route, at every plan 3.5 size and threshold edge, with `TierLayoutTests.AssertPage`'s assertions (the tier in the FIRST
// frame, no overlap of the four regions, no page-level horizontal overflow, Compact title-bar targets, the 210 ShortFloor) and, on top, the record in use. The route
// loads whatever the headless host's release store gives (a fresh profile has nothing), so the page-level checks run on the state it shows. And the page VIEW alone,
// loaded with a fixture release (three highlights, three sections, two releases), under Classic at each size: that is where the forms are asserted, on the node
// geometry the record decides (rail or picker, the hero's actions, the highlight form, the section columns, the body column's cap and its left edge), never on pixels.
//
// The viewer and the after-update plate are mounted under the same sizes: no overflow past the window, and at Compact every control a hit target of 40.

using FluentGpu.Foundation;
using FluentGpu.Scene;
using Wavee;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Themes.Classic;
using Xunit;
using static Wavee.ShellTests.ShellProbe;

namespace Wavee.ShellTests;

public sealed partial class TierLayoutTests
{
    // ── the real frame on the What's-new route ───────────────────────────────────────────────────────────────────────────

    static readonly TierPage WhatsNew = new("whatsnew", new Shell.Route(Shell.RouteKind.WhatsNew), "rn:title");

    [Theory, MemberData(nameof(PageSizes))]
    public void The_whats_new_route_holds_its_record_at_every_tier(string size)
    {
        OpenOn(WhatsNew.Route);   // a remount opens on the page the last root showed: the page under test is the only one in the frame
        AssertPage(WhatsNew, size, (shell, tiers) =>
        {
            var layout = ClassicReleaseNotesTiers.Of(tiers);
            Assert.Equal(tiers.Content > WidthTier.Huge ? WidthTier.Huge : tiers.Content, layout.Tier);   // the content tier's record
            Assert.Equal(tiers.Height == HeightTier.Short, layout.Short);
            var scene = shell.Host.Scene;
            var card = scene.AbsoluteRect(Node(shell, ClassicFrameLayout.ContentCardKey));
            var title = scene.AbsoluteRect(Node(shell, "rn:title"));
            // The title row lies in the card, on the page gutter, whatever state the page is in.
            Assert.True(title.X >= card.X - 0.5f && title.X + title.W <= card.X + card.W + 0.5f, $"{size}: the title row runs outside the card ({title} in {card})");
            // The picker's slot exists wherever a picker switches releases (Compact, Small), its height held even before the release list loads.
            Assert.Equal(!layout.HasRail, !shell.Find("rn:picker-slot").IsNull);
            if (!layout.HasRail) Assert.True(scene.AbsoluteRect(Node(shell, "rn:picker-slot")).H >= layout.PickerHeight - 0.5f, $"{size}: the picker slot's height is not held");
            // A rail never draws without its form, and a picker never beside a rail.
            if (!layout.HasRail) Assert.True(shell.Find("rn:rail").IsNull, $"{size}: a rail at {layout.Tier}");
            else Assert.True(shell.Find("rn:picker").IsNull, $"{size}: a picker at {layout.Tier}");
        });
    }

    // ── the page view alone, loaded with a fixture release ───────────────────────────────────────────────────────────────

    /// <summary>The sizes where the tier does not depend on the direction the window was resized from (a view mounted alone resolves a fresh size).</summary>
    public static TheoryData<string> FreshPageSizes()
    {
        var data = new TheoryData<string>();
        foreach (var s in Sizes.Values) if (s.From is null && s.Width < 3840f) data.Add(s.Name);
        return data;
    }

    static ViewHarnessOptions RnOptions(TierSize size) => new() { Composition = ClassicTheme.Create(), WidthDip = size.Width, HeightDip = size.Height };

    static ReleaseNotes.ReleaseNotesDocument RnDoc(string version, params string[] highlightIds)
    {
        var doc = new ReleaseNotes.ReleaseNotesDocument
        {
            Version = version, Name = "Crest", Tagline = "A fixture release.", Date = "2026-10-01",
            Sections =
            [
                new ReleaseNotes.ReleaseSection { Kind = "added", Items = [new ReleaseNotes.ReleaseItem { Id = "a1", Text = "Something **new**." }, new ReleaseNotes.ReleaseItem { Id = "a2", Text = "Another thing." }] },
                new ReleaseNotes.ReleaseSection { Kind = "fixed", Items = [new ReleaseNotes.ReleaseItem { Id = "f1", Text = "A fix." }] },
                new ReleaseNotes.ReleaseSection { Kind = "changed", Items = [new ReleaseNotes.ReleaseItem { Id = "c1", Text = "A change." }] },
            ],
            Highlights = highlightIds.Select(id => new ReleaseNotes.ReleaseHighlight { Id = id, Title = "Title " + id, Body = "Body of " + id }).ToArray(),
        };
        doc.Normalize();
        return doc;
    }

    static ReleaseNotes.HighlightItem[] RnItems(ReleaseNotes.ReleaseNotesDocument doc)
        => doc.Highlights.Select(h => new ReleaseNotes.HighlightItem(h, doc, null)).ToArray();

    static WhatsNewPageProps RnPage()
    {
        var doc = RnDoc("0.3.0", "h1", "h2", "h3");
        var index = new ReleaseNotes.ReleaseNotesIndex
        {
            Releases =
            [
                new ReleaseNotes.ReleaseNotesIndexEntry { Version = "0.3.0", Name = "Crest", Date = "2026-10-01" },
                new ReleaseNotes.ReleaseNotesIndexEntry { Version = "0.2.9", Name = "Dune", Date = "2026-09-01" },
            ],
        };
        var view = new ReleaseNotes.ReleaseNotesView([new ReleaseNotes.ReleaseEntry(doc, null, IsYou: true, IsUnread: false)], RnItems(doc), index, "0.3.0", "0.2.9");
        return new WhatsNewPageProps
        {
            State = WhatsNewState.Loaded, View = view, OnlyLatest = false, RunningVersion = "0.3.0",
            ShowOnlyLatest = static () => { }, CopyLink = static _ => { }, OpenHighlight = static _ => { },
            OpenUrl = static _ => { }, OpenStoreListing = static () => { }, OpenRelease = static _ => { },
        };
    }

    [Theory, MemberData(nameof(FreshPageSizes))]
    public void The_loaded_whats_new_page_follows_its_record_at_every_tier(string sizeName)
    {
        var size = Sizes[sizeName];
        ViewHarness.With(host, ViewKeys.WhatsNewPage, RnPage(), harness =>
        {
            harness.RunFrames(60);   // entrances settle
            var tiers = harness.Facts.Peek().Tiers;
            var layout = ClassicReleaseNotesTiers.Of(tiers);
            Assert.Equal(tiers.Content > WidthTier.Huge ? WidthTier.Huge : tiers.Content, layout.Tier);
            var scene = harness.Shell.Host.Scene;
            RectF At(string key) => scene.AbsoluteRect(harness.Node(key));
            float gutter = ClassicTiers.PageOf(tiers).Gutter;

            // The release switch: the rail, or a picker (never both, never neither), the rail at the record's width.
            Assert.Equal(layout.HasRail, harness.Contains("rn:rail"));
            Assert.Equal(!layout.HasRail, harness.Contains("rn:picker"));
            if (layout.HasRail) Assert.Equal(layout.RailWidth, At("rn:rail").W, 0.5f);
            Assert.Equal(!layout.HasRail, harness.Contains("rn:picker-slot"));
            if (!layout.HasRail) Assert.True(At("rn:picker").H >= layout.PickerHeight - 0.5f, $"{sizeName}: the picker is {At("rn:picker").H} tall, under {layout.PickerHeight}");
            // Small: the picker rides the title row; Compact: it sits under it.
            if (!layout.HasRail)
            {
                var title = At("rn:title");
                var picker = At("rn:picker");
                if (layout.PickerInTitleRow) Assert.True(picker.Y >= title.Y - 0.5f && picker.Y + picker.H <= title.Y + title.H + 0.5f, $"{sizeName}: the picker is not in the title row");
                else Assert.True(picker.Y >= title.Y + title.H - 0.5f, $"{sizeName}: the picker overlaps the title row");
            }

            // The body column: capped at the record's width and LEFT-aligned on the page gutter, never centred.
            var frame = At("rn:frame");
            var body = At("rn:body");
            Assert.Equal(frame.X + gutter, body.X, 1f);
            if (!float.IsNaN(layout.BodyMaxW)) Assert.True(body.W <= layout.BodyMaxW + 0.5f, $"{sizeName}: the body is {body.W}, over its cap {layout.BodyMaxW}");
            Assert.True(body.X + body.W <= size.Width + 0.5f, $"{sizeName}: the body runs past the window");
            if (layout.HasRail) Assert.True(At("rn:rail").X >= body.X + body.W - 0.5f, $"{sizeName}: the rail overlaps the body");

            // The hero: its actions beside the copy, or under it; the Compact pair is a two-column grid of 40-high buttons.
            var hero = At("rn:hero");
            var open = At("rn:hero:open");
            var copy = At("rn:hero:copy");
            Assert.True(hero.X + hero.W <= size.Width + 0.5f, $"{sizeName}: the hero runs past the window");
            if (layout.ActionsBeside) Assert.True(open.X > hero.X + hero.W / 2f, $"{sizeName}: the actions are not beside the copy");
            else Assert.True(open.Y > hero.Y + hero.H / 2f - 40f && open.X < hero.X + hero.W / 2f, $"{sizeName}: the actions are not under the copy");
            if (layout.HeroActions == ClassicReleaseHeroActions.GridBelow)
            {
                Assert.Equal(open.Y, copy.Y, 0.5f);
                Assert.Equal(open.W, copy.W, 1f);
                Assert.True(open.H >= 40f - 0.5f && copy.H >= 40f - 0.5f, $"{sizeName}: the Compact hero buttons are {open.H} / {copy.H} tall");
            }

            // The highlights: one row of poster cards, two columns of row cards, or a column of them.
            var cards = harness.FindPrefix("hl:").Select(c => scene.AbsoluteRect(c.Node)).OrderBy(r => r.Y).ThenBy(r => r.X).ToList();
            Assert.Equal(3, cards.Count);
            switch (layout.HighlightForm)
            {
                case ClassicReleaseHighlightForm.Strip:
                    Assert.True(cards.All(c => Math.Abs(c.Y - cards[0].Y) < 1f), $"{sizeName}: the strip is not one row");
                    Assert.True(cards.All(c => c.W <= 420f + 0.5f), $"{sizeName}: a strip card is wider than its 420 cap");
                    break;
                case ClassicReleaseHighlightForm.Grid2:
                    Assert.Equal(cards[0].Y, cards[1].Y, 1f);           // two across
                    Assert.True(cards[2].Y > cards[0].Y + cards[0].H - 1f, $"{sizeName}: the third card does not wrap under the first");
                    Assert.Equal(cards[0].X, cards[2].X, 1f);
                    break;
                default:   // Column
                    Assert.True(cards.All(c => Math.Abs(c.X - cards[0].X) < 1f), $"{sizeName}: the column is not one column");
                    Assert.True(cards[1].Y >= cards[0].Y + cards[0].H - 1f && cards[2].Y >= cards[1].Y + cards[1].H - 1f, $"{sizeName}: the column cards overlap");
                    break;
            }
            if (layout.Target > 0f) Assert.True(cards.All(c => c.H >= layout.Target - 0.5f), $"{sizeName}: a Compact highlight card is under 40 tall");

            // The changelog: the sections stacked, or side by side in rows of SectionColumns (Huge: three).
            var added = At("sec:0.3.0:added");
            var fixedSection = At("sec:0.3.0:fixed");
            var changed = At("sec:0.3.0:changed");
            if (layout.SectionColumns == 3)
            {
                Assert.True(Math.Abs(added.Y - fixedSection.Y) < 1f && Math.Abs(fixedSection.Y - changed.Y) < 1f, $"{sizeName}: the Huge sections are not in one row");
                Assert.True(added.X < fixedSection.X && fixedSection.X < changed.X);
            }
            else
            {
                Assert.True(fixedSection.Y >= added.Y + added.H - 1f && changed.Y >= fixedSection.Y + fixedSection.H - 1f, $"{sizeName}: the sections are not stacked");
            }

            // Compact: every control the page draws is a 40-DIP target (the picker's menu button, the hero buttons, the cards).
            if (layout.Target > 0f)
            {
                foreach (var n in Subtree(scene, harness.Node("rn:picker")))
                {
                    if ((scene.Flags(n) & NodeFlags.Focusable) == 0) continue;
                    var r = scene.AbsoluteRect(n);
                    Assert.True(r.H >= 40f - 0.5f, $"{sizeName}: a picker target is {r.W} x {r.H}");
                }
            }
        }, RnOptions(size));
    }

    // ── the viewer and the after-update plate ────────────────────────────────────────────────────────────────────────────

    /// <summary>The window sizes the overlays are held at: the Compact board, the 300 floor, the two 330-tall windows, Small, Standard and Huge.</summary>
    public static TheoryData<string> OverlaySizes() => new()
    {
        "compact 400x860", "floor 300x640", "floor 300x330", "short 800x330", "small 800x600", "standard 1440x960", "huge 2560x1440",
    };

    [Theory, MemberData(nameof(OverlaySizes))]
    public void The_highlight_viewer_holds_at_every_tier(string sizeName)
    {
        var size = Sizes[sizeName];
        var props = new HighlightViewerProps
        {
            Items = RnItems(RnDoc("0.3.0", "h1", "h2", "h3")), Initial = 0, Close = static () => { }, TryItFor = static _ => null,
            OpenUrl = static _ => { }, OpenStoreListing = static () => { },
        };
        ViewHarness.With(host, ViewKeys.ReleaseNotesViewer, props, harness =>
        {
            harness.RunFrames(60);
            var scene = harness.Shell.Host.Scene;
            var ov = ClassicReleaseNotesTiers.Overlay(harness.Facts.Peek().Tiers);
            Assert.Equal(size.Window == WidthTier.Compact, ov.Target > 0f);
            foreach (var key in new[] { "hv:close", "hv:prev", "hv:next" })
            {
                var r = scene.AbsoluteRect(harness.Node(key));
                Assert.True(r.X >= -0.5f && r.X + r.W <= size.Width + 0.5f, $"{sizeName}: {key} runs outside the window ({r})");
                Assert.True(r.W >= ov.ChromeCircle - 0.5f && r.H >= ov.ChromeCircle - 0.5f, $"{sizeName}: {key} is {r.W} x {r.H}, under {ov.ChromeCircle}");
            }
            var pips = harness.FindPrefix("pip:");
            Assert.Equal(3, pips.Count);
            foreach (var (node, key) in pips)
            {
                var r = scene.AbsoluteRect(node);
                Assert.True(r.X >= -0.5f && r.X + r.W <= size.Width + 0.5f, $"{sizeName}: {key} runs outside the window ({r})");
                if (ov.Target > 0f) Assert.True(r.W >= ov.Target - 0.5f && r.H >= ov.Target - 0.5f, $"{sizeName}: {key} is {r.W} x {r.H}, under 40");
            }
        }, RnOptions(size));
    }

    [Theory, MemberData(nameof(OverlaySizes))]
    public void The_after_update_plate_holds_at_every_tier(string sizeName)
    {
        var size = Sizes[sizeName];
        var doc = RnDoc("0.3.0", "h1", "h2", "h3");
        var props = new AfterUpdatePlateProps
        {
            VersionRange = "0.2.9 → 0.3.0.1", VersionText = "Wavee 0.3.0", Tagline = doc.Tagline, Cards = RnItems(doc),
            OpenCard = static _ => { }, SetDontShowAgain = static _ => { }, ShowFull = static () => { }, Close = static () => { },
            OpenStoreListing = static () => { },
        };
        ViewHarness.With(host, ViewKeys.ReleaseNotesAfterUpdatePlate, props, harness =>
        {
            harness.RunFrames(60);
            var scene = harness.Shell.Host.Scene;
            var ov = ClassicReleaseNotesTiers.Overlay(harness.Facts.Peek().Tiers);
            var plate = scene.AbsoluteRect(harness.Node("rn:plate"));
            // Never wider than the window less the margins, never taller than the window less them, inside the window.
            Assert.True(plate.W <= ov.PlateWidth(size.Width) + 0.5f, $"{sizeName}: the plate is {plate.W} wide, over {ov.PlateWidth(size.Width)}");
            Assert.True(plate.H <= ov.PlateMaxHeight(size.Height) + 0.5f, $"{sizeName}: the plate is {plate.H} tall, over {ov.PlateMaxHeight(size.Height)}");
            Assert.True(plate.X >= -0.5f && plate.X + plate.W <= size.Width + 0.5f, $"{sizeName}: the plate runs outside the window ({plate})");
            // The footer's buttons are always inside the plate (a body that is too tall scrolls, the footer stays pinned).
            foreach (var key in new[] { "rn:plate:full", "rn:plate:ok" })
            {
                var r = scene.AbsoluteRect(harness.Node(key));
                Assert.True(r.X >= plate.X - 0.5f && r.X + r.W <= plate.X + plate.W + 0.5f, $"{sizeName}: {key} runs outside the plate ({r} in {plate})");
                Assert.True(r.Y + r.H <= plate.Y + plate.H + 0.5f, $"{sizeName}: {key} is cut off the plate's foot ({r} in {plate})");
                if (ov.Target > 0f) Assert.True(r.H >= 40f - 0.5f, $"{sizeName}: {key} is {r.H} tall, under 40");
            }
            if (ov.Target > 0f)
            {
                Assert.True(scene.AbsoluteRect(harness.Node("rn:plate:check")).H >= 40f - 0.5f, $"{sizeName}: the check box is under 40 tall");
                foreach (var (node, key) in harness.FindPrefix("dlg-hl:"))
                    Assert.True(scene.AbsoluteRect(node).H >= 40f - 0.5f, $"{sizeName}: {key} is under 40 tall");
            }
        }, RnOptions(size));
    }
}
