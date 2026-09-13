// ── Platform/Actions.cs ────────────────────────────────────────────────────────────────────────────────────────────
// the descriptor, the targeting matrix, the seven reasons, the one action table, the thirteen first-party
// descriptors, the extension registry
//
// Role: CORE
// Owner: I
// Wave: 4
// Budget: 950 lines
// Spec: ch 29 §9.10 (Actions.cs 700 + Actions.Table.cs 250)
//
// The thirteen first-party descriptors are the NAMED PARTIAL `Platform/Actions.Table.cs` (this file ran >30 % past its
// budget; the name is the one ch 29 §9.10 gave that half).
//
// SHIPPED EARLY IN THE WAVE (§5 sequencing item 2, A7): owner J's binder and the customizer's action picker read the
// DESCRIPTOR SHAPE below, and `PinRowRule` goes the other way, into `Sidebar.cs`. The shape is frozen when this lands.
//
// TWO THINGS THIS FILE OWNS THAT 0.2.9 PUT ELSEWHERE, and why:
//
//   * `ActionBinding` / `ActionTargetMode` — 0.2.9 spelled these `SidebarActionBinding` / `SidebarActionTargetMode`
//     and put them in the sidebar's document. They are the PERSISTED VOCABULARY a descriptor is resolved against, and
//     the registry, the picker and (later) the command palette all speak it, so it belongs with the descriptor. Owner
//     J's `Sidebar.Doc.cs` DECODES `sidebar-layout.json` into this type rather than declaring a second one.
//   * The route↔pin-id mapping is `Shell`'s (`Shell.NameOf` / `Shell.For` / `Shell.UriOf`), because a pin id IS a
//     route key. 0.2.9's `SidebarPinId` is not re-created here.
//
// AND ONE THING IT DELIBERATELY DOES NOT OWN: the VERBS. Ch 29 §9.10 moves them out ("the menu vocabulary — down from
// 1,197 raw because the verbs move to `Entities/*`"). A descriptor's `Run` dispatches through a seam on
// `ActionServices`, which the entity files and the shell fill — so a binding and a right-click can never reach two
// implementations of the same verb.

using System;
using System.Collections.Generic;

namespace Wavee;

// ══ 1. THE STABLE IDENTITIES ════════════════════════════════════════════════════════════════════════════════════════

/// <summary>Stable action identities — the shortcut map / command-palette key, and what every descriptor records as
/// the verb it wraps. Values are PERSISTED-SAFE: append only, never reorder or reuse.</summary>
public enum ActionId : ushort
{
    None = 0,
    Play, PlayNext, AddToQueue, ToggleLike, AddToPlaylist, AddToDefaultPlaylist,
    GoToAlbum, GoToArtist, CopyLink, RemoveFromThisPlaylist, RemoveFromQueue, SelectAll,
    OpenItem, PlayContext, SaveContext /* save album · follow artist · save playlist */,
    RenamePlaylist, TogglePlaylistPublic, InviteCollaborators, DeletePlaylist,
    PinToSidebar,
    GoToSongRadio, GoToArtistRadio, ViewCredits, CopySpotifyUri, OpenInSpotifyWeb,
    // The Video ▸ submenu (local video overrides): attach/replace are the same verb over the uri primary key, so they
    // are still two IDENTITIES — the label, the icon and the undo semantics differ.
    AttachVideo, ReplaceVideo, LocateVideo, RemoveVideo, ShowVideoInExplorer,
    // The pin pair's second half. Pin/unpin are an ABSOLUTE-STATE pair, not one toggle — two identities, two labels,
    // two icons.
    UnpinFromSidebar,
    // The CONTAINER half of the transport/collection verbs. A container's tracks are not in hand at menu-open time —
    // they resolve through the SAME reader the drag payload uses — so these are their own identities rather than the
    // track verbs with a different target: the label is the same, the enablement rule and the execution path are not.
    PlayContextNext, AddContextToQueue, AddContextToPlaylist,
    /// <summary>Album card → its primary artist (the track menu's Go-to-artist, for a target that carries no track).</summary>
    GoToAlbumArtist,
}

/// <summary>The action icon-KEY vocabulary: an action carries a SEMANTIC key and never a raw glyph. The one
/// <c>Resolve</c> that maps a key to an icon lives in <c>Actions.UI.cs</c> (ch 29 §9.10 puts `ActionIcons` there);
/// only that table changes as the themed-icon harvest grows — action definitions never do.</summary>
public static partial class ActionIcons
{
    public const string Play = "play";
    public const string PlayNext = "play-next";
    public const string Queue = "queue";
    public const string Like = "like";
    public const string Save = "save";
    /// <summary>Legacy semantic key retained for existing app actions; new descriptors choose Like or Save.</summary>
    public const string Heart = "heart";
    public const string Add = "add";
    public const string Album = "album";
    public const string Artist = "artist";
    public const string Link = "link";
    public const string Remove = "remove";
    public const string Delete = "delete";
    public const string Open = "open";
    public const string Rename = "rename";
    public const string People = "people";
    public const string Globe = "globe";
    public const string Credits = "credits";
    public const string Share = "share";
    public const string CopyUri = "copy-uri";
    public const string OpenWeb = "open-web";
    public const string Radio = "radio";
    public const string Video = "video";
    public const string Replace = "replace";
    public const string Locate = "locate";
    public const string RevealFolder = "reveal-folder";
    public const string Pin = "pin";
    public const string Unpin = "unpin";
    public const string Folder = "folder";
}

/// <summary>The capability names a descriptor declares. RECORDED but UNENFORCED until the sandboxed host lands, when
/// it gates invocation on the extension's granted set — first-party descriptors still declare them honestly, so
/// enforcement turns on without re-authoring the table.</summary>
public static class Permissions
{
    public const string LibraryRead = "library.read";
    public const string LibraryWrite = "library.write";
    public const string HistoryRead = "history.read";
    public const string PlaybackRead = "playback.read";
    public const string PlaybackControl = "playback.control";
    public const string NavigationContribute = "navigation.contribute";
    public const string ActionsInvoke = "actions.invoke";
    public const string StoragePrivate = "storage.private";
    public const string SecretsPrivate = "secrets.private";
    public const string ClipboardWrite = "clipboard.write";
    public const string ExternalOpen = "external.open";
    /// <summary>Local presentation state (pins, section expansion) — deliberately NOT in the third-party list: an
    /// external extension does not get to rewrite the user's sidebar behind their back.</summary>
    public const string SidebarPins = "sidebar.pins";
}

// ══ 2. THE TARGETING MATRIX ═════════════════════════════════════════════════════════════════════════════════════════

/// <summary>What a stored binding points AT. The PERSISTED vocabulary (a document records the name), so append only.
/// 0.2.9 called this <c>SidebarActionTargetMode</c>; the wire spelling is unchanged.</summary>
public enum ActionTargetMode : byte
{
    /// <summary>No target at all — a global verb (play/pause, open a page).</summary>
    None = 0,
    /// <summary>An entity the user picked (an album, a playlist, an artist, a show).</summary>
    FixedEntity = 1,
    /// <summary>A track the user picked. Tracks have no route and are never pinnable — the uri IS the whole target.</summary>
    FixedTrack = 2,
    /// <summary>Whatever is playing right now.</summary>
    NowPlaying = 3,
    /// <summary>Whatever page is showing right now.</summary>
    ActiveRoute = 4,
}

