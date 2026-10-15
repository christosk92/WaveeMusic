// ── Shell/Rail.Wiring.cs ───────────────────────────────────────────────────────────────────────────────────────────────
// the CORE half of the right panel and the Player family's T6b views (themes plan 6.1, T6b): what each view reads, what each
// click asks for, and the builders of the bodies other lanes still own
//
// Role: CORE ([CoreHost])
// Spec: `Theming\Contracts\PanelProps.cs`, `NowPlayingHeroProps.cs`, `FriendsProps.cs`, `DevicePickerProps.cs`,
//       `OnMediaTransportProps.cs` (the contracts); `Shell/PanelHost.cs` (the state machine); `Rail.cs` (the decisions)
//
// Before T6b the rail drew itself (`Rail.FrameCore` and its arms) and every one of its parts read `Shell.Ui`, `Playback` and
// `Entities` inline. The pictures moved to the structure; this file is what they were reading, answered once:
//
//   the panel        `ReadPanel`: the state machine's state (`PanelHost.State`), the video surface's placement, the stage that may
//                    host the docked video, the body after the stage's substitution (`VideoCoupling.BodyModeFor`) and what occupies
//                    the now-playing arm's pinned slot (`VideoCoupling.PinnedFor`), as one `PanelView`
//   the hero         the playing track and cover, the deck (hosted, never built by a view), the deck-preference verbs with their
//                    attribution, and the entity subscriptions, the demand and the string table the now-playing body needs
//   the friends      the feed's rows and state with the tables they read subscribed, the clock, the retry seam, the profile verb
//   the picker       the roster, owner, active device and outputs composed into entries, each with its glyph and its verb
//   the on-media     the playback facts and verbs the strip draws and presses, over the surface's own idle signal and chrome feed
//
// WVTH008: this type builds no element and reads no token or role. The lyrics header (`Panel.LyricsHeader`) and the "Next up" rows
// (`Panel.NextUp`) are keys whose facts and verbs are read here; the lyrics peek is the core host `Lyrics.NpvPeek()`.

using System.Collections.Generic;

using FluentGpu.Controls;
using FluentGpu.Controls.Media;
using FluentGpu.Foundation;
using FluentGpu.Localization;
using FluentGpu.Signals;

using Wavee.Theming.Contracts;

namespace Wavee;

/// <summary>The panel's core half: see the file header. UI thread only (the reads run inside a render).</summary>
[CoreHost]
internal static class RailWiring
{
    // ══ THE PANEL ════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The rail mode as the contract speaks it (same members, values and order).</summary>
    internal static PanelMode ModeOf(Shell.RailMode mode) => (PanelMode)(byte)mode;

    /// <summary>The decided view of the panel. Reads, in this order: the panel's cells, the video surface, the page stage's playable
    /// and the playing id, and the active policy (tracked: a theme switch re-resolves the panel in place).</summary>
    internal static PanelView ReadPanel()
    {
        var panel = Shell.PanelHost.Shared.State();
        var state = Video.State.Surface.Value;
        var resolved = Video.PlacementCore.Resolve(state);

        // The rail YIELDS to a watch page's in-page stage: the host is DERIVED, never claimed.
        string stagePlayable = Shell.Ui.ActiveStagePlayable.Value;
        bool stageHosts = stagePlayable.Length > 0
            && Video.DockedHosting.HostFor(resolved, stagePlayable, Playback.CurrentId.Value.Text) == Video.DockedHost.PageStage;
        bool docked = resolved == Video.SurfacePlacement.Docked;

        return new PanelView
        {
            Open = panel.Open,
            Width = panel.Width,
            Floats = panel.Floats,
            FullHeight = ShellPolicy.Tracked.Panel.FullHeight,
            Body = ModeOf(Rail.VideoCoupling.BodyModeFor(panel.Mode, stageHosts)),
            DockedHere = docked && !stageHosts,
            Pinned = Rail.VideoCoupling.PinnedFor(stageHosts, docked, Video.PlacementCore.Allows(state.Available, Video.SurfacePlacement.Docked)),
        };
    }

