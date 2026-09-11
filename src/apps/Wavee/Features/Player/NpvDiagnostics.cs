namespace Wavee;

// Features/Player/NpvDiagnostics.cs — PlaybackBucketDiagnostics wrapper shape; category "npv" appears in Settings › Logs automatically.
internal static class NpvDiagnostics
{
    const string Category = "npv";
    public const string SourceHeader = "header", SourceFlyout = "flyout", SourceArtMenu = "artMenu", SourceSettings = "settings", SourcePalette = "palette";
    public static void PresentationSet(int from, int to, string source) => WaveeLog.Instance.Info(Category, "presentation.set",
        "now-playing hero " + Name(from) + " -> " + Name(to) + " via " + source, WaveeLogField.Of("from", Name(from)), WaveeLogField.Of("to", Name(to)), WaveeLogField.Of("source", source));
    public static void StyleSet(string from, string to, string source) => WaveeLog.Instance.Info(Category, "style.set",
        "player style " + from + " -> " + to + " via " + source, WaveeLogField.Of("from", from), WaveeLogField.Of("to", to), WaveeLogField.Of("source", source));
    public static void OptionSet(string style, string option, string choice, string source) => WaveeLog.Instance.Info(Category, "option.set",
        "player option " + style + "." + option + "=" + choice + " via " + source, WaveeLogField.Of("style", style), WaveeLogField.Of("option", option), WaveeLogField.Of("choice", choice), WaveeLogField.Of("source", source));
    public static void FlyoutClosed() => WaveeLog.Instance.Debug(Category, "flyout.close", "player style flyout dismissed");
    static string Name(int p) => p == NpvPlayerPrefs.Player ? "player" : "cover";
}
