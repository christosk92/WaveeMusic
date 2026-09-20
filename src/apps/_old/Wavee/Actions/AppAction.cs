using System;
using FluentGpu.Controls;

namespace Wavee;

/// <summary>
/// ONE action definition class — every instance is a static readonly singleton (TrackActions / ContainerActions) with a
/// stable <see cref="ActionId"/> (the future shortcut-map / palette key). Everything dynamic — label (count-aware),
/// enablement, checked-state — is a lambda over <see cref="ActionContext"/>: evaluated inside a component render it is
/// reactive for free (signal reads subscribe), evaluated in a menu open-thunk it is a one-shot snapshot (menus close on
/// invoke — Explorer/Spotify never re-enable an open menu either). No CanExecuteChanged, no registry, no reflection
/// (AOT-safe statics). The TWO projections below are the ONLY places action → UI mapping exists (a swipe projection
/// arrives with the touch phase).
/// </summary>
public sealed class AppAction
{
    public required ActionId Id { get; init; }
    /// <summary>Count-aware display label (e.g. "Play 3 next").</summary>
    public required Func<ActionContext, string> Label { get; init; }
    /// <summary>Semantic icon key — resolved through <see cref="ActionIcons.Resolve"/> (never a raw glyph).</summary>
    public required string IconKey { get; init; }
    public string? AcceleratorText { get; init; }
    /// <summary>Null ⇒ always enabled.</summary>
    public Func<ActionContext, bool>? IsEnabled { get; init; }
    /// <summary>Non-null ⇒ a toggle row / toggle button (the saved heart).</summary>
    public Func<ActionContext, bool>? IsChecked { get; init; }
    /// <summary>Icon shown when a toggle is CHECKED in the labeled context-menu command strip — rendered accent-tinted
    /// on a transparent plate (no accent pill), matching the app's toggle language (e.g. the player-bar Like: a filled
    /// heart tinted accent). Null ⇒ the strip reuses the normal <see cref="IconKey"/> icon.</summary>
    public IconRef? CheckedIcon { get; init; }
    /// <summary>Destructive verbs (remove/delete) — v1 renders them as plain rows behind confirm flows; the flag is
    /// carried for a future red-text variant.</summary>
    public bool Destructive { get; init; }
    public required Action<ActionContext> Execute { get; init; }

    public bool EnabledFor(in ActionContext ctx) => IsEnabled?.Invoke(ctx) ?? true;

    /// <summary>Project into a menu row. The presenter owns close-on-invoke; <paramref name="after"/> runs after
    /// Execute (the batch bar's DeselectAll).</summary>
    public MenuFlyoutItem ToMenuItem(in ActionContext ctx, Action? after = null)
    {
        var c = ctx;
        var run = Execute;
        var post = after;
        Action invoke = () => { run(c); post?.Invoke(); };
        bool enabled = EnabledFor(c);
        if (IsChecked is { } isChecked)
        {
            bool on = isChecked(c);
            return MenuFlyoutItem.Toggle(Label(c), on, invoke, ActionIcons.Resolve(IconKey, on), enabled)
                with { AcceleratorText = AcceleratorText };
        }
        return new MenuFlyoutItem(Label(c), ActionIcons.Resolve(IconKey), enabled, invoke)
        { AcceleratorText = AcceleratorText };
    }

    /// <summary>Project into a command-bar button (the context menu's primary strip / the batch bar).</summary>
    public AppBarCommand ToBarCommand(in ActionContext ctx, Action? after = null)
    {
        var c = ctx;
        var run = Execute;
        var post = after;
        bool enabled = EnabledFor(c);
        bool isToggle = IsChecked is not null;
        bool on = isToggle && IsChecked!(c);
        return new AppBarCommand(
            ActionIcons.Resolve(IconKey, on), Label(c), () => { run(c); post?.Invoke(); },
            isToggle ? AppBarCommandKind.ToggleButton : AppBarCommandKind.Button, on, enabled)
        { AcceleratorText = AcceleratorText, CheckedIcon = CheckedIcon };
    }

    /// <summary>Project into a touch swipe action (the THIRD projection — <see cref="RowSwipe"/>): the semantic
    /// <see cref="IconRef"/> + count-aware label, invoking <see cref="Execute"/> on the swipe commit. A toggle carries
    /// its checked-state icon (the saved heart) exactly like the menu/bar projections. The custom background colour
    /// (destructive red etc.) is applied by <see cref="RowSwipe"/>, which owns the swipe presentation.</summary>
    public SwipeAction ToSwipeAction(in ActionContext ctx, Action? after = null)
    {
        var c = ctx;
        var run = Execute;
        var post = after;
        bool on = IsChecked?.Invoke(c) ?? false;
        return new SwipeAction(ActionIcons.Resolve(IconKey, on), Label(c))
        { OnInvoked = () => { run(c); post?.Invoke(); } };
    }
}

/// <summary>The flat identity table (shortcut map / command palette forward path). Composition never scans this —
/// menus are plain code in <see cref="Menus"/>.
///
/// <para>NOT the extension-platform path. Anything BOUND (a Curated action-shortcut row, the customizer's action picker,
/// a contributed action) resolves through <see cref="WaveeExtensionRegistry"/> instead, whose first-party descriptors wrap
/// these same verbs — REVISION 2's guardrail is that <b>no new UI code looks up <see cref="All"/> directly</b>. Existing
/// call sites stay as they are; new ones go through the registry.</para></summary>
public static class AppActions
{
    public static readonly AppAction[] All =
    [
        TrackActions.Play, TrackActions.PlayNext, TrackActions.AddToQueue, TrackActions.ToggleLike,
        TrackActions.CopyLink, TrackActions.GoToAlbum, TrackActions.GoToArtist,
        TrackActions.ViewCredits, TrackActions.CopySpotifyUri, TrackActions.OpenInSpotifyWeb,
        TrackActions.RemoveFromThisPlaylist, TrackActions.RemoveFromQueue,
        VideoActions.AttachVideo, VideoActions.ReplaceVideo, VideoActions.LocateVideo,
        VideoActions.RemoveVideo, VideoActions.ShowVideoInExplorer,
        ContainerActions.PlayContext, ContainerActions.PlayContextNext, ContainerActions.AddContextToQueue,
        ContainerActions.SaveContext, ContainerActions.OpenItem, ContainerActions.GoToAlbumArtist,
        ContainerActions.RenamePlaylist, ContainerActions.InviteCollaborators, ContainerActions.DeletePlaylist,
        PinActions.PinToSidebar, PinActions.UnpinFromSidebar,
    ];
}
