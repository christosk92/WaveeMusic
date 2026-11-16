// ── Home/Board/Hosts/Tile.Hosts.cs — one core host per tile source (pinned-home-tech.md 1.5) ─────────────────────────────────────
//
// Role: CORE hosts. `HomeTileHost` is the one door: per entry it picks the form the packed box can carry (the theme's rule over
// `PackCell.Box`, re-rendering only when the FORM changes, never per pixel of a re-flow) and mounts its source's host. Each source host reads
// its own core state and mounts its `Home.Tile.*` key; the bodies are today's (the daylist card, the Recents grid, the release list, the zone
// bodies), so nothing is drawn twice. No element types, no tokens (WVTH008).

using System.Collections.Generic;
using System.Globalization;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Hooks;
using Wavee.HomeUi;
using Wavee.Theming.Contracts;
using Slots = FluentGpu.Theming.Slots;

namespace Wavee;

/// <summary>The props of a tile host: the entry by value (a re-projection with an equal entry is a no-op at the embed boundary), the cell, the
/// theme's form rule, the verbs and the overlay by reference. The rule is the theme's lambda, rebuilt with its board's render: it is NOT part of
/// equality (the same rule over the same cell gives the same form).</summary>
public sealed record HomeTileHostProps(BoardEntry Entry, PackCell? Cell, Func<PackBox, HomeTileForm> FormRule, IHomeBoardVerbs Verbs, IOverlayService? Overlay, HomeBoardView View)
{
    /// <inheritdoc/>
    public bool Equals(HomeTileHostProps? o)
        => o is not null && Entry.Equals(o.Entry) && ReferenceEquals(Cell, o.Cell) && ReferenceEquals(Verbs, o.Verbs) && ReferenceEquals(Overlay, o.Overlay);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Entry, Cell);
}

/// <summary>The tile door: the form, then the source's host.</summary>
[CoreHost]
public sealed class HomeTileHost : Component
{
    HomeTileHostProps? _p;
    static readonly Dictionary<string, HomeTileForm> s_forms = new(StringComparer.Ordinal);

    /// <summary>The form the realized tile of an entry drew last (the inspector's `home do=status`).</summary>
    public static HomeTileForm? FormOf(string entryId) => s_forms.TryGetValue(entryId, out var f) ? f : null;

    /// <summary>The tile of an entry.</summary>
    public static Element For(BoardEntry entry, PackCell? cell, Func<PackBox, HomeTileForm> form, IHomeBoardVerbs verbs, IOverlayService? overlay, HomeBoardView view)
        => Embed.Comp(new HomeTileHostProps(entry, cell, form, verbs, overlay, view), static () => new HomeTileHost());

    /// <summary>The options body of the open entry.</summary>
    public static Element OptionsFor(BoardEntry entry, IHomeBoardVerbs verbs, HomeBoardView view)
        => Embed.Comp(new HomeTileOptionsHostProps(entry, verbs), static () => new HomeTileOptionsHost());