/// <summary>The target modes a DESCRIPTOR accepts — the flag mirror of <see cref="ActionTargetMode"/>, because a
/// descriptor accepts a SET while a binding names exactly one. Bit values are an implementation detail (never
/// persisted; the persisted vocabulary is <see cref="ActionTargetMode"/>).</summary>
[Flags]
public enum ActionTargetModes : byte
{
    /// <summary>Accepts nothing — an unbindable descriptor.</summary>
    Nothing = 0,
    None = 1,
    FixedEntity = 2,
    FixedTrack = 4,
    NowPlaying = 8,
    ActiveRoute = 16,
    /// <summary>The two "an entity the user picked" forms.</summary>
    AnyFixed = FixedEntity | FixedTrack,
    /// <summary>The two forms resolved from live app state.</summary>
    AnyDynamic = NowPlaying | ActiveRoute,
    All = None | FixedEntity | FixedTrack | NowPlaying | ActiveRoute,
}

/// <summary>Why a bound action cannot run right now — THE SEVEN REASONS. The row still RENDERS (visible-but-disabled
/// with <see cref="Actions.ReasonLocKey"/>'s explanation); it never vanishes, because a vanishing row makes the user's
/// own sidebar look broken.</summary>
public enum ActionUnavailable : byte
{
    None = 0,
    /// <summary>The binding names a mode this descriptor does not accept (a document authored by a newer build, or an
    /// extension that narrowed its accepted set in an update).</summary>
    ModeNotSupported = 1,
    /// <summary>A FixedEntity/FixedTrack binding with no target key.</summary>
    MissingTargetKey = 2,
    /// <summary>A NowPlaying binding while nothing is playing.</summary>
    NoNowPlaying = 3,
    /// <summary>An ActiveRoute binding with no resolvable current page.</summary>
    NoActiveRoute = 4,
    /// <summary>No descriptor is registered for the binding's key (extension removed or disabled).</summary>
    ActionMissing = 5,
    /// <summary>The descriptor resolved but a service it needs is absent — INCLUDING the deliberate refusal to run a
    /// confirmation-required action with nowhere to confirm. Never silently skip the confirm.</summary>
    HostUnavailable = 6,
    /// <summary>The descriptor's own enablement resolver said no (nothing to say beyond "not right now").</summary>
    NotApplicable = 7,
}

/// <summary>ONE bound action's persisted target. Owner J's <c>Sidebar.Doc.cs</c> decodes <c>sidebar-layout.json</c>
/// into this; the customizer's picker writes it; the registry resolves it.</summary>
/// <param name="ProviderId">The publisher segment (<c>wavee</c>, <c>publisher</c>).</param>
/// <param name="ActionId">The contribution segment, or already the fully-qualified key.</param>
/// <param name="TargetMode">Which target this binding names.</param>
/// <param name="TargetKey">The entity uri / pin id for the two FIXED modes; null otherwise.</param>
/// <param name="Arguments">OPAQUE argument JSON, never interpreted here.</param>
public readonly record struct ActionBinding(
    string ProviderId,
    string ActionId,
    ActionTargetMode TargetMode = ActionTargetMode.None,
    string? TargetKey = null,
    string? Arguments = null);

/// <summary>The live app facts the dynamic target modes resolve against. A plain readonly struct so the pure resolver
/// never touches a signal, a store or a service: the caller SNAPSHOTS it.</summary>
public readonly record struct ActionHostState(
    EntityUri NowPlaying = default,
    EntityUri NowPlayingContext = default,
    Shell.Route? ActiveRoute = null,
    string? FixedTargetName = null,
    string? NowPlayingName = null,
    string? ActiveRouteName = null)
{
    public static ActionHostState Empty => default;
}

/// <summary>The resolved WHAT of a bound invocation: the entity, its route, the surrounding context — or the reason
/// none of them could be produced. Passed to a descriptor's execution adapter, so an adapter never RE-resolves and
/// therefore can never disagree with the enablement the row rendered.</summary>
public readonly record struct ActionResolution(
    ActionTargetMode Mode,
    EntityUri Uri,
    Shell.Route Route,
    EntityUri ContextUri,
    ActionUnavailable Reason,
    string Name = "")
{
    public bool Available => Reason == ActionUnavailable.None;

    /// <summary>The loc key of the concise explanation a disabled row shows. Null when available.</summary>
    public string? ReasonLocKey => Actions.ReasonLocKey(Reason);
}

// ══ 3. THE DESCRIPTOR ═══════════════════════════════════════════════════════════════════════════════════════════════

/// <summary>The ambient seam bag every action's enablement and execution reads — ONE reference-stable instance,
/// provided at the app root and refreshed by the shell every render (the instance never changes, so the provide never
/// churns its consumers).
/// <para>Everything here is a DELEGATE, not a service object: this file is CORE, and a `Track` verb that reached into
/// an overlay type or a bridge would drag the engine's UI surface into the action table. Owner I's
/// <c>Actions.UI.cs</c> and the Wave-5 entity files fill the seams; a seam left null makes the action render
/// visible-but-disabled with <see cref="ActionUnavailable.HostUnavailable"/>, never throw.</para></summary>
public sealed class ActionServices
{
    // ── navigation ──
    /// <summary>Go to a route (the app's one nav verb).</summary>
    public Action<Shell.Route>? Go;
    /// <summary>The ACTIVE page's route, read at INVOKE time. A FUNC, not a value: the bag is refreshed per render but
    /// a bound invoke happens later, and re-reading is the only way the answer is the page the user is looking at.</summary>
    public Func<Shell.Route>? CurrentRoute;
    /// <summary>The active page as a canonical PINNABLE destination — null means the surface is deliberately internal
    /// and non-pinnable. Tabs, page overflow and drag sources read this at invoke time so they cannot disagree about
    /// what "this page" means.</summary>
    public Func<Shell.Route?>? CurrentDestination;

    // ── playback ──
    public Action<EntityUri>? Play;
    public Action<EntityUri>? PlayNext;
    public Action<EntityUri>? AddToQueue;
    /// <summary>Start the song/artist radio seeded by this uri.</summary>
    public Action<EntityUri>? StartRadio;

    // ── library ──
    public Func<EntityUri, bool>? IsSaved;
    public Action<EntityUri, bool>? SetSaved;

    // ── sidebar pins (owner J fills these; `PinRowRule` itself is `Sidebar.cs`) ──
    public Func<Shell.Route, bool>? IsPinned;
    public Action<Shell.Route, bool>? SetPinned;

    // ── platform ──
    /// <summary>Copy text to the clipboard.</summary>
    public Action<string>? Clipboard;
    /// <summary>Open a url in the user's browser.</summary>
    public Action<string>? OpenExternal;
    /// <summary>UI-thread marshal for async completions that must touch signals.</summary>
    public Action<Action>? Post;