    // ══ THE NOW-PLAYING HERO ═════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The deck preferences' writes, attributed to the surface that made them.</summary>
    sealed class DeckStyleVerbs : IDeckStyleVerbs
    {
        public void SetStyle(int id) => Shell.DeckPrefs.SetStyle(id, Shell.DeckPrefSource.Flyout);

        public void SetChoice(in Rail.PlayerCatalog.Preset preset, in Rail.PlayerCatalog.OptionDef option, int index)
            => Shell.DeckPrefs.SetChoice(in preset, in option, index, Shell.DeckPrefSource.Flyout);

        public Signal<int> ChoiceMirror(in Rail.PlayerCatalog.Preset preset, in Rail.PlayerCatalog.OptionDef option)
            => Shell.DeckPrefs.ChoiceMirror(in preset, in option);

        public void SetPresentation(int presentation, DeckStyleSource source)
            => Shell.DeckPrefs.SetPresentation(presentation, source switch
            {
                DeckStyleSource.Header => Shell.DeckPrefSource.Header,
                DeckStyleSource.Flyout => Shell.DeckPrefSource.Flyout,
                _ => Shell.DeckPrefSource.ArtMenu,
            });

        public void OpenStyleFlyout() => Shell.DeckPrefs.OpenStyleFlyout();

        public void ToggleStyleFlyout() => Shell.DeckPrefs.ToggleStyleFlyout();
    }

    static readonly DeckStyleVerbs s_deckVerbs = new();

    static NowPlayingHeroProps BuildNowPlaying() => new()
    {
        NowTrack = static () => Rail.NowTrack(),
        NowArtUrl = static () => Rail.NowArtUrl(),
        RailWidth = Shell.Ui.RailWidth,
        Deck = static () => Deck.View(),
        Style = s_deckVerbs,
        Flyout = new DeckStyleFlyoutProps { Verbs = s_deckVerbs, NowArtUrl = static () => Rail.NowArtUrl() },
        SubscribeEntities = static () =>
        {
            _ = Entities.ScopeEpoch.Value;   // FIRST (G-179): a scope switch re-points the table read below
            _ = Entities.Current.Artists.Changed.Value;
            _ = Entities.Current.Albums.Changed.Value;
            _ = Entities.Current.Edges.ArtistCities.Changed.Value;
            _ = Entities.Current.Edges.Queue.Changed.Value;
        },
        DemandNowPlaying = static (trackSlot, artistSlot) => Shell.PanelHost.Shared.DemandNowPlaying(trackSlot, artistSlot),
        HasNextUp = static () => Shell.NextUpWindow.Any(),
        NextUp = new NextUpProps { Read = static () => Rail.NextUpRows() },
        LyricsPeek = static () => Lyrics.NpvPeek(),
        ResolveString = static id => Entities.Strings.Resolve(id),
        GoTo = static (uri, name) => Shell.GoTo(Shell.For(uri, name)),
    };

    // ══ THE LYRICS HEADER ════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The lyrics header's facts, read inside the `Panel.LyricsHeader` view's render (so the capability and the prefs epoch
    /// subscribe the HEADER, not the whole rail). The toggle is composed only when the document on screen carries a second layer; the
    /// expand button shows in both arms (a track with no lyrics still opens a full stage).</summary>
    static LyricsHeaderFacts ReadLyricsHeader()
    {
        int available = Prefs.Lyrics.Available.Value;
        int secondary = Prefs.Lyrics.SecondaryLine();
        string title = Loc.Get(Playback.CurrentId.Value.Kind == EntityKind.Episode ? Strings.Podcast.Reader.Transcript : Strings.Player.Lyrics);
        bool second = available != 0;
        string secondTip = second ? Prefs.Lyrics.Tooltip(secondary) : "";
        bool secondActive = second && (available & Prefs.Lyrics.BitFor(secondary)) != 0;
        string expandTip = Loc.Get(Playback.CurrentId.Value.Kind == EntityKind.Episode ? Strings.Podcast.Reader.Transcript : Strings.Player.ExpandLyrics);
        return new LyricsHeaderFacts(title, second, secondActive, secondTip, expandTip);
    }

