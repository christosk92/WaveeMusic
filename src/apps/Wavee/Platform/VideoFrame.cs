// ── Platform/VideoFrame.cs ───────────────────────────────────────────────────────────────────────────────────────────
// SHELL (Windows): one still of a local video file, as JPEG bytes, for its cover (#240 local video; `LocalArt` stores it once in
// Cache\art). Media Foundation's source reader opens the file, seeks to about 10 % of its length (a music video's first frames are
// usually a black card or a label's logo), decodes forward to the first frame at or past that point and hands it over as RGB32 already
// scaled to at most `MaxWidth` wide (the reader's advanced video processing does the colour conversion and the scale); WIC encodes it.
// The picture keeps the video's own aspect. Worker thread only: every COM object is created and released inside one call (the
// engine's FGCOM per-thread rule, cf. `CoverImage`).

using TerraFX.Interop.Windows;
using static TerraFX.Interop.Windows.Windows;

namespace Wavee;

public static unsafe class VideoFrame
{
    /// <summary>The still's width at most (a 1920x1080 video gives 960x540): enough for the widest cover slot at 2x.</summary>
    public const int MaxWidth = 960;

    /// <summary>Where in the video the still is taken, as a fraction of its length.</summary>
    public const double At = 0.10;

    /// <summary>How many samples the reader may decode past the seek's key frame before it settles for the last one.</summary>
    const int MaxSamples = 240;

    const uint FirstVideoStream = 0xFFFFFFFC, AllStreams = 0xFFFFFFFE, MediaSource = 0xFFFFFFFF;
    const uint ReaderEndOfStream = 0x2;
    const uint MfVersion = 0x00020070, MfStartupLite = 0x1;

    // CLSID_WICImagingFactory {CACAF262-9370-4615-A13B-9F5539DA4C0A}
    static readonly Guid ClsidFactory = new(0xCACAF262, 0x9370, 0x4615, 0xA1, 0x3B, 0x9F, 0x55, 0x39, 0xDA, 0x4C, 0x0A);
    // GUID_ContainerFormatJpeg {19E4A5AA-5662-4FC5-A0C0-1758028E1057}
    static readonly Guid ContainerJpeg = new(0x19E4A5AA, 0x5662, 0x4FC5, 0xA0, 0xC0, 0x17, 0x58, 0x02, 0x8E, 0x10, 0x57);
    // GUID_WICPixelFormat24bppBGR {6FDDC324-4E03-4BFE-B185-3D77768DC90C}
    static readonly Guid Format24bppBgr = new(0x6FDDC324, 0x4E03, 0x4BFE, 0xB1, 0x85, 0x3D, 0x77, 0x76, 0x8D, 0xC9, 0x0C);

    // The Media Foundation keys this file reads (mfapi.h, mfidl.h, mfreadwrite.h).
    static readonly Guid AdvancedVideoProcessing = new(0x0F81DA2C, 0xB537, 0x4672, 0xA8, 0xB2, 0xA6, 0x81, 0xB1, 0x73, 0x07, 0xA3);
    static readonly Guid FrameSize = new(0x1652C33D, 0xD6B2, 0x4012, 0xB8, 0x34, 0x72, 0x03, 0x08, 0x49, 0xA3, 0x7D);
    static readonly Guid MajorType = new(0x48EBA18E, 0xF8C9, 0x4687, 0xBF, 0x11, 0x0A, 0x74, 0xC9, 0xF9, 0x6A, 0x8F);
    static readonly Guid Subtype = new(0xF7E34C9A, 0x42E8, 0x4714, 0xB7, 0x4B, 0xCB, 0x29, 0xD7, 0x2C, 0x35, 0xE5);
    static readonly Guid MediaTypeVideo = new(0x73646976, 0x0000, 0x0010, 0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71);
    static readonly Guid VideoFormatRgb32 = new(0x00000016, 0x0000, 0x0010, 0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71);
    static readonly Guid PresentationDuration = new(0x6C990D33, 0xBB8E, 0x477A, 0x85, 0x98, 0x0D, 0x5D, 0x96, 0xFC, 0xD8, 0x8A);

    /// <summary>Why the last <see cref="Grab"/> on this thread returned null (the failing call and its HRESULT), for the log.</summary>
    public static string LastFailure => s_why ?? "";
    [ThreadStatic] static string? s_why;

    static bool Failed(HRESULT hr, string stage)
    {
        if (hr.SUCCEEDED) return false;
        s_why = stage + " 0x" + ((uint)hr.Value).ToString("X8", System.Globalization.CultureInfo.InvariantCulture);
        return true;
    }

