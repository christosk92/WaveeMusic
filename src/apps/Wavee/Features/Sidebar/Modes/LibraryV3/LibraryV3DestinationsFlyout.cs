using System;
using System.Collections.Generic;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Core.Sidebar;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

/// <summary>
/// The "Your Library" flyout — the library's fixed destinations (Liked Songs · Albums · Artists · Podcasts), one row
/// each: the row opens its page, the trailing pin toggle puts it in (or takes it out of) the pinned band. This is
/// where V3.1 put the destination word rail: one click behind the title instead of 30 DIP of permanent chrome that
/// repeated the chips' nouns, and — unlike the rail — every destination is pinnable in place, which is how a user
/// promotes the one or two they actually open to a permanent row.
///
/// <para>Mounted fresh on every open by <c>LibraryV3Header.ToggleDestinations</c> inside an <c>Overlay.Service</c> popup
/// (light-dismiss, focus-trapped), so the rows read live state at render: the pin glyph follows
/// <c>SidebarPreferences.PinsVersion</c> and the counts follow <c>LibraryStore.Stats</c>. The destination list is the ONE
/// shared owner, <see cref="SidebarShortcutsSection.LibraryDestinations"/>; the labels and glyphs come from
/// <c>ShellNav.Dest</c>, so the flyout, Classic's "Your Library" section and the rail tiles cannot disagree.</para>
/// </summary>
sealed class LibraryV3DestinationsFlyout : Component
{
    /// <summary>Wide enough for "Liked Songs" + a five-digit count + the toggle at the row's 14-DIP type; narrower than
    /// the rail folder flyout (300) because these rows carry no cover.</summary>
    const float PanelW = 248f;
    const float RowH = 36f;

    readonly LibraryV3Session _session;
    readonly Action _close;

    public LibraryV3DestinationsFlyout(LibraryV3Session session, Action close)
    {
        _session = session;
        _close = close;
    }

    public override Element Render()
    {
        var prefs = UseContext(SidebarPreferences.Slot);
        var store = UseContext(LibraryStore.Slot);
        if (prefs is not null) _ = prefs.PinsVersion.Value;   // subscribe: a pin toggle repaints the row's glyph in place
        // The counts subscribe through Count()'s own reads of Stats.State / Stats.Value, so they fill in when the stats land.

        var keys = SidebarShortcutsSection.LibraryDestinations;
        var rows = new List<Element>(keys.Length);
        for (int i = 0; i < keys.Length; i++) rows.Add(Row(keys[i], prefs, store));

        return new BoxEl
        {
            Direction = 1, Width = PanelW, MinWidth = 0f, Gap = 1f,
            Padding = Edges4.All(Spacing.XS),
            Children = [.. rows],
        };
    }

    Element Row(string key, SidebarPreferences? prefs, LibraryStore? store)
    {
        var dest = ShellNav.Dest(key);
        string? pinId = SidebarPinId.FromRoute(key);
        bool pinned = pinId is not null && prefs is not null && prefs.IsPinned(pinId);

        var kids = new List<Element>(4)
        {
            Icon(dest.Glyph, 16f, Tok.TextSecondary) with { Shrink = 0f },
            new TextEl(dest.Title)
            {
                Size = 14f, Color = Tok.TextPrimary, Grow = 1f, Shrink = 1f, MinWidth = 0f,
                MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
            },
        };
        if (Count(store, key) is { } n) kids.Add(SidebarCounts.Number(n));
        if (pinId is not null && prefs is not null)
        {
            string name = dest.Title;
            string id = pinId;
            var p = prefs;
            // A real button (focus ring, Space/Enter, AutomationRole) INSIDE the row button: the inner target wins the
            // click, exactly as the folder "+" does inside a folder row. Both verbs go through PinActions — the ONE pin
            // mutation path, with its undo toast — never a bare store write.
            kids.Add(ToolTip.Wrap(
                IconButton.Create(pinned ? Icons.UnPin : Icons.Pin,
                    pinned ? () => PinActions.Unpin(p, id, name)
                           : () => PinActions.Pin(p, id, SidebarEntryKind.AppRoute, SidebarPinId.UriOf(id), name),
                    size: ControlSize.Small) with { Key = "dest-pin:" + key },
                Loc.Get(pinned ? Strings.Sidebar.Pin.Unpin : Strings.Sidebar.Pin.PinTo)));
        }

        return new BoxEl
        {
            Key = "dest:" + key,
            Direction = 0, Height = RowH, Gap = Spacing.M, AlignItems = FlexAlign.Center, MinWidth = 0f,
            Padding = new Edges4(Spacing.M, 0f, Spacing.XS, 0f), Corners = Radii.ControlAll,
            Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand,
            OnClick = () => { _close(); _session.Go(key, null); },
            Children = [.. kids],
        }.Interactive(Interaction.Subtle);
    }

    /// <summary>The destination's library count, or null while the stats are still warming (the row simply shows no
    /// number until they land). Mirrors <c>SidebarPaneSlot.CountBadge</c>'s mapping.</summary>
    static int? Count(LibraryStore? store, string routeKey)
    {
        int index = routeKey switch { "albums" => 0, "artists" => 1, "liked" => 2, "podcasts" => 3, _ => -1 };
        if (index < 0 || store is null) return null;
        var stats = store.Stats;
        if ((Wavee.Backend.LoadState)stats.State.Value != Wavee.Backend.LoadState.Ready ||
            stats.Value.Value is not { } s) return null;
        return index switch { 0 => s.Albums, 1 => s.Artists, 2 => s.LikedSongs, _ => s.Podcasts };
    }
}
