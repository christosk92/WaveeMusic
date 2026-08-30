// FluentGpu PlayReady UWP helper — produces a CROSS-PROCESS-SHAREABLE DirectComposition surface handle of video
// and publishes it (PID + handle value) so the full-trust FluentGpu engine can DuplicateHandle it and composite it
// through the spine's BindSurfaceHandle hole-punch.
//
// This is the producer half of the "cross-process protected-surface pipe" integration milestone. It is built two
// ways from the SAME source:
//   * build-console.cmd  -> a plain full-trust Win32 console exe (fast iteration; proves the pipe mechanics)
//   * build-uwp.cmd      -> a genuine AppContainer UWP CoreApplication app (the InboxOnly PlayReady context)
// The FG_UWP macro (set by build-uwp.cmd) selects the UWP entry point + LocalState coord path.
//
// Modes (argv[1]):
//   pattern           - render a magenta/cyan two-tone test pattern into a shareable DComp surface (NO media/DRM).
//                       Proves the cross-process DComp-handle pipe end to end with deterministic pixels.
//   clear   <url>     - decode a CLEAR MP4 via IMFMediaEngineEx windowless swap-chain -> GetVideoSwapchainHandle
//                       (the handle IS a shareable DComp surface handle). Proves the cross-process VIDEO pipe.
//   protected         - the proven UWP WinRT MediaProtectionManager+MediaPlayer PlayReady path (Axinom vector) ->
//                       reach Playing+license, and surface the protected DComp handle. Also probes whether the
//                       MF-CDM SetPMPHostApp path now succeeds in the genuine-UWP InboxOnly context.
//
// Coordination: the helper writes a coord file (PID, HANDLE value, KIND, W, H, STATE) that the engine reads. The
// helper stays alive (re-presenting) until a stop file appears or a lifetime ceiling elapses, so DWM keeps reading
// live pixels from the producer swap chain while the engine composites the duplicated handle.

#include <windows.h>
#include <d3d11.h>
#include <d3d11_1.h>
#include <dxgi1_3.h>
#include <dcomp.h>
#include <appmodel.h>
#include <processthreadsapi.h>
#include <mfapi.h>
#include <mfmediaengine.h>
#include <mfidl.h>
#include <mferror.h>

#include <string>
#include <sstream>
#include <iomanip>
#include <fstream>
#include <chrono>
#include <thread>
#include <cstdint>
#include <cstddef>   // offsetof — the per-field ABI guard (FG_DESC_HAS)
#include <cmath>
#include <cstdio>
#include <atomic>
#include <mutex>
#include <new>        // placement-new for the never-destroyed session HttpClient
#include <functional>   // the feeder's cancel predicate (a seek/shutdown must not wait out an in-flight segment GET)
#include <algorithm>

#pragma comment(lib, "d3d11.lib")
#pragma comment(lib, "dxgi.lib")
#pragma comment(lib, "dcomp.lib")
#pragma comment(lib, "mfplat.lib")
#pragma comment(lib, "mfuuid.lib")
#pragma comment(lib, "mf.lib")
#pragma comment(lib, "ole32.lib")
#pragma comment(lib, "oleaut32.lib")

#ifdef FG_UWP
#include <mfcontentdecryptionmodule.h>
#include <mfidl.h>
#include <windows.media.protection.h>   // ABI::...::IMediaProtectionPMPServer (PMP-server sharing to the media engine)
#include <propsys.h>
#include <propvarutil.h>
#include <wincrypt.h>
#include <vector>
#pragma comment(lib, "propsys.lib")
#pragma comment(lib, "crypt32.lib")
#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.Foundation.Collections.h>
#include <winrt/Windows.ApplicationModel.Core.h>
#include <winrt/Windows.UI.Core.h>
#include <winrt/Windows.Storage.h>
#include <winrt/Windows.Storage.Streams.h>
#include <winrt/Windows.Media.Protection.h>
#include <winrt/Windows.Media.Protection.PlayReady.h>
#include <winrt/Windows.Media.Core.h>
#include <winrt/Windows.Media.Playback.h>
#include <winrt/Windows.Media.Streaming.Adaptive.h>
#include <winrt/Windows.Web.Http.h>
#include <winrt/Windows.Web.Http.Headers.h>
#endif

#ifndef COMPOSITIONOBJECT_ALL_ACCESS
#define COMPOSITIONOBJECT_READ  0x0001L
#define COMPOSITIONOBJECT_WRITE 0x0002L
#define COMPOSITIONOBJECT_ALL_ACCESS (COMPOSITIONOBJECT_READ | COMPOSITIONOBJECT_WRITE)
#endif

// ── Global coord/log state ──────────────────────────────────────────────────────────────────────────────────────
static std::wstring g_coordPath;   // where the coord file is written
static std::wstring g_logPath;     // running log (diagnostics)
static std::wstring g_stopPath;    // touch this file to ask the helper to exit

// ── Duplex protocol channel (engine <-> helper) — newline-delimited "seq\tVERB k=v..." frames in LocalState. ────────
static std::wstring g_evtPath;              // helper -> engine events (append-only; the engine tails it)
static std::wstring g_cmdPath;              // engine -> helper commands (append-only; the helper tracks the last seq)
static uint64_t g_evtSeq = 0;               // outgoing event sequence (== the channel seq the engine reads)
static uint64_t g_cmdSeq = 0;               // highest consumed command sequence
static volatile bool g_shutdownRequested = false;
// Owner-liveness heartbeat: once the engine has sent ANY command (incl. periodic PING), a >15s gap means the engine
// died/closed WITHOUT a clean SHUTDOWN — so self-exit instead of lingering. Stays dormant until the first command, so a
// ping-less launcher (deploy-run.ps1) keeps the helper alive normally.
static bool g_sawAnyCmd = false;
static uint64_t g_lastCmdActivityMs = 0;

#ifdef FG_DESKTOP_DLL
// Normal Win32 in-process backend state. The managed FluentGpu host polls this small blittable snapshot; there is no
// helper process, package activation, IPC channel, or handle duplication. The only process boundary is Windows'
// OS-owned Protected Media Path (mfpmp.exe), created internally by the PlayReady CDM.
struct FgPlayReadySnapshot
{
    int state;          // 0 idle, 1 loading, 2 playing, 3 paused, 4 stopped, 5 error
    int errorHr;
    uint64_t handle;    // same-process DirectComposition surface HANDLE
    int width;
    int height;
    int64_t positionMs;
    int64_t durationMs;
    uint64_t playAppliedSeq;
    uint64_t seekAppliedSeq;
    uint64_t volumeAppliedSeq;
    uint64_t rateAppliedSeq;
};

static std::atomic<int>      g_desktopState{0};
static std::atomic<int>      g_desktopErrorHr{0};
static std::atomic<uint64_t> g_desktopHandle{0};
static std::atomic<int>      g_desktopWidth{0}, g_desktopHeight{0};
static std::atomic<int64_t>  g_desktopPositionMs{0}, g_desktopDurationMs{0};
// Transport is delivered as SEPARATE, non-clobbering slots (the old single g_desktopCommand slot lost commands: the
// managed re-assert stored Play on nearly every frame and overwrote a Seek/Pause issued in the same 80ms window).
// Play/pause is a LEVEL the MTA loop reconciles against the engine's real paused state; seek + volume are one-shot
// slots (-1 = none) drained independently. This removes the need for the managed 60Hz Play re-assert entirely.
static std::atomic<int>      g_desktopDesiredPlay{1};   // 1 = playing, 0 = paused (managed writes; loop reconciles)
static std::atomic<int64_t>  g_desktopSeekMs{0};
// 0 = exact (SetCurrentTime), 1 = approximate (SetCurrentTimeEx + MF_MEDIA_ENGINE_SEEK_MODE_APPROXIMATE). The UI asks
// for approximate while the thumb is moving and exact on the commit; there is still ONE seek slot (latest-wins),
// because MF queues seeks FIFO and the UI runs far ahead of the pipeline.
static std::atomic<int32_t>  g_desktopSeekMode{0};
static std::atomic<int64_t>  g_desktopVolMilli{1000000};
static std::atomic<int64_t>  g_desktopRateMilli{1000000};
static std::atomic<uint64_t> g_desktopPlayRequestSeq{0}, g_desktopPlayAppliedSeq{0};
static std::atomic<uint64_t> g_desktopSeekRequestSeq{0}, g_desktopSeekAppliedSeq{0};
// A streaming CENC seek may target a segment the bounded feeder has not downloaded yet. The media-engine thread must
// not call SetCurrentTime until the feeder has appended the target video/audio segment, otherwise the source can only
// choose the last buffered keyframe and playback spends tens of seconds catching up to the requested position.
static std::atomic<uint64_t> g_desktopSeekBufferedSeq{0};
static std::atomic<uint64_t> g_desktopVolumeRequestSeq{0}, g_desktopVolumeAppliedSeq{0};
static std::atomic<uint64_t> g_desktopRateRequestSeq{0}, g_desktopRateAppliedSeq{0};
static std::atomic<bool>     g_desktopRunning{false};
static std::atomic<uint64_t> g_desktopBytesDownloaded{0};
static std::atomic<uint64_t> g_desktopDownloadElapsedMs{0};
static std::atomic<int64_t>  g_desktopForwardBufferedMs{0};
static std::atomic<int32_t>  g_desktopActiveVideoRepresentation{-1};

// V2 is a separate export: extending the original snapshot in place would make a new DLL overwrite an old managed
// caller's smaller buffer. Every field is copied only when the caller-provided size contains it.
struct FgPlayReadySnapshotV2
{
    uint32_t structSize;
    FgPlayReadySnapshot legacy;
    uint64_t bytesDownloaded;
    int64_t forwardBufferedMs;
    int32_t activeVideoRepresentation;
    uint64_t representationAppliedSeq;
    uint64_t downloadElapsedMs;
};

#define FG_SNAPSHOT_HAS(value, size, field) \
    ((size) >= (uint32_t)(offsetof(FgPlayReadySnapshotV2, field) + sizeof((value)->field)))

struct FgVideoSwitchRequest
{
    int32_t index = -1;
    std::wstring initUrl, segmentBaseUrl, segmentPrefix, segmentSuffix;
};
static std::mutex            g_videoSwitchMx;
static FgVideoSwitchRequest  g_videoSwitch;
static std::atomic<uint64_t> g_videoSwitchRequestSeq{0}, g_videoSwitchAppliedSeq{0};

// ── M5 generalized open ABI ─────────────────────────────────────────────────────────────────────────────────────────
// The managed host hands native a SOURCE DESCRIPTOR (a DASH init+segment URI template and/or an explicit PSSH + HTTP
// headers) plus a LICENSE CALLBACK. License acquisition now lives in managed app code (the WithDrm relay): when the CDM
// raises a KeyMessage, native invokes the callback with the challenge bytes and hands the returned license blob to the
// CDM's Update(). The old hardcoded Axinom license POST survives ONLY behind FG_CENC_BAKED_AXINOM=1 (on-box A/B). When
// every descriptor source field is empty native falls back to the baked Axinom singlekey vector so the legacy
// FgPlayReadyRun shim still demuxes a known-good stream.
//
// Native calls this to receive the license blob for a challenge; managed calls `deliver` to hand bytes back (an
// allocation-safe sink: native owns the destination buffer, so there is no cross-allocator free).
typedef void    (__stdcall *FgLicenseDeliver)(void* deliverCtx, const uint8_t* license, int32_t licenseLen);
// Managed provides this; native calls it on the CDM/MF thread. Returns 0 (S_OK) when `deliver` was called with a
// license, negative on failure/timeout (native then surfaces a DRM error). `keyIdHex` may be null.
typedef int32_t (__stdcall *FgLicenseCallback)(void* ctx, const uint8_t* challenge, int32_t challengeLen,
                                               const wchar_t* keyIdHex, FgLicenseDeliver deliver, void* deliverCtx);

struct FgPlayReadyOpenDesc
{
    uint32_t       structSize;      // sizeof(FgPlayReadyOpenDesc) — ABI version guard
    int32_t        mode;            // 0 protected custom CENC, 1 clear diag
    const wchar_t* initUrl;         // init-segment URL (null => baked Axinom source)
    const wchar_t* segmentBaseUrl;  // base URL for numbered media segments
    const wchar_t* segmentPrefix;   // segment name prefix
    const wchar_t* segmentSuffix;   // segment name suffix (e.g. ".m4s")
    int32_t        startNumber;     // first segment number
    int32_t        segmentCount;    // segment count to fetch
    const uint8_t* pssh;            // optional explicit PlayReady PSSH init data (null => parse from init)
    int32_t        psshLen;
    const wchar_t* httpHeaders;     // optional "Name: Value\n" lines applied to segment fetches (null => none)
    const wchar_t* licenseServerUrl;// optional license destination hint (advisory; the callback owns licensing)
    int32_t        segmentStride;   // ABI-appended: segment-number step (1 = numbered $Number$; N = Spotify's N-second
                                    // timestamped segments, e.g. startNumber=0, stride=segment_length). Guarded on structSize.
    // ABI-appended: the paired AUDIO representation — the video's OWN soundtrack. A music video is its own edit (intros,
    // spoken pre/post-roll), so this audio is NOT interchangeable with the plain song's audio track. It sits on the SAME
    // segment grid as the video (startNumber / segmentCount / segmentStride are shared) under the SAME content key, so it
    // needs no separate licence acquisition. All null => video only.
    const wchar_t* audioInitUrl;
    const wchar_t* audioSegmentBaseUrl;
    const wchar_t* audioSegmentPrefix;
    const wchar_t* audioSegmentSuffix;
};

// An ABI-appended field is present iff the caller's struct is large enough to CONTAIN it. A `structSize >= sizeof(whole
// struct)` test breaks the moment another field is appended — every older-but-still-valid caller would suddenly fail the
// check for fields it genuinely does send — so version the fields individually.
#define FG_DESC_HAS(desc, field) \
    ((desc)->structSize >= (uint32_t)(offsetof(FgPlayReadyOpenDesc, field) + sizeof((desc)->field)))

// The active open descriptor (copied deep by FgPlayReadyRunEx) + the managed license callback.
static std::wstring       g_openInitUrl, g_openSegBase, g_openSegPrefix, g_openSegSuffix, g_openHeaders, g_openLicenseUrl;
static std::wstring       g_openAudioInitUrl, g_openAudioSegBase, g_openAudioSegPrefix, g_openAudioSegSuffix;
static int                g_openStartNumber = 1, g_openSegCount = 6, g_openSegStride = 1;
static std::vector<uint8_t> g_openPssh;
static FgLicenseCallback  g_licenseCallback = nullptr;
static void*              g_licenseCtx = nullptr;
static std::wstring       g_kidHex;   // hex of the content KID (parsed from init) for the license request
#endif

static void LogLine(const std::string& s)
{
    OutputDebugStringA((s + "\n").c_str());
    if (!g_logPath.empty())
    {
        std::ofstream f(g_logPath, std::ios::app | std::ios::binary);
        f << s << "\r\n";
    }
    std::fputs((s + "\n").c_str(), stderr);
}

static void WriteCoord(const char* kind, uint64_t pid, uint64_t handle, int w, int h, const char* state)
{
    std::stringstream v;
    v << "KIND=" << kind << "\r\n"
      << "PID=" << pid << "\r\n"
      << "HANDLE=" << handle << "\r\n"
      << "W=" << w << "\r\n"
      << "H=" << h << "\r\n"
      << "STATE=" << state << "\r\n";
    std::string text = v.str();
    // Write atomically-ish: temp then rename would be ideal, but a truncating write is fine for our poll loop.
    std::ofstream f(g_coordPath, std::ios::binary | std::ios::trunc);
    f.write(text.data(), (std::streamsize)text.size());
    f.close();
    LogLine(std::string("[coord] ") + kind + " pid=" + std::to_string(pid) + " handle=" + std::to_string(handle) +
            " " + std::to_string(w) + "x" + std::to_string(h) + " state=" + state);
#ifdef FG_DESKTOP_DLL
    g_desktopHandle.store(handle, std::memory_order_release);
    g_desktopWidth.store(w, std::memory_order_release);
    g_desktopHeight.store(h, std::memory_order_release);
    if (strcmp(state, "playing") == 0 || strcmp(state, "ready") == 0)
        g_desktopState.store(2, std::memory_order_release);
    else if (strcmp(state, "error") == 0)
        g_desktopState.store(5, std::memory_order_release);
#endif
}

static bool StopRequested()
{
    if (g_shutdownRequested) return true;   // engine sent SHUTDOWN over the command channel
    if (g_stopPath.empty()) return false;
    DWORD a = GetFileAttributesW(g_stopPath.c_str());
    return a != INVALID_FILE_ATTRIBUTES;
}

// Append one event frame the engine's FileSidecarChannel reads (its channel seq == our g_evtSeq).
static void WriteEvent(const std::string& verb, const std::string& args = "")
{
    if (g_evtPath.empty()) return;
    std::string line = std::to_string(++g_evtSeq) + "\t" + verb + (args.empty() ? "" : (" " + args)) + "\n";
    std::ofstream f(g_evtPath, std::ios::app | std::ios::binary);
    f.write(line.data(), (std::streamsize)line.size());
    LogLine("[evt] " + verb + (args.empty() ? "" : (" " + args)));
}

// Apply any new engine commands (PLAY/PAUSE/SEEK/VOLUME/STOP/SHUTDOWN). Cheap; called each keep-alive tick. Values for
// these verbs are plain (no percent-escaping), so a simple substring parse is sufficient (LOAD/URLs are ignored here —
// this build uses its built-in Axinom vector).
static void PollCommands(IMFMediaEngine* engine)
{
    if (g_cmdPath.empty()) return;
    std::ifstream f(g_cmdPath, std::ios::binary);
    if (!f) return;
    std::string line;
    while (std::getline(f, line))
    {
        if (!line.empty() && line.back() == '\r') line.pop_back();
        size_t tab = line.find('\t');
        if (tab == std::string::npos) continue;
        uint64_t seq = strtoull(line.substr(0, tab).c_str(), nullptr, 10);
        if (seq <= g_cmdSeq) continue;
        g_cmdSeq = seq;
        g_sawAnyCmd = true; g_lastCmdActivityMs = GetTickCount64();   // heartbeat: the engine is alive
        std::string rest = line.substr(tab + 1);
        std::string verb = rest.substr(0, rest.find(' '));
        LogLine("[cmd] " + rest);
        if (verb == "SHUTDOWN") { g_shutdownRequested = true; }
        else if (engine)
        {
            if (verb == "PLAY") { engine->Play(); WriteEvent("STATE", "value=playing"); }
            else if (verb == "PAUSE" || verb == "STOP") { engine->Pause(); WriteEvent("STATE", "value=paused"); }
            else if (verb == "VOLUME") { size_t p = rest.find("value="); if (p != std::string::npos) engine->SetVolume(atof(rest.c_str() + p + 6)); }
            else if (verb == "SEEK")   { size_t p = rest.find("ms=");    if (p != std::string::npos) engine->SetCurrentTime(atof(rest.c_str() + p + 3) / 1000.0); }
        }
    }
}

static const char* PolicyName(int v)
{
    return v == 0 ? "All (full-trust/desktop)" : v == 1 ? "InboxOnly (AppContainer/true-UWP)" : "<unknown>";
}

static void LogAppModelPolicy()
{
    // AppPolicyGetMediaFoundationCodecLoading — the confirmation of true-UWP context (InboxOnly) vs full-trust (All).
    typedef LONG (WINAPI *PFN)(HANDLE, int*);
    HMODULE m = LoadLibraryW(L"api-ms-win-appmodel-runtime-l1-1-2.dll");
    if (!m) { LogLine("[policy] appmodel dll not present"); return; }
    auto fn = (PFN)GetProcAddress(m, "AppPolicyGetMediaFoundationCodecLoading");
    if (fn)
    {
        int pol = -99;
        LONG rc = fn((HANDLE)(LONG_PTR)-6 /*GetCurrentThreadEffectiveToken*/, &pol);
        std::stringstream ss; ss << "[policy] AppPolicyGetMediaFoundationCodecLoading rc=" << rc
                                 << " value=" << pol << " (" << PolicyName(pol) << ")";
        LogLine(ss.str());
    }
    FreeLibrary(m);
}

// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════
//  PATTERN producer — create a shareable DComp surface handle + a producer swap chain, render a two-tone pattern.
//  Mirrors D3D12Device.CreateEngineTestSurfaceHandle but in D3D11 (simpler; no fences/command lists needed).
// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════
static int RunPattern(int w, int h)
{
    LogAppModelPolicy();

    // 1. D3D11 device (BGRA support for composition).
    ID3D11Device* dev = nullptr;
    ID3D11DeviceContext* ctx = nullptr;
    UINT flags = D3D11_CREATE_DEVICE_BGRA_SUPPORT;
    D3D_FEATURE_LEVEL fl;
    HRESULT hr = D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, flags, nullptr, 0,
                                   D3D11_SDK_VERSION, &dev, &fl, &ctx);
    if (FAILED(hr)) { LogLine("[pattern] D3D11CreateDevice hr=0x" + std::to_string((uint32_t)hr)); return 2; }

    // 2. The shareable DComp surface handle (COMPOSITIONOBJECT_ALL_ACCESS so it can be duplicated across processes).
    HANDLE surfHandle = nullptr;
    hr = DCompositionCreateSurfaceHandle(COMPOSITIONOBJECT_ALL_ACCESS, nullptr, &surfHandle);
    if (FAILED(hr)) { LogLine("[pattern] DCompositionCreateSurfaceHandle hr=0x" + std::to_string((uint32_t)hr)); return 3; }
    LogLine("[pattern] DCompositionCreateSurfaceHandle OK handle=" + std::to_string((uint64_t)(uintptr_t)surfHandle));

    // 3. A DXGI composition-surface-handle swap chain that renders INTO that handle (the producer side).
    IDXGIFactoryMedia* factoryMedia = nullptr;
    hr = CreateDXGIFactory2(0, __uuidof(IDXGIFactoryMedia), (void**)&factoryMedia);
    if (FAILED(hr)) { LogLine("[pattern] CreateDXGIFactory2(IDXGIFactoryMedia) hr=0x" + std::to_string((uint32_t)hr)); return 4; }

    DXGI_SWAP_CHAIN_DESC1 sd = {};
    sd.Width = (UINT)w;
    sd.Height = (UINT)h;
    sd.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
    sd.SampleDesc.Count = 1;
    sd.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
    sd.BufferCount = 2;
    sd.Scaling = DXGI_SCALING_STRETCH;
    sd.SwapEffect = DXGI_SWAP_EFFECT_FLIP_SEQUENTIAL;
    sd.AlphaMode = DXGI_ALPHA_MODE_IGNORE;

    IDXGISwapChain1* sc = nullptr;
    hr = factoryMedia->CreateSwapChainForCompositionSurfaceHandle(dev, surfHandle, &sd, nullptr, &sc);
    if (FAILED(hr)) { LogLine("[pattern] CreateSwapChainForCompositionSurfaceHandle hr=0x" + std::to_string((uint32_t)hr)); return 5; }
    LogLine("[pattern] producer swap chain created.");

    // 4. Render one frame: whole = magenta, left half = cyan (a two-tone split, matches the engine's M0 test surface).
    auto present = [&]() -> HRESULT
    {
        ID3D11Texture2D* buf = nullptr;
        HRESULT h2 = sc->GetBuffer(0, __uuidof(ID3D11Texture2D), (void**)&buf);
        if (FAILED(h2)) return h2;
        ID3D11RenderTargetView* rtv = nullptr;
        h2 = dev->CreateRenderTargetView(buf, nullptr, &rtv);
        if (SUCCEEDED(h2))
        {
            const float magenta[4] = { 1.f, 0.f, 1.f, 1.f };
            const float cyan[4]    = { 0.f, 1.f, 1.f, 1.f };
            ctx->ClearRenderTargetView(rtv, magenta);
            ID3D11DeviceContext1* ctx1 = nullptr;
            if (SUCCEEDED(ctx->QueryInterface(__uuidof(ID3D11DeviceContext1), (void**)&ctx1)))
            {
                D3D11_RECT left = { 0, 0, w / 2, h };
                ctx1->ClearView(rtv, cyan, &left, 1);
                ctx1->Release();
            }
            ctx->Flush();
            rtv->Release();
        }
        if (buf) buf->Release();
        return sc->Present(0, 0);
    };

    hr = present();
    if (FAILED(hr)) { LogLine("[pattern] first Present hr=0x" + std::to_string((uint32_t)hr)); return 6; }

    WriteCoord("pattern", GetCurrentProcessId(), (uint64_t)(uintptr_t)surfHandle, w, h, "ready");
    WriteEvent("SURFACE", "handle=" + std::to_string((uint64_t)(uintptr_t)surfHandle) + " pid=" +
               std::to_string(GetCurrentProcessId()) + " w=" + std::to_string(w) + " h=" + std::to_string(h));
    WriteEvent("STATE", "value=playing");
    LogLine("[pattern] READY - publishing handle; keeping producer alive until SHUTDOWN.");

    // 5. Keep the producer alive so DWM reads live pixels while the engine composites. Re-present periodically.
    while (!StopRequested())
    {
        std::this_thread::sleep_for(std::chrono::milliseconds(120));
        present();
        PollCommands(nullptr);   // no media engine here; still feeds the heartbeat + honors SHUTDOWN
        if (g_sawAnyCmd && (GetTickCount64() - g_lastCmdActivityMs) > 15000) { g_shutdownRequested = true; }
    }
    WriteEvent("STATE", "value=stopped"); WriteEvent("BYE");
    LogLine("[pattern] exiting (shutdown).");

    sc->Release();
    factoryMedia->Release();
    if (ctx) ctx->Release();
    if (dev) dev->Release();
    // Intentionally leave surfHandle open until process exit (the engine may still hold the duplicate).
    return 0;
}

// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════
//  CLEAR video producer — IMFMediaEngineEx windowless swap-chain decode of a CLEAR MP4. GetVideoSwapchainHandle
//  returns a shareable DComp surface handle (created by MF via DCompositionCreateSurfaceHandle). Ports the proven
//  VideoMediaEngine.cs sequence (incl. the ID3D10Multithread::SetMultithreadProtected slot-5 fix).
// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════
struct MediaEngineNotify : public IMFMediaEngineNotify
{
    std::atomic<long> rc{1};
    std::atomic<bool> metadata{false}, canplay{false}, playing{false}, error{false};
    std::atomic<int> lastEvent{-1}, errCode{0}, errHr{0};

    HRESULT __stdcall QueryInterface(REFIID riid, void** ppv) override
    {
        if (!ppv) return E_POINTER;
        if (riid == __uuidof(IUnknown) || riid == __uuidof(IMFMediaEngineNotify)) { *ppv = this; AddRef(); return S_OK; }
        *ppv = nullptr; return E_NOINTERFACE;
    }
    ULONG __stdcall AddRef() override { return (ULONG)++rc; }
    ULONG __stdcall Release() override { long v = --rc; if (v == 0) delete this; return (ULONG)v; }
    HRESULT __stdcall EventNotify(DWORD ev, DWORD_PTR p1, DWORD p2) override
    {
        lastEvent = (int)ev;
        switch (ev)
        {
            case MF_MEDIA_ENGINE_EVENT_LOADEDMETADATA: metadata = true; break;
            case MF_MEDIA_ENGINE_EVENT_CANPLAY:        canplay = true; break;
            case MF_MEDIA_ENGINE_EVENT_PLAY:
                LogLine("[media-engine] PLAY requested");
                break;
            case MF_MEDIA_ENGINE_EVENT_PLAYING:
                playing = true; canplay = true;
                LogLine("[media-engine] PLAYING");
                break;
            case MF_MEDIA_ENGINE_EVENT_PAUSE:
                playing = false;
                LogLine("[media-engine] PAUSE");
                break;
            case MF_MEDIA_ENGINE_EVENT_SEEKING:
                LogLine("[media-engine] SEEKING");
                break;
            case MF_MEDIA_ENGINE_EVENT_SEEKED:
                LogLine("[media-engine] SEEKED");
                break;
            case MF_MEDIA_ENGINE_EVENT_ENDED:
                // Natural end of media. This was the whole track-end deadlock: without an ENDED state the managed
                // side only ever saw "paused", so auto-advance never fired, FgPlayReadyStop was never called, and
                // the g_desktopRunning latch wedged every later session with ERROR_BUSY. Ended is a STATE, not a
                // shutdown — the session stays alive so a seek-after-end still works.
                playing = false;
                LogLine("[media-engine] ENDED");
#ifdef FG_DESKTOP_DLL
                g_desktopState.store(6, std::memory_order_release);   // 6 = ended (see ReconcileTransport)
#endif
                break;
            case MF_MEDIA_ENGINE_EVENT_ERROR:          error = true; errCode = (int)p1; errHr = (int)p2; break;
        }
        return S_OK;
    }
};

