using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Wavee.Core;
using Wavee.Core.Catalog;

namespace Wavee;

/// <summary>How the track projection reads one publication's KNOWLEDGE. Two shapes, one contract:
/// <see cref="PublicationTrackFacts"/> reads a query publication's fact map, and <see cref="Delegated"/> answers from
/// plain predicates (the no-publication fallback, and the headless tests).
/// <para>The scope is the reason this is a type rather than two delegates: a fact key is
/// <c>(CatalogScope, uri, facet)</c>, and <c>scope with { Provider = … }</c> per lookup allocated a fresh
/// <see cref="CatalogScope"/> for every one of the ~9,000 probes a 1,494-row projection used to take — and, worse, made
/// every dictionary probe compare nine strings instead of taking the record's reference-equality shortcut. One scope
/// instance per PROVIDER is built here and reused, so a lookup is a hash probe and nothing else.</para>
/// <para>Instances are shared across the projection pool worker and the UI thread (the rows read their own
/// publication's facts), so every member must stay thread-safe.</para></summary>
public abstract class TrackFacts
{
    /// <summary>No publication to answer from: everything counts as KNOWN (an absent publication must never relax a
    /// filter the user set — that is what the loading list already shows) and nothing has a video.</summary>
    public static TrackFacts Unknown { get; } = Delegated(static (_, _) => true, static _ => false);

    /// <summary>Is this subject's facet KNOWN — present, absent or unsupported, but no longer unanswered?</summary>
    public abstract bool Known(string uri, FacetKind facet);

    /// <summary>The one has-video answer for a row, from this publication plus the planes that do not travel on it
    /// (a user attachment, a playback module's own verdict).</summary>
    public abstract bool HasVideo(string uri);

    /// <summary>The cached scope a resource key for this subject is built with, or null when there is no catalog scope
    /// to build one from. Callers that read the publication's RESOURCES (activity, errors) key with this.</summary>
    public abstract CatalogScope? ScopeFor(string uri);

    public static TrackFacts Delegated(Func<string, FacetKind, bool> known, Func<string, bool> hasVideo)
        => new DelegatedTrackFacts(known, hasVideo);
}

sealed class DelegatedTrackFacts(Func<string, FacetKind, bool> known, Func<string, bool> hasVideo) : TrackFacts
{
    public override bool Known(string uri, FacetKind facet) => known(uri, facet);
    public override bool HasVideo(string uri) => hasVideo(uri);
    public override CatalogScope? ScopeFor(string uri) => null;
}

/// <summary>One query publication's facts, read through per-provider cached scopes.</summary>
public sealed class PublicationTrackFacts : TrackFacts
{
    readonly IReadOnlyDictionary<ResourceKey, QueryFact> _facts;
    readonly CatalogScope _scope;
    readonly Func<string, string> _providerForSubject;
    readonly Func<string, bool> _videoBeyondFacts;
    // Provider → the scope instance every key for that provider is built from. Tiny (one or two entries) and
    // concurrent because the pool projection and the realized rows read the same instance.
    readonly ConcurrentDictionary<string, CatalogScope> _scopes = new(StringComparer.Ordinal);

    /// <param name="videoBeyondFacts">The has-video planes that do NOT travel on a publication — the user's own
    /// attachment and a playback module's <c>form: video</c> verdict. Injected so this type stays free of the app's
    /// service statics (it is unit-tested headlessly).</param>
    public PublicationTrackFacts(IReadOnlyDictionary<ResourceKey, QueryFact> facts, CatalogScope scope,
        Func<string, string> providerForSubject, Func<string, bool> videoBeyondFacts)
    {
        _facts = facts ?? throw new ArgumentNullException(nameof(facts));
        _scope = scope ?? throw new ArgumentNullException(nameof(scope));
        _providerForSubject = providerForSubject ?? throw new ArgumentNullException(nameof(providerForSubject));
        _videoBeyondFacts = videoBeyondFacts ?? throw new ArgumentNullException(nameof(videoBeyondFacts));
    }

