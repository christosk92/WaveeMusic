// ── Platform/Actions.UI.cs ─────────────────────────────────────────────────────────────────────────────────────────
// the menu vocabulary, ActionIcons, the action row, the picker, the reason caption. Landed before owner J's picker
//
// Role: UI
// Owner: I
// Wave: 4
// Budget: 1200 lines
// Spec: ch 29 §9.10 (Actions.Menus.cs 900 + Actions.UI.cs 300)
//
// WHAT THIS FILE IS — AND IS NOT. It is the VOCABULARY every context menu, bound row and picker in the app is spelled
// in: the one icon table, the three projections of an action (menu row / command-bar button / swipe), the grammar's
// shared shapes (the header tile, groups, Share ▸, the ONE deposit submenu, Organize ▸, Access ▸, the absolute pin
// pair, the layout-extras splice), the extension platform's bound row with its reason caption, and the two modal
// pickers (a destination picker and the action picker). It is NOT the per-kind compositions: the track menu is owner
// M's `Entities/Track.UI.cs`, the card and sidebar arms are the page/sidebar owners', and the VERBS are the entity
// files' `AppActions.Register` calls (ch 29 §9.10) — which is why a verb is looked up here by `ActionId` and a row for a
// verb nobody has registered yet is simply absent rather than dead.
//
// THE MENU GRAMMAR (0.2.9 `Menus.cs`, D48), restated because every composition built on this file must keep it:
//   order   transport (Play · Play next · Play after) → state (Save) → collection (Add to playlist · Move · Pin)
//           → navigation (Open · Go to album · Go to artist) → Share → surface extras → destructive LAST (behind a
//           separator);
//   shape   header tile → a labeled command strip of the transport verbs → short separator-delimited groups, where a
//           group of low-frequency related verbs collapses into ONE named submenu (Organize ▸, Access ▸).
// A core verb may be omitted only where its seam genuinely does not exist for that kind, and then with a comment.
//
// Every builder here runs at MENU-OPEN time (a human-rate thunk), never per frame, so it allocates freely.

using System;
using System.Collections.Generic;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

// ══ 1. THE ONE ICON TABLE ═════════════════════════════════════════════════════════════════════════════════════════

public static partial class ActionIcons
{
    /// <summary>Key → an <see cref="IconRef"/>. Keys the ThemedIcon starter set covers resolve to a layered vector name
    /// with a glyph fallback (<c>IconRef.Themed</c>); the rest are plain Segoe Fluent glyphs. <paramref name="isChecked"/>
    /// picks the filled variant of a stateful icon (the saved heart, a saved container). An unknown key is the "…"
    /// glyph — visible, never a blank column. Only this table changes as the themed harvest grows.</summary>
    public static IconRef Resolve(string key, bool isChecked = false) => key switch
    {
        Play => IconRef.Themed("Play", Icons.Play),
        // The two-tone layered marks mirror the app's CUSTOM rail glyphs, which stay the fallback — hence the explicit
        // Font, so the fallback resolves against the WaveeIcons face and not Segoe (the 0.2.9 tofu, round-2 defect 6a).
        PlayNext => new IconRef { ThemedName = "PlayNext", Glyph = WaveeIcons.PlayNext, Font = WaveeIcons.Font },
        Queue => new IconRef { ThemedName = "AddToQueue", Glyph = WaveeIcons.PlayAfter, Font = WaveeIcons.Font },
        Like or Heart => isChecked ? IconRef.Themed("HeartFill", Icons.HeartFill) : IconRef.Themed("Heart", Icons.Heart),
        // Saving a container is not liking a track: the add/check pair keeps the two registry rows visually distinct.
        Save => isChecked ? Icons.Check : IconRef.Themed("Add", Icons.Add),
        Add => IconRef.Themed("Add", Icons.Add),
        Album => Icons.Album,
        Artist => Icons.Contact,
        Link => IconRef.Themed("Link", Icons.Link),
        Remove => Icons.Remove,
        Delete => IconRef.Themed("Delete", Icons.Delete),
        Open => IconRef.Themed("Open", Icons.OpenInNewWindow),
        Rename => IconRef.Themed("Rename", Icons.Edit),
        People => Icons.Friends,
        Globe => Icons.Globe,
        Credits => Icons.Document,
        Share => Icons.Share,
        CopyUri => Icons.Copy,
        OpenWeb => Icons.Globe,
        Radio => Icons.RadioTower,
        // The SAME Movie mark the row indicator and the player-bar video button use.
        Video => Icons.Movie,
        Replace => Icons.Refresh,
        Locate => Icons.Search,
        RevealFolder => Icons.FolderOpen,
        Pin => Icons.Pin,
        Unpin => Icons.UnPin,
        Folder => IconRef.Themed("Folder", Icons.Folder),
        _ => Icons.More,
    };
}

// ══ 2. THE THREE PROJECTIONS ═══════════════════════════════════════════════════════════════════════════════════════

/// <summary>The ONLY places an action becomes UI. Everything dynamic on an <see cref="AppAction"/> is a lambda over the
/// context, evaluated HERE at open time — a one-shot snapshot, because a menu closes on invoke.</summary>
public static class ActionProjections
{
    /// <summary>A menu row. A toggle renders as a checkable row with its checked-state icon; the presenter owns
    /// close-on-invoke; <paramref name="after"/> runs after the verb (the selection bar's DeselectAll).</summary>
    public static MenuFlyoutItem ToMenuItem(this AppAction action, in ActionContext ctx, Action? after = null)
    {
        var c = ctx;
        var run = action.Execute;
        Action invoke = () => { run(c); after?.Invoke(); };
        bool enabled = action.EnabledFor(in c);
        if (action.IsChecked is { } isChecked)
        {
            bool on = isChecked(c);
            return MenuFlyoutItem.Toggle(action.Label(c), on, invoke, ActionIcons.Resolve(action.IconKey, on), enabled)
                with { AcceleratorText = action.AcceleratorText };
        }
        return new MenuFlyoutItem(action.Label(c), ActionIcons.Resolve(action.IconKey), enabled, invoke)
            { AcceleratorText = action.AcceleratorText };
    }