    static PanelLyricsHeaderProps BuildLyricsHeader() => new()
    {
        Read = static () => ReadLyricsHeader(),
        ToggleSecondLine = static () => LyricsVerbs.Global.ToggleSecondLine(),
        Expand = static begin => LyricsVerbs.Global.ExpandToStage(begin, "rail-header"),
        Sparkle = static () => AiLyrics.HeaderFacts(),
        SparkleClick = static facts => AiLyrics.HeaderClick(facts),
        InspectorShown = static () => Diagnostics.StoreShot.DeveloperSurfaces(),
        OpenInspector = static overlay =>
        {
            if (Controls.IsNullOverlay(overlay)) return;
            Diagnostics.LyricsInspector.Open(overlay, Diagnostics.LyricsInspector.CurrentTrackId());
        },
        Close = static () => Shell.PanelHost.Shared.Close(),
    };

    // ══ THE FRIENDS PANEL ════════════════════════════════════════════════════════════════════════════════════════════

    sealed class FriendsFeed : IFriendsFeed
    {
        public ReadOnlySpan<FriendEdge> Read(out Rail.Friends.FeedState state)
        {
            state = Rail.Friends.State.Value;
            var table = Entities.Current.Edges.Friends;
            _ = Entities.ScopeEpoch.Value;   // FIRST (G-179): a scope switch re-points the table read below
            _ = table.Changed.Value;
            _ = Entities.Current.Users.Changed.Value;
            _ = Entities.Current.Tracks.Changed.Value;
            return table.Payload(Rail.Friends.Feed);
        }
    }

    static FriendsProps BuildFriends() => new()
    {
        Feed = new FriendsFeed(),
        SetActive = static active => Shell.PanelHost.Shared.SetFriendsActive(active),
        NowMs = static () => Playback.UnixNowMs(),
        Refresh = static () => Rail.Friends.Refresh,
        GoTo = static (uri, name) => Shell.GoTo(Shell.For(uri, name)),
        OpenProfile = static user =>
        {
            var profile = ProfileRoute.For(user);
            if (profile.IsNone) return null;
            return () => Shell.GoTo(profile);
        },
        ResolveString = static id => Entities.Strings.Resolve(id),
    };

    // ══ THE DEVICE PICKER ════════════════════════════════════════════════════════════════════════════════════════════

    static readonly Func<DevicePickerView> s_readDevices = ReadDevicePicker;

    /// <summary>The props of one open of the picker (it carries that open's close verb).</summary>
    internal static DevicePickerProps DevicePicker(Action close) => new()
    {
        Read = s_readDevices,
        MaxHeight = static windowHeight => Shell.DeviceRoster.PickerMaxHeight(windowHeight),
        Close = close,
    };

    /// <summary>The picker's entries, composed from the roster, the owner, the active device and the outputs (this computer first, then
    /// Spotify Connect with this device filtered out), plus a fingerprint of everything they were built from. The verification row is
    /// the player's own echo, never a loopback of the setting.</summary>
    internal static DevicePickerView ReadDevicePicker()
    {
        uint roster = Playback.Devices.Changed.Value;
        var owner = Playback.OwnerSignal.Value;
        int activeSlot = Playback.ActiveDeviceSlot.Value;
        var local = Playback.Audio.Devices.Value;
        string? selected = Playback.Audio.SelectedOutputId.Value;
        bool supported = Playback.Audio.Supported.Value;

        var observed = Playback.Audio.PlayingOpened;
        var asked = Shell.DevicePickerFacts.Quality;
        var picker = Shell.DevicePicker.Items(owner, activeSlot, Playback.Devices.Rows, local, selected, supported, observed, asked);

        var entries = Shell.DevicePicker.Entries(picker);

        int stamp = HashCode.Combine(roster, (byte)owner, activeSlot, local.Length, selected, supported,
            HashCode.Combine(observed.Label, (byte)observed.Format, (byte)asked));
        return new DevicePickerView(entries, stamp);
    }

    // ══ THE ON-MEDIA TRANSPORT ═══════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The ONE cell every strip's volume popup binds: the model's volume mirrored for a slider.</summary>
    static readonly FloatSignal s_onMediaVolume = new(1f);

