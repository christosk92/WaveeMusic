// ── Entities/LocalLink.Frame.Page.cs ───────────────────────────────────────────────────────────────────────────────
// "Choose frame…" (#240 version cards): the picture a video of mine wears, picked by scrubbing it. A small dialog: the frame at the slider's
// time over its time; each settled position is grabbed once off the UI thread (LocalArt.MakeFrame: Media Foundation, Cache\art beside the
// stills), the newest answer wins. "Use this frame" keeps the choice (LocalLink.SetThumb, with the links), so the version's card, its song's video
// picture (the album's video card, the queue's still, the stage's poster) and the file's own row all wear it.
//
// Role: SHELL (UI thread; the grabs on the thread pool)

using FluentGpu;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using V = Wavee.Strings.Detail.Versions;

namespace Wavee;

public static partial class LocalLinkPage
{
    const float FrameW = 320f, FrameH = 180f, FrameGap = 12f;

    /// <summary>Open "Choose frame…" on a video file of mine. A no-op for anything else or with no overlay host.</summary>
    public static void OpenChooseFrame(long localId, IOverlayService? overlay)
    {
        if (overlay is null || Controls.IsNullOverlay(overlay) || !LocalLink.IsVideo(localId) || LocalLibrary.PathOf(localId) is not { } path
            || !LocalLibrary.Index.TryGet(localId, out LocalEntry? e)) return;
        long length = Math.Max(1000L, e.Tags.DurationMs);
        long chosen = LocalLink.ThumbOf(localId);
        long start = chosen > 0 ? chosen : length / 10;   // the video's own still is at a tenth of it (VideoFrame.At)
        var picked = new long[] { start };
        ContentDialog.Show(overlay, d =>
        {
            d.Title = Loc.Get(V.ChooseFrameTitle);
            d.PrimaryText = Loc.Get(V.UseThisFrame);
            d.CloseText = Loc.Get(Strings.Auth.Cancel);
            d.DefaultButton = ContentDialog.DefaultBtn.Primary;
            d.PrimaryClick = () => LocalLink.SetThumb(localId, Math.Max(1L, picked[0]));
            d.Content = Embed.Comp(new FrameProps(path, length, start, ms => picked[0] = ms), static () => new FramePicker()) with { Key = "frame-picker" };
            OverlayFit.Dialog(d, Controls.DialogWidthCompact);
        });
    }

    sealed record FrameProps(string Path, long LengthMs, long StartMs, Action<long> Picked);

    /// <summary>The frame at the slider's time, its time, the slider. A settled position is grabbed off the UI thread; a stale answer is dropped.</summary>
    sealed class FramePicker : Component
    {
        FloatSignal? _at;
        readonly Signal<string?> _frame = new(null);
        readonly Signal<string> _time = new("");
        int _asked;
        FrameProps? _p;
        Action<float>? _moved;

        public override Element Render()
        {
            var p = UseProps<FrameProps>();
            _p = p;
            if (_at is null)
            {
                _at = new FloatSignal(p.StartMs / 1000f);
                _moved = s => Ask((long)(s * 1000f));
                Ask(p.StartMs);
            }
            return new BoxEl
            {
                Direction = 1, Gap = FrameGap, AlignItems = FlexAlign.Center,
                Children =
                [
                    Embed.Comp(new FrameImageProps(_frame), static () => new FrameImage()) with { Key = "frame-image" },
                    new TextEl("") { Text = _time, Size = 12f, Color = Tok.TextSecondary },
                    Slider.Create(_at, _moved, new Slider.SliderOptions { Min = 0f, Max = p.LengthMs / 1000f, Step = 0.5f }, length: FrameW),
                ],
            };
        }

        void Ask(long ms)
        {
            if (_p is not { } p) return;
            ms = Math.Clamp(ms, 0L, p.LengthMs);
            p.Picked(ms);
            _time.Value = Track.Format.DurationCell(ms);
            int asked = ++_asked;
            string path = p.Path;
            // Grab after the pointer rests a moment: a drag across the bar asks for its last position, not for each one it passed.
            _ = Task.Run(async () =>
            {
                await Task.Delay(120).ConfigureAwait(false);
                if (Volatile.Read(ref _asked) != asked) return;
                string? uri = LocalArt.MakeFrame(path, Math.Max(1L, ms));
                Playback.ToUi(() => { if (_asked == asked && uri is not null) _frame.Value = uri; });
            });
        }
    }

    sealed record FrameImageProps(IReadSignal<string?> Frame);

    /// <summary>The frame itself, 16:9 (its own re-render on a new frame, nothing else moves).</summary>
    sealed class FrameImage : Component
    {
        public override Element Render()
        {
            var p = UseProps<FrameImageProps>();
            return Controls.Artwork(p.Frame.Value, FrameW, FrameH, Radii.Control);
        }
    }
}
