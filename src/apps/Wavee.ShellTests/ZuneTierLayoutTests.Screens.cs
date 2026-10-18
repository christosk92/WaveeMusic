// ── Wavee.ShellTests/ZuneTierLayoutTests.Screens.cs — Zune's screens, overlays and dialogs at every tier size and edge (themes plan 3.5, 6.3; P5 ZT8) ──
//
// Settings, the setup wizard, the release notes, an overlay (the log-out card, the offline strip, the notification panel) and the report / crash dialogs' shapes, at the plan sizes and
// Zune's edges (639/640, 1007/1008, 1919/1920, 3199/3200; 300 x 330, 400 x 860; 3840 x 2160), on the facts and the records, never on pixels:
//
//   · RECORD PER TIER. The record in use is the tier's (`ZuneSettingsTiers`, `ZuneSetupTiers`, `ZuneReleaseNotesTiers`, `ZuneOverlayTiers`, `ZuneDialogTiers`): the section list's
//     form and width, the label column, the picker's tiles, the rail or the picker, the plate's and the accent column's size, the card's width, the strip's height.
//   · NO OVERFLOW. Nothing the record sizes runs outside the page, the plate or the window.
//   · COMPACT TARGETS. At Compact every section word, chip, swatch, picker card, footer command and card command is at least 40 tall.
//   · IDENTITY. A crossing (639/640 and 1007/1008, both ways, the narrowing ones from the held side) keeps the page's, the title's, the list's, the lane's, the column's and every
//     section word's node; the parts whose size changes glide (they hold animation tracks).
//
// Pure rules (the records' arithmetic, the dialog shapes, the 4K chrome row) are plain facts at the top; the mounted facts follow. The release notes, the setup plate and the logout
// card mount alone (`ViewHarness` under Zune's composition: the tiers resolve from the window the view is mounted at), the Settings page in the real frame.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Pal;
using FluentGpu.Scene;
using FluentGpu.Signals;
using Wavee;
using Wavee.Themes.Zune;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Xunit;
using static Wavee.ShellTests.ShellProbe;

namespace Wavee.ShellTests;

public sealed partial class ZuneTierLayoutTests
{
    static readonly WidthTier[] AllTiers = [WidthTier.Compact, WidthTier.Small, WidthTier.Standard, WidthTier.Huge, WidthTier.Huge4K];

    // ══ the records (pure) ═════════════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void The_settings_records_follow_their_boards()
    {
        foreach (var tier in AllTiers)
        {
            var r = ZuneSettingsTiers.For(tier);
            Assert.Equal(tier, r.Tier);
            Assert.Equal(ZuneTiers.Frame(tier).Gutter, r.Gutter);
        }

        var compact = ZuneSettingsTiers.For(WidthTier.Compact);
        Assert.True(compact.Touch);
        Assert.Equal(40f, compact.Target);
        Assert.True(compact.ListIsStrip);                // Z-Settings-Sections-Compact: the words sit above the column
        Assert.True(compact.StacksRows);                 // a control UNDER its label
        Assert.True(compact.ListItemH >= 40f);           // every section word is a 40 target
        Assert.True(compact.SwatchSize >= 40f);          // Z-Theme-Options-Compact: 40 swatches
        Assert.True(compact.PickerShowsText);            // the Compact tiles carry the line under the name

        var small = ZuneSettingsTiers.For(WidthTier.Small);
        Assert.False(small.ListIsStrip);
        Assert.Equal(180f, small.ListW);                 // Z-Settings-Appearance-Small
        Assert.Equal(520f, small.ColumnW);
        Assert.Equal(30f, small.ListItemH);
        Assert.False(small.StacksRows);                  // 140 | rest
        Assert.True(small.RowsWrap);                     // ... and a control wraps under its label where the column is too narrow
        Assert.Equal(140f, small.LabelW);
        Assert.Equal(28f, small.SwatchSize);
        Assert.False(small.PickerShowsText);             // the name alone
        Assert.Equal(0f, small.Target);

        var standard = ZuneSettingsTiers.For(WidthTier.Standard);
        Assert.Equal(220f, standard.ListW);
        Assert.Equal(660f, standard.ColumnW);
        Assert.Equal(200f, standard.LabelW);
        Assert.False(standard.RowsWrap);

        var huge = ZuneSettingsTiers.For(WidthTier.Huge);
        Assert.Equal(150f, huge.TitleSize);              // Z-Settings-Appearance-Huge: the 150 / 160 panorama
        Assert.Equal(160f, huge.TitleH);                 // the zone to the body's 176 (Z-Settings-*-Huge): the panorama sits at -12 on its exact 160 line
        Assert.Equal(260f, huge.ListW);
        Assert.Equal(52f, huge.ListItemH);
        Assert.Equal(760f, huge.ColumnW);
        Assert.Equal(220f, huge.LabelW);
        Assert.Equal(36f, huge.SwatchSize);

        // The panorama sits on its exact line (block line stacking) over a zone that ends where the boards' body starts (140 / 70 / 176 under the band).
        Assert.Equal(124f, standard.TitleH);
        Assert.Equal(84f, compact.TitleH);
        Assert.Equal(16f, ZuneSettingsRules.TitleBodyGap);

        // Pages cap at 2400 at 4K (owner 6.2): the 4K record holds Huge's geometry under the frame's 64 gutter.
        var huge4k = ZuneSettingsTiers.For(WidthTier.Huge4K);
        Assert.Equal(huge.ListW, huge4k.ListW);
        Assert.Equal(huge.ColumnW, huge4k.ColumnW);
        Assert.Equal(huge.TitleH, huge4k.TitleH);
        Assert.Equal(64f, huge4k.Gutter);
    }

