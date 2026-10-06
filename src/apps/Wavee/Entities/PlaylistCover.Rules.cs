// ── Entities/PlaylistCover.Rules.cs ──────────────────────────────────────────────────────────────────────────────────
// CORE, PURE: the playlist cover's rules (#155) — which files are offered and accepted, the square crop, the EXIF turn,
// the JPEG budget ladder, the register-image answer and the cover's CDN url, and the file picker's single-flight gate
// and watchdog verdict. No engine, no WIC, no network, no clock: the WIC codec (Platform/CoverImage.cs), the STA picker
// (Platform/ModalFilePicker.cs) and the flow (Entities/PlaylistCover.cs) are the impure halves, and
// Wavee.Tests/PlaylistCoverTests.cs pins this file.

using System.Text.Json;

namespace Wavee;

/// <summary>Why a picked file is not a cover. <see cref="None"/> = go ahead.</summary>
public enum CoverProblem : byte { None = 0, Unsupported, Missing, TooLarge, TooSmall, Unreadable }

/// <summary>An encoded cover: the JPEG and the edge / quality the budget ladder settled on.</summary>
public readonly record struct CoverEncoding(byte[] Jpeg, int Edge, int Quality);

public static class PlaylistCoverRules
{
    /// <summary>The upload budget. Spotify's documented playlist-image limit is 256 KB of JPEG; the desktop's private
    /// image service took more in the one capture, but nothing is gained by testing that edge.</summary>
    public const int MaxUploadBytes = 256 * 1024;

    /// <summary>The square the cover is drawn to — Spotify's largest playlist rendition.</summary>
    public const int TargetEdge = 640;

    /// <summary>Below this the source is an icon, not a cover.</summary>
    public const int MinSourceEdge = 64;

    /// <summary>A file this large is refused before it is read (the encoded cover is ≤ 256 KB whatever goes in).</summary>
    public const long MaxSourceFileBytes = 40L * 1024 * 1024;

    /// <summary>The edges tried, largest first, below the first fit (<see cref="Edges"/>).</summary>
    public static readonly int[] EdgeSteps = [640, 512, 400, 300];

    /// <summary>The JPEG qualities tried at each edge, best first.</summary>
    public static readonly int[] QualityLadder = [90, 84, 78, 72, 66, 60, 52, 45];

    /// <summary>What the picker offers and the drop accepts: the formats Windows Imaging Component decodes in the box.</summary>
    public static readonly string[] Extensions = [".jpg", ".jpeg", ".jfif", ".png", ".bmp", ".gif", ".webp"];

    /// <summary>The picker's filter spec for <see cref="Extensions"/>.</summary>
    public const string PickerSpec = "*.jpg;*.jpeg;*.jfif;*.png;*.bmp;*.gif;*.webp";