    /// <summary>The still of <paramref name="path"/> as JPEG bytes, or null (no video stream, no decoder for it, an unreadable file).
    /// <paramref name="atMs"/>: the frame at that time (a frame the user chose, "Choose frame…"); negative: at <see cref="At"/> of its length.
    /// Blocks for the open, the seek and a few decodes: worker thread only.</summary>
    public static byte[]? Grab(string path, long atMs = -1)
    {
        s_why = null;
        if (Failed(MFStartup(MfVersion, MfStartupLite), "MFStartup")) return null;
        using var crashRegion = FluentGpu.Foundation.CrashTrail.Enter(FluentGpu.Foundation.NativeRegion.WicDecode, "video-frame", 0);
        try
        {
            if (Still(path, atMs, out int w, out int h) is not { } bgr) return null;
            byte[]? jpeg = Encode(bgr, w, h);
            if (jpeg is null) s_why ??= "encode";
            return jpeg;
        }
        finally { MFShutdown(); }
    }

    /// <summary>The frame as 24-bit BGR rows, top-down, <paramref name="w"/> x <paramref name="h"/>.</summary>
    static byte[]? Still(string path, long atMs, out int w, out int h)
    {
        w = h = 0;
        IMFAttributes* attributes = null;
        IMFSourceReader* reader = null;
        IMFMediaType* native = null;
        IMFMediaType* wanted = null;
        IMFMediaType* current = null;
        Guid advanced = AdvancedVideoProcessing, frameSize = FrameSize, majorType = MajorType, subtype = Subtype, video = MediaTypeVideo,
             rgb32 = VideoFormatRgb32, durationKey = PresentationDuration;
        try
        {
            if (Failed(MFCreateAttributes(&attributes, 2), "MFCreateAttributes")) return null;
            attributes->SetUINT32(&advanced, 1);
            fixed (char* url = path)
                if (Failed(MFCreateSourceReaderFromURL(url, attributes, &reader), "MFCreateSourceReaderFromURL")) return null;
            reader->SetStreamSelection(AllStreams, BOOL.FALSE);
            if (Failed(reader->SetStreamSelection(FirstVideoStream, BOOL.TRUE), "SetStreamSelection")) return null;
            if (Failed(reader->GetNativeMediaType(FirstVideoStream, 0, &native), "GetNativeMediaType")) return null;
            ulong size = 0;
            if (Failed(native->GetUINT64(&frameSize, &size), "GetUINT64")) return null;
            int nw = (int)(size >> 32), nh = (int)(size & 0xFFFFFFFF);
            if (nw <= 0 || nh <= 0) return null;
            int tw = Math.Min(nw, MaxWidth) & ~1;
            int th = Math.Max(2, (int)Math.Round(nh * (double)tw / nw) & ~1);

            if (Failed(MFCreateMediaType(&wanted), "MFCreateMediaType")) return null;
            wanted->SetGUID(&majorType, &video);
            wanted->SetGUID(&subtype, &rgb32);
            wanted->SetUINT64(&frameSize, ((ulong)(uint)tw << 32) | (uint)th);
            if (reader->SetCurrentMediaType(FirstVideoStream, null, wanted).FAILED)
            {
                // No scaler on this path: take the frame at the decoder's size.
                wanted->DeleteItem(&frameSize);
                if (Failed(reader->SetCurrentMediaType(FirstVideoStream, null, wanted), "SetCurrentMediaType")) return null;
            }
            if (Failed(reader->GetCurrentMediaType(FirstVideoStream, &current), "GetCurrentMediaType") || Failed(current->GetUINT64(&frameSize, &size), "FrameSize")) return null;
            int fw = (int)(size >> 32), fh = (int)(size & 0xFFFFFFFF);
            if (fw <= 0 || fh <= 0) return null;

            PROPVARIANT duration = default;
            long target = 0;
            if (reader->GetPresentationAttribute(MediaSource, &durationKey, &duration).SUCCEEDED)
            {
                long length = (long)duration.uhVal.QuadPart;   // 100 ns units
                target = atMs >= 0 ? Math.Min(atMs * 10_000L, Math.Max(0L, length - 10_000_000L)) : (long)(length * At);
                PropVariantClear(&duration);
            }
            else if (atMs > 0) target = atMs * 10_000L;
            if (target > 0)
            {
                PROPVARIANT position = default;
                position.vt = (ushort)VARENUM.VT_I8;
                position.hVal.QuadPart = target;
                Guid format = default;
                if (reader->SetCurrentPosition(&format, &position).FAILED) target = 0;
            }

            byte[]? last = null;
            for (int n = 0; n < MaxSamples; n++)
            {
                uint index = 0, flags = 0;
                long time = 0;
                IMFSample* sample = null;
                if (reader->ReadSample(FirstVideoStream, 0, &index, &flags, &time, &sample).FAILED) break;
                if (sample != null)
                {
                    try { last = Rows(sample, fw, fh) ?? last; }
                    finally { sample->Release(); }
                    if (last is not null && time >= target) break;
                }
                if ((flags & ReaderEndOfStream) != 0) break;
            }
            if (last is null) { s_why = "no sample"; return null; }
            w = fw;
            h = fh;
            return last;
        }
        finally
        {
            if (current != null) current->Release();
            if (wanted != null) wanted->Release();
            if (native != null) native->Release();
            if (reader != null) reader->Release();
            if (attributes != null) attributes->Release();
        }
    }

