// ── Platform/CoverImage.cs ───────────────────────────────────────────────────────────────────────────────────────────
// SHELL (Windows): a picked image → the playlist cover Spotify takes (#155). Windows Imaging Component decodes whatever
// the user chose (JPEG, PNG, BMP, GIF, WebP), the centred square is clipped and scaled on the STORED pixels, the EXIF turn
// is applied to the small square (PlaylistCoverRules.Orient), and the JPEG encoder walks the budget ladder
// (PlaylistCoverRules.Fit) until the file is ≤ 256 KB. Worker thread only — never the UI thread; every COM object is
// created and released inside one call (the engine's FGCOM per-thread rule, cf. FluentGpu.Windows/Wic/WicImageCodec.cs).

using TerraFX.Interop.Windows;
using static TerraFX.Interop.Windows.Windows;

namespace Wavee;

public static unsafe class CoverImage
{
    // CLSID_WICImagingFactory {CACAF262-9370-4615-A13B-9F5539DA4C0A}
    static readonly Guid ClsidFactory = new(0xCACAF262, 0x9370, 0x4615, 0xA1, 0x3B, 0x9F, 0x55, 0x39, 0xDA, 0x4C, 0x0A);
    // GUID_ContainerFormatJpeg {19E4A5AA-5662-4FC5-A0C0-1758028E1057}
    static readonly Guid ContainerJpeg = new(0x19E4A5AA, 0x5662, 0x4FC5, 0xA0, 0xC0, 0x17, 0x58, 0x02, 0x8E, 0x10, 0x57);
    // GUID_WICPixelFormat24bppBGR {6FDDC324-4E03-4BFE-B185-3D77768DC90C}
    static readonly Guid Format24bppBgr = new(0x6FDDC324, 0x4E03, 0x4BFE, 0xB1, 0x85, 0x3D, 0x77, 0x76, 0x8D, 0xC9, 0x0C);
    // GUID_WICPixelFormat32bppPBGRA {6FDDC324-4E03-4BFE-B185-3D77768DC910}
    static readonly Guid Format32bppPbgra = new(0x6FDDC324, 0x4E03, 0x4BFE, 0xB1, 0x85, 0x3D, 0x77, 0x76, 0x8D, 0xC9, 0x10);

    /// <summary>The encoder's scratch: a 640² 24-bit JPEG at quality 90 is well under 1 MB even for noise.</summary>
    const int OutputScratchBytes = 4 * 1024 * 1024;