    /// <summary>Is there an overlay to confirm in? A confirmation-required action REFUSES to run when there is not —
    /// a null overlay must never degrade into an unconfirmed run.</summary>
    public Func<bool>? CanConfirm;
    /// <summary>Present the confirm dialog and invoke the continuation only on OK. Loc KEYS in, never literals.</summary>
    public Action<ConfirmRequest>? Confirm;

    /// <summary>The live contribution registry — the ONE lookup path for anything BOUND. Null before the composition
    /// root builds it; bound UI then renders its rows disabled rather than throwing.</summary>
    public Actions.Registry? Extensions;
}

/// <summary>A confirmation, as loc KEYS plus the continuation.</summary>
public readonly record struct ConfirmRequest(string TitleLocKey, string BodyLocKey, string PrimaryLocKey, Action OnConfirm);

/// <summary>ONE bindable action, as the extension platform sees it.
/// <para>RELATIONSHIP TO <see cref="AppAction"/>: deliberately different shapes, and neither replaces the other.
/// <see cref="AppAction"/> is the CONTEXT-MENU model — it acts on a live <see cref="ActionTarget"/> built at menu-open
/// time (a resolved track set, a playlist host with row ids) and its label is count-aware. A DESCRIPTOR is the BOUND
/// model — it acts on a persisted <see cref="ActionBinding"/> whose target is a mode plus a key, so it must resolve
/// the target itself, must be able to say WHY it cannot, and must survive an app restart. First-party descriptors
/// therefore WRAP the existing <see cref="ActionId"/> verbs (<see cref="LegacyId"/> records which one) rather than
/// re-implementing them.</para>
/// <para>AOT SHAPE: a plain init-only class with delegate members, constructed by hand in <see cref="Actions.Builtins"/>
/// today and emittable verbatim by a source generator. No reflection, no attributes, no runtime discovery.</para>
/// <para>THREADING: constructed at startup on the UI thread and then IMMUTABLE. The delegates run on the UI thread
/// only.</para></summary>
public sealed class ActionDescriptor
{
    /// <summary>The namespaced stable key — <c>wavee.play</c>, <c>publisher.extension.refresh</c>. PERSISTED inside
    /// bindings, so it may never change once shipped.</summary>
    public required string Key { get; init; }

    /// <summary>Loc KEY of the display label — never a literal string.</summary>
    public required string LabelLocKey { get; init; }

    /// <summary>Semantic icon key (<see cref="ActionIcons"/>) — never a raw glyph.</summary>
    public required string IconKey { get; init; }

    /// <summary>The target modes this action accepts. The customizer's binding UI offers exactly these; a stored
    /// binding naming anything else resolves <see cref="ActionUnavailable.ModeNotSupported"/>.</summary>
    public ActionTargetModes AcceptedTargets { get; init; } = ActionTargetModes.None;

    /// <summary>The argument schema as OPAQUE JSON (the customizer generates property controls from it later). Null =
    /// the action takes no arguments. Arguments themselves ride on the binding and are likewise never interpreted
    /// here.</summary>
    public string? ArgumentSchema { get; init; }

    /// <summary>Extra enablement beyond target resolution (a seam present, a capability live). Null ⇒ enabled whenever
    /// the target resolves. A false result renders the row visible-but-disabled with
    /// <see cref="ActionUnavailable.NotApplicable"/>.</summary>
    public Func<ActionServices, ActionBinding, ActionResolution, bool>? IsEnabled { get; init; }

    /// <summary>Non-null ⇒ the action is a TOGGLE (a saved heart, a pin state): the row renders checked/unchecked and
    /// the icon picks the filled variant.</summary>
    public Func<ActionServices, ActionBinding, ActionResolution, bool>? IsChecked { get; init; }

    /// <summary>Destructive verb (remove/delete). Carried for presentation — the SAFETY gate is
    /// <see cref="RequiresConfirmation"/>, which is what actually blocks a bypass.</summary>
    public bool Destructive { get; init; }

    /// <summary>True ⇒ <see cref="Actions.Execute"/> routes through the app's existing confirmation surface and
    /// REFUSES to run at all when there is nowhere to confirm. No binding can bypass it.</summary>
    public bool RequiresConfirmation { get; init; }

    /// <summary>Confirmation copy (loc KEYS). Title/primary fall back to <see cref="LabelLocKey"/>; the body falls
    /// back to the title. Ignored unless <see cref="RequiresConfirmation"/>.</summary>
    public string? ConfirmTitleLocKey { get; init; }
    public string? ConfirmBodyLocKey { get; init; }
    public string? ConfirmPrimaryLocKey { get; init; }

    /// <summary>Capability names this action needs. Recorded, unenforced (see <see cref="Permissions"/>).</summary>
    public string[] RequiredPermissions { get; init; } = [];

    /// <summary>The <see cref="Wavee.ActionId"/> this first-party descriptor wraps
    /// (<see cref="Wavee.ActionId.None"/> for a third-party contribution). Diagnostics only — nothing dispatches on
    /// it.</summary>
    public ActionId LegacyId { get; init; }

    /// <summary>The execution adapter. Receives the ALREADY-RESOLVED target, so an adapter never re-resolves and
    /// therefore can never disagree with the enablement the row rendered.</summary>
    public required Action<ActionServices, ActionBinding, ActionResolution> Run { get; init; }
}

// ══ 4. THE PLATFORM ════════════════════════════════════════════════════════════════════════════════════════════════

public static partial class Actions
{
    // ── 4.1 the namespaced-key vocabulary ───────────────────────────────────────────────────────────────────────────

    /// <summary>The key scheme shared by actions, data sources and every future contribution kind:
    /// <c>publisher.contribution[.sub]</c>. ONE place, so a key a descriptor declares and a key a stored binding
    /// composes can never disagree.</summary>
    public static class Key
    {
        public const char Separator = '.';

        /// <summary>The trusted first-party publisher segment. First-party contributions are literally the extension
        /// <c>wavee</c> — there is no privileged non-extension path.</summary>
        public const string FirstPartyPublisher = "wavee";

        /// <summary>Upper bound on a whole key. Generous but bounded: a key is persisted inside
        /// <c>sidebar-layout.json</c>, so an unbounded one is a document-size hazard.</summary>
        public const int MaxLength = 128;

        /// <summary>A valid key is 2+ separator-separated segments, each starting with an ASCII letter and continuing
        /// with ASCII letters/digits/<c>-</c>/<c>_</c>. camelCase is allowed and used (<c>wavee.playNext</c>); what is
        /// refused is an empty segment, a leading/trailing/doubled dot, whitespace, and a SINGLE-segment key (which
        /// would let a contribution squat the publisher namespace itself).</summary>
        public static bool IsValid(string? key)
        {
            if (string.IsNullOrEmpty(key) || key.Length > MaxLength) return false;
            if (key[^1] == Separator) return false;                 // a trailing separator is an empty last segment
            int segments = 0;
            int i = 0;
            while (i < key.Length)
            {
                int start = i;
                while (i < key.Length && key[i] != Separator) i++;
                if (i == start) return false;                       // empty segment ⇒ "", ".x", "a..b", "a."
                if (!IsSegmentStart(key[start])) return false;
                for (int k = start + 1; k < i; k++)
                    if (!IsSegmentBody(key[k])) return false;
                segments++;
                if (i < key.Length) i++;                            // step over the separator
            }
            return segments >= 2;
        }

