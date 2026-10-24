// ── Platform/FaceFinder.cs — SHELL (Windows): the faces in a photograph, found offline by the OS (lane B10) ──────────────────────────────────
//
// `Windows.Media.FaceAnalysis.FaceDetector` (the offline detector Windows ships) over a Gray8 copy of a photograph, for `PhotoSubject` (the artist
// hero keeps the faces in view). The engine's WinRT posture (`Win32InputPane`): the cold activation entry points declared locally, a hand-vtable
// consume path (`lpVtbl[n]` calli), no projection and no ComWrappers, so it is NativeAOT-safe. The IIDs and vtable slots are the 10.0.26100
// SDK's (winrt\windows.media.faceanalysis.h, windows.graphics.imaging.h, windows.security.cryptography.h, asyncinfo.h).
//
// Worker thread only (the photograph's measuring worker, `Palette.Tone`); the two async operations are awaited there by polling their
// `IAsyncInfo`, within a budget. Every step degrades to "no faces": a Windows without the detector (an N edition without the media pack), a
// denied activation, a failed or slow operation. Every object is released inside the call.

using System.Runtime.InteropServices;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.Windows.Windows;

namespace Wavee;

/// <summary>Finds the faces in a grey image with the OS's offline face detector. See the file header.</summary>
public static unsafe partial class FaceFinder
{
    [LibraryImport("combase.dll")]
    private static partial int RoGetActivationFactory(nint activatableClassId, Guid* iid, void** factory);

