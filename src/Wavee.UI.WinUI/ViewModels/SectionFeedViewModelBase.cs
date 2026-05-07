using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Klankhuis.Hero.Controls;
using Microsoft.UI.Xaml.Media;

namespace Wavee.UI.WinUI.ViewModels;

/// <summary>
/// Shared observable surface for any "header band + hero carousel + section
/// shelves" page. <see cref="HomeViewModel"/> intentionally does NOT inherit
/// from this — it owns chips, regions, baseline enrichment, recents, etc.,
/// which would clutter the base. New pages with the simple feed shape
/// (Browse, future Search/Genre/Discovery destinations) inherit here and
/// only implement <see cref="ReloadAsync"/> to do their fetch + map.
/// </summary>
public abstract partial class SectionFeedViewModelBase : ObservableObject, IDisposable
{
    [ObservableProperty]
    private string? _title;

    [ObservableProperty]
    private string? _subtitle;

    /// <summary>Drives the page's header backdrop (typically a vertical
    /// gradient tinted with the response's accent colour).</summary>
    [ObservableProperty]
    private Brush? _headerBackdropBrush;

    /// <summary>The carousel slides. Reassign on each rebuild — Klankhuis's
    /// HeroCarousel tracks both DP-change and collection-change after Phase 8,
    /// but reassigning is the simplest path for derived state.</summary>
    [ObservableProperty]
    private IList<HeroCarouselItem> _heroSlides = new List<HeroCarouselItem>();

    /// <summary>Section shelves below the hero. Mutated in place by the
    /// derived class so <c>SectionShelvesView</c>'s ItemsRepeater can recycle.</summary>
    public ObservableCollection<HomeSection> Sections { get; } = new();

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _hasError;

    [ObservableProperty]
    private string? _errorMessage;

    /// <summary>Implemented by the derived class — fetch from the service,
    /// map into <see cref="HeroSlides"/> + <see cref="Sections"/>, set
    /// <see cref="HeaderBackdropBrush"/> + <see cref="Title"/>. The base
    /// owns the observable state; subclasses own the data flow.</summary>
    public abstract Task ReloadAsync();

    public virtual void Dispose() { }
}