#ifdef FG_DESKTOP_DLL
// Reconcile the media engine to the managed-published transport intents (desktop DLL path). Drains the one-shot seek +
// volume slots, then asserts the desired play/pause LEVEL against IMFMediaEngine::IsPaused. A command is retried only
// while the engine has not reached the requested level; once it does, no Play/Pause calls are made. Seek has its OWN
// slot, so the old
// single-command clobbering (a Play overwriting a Seek in the same 80ms window) is gone. Runs on the owning MTA loop
// thread only. The custom CENC source implements the corresponding pause/resume/seek events in CencMediaSource.h.
static void ReconcileTransport(IMFMediaEngine* engine, IMFMediaEngineEx* engineEx, bool waitForSeekBuffer = false)
{
    auto hrText = [](HRESULT h) { std::stringstream ss; ss << "0x" << std::hex << (uint32_t)h; return ss.str(); };
    uint64_t seekSeq = g_desktopSeekRequestSeq.load(std::memory_order_acquire);
    if (seekSeq != g_desktopSeekAppliedSeq.load(std::memory_order_acquire) &&
        (!waitForSeekBuffer || g_desktopSeekBufferedSeq.load(std::memory_order_acquire) == seekSeq))
    {
        int64_t seekMs = g_desktopSeekMs.load(std::memory_order_acquire);
        // Scrub/preview seeks ask for APPROXIMATE: the engine lands on the nearest keyframe instead of decoding a
        // preroll to the exact frame, which is the difference between a scrub that tracks the thumb and one that
        // lurches. The commit seek is exact. NOTE the surface is never blanked here — in windowless swapchain mode
        // UpdateVideoStream(nullptr,nullptr,nullptr) on the keep-alive tick repaints the latest frame, so the old
        // picture simply holds until the new one decodes.
        const bool approximate = g_desktopSeekMode.load(std::memory_order_acquire) != 0;
        HRESULT hr = (approximate && engineEx)
            ? engineEx->SetCurrentTimeEx((double)seekMs / 1000.0, MF_MEDIA_ENGINE_SEEK_MODE_APPROXIMATE)
            : engine->SetCurrentTime((double)seekMs / 1000.0);
        LogLine("[transport] SEEK ms=" + std::to_string((long long)seekMs) +
                (approximate ? " mode=approximate" : " mode=exact") + " hr=" + hrText(hr));
        if (SUCCEEDED(hr))
        {
            g_desktopSeekAppliedSeq.store(seekSeq, std::memory_order_release);
            // At rate 0 the MF video renderer does not pre-roll, so a seek issued while PAUSED decodes nothing and the
            // surface keeps showing the pre-seek frame forever. One frame step forces the new frame out (Chromium's
            // shipped workaround for the same renderer behaviour).
            if (engineEx && engine->IsPaused())
            {
                HRESULT hrStep = engineEx->FrameStep(TRUE);
                LogLine("[transport] paused seek -> FrameStep hr=" + hrText(hrStep));
            }
        }
    }
    uint64_t volumeSeq = g_desktopVolumeRequestSeq.load(std::memory_order_acquire);
    if (volumeSeq != g_desktopVolumeAppliedSeq.load(std::memory_order_acquire))
    {
        int64_t volMilli = g_desktopVolMilli.load(std::memory_order_acquire);
        HRESULT hr = engine->SetVolume((double)volMilli / 1000000.0);
        if (FAILED(hr)) LogLine("[transport] VOLUME hr=" + hrText(hr));
        else g_desktopVolumeAppliedSeq.store(volumeSeq, std::memory_order_release);
    }
    uint64_t rateSeq = g_desktopRateRequestSeq.load(std::memory_order_acquire);
    if (rateSeq != g_desktopRateAppliedSeq.load(std::memory_order_acquire))
    {
        int64_t rateMilli = g_desktopRateMilli.load(std::memory_order_acquire);
        HRESULT hr = engine->SetPlaybackRate((double)rateMilli / 1000000.0);
        if (FAILED(hr)) LogLine("[transport] RATE hr=" + hrText(hr));
        else g_desktopRateAppliedSeq.store(rateSeq, std::memory_order_release);
    }

    double nowSec = engine->GetCurrentTime();
    uint64_t playSeq = g_desktopPlayRequestSeq.load(std::memory_order_acquire);
    bool wantsPlay = g_desktopDesiredPlay.load(std::memory_order_acquire) != 0;
    double durSec = engine->GetDuration();
    bool atEnd = durSec > 0.0 && nowSec >= durSec - 0.05;
    if (wantsPlay)
    {
        if (!atEnd && engine->IsPaused())
        {
            HRESULT hr = engine->Play();
            LogLine("[transport] PLAY hr=" + hrText(hr) + " posMs=" +
                    std::to_string((long long)(nowSec * 1000.0)));
        }
    }
    else
    {
        if (!engine->IsPaused())
        {
            HRESULT hr = engine->Pause();
            LogLine("[transport] PAUSE hr=" + hrText(hr) + " posMs=" +
                    std::to_string((long long)(nowSec * 1000.0)));
        }
    }
    bool paused = engine->IsPaused();
    if ((wantsPlay && !paused) || (!wantsPlay && paused))
        g_desktopPlayAppliedSeq.store(playSeq, std::memory_order_release);
    // 6 = ended: play intent is standing but the clock sits at the end of the presentation. This tick runs every
    // 80ms and previously stored plain paused(3) here — silently CLOBBERING the ENDED event's state and hiding the
    // end of the track from the managed side forever (the auto-advance deadlock). The atEnd fold also covers a
    // missed/never-fired MF ENDED event. A user who seeks-to-end while PAUSED keeps reading as paused (no intent).
    g_desktopState.store(wantsPlay && atEnd ? 6 : paused ? 3 : 2, std::memory_order_release);
    g_desktopPositionMs.store((int64_t)(nowSec * 1000.0), std::memory_order_release);
}
#endif

static int RunClear(const std::wstring& url)
{
    LogAppModelPolicy();
    int hr;

    if (FAILED(hr = CoInitializeEx(nullptr, COINIT_MULTITHREADED)) && hr != S_FALSE)
        LogLine("[clear] CoInitializeEx hr=0x" + std::to_string((uint32_t)hr));
    if (FAILED(hr = MFStartup(MF_VERSION, MFSTARTUP_FULL)))
    { LogLine("[clear] MFStartup hr=0x" + std::to_string((uint32_t)hr)); return 2; }

    // D3D11 video device + IMFDXGIDeviceManager (shared with MF).
    ID3D11Device* d3d = nullptr; ID3D11DeviceContext* ctx = nullptr;
    UINT flags = D3D11_CREATE_DEVICE_BGRA_SUPPORT | D3D11_CREATE_DEVICE_VIDEO_SUPPORT;
    if (FAILED(hr = D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, flags, nullptr, 0,
                                      D3D11_SDK_VERSION, &d3d, nullptr, &ctx)))
    { LogLine("[clear] D3D11CreateDevice hr=0x" + std::to_string((uint32_t)hr)); return 3; }
    if (ctx) ctx->Release();

    // SetMultithreadProtected — REQUIRED when the D3D11 device is shared with MF (else source-resolution deadlock).
    // ID3D10Multithread vtable slot 5 = SetMultithreadProtected (the fix documented in VideoMediaEngine.cs).
    {
        void* mt = nullptr;
        GUID iidMt = { 0x9b7e4e00, 0x342c, 0x4106, {0xa1,0x9f,0x4f,0x27,0x04,0xf6,0x89,0xf0} };
        if (SUCCEEDED(d3d->QueryInterface(iidMt, &mt)) && mt)
        {
            auto setProt = (int (STDMETHODCALLTYPE*)(void*, int))(*(void***)mt)[5];
            setProt(mt, 1);
            ((::IUnknown*)mt)->Release();
        }
    }
    UINT resetToken = 0; IMFDXGIDeviceManager* dxgiMgr = nullptr;
    if (FAILED(hr = MFCreateDXGIDeviceManager(&resetToken, &dxgiMgr)))
    { LogLine("[clear] MFCreateDXGIDeviceManager hr=0x" + std::to_string((uint32_t)hr)); return 4; }
    if (FAILED(hr = dxgiMgr->ResetDevice((::IUnknown*)d3d, resetToken)))
    { LogLine("[clear] ResetDevice hr=0x" + std::to_string((uint32_t)hr)); return 5; }

    // Media engine.
    MediaEngineNotify* notify = new MediaEngineNotify();
    IMFAttributes* attrs = nullptr;
    if (FAILED(hr = MFCreateAttributes(&attrs, 4)))
    { LogLine("[clear] MFCreateAttributes hr=0x" + std::to_string((uint32_t)hr)); return 6; }
    attrs->SetUnknown(MF_MEDIA_ENGINE_CALLBACK, (::IUnknown*)notify);
    attrs->SetUnknown(MF_MEDIA_ENGINE_DXGI_MANAGER, (::IUnknown*)dxgiMgr);
    attrs->SetUINT32(MF_MEDIA_ENGINE_VIDEO_OUTPUT_FORMAT, DXGI_FORMAT_B8G8R8A8_UNORM);

    IMFMediaEngineClassFactory* factory = nullptr;
    if (FAILED(hr = CoCreateInstance(CLSID_MFMediaEngineClassFactory, nullptr, CLSCTX_INPROC_SERVER,
                                     IID_PPV_ARGS(&factory))))
    { LogLine("[clear] CoCreateInstance(MFMediaEngineClassFactory) hr=0x" + std::to_string((uint32_t)hr)); return 7; }

    IMFMediaEngine* engine = nullptr;
    if (FAILED(hr = factory->CreateInstance(0, attrs, &engine)))
    { LogLine("[clear] CreateInstance hr=0x" + std::to_string((uint32_t)hr)); return 8; }
    attrs->Release(); factory->Release();

    IMFMediaEngineEx* engineEx = nullptr;
    if (FAILED(hr = engine->QueryInterface(IID_PPV_ARGS(&engineEx))))
    { LogLine("[clear] QI IMFMediaEngineEx hr=0x" + std::to_string((uint32_t)hr)); return 9; }
    if (FAILED(hr = engineEx->EnableWindowlessSwapchainMode(TRUE)))
    { LogLine("[clear] EnableWindowlessSwapchainMode hr=0x" + std::to_string((uint32_t)hr)); return 10; }

    BSTR burl = SysAllocString(url.c_str());
    hr = engineEx->SetSource(burl);
    SysFreeString(burl);
    if (FAILED(hr)) { LogLine("[clear] SetSource hr=0x" + std::to_string((uint32_t)hr)); return 11; }
    engine->SetLoop(TRUE);
    engine->Play();
    LogLine("[clear] media engine playing; waiting for LOADEDMETADATA ...");

    // Wait for metadata -> the swapchain handle is valid.
    auto metaDeadline = std::chrono::steady_clock::now() + std::chrono::seconds(30);
    while (!notify->metadata && !notify->error && std::chrono::steady_clock::now() < metaDeadline)
        std::this_thread::sleep_for(std::chrono::milliseconds(100));
    if (notify->error)
    { LogLine("[clear] media ERROR code=" + std::to_string(notify->errCode) + " hr=0x" + std::to_string((uint32_t)notify->errHr)); return 12; }
    if (!notify->metadata) { LogLine("[clear] no LOADEDMETADATA within timeout."); return 13; }

    DWORD nvw = 0, nvh = 0; engineEx->GetNativeVideoSize(&nvw, &nvh);
    if (nvw == 0) { nvw = 1280; nvh = 720; }
    RECT dst = { 0, 0, (LONG)nvw, (LONG)nvh };
    MFARGB border = { 0, 0, 0, 255 };
    HANDLE swHandle = nullptr;
    auto handleDeadline = std::chrono::steady_clock::now() + std::chrono::seconds(12);
    do
    {
        LONGLONG pts = 0;
        engine->OnVideoStreamTick(&pts);
        engineEx->UpdateVideoStream(nullptr, &dst, &border);
        hr = engineEx->GetVideoSwapchainHandle(&swHandle);
        if (SUCCEEDED(hr) && swHandle) break;
        std::this_thread::sleep_for(std::chrono::milliseconds(60));
    }
    while (std::chrono::steady_clock::now() < handleDeadline && !notify->error);
    if (FAILED(hr) || !swHandle)
    { LogLine("[clear] GetVideoSwapchainHandle hr=0x" + std::to_string((uint32_t)hr)); return 14; }
    LogLine("[clear] LOADEDMETADATA " + std::to_string(nvw) + "x" + std::to_string(nvh) +
            " swapchainHandle=" + std::to_string((uint64_t)(uintptr_t)swHandle));

    // Point the video at the whole swap chain and present the current frame.
    engineEx->UpdateVideoStream(nullptr, &dst, &border);

    WriteCoord("clear", GetCurrentProcessId(), (uint64_t)(uintptr_t)swHandle, (int)nvw, (int)nvh, "playing");
    WriteEvent("SURFACE", "handle=" + std::to_string((uint64_t)(uintptr_t)swHandle) + " pid=" +
               std::to_string(GetCurrentProcessId()) + " w=" + std::to_string((int)nvw) +
               " h=" + std::to_string((int)nvh));
    WriteEvent("STATE", "value=playing");
    LogLine("[clear] READY — publishing swapchain handle; auto-presenting decoded frames (keep-alive).");

    // Keep alive; force a repaint of the latest decoded frame each turn so DWM has live pixels.
    int posTick = 0;
    while (!StopRequested())
    {
        std::this_thread::sleep_for(std::chrono::milliseconds(80));
        LONGLONG pts;
        engine->OnVideoStreamTick(&pts);
        engineEx->UpdateVideoStream(nullptr, nullptr, nullptr);   // repaint latest frame
#ifdef FG_DESKTOP_DLL
        ReconcileTransport(engine, engineEx);
        g_desktopDurationMs.store((int64_t)(engine->GetDuration() * 1000.0), std::memory_order_release);
#else
        PollCommands(engine);
        if (g_sawAnyCmd && (GetTickCount64() - g_lastCmdActivityMs) > 15000) g_shutdownRequested = true;
#endif
        if (++posTick >= 12)
        {
            posTick = 0;
            WriteEvent("POSITION", "ms=" + std::to_string((long long)(engine->GetCurrentTime() * 1000.0)) +
                       " dur=" + std::to_string((long long)(engine->GetDuration() * 1000.0)));
        }
    }
    WriteEvent("STATE", "value=stopped");
    WriteEvent("BYE");
    LogLine("[clear] exiting.");
    engine->Shutdown();
    engineEx->Release(); engine->Release(); notify->Release();
    dxgiMgr->Release(); d3d->Release();
    MFShutdown();
    return 0;
}

// ── Path setup: console writes beside the exe / to a passed dir; UWP writes to LocalState (added in the UWP build). ──
static std::wstring DefaultDir()
{
#if defined(FG_UWP) && !defined(FG_WIN32_PMP)
    // Set later from ApplicationData::Current().LocalFolder() in the UWP entry.
    return L"";
#else
    wchar_t buf[MAX_PATH]; DWORD n = GetModuleFileNameW(nullptr, buf, MAX_PATH);
    std::wstring p(buf, n); size_t slash = p.find_last_of(L"\\/");
    return slash == std::wstring::npos ? L"." : p.substr(0, slash);
#endif
}

#ifdef FG_UWP
int RunProtected();                                 // implemented in the UWP build (probe USABLE + MSE surface producer)
static int RunMse(bool protectedContent, const std::wstring& baseDir);   // MSE-fed IMFMediaEngine -> shareable handle
static int RunCustomSource(const std::wstring& baseDir);   // custom CENC IMFMediaSource -> encrypted samples -> handle
static int RunWin32PmpProbe(const std::wstring& baseDir);  // normal desktop process -> OS mfpmp.exe host/session
static std::atomic<bool> g_mseNoProtWiring{false};  // diagnostic: feed PROTECTED content through MSE with NO CDM/PMP
#endif

// Core dispatch shared by the console (argv) and UWP (mode file) entry points.
static int RunHelperMode(const std::wstring& mode, const std::wstring& arg2, const std::wstring& baseDir)
{
    g_coordPath = baseDir + L"\\fg-helper-coord.txt";
    g_logPath   = baseDir + L"\\fg-helper-log.txt";
    g_stopPath  = baseDir + L"\\fg-helper-stop.txt";
    g_evtPath   = baseDir + L"\\fg-helper-evt.txt";   // helper -> engine (we own; truncate for a fresh session)
    g_cmdPath   = baseDir + L"\\fg-helper-cmd.txt";   // engine -> helper (engine owns; do NOT truncate)
    { std::ofstream f(g_logPath, std::ios::binary | std::ios::trunc); }
    { std::ofstream f(g_evtPath, std::ios::binary | std::ios::trunc); }
    g_evtSeq = 0; g_cmdSeq = 0; g_shutdownRequested = false;
    DeleteFileW(g_stopPath.c_str());

    // Announce liveness on the duplex channel; the engine's sidecar host waits for this before (re)sending commands.
    WriteEvent("HELLO", "pid=" + std::to_string(GetCurrentProcessId()) + " proto=1");

    LogLine("[helper] mode=" + std::string(mode.begin(), mode.end()) +
            " coord=" + std::string(g_coordPath.begin(), g_coordPath.end()));

    if (mode == L"pattern")
    {
        int w = 500, h = 360;
        if (!arg2.empty()) w = _wtoi(arg2.c_str());
        return RunPattern(w, h);
    }
    if (mode == L"clear")
    {
        std::wstring url = !arg2.empty() ? arg2
            : L"https://test-videos.co.uk/vids/bigbuckbunny/mp4/h264/720/Big_Buck_Bunny_720_10s_1MB.mp4";
        return RunClear(url);
    }
#ifdef FG_UWP
    if (mode == L"pmp-probe") return RunWin32PmpProbe(baseDir);
    if (mode == L"protected") return RunProtected();
    if (mode == L"protected-custom") return RunCustomSource(baseDir);  // custom CENC IMFMediaSource (encrypted samples -> CDM)
    if (mode == L"clear-mse") return RunMse(false, baseDir);       // CLEAR control through the SAME MSE->handle path
    if (mode == L"protected-mse") return RunMse(true, baseDir);    // protected MSE without the standalone USABLE probe
    if (mode == L"protected-mse-noprot") { g_mseNoProtWiring = true; return RunMse(true, baseDir); }  // diag: CENC via MSE, no CDM/PMP
#endif
    LogLine("[helper] unknown mode '" + std::string(mode.begin(), mode.end()) + "' (protected needs the UWP build).");
    return 10;
}

// [M1.5] Removed the standalone console/exe `wmain` spike entry
// (`#if !defined(FG_DESKTOP_DLL) && (!defined(FG_UWP) || defined(FG_WIN32_PMP))`): dead in the desktop DLL build
// (FG_DESKTOP_DLL is defined), which entered via the FgPlayReadyRun/FgPlayReadyRunEx exports instead.

// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════
//  UWP entry — a genuine AppContainer CoreApplication app (InboxOnly MF codec policy). Reads the mode from
//  fg-helper-mode.txt in LocalFolder (written by the orchestrator before launch), runs the producer on a worker MTA
//  thread (MF/DComp work off the ASTA), and pumps the CoreWindow dispatcher until the worker signals done.
// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════
#ifdef FG_UWP
using namespace winrt;
using namespace winrt::Windows::Foundation;
using namespace winrt::Windows::ApplicationModel::Core;
using namespace winrt::Windows::UI::Core;
using namespace winrt::Windows::Storage;
using namespace winrt::Windows::Media::Protection;
using namespace winrt::Windows::Media::Protection::PlayReady;
using namespace winrt::Windows::Media::Core;
using namespace winrt::Windows::Media::Playback;
using namespace winrt::Windows::Media::Streaming::Adaptive;
namespace WWH = winrt::Windows::Web::Http;

static std::atomic<bool> g_workerDone{false};

// ── Axinom public single-key PlayReady v10 test vector (same as playready-uwp-test). ──
static constexpr wchar_t AxinomMpdUrl[]     = L"https://media.axprod.net/TestVectors/Dash/protected_dash_1080p_h264_singlekey/manifest.mpd";
static constexpr wchar_t AxinomLicenseUrl[] = L"https://drm-playready-licensing.axprod.net/AcquireLicense";
static constexpr wchar_t AxinomDrmToken[] =
    L"eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.ewogICJ2ZXJzaW9uIjogMSwKICAiY29tX2tleV9pZCI6ICI2OWU1NDA4OC1lOWUw"
    L"LTQ1MzAtOGMxYS0xZWI2ZGNkMGQxNGUiLAogICJtZXNzYWdlIjogewogICAgInR5cGUiOiAiZW50aXRsZW1lbnRfbWVzc2FnZSIs"
    L"CiAgICAidmVyc2lvbiI6IDIsCiAgICAibGljZW5zZSI6IHsKICAgICAgImFsbG93X3BlcnNpc3RlbmNlIjogdHJ1ZQogICAgfSwK"
    L"ICAgICJjb250ZW50X2tleXNfc291cmNlIjogewogICAgICAiaW5saW5lIjogWwogICAgICAgIHsKICAgICAgICAgICJpZCI6ICI0"
    L"MDYwYTg2NS04ODc4LTQyNjctOWNiZi05MWFlNWJhZTFlNzIiLAogICAgICAgICAgImVuY3J5cHRlZF9rZXkiOiAid3QzRW51dVI1"
    L"UkFybjZBRGYxNkNCQT09IiwKICAgICAgICAgICJ1c2FnZV9wb2xpY3kiOiAiUG9saWN5IEEiCiAgICAgICAgfQogICAgICBdCiAg"
    L"ICB9LAogICAgImNvbnRlbnRfa2V5X3VzYWdlX3BvbGljaWVzIjogWwogICAgICB7CiAgICAgICAgIm5hbWUiOiAiUG9saWN5IEEi"
    L"LAogICAgICAgICJwbGF5cmVhZHkiOiB7CiAgICAgICAgICAibWluX2RldmljZV9zZWN1cml0eV9sZXZlbCI6IDE1MCwKICAgICAg"
    L"ICAgICJwbGF5X2VuYWJsZXJzIjogWwogICAgICAgICAgICAiNzg2NjI3RDgtQzJBNi00NEJFLThGODgtMDhBRTI1NUIwMUE3Igog"
    L"ICAgICAgICAgXQogICAgICAgIH0KICAgICAgfQogICAgXQogIH0KfQ.l8PnZznspJ6lnNmfAE9UQV532Ypzt1JXQkvrk8gFSRw";

static std::atomic<bool> g_prServiceRequested{false}, g_prIndiv{false}, g_prLicense{false};
static std::atomic<int>  g_prLicenseStatus{0};
static std::atomic<bool> g_prMediaOpened{false}, g_prMediaFailed{false};
static std::atomic<int>  g_prMediaFailedHr{0};

static fire_and_forget OnServiceRequested(MediaProtectionManager, ServiceRequestedEventArgs args)
{
    co_await resume_background();
    g_prServiceRequested = true;
    auto request = args.Request();
    auto completion = args.Completion();
    hstring typeName = get_class_name(request);
    LogLine("[pr] ServiceRequested: " + winrt::to_string(typeName));
    try
    {
        if (auto la = request.try_as<PlayReadyLicenseAcquisitionServiceRequest>())
        {
            g_prLicense = true;
            PlayReadySoapMessage soap = la.GenerateManualEnablingChallenge();
            auto bodyBytes = soap.GetMessageBody();
            winrt::Windows::Storage::Streams::DataWriter bw; bw.WriteBytes(bodyBytes);
            auto body = bw.DetachBuffer();
            auto headers = soap.MessageHeaders().GetView();
            WWH::HttpClient http;
            WWH::HttpBufferContent content{ body };
            content.Headers().Append(L"Content-Type", L"text/xml; charset=utf-8");
            WWH::HttpRequestMessage req{ WWH::HttpMethod::Post(), Uri{ AxinomLicenseUrl } };
            req.Content(content);
            req.Headers().Append(L"X-AxDRM-Message", AxinomDrmToken);
            if (headers) for (auto const& kv : headers)
            {
                hstring val = unbox_value_or<hstring>(kv.Value(), L"");
                if (!val.empty()) try { req.Headers().TryAppendWithoutValidation(kv.Key(), val); } catch (...) {}
            }
            auto resp = co_await http.SendRequestAsync(req);
            int status = (int)resp.StatusCode();
            auto response = co_await resp.Content().ReadAsBufferAsync();
            g_prLicenseStatus = status;
            LogLine("[pr] license HTTP " + std::to_string(status) + " (" + std::to_string(response.Length()) + " bytes)");
            winrt::com_array<uint8_t> respBytes(response.Length());
            winrt::Windows::Storage::Streams::DataReader::FromBuffer(response).ReadBytes(respBytes);
            la.ProcessManualEnablingResponse(respBytes);
            completion.Complete(status >= 200 && status < 300);
            co_return;
        }
        if (auto indiv = request.try_as<PlayReadyIndividualizationServiceRequest>())
        {
            g_prIndiv = true;
            co_await indiv.BeginServiceRequest();
            completion.Complete(true);
            co_return;
        }
        completion.Complete(false);
    }
    catch (hresult_error const& e)
    {
        LogLine("[pr] ServiceRequest error hr=0x" + std::to_string((uint32_t)e.code().value) + " " + winrt::to_string(e.message()));
        completion.Complete(false);
    }
}

// Runs the proven WinRT MediaProtectionManager + MediaPlayer PlayReady path to Playing+license (the STATE proof from
// playready-uwp-test), then publishes the coord. NOTE: a raw shareable DComp handle for the PROTECTED surface is the
// hard part (WinRT MediaPlayer exposes no public DComp handle); this reaches Playing/license and reports honestly.
static fire_and_forget RunProtectedAsync(std::shared_ptr<std::atomic<bool>> done)
{
    try
    {
        MediaProtectionManager mpm;
        auto props = mpm.Properties();
        props.Insert(L"Windows.Media.Protection.MediaProtectionSystemId", box_value(L"{F4637010-03C3-42CD-B932-B48ADF3A6A54}"));
        winrt::Windows::Foundation::Collections::PropertySet mapping;
        mapping.Insert(L"{F4637010-03C3-42CD-B932-B48ADF3A6A54}", box_value(L"Windows.Media.Protection.PlayReady.PlayReadyWinRTTrustedInput"));
        props.Insert(L"Windows.Media.Protection.MediaProtectionSystemIdMapping", mapping);
        props.Insert(L"Windows.Media.Protection.MediaProtectionContainerGuid", box_value(L"{9A04F079-9840-4286-AB92-E65BE0885F95}"));
        props.Insert(L"Windows.Media.Protection.UseSoftwareProtectionLayer", box_value(true));
        mpm.ServiceRequested({ &OnServiceRequested });

        LogLine("[pr] creating AdaptiveMediaSource from Axinom MPD ...");
        auto result = co_await AdaptiveMediaSource::CreateFromUriAsync(Uri{ AxinomMpdUrl });
        if (result.Status() != AdaptiveMediaSourceCreationStatus::Success)
        { LogLine("[pr] AMS status=" + std::to_string((int)result.Status())); *done = true; co_return; }
        MediaSource source = MediaSource::CreateFromAdaptiveMediaSource(result.MediaSource());
        MediaPlaybackItem item{ source };
        MediaPlayer player;
        player.AutoPlay(true);
        player.MediaOpened([](MediaPlayer const&, winrt::Windows::Foundation::IInspectable const&) { g_prMediaOpened = true; LogLine("[pr] MediaOpened"); });
        player.MediaFailed([](MediaPlayer const&, MediaPlayerFailedEventArgs const& e)
        {
            g_prMediaFailed = true; g_prMediaFailedHr = e.ExtendedErrorCode().value;
            LogLine("[pr] MediaFailed hr=0x" + std::to_string((uint32_t)e.ExtendedErrorCode().value) + " " + winrt::to_string(e.ErrorMessage()));
        });
        player.ProtectionManager(mpm);   // BEFORE Source
        player.Source(item);
        player.Play();
        LogLine("[pr] Source set + Play(); observing ...");

        auto session = player.PlaybackSession();
        auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds(30);
        while (std::chrono::steady_clock::now() < deadline && !g_prMediaFailed)
        {
            if (session.PlaybackState() == MediaPlaybackState::Playing && g_prServiceRequested) break;
            if (g_prMediaOpened) break;
            co_await resume_after(std::chrono::milliseconds(250));
        }
        co_await resume_after(std::chrono::seconds(1));

        const char* state = (g_prMediaOpened && !g_prMediaFailed) ? "licensed" : "error";
        LogLine(std::string("[pr] RESULT mediaOpened=") + (g_prMediaOpened ? "1" : "0") +
                " mediaFailed=" + (g_prMediaFailed ? "1" : "0") +
                " serviceRequested=" + (g_prServiceRequested ? "1" : "0") +
                " licenseHttp=" + std::to_string(g_prLicenseStatus));
        // Publish the STATE. handle=0: WinRT MediaPlayer exposes no public raw DComp handle to share cross-process.
        WriteCoord("protected", GetCurrentProcessId(), 0, 1920, 1080, state);
        (void)player;   // hold player alive via lifetime below
        // Keep alive so the state is readable (and the process stays for OpenProcess) while the engine reads.
        auto keep = std::chrono::steady_clock::now() + std::chrono::seconds(60);
        while (std::chrono::steady_clock::now() < keep && !StopRequested())
            co_await resume_after(std::chrono::milliseconds(200));
    }
    catch (hresult_error const& e)
    {
        LogLine("[pr] fatal hr=0x" + std::to_string((uint32_t)e.code().value) + " " + winrt::to_string(e.message()));
    }
    *done = true;
}

