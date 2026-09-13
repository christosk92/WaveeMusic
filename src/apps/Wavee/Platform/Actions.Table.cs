// ── Platform/Actions.Table.cs ──────────────────────────────────────────────────────────────────────────────────────
// The named partial of Actions.cs: the thirteen first-party descriptors (the trusted extension `wavee`) and the verb
// helpers they share. Split on day one of the wave because Actions.cs ran past its budget by more than 30 % (plan §5's
// rule: a NAMED partial, never a second type) — and `Actions.Table.cs` is the name ch 29 §9.10 itself gave this half
// before A7 folded the budget into one row.
//
// Role: CORE
// Owner: I
// Wave: 4
// Budget: 250 lines (inside Actions.cs's 950)
// Spec: ch 29 §9.10 (Actions.Table.cs 250)

namespace Wavee;

public static partial class Actions
{
    // ── 4.6 the thirteen first-party descriptors ────────────────────────────────────────────────────────────────────

    /// <summary>The FIRST-PARTY extension's contribution table — the trusted extension literally named <c>wavee</c>
    /// (first-party is not a privileged non-extension path). Hand-written, and shaped so a generator could emit it
    /// verbatim: one static <see cref="RegisterAll"/>, one <c>r.RegisterAction(new ActionDescriptor { … })</c> per
    /// contribution, no reflection, no attribute scanning, no runtime assembly discovery.
    /// <para><b>WHAT IS REGISTERED:</b> the existing <see cref="ActionId"/> verbs that mean something when the target
    /// is a PERSISTED (mode, key) pair rather than a live multi-selection. Deliberately NOT registered, and why:</para>
    /// <list type="bullet">
    /// <item><c>AddToPlaylist</c> / <c>AddToDefaultPlaylist</c> / <c>RemoveFromThisPlaylist</c> /
    /// <c>RemoveFromQueue</c> / <c>SelectAll</c> — they need a live selection, a playlist HOST with resolved row ids,
    /// or a queue row identity; none of that survives a restart.</item>
    /// <item><c>ViewCredits</c> — needs a resolved track with a primary-artist uri (the fetch keys off both).</item>
    /// <item><c>RenamePlaylist</c> / <c>TogglePlaylistPublic</c> / <c>InviteCollaborators</c> /
    /// <c>DeletePlaylist</c> — owner-only playlist MANAGEMENT. A one-click sidebar shortcut is the wrong affordance
    /// (delete especially); the confirmation gate exists for the day one of them is bound anyway.</item>
    /// <item>The <c>Video ▸</c> verbs — they open file pickers over a local-curation service that may not exist.</item>
    /// </list></summary>
    public static class Builtins
    {
        /// <summary>The first-party extension id. Also the publisher segment of every key below.</summary>
        public const string ExtensionId = Key.FirstPartyPublisher;

        // ── keys (stable; PERSISTED inside a binding — never rename one) ─────────────────────────────────────────────
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

        /// <summary>All thirteen keys, in registration order — what a test walks to prove the table is complete.</summary>
        public static readonly string[] AllKeys =
        [
            KeyPlay, KeyPlayNext, KeyAddToQueue, KeyToggleLike, KeySaveContext, KeyOpen,
            KeyGoToAlbum, KeyGoToArtist, KeyCopyLink, KeySongRadio, KeyArtistRadio, KeyPin, KeyUnpin,
        ];