    public override CatalogScope? ScopeFor(string uri) => Scope(uri);

    CatalogScope Scope(string uri)
    {
        string provider = _providerForSubject(uri);
        if (provider.Length == 0 || string.Equals(provider, _scope.Provider, StringComparison.Ordinal)) return _scope;
        return _scopes.GetOrAdd(provider, static (name, baseScope) => baseScope with { Provider = name }, _scope);
    }

    public override bool Known(string uri, FacetKind facet)
        => _facts.TryGetValue(new ResourceKey(Scope(uri), uri, facet), out var fact) && fact.Knowledge != Knowledge.Unknown;

    public override bool HasVideo(string uri)
        => (_facts.TryGetValue(new ResourceKey(Scope(uri), uri, FacetKind.VideoAssociation), out var fact)
            && fact.Value is VideoAssociationValue { Association.HasVideo: true })
           || _videoBeyondFacts(uri);
}

/// <summary>The exact source membership travels with its display-to-source map, including duplicate occurrences.</summary>
public sealed record DetailTrackProjection(IReadOnlyList<Track> Tracks, int[] Indices, string? TopTrackId)
{
    /// <summary>Filter + sort one whole membership into a display→source index map.
    /// <para>KNOWLEDGE IS READ ONLY WHERE IT CHANGES THE ANSWER. Each <c>Known</c> probe below exists to RELAX a filter
    /// whose fact has not landed yet — so when that filter is already at its default the probe cannot change a single
    /// row and is not taken at all. A resting list (no query, no filters) therefore costs ZERO fact lookups instead of
    /// the five-to-seven per track it used to pay on every one of the ~20 publications a cold playlist open produces.
    /// The upper bound is <c>tracks.Count × (the facets the active filters actually consult)</c>.</para></summary>
    public static DetailTrackProjection Build(IReadOnlyList<Track> tracks, DetailTrackSort sort, string query,
        TrackFilterState filters, IReadOnlySet<string>? saved, TrackFacts facts, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(facts);
        // Resting playlist (context order, no query, default filters): the display map IS 0..n-1. Building a List,
        // sorting it, and ToArray() on every 300-subject catalog batch was O(n) alloc on the projection worker for
        // an answer that cannot move until sort/filter/query does.
        if (query.Length == 0 && sort.Column == SortColumn.Index && filters.Equals(TrackFilterState.Default) && saved is null)
        {
            var identity = IdentityIndices(tracks.Count);
            int topPlay = -1;
            for (int i = 0; i < tracks.Count; i++)
                if (tracks[i].PlayCount > 0 && (topPlay < 0 || tracks[i].PlayCount > tracks[topPlay].PlayCount)) topPlay = i;
            return new(tracks, identity, topPlay >= 0 ? tracks[topPlay].Id : null);
        }
        // Which facets this (sort, query, filter) combination can actually be changed by. Everything else is skipped.
        bool needsIdentity = query.Length > 0
            || filters.ExplicitMode != TrackTraitMode.All || filters.Duration != TrackDurationRange.Any
            || filters.ArtistId is { Length: > 0 } || filters.ReleaseYearMin > 0 || filters.ReleaseYearMax > 0
            || filters.Origin != TrackOriginFilter.Any;
        bool needsVideo = filters.VideoMode != TrackTraitMode.All;
        bool needsAudio = filters.Tempo != TrackTempoBand.Any || filters.CamelotCode is { Length: > 0 };
        bool needsDescriptors = filters.Tag is { Length: > 0 };
        bool needsAvailability = (filters.Flags & TrackFilterFlags.PlayableOnly) != 0;
        // One delegate for the whole pass, not one per track (QueryFieldsKnown takes the probe as a function).
        Func<string, FacetKind, bool> known = facts.Known;

        var indices = new List<int>(tracks.Count);
        int best = -1;
        // Artist labels are materialized at most once per member, not once per comparison.
        string[]? artists = sort.Column == SortColumn.Artist ? new string[tracks.Count] : null;
        for (int i = 0; i < tracks.Count; i++)
        {
            var track = tracks[i];
            if (track.PlayCount > 0 && (best < 0 || track.PlayCount > tracks[best].PlayCount)) best = i;
            if (artists is not null) artists[i] = string.Join(", ", track.Artists.Select(artist => artist.Name));
            var effective = filters;
            string effectiveQuery = query;
            if (query.Length > 0 && !TrackFilterModel.QueryFieldsKnown(track, filters.SearchScope, known)) effectiveQuery = "";
            if (needsIdentity)
            {
                var identity = EntityUri.KindOf(track.Uri) == EntityKind.Episode ? FacetKind.EpisodeIdentity : FacetKind.TrackIdentity;
                if (!known(track.Uri, identity))
                {
                    effectiveQuery = "";
                    effective = effective with { ExplicitMode = TrackTraitMode.All, Duration = TrackDurationRange.Any,
                        ArtistId = null, ReleaseYearMin = 0, ReleaseYearMax = 0, Origin = TrackOriginFilter.Any };
                }
            }
            if (needsVideo && !known(track.Uri, FacetKind.VideoAssociation)) effective = effective with { VideoMode = TrackTraitMode.All };
            if (needsAudio && !known(track.Uri, FacetKind.AudioAttributes)) effective = effective with { Tempo = TrackTempoBand.Any, CamelotCode = null };
            if (needsDescriptors && !known(track.Uri, FacetKind.Descriptors)) effective = effective with { Tag = null };
            if (needsAvailability && !known(track.Uri, FacetKind.Availability)) effective = effective with { Flags = effective.Flags & ~TrackFilterFlags.PlayableOnly };
            // The has-video answer is consulted by exactly one rule (the Videos-only / hide-videos trait), so a list
            // that is not filtering on it never asks: a rendered row gets its film glyph from its own projected
            // RowPresentation, not from this pass.
            if (TrackFilterModel.Matches(track, effectiveQuery, in effective,
                needsVideo && facts.HasVideo(track.Uri), saved?.Contains(track.Uri) ?? false, now)) indices.Add(i);
        }
        Comparison<int> compare = sort.Column switch
        {
            SortColumn.Title => (a, b) => string.Compare(tracks[a].Title, tracks[b].Title, StringComparison.OrdinalIgnoreCase),
            SortColumn.Album => (a, b) => string.Compare(tracks[a].Album.Name, tracks[b].Album.Name, StringComparison.OrdinalIgnoreCase),
            SortColumn.Duration => (a, b) => tracks[a].DurationMs.CompareTo(tracks[b].DurationMs),
            SortColumn.Artist => (a, b) => string.Compare(artists![a], artists[b], StringComparison.OrdinalIgnoreCase),
            SortColumn.DateAdded => (a, b) => Nullable.Compare(tracks[a].AddedAt, tracks[b].AddedAt),
            SortColumn.Plays => (a, b) => tracks[a].PlayCount.CompareTo(tracks[b].PlayCount),
            _ => (a, b) => a.CompareTo(b),
        };
        indices.Sort((a, b) =>
        {
            int comparison = sort.Descending ? compare(b, a) : compare(a, b);
            return comparison != 0 ? comparison : a.CompareTo(b);
        });
        return new(tracks, indices.ToArray(), best >= 0 ? tracks[best].Id : null);
    }

    static readonly ConcurrentDictionary<int, int[]> IdentityByCount = new();
    static int[] IdentityIndices(int count)
        => IdentityByCount.GetOrAdd(count, static n =>
        {
            var map = new int[n];
            for (int i = 0; i < n; i++) map[i] = i;
            return map;
        });
}
