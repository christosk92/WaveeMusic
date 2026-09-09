using Wavee.Backend.Queries;
using Wavee.Core.Catalog;
using Xunit;

namespace Wavee.Tests;

/// <summary>Findings 4.2's DEBUG/FLUENTGPU_DIAG acquire assert (<see cref="QueryService.Acquire{T}"/>) pulled the
/// predicate table out into <see cref="QueryScopeRules.IsStale"/> precisely so it can be pinned without the gate
/// itself (compiled out of Release, and only reachable through a live repository in DEBUG).</summary>
public sealed class QueryScopeRulesTests
{
    static readonly CatalogScope SpotifyOnline = new("spotify", "bob", "en", "US", "premium", 1, false);
    static readonly CatalogScope SpotifyPreLogin = SpotifyOnline with { ContextKnown = false };
    static readonly CatalogScope Local = new("local", "", "en", "US", "premium", 1, false);

    [Theory]
    // (scope, isActiveScope, expected IsStale)
    [MemberData(nameof(Cases))]
    public void Predicate(CatalogScope scope, bool isActiveScope, bool expected)
        => Assert.Equal(expected, QueryScopeRules.IsStale(scope, isActiveScope));

    public static TheoryData<CatalogScope, bool, bool> Cases() => new()
    {
        // An authenticated Spotify scope that is still the repository's active one: never stale.
        { SpotifyOnline, true, false },
        // The exact bug class this gate exists for — the repository moved on, this spec did not: stale.
        { SpotifyOnline, false, true },
        // A pre-login acquire (ContextKnown == false) is legitimate even though it can never BE the repository's
        // active scope — nothing has attempted to authenticate it against one yet.
        { SpotifyPreLogin, false, false },
        { SpotifyPreLogin, true, false },
        // Local (non-Spotify) facts are current before any Spotify session exists; never stale regardless of scope
        // activity.
        { Local, false, false },
        { Local, true, false },
    };
}
