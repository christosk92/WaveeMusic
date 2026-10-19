// ── Wavee.Tests/Theming/WinUiRegistryCompletenessTests.cs — every key the contract declares has a picture in WinUI (P6 W0) ─────────────
//
// A copy of Zune's (ZuneRegistryCompletenessTests), the twin of Classic's RegistryCompletenessTests. The contract is the static fields of
// `ViewKeys` and `StyleKeys`, read by reflection, so a key a PR adds is covered without touching this file. The INSTALLED WinUI (`WinUiTheme.Create().Registry`: WinUI's own views over Classic's stand-in,
// `ViewRegistryBuilder.AddMissing`) must hold a picture for every view key and a style for every style key, and every template id is WinUI's
// own (`winui.*`), a Common block's (`common.*`) or one of the documented stand-ins below (Classic's, `classic.*`). WinUI's OWN registry
// (`new WinUiStructure()`) may leave a key to the stand-in only when it is listed in `DeliberateStandIns` WITH its reason: a new key, or a WinUI
// family that stops registering one, fails here instead of silently drawing Classic's picture.
//
// `Themes` is process state: the test class is in the platform collection and resets it around every fact.

using System.Reflection;
using FluentGpu.Theming;
using Wavee.Themes.Classic;
using Wavee.Themes.WinUi;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Xunit;

namespace Wavee.Tests.Theming;

[Collection(PlatformCollection.Name)]
public sealed class WinUiRegistryCompletenessTests : IDisposable
{
    public WinUiRegistryCompletenessTests() => ThemeHost.Reset();

    public void Dispose() => ThemeHost.Reset();