    /// <summary>One RGB32 sample's pixels as top-down 24-bit BGR (the 2D lock gives the top row and its signed pitch).</summary>
    static byte[]? Rows(IMFSample* sample, int w, int h)
    {
        IMFMediaBuffer* buffer = null;
        IMF2DBuffer* plane = null;
        try
        {
            if (sample->ConvertToContiguousBuffer(&buffer).FAILED) return null;
            var bgr = new byte[w * h * 3];
            Guid iid = __uuidof<IMF2DBuffer>();
            if (buffer->QueryInterface(&iid, (void**)&plane).SUCCEEDED)
            {
                byte* top = null;
                int pitch = 0;
                if (plane->Lock2D(&top, &pitch).FAILED) return null;
                try { Copy(top, pitch, w, h, bgr); }
                finally { plane->Unlock2D(); }
                return bgr;
            }
            byte* data = null;
            uint max = 0, length = 0;
            if (buffer->Lock(&data, &max, &length).FAILED) return null;
            try
            {
                if (length < (uint)(w * h * 4)) return null;
                Copy(data, w * 4, w, h, bgr);
            }
            finally { buffer->Unlock(); }
            return bgr;
        }
        finally
        {
            if (plane != null) plane->Release();
            if (buffer != null) buffer->Release();
        }
    }

    static void Copy(byte* top, int pitch, int w, int h, byte[] bgr)
    {
        int o = 0;
        for (int y = 0; y < h; y++)
        {
            byte* row = top + (long)y * pitch;
            for (int x = 0; x < w; x++, row += 4)
            {
                bgr[o++] = row[0];
                bgr[o++] = row[1];
                bgr[o++] = row[2];
            }
        }
    }

    /// <summary>The rows as one JPEG at quality 85, or null when the encoder failed.</summary>
    static byte[]? Encode(byte[] bgr, int w, int h)
    {
        CoInitializeEx(null, (uint)COINIT.COINIT_MULTITHREADED);   // S_FALSE / RPC_E_CHANGED_MODE: already initialised, fine for WIC
        IWICImagingFactory* factory = null;
        IWICStream* stream = null;
        IWICBitmapEncoder* encoder = null;
        IWICBitmapFrameEncode* frame = null;
        IPropertyBag2* options = null;
        byte[] scratch = new byte[Math.Max(64 * 1024, w * h * 3 / 2)];
        try
        {
            Guid clsid = ClsidFactory;
            Guid iid = __uuidof<IWICImagingFactory>();
            if (CoCreateInstance(&clsid, null, (uint)CLSCTX.CLSCTX_INPROC_SERVER, &iid, (void**)&factory).FAILED) return null;
            fixed (byte* o = scratch)
            {
                if (factory->CreateStream(&stream).FAILED || stream->InitializeFromMemory(o, (uint)scratch.Length).FAILED) return null;
                Guid container = ContainerJpeg;
                if (factory->CreateEncoder(&container, null, &encoder).FAILED
                    || encoder->Initialize((IStream*)stream, WICBitmapEncoderCacheOption.WICBitmapEncoderNoCache).FAILED
                    || encoder->CreateNewFrame(&frame, &options).FAILED)
                    return null;
                fixed (char* name = "ImageQuality")
                {
                    PROPBAG2 option = default;
                    option.pstrName = name;
                    VARIANT value = default;
                    value.vt = (ushort)VARENUM.VT_R4;
                    value.fltVal = 0.85f;
                    if (options->Write(1, &option, &value).FAILED) return null;
                }
                Guid format = Format24bppBgr;
                if (frame->Initialize(options).FAILED || frame->SetSize((uint)w, (uint)h).FAILED
                    || frame->SetPixelFormat(&format).FAILED || format != Format24bppBgr)
                    return null;
                fixed (byte* p = bgr)
                    if (frame->WritePixels((uint)h, (uint)(w * 3), (uint)bgr.Length, p).FAILED) return null;
                if (frame->Commit().FAILED || encoder->Commit().FAILED) return null;
                ULARGE_INTEGER end = default;
                if (((IStream*)stream)->Seek(default, (uint)STREAM_SEEK.STREAM_SEEK_CUR, &end).FAILED) return null;
                int length = (int)end.QuadPart;
                return length > 0 && length <= scratch.Length ? scratch.AsSpan(0, length).ToArray() : null;
            }
        }
        finally
        {
            if (options != null) options->Release();
            if (frame != null) frame->Release();
            if (encoder != null) encoder->Release();
            if (stream != null) stream->Release();
            if (factory != null) factory->Release();
        }
    }
}
