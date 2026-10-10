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
/// <item>Playlist — the album's, minus Go to artist (Liked Songs also drops Save: it is always yours).</item>
/// <item>Artist — strip Play · Follow; rows Open · Pin/Unpin · Go to artist radio · Share (no queue pair, no deposit).</item>
/// <item>Show — strip Play · Play next · Add to queue · Save; rows Open · Pin/Unpin · Share.</item>
/// </list>
/// <b>On a page</b> (the hero "…") the page already has its own Play, Save and Open, so the strip dissolves: the strip's
/// remaining verbs lead the rows, then the rows minus Open and Go to artist — Play next · Add to queue · Add to playlist ·
/// Pin/Unpin · Share.
/// <para><b>On a page with an action row</b> (an album's or playlist's hero, whose Play split already runs Add to queue and
/// Play next and whose own Share button replaces the Share submenu) the plan is the on-page one minus those three — the
/// remaining rows, led at composition time by the page's <see cref="ContainerExtras.Lead"/> rows.</para></summary>
public static class ContainerMenuRules
{
    static readonly ContainerVerb[] s_none = [];

    static readonly ContainerVerb[] s_listStrip =
        [ContainerVerb.Play, ContainerVerb.PlayNext, ContainerVerb.AddToQueue, ContainerVerb.Save];
    static readonly ContainerVerb[] s_listStripOwn =
        [ContainerVerb.Play, ContainerVerb.PlayNext, ContainerVerb.AddToQueue];
    static readonly ContainerVerb[] s_albumRows =
        [ContainerVerb.AddToPlaylist, ContainerVerb.Open, ContainerVerb.Pin, ContainerVerb.GoToArtist, ContainerVerb.Share];
    static readonly ContainerVerb[] s_playlistRows =
        [ContainerVerb.AddToPlaylist, ContainerVerb.Open, ContainerVerb.Pin, ContainerVerb.Share];
    static readonly ContainerVerb[] s_artistStrip = [ContainerVerb.Play, ContainerVerb.Save];
    static readonly ContainerVerb[] s_artistRows =
        [ContainerVerb.Open, ContainerVerb.Pin, ContainerVerb.ArtistRadio, ContainerVerb.Share];
    static readonly ContainerVerb[] s_showRows = [ContainerVerb.Open, ContainerVerb.Pin, ContainerVerb.Share];

    // The on-page variants are DERIVED from the card tables (not restated) so the two cannot drift.
    static readonly ContainerMenuPlan s_album = new(s_listStrip, s_albumRows);
    static readonly ContainerMenuPlan s_playlist = new(s_listStrip, s_playlistRows);
    static readonly ContainerMenuPlan s_liked = new(s_listStripOwn, s_playlistRows);
    static readonly ContainerMenuPlan s_artist = new(s_artistStrip, s_artistRows);
    static readonly ContainerMenuPlan s_show = new(s_listStrip, s_showRows);

    static readonly ContainerMenuPlan s_albumPage = OnPage(s_album);
    static readonly ContainerMenuPlan s_playlistPage = OnPage(s_playlist);
    static readonly ContainerMenuPlan s_likedPage = OnPage(s_liked);
    static readonly ContainerMenuPlan s_artistPage = OnPage(s_artist);
    static readonly ContainerMenuPlan s_showPage = OnPage(s_show);

    // The action-row variants are DERIVED from the on-page plans (not restated), so they cannot drift either.
    static readonly ContainerMenuPlan s_albumRow = ActionRowOf(s_albumPage);
    static readonly ContainerMenuPlan s_playlistRow = ActionRowOf(s_playlistPage);

    /// <param name="kind">Album · Artist · Playlist · Show; anything else has no container menu (empty plan).</param>
    /// <param name="liked">Liked Songs: a playlist target that is always saved, so it has no Save.</param>
    /// <param name="onPage">The hero "…" of the entity's own page: no strip, no Open, no Go to artist.</param>
    /// <param name="actionRow">With <paramref name="onPage"/>, on an album / playlist whose hero carries the Play split and
    /// a Share button: Play next, Add to queue and Share live there, so the "…" drops them. Other kinds (Liked Songs among
    /// them, which has no Play split) ignore it.</param>
    public static ContainerMenuPlan For(TargetKind kind, bool liked, bool onPage, bool actionRow = false)
    {
        bool l = liked && kind == TargetKind.Playlist;
        bool row = onPage && actionRow;
        return kind switch
        {
            TargetKind.Album => row ? s_albumRow : onPage ? s_albumPage : s_album,
            TargetKind.Playlist => l ? (onPage ? s_likedPage : s_liked)
                                     : (row ? s_playlistRow : onPage ? s_playlistPage : s_playlist),
            TargetKind.Artist => onPage ? s_artistPage : s_artist,
            TargetKind.Show => onPage ? s_showPage : s_show,
            _ => new ContainerMenuPlan(s_none, s_none),
        };
    }