    public override Element Render()
    {
        var p = UseProps<HomeTileHostProps>();
        _p = p;
        // The form follows the box, but only its edges re-render the tile (a memo with an equality cut-off over the cell's box signal).
        var form = UseComputed(() => _p!.FormRule(_p.Cell?.Box.Value ?? default)).Value;
        var frame = new HomeTileFrame(p.Entry, p.Cell, form, p.Verbs, p.Overlay, p.View);
        if (p.Cell is not null) s_forms[p.Entry.Id] = form;
        var e = p.Entry;
        if (e.Kind == BoardEntryKind.Placeholder)
            return Slots.View(ViewKeys.HomeTileVacant, new HomeTileVacantProps
            {
                Frame = frame, Name = e.Title,
                Reason = e.Feed switch { HomeFeedState.Offline => HomeTileVacancy.Offline, HomeFeedState.Error => HomeTileVacancy.Failed, _ => HomeTileVacancy.Loading },
            });
        if (e.Paused && e.Live && e.Raw is WidgetSource { Widget: "nowPlaying" })
            return Slots.View(ViewKeys.HomeTileVacant, new HomeTileVacantProps { Frame = frame, Reason = HomeTileVacancy.Paused, Name = e.Title });

        switch (e.Raw)
        {
            case PinSource pin:
                return Embed.Comp(new PinTileHostProps(frame, pin.PinId, pin.NameHint, pin.CoverHint, UserPin: e.Kind != BoardEntryKind.FillCell),
                                  static () => new PinTileHost());
            case WidgetSource w:
                switch (w.Widget)
                {
                    case "nowPlaying": return Embed.Comp(new WidgetTileProps(frame), static () => new NowPlayingTileHost());
                    case "upNext": return Embed.Comp(new WidgetTileProps(frame), static () => new UpNextTileHost());
                    case "recents": return Embed.Comp(new WidgetTileProps(frame), static () => new RecentsTileHost());
                    case "releases": return Embed.Comp(new WidgetTileProps(frame), static () => new ReleasesTileHost());
                    case "daylist": return Embed.Comp(new WidgetTileProps(frame), static () => new DaylistTileHost());
                }
                break;
        }
        if (e.Kind is BoardEntryKind.FeedSection or BoardEntryKind.Fold || e.Raw is SpotifySectionSource)
            return Embed.Comp(new WidgetTileProps(frame), static () => new SectionTileHost());
        return Slots.View(ViewKeys.HomeTileVacant, new HomeTileVacantProps { Frame = frame, Reason = HomeTileVacancy.Unknown, Name = e.Title });
    }
}

/// <summary>A widget host's props: the frame (its entry by value, its form).</summary>
public sealed record WidgetTileProps(HomeTileFrame Frame);

/// <summary>A pin host's props.</summary>
public sealed record PinTileHostProps(HomeTileFrame Frame, string PinId, string NameHint, string CoverHint, bool UserPin);

/// <summary>A pinned entity or a shortcut fill cell: the entity tables' row, the document's hints until it lands.</summary>
[CoreHost]
public sealed class PinTileHost : Component
{
    public override Element Render()
    {
        var p = UseProps<PinTileHostProps>();
        string pinId = p.PinId;
        string uri = SidebarPinId.UriOf(pinId);
        if (pinId == "liked" || uri.Length == 0 && EntityUri.IsLikedCollection(pinId)) uri = EntityUri.LikedCollection;
        var id = uri.Length > 0 ? EntityUri.Parse(uri) : default;
        var target = id.IsValid ? Entities.Ref(id.Id) : default;

        string hintName = p.NameHint, hintCover = p.CoverHint;
        // A pin nobody else shows is asked here (an effect, never the render); a row the answer settled without is gone.
        UseEffect(() => { HomePinState.Ensure(target); }, DepKey.From((int)target.Kind, target.Slot));
        if (HomePinState.IsGone(target))
            return Slots.View(ViewKeys.HomeTileVacant, new HomeTileVacantProps { Frame = p.Frame, Reason = HomeTileVacancy.Unavailable, Name = hintName });
        Controls.CardData? card = target.IsNone ? null : Search.CardDataOf(target, HomeCardNav.HomeOrigin);
        return Slots.View(ViewKeys.HomeTilePin, new HomePinTileProps
        {
            Frame = p.Frame,
            Name = () => NameOf(target, hintName),
            Line = EntityUri.IsLikedCollection(uri) ? s_likedLine : () => LineOf(target),
            Cover = () => CoverOf(target, uri) ?? (hintCover.Length > 0 ? hintCover : null),
            Round = !target.IsNone && Search.RoundArt(target.Kind),
            CoverArt = EntityUri.IsLikedCollection(uri) ? s_liked : null,
            UserPin = p.UserPin,
            Playing = uri.Length > 0 ? () => Controls.RelatesNow(uri) : static () => false,
            Open = card?.OnClick ?? (() => { if (pinId == "liked") Shell.GoTo(new Shell.Route(Shell.RouteKind.Liked)); }),
            Play = card?.OnPlay,
            Card = card,
        });
    }

    static readonly Func<float, Element> s_liked = static edge => Sidebar.Cover.Liked(edge);

