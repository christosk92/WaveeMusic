using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Wavee.UI.Contracts;
using Wavee.UI.Helpers;
using Wavee.UI.WinUI.Extensions;
using Wavee.UI.WinUI.ViewModels;

namespace Wavee.UI.WinUI.ViewModels.Artist;

/// <summary>
/// Owns the "Fans also like" related-artists shelf. Extracted from
/// <c>ArtistViewModel</c>. Pure presentation — no remote fetches of its
/// own, the parent feeds it the related-artists list from
/// <see cref="ArtistOverviewResult.RelatedArtists"/> on every overview apply.
/// </summary>
public sealed partial class ArtistRelatedArtistsViewModel : ObservableObject, IDisposable
{
    private readonly ObservableCollection<RelatedArtistVm> _relatedArtists = [];

    /// <summary>Bound collection — mutated in place to keep ItemsRepeater
    /// stable across cache-served re-shows.</summary>
    public IReadOnlyList<RelatedArtistVm> RelatedArtists => _relatedArtists;

    public bool HasRelatedArtists => _relatedArtists.Count > 0;

    public void ApplyOverview(ArtistOverviewResult overview)
    {
        _relatedArtists.ReplaceWith(overview.RelatedArtists.Select(ra => new RelatedArtistVm
        {
            Id = ra.Id,
            Uri = ra.Uri,
            Name = ra.Name,
            ImageUrl = ra.ImageUrl
        }));
        OnPropertyChanged(nameof(HasRelatedArtists));
    }

    public void ResetForNewArtist()
    {
        _relatedArtists.Clear();
        OnPropertyChanged(nameof(HasRelatedArtists));
    }

    public void Dispose()
    {
        // No managed resources — disposal exists for parity with siblings.
    }
}