    /// <summary>The view keys WinUI deliberately leaves to Classic's picture (key id -> the P6 lane that draws it). Every other key is WinUI's own
    /// (`winui.*`) or a Common block's. A family lane deletes its lines in the PR that registers its keys; W9 empties the list (P6 exit 1).</summary>
    static readonly Dictionary<string, string> DeliberateStandIns = new(StringComparer.Ordinal)
    {
        // W3: the lists (0)
        // W4a: the player and the panel (16)
        ["Panel.Friends"] = "W4a",  // ViewKeys.PanelFriends
        ["Panel.LyricsHeader"] = "W4a",  // ViewKeys.PanelLyricsHeader
        ["Panel.NextUp"] = "W4a",  // ViewKeys.PanelNextUp
        ["Player.Bar"] = "W4a",  // ViewKeys.PlayerBar
        ["Player.ChapterTip"] = "W4a",  // ViewKeys.ChapterTip
        ["Player.DeckStyleFlyout"] = "W4a",  // ViewKeys.DeckStyleFlyout
        ["Player.DevicePicker"] = "W4a",  // ViewKeys.DevicePicker
        ["Player.LyricsPeek"] = "W4a",  // ViewKeys.LyricsPeek
        ["Player.NowPlayingHero"] = "W4a",  // ViewKeys.NowPlayingHero
        ["Player.OnMediaTransport"] = "W4a",  // ViewKeys.OnMediaTransport
        ["Player.SeekChapters"] = "W4a",  // ViewKeys.SeekChapters
        ["Player.SeekRail"] = "W4a",  // ViewKeys.SeekRail
        ["Player.SeekReadout"] = "W4a",  // ViewKeys.SeekReadout
        ["Player.TimeLabel"] = "W4a",  // ViewKeys.TimeLabel
        ["Queue.Panel"] = "W4a",  // ViewKeys.QueuePanel
        ["Shell.Panel"] = "W4a",  // ViewKeys.ShellPanel
        // W4b: the stage (13)
        ["Stage.Backdrop"] = "W4b",  // ViewKeys.StageBackdrop
        ["Stage.Caption"] = "W4b",  // ViewKeys.StageCaption
        ["Stage.Face"] = "W4b",  // ViewKeys.StageFace
        ["Stage.FaceScrim"] = "W4b",  // ViewKeys.StageFaceScrim
        ["Stage.Gallery"] = "W4b",  // ViewKeys.StageGallery
        ["Stage.Identity"] = "W4b",  // ViewKeys.StageIdentity
        ["Stage.NowPlayingCard"] = "W4b",  // ViewKeys.StageNowPlayingCard
        ["Stage.Options"] = "W4b",  // ViewKeys.StageOptions
        ["Stage.Pane"] = "W4b",  // ViewKeys.StagePane
        ["Stage.Root"] = "W4b",  // ViewKeys.StageRoot
        ["Stage.Spectrum"] = "W4b",  // ViewKeys.StageSpectrum
        ["Stage.TopBar"] = "W4b",  // ViewKeys.StageTopBar
        ["Stage.Transport"] = "W4b",  // ViewKeys.StageTransport
        // W4c: the rest of the stage (11)
        ["Lyrics.View"] = "W4c",  // ViewKeys.LyricsView
        ["Queue.StagePane"] = "W4c",  // ViewKeys.QueueStagePane
        ["Stage.ArtistName"] = "W4c",  // ViewKeys.StageArtistName
        ["Stage.ArtistPane"] = "W4c",  // ViewKeys.StageArtistPane
        ["Stage.Hero"] = "W4c",  // ViewKeys.StageHero
        ["Video.Docked"] = "W4c",  // ViewKeys.VideoDocked
        ["Video.Fullscreen"] = "W4c",  // ViewKeys.VideoFullscreen
        ["Video.JoinPoster"] = "W4c",  // ViewKeys.VideoJoinPoster
        ["Video.Pip"] = "W4c",  // ViewKeys.VideoPip
        ["Video.PlacementMenu"] = "W4c",  // ViewKeys.VideoPlacementMenu
        ["Video.PopOut"] = "W4c",  // ViewKeys.VideoPopOut
        // W5: discovery (26)
        ["Browse.Charts"] = "W5",  // ViewKeys.BrowseCharts
        ["Home.Charts"] = "W5",  // ViewKeys.HomeCharts
        ["Home.Daylist"] = "W5",  // ViewKeys.HomeDaylist
        ["Home.DaylistClock"] = "W5",  // ViewKeys.HomeDaylistClock
        ["Home.FacetRow"] = "W5",  // ViewKeys.HomeFacetRow
        ["Home.Zone.BrowseTiles"] = "W5",  // ViewKeys.HomeZoneBrowseTiles
        ["Home.Zone.ClusterCards"] = "W5",  // ViewKeys.HomeZoneClusterCards
        ["Home.Zone.ContinueEpisodes"] = "W5",  // ViewKeys.HomeZoneContinueEpisodes
        ["Home.Zone.CoverShelf"] = "W5",  // ViewKeys.HomeZoneCoverShelf
        ["Home.Zone.EmptyFacet"] = "W5",  // ViewKeys.HomeZoneEmptyFacet
        ["Home.Zone.EpisodeLead"] = "W5",  // ViewKeys.HomeZoneEpisodeLead
        ["Home.Zone.EpisodeRows"] = "W5",  // ViewKeys.HomeZoneEpisodeRows
        ["Home.Zone.MixedCovers"] = "W5",  // ViewKeys.HomeZoneMixedCovers
        ["Home.Zone.PodcastGroups"] = "W5",  // ViewKeys.HomeZonePodcastGroups
        ["Home.Zone.RadioShelf"] = "W5",  // ViewKeys.HomeZoneRadioShelf
        ["Home.Zone.RecentGrid"] = "W5",  // ViewKeys.HomeZoneRecentGrid
        ["Home.Zone.ReleaseList"] = "W5",  // ViewKeys.HomeZoneReleaseList
        ["Home.Zone.ShowGrid"] = "W5",  // ViewKeys.HomeZoneShowGrid
        ["Home.Zone.VideoTiles"] = "W5",  // ViewKeys.HomeZoneVideoTiles
        ["Home.Zone.WideTiles"] = "W5",  // ViewKeys.HomeZoneWideTiles
        ["Pages.Browse"] = "W5",  // ViewKeys.BrowseDirectory
        ["Pages.BrowseCategory"] = "W5",  // ViewKeys.BrowseCategory
        ["Pages.BrowseSection"] = "W5",  // ViewKeys.BrowseSection
        ["Pages.Home"] = "W5",  // ViewKeys.HomePage
        ["Pages.HomeSection"] = "W5",  // ViewKeys.HomeSection
        ["Pages.Search"] = "W5",  // ViewKeys.SearchPage
        // W6a: album and artist (21)
        ["Album.ArtistFlyout"] = "W6a",  // ViewKeys.AlbumArtistFlyout
        ["Album.ArtistLine"] = "W6a",  // ViewKeys.AlbumArtistLine
        ["Album.Aside"] = "W6a",  // ViewKeys.AlbumAside
        ["Album.Countdown"] = "W6a",  // ViewKeys.AlbumCountdown
        ["Album.ReleasePanel"] = "W6a",  // ViewKeys.AlbumReleasePanel
        ["Album.Sections"] = "W6a",  // ViewKeys.AlbumSections
        ["Album.TrailStack"] = "W6a",  // ViewKeys.AlbumTrailStack
        ["Album.Trailing"] = "W6a",  // ViewKeys.AlbumTrailing
        ["Album.Unresolved"] = "W6a",  // ViewKeys.AlbumUnresolved
        ["Artist.Lightbox"] = "W6a",  // ViewKeys.ArtistLightbox
        ["Detail.CompactRail"] = "W6a",  // ViewKeys.DetailCompactRail
        ["Detail.Frame"] = "W6a",  // ViewKeys.DetailFrame
        ["Detail.Hero"] = "W6a",  // ViewKeys.DetailHero
        ["Detail.Insights"] = "W6a",  // ViewKeys.DetailInsights
        ["Detail.InsightsToggle"] = "W6a",  // ViewKeys.DetailInsightsToggle
        ["Detail.Notice"] = "W6a",  // ViewKeys.DetailNotice
        ["Detail.Rail"] = "W6a",  // ViewKeys.DetailRail
        ["Detail.ShowHeader"] = "W6a",  // ViewKeys.DetailShowHeader
        ["Detail.VerticalTrailing"] = "W6a",  // ViewKeys.DetailVerticalTrailing
        ["Pages.Artist"] = "W6a",  // ViewKeys.ArtistPage
        ["Pages.Discography"] = "W6a",  // ViewKeys.Discography
        // W6b: the other entities (35)
        ["Concert.PlacePicker"] = "W6b",  // ViewKeys.ConcertPlacePicker
        ["Concert.Tail"] = "W6b",  // ViewKeys.ConcertTail
        ["Episode.About"] = "W6b",  // ViewKeys.EpisodeAbout
        ["Episode.Actions"] = "W6b",  // ViewKeys.EpisodeActions
        ["Episode.Attribution"] = "W6b",  // ViewKeys.EpisodeAttribution
        ["Episode.Chapters"] = "W6b",  // ViewKeys.EpisodeChapters
        ["Episode.CommentCard"] = "W6b",  // ViewKeys.EpisodeCommentCard
        ["Episode.CommentSeed"] = "W6b",  // ViewKeys.EpisodeCommentSeed
        ["Episode.Discussion"] = "W6b",  // ViewKeys.EpisodeDiscussion
        ["Episode.LoadMore"] = "W6b",  // ViewKeys.EpisodeLoadMore
        ["Episode.Primary"] = "W6b",  // ViewKeys.EpisodePrimary
        ["Episode.RailAbout"] = "W6b",  // ViewKeys.EpisodeRailAbout
        ["Episode.Rating"] = "W6b",  // ViewKeys.EpisodeRating
        ["Episode.ReactionFlyout"] = "W6b",  // ViewKeys.EpisodeReactionFlyout
        ["Episode.ReactionPeople"] = "W6b",  // ViewKeys.EpisodeReactionPeople
        ["Episode.ReactionRoster"] = "W6b",  // ViewKeys.EpisodeReactionRoster
        ["Episode.Reader"] = "W6b",  // ViewKeys.EpisodeReader
        ["Episode.Save"] = "W6b",  // ViewKeys.EpisodeSave
        ["Liked.ChipBar"] = "W6b",  // ViewKeys.LikedChipBar
        ["Liked.Cover"] = "W6b",  // ViewKeys.LikedCover
        ["Liked.CoverPicker"] = "W6b",  // ViewKeys.LikedCoverPicker
        ["Liked.CoverStyleFlyout"] = "W6b",  // ViewKeys.LikedCoverStyleFlyout
        ["Pages.ArtistConcerts"] = "W6b",  // ViewKeys.ArtistConcerts
        ["Pages.Concert"] = "W6b",  // ViewKeys.ConcertDetail
        ["Pages.Concerts"] = "W6b",  // ViewKeys.ConcertsHub
        ["Playlist.Access"] = "W6b",  // ViewKeys.PlaylistAccess
        ["Playlist.CoverArt"] = "W6b",  // ViewKeys.PlaylistCoverArt
        ["Playlist.CoverEditor"] = "W6b",  // ViewKeys.PlaylistCoverEditor
        ["Playlist.Daylist"] = "W6b",  // ViewKeys.PlaylistDaylist
        ["Playlist.Editor"] = "W6b",  // ViewKeys.PlaylistEditor
        ["Playlist.MembersFlyout"] = "W6b",  // ViewKeys.PlaylistMembersFlyout
        ["Playlist.Owner"] = "W6b",  // ViewKeys.PlaylistOwner
        ["Playlist.RecRow"] = "W6b",  // ViewKeys.PlaylistRecRow
        ["Playlist.Recs"] = "W6b",  // ViewKeys.PlaylistRecs
        ["Playlist.Tune"] = "W6b",  // ViewKeys.PlaylistTune
        // W7: the collection (8)
        ["Library.AlbumPane"] = "W7",  // ViewKeys.LibraryAlbumPane
        ["Library.ArtistReader"] = "W7",  // ViewKeys.LibraryArtistReader
        ["Library.SavedEpisodes"] = "W7",  // ViewKeys.LibrarySavedEpisodes
        ["Library.ShowPane"] = "W7",  // ViewKeys.LibraryShowPane
        ["Pages.Library"] = "W7",  // ViewKeys.LibraryPage
        ["Pages.ProfileList"] = "W7",  // ViewKeys.ProfileListPage
        ["Pages.Recents"] = "W7",  // ViewKeys.RecentsPage
        ["Pages.User"] = "W7",  // ViewKeys.ProfilePage
        // W8a: the overlays (15)
        ["Dialog.FolderPicker"] = "W8a",  // ViewKeys.DialogFolderPicker
        ["Dialog.LogoutConfirm"] = "W8a",  // ViewKeys.DialogLogoutConfirm
        ["Dialog.PlayLink"] = "W8a",  // ViewKeys.DialogPlayLink
        ["Dialog.Sheet"] = "W8a",  // ViewKeys.DialogSheet
        ["Dialog.Signature"] = "W8a",  // ViewKeys.DialogSignature
        ["Menu.Context"] = "W8a",  // ViewKeys.MenuContext
        ["Overlay.ContextSheet"] = "W8a",  // ViewKeys.OverlayContextSheet
        ["Overlay.Notifications"] = "W8a",  // ViewKeys.OverlayNotifications
        ["Overlay.OfflineStrip"] = "W8a",  // ViewKeys.OverlayOfflineStrip
        ["Overlay.OfflineVacancy"] = "W8a",  // ViewKeys.OverlayOfflineVacancy
        ["Overlay.ProfileChip"] = "W8a",  // ViewKeys.OverlayProfileChip
        ["Overlay.ProfileMenu"] = "W8a",  // ViewKeys.OverlayProfileMenu
        ["Overlay.RuntimeBanner"] = "W8a",  // ViewKeys.OverlayRuntimeBanner
        ["Overlay.Toast"] = "W8a",  // ViewKeys.OverlayToast
        ["State.Vacancy"] = "W8a",  // ViewKeys.StateVacancy
        // W8b: the screens (15)
        ["Dialog.Crash"] = "W8b",  // ViewKeys.DialogCrash
        ["Dialog.Feedback"] = "W8b",  // ViewKeys.DialogFeedback
        ["Pages.Settings"] = "W8b",  // ViewKeys.SettingsPage
        ["Pages.WhatsNew"] = "W8b",  // ViewKeys.WhatsNewPage
        ["ReleaseNotes.AfterUpdatePlate"] = "W8b",  // ViewKeys.ReleaseNotesAfterUpdatePlate
        ["ReleaseNotes.Viewer"] = "W8b",  // ViewKeys.ReleaseNotesViewer
        ["Settings.ThemeOptions"] = "W8b",  // ViewKeys.SettingsThemeOptions
        ["Settings.ThemePicker"] = "W8b",  // ViewKeys.SettingsThemePicker
        ["Setup.LocalPlayback"] = "W8b",  // ViewKeys.SetupLocalPlayback
        ["Setup.RuntimeCard"] = "W8b",  // ViewKeys.SetupRuntimeCard
        ["Setup.SignIn"] = "W8b",  // ViewKeys.SetupSignIn
        ["Setup.SignInDoor"] = "W8b",  // ViewKeys.SetupSignInDoor
        ["Setup.Terms"] = "W8b",  // ViewKeys.SetupTerms
        ["Setup.Theme"] = "W8b",  // ViewKeys.SetupTheme
        ["Setup.Wizard"] = "W8b",  // ViewKeys.SetupWizard
    };

