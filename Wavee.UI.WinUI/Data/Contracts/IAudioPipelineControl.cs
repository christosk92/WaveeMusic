using System.Threading;
using System.Threading.Tasks;
using Wavee.Core.Audio;

namespace Wavee.UI.WinUI.Data.Contracts;

/// <summary>
/// Abstraction for live audio pipeline adjustments (quality switching, normalization).
/// Decouples ViewModels from concrete <c>AudioPipeline</c> / <c>ConnectCommandExecutor</c> types.
/// </summary>
public interface IAudioPipelineControl
{
    Task SwitchQualityAsync(AudioQuality quality, CancellationToken ct = default);
    void SetNormalizationEnabled(bool enabled);
}
