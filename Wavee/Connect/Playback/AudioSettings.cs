using System.Reactive.Linq;
using System.Reactive.Subjects;

namespace Wavee.Connect.Playback;

/// <summary>
/// Available audio processing presets.
/// </summary>
public enum AudioPreset
{
    /// <summary>
    /// No processing - flat/transparent playback.
    /// </summary>
    None,

    /// <summary>
    /// FM Radio broadcast sound - compression, EQ, and limiting.
    /// Makes audio punchy, loud, and consistent.
    /// </summary>
    Radio,

    // Future presets:
    // BassBoost,  // Enhanced low end
    // Vocal,      // Emphasize vocals/mids
    // Loudness,   // Fletcher-Munson curve compensation
    // Electronic, // Enhanced highs and sub-bass
}

/// <summary>
/// Reactive audio settings that can be changed during playback.
/// Processors subscribe to these observables and react to changes immediately.
/// </summary>
public sealed class AudioSettings : IDisposable
{
    private readonly BehaviorSubject<AudioPreset> _currentPreset = new(AudioPreset.None);
    private readonly List<IDisposable> _subscriptions = new();
    private bool _disposed;

    /// <summary>
    /// Observable for preset changes.
    /// Emits immediately with current value on subscription.
    /// </summary>
    public IObservable<AudioPreset> PresetChanged => _currentPreset.AsObservable();

    /// <summary>
    /// Gets or sets the current audio preset. Changes are immediately pushed to all subscribers.
    /// </summary>
    public AudioPreset Preset
    {
        get => _currentPreset.Value;
        set
        {
            if (_currentPreset.Value != value)
            {
                _currentPreset.OnNext(value);
            }
        }
    }

    /// <summary>
    /// Gets all available presets.
    /// </summary>
    public static IReadOnlyList<AudioPreset> AvailablePresets { get; } =
        Enum.GetValues<AudioPreset>();

    /// <summary>
    /// Cycles to the next preset in the list.
    /// </summary>
    /// <returns>The new preset after cycling.</returns>
    public AudioPreset CyclePreset()
    {
        var presets = AvailablePresets;
        var currentIndex = Array.IndexOf(Enum.GetValues<AudioPreset>(), Preset);
        var nextIndex = (currentIndex + 1) % presets.Count;
        Preset = presets[nextIndex];
        return Preset;
    }

    /// <summary>
    /// Tries to set a preset by name (case-insensitive).
    /// </summary>
    /// <param name="name">Preset name (e.g., "radio", "none").</param>
    /// <returns>True if preset was found and set, false otherwise.</returns>
    public bool TrySetPreset(string name)
    {
        if (Enum.TryParse<AudioPreset>(name, ignoreCase: true, out var preset))
        {
            Preset = preset;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Registers a subscription to be disposed when this settings instance is disposed.
    /// </summary>
    internal void TrackSubscription(IDisposable subscription)
    {
        _subscriptions.Add(subscription);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        foreach (var subscription in _subscriptions)
        {
            subscription.Dispose();
        }
        _subscriptions.Clear();

        _currentPreset.Dispose();
        _disposed = true;
    }
}
