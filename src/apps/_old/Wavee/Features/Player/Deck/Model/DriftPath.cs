namespace Wavee.Features.Player.Deck.Model;

/// <summary>Pure keyframe data for the Canvas-drift deck's slow pan/zoom (Part 3, "Other decks"). No model/tick here —
/// the face drives an engine `anim.Keyframes` loop directly off this table; it exists purely so the timing/shape is
/// pinned in one engine-free place instead of copied into face code.</summary>
public static class DriftPath
{
    public const float SlowSeconds = 40f, FastSeconds = 16f;

    /// (offset 0..1 through the loop, tx, ty, scale) — tx/ty are fractions of the frame size, mirrored so the loop
    /// (0 → 1) has no seam when played back-and-forth.
    public static readonly (float offset, float tx, float ty, float scale)[] Keys =
    {
        (0f, 0f, 0f, 1f),
        (0.5f, -0.05f, 0.03f, 1.06f),
        (1f, 0.04f, -0.04f, 1.02f),
    };
}
