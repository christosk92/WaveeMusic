using FluentAssertions;
using Wavee.Connect.Playback;
using Wavee.Connect.Playback.Decoders;
using Wavee.Connect.Playback.Processors;
using Wavee.Connect.Playback.Sinks;
using Wavee.Core.Audio;
using Xunit;

namespace Wavee.Tests.Connect.Playback;

/// <summary>
/// Tests for AudioPipelineFactory.
/// Validates factory creation of audio pipeline components.
/// </summary>
public class AudioPipelineFactoryTests
{
    [Fact]
    public void CreateDecoderRegistry_ContainsVorbisDecoder()
    {
        // ============================================================
        // WHY: Decoder registry should include Vorbis decoder for Spotify audio.
        // ============================================================

        // Act
        var registry = AudioPipelineFactory.CreateDecoderRegistry();

        // Assert
        registry.Should().NotBeNull();
        // The registry should have at least one decoder
        registry.FindDecoder(CreateMockOggStream()).Should().NotBeNull(
            "Registry should have a decoder that can handle OGG streams");
    }

    [Fact]
    public void CreateProcessingChain_NormalizationEnabled_HasNormalizationProcessor()
    {
        // ============================================================
        // WHY: When normalization is enabled, chain should include it.
        // ============================================================

        // Arrange
        var options = new AudioPipelineOptions
        {
            EnableNormalization = true,
            NormalizationTargetLufs = -14f
        };

        // Act
        var chain = AudioPipelineFactory.CreateProcessingChain(options);

        // Assert
        chain.Should().NotBeNull();
        // Chain should have processors
        var hasNormalization = chain.Processors.Any(p => p is NormalizationProcessor);
        hasNormalization.Should().BeTrue("Normalization should be in chain when enabled");
    }

    [Fact]
    public void CreateProcessingChain_NormalizationDisabled_NoNormalizationProcessor()
    {
        // ============================================================
        // WHY: When normalization is disabled, it should not be in chain.
        // ============================================================

        // Arrange
        var options = new AudioPipelineOptions
        {
            EnableNormalization = false
        };

        // Act
        var chain = AudioPipelineFactory.CreateProcessingChain(options);

        // Assert
        var hasNormalization = chain.Processors.Any(p => p is NormalizationProcessor);
        hasNormalization.Should().BeFalse("Normalization should not be in chain when disabled");
    }

    [Fact]
    public void CreateProcessingChain_EqualizerEnabled_HasEqualizerProcessor()
    {
        // ============================================================
        // WHY: When equalizer is enabled, chain should include it.
        // ============================================================

        // Arrange
        var options = new AudioPipelineOptions
        {
            EnableEqualizer = true
        };

        // Act
        var chain = AudioPipelineFactory.CreateProcessingChain(options);

        // Assert
        var hasEqualizer = chain.Processors.Any(p => p is EqualizerProcessor);
        hasEqualizer.Should().BeTrue("Equalizer should be in chain when enabled");
    }

    [Fact]
    public void CreateProcessingChain_EqualizerDisabled_NoEqualizerProcessor()
    {
        // ============================================================
        // WHY: When equalizer is disabled, it should not be in chain.
        // ============================================================

        // Arrange
        var options = new AudioPipelineOptions
        {
            EnableEqualizer = false
        };

        // Act
        var chain = AudioPipelineFactory.CreateProcessingChain(options);

        // Assert
        var hasEqualizer = chain.Processors.Any(p => p is EqualizerProcessor);
        hasEqualizer.Should().BeFalse("Equalizer should not be in chain when disabled");
    }

    [Fact]
    public void CreateProcessingChain_AlwaysHasVolumeProcessor()
    {
        // ============================================================
        // WHY: Volume processor should always be in the chain.
        // ============================================================

        // Arrange
        var options = new AudioPipelineOptions
        {
            EnableNormalization = false,
            EnableEqualizer = false
        };

        // Act
        var chain = AudioPipelineFactory.CreateProcessingChain(options);

        // Assert
        var hasVolume = chain.Processors.Any(p => p is VolumeProcessor);
        hasVolume.Should().BeTrue("Volume processor should always be present");
    }

    [Fact]
    public void CreateProcessingChain_VolumeProcessorHasInitialVolume()
    {
        // ============================================================
        // WHY: Volume processor should be initialized with specified volume.
        // ============================================================

        // Arrange
        var options = new AudioPipelineOptions
        {
            InitialVolume = 0.75f
        };

        // Act
        var chain = AudioPipelineFactory.CreateProcessingChain(options);

        // Assert
        var volumeProcessor = chain.Processors.OfType<VolumeProcessor>().FirstOrDefault();
        volumeProcessor.Should().NotBeNull();
        volumeProcessor!.Volume.Should().BeApproximately(0.75f, 0.001f);
    }