    /// <summary>A command-bar button (the context menu's labeled strip, the selection bar). A checked toggle carries
    /// its filled icon as <c>CheckedIcon</c> — accent-tinted on a transparent plate in the strip, no pill.</summary>
    public static AppBarCommand ToBarCommand(this AppAction action, in ActionContext ctx, Action? after = null)
    {
        var c = ctx;
        var run = action.Execute;
        bool isToggle = action.IsToggle;
        bool on = isToggle && action.CheckedFor(in c);
        return new AppBarCommand(
            ActionIcons.Resolve(action.IconKey, on), action.Label(c), () => { run(c); after?.Invoke(); },
            isToggle ? AppBarCommandKind.ToggleButton : AppBarCommandKind.Button, on, action.EnabledFor(in c))
        {
            AcceleratorText = action.AcceleratorText,
            CheckedIcon = isToggle ? ActionIcons.Resolve(action.IconKey, true) : null,
        };
    }

    /// <summary>A touch swipe action (the row-swipe belt owns the capsule colour).</summary>
    public static SwipeAction ToSwipeAction(this AppAction action, in ActionContext ctx, Action? after = null)
    {
        var c = ctx;
        var run = action.Execute;
        bool on = action.CheckedFor(in c);
        return new SwipeAction(ActionIcons.Resolve(action.IconKey, on), action.Label(c))
        {
            OnInvoked = () => { run(c); after?.Invoke(); },
        };
    }

    /// <summary>A descriptor's icon (never a raw glyph — the checked/unchecked variants stay paired).</summary>
    public static IconRef Icon(this ActionDescriptor descriptor, bool isChecked = false)
        => ActionIcons.Resolve(descriptor.IconKey, isChecked);

    /// <summary>A descriptor's display label. A missing key renders loudly as "[key]" by design.</summary>
    public static string Label(this ActionDescriptor descriptor) => Loc.Get(descriptor.LabelLocKey);

    /// <summary>A BOUND action as a menu row: visible-but-disabled when it cannot run (the reason lives in the bound
    /// row's caption, not in a menu), checked when it is a toggle that is on, and it invokes through the ONE
    /// <see cref="Actions.Execute"/> path, so the confirmation gate cannot be routed around.</summary>
    public static MenuFlyoutItem ToMenuItem(this ActionDescriptor descriptor, ActionServices services, in ActionBinding binding)
    {
        var d = descriptor;
        var s = services;
        var b = binding;
        bool enabled = Actions.Resolve(d, s, in b).Available;
        Action invoke = () => Actions.Execute(d, s, in b);
        if (d.IsChecked is not null)
        {
            bool on = Actions.Checked(d, s, in b);
            return MenuFlyoutItem.Toggle(d.Label(), on, invoke, d.Icon(on), enabled);
        }
        return new MenuFlyoutItem(d.Label(), d.Icon(), enabled, invoke);
    }
}

public static partial class Actions
{
    /// <summary>THE app's one reference-stable seam bag (<see cref="ActionServices"/>'s own contract: provided once,
    /// never swapped, its delegates refreshed in place). The entity files fill the verbs, owner J the pin pair, and the
    /// shell's overlay layer (<c>+Shell.Overlays.UI.cs</c>) the confirm pair, the clipboard, the external opener and the
    /// UI-thread post — because that layer is the one place inside the overlay host that holds all four.</summary>
    public static ActionServices Services { get; } = new();

    // ══ 3. THE MENU VOCABULARY ══════════════════════════════════════════════════════════════════════════════════════

    public static class Menu
    {
        public static MenuFlyoutItem Separator => MenuFlyoutItem.Separator;

        // ── the header tile ─────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>The identity strip above a context menu: a 38-DIP tile (r6, or a 19 circle for an artist), a title
        /// and a subtitle. <paramref name="leading"/> replaces the art when the caller has its own (the Liked Songs
        /// treatment); a null url and no leading element draw no tile at all. 0.3 subtitles are plain text by
        /// construction — there is no HTML flattening step because no producer builds markup any more.</summary>
        public static ContextMenuHeader Header(string? artUrl, string title, string? subtitle, bool circular = false,
                                               Element? leading = null)
        {
            Element? tile = leading ?? (artUrl is { Length: > 0 }
                ? Controls.Artwork(artUrl, 38f, 38f, circular ? 19f : 6f, decodePx: 76)
                : null);
            return new ContextMenuHeader(tile, title, subtitle);
        }

        /// <summary>The multi-selection header: "{n} songs selected" over "{first title}  +{n−1} more" (two spaces).</summary>
        public static ContextMenuHeader SelectionHeader(string? artUrl, int count, string firstTitle)
            => Header(artUrl, Strings.Menu.SongsSelected(count),
                count > 1 ? firstTitle + "  " + Strings.Menu.MoreCount(count - 1) : firstTitle);

        /// <summary>The kind word a card header falls back to when it has no subtitle of its own.</summary>
        public static string KindWord(TargetKind kind) => kind switch
        {
            TargetKind.Album => Loc.Get(Strings.Menu.KindAlbum),
            TargetKind.Artist => Loc.Get(Strings.Menu.KindArtist),
            TargetKind.Playlist => Loc.Get(Strings.Menu.KindPlaylist),
            _ => "",
        };

        // ── registered verbs ────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>A registered verb as a row, or null while its owning wave has not registered it — the row is then
        /// ABSENT rather than a promise that does nothing.</summary>
        public static MenuFlyoutItem? Row(ActionId id, in ActionContext ctx, Action? after = null)
            => AppActions.Find(id) is { } action ? action.ToMenuItem(in ctx, after) : null;

        /// <summary>A registered verb as a strip command, or null when unregistered.</summary>
        public static AppBarCommand? Command(ActionId id, in ActionContext ctx, Action? after = null)
            => AppActions.Find(id) is { } action ? action.ToBarCommand(in ctx, after) : null;

        /// <summary>Append the registered verbs, in the order given, skipping any not yet registered.</summary>
        public static void AddRows(List<MenuFlyoutItem> rows, in ActionContext ctx, ReadOnlySpan<ActionId> ids)
        {
            for (int i = 0; i < ids.Length; i++)
                if (AppActions.Find(ids[i]) is { } action) rows.Add(action.ToMenuItem(in ctx));
        }

        /// <summary>The labeled command strip (the Win11 Explorer body) over the given verbs, in order.</summary>
        public static AppBarCommand[] Strip(in ActionContext ctx, ReadOnlySpan<ActionId> ids)
        {
            var strip = new List<AppBarCommand>(ids.Length);
            for (int i = 0; i < ids.Length; i++)
                if (AppActions.Find(ids[i]) is { } action) strip.Add(action.ToBarCommand(in ctx));
            return strip.ToArray();
        }