        static bool IsSegmentStart(char c) => (uint)((c | 0x20) - 'a') <= 'z' - 'a';
        static bool IsSegmentBody(char c) => IsSegmentStart(c) || (uint)(c - '0') <= 9 || c == '-' || c == '_';

        /// <summary>The registry key a stored binding resolves to. A binding carries the publisher and the
        /// contribution separately; older/hand-edited documents may already carry the fully-qualified form in
        /// <c>ActionId</c>, so an id that ALREADY starts with the publisher + '.' is taken as-is rather than
        /// double-prefixed. Returns "" when either half is missing — an unresolvable key, which the registry then
        /// reports as a MISSING action rather than silently matching something else.</summary>
        public static string Compose(string? providerId, string? actionId)
        {
            if (string.IsNullOrEmpty(actionId)) return "";
            if (string.IsNullOrEmpty(providerId)) return actionId;
            if (actionId.Length > providerId.Length
                && actionId[providerId.Length] == Separator
                && actionId.StartsWith(providerId, StringComparison.Ordinal)) return actionId;
            return providerId + Separator + actionId;
        }

        /// <summary>The publisher segment of a key ("" when there is none).</summary>
        public static string PublisherOf(string? key)
        {
            if (string.IsNullOrEmpty(key)) return "";
            int dot = key.IndexOf(Separator);
            return dot <= 0 ? "" : key[..dot];
        }

        public static bool IsFirstParty(string? key)
            => string.Equals(PublisherOf(key), FirstPartyPublisher, StringComparison.Ordinal);
    }

    /// <summary>The registry key a binding resolves to.</summary>
    public static string KeyOf(in ActionBinding binding) => Key.Compose(binding.ProviderId, binding.ActionId);

    // ── 4.2 the seven reasons' captions ─────────────────────────────────────────────────────────────────────────────
    //
    // LITERAL loc keys rather than generated members: a missing key renders loudly as "[key]" by design, which is
    // exactly the signal we want. `Loc.Get` happens at the UI edge — this file returns the KEY.

    public const string LocKeyModeNotSupported = "sidebar.action.unavailable.mode";
    public const string LocKeyMissingTargetKey = "sidebar.action.unavailable.noTarget";
    public const string LocKeyNoNowPlaying = "sidebar.action.unavailable.noNowPlaying";
    public const string LocKeyNoActiveRoute = "sidebar.action.unavailable.noRoute";
    public const string LocKeyActionMissing = "sidebar.action.unavailable.missing";
    public const string LocKeyHostUnavailable = "sidebar.action.unavailable.host";
    public const string LocKeyNotApplicable = "sidebar.action.unavailable.notNow";

    public static string? ReasonLocKey(ActionUnavailable reason) => reason switch
    {
        ActionUnavailable.None => null,
        ActionUnavailable.ModeNotSupported => LocKeyModeNotSupported,
        ActionUnavailable.MissingTargetKey => LocKeyMissingTargetKey,
        ActionUnavailable.NoNowPlaying => LocKeyNoNowPlaying,
        ActionUnavailable.NoActiveRoute => LocKeyNoActiveRoute,
        ActionUnavailable.ActionMissing => LocKeyActionMissing,
        ActionUnavailable.HostUnavailable => LocKeyHostUnavailable,
        _ => LocKeyNotApplicable,
    };

    // ── 4.3 the pure target matrix ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>The single-mode flag for a persisted mode value. An unknown (future) value maps to
    /// <see cref="ActionTargetModes.Nothing"/>, so it is REFUSED by <see cref="Accepts"/> rather than silently treated
    /// as <see cref="ActionTargetMode.None"/>.</summary>
    public static ActionTargetModes Bit(ActionTargetMode mode) => mode switch
    {
        ActionTargetMode.None => ActionTargetModes.None,
        ActionTargetMode.FixedEntity => ActionTargetModes.FixedEntity,
        ActionTargetMode.FixedTrack => ActionTargetModes.FixedTrack,
        ActionTargetMode.NowPlaying => ActionTargetModes.NowPlaying,
        ActionTargetMode.ActiveRoute => ActionTargetModes.ActiveRoute,
        _ => ActionTargetModes.Nothing,
    };

    public static bool Accepts(ActionTargetModes accepted, ActionTargetMode mode)
    {
        var bit = Bit(mode);
        return bit != ActionTargetModes.Nothing && (accepted & bit) != 0;
    }

    /// <summary>Resolve a binding's target. <paramref name="accepted"/> is checked FIRST: a mode the descriptor does
    /// not accept is <see cref="ActionUnavailable.ModeNotSupported"/> even when the app state would have satisfied it,
    /// so NARROWING a descriptor's accepted set can never WIDEN what an old binding does.</summary>
    public static ActionResolution ResolveTarget(ActionTargetMode mode, string? targetKey,
        ActionTargetModes accepted, in ActionHostState host)
    {
        if (!Accepts(accepted, mode)) return Fail(mode, ActionUnavailable.ModeNotSupported);

        switch (mode)
        {
            case ActionTargetMode.None:
                return new ActionResolution(mode, default, Shell.Route.None, default, ActionUnavailable.None);

            case ActionTargetMode.FixedEntity:
            {
                if (string.IsNullOrEmpty(targetKey)) return Fail(mode, ActionUnavailable.MissingTargetKey);
                // A stored key may be EITHER an entity uri ("spotify:album:…") or the pin/route form
                // ("album:spotify:album:…"). Both normalize through the ONE authority — `Shell`'s route codec — so a
                // key captured from a menu and a key captured from a sidebar row resolve to the same target.
                var asRoute = Shell.Parse(targetKey);
                // An EXACT destination (`liked`, `local`, `history`) is a pinnable target with no entity behind it.
                if (!asRoute.IsNone && (asRoute.Subject.IsValid || !Shell.Row(asRoute.Kind).IsPrefix))
                    return new ActionResolution(mode, asRoute.Subject, asRoute, default,
                        ActionUnavailable.None, host.FixedTargetName ?? "");
                var uri = EntityUri.Parse(targetKey);
                if (uri.IsValid)
                    return new ActionResolution(mode, uri, Shell.For(uri), default,
                        ActionUnavailable.None, host.FixedTargetName ?? "");
                // An unrecognized key is still handed through: a future/third-party entity scheme must not be silently
                // unbindable just because the route scheme does not know it.
                return new ActionResolution(mode, default, Shell.Route.None, default,
                    ActionUnavailable.None, host.FixedTargetName ?? "");
            }

            case ActionTargetMode.FixedTrack:
            {
                if (string.IsNullOrEmpty(targetKey)) return Fail(mode, ActionUnavailable.MissingTargetKey);
                // Tracks have no route and are never pinnable — the uri IS the whole target.
                return new ActionResolution(mode, EntityUri.Parse(targetKey), Shell.Route.None, default,
                    ActionUnavailable.None, host.FixedTargetName ?? "");
            }

            case ActionTargetMode.NowPlaying:
                if (!host.NowPlaying.IsValid) return Fail(mode, ActionUnavailable.NoNowPlaying);
                return new ActionResolution(mode, host.NowPlaying,
                    host.NowPlayingContext.IsValid ? Shell.For(host.NowPlayingContext) : Shell.Route.None,
                    host.NowPlayingContext, ActionUnavailable.None, host.NowPlayingName ?? "");

            case ActionTargetMode.ActiveRoute:
                if (host.ActiveRoute is not { IsNone: false } active) return Fail(mode, ActionUnavailable.NoActiveRoute);
                // The route key IS the pin id for every navigable kind, so the entity behind it (when there is one)
                // comes straight back out of the scheme.
                return new ActionResolution(mode, active.Subject, active, default,
                    ActionUnavailable.None, host.ActiveRouteName ?? "");

            default:
                return Fail(mode, ActionUnavailable.ModeNotSupported);
        }
    }