    /// <summary>Read, check and encode the file at <paramref name="path"/>. Worker thread.</summary>
    public static (CoverEncoding? Cover, CoverProblem Problem) Prepare(string path)
    {
        long length;
        try
        {
            var info = new FileInfo(path);
            length = info.Exists ? info.Length : -1;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            length = -1;
        }
        var problem = PlaylistCoverRules.CheckFile(path, length);
        if (problem != CoverProblem.None) return (null, problem);
        byte[] source;
        try { source = File.ReadAllBytes(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return (null, CoverProblem.Unreadable); }
        return Encode(source);
    }

    /// <summary>Encoded image bytes → the cover. Worker thread.</summary>
    public static (CoverEncoding? Cover, CoverProblem Problem) Encode(byte[] source)
    {
        if (source.Length == 0) return (null, CoverProblem.Unreadable);
        CoInitializeEx(null, (uint)COINIT.COINIT_MULTITHREADED);   // S_FALSE / RPC_E_CHANGED_MODE: already initialised — fine for WIC

        IWICImagingFactory* factory = null;
        IWICStream* stream = null;
        IWICBitmapDecoder* decoder = null;
        IWICBitmapFrameDecode* frame = null;
        IWICBitmapClipper* clipper = null;
        try
        {
            Guid clsid = ClsidFactory;
            Guid iid = __uuidof<IWICImagingFactory>();
            if (CoCreateInstance(&clsid, null, (uint)CLSCTX.CLSCTX_INPROC_SERVER, &iid, (void**)&factory).FAILED) return (null, CoverProblem.Unreadable);
            if (factory->CreateStream(&stream).FAILED) return (null, CoverProblem.Unreadable);
            fixed (byte* p = source)
            {
                if (stream->InitializeFromMemory(p, (uint)source.Length).FAILED) return (null, CoverProblem.Unreadable);
                if (factory->CreateDecoderFromStream((IStream*)stream, null, WICDecodeOptions.WICDecodeMetadataCacheOnDemand, &decoder).FAILED
                    || decoder->GetFrame(0, &frame).FAILED)
                    return (null, CoverProblem.Unreadable);

                uint w = 0, h = 0;
                if (frame->GetSize(&w, &h).FAILED || w == 0 || h == 0) return (null, CoverProblem.Unreadable);
                var (x, y, side) = PlaylistCoverRules.CenterSquare((int)w, (int)h);
                if (side < PlaylistCoverRules.MinSourceEdge) return (null, CoverProblem.TooSmall);
                int orientation = Orientation(frame);

                if (factory->CreateBitmapClipper(&clipper).FAILED) return (null, CoverProblem.Unreadable);
                var rect = new WICRect { X = x, Y = y, Width = side, Height = side };
                if (clipper->Initialize((IWICBitmapSource*)frame, &rect).FAILED) return (null, CoverProblem.Unreadable);

                // The ladder re-encodes at one edge before it scales again: decode a size once, encode it per quality.
                // (Copies of the two pointers: a lambda may not capture a local whose address was taken above.)
                IWICImagingFactory* f = factory;
                IWICBitmapClipper* c = clipper;
                byte[] scratch = new byte[OutputScratchBytes];
                int pixelsEdge = 0;
                byte[]? bgr = null;
                var fit = PlaylistCoverRules.Fit(side, (edge, quality) =>
                {
                    if (edge != pixelsEdge)
                    {
                        bgr = Pixels(f, c, edge, orientation);
                        pixelsEdge = edge;
                    }
                    return bgr is null ? null : EncodeJpeg(f, bgr, edge, quality, scratch);
                });
                return fit is { } cover ? (cover, CoverProblem.None) : (null, CoverProblem.Unreadable);
            }
        }
        finally
        {
            if (clipper != null) clipper->Release();
            if (frame != null) frame->Release();
            if (decoder != null) decoder->Release();
            if (stream != null) stream->Release();
            if (factory != null) factory->Release();
        }
    }

    /// <summary>The clipped square scaled to <paramref name="edge"/>², turned upright, as 24-bit BGR.</summary>
    static byte[]? Pixels(IWICImagingFactory* factory, IWICBitmapClipper* clipper, int edge, int orientation)
    {
        IWICBitmapScaler* scaler = null;
        IWICFormatConverter* converter = null;
        try
        {
            if (factory->CreateBitmapScaler(&scaler).FAILED
                || scaler->Initialize((IWICBitmapSource*)clipper, (uint)edge, (uint)edge,
                       WICBitmapInterpolationMode.WICBitmapInterpolationModeHighQualityCubic).FAILED)
                return null;
            if (factory->CreateFormatConverter(&converter).FAILED) return null;
            Guid pbgra = Format32bppPbgra;
            if (converter->Initialize((IWICBitmapSource*)scaler, &pbgra, WICBitmapDitherType.WICBitmapDitherTypeNone,
                    null, 0.0, WICBitmapPaletteType.WICBitmapPaletteTypeCustom).FAILED)
                return null;
            byte[] bgra = new byte[edge * edge * 4];
            fixed (byte* d = bgra)
                if (converter->CopyPixels(null, (uint)(edge * 4), (uint)bgra.Length, d).FAILED) return null;
            PlaylistCoverRules.Orient(bgra, edge, orientation);
            byte[] bgr = new byte[edge * edge * 3];
            PlaylistCoverRules.BgraToBgr(bgra, bgr);
            return bgr;
        }
        finally
        {
            if (converter != null) converter->Release();
            if (scaler != null) scaler->Release();
        }
    }

    /// <summary>One JPEG at <paramref name="quality"/> (0-100), or null when the encoder failed.</summary>
    static byte[]? EncodeJpeg(IWICImagingFactory* factory, byte[] bgr, int edge, int quality, byte[] scratch)
    {
        IWICStream* stream = null;
        IWICBitmapEncoder* encoder = null;
        IWICBitmapFrameEncode* frame = null;
        IPropertyBag2* options = null;
        try
        {
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
                    value.fltVal = Math.Clamp(quality, 1, 100) / 100f;
                    if (options->Write(1, &option, &value).FAILED) return null;
                }
                Guid format = Format24bppBgr;
                if (frame->Initialize(options).FAILED || frame->SetSize((uint)edge, (uint)edge).FAILED
                    || frame->SetPixelFormat(&format).FAILED || format != Format24bppBgr)
                    return null;
                fixed (byte* p = bgr)
                    if (frame->WritePixels((uint)edge, (uint)(edge * 3), (uint)bgr.Length, p).FAILED) return null;
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
        }
    }

    /// <summary>EXIF tag 274 (Orientation) under a JPEG's APP1, then a TIFF's own IFD.</summary>
    static readonly string[] OrientationQueries = ["/app1/ifd/{ushort=274}", "/ifd/{ushort=274}"];

    /// <summary>The EXIF orientation (1-8) of a JPEG or TIFF frame; 1 when there is none.</summary>
    static int Orientation(IWICBitmapFrameDecode* frame)
    {
        IWICMetadataQueryReader* reader = null;
        if (frame->GetMetadataQueryReader(&reader).FAILED || reader == null) return 1;
        try
        {
            foreach (string query in OrientationQueries)
            {
                PROPVARIANT value = default;
                fixed (char* name = query)
                {
                    if (reader->GetMetadataByName(name, &value).FAILED) continue;
                }
                int orientation = value.vt == (ushort)VARENUM.VT_UI2 ? value.uiVal : 0;
                PropVariantClear(&value);
                if (orientation is >= 1 and <= 8) return orientation;
            }
            return 1;
        }
        finally { reader->Release(); }
    }
}
