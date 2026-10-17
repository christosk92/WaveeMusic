// ── Shell/OnMedia.UI.cs ────────────────────────────────────────────────────────────────────────────────────────────
// the shared on-media surface player's core half: the mount of the structure's transport strip, its "more" menu and its fade seed
//
// Role: UI
// Owner: K
//
// T6b MOVED THE STRIP TO THE STRUCTURE (`Player.OnMediaTransport`, `Themes\Classic\Structure\Views\Player\ClassicOnMedia.cs`): Stage's
// transport language on every video overlay (engine chrome is off). What stays here is what DECIDES and what the video band shares:
// `Transport` mounts the key with the surface's own idle signal and chrome feed (the props are `RailWiring.OnMediaProps`), `MoreMenu`
// builds the rows from live reads when the menu opens, and `FadeChrome` seeds the eased reveal and conceal.
//
// WHY THIS IS NOT HOVER-REVEALED. The first cut used the card idiom (`Opacity = 0, HoverOpacity = 1`), which is right
// for a strip inside a hover container and wrong here: a reveal is driven by the nearest INTERACTIVE ancestor
// (`AnimScheduler.Hover.EnclosingScopeHovered`), and the pop-out window's tree has no pointer handler above the stage
// at all — so in the pop-out the controls never appeared, on any pointer input. They are driven instead by the video
// element's OWN idle machine (`MediaPlayerElement.ChromeVisibleOut`): one machine per surface, already carrying dwell,
// the leave debounce, the scrub / menu / keyboard / window-move holds and the accessibility override, and already the
// thing that decides when the cursor hides. Two timers could not agree; one cannot disagree.

using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Controls.Media;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Theming.Contracts;
using ViewSlots = FluentGpu.Theming.Slots;

namespace Wavee;

public static class OnMedia
{
    static readonly float[] s_rates = [0.5f, 0.75f, 1f, 1.25f, 1.5f, 1.75f, 2f];

    /// <summary>The on-media transport over a video hole. Ink, seek bar and time labels are Stage's / the player bar's.
    /// Revealed and concealed by <paramref name="visible"/> — the surface's idle machine, never hover (file header).
    /// <paramref name="feed"/> is the SAME window's <c>PlayerChromeFeed</c> the video element reports activity to, so
    /// the pointer sitting over a transport button counts as chrome activity too (a pointer parked on ⋯ must not let
    /// the idle machine hide the very menu button it is over).</summary>
    public static Element Transport(IReadSignal<bool> visible, PlayerChromeFeed feed)
        => ViewSlots.View(ViewKeys.OnMediaTransport, RailWiring.OnMediaProps(visible, feed));

    /// <summary>The on-media chrome fade, the engine transport's own idiom: asymmetric by token (reveal 150 ms
    /// decelerate, conceal 400 ms ease-out), reduced motion resolved as a VALUE by <c>EffectiveDurationMs</c> and never
    /// as a branch here. The caller's static <c>Opacity</c> is the TERMINAL, so an unrelated re-render always
    /// re-asserts the right end value; this seeds the eased approach to it, FROM where the pixels actually are (a live
    /// track when a reveal interrupts a conceal, the previous terminal otherwise). Call it from a
    /// <c>UseLayoutEffect</c> keyed on <paramref name="show"/>, and skip the mount run: chrome is born at its
    /// terminal.</summary>
    public static void FadeChrome(RenderContext ctx, NodeHandle node, bool show)
    {
        var scene = ctx.Scene;
        if (ctx.Anim is not { } anim || scene is null || node.IsNull || !scene.IsLive(node)) return;
        var tok = show ? MotionTok.MediaChromeReveal : MotionTok.MediaChromeConceal;
        float to = show ? 1f : 0f;
        float ms = tok.EffectiveDurationMs(AnimChannel.Opacity);
        if (ms <= 0f) return;                       // reduced motion: the terminal already IS the answer
        float from = anim.TryGetTrackValue(node, AnimChannel.Opacity, out float live) ? live : 1f - to;
        if (MathF.Abs(from - to) < 0.001f) return;
        anim.SeedEased(node, AnimChannel.Opacity, from, to, ms, tok.Easing);
    }

    /// <summary>⋯ rows: WHERE the video plays at the top level, then the three settings as cascades — the same shape
    /// the engine's own transport uses for this menu (<c>MediaPlayerElement</c>: an Aspect ratio / Playback speed /
    /// Quality trio of sub-menus). Flat, the four groups ran to ~25 rows and scrolled INSIDE the presenter at its
    /// 468 DIP cap, so the rungs at the bottom were simply not visible.
    ///
    /// <para><see cref="Video.PlacementMenu"/> bundles the placement radios AND its own trailing extras (Always on
    /// top while detached, Turn off video while active), each behind its own separator. Those extras must stay LAST
    /// - "Turn off video" under three settings cascades reads as part of them - so the list is split at its first
    /// separator here rather than adding an overload to the shared builder: the player bar's chevron and its
    /// overflow cascade use the very same list and must not change shape.</para></summary>
    public static IReadOnlyList<MenuFlyoutItem> MoreMenu()
    {
        var placement = Video.PlacementMenu(includeFullscreen: true);
        int cut = 0;
        while (cut < placement.Count && !placement[cut].IsSeparator) cut++;

        var items = new List<MenuFlyoutItem>(12);
        for (int i = 0; i < cut; i++) items.Add(placement[i]);
        items.Add(MenuFlyoutItem.Separator);
        // Aspect carries the icon and the other two do not, matching the engine's transport exactly. Every cascade
        // here is non-empty by construction (four aspect rows plus a separator and four numeric-ratio rows, Auto plus
        // any rungs, the seven fixed rates), which is
        // the one real foot-gun: `OpenSub` accepts a non-null EMPTY list and opens a blank popup.
        items.Add(MenuFlyoutItem.SubMenu(Loc.Get(Strings.Player.AspectMenu), AspectRows(), Icons.Movie));
        items.Add(MenuFlyoutItem.SubMenu(Loc.Get(Strings.Player.QualityMenu), QualityRows()));
        items.Add(MenuFlyoutItem.SubMenu(Loc.Get(Strings.Player.SpeedMenu), SpeedRows()));
        for (int i = cut; i < placement.Count; i++) items.Add(placement[i]);
        return items;
    }

