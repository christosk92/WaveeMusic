using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Core;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

// The right-column content for a podcast SHOW on the shared detail surface (the episodes analogue of TrackList). Same
// ctor shape as TrackList so DetailShell swaps between them by DetailConfig.Content. Owns: a status filter
// (All/Unplayed/In-progress/Played) + a Newest/Oldest order (SelectorBars), a "Listen next" resume banner promoting the
// in-progress episode, and the episode list. Episodes play through the shared handler (Play(originalIndex) → the show
// context resolves the episode), so no new wiring. The model is Ready when this mounts (the shell mounts on Ready).
sealed class EpisodeList : Component
{
    readonly Loadable<DetailModel> _full;
    readonly DetailHandlers _h;
    readonly bool _showToolbar;
    readonly Signal<int> _status = new(0);   // 0 All · 1 Unplayed · 2 In progress · 3 Played
    readonly Signal<int> _order = new(0);    // 0 Newest · 1 Oldest
    // Paging (design 2.3): the show ladder brings the FIRST page of episodes up on open and pages the rest onto the
    // pump; this is the foreground "I reached the end of the list" ask for the next one. The flag is re-entrancy
    // control only — the rows arrive through the store, which re-maps the model this list renders.
    readonly Signal<bool> _paging = new(false);
    // The LOCAL half of the paging cursor: the offset the last load-more asked for, per show. The model carries the
    // authoritative one (Show.PagedThrough), but a page whose members all failed to hydrate writes nothing to the store,
    // so no re-map happens and the model's cursor never moves — the pill would then re-ask the same page on every tap.
    // Keyed by show uri so a reused instance (DetailShell swaps content in place) cannot carry one show's cursor to another.
    readonly Signal<(string Uri, int Through)> _pagedTo = new(("", 0));

    public EpisodeList(Loadable<DetailModel> full, DetailHandlers h, bool showToolbar = true)
    { _full = full; _h = h; _showToolbar = showToolbar; }

    static float Pct(Episode e) => e.DurationMs > 0 ? Math.Clamp(e.ProgressMs / (float)e.DurationMs, 0f, 1f) : 0f;
    static bool InProgress(Episode e) { float p = Pct(e); return p > 0.01f && p < 0.98f; }
    static bool Played(Episode e) => Pct(e) >= 0.98f;

    public override Element Render()
    {
        var m = _full.Value.Value;                 // subscribe → episodes appear on load
        var eps = m.Episodes ?? Array.Empty<Episode>();
        int status = _status.Value;                // subscribe
        int order = _order.Value;                  // subscribe

        // Filtered + ordered ORIGINAL indices (so Play uses the show-context index regardless of view).
        var view = new List<int>(eps.Count);
        for (int i = 0; i < eps.Count; i++)
        {
            var e = eps[i];
            bool match = status switch { 1 => Pct(e) <= 0.01f, 2 => InProgress(e), 3 => Played(e), _ => true };
            if (match) view.Add(i);
        }
        if (order == 1) view.Reverse();            // Oldest first (episodes are newest-first by default)

        var svc = UseContext(Services.Slot);
        var post = Context.UsePost();
        bool paging = _paging.Value;                                  // subscribe
        var pagedTo = _pagedTo.Value;                                 // subscribe
        // THE paging cursor, not a resident-vs-total count. `m.TotalEpisodes > eps.Count` was wrong in both directions:
        // an episode that cannot hydrate at all (withdrawn, region-locked) keeps the resident count permanently short,
        // so the pill stayed on screen forever and every tap re-asked the same unanswerable members from `eps.Count`.
        // PagedThrough is how far we have ASKED (design §2.3), which advances whether or not rows came back.
        string? showUri = m.ContextUri is { Length: > 0 } ? m.ContextUri : null;
        int pagedThrough = showUri is not null && pagedTo.Uri == showUri
            ? Math.Max(m.PagedThrough, pagedTo.Through) : m.PagedThrough;
        bool hasMore = showUri is not null && pagedThrough < m.TotalEpisodes;

        var children = new List<Element>(view.Count + 5);
        if (_showToolbar) children.Add(Toolbar(status, order));

        // "Listen next" resume banner — the most-progressed in-progress episode.
        int resume = -1; float best = 0f;
        for (int i = 0; i < eps.Count; i++)
            if (InProgress(eps[i]) && Pct(eps[i]) > best) { best = Pct(eps[i]); resume = i; }
        if (resume >= 0) { int ri = resume; children.Add(ResumeBanner(eps[resume], () => _h.Play(ri))); }

        children.Add(WaveeType.RailHeader(Loc.Get(Strings.Podcast.Episodes)));
        if (view.Count == 0)
            children.Add(new BoxEl { Padding = new Edges4(Spacing.L, Spacing.XL, Spacing.L, Spacing.XL),
                Children = [new TextEl(Loc.Get(Strings.Podcast.NoEpisodes)) { Size = 14f, Color = Tok.TextTertiary }] });
        else foreach (int oi in view) { int idx = oi; children.Add(EpisodeRow(eps[oi], () => _h.Play(idx))); }
        if (hasMore)
        {
            string uri = showUri!;
            int from = pagedThrough;
            children.Add(LoadMore(paging, () => Page(svc, uri, from, post)));
        }

        var body = new BoxEl
        {
            Direction = 1, Gap = Spacing.M,
            Padding = new Edges4(Spacing.L, Spacing.M, Spacing.L, PlayerDock.Reserve + Spacing.XXL),
            Children = children.ToArray(),
        };
        return ScrollView(body) with { Grow = 1f };
    }

