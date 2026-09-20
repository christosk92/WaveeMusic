// Features/Player/NpvPlayerPrefs.cs — the LyricsPrefs/PlayerBarPrefs idiom: reads subscribe Epoch then re-read the store;
// every writer clamps, persists, logs, Bumps ONCE. Surfaces: header row, flyout, art context menu, Settings › Appearance,
// palette — and the deck faces for their options.
using FluentGpu.Signals;

namespace Wavee;

static class NpvPlayerPrefs
{
    public static readonly Signal<int> Epoch = new(0);
    public static void Bump() => Epoch.Value = Epoch.Peek() + 1;
    /// The style flyout's controlled open state — shared so the art context menu opens the SAME popup the gear owns.
    public static readonly Signal<bool> StyleFlyoutOpen = new(false);
    public const int Cover = 0, Player = 1;

    public static int ClampPresentation(int v) => v == Player ? Player : Cover;
    public static int ClampStyle(int id) => NpvPlayerCatalog.IsPresetId(id) ? id : NpvPlayerCatalog.DefaultPresetId;
    public static int ClampChoice(in NpvPlayerCatalog.OptionDef o, int i) => (uint)i < (uint)o.Choices.Length ? i : 0;

    public static int Presentation(IAppSettings? s) { _ = Epoch.Value; return ClampPresentation(s?.Get(WaveeSettings.NpvPresentation) ?? WaveeSettings.NpvPresentation.Default); }
    public static int Style(IAppSettings? s)        { _ = Epoch.Value; return ClampStyle(s?.Get(WaveeSettings.NpvPlayerStyle) ?? WaveeSettings.NpvPlayerStyle.Default); }
    public static int Choice(IAppSettings? s, in NpvPlayerCatalog.Preset p, in NpvPlayerCatalog.OptionDef o)
    { _ = Epoch.Value; return ClampChoice(o, s?.Get(NpvPlayerKeys.Option(p.Slug, o.Slug)) ?? 0); }
    public static int Choice(IAppSettings? s, in NpvPlayerCatalog.Preset p, string optionSlug) => Choice(s, p, NpvPlayerCatalog.Option(p, optionSlug));
    public static string ChoiceSlug(IAppSettings? s, in NpvPlayerCatalog.Preset p, string optionSlug)
    { var o = NpvPlayerCatalog.Option(p, optionSlug); return o.Choices[Choice(s, p, o)].Slug; }

    public static void SetPresentation(IAppSettings? s, int v, string source)
    {
        int from = ClampPresentation(s?.Get(WaveeSettings.NpvPresentation) ?? 0), to = ClampPresentation(v);
        s?.Set(WaveeSettings.NpvPresentation, to); NpvDiagnostics.PresentationSet(from, to, source); Bump();
    }
    public static void TogglePresentation(IAppSettings? s, string source) => SetPresentation(s, Presentation(s) == Cover ? Player : Cover, source);
    /// Picking a player ALSO shows it: a style chosen while the cover was up flips to Player.
    public static void SetStyle(IAppSettings? s, int id, string source)
    {
        int from = ClampStyle(s?.Get(WaveeSettings.NpvPlayerStyle) ?? 0), to = ClampStyle(id);
        s?.Set(WaveeSettings.NpvPlayerStyle, to);
        if (ClampPresentation(s?.Get(WaveeSettings.NpvPresentation) ?? 0) != Player) s?.Set(WaveeSettings.NpvPresentation, Player);
        NpvDiagnostics.StyleSet(NpvPlayerCatalog.ById(from).Slug, NpvPlayerCatalog.ById(to).Slug, source); Bump();
    }
    public static void NextStyle(IAppSettings? s, string source) => SetStyle(s, (Style(s) + 1) % NpvPlayerCatalog.Presets.Length, source);
    public static void SetChoice(IAppSettings? s, in NpvPlayerCatalog.Preset p, in NpvPlayerCatalog.OptionDef o, int i, string source)
    {
        int to = ClampChoice(o, i); s?.Set(NpvPlayerKeys.Option(p.Slug, o.Slug), to);
        NpvDiagnostics.OptionSet(p.Slug, o.Slug, o.Choices[to].Slug, source); Bump();
    }
}
