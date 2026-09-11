using System;
using System.Collections.Generic;
using System.Linq;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using Wavee.Core;
using Wavee.Core.Catalog;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

/// <summary>The expandable card list owns a separate lease so its demand never replaces the track viewport.</summary>
sealed class AlbumMoreByList : Component
{
    internal sealed record Props(string AlbumUri, string ArtistName, IReadOnlyList<Album> Fallback,
        DetailHandlers Handlers, ActionServices? Actions);
    sealed record Cards(string ArtistName, IReadOnlyList<Album> Albums);

    public override Element Render()
    {
        var p = UseProps<Props>();
        // Parked-page mount can precede the provider; see findings 4.1. Re-render on un-park so the section fills in
        // once the page is reachable again instead of staying empty forever.
        UseActivation(onActivated: () => Context.RequestRerender());
        var svc = UseContext(Services.Slot);
        if (svc is null) return new BoxEl();
        var shown = UseSignal(TrailingStack.Cap);
        var demand = new QueryDemand(true, QueryPriority.Visible, []);
        var view = QueryHooks.UseMapped(Context, static (page, value) => page.SetReady(value), svc.Queries, new AlbumDetailQuery(svc.CatalogScope, p.AlbumUri),
            album => new Cards(album.Artists.FirstOrDefault()?.Name ?? p.ArtistName, album.MoreByArtist ?? p.Fallback),
            new Cards(p.ArtistName, p.Fallback), demand);
        UseEffect(() => { view.Binding.Value?.SetDemand(demand); });
        var cards = view.Loadable.Value.Value;
        return AlbumTrailing.AlbumList(Strings.Detail.MoreBy(cards.ArtistName), cards.Albums, p.Handlers, p.Actions,
            identity: "more-by:" + p.AlbumUri, shownChanged: count => shown.SetIfChanged(count));
    }
}

/// <summary>The popup mounts its query only while open. Closing or parking releases card demand.</summary>
sealed class AlbumVersionsSelector : Component
{
    internal sealed record Props(string AlbumUri, DetailHandlers Handlers);

    public override Element Render()
    {
        var p = UseProps<Props>();
        var open = UseSignal(false);
        UseActivation(onDeactivated: () => open.Value = false);
        return Popup.Create(Button.Standard(Loc.Get(Strings.Detail.OtherVersions), () => open.Value = !open.Peek()),
            () => Embed.Comp(new AlbumVersionsMenu.Props(p.AlbumUri, p.Handlers, () => open.Value = false),
                () => new AlbumVersionsMenu()), open, options: new PopupOptions(FocusTrap: true));
    }
}

sealed class AlbumVersionsMenu : Component
{
    internal sealed record Props(string AlbumUri, DetailHandlers Handlers, Action Close);

    public override Element Render()
    {
        var p = UseProps<Props>();
        // Parked-page mount can precede the provider; see findings 4.1. Re-render on un-park so the menu fills in
        // once the page is reachable again instead of staying empty forever.
        UseActivation(onActivated: () => Context.RequestRerender());
        var svc = UseContext(Services.Slot);
        if (svc is null) return new BoxEl();
        var end = UseSignal(50);
        var demand = new QueryDemand(true, QueryPriority.Visible, []);
        var view = QueryHooks.Use(Context, static (page, value) => page.SetReady(value), svc.Queries, new AlbumDetailQuery(svc.CatalogScope, p.AlbumUri),
            new Album(EntityUri.IdOf(p.AlbumUri), p.AlbumUri, "", null, [], 0, 0), demand);
        UseEffect(() => { view.Binding.Value?.SetDemand(demand); });
        var versions = view.Loadable.Value.Value.OtherVersions;
        var resources = view.Binding.Value?.Resources.Value;
        var pages = resources?.Values.Select(resource => resource.Value).OfType<RelationPageValue>()
            .Where(page => page.Kind == FacetKind.AlbumVersions).ToArray() ?? [];
        int total = pages.Select(page => page.Total ?? 0).DefaultIfEmpty().Max();
        var rows = new List<Element>();
        if (versions is not null)
            foreach (var album in versions.Take(end.Value))
                rows.Add(album.Name.Length == 0
                    ? new BoxEl { Height = 32f, Padding = Edges4.All(Spacing.S), Children =
                        [new BoxEl { Width = 180f, Height = 12f, Corners = Radii.CardAll, Fill = Tok.FillCardDefault }] }
                    : Button.Create(AlbumTrailing.VersionLabel(album), () => { p.Close(); p.Handlers.OpenAlbum(album); },
                        ButtonAppearance.Subtle, ControlSize.Small) with { Key = album.Uri });
        bool failed = view.Failure.Value is not null || view.Problems.Value.Count > 0;
        if (failed)
        {
            rows.Add(new TextEl(Loc.Get(Strings.Common.ErrorTitle)) { Size = 12f, Color = Tok.TextSecondary });
            rows.Add(Button.Standard(Loc.Get(Strings.Common.Retry), () =>
            { if (view.Binding.Peek() is { } binding) _ = binding.RefreshAsync(); }));
        }
        else if (versions is null)
            rows.Add(new TextEl(Loc.Get(Strings.Player.Loading)) { Size = 12f, Color = Tok.TextSecondary });
        else if (versions.Count == 0)
            rows.Add(new TextEl(Loc.Get(Strings.Search.NoResults)) { Size = 12f, Color = Tok.TextSecondary });
        bool more = versions is not null && (total > Math.Min(versions.Count, end.Value)
            || pages.Any(page => page.Coverage == RelationCoverage.Partial && page.NextCursor is not null));
        if (more)
            rows.Add(Button.Standard(Loc.Get(Strings.Podcast.LoadMore), () => end.Value += 50));
        return ScrollView(new BoxEl { Direction = 1, Gap = Spacing.XS, Padding = Edges4.All(Spacing.S),
            Children = rows.ToArray() }) with { Width = 340f, MaxHeight = 420f };
    }
}
