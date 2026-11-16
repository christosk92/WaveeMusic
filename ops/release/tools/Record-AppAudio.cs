// Record-AppAudio.cs — record ONE process's audio output (and its children's) to a WAV, nothing else on the machine.
//
//   dotnet run ops/release/tools/Record-AppAudio.cs -- <pid> <out.wav>      (a line "q" on stdin stops it)
//
// Record-Feature.ps1 -Audio runs it beside the window capture so a take carries the verify Wavee's 30 s song previews and no
// other sound (the owner's own Wavee, a browser, notifications). It is WASAPI process loopback (Windows 10 2004+): an audio
// client activated on the virtual device VAD\Process_Loopback with AUDIOCLIENT_ACTIVATION_PARAMS { PROCESS_LOOPBACK, pid,
// INCLUDE_TARGET_PROCESS_TREE }, 48 kHz stereo 16-bit (the engine converts). Silent stretches are written as silence, so the
// file's time runs with the wall clock from the start. <out.wav>.json records that start's QPC time (the system
// clock the capture's frames also come from) for the muxer to line the two up.

#:property AllowUnsafeBlocks=true
#:property TargetFramework=net11.0-windows
#:property PublishAot=false
#:property BuiltInComInteropSupport=true

using System.Diagnostics;
using System.Runtime.InteropServices;

if (args.Length < 2 || !int.TryParse(args[0], out int pid)) { Console.Error.WriteLine("usage: Record-AppAudio.cs <pid> <out.wav>"); return 64; }
string outPath = Path.GetFullPath(args[1]);

const int Rate = 48000, Channels = 2, Bits = 16, BlockAlign = Channels * Bits / 8;
var activation = new AudioClientActivationParams { ActivationType = 1, TargetProcessId = (uint)pid, ProcessLoopbackMode = 0 };
IntPtr paramsPtr = Marshal.AllocHGlobal(Marshal.SizeOf<AudioClientActivationParams>());
Marshal.StructureToPtr(activation, paramsPtr, false);
var prop = new PropVariantBlob { vt = 65 /* VT_BLOB */, cbSize = (uint)Marshal.SizeOf<AudioClientActivationParams>(), pBlobData = paramsPtr };
IntPtr propPtr = Marshal.AllocHGlobal(Marshal.SizeOf<PropVariantBlob>());
Marshal.StructureToPtr(prop, propPtr, false);

var handler = new Completion();
Guid iidAudioClient = typeof(IAudioClient).GUID;
Native.ActivateAudioInterfaceAsync("VAD\\Process_Loopback", ref iidAudioClient, propPtr, handler, out _);
if (!handler.Done.Wait(5000)) { Console.Error.WriteLine("activation timed out"); return 2; }
handler.Op!.GetActivateResult(out int hrActivate, out object? unk);
if (hrActivate < 0 || unk is not IAudioClient client) { Console.Error.WriteLine($"activation failed 0x{hrActivate:x8}"); return 3; }

var fmt = new WaveFormatEx { wFormatTag = 1, nChannels = Channels, nSamplesPerSec = Rate, nAvgBytesPerSec = Rate * BlockAlign, nBlockAlign = BlockAlign, wBitsPerSample = Bits, cbSize = 0 };
const uint Loopback = 0x00020000, AutoConvert = 0x80000000, SrcDefaultQuality = 0x08000000;
Guid empty = Guid.Empty;
int hr = client.Initialize(0, Loopback | AutoConvert | SrcDefaultQuality, 2_000_000, 0, ref fmt, ref empty);
if (hr < 0) { Console.Error.WriteLine($"Initialize failed 0x{hr:x8}"); return 4; }
Guid iidCapture = typeof(IAudioCaptureClient).GUID;
hr = client.GetService(ref iidCapture, out object captureObj);
if (hr < 0) { Console.Error.WriteLine($"GetService failed 0x{hr:x8}"); return 5; }
var capture = (IAudioCaptureClient)captureObj;