    [LibraryImport("combase.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int WindowsCreateString(string sourceString, uint length, nint* hstring);

    [LibraryImport("combase.dll")]
    private static partial int WindowsDeleteString(nint hstring);

    const string FaceDetectorClass = "Windows.Media.FaceAnalysis.FaceDetector";
    const string SoftwareBitmapClass = "Windows.Graphics.Imaging.SoftwareBitmap";
    const string CryptographicBufferClass = "Windows.Security.Cryptography.CryptographicBuffer";

    static readonly Guid IID_IFaceDetectorStatics = new("bc042d67-9047-33f6-881b-6746c1b218b8");
    static readonly Guid IID_ISoftwareBitmapStatics = new("df0385db-672f-4a9d-806e-c2442f343e86");
    static readonly Guid IID_ICryptographicBufferStatics = new("320b7e22-3cb0-4cdf-8663-1d28910065eb");
    static readonly Guid IID_IAsyncInfo = new("00000036-0000-0000-c000-000000000046");

    /// <summary>`BitmapPixelFormat.Gray8`.</summary>
    const int Gray8 = 62;

    /// <summary>`AsyncStatus`: Started, Completed (Canceled and Error are the rest).</summary>
    const int Started = 0, Completed = 1;

    // vtable slots (IInspectable takes 0..5)
    const int SlotQueryInterface = 0, SlotRelease = 2;
    const int SlotFaceStaticsCreateAsync = 6, SlotFaceStaticsIsSupported = 9;
    const int SlotFaceDetectFacesAsync = 6;
    const int SlotBufferCreateFromByteArray = 9;
    const int SlotBitmapCreateCopyFromBuffer = 9;
    const int SlotAsyncGetResults = 8;
    const int SlotInfoStatus = 7, SlotInfoCancel = 9, SlotInfoClose = 10;
    const int SlotVectorGetAt = 6, SlotVectorSize = 7;
    const int SlotFaceBox = 6;

    [StructLayout(LayoutKind.Sequential)]
    struct BitmapBounds { public uint X, Y, Width, Height; }

    /// <summary>The faces in the Gray8 image <paramref name="gray"/> (<paramref name="width"/> x <paramref name="height"/>, rows packed), as boxes in its
    /// pixels; empty when there are none or the detector cannot run within <paramref name="budget"/>. Worker thread.</summary>
    public static (float X, float Y, float W, float H)[] Find(byte[] gray, int width, int height, TimeSpan budget)
    {
        if (width <= 0 || height <= 0 || gray.Length < width * height) return [];
        CoInitializeEx(null, (uint)COINIT.COINIT_MULTITHREADED);   // S_FALSE / RPC_E_CHANGED_MODE: already initialised, fine for WinRT activation
        long deadline = Environment.TickCount64 + (long)budget.TotalMilliseconds;
        void* faceStatics = null, bufferStatics = null, bitmapStatics = null, detector = null, buffer = null, bitmap = null, faces = null;
        try
        {
            if (!Factory(FaceDetectorClass, IID_IFaceDetectorStatics, &faceStatics)) return [];
            byte supported = 0;
            if (Call(faceStatics, SlotFaceStaticsIsSupported, &supported) < 0 || supported == 0) return [];

            void* create = null;
            if (((delegate* unmanaged<void*, void**, int>)Slot(faceStatics, SlotFaceStaticsCreateAsync))(faceStatics, &create) < 0 || create is null) return [];
            if (!Await(create, deadline, &detector)) return [];

            if (!Factory(CryptographicBufferClass, IID_ICryptographicBufferStatics, &bufferStatics)) return [];
            fixed (byte* pixels = gray)
                if (((delegate* unmanaged<void*, uint, byte*, void**, int>)Slot(bufferStatics, SlotBufferCreateFromByteArray))(
                        bufferStatics, (uint)(width * height), pixels, &buffer) < 0 || buffer is null)
                    return [];
            if (!Factory(SoftwareBitmapClass, IID_ISoftwareBitmapStatics, &bitmapStatics)) return [];
            if (((delegate* unmanaged<void*, void*, int, int, int, void**, int>)Slot(bitmapStatics, SlotBitmapCreateCopyFromBuffer))(
                    bitmapStatics, buffer, Gray8, width, height, &bitmap) < 0 || bitmap is null)
                return [];

            void* detect = null;
            if (((delegate* unmanaged<void*, void*, void**, int>)Slot(detector, SlotFaceDetectFacesAsync))(detector, bitmap, &detect) < 0 || detect is null) return [];
            if (!Await(detect, deadline, &faces)) return [];

            uint count = 0;
            if (Call(faces, SlotVectorSize, &count) < 0 || count == 0) return [];
            var found = new (float X, float Y, float W, float H)[count];
            int n = 0;
            for (uint i = 0; i < count; i++)
            {
                void* face = null;
                if (((delegate* unmanaged<void*, uint, void**, int>)Slot(faces, SlotVectorGetAt))(faces, i, &face) < 0 || face is null) continue;
                BitmapBounds box;
                int hr = Call(face, SlotFaceBox, &box);
                Release(ref face);
                if (hr >= 0 && box.Width > 0 && box.Height > 0) found[n++] = (box.X, box.Y, box.Width, box.Height);
            }
            return n == found.Length ? found : found[..n];
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or SEHException) { return []; }
        finally
        {
            Release(ref faces);
            Release(ref bitmap);
            Release(ref buffer);
            Release(ref detector);
            Release(ref bitmapStatics);
            Release(ref bufferStatics);
            Release(ref faceStatics);
        }
    }

    /// <summary>The activation factory of <paramref name="runtimeClass"/> as <paramref name="iid"/>.</summary>
    static bool Factory(string runtimeClass, Guid iid, void** factory)
    {
        nint classId = 0;
        if (WindowsCreateString(runtimeClass, (uint)runtimeClass.Length, &classId) < 0 || classId == 0) return false;
        try { return RoGetActivationFactory(classId, &iid, factory) >= 0 && *factory != null; }
        finally { WindowsDeleteString(classId); }
    }

    /// <summary>Waits for the async operation <paramref name="op"/> (released here) until <paramref name="deadline"/>, then takes its result.</summary>
    static bool Await(void* op, long deadline, void** result)
    {
        void* info = null;
        try
        {
            Guid iid = IID_IAsyncInfo;
            if (((delegate* unmanaged<void*, Guid*, void**, int>)Slot(op, SlotQueryInterface))(op, &iid, &info) < 0 || info is null) return false;
            int status = Started;
            while (Call(info, SlotInfoStatus, &status) >= 0 && status == Started)
            {
                if (Environment.TickCount64 > deadline)
                {
                    ((delegate* unmanaged<void*, int>)Slot(info, SlotInfoCancel))(info);
                    return false;
                }
                Thread.Sleep(1);
            }
            if (status != Completed) return false;
            return ((delegate* unmanaged<void*, void**, int>)Slot(op, SlotAsyncGetResults))(op, result) >= 0 && *result != null;
        }
        finally
        {
            if (info != null) ((delegate* unmanaged<void*, int>)Slot(info, SlotInfoClose))(info);
            Release(ref info);
            Release(ref op);
        }
    }

    static void* Slot(void* obj, int slot) => (*(void***)obj)[slot];

    static int Call<T>(void* obj, int slot, T* value) where T : unmanaged
        => ((delegate* unmanaged<void*, T*, int>)Slot(obj, slot))(obj, value);

    static void Release(ref void* obj)
    {
        if (obj == null) return;
        ((delegate* unmanaged<void*, uint>)Slot(obj, SlotRelease))(obj);
        obj = null;
    }
}
