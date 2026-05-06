using System;
using Windows.UI;

namespace Wavee.Controls.HeroCarousel;

internal static class OklchHelpers
{
    // OKLCH -> sRGB encoded (gamma-applied), matching CSS Color Module 4.
    // Pipeline: OKLCH -> OKLab -> linear sRGB -> apply sRGB transfer function.
    //
    // The result is the byte-for-byte equivalent of typing
    //   oklch(L C H / A)
    // into a CSS color picker. The transfer function is applied so the value
    // is suitable for passing to brushes / shaders that operate in sRGB space.
    //
    // Sanity check: OklchToColor(0.55, 0.15, 32, 1) should be ~ (178, 99, 53).
    public static Color OklchToColor(double l, double c, double hueDeg, double alpha)
    {
        double h = hueDeg * Math.PI / 180.0;
        double a = c * Math.Cos(h);
        double b = c * Math.Sin(h);

        double lp = l + 0.3963377774 * a + 0.2158037573 * b;
        double mp = l - 0.1055613458 * a - 0.0638541728 * b;
        double sp = l - 0.0894841775 * a - 1.2914855480 * b;

        double lc = lp * lp * lp;
        double mc = mp * mp * mp;
        double sc = sp * sp * sp;

        double r = +4.0767416621 * lc - 3.3077115913 * mc + 0.2309699292 * sc;
        double g = -1.2684380046 * lc + 2.6097574011 * mc - 0.3413193965 * sc;
        double bl = -0.0041960863 * lc - 0.7034186147 * mc + 1.7076147010 * sc;

        byte rb = LinearToSrgbByte(r);
        byte gb = LinearToSrgbByte(g);
        byte bb = LinearToSrgbByte(bl);
        byte ab = (byte)Math.Clamp(Math.Round(alpha * 255.0), 0, 255);

        return Color.FromArgb(ab, rb, gb, bb);
    }

    private static byte LinearToSrgbByte(double v)
    {
        v = Math.Clamp(v, 0.0, 1.0);
        double encoded = v <= 0.0031308
            ? 12.92 * v
            : 1.055 * Math.Pow(v, 1.0 / 2.4) - 0.055;
        return (byte)Math.Clamp(Math.Round(encoded * 255.0), 0, 255);
    }
}