        // ── grouping ────────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>Open a new group: a separator in front of <paramref name="lead"/> only when there are rows above and
        /// the last is not already one; nothing at all when the group is empty on this row.</summary>
        public static void Group(List<MenuFlyoutItem> rows, MenuFlyoutItem? lead)
        {
            if (lead is not { } row) return;
            OpenGroup(rows);
            rows.Add(row);
        }

        /// <summary>A separator iff the grammar needs one here.</summary>
        public static void OpenGroup(List<MenuFlyoutItem> rows)
        {
            if (MenuRules.NeedsSeparator(rows.Count, rows.Count > 0 && rows[^1].IsSeparator)) rows.Add(Separator);
        }

        /// <summary>A named submenu whose icon is a semantic key.</summary>
        public static MenuFlyoutItem SubMenu(string title, IReadOnlyList<MenuFlyoutItem> items, string iconKey,
                                             bool enabled = true)
            => MenuFlyoutItem.SubMenu(title, items, ActionIcons.Resolve(iconKey), enabled);

        // ── Share ▸ ─────────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>The app-wide Share submenu: Copy link(s) always; Copy Spotify URI and Open in Spotify Web only for ONE
        /// shareable Spotify entity (a multi-selection collapses to "Copy links (n)"). Null while the copy-link verb is
        /// unregistered.</summary>
        public static MenuFlyoutItem? Share(in ActionContext ctx)
        {
            if (Row(ActionId.CopyLink, in ctx) is not { } copy) return null;
            var items = new List<MenuFlyoutItem>(3) { copy };
            if (MenuRules.SingleShareableUri(ctx.Target).IsValid)
            {
                if (Row(ActionId.CopySpotifyUri, in ctx) is { } uri) items.Add(uri);
                if (Row(ActionId.OpenInSpotifyWeb, in ctx) is { } web) items.Add(web);
            }
            return SubMenu(Loc.Get(Strings.Menu.Share), items, ActionIcons.Share);
        }

        // ── the ONE playlist-deposit submenu ────────────────────────────────────────────────────────────────────────

        /// <summary>One playlist a deposit can land in.</summary>
        public readonly record struct DepositTarget(EntityUri Uri, string Name);

        /// <summary>THE deposit submenu behind Add to playlist (tracks), Add to playlist (container) and Move to
        /// playlist: "New playlist" → up to <see cref="MenuRules.MaxInlinePlaylists"/> playlists in the caller's order
        /// (most-recently-filed first, eligibility already filtered, the source already excluded) → separator →
        /// "More playlists…". Only what a pick DOES differs, so only that is a parameter. The submenu is DISABLED, not
        /// absent, when nothing can be added; "More playlists…" additionally needs somewhere to open the picker.</summary>
        public static MenuFlyoutItem Deposit(string title, IconRef icon, bool canAdd, IReadOnlyList<DepositTarget> ordered,
                                             Action<DepositTarget> deposit, Action createAndDeposit, Action? morePlaylists)
        {
            int inline = MenuRules.InlineCount(ordered.Count);
            var items = new List<MenuFlyoutItem>(inline + 3)
            {
                new(Loc.Get(Strings.Detail.NewPlaylist), ActionIcons.Resolve(ActionIcons.Add), canAdd, createAndDeposit),
            };
            for (int i = 0; i < inline; i++)
            {
                var target = ordered[i];   // fresh capture per row — each deposits into its OWN playlist
                items.Add(new MenuFlyoutItem(target.Name, default, canAdd, () => deposit(target)));
            }
            items.Add(Separator);
            items.Add(new MenuFlyoutItem(Loc.Get(Strings.Menu.MorePlaylists), default,
                canAdd && morePlaylists is not null, morePlaylists));
            return MenuFlyoutItem.SubMenu(title, items, icon, enabled: canAdd);
        }

        // ── Organize ▸ / Access ▸ / the pin pair ────────────────────────────────────────────────────────────────────

        /// <summary><b>Organize ▸</b> — every verb that changes where a sidebar row LIVES: the pane's positional moves,
        /// then "Move out of {parent}", then (behind a separator — the pinned list is a different list) the pin row.
        /// Null when none apply, so the row is absent rather than an empty cascade.</summary>
        public static MenuFlyoutItem? Organize(IReadOnlyList<MenuFlyoutItem>? moves, MenuFlyoutItem? moveOut, MenuFlyoutItem? pin)
        {
            var items = new List<MenuFlyoutItem>(6);
            if (moves is { Count: > 0 })
                for (int i = 0; i < moves.Count; i++) items.Add(moves[i]);
            if (moveOut is { } lift) items.Add(lift);
            if (pin is { } pinRow)
            {
                if (items.Count > 0) items.Add(Separator);
                items.Add(pinRow);
            }
            return items.Count == 0 ? null : SubMenu(Loc.Get(Strings.Menu.Organize), items, ActionIcons.Folder);
        }

        /// <summary>"Move out of {parent}". The caller passes an already-clipped parent name (the sidebar's
        /// <c>MenuLabel.Clip</c>), so a long folder name cannot widen the whole flyout.</summary>
        public static MenuFlyoutItem MoveOutOf(string clippedParentName, bool enabled, Action onClick)
            => new(Strings.Menu.MoveOutOf(clippedParentName), ActionIcons.Resolve(ActionIcons.Folder), enabled, onClick);

        /// <summary>Move up / Move down, each present only when its closure is.</summary>
        public static void AddMoveRows(List<MenuFlyoutItem> rows, Action? moveUp, Action? moveDown)
        {
            if (moveUp is not null)
                rows.Add(new MenuFlyoutItem(Loc.Get(Strings.Menu.MoveUp), Icons.ChevronUp, true, moveUp));
            if (moveDown is not null)
                rows.Add(new MenuFlyoutItem(Loc.Get(Strings.Menu.MoveDown), Icons.ChevronDown, true, moveDown));
        }

        /// <summary><b>Access ▸</b> — who may see and who may edit: Public · Private (an ABSOLUTE radio pair, each SETS
        /// what it names) · — · Collaborative (a toggle) · — · Invite collaborators. With the header unknown both radios
        /// render unchecked, because inventing a checked state would invert the user's intent.</summary>
        public static MenuFlyoutItem Access(bool known, bool isPublic, bool collaborative, Action<bool> setPublic,
                                            Action<bool> setCollaborative, MenuFlyoutItem invite)
        {
            var items = new List<MenuFlyoutItem>(6)
            {
                MenuFlyoutItem.RadioItem(Loc.Get(Strings.Menu.MakePublic), known && isPublic, () => setPublic(true)),
                MenuFlyoutItem.RadioItem(Loc.Get(Strings.Menu.MakePrivate), known && !isPublic, () => setPublic(false)),
                Separator,
                MenuFlyoutItem.Toggle(Loc.Get(Strings.Detail.Edit.Collaborative), collaborative,
                    () => setCollaborative(!collaborative)),
                Separator,
                invite,
            };
            return SubMenu(Loc.Get(Strings.Menu.Access), items, ActionIcons.Globe);
        }