// Microsoft public PlayReady test vector (from samples/MediaEngineEMEUWPSample TestContent.h). The LA_URL is embedded
// in the PSSH WRMHEADER, so the CDM hands it back as the KeyMessage destinationUrl (anonymous, no auth).
static const char* s_msPlayReadyInitDataB64 =
    "AAADVHBzc2gAAAAAmgTweZhAQoarkuZb4IhflQAAAzQ0AwAAAQABACoDPABXAFIATQBIAEUAQQBEAEUAUgAgAHgAbQBsAG4AcwA9ACIAaAB0AHQAcAA6"
    "AC8ALwBzAGMAaABlAG0AYQBzAC4AbQBpAGMAcgBvAHMAbwBmAHQALgBjAG8AbQAvAEQAUgBNAC8AMgAwADAANwAvADAAMwAvAFAAbABhAHkAUgBlAGEA"
    "ZAB5AEgAZQBhAGQAZQByACIAIAB2AGUAcgBzAGkAbwBuAD0AIgA0AC4AMwAuADAALgAwACIAPgA8AEQAQQBUAEEAPgA8AEwAQQBfAFUAUgBMAD4AaAB0"
    "AHQAcABzADoALwAvAHQAZQBzAHQALgBwAGwAYQB5AHIAZQBhAGQAeQAuAG0AaQBjAHIAbwBzAG8AZgB0AC4AYwBvAG0ALwBjAG8AcgBlAC8AcgBpAGcA"
    "aAB0AHMAbQBhAG4AYQBnAGUAcgAuAGEAcwBtAHgAPwBjAGYAZwA9ACgAcABsAGEAeQBlAG4AYQBiAGwAZQByAHMAOgAoADcAOAA2ADYAMgA3AGQAOAAt"
    "AGMAMgBhADYALQA0ADQAYgBlAC0AOABmADgAOAAtADAAOABhAGUAMgA1ADUAYgAwADEAYQA3ACkALABzAGwAOgAxADUAMAAsAGMAawA6AFcAMwAxAGIA"
    "ZgBWAHQAOQBXADMAMQBiAGYAVgB0ADkAVwAzADEAYgBmAFEAPQA9ACwAYwBrAHQAOgBBAEUAUwAxADIAOABCAGkAdABDAEIAQwApADwALwBMAEEAXwBV"
    "AFIATAA+ADwAUABSAE8AVABFAEMAVABJAE4ARgBPAD4APABLAEkARABTAD4APABLAEkARAAgAEEATABHAEkARAA9ACIAQQBFAFMAQwBCAEMAIgAgAFYA"
    "QQBMAFUARQA9ACIAQQBBAEEAQQBFAEEAQQBRAEEAQgBBAFEAQQBCAEEAQQBBAEEAQQBBAEEAUQA9AD0AIgA+ADwALwBLAEkARAA+ADwALwBLAEkARABT"
    "AD4APAAvAFAAUgBPAFQARQBDAFQASQBOAEYATwA+ADwALwBEAEEAVABBAD4APAAvAFcAUgBNAEgARQBBAEQARQBSAD4A";

static std::atomic<bool> g_cdmKeyMsg{false}, g_cdmUsable{false};
static std::atomic<int>  g_cdmLicHttp{0};
static IMFContentDecryptionModuleSession* g_cdmSession = nullptr;
static std::string       g_cdmKeyStatus = "<none>";

static std::vector<uint8_t> Base64Decode(const std::string& b64)
{
    DWORD n = 0; std::wstring w(b64.begin(), b64.end());
    CryptStringToBinaryW(w.c_str(), (DWORD)w.size(), CRYPT_STRING_BASE64, nullptr, &n, nullptr, nullptr);
    std::vector<uint8_t> out(n);
    if (n) CryptStringToBinaryW(w.c_str(), (DWORD)w.size(), CRYPT_STRING_BASE64, out.data(), &n, nullptr, nullptr);
    return out;
}

static void QueryCdmKeyStatus()
{
    if (!g_cdmSession) return;
    MFMediaKeyStatus* st = nullptr; UINT n = 0;
    if (SUCCEEDED(g_cdmSession->GetKeyStatuses(&st, &n)) && st)
    {
        std::string s;
        for (UINT i = 0; i < n; i++) { if (i) s += ","; s += std::to_string((int)st[i].eMediaKeyStatus); }
        g_cdmKeyStatus = n ? s : "<empty>";
        // MF_MEDIAKEY_STATUS_USABLE == 0
        for (UINT i = 0; i < n; i++) if (st[i].eMediaKeyStatus == MF_MEDIAKEY_STATUS_USABLE) g_cdmUsable = true;
        CoTaskMemFree(st);
    }
}

// The session KeyMessage: POST the challenge to the PlayReady license server (destUrl from the PSSH), Update the session.
static void HandleCdmKeyMessage(const BYTE* msg, DWORD cb, LPCWSTR destUrl)
{
    g_cdmKeyMsg = true;
    try
    {
        std::wstring url = (destUrl && *destUrl) ? destUrl
            : L"https://test.playready.microsoft.com/service/rightsmanager.asmx";
        // CONFIRMED (2026-07-19, offline checksum proof): the Axinom singlekey vector is encrypted with AXINOM's
        // content key, NOT one derived from the PlayReady test key seed — the WRMHEADER CHECKSUM is yxlGlhfD+ac=
        // while the Microsoft-test-server derivation for this KID yields AnVj5bX2n1M=. A license from
        // test.playready.microsoft.com therefore carries a WRONG key: the CDM still reports it USABLE, the
        // sample-attribute path then decrypts garbage (decode error MF_E_INVALIDREQUEST on sample #0), and the real
        // protected topology rejects the bind as DRM_E_CH_BAD_KEY (0x8004110E). Axinom content MUST be licensed by
        // Axinom's service (X-AxDRM-Message added below). FG_PLAYREADY_LICENSE_URL overrides for other content.
        bool bakedAxinom = GetEnvironmentVariableW(L"FG_CENC_BAKED_AXINOM", nullptr, 0) != 0;
        wchar_t urlOverride[512];
        DWORD ovLen = GetEnvironmentVariableW(L"FG_PLAYREADY_LICENSE_URL", urlOverride, 512);
        if (ovLen > 0 && ovLen < 512) url = urlOverride;
        // The Axinom license-server rewrite is a diagnostic fallback only (production relays the license in managed code).
        else if (bakedAxinom && url.find(L"playready.microsoft.com") != std::wstring::npos) url = AxinomLicenseUrl;
        // The PlayReady KeyMessage is a UTF-16 XML envelope: <Challenge encoding="base64encoded">B64</Challenge> plus
        // <HttpHeaders><HttpHeader><name/><value/>...  Extract the base64 challenge AND the headers (SOAPAction matters).
        std::wstring xml((const wchar_t*)msg, cb / sizeof(wchar_t));
        std::vector<uint8_t> challenge;
        std::vector<std::pair<std::wstring, std::wstring>> hdrs;
        size_t ctag = xml.find(L"<Challenge");
        size_t cgt = ctag != std::wstring::npos ? xml.find(L'>', ctag) : std::wstring::npos;
        size_t ce = xml.find(L"</Challenge>");
        if (cgt != std::wstring::npos && ce != std::wstring::npos)
        {
            std::wstring inner = xml.substr(cgt + 1, ce - (cgt + 1));
            // strip a possible CDATA wrapper
            size_t cd = inner.find(L"<![CDATA["); if (cd != std::wstring::npos) { inner = inner.substr(cd + 9); size_t e = inner.find(L"]]>"); if (e != std::wstring::npos) inner = inner.substr(0, e); }
            // trim whitespace
            while (!inner.empty() && iswspace(inner.front())) inner.erase(inner.begin());
            while (!inner.empty() && iswspace(inner.back())) inner.pop_back();
            challenge = Base64Decode(std::string(inner.begin(), inner.end()));
        }
        else challenge.assign(msg, msg + cb);
        // Parse <HttpHeader><name>..</name><value>..</value>
        size_t pos = 0;
        for (;;)
        {
            size_t ns = xml.find(L"<name>", pos); if (ns == std::wstring::npos) break;
            size_t ne = xml.find(L"</name>", ns); size_t vs = xml.find(L"<value>", ne); size_t ve = xml.find(L"</value>", vs);
            if (ne == std::wstring::npos || vs == std::wstring::npos || ve == std::wstring::npos) break;
            hdrs.emplace_back(xml.substr(ns + 6, ne - (ns + 6)), xml.substr(vs + 7, ve - (vs + 7)));
            pos = ve + 8;
        }

#ifdef FG_DESKTOP_DLL
        // M5 production path: hand the challenge to the managed WithDrm relay and Update() with the returned license.
        // Native never holds a key/token here. FG_CENC_BAKED_AXINOM=1 forces the legacy in-native Axinom POST for A/B.
        if (g_licenseCallback && !bakedAxinom)
        {
            std::vector<uint8_t> license;
            int32_t rc = g_licenseCallback(g_licenseCtx, challenge.data(), (int32_t)challenge.size(),
                                           g_kidHex.empty() ? nullptr : g_kidHex.c_str(),
                                           [](void* ctx, const uint8_t* lic, int32_t n)
                                           { auto* v = (std::vector<uint8_t>*)ctx; v->assign(lic, lic + (size_t)n); },
                                           &license);
            LogLine("[cdm] managed relay rc=" + std::to_string(rc) + " license=" + std::to_string(license.size()) + "B");
            if (rc == 0 && !license.empty() && g_cdmSession)
            {
                HRESULT hu = g_cdmSession->Update(license.data(), (DWORD)license.size());
                LogLine("[cdm] Update() (relay) hr=0x" + [&]{ std::stringstream s; s << std::hex << (uint32_t)hu; return s.str(); }());
                g_cdmLicHttp = (rc == 0) ? 200 : 0;
                if (FAILED(hu))
                {
                    // CRITICAL DIAGNOSTIC: dump a printable prefix of the relay license body so we can tell a genuine
                    // license apart from a SOAP fault / error page (mirrors the non-relay path below).
                    std::string head; head.reserve(600);
                    for (size_t i = 0; i < license.size() && head.size() < 600; i++)
                    {
                        char c = (char)license[i];
                        if (c == '\r' || c == '\n' || c == '\t') c = ' ';
                        if (c >= 32 && c < 127) head.push_back(c);
                    }
                    LogLine("[cdm] relay license response head: " + head);
                }
                QueryCdmKeyStatus();
                // Surface a hard Error via the desktop snapshot atomics ONLY when Update() itself failed — a rejected
                // license means every layer above would otherwise spin on "Loading" forever. Do NOT gate on
                // !g_cdmUsable here: the key status settles ASYNCHRONOUSLY (KeyStatusChanged fires a beat later), so
                // right after a SUCCESSFUL Update() g_cdmUsable is legitimately still false — treating that as an
                // error is a false positive. The managed watchdog (FG_VIDEO_START_TIMEOUT_MS) catches a key that
                // never becomes usable.
                if (FAILED(hu))
                {
                    g_desktopErrorHr.store((int)hu, std::memory_order_release);
                    g_desktopState.store(5, std::memory_order_release); // 5 = Error
                    LogLine("[cdm] relay bind FAILED — surfacing Error (hr=0x" +
                            [&]{ std::stringstream s; s << std::hex << (uint32_t)hu; return s.str(); }() + ")");
                }
            }
            else
            {
                LogLine("[cdm] managed relay produced no license — key will not become usable.");
                g_desktopErrorHr.store((int)0x80704005, std::memory_order_release); // MF_TYPE_ERR sentinel
                g_desktopState.store(5, std::memory_order_release);                 // 5 = Error
                LogLine("[cdm] relay produced no license — surfacing Error.");
            }
            return;
        }
#endif

        WWH::HttpClient http;
        winrt::Windows::Storage::Streams::DataWriter dw; dw.WriteBytes(winrt::array_view<uint8_t const>(challenge.data(), challenge.data() + challenge.size()));
        WWH::HttpBufferContent content{ dw.DetachBuffer() };
        std::wstring ctype = L"text/xml; charset=utf-8";
        for (auto const& h : hdrs) if (_wcsicmp(h.first.c_str(), L"Content-Type") == 0) ctype = h.second;
        content.Headers().Append(L"Content-Type", winrt::hstring(ctype));
        WWH::HttpRequestMessage req{ WWH::HttpMethod::Post(), Uri{ winrt::hstring(url) } };
        req.Content(content);
        for (auto const& h : hdrs)
            if (_wcsicmp(h.first.c_str(), L"Content-Type") != 0)
                try { req.Headers().TryAppendWithoutValidation(winrt::hstring(h.first), winrt::hstring(h.second)); } catch (...) {}
        // Baked diagnostic only: Axinom AcquireLicense requires the v10 entitlement token in X-AxDRM-Message. In
        // production this token lives in managed app code (the WithDrm relay), never in native.
        if (bakedAxinom && (url.find(L"axprod") != std::wstring::npos || url.find(L"axinom") != std::wstring::npos))
            try { req.Headers().TryAppendWithoutValidation(L"X-AxDRM-Message", AxinomDrmToken); LogLine("[cdm] + X-AxDRM-Message (Axinom license, baked)"); } catch (...) {}
        LogLine("[cdm] POST challenge=" + std::to_string(challenge.size()) + "B headers=" + std::to_string(hdrs.size()) + " -> " + winrt::to_string(winrt::hstring(url)));
        auto resp = http.SendRequestAsync(req).get();
        g_cdmLicHttp = (int)resp.StatusCode();
        auto respBuf = resp.Content().ReadAsBufferAsync().get();
        LogLine("[cdm] KeyMessage -> license HTTP " + std::to_string(g_cdmLicHttp) + " (" + std::to_string(respBuf.Length()) + " bytes)");
        if (g_cdmLicHttp >= 200 && g_cdmLicHttp < 300 && g_cdmSession)
        {
            winrt::com_array<uint8_t> lic(respBuf.Length());
            winrt::Windows::Storage::Streams::DataReader::FromBuffer(respBuf).ReadBytes(lic);
            HRESULT hu = g_cdmSession->Update(lic.data(), lic.size());
            LogLine("[cdm] Update() hr=0x" + [&]{ std::stringstream s; s << std::hex << (uint32_t)hu; return s.str(); }());
            if (FAILED(hu))
            {
                // Diagnose exactly what the server sent (SOAP fault vs license): log a UTF-8-ish prefix.
                std::string head; head.reserve(600);
                for (size_t i = 0; i < lic.size() && head.size() < 600; i++)
                {
                    char c = (char)lic[i];
                    if (c == '\r' || c == '\n' || c == '\t') c = ' ';
                    if (c >= 32 && c < 127) head.push_back(c);
                }
                LogLine("[cdm] license response head: " + head);
            }
            QueryCdmKeyStatus();
        }
    }
    catch (hresult_error const& e) { LogLine("[cdm] KeyMessage error hr=0x" + std::to_string((uint32_t)e.code().value)); }
}

struct CdmSessionCallbacks : public IMFContentDecryptionModuleSessionCallbacks
{
    std::atomic<long> rc{1};
    HRESULT __stdcall QueryInterface(REFIID riid, void** ppv) override
    {
        if (!ppv) return E_POINTER;
        if (riid == __uuidof(::IUnknown) || riid == __uuidof(IMFContentDecryptionModuleSessionCallbacks)) { *ppv = this; AddRef(); return S_OK; }
        *ppv = nullptr; return E_NOINTERFACE;
    }
    ULONG __stdcall AddRef() override { return (ULONG)++rc; }
    ULONG __stdcall Release() override { long v = --rc; if (!v) delete this; return (ULONG)v; }
    HRESULT __stdcall KeyMessage(MF_MEDIAKEYSESSION_MESSAGETYPE, const BYTE* msg, DWORD cb, LPCWSTR url) override
    { HandleCdmKeyMessage(msg, cb, url); return S_OK; }
    HRESULT __stdcall KeyStatusChanged() override { QueryCdmKeyStatus(); LogLine("[cdm] KeyStatusChanged -> status=" + g_cdmKeyStatus); return S_OK; }
};

// The deliverable's key question: in the GENUINE-UWP (InboxOnly) context, does the MF-CDM SetPMPHostApp path — which
// returns E_FAIL (0x80004005) in full-trust / LPAC / packaged (see docs/plans/video-drm-layer-design.md) — now
// SUCCEED? If S_OK here, the protected surface can come from IMFMediaEngine+CDM (a shareable windowless-swapchain
// handle, exactly like the clear path). This probes CDM creation + GetService(IMFPMPHost/App) + SetPMPHostApp and logs
// every HRESULT. Compact port of tools/playready-mf-spike (Microsoft public PlayReady key system).
static void ProbeSetPmpHostAppInUwp()
{
    const wchar_t* keySystem = L"com.microsoft.playready.recommendation";
    std::wstring storePath = std::wstring(g_coordPath.substr(0, g_coordPath.find_last_of(L"\\/"))) + L"\\PRStore";
    CreateDirectoryW(storePath.c_str(), nullptr);
    LogLine("[pmp-probe] keySystem=com.microsoft.playready.recommendation store=" + std::string(storePath.begin(), storePath.end()));

    IMFMediaEngineClassFactory* baseFactory = nullptr;
    IMFMediaEngineClassFactory4* factory4 = nullptr;
    IMFContentDecryptionModuleFactory* cdmFactory = nullptr;
    IMFContentDecryptionModuleAccess* cdmAccess = nullptr;
    IMFContentDecryptionModule* cdm = nullptr;
    HRESULT hr;
    auto hx = [](HRESULT h){ std::stringstream ss; ss << "0x" << std::hex << (uint32_t)h; return ss.str(); };
    do {
        if (FAILED(hr = CoCreateInstance(CLSID_MFMediaEngineClassFactory, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&baseFactory))))
        { LogLine("[pmp-probe] CoCreate(MFMediaEngineClassFactory) hr=" + hx(hr)); break; }
        if (FAILED(hr = baseFactory->QueryInterface(IID_PPV_ARGS(&factory4))))
        { LogLine("[pmp-probe] QI IMFMediaEngineClassFactory4 hr=" + hx(hr)); break; }
        GUID iidCdmFac = __uuidof(IMFContentDecryptionModuleFactory);
        if (FAILED(hr = factory4->CreateContentDecryptionModuleFactory(keySystem, iidCdmFac, (void**)&cdmFactory)) || !cdmFactory)
        { LogLine("[pmp-probe] CreateContentDecryptionModuleFactory hr=" + hx(hr)); break; }

        // Minimal config property store: init data types {cenc}, session types {temporary}, distinctiveId/persist optional.
        IPropertyStore* cfg = nullptr;
        PSCreateMemoryPropertyStore(IID_PPV_ARGS(&cfg));
        auto setVecBstr = [&](const PROPERTYKEY& key, const wchar_t* one){
            PROPVARIANT pv; memset(&pv, 0, sizeof(pv));
            BSTR* arr = (BSTR*)CoTaskMemAlloc(sizeof(BSTR)); arr[0] = SysAllocString(one);
            pv.vt = VT_VECTOR | VT_BSTR; pv.cabstr.cElems = 1; pv.cabstr.pElems = arr;
            cfg->SetValue(key, pv); PropVariantClear(&pv);
        };
        auto setVecUI4 = [&](const PROPERTYKEY& key, ULONG one){
            PROPVARIANT pv; memset(&pv, 0, sizeof(pv));
            ULONG* arr = (ULONG*)CoTaskMemAlloc(sizeof(ULONG)); arr[0] = one;
            pv.vt = VT_VECTOR | VT_UI4; pv.caul.cElems = 1; pv.caul.pElems = arr;
            cfg->SetValue(key, pv); PropVariantClear(&pv);
        };
        auto setUI4 = [&](const PROPERTYKEY& key, ULONG v){ PROPVARIANT pv; memset(&pv,0,sizeof(pv)); pv.vt=VT_UI4; pv.ulVal=v; cfg->SetValue(key,pv); };
        auto setEmptyVecVar = [&](const PROPERTYKEY& key){ PROPVARIANT pv; memset(&pv,0,sizeof(pv)); pv.vt=VT_VECTOR|VT_VARIANT; pv.capropvar.cElems=0; cfg->SetValue(key,pv); };
        setVecBstr(MF_EME_INITDATATYPES, L"cenc");
        setEmptyVecVar(MF_EME_AUDIOCAPABILITIES);
        setEmptyVecVar(MF_EME_VIDEOCAPABILITIES);
        setUI4(MF_EME_DISTINCTIVEID, MF_MEDIAKEYS_REQUIREMENT_OPTIONAL);
        setUI4(MF_EME_PERSISTEDSTATE, MF_MEDIAKEYS_REQUIREMENT_OPTIONAL);
        setVecUI4(MF_EME_SESSIONTYPES, (ULONG)MF_MEDIAKEYSESSION_TYPE_TEMPORARY);

        IPropertyStore* cfgArr = cfg;
        hr = cdmFactory->CreateContentDecryptionModuleAccess(keySystem, &cfgArr, 1, &cdmAccess);
        cfg->Release();
        if (FAILED(hr) || !cdmAccess) { LogLine("[pmp-probe] CreateContentDecryptionModuleAccess hr=" + hx(hr)); break; }

        // CDM properties: origin id + explicit store path (the MSPR_E_HWDRM_SUPPORTED_BUT_NO_PATHS fix).
        IPropertyStore* cdmProps = nullptr; PSCreateMemoryPropertyStore(IID_PPV_ARGS(&cdmProps));
        { PROPVARIANT pv; InitPropVariantFromString(L"fluentgpu-uwp-helper", &pv); PROPERTYKEY k = MF_CONTENTDECRYPTIONMODULE_STOREPATH; PROPVARIANT sp; InitPropVariantFromString(storePath.c_str(), &sp); cdmProps->SetValue(k, sp); PropVariantClear(&sp); PropVariantClear(&pv); }
        hr = cdmAccess->CreateContentDecryptionModule(cdmProps, &cdm);
        cdmProps->Release();
        if (FAILED(hr) || !cdm)
        { LogLine("[pmp-probe] CreateContentDecryptionModule hr=" + hx(hr) + (((uint32_t)hr==0x8004B8CF)?"  (MSPR_E_HWDRM_SUPPORTED_BUT_NO_PATHS)":"")); break; }
        LogLine("[pmp-probe] IMFContentDecryptionModule CREATED (store path accepted).");

        // GetService(IMFPMPHost / IMFPMPHostApp) + SetPMPHostApp — the make-or-break for native PlayReady key acq.
        IMFGetService* svc = nullptr;
        if (SUCCEEDED(cdm->QueryInterface(IID_PPV_ARGS(&svc))) && svc)
        {
            GUID svcId = MF_CONTENTDECRYPTIONMODULE_SERVICE;
            IMFPMPHostApp* hostApp = nullptr;
            void* host = nullptr;
            HRESULT hrHost = svc->GetService(svcId, __uuidof(IMFPMPHost), &host);
            HRESULT hrApp  = svc->GetService(svcId, __uuidof(IMFPMPHostApp), (void**)&hostApp);
            LogLine("[pmp-probe] GetService(IMFPMPHost) hr=" + hx(hrHost) + " ; GetService(IMFPMPHostApp) hr=" + hx(hrApp) + (hostApp?" (hostApp OK)":""));
            bool pmpOk = false;
            if (hostApp)
            {
                HRESULT hrSet = cdm->SetPMPHostApp(hostApp);
                pmpOk = SUCCEEDED(hrSet);
                LogLine("[pmp-probe] *** SetPMPHostApp(direct hostApp) hr=" + hx(hrSet) + " ***  " + (pmpOk ? "SUCCESS — the full-trust E_FAIL wall is CLEARED in UWP" : "FAILED (same wall as full-trust)"));
                hostApp->Release();
            }
            else LogLine("[pmp-probe] no IMFPMPHostApp from CDM — cannot SetPMPHostApp (this is the non-UWP behavior).");
            if (host) ((::IUnknown*)host)->Release();
            svc->Release();

            // Since SetPMPHostApp no longer fails, GenerateRequest (which returned DRM_E_LOGICERR 0x8004C3E8 in full-trust)
            // may now proceed. Create a session, GenerateRequest with the MS PlayReady PSSH, POST the license, check status.
            if (pmpOk)
            {
                CdmSessionCallbacks* cb = new CdmSessionCallbacks();
                IMFContentDecryptionModuleSession* session = nullptr;
                HRESULT hrs = cdm->CreateSession(MF_MEDIAKEYSESSION_TYPE_TEMPORARY, cb, &session);
                LogLine("[pmp-probe] CreateSession hr=" + hx(hrs));
                if (SUCCEEDED(hrs) && session)
                {
                    g_cdmSession = session;
                    std::vector<uint8_t> initData = Base64Decode(s_msPlayReadyInitDataB64);
                    HRESULT hrg = session->GenerateRequest(L"cenc", initData.data(), (DWORD)initData.size());
                    LogLine("[pmp-probe] *** GenerateRequest hr=" + hx(hrg) + " ***  " +
                            (SUCCEEDED(hrg) ? "S_OK — native protected key request PROCEEDS in UWP (DRM_E_LOGICERR wall gone)"
                                            : (((uint32_t)hrg==0x8004C3E8) ? "DRM_E_LOGICERR (still gated)" : "failed")));
                    if (SUCCEEDED(hrg))
                    {
                        // Wait for KeyMessage -> license POST -> Update -> KeyStatusChanged.
                        auto dl = std::chrono::steady_clock::now() + std::chrono::seconds(30);
                        while (!g_cdmUsable && std::chrono::steady_clock::now() < dl)
                            std::this_thread::sleep_for(std::chrono::milliseconds(200));
                        QueryCdmKeyStatus();
                        LogLine("[pmp-probe] key exchange: keyMessage=" + std::string(g_cdmKeyMsg?"1":"0") +
                                " licenseHttp=" + std::to_string(g_cdmLicHttp) +
                                " keyStatus=" + g_cdmKeyStatus + " USABLE=" + (g_cdmUsable?"YES":"no"));
                    }
                    g_cdmSession = nullptr; session->Release();
                }
                cb->Release();
            }
        }
    } while (false);
    if (cdm) cdm->Release();
    if (cdmAccess) cdmAccess->Release();
    if (cdmFactory) cdmFactory->Release();
    if (factory4) factory4->Release();
    if (baseFactory) baseFactory->Release();
}

// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════
//  PROTECTED video producer — IMFMediaEngine + the MODERN MF Content Decryption Module (EME). The legacy
//  IMFMediaEngineClassFactoryEx::CreateMediaKeys path returns MF_NOT_SUPPORTED_ERR (0x80700009) for PlayReady on
//  current Windows — the in-proc EME software CDM is gone. The working path (per Microsoft's MediaEngineEMEUWPSample)
//  is the SAME modern IMFContentDecryptionModule the probe reaches USABLE with, connected to the media engine via:
//     engine attr MF_MEDIA_ENGINE_CONTENT_PROTECTION_FLAGS = MF_MEDIA_ENGINE_ENABLE_PROTECTED_CONTENT
//     engine.QI(IMFMediaEngineProtectedContent)->SetContentProtectionManager(pm)
//  where 'pm' (MediaEngineProtectionManager) implements IMFContentProtectionManager + WinRT IMediaProtectionManager,
//  exposes the CDM's IMediaProtectionPMPServer via Properties()["Windows.Media.Protection.MediaProtectionPMPServer"]
//  (so the engine's protected pipeline runs in the CDM's PMP), and routes BeginEnableContent -> cdm->SetContentEnabler.
//  The engine's MF_MEDIA_ENGINE_NEEDKEY_CALLBACK delivers the CONTENT's init data; we drive the PROVEN CDM session
//  (CreateSession + GenerateRequest("cenc", initData) + license POST + Update -> USABLE) with it. Same windowless
//  swapchain as RunClear -> GetVideoSwapchainHandle -> the PROTECTED shareable DComp handle -> publish. Protected
//  pixels capture BLACK (output protection) — CORRECT; the proof is Playing + USABLE key + non-zero handle + bind.
// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════
static const wchar_t* kEmeLicenseDefault =
    L"http://test.playready.microsoft.com/service/rightsmanager.asmx?cfg=(persist:false,sl:150)";

static IMFContentDecryptionModule* g_emeCdm = nullptr;   // the CDM shared by the protection manager + the media engine
static std::atomic<bool> g_emeNeedKey{false};
static std::atomic<bool> g_emePublished{false};   // set the instant a non-zero protected handle is published
static std::atomic<bool> g_emeAttemptDone{false}; // set when an attempt returns (clean fail); NOT set if Play() wedges

// Missing from the Windows SDK (confirmed by Microsoft's PlayReady architect in media-foundation#37), but required
// by the normal Win32 IMFPMPHost -> IMFPMPHostApp bridge used by Firefox/Chromium.
static const GUID kGuidObjectStream =
    { 0x3e73735c, 0xe6c0, 0x481d, { 0x82, 0x60, 0xee, 0x5d, 0xb1, 0x34, 0x3b, 0x5f } };
static const GUID kGuidClassName =
    { 0x77631a31, 0xe5e7, 0x4785, { 0xbf, 0x17, 0x20, 0xf5, 0x7b, 0x22, 0x48, 0x02 } };
static const GUID kClsidEmeStoreActivate =
    { 0x2df7b51e, 0x797b, 0x4d06, { 0xbe, 0x71, 0xd1, 0x4a, 0x52, 0xcf, 0x84, 0x21 } };

// Ordinary desktop processes receive IMFPMPHost (not IMFPMPHostApp) from the OS CDM service. Adapt it using the exact
// browser-proven activation envelope: runtime-class name + optional object stream -> serialized MF attributes ->
// CLSID_EMEStoreActivate -> IMFActivate::ActivateObject. No UWP/package/AppContainer is involved.
struct DesktopPmpHostApp : winrt::implements<DesktopPmpHostApp, IMFPMPHostApp>
{
    winrt::com_ptr<IMFPMPHost> m_host;
    explicit DesktopPmpHostApp(IMFPMPHost* host) { m_host.copy_from(host); }

    IFACEMETHODIMP LockProcess() noexcept override { return m_host ? m_host->LockProcess() : E_FAIL; }
    IFACEMETHODIMP UnlockProcess() noexcept override { return m_host ? m_host->UnlockProcess() : E_FAIL; }

    IFACEMETHODIMP ActivateClassById(LPCWSTR id, IStream* input, REFIID riid, void** activated) noexcept override
    {
        if (!id || !activated || !m_host) return E_POINTER;
        *activated = nullptr;
        auto hx = [](HRESULT h){ std::stringstream s; s << "0x" << std::hex << (uint32_t)h; return s.str(); };
        LogLine("[pmp-wrap] ActivateClassById id=" + winrt::to_string(winrt::hstring(id)));

        winrt::com_ptr<IMFAttributes> attrs;
        HRESULT hr = MFCreateAttributes(attrs.put(), 2);
        if (FAILED(hr)) return hr;
        if (FAILED(hr = attrs->SetString(kGuidClassName, id))) return hr;

        if (input)
        {
            STATSTG stat{};
            if (FAILED(hr = input->Stat(&stat, STATFLAG_NOOPEN | STATFLAG_NONAME))) return hr;
            if (stat.cbSize.HighPart != 0) return E_INVALIDARG;
            if (stat.cbSize.LowPart)
            {
                std::vector<uint8_t> blob(stat.cbSize.LowPart);
                ULONG read = 0;
                if (FAILED(hr = input->Read(blob.data(), (ULONG)blob.size(), &read))) return hr;
                if (read > blob.size()) return E_UNEXPECTED;
                if (FAILED(hr = attrs->SetBlob(kGuidObjectStream, blob.data(), read))) return hr;
            }
        }

        winrt::com_ptr<IStream> serialized;
        if (FAILED(hr = CreateStreamOnHGlobal(nullptr, TRUE, serialized.put()))) return hr;
        if (FAILED(hr = MFSerializeAttributesToStream(attrs.get(), 0, serialized.get()))) return hr;
        LARGE_INTEGER zero{};
        if (FAILED(hr = serialized->Seek(zero, STREAM_SEEK_SET, nullptr))) return hr;

        winrt::com_ptr<IMFActivate> activator;
        if (FAILED(hr = m_host->CreateObjectByCLSID(kClsidEmeStoreActivate, serialized.get(),
                                                    __uuidof(IMFActivate), (void**)activator.put())))
        {
            LogLine("[pmp-wrap] CreateObjectByCLSID(EMEStoreActivate) hr=" + hx(hr));
            return hr;
        }
        hr = activator->ActivateObject(riid, activated);
        LogLine("[pmp-wrap] ActivateObject hr=" + hx(hr));
        return hr;
    }
};

// Create + prepare the modern CDM (factory4 -> CDM factory -> access -> CDM with explicit store path -> SetPMPHostApp).
// Faithful to the PROVEN ProbeSetPmpHostAppInUwp sequence, but returns the CDM kept alive for the media engine.
static IMFContentDecryptionModule* CreateAndPrepareCdm(const wchar_t* keySystem, const std::wstring& storePath)
{
    auto hx = [](HRESULT h){ std::stringstream ss; ss << "0x" << std::hex << (uint32_t)h; return ss.str(); };
    IMFMediaEngineClassFactory* baseFactory = nullptr;
    IMFMediaEngineClassFactory4* factory4 = nullptr;
    IMFContentDecryptionModuleFactory* cdmFactory = nullptr;
    IMFContentDecryptionModuleAccess* cdmAccess = nullptr;
    IMFContentDecryptionModule* cdm = nullptr;
    HRESULT hr;
    do {
        if (FAILED(hr = CoCreateInstance(CLSID_MFMediaEngineClassFactory, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&baseFactory)))) { LogLine("[eme-cdm] CoCreate factory hr=" + hx(hr)); break; }
        if (FAILED(hr = baseFactory->QueryInterface(IID_PPV_ARGS(&factory4)))) { LogLine("[eme-cdm] QI factory4 hr=" + hx(hr)); break; }
        GUID iidCdmFac = __uuidof(IMFContentDecryptionModuleFactory);
        if (FAILED(hr = factory4->CreateContentDecryptionModuleFactory(keySystem, iidCdmFac, (void**)&cdmFactory)) || !cdmFactory) { LogLine("[eme-cdm] CreateCdmFactory hr=" + hx(hr)); break; }

        IPropertyStore* cfg = nullptr; PSCreateMemoryPropertyStore(IID_PPV_ARGS(&cfg));
        auto setVecBstr = [&](const PROPERTYKEY& key, const wchar_t* one){ PROPVARIANT pv; memset(&pv,0,sizeof(pv)); BSTR* arr=(BSTR*)CoTaskMemAlloc(sizeof(BSTR)); arr[0]=SysAllocString(one); pv.vt=VT_VECTOR|VT_BSTR; pv.cabstr.cElems=1; pv.cabstr.pElems=arr; cfg->SetValue(key,pv); PropVariantClear(&pv); };
        auto setVecUI4x2 = [&](const PROPERTYKEY& key, ULONG a, ULONG b){ PROPVARIANT pv; memset(&pv,0,sizeof(pv)); ULONG* arr=(ULONG*)CoTaskMemAlloc(sizeof(ULONG)*2); arr[0]=a; arr[1]=b; pv.vt=VT_VECTOR|VT_UI4; pv.caul.cElems=2; pv.caul.pElems=arr; cfg->SetValue(key,pv); PropVariantClear(&pv); };
        auto setUI4      = [&](const PROPERTYKEY& key, ULONG v){ PROPVARIANT pv; memset(&pv,0,sizeof(pv)); pv.vt=VT_UI4; pv.ulVal=v; cfg->SetValue(key,pv); };
        auto setEmptyVec = [&](const PROPERTYKEY& key){ PROPVARIANT pv; memset(&pv,0,sizeof(pv)); pv.vt=VT_VECTOR|VT_VARIANT; pv.capropvar.cElems=0; cfg->SetValue(key,pv); };
        setVecBstr(MF_EME_INITDATATYPES, L"cenc");
        setEmptyVec(MF_EME_AUDIOCAPABILITIES);
        setEmptyVec(MF_EME_VIDEOCAPABILITIES);
        setUI4(MF_EME_DISTINCTIVEID, MF_MEDIAKEYS_REQUIREMENT_OPTIONAL);
        setUI4(MF_EME_PERSISTEDSTATE, MF_MEDIAKEYS_REQUIREMENT_OPTIONAL);
        // Both session types: the proactive license session runs persistent-license (the Axinom test license is
        // persistable — see DriveCdmLicenseProactive), and persistent MUST be declared here or CreateSession rejects it.
        setVecUI4x2(MF_EME_SESSIONTYPES,
                    (ULONG)MF_MEDIAKEYSESSION_TYPE_TEMPORARY, (ULONG)MF_MEDIAKEYSESSION_TYPE_PERSISTENT_LICENSE);
        IPropertyStore* cfgArr = cfg;
        hr = cdmFactory->CreateContentDecryptionModuleAccess(keySystem, &cfgArr, 1, &cdmAccess);
        cfg->Release();
        if (FAILED(hr) || !cdmAccess) { LogLine("[eme-cdm] CreateCdmAccess hr=" + hx(hr)); break; }

        IPropertyStore* cdmProps = nullptr; PSCreateMemoryPropertyStore(IID_PPV_ARGS(&cdmProps));
        { PROPERTYKEY k = MF_CONTENTDECRYPTIONMODULE_STOREPATH; PROPVARIANT sp; InitPropVariantFromString(storePath.c_str(), &sp); cdmProps->SetValue(k, sp); PropVariantClear(&sp); }
        hr = cdmAccess->CreateContentDecryptionModule(cdmProps, &cdm);
        cdmProps->Release();
        if (FAILED(hr) || !cdm) { LogLine("[eme-cdm] CreateCdm hr=" + hx(hr)); cdm = nullptr; break; }
        LogLine("[eme-cdm] IMFContentDecryptionModule CREATED.");

        // SetPMPHostApp (proven S_FALSE=success in genuine-UWP) — required before GenerateRequest / protected decode.
        IMFGetService* svc = nullptr;
        if (SUCCEEDED(cdm->QueryInterface(IID_PPV_ARGS(&svc))) && svc)
        {
            IMFPMPHostApp* hostApp = nullptr;
            HRESULT hrApp = svc->GetService(MF_CONTENTDECRYPTIONMODULE_SERVICE, __uuidof(IMFPMPHostApp), (void**)&hostApp);
            if (hostApp)
            {
                HRESULT hrSet = cdm->SetPMPHostApp(hostApp);
                LogLine("[eme-cdm] SetPMPHostApp(direct) hr=" + hx(hrSet));
                hostApp->Release();
            }
            else
            {
                winrt::com_ptr<IMFPMPHost> host;
                HRESULT hrHost = svc->GetService(MF_CONTENTDECRYPTIONMODULE_SERVICE, __uuidof(IMFPMPHost),
                                                 (void**)host.put());
                LogLine("[eme-cdm] direct IMFPMPHostApp hr=" + hx(hrApp) +
                        "; IMFPMPHost hr=" + hx(hrHost));
                if (SUCCEEDED(hrHost) && host)
                {
                    auto wrapper = winrt::make_self<DesktopPmpHostApp>(host.get());
                    HRESULT hrSet = cdm->SetPMPHostApp(wrapper.get());
                    LogLine("[eme-cdm] SetPMPHostApp(desktop wrapper) hr=" + hx(hrSet));
                    if (FAILED(hrSet)) { cdm->Release(); cdm = nullptr; }
                }
            }
            svc->Release();
        }
    } while (false);
    if (cdmAccess) cdmAccess->Release();
    if (cdmFactory) cdmFactory->Release();
    if (factory4) factory4->Release();
    if (baseFactory) baseFactory->Release();
    return cdm;
}

// The protection manager the media engine talks to: implements IMFContentProtectionManager + WinRT
// IMediaProtectionManager, exposes the CDM's PMP server, and routes content-enabler requests to the CDM.
// (Direct port of Microsoft's MediaEngineEMEUWPSample MediaEngineProtectionManager.)
struct MediaEngineProtectionManager
    : winrt::implements<MediaEngineProtectionManager, IMFContentProtectionManager, winrt::Windows::Media::Protection::IMediaProtectionManager>
{
    winrt::com_ptr<IMFContentDecryptionModule> m_cdm;
    winrt::Windows::Foundation::Collections::PropertySet m_props;

    MediaEngineProtectionManager(IMFContentDecryptionModule* cdm)
    {
        m_cdm.copy_from(cdm);
        winrt::com_ptr<IMFGetService> svc = m_cdm.as<IMFGetService>();
        winrt::com_ptr<ABI::Windows::Media::Protection::IMediaProtectionPMPServer> abiPmp;
        winrt::check_hresult(svc->GetService(MF_CONTENTDECRYPTIONMODULE_SERVICE, IID_PPV_ARGS(abiPmp.put())));
        winrt::Windows::Media::Protection::MediaProtectionPMPServer pmp{ nullptr };
        winrt::copy_from_abi(pmp, abiPmp.get());
        auto map = m_props.as<winrt::Windows::Foundation::Collections::IMap<winrt::hstring, winrt::Windows::Foundation::IInspectable>>();
        map.Insert(L"Windows.Media.Protection.MediaProtectionPMPServer", pmp);
        // Firefox's working desktop MFCDM path inserts ONLY the PMP-server property (gecko
        // MFContentProtectionManager::SetPMPServer) — the SW/HW protection layer is decided by the CDM's own
        // configuration, not the protection manager. Forcing UseSoftwareProtectionLayer here creates a protection
        // context that diverges from the CDM's PMP server and broke the protected-stream (MF_SD_PROTECTED + wrapped
        // MFMediaType_Protected) topology with DRM_E_CH_BAD_KEY. Set FG_CENC_FORCE_SW_LAYER=1 to A/B the old behavior.
        bool forceSw = GetEnvironmentVariableW(L"FG_CENC_FORCE_SW_LAYER", nullptr, 0) != 0;
        if (forceSw) map.Insert(L"Windows.Media.Protection.UseSoftwareProtectionLayer", winrt::box_value(true));
        LogLine(std::string("[eme-pm] protection manager ready (PMP server from CDM") +
                (forceSw ? "; FORCED software protection layer)." : "; layer per CDM config)."));
    }

    // IMFContentProtectionManager
    IFACEMETHODIMP BeginEnableContent(IMFActivate* enablerActivate, IMFTopology*, IMFAsyncCallback* callback, ::IUnknown* state) noexcept override
    {
        auto hx = [](HRESULT h){ std::stringstream ss; ss << "0x" << std::hex << (uint32_t)h; return ss.str(); };
        winrt::com_ptr<::IUnknown> obj;
        winrt::com_ptr<IMFAsyncResult> asyncResult;
        HRESULT hr = MFCreateAsyncResult(nullptr, callback, state, asyncResult.put());
        if (FAILED(hr)) return hr;
        hr = enablerActivate->ActivateObject(IID_PPV_ARGS(obj.put()));
        if (FAILED(hr)) { LogLine("[eme-pm] ActivateObject hr=" + hx(hr)); return hr; }
        GUID enablerType = GUID_NULL;
        winrt::com_ptr<IMFContentEnabler> enabler = obj.try_as<IMFContentEnabler>();
        if (enabler) enabler->GetEnableType(&enablerType);
        LogLine("[eme-pm] BeginEnableContent enablerType={" + [&]{ wchar_t b[64]; StringFromGUID2(enablerType, b, 64); return winrt::to_string(b); }() + "}");
        if (enablerType == MFENABLETYPE_MF_RebootRequired) return MF_E_REBOOT_REQUIRED;
        if (enablerType == MFENABLETYPE_MF_UpdateRevocationInformation) return MF_E_GRL_VERSION_TOO_LOW;
        if (enablerType == MFENABLETYPE_MF_UpdateUntrustedComponent) return HRESULT_FROM_WIN32(ERROR_INVALID_IMAGE_HASH);
        hr = m_cdm->SetContentEnabler(enabler.get(), asyncResult.get());
        LogLine("[eme-pm] SetContentEnabler hr=" + hx(hr));
        return hr;
    }
    IFACEMETHODIMP EndEnableContent(IMFAsyncResult*) noexcept override { return S_OK; }

    // IMediaProtectionManager (only Properties() is consumed by the media engine; events are unused).
    winrt::event_token ServiceRequested(winrt::Windows::Media::Protection::ServiceRequestedEventHandler const) { throw winrt::hresult_not_implemented(); }
    void ServiceRequested(winrt::event_token const) {}
    winrt::event_token RebootNeeded(winrt::Windows::Media::Protection::RebootNeededEventHandler const) { throw winrt::hresult_not_implemented(); }
    void RebootNeeded(winrt::event_token const) {}
    winrt::event_token ComponentLoadFailed(winrt::Windows::Media::Protection::ComponentLoadFailedEventHandler const) { throw winrt::hresult_not_implemented(); }
    void ComponentLoadFailed(winrt::event_token const) {}
    winrt::Windows::Foundation::Collections::PropertySet Properties() { return m_props; }
};

// The engine's NeedKey callback: drive the PROVEN CDM session (CreateSession + GenerateRequest + license + Update)
// with the CONTENT's init data (reuses CdmSessionCallbacks / HandleCdmKeyMessage / QueryCdmKeyStatus from the probe).
struct EmeNeedKeyNotify : public IMFMediaEngineNeedKeyNotify
{
    std::atomic<long> rc{1};
    HRESULT __stdcall QueryInterface(REFIID riid, void** ppv) override
    {
        if (!ppv) return E_POINTER;
        if (riid == __uuidof(::IUnknown) || riid == __uuidof(IMFMediaEngineNeedKeyNotify)) { *ppv = this; AddRef(); return S_OK; }
        *ppv = nullptr; return E_NOINTERFACE;
    }
    ULONG __stdcall AddRef() override { return (ULONG)++rc; }
    ULONG __stdcall Release() override { long v = --rc; if (!v) delete this; return (ULONG)v; }
    void __stdcall NeedKey(const BYTE* initData, DWORD cb) override
    {
        g_emeNeedKey = true;
        LogLine("[eme] *** NeedKey initData=" + std::to_string(cb) + "B ***");
        if (!g_emeCdm) { LogLine("[eme] NeedKey but no CDM."); return; }
        if (g_cdmSession) { LogLine("[eme] NeedKey again — session already open, ignoring."); return; }
        auto hx = [](HRESULT h){ std::stringstream ss; ss << "0x" << std::hex << (uint32_t)h; return ss.str(); };
        CdmSessionCallbacks* cb2 = new CdmSessionCallbacks();
        IMFContentDecryptionModuleSession* session = nullptr;
        HRESULT hr = g_emeCdm->CreateSession(MF_MEDIAKEYSESSION_TYPE_TEMPORARY, cb2, &session);
        LogLine("[eme] CreateSession hr=" + hx(hr));
        if (SUCCEEDED(hr) && session)
        {
            g_cdmSession = session;
            HRESULT hrg = session->GenerateRequest(L"cenc", initData, cb);   // fires KeyMessage -> HandleCdmKeyMessage
            LogLine("[eme] GenerateRequest hr=" + hx(hrg));
        }
        cb2->Release();
    }
};

// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════
//  MSE (Media Source Extensions) route — the clean path Microsoft's MediaEngineEMEUWPSample uses instead of a URL.
//  Rather than SetSource(url) (which hard-wedges IMFMediaEngine's PMP protected pipeline — see the pass-2 findings),
//  we feed the engine an in-app MSE source: IMFMediaEngineClassFactoryEx::CreateMediaSourceExtension gives an
//  IMFMediaSourceExtension; we hand it to the engine via the MF_MEDIA_ENGINE_EXTENSION extension (whose
//  BeginCreateObject returns the MSE QI'd to IMFMediaSource). We fetch a DASH fragmented-MP4/CENC video Representation
//  (init + media segments) over HTTP and IMFSourceBuffer::Append them; the built-in MSE demuxes internally and (for
//  protected content) raises the engine's NeedKey flow to the PROVEN CDM session (GenerateRequest + license + Update
//  -> USABLE). Same windowless swapchain as RunClear -> GetVideoSwapchainHandle -> the shareable DComp handle.
// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════

// ONE HttpClient for the whole process. A per-request client (what this used to construct) gets no connection pool and
// no TLS session cache, so every segment paid a fresh connect + full handshake — six serial handshakes before the first
// frame. WinRT's HttpClient is agile and safe to share across the feeder and the MTA loop. Deliberately leaked: a
// static WinRT object destroyed during DLL/CRT teardown would run its release on an already-torn-down apartment.
static WWH::HttpClient& SessionHttpClient()
{
    // Constructed once into static storage and never destroyed: a WinRT projection object released during CRT/DLL
    // teardown would call into an apartment that no longer exists.
    alignas(WWH::HttpClient) static unsigned char storage[sizeof(WWH::HttpClient)];
    static WWH::HttpClient* client = ::new (static_cast<void*>(storage)) WWH::HttpClient();
    return *client;
}

// One media-segment fetch, as the ABR estimator should see it: BODY bytes and BODY transfer time only. Connect + TLS +
// time-to-first-byte are excluded (they are RTT, not throughput — dash.js excludes them for exactly this reason), and
// so are init segments, which are small and RTT-dominated and would otherwise depress the estimate at startup.
struct HttpFetchTiming
{
    uint64_t headerMs = 0;    // connect + TLS + TTFB (diagnostic only; never folded into the throughput estimate)
    uint64_t transferMs = 0;  // response-body transfer
    uint64_t bytes = 0;
};

// Synchronous HTTP GET -> bytes (runs on the MTA worker thread). `mediaSegment` opts the transfer into the ABR
// telemetry; `cancel`, when supplied, is polled while the request is in flight so a seek or a shutdown does not have to
// wait out a whole segment download.
static std::vector<uint8_t> HttpGetBytes(const std::wstring& url, int& status, const std::wstring& extraHeaders = L"",
                                         bool mediaSegment = false, HttpFetchTiming* timing = nullptr,
                                         const std::function<bool()>& cancel = nullptr)
{
    status = 0;
    try
    {
        WWH::HttpRequestMessage req{ WWH::HttpMethod::Get(), Uri{ winrt::hstring(url) } };
        // Optional app-supplied request headers ("Name: Value\n" lines) — e.g. auth for a real CDN (M6).
        for (size_t p = 0; p < extraHeaders.size(); )
        {
            size_t nl = extraHeaders.find(L'\n', p);
            std::wstring line = extraHeaders.substr(p, nl == std::wstring::npos ? std::wstring::npos : nl - p);
            p = nl == std::wstring::npos ? extraHeaders.size() : nl + 1;
            size_t colon = line.find(L':');
            if (colon == std::wstring::npos) continue;
            std::wstring name = line.substr(0, colon), value = line.substr(colon + 1);
            while (!value.empty() && iswspace(value.front())) value.erase(value.begin());
            while (!name.empty() && iswspace(name.back())) name.pop_back();
            if (!name.empty()) try { req.Headers().TryAppendWithoutValidation(winrt::hstring(name), winrt::hstring(value)); } catch (...) {}
        }

        // ResponseHeadersRead (not the default ResponseContentRead) is what splits the two phases apart: this call
        // returns once the response line + headers are in, so everything after it is pure body transfer.
        const uint64_t headerStartMs = GetTickCount64();
        auto sendOp = SessionHttpClient().SendRequestAsync(req, WWH::HttpCompletionOption::ResponseHeadersRead);
        if (cancel)
        {
            while (sendOp.Status() == winrt::Windows::Foundation::AsyncStatus::Started)
            {
                if (cancel()) { sendOp.Cancel(); status = -1; return {}; }
                std::this_thread::sleep_for(std::chrono::milliseconds(10));
            }
        }
        auto resp = sendOp.get();
        const uint64_t headerMs = GetTickCount64() - headerStartMs;
        status = (int)resp.StatusCode();

        const uint64_t bodyStartMs = GetTickCount64();
        auto readOp = resp.Content().ReadAsBufferAsync();
        if (cancel)
        {
            while (readOp.Status() == winrt::Windows::Foundation::AsyncStatus::Started)
            {
                if (cancel()) { readOp.Cancel(); status = -1; return {}; }
                std::this_thread::sleep_for(std::chrono::milliseconds(10));
            }
        }
        auto buf = readOp.get();
        const uint64_t transferMs = GetTickCount64() - bodyStartMs;

        std::vector<uint8_t> out(buf.Length());
        if (buf.Length()) winrt::Windows::Storage::Streams::DataReader::FromBuffer(buf).ReadBytes(winrt::array_view<uint8_t>(out.data(), out.data() + out.size()));
        if (timing) { timing->headerMs = headerMs; timing->transferMs = transferMs; timing->bytes = out.size(); }
#ifdef FG_DESKTOP_DLL
        if (mediaSegment && status == 200 && !out.empty())
        {
            g_desktopBytesDownloaded.fetch_add((uint64_t)out.size(), std::memory_order_relaxed);
            g_desktopDownloadElapsedMs.fetch_add(std::max<uint64_t>(1, transferMs), std::memory_order_relaxed);
        }
#else
        (void)mediaSegment;
#endif
        return out;
    }
    catch (hresult_error const& e) { status = -(int)(uint32_t)e.code().value; return {}; }
}

// The always-on per-segment ABR ledger: every input the throughput estimate is built from, in one line, so a bad
// estimate can be read straight out of desktop-playready.log instead of inferred from a stalled picture. bytes*8/ms is
// exactly kbit/s. `rep` is -1 during the initial burst (before a representation has been selected).
static void LogAbrSegment(const char* track, int rep, int seg, const HttpFetchTiming& t, int64_t aheadMs)
{
    const uint64_t ms = t.transferMs ? t.transferMs : 1;
    LogLine(std::string("[cenc-abr] ") + track + " rep=" + std::to_string(rep) + " seg=" + std::to_string(seg) +
            " bytes=" + std::to_string(t.bytes) + " transferMs=" + std::to_string(t.transferMs) +
            " headerMs=" + std::to_string(t.headerMs) + " kbps=" + std::to_string((t.bytes * 8ULL) / ms) +
            " aheadMs=" + std::to_string((long long)aheadMs));
}

// MSE-level notify (MF_MSE_CALLBACK): source open/ended/close transitions.
struct MseSourceNotify : public IMFMediaSourceExtensionNotify
{
    std::atomic<long> rc{1};
    std::atomic<bool> opened{false}, ended{false}, closed{false};
    HRESULT __stdcall QueryInterface(REFIID riid, void** ppv) override
    {
        if (!ppv) return E_POINTER;
        if (riid == __uuidof(::IUnknown) || riid == __uuidof(IMFMediaSourceExtensionNotify)) { *ppv = this; AddRef(); return S_OK; }
        *ppv = nullptr; return E_NOINTERFACE;
    }
    ULONG __stdcall AddRef() override { return (ULONG)++rc; }
    ULONG __stdcall Release() override { long v = --rc; if (!v) delete this; return (ULONG)v; }
    void __stdcall OnSourceOpen() override  { opened = true; LogLine("[mse] OnSourceOpen"); }
    void __stdcall OnSourceEnded() override { ended = true;  LogLine("[mse] OnSourceEnded"); }
    void __stdcall OnSourceClose() override { closed = true; LogLine("[mse] OnSourceClose"); }
};

