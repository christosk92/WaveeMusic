// ── Platform/Menus.Container.cs — THE one album / playlist / artist / show menu ───────────────────────────────────────────
//
// Before: five builders (Browse's card menu, the Artist page's, Recents', the sidebar's, the Album/Playlist/Show heroes')
// each spelled the same entity's menu in its own verbs and its own order — an album offered Save as a row here and a strip
// button there, Go to artist on two surfaces of four. Now ONE pure table (ContainerMenuRules: which verbs, in which order,
// per kind) and ONE composer (Menus.Container: that table over the registered verbs, in the Actions.Menu grammar). A
// surface passes what is genuinely its own through ContainerExtras and nothing else.

using System;
using System.Collections.Generic;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Localization;

namespace Wavee;

/// <summary>A container verb, independent of how it is registered or drawn.</summary>
public enum ContainerVerb : byte
{
    Play, PlayNext, AddToQueue, Save, AddToPlaylist, Open, Pin, GoToArtist, ArtistRadio, Share,
    /// <summary>Open in new tab: directly under Open, where the structure has tabs (owner, 2026-11-18).</summary>
    OpenInTab,
    /// <summary>Pin to Home / Unpin from Home: directly under the sidebar's pin row (design 1.6).</summary>
    PinHome,
}

/// <summary>One kind's menu: the labeled <see cref="Strip"/> (the Win11 command bar) over the <see cref="Rows"/>.</summary>
public readonly record struct ContainerMenuPlan(ContainerVerb[] Strip, ContainerVerb[] Rows)
{
    public bool IsEmpty => Strip.Length == 0 && Rows.Length == 0;
}

/// <summary>PURE: the container menu's verb table. The order is the app grammar (transport → state → collection →
/// navigation → Share), settled from what the five old builders agreed on:
/// <list type="bullet">
/// <item>Album — strip Play · Play next · Add to queue · Save; rows Add to playlist · Open · Pin/Unpin · Go to artist · Share.</item>
/// <item>Playlist — the album's, minus Go to artist (the account's own — Liked Songs, Your Episodes, a playlist it owns — also
/// drops Save: it is always yours).</item>
/// <item>Artist — strip Play · Follow; rows Open · Pin/Unpin · Go to artist radio · Share (no queue pair, no deposit).</item>
/// <item>Show — strip Play · Play next · Add to queue · Save; rows Open · Pin/Unpin · Share.</item>
/// </list>
/// <b>On a page</b> (the hero "…") the page already has its own Play, Save and Open, so the strip dissolves: the strip's
/// remaining verbs lead the rows, then the rows minus Open and Go to artist — Play next · Add to queue · Add to playlist ·
/// Pin/Unpin · Share.</summary>
public static class ContainerMenuRules
{
    static readonly ContainerVerb[] s_none = [];

    static readonly ContainerVerb[] s_listStrip =
        [ContainerVerb.Play, ContainerVerb.PlayNext, ContainerVerb.AddToQueue, ContainerVerb.Save];
    static readonly ContainerVerb[] s_listStripOwn =
        [ContainerVerb.Play, ContainerVerb.PlayNext, ContainerVerb.AddToQueue];
    static readonly ContainerVerb[] s_albumRows =
        [ContainerVerb.AddToPlaylist, ContainerVerb.Open, ContainerVerb.OpenInTab, ContainerVerb.Pin, ContainerVerb.PinHome, ContainerVerb.GoToArtist, ContainerVerb.Share];
    static readonly ContainerVerb[] s_playlistRows =
        [ContainerVerb.AddToPlaylist, ContainerVerb.Open, ContainerVerb.OpenInTab, ContainerVerb.Pin, ContainerVerb.PinHome, ContainerVerb.Share];
    static readonly ContainerVerb[] s_artistStrip = [ContainerVerb.Play, ContainerVerb.Save];
    static readonly ContainerVerb[] s_artistRows =
        [ContainerVerb.Open, ContainerVerb.OpenInTab, ContainerVerb.Pin, ContainerVerb.PinHome, ContainerVerb.ArtistRadio, ContainerVerb.Share];
    static readonly ContainerVerb[] s_showRows = [ContainerVerb.Open, ContainerVerb.OpenInTab, ContainerVerb.Pin, ContainerVerb.PinHome, ContainerVerb.Share];