    /// <summary>Binding overload — the form every call site actually uses.</summary>
    public static ActionResolution ResolveTarget(in ActionBinding binding, ActionTargetModes accepted,
        in ActionHostState host)
        => ResolveTarget(binding.TargetMode, binding.TargetKey, accepted, in host);

    /// <summary>An unavailability produced OUTSIDE the matrix (a missing descriptor, a refused confirmation surface, a
    /// descriptor's own veto), shaped as a resolution so ONE struct carries every disabled reason a row can show.</summary>
    public static ActionResolution Unavailable(ActionTargetMode mode, ActionUnavailable reason) => Fail(mode, reason);

    static ActionResolution Fail(ActionTargetMode mode, ActionUnavailable reason)
        => new(mode, default, Shell.Route.None, default, reason);

    // ── 4.4 resolve + execute (the one path bound UI takes) ─────────────────────────────────────────────────────────

    /// <summary>Snapshot the live app facts the dynamic modes resolve against. When the host supplied no route
    /// provider, an <see cref="ActionTargetMode.ActiveRoute"/> binding resolves UNAVAILABLE rather than guessing.</summary>
    public static ActionHostState HostState(ActionServices services, in ActionBinding binding)
    {
        var playing = new EntityUri(Playback.CurrentId.Peek());
        var context = new EntityUri(Playback.ContextUri.Peek());
        var route = services.CurrentRoute?.Invoke() ?? new Shell.Route(Shell.RouteKind.NotFound);
        string? playingName = playing.IsValid ? NowPlayingTitle() : null;
        string? fixedName = null;
        if (binding.TargetMode is ActionTargetMode.FixedEntity or ActionTargetMode.FixedTrack
            && binding.TargetKey is { Length: > 0 } key)
        {
            var fixedUri = EntityUri.Parse(key);
            if (fixedUri.IsValid && playing.IsValid && fixedUri == playing) fixedName = playingName;
        }
        return new ActionHostState(playing, context, route, fixedName, playingName, Shell.Dest(route).Title);
    }

    /// <summary>The playing row's title, WITHOUT allocating a slot: the ref already names the table, so this is two
    /// array indexes. Going through <c>Entities.Track(id)</c> would ALLOCATE a row for an id the table has never seen,
    /// inside a render-time enablement predicate — a write during render, and the exact shape P1 forbids.</summary>
    static string NowPlayingTitle()
    {
        var r = Playback.Current.Peek();
        if (r.IsNone) return "";
        return r.Kind switch
        {
            EntityKind.Track => new Track(r.Slot).Title,
            EntityKind.Episode => new Episode(r.Slot).Title,
            _ => "",
        };
    }

    /// <summary>The playing row as a TRACK handle. Gated on the REF, never on <c>Track.IsValid</c>: a handle's
    /// validity probe reads <c>Entities.Current</c>, which a headless test has not booted — and "nothing is playing"
    /// must answer false rather than throw.</summary>
    internal static bool TryNowPlayingTrack(out Track track)
    {
        var r = Playback.Current.Peek();
        if (r.IsNone || r.Kind != EntityKind.Track) { track = default; return false; }
        track = new Track(r.Slot);
        return true;
    }

    /// <summary>Resolve a binding's target AND fold in every NON-target reason a row can be disabled: a mode the
    /// descriptor does not accept, a missing key, no now-playing / no active route, the descriptor's own veto, and a
    /// confirmation-required action with nowhere to confirm. ONE call, so the row's disabled state and
    /// <see cref="Execute"/>'s refusal can never disagree.</summary>
    public static ActionResolution Resolve(ActionDescriptor descriptor, ActionServices services, in ActionBinding binding)
    {
        var host = HostState(services, in binding);
        var target = ResolveTarget(in binding, descriptor.AcceptedTargets, in host);
        if (!target.Available) return target;

        if (descriptor.RequiresConfirmation && (services.Confirm is null || services.CanConfirm?.Invoke() != true))
            return Unavailable(binding.TargetMode, ActionUnavailable.HostUnavailable);

        if (descriptor.IsEnabled is { } gate && !gate(services, binding, target))
            return Unavailable(binding.TargetMode, ActionUnavailable.NotApplicable);

        return target;
    }

    /// <summary>Is a bound toggle currently ON?</summary>
    public static bool Checked(ActionDescriptor descriptor, ActionServices services, in ActionBinding binding)
    {
        if (descriptor.IsChecked is not { } isChecked) return false;
        var target = Resolve(descriptor, services, in binding);
        return target.Available && isChecked(services, binding, target);
    }

    /// <summary>Invoke. NO-OPS (returning the reason, never throwing) when the target is unavailable; routes a
    /// confirmation-required action through the confirm seam and runs nothing until the user confirms.</summary>
    public static ActionUnavailable Execute(ActionDescriptor descriptor, ActionServices services, in ActionBinding binding)
    {
        var target = Resolve(descriptor, services, in binding);
        if (!target.Available) return target.Reason;

        if (!descriptor.RequiresConfirmation)
        {
            descriptor.Run(services, binding, target);
            return ActionUnavailable.None;
        }

        // Resolve() already refused a missing confirm seam, so this cannot silently skip the confirmation.
        if (services.Confirm is not { } confirm) return ActionUnavailable.HostUnavailable;
        var run = descriptor.Run;
        var s = services;
        var b = binding;
        var t = target;
        confirm(new ConfirmRequest(
            descriptor.ConfirmTitleLocKey ?? descriptor.LabelLocKey,
            descriptor.ConfirmBodyLocKey ?? descriptor.ConfirmTitleLocKey ?? descriptor.LabelLocKey,
            descriptor.ConfirmPrimaryLocKey ?? descriptor.LabelLocKey,
            () => run(s, b, t)));
        return ActionUnavailable.None;
    }

    // ── 4.5 the registry's bookkeeping core ─────────────────────────────────────────────────────────────────────────