// Source-buffer notify: append/update lifecycle.
struct MseSourceBufferNotify : public IMFSourceBufferNotify
{
    std::atomic<long> rc{1};
    std::atomic<bool> updateEnded{false}, aborted{false}, errored{false};
    std::atomic<int>  errHr{0};
    HRESULT __stdcall QueryInterface(REFIID riid, void** ppv) override
    {
        if (!ppv) return E_POINTER;
        if (riid == __uuidof(::IUnknown) || riid == __uuidof(IMFSourceBufferNotify)) { *ppv = this; AddRef(); return S_OK; }
        *ppv = nullptr; return E_NOINTERFACE;
    }
    ULONG __stdcall AddRef() override { return (ULONG)++rc; }
    ULONG __stdcall Release() override { long v = --rc; if (!v) delete this; return (ULONG)v; }
    void __stdcall OnUpdateStart() override {}
    void __stdcall OnAbort() override { aborted = true; }
    void __stdcall OnError(HRESULT hr) override { errored = true; errHr = (int)hr; LogLine("[mse-sb] OnError hr=0x" + [&]{ std::stringstream s; s<<std::hex<<(uint32_t)hr; return s.str(); }()); }
    void __stdcall OnUpdate() override {}
    void __stdcall OnUpdateEnd() override { updateEnded = true; }
};

// The engine extension (MF_MEDIA_ENGINE_EXTENSION): its BeginCreateObject hands the engine our MSE as an IMFMediaSource,
// so engine->SetSource(<any mse url>) resolves to the in-app MSE instead of a real byte-stream/URL resolver.
struct MseEngineExtension : public IMFMediaEngineExtension
{
    std::atomic<long> rc{1};
    IUnknown* m_mse = nullptr;   // owned (the MSE's IUnknown — the object the engine resolves the source to)
    ~MseEngineExtension() { if (m_mse) m_mse->Release(); }
    HRESULT __stdcall QueryInterface(REFIID riid, void** ppv) override
    {
        if (!ppv) return E_POINTER;
        if (riid == __uuidof(::IUnknown) || riid == __uuidof(IMFMediaEngineExtension)) { *ppv = this; AddRef(); return S_OK; }
        *ppv = nullptr; return E_NOINTERFACE;
    }
    ULONG __stdcall AddRef() override { return (ULONG)++rc; }
    ULONG __stdcall Release() override { long v = --rc; if (!v) delete this; return (ULONG)v; }
    HRESULT __stdcall CanPlayType(BOOL, BSTR mime, MF_MEDIA_ENGINE_CANPLAY* answer) override
    { LogLine("[mse-ext] CanPlayType " + winrt::to_string(winrt::hstring(mime ? mime : L""))); if (answer) *answer = MF_MEDIA_ENGINE_CANPLAY_PROBABLY; return S_OK; }
    HRESULT __stdcall BeginCreateObject(BSTR url, IMFByteStream*, MF_OBJECT_TYPE type, IUnknown** cancelCookie,
                                        IMFAsyncCallback* callback, IUnknown* state) override
    {
        LogLine("[mse-ext] BeginCreateObject type=" + std::to_string((int)type) + " url=" + winrt::to_string(winrt::hstring(url ? url : L"")));
        if (cancelCookie) *cancelCookie = nullptr;
        if (type != MF_OBJECT_MEDIASOURCE || !m_mse) return MF_E_UNSUPPORTED_BYTESTREAM_TYPE;
        IMFAsyncResult* result = nullptr;
        HRESULT hr = MFCreateAsyncResult(m_mse, callback, state, &result);
        if (FAILED(hr)) return hr;
        result->SetStatus(S_OK);
        hr = MFInvokeCallback(result);
        result->Release();
        return hr;
    }
    HRESULT __stdcall CancelObjectCreation(IUnknown*) override { return S_OK; }
    HRESULT __stdcall EndCreateObject(IMFAsyncResult* result, IUnknown** ppObject) override
    {
        if (!ppObject) return E_POINTER;
        *ppObject = nullptr;
        HRESULT hr = result ? result->GetStatus() : E_UNEXPECTED;
        if (SUCCEEDED(hr) && result) { IUnknown* o = nullptr; result->GetObject(&o); *ppObject = o; }
        return hr;
    }
};

// Append one buffer and block until the source buffer finishes updating (MSE requires updating==false before the next).
static bool AppendAndWait(IMFSourceBuffer* sb, MseSourceBufferNotify* n, const std::vector<uint8_t>& data, const char* tag)
{
    auto hx = [](HRESULT h){ std::stringstream s; s << "0x" << std::hex << (uint32_t)h; return s.str(); };
    if (data.empty()) { LogLine(std::string("[mse] ") + tag + " EMPTY (fetch failed)"); return false; }
    n->updateEnded = false; n->errored = false;
    HRESULT hr = sb->Append(data.data(), (DWORD)data.size());
    if (FAILED(hr)) { LogLine(std::string("[mse] Append(") + tag + ") hr=" + hx(hr)); return false; }
    auto dl = std::chrono::steady_clock::now() + std::chrono::seconds(20);
    while (sb->GetUpdating() && !n->errored && std::chrono::steady_clock::now() < dl)
        std::this_thread::sleep_for(std::chrono::milliseconds(10));
    bool ok = !sb->GetUpdating() && !n->errored;
    LogLine(std::string("[mse] appended ") + tag + " " + std::to_string(data.size()) + "B -> " + (ok ? "ok" : (n->errored ? "ERROR" : "timeout")));
    return ok;
}

// A single video Representation of a DASH fMP4/CENC (or clear) stream: how to build its init + media segment URLs.
struct DashRep { std::wstring base, initName, mediaPrefix, mediaSuffix; int pad; int startNumber; int segCount; std::wstring mime; };

// Fetch the init segment + N media segments and Append them (init first) to the MSE source buffer.
static int FetchAndAppendDash(IMFSourceBuffer* sb, MseSourceBufferNotify* n, const DashRep& rep)
{
    int st = 0;
    LogLine("[mse] fetching init " + winrt::to_string(winrt::hstring(rep.base + rep.initName)) + " ...");
    auto init = HttpGetBytes(rep.base + rep.initName, st);
    LogLine("[mse] init HTTP " + std::to_string(st) + " (" + std::to_string(init.size()) + " bytes)");
    if (!AppendAndWait(sb, n, init, "init")) return 0;
    int appended = 0;
    for (int i = 0; i < rep.segCount; i++)
    {
        int num = rep.startNumber + i;
        wchar_t numbuf[16];
        if (rep.pad > 0) swprintf(numbuf, 16, L"%0*d", rep.pad, num); else swprintf(numbuf, 16, L"%d", num);
        std::wstring url = rep.base + rep.mediaPrefix + numbuf + rep.mediaSuffix;
        auto seg = HttpGetBytes(url, st);
        if (st != 200 || seg.empty()) { LogLine("[mse] media #" + std::to_string(num) + " HTTP " + std::to_string(st) + " — stop"); break; }
        char tag[24]; snprintf(tag, 24, "seg#%d", num);
        if (!AppendAndWait(sb, n, seg, tag)) break;
        appended++;
    }
    return appended;
}

// One MSE-fed media-engine attempt. protectedContent => wire the CDM + protection manager + NeedKey (Axinom singlekey
// vector); else a clear DASH Representation through the SAME MSE->handle path (the visual control). Returns true only
// if it reached LOADEDMETADATA with a non-zero swapchain handle (then publishes the coord + keeps the producer alive).
static bool RunMseAttempt(bool protectedContent, const std::wstring& storePath)
{
    auto hx = [](HRESULT h){ std::stringstream s; s << "0x" << std::hex << (uint32_t)h; return s.str(); };
    bool wireProt = protectedContent && !g_mseNoProtWiring;   // full CDM/PMP/NeedKey wiring vs. diagnostic bare feed
    LogLine(std::string("[mse] ===== attempt ") + (protectedContent ? (wireProt ? "PROTECTED (Axinom singlekey CENC)" : "PROTECTED-CONTENT / NO-PROTECTION-WIRING (diag)") : "CLEAR (control)"));
    g_emeNeedKey = false; g_cdmUsable = false; g_cdmKeyMsg = false; g_cdmLicHttp = 0; g_cdmSession = nullptr;

    // The content: a single video Representation of a DASH fMP4 stream (protected = Axinom CENC singlekey 720p H.264;
    // clear = Axinom v7-Clear 288p H.264 — same MSE->handle path, no CDM).
    DashRep rep;
    if (protectedContent)
        rep = { L"https://media.axprod.net/TestVectors/Dash/protected_dash_1080p_h264_singlekey/",
                L"video-H264-720-2100k_init.mp4", L"video-H264-720-2100k_", L".m4s", 0, 1, 4,
                L"video/mp4; codecs=\"avc1.64001f\"" };
    else
        rep = { L"https://media.axprod.net/TestVectors/v7-Clear/",
                L"1/init.mp4", L"1/", L".m4s", 4, 1, 4,
                L"video/mp4; codecs=\"avc1.64001f\"" };

    HRESULT hr;
    ID3D11Device* d3d = nullptr; ID3D11DeviceContext* ctx = nullptr;
    UINT flags = D3D11_CREATE_DEVICE_BGRA_SUPPORT | D3D11_CREATE_DEVICE_VIDEO_SUPPORT;
    if (FAILED(hr = D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, flags, nullptr, 0, D3D11_SDK_VERSION, &d3d, nullptr, &ctx)))
    { LogLine("[mse] D3D11CreateDevice hr=" + hx(hr)); return false; }
    if (ctx) ctx->Release();
    {   // SetMultithreadProtected — ID3D10Multithread vtable slot 5 (required when the D3D device is shared with MF).
        void* mt = nullptr; GUID iidMt = { 0x9b7e4e00, 0x342c, 0x4106, {0xa1,0x9f,0x4f,0x27,0x04,0xf6,0x89,0xf0} };
        if (SUCCEEDED(d3d->QueryInterface(iidMt, &mt)) && mt)
        { auto setProt = (int (STDMETHODCALLTYPE*)(void*, int))(*(void***)mt)[5]; setProt(mt, 1); ((::IUnknown*)mt)->Release(); }
    }
    UINT resetToken = 0; IMFDXGIDeviceManager* dxgiMgr = nullptr;
    if (FAILED(hr = MFCreateDXGIDeviceManager(&resetToken, &dxgiMgr))) { LogLine("[mse] MFCreateDXGIDeviceManager hr=" + hx(hr)); d3d->Release(); return false; }
    if (FAILED(hr = dxgiMgr->ResetDevice((::IUnknown*)d3d, resetToken))) { LogLine("[mse] ResetDevice hr=" + hx(hr)); dxgiMgr->Release(); d3d->Release(); return false; }

    // CDM + protection manager (protected only).
    winrt::com_ptr<MediaEngineProtectionManager> pm;
    if (wireProt)
    {
        g_emeCdm = CreateAndPrepareCdm(L"com.microsoft.playready.recommendation", storePath);
        if (!g_emeCdm) { LogLine("[mse] CDM creation failed."); dxgiMgr->Release(); d3d->Release(); return false; }
        try { pm = winrt::make_self<MediaEngineProtectionManager>(g_emeCdm); }
        catch (winrt::hresult_error const& e) { LogLine("[mse] protection manager ctor hr=" + hx(e.code().value)); g_emeCdm->Release(); g_emeCdm=nullptr; dxgiMgr->Release(); d3d->Release(); return false; }
    }

    // Class factory (+Ex for CreateMediaSourceExtension).
    IMFMediaEngineClassFactory* factory = nullptr;
    if (FAILED(hr = CoCreateInstance(CLSID_MFMediaEngineClassFactory, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&factory))))
    { LogLine("[mse] CoCreate factory hr=" + hx(hr)); if (g_emeCdm){g_emeCdm->Release();g_emeCdm=nullptr;} dxgiMgr->Release(); d3d->Release(); return false; }
    IMFMediaEngineClassFactoryEx* factoryEx = nullptr;
    if (FAILED(hr = factory->QueryInterface(IID_PPV_ARGS(&factoryEx))))
    { LogLine("[mse] QI ClassFactoryEx hr=" + hx(hr)); factory->Release(); if (g_emeCdm){g_emeCdm->Release();g_emeCdm=nullptr;} dxgiMgr->Release(); d3d->Release(); return false; }

    // Create the MSE (with the MF_MSE_CALLBACK source notify).
    MseSourceNotify* mseNotify = new MseSourceNotify();
    IMFAttributes* mseAttr = nullptr; MFCreateAttributes(&mseAttr, 1);
    mseAttr->SetUnknown(MF_MSE_CALLBACK, (::IUnknown*)mseNotify);
    IMFMediaSourceExtension* mse = nullptr;
    hr = factoryEx->CreateMediaSourceExtension(0, mseAttr, &mse);
    mseAttr->Release();
    LogLine("[mse] CreateMediaSourceExtension hr=" + hx(hr));
    if (FAILED(hr) || !mse) { factoryEx->Release(); factory->Release(); mseNotify->Release(); if (g_emeCdm){g_emeCdm->Release();g_emeCdm=nullptr;} dxgiMgr->Release(); d3d->Release(); return false; }
    // Diagnostic: does the MSE expose a media-source facet? (E_NOINTERFACE observed — the engine consumes the MSE itself.)
    { IMFMediaSource* probe = nullptr; HRESULT qh = mse->QueryInterface(IID_PPV_ARGS(&probe)); LogLine("[mse] (QI MSE->IMFMediaSource hr=" + hx(qh) + ")"); if (probe) probe->Release(); }

    MseEngineExtension* ext = new MseEngineExtension();
    mse->QueryInterface(IID_PPV_ARGS(&ext->m_mse));   // the MSE's IUnknown — the object the engine resolves to

    // Media engine.
    MediaEngineNotify* notify = new MediaEngineNotify();
    EmeNeedKeyNotify* needKey = wireProt ? new EmeNeedKeyNotify() : nullptr;
    IMFAttributes* attrs = nullptr; MFCreateAttributes(&attrs, 8);
    attrs->SetUnknown(MF_MEDIA_ENGINE_CALLBACK, (::IUnknown*)notify);
    attrs->SetUnknown(MF_MEDIA_ENGINE_DXGI_MANAGER, (::IUnknown*)dxgiMgr);
    attrs->SetUnknown(MF_MEDIA_ENGINE_EXTENSION, (::IUnknown*)ext);
    attrs->SetUINT32(MF_MEDIA_ENGINE_VIDEO_OUTPUT_FORMAT, DXGI_FORMAT_B8G8R8A8_UNORM);
    if (wireProt)
    {
        attrs->SetUnknown(MF_MEDIA_ENGINE_NEEDKEY_CALLBACK, (::IUnknown*)needKey);
        attrs->SetUINT32(MF_MEDIA_ENGINE_CONTENT_PROTECTION_FLAGS, MF_MEDIA_ENGINE_ENABLE_PROTECTED_CONTENT);
    }
    IMFMediaEngine* engine = nullptr;
    hr = factory->CreateInstance(0, attrs, &engine);
    attrs->Release();
    LogLine("[mse] CreateInstance(engine) hr=" + hx(hr));
    if (FAILED(hr) || !engine)
    { if (needKey) needKey->Release(); notify->Release(); ext->Release(); mse->Release(); factoryEx->Release(); factory->Release(); mseNotify->Release(); if (g_emeCdm){g_emeCdm->Release();g_emeCdm=nullptr;} dxgiMgr->Release(); d3d->Release(); return false; }

    if (wireProt)
    {
        IMFMediaEngineProtectedContent* prot = nullptr;
        if (SUCCEEDED(engine->QueryInterface(IID_PPV_ARGS(&prot))) && prot)
        { auto cpm = pm.as<IMFContentProtectionManager>(); HRESULT hs = prot->SetContentProtectionManager(cpm.get()); LogLine("[mse] SetContentProtectionManager hr=" + hx(hs)); prot->Release(); }
        else LogLine("[mse] QI IMFMediaEngineProtectedContent FAILED.");
    }

    IMFMediaEngineEx* engineEx = nullptr;
    engine->QueryInterface(IID_PPV_ARGS(&engineEx));
    if (engineEx) engineEx->EnableWindowlessSwapchainMode(TRUE);

    // SetSource(mse url) — routes through the extension's BeginCreateObject, which returns our MSE. The engine's
    // resource-selection/load then OPENs the MSE (OnSourceOpen) without needing Play().
    BSTR burl = SysAllocString(L"mse://fluentgpu/stream.mp4");
    hr = engineEx ? engineEx->SetSource(burl) : engine->SetSource(burl);
    SysFreeString(burl);
    LogLine("[mse] SetSource hr=" + hx(hr));

    // Wait for the MSE to open.
    auto openDl = std::chrono::steady_clock::now() + std::chrono::seconds(10);
    while (!mseNotify->opened && mse->GetReadyState() != MF_MSE_READY_OPEN && !notify->error && std::chrono::steady_clock::now() < openDl)
        std::this_thread::sleep_for(std::chrono::milliseconds(50));
    bool open = mseNotify->opened || mse->GetReadyState() == MF_MSE_READY_OPEN;
    LogLine(std::string("[mse] source open=") + (open ? "1" : "0") + " readyState=" + std::to_string((int)mse->GetReadyState()));

    bool ok = false; HANDLE swHandle = nullptr; DWORD nvw = 0, nvh = 0;
    IMFSourceBuffer* sb = nullptr;
    MseSourceBufferNotify* sbN = new MseSourceBufferNotify();
    if (open)
    {
        BOOL supported = mse->IsTypeSupported(SysAllocString(rep.mime.c_str()));   // (leak of BSTR ok — one-shot helper)
        BSTR mime = SysAllocString(rep.mime.c_str());
        hr = mse->AddSourceBuffer(mime, sbN, &sb);
        SysFreeString(mime);
        LogLine("[mse] IsTypeSupported=" + std::to_string((int)supported) + " AddSourceBuffer hr=" + hx(hr));
        if (SUCCEEDED(hr) && sb)
        {
            int appended = FetchAndAppendDash(sb, sbN, rep);
            LogLine("[mse] appended " + std::to_string(appended) + " media segment(s); marking end of stream.");
            if (!sb->GetUpdating()) mse->SetEndOfStream(MF_MSE_ERROR_NOERROR);
            engine->Play();
            LogLine("[mse] Play() returned; waiting for CANPLAY (NeedKey/license run on MF threads) ...");

            // Wait for metadata + first-frame-ready. NeedKey/license (protected) happen on MF threads meanwhile.
            auto metaDl = std::chrono::steady_clock::now() + std::chrono::seconds(45);
            while (!notify->canplay && !notify->error && std::chrono::steady_clock::now() < metaDl)
                std::this_thread::sleep_for(std::chrono::milliseconds(100));
            LogLine(std::string("[mse] post-play metadata=") + (notify->metadata?"1":"0") + " canplay=" + (notify->canplay?"1":"0") +
                    " playing=" + (notify->playing?"1":"0") + " error=" + (notify->error?"1":"0") + " lastEvent=" + std::to_string(notify->lastEvent));

            if ((notify->metadata || notify->canplay) && engineEx)
            {
                engineEx->GetNativeVideoSize(&nvw, &nvh); if (!nvw) { nvw = (DWORD)(protectedContent ? 1280 : 512); nvh = (DWORD)(protectedContent ? 720 : 288); }
                // The windowless swapchain is created lazily once a frame is presented — pump OnVideoStreamTick +
                // UpdateVideoStream and poll GetVideoSwapchainHandle until it is non-zero (S_FALSE + null means not-yet).
                RECT dst = { 0, 0, (LONG)nvw, (LONG)nvh }; MFARGB border = { 0, 0, 0, 255 };
                auto hdlDl = std::chrono::steady_clock::now() + std::chrono::seconds(12);
                while (std::chrono::steady_clock::now() < hdlDl && !notify->error)
                {
                    LONGLONG pts; engine->OnVideoStreamTick(&pts);
                    engineEx->UpdateVideoStream(nullptr, &dst, &border);
                    hr = engineEx->GetVideoSwapchainHandle(&swHandle);
                    if (SUCCEEDED(hr) && swHandle) break;
                    std::this_thread::sleep_for(std::chrono::milliseconds(60));
                }
                LogLine("[mse] LOADEDMETADATA " + std::to_string(nvw) + "x" + std::to_string(nvh) + " GetVideoSwapchainHandle hr=" + hx(hr) + " handle=" + std::to_string((uint64_t)(uintptr_t)swHandle));
                ok = SUCCEEDED(hr) && swHandle != nullptr;
            }
            else
                LogLine(std::string("[mse] no canplay (") + (notify->error ? ("error code=" + std::to_string(notify->errCode) + " hr=" + hx(notify->errHr)) : "timeout") + ")");
        }
    }

    QueryCdmKeyStatus();
    LogLine(std::string("[mse] STATE open=") + (open?"1":"0") + " needKey=" + (g_emeNeedKey?"1":"0") + " keyMsg=" + (g_cdmKeyMsg?"1":"0") +
            " licHttp=" + std::to_string(g_cdmLicHttp) + " keyStatus=" + g_cdmKeyStatus + " USABLE=" + (g_cdmUsable?"YES":"no") +
            " metadata=" + (notify->metadata?"1":"0"));

    if (ok)
    {
        RECT dst = { 0, 0, (LONG)nvw, (LONG)nvh }; MFARGB border = { 0, 0, 0, 255 };
        engineEx->UpdateVideoStream(nullptr, &dst, &border);
        WriteCoord(protectedContent ? "protected" : "clear", GetCurrentProcessId(), (uint64_t)(uintptr_t)swHandle, (int)nvw, (int)nvh, "playing");
        g_emePublished = true;
        LogLine(std::string("[mse] READY — published ") + (protectedContent ? "PROTECTED" : "CLEAR") + " swapchain handle; keeping alive (auto-present).");
        auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds(120);
        while (std::chrono::steady_clock::now() < deadline && !StopRequested())
        {
            std::this_thread::sleep_for(std::chrono::milliseconds(80));
            LONGLONG pts; engine->OnVideoStreamTick(&pts);
            engineEx->UpdateVideoStream(nullptr, nullptr, nullptr);
        }
        LogLine("[mse] exiting keep-alive.");
    }

    engine->Shutdown();
    if (g_cdmSession) { g_cdmSession->Release(); g_cdmSession = nullptr; }
    if (sb) sb->Release();
    sbN->Release();
    if (engineEx) engineEx->Release();
    engine->Release();
    if (needKey) needKey->Release();
    notify->Release();
    ext->Release();          // Releases its MSE ref in its dtor
    mse->Release();
    mseNotify->Release();
    factoryEx->Release(); factory->Release();
    pm = nullptr;
    if (g_emeCdm) { g_emeCdm->Release(); g_emeCdm = nullptr; }
    dxgiMgr->Release(); d3d->Release();
    g_emeAttemptDone = true;
    return ok;
}

// Drive one MSE attempt on a detached thread with a bounded publish-wait (protected Play()/PMP can still block), then
// ExitProcess so the process always publishes a coord and terminates cleanly for the deploy loop.
static int RunMse(bool protectedContent, const std::wstring& baseDir)
{
    int hr;
    if (FAILED(hr = CoInitializeEx(nullptr, COINIT_MULTITHREADED)) && hr != S_FALSE) LogLine("[mse] CoInit hr=0x" + std::to_string((uint32_t)hr));
    if (FAILED(hr = MFStartup(MF_VERSION, MFSTARTUP_FULL))) { LogLine("[mse] MFStartup hr=0x" + std::to_string((uint32_t)hr)); return 2; }
    std::wstring storePath = baseDir + L"\\PRStore"; CreateDirectoryW(storePath.c_str(), nullptr);

    g_emePublished = false; g_emeAttemptDone = false;
    std::thread([protectedContent, storePath]{ RunMseAttempt(protectedContent, storePath); }).detach();
    auto dl = std::chrono::steady_clock::now() + std::chrono::seconds(140);
    while (std::chrono::steady_clock::now() < dl && !g_emePublished && !g_emeAttemptDone)
        std::this_thread::sleep_for(std::chrono::milliseconds(200));

    if (g_emePublished)
    {
        LogLine("[mse] handle published — holding process for the engine consumer.");
        auto keep = std::chrono::steady_clock::now() + std::chrono::seconds(120);
        while (std::chrono::steady_clock::now() < keep && !StopRequested()) std::this_thread::sleep_for(std::chrono::milliseconds(200));
        MFShutdown(); ExitProcess(0);
    }
    bool wedged = !g_emeAttemptDone;
    LogLine(std::string("[mse] NO handle (") + (wedged ? "Play/PMP pipeline blocked" : "attempt failed cleanly") + ") — publishing error state.");
    WriteCoord(protectedContent ? "protected" : "clear", GetCurrentProcessId(), 0, protectedContent ? 1280 : 512, protectedContent ? 720 : 288, "error");
    auto keep = std::chrono::steady_clock::now() + std::chrono::seconds(15);
    while (std::chrono::steady_clock::now() < keep && !StopRequested()) std::this_thread::sleep_for(std::chrono::milliseconds(200));
    ExitProcess(1);
}

// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════
//  CUSTOM CENC IMFMediaSource route — the last mile (Microsoft's MediaEngineEMEUWPSample `CdmMediaSource` model).
//  We demux fragmented-MP4/CENC IN-APP (CencMediaSource.h) and present the media engine a custom IMFMediaSource whose
//  IMFMediaStream emits already-ENCRYPTED IMFSamples carrying the CENC per-sample attributes (protection scheme, KID,
//  IV/SampleID, subsample split). With the CDM + protection manager wired (proven USABLE key), the engine's protected
//  pipeline decrypts+decodes them → windowless swapchain → GetVideoSwapchainHandle → the shareable DComp handle. This
//  is what the built-in MSE could not do (it rejects protected byte streams — MF_E_DRM_UNSUPPORTED).
// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════
#include "CencMediaSource.h"

// Proactively acquire the license into the CDM (custom-source path does not surface a PSSH for the engine's NeedKey,
// so we drive the PROVEN session directly with the content's init data). Leaves g_cdmSession alive so the key stays
// usable during decode. Returns true once the key reaches USABLE.
static bool DriveCdmLicenseProactive(const std::vector<uint8_t>& initData)
{
    auto hx = [](HRESULT h){ std::stringstream s; s << "0x" << std::hex << (uint32_t)h; return s.str(); };
    if (!g_emeCdm) { LogLine("[cenc] no CDM for proactive license."); return false; }
    if (initData.empty()) { LogLine("[cenc] no init data (pssh) for GenerateRequest."); return false; }
    CdmSessionCallbacks* cb = new CdmSessionCallbacks();
    IMFContentDecryptionModuleSession* session = nullptr;
    // Session type MUST match the license's persistence or Update() rejects it with TypeError (MF_TYPE_ERR,
    // 0x80704005). Production = Spotify, which issues NON-persistable streaming licenses → TEMPORARY (verified: temp
    // session → Update() hr=0x0, key USABLE, plays). The Axinom test entitlement carries allow_persistence=true and
    // returns a PERSISTABLE license, which conversely needs PERSISTENT_LICENSE — set FG_CENC_PERSIST_SESSION=1 for
    // that A/B path. Default TEMPORARY.
    MF_MEDIAKEYSESSION_TYPE sessionType = GetEnvironmentVariableW(L"FG_CENC_PERSIST_SESSION", nullptr, 0) != 0
        ? MF_MEDIAKEYSESSION_TYPE_PERSISTENT_LICENSE : MF_MEDIAKEYSESSION_TYPE_TEMPORARY;
    HRESULT hr = g_emeCdm->CreateSession(sessionType, cb, &session);
    LogLine(std::string("[cenc] proactive CreateSession (") +
            (sessionType == MF_MEDIAKEYSESSION_TYPE_TEMPORARY ? "temporary" : "persistent-license") +
            ") hr=" + hx(hr));
    if (SUCCEEDED(hr) && session)
    {
        g_cdmSession = session;   // kept alive (not released here) so the key persists through decode
        HRESULT hrg = session->GenerateRequest(L"cenc", initData.data(), (DWORD)initData.size());
        LogLine("[cenc] proactive GenerateRequest(cenc, " + std::to_string(initData.size()) + "B) hr=" + hx(hrg));
        auto dl = std::chrono::steady_clock::now() + std::chrono::seconds(30);
        while (!g_cdmUsable && std::chrono::steady_clock::now() < dl)
            std::this_thread::sleep_for(std::chrono::milliseconds(200));
        QueryCdmKeyStatus();
    }
    cb->Release();
    LogLine(std::string("[cenc] proactive license: keyMsg=") + (g_cdmKeyMsg?"1":"0") + " licHttp=" + std::to_string(g_cdmLicHttp) +
            " keyStatus=" + g_cdmKeyStatus + " USABLE=" + (g_cdmUsable?"YES":"no"));
    return g_cdmUsable;
}