    // The on-page variants are DERIVED from the card tables (not restated) so the two cannot drift.
    static readonly ContainerMenuPlan s_album = new(s_listStrip, s_albumRows);
    static readonly ContainerMenuPlan s_playlist = new(s_listStrip, s_playlistRows);
    static readonly ContainerMenuPlan s_own = new(s_listStripOwn, s_playlistRows);
    static readonly ContainerMenuPlan s_artist = new(s_artistStrip, s_artistRows);
    static readonly ContainerMenuPlan s_show = new(s_listStrip, s_showRows);

    static readonly ContainerMenuPlan s_albumPage = OnPage(s_album);
    static readonly ContainerMenuPlan s_playlistPage = OnPage(s_playlist);
    static readonly ContainerMenuPlan s_ownPage = OnPage(s_own);
    static readonly ContainerMenuPlan s_artistPage = OnPage(s_artist);
    static readonly ContainerMenuPlan s_showPage = OnPage(s_show);

    /// <param name="kind">Album · Artist · Playlist · Show; anything else has no container menu (empty plan).</param>
    /// <param name="own">The account's own collection (<c>Entities.IsOwnCollection</c>): a playlist target that is always
    /// yours, so it has no Save.</param>
    /// <param name="onPage">The hero "…" of the entity's own page: no strip, no Open, no Go to artist.</param>
    public static ContainerMenuPlan For(TargetKind kind, bool own, bool onPage)
    {
        bool l = own && kind == TargetKind.Playlist;
        return kind switch
        {
            TargetKind.Album => onPage ? s_albumPage : s_album,
            TargetKind.Playlist => l ? (onPage ? s_ownPage : s_own) : (onPage ? s_playlistPage : s_playlist),
            TargetKind.Artist => onPage ? s_artistPage : s_artist,
            TargetKind.Show => onPage ? s_showPage : s_show,
            _ => new ContainerMenuPlan(s_none, s_none),
        };
    }

    static ContainerMenuPlan OnPage(ContainerMenuPlan card)
    {
        var rows = new List<ContainerVerb>(card.Strip.Length + card.Rows.Length);
        foreach (var v in card.Strip) if (v is not (ContainerVerb.Play or ContainerVerb.Save)) rows.Add(v);
        foreach (var v in card.Rows) if (v is not (ContainerVerb.Open or ContainerVerb.OpenInTab or ContainerVerb.GoToArtist)) rows.Add(v);
        return new ContainerMenuPlan(s_none, rows.ToArray());
    }
}

/// <summary>What a surface adds to the one menu. Everything defaults to "nothing": the card passes only
/// <see cref="OpenOrigin"/>, a hero passes <see cref="OnPage"/>, <see cref="Deposit"/> and <see cref="Tail"/>.</summary>
public readonly record struct ContainerExtras
{
    /// <summary>The seam bag the verbs run through; null = the app's <see cref="Actions.Services"/>.</summary>
    public ActionServices? Services { get; init; }
    /// <summary>The hero "…" of the entity's own page (see <see cref="ContainerMenuRules"/>).</summary>
    public bool OnPage { get; init; }
    /// <summary>Liked Songs (a playlist target without Save).</summary>
    public bool Liked { get; init; }
    /// <summary>Where the destination's masthead says it came from. The registered Open verb navigates without an
    /// origin (<c>ActionServices.Go</c> takes a route only), so a surface that has one gets an Open row that carries it.</summary>
    public Shell.NavOrigin? OpenOrigin { get; init; }
    /// <summary>Replaces the registered Add to playlist row — a page that already holds the tracks passes the track
    /// menu's own deposit submenu (immediate, no resolve round-trip).</summary>
    public MenuFlyoutItem? Deposit { get; init; }
    /// <summary>Fills the Pin/Unpin slot when set (the sidebar's pin ids are its own). The function is the answer, including
    /// null = no row.</summary>
    public Func<MenuFlyoutItem?>? Pin { get; init; }
    /// <summary>Open a separator group in front of the <see cref="Pin"/> slot (the sidebar's pin and Organize ▸ stand apart).</summary>
    public bool PinStartsGroup { get; init; }
    /// <summary>A row that follows the Pin slot in its group (the sidebar's Organize ▸; read only with <see cref="PinStartsGroup"/>).
    /// The function is the answer, including null = no row.</summary>
    public Func<MenuFlyoutItem?>? Organize { get; init; }
    /// <summary>Page- or surface-only rows, appended after Share behind a separator (owner pair, Rename, Delete…).</summary>
    public IReadOnlyList<MenuFlyoutItem>? Tail { get; init; }
    /// <summary>Replaces the header tile (the Liked Songs treatment).</summary>
    public Element? HeaderLeading { get; init; }
}

