// ── Wavee.Tests/OnMediaMenuShapeTests.cs — the ⋯ menu over the video is GROUPED ──────────────────────────────────────
//
// The on-media transport's ⋯ menu used to be one flat list: the placement ladder, then aspect, then quality, then
// speed, four groups behind hairline separators and ~25 rows deep. `MenuFlyoutPresenter` caps its scroller at 468 DIP
// (about eleven rows), so the bottom of the menu was not merely ugly, it was SCROLLED OUT OF SIGHT — a reported "720p
// is cut off". The three settings groups are now cascading sub-menus, which is also the shape the engine's own
// transport ships for the identical menu (`MediaPlayerElement`: Aspect ratio / Playback speed / Quality).
//
// Two things are worth a gate rather than an eyeball, because both are silent when they break:
//   • the placement EXTRAS ("Always on top", "Turn off video") must stay at the BOTTOM. `Video.PlacementMenu` returns
//     them bundled behind their own separators, so `MoreMenu` splits that list at its first separator and re-appends
//     the tail after the cascades. Get the split wrong and "Turn off video" slides up under three settings menus and
//     reads as one of them.
//   • a sub-menu row whose `SubItems` is non-null but EMPTY still opens — `MenuFlyoutPresenter.OpenSub` only checks
//     for null — so it yields a blank popup. Every cascade here is non-empty by construction; this pins that.
//
// `Platform`'s settings store is process state, so each fact installs a MemoryAppSettings and restores it, exactly as
// `PlatformSettingsTests.WithStore` does.

using System;
using System.Collections.Generic;
using System.Linq;
using FluentGpu.Controls;
using FluentGpu.Localization;
using Wavee;
using Xunit;

namespace Wavee.Tests;

[Collection(PlatformCollection.Name)]
public class OnMediaMenuShapeTests
{
    static void WithStore(Action body)
    {
        Platform.UseSettings(new MemoryAppSettings());
        try { body(); }
        finally { Platform.UseSettings(null); }
    }

    static IReadOnlyList<MenuFlyoutItem> Cascades(IReadOnlyList<MenuFlyoutItem> items)
        => items.Where(i => i.Kind == MenuItemKind.SubMenu).ToArray();

    /// <summary>THE regroup: exactly three cascades, in the engine transport's order, and each one actually carries
    /// rows. An empty `SubItems` is the one shape the presenter will happily open as a blank popup.</summary>
    [Fact]
    public void The_three_settings_groups_are_non_empty_cascades()
    {
        WithStore(() =>
        {
            var items = OnMedia.MoreMenu();
            var subs = Cascades(items);

            Assert.Equal(3, subs.Count);
            Assert.Equal(
                new[] { Loc.Get(Strings.Player.AspectMenu), Loc.Get(Strings.Player.QualityMenu), Loc.Get(Strings.Player.SpeedMenu) },
                subs.Select(s => s.Label).ToArray());

            foreach (var sub in subs)
            {
                Assert.NotNull(sub.SubItems);
                Assert.NotEmpty(sub.SubItems!);
            }
        });
    }

    /// <summary>Each cascade is a RADIO set (separators aside) with exactly one mark, so "what is it set to" survives
    /// being hidden one level down. Quality is Auto plus whatever rungs the live manifest offers; with no player open
    /// that is Auto alone, and Auto is still the checked one. Aspect now carries a separator between its four modes
    /// and the four numeric-ratio rows below it (see the dedicated shape fact below), so this fact skips separators
    /// rather than asserting every row is a radio.</summary>
    [Fact]
    public void Every_cascade_carries_exactly_one_checked_radio()
    {
        WithStore(() =>
        {
            foreach (var sub in Cascades(OnMedia.MoreMenu()))
            {
                var rows = sub.SubItems!.Where(r => !r.IsSeparator).ToArray();
                Assert.All(rows, r => Assert.Equal(MenuItemKind.Radio, r.Kind));
                Assert.Equal(1, rows.Count(r => r.IsChecked));
            }
        });
    }

    /// <summary>The Aspect cascade's exact shape: the four modes, one separator, then the four numeric ratios — in
    /// that order, matching the engine's own (suppressed) transport list.</summary>
    [Fact]
    public void The_aspect_cascade_is_four_rows_a_separator_and_four_rows()
    {
        WithStore(() =>
        {
            var aspect = Cascades(OnMedia.MoreMenu()).Single(s => s.Label == Loc.Get(Strings.Player.AspectMenu));
            var rows = aspect.SubItems!;

            Assert.Equal(9, rows.Count);
            for (int i = 0; i < 4; i++) Assert.Equal(MenuItemKind.Radio, rows[i].Kind);
            Assert.True(rows[4].IsSeparator);
            for (int i = 5; i < 9; i++) Assert.Equal(MenuItemKind.Radio, rows[i].Kind);
        });
    }

