using System.ComponentModel;
using FluentAssertions;
using Wavee.Connect;
using Wavee.Core.Session;
using Wavee.Playback.Contracts;
using Wavee.UI.Contracts;
using Wavee.UI.Enums;
using Wavee.UI.Models;
using Wavee.UI.Services;

namespace Wavee.UI.Tests.Services;

public sealed class CardPreviewPlaybackCoordinatorTests
{
    [Fact]
    public async Task ScheduleHover_CancelledBeforeDelay_DoesNotStartPreviewOrDuck()
    {
        var state = new FakePlaybackStateService { IsPlaying = true, Volume = 64 };
        var playback = new FakePlaybackService(state);
        var engine = new FakePreviewAudioPlaybackEngine();
        using var coordinator = CreateCoordinator(engine, playback, state, hoverDelayMs: 50);
        var probe = new PreviewProbe();
        var ownerId = Guid.NewGuid();

        await coordinator.ScheduleHover(CreateRequest(ownerId, "https://example.com/a.mp3", probe));
        await coordinator.CancelOwner(ownerId);
        await Task.Delay(90);

        engine.StartCalls.Should().Be(0);
        playback.SetVolumeCalls.Should().BeEmpty();
        probe.States.Should().Contain(state => !state.IsPending && !state.IsPlaying);
    }

    [Fact]
    public async Task ScheduleHover_SecondOwnerWins_WhenUsersScrubAcrossCards()
    {
        var state = new FakePlaybackStateService { IsPlaying = true, Volume = 70 };
        var playback = new FakePlaybackService(state);
        var engine = new FakePreviewAudioPlaybackEngine();
        using var coordinator = CreateCoordinator(engine, playback, state, hoverDelayMs: 40);
        var probeA = new PreviewProbe();
        var probeB = new PreviewProbe();
        var ownerA = Guid.NewGuid();
        var ownerB = Guid.NewGuid();

        await coordinator.ScheduleHover(CreateRequest(ownerA, "https://example.com/a.mp3", probeA));
        await Task.Delay(10);
        await coordinator.ScheduleHover(CreateRequest(ownerB, "https://example.com/b.mp3", probeB));
        await WaitForConditionAsync(() => engine.StartCalls == 1);

        engine.StartedPreviewUrls.Should().ContainSingle().Which.Should().Be("https://example.com/b.mp3");
        probeA.CompletedCount.Should().Be(0);
        probeA.States.Should().NotContain(state => state.IsPlaying);
        probeB.States.Should().Contain(state => state.IsPlaying);
    }