using var wav = new FileStream(outPath, FileMode.Create, FileAccess.Write);
wav.Write(new byte[44]);   // the header, written at the end
long dataBytes = 0;
var stop = new ManualResetEventSlim(false);
_ = Task.Run(() => { while (Console.ReadLine() is { } line) if (line.Trim() == "q") break; stop.Set(); });
client.Start();
long startQpc = Stopwatch.GetTimestamp(), lastPacket = startQpc;
byte[] buffer = new byte[Rate * BlockAlign];
while (!stop.IsSet)
{
    Thread.Sleep(5);
    bool got = false;
    while (true)
    {
        if (capture.GetNextPacketSize(out uint packet) < 0 || packet == 0) break;
        if (capture.GetBuffer(out IntPtr data, out uint frames, out uint flags, out _, out ulong qpc100ns) < 0) break;
        got = true;
        long at = Stopwatch.GetTimestamp();
        if ((at - lastPacket) * 1000 / Stopwatch.Frequency > 100)
        {
            // the first packet after a quiet stretch: the silence runs up to the moment this packet's frames end (now)
            long pad = (at - startQpc) * Rate / Stopwatch.Frequency - frames - dataBytes / BlockAlign;
            while (pad > 0)
            {
                int padBytes = (int)Math.Min(pad * BlockAlign, buffer.Length);
                Array.Clear(buffer, 0, padBytes);
                wav.Write(buffer, 0, padBytes);
                dataBytes += padBytes;
                pad -= padBytes / BlockAlign;
            }
        }
        lastPacket = at;
        int bytes = (int)frames * BlockAlign;
        if (bytes > buffer.Length) buffer = new byte[bytes];
        if ((flags & 0x2) != 0) Array.Clear(buffer, 0, bytes);   // AUDCLNT_BUFFERFLAGS_SILENT
        else Marshal.Copy(data, buffer, 0, bytes);
        wav.Write(buffer, 0, bytes);
        dataBytes += bytes;
        capture.ReleaseBuffer(frames);
    }
    // A process that plays nothing delivers no packets at all: keep the file on the wall clock with silence, 100 ms behind
    // (a packet that is merely late lands before the pad reaches it).
    long now = Stopwatch.GetTimestamp();
    if (!got && (now - lastPacket) * 1000 / Stopwatch.Frequency > 100)
    {
        long dueFrames = (now - startQpc) * Rate / Stopwatch.Frequency - Rate / 10;
        long pad = dueFrames - dataBytes / BlockAlign;
        if (pad > 0)
        {
            int bytes = (int)Math.Min(pad * BlockAlign, buffer.Length);
            Array.Clear(buffer, 0, bytes);
            wav.Write(buffer, 0, bytes);
            dataBytes += bytes;
        }
    }
}
client.Stop();
wav.Position = 0;
using (var w = new BinaryWriter(wav, System.Text.Encoding.ASCII, leaveOpen: true))
{
    w.Write("RIFF"u8.ToArray()); w.Write((int)(36 + dataBytes)); w.Write("WAVE"u8.ToArray());
    w.Write("fmt "u8.ToArray()); w.Write(16); w.Write((short)1); w.Write((short)Channels); w.Write(Rate); w.Write(Rate * BlockAlign);
    w.Write((short)BlockAlign); w.Write((short)Bits);
    w.Write("data"u8.ToArray()); w.Write((int)dataBytes);
}
File.WriteAllText(outPath + ".json", "{ \"startQpc100ns\": " + (long)(startQpc * (10_000_000.0 / Stopwatch.Frequency)) + ", \"seconds\": " + (dataBytes / (double)(Rate * BlockAlign)).ToString(System.Globalization.CultureInfo.InvariantCulture) + " }");
Console.WriteLine($"wrote {outPath}: {dataBytes / (double)(Rate * BlockAlign):0.00} s");
return 0;

[StructLayout(LayoutKind.Sequential)]
struct AudioClientActivationParams { public int ActivationType; public uint TargetProcessId; public int ProcessLoopbackMode; }

[StructLayout(LayoutKind.Sequential)]
struct PropVariantBlob { public ushort vt; public ushort r1, r2, r3; public uint cbSize; public IntPtr pBlobData; }

[StructLayout(LayoutKind.Sequential, Pack = 2)]
struct WaveFormatEx { public ushort wFormatTag; public ushort nChannels; public int nSamplesPerSec; public int nAvgBytesPerSec; public ushort nBlockAlign; public ushort wBitsPerSample; public ushort cbSize; }

static class Native
{
    [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = false)]
    public static extern void ActivateAudioInterfaceAsync([MarshalAs(UnmanagedType.LPWStr)] string path, ref Guid riid, IntPtr activationParams,
        IActivateAudioInterfaceCompletionHandler handler, out IActivateAudioInterfaceAsyncOperation op);
}

[ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IActivateAudioInterfaceAsyncOperation { void GetActivateResult(out int hr, [MarshalAs(UnmanagedType.IUnknown)] out object? iface); }

[ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IActivateAudioInterfaceCompletionHandler { void ActivateCompleted(IActivateAudioInterfaceAsyncOperation op); }

[ComImport, Guid("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAgileObject { }

[ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioClient
{
    [PreserveSig] int Initialize(int shareMode, uint flags, long bufferDuration, long periodicity, ref WaveFormatEx format, ref Guid session);
    [PreserveSig] int GetBufferSize(out uint frames);
    [PreserveSig] int GetStreamLatency(out long latency);
    [PreserveSig] int GetCurrentPadding(out uint padding);
    [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closest);
    [PreserveSig] int GetMixFormat(out IntPtr format);
    [PreserveSig] int GetDevicePeriod(out long def, out long min);
    [PreserveSig] int Start();
    [PreserveSig] int Stop();
    [PreserveSig] int Reset();
    [PreserveSig] int SetEventHandle(IntPtr h);
    [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
}

[ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioCaptureClient
{
    [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition);
    [PreserveSig] int ReleaseBuffer(uint frames);
    [PreserveSig] int GetNextPacketSize(out uint frames);
}

sealed class Completion : IActivateAudioInterfaceCompletionHandler, IAgileObject
{
    public readonly ManualResetEventSlim Done = new(false);
    public IActivateAudioInterfaceAsyncOperation? Op;
    public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation op) { Op = op; Done.Set(); }
}