        /// <summary>The pin pair — ABSOLUTE state, not a toggle: exactly one of "Pin to sidebar" / "Unpin from sidebar",
        /// as Spotify's own menu shows it. Whether a pin row appears at all is <c>PinRowRule</c>'s (owner J).</summary>
        public static MenuFlyoutItem Pin(bool pinned, Action pin, Action unpin)
            => pinned
                ? new MenuFlyoutItem(Loc.Get(Strings.Sidebar.Pin.Unpin), ActionIcons.Resolve(ActionIcons.Unpin), true, unpin)
                : new MenuFlyoutItem(Loc.Get(Strings.Sidebar.Pin.PinTo), ActionIcons.Resolve(ActionIcons.Pin), true, pin);

        // ── Go to artists ▸ ─────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>One artist a menu can navigate to (callers filter out name-only artists first — a uri-less artist
        /// has nowhere to go).</summary>
        public readonly record struct ArtistLink(string Name, Shell.Route Route);

        /// <summary>"Go to artist" for ONE named artist (a secondary the primary-artist verb cannot express).</summary>
        public static MenuFlyoutItem GoToArtist(in ArtistLink artist, Action<Shell.Route>? go)
        {
            var route = artist.Route;
            return new MenuFlyoutItem(Loc.Get(Strings.Detail.GoToArtist), ActionIcons.Resolve(ActionIcons.Artist),
                go is not null, () => go?.Invoke(route));
        }

        /// <summary>"Go to artists ▸" — one plain row per artist, in billing order.</summary>
        public static MenuFlyoutItem GoToArtists(IReadOnlyList<ArtistLink> artists, Action<Shell.Route>? go)
        {
            var items = new MenuFlyoutItem[artists.Count];
            for (int i = 0; i < artists.Count; i++)
            {
                var route = artists[i].Route;
                items[i] = new MenuFlyoutItem(artists[i].Name, default, go is not null, () => go?.Invoke(route));
            }
            return SubMenu(Loc.Get(Strings.Menu.GoToArtists), items, ActionIcons.Artist);
        }

        // ── layout extras ───────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>A surface's per-row LAYOUT verbs, split by where the grammar puts them: <see cref="Organize"/> are
        /// positional (they fold INTO an arm's Organize ▸), <see cref="Trailing"/> is the document-level Remove that stays
        /// at the bottom. An arm with no Organize submenu takes <see cref="Flat"/>.</summary>
        public readonly record struct Extras(IReadOnlyList<MenuFlyoutItem>? Organize = null,
                                             IReadOnlyList<MenuFlyoutItem>? Trailing = null)
        {
            public bool IsEmpty => (Organize is null || Organize.Count == 0) && (Trailing is null || Trailing.Count == 0);

            /// <summary>Both groups as one list, positional verbs first; the one non-empty group is returned unwrapped.</summary>
            public IReadOnlyList<MenuFlyoutItem>? Flat()
            {
                if (Trailing is not { Count: > 0 }) return Organize;
                if (Organize is not { Count: > 0 }) return Trailing;
                var all = new List<MenuFlyoutItem>(Organize.Count + Trailing.Count);
                for (int i = 0; i < Organize.Count; i++) all.Add(Organize[i]);
                for (int i = 0; i < Trailing.Count; i++) all.Add(Trailing[i]);
                return all;
            }
        }

        /// <summary>Splice extras after the entity verbs and IN FRONT of a trailing destructive block. A layout-only
        /// menu (no entity verbs) is just the extras; null or empty extras leave the menu unchanged — including a null
        /// menu, which still opens nothing.</summary>
        public static ContextMenuModel? WithLayoutExtras(ContextMenuModel? menu, IReadOnlyList<MenuFlyoutItem>? extras)
        {
            if (extras is not { Count: > 0 }) return menu;
            if (menu is not { } present)
            {
                var only = new List<MenuFlyoutItem>(extras.Count);
                for (int i = 0; i < extras.Count; i++) only.Add(extras[i]);
                return new ContextMenuModel(only);
            }
            var rows = new List<MenuFlyoutItem>(present.Rows.Count + extras.Count + 1);
            for (int i = 0; i < present.Rows.Count; i++) rows.Add(present.Rows[i]);
            int at = MenuRules.ExtrasInsertIndex(rows.Count, rows.Count >= 2 && rows[^2].IsSeparator);
            if (MenuRules.NeedsSeparator(at, at > 0 && rows[at - 1].IsSeparator)) rows.Insert(at++, Separator);
            for (int i = 0; i < extras.Count; i++) rows.Insert(at++, extras[i]);
            return present with { Rows = rows };
        }
    }

    // ══ 4. THE BOUND ROW, THE TARGET SUMMARY, THE REASON CAPTION (ch 29 §2 W12) ════════════════════════════════════

    /// <summary>The bound row's height (<c>SidebarItemPickers.cs:432</c>).</summary>
    public const float ActionRowHeight = 48f;

    /// <summary>What an action can be pointed at, as a caption — the one thing that distinguishes two otherwise
    /// identical rows ("Save" for tracks vs "Save" for entities, round-2 defect 6b/6c).</summary>
    public static string TargetSummary(ActionDescriptor descriptor)
    {
        var modes = PickRules.AcceptedModes(descriptor.AcceptedTargets);
        if (modes.Length == 0) return "";
        if (modes.Length == 1) return Loc.Get(PickRules.ModeLocKey(modes[0]));
        var sb = new System.Text.StringBuilder(48);
        for (int i = 0; i < modes.Length; i++)
        {
            if (i > 0) sb.Append(" · ");
            sb.Append(Loc.Get(PickRules.ModeLocKey(modes[i])));
        }
        return sb.ToString();
    }

