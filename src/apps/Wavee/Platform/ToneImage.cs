// ── Platform/ToneImage.cs — SHELL (Windows): encoded image bytes → the small luminance map the scrim solve reads ─────────────────────
//
// Windows Imaging Component decodes the bytes straight to `MediaScrim.MapEdge` on the long side (the source's aspect kept, as the engine's
// own decode keeps it), as premultiplied BGRA, and `MediaScrim.Measure` turns that into the map. Worker thread only; every COM object is
// created and released inside one call (the engine's FGCOM per-thread rule, cf. CoverImage.cs).

using TerraFX.Interop.Windows;
using static TerraFX.Interop.Windows.Windows;

namespace Wavee;

public static unsafe class ToneImage
{
    // CLSID_WICImagingFactory {CACAF262-9370-4615-A13B-9F5539DA4C0A}
    static readonly Guid ClsidFactory = new(0xCACAF262, 0x9370, 0x4615, 0xA1, 0x3B, 0x9F, 0x55, 0x39, 0xDA, 0x4C, 0x0A);
    // GUID_WICPixelFormat32bppPBGRA {6FDDC324-4E03-4BFE-B185-3D77768DC910}
    static readonly Guid Format32bppPbgra = new(0x6FDDC324, 0x4E03, 0x4BFE, 0xB1, 0x85, 0x3D, 0x77, 0x76, 0x8D, 0xC9, 0x10);

    /// <summary>The map of the image in <paramref name="source"/> (its first <paramref name="length"/> bytes), or null when it does not decode. Worker thread.</summary>
    public static MediaScrim.LumaMap? Measure(byte[] source, int length)
    {
        if (length <= 0) return null;
        CoInitializeEx(null, (uint)COINIT.COINIT_MULTITHREADED);   // S_FALSE / RPC_E_CHANGED_MODE: already initialised, fine for WIC
        IWICImagingFactory* factory = null;
        IWICStream* stream = null;
        IWICBitmapDecoder* decoder = null;
        IWICBitmapFrameDecode* frame = null;
        IWICBitmapScaler* scaler = null;
        IWICFormatConverter* converter = null;
        try
        {
            Guid clsid = ClsidFactory;
            Guid iid = __uuidof<IWICImagingFactory>();
            if (CoCreateInstance(&clsid, null, (uint)CLSCTX.CLSCTX_INPROC_SERVER, &iid, (void**)&factory).FAILED) return null;
            if (factory->CreateStream(&stream).FAILED) return null;
            fixed (byte* p = source)
            {
                if (stream->InitializeFromMemory(p, (uint)length).FAILED) return null;
                if (factory->CreateDecoderFromStream((IStream*)stream, null, WICDecodeOptions.WICDecodeMetadataCacheOnDemand, &decoder).FAILED
                    || decoder->GetFrame(0, &frame).FAILED)
                    return null;
                uint w = 0, h = 0;
                if (frame->GetSize(&w, &h).FAILED || w == 0 || h == 0) return null;
                float fit = MediaScrim.MapEdge / (float)Math.Max(w, h);
                int tw = Math.Max(1, (int)MathF.Round(w * MathF.Min(1f, fit))), th = Math.Max(1, (int)MathF.Round(h * MathF.Min(1f, fit)));
                if (factory->CreateBitmapScaler(&scaler).FAILED
                    || scaler->Initialize((IWICBitmapSource*)frame, (uint)tw, (uint)th, WICBitmapInterpolationMode.WICBitmapInterpolationModeFant).FAILED)
                    return null;
                if (factory->CreateFormatConverter(&converter).FAILED) return null;
                Guid pbgra = Format32bppPbgra;
                if (converter->Initialize((IWICBitmapSource*)scaler, &pbgra, WICBitmapDitherType.WICBitmapDitherTypeNone,
                        null, 0.0, WICBitmapPaletteType.WICBitmapPaletteTypeCustom).FAILED)
                    return null;
                byte[] bgra = new byte[tw * th * 4];
                fixed (byte* d = bgra)
                    if (converter->CopyPixels(null, (uint)(tw * 4), (uint)bgra.Length, d).FAILED) return null;
                return MediaScrim.Measure(bgra, tw, th);
            }
        }
        finally
        {
            if (converter != null) converter->Release();
            if (scaler != null) scaler->Release();
            if (frame != null) frame->Release();
            if (decoder != null) decoder->Release();
            if (stream != null) stream->Release();
            if (factory != null) factory->Release();
        }
    }
}
