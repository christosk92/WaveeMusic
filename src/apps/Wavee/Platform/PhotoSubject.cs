// ── Platform/PhotoSubject.cs — where a photograph's subject is, so a crop can keep it in view (lane B10) ────────────────────────────────────
//
// Role: CORE (pure: no engine scene, no entity, no theme read; every input is an argument)
//
// An artist's header photograph is cropped into a wide, short band with the artist's name over its foot (every theme's artist hero). The
// photograph says nothing about where its subject stands, so a fixed crop shows foreheads on one photograph and chins on the next (the owner,
// 2026-10-24: "the hero image is kinda cut off, so I can't see shit. Move it up a bit more"). A photograph is measured ONCE (`Palette.Subject`,
// on the measuring worker that already reads its luminance for the scrims) and answers one point, 0..1 of the image:
//
//   FACES      the OS's offline face detector (`FaceFinder`) found faces: their area-weighted centre (a larger face counts for more).
//   SAFE ZONE  no face (a back, a silhouette, a landscape, an N edition without the detector, a decode that failed): Spotify's own guidance for
//              artist headers (2660 x 1140, key content in the upper two-thirds, the bottom third under the name and its gradient), the subject at a
//              third of the height, centred across.
//              A PORTRAIT (taller than wide) with no face puts the subject in the top fifth instead: its head, not its chest.
//
// Why no saliency map: measured on the three real headers in the owner's cache (lane B10's PROGRESS), an edges + saturation + skin-tone map
// (smartcrop style) put the subject at the legs and the floor (edges) or at brown leaves (skin tone) on two of three, lower than the fixed crop
// it would replace. Faces or the safe zone, never a guess that moves the photograph the wrong way.
//
// A theme places the point at its own frame position (the engine's `ImageEl.FocusFrameX/Y`): where its layout leaves the subject in view.

namespace Wavee;

/// <summary>Where a photograph's subject stands, 0..1 of the image (see the file header).</summary>
/// <param name="X">The subject's centre across, 0 left, 1 right.</param>
/// <param name="Y">The subject's centre down, 0 top, 1 foot.</param>
/// <param name="Faces">True when faces placed it; false for the safe zone.</param>
public readonly record struct PhotoSubject(float X, float Y, bool Faces)
{
    /// <summary>Spotify's artist-header safe zone: the subject a third of the way down, centred across (key content in the upper two-thirds).</summary>
    public const float SafeZoneY = 1f / 3f, SafeZoneX = 0.5f;

    /// <summary>The subject of a photograph with no face found.</summary>
    public static PhotoSubject SafeZone => new(SafeZoneX, SafeZoneY, false);

    /// <summary>A PORTRAIT's safe zone (taller than wide, no face found): a person photographed standing, whose head is in the top fifth. A third of the way
    /// down a tall portrait is the chest, so a wide band framing it there showed the waist and the legs (owner 2026-11-07, INNA's About card).</summary>
    public const float PortraitSafeZoneY = 0.2f;

    /// <summary>A photograph is a portrait when it is this much taller than wide.</summary>
    public const float PortraitAspect = 1.1f;

    /// <summary>The subject of an image <paramref name="width"/> x <paramref name="height"/> whose faces are <paramref name="faces"/> (boxes in its pixels):
    /// the faces' centre weighted by their area, or the safe zone with none.</summary>
    public static PhotoSubject Of(ReadOnlySpan<(float X, float Y, float W, float H)> faces, int width, int height)
    {
        if (width <= 0 || height <= 0) return SafeZone;
        if (faces.IsEmpty) return height > width * PortraitAspect ? new PhotoSubject(SafeZoneX, PortraitSafeZoneY, false) : SafeZone;
        double sx = 0, sy = 0, sa = 0;
        foreach (var f in faces)
        {
            double a = Math.Max(1.0, (double)f.W * f.H);
            sx += (f.X + f.W / 2.0) * a;
            sy += (f.Y + f.H / 2.0) * a;
            sa += a;
        }
        return new PhotoSubject(Math.Clamp((float)(sx / sa / width), 0f, 1f), Math.Clamp((float)(sy / sa / height), 0f, 1f), true);
    }
}