public static partial class Menus
{
    /// <summary>The container menu for an album, playlist, artist or show target, built at OPEN time. Null when the
    /// kind has no container menu or nothing is offerable.</summary>
    public static ContextMenuModel? Container(in ActionTarget target, string? art, string? subtitle,
                                              in ContainerExtras x = default)
    {
        // Own = the surface said Liked, or the target is the account's own collection (one rule for every surface: search, cards,
        // library, sidebar), so no caller has to remember it.
        var plan = ContainerMenuRules.For(target.Kind,
            x.Liked || target.Kind == TargetKind.Playlist && Entities.IsOwnCollection(target.Uri), x.OnPage);
        if (plan.IsEmpty) return null;
        var ctx = new ActionContext(target, x.Services ?? Actions.Services);

        var strip = new List<AppBarCommand>(plan.Strip.Length);
        foreach (var verb in plan.Strip)
            if (StripCommand(verb, in ctx) is { } cmd) strip.Add(cmd);

        var rows = new List<MenuFlyoutItem>(plan.Rows.Length + 2);
        bool sharePending = false;
        foreach (var verb in plan.Rows)
        {
            if (verb == ContainerVerb.Share) { sharePending = true; continue; }   // Share is its own group
            if (verb == ContainerVerb.Pin && x.PinStartsGroup)
            {
                var pinRow = x.Pin is { } pin ? pin() : PinRow(in ctx);
                var organize = x.Organize?.Invoke();
                Actions.Menu.Group(rows, pinRow ?? organize);
                if (pinRow is not null && organize is { } organizeRow) rows.Add(organizeRow);
                continue;
            }
            if (RowFor(verb, in ctx, in x) is { } row) rows.Add(row);
        }
        if (sharePending && Actions.Menu.Share(in ctx) is { } share) { Actions.Menu.OpenGroup(rows); rows.Add(share); }
        if (x.Tail is { Count: > 0 } tail)
        {
            Actions.Menu.OpenGroup(rows);
            for (int i = 0; i < tail.Count; i++) rows.Add(tail[i]);
        }
        if (strip.Count == 0 && rows.Count == 0) return null;

        string sub = subtitle is { Length: > 0 } ? subtitle : KindWordOf(target.Kind);
        return new ContextMenuModel(strip, rows,
            Actions.Menu.Header(art, target.Name, sub, circular: target.Kind == TargetKind.Artist, leading: x.HeaderLeading));
    }

    static string KindWordOf(TargetKind kind)
        => kind == TargetKind.Show ? Loc.Get(Strings.Menu.KindPodcast) : Actions.Menu.KindWord(kind);

    static ActionId IdOf(ContainerVerb verb) => verb switch
    {
        ContainerVerb.Play => ActionId.PlayContext,
        ContainerVerb.PlayNext => ActionId.PlayContextNext,
        ContainerVerb.AddToQueue => ActionId.AddContextToQueue,
        ContainerVerb.Save => ActionId.SaveContext,
        ContainerVerb.AddToPlaylist => ActionId.AddContextToPlaylist,
        ContainerVerb.Open => ActionId.OpenItem,
        ContainerVerb.GoToArtist => ActionId.GoToAlbumArtist,
        ContainerVerb.ArtistRadio => ActionId.GoToArtistRadio,
        _ => ActionId.None,
    };

    static AppBarCommand? StripCommand(ContainerVerb verb, in ActionContext ctx)
    {
        // A Save the target cannot take (Local Files, a stream with no library relation) is absent, never a dead button. The verb
        // words itself by kind (Save / Saved; Follow / Following for an artist or show: User.InstallSaveVerb).
        if (verb == ContainerVerb.Save && AppActions.Find(ActionId.SaveContext) is { } save && !save.EnabledFor(in ctx)) return null;
        return Actions.Menu.Command(IdOf(verb), in ctx);
    }