    /// <summary>Liked Songs' line: its song count, as the Recently played grid words it (a subscribing read of the user's liked set).</summary>
    static readonly Func<string> s_likedLine = static () =>
    {
        _ = Entities.ScopeEpoch.Value;
        _ = Entities.Current.Users.Changed.Value;
        return Strings.Detail.SongCount(Math.Max(0, User.Me.LikedTrackSlots.Length));
    };

    static string NameOf(EntityRef r, string hint)
    {
        if (r.IsNone) return hint;
        _ = Entities.ScopeEpoch.Value;
        string t = Search.TitleOf(r);
        return t.Length > 0 ? t : hint;
    }

    static string LineOf(EntityRef r)
    {
        if (r.IsNone) return "";
        _ = Entities.ScopeEpoch.Value;
        var card = new HomeCard(r);
        string kind = Search.KindWordOf(r.Kind, default);
        string? sub = r.Kind is EntityKind.Playlist or EntityKind.Collection ? card.OwnerName : card.Subtitle;
        if (sub is { Length: > 0 } && string.Equals(sub, kind, StringComparison.CurrentCultureIgnoreCase)) return kind;
        return sub is { Length: > 0 } && kind.Length > 0 ? kind + " · " + sub : sub is { Length: > 0 } ? sub : kind;
    }

    static string? CoverOf(EntityRef r, string uri)
    {
        if (r.IsNone) return null;
        return Search.CoverOf(r) ?? (EntityUri.IsLikedCollection(uri) ? User.LikedNewestTileUrl() : null);
    }
}

/// <summary>The Now playing widget: the player bar's own reads.</summary>
[CoreHost]
public sealed class NowPlayingTileHost : Component
{
    static readonly Func<bool> s_has = static () => !Playback.Current.Value.IsNone;
    static readonly Func<string> s_title = static () => Shell.PlayerBarHost.Props.TitleText();
    static readonly Func<string> s_line = static () => MiniPlayer.ArtistText();
    static readonly Func<string?> s_cover = static () => Rail.NowArtUrl();
    static readonly Func<Element> s_progress = static () => Shell.ProgressLine();
    static readonly Action s_playPause = static () => Playback.TogglePlay("home.tile");
    static readonly Action s_next = static () => Playback.Next();
    static readonly Action s_prev = static () => Playback.Previous();
    static readonly Action s_open = static () => Shell.PlayerBarHost.Props.OpenNowPlaying?.Invoke();
    static readonly Func<bool> s_shown = static () => Shell.PageShown.Value;
    static readonly Func<Func<MiniLyricsFrame, Element>, Element> s_lyric = static draw => Lyrics.Mini(draw, s_shown);

    public override Element Render()
    {
        var p = UseProps<WidgetTileProps>();
        bool lyric = p.Frame.Entry.Options.TryGetValue("lyric", out var l) && l == "1";
        return Slots.View(ViewKeys.HomeTileNowPlaying, new HomeNowPlayingTileProps
        {
            Frame = p.Frame, HasTrack = s_has, Title = s_title, Line = s_line, Cover = s_cover, ProgressLine = s_progress,
            Playing = Playback.IsPlaying, Lyric = lyric ? s_lyric : null,
            PlayPause = s_playPause, Next = s_next, Previous = s_prev, Open = s_open,
        });
    }
}

/// <summary>The Up next widget: the queue panel's own rows.</summary>
[CoreHost]
public sealed class UpNextTileHost : Component
{
    static readonly Func<NextUpRow[]> s_rows = static () => Rail.NextUpRows();
    static readonly Action s_queue = static () => Shell.PlayerBarHost.Props.OpenQueue?.Invoke();

    public override Element Render()
    {
        var p = UseProps<WidgetTileProps>();
        return Slots.View(ViewKeys.HomeTileUpNext, new HomeUpNextTileProps { Frame = p.Frame, Rows = s_rows, OpenQueue = s_queue });
    }
}

/// <summary>The Recently played widget: the Recents grid's own reads (`RecentGrid`), handed to the tile with the zone the projection gave.</summary>
[CoreHost]
public sealed class RecentsTileHost : Component
{
    RecentGrid.Cache? _cache;