    /// <summary>Why a registration was accepted or refused. <see cref="Registered"/> is the only success.</summary>
    public enum RegisterOutcome : byte
    {
        Registered = 0,
        /// <summary>The contribution itself was null, or carried no key at all.</summary>
        RejectedNull = 1,
        /// <summary>The key is not a valid namespaced key.</summary>
        RejectedInvalidKey = 2,
        /// <summary>Something is already registered under this key. FIRST WINS — the earlier registration is kept
        /// untouched and this one is dropped, so a broken or malicious extension can never shadow a first-party
        /// action.</summary>
        RejectedDuplicate = 3,
    }

    /// <summary>One refused registration. Surfaced by devtools / the extensions page — NEVER a toast: a registration
    /// problem is a developer/publisher fact, not a user interruption.</summary>
    public readonly record struct RegistryDiagnostic(string Key, RegisterOutcome Outcome, string Detail);

    /// <summary>An append-only, insertion-ordered, key-unique table of contributions of one kind. <see cref="Items"/>
    /// preserves registration order (the picker lists first-party first because the built-in table registers first),
    /// and <see cref="Diagnostics"/> records every refusal so a silently-missing action is always explainable.
    /// <para>NOTHING IS EVER REMOVED — a disabled extension is filtered at the CONSUMPTION site, never unregistered,
    /// which keeps every stored binding resolvable to a descriptor so the row renders visible-but-disabled with a
    /// reason instead of vanishing.</para></summary>
    public sealed class RegistryTable<T> where T : class
    {
        readonly List<string> _keys = [];
        readonly List<T> _items = [];
        readonly Dictionary<string, int> _index = new(StringComparer.Ordinal);
        readonly List<RegistryDiagnostic> _diagnostics = [];

        public int Count => _items.Count;
        public IReadOnlyList<T> Items => _items;
        public IReadOnlyList<string> Keys => _keys;
        public IReadOnlyList<RegistryDiagnostic> Diagnostics => _diagnostics;

        public string KeyAt(int i) => _keys[i];
        public T ItemAt(int i) => _items[i];
        public bool Contains(string? key) => key is not null && _index.ContainsKey(key);
        public int IndexOf(string? key) => key is not null && _index.TryGetValue(key, out int i) ? i : -1;

        /// <summary>Register under <paramref name="key"/>. FIRST WINS.</summary>
        public RegisterOutcome Add(string? key, T? value)
        {
            if (value is null || string.IsNullOrEmpty(key))
                return Reject(key ?? "", RegisterOutcome.RejectedNull,
                    value is null ? "null contribution" : "empty key");
            if (!Key.IsValid(key))
                return Reject(key, RegisterOutcome.RejectedInvalidKey, "not a namespaced 'publisher.contribution' key");
            if (_index.ContainsKey(key))
                return Reject(key, RegisterOutcome.RejectedDuplicate, "already registered; the first registration wins");

            _index[key] = _items.Count;
            _keys.Add(key);
            _items.Add(value);
            return RegisterOutcome.Registered;
        }

        public bool TryGet(string? key, out T value)
        {
            if (key is not null && _index.TryGetValue(key, out int i)) { value = _items[i]; return true; }
            value = null!;
            return false;
        }

        RegisterOutcome Reject(string key, RegisterOutcome outcome, string detail)
        {
            _diagnostics.Add(new RegistryDiagnostic(key, outcome, detail));
            return outcome;
        }
    }

    /// <summary>What an extension may contribute today: actions (bindable from the customizer's action picker) and
    /// sidebar data sources (the row providers behind an extension section). Widened additively later, so an
    /// implementation written against this keeps compiling.
    /// <para>A data source is owner J's type (<c>Sidebar.cs</c>); the registry stores it behind <see cref="object"/>
    /// so this CORE file does not depend on the sidebar's row model, and <c>TryGetSource&lt;T&gt;</c> casts at the
    /// consumption site.</para></summary>
    public interface IRegistrar
    {
        /// <summary>Contribute one action. A duplicate key is REJECTED — the first registration wins — and recorded as
        /// a diagnostic.</summary>
        void RegisterAction(ActionDescriptor descriptor);

        /// <summary>Contribute one data source under its own namespaced id. Same first-wins policy.</summary>
        void RegisterDataSource(string id, object source);
    }

    /// <summary>A trusted, compile-time extension. Implementations register their contributions and keep no reference
    /// to the registrar afterwards.</summary>
    public interface IExtension
    {
        void Register(IRegistrar registrar);
    }

    /// <summary>THE contribution registry. It replaces the fixed-only <see cref="AppActions.All"/> lookup for
    /// everything BOUND — a binding, the customizer's action picker, a curated section's data source — while the
    /// <see cref="ActionId"/> enum and the <see cref="AppAction"/> context-menu table stay as they are (first-party
    /// descriptors wrap them). The guardrails, encoded here:
    /// <list type="bullet">
    /// <item><b>No new UI code looks up <see cref="AppActions.All"/>.</b> Bound UI resolves through
    /// <see cref="TryGetAction(in ActionBinding, out ActionDescriptor)"/>.</item>
    /// <item><b>Never <c>switch</c> on an extension id.</b> Section rendering resolves a contribution id through
    /// <see cref="TryGetSource"/>; first-party ids are ordinary registry keys under the publisher <c>wavee</c>.</item>
    /// <item><b>A stored binding always resolves to SOMETHING renderable.</b> An unresolvable key yields
    /// <see cref="ActionUnavailable.ActionMissing"/> — a visible-but-disabled row with a reason, never a vanishing
    /// one.</item>
    /// </list>
    /// <para>THREADING: UI thread only, unsynchronized, REGISTRATION-THEN-READ. Every contribution is registered
    /// during startup; afterwards the tables are read-only from the render path. No lock, no off-thread producer.</para></summary>
    public sealed class Registry : IRegistrar
    {
        readonly RegistryTable<ActionDescriptor> _actions = new();
        readonly RegistryTable<object> _sources = new();
        readonly List<string> _extensions = [];

        /// <summary>The process-wide instance <see cref="Build"/> produced. A composition root that has not built it
        /// yet must see NULL rather than an empty-but-plausible registry.</summary>
        public static Registry? Current { get; private set; }

        /// <summary>Build the registry and register the first-party extension's contributions. Called ONCE from the
        /// composition root; a second call REPLACES <see cref="Current"/> with the new instance (a login-gate swap
        /// must not double-register into a live table).</summary>
        public static Registry Build(ActionServices services)
        {
            var registry = new Registry();
            registry.Register(Key.FirstPartyPublisher, r => Builtins.RegisterAll(r, services));
            Current = registry;
            return registry;
        }

        /// <summary>Every registered action, in registration order — first-party first, because the built-in table
        /// registers first. This IS the customizer's action-picker source.</summary>
        public IReadOnlyList<ActionDescriptor> Actions => _actions.Items;

        /// <summary>Every registered data source, in registration order.</summary>
        public IReadOnlyList<object> Sources => _sources.Items;

        /// <summary>The extension ids that registered anything, in order.</summary>
        public IReadOnlyList<string> Extensions => _extensions;