    static MenuFlyoutItem? RowFor(ContainerVerb verb, in ActionContext ctx, in ContainerExtras x)
    {
        switch (verb)
        {
            case ContainerVerb.AddToPlaylist:
                return x.Deposit ?? Actions.Menu.Row(ActionId.AddContextToPlaylist, in ctx);
            case ContainerVerb.Open:
                return OpenRow(in ctx, in x);
            case ContainerVerb.OpenInTab:
                return OpenInTabRow(in ctx);
            case ContainerVerb.Pin:
                return x.Pin is { } pin ? pin() : PinRow(in ctx);
            case ContainerVerb.PinHome:
                return HomePinRow(in ctx);
            default:
                return Actions.Menu.Row(IdOf(verb), in ctx);
        }
    }

    static MenuFlyoutItem? OpenRow(in ActionContext ctx, in ContainerExtras x)
    {
        if (x.OpenOrigin is not { } origin) return Actions.Menu.Row(ActionId.OpenItem, in ctx);
        var target = ctx.Target;
        var route = ActionRules.RouteFor(in target);
        return new MenuFlyoutItem(Loc.Get(Strings.Menu.Open), ActionIcons.Resolve(ActionIcons.Open), !route.IsNone,
            () => Shell.GoTo(route, origin));
    }

    /// <summary>"Open in new tab": the target's own page in a new tab beside this one (in the background where the structure hides its strip).
    /// Absent where the structure has no tabs (Zune), or the target has no page.</summary>
    static MenuFlyoutItem? OpenInTabRow(in ActionContext ctx)
    {
        if (!ShellCaps.Now.Tabs) return null;
        var target = ctx.Target;
        var route = ActionRules.RouteFor(in target);
        if (route.IsNone) return null;
        return Shell.OpenInNewTabRow(route);
    }

    /// <summary>The Home pin pair: "Unpin from Home" when the target is on Home; else "Pin to Home" as one row, or (Classic,
    /// <see cref="Theming.HomePinMenu.GroupsSubmenu"/>) a submenu of Home's groups with Pinned first. Built from the target's route
    /// (HomePinVerbs), so every container menu carries it.</summary>
    static MenuFlyoutItem? HomePinRow(in ActionContext ctx)
    {
        var target = ctx.Target;
        var route = ActionRules.RouteFor(in target);
        if (route.IsNone || HomePinVerbs.TargetFor(route) is not { } t) return null;
        string pinId = t.PinId, title = target.Name is { Length: > 0 } n ? n : t.Title;
        if (t.Pinned)
            return new MenuFlyoutItem(Loc.Get(Strings.Home.Board.Pin.Unpin), ActionIcons.Resolve(ActionIcons.Unpin), true, () => HomePinVerbs.Unpin(pinId));
        if (ShellPolicy.Now.Home.PinMenu != Theming.HomePinMenu.GroupsSubmenu)
            return new MenuFlyoutItem(Loc.Get(Strings.Home.Board.Pin.PinTo), ActionIcons.Resolve(ActionIcons.Pin), true, () => HomePinVerbs.Pin(pinId, title, ""));
        var doc = HomeBoardStore.Instance.Doc.Peek();
        var items = new List<MenuFlyoutItem>(doc.Groups.Length);
        int pinned = doc.GroupIndex(HomeDocs.Pinned);
        if (pinned >= 0) items.Add(GroupItem(doc.Groups[pinned].Id, pinId, title, first: true));
        for (int g = 0; g < doc.Groups.Length; g++)
            if (g != pinned) items.Add(GroupItem(doc.Groups[g].Id, pinId, title, first: false));
        return MenuFlyoutItem.SubMenu(Loc.Get(Strings.Home.Board.Pin.PinTo), items, ActionIcons.Resolve(ActionIcons.Pin));

        static MenuFlyoutItem GroupItem(string groupId, string pinId, string title, bool first)
            => new MenuFlyoutItem(HomePinVerbs.GroupTitle(groupId), first ? ActionIcons.Resolve(ActionIcons.Pin) : default, true,
                () => HomePinVerbs.Pin(pinId, title, "", groupId));
    }

    /// <summary>The absolute pin pair: only the one that can run is offered.</summary>
    static MenuFlyoutItem? PinRow(in ActionContext ctx)
    {
        if (AppActions.Find(ActionId.PinToSidebar) is { } pin && pin.EnabledFor(in ctx)) return pin.ToMenuItem(in ctx);
        if (AppActions.Find(ActionId.UnpinFromSidebar) is { } unpin && unpin.EnabledFor(in ctx)) return unpin.ToMenuItem(in ctx);
        return null;
    }
}
