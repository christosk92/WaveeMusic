using System;

namespace Wavee;

/// <summary>The app-owned, storage-stable names for the control kit's video aspect policies. This enum deliberately
/// does not reuse <c>VideoAspectMode</c>'s numeric values: persisted data must survive an engine enum reorder.</summary>
public enum VideoAspectPreference : byte { Fit, Crop, Stretch, Native, Custom }

/// <summary>Codec for the global video-aspect preference. Missing/corrupt values degrade to Fit and 16:9; wire names
/// are append-only, culture-invariant settings data rather than UI strings.</summary>
public static class VideoAspectPersistence
{
    public const double DefaultCustomRatio = 16.0 / 9.0;

    public static VideoAspectPreference LoadMode(string? raw) => raw switch
    {
        "crop" => VideoAspectPreference.Crop,
        "stretch" => VideoAspectPreference.Stretch,
        "native" => VideoAspectPreference.Native,
        "custom" => VideoAspectPreference.Custom,
        _ => VideoAspectPreference.Fit,
    };

    public static string SaveMode(VideoAspectPreference mode) => mode switch
    {
        VideoAspectPreference.Crop => "crop",
        VideoAspectPreference.Stretch => "stretch",
        VideoAspectPreference.Native => "native",
        VideoAspectPreference.Custom => "custom",
        _ => "fit",
    };

    public static double LoadRatio(double raw)
        => double.IsFinite(raw) && raw > 0.01 && raw < 100.0 ? raw : DefaultCustomRatio;
}