    [Fact]
    public void A_pickers_tiles_fit_their_column_the_way_the_board_draws_them()
    {
        static float Row(ZuneSettingsLayout r, int tiles) => tiles * r.PickerCardW + (tiles - 1) * r.PickerGap;

        var small = ZuneSettingsTiers.For(WidthTier.Small);
        Assert.True(Row(small, 4) <= small.ColumnW, "Small draws four tiles in its 520 column");
        var huge = ZuneSettingsTiers.For(WidthTier.Huge);
        Assert.True(Row(huge, 4) <= huge.ColumnW, "Huge draws four tiles in its 760 column");
        var standard = ZuneSettingsTiers.For(WidthTier.Standard);
        Assert.True(Row(standard, 4) <= standard.ColumnW, "Standard draws four 150 tiles in its 660 column");

        // Compact: two to a line in the narrowest page (300 less the 16 gutters), never three.
        var compact = ZuneSettingsTiers.For(WidthTier.Compact);
        float narrowest = 300f - 2f * compact.Gutter;
        Assert.True(Row(compact, 2) <= narrowest);
        Assert.True(Row(compact, 3) > narrowest);
        Assert.True(compact.PickerGrows && small.PickerGrows && huge.PickerGrows && !standard.PickerGrows);
    }

    [Fact]
    public void The_setup_records_follow_their_boards()
    {
        foreach (var tier in AllTiers) Assert.Equal(tier, ZuneSetupTiers.For(tier).Tier);

        var compact = ZuneSetupTiers.For(WidthTier.Compact);
        Assert.True(compact.Sheet);                                   // Z-Setup-Compact: a sheet under the title row
        Assert.Equal(ZuneTiers.Frame(WidthTier.Compact).TitleBarH, compact.RootTopPad);
        Assert.Equal(120f, compact.AccentStripHeight);
        Assert.Equal(40f, compact.Target);
        Assert.True(compact.FooterStacked);
        Assert.Equal(300f, compact.RootWidth(300f));                  // the sheet is the window's
        Assert.Equal(330f, compact.RootHeight(330f));

        var small = ZuneSetupTiers.For(WidthTier.Small);
        Assert.False(small.Sheet);
        Assert.Equal(720f, small.PlateWidth);                         // Z-Setup-Small
        Assert.Equal(480f, small.PlateHeight);
        Assert.Equal(160f, small.AccentWidth);
        Assert.Equal(72f, small.QrSize);
        Assert.Equal(180f, small.ProgressColumn);
        Assert.Equal(720f, small.RootWidth(800f));
        Assert.Equal(640f - 32f, small.RootWidth(640f));              // never wider than the window less the margins
        Assert.Equal(330f - 32f, small.RootHeight(330f));

        var standard = ZuneSetupTiers.For(WidthTier.Standard);
        Assert.Equal(762f, standard.PlateWidth);
        Assert.Equal(192f, standard.AccentWidth);
        foreach (var tier in new[] { WidthTier.Huge, WidthTier.Huge4K })
        {
            var r = ZuneSetupTiers.For(tier);               // Z-Setup-Huge: the same plate; it never stretches
            Assert.Equal(standard.PlateWidth, r.PlateWidth);
            Assert.Equal(standard.PlateHeight, r.PlateHeight);
            Assert.Equal(standard.AccentWidth, r.AccentWidth);
        }
    }

    [Fact]
    public void The_release_notes_records_follow_their_boards()
    {
        foreach (var tier in AllTiers) Assert.Equal(tier, ZuneReleaseNotesTiers.For(tier).Tier);

        var compact = ZuneReleaseNotesTiers.For(WidthTier.Compact);
        Assert.False(compact.HasRail);                                // Z-ReleaseNotes-Compact: a 40 picker under the title
        Assert.Equal(40f, compact.PickerHeight);
        Assert.Equal(40f, compact.Target);
        Assert.Equal(1, compact.HighlightColumns);

        var small = ZuneReleaseNotesTiers.For(WidthTier.Small);
        Assert.True(small.HasRail);                                   // Z-ReleaseNotes-Small: the 160 rail 32 from the body, three panels
        Assert.Equal(160f, small.RailWidth);
        Assert.Equal(32f, small.FrameGap);
        Assert.Equal(0f, small.PickerHeight);
        Assert.Equal(3, small.HighlightColumns);
        Assert.True(small.CompactTitle);

        var standard = ZuneReleaseNotesTiers.For(WidthTier.Standard);
        Assert.Equal(208f, standard.RailWidth);
        Assert.Equal(64f, standard.FrameGap);
        Assert.False(standard.CompactTitle);

        foreach (var tier in new[] { WidthTier.Huge, WidthTier.Huge4K })
        {
            var r = ZuneReleaseNotesTiers.For(tier);                  // Z-ReleaseNotes-Huge: two 760 columns and a 32 gap beside the 160 rail
            Assert.True(r.HasRail);
            Assert.Equal(160f, r.RailWidth);
            Assert.Equal(32f, r.FrameGap);
            Assert.Equal(1552f, r.BodyMaxW);
        }
    }

