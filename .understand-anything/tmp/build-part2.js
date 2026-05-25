const fs = require('fs');
const path = require('path');

const outDir = 'C:/WAVEE/WaveeMusic/.understand-anything/intermediate';

const part2 = {
  nodes: [
    {
      id: 'file:src/Wavee.AudioHost/Audio/AudioEngine.cs',
      type: 'file',
      name: 'AudioEngine.cs',
      filePath: 'src/Wavee.AudioHost/Audio/AudioEngine.cs',
      summary: 'Core audio playback engine for the out-of-process AudioHost: manages play/pause/resume/seek/stop/volume, drives three playback loops (deferred Spotify stream, local file, generic URL), and publishes reactive state/error/track-completed observables.',
      tags: ['audio', 'service', 'playback', 'reactive'],
      complexity: 'complex',
      languageNotes: 'Uses System.Reactive BehaviorSubject for state, GC.Collect before decode loops to reduce GC pauses during hot audio path.'
    },
    {
      id: 'class:src/Wavee.AudioHost/Audio/AudioEngine.cs:AudioEngine',
      type: 'class',
      name: 'AudioEngine',
      filePath: 'src/Wavee.AudioHost/Audio/AudioEngine.cs',
      lineRange: [20, 1157],
      summary: 'Singleton audio engine composing sink, decoder registry, processing chain, and HTTP client into three playback loop strategies (deferred/local/remote), with seek, volume, EQ, and normalization controls.',
      tags: ['audio', 'service', 'playback', 'singleton'],
      complexity: 'complex'
    },
    {
      id: 'function:src/Wavee.AudioHost/Audio/AudioEngine.cs:PlaybackLoopDeferredAsync',
      type: 'function',
      name: 'PlaybackLoopDeferredAsync',
      filePath: 'src/Wavee.AudioHost/Audio/AudioEngine.cs',
      lineRange: [353, 725],
      summary: 'Playback loop for Spotify-sourced tracks using LazyProgressiveDownloader; handles decrypt, seek-within-loop, normalization, DSP processing, and drains the audio sink on completion.',
      tags: ['audio', 'playback', 'spotify', 'streaming'],
      complexity: 'complex'
    },
    {
      id: 'function:src/Wavee.AudioHost/Audio/AudioEngine.cs:PlaybackLoopAsync',
      type: 'function',
      name: 'PlaybackLoopAsync',
      filePath: 'src/Wavee.AudioHost/Audio/AudioEngine.cs',
      lineRange: [899, 1106],
      summary: 'Generic HTTP-based playback loop for remote audio URLs; downloads via HttpClient, optionally decrypts with AudioDecryptStream, decodes, processes, and writes to sink.',
      tags: ['audio', 'playback', 'http', 'streaming'],
      complexity: 'complex'
    },
    {
      id: 'function:src/Wavee.AudioHost/Audio/AudioEngine.cs:PlaybackLoopLocalAsync',
      type: 'function',
      name: 'PlaybackLoopLocalAsync',
      filePath: 'src/Wavee.AudioHost/Audio/AudioEngine.cs',
      lineRange: [737, 895],
      summary: 'Playback loop for local files: opens FileStream, finds decoder via registry, applies normalization and DSP chain, and drains sink on completion.',
      tags: ['audio', 'playback', 'local-file'],
      complexity: 'complex'
    },
    {
      id: 'function:src/Wavee.AudioHost/Audio/AudioEngine.cs:SetEqualizerEnabledAsync',
      type: 'function',
      name: 'SetEqualizerEnabledAsync',
      filePath: 'src/Wavee.AudioHost/Audio/AudioEngine.cs',
      lineRange: [292, 334],
      summary: 'Configures EQ bands on the EqualizerProcessor then awaits version-processed confirmation to ensure the change is applied before returning.',
      tags: ['audio', 'equalizer', 'dsp'],
      complexity: 'moderate'
    },
    {
      id: 'function:src/Wavee.AudioHost/Audio/AudioEngine.cs:StopInternalAsync',
      type: 'function',
      name: 'StopInternalAsync',
      filePath: 'src/Wavee.AudioHost/Audio/AudioEngine.cs',
      lineRange: [1118, 1146],
      summary: 'Cancels active playback CancellationTokenSource, waits up to 3s for the playback task to finish, disposes the CTS, and flushes the audio sink.',
      tags: ['audio', 'lifecycle', 'cancellation'],
      complexity: 'moderate'
    },
    {
      id: 'file:src/Wavee.AudioHost/Audio/AudioSettings.cs',
      type: 'file',
      name: 'AudioSettings.cs',
      filePath: 'src/Wavee.AudioHost/Audio/AudioSettings.cs',
      summary: 'Manages audio preset selection (cycle, try-set by name) with reactive PresetChanged observable and IDisposable subscription tracking.',
      tags: ['audio', 'settings', 'reactive'],
      complexity: 'moderate'
    },
    {
      id: 'class:src/Wavee.AudioHost/Audio/AudioSettings.cs:AudioSettings',
      type: 'class',
      name: 'AudioSettings',
      filePath: 'src/Wavee.AudioHost/Audio/AudioSettings.cs',
      lineRange: [33, 116],
      summary: 'Holds AudioPreset state with a BehaviorSubject-backed PresetChanged observable; supports cycle, try-set-by-name, subscription tracking, and disposal.',
      tags: ['audio', 'settings', 'reactive'],
      complexity: 'moderate'
    },
    {
      id: 'file:src/Wavee.AudioHost/Audio/NormalizationData.cs',
      type: 'file',
      name: 'NormalizationData.cs',
      filePath: 'src/Wavee.AudioHost/Audio/NormalizationData.cs',
      summary: 'Parses Spotify normalization metadata from a binary span (4 little-endian floats) and computes track/album gain factors with peak-limiting.',
      tags: ['audio', 'normalization', 'data-model'],
      complexity: 'moderate'
    }
  ],
  edges: [
    { source: 'file:src/Wavee.AudioHost/Audio/AudioEngine.cs', target: 'class:src/Wavee.AudioHost/Audio/AudioEngine.cs:AudioEngine', type: 'contains', direction: 'forward', weight: 1.0 },
    { source: 'file:src/Wavee.AudioHost/Audio/AudioEngine.cs', target: 'class:src/Wavee.AudioHost/Audio/AudioEngine.cs:AudioEngine', type: 'exports', direction: 'forward', weight: 0.8 },
    { source: 'file:src/Wavee.AudioHost/Audio/AudioEngine.cs', target: 'function:src/Wavee.AudioHost/Audio/AudioEngine.cs:PlaybackLoopDeferredAsync', type: 'contains', direction: 'forward', weight: 1.0 },
    { source: 'file:src/Wavee.AudioHost/Audio/AudioEngine.cs', target: 'function:src/Wavee.AudioHost/Audio/AudioEngine.cs:PlaybackLoopAsync', type: 'contains', direction: 'forward', weight: 1.0 },
    { source: 'file:src/Wavee.AudioHost/Audio/AudioEngine.cs', target: 'function:src/Wavee.AudioHost/Audio/AudioEngine.cs:PlaybackLoopLocalAsync', type: 'contains', direction: 'forward', weight: 1.0 },
    { source: 'file:src/Wavee.AudioHost/Audio/AudioEngine.cs', target: 'function:src/Wavee.AudioHost/Audio/AudioEngine.cs:SetEqualizerEnabledAsync', type: 'contains', direction: 'forward', weight: 1.0 },
    { source: 'file:src/Wavee.AudioHost/Audio/AudioEngine.cs', target: 'function:src/Wavee.AudioHost/Audio/AudioEngine.cs:StopInternalAsync', type: 'contains', direction: 'forward', weight: 1.0 },
    { source: 'class:src/Wavee.AudioHost/Audio/AudioEngine.cs:AudioEngine', target: 'class:src/Wavee.AudioHost/Audio/Abstractions/IAudioSink.cs:IAudioSink', type: 'depends_on', direction: 'forward', weight: 0.6 },
    { source: 'class:src/Wavee.AudioHost/Audio/AudioEngine.cs:AudioEngine', target: 'class:src/Wavee.AudioHost/Audio/Abstractions/IAudioDecoder.cs:IAudioDecoder', type: 'depends_on', direction: 'forward', weight: 0.6 },
    { source: 'class:src/Wavee.AudioHost/Audio/AudioEngine.cs:AudioEngine', target: 'class:src/Wavee.AudioHost/Audio/Abstractions/IAudioProcessor.cs:IAudioProcessor', type: 'depends_on', direction: 'forward', weight: 0.6 },
    { source: 'file:src/Wavee.AudioHost/Audio/AudioSettings.cs', target: 'class:src/Wavee.AudioHost/Audio/AudioSettings.cs:AudioSettings', type: 'contains', direction: 'forward', weight: 1.0 },
    { source: 'file:src/Wavee.AudioHost/Audio/AudioSettings.cs', target: 'class:src/Wavee.AudioHost/Audio/AudioSettings.cs:AudioSettings', type: 'exports', direction: 'forward', weight: 0.8 }
  ]
};

fs.writeFileSync(path.join(outDir, 'batch-13-part-2.json'), JSON.stringify(part2, null, 2));
console.log('part-2 written: ' + part2.nodes.length + ' nodes, ' + part2.edges.length + ' edges');
