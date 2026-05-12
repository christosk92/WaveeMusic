using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Wavee.UI.WinUI.Services;

/// <inheritdoc cref="INowPlayingPresentationService"/>
public sealed partial class NowPlayingPresentationService :
    ObservableObject, INowPlayingPresentationService
{
    private ILogger? _logger;
    private ILogger? Logger => _logger ??= Ioc.Default
        .GetService<ILoggerFactory>()?.CreateLogger<NowPlayingPresentationService>();

    [ObservableProperty]
    private NowPlayingPresentation _presentation = NowPlayingPresentation.Normal;

    public bool IsExpanded => Presentation != NowPlayingPresentation.Normal;
    public bool IsNormal => Presentation == NowPlayingPresentation.Normal;

    partial void OnPresentationChanged(NowPlayingPresentation oldValue, NowPlayingPresentation newValue)
    {
        Logger?.LogInformation("[presentation] {Old} → {New}", oldValue, newValue);
        OnPropertyChanged(nameof(IsExpanded));
        OnPropertyChanged(nameof(IsNormal));
    }

    public void EnterTheatre() => Presentation = NowPlayingPresentation.Theatre;
    public void EnterFullscreen() => Presentation = NowPlayingPresentation.Fullscreen;
    public void ExitToNormal() => Presentation = NowPlayingPresentation.Normal;

    public void ToggleFullscreen()
        => Presentation = Presentation == NowPlayingPresentation.Fullscreen
            ? NowPlayingPresentation.Normal
            : NowPlayingPresentation.Fullscreen;

    public void ToggleTheatre()
        => Presentation = Presentation == NowPlayingPresentation.Theatre
            ? NowPlayingPresentation.Normal
            : NowPlayingPresentation.Theatre;
}