    /// <summary>Ask the library for the next page of THIS show's episodes. Fire-and-forget: the rows land in the store,
    /// which bumps the show and re-maps the model this list renders — so there is nothing to assign back here, only the
    /// re-entrancy flag to clear (on the UI thread, through the page's post).</summary>
    void Page(Services? svc, string showUri, int from, Action<Action> post)
    {
        if (svc is null || _paging.Value) return;
        _paging.Value = true;
        _ = Task.Run(async () =>
        {
            try
            {
                // The returned cursor is the load-bearing part: it moves even when the page landed no rows, so a show
                // with unhydratable members still walks forward instead of re-asking the same block on every tap.
                int through = await svc.Library.LoadMoreEpisodesAsync(showUri, from).ConfigureAwait(false);
                post(() => _pagedTo.Value = (showUri, through));
            }
            catch { /* a failed page keeps the affordance: the next tap retries */ }
            finally { post(() => _paging.Value = false); }
        });
    }

    // A plain standard pill at the end of the list, not an infinite-scroll sentinel: a page is 300 EpisodeV4 rows over
    // the network, and firing that off a scroll position would page a long back-catalogue the moment a flick overshoots.
    static Element LoadMore(bool paging, Action page) => new BoxEl
    {
        Direction = 0, Justify = FlexJustify.Center, Margin = new Edges4(Spacing.S, 0f, 0f, 0f),
        Children =
        [
            WaveeCta.Pill(Loc.Get(paging ? Strings.Podcast.LoadingMore : Strings.Podcast.LoadMore),
                          paging ? static () => { } : page, ButtonAppearance.Standard),
        ],
    };

    Element Toolbar(int status, int order) => new BoxEl
    {
        Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M, Margin = new Edges4(0f, 0f, 0f, Spacing.XS),
        Children =
        [
            SelectorBar.Create(StatusLabels(), _status),
            new BoxEl { Grow = 1f },
            SelectorBar.Create(OrderLabels(), _order),
        ],
    };

    static string[] StatusLabels() =>
    [
        Loc.Get(Strings.Podcast.Filter.All), Loc.Get(Strings.Podcast.Filter.Unplayed),
        Loc.Get(Strings.Podcast.Filter.InProgress), Loc.Get(Strings.Podcast.Filter.Played),
    ];
    static string[] OrderLabels() => [Loc.Get(Strings.Podcast.Sort.Newest), Loc.Get(Strings.Podcast.Sort.Oldest)];

