// ── Platform/ToneImage.cs — SHELL (Windows): encoded image bytes → the small luminance map the scrim solve reads, and the photograph's subject ─
//
// Windows Imaging Component decodes the bytes straight to `MediaScrim.MapEdge` on the long side (the source's aspect kept, as the engine's
// own decode keeps it), as premultiplied BGRA, and `MediaScrim.Measure` turns that into the map. From the same decoded frame it scales a grey copy
// to `FaceEdge` on the long side for the OS's face detector (`FaceFinder`), whose faces place the photograph's subject (`PhotoSubject`; the safe
// zone without a face). Worker thread only; every COM object is created and released inside one call (the engine's FGCOM per-thread rule, cf.
// CoverImage.cs).

using TerraFX.Interop.Windows;
using static TerraFX.Interop.Windows.Windows;

namespace Wavee;

public static unsafe class ToneImage
{
    // CLSID_WICImagingFactory {CACAF262-9370-4615-A13B-9F5539DA4C0A}
    static readonly Guid ClsidFactory = new(0xCACAF262, 0x9370, 0x4615, 0xA1, 0x3B, 0x9F, 0x55, 0x39, 0xDA, 0x4C, 0x0A);
    // GUID_WICPixelFormat32bppPBGRA {6FDDC324-4E03-4BFE-B185-3D77768DC910}
    static readonly Guid Format32bppPbgra = new(0x6FDDC324, 0x4E03, 0x4BFE, 0xB1, 0x85, 0x3D, 0x77, 0x76, 0x8D, 0xC9, 0x10);
    // GUID_WICPixelFormat8bppGray {6FDDC324-4E03-4BFE-B185-3D77768DC908}
    static readonly Guid Format8bppGray = new(0x6FDDC324, 0x4E03, 0x4BFE, 0xB1, 0x85, 0x3D, 0x77, 0x76, 0x8D, 0xC9, 0x08);

    /// <summary>The grey copy the face detector reads: this long side (a face a tenth of a 7:3 header's height is ~70 px tall there, over the detector's
    /// floor), its width a multiple of 4 (the detector's bitmap rows are packed).</summary>
    public const int FaceEdge = 1600;

    /// <summary>The detector's whole budget (its model loads on the first photograph); past it the photograph takes the safe zone.</summary>
    public static readonly TimeSpan FaceBudget = TimeSpan.FromSeconds(4);

    /// <summary>What a photograph measures to: its luminance map (null when it does not decode) and its subject (the safe zone without faces).</summary>
    public readonly record struct Reading(MediaScrim.LumaMap? Map, PhotoSubject Subject);

    /// <summary>The map and the subject of the image in <paramref name="source"/> (its first <paramref name="length"/> bytes). Worker thread.</summary>
    public static Reading Read(byte[] source, int length)
    {
        if (length <= 0) return new(null, PhotoSubject.SafeZone);
        CoInitializeEx(null, (uint)COINIT.COINIT_MULTITHREADED);   // S_FALSE / RPC_E_CHANGED_MODE: already initialised, fine for WIC
        IWICImagingFactory* factory = null;
        IWICStream* stream = null;
        IWICBitmapDecoder* decoder = null;
        IWICBitmapFrameDecode* frame = null;
        try
        {
            Guid clsid = ClsidFactory;
            Guid iid = __uuidof<IWICImagingFactory>();
            if (CoCreateInstance(&clsid, null, (uint)CLSCTX.CLSCTX_INPROC_SERVER, &iid, (void**)&factory).FAILED) return new(null, PhotoSubject.SafeZone);
            if (factory->CreateStream(&stream).FAILED) return new(null, PhotoSubject.SafeZone);
            fixed (byte* p = source)
            {
                if (stream->InitializeFromMemory(p, (uint)length).FAILED) return new(null, PhotoSubject.SafeZone);
                if (factory->CreateDecoderFromStream((IStream*)stream, null, WICDecodeOptions.WICDecodeMetadataCacheOnDemand, &decoder).FAILED
                    || decoder->GetFrame(0, &frame).FAILED)
                    return new(null, PhotoSubject.SafeZone);
                uint w = 0, h = 0;
                if (frame->GetSize(&w, &h).FAILED || w == 0 || h == 0) return new(null, PhotoSubject.SafeZone);

                float fit = MediaScrim.MapEdge / (float)Math.Max(w, h);
                int tw = Math.Max(1, (int)MathF.Round(w * MathF.Min(1f, fit))), th = Math.Max(1, (int)MathF.Round(h * MathF.Min(1f, fit)));
                byte[]? bgra = Pixels(factory, frame, tw, th, Format32bppPbgra, 4);
                var map = bgra is null ? null : MediaScrim.Measure(bgra, tw, th);

                float faceFit = MathF.Min(1f, FaceEdge / (float)Math.Max(w, h));
                int gw = Math.Max(4, (int)(w * faceFit) & ~3), gh = Math.Max(1, (int)MathF.Round(h * faceFit));
                byte[]? gray = Pixels(factory, frame, gw, gh, Format8bppGray, 1);
                var subject = gray is null ? PhotoSubject.SafeZone : PhotoSubject.Of(FaceFinder.Find(gray, gw, gh, FaceBudget), gw, gh);
                return new(map, subject);
            }
        }
        finally
        {
            if (frame != null) frame->Release();
            if (decoder != null) decoder->Release();
            if (stream != null) stream->Release();
            if (factory != null) factory->Release();
        }
    }

    /// <summary><paramref name="frame"/> scaled to <paramref name="width"/> x <paramref name="height"/> (Fant) in <paramref name="format"/>, rows packed
    /// at <paramref name="bytesPerPixel"/>; null when a step fails.</summary>
    static byte[]? Pixels(IWICImagingFactory* factory, IWICBitmapFrameDecode* frame, int width, int height, Guid format, int bytesPerPixel)
    {
        IWICBitmapScaler* scaler = null;
        IWICFormatConverter* converter = null;
        try
        {
            if (factory->CreateBitmapScaler(&scaler).FAILED
                || scaler->Initialize((IWICBitmapSource*)frame, (uint)width, (uint)height, WICBitmapInterpolationMode.WICBitmapInterpolationModeFant).FAILED)
                return null;
            if (factory->CreateFormatConverter(&converter).FAILED) return null;
            if (converter->Initialize((IWICBitmapSource*)scaler, &format, WICBitmapDitherType.WICBitmapDitherTypeNone,
                    null, 0.0, WICBitmapPaletteType.WICBitmapPaletteTypeCustom).FAILED)
                return null;
            byte[] pixels = new byte[width * height * bytesPerPixel];
            fixed (byte* d = pixels)
                if (converter->CopyPixels(null, (uint)(width * bytesPerPixel), (uint)pixels.Length, d).FAILED) return null;
            return pixels;
        }
        finally
        {
            if (converter != null) converter->Release();
            if (scaler != null) scaler->Release();
        }
    }
}
