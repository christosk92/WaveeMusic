using System;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Signals;
using FluentGpu.WindowsApi.Network;
using Wavee.Backend;

namespace Wavee;

/// <summary>
/// Cached connection-cost policy: caps streaming quality on metered networks and defers prefetch.
/// Fail-soft to <see cref="NetworkCost.Unknown"/> (unmetered-conservative — a probe failure never throttles playback).
/// </summary>
static class NetworkPolicy
{
    const int RefreshMs = 60_000;
    const int QualityMin = 0;
    const int QualityMax = 2;

    static readonly object Gate = new();
    static IAppSettings? _settings;
    static Action<Action>? _post;
    static NetworkCost _cost = NetworkCost.Unknown;
    static IDisposable? _connectivity;
    static Timer? _timer;
    static int _refreshing;
    static bool _installed;

    /// <summary>Last <see cref="NetworkStatus.ReadCostAsync"/> snapshot. UI-thread writes after the MTA read completes.</summary>
    public static NetworkCost Cost => _cost;

    /// <summary>Quiet metered snapshot for UI (a status line, never a nag). Subscribe via <see cref="Metered"/>.</summary>
    public static bool IsMetered => _cost.IsMetered;

    /// <summary>Reactive metered flag — Settings reads <c>.Value</c> for the quiet helper; cost refreshes hop here.</summary>
    public static Signal<bool> Metered { get; } = new(false);

    /// <summary>The persisted metered cap (0..2). Seeded at <see cref="Install"/>; the Settings combo writes it.</summary>
    public static Signal<int> MeteredQualityCap { get; } = new(WaveeSettings.MeteredQualityCap.Default);
    /// <summary>Protected-video Auto cap on a metered connection. Zero means unlimited.</summary>
    public static Signal<int> MeteredVideoMaxHeight { get; } = new(WaveeSettings.VideoMeteredMaxHeight.Default);

    /// <summary>True when prefetch/warm downloads should wait (metered). Unknown cost does not defer.</summary>
    public static bool ShouldDeferPrefetch => _cost.IsMetered;

    /// <summary>Idempotent. Kicks an immediate cost read and a slow refresh timer. Not per-frame.</summary>
    public static void Install(IAppSettings settings, Action<Action> post)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(post);
        lock (Gate)
        {
            if (_installed) return;
            _installed = true;
            _settings = settings;
            _post = post;
        }

        try
        {
            MeteredQualityCap.Value = Math.Clamp(settings.Get(WaveeSettings.MeteredQualityCap), QualityMin, QualityMax);
            MeteredVideoMaxHeight.Value = Math.Max(0, settings.Get(WaveeSettings.VideoMeteredMaxHeight));
        }
        catch { }

        Refresh();
        try
        {
            _timer = new Timer(static _ => Refresh(), null, RefreshMs, RefreshMs);
        }
        catch { _timer = null; }

        try
        {
            _connectivity = NetworkStatus.Subscribe(static _ => Refresh());
        }
        catch { _connectivity = null; }
    }

    /// <summary>
    /// Effective streaming quality: on a Fixed/Variable (metered) connection, <c>min(userQuality, cap)</c>;
    /// otherwise the user's <paramref name="userQuality"/>. Both clamped to 0..2 (Normal96 · High160 · VeryHigh320).
    /// Unknown cost is unmetered-conservative (no cap).
    /// </summary>
    public static int EffectiveQuality(int userQuality, int meteredCap)
    {
        int q = Math.Clamp(userQuality, QualityMin, QualityMax);
        int cap = Math.Clamp(meteredCap, QualityMin, QualityMax);
        return _cost.IsMetered ? Math.Min(q, cap) : q;
    }

    /// <summary>Reads <see cref="WaveeSettings.PlaybackQuality"/> + <see cref="WaveeSettings.MeteredQualityCap"/>.</summary>
    public static int EffectiveQuality(IAppSettings settings)
        => EffectiveQuality(settings.Get(WaveeSettings.PlaybackQuality), settings.Get(WaveeSettings.MeteredQualityCap));

    /// <summary>Same as <see cref="EffectiveQuality(IAppSettings)"/> against the settings captured at <see cref="Install"/>.</summary>
    public static int EffectiveQuality()
        => _settings is { } s ? EffectiveQuality(s) : Math.Clamp(WaveeSettings.PlaybackQuality.Default, QualityMin, QualityMax);

    /// <summary>The <see cref="AudioQualityPreference"/> the resolver should aim at (Ogg rungs only — Lossless reserved).</summary>
    public static AudioQualityPreference EffectiveQualityPreference(IAppSettings settings)
        => (AudioQualityPreference)EffectiveQuality(settings);

    /// <summary>Protected-video Auto height cap for the current connection. <see cref="int.MaxValue"/> means unlimited.</summary>
    public static int EffectiveVideoMaxHeight(IAppSettings settings)
    {
        if (!_cost.IsMetered) return int.MaxValue;
        int cap = Math.Max(0, settings.Get(WaveeSettings.VideoMeteredMaxHeight));
        return cap == 0 ? int.MaxValue : cap;
    }

    public static void Shutdown()
    {
        try { _connectivity?.Dispose(); } catch { }
        _connectivity = null;
        try { _timer?.Dispose(); } catch { }
        _timer = null;
        lock (Gate)
        {
            _installed = false;
            _settings = null;
            _post = null;
        }
    }

    static void Refresh()
    {
        if (Interlocked.Exchange(ref _refreshing, 1) != 0) return;
        _ = RefreshAsync();
    }

    static async Task RefreshAsync()
    {
        try
        {
            NetworkCost cost = NetworkCost.Unknown;
            try { cost = await NetworkStatus.ReadCostAsync().ConfigureAwait(false); }
            catch { cost = NetworkCost.Unknown; }

            void Apply()
            {
                _cost = cost;
                if (Metered.Peek() != cost.IsMetered)
                    Metered.Value = cost.IsMetered;
            }

            if (_post is { } post)
            {
                try { post(Apply); }
                catch { Apply(); }
            }
            else Apply();
        }
        finally
        {
            Interlocked.Exchange(ref _refreshing, 0);
        }
    }
}