    static List<(string Name, string Id, Type Arg, object Key)> Declared(Type keys)
    {
        var found = new List<(string, string, Type, object)>();
        foreach (FieldInfo f in keys.GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            object key = f.GetValue(null)!;
            found.Add((f.Name, (string)f.FieldType.GetProperty("Id")!.GetValue(key)!, f.FieldType.GetGenericArguments()[0], key));
        }
        return found;
    }

    [Fact]
    public void WinUi_validates_and_is_developer_only()
    {
        var theme = WinUiTheme.Create();
        Assert.Empty(theme.Validate());
        Assert.Equal(ThemeAvailability.Developer, theme.Manifest.Availability);
        Assert.Equal(ThemeAvailability.Developer, WinUiTheme.Create().Manifest.Availability);
    }

    [Fact]
    public void Every_view_key_has_a_winui_common_or_documented_stand_in_template()
    {
        var registry = WinUiTheme.Create().Registry;
        var declared = Declared(typeof(ViewKeys));
        Assert.True(declared.Count > 150, $"only {declared.Count} view keys were found: the reflection missed the contract");

        var missing = new List<string>();
        var foreign = new List<string>();
        foreach (var (name, id, _, _) in declared)
        {
            string? templateId = registry.TemplateIdOf(id);
            if (templateId is null) { missing.Add($"ViewKeys.{name} ({id})"); continue; }
            bool own = templateId.StartsWith("winui.", StringComparison.Ordinal) || templateId.StartsWith("common.", StringComparison.Ordinal);
            if (!own && !DeliberateStandIns.ContainsKey(id)) foreign.Add($"ViewKeys.{name} ({id}) -> {templateId}");
        }
        Assert.True(missing.Count == 0, "WinUI renders nothing for: " + string.Join(", ", missing));
        Assert.True(foreign.Count == 0,
            "these keys are drawn by the Classic stand-in and are not listed in DeliberateStandIns (draw them in WinUI, or list them with a reason): "
            + string.Join(", ", foreign));

        // A listed stand-in that WinUI now draws is a stale line.
        var stale = DeliberateStandIns.Keys.Where(id => registry.TemplateIdOf(id) is { } t && (t.StartsWith("winui.", StringComparison.Ordinal) || t.StartsWith("common.", StringComparison.Ordinal))).ToList();
        Assert.True(stale.Count == 0, "DeliberateStandIns lists keys WinUI draws itself: " + string.Join(", ", stale));
    }

