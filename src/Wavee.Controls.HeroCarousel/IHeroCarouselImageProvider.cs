using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Wavee.Controls.HeroCarousel;

public interface IHeroCarouselImageProvider
{
    object? GetCacheKey(object? item, object? image);

    ValueTask<ImageSource?> LoadAsync(object? item, object? image, CancellationToken cancellationToken);
}

public sealed class DefaultHeroCarouselImageProvider : IHeroCarouselImageProvider
{
    public static DefaultHeroCarouselImageProvider Instance { get; } = new();

    private DefaultHeroCarouselImageProvider()
    {
    }

    public object? GetCacheKey(object? item, object? image)
    {
        return image switch
        {
            ImageSource source => source,
            Uri uri => uri,
            string text when Uri.TryCreate(text, UriKind.RelativeOrAbsolute, out Uri? uri) => uri,
            string text => text,
            null => item,
            _ => image,
        };
    }

    public ValueTask<ImageSource?> LoadAsync(object? item, object? image, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return image switch
        {
            ImageSource source => new ValueTask<ImageSource?>(source),
            Uri uri => new ValueTask<ImageSource?>(new BitmapImage(uri)),
            string text when Uri.TryCreate(text, UriKind.RelativeOrAbsolute, out Uri? uri) => new ValueTask<ImageSource?>(new BitmapImage(uri)),
            _ => new ValueTask<ImageSource?>((ImageSource?)null),
        };
    }
}
