using FluentAssertions;
using Wavee.UI.Services;
using Xunit;

namespace Wavee.UI.Tests.Services;

public sealed class LocalCreditsDetectorTests
{
    [Fact]
    public void BareCreditsAtIntro_UsesFallbackInsteadOfIntroChapter()
    {
        const long durationMs = 44 * 60_000;
        var chapters = new[]
        {
            new EpisodeChapter("Credits", 3 * 60_000 + 34_000),
        };

        var trigger = LocalCreditsDetector.GetTriggerMs(chapters, durationMs);

        trigger.Should().Be(durationMs - LocalCreditsDetector.FallbackPreEndMs);
    }

    [Fact]
    public void BareCreditsNearEnd_UsesChapter()
    {
        const long durationMs = 44 * 60_000;
        const long creditsStartMs = 42 * 60_000;

        var trigger = LocalCreditsDetector.GetTriggerMs(
            new[] { new EpisodeChapter("Credits", creditsStartMs) },
            durationMs);

        trigger.Should().Be(creditsStartMs);
    }

    [Fact]
    public void ExplicitEndCredits_UsesChapter()
    {
        const long durationMs = 44 * 60_000;
        const long creditsStartMs = 41 * 60_000;

        var trigger = LocalCreditsDetector.GetTriggerMs(
            new[] { new EpisodeChapter("End Credits", creditsStartMs) },
            durationMs);

        trigger.Should().Be(creditsStartMs);
    }

    [Fact]
    public void NoChapters_UsesFallback()
    {
        const long durationMs = 44 * 60_000;

        var trigger = LocalCreditsDetector.GetTriggerMs([], durationMs);

        trigger.Should().Be(durationMs - LocalCreditsDetector.FallbackPreEndMs);
    }

    [Fact]
    public void InvalidDuration_ReturnsNull()
    {
        var trigger = LocalCreditsDetector.GetTriggerMs([], 0);

        trigger.Should().BeNull();
    }
}
