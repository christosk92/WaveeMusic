using System;
using System.IO;
using System.Threading.Tasks;
using FluentGpu.Controls;
using FluentGpu.Lottie;

namespace Wavee;

/// <summary>The setup wizard's Lottie hero SEAM (plan: <c>docs/plans/wavee/lottie-heroes-implementation.md</c>).
/// <see cref="For"/> resolves the one asset each <see cref="SetupPage"/> plays, cached per process the same way
/// <c>AppLocale.cs</c>'s <c>Localization.LoadFolder</c> resolves <c>assets/loc</c> — an unpackaged
/// <c>dotnet run</c> and a packaged MSIX both read straight off <see cref="AppContext.BaseDirectory"/>, so there is
/// no separate packaged/unpackaged branch to maintain. <c>SetupPageFrame</c> (<c>SetupPageHost.cs</c>) is the one
/// caller, mounting <c>LottieView.Create(For(page), SetupLayout.IconColumnWidth, Options)</c> directly in the icon
/// column.</summary>
static class WaveeLottie
{
    // One LottieSource per asset, parsed/compiled lazily and at most once per process (LottieSource.Plan itself is
    // also lock-guarded, but the Lazy<T> here means a concurrent first call to two different pages never races the
    // SAME file open). Lazy<T> defaults to ExecutionAndPublication, i.e. thread-safe.
    static readonly Lazy<LottieSource> _eula = new(() => Load("eula"));
    static readonly Lazy<LottieSource> _connect = new(() => Load("connect"));
    static readonly Lazy<LottieSource> _patch = new(() => Load("patch"));

    static LottieSource Load(string name) =>
        LottieSource.FromFile(Path.Combine(AppContext.BaseDirectory, "assets", "lottie", name + ".json"));

    /// <summary>The per-page Lottie asset: Terms plays the EULA/checkmark scene, SignIn the device-pairing scene,
    /// LocalPlayback the shield/verify scene. Every other <see cref="SetupPage"/> value (there are only the three
    /// today) has no hero and is a programming error to ask for.</summary>
    public static LottieSource For(SetupPage page) => page switch
    {
        SetupPage.Terms => _eula.Value,
        SetupPage.SignIn => _connect.Value,
        SetupPage.LocalPlayback => _patch.Value,
        _ => throw new ArgumentOutOfRangeException(nameof(page), page, "No Lottie hero for this SetupPage."),
    };

    /// <summary>THE configurable seam for how every hero plays: Rise Media Player's own setup cadence (the first
    /// half of the timeline, once, then hold — <see cref="LottieOptions.RiseSetup"/>) recoloured to Wavee's live
    /// accent (<see cref="WaveeLottieRecolor.Apply(FluentGpu.Foundation.ColorF)"/>), zoomed 1.2× — the OOBE scenes
    /// carry ~25% empty margin at their authored fit, and Rise's own 192-DIP icon column shows them at that same
    /// scene scale; the zoom is Wavee's one deliberate deviation (bigger, per design review), still centred and
    /// clipped to the box (<c>LottieOptions.Zoom</c>). <c>Loop</c>/<c>To</c> on <see cref="LottieOptions"/> are the
    /// two knobs a future change (looping heroes, a different hold point) edits — in exactly this one place, for all
    /// three pages at once.</summary>
    public static LottieOptions Options => LottieOptions.RiseSetup with { Recolor = WaveeLottieRecolor.Apply, Zoom = 1.2f };

    /// <summary>Preloads (parses + compiles) all three assets off the UI thread. Fire-and-forget from the wizard's
    /// root mounts (<see cref="SetupPreAuthRoot"/>, <see cref="SetupChrome"/>) so the first real mount of a hero
    /// never pays the parse/compile cost — <see cref="LottieView.Preload"/> is ~1-2 ms per file, so this is a
    /// nicety, not a correctness requirement (a cold <see cref="For"/> call still works, just costs a frame more).</summary>
    public static Task Warm() => Task.WhenAll(
        LottieView.Preload(_eula.Value),
        LottieView.Preload(_connect.Value),
        LottieView.Preload(_patch.Value));
}