    /// <summary>ONE bound action's row in its four states: enabled · selected (the app's selection ramp, a 3-DIP accent
    /// bar, the action's OWN icon kept and a check mark ADDED — never a radio bullet, round-2 defect 6c) · checked (the
    /// filled icon) · and disabled, which is the caller's <see cref="ReasonCaption"/> under it, never a hidden row.
    /// The selection ramp is set EXPLICITLY: <c>.Interactive(…)</c> would overwrite all three fills and erase it.</summary>
    public static Element ActionRow(ActionDescriptor descriptor, bool selected, Action onClick, bool isChecked = false)
    {
        string sub = TargetSummary(descriptor);
        var icon = descriptor.Icon(isChecked);
        var lines = new List<Element>(2)
        {
            new TextEl(descriptor.Label())
            {
                Size = 13f, LineHeight = 18f, Weight = 600, Color = Tok.TextPrimary, MaxLines = 1,
                Trim = TextTrim.CharacterEllipsis,
            },
        };
        if (sub.Length > 0)
            lines.Add(new TextEl(sub)
            {
                Size = 11f, LineHeight = 14f, Color = Tok.TextTertiary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
            });

        return new BoxEl
        {
            Direction = 0, Height = ActionRowHeight, Shrink = 0f, Gap = Spacing.S, AlignItems = FlexAlign.Center,
            Padding = new Edges4(2f, 0f, Spacing.S, 0f), Corners = Radii.ControlAll,
            Fill = selected ? Design.Colors.SelectedRest : ColorF.Transparent,
            HoverFill = selected ? Design.Colors.SelectedHover : Tok.FillSubtleSecondary,
            PressedFill = selected ? Design.Colors.SelectedPressed : Tok.FillSubtleTertiary,
            BrushTransitionMs = Design.Motion.Faster,
            Cursor = CursorId.Hand, Focusable = true, Role = AutomationRole.RadioButton,
            FocusVisualMargin = Design.FocusInsetRow,
            OnClick = onClick,
            Children =
            [
                new BoxEl
                {
                    Width = 3f, Height = 20f, Shrink = 0f, Corners = CornerRadius4.All(1.5f),
                    Fill = Tok.AccentDefault, Opacity = selected ? 1f : 0f, HitTestVisible = false,
                },
                new BoxEl
                {
                    Width = 28f, Height = 28f, Shrink = 0f, Corners = Radii.ControlAll,
                    Fill = selected ? Tok.AccentSubtle : Tok.FillSubtleSecondary,
                    AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, HitTestVisible = false,
                    // The ref's own FONT travels with the glyph: two first-party marks live in the WaveeIcons face.
                    Children =
                    [
                        Icon(icon.Glyph ?? Icons.More, 14f,
                            selected ? Tok.AccentTextPrimary
                                     : descriptor.Destructive ? Tok.SystemFillCritical : Tok.TextSecondary,
                            icon.Font),
                    ],
                },
                new BoxEl
                {
                    Direction = 1, Grow = 1f, Basis = 0f, Shrink = 1f, MinWidth = 0f, Gap = 1f,
                    Justify = FlexJustify.Center, HitTestVisible = false, Children = [.. lines],
                },
                selected
                    ? Icon(Icons.Accept, 14f, Tok.AccentTextPrimary)
                    : new BoxEl { Width = 14f, Shrink = 0f },
            ],
        };
    }

    /// <summary>Why a binding would be inert: an unavailable target is still a LEGAL binding, so this EXPLAINS rather
    /// than blocks — a 12-DIP warning glyph and an 11-DIP tertiary line, wrapped to two. A zero-height box when the
    /// resolution is available, so the layout never jumps between the two.</summary>
    public static Element ReasonCaption(in ActionResolution resolution)
    {
        if (resolution.Available || resolution.ReasonLocKey is not { } key) return new BoxEl { Height = 0f };
        return new BoxEl
        {
            Direction = 0, Gap = Spacing.S, Shrink = 0f, AlignItems = FlexAlign.Center,
            Padding = new Edges4(Spacing.XS, 0f, Spacing.XS, 0f),
            Children =
            [
                Icon(Icons.StatusWarning, 12f, Tok.TextTertiary),
                new TextEl(Loc.Get(key))
                {
                    Size = 11f, LineHeight = 14f, Color = Tok.TextTertiary, Grow = 1f, Shrink = 1f, MinWidth = 0f,
                    MaxLines = 2, Wrap = TextWrap.Wrap,
                },
            ],
        };
    }

    // ══ 5. THE DESTINATION PICKER (a row-commit ContentDialog) ═════════════════════════════════════════════════════
    //
    // "More playlists…" and "Move to folder…" are the same dialog: a context-menu verb whose menu is GONE by invoke time,
    // so there is no anchor to place a flyout against. The dialog carries only Cancel (the default button, so Enter
    // cancels) and a ROW is the only affirmative action — a click commits and closes in one gesture.

    /// <summary>One row of a destination picker.</summary>
    /// <param name="Key">Stable identity (the row's Key and what the caller receives back).</param>
    /// <param name="Label">The row's text — also what the live filter matches.</param>
    /// <param name="Subtitle">A second line (12 TextSecondary), e.g. "Collaborator".</param>
    /// <param name="Glyph">An 18-DIP leading glyph (Top level, a folder), or null.</param>
    /// <param name="ArtUrl">A 40-DIP r6 cover (a playlist row, 44 tall), or null.</param>
    /// <param name="Depth">Tree indent: 12 DIP per level (the sidebar tree's own step).</param>
    /// <param name="Pinned">Never filtered away (Top level, New playlist).</param>
    /// <param name="Plated">Draw the glyph on a 40-DIP subtle plate (the "New playlist" row).</param>
    public readonly record struct PickerItem(string Key, string Label, string? Subtitle = null, string? Glyph = null,
                                             string? ArtUrl = null, int Depth = 0, bool Pinned = false, bool Plated = false);

    /// <summary>A destination picker, fully described at OPEN time (every field is an open-time constant, so freezing it
    /// at mount is the contract, not the trap).</summary>
    public sealed record PickerSpec(string Title, IReadOnlyList<PickerItem> Items, Action<PickerItem> Pick)
    {
        /// <summary>The one line of guidance naming WHAT is moving (13 TextSecondary, ≤ 2 lines).</summary>
        public string? Body { get; init; }
        public string Placeholder { get; init; } = "";
        /// <summary>The filtered-empty line (44 DIP, 13 TextSecondary).</summary>
        public string EmptyText { get; init; } = "";
        /// <summary>Null ⇒ the engine's own 320 floor — what both 0.2.9 pickers painted (ch 25 §9 trap: do not "fix" it
        /// into a wider rung).</summary>
        public float? DialogWidth { get; init; }
        /// <summary>The panel's inner padding (the playlist picker carried 8, the folder picker 0).</summary>
        public float PanelPadding { get; init; }
    }