        /// <summary>Refused registrations across BOTH tables. Empty on a healthy startup.</summary>
        public IReadOnlyList<RegistryDiagnostic> Diagnostics
        {
            get
            {
                if (_actions.Diagnostics.Count == 0) return _sources.Diagnostics;
                if (_sources.Diagnostics.Count == 0) return _actions.Diagnostics;
                var all = new List<RegistryDiagnostic>(_actions.Diagnostics.Count + _sources.Diagnostics.Count);
                all.AddRange(_actions.Diagnostics);
                all.AddRange(_sources.Diagnostics);
                return all;
            }
        }

        /// <summary>Run one trusted extension's registration pass.</summary>
        public Registry Register(string extensionId, IExtension extension) => Register(extensionId, extension.Register);

        /// <summary>Run one registration pass under a named extension id. The DELEGATE form is what the built-in
        /// table (and a future generator) uses — a static table is not an <see cref="IExtension"/> instance, and
        /// inventing one would add an allocation and a type per extension for nothing.</summary>
        public Registry Register(string extensionId, Action<IRegistrar> register)
        {
            if (!string.IsNullOrEmpty(extensionId) && !_extensions.Contains(extensionId)) _extensions.Add(extensionId);
            register(this);
            return this;
        }

        // Both accept null defensively: the interface says non-null, but the caller can be a sandboxed extension's
        // manifest replay, and a hostile or broken contribution must be a DIAGNOSTIC rather than an NRE at startup.
        public void RegisterAction(ActionDescriptor descriptor)
            => _actions.Add(descriptor?.Key, descriptor);

        public void RegisterDataSource(string id, object source) => _sources.Add(id, source);

        public bool TryGetAction(string? key, out ActionDescriptor descriptor) => _actions.TryGet(key, out descriptor);

        /// <summary>Lookup for a stored binding: the key is <c>ProviderId + '.' + ActionId</c>, tolerating a document
        /// that already stored the fully-qualified form.</summary>
        public bool TryGetAction(in ActionBinding binding, out ActionDescriptor descriptor)
            => _actions.TryGet(KeyOf(in binding), out descriptor);

        public bool TryGetSource<T>(string? id, out T source) where T : class
        {
            if (_sources.TryGet(id, out var raw) && raw is T typed) { source = typed; return true; }
            source = null!;
            return false;
        }

        public bool HasAction(string? key) => _actions.Contains(key);
        public bool HasSource(string? id) => _sources.Contains(id);

        /// <summary>What a bound row should render: available, or visible-but-disabled with a reason. A key that
        /// resolves to nothing is <see cref="ActionUnavailable.ActionMissing"/> — the extension was removed or
        /// disabled, and the user must be TOLD that rather than have their row silently disappear.</summary>
        public ActionResolution Resolve(ActionServices services, in ActionBinding binding)
            => TryGetAction(in binding, out var descriptor)
                ? Wavee.Actions.Resolve(descriptor, services, in binding)
                : Unavailable(binding.TargetMode, ActionUnavailable.ActionMissing);

        /// <summary>Invoke a bound action. Returns the refusal reason (never throws) when nothing ran.</summary>
        public ActionUnavailable Execute(ActionServices services, in ActionBinding binding)
            => TryGetAction(in binding, out var descriptor)
                ? Wavee.Actions.Execute(descriptor, services, in binding)
                : ActionUnavailable.ActionMissing;
    }
}

// ══ 5. THE CONTEXT-MENU MODEL (the other half of A7's "one action table") ═══════════════════════════════════════════
//
// A DESCRIPTOR acts on a persisted binding; an `AppAction` acts on a LIVE target built at menu-open time. Both are
// here so the two shapes cannot drift into two files with two opinions about what an action is.

/// <summary>What a context menu / batch bar is acting on.</summary>
public enum TargetKind : byte { None, Tracks, Album, Artist, Playlist, QueueEntry, SidebarItem, NowPlaying }

/// <summary>The hosting playlist a track set was right-clicked INSIDE (default elsewhere). <see cref="Caps"/> is the
/// playlist table's own <see cref="PlaylistCaps"/> bitmask (<c>Entities/Playlist.cs</c>) — one vocabulary, not a
/// second one here. Rows are resolved at
/// open time (original playlist indices, in display order) so a remove has indices without re-querying. For a
/// CONTAINER target (a sidebar playlist row) <see cref="Rows"/> is empty and only <see cref="Caps"/> gates.</summary>
public readonly record struct PlaylistHost(EntityUri Playlist, PlaylistCaps Caps, IReadOnlyList<int> Rows)
{
    public static PlaylistHost None => new(default, PlaylistCaps.None, []);
    public bool IsSome => Playlist.IsValid;
}

/// <summary>The action target: kind + the track set (Tracks / QueueEntry / NowPlaying) or the container uri/name, plus
/// the optional playlist host and the queue-entry identity. Built at menu-open / bar-render time, NEVER retained.</summary>
public readonly record struct ActionTarget(
    TargetKind Kind,
    IReadOnlyList<Track> Tracks,
    EntityUri Uri,
    string Name,
    PlaylistHost Host,
    long QueueItemId = 0)
{
    static readonly Track[] NoTracks = [];

    public int Count => Tracks?.Count ?? 0;
    public Track? Single => Count == 1 ? Tracks[0] : null;

    public static ActionTarget ForTracks(IReadOnlyList<Track> tracks, PlaylistHost host = default)
        => new(TargetKind.Tracks, tracks ?? NoTracks,
            tracks is { Count: > 0 } ? tracks[0].Uri : default,
            tracks is { Count: > 0 } ? tracks[0].Title : "", host);

    public static ActionTarget ForAlbum(EntityUri uri, string name)
        => new(TargetKind.Album, NoTracks, uri, name, PlaylistHost.None);

    public static ActionTarget ForArtist(EntityUri uri, string name)
        => new(TargetKind.Artist, NoTracks, uri, name, PlaylistHost.None);

    public static ActionTarget ForPlaylist(EntityUri uri, string name, PlaylistHost host = default)
        => new(TargetKind.Playlist, NoTracks, uri, name, host);

    public static ActionTarget ForQueueEntry(Track track, long queueItemId)
        => new(TargetKind.QueueEntry, [track], track.Uri, track.Title, PlaylistHost.None, queueItemId);

    public static ActionTarget ForNowPlaying(Track track)
        => new(TargetKind.NowPlaying, [track], track.Uri, track.Title, PlaylistHost.None);
}

/// <summary>The action context an <see cref="AppAction"/> receives: the WHAT (<see cref="Target"/>) + the HOW
/// (<see cref="S"/>). Built at open/render time, passed by value, never retained.</summary>
public readonly record struct ActionContext(ActionTarget Target, ActionServices S);

/// <summary>The PURE decision core behind the enablement / checked predicates — extracted so the rules are testable
/// engine-free. The <see cref="AppAction"/> lambdas are thin adapters over these: seams in, rule here.</summary>
public static class ActionRules
{
    /// <summary>Toggle-like checked state: checked iff EVERY track (≥ 1) is saved. A track without a uri counts
    /// unsaved.</summary>
    public static bool AllSaved(IReadOnlyList<Track> tracks, Func<EntityUri, bool> isSaved)
    {
        if (tracks is not { Count: > 0 }) return false;
        for (int i = 0; i < tracks.Count; i++)
        {
            var uri = tracks[i].Uri;
            if (!uri.IsValid || !isSaved(uri)) return false;
        }
        return true;
    }

