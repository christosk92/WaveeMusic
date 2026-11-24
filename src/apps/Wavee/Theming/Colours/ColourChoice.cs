// ── Theming/Colours/ColourChoice.cs — the user's colour sets and the one set of verbs that changes them (#184, plan 3.4) ───
//
// UI thread, like every signal and the token set. The choice is stored as flat keys (`ColourKeys`); `Current` is the
// resolved pair the styles are handed through `ThemeHost.Colours`. Every setter validates, persists, resolves, hands the pair to the
// host and applies:
//   · the colours alone changed: `Tok.Use` with the live 250 ms cross-fade;
//   · the window material changed with them (a palette mode is opaque, plan D5): the theme transaction, in place, which dissolves.
// `Seed` runs before the window and before `Appearance.Seed` (which installs the definition), so the first frame is already in the
// user's colours: there is no startup flash.

using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Signals;

namespace Wavee.Theming.Colours;

/// <summary>The stored colour choice (see the file header).</summary>
public static class ColourChoice
{
    private static readonly Signal<int> s_epoch = new(0);
    private static ColourPair s_current;

    /// <summary>The resolved pair the styles are handed.</summary>
    public static ColourPair Current => s_current;

    /// <summary>Bumped by every change: a Settings row or a preview reads it.</summary>
    public static IReadSignal<int> Epoch => s_epoch;

    /// <summary>The stored set of <paramref name="kind"/>, or null for the theme's own colours (an unknown id reads as null).</summary>
    public static ColourSpec? SpecOf(ThemeKind kind) => ColourCatalog.Find(Platform.Settings.Get(KeyOf(kind)), kind);

    /// <summary>True when the colour set of the CURRENT mode takes over what <paramref name="what"/> names (a theme option's row then shows disabled
    /// with "Set by your colours").</summary>
    public static bool Overrides(ColourOverride what) => what switch
    {
        ColourOverride.Accent => s_current.OwnsAccent && s_current.For(Tok.Theme) is not null,
        ColourOverride.Material => s_current.For(Tok.Theme) is not null,
        _ => false,
    };

    /// <summary>The stored named accent of <paramref name="kind"/> (empty: the set's default).</summary>
    public static string NamedAccentOf(ThemeKind kind) => Platform.Settings.Get(kind == ThemeKind.Light ? ColourKeys.AccentNameLight : ColourKeys.AccentNameDark);

    /// <summary>The stored accent source.</summary>
    public static AccentSource AccentSource => SourceOf(Platform.Settings.Get(ColourKeys.Accent));

    /// <summary>Before the window (and before <c>Appearance.Seed</c>, which installs the definition): read the stored choice and hand it to
    /// the theme host.</summary>
    public static void Seed(IAppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        s_current = Resolve(settings);
        ThemeHost.Colours = s_current;
    }

    /// <summary>The set for <paramref name="kind"/>: a catalog id offered for that mode, or null for the theme's own colours. An id that is
    /// not offered for <paramref name="kind"/> is refused (false, nothing changes).</summary>
    public static bool SetSpec(ThemeKind kind, string? specId)
    {
        if (!string.IsNullOrEmpty(specId) && ColourCatalog.Find(specId, kind) is null) return false;
        Platform.Settings.Set(KeyOf(kind), specId ?? "");
        Changed();
        return true;
    }

    /// <summary>Where the accent comes from.</summary>
    public static void SetAccentSource(AccentSource source)
    {
        Platform.Settings.Set(ColourKeys.Accent, (int)source);
        Changed();
    }

    /// <summary>The palette's named accent for <paramref name="kind"/> (an id of the set's <see cref="ColourSpec.Accents"/>; empty = its default).</summary>
    public static void SetNamedAccent(ThemeKind kind, string accentId)
    {
        Platform.Settings.Set(kind == ThemeKind.Light ? ColourKeys.AccentNameLight : ColourKeys.AccentNameDark, accentId ?? "");
        Changed();
    }

    /// <summary>My own accent (floored per mode by the solver).</summary>
    public static void SetOwnAccent(ColorF colour)
    {
        Platform.Settings.Set(ColourKeys.AccentOwn, ColorOption.Pack(colour with { A = 1f }));
        Changed();
    }

    private static void Changed()
    {
        ColourPair next = Resolve(Platform.Settings);
        bool moved = next != s_current;
        s_current = next;
        ThemeHost.Colours = next;
        s_epoch.Value = s_epoch.Peek() + 1;
        if (moved) Apply();
    }

    /// <summary>Installs the active definition for the new pair: live when only colours differ, else on the theme transaction.</summary>
    private static void Apply()
    {
        if (ThemeHost.ActiveDefinition is not { } next) return;   // before Install: the seed installs it
        if (ReferenceEquals(next, Tok.Definition)) return;          // an unchanged pick is not a change
        if (next.DiffersOnlyInColours(Tok.Definition))
        {
            Tok.Use(next, Tok.Theme);
            Appearance.RequestTransition();
        }
        else _ = ThemeHost.ApplyColoursInTransaction(next);
    }

    private static ColourPair Resolve(IAppSettings settings)
    {
        AccentSource source = SourceOf(settings.Get(ColourKeys.Accent));
        int own = settings.Get(ColourKeys.AccentOwn);
        return new ColourPair(
            Ladder(settings, ThemeKind.Light, source, own),
            Ladder(settings, ThemeKind.Dark, source, own),
            source);
    }

    private static ColourLadder? Ladder(IAppSettings settings, ThemeKind kind, AccentSource source, int own)
    {
        if (ColourCatalog.Find(settings.Get(KeyOf(kind)), kind) is not { } spec) return null;
        ColorF accent = source switch
        {
            AccentSource.Palette => spec.AccentNamed(settings.Get(kind == ThemeKind.Light ? ColourKeys.AccentNameLight : ColourKeys.AccentNameDark)),
            AccentSource.Own when own != 0 => ColorOption.Unpack(own) with { A = 1f },
            _ => spec.Seeds.Accent,
        };
        return ColourSolver.Resolve(spec, accent);
    }

    private static SettingKey<string> KeyOf(ThemeKind kind) => kind == ThemeKind.Light ? ColourKeys.Light : ColourKeys.Dark;

    private static AccentSource SourceOf(int stored) => stored switch
    {
        1 => AccentSource.Palette,
        2 => AccentSource.Own,
        _ => AccentSource.Theme,
    };
}
