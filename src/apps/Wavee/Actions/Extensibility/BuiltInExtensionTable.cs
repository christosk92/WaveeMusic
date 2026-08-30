using System;
using FluentGpu.Controls;
using FluentGpu.Localization;
using Wavee.Core;
using Wavee.Core.Sidebar;

namespace Wavee;

/// <summary>
/// The FIRST-PARTY extension's contribution table — the trusted extension literally named <c>"wavee"</c> (REVISION 2's
/// forward-compatibility guardrail: first-party is not a privileged non-extension path). HAND-WRITTEN in M1 and
/// SOURCE-GENERATED in M4 polish against this exact call shape, so nothing here may need rework: one static
/// <see cref="RegisterAll"/>, one <c>r.RegisterAction(new WaveeActionDescriptor { … })</c> per contribution, no
/// reflection, no attribute scanning, no runtime assembly discovery.
///
/// WHAT IS REGISTERED: the existing <see cref="ActionId"/> verbs that make sense as a SIDEBAR BINDING — i.e. that mean
/// something when the target is a persisted (mode, key) pair rather than a live multi-selection. Deliberately NOT
/// registered, and why:
///   * <c>AddToPlaylist</c> / <c>AddToDefaultPlaylist</c> / <c>RemoveFromThisPlaylist</c> / <c>RemoveFromQueue</c> /
///     <c>SelectAll</c> — they need a live selection, a playlist HOST with resolved row ids, or a queue row identity;
///     none of that survives a restart, so a stored binding could not honestly re-target them.
///   * <c>ViewCredits</c> — needs a resolved <see cref="Track"/> with a primary-artist uri (the fetch keys off both).
///   * <c>Rename</c> / <c>TogglePlaylistPublic</c> / <c>InviteCollaborators</c> / <c>DeletePlaylist</c> — owner-only
///     playlist MANAGEMENT. A one-click sidebar shortcut is the wrong affordance for them (delete especially); they stay
///     context-menu-only. The descriptor's confirmation gate exists for the day one of them is bound anyway.
///   * The <c>Video ▸</c> verbs — they open file pickers over a local-curation service that may not exist.
///
/// Every descriptor delegates to the SAME code path the context menu takes (<c>TrackActions</c> / <c>ContainerActions</c>
/// / <c>PinActions</c> / <c>DetailQueueActions</c> / <c>RadioLaunch</c>), never a second implementation — a binding and a
/// right-click must never be able to disagree.
/// </summary>
public static class BuiltInExtensionTable
{
    /// <summary>The first-party extension id. Also the publisher segment of every key below.</summary>
    public const string ExtensionId = WaveeExtensionKey.FirstPartyPublisher;

    // ── keys (stable; persisted inside SidebarActionBinding — never rename one) ───────────────────────────────────────
    public const string KeyPlay = "wavee.play";
    public const string KeyPlayNext = "wavee.playNext";
    public const string KeyAddToQueue = "wavee.addToQueue";
    public const string KeyToggleLike = "wavee.toggleLike";
    public const string KeySaveContext = "wavee.save";
    public const string KeyOpen = "wavee.open";
    public const string KeyGoToAlbum = "wavee.goToAlbum";
    public const string KeyGoToArtist = "wavee.goToArtist";
    public const string KeyCopyLink = "wavee.copyLink";
    public const string KeySongRadio = "wavee.songRadio";
    public const string KeyArtistRadio = "wavee.artistRadio";
    public const string KeyPin = "wavee.pinToSidebar";
    public const string KeyUnpin = "wavee.unpinFromSidebar";