    /// <summary>View-credits gate: a single track carrying a primary-artist uri (the fetch keys off artist + track, so
    /// both must be present).</summary>
    public static bool CanViewCredits(in ActionTarget target)
        => target.Single is { } t && t.Uri.IsValid && t.ArtistSlots.Length > 0
           && new Artist(t.ArtistSlots[0]).Uri.IsValid;

    /// <summary>Go-to-album gate: a single row whose album ref names a real RELEASE. An EPISODE rides the same track
    /// read-model but carries its SHOW in that slot, so an unguarded "Go to album" offered a podcast episode a route
    /// into the album page of a show — a page that does not exist. Only a SHOW ref is excluded; a uri the parser
    /// cannot classify keeps the row it has always had.</summary>
    public static bool CanGoToAlbum(in ActionTarget target)
        => target.Single is { } t && t.Album.IsValid && t.Album.Uri.IsValid
           && t.Album.Uri.Kind != EntityKind.Show;

    /// <summary>Go-to-podcast gate: the same single row, when the ref in the album slot IS a show. A name-only show
    /// has nowhere to go, so the row is ABSENT rather than dead.</summary>
    public static bool CanGoToPodcast(in ActionTarget target)
        => target.Single is { } t && t.Album.IsValid && t.Album.Uri.Kind == EntityKind.Show;

    /// <summary>Go-to-artist gate: a single track whose PRIMARY artist carries a uri. A name-only artist (a projected
    /// row, a search row without an artist link) is not navigable — offering the row anyway would navigate to an empty
    /// artist route, i.e. a dead page.</summary>
    public static bool CanGoToArtist(in ActionTarget target)
        => target.Single is { } t && t.ArtistSlots.Length > 0 && new Artist(t.ArtistSlots[0]).Uri.IsValid;

    /// <summary>Song-radio gate: exactly one track carrying a track uri. Radio seeds a SINGLE track — a multi-select
    /// or a non-track uri is disabled.</summary>
    public static bool CanStartTrackRadio(in ActionTarget target)
        => target.Single is { } t && t.Uri.IsValid && t.Uri.Kind == EntityKind.Track;

    /// <summary>Artist-radio gate: an Artist container target carrying an artist uri.</summary>
    public static bool CanStartArtistRadio(in ActionTarget target)
        => target.Kind == TargetKind.Artist && target.Uri.IsValid && target.Uri.Kind == EntityKind.Artist;

    /// <summary>Remove-from-this-playlist gate: an editable host with resolved rows.</summary>
    public static bool CanRemoveFromPlaylist(in PlaylistHost host)
        => host.IsSome && (host.Caps & PlaylistCaps.CanEditItems) != 0 && host.Rows.Count > 0;

    /// <summary>The route a container target navigates to. Every LIKED spelling folds through the one parser — a card
    /// built from a Home/recents section item can carry <c>spotify:user:&lt;u&gt;:collection</c>, which used to route
    /// to a playlist page instead of Liked Songs.</summary>
    public static Shell.Route RouteFor(in ActionTarget target)
    {
        if (!target.Uri.IsValid) return new Shell.Route(Shell.RouteKind.NotFound);
        return target.Kind switch
        {
            TargetKind.Album or TargetKind.Artist or TargetKind.Playlist or TargetKind.SidebarItem
                => Shell.For(target.Uri, target.Name),
            _ => new Shell.Route(Shell.RouteKind.NotFound),
        };
    }
}

/// <summary>ONE action definition — every instance is a static readonly singleton with a stable
/// <see cref="ActionId"/>. Everything dynamic (a count-aware label, enablement, checked state) is a lambda over
/// <see cref="ActionContext"/>: evaluated inside a component render it is reactive for free (signal reads subscribe),
/// evaluated in a menu open-thunk it is a ONE-SHOT SNAPSHOT (menus close on invoke — Explorer and Spotify never
/// re-enable an open menu either). No CanExecuteChanged, no registry, no reflection.
/// <para>The PROJECTIONS (menu row, command-bar button, swipe action) live in <c>Actions.UI.cs</c>: they are the only
/// places action → UI mapping exists, and they are UI.</para></summary>
public sealed class AppAction
{
    public required ActionId Id { get; init; }
    /// <summary>Count-aware display label (e.g. "Play 3 next").</summary>
    public required Func<ActionContext, string> Label { get; init; }
    /// <summary>Semantic icon key — resolved through <c>ActionIcons.Resolve</c>, never a raw glyph.</summary>
    public required string IconKey { get; init; }
    public string? AcceleratorText { get; init; }
    /// <summary>Null ⇒ always enabled.</summary>
    public Func<ActionContext, bool>? IsEnabled { get; init; }
    /// <summary>Non-null ⇒ a toggle row / toggle button (the saved heart).</summary>
    public Func<ActionContext, bool>? IsChecked { get; init; }
    /// <summary>Destructive verbs (remove/delete) — rendered as plain rows behind confirm flows; the flag is carried
    /// for a red-text variant.</summary>
    public bool Destructive { get; init; }
    public required Action<ActionContext> Execute { get; init; }

    public bool EnabledFor(in ActionContext ctx) => IsEnabled?.Invoke(ctx) ?? true;
    public bool CheckedFor(in ActionContext ctx) => IsChecked?.Invoke(ctx) ?? false;
    public bool IsToggle => IsChecked is not null;
}

/// <summary>The flat identity table (the shortcut map / palette forward path). Composition never scans this — menus
/// are plain code in <c>Actions.UI.cs</c>.
/// <para><b>NOT the extension-platform path.</b> Anything BOUND resolves through <see cref="Actions.Registry"/>,
/// whose first-party descriptors wrap these same verbs. The guardrail is that <b>no new UI code looks up
/// <see cref="All"/> directly</b>; existing call sites stay as they are, new ones go through the registry.</para>
/// <para>The VERBS themselves are the Wave-5 entity files' (ch 29 §9.10 moves them out of this platform file); this
/// list is filled by <see cref="Register"/> as each owner lands theirs, in declaration order.</para></summary>
public static class AppActions
{
    static readonly List<AppAction> s_all = [];

    public static IReadOnlyList<AppAction> All => s_all;

    /// <summary>Add a verb to the flat table. First registration per <see cref="ActionId"/> wins, so a stub cannot
    /// shadow the real verb and a double-registration is a no-op rather than a duplicate row.</summary>
    public static void Register(AppAction action)
    {
        for (int i = 0; i < s_all.Count; i++) if (s_all[i].Id == action.Id) return;
        s_all.Add(action);
    }

    public static AppAction? Find(ActionId id)
    {
        for (int i = 0; i < s_all.Count; i++) if (s_all[i].Id == id) return s_all[i];
        return null;
    }
}