    [Fact]
    public void The_dialog_shapes_follow_their_boards_and_never_squeeze_a_pane()
    {
        Assert.Equal(ZuneReportPreview.Collapsed, ZuneDialogTiers.For(WidthTier.Compact).Preview);
        Assert.Equal(ZuneReportPreview.Pane, ZuneDialogTiers.For(WidthTier.Small).Preview);        // Z-Dialogs-Small: the 752 card, form beside report
        Assert.Equal(ZuneReportPreview.Inline, ZuneDialogTiers.For(WidthTier.Standard).Preview);
        Assert.Equal(ZuneReportPreview.Inline, ZuneDialogTiers.For(WidthTier.Huge).Preview);       // Z-Dialogs-Huge: the 548 plate, nothing wider
        Assert.Equal(ZuneReportPreview.Inline, ZuneDialogTiers.For(WidthTier.Huge4K).Preview);
        Assert.Equal(40f, ZuneDialogTiers.For(WidthTier.Compact).Target);

        // The Small card's body is 752 less the engine's 24 either side; its pane is the board's 300.
        var full = ZuneDialogTiers.Shape(ZuneDialogTiers.For(WidthTier.Small), ZuneDialogRules.SmallBodyWidth);
        Assert.True(full.Layout.TwoPane);
        Assert.Equal(ZuneDialogRules.SmallPaneWidth, full.PreviewPaneWidth, 0.5f);

        foreach (var tier in AllTiers)
            for (float body = 200f; body <= 1100f; body += 12f)
            {
                var shape = ZuneDialogTiers.Shape(ZuneDialogTiers.For(tier), body);
                Assert.True(shape.FormWidth <= body + 0.5f, $"{tier} at {body}: the form column is {shape.FormWidth}");
                if (shape.Layout.TwoPane)
                    Assert.True(shape.PreviewPaneWidth >= ZuneDialogRules.PaneMinWidth - 0.5f, $"{tier} at {body}: the report pane is {shape.PreviewPaneWidth}");
            }

        // A Small card the window leaves too narrow for the pane (a 640 window's 592 card) keeps the report under the form.
        var narrow = ZuneDialogTiers.Shape(ZuneDialogTiers.For(WidthTier.Small), 592f - 48f);
        Assert.False(narrow.Layout.TwoPane);
        Assert.Equal(narrow.BodyWidth, narrow.FormWidth);
    }

    [Fact]
    public void The_overlay_records_are_one_per_tier_and_the_4K_chrome_row_is_the_frames_56()
    {
        foreach (var tier in AllTiers)
        {
            var r = ZuneOverlayTiers.For(tier);
            Assert.Equal(tier, r.Tier);
            Assert.Equal(ZuneTiers.Frame(tier).TitleBarH, r.ChromeRow);   // the row a panel and a sheet keep clear is the frame's for the tier
        }
        Assert.Equal(48f, ZuneOverlayTiers.For(WidthTier.Huge).ChromeRow);
        Assert.Equal(56f, ZuneOverlayTiers.For(WidthTier.Huge4K).ChromeRow);        // ZT1: Huge 4K's title bar is 56, not Huge's 48

        // A panel in a window as short as the minimum is bounded by the window less the row and the margins.
        var huge4k = ZuneOverlayTiers.For(WidthTier.Huge4K);
        Assert.Equal(330f - 56f - 2f * huge4k.PanelMargin, huge4k.PanelHeight(ZuneOverlayRules.PanelMaxHeight, 330f), 0.01f);
        var huge = ZuneOverlayTiers.For(WidthTier.Huge);
        Assert.Equal(330f - 48f - 2f * huge.PanelMargin, huge.PanelHeight(ZuneOverlayRules.PanelMaxHeight, 330f), 0.01f);

        // What the core hosts read: Small is its own band (the 752 form card); Standard and Huge the 548 plate; Compact the sheet.
        var policy = ZuneOverlayTiers.Policy;
        Assert.Same(ZuneOverlayTiers.Small.Core, policy.For(WidthTier.Small));
        Assert.Same(ZuneOverlayTiers.Standard.Core, policy.For(WidthTier.Standard));
        Assert.Same(ZuneOverlayTiers.Huge.Core, policy.For(WidthTier.Huge));
        Assert.Same(ZuneOverlayTiers.Huge.Core, policy.For(WidthTier.Huge4K));
        Assert.Same(ZuneOverlayTiers.Compact.Core, policy.For(WidthTier.Compact));
        Assert.True(policy.For(WidthTier.Compact).ContextSheet);
        Assert.Equal(ZuneDialogRules.SmallCardWidth, policy.For(WidthTier.Small).Dialog.FormWidth);
        Assert.Null(policy.For(WidthTier.Standard).Dialog.FormWidth);
        Assert.Null(policy.For(WidthTier.Huge).Dialog.FormWidth);
        Assert.Empty(policy.Validate());

        // A structure that names no Small band is what it was: Small takes Standard.
        Assert.Same(OverlayPolicy.Default.Standard, OverlayPolicy.Default.For(WidthTier.Small));
    }

    // ══ Settings, in the real frame ════════════════════════════════════════════════════════════════════════════════════════

    static readonly TierPage SettingsPageFixture = new("settings", new Shell.Route(Shell.RouteKind.Settings), "zs:page");

