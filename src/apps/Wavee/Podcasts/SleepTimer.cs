// ── Podcasts/SleepTimer.cs — the sleep timer: pause playback after N minutes, or when the current chapter / episode ends ──────────────────────────────
//
// Wavee had none (the audiobook page asks for one beside Continue: a hidden sleep timer goes unfound, concept note "Library, sample, speed, sleep
// timer, hours"). One app-wide timer, owned by no page (it keeps running when the page closes): `Set(minutes)`, `SetEndOfItem()`, `Off()`. A one-second
// thread-pool tick marshals onto the UI thread (`Store.Post`): it refreshes `MinutesLeft` and pauses once the time is up, or, in the end-of-item mode,
// when the item that was on the deck when it was armed has (nearly) run out or the deck moved on to the next one. A pause also disarms it.

using FluentGpu.Signals;

namespace Wavee;

public static class SleepTimer
{
    /// <summary>The armed mode: Off, a number of minutes, or the end of the playing item.</summary>
    public enum Mode : byte { Off, Minutes, EndOfItem }

    static readonly Signal<Mode> s_mode = new(Mode.Off);
    static readonly Signal<int> s_minutesLeft = new(0);
    static long s_endsAtMs;
    static EntityId s_armedItem;
    static System.Threading.Timer? s_tick;

    /// <summary>The armed mode (Off while nothing is armed).</summary>
    public static IReadSignal<Mode> Armed => s_mode;
    /// <summary>Whole minutes left in the Minutes mode (rounded up), 0 otherwise.</summary>
    public static IReadSignal<int> MinutesLeft => s_minutesLeft;

    /// <summary>The presets the menus offer.</summary>
    public static ReadOnlySpan<int> Presets => [15, 30, 45, 60];

    public static void Set(int minutes)
    {
        if (minutes <= 0) { Off(); return; }
        s_endsAtMs = Environment.TickCount64 + minutes * 60_000L;
        s_minutesLeft.Value = minutes;
        s_mode.Value = Mode.Minutes;
        Start();
    }

    /// <summary>Pause when the item on the deck now ends (the chapter, the episode).</summary>
    public static void SetEndOfItem()
    {
        s_armedItem = Playback.CurrentId.Peek();
        s_minutesLeft.Value = 0;
        s_mode.Value = Mode.EndOfItem;
        Start();
    }

    public static void Off()
    {
        s_tick?.Dispose();
        s_tick = null;
        s_mode.Value = Mode.Off;
        s_minutesLeft.Value = 0;
    }

    static void Start()
    {
        s_tick ??= new System.Threading.Timer(static _ => Store.Post(Tick), null, 1000, 1000);
    }

    static void Tick()
    {
        switch (s_mode.Peek())
        {
            case Mode.Minutes:
            {
                long left = s_endsAtMs - Environment.TickCount64;
                if (left <= 0) { Fire(); return; }
                int minutes = (int)((left + 59_999) / 60_000);
                if (minutes != s_minutesLeft.Peek()) s_minutesLeft.Value = minutes;
                return;
            }
            case Mode.EndOfItem:
            {
                var current = Playback.CurrentId.Peek();
                if (s_armedItem.IsEmpty) { s_armedItem = current; return; }
                int duration = Playback.DurationMs.Peek();
                bool moved = !current.IsEmpty && current != s_armedItem;
                bool ending = current == s_armedItem && duration > 0 && Playback.PositionMs.Peek() >= duration - 1500;
                if (moved || ending) Fire();
                return;
            }
            default:
                Off();
                return;
        }
    }

    static void Fire()
    {
        Off();
        if (Playback.IsPlaying.Peek()) Playback.Pause("sleep-timer");
    }
}