    /// <summary>Register the first-party actions. <paramref name="services"/> is part of the SIGNATURE the M4 generator
    /// emits, and is deliberately NOT branched on: descriptors receive the service bag per invocation (its fields are
    /// refreshed by the shell every render), so probing it at startup would bake in a cold-start snapshot and make the
    /// table non-deterministic. Data sources are registered by the data-source layer's own table.</summary>
    public static void RegisterAll(IWaveeExtensionRegistrar r, ActionServices services)
    {
        _ = services;   // see the doc comment: signature stability for the generator, never a startup capability probe.

        // ── playback ──────────────────────────────────────────────────────────────────────────────────────────────────
        r.RegisterAction(new WaveeActionDescriptor
        {
            Key = KeyPlay, LegacyId = ActionId.PlayContext,
            LabelLocKey = Strings.Detail.Play, IconKey = ActionIcons.Play,
            AcceptedTargets = WaveeActionTargetModes.FixedEntity | WaveeActionTargetModes.FixedTrack,
            RequiredPermissions = [WaveePermissions.PlaybackControl],
            IsEnabled = static (s, _) => s.Svc?.Player is not null,
            Run = static (s, bind, t) =>   // param must not be named "_": `_ = task` would ASSIGN it, not discard
            {
                if (s.Svc?.Player is not { } p || t.Uri.Length == 0) return;
                // A track uri plays as a TRACK; anything else is a context. One branch, on the resolved mode — never a
                // string sniff at three call sites.
                if (t.Mode == SidebarActionTargetMode.FixedTrack) _ = p.PlayTrackAsync(t.Uri);
                else _ = p.PlayAsync(t.Uri);
            },
        });

        r.RegisterAction(new WaveeActionDescriptor
        {
            Key = KeyPlayNext, LegacyId = ActionId.PlayNext,
            LabelLocKey = Strings.Detail.PlayNext, IconKey = ActionIcons.PlayNext,
            AcceptedTargets = WaveeActionTargetModes.FixedTrack,
            RequiredPermissions = [WaveePermissions.PlaybackControl],
            IsEnabled = static (s, _) => s.Svc?.Player is not null,
            Run = static (s, bind, t) =>
            {
                if (s.Svc?.Player is not { } p || t.Uri.Length == 0) return;
                _ = p.PlayNextAsync([new PlaybackContextTrack(t.Uri)]);
            },
        });

        r.RegisterAction(new WaveeActionDescriptor
        {
            Key = KeyAddToQueue, LegacyId = ActionId.AddToQueue,
            LabelLocKey = Strings.Detail.PlayAfter, IconKey = ActionIcons.Queue,
            AcceptedTargets = WaveeActionTargetModes.FixedTrack,
            RequiredPermissions = [WaveePermissions.PlaybackControl],
            IsEnabled = static (s, _) => s.Svc?.Player is not null,
            Run = static (s, bind, t) =>
            {
                if (s.Svc?.Player is not { } p || t.Uri.Length == 0) return;
                _ = p.EnqueueAsync(t.Uri);
            },
        });

        // ── library ───────────────────────────────────────────────────────────────────────────────────────────────────
        //
        // TWO saved-set toggles, ONE verb, DISTINCT labels. Both wrap LibraryBridge.ToggleSaved and differ only in what
        // they ACCEPT: this one takes a TRACK (or now-playing), KeySaveContext below takes a container (album / artist /
        // playlist). They used to share `Strings.Menu.Save`, so every surface that lists the registry — the sidebar's
        // action picker most visibly — showed two consecutive rows both reading "Save" with the same heart, tellable apart
        // only by clicking one (round-2 defect 6b). NEITHER is dropped: removing either makes its target kind unbindable.
        // `menu.saveToLiked` / `menu.saveToLibrary` were already in the catalog and previously unreferenced from C#. The
        // semantic Like/Save icon keys now keep the two rows visually distinct as well.
        r.RegisterAction(new WaveeActionDescriptor
        {
            Key = KeyToggleLike, LegacyId = ActionId.ToggleLike,
            LabelLocKey = Strings.Menu.SaveToLiked, IconKey = ActionIcons.Like,
            AcceptedTargets = WaveeActionTargetModes.FixedTrack | WaveeActionTargetModes.NowPlaying,
            RequiredPermissions = [WaveePermissions.LibraryWrite],
            IsEnabled = static (s, _) => s.Library is not null,
            IsChecked = static (s, b) => IsSavedFor(s, b, WaveeActionTargetModes.FixedTrack | WaveeActionTargetModes.NowPlaying),
            Run = static (s, _, t) =>
            {
                if (s.Library is not { } lib || t.Uri.Length == 0) return;
                lib.ToggleSaved(t.Uri, t.Name);
            },
        });

        // Save album · follow artist · save playlist — the one saved-set toggle. Registered as a TOGGLE (IsChecked), so
        // "unsave" is its off-state rather than a second key: the app has exactly one verb here (ContainerActions.SaveContext)
        // and two keys would let a binding claim a direction the underlying verb does not have.
        r.RegisterAction(new WaveeActionDescriptor
        {
            Key = KeySaveContext, LegacyId = ActionId.SaveContext,
            LabelLocKey = Strings.Menu.SaveToLibrary, IconKey = ActionIcons.Save,
            AcceptedTargets = WaveeActionTargetModes.FixedEntity,
            RequiredPermissions = [WaveePermissions.LibraryWrite],
            IsEnabled = static (s, _) => s.Library is not null,
            IsChecked = static (s, b) => IsSavedFor(s, b, WaveeActionTargetModes.FixedEntity),
            Run = static (s, _, t) =>
            {
                if (s.Library is not { } lib || t.Uri.Length == 0) return;
                lib.ToggleSaved(t.Uri, t.Name);
            },
        });

        // ── navigation ────────────────────────────────────────────────────────────────────────────────────────────────
        r.RegisterAction(new WaveeActionDescriptor
        {
            Key = KeyOpen, LegacyId = ActionId.OpenItem,
            LabelLocKey = Strings.Menu.Open, IconKey = ActionIcons.Open,
            AcceptedTargets = WaveeActionTargetModes.FixedEntity | WaveeActionTargetModes.NowPlaying,
            RequiredPermissions = [WaveePermissions.NavigationContribute],
            IsEnabled = static (s, _) => s.Go is not null,
            Run = static (s, _, t) =>
            {
                if (t.RouteKey is { Length: > 0 } route) s.Go?.Invoke(route, null);
            },
        });

        // Go to album / artist need the RESOLVED Track (its album/artist uri), which only the now-playing state carries —
        // a persisted track uri alone cannot name its album without a metadata round-trip. Accepting only NowPlaying is
        // the honest surface, and it is the useful one ("jump to what's playing").
        r.RegisterAction(new WaveeActionDescriptor
        {
            Key = KeyGoToAlbum, LegacyId = ActionId.GoToAlbum,
            LabelLocKey = Strings.Menu.GoToAlbum, IconKey = ActionIcons.Album,
            AcceptedTargets = WaveeActionTargetModes.NowPlaying,
            RequiredPermissions = [WaveePermissions.NavigationContribute, WaveePermissions.PlaybackRead],
            IsEnabled = static (s, _) => s.Go is not null
                                         && s.Playback?.CurrentTrack.Value is { Album.Uri.Length: > 0 },
            Run = static (s, _, _) =>
            {
                if (s.Playback?.CurrentTrack.Peek() is not { Album: { Uri.Length: > 0 } album }) return;
                s.Go?.Invoke("album:" + album.Uri, album.Name);
            },
        });

        r.RegisterAction(new WaveeActionDescriptor
        {
            Key = KeyGoToArtist, LegacyId = ActionId.GoToArtist,
            LabelLocKey = Strings.Detail.GoToArtist, IconKey = ActionIcons.Artist,
            AcceptedTargets = WaveeActionTargetModes.NowPlaying,
            RequiredPermissions = [WaveePermissions.NavigationContribute, WaveePermissions.PlaybackRead],
            IsEnabled = static (s, _) => s.Go is not null
                                         && s.Playback?.CurrentTrack.Value is { Artists.Count: > 0 },
            Run = static (s, _, _) =>
            {
                if (s.Playback?.CurrentTrack.Peek() is not { Artists.Count: > 0 } track) return;
                var a = track.Artists[0];
                s.Go?.Invoke("artist:" + a.Uri, a.Name);
            },
        });

        // ── share ─────────────────────────────────────────────────────────────────────────────────────────────────────
        r.RegisterAction(new WaveeActionDescriptor
        {
            Key = KeyCopyLink, LegacyId = ActionId.CopyLink,
            LabelLocKey = Strings.Menu.CopyLink, IconKey = ActionIcons.Link,
            AcceptedTargets = WaveeActionTargetModes.FixedEntity | WaveeActionTargetModes.FixedTrack
                              | WaveeActionTargetModes.NowPlaying,
            RequiredPermissions = [WaveePermissions.ClipboardWrite],
            IsEnabled = static (s, _) => s.Clipboard is not null,
            Run = static (s, _, t) =>
            {
                if (s.Clipboard is not { } clip || SpotifyLink.WebUrl(t.Uri) is not { } url) return;
                try { clip.SetText(url); }
                catch (Exception ex) { PlaylistEditErrors.Toast(ex); return; }
                Toast.Show(Loc.Get(Strings.Menu.LinkCopied),
                    new ToastOptions { Severity = InfoBarSeverity.Success });
            },
        });

        // ── radio ─────────────────────────────────────────────────────────────────────────────────────────────────────
        r.RegisterAction(new WaveeActionDescriptor
        {
            Key = KeySongRadio, LegacyId = ActionId.GoToSongRadio,
            LabelLocKey = Strings.Menu.GoToSongRadio, IconKey = ActionIcons.Radio,
            AcceptedTargets = WaveeActionTargetModes.FixedTrack | WaveeActionTargetModes.NowPlaying,
            RequiredPermissions = [WaveePermissions.PlaybackControl],
            IsEnabled = static (s, _) => s.Svc?.Player is not null,
            Run = static (s, _, t) =>
            {
                if (t.Uri.Length == 0) return;
                RadioLaunch.Start(s.Svc?.Player, t.Uri, null, s.Go);
            },
        });

        r.RegisterAction(new WaveeActionDescriptor
        {
            Key = KeyArtistRadio, LegacyId = ActionId.GoToArtistRadio,
            LabelLocKey = Strings.Menu.GoToArtistRadio, IconKey = ActionIcons.Radio,
            AcceptedTargets = WaveeActionTargetModes.FixedEntity,
            RequiredPermissions = [WaveePermissions.PlaybackControl],
            IsEnabled = static (s, _) => s.Svc?.Player is not null,
            Run = static (s, _, t) =>
            {
                if (t.Uri.Length == 0) return;
                RadioLaunch.Start(s.Svc?.Player, t.Uri, null, s.Go);
            },
        });

        // ── the pin pair (the absolute-state pair, not a toggle — see PinActions) ─────────────────────────────────────
        r.RegisterAction(new WaveeActionDescriptor
        {
            Key = KeyPin, LegacyId = ActionId.PinToSidebar,
            LabelLocKey = Strings.Sidebar.Pin.PinTo, IconKey = ActionIcons.Pin,
            AcceptedTargets = WaveeActionTargetModes.FixedEntity | WaveeActionTargetModes.ActiveRoute,
            RequiredPermissions = [WaveePermissions.SidebarPins],
            // The pin store's presence is the feature's kill switch, and only a PINNABLE id may be bound at all.
            IsEnabled = static (s, b) => s.Sidebar is not null && PinBindable(b),
            Run = static (s, _, t) =>
            {
                if (s.Sidebar is not { } prefs || PinIdOf(in t) is not { } id) return;
                PinActions.Pin(prefs, id, SidebarPinId.KindOf(id), SidebarPinId.UriOf(id), "");
            },
        });

        r.RegisterAction(new WaveeActionDescriptor
        {
            Key = KeyUnpin, LegacyId = ActionId.UnpinFromSidebar,
            LabelLocKey = Strings.Sidebar.Pin.Unpin, IconKey = ActionIcons.Unpin,
            AcceptedTargets = WaveeActionTargetModes.FixedEntity | WaveeActionTargetModes.ActiveRoute,
            RequiredPermissions = [WaveePermissions.SidebarPins],
            IsEnabled = static (s, b) => s.Sidebar is not null && PinBindable(b),
            Run = static (s, _, t) =>
            {
                if (s.Sidebar is not { } prefs || PinIdOf(in t) is not { } id) return;
                PinActions.Unpin(prefs, id);
            },
        });
    }