        /// <summary>Register the first-party actions. <paramref name="services"/> is part of the SIGNATURE a generator
        /// would emit and is deliberately NOT branched on: descriptors receive the seam bag per invocation (its fields
        /// are refreshed by the shell every render), so probing it at startup would bake in a cold-start snapshot and
        /// make the table non-deterministic.</summary>
        public static void RegisterAll(IRegistrar r, ActionServices services)
        {
            _ = services;   // signature stability, never a startup capability probe

            // ── playback ────────────────────────────────────────────────────────────────────────────────────────────
            r.RegisterAction(new ActionDescriptor
            {
                Key = KeyPlay, LegacyId = ActionId.PlayContext,
                LabelLocKey = Strings.Detail.Play, IconKey = ActionIcons.Play,
                AcceptedTargets = ActionTargetModes.FixedEntity | ActionTargetModes.FixedTrack,
                RequiredPermissions = [Permissions.PlaybackControl],
                IsEnabled = static (s, _, t) => s.Play is not null && t.Uri.IsValid,
                Run = static (s, _, t) => s.Play?.Invoke(t.Uri),
            });

            r.RegisterAction(new ActionDescriptor
            {
                Key = KeyPlayNext, LegacyId = ActionId.PlayNext,
                LabelLocKey = Strings.Detail.PlayNext, IconKey = ActionIcons.PlayNext,
                AcceptedTargets = ActionTargetModes.FixedTrack,
                RequiredPermissions = [Permissions.PlaybackControl],
                IsEnabled = static (s, _, t) => s.PlayNext is not null && t.Uri.IsValid,
                Run = static (s, _, t) => s.PlayNext?.Invoke(t.Uri),
            });

            r.RegisterAction(new ActionDescriptor
            {
                Key = KeyAddToQueue, LegacyId = ActionId.AddToQueue,
                LabelLocKey = Strings.Detail.PlayAfter, IconKey = ActionIcons.Queue,
                AcceptedTargets = ActionTargetModes.FixedTrack,
                RequiredPermissions = [Permissions.PlaybackControl],
                IsEnabled = static (s, _, t) => s.AddToQueue is not null && t.Uri.IsValid,
                Run = static (s, _, t) => s.AddToQueue?.Invoke(t.Uri),
            });

            // ── library ─────────────────────────────────────────────────────────────────────────────────────────────
            r.RegisterAction(new ActionDescriptor
            {
                Key = KeyToggleLike, LegacyId = ActionId.ToggleLike,
                LabelLocKey = Strings.Menu.SaveToLiked, IconKey = ActionIcons.Like,
                AcceptedTargets = ActionTargetModes.FixedTrack | ActionTargetModes.NowPlaying,
                RequiredPermissions = [Permissions.LibraryWrite],
                IsEnabled = static (s, _, t) => s.SetSaved is not null && t.Uri.IsValid,
                IsChecked = static (s, _, t) => s.IsSaved?.Invoke(t.Uri) ?? false,
                Run = static (s, _, t) => s.SetSaved?.Invoke(t.Uri, !(s.IsSaved?.Invoke(t.Uri) ?? false)),
            });

            r.RegisterAction(new ActionDescriptor
            {
                Key = KeySaveContext, LegacyId = ActionId.SaveContext,
                LabelLocKey = Strings.Menu.SaveToLibrary, IconKey = ActionIcons.Save,
                AcceptedTargets = ActionTargetModes.FixedEntity,
                RequiredPermissions = [Permissions.LibraryWrite],
                IsEnabled = static (s, _, t) => s.SetSaved is not null && t.Uri.IsValid,
                IsChecked = static (s, _, t) => s.IsSaved?.Invoke(t.Uri) ?? false,
                Run = static (s, _, t) => s.SetSaved?.Invoke(t.Uri, !(s.IsSaved?.Invoke(t.Uri) ?? false)),
            });

            // ── navigation ──────────────────────────────────────────────────────────────────────────────────────────
            r.RegisterAction(new ActionDescriptor
            {
                Key = KeyOpen, LegacyId = ActionId.OpenItem,
                LabelLocKey = Strings.Menu.Open, IconKey = ActionIcons.Open,
                AcceptedTargets = ActionTargetModes.FixedEntity | ActionTargetModes.NowPlaying,
                RequiredPermissions = [Permissions.NavigationContribute],
                IsEnabled = static (s, _, t) => s.Go is not null && t.Route.Kind != Shell.RouteKind.NotFound,
                Run = static (s, _, t) => s.Go?.Invoke(t.Route),
            });

            r.RegisterAction(new ActionDescriptor
            {
                Key = KeyGoToAlbum, LegacyId = ActionId.GoToAlbum,
                LabelLocKey = Strings.Menu.GoToAlbum, IconKey = ActionIcons.Album,
                AcceptedTargets = ActionTargetModes.NowPlaying,
                RequiredPermissions = [Permissions.NavigationContribute, Permissions.PlaybackRead],
                IsEnabled = static (s, _, _) => s.Go is not null && AlbumRouteOfNowPlaying().Kind != Shell.RouteKind.NotFound,
                Run = static (s, _, _) => s.Go?.Invoke(AlbumRouteOfNowPlaying()),
            });

            r.RegisterAction(new ActionDescriptor
            {
                Key = KeyGoToArtist, LegacyId = ActionId.GoToArtist,
                LabelLocKey = Strings.Detail.GoToArtist, IconKey = ActionIcons.Artist,
                AcceptedTargets = ActionTargetModes.NowPlaying,
                RequiredPermissions = [Permissions.NavigationContribute, Permissions.PlaybackRead],
                IsEnabled = static (s, _, _) => s.Go is not null && ArtistRouteOfNowPlaying().Kind != Shell.RouteKind.NotFound,
                Run = static (s, _, _) => s.Go?.Invoke(ArtistRouteOfNowPlaying()),
            });

            // ── platform ────────────────────────────────────────────────────────────────────────────────────────────
            r.RegisterAction(new ActionDescriptor
            {
                Key = KeyCopyLink, LegacyId = ActionId.CopyLink,
                LabelLocKey = Strings.Menu.CopyLink, IconKey = ActionIcons.Link,
                AcceptedTargets = ActionTargetModes.FixedEntity | ActionTargetModes.FixedTrack
                                | ActionTargetModes.NowPlaying,
                RequiredPermissions = [Permissions.ClipboardWrite],
                IsEnabled = static (s, _, t) => s.Clipboard is not null && t.Uri.IsValid,
                Run = static (s, _, t) => s.Clipboard?.Invoke(WebLinkOf(t.Uri)),
            });

            // ── radio ───────────────────────────────────────────────────────────────────────────────────────────────
            r.RegisterAction(new ActionDescriptor
            {
                Key = KeySongRadio, LegacyId = ActionId.GoToSongRadio,
                LabelLocKey = Strings.Menu.GoToSongRadio, IconKey = ActionIcons.Radio,
                AcceptedTargets = ActionTargetModes.FixedTrack | ActionTargetModes.NowPlaying,
                RequiredPermissions = [Permissions.PlaybackControl],
                IsEnabled = static (s, _, t) => s.StartRadio is not null
                    && t.Uri.Kind is EntityKind.Track or EntityKind.Episode,
                Run = static (s, _, t) => s.StartRadio?.Invoke(t.Uri),
            });

            r.RegisterAction(new ActionDescriptor
            {
                Key = KeyArtistRadio, LegacyId = ActionId.GoToArtistRadio,
                LabelLocKey = Strings.Menu.GoToArtistRadio, IconKey = ActionIcons.Radio,
                AcceptedTargets = ActionTargetModes.FixedEntity,
                RequiredPermissions = [Permissions.PlaybackControl],
                IsEnabled = static (s, _, t) => s.StartRadio is not null && t.Uri.Kind == EntityKind.Artist,
                Run = static (s, _, t) => s.StartRadio?.Invoke(t.Uri),
            });

            // ── pins ────────────────────────────────────────────────────────────────────────────────────────────────
            // Pin/unpin are an ABSOLUTE-STATE pair, not one toggle: two labels, two icons, two identities.
            r.RegisterAction(new ActionDescriptor
            {
                Key = KeyPin, LegacyId = ActionId.PinToSidebar,
                LabelLocKey = Strings.Sidebar.Pin.PinTo, IconKey = ActionIcons.Pin,
                AcceptedTargets = ActionTargetModes.FixedEntity | ActionTargetModes.ActiveRoute,
                RequiredPermissions = [Permissions.SidebarPins],
                IsEnabled = static (s, _, t) => s.SetPinned is not null && t.Route.Kind != Shell.RouteKind.NotFound
                    && !(s.IsPinned?.Invoke(t.Route) ?? false),
                Run = static (s, _, t) => s.SetPinned?.Invoke(t.Route, true),
            });

            r.RegisterAction(new ActionDescriptor
            {
                Key = KeyUnpin, LegacyId = ActionId.UnpinFromSidebar,
                LabelLocKey = Strings.Sidebar.Pin.Unpin, IconKey = ActionIcons.Unpin,
                AcceptedTargets = ActionTargetModes.FixedEntity | ActionTargetModes.ActiveRoute,
                RequiredPermissions = [Permissions.SidebarPins],
                IsEnabled = static (s, _, t) => s.SetPinned is not null && t.Route.Kind != Shell.RouteKind.NotFound
                    && (s.IsPinned?.Invoke(t.Route) ?? false),
                Run = static (s, _, t) => s.SetPinned?.Invoke(t.Route, false),
            });
        }

        static Shell.Route AlbumRouteOfNowPlaying()
            => TryNowPlayingTrack(out var track)
                ? Shell.LinkFor(track, Shell.LinkSlot.Title)
                : new Shell.Route(Shell.RouteKind.NotFound);

        static Shell.Route ArtistRouteOfNowPlaying()
            => TryNowPlayingTrack(out var track)
                ? Shell.LinkFor(track, Shell.LinkSlot.Artist)
                : new Shell.Route(Shell.RouteKind.NotFound);
    }

    /// <summary>The <c>open.spotify.com</c> form of an entity uri — what "Copy link" puts on the clipboard.</summary>
    public static string WebLinkOf(EntityUri uri)
    {
        if (!uri.IsValid) return "";
        string text = uri.Text;
        int colon = text.LastIndexOf(':');
        if (colon <= 0 || colon + 1 >= text.Length) return "";
        // spotify:album:<id> → https://open.spotify.com/album/<id>
        int kindStart = text.LastIndexOf(':', colon - 1) + 1;
        string kind = text[kindStart..colon];
        string id = text[(colon + 1)..];
        return kind.Length == 0 || id.Length == 0 ? "" : "https://open.spotify.com/" + kind + "/" + id;
    }
}