    static readonly Func<OnMediaFacts> s_readOnMedia = ReadOnMedia;

    static OnMediaFacts ReadOnMedia()
    {
        bool playing = Playback.IsPlaying.Value;
        bool can = !Playback.Current.Value.IsNone;
        var tf = PlayerVerbs.TransportFacts();                        // the same skip fact set the bar and the stage read
        bool muted = Playback.Muted.Value;
        return new OnMediaFacts(playing, can, tf.PrevEnabled, tf.NextEnabled, muted);
    }

    /// <summary>The props of one surface's strip: its idle machine's signal and its chrome feed, over the shared verbs. Everything
    /// but those two is a cached delegate, so two reads for the same surface compare equal and the template does not re-run.</summary>
    internal static OnMediaTransportProps OnMediaProps(IReadSignal<bool> visible, PlayerChromeFeed feed) => new()
    {
        Visible = visible,
        Feed = feed,
        Read = s_readOnMedia,
        Volume = Playback.Volume,
        VolumeCell = s_onMediaVolume,
        Previous = static () => Playback.Previous(),
        Next = static () => Playback.Next(),
        TogglePlay = static () => PlayerVerbs.Default.TogglePlay("onmedia.button"),
        ToggleMute = static () => Playback.ToggleMute(),
        SetVolume = static v => Playback.SetVolume(v),
        MoreMenu = static () => OnMedia.MoreMenu(),
        SeekBar = static (chromeFeed, reveal) => Shell.SeekBar(chromeFeed, reveal: reveal),
        TimeText = static (remaining, ink, reveal) => Shell.TimeText(remaining, ink, reveal),
        FadeChrome = static (ctx, node, show) => OnMedia.FadeChrome(ctx, node, show),
    };

    // ══ THE PROPS THE FRAME MOUNTS ═══════════════════════════════════════════════════════════════════════════════════

    // Declared LAST: static fields initialize in textual order, and this one builds its parts from the helpers above.

    /// <summary>The props the frame mounts <c>Shell.Panel</c> with, built once.</summary>
    public static readonly PanelProps Panel = new()
    {
        Read = static () => ReadPanel(),
        InlineAndOpen = static () => Shell.Ui.RailFits.Value && Shell.Ui.RailOpen.Value,
        BodyHeightChanged = static h =>
        {
            if (MathF.Abs(Shell.Ui.RailBodyHeight.Peek() - h) > 0.5f) Shell.Ui.RailBodyHeight.Value = h;
        },
        DockedVideoHeight = Shell.Ui.DockedVideoHeight,
        DockedVideoMin = static railWidth => Shell.DockedVideoNaturalH(railWidth),
        DockedVideoMax = static () => Shell.DockedCapCeiling(Shell.Ui.RailBodyHeight.Peek()),
        CommitDockedVideo = Shell.PanelHost.CommitDockedVideo,
        Close = static () => Shell.PanelHost.Shared.Close(),
        ShowArt = static () => Shell.PanelHost.Shared.HideVideo(),
        ShowVideoDocked = static () => Shell.PanelHost.Shared.ShowVideoAt(Video.SurfacePlacement.Docked),
        VideoToMiniPlayer = static () => Shell.PanelHost.Shared.ShowVideoAt(Video.SurfacePlacement.Floating),
        VideoToFullScreen = static () => Shell.PanelHost.Shared.ShowVideoAt(Video.SurfacePlacement.Fullscreen),
        DockedCard = static () => Video.DockedCap(),
        LyricsHeader = BuildLyricsHeader(),
        LyricsBody = static () => Lyrics.View(),
        QueueBody = static () => Rail.QueueBody!(),
        SubscribeVideoBody = static () =>
        {
            _ = Entities.ScopeEpoch.Value;   // FIRST (G-179): a scope switch re-points the table read below
            _ = Entities.Current.Edges.Queue.Changed.Value;
            _ = Entities.Current.Albums.Changed.Value;
        },
        NowPlaying = BuildNowPlaying(),
        Friends = BuildFriends(),
    };
}
