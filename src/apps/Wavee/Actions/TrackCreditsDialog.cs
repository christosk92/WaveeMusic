using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;
using FluentGpu.Localization;
using Wavee.Core;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

// The "View credits" modal body (opened by TrackActions.ViewCredits inside a ContentDialog titled with the track name).
// Fetches the track's NPV contributor list through the album-enrichment seam — the SAME source NowPlayingPanel's inline
// credits section reads — and renders it with that section's row-rendering approach: grouped by role group with a small
// caps header, accent-linked contributor rows, and a source-attribution line. Loading + empty states included.
sealed class TrackCreditsDialog : Component
{
    readonly Services _svc;
    readonly string _artistUri;
    readonly string _trackUri;
    readonly Action<string, string?>? _go;

    public TrackCreditsDialog(Services svc, string artistUri, string trackUri, Action<string, string?>? go)
    {
        _svc = svc; _artistUri = artistUri; _trackUri = trackUri; _go = go;
    }

    public override Element Render()
    {
        var infoL = UseResource(ct => LoadAsync(_svc, _artistUri, _trackUri, ct), (TrackNpvInfo?)null, (_artistUri, _trackUri)).Loadable;
        var state = (LoadState)infoL.State.Value;
        var info = infoL.Value.Value;
        // The uncapped kind-186 drawer, keyed on the track alone. It is the preferred list — NPV's capped ten rows are
        // the fallback for a track the wire has no 186 for, and for offline.
        var creditsL = UseResource(ct => LoadCreditsAsync(_svc, _trackUri, ct), (TrackCredits?)null, _trackUri).Loadable;
        var full = creditsL.Value.Value;

        Element body =
            full is { Credits.Count: > 0 } ? CreditsList(full.Credits, full.Sources, _go)
            : info?.Credits is { Count: > 0 } credits ? CreditsList(credits, info.CreditSources, _go)
            // Both reads have to be done before "no credits" is the truth — 186 answering late must not flash an empty
            // drawer first.
            : state == LoadState.Pending || (LoadState)creditsL.State.Value == LoadState.Pending ? Loading()
            : new TextEl(Loc.Get(Strings.Menu.NoCredits)) { Size = 13f, Color = Tok.TextSecondary };

        return new BoxEl { Direction = 1, MinWidth = 360f, MaxWidth = 440f, Children = [body] };
    }

    static async Task<TrackNpvInfo?> LoadAsync(Services svc, string artistUri, string trackUri, CancellationToken ct)
    {
        try { return (await svc.AlbumEnrichment.GetNowPlayingInfoAsync(artistUri, trackUri, ct).ConfigureAwait(false))?.Track; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return null; }
    }

    static async Task<TrackCredits?> LoadCreditsAsync(Services svc, string trackUri, CancellationToken ct)
    {
        if (trackUri.Length == 0) return null;
        try { return await svc.TrackCredits.GetAsync(trackUri, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return null; }
    }

    // NowPlayingPanel.Credits, verbatim in shape: group by RoleGroup (else Role), a caps header per group, one row per
    // contributor, then the source line. Wrapped in a bounded scroll so a long liner-note credit list stays inside the modal.
    static Element CreditsList(IReadOnlyList<TrackCredit> credits, IReadOnlyList<string> sources, Action<string, string?>? go)
    {
        var kids = new List<Element>(credits.Count + 3);
        foreach (var group in credits.GroupBy(c => string.IsNullOrWhiteSpace(c.RoleGroup) ? c.Role : c.RoleGroup!))
        {
            if (!string.IsNullOrWhiteSpace(group.Key))
                // The role-group name arrives from the server ("Performers", "Songwriters", …) — its own casing is the
                // right casing, and 10.5/750 was a rung of nobody's ramp.
                kids.Add(WaveeType.Eyebrow(group.Key!) with { Color = Tok.TextTertiary });
            foreach (var c in group) kids.Add(CreditRow(c, go));
        }
        if (sources.Count > 0)
            kids.Add(new TextEl(Strings.Player.CreditsSource(string.Join(", ", sources)))
            {
                Size = 11f, Color = Tok.TextTertiary, Wrap = TextWrap.Wrap, MaxLines = 2, Trim = TextTrim.CharacterEllipsis,
            });

        return ScrollView(new BoxEl { Direction = 1, Gap = 8f, Children = kids.ToArray() }) with { MaxHeight = 420f };
    }

    static Element CreditRow(TrackCredit c, Action<string, string?>? go)
    {
        Element name = c.Linkable && c.ArtistUri is { Length: > 0 } uri && go is not null
            ? new SpanTextEl([new TextSpan(c.Name, OnClick: () => go("artist:" + uri, c.Name))])
            {
                Size = 13f, Weight = 650, Color = Tok.AccentTextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
            }
            : new TextEl(c.Name) { Size = 13f, Weight = 650, Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis };
        return new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = 8f,
            Children =
            [
                new BoxEl { Grow = 1f, Basis = 0f, MinWidth = 0f, Children = [name] },
                string.IsNullOrWhiteSpace(c.Role)
                    ? new BoxEl()
                    : new TextEl(c.Role) { Size = 11f, Color = Tok.TextTertiary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
            ],
        };
    }

    static Element Loading() => new BoxEl
    {
        Direction = 1, Gap = 10f, Padding = new Edges4(0f, 8f, 0f, 8f),
        Children =
        [
            new BoxEl { Height = 12f, Width = 120f, Corners = CornerRadius4.All(4f), Fill = Tok.FillSubtleSecondary },
            new BoxEl { Height = 14f, Width = 220f, Corners = CornerRadius4.All(4f), Fill = Tok.FillSubtleSecondary },
            new BoxEl { Height = 14f, Width = 190f, Corners = CornerRadius4.All(4f), Fill = Tok.FillSubtleSecondary },
            new BoxEl { Height = 14f, Width = 210f, Corners = CornerRadius4.All(4f), Fill = Tok.FillSubtleSecondary },
        ],
    };
}
