using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Wavee.UI.Contracts;
using Wavee.UI.WinUI.Data.Contexts;
using Wavee.UI.WinUI.Data.Parameters;

namespace Wavee.UI.WinUI.ViewModels;

/// <summary>
/// ViewModel for <c>ArtistDiscographyPage</c> — the "See all albums / singles"
/// destination opened from the capped grids on <c>ArtistPage</c>. Self-
/// contained: paginates its own group via
/// <see cref="ArtistService.GetDiscographyPageAsync"/> so the page works
/// independently of whatever <see cref="ArtistViewModel"/> happens to be
/// loaded (the latter is registered as transient, so the singleton-sharing
/// shortcut wouldn't be reliable for deep-link / tab-restore entry).
/// Each Pathfinder page response is cached one layer down, so the second
/// visit to the same group is a sequence of cache hits.
/// </summary>
[global::WinRT.GeneratedBindableCustomProperty]
public sealed partial class ArtistDiscographyPageViewModel : ObservableObject
{
    private const int PageSize = 50;

    private readonly IArtistService _artistService;
    private readonly ILogger<ArtistDiscographyPageViewModel>? _logger;

    private CancellationTokenSource? _loadCts;
    private int _loadRevision;

    [ObservableProperty] public partial string? ArtistUri { get; set; }
    [ObservableProperty] public partial string? ArtistName { get; set; }
    [ObservableProperty] public partial string? ArtistImageUrl { get; set; }
    [ObservableProperty] public partial ArtistDiscographyGroupKind GroupKind { get; set; } = ArtistDiscographyGroupKind.Albums;
    [ObservableProperty] public partial bool IsLoading { get; set; }

    private readonly ObservableCollection<LazyReleaseItem> _items = [];
    public IReadOnlyList<LazyReleaseItem> Items => _items;

    public ArtistDiscographyPageViewModel(
        IArtistService artistService,
        ILogger<ArtistDiscographyPageViewModel>? logger = null)
    {
        _artistService = artistService ?? throw new ArgumentNullException(nameof(artistService));
        _logger = logger;
    }

    /// <summary>
    /// Display label for the trailing breadcrumb crumb: "Albums" / "Singles".
    /// </summary>
    public string GroupLabel => GroupKind == ArtistDiscographyGroupKind.Albums ? "Albums" : "Singles";

    /// <summary>
    /// Entry point called from <c>ArtistDiscographyPage.OnNavigatedTo</c>
    /// (and <c>RefreshWithParameter</c>). Cancels any in-flight pagination
    /// for the previous parameter, clears the bound collection, and starts
    /// a fresh paginated fetch.
    /// </summary>
    public void Initialize(ArtistDiscographyNavigationParameter parameter)
    {
        ArgumentNullException.ThrowIfNull(parameter);

        ArtistUri = parameter.ArtistUri;
        ArtistName = parameter.ArtistName;
        ArtistImageUrl = parameter.ArtistImageUrl;
        GroupKind = parameter.GroupKind;

        // Cancel any in-flight pagination from a prior Initialize, then kick
        // off a new one. Revision counter lets each LoadAllAsync bail when
        // a follow-on Initialize starts before it completes.
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = new CancellationTokenSource();
        var revision = ++_loadRevision;
        _items.Clear();
        IsLoading = true;
        _ = LoadAllAsync(revision, _loadCts.Token);
    }

    partial void OnGroupKindChanged(ArtistDiscographyGroupKind value)
        => OnPropertyChanged(nameof(GroupLabel));

    private async Task LoadAllAsync(int revision, CancellationToken ct)
    {
        var artistUri = ArtistUri;
        if (string.IsNullOrWhiteSpace(artistUri))
        {
            IsLoading = false;
            return;
        }

        var type = GroupKind == ArtistDiscographyGroupKind.Albums ? "ALBUM" : "SINGLE";
        try
        {
            var offset = 0;
            var added = 0;
            while (!ct.IsCancellationRequested)
            {
                var page = await _artistService
                    .GetDiscographyPageAsync(artistUri, type, offset, PageSize, ct)
                    .ConfigureAwait(true);

                if (ct.IsCancellationRequested || revision != _loadRevision) return;
                if (page is null || page.Count == 0) break;

                foreach (var r in page)
                {
                    if (ct.IsCancellationRequested) return;
                    var vm = new ArtistReleaseVm
                    {
                        Id = r.Id,
                        Uri = r.Uri,
                        Name = r.Name,
                        Type = type,
                        ImageUrl = r.ImageUrl,
                        ReleaseDate = r.ReleaseDate,
                        TrackCount = r.TrackCount,
                        Label = r.Label,
                        Year = r.Year,
                    };
                    _items.Add(LazyReleaseItem.Loaded(r.Id, added, vm));
                    added++;
                }

                if (page.Count < PageSize) break;
                offset += page.Count;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Superseded — newer Initialize ran while we were paginating.
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex,
                "Discography pagination failed for {Artist} group={Group}",
                artistUri, type);
        }
        finally
        {
            if (revision == _loadRevision)
                IsLoading = false;
        }
    }
}