    const float PickerPanelW = 320f, PickerFieldW = 300f, PickerListMaxH = 360f, PickerIndentStep = 12f;

    /// <summary>Open a destination picker. Returns null (and opens nothing) with no overlay host or no rows — an empty
    /// picker is a worse answer than a verb the menu did not offer.</summary>
    public static OverlayHandle? OpenPicker(IOverlayService? overlay, PickerSpec spec)
    {
        if (overlay is null || spec.Items.Count == 0) return null;
        OverlayHandle? handle = null;
        var pick = spec.Pick;
        handle = ContentDialog.Show(overlay, d =>
        {
            d.Title = spec.Title;
            d.PrimaryText = "";                       // "" HIDES the primary; null would show a stray localized "OK"
            d.CloseText = Loc.Get(Strings.Auth.Cancel);
            d.DefaultButton = ContentDialog.DefaultBtn.Close;
            d.DialogWidth = spec.DialogWidth;
            d.Content = Embed.Comp(() => new DestinationPicker(spec, item => { pick(item); handle?.Close(); }));
        });
        return handle;
    }

    /// <summary>The picker's panel: guidance, a live filter over the FROZEN row list, the rows.</summary>
    sealed class DestinationPicker(PickerSpec spec, Action<PickerItem> commit) : Component
    {
        public override Element Render()
        {
            var query = UseSignal("");
            string q = query.Value;

            var rows = new List<Element>(spec.Items.Count);
            for (int i = 0; i < spec.Items.Count; i++)
            {
                var item = spec.Items[i];
                if (!PickRules.Matches(q, item.Label, item.Pinned)) continue;
                rows.Add(PickerRow(item, () => commit(item)));
            }

            Element list = rows.Count > 0
                ? new ScrollEl
                {
                    // No colour edge cue: the list sits on ContentDialog's translucent FillLayerAlt overlay, where the
                    // surface-colour cue samples the plate behind it and paints a darker band instead.
                    ContentSized = true, MaxHeight = PickerListMaxH, EdgeCues = ScrollEdgeCues.None,
                    Content = new BoxEl { Direction = 1, Gap = 2f, Children = rows.ToArray() },
                }
                : new BoxEl
                {
                    Height = 44f, AlignItems = FlexAlign.Center, Padding = new Edges4(8f, 0f, 8f, 0f),
                    Children = [new TextEl(spec.EmptyText) { Size = 13f, LineHeight = 18f, Color = Tok.TextSecondary }],
                };

            var kids = new List<Element>(3);
            if (spec.Body is { Length: > 0 } body)
                kids.Add(new TextEl(body)
                {
                    Size = 13f, LineHeight = 18f, Color = Tok.TextSecondary, MaxLines = 2, Trim = TextTrim.CharacterEllipsis,
                });
            kids.Add(Embed.Comp(() => new EditableText
            {
                Placeholder = spec.Placeholder, Width = PickerFieldW, Height = 32f, Text = query,
            }));
            kids.Add(list);

            return new BoxEl
            {
                Direction = 1, Width = PickerPanelW, Gap = Spacing.XS, Padding = Edges4.All(spec.PanelPadding),
                Children = kids.ToArray(),
            };
        }