// One custom-source attempt: demux the Axinom singlekey CENC Representation, build the custom IMFMediaSource, wire it to
// the CDM-backed media engine, and pump the windowless swapchain handle. Returns true on a non-zero handle (published).
static bool RunCustomSourceAttempt(const std::wstring& storePath)
{
    auto hx = [](HRESULT h){ std::stringstream s; s << "0x" << std::hex << (uint32_t)h; return s.str(); };
    LogLine("[cenc] ===== CUSTOM CENC SOURCE attempt (Axinom singlekey fMP4/CENC, in-app demux)");
    g_emeNeedKey = false; g_cdmUsable = false; g_cdmKeyMsg = false; g_cdmLicHttp = 0; g_cdmSession = nullptr;

    // ── 1. Demux: fetch init + media segments, parse CENC. The source comes from the managed OPEN DESCRIPTOR; when it
    //       supplies no init URL we fall back to the baked Axinom singlekey vector (legacy FgPlayReadyRun shim). ───────
    const std::wstring axBase = L"https://media.axprod.net/TestVectors/Dash/protected_dash_1080p_h264_singlekey/";
    std::wstring initUrl, segBase, segPrefix, segSuffix, segHeaders;
    std::wstring aInitUrl, aSegBase, aSegPrefix, aSegSuffix;   // the video's own soundtrack; empty => video only
    int startNumber = 1, segCount = 6, segStride = 1;
#ifdef FG_DESKTOP_DLL
    if (!g_openInitUrl.empty())
    {
        initUrl    = g_openInitUrl;
        segBase    = g_openSegBase;
        segPrefix  = g_openSegPrefix;
        segSuffix  = g_openSegSuffix.empty() ? L".m4s" : g_openSegSuffix;
        startNumber= g_openStartNumber >= 0 ? g_openStartNumber : 1;   // >= 0: Spotify's timestamped segments start at 0
        segCount   = g_openSegCount   > 0 ? g_openSegCount   : 1;
        segStride  = g_openSegStride  > 0 ? g_openSegStride   : 1;      // 1 = numbered; N = N-second timestamped step
        segHeaders = g_openHeaders;
        // The audio representation rides the SAME segment grid (same start number, count and stride) — only the profile
        // in its URL differs — so one loop drives both tracks.
        aInitUrl   = g_openAudioInitUrl;
        aSegBase   = g_openAudioSegBase;
        aSegPrefix = g_openAudioSegPrefix;
        aSegSuffix = g_openAudioSegSuffix;
        LogLine("[cenc] source: descriptor initUrl=" + winrt::to_string(winrt::hstring(initUrl)) +
                " segs=" + std::to_string(startNumber) + ".." + std::to_string(startNumber + (segCount - 1) * segStride) +
                " stride=" + std::to_string(segStride) +
                " audio=" + (aInitUrl.empty() ? "none" : "yes"));
    }
    else
#endif
    {
        initUrl    = axBase + L"video-H264-720-2100k_init.mp4";
        segBase    = axBase;
        segPrefix  = L"video-H264-720-2100k_";
        segSuffix  = L".m4s";
        startNumber= 1; segCount = 6;
        LogLine("[cenc] source: baked Axinom singlekey vector (no descriptor source supplied).");
    }

    int st = 0;
    auto initSeg = HttpGetBytes(initUrl, st, segHeaders);
    LogLine("[cenc] init " + winrt::to_string(winrt::hstring(initUrl)) + " HTTP " + std::to_string(st) + " (" + std::to_string(initSeg.size()) + "B)");
    cenc::InitInfo info;
    if (!cenc::ParseInit(initSeg, info))
    { LogLine("[cenc] ParseInit FAILED (no usable video sample entry / avcC)."); return false; }
    {
        std::stringstream kh; for (int i = 0; i < 16; i++) { kh << std::hex << std::setw(2) << std::setfill('0') << (int)info.kid[i]; }
        char cc[5] = { (char)(info.codec4cc >> 24), (char)(info.codec4cc >> 16), (char)(info.codec4cc >> 8), (char)info.codec4cc, 0 };
        LogLine("[cenc] init parsed: " + std::to_string(info.width) + "x" + std::to_string(info.height) +
                " codec=" + std::string(cc) +
                " scheme=" + std::string(info.scheme == 1 ? "cbcs" : "cenc") +
                " enc=" + (info.encrypted ? "1" : "0") + " ivSize=" + std::to_string((int)info.perSampleIvSize) +
                " timescale=" + std::to_string(info.timescale) + " KID=" + kh.str() +
                " avcC=" + std::to_string(info.avcC.size()) + "B pssh=" + std::to_string(info.pssh.size()) + "B");
    }
#ifdef FG_DESKTOP_DLL
    // Publish the KID (hex) so the managed license relay can populate the LicenseRequest.KeyId.
    { std::wstringstream ks; for (int i = 0; i < 16; i++) ks << std::hex << std::setw(2) << std::setfill(L'0') << (int)info.kid[i]; g_kidHex = ks.str(); }
#endif

    // ── 1b. The AUDIO init segment: the video's own soundtrack, a separate single-track file under the same key. Its
    //        absence is NOT a failure — the session then plays video only, exactly as it did before. ────────────────
    cenc::InitInfo audioInfo;
    bool haveAudio = false;
    if (!aInitUrl.empty())
    {
        int ast = 0;
        auto aInitSeg = HttpGetBytes(aInitUrl, ast, segHeaders);
        if (ast == 200 && !aInitSeg.empty() && cenc::ParseInit(aInitSeg, audioInfo) &&
            audioInfo.kind == cenc::TrackKind::Audio)
        {
            haveAudio = true;
            char acc[5] = { (char)(audioInfo.codec4cc >> 24), (char)(audioInfo.codec4cc >> 16),
                            (char)(audioInfo.codec4cc >> 8), (char)audioInfo.codec4cc, 0 };
            LogLine("[cenc] audio init parsed: codec=" + std::string(acc) +
                    " " + std::to_string(audioInfo.channels) + "ch/" + std::to_string(audioInfo.sampleRate) + "Hz" +
                    " asc=" + std::to_string(audioInfo.asc.size()) + "B" +
                    " enc=" + (audioInfo.encrypted ? "1" : "0") +
                    " timescale=" + std::to_string(audioInfo.timescale) +
                    " ivSize=" + std::to_string((int)audioInfo.perSampleIvSize));
        }
        else
        {
            LogLine("[cenc] audio init UNUSABLE (HTTP " + std::to_string(ast) + ", " + std::to_string(aInitSeg.size()) +
                    "B) — continuing VIDEO ONLY");
        }
    }

    // ── 1c. Fetch a SHORT initial burst, then let the rest stream in behind playback. Downloading the whole track first
    //        (~50 serial requests for a 3.5-minute video, doubled once audio joins) is what made "watch video" sit on a
    //        spinner for tens of seconds and would now trip the managed start watchdog outright. ─────────────────────
    // ~16s of media at Spotify's 4s segments. Two segments (~8s) sat BELOW the managed ABR's 12s upgrade gate, so the
    // session could never accumulate the headroom that authorises an upshift and stayed pinned at the conservative
    // starting representation forever.
    constexpr int kInitialBurstSegments = 4;
    constexpr size_t kMaxSamplesAhead   = 900;  // fetch-ahead ceiling per track (~30s of video) so memory stays bounded

    std::vector<cenc::Sample> samples, audioSamples;
    uint64_t decodeTicks = 0, audioDecodeTicks = 0;
    int burst = segCount < kInitialBurstSegments ? segCount : kInitialBurstSegments;
    int nextSegment = 0;   // index of the first segment the background feeder must fetch
    for (int i = 0; i < burst; i++)
    {
        int num = startNumber + i * segStride;
        std::wstring url = segBase + segPrefix + std::to_wstring(num) + segSuffix;
        HttpFetchTiming timing;
        auto seg = HttpGetBytes(url, st, segHeaders, true, &timing);
        if (st != 200 || seg.empty()) { LogLine("[cenc] media #" + std::to_string(num) + " HTTP " + std::to_string(st) + " — stop"); break; }
        int n = cenc::ParseSegment(seg, info, samples, decodeTicks);
        LogAbrSegment("video", -1, i, timing,
                      info.timescale ? (int64_t)((decodeTicks * 1000ULL) / info.timescale) : 0);
        LogLine("[cenc] seg#" + std::to_string(num) + " " + std::to_string(seg.size()) + "B -> " + std::to_string(n) + " sample(s)");
        if (haveAudio)
        {
            int ast = 0;
            std::wstring aUrl = aSegBase + aSegPrefix + std::to_wstring(num) + aSegSuffix;
            HttpFetchTiming aTiming;
            auto aSeg = HttpGetBytes(aUrl, ast, segHeaders, true, &aTiming);
            if (ast == 200 && !aSeg.empty())
            {
                int an = cenc::ParseSegment(aSeg, audioInfo, audioSamples, audioDecodeTicks);
                LogAbrSegment("audio", -1, i, aTiming,
                              audioInfo.timescale ? (int64_t)((audioDecodeTicks * 1000ULL) / audioInfo.timescale) : 0);
                LogLine("[cenc] aseg#" + std::to_string(num) + " " + std::to_string(aSeg.size()) + "B -> " + std::to_string(an) + " sample(s)");
            }
            else
            {
                LogLine("[cenc] aseg#" + std::to_string(num) + " HTTP " + std::to_string(ast) + " — dropping audio track");
                haveAudio = false; audioSamples.clear();
            }
        }
        nextSegment = i + 1;
    }
    if (samples.empty()) { LogLine("[cenc] no samples demuxed — abort."); return false; }
    bool streaming = nextSegment < segCount;
    LogLine("[cenc] initial burst: " + std::to_string(samples.size()) + " video + " + std::to_string(audioSamples.size()) +
            " audio sample(s) from " + std::to_string(nextSegment) + "/" + std::to_string(segCount) + " segment(s)" +
            (streaming ? " — the rest streams in behind playback" : " (complete)"));

    // ── 2. D3D11 + MF DXGI manager (shared, multithread-protected). ─────────────────────────────────────────────────
    HRESULT hr;
    ID3D11Device* d3d = nullptr; ID3D11DeviceContext* ctx = nullptr;
    UINT flags = D3D11_CREATE_DEVICE_BGRA_SUPPORT | D3D11_CREATE_DEVICE_VIDEO_SUPPORT;
    if (FAILED(hr = D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, flags, nullptr, 0, D3D11_SDK_VERSION, &d3d, nullptr, &ctx)))
    { LogLine("[cenc] D3D11CreateDevice hr=" + hx(hr)); return false; }
    if (ctx) ctx->Release();
    {   void* mt = nullptr; GUID iidMt = { 0x9b7e4e00, 0x342c, 0x4106, {0xa1,0x9f,0x4f,0x27,0x04,0xf6,0x89,0xf0} };
        if (SUCCEEDED(d3d->QueryInterface(iidMt, &mt)) && mt)
        { auto setProt = (int (STDMETHODCALLTYPE*)(void*, int))(*(void***)mt)[5]; setProt(mt, 1); ((::IUnknown*)mt)->Release(); } }
    UINT resetToken = 0; IMFDXGIDeviceManager* dxgiMgr = nullptr;
    if (FAILED(hr = MFCreateDXGIDeviceManager(&resetToken, &dxgiMgr))) { LogLine("[cenc] MFCreateDXGIDeviceManager hr=" + hx(hr)); d3d->Release(); return false; }
    if (FAILED(hr = dxgiMgr->ResetDevice((::IUnknown*)d3d, resetToken))) { LogLine("[cenc] ResetDevice hr=" + hx(hr)); dxgiMgr->Release(); d3d->Release(); return false; }

    // ── 3. CDM (proven S_FALSE SetPMPHostApp) + proactive license to USABLE + protection manager. ───────────────────
    g_emeCdm = CreateAndPrepareCdm(L"com.microsoft.playready.recommendation", storePath);
    if (!g_emeCdm) { LogLine("[cenc] CDM creation failed."); dxgiMgr->Release(); d3d->Release(); return false; }

    std::vector<uint8_t> initData = info.pssh;
#ifdef FG_DESKTOP_DLL
    if (!g_openPssh.empty()) { initData = g_openPssh; LogLine("[cenc] using descriptor PSSH (" + std::to_string(initData.size()) + "B)."); }
#endif
    if (initData.empty())
    {
        std::wstring laUrl = AxinomLicenseUrl;
#ifdef FG_DESKTOP_DLL
        if (!g_openLicenseUrl.empty()) laUrl = g_openLicenseUrl;
#endif
        initData = BuildPlayReadyPssh(info.kid, laUrl);
        LogLine("[cenc] init had no pssh — built PlayReady pssh from KID (" + std::to_string(initData.size()) + "B).");
    }
    bool usable = DriveCdmLicenseProactive(initData);

    winrt::com_ptr<MediaEngineProtectionManager> pm;
    try { pm = winrt::make_self<MediaEngineProtectionManager>(g_emeCdm); }
    catch (winrt::hresult_error const& e) { LogLine("[cenc] protection manager ctor hr=" + hx(e.code().value)); g_emeCdm->Release(); g_emeCdm=nullptr; dxgiMgr->Release(); d3d->Release(); return false; }

    // ── 4. Build the custom source + wire it to the media engine via the MF_MEDIA_ENGINE_EXTENSION extension. ───────
    CencAudioFeed audioFeed;
    if (haveAudio) { audioFeed.info = audioInfo; audioFeed.samples = std::move(audioSamples); }
    // The WHOLE track's duration, extrapolated from the burst: segments are equal length, so the media time covered by
    // `nextSegment` of them scales to `segCount`. Scheme-agnostic (works for both time-addressed and numbered segments)
    // and accurate to within one segment. Without it the presentation would claim to be only as long as the burst.
    uint64_t burstEnd100ns = info.timescale ? (decodeTicks * 10000000ULL) / info.timescale : 0;
    uint64_t total100ns = (streaming && nextSegment > 0)
        ? burstEnd100ns * (uint64_t)segCount / (uint64_t)nextSegment
        : 0;
    if (total100ns)
        LogLine("[cenc] presentation duration " + std::to_string(total100ns / 10000000ULL) + "s (extrapolated from " +
                std::to_string(nextSegment) + "/" + std::to_string(segCount) + " segments)");
    winrt::com_ptr<CencMediaSource> source =
        BuildCencSource(info, std::move(samples), haveAudio ? &audioFeed : nullptr, streaming, total100ns);
#ifdef FG_DESKTOP_DLL
    g_desktopForwardBufferedMs.store(source->m_streams[0]->AheadDurationMs(), std::memory_order_release);
#endif

    // ── 4b. The background feeder: keeps fetching + demuxing behind the playhead, bounded so memory does not grow with
    //        the whole track. It owns strong refs to the streams, and the joiner below guarantees it is stopped and
    //        joined before those streams (and this frame) go away — on EVERY exit path, including the failure ones.
    std::atomic<bool> feedStop{ false };
    std::thread feeder;
    struct FeedJoiner
    {
        std::atomic<bool>& stop; std::thread& t;
        ~FeedJoiner() { stop.store(true, std::memory_order_release); if (t.joinable()) t.join(); }
    } feedJoiner{ feedStop, feeder };

    if (streaming)
    {
        auto videoStream = source->m_streams[0];
        auto audioStream = source->m_streams.size() > 1 ? source->m_streams[1] : nullptr;
        feeder = std::thread([=, &feedStop]() mutable
        {
            int hrInit = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
            uint64_t vTicks = decodeTicks, aTicks = audioDecodeTicks;
            int i = nextSegment;
            int audioNextSegment = nextSegment;
            auto aborting = [&]() -> bool
            {
                return feedStop.load(std::memory_order_acquire) || StopRequested() || videoStream->IsShutdown();
            };
#ifdef FG_DESKTOP_DLL
            uint64_t seenSwitch = g_videoSwitchAppliedSeq.load(std::memory_order_acquire);
            uint64_t seenSeek = g_desktopSeekAppliedSeq.load(std::memory_order_acquire);
            uint64_t servingSeek = seenSeek;   // the newest seek this iteration has already retargeted for
            uint64_t segmentDurationMs = nextSegment > 0 && info.timescale > 0
                ? ((decodeTicks * 1000ULL) / info.timescale) / (uint64_t)nextSegment
                : (uint64_t)(segStride > 1 ? segStride : 1) * 1000ULL;
            if (segmentDurationMs == 0) segmentDurationMs = 1000;
            // Polled WHILE an HTTP request is in flight. Without it a seek — or a session teardown — had to wait out the
            // whole in-flight segment before the feeder even looked at it, then serially fetch the target video and
            // target audio segments: three round trips of latency on every scrub, and a stop that could not be
            // acknowledged until the current GET finished.
            auto cancelFetch = [&]() -> bool
            {
                return aborting() || g_desktopSeekRequestSeq.load(std::memory_order_acquire) > servingSeek;
            };
            // A seek is "already buffered" only when BOTH tracks can serve it — video from a keyframe at or before the
            // target, audio from any sample — with contiguous coverage past it.
            auto bufferedSeekTarget = [&](int64_t targetMs) -> bool
            {
                return videoStream->CanSeekTo(targetMs, true) &&
                       (!audioStream || audioStream->CanSeekTo(targetMs, false));
            };
#else
            auto cancelFetch = [&]() -> bool { return aborting(); };
#endif
            while (i < segCount)
            {
                bool switching = false;
                bool seeking = false;
#ifdef FG_DESKTOP_DLL
                int feederIndex = i;
                uint64_t requestedSeek = g_desktopSeekRequestSeq.load(std::memory_order_acquire);
                servingSeek = requestedSeek;
                if (requestedSeek > seenSeek)
                {
                    int64_t targetMs = g_desktopSeekMs.load(std::memory_order_acquire);
                    if (bufferedSeekTarget(targetMs))
                    {
                        // The fast path: release the transport gate NOW and keep feeding sequentially. Rewinding the
                        // feeder for a target that is already in memory is what turned a backward scrub into a fresh
                        // download and left the sample vector duplicated and non-monotonic.
                        seenSeek = requestedSeek;
                        g_desktopSeekBufferedSeq.store(requestedSeek, std::memory_order_release);
                        int64_t bufStart = 0, bufEnd = 0; videoStream->BufferedRangeMs(bufStart, bufEnd);
                        LogLine("[cenc-feed] seek ms=" + std::to_string((long long)targetMs) +
                                " served from buffer [" + std::to_string((long long)bufStart) + ".." +
                                std::to_string((long long)bufEnd) + "]ms — no fetch (seq=" +
                                std::to_string(requestedSeek) + ")");
                    }
                    else
                    {
                        int boundary = (int)((uint64_t)(targetMs > 0 ? targetMs : 0) / segmentDurationMs);
                        i = std::clamp(boundary, 0, segCount - 1);
                        audioNextSegment = i;
                        seeking = true;
                        LogLine("[cenc-feed] seek prefetch requested ms=" + std::to_string((long long)targetMs) +
                                " at segment index " + std::to_string(i));
                    }
                }
                uint64_t requestedSwitch = g_videoSwitchRequestSeq.load(std::memory_order_acquire);
                FgVideoSwitchRequest requestedRepresentation;
                cenc::InitInfo requestedInfo;
                if (!seeking && requestedSwitch > seenSwitch)
                {
                    {
                        std::lock_guard<std::mutex> lock(g_videoSwitchMx);
                        requestedRepresentation = g_videoSwitch;
                    }
                    int initStatus = 0;
                    auto nextInitBytes = HttpGetBytes(requestedRepresentation.initUrl, initStatus, segHeaders,
                                                      false, nullptr, cancelFetch);
                    cenc::InitInfo nextInfo;
                    if (initStatus == 200 && cenc::ParseInit(nextInitBytes, nextInfo) &&
                        nextInfo.kind == cenc::TrackKind::Video && nextInfo.timescale == info.timescale &&
                        memcmp(nextInfo.kid, info.kid, sizeof(info.kid)) == 0)
                    {
                        // THE SWITCH BOUNDARY — at or before the segment the PLAYHEAD is in, NEVER forward of it.
                        // This used to clamp UP to `nextSegment`, the initial-burst count captured before playback
                        // started: a second in, the boundary computed 0, clamped up to 2, and the replacement segment
                        // landed ~8s ahead. SwitchVideoRepresentation then erased every buffered sample under the
                        // playhead and refilled from there, so video had a multi-second hole while audio (never
                        // repositioned) and the clock ran on — the frozen picture.
                        // CEIL, not floor: the splice must land at a boundary at or AFTER the next undelivered sample
                        // so everything between the playhead and the splice stays continuous in the old representation
                        // (no hole). And never past the feeder's own cursor, which is where the buffer ends.
                        const int64_t nextTimeMs = videoStream->NextSampleTimeMs();
                        int boundary = videoStream->Ahead() == 0
                            ? i   // the buffer is drained — the feeder cursor IS the playhead
                            : (int)(((uint64_t)std::max<int64_t>(nextTimeMs, 0) + segmentDurationMs - 1) / segmentDurationMs);
                        i = std::clamp(boundary, 0, i);
                        requestedInfo = std::move(nextInfo);
                        switching = true;
                        LogLine("[cenc-feed] quality switch at segment index " + std::to_string(i) +
                                " (playhead t=" + std::to_string((long long)nextTimeMs) + "ms, segment=" +
                                std::to_string(segmentDurationMs) + "ms, cursor was " + std::to_string(feederIndex) +
                                ") -> representation " + std::to_string(requestedRepresentation.index));
                    }
                    else if (initStatus == -1 && cancelFetch())
                    {
                        // Cancelled in flight by a newer seek (or teardown) — the switch stays PENDING, not rejected.
                        LogLine("[cenc-feed] quality switch init fetch cancelled — switch still pending");
                        if (aborting()) break;
                        continue;
                    }
                    else
                    {
                        LogLine("[cenc-feed] quality switch rejected: init/KID/timescale incompatible (HTTP " +
                                std::to_string(initStatus) + ")");
                        seenSwitch = requestedSwitch;
                        g_videoSwitchAppliedSeq.store(requestedSwitch, std::memory_order_release);
                    }
                }
#endif
                // Backpressure: stay a bounded distance ahead of the playhead. While paused the playhead stops, so this
                // naturally stops fetching too instead of racing to download the rest of the track.
                while (!switching && !seeking && !aborting() && videoStream->Ahead() > kMaxSamplesAhead
#ifdef FG_DESKTOP_DLL
                       && g_desktopSeekRequestSeq.load(std::memory_order_acquire) <= seenSeek
                       && g_videoSwitchRequestSeq.load(std::memory_order_acquire) <= seenSwitch
#endif
                       )
                    std::this_thread::sleep_for(std::chrono::milliseconds(50));
                if (aborting()) break;
#ifdef FG_DESKTOP_DLL
                // A seek or a quality switch arrived while backpressure was sleeping (a switch parked here used to wait
                // out the whole 30s buffer). Re-enter the top of the loop so it is handled before the next sequential
                // segment is fetched.
                if (!seeking && !switching &&
                    (g_desktopSeekRequestSeq.load(std::memory_order_acquire) > seenSeek ||
                     g_videoSwitchRequestSeq.load(std::memory_order_acquire) > seenSwitch))
                    continue;
#endif

                int num = startNumber + i * segStride;
                int status = 0;
                HttpFetchTiming timing;
                std::wstring videoUrl =
#ifdef FG_DESKTOP_DLL
                    switching
                        ? requestedRepresentation.segmentBaseUrl + requestedRepresentation.segmentPrefix +
                            std::to_wstring(num) + requestedRepresentation.segmentSuffix
                        :
#endif
                    segBase + segPrefix + std::to_wstring(num) + segSuffix;
                auto seg = HttpGetBytes(videoUrl, status, segHeaders, true, &timing, cancelFetch);
                if (aborting()) break;
#ifdef FG_DESKTOP_DLL
                // Cancelled mid-flight because a NEWER seek landed: drop this segment and let the loop top retarget.
                if (g_desktopSeekRequestSeq.load(std::memory_order_acquire) > servingSeek) continue;
                if (switching && (status != 200 || seg.empty()))
                {
                    LogLine("[cenc-feed] quality switch rejected: target segment HTTP " + std::to_string(status));
                    i = feederIndex;
                    num = startNumber + i * segStride;
                    seg = HttpGetBytes(segBase + segPrefix + std::to_wstring(num) + segSuffix, status, segHeaders,
                                       true, &timing, cancelFetch);
                    switching = false;
                    seenSwitch = requestedSwitch;
                    g_videoSwitchAppliedSeq.store(requestedSwitch, std::memory_order_release);
                }
#endif
                if (status != 200 || seg.empty())
                { LogLine("[cenc-feed] video seg#" + std::to_string(num) + " HTTP " + std::to_string(status) + " — stopping feed"); break; }
                std::vector<cenc::Sample> more;
                cenc::ParseSegment(seg,
#ifdef FG_DESKTOP_DLL
                    switching ? requestedInfo :
#endif
                    info, more, vTicks);
#ifdef FG_DESKTOP_DLL
                if (switching && !more.empty() && more[0].keyframe)
                {
                    initUrl = requestedRepresentation.initUrl;
                    segBase = requestedRepresentation.segmentBaseUrl;
                    segPrefix = requestedRepresentation.segmentPrefix;
                    segSuffix = requestedRepresentation.segmentSuffix;
                    info = std::move(requestedInfo);
                    videoStream->SwitchVideoRepresentation(info, std::move(more));
                    seenSwitch = requestedSwitch;
                    g_desktopActiveVideoRepresentation.store(requestedRepresentation.index, std::memory_order_release);
                    g_videoSwitchAppliedSeq.store(requestedSwitch, std::memory_order_release);
                }
                else
                {
                    if (switching)
                    {
                        LogLine("[cenc-feed] quality switch rejected: target segment did not begin with a keyframe");
                        // Keep the current representation continuous. The target boundary may be behind the feeder's
                        // current cursor, so fetch the original cursor rather than appending target samples or skipping
                        // an old-representation segment.
                        i = feederIndex;
                        num = startNumber + i * segStride;
                        int fallbackStatus = 0;
                        auto fallback = HttpGetBytes(segBase + segPrefix + std::to_wstring(num) + segSuffix,
                                                     fallbackStatus, segHeaders, true, &timing, cancelFetch);
                        more.clear();
                        if (fallbackStatus != 200 || fallback.empty())
                        {
                            LogLine("[cenc-feed] video fallback seg#" + std::to_string(num) + " HTTP " +
                                    std::to_string(fallbackStatus) + " — stopping feed");
                            break;
                        }
                        cenc::ParseSegment(fallback, info, more, vTicks);
                        seenSwitch = requestedSwitch;
                        g_videoSwitchAppliedSeq.store(requestedSwitch, std::memory_order_release);
                    }
                    videoStream->AppendSamples(std::move(more));
                }
                g_desktopForwardBufferedMs.store(videoStream->AheadDurationMs(), std::memory_order_release);
                LogAbrSegment("video", g_desktopActiveVideoRepresentation.load(std::memory_order_acquire), i, timing,
                              videoStream->AheadDurationMs());
#else
                videoStream->AppendSamples(std::move(more));
#endif

                if (audioStream && audioNextSegment >= segCount)
                {
                    audioStream->MarkComplete();
                    audioStream = nullptr;
                }
                else if (audioStream)
                {
                    int audioNum = startNumber + audioNextSegment * segStride;
                    int aStatus = 0;
                    HttpFetchTiming aTiming;
                    auto aSeg = HttpGetBytes(aSegBase + aSegPrefix + std::to_wstring(audioNum) + aSegSuffix, aStatus,
                                             segHeaders, true, &aTiming, cancelFetch);
                    if (aborting()) break;
                    if (aStatus == 200 && !aSeg.empty())
                    {
                        std::vector<cenc::Sample> aMore;
                        cenc::ParseSegment(aSeg, audioInfo, aMore, aTicks);
                        audioStream->AppendSamples(std::move(aMore));
#ifdef FG_DESKTOP_DLL
                        LogAbrSegment("audio", -1, audioNextSegment, aTiming, audioStream->AheadDurationMs());
#endif
                        audioNextSegment++;
                    }
#ifdef FG_DESKTOP_DLL
                    else if (aStatus == -1 && g_desktopSeekRequestSeq.load(std::memory_order_acquire) > servingSeek)
                    {
                        // Cancelled by a newer seek, not a real failure — retarget instead of ending the audio track.
                        continue;
                    }
#endif
                    else
                    {
                        // Audio ran out but video has not: end the audio stream cleanly so the presentation still ends
                        // on the video, rather than leaving a stream that can never satisfy another request.
                        LogLine("[cenc-feed] audio seg#" + std::to_string(audioNum) + " HTTP " + std::to_string(aStatus) + " — ending audio feed");
                        audioStream->MarkComplete();
                        audioStream = nullptr;
                    }
                }
#ifdef FG_DESKTOP_DLL
                if (seeking)
                {
                    seenSeek = requestedSeek;
                    g_desktopSeekBufferedSeq.store(requestedSeek, std::memory_order_release);
                    LogLine("[cenc-feed] seek target buffered seq=" + std::to_string(requestedSeek) +
                            " segment=" + std::to_string(i));
                }
#endif
                if ((i % 10) == 0)
                    LogLine("[cenc-feed] seg#" + std::to_string(num) + " fed (" + std::to_string(i + 1) + "/" +
                            std::to_string(segCount) + ", ahead=" + std::to_string(videoStream->Ahead()) + ")");
                i++;
            }
            // Whatever the reason we stopped, both streams must be told: a stream left incomplete would park requests
            // forever instead of reporting end-of-stream.
            videoStream->MarkComplete();
            if (audioStream) audioStream->MarkComplete();
            LogLine("[cenc-feed] feeder exit at segment " + std::to_string(i) + "/" + std::to_string(segCount));
            if (SUCCEEDED(hrInit)) CoUninitialize();
        });
    }
