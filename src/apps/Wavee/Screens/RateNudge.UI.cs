// ── Screens/RateNudge.UI.cs ────────────────────────────────────────────────────────────────────────────────────────
// the overlay lane's rate-nudge layer: wires the host's entries to live signals, resolves the card's words, hands the picture
// (`ViewKeys.ShellRateNudge`, `Themes\Common\RateNudgeCard.cs`) its props; and the doors' verbs (Settings → About, the account menu,
// the after-update plate's footer).
//
// Role: UI
// Owner: R
// Budget: 160 lines
// Spec: docs/plans/wavee/rate-nudge-implementation.md §5.2
//
// MOUNTED ONCE, in Shell.Overlays after ReleaseNotes.AfterUpdateChrome (the mount order there is load-bearing: the session is known
// by then). The layer reads no signal in its render: each effect subscribes to exactly the one signal that should wake the host, and
// the 1 Hz interval allocates nothing while nothing is armed (`Tick` returns at its first null check).

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using Wavee.Theming.Contracts;
using ViewSlots = FluentGpu.Theming.Slots;

namespace Wavee;

public static partial class RateNudge
{
    /// <summary>The layer (Shell.Overlays mounts it once).</summary>
    public static Element Chrome() => Embed.Comp(static () => new RateNudgeLayer());

    [CoreHost]
    sealed class RateNudgeLayer : Component
    {
        string? _lastTrack;   // the track the last boundary check saw (null: none yet, so the track playing at mount is no boundary)

        public override Element Render()
        {
            var overlay = UseContext(Overlay.Service);
            var post = UsePost();
            UseEffect(() => BindPost(post), DepKey.Empty);

            // The one track-boundary entry (IsTrackBoundary: a playing track replaced by another; the first of a run is none).
            UseEffect(() =>
            {
                EntityId id = Playback.CurrentId.Value;
                string? next = id.IsEmpty ? null : id.Text;   // one string per track change, never per frame
                if (IsTrackBoundary(_lastTrack, next)) OnTrackBoundary();
                _lastTrack = next;
            });
            UseEffect(() => OnPlaying(Playback.IsPlaying.Value));
            UseEffect(() => { var f = Playback.Error.Value; if (IsTrouble(f)) NoteTrouble("playback " + f); });
            UseEffect(() => { var f = Spotify.Fault.Value; if (IsTrouble(f)) NoteTrouble("sign-in " + f); });
            UseInterval(() => Tick(overlay.AnyOpen), 1000f);

            // Equal words compare equal (a record), so a hold on hover, which rewrites Shown, never re-renders the picture.
            var view = UseComputed(() => Shown.Value is { } c ? ViewFor(c) : null);
            return ViewSlots.View(ViewKeys.ShellRateNudge, new RateNudgeCardProps
            {
                View = view,
                Primary = static () => _ = PrimaryAsync(Origin.Card),
                Later = static () => Answer(Event.NotNow, Origin.Card),
                Never = static () => Answer(Event.NeverAgain, Origin.Card),
                Hold = static on => Hold(on),
            });
        }
    }

    /// <summary>The card's words, picked by (Ask, milestone) from en-US.json's rateNudge.*, first person (owner, 2026-11-20). The star
    /// line only on a star card, and only when StarCount has a count (StarsShown decides). A Store card never names GitHub or a star.</summary>
    static RateNudgeView ViewFor(Card c) => c.Ask == Ask.Rate
        ? new(Loc.Get(Strings.RateNudge.Rate.Title),
              Loc.Get(c.Milestone is null ? Strings.RateNudge.Rate.Body : Strings.RateNudge.Rate.MilestoneBody), null,
              Big(c), BigCaption(c), BigStats(c),
              Loc.Get(Strings.RateNudge.Rate.Primary), Loc.Get(Strings.RateNudge.Later), Loc.Get(Strings.RateNudge.Never), RateNudgeGlyph.Store)
        : new(Loc.Get(Strings.RateNudge.Star.Title),
              Loc.Get(c.Milestone is null ? Strings.RateNudge.Star.Body : Strings.RateNudge.Star.MilestoneBody),
              StarCount is { } n ? Strings.RateNudge.Star.Meta(n) : null,
              Big(c), BigCaption(c), BigStats(c),
              Loc.Get(Strings.RateNudge.Star.Primary), Loc.Get(Strings.RateNudge.Later), Loc.Get(Strings.RateNudge.Never), RateNudgeGlyph.Star);

    /// <summary>"25 hours": the milestone the ask waited for, or null on a card that is not a milestone's.</summary>
    static string? Big(Card c) => c.Milestone is { } h ? Strings.RateNudge.Big((int)h) : null;

    /// <summary>"of music played in Wavee since July 2" (the ledger's CountedSince in the app culture; the year too when it is not this
    /// year's), or null without a milestone or a start.</summary>
    static string? BigCaption(Card c)
    {
        if (c.Milestone is null || Current.CountedSince is not { } since) return null;
        DateTimeOffset local = since.ToLocalTime();
        // A no-break space keeps "July 30" on one line when the caption wraps (Zune's larger type).
        string pattern = local.Year == s_seams.Now().ToLocalTime().Year ? "MMMM d" : "MMMM d, yyyy";
        return Strings.RateNudge.BigCaption(local.ToString(pattern, DiscoCardText.Culture()));
    }

    /// <summary>"on 41 listening days" under a milestone (the ledger's ListenDays), or null on a card that is not a milestone's.</summary>
    static string? BigStats(Card c) => c.Milestone is null ? null : Strings.RateNudge.BigStats(Current.ListenDays);

    /// <summary>The doors' verb (the Settings → About dial, the account menu, the after-update plate's footer): one home, the same flow as
    /// the card's primary. False: nothing on the page to re-read.</summary>
    public static bool DoorPrimary() { _ = PrimaryAsync(Origin.Door); return false; }

    /// <summary>The switch's caption: "At most three times, ever" on an install with a launch ask, "twice" on a new one.</summary>
    public static string CapHint() => Loc.Get(CapFor(Current, Rules) > Rules.RegularAsks
        ? Strings.RateNudge.Setting.HintThreeTimes : Strings.RateNudge.Setting.HintTwice);
}