    public override Element Render()
    {
        var p = UseProps<WidgetTileProps>();
        var zone = HomeBoardZones.RecentsZone(p.Frame.Entry);
        var handle = Recents.Me;
        UseEffect(() => { if (handle.Slot > 0) Entities.EnsureEdge(FetchEdge.Recents, handle.Slot); });
        var props = RecentGrid.PropsOf(zone, ref _cache);
        return Slots.View(ViewKeys.HomeTileRecents, new HomeRecentsTileProps { Frame = p.Frame, Recents = props, OpenHistory = HomeVerbs.OpenRecents });
    }
}

/// <summary>The New releases widget: the Following release list.</summary>
[CoreHost]
public sealed class ReleasesTileHost : Component
{
    public override Element Render()
    {
        var p = UseProps<WidgetTileProps>();
        var zone = HomeBoardZones.ReleasesZone(p.Frame.Entry);
        return Slots.View(ViewKeys.HomeTileReleases, new HomeReleasesTileProps { Frame = p.Frame, Zone = zone, SeeAll = HomeVerbs.SeeAll(zone) });
    }
}

/// <summary>The Daylist widget: today's daylist card through its own host.</summary>
[CoreHost]
public sealed class DaylistTileHost : Component
{
    public override Element Render()
    {
        var p = UseProps<WidgetTileProps>();
        var zone = HomeBoardZones.DaylistZone(p.Frame.Entry);
        var overlay = p.Frame.Overlay;
        string uri = zone.Items.Count > 0 ? zone.Items[0].Uri : "";
        return Slots.View(ViewKeys.HomeTileDaylist, new HomeDaylistTileProps
        {
            Frame = p.Frame,
            Zone = zone,
            Card = Embed.Comp(new DaylistProps(zone, overlay), static () => new DaylistCard()),
            Eyebrow = DaylistCard.EyebrowText,
            Play = uri.Length > 0 ? () => Playback.PlayOrToggleContext(uri, "home.tile") : null,
        });
    }
}

/// <summary>One Spotify section (or a fold of one-card sections): its zone, its "See all".</summary>
[CoreHost]
public sealed class SectionTileHost : Component
{
    public override Element Render()
    {
        var p = UseProps<WidgetTileProps>();
        var zone = HomeBoardZones.SectionZone(p.Frame.Entry);
        return Slots.View(ViewKeys.HomeTileSection, new HomeSectionTileProps
        {
            Frame = p.Frame, Zone = zone, Template = p.Frame.Entry.Template, SeeAll = HomeVerbs.SeeAll(zone),
        });
    }
}

/// <summary>The options host's props.</summary>
public sealed record HomeTileOptionsHostProps(BoardEntry Entry, IHomeBoardVerbs Verbs);

/// <summary>The open tile's options: the source's schema, the document's values, the verbs.</summary>
[CoreHost]
public sealed class HomeTileOptionsHost : Component
{
    public override Element Render()
    {
        var p = UseProps<HomeTileOptionsHostProps>();
        var e = p.Entry;
        var verbs = p.Verbs;
        var source = e.Source;
        string id = e.Id;
        return Slots.View(ViewKeys.HomeTileOptions, new HomeTileOptionsProps
        {
            Entry = e,
            Title = e.Title,
            Options = source?.Options ?? [],
            Value = key => OptionOf(id, key) ?? DefaultOf(source, key),
            Set = verbs.SetOption,
            Live = e.Live,
            Paused = () => OptionOf(id, "live") == "0",
            ToggleLive = e.Live ? () => verbs.ToggleLive(id) : null,
            Done = () => verbs.Close(refocus: true),
        });
    }

    /// <summary>An item's saved option (a subscribing read of the document), or null.</summary>
    static string? OptionOf(string id, string key)
    {
        var doc = HomeBoardStore.Instance.Doc.Value;
        foreach (var g in doc.Groups)
            foreach (var it in g.Items)
                if (it.Id == id) return it.Options.TryGetValue(key, out var v) ? v : null;
        return null;
    }

    static string DefaultOf(HomeSourceDef? source, string key)
    {
        if (source is null) return "";
        foreach (var o in source.Options) if (o.Key == key) return o.Default;
        return "";
    }
}