    [Fact]
    public async Task CancelOwner_DuringEngineStartup_CancelsTheInFlightStart()
    {
        var state = new FakePlaybackStateService { IsPlaying = true, Volume = 55 };
        var playback = new FakePlaybackService(state);
        var engine = new FakePreviewAudioPlaybackEngine
        {
            StartGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        using var coordinator = CreateCoordinator(engine, playback, state, hoverDelayMs: 10);
        var probe = new PreviewProbe();
        var ownerId = Guid.NewGuid();

        await coordinator.ScheduleHover(CreateRequest(ownerId, "https://example.com/startup.mp3", probe));
        await WaitForConditionAsync(() => engine.StartCalls == 1);

        await coordinator.CancelOwner(ownerId);
        engine.StartGate.TrySetResult();
        await WaitForConditionAsync(() => engine.StopCalls == 1);

        probe.States.Should().Contain(state => !state.IsPlaying);
        engine.CurrentSessionId.Should().BeNull();
    }

    [Fact]
    public async Task StartImmediate_BypassesHoverDelay()
    {
        var state = new FakePlaybackStateService { IsPlaying = true, Volume = 72 };
        var playback = new FakePlaybackService(state);
        var engine = new FakePreviewAudioPlaybackEngine();
        using var coordinator = CreateCoordinator(engine, playback, state, hoverDelayMs: 500);

        await coordinator.StartImmediate(CreateRequest(Guid.NewGuid(), "https://example.com/immediate.mp3", new PreviewProbe()));
        await WaitForConditionAsync(() => engine.StartCalls == 1);

        engine.StartedPreviewUrls.Should().ContainSingle().Which.Should().Be("https://example.com/immediate.mp3");
    }

    [Fact]
    public async Task RapidStartStopStartStop_RestoresPlaybackVolumeAfterEachCycle()
    {
        var state = new FakePlaybackStateService { IsPlaying = true, Volume = 70 };
        var playback = new FakePlaybackService(state);
        var engine = new FakePreviewAudioPlaybackEngine();
        using var coordinator = CreateCoordinator(engine, playback, state);
        var ownerId = Guid.NewGuid();
        var probe = new PreviewProbe();

        await coordinator.StartImmediate(CreateRequest(ownerId, "https://example.com/one.mp3", probe));
        await WaitForConditionAsync(() => engine.StartCalls == 1);
        await coordinator.CancelOwner(ownerId);
        await WaitForConditionAsync(() => state.Volume == 70);

        await coordinator.StartImmediate(CreateRequest(ownerId, "https://example.com/two.mp3", probe));
        await WaitForConditionAsync(() => engine.StartCalls == 2);
        await coordinator.CancelOwner(ownerId);
        await WaitForConditionAsync(() => state.Volume == 70);

        engine.StopCalls.Should().Be(2);
        state.Volume.Should().Be(70);
        playback.SetVolumeCalls.Should().Contain(15);
    }

    [Fact]
    public async Task StartImmediate_ForTrackSwitch_KeepsPlaybackDucked()
    {
        var state = new FakePlaybackStateService { IsPlaying = true, Volume = 80 };
        var playback = new FakePlaybackService(state);
        var engine = new FakePreviewAudioPlaybackEngine();
        using var coordinator = CreateCoordinator(engine, playback, state);
        var ownerId = Guid.NewGuid();
        var probe = new PreviewProbe();

        await coordinator.StartImmediate(CreateRequest(ownerId, "https://example.com/track-a.mp3", probe));
        await WaitForConditionAsync(() => engine.StartCalls == 1 && state.Volume == 15);
        playback.ClearVolumeCalls();

        await coordinator.StartImmediate(CreateRequest(ownerId, "https://example.com/track-b.mp3", probe));
        await WaitForConditionAsync(() => engine.StartCalls == 2);

        playback.SetVolumeCalls.Should().NotContain(80);
        state.Volume.Should().Be(15);
    }

    [Fact]
    public async Task OldSessionCompletion_IsIgnoredAfterANewSessionStarts()
    {
        var state = new FakePlaybackStateService { IsPlaying = true, Volume = 65 };
        var playback = new FakePlaybackService(state);
        var engine = new FakePreviewAudioPlaybackEngine();
        using var coordinator = CreateCoordinator(engine, playback, state);
        var ownerId = Guid.NewGuid();
        var probe = new PreviewProbe();

        await coordinator.StartImmediate(CreateRequest(ownerId, "https://example.com/old.mp3", probe));
        await WaitForConditionAsync(() => engine.StartCalls == 1);
        var oldSessionId = engine.SessionIds[0];

        await coordinator.StartImmediate(CreateRequest(ownerId, "https://example.com/new.mp3", probe));
        await WaitForConditionAsync(() => engine.StartCalls == 2);

        engine.CompleteSession(oldSessionId);
        await Task.Delay(30);
        probe.CompletedCount.Should().Be(0);

        engine.CompleteSession(engine.SessionIds[1]);
        await WaitForConditionAsync(() => probe.CompletedCount == 1);
    }

    [Fact]
    public async Task ExternalVolumeChangeWhileDucked_RestoresToTheNewUserLevel()
    {
        var state = new FakePlaybackStateService { IsPlaying = true, Volume = 60 };
        var playback = new FakePlaybackService(state);
        var engine = new FakePreviewAudioPlaybackEngine();
        using var coordinator = CreateCoordinator(engine, playback, state);
        var ownerId = Guid.NewGuid();

        await coordinator.StartImmediate(CreateRequest(ownerId, "https://example.com/duck.mp3", new PreviewProbe()));
        await WaitForConditionAsync(() => state.Volume == 15);

        state.SetVolume(42);
        await WaitForConditionAsync(() => state.Volume == 15);

        await coordinator.CancelOwner(ownerId);
        await WaitForConditionAsync(() => state.Volume == 42);
    }

    [Fact]
    public async Task RemotePlayback_DoesNotDuck()
    {
        var state = new FakePlaybackStateService
        {
            IsPlaying = true,
            IsPlayingRemotely = true,
            Volume = 58
        };
        var playback = new FakePlaybackService(state);
        var engine = new FakePreviewAudioPlaybackEngine();
        using var coordinator = CreateCoordinator(engine, playback, state);

        await coordinator.StartImmediate(CreateRequest(Guid.NewGuid(), "https://example.com/remote.mp3", new PreviewProbe()));
        await WaitForConditionAsync(() => engine.StartCalls == 1);

        playback.SetVolumeCalls.Should().BeEmpty();
        state.Volume.Should().Be(58);
    }

    [Fact]
    public async Task UnregisterOwner_CancelsPendingHover_AndStopsActivePreview()
    {
        var state = new FakePlaybackStateService { IsPlaying = true, Volume = 68 };
        var playback = new FakePlaybackService(state);
        var engine = new FakePreviewAudioPlaybackEngine();
        using var coordinator = CreateCoordinator(engine, playback, state, hoverDelayMs: 50);
        var ownerId = Guid.NewGuid();

        await coordinator.ScheduleHover(CreateRequest(ownerId, "https://example.com/pending.mp3", new PreviewProbe()));
        await coordinator.UnregisterOwner(ownerId);
        await Task.Delay(90);
        engine.StartCalls.Should().Be(0);

        await coordinator.StartImmediate(CreateRequest(ownerId, "https://example.com/active.mp3", new PreviewProbe()));
        await WaitForConditionAsync(() => engine.StartCalls == 1);
        await coordinator.UnregisterOwner(ownerId);
        await WaitForConditionAsync(() => engine.StopCalls == 1);
    }

    private static CardPreviewPlaybackCoordinator CreateCoordinator(
        FakePreviewAudioPlaybackEngine engine,
        FakePlaybackService playback,
        FakePlaybackStateService state,
        int hoverDelayMs = 20)
    {
        return new CardPreviewPlaybackCoordinator(
            engine,
            playback,
            state,
            hoverDelay: TimeSpan.FromMilliseconds(hoverDelayMs),
            completionRestoreGraceDelay: TimeSpan.FromMilliseconds(40),
            duckFadeDuration: TimeSpan.FromMilliseconds(1),
            restoreFadeDuration: TimeSpan.FromMilliseconds(1));
    }

    private static CardPreviewRequest CreateRequest(Guid ownerId, string previewUrl, PreviewProbe probe)
    {
        return new CardPreviewRequest(
            ownerId,
            previewUrl,
            probe.OnFrame,
            probe.OnStateChanged,
            probe.OnCompleted);
    }

    private static async Task WaitForConditionAsync(Func<bool> condition, int timeoutMs = 1000)
    {
        var started = DateTime.UtcNow;
        while (!condition())
        {
            if ((DateTime.UtcNow - started).TotalMilliseconds > timeoutMs)
                throw new TimeoutException("Timed out waiting for condition.");

            await Task.Delay(10);
        }
    }

    private sealed class PreviewProbe
    {
        public List<CardPreviewPlaybackState> States { get; } = [];
        public int CompletedCount { get; private set; }

        public void OnFrame(PreviewVisualizationFrame frame)
        {
        }

        public void OnStateChanged(CardPreviewPlaybackState state)
        {
            States.Add(state);
        }

        public void OnCompleted()
        {
            CompletedCount++;
        }
    }

    private sealed class FakePreviewAudioPlaybackEngine : IPreviewAudioPlaybackEngine
    {
        private readonly Dictionary<string, Action> _completionCallbacks = [];
        private int _sessionCounter;

        public TaskCompletionSource? StartGate { get; set; }
        public int StartCalls { get; private set; }
        public int StopCalls { get; private set; }
        public string? CurrentSessionId { get; private set; }
        public List<string> StartedPreviewUrls { get; } = [];
        public List<string> SessionIds { get; } = [];

        public async Task<PreviewStartResult> StartAsync(
            string previewUrl,
            Action<PreviewVisualizationFrame> onFrame,
            Action onCompleted,
            CancellationToken ct = default)
        {
            StartCalls++;
            StartedPreviewUrls.Add(previewUrl);

            if (StartGate != null)
                await StartGate.Task.WaitAsync(ct);

            var sessionId = $"session-{Interlocked.Increment(ref _sessionCounter)}";
            CurrentSessionId = sessionId;
            SessionIds.Add(sessionId);
            _completionCallbacks[sessionId] = onCompleted;

            return new PreviewStartResult(true, sessionId);
        }

        public Task StopAsync(CancellationToken ct = default)
        {
            StopCalls++;
            CurrentSessionId = null;
            return Task.CompletedTask;
        }

        public void CompleteSession(string sessionId)
        {
            if (_completionCallbacks.TryGetValue(sessionId, out var callback))
                callback();
        }
    }

    private sealed class FakePlaybackService : IPlaybackService
    {
        private readonly FakePlaybackStateService _state;

        public FakePlaybackService(FakePlaybackStateService state)
        {
            _state = state;
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        public event Action<string?>? BufferingStarted;

        public bool IsBuffering => false;
        public bool IsExecutingCommand => false;
        public string? ActiveDeviceId => null;
        public string? ActiveDeviceName => null;
        public bool IsPlayingRemotely => _state.IsPlayingRemotely;
        public IObservable<PlaybackErrorEvent> Errors { get; } = new EmptyObservable<PlaybackErrorEvent>();
        public List<int> SetVolumeCalls { get; } = [];

        public Task<PlaybackResult> PlayContextAsync(string contextUri, PlayContextOptions? options = null, CancellationToken ct = default)
            => Task.FromResult(PlaybackResult.Success());

        public Task<PlaybackResult> PlayTrackInContextAsync(string trackUri, string contextUri, PlayContextOptions? options = null, CancellationToken ct = default)
            => Task.FromResult(PlaybackResult.Success());

        public Task<PlaybackResult> PlayTracksAsync(IReadOnlyList<string> trackUris, int startIndex = 0, CancellationToken ct = default)
            => Task.FromResult(PlaybackResult.Success());

        public Task<PlaybackResult> ResumeAsync(CancellationToken ct = default)
            => Task.FromResult(PlaybackResult.Success());

        public Task<PlaybackResult> PauseAsync(CancellationToken ct = default)
            => Task.FromResult(PlaybackResult.Success());

        public Task<PlaybackResult> TogglePlayPauseAsync(CancellationToken ct = default)
            => Task.FromResult(PlaybackResult.Success());

        public Task<PlaybackResult> SkipNextAsync(CancellationToken ct = default)
            => Task.FromResult(PlaybackResult.Success());

        public Task<PlaybackResult> SkipPreviousAsync(CancellationToken ct = default)
            => Task.FromResult(PlaybackResult.Success());

        public Task<PlaybackResult> SeekAsync(long positionMs, CancellationToken ct = default)
            => Task.FromResult(PlaybackResult.Success());

        public Task<PlaybackResult> SetShuffleAsync(bool enabled, CancellationToken ct = default)
            => Task.FromResult(PlaybackResult.Success());

        public Task<PlaybackResult> SetRepeatModeAsync(RepeatMode mode, CancellationToken ct = default)
            => Task.FromResult(PlaybackResult.Success());

        public Task<PlaybackResult> SetVolumeAsync(int volumePercent, CancellationToken ct = default)
        {
            SetVolumeCalls.Add(volumePercent);
            _state.SetVolume(volumePercent);
            return Task.FromResult(PlaybackResult.Success());
        }

        public Task<PlaybackResult> AddToQueueAsync(string trackUri, CancellationToken ct = default)
            => Task.FromResult(PlaybackResult.Success());

        public Task<PlaybackResult> TransferPlaybackAsync(string deviceId, bool startPlaying = true, CancellationToken ct = default)
            => Task.FromResult(PlaybackResult.Success());

        public Task<PlaybackResult> SwitchAudioOutputAsync(int deviceIndex, CancellationToken ct = default)
            => Task.FromResult(PlaybackResult.Success());

        public void ClearVolumeCalls()
        {
            SetVolumeCalls.Clear();
        }
    }

    private sealed class FakePlaybackStateService : IPlaybackStateService
    {
        private double _volume;
        private bool _isPlaying;
        private bool _isPlayingRemotely;
        private bool _isVolumeRestricted;

        public event PropertyChangedEventHandler? PropertyChanged;

        public bool IsPlaying
        {
            get => _isPlaying;
            set
            {
                if (_isPlaying == value) return;
                _isPlaying = value;
                OnPropertyChanged(nameof(IsPlaying));
            }
        }

        public bool IsBuffering => false;
        public string? BufferingTrackId => null;
        public string? CurrentTrackId => null;
        public string? CurrentTrackTitle => null;
        public string? CurrentArtistName => null;
        public string? CurrentAlbumArt => null;
        public string? CurrentAlbumArtLarge => null;
        public string? CurrentArtistId => null;
        public string? CurrentAlbumId => null;
        public IReadOnlyList<ArtistCredit>? CurrentArtists => null;
        public string? CurrentAlbumArtColor => null;

        public bool IsPlayingRemotely
        {
            get => _isPlayingRemotely;
            set
            {
                if (_isPlayingRemotely == value) return;
                _isPlayingRemotely = value;
                OnPropertyChanged(nameof(IsPlayingRemotely));
            }
        }

        public bool IsVolumeRestricted
        {
            get => _isVolumeRestricted;
            set
            {
                if (_isVolumeRestricted == value) return;
                _isVolumeRestricted = value;
                OnPropertyChanged(nameof(IsVolumeRestricted));
            }
        }

        public string? ActiveDeviceName => null;

        // When the fake is playing locally (the default for these tests), ActiveDeviceType
        // is Computer. When IsPlayingRemotely is flipped, we surface a Speaker type so callers
        // that branch on device type see a non-local value. Kept derived so there's no
        // second piece of state to keep in sync.
        public DeviceType ActiveDeviceType => _isPlayingRemotely ? DeviceType.Speaker : DeviceType.Computer;

        // The fake does not model Spotify Connect discovery.
        public IReadOnlyList<ConnectDevice> AvailableConnectDevices { get; } = [];

        // The fake simulates a working local audio engine.
        public string? ActiveAudioDeviceName => "Fake Audio Device";
        public IReadOnlyList<AudioOutputDeviceDto> AvailableAudioDevices { get; } = [];
        public bool IsAudioEngineAvailable => true;

        public double Position { get; set; }
        public double Duration => 0;

        public double Volume
        {
            get => _volume;
            set => SetVolume(value);
        }

        public bool IsShuffle => false;
        public RepeatMode RepeatMode => RepeatMode.Off;
        public PlaybackContextInfo? CurrentContext => null;
        public IReadOnlyList<QueueItem> Queue { get; } = [];
        public int QueuePosition => 0;

        public void PlayPause()
        {
        }

        public void Next()
        {
        }

        public void Previous()
        {
        }

        public void Seek(double positionMs)
        {
        }

        public void SetShuffle(bool shuffle)
        {
        }

        public void SetRepeatMode(RepeatMode mode)
        {
        }

        public void PlayContext(PlaybackContextInfo context, int startIndex = 0)
        {
        }

        public void PlayTrack(string trackId, PlaybackContextInfo? context = null)
        {
        }

        public void AddToQueue(string trackId)
        {
        }

        public void AddToQueue(IEnumerable<string> trackIds)
        {
        }

        public void LoadQueue(IReadOnlyList<QueueItem> items, PlaybackContextInfo context, int startIndex = 0)
        {
        }

        public void NotifyBuffering(string? trackId)
        {
        }

        public void ClearBuffering()
        {
        }

        public void SetVolume(double volume)
        {
            _volume = volume;
            OnPropertyChanged(nameof(Volume));
        }

        private void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    private sealed class EmptyObservable<T> : IObservable<T>
    {
        public IDisposable Subscribe(IObserver<T> observer)
        {
            return EmptyDisposable.Instance;
        }
    }

    private sealed class EmptyDisposable : IDisposable
    {
        public static EmptyDisposable Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