#if defined(FG_DESKTOP_DLL) || defined(FG_WIN32_PMP)
    // Desktop-only PMP bridge: the protected source exposes the CDM's trusted input so Media Foundation can obtain
    // the per-stream ITA/decrypter inside Windows' protected process.
    IMFTrustedInput* trustedInput = nullptr;
    // The proactive EME session above already supplied the PSSH. Match Firefox's working desktop MFCDM path and let
    // the CDM associate that session with the trusted input; passing the same PSSH again creates a second content
    // binding whose ITA proxy is rejected during protected-topology negotiation on some PlayReady implementations.
    hr = g_emeCdm->CreateTrustedInput(nullptr, 0, &trustedInput);
    LogLine("[cenc] CreateTrustedInput hr=" + hx(hr));
    if (FAILED(hr) || !trustedInput)
    {
        g_emeCdm->Release(); g_emeCdm = nullptr; dxgiMgr->Release(); d3d->Release(); return false;
    }
    source->m_trustedInput.attach(trustedInput);

    // Diagnose the real CDM-owned ITA before handing it to MediaEngine/PMP. This uses a separate trusted-input
    // instance so RequestAccess/GetPolicy cannot disturb the instance cached by CencMediaSource.
    IMFTrustedInput* probeTrustedInput = nullptr;
    HRESULT probeHr = g_emeCdm->CreateTrustedInput(nullptr, 0, &probeTrustedInput);
    LogLine("[cenc-preflight] CreateTrustedInput hr=" + hx(probeHr));
    if (SUCCEEDED(probeHr) && probeTrustedInput)
    {
        ::IUnknown* unknown = nullptr;
        probeHr = probeTrustedInput->GetInputTrustAuthority(1, __uuidof(IMFInputTrustAuthority), &unknown);
        LogLine("[cenc-preflight] GetInputTrustAuthority hr=" + hx(probeHr));
        if (SUCCEEDED(probeHr) && unknown)
        {
            IMFInputTrustAuthority* ita = nullptr;
            probeHr = unknown->QueryInterface(IID_PPV_ARGS(&ita));
            LogLine("[cenc-preflight] QI IMFInputTrustAuthority hr=" + hx(probeHr));
            if (SUCCEEDED(probeHr) && ita)
            {
                IMFActivate* enabler = nullptr;
                probeHr = ita->RequestAccess(PEACTION_PLAY, &enabler);
                LogLine("[cenc-preflight] RequestAccess(PLAY) hr=" + hx(probeHr) +
                        " enabler=" + std::to_string(enabler != nullptr));
                if (enabler) enabler->Release();

                IMFOutputPolicy* policy = nullptr;
                probeHr = ita->GetPolicy(PEACTION_PLAY, &policy);
                LogLine("[cenc-preflight] GetPolicy(PLAY) hr=" + hx(probeHr) +
                        " policy=" + std::to_string(policy != nullptr));
                if (policy) policy->Release();

                IMFTransform* decrypter = nullptr;
                probeHr = ita->GetDecrypter(IID_PPV_ARGS(&decrypter));
                LogLine("[cenc-preflight] GetDecrypter hr=" + hx(probeHr) +
                        " decrypter=" + std::to_string(decrypter != nullptr));
                if (decrypter) decrypter->Release();
                ita->Reset();
                ita->Release();
            }
            unknown->Release();
        }
        probeTrustedInput->Release();
    }
#endif

    IMFMediaEngineClassFactory* factory = nullptr;
    if (FAILED(hr = CoCreateInstance(CLSID_MFMediaEngineClassFactory, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&factory))))
    { LogLine("[cenc] CoCreate factory hr=" + hx(hr)); g_emeCdm->Release(); g_emeCdm=nullptr; dxgiMgr->Release(); d3d->Release(); return false; }

    MseEngineExtension* ext = new MseEngineExtension();
    source->QueryInterface(__uuidof(::IUnknown), (void**)&ext->m_mse);   // the source's IUnknown — engine resolves to it

    MediaEngineNotify* notify = new MediaEngineNotify();
    EmeNeedKeyNotify* needKey = new EmeNeedKeyNotify();
    auto cpmForAttr = pm.as<IMFContentProtectionManager>();
    IMFAttributes* attrs = nullptr; MFCreateAttributes(&attrs, 10);
    attrs->SetUnknown(MF_MEDIA_ENGINE_CALLBACK, (::IUnknown*)notify);
    attrs->SetUnknown(MF_MEDIA_ENGINE_DXGI_MANAGER, (::IUnknown*)dxgiMgr);
    attrs->SetUnknown(MF_MEDIA_ENGINE_EXTENSION, (::IUnknown*)ext);
    attrs->SetUnknown(MF_MEDIA_ENGINE_NEEDKEY_CALLBACK, (::IUnknown*)needKey);
    // Firefox-exact protected wiring (gecko MFMediaEngineParent::CreateMediaEngine + RecvSetCDMProxy): the creation
    // attributes carry ONLY MF_MEDIA_ENGINE_ENABLE_PROTECTED_CONTENT; the protection manager is attached AFTER engine
    // creation via IMFMediaEngineProtectedContent::SetContentProtectionManager (below). The old wiring additionally
    // passed the manager as a creation attribute + USE_PMP_FOR_ALL_CONTENT — set FG_CENC_LEGACY_ENGINE_WIRING=1 to A/B.
    bool legacyWiring = GetEnvironmentVariableW(L"FG_CENC_LEGACY_ENGINE_WIRING", nullptr, 0) != 0;
    if (legacyWiring)
    {
        attrs->SetUnknown(MF_MEDIA_ENGINE_CONTENT_PROTECTION_MANAGER, (::IUnknown*)cpmForAttr.get());
        attrs->SetUINT32(MF_MEDIA_ENGINE_CONTENT_PROTECTION_FLAGS,
                         MF_MEDIA_ENGINE_ENABLE_PROTECTED_CONTENT | MF_MEDIA_ENGINE_USE_PMP_FOR_ALL_CONTENT);
    }
    else
    {
        attrs->SetUINT32(MF_MEDIA_ENGINE_CONTENT_PROTECTION_FLAGS, MF_MEDIA_ENGINE_ENABLE_PROTECTED_CONTENT);
    }
    attrs->SetUINT32(MF_MEDIA_ENGINE_VIDEO_OUTPUT_FORMAT, DXGI_FORMAT_B8G8R8A8_UNORM);
    IMFMediaEngine* engine = nullptr;
    hr = factory->CreateInstance(0, attrs, &engine);
    attrs->Release();
    LogLine("[cenc] CreateInstance(engine) hr=" + hx(hr));
    if (FAILED(hr) || !engine)
    { needKey->Release(); notify->Release(); ext->Release(); factory->Release(); if (g_emeCdm){g_emeCdm->Release();g_emeCdm=nullptr;} dxgiMgr->Release(); d3d->Release(); return false; }

    IMFMediaEngineProtectedContent* prot = nullptr;
    if (SUCCEEDED(engine->QueryInterface(IID_PPV_ARGS(&prot))) && prot)
    { auto cpm = pm.as<IMFContentProtectionManager>(); HRESULT hs = prot->SetContentProtectionManager(cpm.get()); LogLine("[cenc] SetContentProtectionManager hr=" + hx(hs)); }
    else LogLine("[cenc] QI IMFMediaEngineProtectedContent FAILED.");

    IMFMediaEngineEx* engineEx = nullptr;
    engine->QueryInterface(IID_PPV_ARGS(&engineEx));
    if (engineEx) engineEx->EnableWindowlessSwapchainMode(TRUE);

    BSTR burl = SysAllocString(L"cenc://fluentgpu/protected.mp4");
    hr = engineEx ? engineEx->SetSource(burl) : engine->SetSource(burl);
    SysFreeString(burl);
    LogLine("[cenc] SetSource hr=" + hx(hr));
    engine->Play();
    LogLine("[cenc] Play(); waiting for CANPLAY (decrypt/decode on MF threads) ...");

    bool ok = false; HANDLE swHandle = nullptr; DWORD nvw = 0, nvh = 0;
    auto metaDl = std::chrono::steady_clock::now() + std::chrono::seconds(45);
    while (!notify->canplay && !notify->metadata && !notify->error && std::chrono::steady_clock::now() < metaDl)
        std::this_thread::sleep_for(std::chrono::milliseconds(100));
    LogLine(std::string("[cenc] post-play metadata=") + (notify->metadata?"1":"0") + " canplay=" + (notify->canplay?"1":"0") +
            " playing=" + (notify->playing?"1":"0") + " error=" + (notify->error?"1":"0") + " lastEvent=" + std::to_string(notify->lastEvent));
    if (notify->error)
    {
        LogLine("[cenc] media ERROR code=" + std::to_string(notify->errCode) + " hr=" + hx(notify->errHr));
#ifdef FG_DESKTOP_DLL
        if (FAILED(notify->errHr))
        {
            g_desktopErrorHr.store(notify->errHr, std::memory_order_release);
            g_desktopState.store(5, std::memory_order_release);
        }
#endif
    }

    if ((notify->metadata || notify->canplay) && engineEx)
    {
        engineEx->GetNativeVideoSize(&nvw, &nvh); if (!nvw) { nvw = info.width ? info.width : 1280; nvh = info.height ? info.height : 720; }
        RECT dst = { 0, 0, (LONG)nvw, (LONG)nvh }; MFARGB border = { 0, 0, 0, 255 };
        // MF only creates the windowless swap chain once UpdateVideoStream has given it a NON-ZERO destination
        // rectangle, so the rect is logged before the first handle query: a {0,0,0,0} or 1x1 dst here is the bug, not
        // the handle. An invalid handle on a SUCCEEDED (typically S_FALSE) hr means "not ready yet", never "done".
        LogLine("[cenc] swapchain handle query: dst={" + std::to_string(dst.left) + "," + std::to_string(dst.top) +
                "," + std::to_string(dst.right) + "," + std::to_string(dst.bottom) + "} native=" +
                std::to_string(nvw) + "x" + std::to_string(nvh));
        auto handleUsable = [](HRESULT h, HANDLE v) { return SUCCEEDED(h) && v != nullptr && v != INVALID_HANDLE_VALUE; };
        auto hdlDl = std::chrono::steady_clock::now() + std::chrono::seconds(12);
        bool firstQuery = true;
        while (std::chrono::steady_clock::now() < hdlDl && !notify->error)
        {
            LONGLONG pts; engine->OnVideoStreamTick(&pts);
            engineEx->UpdateVideoStream(nullptr, &dst, &border);
            hr = engineEx->GetVideoSwapchainHandle(&swHandle);
            if (firstQuery)
            {
                firstQuery = false;
                LogLine("[cenc] first GetVideoSwapchainHandle hr=" + hx(hr) + " handle=" +
                        std::to_string((uint64_t)(uintptr_t)swHandle) +
                        (handleUsable(hr, swHandle) ? " (usable)" : " (not ready)"));
            }
            if (handleUsable(hr, swHandle)) break;
            swHandle = nullptr;   // S_FALSE hands back a null/invalid handle; never publish one
            std::this_thread::sleep_for(std::chrono::milliseconds(60));
        }
        LogLine("[cenc] " + std::to_string(nvw) + "x" + std::to_string(nvh) + " GetVideoSwapchainHandle hr=" + hx(hr) + " handle=" + std::to_string((uint64_t)(uintptr_t)swHandle));
        ok = handleUsable(hr, swHandle);
    }
    else
    {
        LogLine(std::string("[cenc] no canplay (") + (notify->error ? ("error code=" + std::to_string(notify->errCode) + " hr=" + hx(notify->errHr)) : "timeout") + ")");
#ifdef FG_DESKTOP_DLL
        if (notify->error && FAILED(notify->errHr))
        {
            g_desktopErrorHr.store(notify->errHr, std::memory_order_release);
            g_desktopState.store(5, std::memory_order_release);
        }
#endif
    }

    QueryCdmKeyStatus();
    LogLine(std::string("[cenc] STATE needKey=") + (g_emeNeedKey?"1":"0") + " keyMsg=" + (g_cdmKeyMsg?"1":"0") +
            " licHttp=" + std::to_string(g_cdmLicHttp) + " keyStatus=" + g_cdmKeyStatus + " USABLE=" + (g_cdmUsable?"YES":"no") +
            " proactiveUsable=" + (usable?"1":"0") + " metadata=" + (notify->metadata?"1":"0") + " handle=" + (ok?"NONZERO":"0"));

    if (ok)
    {
        RECT dst = { 0, 0, (LONG)nvw, (LONG)nvh }; MFARGB border = { 0, 0, 0, 255 };
        engineEx->UpdateVideoStream(nullptr, &dst, &border);
        WriteCoord("protected", GetCurrentProcessId(), (uint64_t)(uintptr_t)swHandle, (int)nvw, (int)nvh, "playing");
        WriteEvent("SURFACE", "handle=" + std::to_string((uint64_t)(uintptr_t)swHandle) + " pid=" +
                   std::to_string(GetCurrentProcessId()) + " w=" + std::to_string((int)nvw) + " h=" + std::to_string((int)nvh));
        WriteEvent("STATE", "value=playing");
        g_emePublished = true;
        LogLine("[cenc] READY — published PROTECTED swapchain handle (custom CENC source); keeping alive until SHUTDOWN.");
        int posTick = 0;
        while (!StopRequested())   // run until the engine sends SHUTDOWN (or the stop file appears) — no fixed ceiling
        {
            std::this_thread::sleep_for(std::chrono::milliseconds(80));
            LONGLONG pts; engine->OnVideoStreamTick(&pts);
            engineEx->UpdateVideoStream(nullptr, nullptr, nullptr);
#ifdef FG_DESKTOP_DLL
            // The seek gate, re-evaluated against the CURRENT buffer every tick (never a range sampled when the seek
            // was issued). If the target already lies in both tracks' buffers the gate opens here with no download at
            // all, so SetCurrentTime fires on this same 80ms tick instead of waiting for the feeder to finish a fetch
            // it does not need. The feeder opens the same gate for a target it had to fetch.
            {
                uint64_t pendingSeek = g_desktopSeekRequestSeq.load(std::memory_order_acquire);
                if (pendingSeek != g_desktopSeekBufferedSeq.load(std::memory_order_acquire) &&
                    pendingSeek != g_desktopSeekAppliedSeq.load(std::memory_order_acquire))
                {
                    int64_t targetMs = g_desktopSeekMs.load(std::memory_order_acquire);
                    bool videoReady = source->m_streams[0]->CanSeekTo(targetMs, true);
                    bool audioReady = source->m_streams.size() < 2 ||
                                      source->m_streams[1]->CanSeekTo(targetMs, false);
                    if (videoReady && audioReady)
                    {
                        g_desktopSeekBufferedSeq.store(pendingSeek, std::memory_order_release);
                        LogLine("[transport] seek ms=" + std::to_string((long long)targetMs) +
                                " already buffered — gate opened without a fetch (seq=" +
                                std::to_string(pendingSeek) + ")");
                    }
                }
            }
            // Transport crosses no process boundary: managed publishes the desired level + one-shot seek/volume slots
            // and this owning MTA thread reconciles the media engine (no single-slot clobbering, no managed re-assert).
            ReconcileTransport(engine, engineEx, true);
            g_desktopDurationMs.store((int64_t)(engine->GetDuration() * 1000.0), std::memory_order_release);
            g_desktopForwardBufferedMs.store(source->m_streams[0]->AheadDurationMs(), std::memory_order_release);
#else
            PollCommands(engine);
            if (g_sawAnyCmd && (GetTickCount64() - g_lastCmdActivityMs) > 15000)
            { LogLine("[cenc] engine heartbeat lost (>15s) -> self-exiting."); g_shutdownRequested = true; }
#endif
            if (++posTick >= 12) { posTick = 0; WriteEvent("POSITION", "ms=" +
                std::to_string((long long)(engine->GetCurrentTime() * 1000.0)) + " dur=" +
                std::to_string((long long)(engine->GetDuration() * 1000.0))); }
        }
        WriteEvent("STATE", "value=stopped");
        WriteEvent("BYE");
        LogLine("[cenc] exiting keep-alive (shutdown).");
    }

    // Stop and join the feeder BEFORE tearing the source down. Its streams hold a weak back-pointer to this source, so
    // an append (or an end-of-stream notification) racing with the releases below would touch freed memory. The RAII
    // joiner above remains the backstop for the early-return paths; here it becomes a no-op.
    feedStop.store(true, std::memory_order_release);
    if (feeder.joinable()) feeder.join();

    engine->Shutdown();
    if (g_cdmSession) { g_cdmSession->Release(); g_cdmSession = nullptr; }
    if (engineEx) engineEx->Release();
    if (prot) prot->Release();
    engine->Release();
    needKey->Release(); notify->Release();
    ext->Release();
    factory->Release();
    source = nullptr;
    pm = nullptr;
    if (g_emeCdm) { g_emeCdm->Release(); g_emeCdm = nullptr; }
    dxgiMgr->Release(); d3d->Release();
    g_emeAttemptDone = true;
    return ok;
}

#ifdef FG_DESKTOP_DLL
// Public ABI used by FluentGpu.WindowsApi. FgPlayReadyRun is deliberately blocking and runs on one managed background
// thread. The control/snapshot exports are lock-free and safe to call from the UI thread.
// The generalized open: a source descriptor + a managed license callback (the WithDrm relay). Blocking; runs on one
// managed background thread. FgPlayReadyRun is a thin shim over this (baked source, no managed relay).
extern "C" __declspec(dllexport) int __stdcall FgPlayReadyRunEx(const wchar_t* baseDir,
                                                                const FgPlayReadyOpenDesc* desc,
                                                                FgLicenseCallback licenseCallback, void* licenseCtx)
{
    bool expected = false;
    if (!g_desktopRunning.compare_exchange_strong(expected, true)) return HRESULT_FROM_WIN32(ERROR_BUSY);

    g_shutdownRequested = false;
    // Preserve the level pre-seeded by DesktopProtectedVideoPlayer.Start. OpenAsync starts paused and PlayAsync may run
    // before this worker enters RunEx; resetting here would lose that legitimate Play request.
    // Ignore one-shot controls left by a prior session. Play is intentionally not acknowledged here: Start pre-seeds
    // the new session's level before this blocking entry point begins, and the MTA loop must reach that level first.
    g_desktopSeekAppliedSeq.store(g_desktopSeekRequestSeq.load(std::memory_order_acquire), std::memory_order_release);
    g_desktopSeekBufferedSeq.store(g_desktopSeekRequestSeq.load(std::memory_order_acquire), std::memory_order_release);
    g_desktopSeekMode.store(0, std::memory_order_release);
    g_desktopVolumeAppliedSeq.store(g_desktopVolumeRequestSeq.load(std::memory_order_acquire), std::memory_order_release);
    g_desktopRateAppliedSeq.store(g_desktopRateRequestSeq.load(std::memory_order_acquire), std::memory_order_release);
    g_desktopPlayAppliedSeq.store(0, std::memory_order_release);
    g_desktopErrorHr.store(0);
    g_desktopHandle.store(0);
    g_desktopWidth.store(0); g_desktopHeight.store(0);
    g_desktopPositionMs.store(0); g_desktopDurationMs.store(0);
    g_desktopBytesDownloaded.store(0); g_desktopDownloadElapsedMs.store(0); g_desktopForwardBufferedMs.store(0);
    g_desktopActiveVideoRepresentation.store(-1);
    g_desktopState.store(1, std::memory_order_release);

    // Deep-copy the descriptor into the active-open globals (the caller's pointers may not outlive this call).
    int mode = 0;
    g_openInitUrl.clear(); g_openSegBase.clear(); g_openSegPrefix.clear(); g_openSegSuffix.clear();
    g_openHeaders.clear(); g_openLicenseUrl.clear(); g_openPssh.clear(); g_kidHex.clear();
    g_openAudioInitUrl.clear(); g_openAudioSegBase.clear(); g_openAudioSegPrefix.clear(); g_openAudioSegSuffix.clear();
    g_openStartNumber = 1; g_openSegCount = 6; g_openSegStride = 1;
    if (desc)
    {
        mode = desc->mode;
        if (desc->initUrl)          g_openInitUrl    = desc->initUrl;
        if (desc->segmentBaseUrl)   g_openSegBase     = desc->segmentBaseUrl;
        if (desc->segmentPrefix)    g_openSegPrefix   = desc->segmentPrefix;
        if (desc->segmentSuffix)    g_openSegSuffix   = desc->segmentSuffix;
        if (desc->startNumber >= 0) g_openStartNumber = desc->startNumber;   // >= 0: Spotify timestamped segments start at 0
        if (desc->segmentCount > 0) g_openSegCount    = desc->segmentCount;
        // ABI-appended fields: each read only when the caller's struct is large enough to carry it.
        if (FG_DESC_HAS(desc, segmentStride) && desc->segmentStride > 0) g_openSegStride = desc->segmentStride;
        if (desc->httpHeaders)      g_openHeaders     = desc->httpHeaders;
        if (desc->licenseServerUrl) g_openLicenseUrl  = desc->licenseServerUrl;
        if (desc->pssh && desc->psshLen > 0) g_openPssh.assign(desc->pssh, desc->pssh + desc->psshLen);
        // The paired audio representation. Copied here (M2) so the addressing is available to the CENC source; the
        // two-stream demux that actually PLAYS it is the next milestone (M3) — until then these are inert and the
        // session behaves exactly as before, which is deliberate: this half cannot regress playback.
        if (FG_DESC_HAS(desc, audioSegmentSuffix))
        {
            if (desc->audioInitUrl)        g_openAudioInitUrl   = desc->audioInitUrl;
            if (desc->audioSegmentBaseUrl) g_openAudioSegBase   = desc->audioSegmentBaseUrl;
            if (desc->audioSegmentPrefix)  g_openAudioSegPrefix = desc->audioSegmentPrefix;
            if (desc->audioSegmentSuffix)  g_openAudioSegSuffix = desc->audioSegmentSuffix;
        }
    }
    g_licenseCallback = licenseCallback;
    g_licenseCtx = licenseCtx;

    std::wstring root = (baseDir && *baseDir) ? baseDir : DefaultDir();
    CreateDirectoryW(root.c_str(), nullptr);
    g_coordPath = root + L"\\desktop-coord.txt";
    g_logPath = root + L"\\desktop-playready.log";
    g_stopPath.clear(); g_evtPath.clear(); g_cmdPath.clear();
    { std::ofstream f(g_logPath, std::ios::binary | std::ios::trunc); }
    LogLine("[desktop] BUILD=desktop-cdm-20260822-abrseek-v17 root=" +
            std::string(root.begin(), root.end()));
    // Proof that the audio representation crossed the ABI (the M2 acceptance line, and the first thing to check when the
    // M3 two-stream demux misbehaves). "none" is a legitimate outcome: a manifest without AAC audio plays video only.
    LogLine(std::string("[desktop] audio representation: ") +
            (g_openAudioInitUrl.empty()
                 ? "none (video only)"
                 : "present prefix=" + std::string(g_openAudioSegPrefix.begin(), g_openAudioSegPrefix.end())));

    // Clear-video diagnostic through the exact same in-process DLL and DirectComposition handoff. This is used to
    // distinguish a compositor-binding defect from protected-output behavior; production PlayReady uses mode 0.
    if (mode == 1)
    {
        int rc = RunClear(L"https://test-videos.co.uk/vids/bigbuckbunny/mp4/h264/720/Big_Buck_Bunny_720_10s_1MB.mp4");
        if (rc != 0) g_desktopState.store(5); else g_desktopState.store(4);
        g_desktopRunning.store(false, std::memory_order_release);
        return rc == 0 ? S_OK : E_FAIL;
    }

    HRESULT hr = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    bool uninit = SUCCEEDED(hr);
    if (FAILED(hr) && hr != RPC_E_CHANGED_MODE)
    {
        g_desktopErrorHr.store(hr); g_desktopState.store(5); g_desktopRunning.store(false);
        return hr;
    }
    hr = MFStartup(MF_VERSION, MFSTARTUP_FULL);
    if (FAILED(hr))
    {
        g_desktopErrorHr.store(hr); g_desktopState.store(5); g_desktopRunning.store(false);
        if (uninit) CoUninitialize();
        return hr;
    }

    std::wstring storePath = root + L"\\PRStore";
    CreateDirectoryW(storePath.c_str(), nullptr);
    bool ok = RunCustomSourceAttempt(storePath);

    MFShutdown();
    if (uninit) CoUninitialize();
    // Drop the managed callback — it must not be invoked after this call returns (the managed GCHandle is freed then).
    g_licenseCallback = nullptr; g_licenseCtx = nullptr;
    if (!ok && g_desktopState.load() != 5) g_desktopState.store(5);
    else if (g_desktopState.load() != 5) g_desktopState.store(4);
    g_desktopRunning.store(false, std::memory_order_release);
    if (ok) return S_OK;
    HRESULT failure = (HRESULT)g_desktopErrorHr.load(std::memory_order_acquire);
    return FAILED(failure) ? failure : E_FAIL;
}

// Legacy shim: the baked Axinom vector, no managed relay (licensing then requires FG_CENC_BAKED_AXINOM=1). Preserved so
// the original entry point + on-box A/B still work.
extern "C" __declspec(dllexport) int __stdcall FgPlayReadyRun(const wchar_t* baseDir, int mode)
{
    // The legacy entry point has no initial-transport field. Preserve its historical autoplay behavior even if a
    // previous generalized session left the process-global level paused.
    g_desktopDesiredPlay.store(1, std::memory_order_release);
    FgPlayReadyOpenDesc desc{};
    desc.structSize = sizeof(FgPlayReadyOpenDesc);
    desc.mode = mode;
    return FgPlayReadyRunEx(baseDir, &desc, nullptr, nullptr);
}

extern "C" __declspec(dllexport) int __stdcall FgPlayReadyGetSnapshot(FgPlayReadySnapshot* value)
{
    if (!value) return E_POINTER;
    value->state = g_desktopState.load(std::memory_order_acquire);
    value->errorHr = g_desktopErrorHr.load(std::memory_order_acquire);
    value->handle = g_desktopHandle.load(std::memory_order_acquire);
    value->width = g_desktopWidth.load(std::memory_order_acquire);
    value->height = g_desktopHeight.load(std::memory_order_acquire);
    value->positionMs = g_desktopPositionMs.load(std::memory_order_acquire);
    value->durationMs = g_desktopDurationMs.load(std::memory_order_acquire);
    value->playAppliedSeq = g_desktopPlayAppliedSeq.load(std::memory_order_acquire);
    value->seekAppliedSeq = g_desktopSeekAppliedSeq.load(std::memory_order_acquire);
    value->volumeAppliedSeq = g_desktopVolumeAppliedSeq.load(std::memory_order_acquire);
    value->rateAppliedSeq = g_desktopRateAppliedSeq.load(std::memory_order_acquire);
    return S_OK;
}