    static Element ResumeBanner(Episode e, Action resume) => new BoxEl
    {
        Direction = 1, Gap = Spacing.S,
        Children =
        [
            WaveeType.RailHeader(Loc.Get(Strings.Podcast.ListenNext)),
            new BoxEl
            {
                Direction = 0, Gap = Spacing.L, AlignItems = FlexAlign.Center,
                Padding = new Edges4(Spacing.M, Spacing.M, Spacing.L, Spacing.M),
                Corners = CornerRadius4.All(Radii.Card), Fill = Tok.FillCardSecondary,
                BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault, ClipToBounds = true,
                Children =
                [
                    new BoxEl { Width = 72f, Height = 72f, Shrink = 0f, Corners = CornerRadius4.All(8f), ClipToBounds = true,
                        Children = [Surfaces.Artwork(e.Image, e.Id.GetHashCode() & 0x7fffffff, 72f, 72f, 8f)] },
                    new BoxEl
                    {
                        Direction = 1, Grow = 1f, Basis = 0f, Gap = Spacing.XS,
                        Children =
                        [
                            WaveeType.Eyebrow(Loc.Get(Strings.Podcast.ContinueListening)) with { Color = Tok.TextTertiary },
                            new TextEl(e.Title) { Size = 15f, Weight = 700, Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                            new TextEl($"{e.PublishedAt:MMM d} · {e.DurationMs / 60000} min") { Size = 12f, Color = Tok.TextSecondary },
                            ProgressBar(Pct(e)),
                        ],
                    },
                    ResumePill(resume),
                ],
            },
        ],
    };

    static Element EpisodeRow(Episode e, Action play)
    {
        float pct = Pct(e);
        var meta = new List<Element>(5)
        {
            new TextEl(e.PublishedAt.ToString("MMM d")) { Size = 12f, Color = Tok.TextTertiary },
            Dot(),
            new TextEl($"{e.DurationMs / 60000} min") { Size = 12f, Color = Tok.TextTertiary },
        };
        if (pct > 0.01f && pct < 0.98f)
        {
            meta.Add(Dot());
            meta.Add(new TextEl(Loc.Get(Strings.Podcast.InProgress)) { Size = 12f, Weight = 600, Color = Tok.AccentTextPrimary });
        }
        return new BoxEl
        {
            Direction = 1, Gap = Spacing.S,
            Padding = new Edges4(Spacing.M, Spacing.M, Spacing.M, Spacing.M),
            Corners = CornerRadius4.All(Radii.Card), HoverFill = Tok.FillSubtleSecondary,
            BorderWidth = 1f, BorderColor = Tok.StrokeDividerDefault,
            Children =
            [
                new BoxEl
                {
                    Direction = 0, Gap = Spacing.L, AlignItems = FlexAlign.Center,
                    Children =
                    [
                        new BoxEl { Width = 56f, Height = 56f, Shrink = 0f, Corners = CornerRadius4.All(8f), ClipToBounds = true,
                            Children = [Surfaces.Artwork(e.Image, e.Id.GetHashCode() & 0x7fffffff, 56f, 56f, 8f)] },
                        new BoxEl
                        {
                            Direction = 1, Grow = 1f, Basis = 0f, Gap = 4f,
                            Children =
                            [
                                new TextEl(e.Title) { Size = 14f, Weight = 700, Color = Tok.TextPrimary, MaxLines = 2, Wrap = TextWrap.Wrap, Trim = TextTrim.CharacterEllipsis },
                                new TextEl(e.Description ?? "") { Size = 12f, Color = Tok.TextSecondary, MaxLines = 2, Wrap = TextWrap.Wrap, Trim = TextTrim.CharacterEllipsis },
                            ],
                        },
                        PlayCircle(play),
                    ],
                },
                new BoxEl { Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, Children = meta.ToArray() },
                pct > 0.01f ? ProgressBar(pct) : new BoxEl { Height = 0f },
            ],
        };
    }

    static Element Dot() => new TextEl("·") { Size = 12f, Color = Tok.TextTertiary };

    static Element ProgressBar(float pct) => new BoxEl
    {
        Direction = 0, Height = 3f, Corners = CornerRadius4.All(2f), Fill = Tok.FillSubtleTertiary, ClipToBounds = true,
        Children =
        [
            new BoxEl { Grow = Math.Max(0.001f, pct), Fill = Tok.AccentDefault },
            new BoxEl { Grow = Math.Max(0.001f, 1f - pct) },
        ],
    };

    static Element PlayCircle(Action play) => new BoxEl
    {
        Width = 40f, Height = 40f, Shrink = 0f, Corners = CornerRadius4.All(20f), Fill = Tok.AccentDefault,
        AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Shadow = Elevation.Card,
        HoverScale = WaveeMotion.ScaleEmphatic.Hover, PressScale = WaveeMotion.ScaleEmphatic.Press, OnClick = play,
        Children = [Icon(Icons.Play, 15f, Tok.TextOnAccentPrimary)],
    };

    // The shared media pill on the SYSTEM accent (nothing here is artwork-derived). Shrink 0 so the banner's flexing copy
    // column gives first — without it the pill is the child that collapses when the title is long.
    static Element ResumePill(Action resume)
        => WaveeCta.Accent(Loc.Get(Strings.Podcast.Resume), Tok.AccentDefault, resume)
            with { Shrink = 0f };
}