    public static bool IsSupportedPath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        string ext = Path.GetExtension(path);
        foreach (string e in Extensions)
            if (ext.Equals(e, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>The checks a path can fail before any pixel is read: <paramref name="length"/> is the file's size, -1
    /// when it does not exist.</summary>
    public static CoverProblem CheckFile(string? path, long length)
    {
        if (!IsSupportedPath(path)) return CoverProblem.Unsupported;
        if (length < 0) return CoverProblem.Missing;
        if (length == 0) return CoverProblem.Unreadable;
        if (length > MaxSourceFileBytes) return CoverProblem.TooLarge;
        return CoverProblem.None;
    }

    /// <summary>The centred square of a <paramref name="width"/> × <paramref name="height"/> image (a cover is square;
    /// Spotify crops a non-square upload anyway, so the crop the user sees is the crop that ships).</summary>
    public static (int X, int Y, int Side) CenterSquare(int width, int height)
    {
        int side = Math.Min(width, height);
        return ((width - side) / 2, (height - side) / 2, side);
    }

    /// <summary>The edges to encode at for a source square of <paramref name="side"/>: never upscaled, then the steps
    /// below it. Empty when the source is too small to be a cover.</summary>
    public static int[] Edges(int side)
    {
        if (side < MinSourceEdge) return [];
        int first = Math.Min(side, TargetEdge);
        var edges = new List<int>(EdgeSteps.Length + 1) { first };
        foreach (int e in EdgeSteps)
            if (e < first) edges.Add(e);
        return [.. edges];
    }

    /// <summary>THE BUDGET LADDER: the first (edge, quality) whose JPEG fits <see cref="MaxUploadBytes"/>, largest edge
    /// and best quality first. <paramref name="encode"/> is the codec (edge, quality) → bytes, null when it failed —
    /// which ends the ladder (an encoder that cannot encode will not succeed at a smaller size). Null when nothing fits.</summary>
    public static CoverEncoding? Fit(int side, Func<int, int, byte[]?> encode)
    {
        foreach (int edge in Edges(side))
            foreach (int quality in QualityLadder)
            {
                byte[]? jpeg = encode(edge, quality);
                if (jpeg is null) return null;
                if (jpeg.Length <= MaxUploadBytes) return new CoverEncoding(jpeg, edge, quality);
            }
        return null;
    }

    /// <summary>The EXIF turn, applied to a SQUARE <paramref name="edge"/>² BGRA buffer. A centred square crop commutes
    /// with every orientation (the centre square of the turned image is the turned centre square), so the crop and the
    /// scale happen on the stored pixels and only the small square turns. 1 (or anything unknown) leaves it as is.</summary>
    public static void Orient(Span<byte> bgra, int edge, int orientation)
    {
        if (orientation is < 2 or > 8 || edge <= 1) return;
        int n = edge - 1;
        byte[] src = bgra[..(edge * edge * 4)].ToArray();
        for (int y = 0; y < edge; y++)
            for (int x = 0; x < edge; x++)
            {
                // The displayed pixel (x, y) is the stored pixel (sx, sy).
                (int sx, int sy) = orientation switch
                {
                    2 => (n - x, y),          // mirrored horizontally
                    3 => (n - x, n - y),      // turned 180°
                    4 => (x, n - y),          // mirrored vertically
                    5 => (y, x),              // transposed
                    6 => (y, n - x),          // turn 90° clockwise to view
                    7 => (n - y, n - x),      // transversed
                    _ => (n - y, x),          // 8: turn 90° counter-clockwise to view
                };
                src.AsSpan((sy * edge + sx) * 4, 4).CopyTo(bgra.Slice((y * edge + x) * 4, 4));
            }
    }

    /// <summary>Drop the alpha of a premultiplied BGRA buffer into 24-bit BGR — i.e. a transparent image lands on black,
    /// the dark surface every cover sits on.</summary>
    public static void BgraToBgr(ReadOnlySpan<byte> bgra, Span<byte> bgr)
    {
        int pixels = Math.Min(bgra.Length / 4, bgr.Length / 3);
        for (int i = 0; i < pixels; i++)
        {
            bgr[i * 3] = bgra[i * 4];
            bgr[i * 3 + 1] = bgra[i * 4 + 1];
            bgr[i * 3 + 2] = bgra[i * 4 + 2];
        }
    }

    /// <summary>Hop two's answer, <c>{"picture":"&lt;base64&gt;"}</c> → the picture id bytes (20 in every capture), or
    /// null when the body is not that shape.</summary>
    public static byte[]? RegisteredPicture(byte[]? body)
    {
        if (body is not { Length: > 0 }) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("picture", out var v) || v.ValueKind != JsonValueKind.String)
                return null;
            byte[] picture = Convert.FromBase64String(v.GetString() ?? "");
            return picture.Length is > 0 and <= 64 ? picture : null;
        }
        catch (JsonException) { return null; }
        catch (FormatException) { return null; }
    }

    /// <summary>The cover's url from its picture id — the same spelling the playlist decode gives attribute 3
    /// (<c>Spotify.Decode</c>'s <c>Image</c>), so the next server read lands on the very url the edit wrote.</summary>
    public static string CdnUrlOf(ReadOnlySpan<byte> picture) => "https://i.scdn.co/image/" + Convert.ToHexStringLower(picture);

    /// <summary>Does the playlist carry a cover of its OWN (something "Remove cover" would take away)? A cover-less
    /// playlist reads as nothing or as a server mosaic of its first albums.</summary>
    public static bool IsOwnCover(string? image)
        => image is { Length: > 0 } && !image.Contains("mosaic", StringComparison.OrdinalIgnoreCase);

    // ── the picker's threading contract (Platform/ModalFilePicker.cs) ─────────────────────────────────────────────

    /// <summary>How long a picker may stay invisible before the owner window is handed back (see <see cref="Watch"/>).</summary>
    public const int PickerWatchdogMs = 8000;

    public enum PickerVerdict : byte { Done = 0, Wait, ReleaseOwner }

    /// <summary>THE WATCHDOG (the #155 symptom, made survivable). <c>IModalWindow.Show</c> disables the owner FIRST and
    /// shows the dialog only once the shell view behind it has navigated; when that stalls, the window is dead to every
    /// click (the "ding") with nothing on screen. The picker now runs on its own thread, so Wavee keeps drawing — and
    /// once the dialog has stayed invisible for <see cref="PickerWatchdogMs"/>, the owner is enabled again.</summary>
    public static PickerVerdict Watch(bool pickerOpen, bool dialogVisible, bool ownerEnabled, long elapsedMs)
    {
        if (!pickerOpen || dialogVisible) return PickerVerdict.Done;
        if (elapsedMs < PickerWatchdogMs) return PickerVerdict.Wait;
        return ownerEnabled ? PickerVerdict.Done : PickerVerdict.ReleaseOwner;
    }
}

/// <summary>ONE picker at a time, process-wide: a second "Change cover" while a dialog is up (or still coming up)
/// must not stack another modal on the first. Thread-safe; held from the click until the dialog thread returns.</summary>
public sealed class PickerGate
{
    int _held;

    public bool IsHeld => Volatile.Read(ref _held) != 0;

    public bool TryEnter() => Interlocked.CompareExchange(ref _held, 1, 0) == 0;

    public void Exit() => Volatile.Write(ref _held, 0);
}