    /// <summary>Choosing Custom at 4:3 checks exactly the "4:3" ratio row, and none of the four fixed modes.</summary>
    [Fact]
    public void Choosing_custom_4_3_checks_only_the_4_3_row()
    {
        WithStore(() =>
        {
            Video.Prefs.SetAspect(Platform.Settings, Video.AspectPreference.Custom, 4.0 / 3.0);
            var aspect = Cascades(OnMedia.MoreMenu()).Single(s => s.Label == Loc.Get(Strings.Player.AspectMenu));
            var rows = aspect.SubItems!;

            Assert.Equal(1, rows.Count(r => r.IsChecked));
            Assert.True(rows[6].IsChecked, "the 4:3 row (index 6) should be the checked one");
            Assert.Equal(Loc.Get(Strings.Player.AspectRatio43), rows[6].Label);
        });
    }

    /// <summary>The placement EXTRAS stay last. `Video.PlacementMenu` bundles them behind their own separators, and
    /// `MoreMenu` re-appends that tail AFTER the cascades — so nothing from the placement ladder may appear between
    /// the first cascade and the end, and no cascade may follow a placement extra.</summary>
    [Fact]
    public void The_placement_rows_lead_and_its_extras_trail_the_cascades()
    {
        WithStore(() =>
        {
            var items = OnMedia.MoreMenu();
            int first = items.ToList().FindIndex(i => i.Kind == MenuItemKind.SubMenu);
            int last = items.ToList().FindLastIndex(i => i.Kind == MenuItemKind.SubMenu);

            Assert.True(first > 0, "the placement radios lead the menu");
            Assert.Equal(first + 2, last);                       // the three cascades are contiguous

            // Everything before the cascades is placement, and none of it is a separator-led extra.
            for (int i = 0; i < first; i++)
                Assert.NotEqual(MenuItemKind.SubMenu, items[i].Kind);
        });
    }

    /// <summary>A separator never leads, never doubles and never trails — the grammar `Actions.MenuRules` already
    /// enforces elsewhere, applied to the one menu that now splices two lists together.</summary>
    [Fact]
    public void Separators_never_lead_double_or_trail()
    {
        WithStore(() =>
        {
            var items = OnMedia.MoreMenu();
            Assert.False(items[0].IsSeparator);
            Assert.False(items[^1].IsSeparator);
            for (int i = 1; i < items.Count; i++)
                Assert.False(items[i].IsSeparator && items[i - 1].IsSeparator, "two separators in a row at " + i);
        });
    }

    static IReadOnlyList<MenuFlyoutItem> QualityCascade()
        => Cascades(OnMedia.MoreMenu()).Single(s => s.Label == Loc.Get(Strings.Player.QualityMenu)).SubItems!;

    /// <summary>F176: the on-media Quality rows go through the SAME writer as Settings (persist, then apply live). The
    /// session-side pin alone died with the session — a rebuild derives its ceiling from the stored key and starts Auto — so
    /// a pick that never reached the store reverted on the next track and the radio kept reading the old value.</summary>
    [Fact]
    public void Pinning_a_quality_persists_the_height_and_the_radio_follows_the_store()
    {
        WithStore(() =>
        {
            Assert.Equal(0, Platform.Settings.Get(Platform.Keys.VideoQuality));
            Assert.True(QualityCascade()[0].IsChecked, "Auto is checked while nothing is pinned");

            Playback.Video.PinQuality(720);

            Assert.Equal(720, Platform.Settings.Get(Platform.Keys.VideoQuality));
            Assert.False(QualityCascade()[0].IsChecked, "a persisted pin un-checks Auto the next time the menu opens");
        });
    }

    /// <summary>The Auto row is a real pick too: it clears the stored pin, and a negative height is Auto, never a stored
    /// nonsense value.</summary>
    [Fact]
    public void The_auto_row_clears_the_stored_pin()
    {
        WithStore(() =>
        {
            Playback.Video.PinQuality(1080);
            Assert.Equal(1080, Platform.Settings.Get(Platform.Keys.VideoQuality));

            QualityCascade()[0].Invoke!();

            Assert.Equal(0, Platform.Settings.Get(Platform.Keys.VideoQuality));
            Assert.True(QualityCascade()[0].IsChecked);

            Playback.Video.PinQuality(-5);
            Assert.Equal(0, Platform.Settings.Get(Platform.Keys.VideoQuality));
        });
    }
}