    /// <summary>The four modes, then the numeric ratios the engine's own (suppressed) transport offers — the only rows
    /// that can show a visibly different frame for Wavee's overwhelmingly 16:9 surfaces (docked, pop-out and mini are
    /// all within 3 DIP of 16:9, so Fit/Crop/Stretch barely differ there). <see cref="Video.AspectRatioRows"/> is the
    /// pure "which row is checked" rule; the ratio itself is captured per row by the lambda below, built fresh every
    /// time the menu opens.</summary>
    static IReadOnlyList<MenuFlyoutItem> AspectRows()
    {
        var mode = Video.Prefs.Aspect(Platform.Settings);
        double ratio = Video.Prefs.CustomRatio(Platform.Settings);
        return
        [
            MenuFlyoutItem.RadioItem(Loc.Get(Strings.Player.AspectFit), mode == Video.AspectPreference.Fit,
                static () => Video.Prefs.SetAspect(Platform.Settings, Video.AspectPreference.Fit, 0)),
            MenuFlyoutItem.RadioItem(Loc.Get(Strings.Player.AspectCrop), mode == Video.AspectPreference.Crop,
                static () => Video.Prefs.SetAspect(Platform.Settings, Video.AspectPreference.Crop, 0)),
            MenuFlyoutItem.RadioItem(Loc.Get(Strings.Player.AspectStretch), mode == Video.AspectPreference.Stretch,
                static () => Video.Prefs.SetAspect(Platform.Settings, Video.AspectPreference.Stretch, 0)),
            MenuFlyoutItem.RadioItem(Loc.Get(Strings.Player.AspectNative), mode == Video.AspectPreference.Native,
                static () => Video.Prefs.SetAspect(Platform.Settings, Video.AspectPreference.Native, 0)),
            MenuFlyoutItem.Separator,
            RatioRow(Strings.Player.AspectRatio169, Video.AspectRatioRows.Standard[0], mode, ratio),
            RatioRow(Strings.Player.AspectRatio43, Video.AspectRatioRows.Standard[1], mode, ratio),
            RatioRow(Strings.Player.AspectRatio219, Video.AspectRatioRows.Standard[2], mode, ratio),
            RatioRow(Strings.Player.AspectRatio239, Video.AspectRatioRows.Standard[3], mode, ratio),
        ];
    }

    static MenuFlyoutItem RatioRow(string key, double r, Video.AspectPreference mode, double current)
        => MenuFlyoutItem.RadioItem(Loc.Get(key), Video.AspectRatioRows.IsChecked(mode, current, r),
            () => Video.Prefs.SetAspect(Platform.Settings, Video.AspectPreference.Custom, r));

    /// <summary>Auto plus the rungs THIS manifest actually offers. A fixed 720/1080/1440/2160 ladder lied twice: a rung
    /// no variant matches does not pin (<c>Playback.Video.PinQuality</c> persists it but <c>SetPreferredHeight</c> finds nothing to select) yet still
    /// moves the ABR ceiling, so the row read as selected while the player was really on Auto — and the rungs the
    /// manifest DID offer were missing from the menu.</summary>
    static IReadOnlyList<MenuFlyoutItem> QualityRows()
    {
        int pin = Platform.Settings.Get(Platform.Keys.VideoQuality);
        int[] rungs = Playback.Video.QualityRungs();
        var rows = new List<MenuFlyoutItem>(rungs.Length + 1)
        {
            MenuFlyoutItem.RadioItem(Loc.Get(Strings.Player.QualityAuto), pin == 0,
                static () => Playback.Video.PinQuality(0)),
        };
        for (int i = 0; i < rungs.Length; i++)
        {
            int h = rungs[i];
            rows.Add(MenuFlyoutItem.RadioItem(h.ToString(System.Globalization.CultureInfo.InvariantCulture) + "p", pin == h,
                () => Playback.Video.PinQuality(h)));
        }
        return rows;
    }

    static IReadOnlyList<MenuFlyoutItem> SpeedRows()
    {
        float now = Playback.EpisodeSpeed.Peek();
        var rows = new List<MenuFlyoutItem>(s_rates.Length);
        for (int i = 0; i < s_rates.Length; i++)
        {
            float rate = s_rates[i];
            string label = rate.ToString("0.##") + "×";
            rows.Add(MenuFlyoutItem.RadioItem(label, MathF.Abs(now - rate) < 0.01f, () => Playback.SetEpisodeSpeed(rate)));
        }
        return rows;
    }
}