    /// <summary>Can this binding ever name a pinnable target? Checked WITHOUT a resolution, because an enablement
    /// resolver runs before one. An <c>ActiveRoute</c> binding is always bindable (whether the CURRENT page is pinnable
    /// is decided per invocation by <see cref="PinIdOf"/>); a fixed binding must carry a key the pin-id scheme accepts,
    /// which is exactly how "a track is never pinnable" (locked decision 4) reaches the picker.</summary>
    static bool PinBindable(SidebarActionBinding binding)
        => binding.TargetMode == SidebarActionTargetMode.ActiveRoute
           || (SidebarPinId.FromUri(binding.TargetKey) ?? SidebarPinId.FromRoute(binding.TargetKey)) is not null;

    /// <summary>The pin id an invocation acts on: the resolved route key IS the pin id for every pinnable kind
    /// (F.5.4), so <c>ActiveRoute</c> and <c>FixedEntity</c> collapse into one lookup. Null ⇒ the current target is not
    /// pinnable (a settings page, a track) and the invocation is a silent no-op.</summary>
    static string? PinIdOf(in WaveeActionTargetResolution t)
        => t.RouteKey is { Length: > 0 } route ? SidebarPinId.FromRoute(route) : SidebarPinId.FromUri(t.Uri);

    /// <summary>Saved-state for a toggle descriptor. Resolves the target itself (a checked resolver is handed the
    /// binding, not the resolution — the platform doc's signature), reusing the ONE target matrix.</summary>
    static bool IsSavedFor(ActionServices s, SidebarActionBinding b, WaveeActionTargetModes accepted)
    {
        if (s.Library is not { } lib) return false;
        var host = new WaveeActionHostState(s.Playback?.CurrentTrack.Value?.Uri,
            s.Playback?.CurrentContext.Value, s.CurrentRoute?.Invoke());
        var t = WaveeActionTargets.Resolve(b, accepted, in host);
        return t.Available && t.Uri.Length > 0 && lib.IsSaved(t.Uri);
    }
}