    [Theory, MemberData(nameof(FrameSizes))]
    public void Settings_holds_its_tier_record(string size) => AssertPage(SettingsPageFixture, size, (shell, tiers) =>
    {
        var s = Sizes[size];
        var scene = shell.Host.Scene;
        RectF At(string key) => scene.AbsoluteRect(Node(shell, key));

        var lay = ZuneSettingsTiers.Of(tiers);
        // (Compact opens on the sections list: the section is entered by a tap below, and by Settings.Open after it.)
        if (!lay.ListIsStrip) Settings.Open(Settings.Tab.Appearance);
        shell.RunFrames(90);

        Assert.Equal(tiers.Content, lay.Tier);
        var page = At("zs:page");

        // The title: the record's box (the larger size's line, reserved).
        // (Compact lands on a section's page, whose title is the section's name on its own height; the list page below holds the record's box.)
        if (!lay.ListIsStrip) Until(shell, () => MathF.Abs(At("zs:title").H - lay.TitleH) < 0.5f, $"{size}: the title box at {lay.TitleH}");

        if (lay.ListIsStrip)
        {
            // Compact: the sections list is the page without an arg.
            Assert.True(shell.Find("zs:crumb").IsNull, $"{size}: the page opens on the sections list");
            Until(shell, () => MathF.Abs(At("zs:title").H - lay.TitleH) < 0.5f, $"{size}: the title box at {lay.TitleH}");

            // The page is the sections list (one 56-tall target per section, stacked), and a tap opens the section's own page under a "‹ settings" crumb.
            Assert.True(shell.Find("zs:column").IsNull, $"{size}: the sections page draws no section column");
            float previousY = float.NegativeInfinity;
            foreach (string slug in Settings.AreaSlugs)
            {
                var link = At("zs:link:" + slug);
                Assert.True(link.H >= ZuneSettingsRules.SectionRowH - 0.5f && link.W >= 39.5f, $"{size}: the '{slug}' row is {link.W} x {link.H}, under {ZuneSettingsRules.SectionRowH}");
                Assert.True(link.Y > previousY, $"{size}: the '{slug}' row is not under the one before it");
                previousY = link.Y;
            }
            // (The tap itself is OpenTab -> a route to the section: Settings.Open takes the same route.)
            Settings.Open(Settings.Tab.Appearance);
            shell.RunFrames(90);
            Assert.False(shell.Find("zs:crumb").IsNull, $"{size}: the section's page has its crumb");
            // The open section is the route's: Back closes it, Forward reopens it, and Settings.Open lands on it.
            Shell.GoBack();
            shell.RunFrames(60);
            Assert.True(shell.Find("zs:crumb").IsNull, $"{size}: Back closes the section");
            Shell.GoForward();
            shell.RunFrames(60);
            Assert.False(shell.Find("zs:crumb").IsNull, $"{size}: Forward reopens the section");
            Settings.Open(Settings.Tab.Playback);
            shell.RunFrames(60);
            Assert.False(shell.Find("zs:crumb").IsNull, $"{size}: Settings.Open lands on its section");
            Assert.False(shell.Find("zs:tab:playback").IsNull, $"{size}: the section is the one Settings.Open named");
            Settings.Open(Settings.Tab.Appearance);
            shell.RunFrames(90);
            Assert.True(shell.Find("zs:sections").IsNull, $"{size}: the section's page draws no sections list");
        }
        else
        {
            // The section list: the record's column beside the column.
            var sections = At("zs:sections");
            Assert.Equal(lay.ListW, sections.W, 0.5f);
            foreach (string slug in Settings.AreaSlugs)
            {
                var link = At("zs:link:" + slug);
                Assert.True(link.H >= lay.ListItemH - 0.5f, $"{size}: the '{slug}' word is {link.H} tall, under {lay.ListItemH}");
            }
        }

        // The column holds the record's width and the page's edge.
        var column = At("zs:column");
        Assert.True(column.W <= lay.ColumnW + 0.5f, $"{size}: the column is {column.W}, over {lay.ColumnW}");
        Assert.True(column.X + column.W <= page.X + page.W + 0.5f, $"{size}: the column runs past the page");

        // The rows: the label column is the record's where rows do not stack; a stacked row puts its control under its label.
        var labels = shell.FindPrefix("zs:row:").Where(f => f.Key.EndsWith(":label", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(labels);
        foreach (var (node, key) in labels)
        {
            var label = scene.AbsoluteRect(node);
            var control = At(key[..^":label".Length] + ":control");
            if (lay.StacksRows) Assert.True(control.Y >= label.Y + label.H - 0.5f, $"{size}: {key}: the control is not under its label");
            else
            {
                Assert.Equal(lay.LabelW, label.W, 0.5f);
                if (!lay.RowsWrap) Assert.True(control.X >= label.X + label.W - 0.5f, $"{size}: {key}: the control is not beside its label");
            }
        }

        // The preview beside or under the column is drawn exactly where the record says.
        Assert.Equal(lay.ShowsAside, !shell.Find("zs:aside").IsNull);

        // The picker: its tiles fit the column (two to a line at Compact), the thumbnail is the record's height (plus the selection ring's constant spend).
        var cards = shell.FindPrefix("zs:picker:card:");
        Assert.True(cards.Count >= 2, $"{size}: the picker's tiles");
        float ring = 2f * (ZuneSettingsRules.SelectRing + ZuneSettingsRules.SelectGap);
        foreach (var (node, key) in cards)
        {
            var card = scene.AbsoluteRect(node);
            Assert.True(card.X + card.W <= page.X + page.W + 0.5f, $"{size}: {key} runs past the page");
            if (s.Window == WidthTier.Compact) Assert.True(card.H >= 39.5f && card.W >= 39.5f, $"{size}: {key} is {card.W} x {card.H}, under 40 x 40");
        }
        foreach (var (node, key) in shell.FindPrefix("zs:picker:thumb:"))
        {
            float h = scene.AbsoluteRect(node).H;
            Assert.True(h >= lay.PickerThumbH - 0.5f && h <= lay.PickerThumbH + ring + 0.5f, $"{size}: {key} is {h} tall, not {lay.PickerThumbH}");
        }
        if (s.Window == WidthTier.Compact)
        {
            float firstY = scene.AbsoluteRect(cards[0].Node).Y;
            int perLine = cards.Count(c => MathF.Abs(scene.AbsoluteRect(c.Node).Y - firstY) < 1f);
            Assert.True(perLine <= 2, $"{size}: {perLine} picker tiles on a Compact line");
        }

        // The swatches are the record's edge (a hit target at Compact); the short choices are chips (wrapping buttons at Compact, 40 tall).
        float edge = MathF.Max(lay.SwatchSize, lay.Target);
        foreach (var (node, key) in shell.FindPrefix("zs:swatch:"))
        {
            var r = scene.AbsoluteRect(node);
            Assert.True(MathF.Abs(r.W - edge) < 0.5f && MathF.Abs(r.H - edge) < 0.5f, $"{size}: {key} is {r.W} x {r.H}, not {edge}");
            if (s.Window == WidthTier.Compact) Assert.True(r.W >= 39.5f && r.H >= 39.5f, $"{size}: {key} is under 40 x 40");
        }
        if (s.Window == WidthTier.Compact)
        {
            var chips = shell.FindPrefix("zs:chip:");
            Assert.NotEmpty(chips);
            foreach (var (node, key) in chips)
            {
                var r = scene.AbsoluteRect(node);
                Assert.True(r.H >= 39.5f && r.W >= 39.5f, $"{size}: {key} is {r.W} x {r.H}, under 40 x 40");
                Assert.True(r.X + r.W <= page.X + page.W + 0.5f, $"{size}: {key} runs past the page");
            }
        }
    });

    /// <summary>Each Settings crossing: width from, width to, the height both sit at. The narrowing ones start on the held side of the line (Zune holds 24 below it).</summary>
    public static TheoryData<float, float, float> SettingsCrossings() => new()
    {
        { 1007f, 1008f, 760f },    // 1008: Small -> Standard (180 -> 220, the 520 column -> 660, the 72 title -> the 130 panorama)
        { 1008f, 983f, 760f },     // 984 on the way down: Standard -> Small
    };

    [Theory, MemberData(nameof(SettingsCrossings))]
    public void A_settings_crossing_keeps_the_page_and_its_parts_and_the_ones_that_resize_glide(float fromW, float toW, float height) => Mounted(shell =>
    {
        var scene = shell.Host.Scene;
        try
        {
            shell.RunFrames(20);
            Settings.Open(Settings.Tab.Playback);
            Until(shell, () => !shell.Find("zs:page").IsNull, "the Settings page on screen");
            shell.RunFrames(60);
            var before = Tiers;
            Assert.Equal(ZuneTierAt(fromW), before.Window);

            var parts = new List<FramePart>();
            foreach (string key in new[] { "zs:page", "zs:title", "zs:body", "zs:sections", "zs:tab:playback", "zs:column" })
            {
                var n = Node(shell, key);
                parts.Add(new FramePart(key, n, scene.Bounds(n)));
            }
            foreach (var (node, key) in shell.FindPrefix("zs:link:")) parts.Add(new FramePart(key, node, scene.Bounds(node)));
            Assert.Equal(6 + Settings.AreaSlugs.Length, parts.Count);

            // The resized frame: the old tier, laid out at the new size (a narrowing drag settles through the modal loop).
            bool narrowing = toW < fromW;
            shell.Window.Composited = true;
            if (narrowing)
            {
                shell.Window.InModalLoop = true;
                shell.Window.SizedInModalLoop = true;
                shell.Resize(toW, height);
                shell.Host.Paint(0, keepAlive: true);
                shell.Window.InModalLoop = false;
                shell.Window.SizedInModalLoop = false;
                shell.Host.Paint(0, keepAlive: true);
            }
            else
            {
                shell.Resize(toW, height);
                shell.Host.Paint(0, keepAlive: true);
            }
            Assert.Equal(before.Window, Tiers.Window);
            parts = parts.ConvertAll(p => p with { Local = scene.Bounds(p.Node) });

            // The crossing frame: the new tier commits here, and nothing is thrown away.
            if (narrowing) shell.Host.Paint(0, keepAlive: true); else shell.Host.RunFrame();
            Assert.True(ZuneTierAt(toW) == Tiers.Window, $"the window tier is {Tiers.Window}, not {ZuneTierAt(toW)}");
            string what = $"{fromW} -> {toW}";
            foreach (var p in parts)
            {
                Assert.True(scene.IsLive(p.Node), $"{what}: {p.Name} was thrown away on the crossing");
                Assert.True(shell.Find(p.Name).Equals(p.Node), $"{what}: {p.Name} was remounted on the crossing");
            }
            // What changes SIZE glides on a size channel: the title scales, the list's box and the column resize. (A position-only slide snaps under the engine's reflow-shove rule.)
            foreach (var p in parts.Where(p => p.Name is "zs:title" or "zs:sections" or "zs:column")) AssertKeptGliding(shell, p, what);

            // Settled: the new tier's record is what is on screen.
            shell.RunFrames(60);
            var lay = ZuneSettingsTiers.Of(Tiers);
            Assert.Equal(lay.TitleH, scene.AbsoluteRect(Node(shell, "zs:title")).H, 0.5f);
            var sections = scene.AbsoluteRect(Node(shell, "zs:sections"));
            if (!lay.ListIsStrip) Assert.Equal(lay.ListW, sections.W, 0.5f);
        }
        finally
        {
            Shell.GoTo(new Shell.Route(Shell.RouteKind.Home));
            shell.RunFrames(10);
        }
    }, fromW, height);
}

/// <summary>The screens that mount alone (the release notes, the setup plate) and the overlays at every tier: a view under Zune's composition, the tiers resolved from the window it is mounted at.</summary>
public sealed class ZuneScreensTierMountedTests(ShellHost host)
{
    static ViewHarnessOptions UnderZune(float width, float height) => new() { Composition = ZuneTheme.Create(), WidthDip = width, HeightDip = height };

    static RectF Box(ViewHarness h, string key) => h.AbsoluteBounds(h.Node(key));

    /// <summary>The sizes: the plan's floor, the Compact board, the edges and their held sides, the boards' desktop sizes and the 4K.</summary>
    public static TheoryData<float, float> Sizes() => new()
    {
        { 300f, 700f }, { 400f, 860f }, { 639f, 860f }, { 640f, 860f }, { 800f, 600f }, { 1007f, 860f }, { 1008f, 860f },
        { 1440f, 960f }, { 1919f, 1000f }, { 1920f, 1080f }, { 2560f, 1440f }, { 3199f, 1440f }, { 3200f, 1440f }, { 3840f, 2160f },
    };

    /// <summary>The edges and their crossings: width from, width to (the held side first when it narrows).</summary>
    public static TheoryData<float, float> Crossings() => new() { { 639f, 640f }, { 640f, 615f }, { 1007f, 1008f }, { 1008f, 983f } };

    // ══ the release notes ═════════════════════════════════════════════════════════════════════════════════════════════════

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

    static WhatsNewPageProps ReleasePage()
    {
        var doc = Doc("0.3.0", "h1", "h2");
        var index = new ReleaseNotes.ReleaseNotesIndex
        {
            Releases =
            [
                new ReleaseNotes.ReleaseNotesIndexEntry { Version = "0.3.0", Name = "Crest", Date = "2026-10-01" },
                new ReleaseNotes.ReleaseNotesIndexEntry { Version = "0.2.9", Name = "Dune", Date = "2026-09-01" },
            ],
        };
        var view = new ReleaseNotes.ReleaseNotesView([new ReleaseNotes.ReleaseEntry(doc, null, IsYou: true, IsUnread: false)],
            doc.Highlights.Select(h => new ReleaseNotes.HighlightItem(h, doc, null)).ToArray(), index, "0.3.0", "0.2.9");
        return new WhatsNewPageProps
        {
            State = WhatsNewState.Loaded, View = view, OnlyLatest = false, RunningVersion = "0.3.0",
            ShowOnlyLatest = static () => { }, CopyLink = static _ => { }, OpenHighlight = static _ => { },
            OpenUrl = static _ => { }, OpenStoreListing = static () => { }, OpenRelease = static _ => { },
        };
    }

    const string H1 = "zrn:hl:0.3.0:h1", H2 = "zrn:hl:0.3.0:h2";

    [Theory, MemberData(nameof(Sizes))]
    public void The_whats_new_page_holds_its_tier_record(float width, float height)
        => ViewHarness.With(host, ViewKeys.WhatsNewPage, ReleasePage(), h =>
        {
            h.RunFrames(40);
            var tiers = h.Facts.Peek().Tiers;
            var lay = ZuneReleaseNotesTiers.Of(tiers);
            string at = $"{width}x{height}";

            Assert.Equal(lay.TitleH, Box(h, "zrn:title").H, 0.5f);
            Assert.Equal(lay.HasRail, h.Contains("zrn:rail"));
            Assert.Equal(!lay.HasRail, h.Contains("zrn:picker"));

            var body = Box(h, "zrn:body");
            Assert.True(body.X + body.W <= width + 0.5f, $"{at}: the body runs past the window");
            Assert.True(body.W <= (float.IsNaN(lay.BodyMaxW) ? width : lay.BodyMaxW) + 0.5f, $"{at}: the body is {body.W}, over its cap {lay.BodyMaxW}");
            if (lay.HasRail)
            {
                var rail = Box(h, "zrn:rail");
                Assert.Equal(lay.RailWidth, rail.W, 0.5f);
                Assert.True(rail.X + rail.W <= width + 0.5f, $"{at}: the rail runs past the window");
                Assert.True(rail.X - (body.X + body.W) >= lay.FrameGap - 0.5f, $"{at}: the rail is {rail.X - (body.X + body.W)} from the body, under {lay.FrameGap}");
            }
            else
            {
                var picker = Box(h, "zrn:picker");
                Assert.True(picker.H >= lay.PickerHeight - 0.5f, $"{at}: the picker is {picker.H} tall, under {lay.PickerHeight}");
                Assert.True(picker.X + picker.W <= width + 0.5f, $"{at}: the picker runs past the window");
            }

            // The panels: the record's columns (the grid has one track per column; two fixture cards fill two of them, or stack in one).
            var a = Box(h, H1);
            var b = Box(h, H2);
            if (lay.HighlightColumns >= 2) Assert.True(MathF.Abs(a.Y - b.Y) < 1f && b.X > a.X + 1f, $"{at}: the panels sit side by side");
            else Assert.True(MathF.Abs(a.X - b.X) < 1f && b.Y > a.Y + 1f, $"{at}: the panels stack");
            Assert.True(b.X + b.W <= width + 0.5f, $"{at}: a panel runs past the window");
            if (lay.Target > 0f)   // Compact: a panel is a 40 target and more
                Assert.True(a.H >= lay.Target - 0.5f && b.H >= lay.Target - 0.5f, $"{at}: a panel is under {lay.Target} tall");
        }, UnderZune(width, height));

    [Theory, MemberData(nameof(Crossings))]
    public void A_whats_new_crossing_keeps_the_head_the_body_and_the_panels(float fromW, float toW)
        => ViewHarness.With(host, ViewKeys.WhatsNewPage, ReleasePage(), h =>
        {
            h.RunFrames(40);
            string[] keys = ["zrn:head", "zrn:title", "zrn:frame", "zrn:body", "zrn:hero", H1, H2];
            var kept = keys.Select(k => (Key: k, Node: h.Node(k))).ToArray();
            var scene = h.Shell.Host.Scene;
            var tierBefore = h.Facts.Peek().Tiers.Window;

            h.Resize(toW, 860f);
            h.RunFrames(40);
            Assert.NotEqual(tierBefore, h.Facts.Peek().Tiers.Window);
            foreach (var (key, node) in kept)
            {
                Assert.True(scene.IsLive(node), $"{fromW} -> {toW}: {key} was thrown away");
                Assert.True(h.Find(key).Equals(node), $"{fromW} -> {toW}: {key} was remounted");
            }
            // The release switch is the new tier's: a rail from Small up, a picker below.
            var lay = ZuneReleaseNotesTiers.Of(h.Facts.Peek().Tiers);
            Assert.Equal(lay.HasRail, h.Contains("zrn:rail"));
            Assert.Equal(!lay.HasRail, h.Contains("zrn:picker"));
        }, UnderZune(fromW, 860f));

    // ══ the setup plate ═══════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The sign-in flows with nothing behind them: a facet and its facts.</summary>
    sealed class SignInFixture(Setup.SignInFacet facet) : ISetupSignInHost
    {
        public SetupSignInFacts Facts()
            => new(facet, Handed: false, Minting: false, BusyMessageKey: Strings.Auth.WaitingApproval,
                FailureKey: facet == Setup.SignInFacet.Failed ? Setup.SignInRules.LocDenied : "", NeedsPairingCode: false, UserCode: "");

        public SetupPairingFacts Pairing() => new(false, true, false, "", "", 0L);
        public SetupLoginStepFacts Step() => new(1, false);
        public bool NeedsPairingChallenge(Setup.WizardPage activePage) => false;
        public SetupAccountFacts Account() => new("Fixture Person", null, Premium: true);
        public void StartBrowser() { }
        public void StartPairing() { }
        public void CancelBrowser() { }
        public void Retry() { }
        public void SwitchAccount() { }
        public void CloseDoor() { }
        public void OpenSignUp() { }
        public void OpenPremium() { }
        public void OpenExternal(string uri) { }
    }

    static SetupWizardProps WizardProps(Setup.WizardModel session) => new()
    {
        Session = session,
        Terms = new SetupTermsProps { Session = session, OpenPrivacy = static () => { }, Consent = new SetupConsent(3, static () => 0, static _ => { }) },
        SignIn = new SetupSignInPageProps { Session = session, Host = new SignInFixture(Setup.SignInFacet.Idle) },
        Theme = new SetupThemeProps { ThemePicker = static () => new BoxEl { Key = "test:theme-picker" }, ThemeOptions = static () => new BoxEl { Key = "test:theme-options" } },
        LocalPlayback = new SetupLocalPlaybackProps { Session = session, OpenSignature = static (_, _) => { }, RevealFolder = static _ => { } },
    };

    [Theory, MemberData(nameof(Sizes))]
    public void The_setup_plate_holds_its_tier_record(float width, float height)
        => host.Run(() =>
        {
            var session = new Setup.WizardModel(Setup.WizardEntry.FirstRun, signedIn: false);
            try
            {
                using var h = ViewHarness.Mount(ViewKeys.SetupWizard, WizardProps(session), UnderZune(width, height));
                h.RunFrames(40);
                var lay = ZuneSetupTiers.Of(h.Facts.Peek().Tiers);
                string at = $"{width}x{height}";

                // The root is the record's size: the plate (never past the window less its margins), or the sheet (the window's).
                var root = Box(h, "zsetup:root");
                Assert.Equal(lay.RootWidth(width), root.W, 0.5f);
                Assert.Equal(lay.RootHeight(height), root.H, 0.5f);
                Assert.True(root.X >= -0.5f && root.X + root.W <= width + 0.5f && root.Y >= -0.5f && root.Y + root.H <= height + 0.5f, $"{at}: the plate runs outside the window");

                // The accent: a column of the record's width, or at Compact a strip of its height.
                var accent = Box(h, "zsetup:accent");
                if (lay.Sheet) Assert.Equal(lay.AccentStripHeight, accent.H, 0.5f);
                else Assert.Equal(lay.AccentWidth, accent.W, 0.5f);

                // The plate holds the footer; the primary command is on the plate, and at Compact a 40 target.
                var plate = Box(h, "zsetup:plate");
                var primary = Box(h, "zsetup:footer:primary");
                Assert.True(primary.X >= plate.X - 0.5f && primary.X + primary.W <= plate.X + plate.W + 0.5f, $"{at}: the primary command runs outside the plate");
                Assert.True(primary.Y + primary.H <= plate.Y + plate.H + 0.5f, $"{at}: the primary command runs below the plate");
                if (lay.Target > 0f)
                {
                    Assert.True(primary.H >= 39.5f && primary.W >= 39.5f, $"{at}: the primary command is {primary.W} x {primary.H}, under 40 x 40");
                    Assert.True(h.Contains("zsetup:page:terms"));
                }
            }
            finally { session.End(); }
        });

    [Theory, MemberData(nameof(Crossings))]
    public void A_setup_crossing_keeps_the_plate_its_accent_and_the_page(float fromW, float toW)
        => host.Run(() =>
        {
            var session = new Setup.WizardModel(Setup.WizardEntry.FirstRun, signedIn: false);
            try
            {
                using var h = ViewHarness.Mount(ViewKeys.SetupWizard, WizardProps(session), UnderZune(fromW, 860f));
                h.RunFrames(40);
                string[] keys = ["zsetup:root", "zsetup:plate", "zsetup:accent", "zsetup:content", "zsetup:footer", "zsetup:page:terms"];
                var kept = keys.Select(k => (Key: k, Node: h.Node(k))).ToArray();
                var scene = h.Shell.Host.Scene;
                var tierBefore = h.Facts.Peek().Tiers.Window;

                h.Resize(toW, 860f);
                h.RunFrames(40);
                Assert.NotEqual(tierBefore, h.Facts.Peek().Tiers.Window);
                foreach (var (key, node) in kept)
                {
                    Assert.True(scene.IsLive(node), $"{fromW} -> {toW}: {key} was thrown away");
                    Assert.True(h.Find(key).Equals(node), $"{fromW} -> {toW}: {key} was remounted");
                }
                var lay = ZuneSetupTiers.Of(h.Facts.Peek().Tiers);
                Assert.Equal(lay.RootWidth(toW), Box(h, "zsetup:root").W, 0.5f);
            }
            finally { session.End(); }
        });

    // ══ the overlays ══════════════════════════════════════════════════════════════════════════════════════════════════════

    [Theory, MemberData(nameof(Sizes))]
    public void The_offline_strip_is_the_tiers_height_and_its_retry_a_hit_target_at_Compact(float width, float height)
        => ViewHarness.With(host, ViewKeys.OverlayOfflineStrip, new OfflineStripProps { OnRetry = static () => { } }, h =>
        {
            h.RunFrames(10);
            var layout = ZuneOverlayTiers.Of(h.Facts.Peek().Tiers);
            var strip = Box(h, ZuneOverlayRules.OfflineStripKey);
            Assert.Equal(layout.StripHeight, strip.H, 0.5f);
            Assert.True(strip.X + strip.W <= width + 0.5f, $"{width}: the strip runs past the window");
            if (layout.Tier == WidthTier.Compact)
            {
                var scene = h.Shell.Host.Scene;
                foreach (var n in h.Subtree(scene.Root).Where(n => scene.Interaction(n).Role == AutomationRole.Button))
                {
                    var r = scene.AbsoluteRect(n);
                    Assert.True(r.H >= 39.5f && r.W >= 39.5f, $"{width}: the retry word is {r.W} x {r.H}, under 40 x 40");
                }
            }
        }, UnderZune(width, height));

    [Theory, MemberData(nameof(Sizes))]
    public void The_logout_card_is_the_tiers_width_and_its_commands_are_targets_at_Compact(float width, float height)
        => ViewHarness.With(host, ViewKeys.DialogLogoutConfirm, new LogoutConfirmProps { Cancel = static () => { }, Confirm = static () => { } }, h =>
        {
            h.RunFrames(10);
            var layout = ZuneOverlayTiers.Of(h.Facts.Peek().Tiers);
            var plate = Box(h, ZuneOverlayRules.PlateKey);
            Assert.Equal(layout.CardWidth(ZuneOverlayRules.LogoutWidth, ZuneOverlayRules.LogoutMaxWidth, width), plate.W, 0.5f);
            Assert.True(plate.X >= -0.5f && plate.X + plate.W <= width + 0.5f, $"{width}: the card runs outside the window");
            var commands = Box(h, ZuneOverlayRules.CommandsKey);
            Assert.True(commands.X + commands.W <= plate.X + plate.W + 0.5f, $"{width}: the commands run outside the card");
            if (layout.Tier == WidthTier.Compact)
            {
                var scene = h.Shell.Host.Scene;
                foreach (var n in h.Subtree(scene.Root).Where(n => scene.Interaction(n).Role == AutomationRole.Button))
                    Assert.True(scene.AbsoluteRect(n).H >= 39.5f, $"{width}: a Compact command is under 40 tall");
            }
        }, UnderZune(width, height));

    sealed class NoNotifications : INotificationsHost
    {
        public INotificationVerbs Verbs => NotificationVerbs.Shared;
        public void SetFilter(NotifyFilter filter) { }
        public void ToggleActivity(in Notification n) { }
        public void Undo(in Notification n) { }
        public NotificationActivityTarget? ActivityTarget(in Notification n) => new("Playlist", null);
        public Func<ContextMenuModel?>? ReleaseMenu(in Notification n) => null;
    }

    static NotificationsProps Panel() => new()
    {
        Filter = NotifyFilter.All,
        Rows =
        [
            new("s1", 1_000, true, NotifyCategory.Social, Title: "Ada followed you"),
            new("a1", 3_000, false, NotifyCategory.Activity, Title: "Added 3 songs", Body: "to Road trip"),
        ],
        Now = 10_000, ExpandedId = "", EmptyText = "Nothing new", CanUndo = false, UpdateProgress = new FloatSignal(0f), PendingEdits = new Signal<int>(0), Host = new NoNotifications(),
    };

    /// <summary>The panel in a window as short as the minimum: bounded by the window less the tier's title row and its margins. At Huge 4K the row is 56.</summary>
    [Theory]
    [InlineData(300f, 330f)]
    [InlineData(800f, 330f)]
    [InlineData(1920f, 330f)]
    [InlineData(3840f, 330f)]
    public void The_notification_panel_keeps_clear_of_the_tiers_title_row(float width, float height)
        => ViewHarness.With(host, ViewKeys.OverlayNotifications, Panel(), h =>
        {
            h.RunFrames(60);
            var layout = ZuneOverlayTiers.Of(h.Facts.Peek().Tiers);
            var panel = Box(h, ZuneOverlayRules.PanelKey);
            float room = height - layout.ChromeRow - 2f * layout.PanelMargin;
            Assert.True(panel.H <= room + 0.5f, $"{width}x{height}: the panel is {panel.H} tall in {room} of room under a {layout.ChromeRow} title row");
            Assert.True(panel.W <= width - 2f * layout.PanelMargin + 0.5f, $"{width}x{height}: the panel is {panel.W} wide");
        }, UnderZune(width, height));
}