        static Element PickerRow(in PickerItem item, Action onClick)
        {
            bool art = item.ArtUrl is { Length: > 0 } || item.Plated;
            Element lead = item.Plated
                ? new BoxEl
                {
                    Width = 40f, Height = 40f, Shrink = 0f, Corners = CornerRadius4.All(6f), Fill = Tok.FillSubtleSecondary,
                    AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                    Children = [Icon(item.Glyph ?? Icons.Add, 20f, Tok.TextSecondary)],
                }
                : item.ArtUrl is { Length: > 0 } url
                    ? Controls.Artwork(url, 40f, 40f, 6f, decodePx: 80)
                    : Icon(item.Glyph ?? Icons.Folder, 18f, Tok.TextSecondary);

            Element text = item.Subtitle is { Length: > 0 } sub
                ? new BoxEl
                {
                    Direction = 1, Grow = 1f, Shrink = 1f, MinWidth = 0f, Gap = 1f,
                    Children =
                    [
                        new TextEl(item.Label) { Size = 14f, LineHeight = 20f, Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                        new TextEl(sub) { Size = 12f, LineHeight = 16f, Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                    ],
                }
                : new TextEl(item.Label)
                {
                    Size = 14f, LineHeight = 20f, Color = Tok.TextPrimary, Grow = 1f, MaxLines = 1,
                    Trim = TextTrim.CharacterEllipsis,
                };

            return new BoxEl
            {
                Key = item.Key,
                Direction = 0, Height = art ? 44f : 40f, AlignItems = FlexAlign.Center, Gap = 10f,
                Padding = new Edges4(6f + item.Depth * PickerIndentStep, 0f, 8f, 0f),
                Corners = CornerRadius4.All(4f),
                Role = AutomationRole.Button, Focusable = true, FocusVisualMargin = Design.FocusInsetRow,
                OnClick = onClick,
                Children = [lead, text],
            }.Interactive(Interaction.Subtle);
        }
    }

    // ══ 6. THE ACTION PICKER (the customizer's "Action shortcut" dialog) ═══════════════════════════════════════════
    //
    // A modal ContentDialog at the 480 rung. It owns NO built-in buttons: the accent "Add" must disable until an action
    // (and, when its mode needs one, a target) is chosen, and ContentDialog.IsPrimaryButtonEnabled is read once at build
    // time — so the body and the Footer share one model and the footer owns every button (the phased-dialog precedent).

    /// <summary>What the action picker is opened with.</summary>
    /// <param name="Registry">The live contribution registry — rows come from it in REGISTRATION order (first-party
    /// first), never from <c>AppActions.All</c>.</param>
    /// <param name="Services">The seam bag the reason caption resolves against.</param>
    /// <param name="Commit">The committed binding.</param>
    public sealed record ActionPickerSpec(Registry? Registry, ActionServices? Services, Action<ActionBinding> Commit)
    {
        /// <summary>The binding being re-bound, pre-selected.</summary>
        public ActionBinding? Existing { get; init; }
        /// <summary>Opens the entity chooser for a FixedEntity binding and hands back (key, label). Null disables the
        /// chooser's button (there is nothing to choose with).</summary>
        public Action<Action<string, string>>? PickEntity { get; init; }
    }

    const float ActionPickerDialogW = 480f;                          // ladder rung 3 (a list)
    const float ActionPickerBodyW = ActionPickerDialogW - 48f;       // 432 = the rung less ContentDialog's 24 padding × 2
    const float ActionPickerListH = 260f;

    /// <summary>Open the action picker. Null with no overlay host.</summary>
    public static OverlayHandle? OpenActionPicker(IOverlayService? overlay, ActionPickerSpec spec)
    {
        if (overlay is null) return null;
        var model = new ActionPickerModel(spec);
        OverlayHandle? handle = null;
        handle = ContentDialog.Show(overlay, d =>
        {
            d.Title = Loc.Get(Strings.Sidebar.Customizer.ItemAction);
            d.DialogWidth = ActionPickerDialogW;
            d.PrimaryText = "";
            d.SecondaryText = "";
            d.CloseText = "";
            d.Content = Embed.Comp(() => new ActionPickerBody(model));
            d.Footer = Embed.Comp(() => new ActionPickerFooter(model));
        });
        model.Close = () => handle?.Close();
        return handle;
    }

    /// <summary>The picker's state, shared by its body and its footer. A plain holder: no hooks, one per open.</summary>
    sealed class ActionPickerModel
    {
        public readonly ActionPickerSpec Spec;
        public readonly Signal<string?> Key;
        public readonly Signal<ActionTargetMode> Mode;
        public readonly Signal<string?> TargetKey;
        public readonly Signal<string?> TargetLabel = new(null);
        /// <summary>The mode dropdown's controlled index — owned here, because the mode row only exists once an action
        /// is chosen and a hook may never live in a conditional branch.</summary>
        public readonly Signal<int> ModeIndex = new(0);
        public Action? Close;

        public ActionPickerModel(ActionPickerSpec spec)
        {
            Spec = spec;
            Key = new Signal<string?>(spec.Existing is { } b ? KeyOf(in b) : null);
            Mode = new Signal<ActionTargetMode>(spec.Existing?.TargetMode ?? ActionTargetMode.None);
            TargetKey = new Signal<string?>(spec.Existing?.TargetKey);
        }

        public ActionDescriptor? Descriptor(string? key)
            => key is not null && Spec.Registry is { } reg && reg.TryGetAction(key, out var d) ? d : null;

        public bool Ready() => PickRules.Ready(Descriptor(Key.Value) is not null, Mode.Value, TargetKey.Value);

        public void Choose(string actionKey)
        {
            Key.Value = actionKey;
            Mode.Value = Descriptor(actionKey) is { } d ? PickRules.FirstMode(d.AcceptedTargets) : ActionTargetMode.None;
            TargetKey.Value = null;
            TargetLabel.Value = null;
        }

        public void Commit()
        {
            if (Descriptor(Key.Peek()) is not { } d || !PickRules.Ready(true, Mode.Peek(), TargetKey.Peek())) return;
            Spec.Commit(PickRules.Bind(d.Key, Mode.Peek(), TargetKey.Peek()));
            Close?.Invoke();
        }
    }

    sealed class ActionPickerBody(ActionPickerModel m) : Component
    {
        public override Element Render()
        {
            string? key = m.Key.Value;
            var descriptor = m.Descriptor(key);
            // HOOKS FIRST, unconditionally (the mode row below is conditional).
            var modes = descriptor is null ? Array.Empty<ActionTargetMode>() : PickRules.AcceptedModes(descriptor.AcceptedTargets);
            int active = PickRules.IndexOfMode(modes, m.Mode.Value);
            UseLayoutEffect(() => m.ModeIndex.SetIfChanged(active), DepKey.From(active));

            var rows = new List<Element>(16);
            var actions = m.Spec.Registry?.Actions;
            if (actions is null || actions.Count == 0)
                rows.Add(Note(Loc.Get(Strings.Sidebar.Extension.Manage)));   // 0.2.9's empty-registry line
            else
                for (int i = 0; i < actions.Count; i++)
                {
                    var a = actions[i];
                    string akey = a.Key;
                    rows.Add(ActionRow(a, string.Equals(akey, key, StringComparison.Ordinal), () => m.Choose(akey)));
                }

            var tail = new List<Element>(4);
            if (descriptor is not null)
            {
                tail.Add(new BoxEl { Height = 1f, Fill = Tok.StrokeDividerDefault, Margin = new Edges4(0f, 4f, 0f, 4f) });
                if (modes.Length > 1) tail.Add(ModeRow(modes));
                if (PickRules.NeedsTarget(m.Mode.Value)) tail.Add(TargetRow(m.Mode.Value));
                tail.Add(Reason(descriptor));
            }

            return new BoxEl
            {
                Direction = 1, Width = ActionPickerBodyW, Gap = Spacing.S, MinHeight = 0f,
                Children =
                [
                    ScrollView(new BoxEl { Direction = 1, Gap = 2f, Children = [.. rows] }) with
                    {
                        Height = ActionPickerListH, Shrink = 0f, AutoEdgeFade = true, ScrollKey = "actions.picker",
                    },
                    new BoxEl { Direction = 1, Gap = Spacing.XS, Shrink = 0f, Children = [.. tail] },
                ],
            };
        }

        Element ModeRow(ActionTargetMode[] modes)
        {
            var labels = new string[modes.Length];
            for (int i = 0; i < modes.Length; i++) labels[i] = Loc.Get(PickRules.ModeLocKey(modes[i]));
            // A dropdown, never a SelectorBar: the mode labels are sentences ("Nothing (a global action)").
            return LabeledRow(Loc.Get(Strings.Sidebar.Customizer.TargetLabel),
                ComboBox.Create(labels, m.ModeIndex, width: 220f, onChange: i =>
                {
                    if ((uint)i < (uint)modes.Length) m.Mode.Value = modes[i];
                    m.TargetKey.Value = null;
                    m.TargetLabel.Value = null;
                }));
        }

        Element TargetRow(ActionTargetMode mode)
        {
            string? label = m.TargetLabel.Value ?? m.TargetKey.Value;
            if (mode == ActionTargetMode.FixedTrack)
            {
                // No track search exists in the binding UI — the honest offer is the track playing right now, captured
                // as a fixed uri (0.2.9's same scope note).
                var playing = new EntityUri(Playback.CurrentId.Value);
                string? nowPlaying = playing.IsValid ? playing.Text : null;
                return LabeledRow(Loc.Get(Strings.Sidebar.Customizer.TargetTrack), label ?? nowPlaying,
                    Button.Standard(Loc.Get(Strings.Sidebar.Customizer.ItemAdd), () =>
                    {
                        if (nowPlaying is not { Length: > 0 } uri) return;
                        m.TargetKey.Value = uri;
                        m.TargetLabel.Value = uri;
                    }, isEnabled: nowPlaying is { Length: > 0 }));
            }
            var pickEntity = m.Spec.PickEntity;
            return LabeledRow(Loc.Get(Strings.Sidebar.Customizer.TargetEntity), label,
                Button.Standard(Loc.Get(Strings.Sidebar.Customizer.ItemAdd), () => pickEntity?.Invoke((k, l) =>
                {
                    m.TargetKey.Value = k;
                    m.TargetLabel.Value = l.Length > 0 ? l : k;
                }), isEnabled: pickEntity is not null));
        }

        Element Reason(ActionDescriptor descriptor)
        {
            if (!m.Ready() || m.Spec.Services is not { } services || m.Spec.Registry is not { } registry)
                return new BoxEl { Height = 0f };
            var binding = PickRules.Bind(descriptor.Key, m.Mode.Value, m.TargetKey.Value);
            return ReasonCaption(registry.Resolve(services, in binding));
        }

        static Element LabeledRow(string label, Element control) => new BoxEl
        {
            Direction = 0, Gap = Spacing.M, AlignItems = FlexAlign.Center, MinHeight = 40f,
            Children =
            [
                new TextEl(label) { Size = 13f, LineHeight = 18f, Color = Tok.TextPrimary, Grow = 1f, MinWidth = 0f },
                control,
            ],
        };

        static Element LabeledRow(string label, string? value, Element control) => new BoxEl
        {
            Direction = 0, Gap = Spacing.M, AlignItems = FlexAlign.Center, MinHeight = 40f,
            Children =
            [
                new BoxEl
                {
                    Direction = 1, Grow = 1f, Shrink = 1f, MinWidth = 0f, Gap = 1f,
                    Children =
                    [
                        new TextEl(label) { Size = 13f, LineHeight = 18f, Color = Tok.TextPrimary },
                        new TextEl(value is { Length: > 0 } v ? v : "—")
                        {
                            Size = 11f, LineHeight = 14f, Color = Tok.TextTertiary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                        },
                    ],
                },
                control,
            ],
        };

        static Element Note(string text) => new TextEl(text)
        {
            Size = 12f, LineHeight = 16f, Color = Tok.TextTertiary, Wrap = TextWrap.Wrap, MaxLines = 3,
            Margin = new Edges4(Spacing.S, Spacing.S, Spacing.S, 0f),
        };
    }

    /// <summary>The footer: Cancel then the accent commit verb, disabled until the pick is committable. Cancel is
    /// STANDARD — what ContentDialog gives a non-default command — so the dialog keeps one button treatment.</summary>
    sealed class ActionPickerFooter(ActionPickerModel m) : Component
    {
        const float BtnMinW = 96f, BtnH = 32f;

        public override Element Render()
        {
            _ = m.Key.Value;
            _ = m.Mode.Value;
            _ = m.TargetKey.Value;
            bool ready = m.Ready();
            return new BoxEl
            {
                Direction = 0, Grow = 1f, Gap = Spacing.S, Justify = FlexJustify.End, AlignItems = FlexAlign.Center,
                Children =
                [
                    Button.Standard(Loc.Get(Strings.Auth.Cancel), () => m.Close?.Invoke()) with
                    {
                        MinWidth = BtnMinW, Height = BtnH, MinHeight = BtnH, Justify = FlexJustify.Center,
                    },
                    Button.Accent(Loc.Get(Strings.Sidebar.Customizer.ItemAdd), m.Commit, isEnabled: ready) with
                    {
                        MinWidth = BtnMinW, Height = BtnH, MinHeight = BtnH, Justify = FlexJustify.Center, TabIndex = 1,
                    },
                ],
            };
        }
    }

    // ══ 13. THE NOW-PLAYING MENU SEAM (G-195) ══════════════════════════════════════════════════════════════════════════
    //
    // The stage's identity strip and the player bar's title cluster share ONE right-click menu — both call
    // `Stage.NowPlayingMenu` (`Shell/Stage.UI.cs`, `Shell/Shell.PlayerBar.UI.cs:322`), asked at OPEN time. This file
    // owns the SEAM, not a composition: what it installs is the REAL track grammar (`Entities/Track.Menu.cs`), so the
    // bar's menu IS the row's menu — the transport strip, Add to playlist ▸ / Move ▸, the navigation rows, Share ▸,
    // View credits, Go to song radio and Video ▸. The thin eight-row duplicate that stood here until owner M's
    // grammar landed (Wave 4.5) is gone with it: every row it had, `Track.Menu` has, and it was missing the one verb
    // a now-playing right-click most obviously wants — Video ▸, the attach/replace/detach of a local file for the
    // track that is playing RIGHT NOW (ch 01 GAP 7, G-220).
    //
    // TWO OPTIONS, and the second is the only thing a static seam cannot resolve for itself:
    //   · `ShowGoToAlbum: true` — the bar belongs to no page, so both container rows belong (the album page is the
    //     one that passes false, ch 01 §6.4 row 3b);
    //   · `PickerOverlay` — the picker's overlay host comes from `UseContext(Overlay.Service)` inside a COMPONENT, and
    //     the seam is a `Func<ContextMenuModel?>` with no such context (the call sites hold an overlay but hand it to
    //     `WithContextMenu`, not to the factory). It uses the ambient overlay the last mounted page published —
    //     `Track.DrawerOverlay`, the same fallback `Playlist.Page`'s `ActionOverlay` takes — and with none the
    //     "More playlists…" row is simply disabled while the deposit submenu itself still works (`Track.Menu.cs`).

    /// <summary>Install the shell seam this file owns: the now-playing right-click, composed by the track grammar over
    /// the current playable. Null while nothing is playing or the playable is not a track (an episode's menu is the
    /// show grammar's). Called once by the composition root (<see cref="Shell.InstallUi"/>).</summary>
    public static void InstallUi() => Stage.NowPlayingMenu = static () =>
    {
        var r = Playback.Current.Peek();
        if (r.IsNone || r.Kind != EntityKind.Track) return null;
        var track = new Track(r.Slot);
        return track.IsValid
            ? Track.Menu([track], new Track.MenuOptions(ShowGoToAlbum: true, PickerOverlay: Track.DrawerOverlay))
            : null;
    };
}