static uint64_t NextTransportSeq(std::atomic<uint64_t>& seq);

extern "C" __declspec(dllexport) int __stdcall FgPlayReadyGetSnapshotV2(FgPlayReadySnapshotV2* value, uint32_t size)
{
    if (!value || size < sizeof(uint32_t)) return E_POINTER;
    value->structSize = size;
    if (FG_SNAPSHOT_HAS(value, size, legacy)) FgPlayReadyGetSnapshot(&value->legacy);
    if (FG_SNAPSHOT_HAS(value, size, bytesDownloaded))
        value->bytesDownloaded = g_desktopBytesDownloaded.load(std::memory_order_acquire);
    if (FG_SNAPSHOT_HAS(value, size, forwardBufferedMs))
        value->forwardBufferedMs = g_desktopForwardBufferedMs.load(std::memory_order_acquire);
    if (FG_SNAPSHOT_HAS(value, size, activeVideoRepresentation))
        value->activeVideoRepresentation = g_desktopActiveVideoRepresentation.load(std::memory_order_acquire);
    if (FG_SNAPSHOT_HAS(value, size, representationAppliedSeq))
        value->representationAppliedSeq = g_videoSwitchAppliedSeq.load(std::memory_order_acquire);
    if (FG_SNAPSHOT_HAS(value, size, downloadElapsedMs))
        value->downloadElapsedMs = g_desktopDownloadElapsedMs.load(std::memory_order_acquire);
    return S_OK;
}

extern "C" __declspec(dllexport) void __stdcall FgPlayReadyResetAdaptive()
{
    std::lock_guard<std::mutex> lock(g_videoSwitchMx);
    g_videoSwitch = {};
    g_videoSwitchRequestSeq.store(0, std::memory_order_release);
    g_videoSwitchAppliedSeq.store(0, std::memory_order_release);
    g_desktopActiveVideoRepresentation.store(-1, std::memory_order_release);
}

extern "C" __declspec(dllexport) uint64_t __stdcall FgPlayReadySelectVideoRepresentation(
    int32_t index, const wchar_t* initUrl, const wchar_t* segmentBaseUrl,
    const wchar_t* segmentPrefix, const wchar_t* segmentSuffix)
{
    if (index < 0 || !initUrl || !*initUrl || !segmentBaseUrl || !segmentPrefix || !segmentSuffix) return 0;
    {
        std::lock_guard<std::mutex> lock(g_videoSwitchMx);
        g_videoSwitch.index = index;
        g_videoSwitch.initUrl = initUrl;
        g_videoSwitch.segmentBaseUrl = segmentBaseUrl;
        g_videoSwitch.segmentPrefix = segmentPrefix;
        g_videoSwitch.segmentSuffix = segmentSuffix;
    }
    return NextTransportSeq(g_videoSwitchRequestSeq);
}

static uint64_t NextTransportSeq(std::atomic<uint64_t>& seq)
{
    return seq.fetch_add(1, std::memory_order_acq_rel) + 1;
}

// Independent sequence-numbered transport slots. The returned sequence appears in FgPlayReadySnapshot only after the
// owning MTA thread has applied (or, for play/pause, actually reached) the requested state.
extern "C" __declspec(dllexport) uint64_t __stdcall FgPlayReadyPlay()
{
    g_desktopDesiredPlay.store(1, std::memory_order_release);
    return NextTransportSeq(g_desktopPlayRequestSeq);
}
extern "C" __declspec(dllexport) uint64_t __stdcall FgPlayReadyPause()
{
    g_desktopDesiredPlay.store(0, std::memory_order_release);
    return NextTransportSeq(g_desktopPlayRequestSeq);
}
extern "C" __declspec(dllexport) void __stdcall FgPlayReadyStop()
{
    g_desktopDesiredPlay.store(0, std::memory_order_release);
    g_shutdownRequested = true;
}
// mode: 0 = exact (frame-accurate; the commit of a scrub, or a direct jump), 1 = approximate (nearest keyframe; what a
// scrub/preview wants — no preroll decode). ONE latest-wins slot either way: seeks are never queued, because MF applies
// queued seeks FIFO and a dragging thumb would leave the pipeline chasing positions the user has already left.
extern "C" __declspec(dllexport) uint64_t __stdcall FgPlayReadySeekEx(int64_t positionMs, int32_t mode)
{
    g_desktopSeekMs.store(positionMs < 0 ? 0 : positionMs, std::memory_order_release);
    g_desktopSeekMode.store(mode != 0 ? 1 : 0, std::memory_order_release);
    return NextTransportSeq(g_desktopSeekRequestSeq);
}
extern "C" __declspec(dllexport) uint64_t __stdcall FgPlayReadySeek(int64_t positionMs)
{
    return FgPlayReadySeekEx(positionMs, 0);
}
extern "C" __declspec(dllexport) uint64_t __stdcall FgPlayReadySetVolume(double volume)
{
    if (volume < 0.0) volume = 0.0; else if (volume > 1.0) volume = 1.0;
    g_desktopVolMilli.store((int64_t)(volume * 1000000.0), std::memory_order_release);
    return NextTransportSeq(g_desktopVolumeRequestSeq);
}
extern "C" __declspec(dllexport) uint64_t __stdcall FgPlayReadySetRate(double rate)
{
    if (!std::isfinite(rate) || rate <= 0.0) rate = 1.0;
    g_desktopRateMilli.store((int64_t)(rate * 1000000.0), std::memory_order_release);
    return NextTransportSeq(g_desktopRateRequestSeq);
}
#endif

// Drive the custom-source attempt on a detached thread with a bounded publish-wait (protected Play() can block), then
// ExitProcess so the process always publishes a coord and terminates cleanly for the deploy loop.
static int RunCustomSource(const std::wstring& baseDir)
{
    int hr;
    if (FAILED(hr = CoInitializeEx(nullptr, COINIT_MULTITHREADED)) && hr != S_FALSE) LogLine("[cenc] CoInit hr=0x" + std::to_string((uint32_t)hr));
    if (FAILED(hr = MFStartup(MF_VERSION, MFSTARTUP_FULL))) { LogLine("[cenc] MFStartup hr=0x" + std::to_string((uint32_t)hr)); return 2; }
    std::wstring storePath = baseDir + L"\\PRStore"; CreateDirectoryW(storePath.c_str(), nullptr);

    g_emePublished = false; g_emeAttemptDone = false;
    std::thread([storePath]{ RunCustomSourceAttempt(storePath); }).detach();
    auto dl = std::chrono::steady_clock::now() + std::chrono::seconds(150);
    while (std::chrono::steady_clock::now() < dl && !g_emePublished && !g_emeAttemptDone)
        std::this_thread::sleep_for(std::chrono::milliseconds(200));

    if (g_emePublished)
    {
        LogLine("[cenc] handle published — holding process for the engine consumer until SHUTDOWN.");
        while (!StopRequested()) std::this_thread::sleep_for(std::chrono::milliseconds(200));
        MFShutdown(); ExitProcess(0);
    }
    bool wedged = !g_emeAttemptDone;
    LogLine(std::string("[cenc] NO handle (") + (wedged ? "Play/decrypt pipeline blocked" : "attempt failed cleanly") + ") — publishing error state.");
    WriteCoord("protected", GetCurrentProcessId(), 0, 1280, 720, "error");
    WriteEvent("ERROR", std::string("code=0 msg=") + (wedged ? "decode-pipeline-blocked" : "attempt-failed"));
    WriteEvent("BYE");
    auto keep = std::chrono::steady_clock::now() + std::chrono::seconds(15);
    while (std::chrono::steady_clock::now() < keep && !StopRequested()) std::this_thread::sleep_for(std::chrono::milliseconds(200));
    ExitProcess(1);
}

// One protected attempt against a single URL + key system. Returns true only if it reached LOADEDMETADATA with a
// non-zero swapchain handle (then it publishes + keeps alive until stop/ceiling); false if it walled early (try next).
static bool RunProtectedUrl(const std::wstring& url, const wchar_t* keySystem, const std::wstring& storePath)
{
    auto hx = [](HRESULT h){ std::stringstream ss; ss << "0x" << std::hex << (uint32_t)h; return ss.str(); };
    LogLine("[eme] ===== attempt keySystem=" + winrt::to_string(winrt::hstring(keySystem)) + " url=" + winrt::to_string(winrt::hstring(url)));
    g_emeNeedKey = false; g_cdmUsable = false; g_cdmKeyMsg = false; g_cdmLicHttp = 0; g_cdmSession = nullptr;

    HRESULT hr;
    ID3D11Device* d3d = nullptr; ID3D11DeviceContext* ctx = nullptr;
    UINT flags = D3D11_CREATE_DEVICE_BGRA_SUPPORT | D3D11_CREATE_DEVICE_VIDEO_SUPPORT;
    if (FAILED(hr = D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, flags, nullptr, 0, D3D11_SDK_VERSION, &d3d, nullptr, &ctx)))
    { LogLine("[eme] D3D11CreateDevice hr=" + hx(hr)); return false; }
    if (ctx) ctx->Release();
    {   // SetMultithreadProtected — ID3D10Multithread vtable slot 5.
        void* mt = nullptr; GUID iidMt = { 0x9b7e4e00, 0x342c, 0x4106, {0xa1,0x9f,0x4f,0x27,0x04,0xf6,0x89,0xf0} };
        if (SUCCEEDED(d3d->QueryInterface(iidMt, &mt)) && mt)
        { auto setProt = (int (STDMETHODCALLTYPE*)(void*, int))(*(void***)mt)[5]; setProt(mt, 1); ((::IUnknown*)mt)->Release(); }
    }
    UINT resetToken = 0; IMFDXGIDeviceManager* dxgiMgr = nullptr;
    if (FAILED(hr = MFCreateDXGIDeviceManager(&resetToken, &dxgiMgr))) { LogLine("[eme] MFCreateDXGIDeviceManager hr=" + hx(hr)); d3d->Release(); return false; }
    if (FAILED(hr = dxgiMgr->ResetDevice((::IUnknown*)d3d, resetToken))) { LogLine("[eme] ResetDevice hr=" + hx(hr)); dxgiMgr->Release(); d3d->Release(); return false; }

    // Modern CDM (shared with the protection manager + engine).
    g_emeCdm = CreateAndPrepareCdm(keySystem, storePath);
    if (!g_emeCdm) { LogLine("[eme] CDM creation failed for this key system."); dxgiMgr->Release(); d3d->Release(); return false; }

    winrt::com_ptr<MediaEngineProtectionManager> pm;
    try { pm = winrt::make_self<MediaEngineProtectionManager>(g_emeCdm); }
    catch (winrt::hresult_error const& e) { LogLine("[eme] protection manager ctor hr=" + hx(e.code().value)); g_emeCdm->Release(); g_emeCdm=nullptr; dxgiMgr->Release(); d3d->Release(); return false; }

    // Media engine.
    MediaEngineNotify* notify = new MediaEngineNotify();
    EmeNeedKeyNotify* needKey = new EmeNeedKeyNotify();
    IMFAttributes* attrs = nullptr; MFCreateAttributes(&attrs, 6);
    attrs->SetUnknown(MF_MEDIA_ENGINE_CALLBACK, (::IUnknown*)notify);
    attrs->SetUnknown(MF_MEDIA_ENGINE_DXGI_MANAGER, (::IUnknown*)dxgiMgr);
    attrs->SetUnknown(MF_MEDIA_ENGINE_NEEDKEY_CALLBACK, (::IUnknown*)needKey);
    attrs->SetUINT32(MF_MEDIA_ENGINE_CONTENT_PROTECTION_FLAGS, MF_MEDIA_ENGINE_ENABLE_PROTECTED_CONTENT);
    attrs->SetUINT32(MF_MEDIA_ENGINE_VIDEO_OUTPUT_FORMAT, DXGI_FORMAT_B8G8R8A8_UNORM);

    IMFMediaEngineClassFactory* factory = nullptr;
    if (FAILED(hr = CoCreateInstance(CLSID_MFMediaEngineClassFactory, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&factory))))
    { LogLine("[eme] CoCreate factory hr=" + hx(hr)); attrs->Release(); needKey->Release(); notify->Release(); g_emeCdm->Release(); g_emeCdm=nullptr; dxgiMgr->Release(); d3d->Release(); return false; }
    IMFMediaEngine* engine = nullptr;
    hr = factory->CreateInstance(0, attrs, &engine);
    attrs->Release(); factory->Release();
    if (FAILED(hr) || !engine) { LogLine("[eme] CreateInstance hr=" + hx(hr)); needKey->Release(); notify->Release(); g_emeCdm->Release(); g_emeCdm=nullptr; dxgiMgr->Release(); d3d->Release(); return false; }

    // Attach the protection manager (the modern EME wiring).
    IMFMediaEngineProtectedContent* prot = nullptr;
    if (SUCCEEDED(engine->QueryInterface(IID_PPV_ARGS(&prot))) && prot)
    { auto cpm = pm.as<IMFContentProtectionManager>(); HRESULT hs = prot->SetContentProtectionManager(cpm.get()); LogLine("[eme] SetContentProtectionManager hr=" + hx(hs)); }
    else LogLine("[eme] QI IMFMediaEngineProtectedContent FAILED — protected pipeline not wired.");

    IMFMediaEngineEx* engineEx = nullptr;
    engine->QueryInterface(IID_PPV_ARGS(&engineEx));
    if (engineEx) engineEx->EnableWindowlessSwapchainMode(TRUE);

    // SetSource(url): manifest URLs (HLS .m3u8 / Smooth) carry a proper media MIME so MF resolves them BY URL — which
    // the PMP can re-resolve for protected playback. (A raw octet-stream .ismv fails resolution with
    // MF_E_UNSUPPORTED_BYTESTREAM_TYPE by URL, and an app-process content-typed byte stream deadlocks the PMP — so
    // URL-resolvable manifests are the protected route.)
    BSTR burl = SysAllocString(url.c_str());
    hr = engineEx ? engineEx->SetSource(burl) : engine->SetSource(burl);
    SysFreeString(burl);
    LogLine("[eme] SetSource hr=" + hx(hr));
    engine->Play();

    // Wait for metadata (handle valid) or a hard error. NeedKey / license run on MF threads meanwhile.
    auto metaDeadline = std::chrono::steady_clock::now() + std::chrono::seconds(45);
    while (!notify->metadata && !notify->error && std::chrono::steady_clock::now() < metaDeadline)
        std::this_thread::sleep_for(std::chrono::milliseconds(100));

    bool ok = false; HANDLE swHandle = nullptr; DWORD nvw = 0, nvh = 0;
    if (notify->metadata && engineEx)
    {
        engineEx->GetNativeVideoSize(&nvw, &nvh); if (!nvw) { nvw = 1920; nvh = 1080; }
        hr = engineEx->GetVideoSwapchainHandle(&swHandle);
        LogLine("[eme] LOADEDMETADATA " + std::to_string(nvw) + "x" + std::to_string(nvh) + " GetVideoSwapchainHandle hr=" + hx(hr) + " handle=" + std::to_string((uint64_t)(uintptr_t)swHandle));
        ok = SUCCEEDED(hr) && swHandle != nullptr;
    }
    else
        LogLine(std::string("[eme] no metadata (") + (notify->error ? ("error code=" + std::to_string(notify->errCode) + " hr=" + hx(notify->errHr)) : "timeout") + ")");

    QueryCdmKeyStatus();
    LogLine(std::string("[eme] STATE needKey=") + (g_emeNeedKey?"1":"0") + " keyMsg=" + (g_cdmKeyMsg?"1":"0") +
            " licHttp=" + std::to_string(g_cdmLicHttp) + " keyStatus=" + g_cdmKeyStatus + " USABLE=" + (g_cdmUsable?"YES":"no") +
            " metadata=" + (notify->metadata?"1":"0"));

    if (ok)
    {
        RECT dst = { 0, 0, (LONG)nvw, (LONG)nvh }; MFARGB border = { 0, 0, 0, 255 };
        engineEx->UpdateVideoStream(nullptr, &dst, &border);
        WriteCoord("protected", GetCurrentProcessId(), (uint64_t)(uintptr_t)swHandle, (int)nvw, (int)nvh, "playing");
        g_emePublished = true;
        LogLine("[eme] READY — published PROTECTED swapchain handle; keeping alive (auto-present).");
        auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds(120);
        while (std::chrono::steady_clock::now() < deadline && !StopRequested())
        {
            std::this_thread::sleep_for(std::chrono::milliseconds(80));
            LONGLONG pts; engine->OnVideoStreamTick(&pts);
            engineEx->UpdateVideoStream(nullptr, nullptr, nullptr);
        }
        LogLine("[eme] exiting keep-alive.");
    }

    engine->Shutdown();
    if (g_cdmSession) { g_cdmSession->Release(); g_cdmSession = nullptr; }
    if (engineEx) engineEx->Release();
    if (prot) prot->Release();
    engine->Release();
    needKey->Release(); notify->Release();
    pm = nullptr;
    if (g_emeCdm) { g_emeCdm->Release(); g_emeCdm = nullptr; }
    dxgiMgr->Release(); d3d->Release();
    g_emeAttemptDone = true;
    return ok;
}

// The protected media-engine driver: try a prioritized list of PlayReady assets IMFMediaEngine can resolve by URL
// (progressive/Smooth single files — NOT DASH). An optional fg-helper-url.txt (one URL per line) overrides the list.
static int RunProtectedMediaEngine(const std::wstring& baseDir)
{
    int hr;
    if (FAILED(hr = CoInitializeEx(nullptr, COINIT_MULTITHREADED)) && hr != S_FALSE) LogLine("[eme] CoInit hr=0x" + std::to_string((uint32_t)hr));
    if (FAILED(hr = MFStartup(MF_VERSION, MFSTARTUP_FULL))) { LogLine("[eme] MFStartup hr=0x" + std::to_string((uint32_t)hr)); return 2; }

    std::wstring storePath = baseDir + L"\\PRStore"; CreateDirectoryW(storePath.c_str(), nullptr);

    std::vector<std::wstring> urls;
    { std::wifstream f(baseDir + L"\\fg-helper-url.txt"); std::wstring line;
      while (f && std::getline(f, line)) { while (!line.empty() && (line.back()==L'\r'||line.back()==L'\n'||line.back()==L' ')) line.pop_back(); if (!line.empty() && line[0]!=L'#') urls.push_back(line); } }
    if (urls.empty())
    {
        // URL-resolvable manifests with proper media MIME (PMP can re-resolve them). HLS first: its KID matches the
        // key the probe proved USABLE (BBB CBCS, H.264). Then the tearsofsteel Smooth manifest.
        urls.push_back(L"https://test.playready.microsoft.com/media/dash/APPLEENC_CBCS_BBB_1080p/1080p_alternate.m3u8");
        urls.push_back(L"https://test.playready.microsoft.com/media/profficialsite/tearsofsteel_4k.ism.smoothstreaming/manifest");
    }
    const wchar_t* keySystems[] = { L"com.microsoft.playready.recommendation", L"com.microsoft.playready" };

    // Each attempt runs on its own thread with a bounded publish-wait: protected Play() can BLOCK forever (the PMP
    // protected pipeline never returns for a URL/byte-stream source — the working route is a custom/MSE source), so a
    // synchronous call would wedge the helper. We poll g_emePublished (success) / g_emeAttemptDone (clean fail) and
    // ExitProcess at the end so the process always publishes a coord and terminates cleanly.
    bool done = false, hung = false;
    for (auto const& url : urls)
    {
        for (auto ks : keySystems)
        {
            g_emePublished = false; g_emeAttemptDone = false;
            std::wstring u = url; const wchar_t* k = ks;
            std::thread([u, k, storePath]{ RunProtectedUrl(u, k, storePath); }).detach();
            auto dl = std::chrono::steady_clock::now() + std::chrono::seconds(70);
            while (std::chrono::steady_clock::now() < dl && !g_emePublished && !g_emeAttemptDone)
                std::this_thread::sleep_for(std::chrono::milliseconds(200));
            if (g_emePublished) { done = true; break; }
            if (g_emeAttemptDone) continue;                 // clean fail (e.g. unsupported bytestream) — try next
            LogLine("[eme] attempt did not return within 70s — protected Play() blocked (PMP pipeline wedged).");
            hung = true; break;                             // don't spawn more wedged engines
        }
        if (done || hung) break;
    }

    if (done)
    {
        LogLine("[eme] protected handle published — holding process for the engine consumer.");
        auto keep = std::chrono::steady_clock::now() + std::chrono::seconds(120);
        while (std::chrono::steady_clock::now() < keep && !StopRequested()) std::this_thread::sleep_for(std::chrono::milliseconds(200));
        MFShutdown();
        ExitProcess(0);   // force-exit past any detached MF/PMP thread
    }
    LogLine(std::string("[eme] NO protected handle (") + (hung ? "Play blocked — MSE/custom-source route required" : "all attempts failed") +
            ") — publishing error state (see per-attempt HRESULTs above).");
    WriteCoord("protected", GetCurrentProcessId(), 0, 1920, 1080, "error");
    auto keep = std::chrono::steady_clock::now() + std::chrono::seconds(15);
    while (std::chrono::steady_clock::now() < keep && !StopRequested()) std::this_thread::sleep_for(std::chrono::milliseconds(200));
    ExitProcess(1);       // force-exit even if a detached Play() thread is wedged
}

// Desktop PMP bootstrap probe. This runs in a normal full-trust Win32 process and asks Windows to create the
// protected Media Session in its OS-owned mfpmp.exe process. The important result is whether that session exposes
// an IMFPMPHostApp suitable for IMFContentDecryptionModule::SetPMPHostApp; unlike the earlier CDM-self-host probe,
// this host represents a real Protected Environment rather than the caller process.
struct Win32PmpBootstrapProtectionManager
    : winrt::implements<Win32PmpBootstrapProtectionManager, IMFContentProtectionManager>
{
    IFACEMETHODIMP BeginEnableContent(IMFActivate*, IMFTopology*, IMFAsyncCallback*, ::IUnknown*) noexcept override
    {
        LogLine("[win32-pmp] protection manager invoked before CDM wiring.");
        return E_NOTIMPL;
    }
    IFACEMETHODIMP EndEnableContent(IMFAsyncResult*) noexcept override { return E_NOTIMPL; }
};

static int RunWin32PmpProbe(const std::wstring& baseDir)
{
    auto hx = [](HRESULT h){ std::stringstream s; s << "0x" << std::hex << (uint32_t)h; return s.str(); };
    wchar_t pmpServiceText[64]{};
    StringFromGUID2(MF_PMP_SERVICE, pmpServiceText, 64);
    std::wstring pmpServiceWide(pmpServiceText);
    LogLine("[win32-pmp] MF_PMP_SERVICE=" + std::string(pmpServiceWide.begin(), pmpServiceWide.end()));
    LogAppModelPolicy();
    HRESULT hr = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    LogLine("[win32-pmp] CoInitializeEx hr=" + hx(hr));
    hr = MFStartup(MF_VERSION, MFSTARTUP_FULL);
    if (FAILED(hr)) { LogLine("[win32-pmp] MFStartup hr=" + hx(hr)); return 2; }

    auto protectionManager = winrt::make_self<Win32PmpBootstrapProtectionManager>();
    IMFAttributes* sessionAttrs = nullptr;
    hr = MFCreateAttributes(&sessionAttrs, 1);
    if (FAILED(hr)) { LogLine("[win32-pmp] MFCreateAttributes hr=" + hx(hr)); MFShutdown(); return 3; }
    hr = sessionAttrs->SetUnknown(MF_SESSION_CONTENT_PROTECTION_MANAGER,
                                  (::IUnknown*)(IMFContentProtectionManager*)protectionManager.get());
    if (FAILED(hr)) { LogLine("[win32-pmp] SetUnknown(CONTENT_PROTECTION_MANAGER) hr=" + hx(hr)); sessionAttrs->Release(); MFShutdown(); return 3; }

    DWORD pmpFlags = 0;
    wchar_t flagText[16]{};
    if (GetEnvironmentVariableW(L"FG_PMP_FLAGS", flagText, 16)) pmpFlags = wcstoul(flagText, nullptr, 0);
    bool noConfig = GetEnvironmentVariableW(L"FG_PMP_NO_CONFIG", flagText, 16) != 0;
    IMFMediaSession* session = nullptr;
    IMFActivate* enabler = nullptr;
    hr = MFCreatePMPMediaSession(pmpFlags, noConfig ? nullptr : sessionAttrs, &session, &enabler);
    sessionAttrs->Release();
    LogLine("[win32-pmp] MFCreatePMPMediaSession(flags=" + std::to_string(pmpFlags) +
            ", config=" + (noConfig ? "null" : "protection-manager") + ") hr=" + hx(hr) +
            " session=" + (session ? "OK" : "null") + " enabler=" + (enabler ? "yes" : "no"));
    if (FAILED(hr) || !session)
    {
        if (enabler) enabler->Release();
        MFShutdown();
        return 3;
    }

    IMFPMPHost* host = nullptr;
    IMFPMPHostApp* hostApp = nullptr;
    HRESULT hrHost = MFGetService(session, MF_PMP_SERVICE, __uuidof(IMFPMPHost), (void**)&host);
    HRESULT hrHostApp = MFGetService(session, MF_PMP_SERVICE, __uuidof(IMFPMPHostApp), (void**)&hostApp);
    LogLine("[win32-pmp] MFGetService(MF_PMP_SERVICE, IMFPMPHost) hr=" + hx(hrHost) +
            " ptr=" + (host ? "OK" : "null"));
    LogLine("[win32-pmp] MFGetService(MF_PMP_SERVICE, IMFPMPHostApp) hr=" + hx(hrHostApp) +
            " ptr=" + (hostApp ? "OK" : "null"));

    if (hostApp)
    {
        HRESULT hl = hostApp->LockProcess();
        LogLine("[win32-pmp] IMFPMPHostApp::LockProcess hr=" + hx(hl));
        if (SUCCEEDED(hl)) hostApp->UnlockProcess();
    }
    if (host) host->Release();
    if (hostApp) hostApp->Release();
    if (enabler) enabler->Release();
    session->Shutdown();
    session->Release();
    MFShutdown();
    LogLine("[win32-pmp] probe complete.");
    (void)baseDir;
    return SUCCEEDED(hrHostApp) && hostApp ? 0 : 4;
}

int RunProtected()
{
    LogAppModelPolicy();
    // Watchdog: if a media-engine call deadlocks (protected pipeline can block the worker thread, wedging worker.join),
    // force-exit after a hard ceiling so the process never lingers and block subsequent deploys. Success keep-alive is
    // 120s and returns well before this.
    std::thread([]{ std::this_thread::sleep_for(std::chrono::seconds(175)); LogLine("[eme] watchdog ceiling — TerminateProcess."); TerminateProcess(GetCurrentProcess(), 0); }).detach();
    ProbeSetPmpHostAppInUwp();   // independent proof: native MF-CDM PlayReady reaches a USABLE key in genuine-UWP.
    // The real surface producer: the custom CENC IMFMediaSource (Microsoft's MediaEngineEMEUWPSample CdmMediaSource
    // model) — in-app fMP4/CENC demux emitting encrypted IMFSamples to the CDM. RunCustomSource ExitProcesses on its own.
    return RunCustomSource(g_coordPath.substr(0, g_coordPath.find_last_of(L"\\/")));
}

static std::wstring ReadModeFile(const std::wstring& baseDir)
{
    std::wifstream f(baseDir + L"\\fg-helper-mode.txt");
    std::wstring mode; if (f) std::getline(f, mode);
    // trim CR/space
    while (!mode.empty() && (mode.back() == L'\r' || mode.back() == L'\n' || mode.back() == L' ')) mode.pop_back();
    return mode.empty() ? L"pattern" : mode;
}

// [M1.5] Removed the pure-UWP entry points (`#ifndef FG_WIN32_PMP` block): the genuine AppContainer
// `CoreApplication::Run` app (`struct App` + `wWinMain`) and the `FG_UWP_CONSOLE` `wmain`. Both were the UWP
// sidecar's producer entries — dead in the desktop DLL build (FG_WIN32_PMP is defined, so the `#ifndef` was
// always false), which is driven entirely through the FG_DESKTOP_DLL exports (FgPlayReadyRunEx / FgPlayReadyRun)
// composited in-process. The sidecar was deleted in M1.5.
#endif   // FG_UWP
