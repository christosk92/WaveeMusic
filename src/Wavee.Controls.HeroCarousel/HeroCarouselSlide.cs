using System;
using Windows.UI;
using Color = Windows.UI.Color;

namespace Wavee.Controls.HeroCarousel;

public sealed class HeroCarouselSlide
{
    public object? Image { get; set; }

    public Uri ImageUri { get; set; } = new("https://picsum.photos/id/1018/2400/1400");

    public string? Tag { get; set; }

    public string Title { get; set; } = string.Empty;

    public string Subtitle { get; set; } = string.Empty;

    public string CtaText { get; set; } = string.Empty;

    public Color AccentColor { get; set; } = Color.FromArgb(0, 0, 0, 0);

    public Color GlowColor { get; set; } = Color.FromArgb(0, 0, 0, 0);

    public bool UseDarkText { get; set; }

    public bool UseScrim { get; set; } = true;

    public bool CtaUsesGlass { get; set; } = true;

    public HeroCarouselRating? Rating { get; set; }

    public string? Eyebrow { get; set; }

    public HeroCarouselCtaStyle CtaStyle { get; set; } = HeroCarouselCtaStyle.Filled;

    public Color CozyTintColor { get; set; }
}

public sealed class HeroCarouselRating
{
    public int Age { get; set; }

    public Color Color { get; set; } = Color.FromArgb(255, 0, 166, 81);

    public string Text { get; set; } = string.Empty;
}