    [Fact]
    public void CreateAudioSink_PortAudio_ReturnsPortAudioSink()
    {
        // ============================================================
        // WHY: PortAudio sink should be returned when requested.
        // ============================================================

        // Act
        var sink = AudioPipelineFactory.CreateAudioSink(AudioSinkType.PortAudio);

        // Assert
        sink.Should().BeOfType<PortAudioSink>();
        sink.SinkName.Should().Be("PortAudio");
    }

    [Fact]
    public void CreateAudioSink_Stub_ReturnsStub()
    {
        // ============================================================
        // WHY: Stub sink should be returned when requested.
        // ============================================================

        // Act
        var sink = AudioPipelineFactory.CreateAudioSink(AudioSinkType.Stub);

        // Assert
        sink.Should().BeOfType<StubAudioSink>();
        sink.SinkName.Should().Be("Stub");
    }

    [Fact]
    public void CreateProcessingChain_DefaultOptions_CreatesValidChain()
    {
        // ============================================================
        // WHY: Default options should create a working processing chain.
        // ============================================================

        // Act
        var chain = AudioPipelineFactory.CreateProcessingChain();

        // Assert
        chain.Should().NotBeNull();
        chain.Processors.Should().NotBeEmpty("Default chain should have processors");
    }

    [Fact]
    public void CreateProcessingChain_NullOptions_UsesDefaults()
    {
        // ============================================================
        // WHY: Null options should use default values.
        // ============================================================

        // Act
        var chain = AudioPipelineFactory.CreateProcessingChain(null);

        // Assert
        chain.Should().NotBeNull();
        // Should have normalization (enabled by default) and volume
        chain.Processors.Should().HaveCountGreaterOrEqualTo(2);
    }

    private static Stream CreateMockOggStream()
    {
        // Create a minimal stream that looks like it could be OGG
        // (just enough for decoder detection)
        var data = new byte[200];
        // OGG magic at offset 0xa7 (167)
        data[167] = 0x4F; // 'O'
        data[168] = 0x67; // 'g'
        data[169] = 0x67; // 'g'
        data[170] = 0x53; // 'S'
        return new MemoryStream(data);
    }
}

/// <summary>
/// Tests for AudioPipelineOptions record.
/// </summary>
public class AudioPipelineOptionsTests
{
    [Fact]
    public void Default_HasExpectedValues()
    {
        // ============================================================
        // WHY: Default options should have sensible values.
        // ============================================================

        // Act
        var options = AudioPipelineOptions.Default;

        // Assert
        options.PreferredQuality.Should().Be(AudioQuality.VeryHigh);
        options.EnableCaching.Should().BeTrue();
        options.EnableNormalization.Should().BeTrue();
        options.NormalizationTargetLufs.Should().Be(-14f);
        options.EnableEqualizer.Should().BeFalse();
        options.InitialVolume.Should().Be(1.0f);
    }

    [Fact]
    public void Default_AudioSinkType_IsPortAudio()
    {
        // ============================================================
        // WHY: Default sink type should be PortAudio (cross-platform).
        // ============================================================

        // Act
        var options = AudioPipelineOptions.Default;

        // Assert - PortAudio is the cross-platform default
        options.AudioSinkType.Should().Be(AudioSinkType.PortAudio);
    }

    [Fact]
    public void WithInitializer_OverridesValues()
    {
        // ============================================================
        // WHY: Record with-expressions should override specific values.
        // ============================================================

        // Act
        var options = AudioPipelineOptions.Default with
        {
            PreferredQuality = AudioQuality.Normal,
            EnableNormalization = false,
            InitialVolume = 0.5f
        };

        // Assert
        options.PreferredQuality.Should().Be(AudioQuality.Normal);
        options.EnableNormalization.Should().BeFalse();
        options.InitialVolume.Should().Be(0.5f);
        // Other values should remain default
        options.EnableCaching.Should().BeTrue();
    }

    [Fact]
    public void Default_IsSingleton()
    {
        // ============================================================
        // WHY: Default property should return the same instance.
        // ============================================================

        // Act
        var default1 = AudioPipelineOptions.Default;
        var default2 = AudioPipelineOptions.Default;

        // Assert
        ReferenceEquals(default1, default2).Should().BeTrue();
    }
}
