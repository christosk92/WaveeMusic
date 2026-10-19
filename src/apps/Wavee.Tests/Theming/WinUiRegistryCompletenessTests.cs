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
        // W3: the lists (0: the seven list styles are WinUI's own, `WinUiStructure.Lists.cs`)
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
        // W6a: album and artist (0: the detail frame, the album, the artist and the discography are WinUI's own, `WinUiStructure.Detail.cs`)
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