    [Fact]
    public void WinUis_own_registry_registers_only_declared_keys_with_winui_or_common_ids()
    {
        var builder = new ViewRegistryBuilder();
        WinUiStructure.RegisterOwn(builder);
        var own = builder.Build();

        var declared = Declared(typeof(ViewKeys)).Select(d => d.Id).ToHashSet(StringComparer.Ordinal);
        var undeclared = own.ViewKeyIds.Where(id => !declared.Contains(id)).ToList();
        Assert.True(undeclared.Count == 0, "WinUI registers ids the contract does not declare: " + string.Join(", ", undeclared));
        foreach (string id in own.ViewKeyIds)
        {
            string templateId = own.TemplateIdOf(id)!;
            Assert.True(templateId.StartsWith("winui.", StringComparison.Ordinal) || templateId.StartsWith("common.", StringComparison.Ordinal),
                id + " -> " + templateId + " is neither WinUI's own (winui.*) nor a Common block's (common.*)");
        }

        // The keys WinUI's own registry leaves out are exactly the documented stand-ins: nothing is inherited by accident.
        var left = declared.Where(id => own.TemplateIdOf(id) is null).Order(StringComparer.Ordinal).ToList();
        Assert.True(left.SequenceEqual(DeliberateStandIns.Keys.Order(StringComparer.Ordinal)),
            "keys WinUI leaves to the stand-in: [" + string.Join(", ", left) + "]; documented: [" + string.Join(", ", DeliberateStandIns.Keys.Order(StringComparer.Ordinal)) + "]");
    }

    [Fact]
    public void Every_style_key_has_a_style_in_the_installed_winui()
    {
        var registry = WinUiTheme.Create().Registry;
        var declared = Declared(typeof(StyleKeys));
        Assert.NotEmpty(declared);
        MethodInfo tryGet = typeof(ViewRegistry).GetMethod(nameof(ViewRegistry.TryGetStyle))!;

        var missing = new List<string>();
        foreach (var (name, id, arg, key) in declared)
        {
            object?[] args = [key, null];
            bool found = (bool)tryGet.MakeGenericMethod(arg).Invoke(registry, args)!;
            if (!found || args[1] is null) missing.Add($"StyleKeys.{name} ({id})");
        }
        Assert.True(missing.Count == 0, "WinUI has no style for: " + string.Join(", ", missing));
        Assert.Equal(declared.Count, declared.Select(d => d.Id).Distinct(StringComparer.Ordinal).Count());
    }
}