    static ContainerMenuPlan OnPage(ContainerMenuPlan card)
    {
        var rows = new List<ContainerVerb>(card.Strip.Length + card.Rows.Length);
        foreach (var v in card.Strip) if (v is not (ContainerVerb.Play or ContainerVerb.Save)) rows.Add(v);
        foreach (var v in card.Rows) if (v is not (ContainerVerb.Open or ContainerVerb.GoToArtist)) rows.Add(v);
        return new ContainerMenuPlan(s_none, rows.ToArray());
    }

    static ContainerMenuPlan ActionRowOf(ContainerMenuPlan onPage)
    {
        var rows = new List<ContainerVerb>(onPage.Rows.Length);
        foreach (var v in onPage.Rows) if (v is not (ContainerVerb.PlayNext or ContainerVerb.AddToQueue or ContainerVerb.Share)) rows.Add(v);
        return new ContainerMenuPlan(onPage.Strip, rows.ToArray());
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
    /// <summary>Fills the Pin/Unpin slot when set (the sidebar's pin lives in its Organize ▸ submenu, and its pin ids are
    /// its own). The function is the answer, including null = no row.</summary>
    public Func<MenuFlyoutItem?>? Pin { get; init; }
    /// <summary>Open a separator group in front of the <see cref="Pin"/> slot (the sidebar's Organize ▸ stands apart).</summary>
    public bool PinStartsGroup { get; init; }
    /// <summary>With <see cref="OnPage"/>: the hero carries the Play split and a Share button (an album's or playlist's
    /// action row), so the "…" drops Play next, Add to queue and Share (<see cref="ContainerMenuRules.For"/>).</summary>
    public bool ActionRow { get; init; }
    /// <summary>Rows BEFORE everything else (then a separator): the page's own lead — Download · Add to folder ·
    /// Copy link on an album or playlist hero.</summary>
    public IReadOnlyList<MenuFlyoutItem>? Lead { get; init; }
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
        var plan = ContainerMenuRules.For(target.Kind, x.Liked, x.OnPage, x.ActionRow);
        if (plan.IsEmpty) return null;
        var ctx = new ActionContext(target, x.Services ?? Actions.Services);

        var strip = new List<AppBarCommand>(plan.Strip.Length);
        foreach (var verb in plan.Strip)
            if (StripCommand(verb, in ctx) is { } cmd) strip.Add(cmd);

        var rows = new List<MenuFlyoutItem>(plan.Rows.Length + 2);
        bool sharePending = false;
        if (x.Lead is { Count: > 0 } lead)
        {
            for (int i = 0; i < lead.Count; i++) rows.Add(lead[i]);
            Actions.Menu.OpenGroup(rows);
        }
        foreach (var verb in plan.Rows)
        {
            if (verb == ContainerVerb.Share) { sharePending = true; continue; }   // Share is its own group
            if (verb == ContainerVerb.Pin && x.PinStartsGroup && x.Pin is { } grouped)
            {
                Actions.Menu.Group(rows, grouped());
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
        if (rows.Count > 0 && rows[^1].IsSeparator) rows.RemoveAt(rows.Count - 1);   // a Lead with nothing after it
        if (strip.Count == 0 && rows.Count == 0) return null;

        string sub = subtitle is { Length: > 0 } ? subtitle : KindWordOf(target.Kind);
        return new ContextMenuModel(strip, rows,
            Actions.Menu.Header(art, target.Name, sub, circular: target.Kind == TargetKind.Artist, leading: x.HeaderLeading));
    }

    static string KindWordOf(TargetKind kind)
        => kind == TargetKind.Show ? Loc.Get(Strings.Menu.KindPodcast) : Actions.Menu.KindWord(kind);

    /// <summary>The registered action a container verb runs (the Play split runs the same two for Play next / Add to queue).</summary>
    public static ActionId IdOf(ContainerVerb verb) => verb switch
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
        var cmd = Actions.Menu.Command(IdOf(verb), in ctx);
        // An artist's Save is Follow: same verb, the word the artist surfaces use.
        if (cmd is { } c && verb == ContainerVerb.Save && ctx.Target.Kind == TargetKind.Artist)
            return c with { Label = Loc.Get(c.IsChecked ? Strings.Artist.Following : Strings.Artist.Follow) };
        return cmd;
    }

    static MenuFlyoutItem? RowFor(ContainerVerb verb, in ActionContext ctx, in ContainerExtras x)
    {
        switch (verb)
        {
            case ContainerVerb.AddToPlaylist:
                return x.Deposit ?? Actions.Menu.Row(ActionId.AddContextToPlaylist, in ctx);
            case ContainerVerb.Open:
                return OpenRow(in ctx, in x);
            case ContainerVerb.Pin:
                return x.Pin is { } pin ? pin() : PinRow(in ctx);
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

    /// <summary>The absolute pin pair: only the one that can run is offered.</summary>
    static MenuFlyoutItem? PinRow(in ActionContext ctx)
    {
        if (AppActions.Find(ActionId.PinToSidebar) is { } pin && pin.EnabledFor(in ctx)) return pin.ToMenuItem(in ctx);
        if (AppActions.Find(ActionId.UnpinFromSidebar) is { } unpin && unpin.EnabledFor(in ctx)) return unpin.ToMenuItem(in ctx);
        return null;
    }
}
