using System.Threading;
using System.Threading.Tasks;
using Wavee.Core;

namespace Wavee;

// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────
// THE ONE PLACE the engine's seek fidelity meets the app's transport seek fidelity.
//
// There are deliberately two SeekMode enums and they may not be merged:
//   • FluentGpu.Media.SeekMode  — the ENGINE's, what MediaSeekBar/MediaPlayerElement raise and what IMediaPlayer.SeekAsync takes.
//   • Wavee.Core.SeekMode       — the APP's transport enum, what IPlaybackPlayer/IMediaHost speak. Wavee.Core is
//     framework-neutral by construction (zero ProjectReferences) and src/apps/Wavee/Backend is source-included into
//     Wavee.Tests, which carries no FluentGpu reference — so neither can name the engine type at all.
//
// This file lives in the app assembly, the only one that can see both, and is the single mapping between them. Its
// namespace is `Wavee`, an ENCLOSING namespace of every `Wavee.Features.*` surface, so the extension below resolves at
// those call sites with no using directive.
// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>Maps the engine's seek fidelity onto the app's, and offers the seek verb in the engine's vocabulary so a UI
/// surface holding an <c>Action&lt;TimeSpan, FluentGpu.Media.SeekMode&gt;</c> can forward it straight to the player seam.</summary>
public static class MediaSeekInterop
{
    /// <summary>The engine's seek fidelity as the app's transport fidelity.</summary>
    public static SeekMode ToTransport(this FluentGpu.Media.SeekMode mode) =>
        mode == FluentGpu.Media.SeekMode.Keyframe ? SeekMode.Keyframe : SeekMode.Accurate;

    /// <summary>Seek the current media from a UI surface that speaks the ENGINE's <c>SeekMode</c> (a
    /// <c>MediaPlayerElement.SeekRequested</c> / <c>MediaSeekBar.SeekRequested</c> callback). Delegates to
    /// <see cref="IPlaybackPlayer.SeekAsync(long, SeekMode, CancellationToken)"/> — the routing (preview vs commit,
    /// local vs remote) lives there, not here.</summary>
    public static Task SeekAsync(this IPlaybackPlayer player, long positionMs, FluentGpu.Media.SeekMode mode,
        CancellationToken ct = default) => player.SeekAsync(positionMs, mode.ToTransport(), ct);
}
