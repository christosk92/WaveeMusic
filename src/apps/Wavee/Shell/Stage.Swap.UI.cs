// ── Shell/Stage.Swap.UI.cs ─────────────────────────────────────────────────────────────────────────────────────────
// the full-screen stage's TRACK SWAP: what a skip changes (the cover, the title column, the chips, the lyrics) cross-fades, the incoming side
// drifting in from the direction of travel
//
// Role: UI
// Owner: K
//
// 0.3.8 cut every part at once on a track change: the cover, the title, the artists line and the chips flipped in one frame, the lyrics
// vanished and the next document appeared (the owner's 0.3.9 report: "skipping next in fullscreen is not a smooth experience at all").
//
//   • ONE swap: the part is a keyed child (per track) of a stable ZStack box that fades in (`TrackSwapIn`) while the outgoing part fades out
//     where it stands under a DECLARED swap exit (`TrackSwapFade`, which the inspector's gates read as the design): nothing reflows.
//   • THE DRIFT: the INCOMING part's entry starts a short way off on the side the track came from (next: from the right, previous: from
//     the left, `Playback.SkipSense`) and travels to rest while the outgoing part fades where it stands. The production
//     timing is the stage's own cross-fade clock (`Tone.CrossFadeMs`, which the backdrop art and the accent colours already change on);
//     reduced motion leaves the cross-fade and no drift.
//   • RAPID SKIPS: each change re-keys the child; the parts in between leave by the same fade from wherever they are, and only the newest
//     part drifts: five presses in a second never stack offsets, the newest track wins and settles once.
//   • The cover swap keeps the previous cover UNDER the new one (the backdrop art's idiom), so the new art fades in over the old art,
//     never over an empty slot, and a cover still decoding never flashes the stage through.

using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;

namespace Wavee;

public static partial class Stage
{
    /// <summary>The track swap's declared cross-fade on the LEAVING part: opacity only, so the inspector's gates read the part's exit as the
    /// designed swap.</summary>
    public static readonly EnterExit TrackSwapFade = SwapFamilies.Declare("Stage.TrackSwap", new EnterExit(Opacity: 0f, Active: true));

    /// <summary>The ENTERING part's fade. Not the declared family: a declared swap is born at once under the leaving tree (the form-swap
    /// idiom), and a track change wants a true cross-fade, the new part rising while the old one falls.</summary>
    public static readonly EnterExit TrackSwapIn = new(Opacity: 0f, Active: true);

    /// <summary>The entering part's fade with the drift from the skip's side (<see cref="Playback.SkipSense"/>); reduced motion keeps the fade
    /// and drops the travel (a value, never a branch around the motion).</summary>
    public static EnterExit TrackSwapEnter => TrackSwapIn with { Dx = Design.Reduced ? 0f : Playback.SkipSense * TrackSwapDrift };

    /// <summary>The swap's timing: the stage's own cross-fade clock (<see cref="Tone.CrossFadeMs"/>, the one the backdrop art and the accent
    /// colours already change on), on the same decelerate curve as the backdrop art's fade, so every part of a skip arrives together and settles calmly. Reduced
    /// motion keeps the fade.</summary>
    public static readonly MotionTokenDef TrackSwapMotion = MotionTokenDef.Eased(Tone.CrossFadeMs, Easing.FluentDecelerate, ReducedMotionPolicy.KeepFade);

    /// <summary>How far the incoming part travels in from the direction of the skip (DIP).</summary>
    public const float TrackSwapDrift = 24f;

    /// <summary><paramref name="body"/> as the part of track <paramref name="track"/> (any stable per-track key: the stage's
    /// <c>TrackKey</c>, a url): a change cross-fades the old part out and the new one in, the new one drifting in from the skip's side. The
    /// box is a ZStack sized by its child; <paramref name="fill"/> stretches it (and the part) over its slot instead.</summary>
    public static Element TrackSwap(string track, Element body, bool fill = false)
        => Embed.Comp(new TrackSwapProps(track, body, fill), static () => new TrackSwapView());

    /// <summary>The cover art of <paramref name="url"/> in a <paramref name="size"/> square: the previous cover stays under the new one, which
    /// fades in over it with the swap's drift (<see cref="TrackSwap"/>).</summary>
    public static Element CoverSwap(string url, float size, float corners, int decodePx, string? morphKey = null)
        => Embed.Comp(new CoverSwapProps(url, size, corners, decodePx, morphKey), static () => new CoverSwapView());

    /// <summary>The swap key of the playing track: its row slot (stable across a metadata refresh of the same track), "" for none.</summary>
    public static string SwapKey(Track track) => track.IsValid ? track.Slot.ToString(System.Globalization.CultureInfo.InvariantCulture) : "";

    internal sealed record TrackSwapProps(string Track, Element Body, bool Fill);

    internal sealed record CoverSwapProps(string Url, float Size, float Corners, int DecodePx, string? MorphKey);

    sealed class TrackSwapView : Component
    {
        string? _seen;

        public override Element Render()
        {
            var p = UseProps<TrackSwapProps>();
            string track = p.Track;
            // the first render is born at rest (the stage's own entry carries it); a change after it cross-fades and drifts in
            bool swapped = _seen is not null && _seen != track;
            _seen = track;
            var part = new BoxEl
            {
                Key = "swap:" + track, Direction = 1, MinWidth = 0f,
                AlignSelf = p.Fill ? FlexAlign.Stretch : FlexAlign.Auto, JustifySelf = p.Fill ? FlexAlign.Stretch : FlexAlign.Auto,
                Enter = swapped ? TrackSwapEnter : null, Exit = TrackSwapFade, Transition = TrackSwapMotion,
                Children = [p.Body],
            };
            return new BoxEl
            {
                ZStack = true, MinWidth = 0f, HitTestPassThrough = true,
                Grow = p.Fill ? 1f : 0f, AlignSelf = p.Fill ? FlexAlign.Stretch : FlexAlign.Auto,
                Children = [part],
            };
        }
    }

    sealed class CoverSwapView : Component
    {
        string _shown = "", _previous = "";

        public override Element Render()
        {
            var p = UseProps<CoverSwapProps>();
            bool changed = p.Url != _shown;
            if (changed) { _previous = _shown; _shown = p.Url; }
            var kids = new List<Element>(2);
            if (_previous.Length > 0) kids.Add(Art(_previous, p) with { Key = "cover:" + _previous });
            kids.Add(Art(_shown, p) with
            {
                Key = "cover:" + _shown,
                // the very first cover is born where it is (the stage's own entry carries it); a later one fades in over the previous
                Enter = changed && _previous.Length > 0 ? TrackSwapEnter : null, Transition = TrackSwapMotion,
            });
            return new BoxEl
            {
                Width = p.Size, Height = p.Size, ZStack = true, HitTestVisible = false,
                Children = kids.ToArray(),
            };
        }

        static BoxEl Art(string url, CoverSwapProps p) => new()
        {
            Width = p.Size, Height = p.Size, ZStack = true, HitTestVisible = false,
            Children = [Controls.Artwork(url.Length > 0 ? url : null, p.Size, p.Size, p.Corners, morphKey: p.MorphKey, decodePx: p.DecodePx)],
        };
    }
}
