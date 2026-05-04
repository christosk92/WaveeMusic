using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Wavee.AudioHost.PlayPlay;

// =============================================================================
// Wavee.PlayPlay.Tests — drives PlayPlayKeyEmulator (the merged AudioHost
// implementation) with the five known upstream test vectors and verifies the
// derived AES keys match.
//
// Post-merge: PlayPlayKeyEmulator lives in Wavee.AudioHost. The test exe is
// x64-only (LoadLibrary Spotify.dll happens in this same process); on ARM64
// hardware it runs under WoA emulation.
// =============================================================================

return Run(args);

static int Run(string[] args)
{
    _ = args;

    var spotifyDll = LocateSpotifyDll();
    if (spotifyDll is null)
    {
        Console.WriteLine("SKIP — No Spotify.dll v1.2.88.483 (x86_64, SHA-256 9CAFE…0CE8) found.");
        Console.WriteLine("       Drop a matching copy at one of:");
        Console.WriteLine("         %APPDATA%\\Spotify\\Spotify.dll");
        Console.WriteLine("         %LOCALAPPDATA%\\Wavee\\PlayPlay\\Spotify.dll");
        return 0;
    }
    Console.WriteLine($"Spotify.dll: {spotifyDll}");

    var vectors = new (string FileId, string Obf, string Expected)[]
    {
        ("2f43127d80edc9cd9f12f441e1cb7904b680f9da", "0694259138997536f3ddcf2be2855c8a", "a503a84c1dc9271460cc13f142e0bae2"),
        ("1a8e5b04837957617162724232b0c96922222447", "92f454b10dfeef9789f90932d423c244", "c3206271b4c70fff8e4ac3993c4dae8a"),
        ("cf1bd197a6f5d613fc856bd689e43c0f4069b800", "85d004ad2d1bc9ed12cf80e5db7cc41a", "8d86fb522c00729f35b34d60b165b922"),
        ("71df45edb4748a8b1bd4126ded06063674745182", "a42fbae85590b9a666e7ea6abfe49e1f", "0fd3998b706247b3474b2d3cf6d8e31f"),
        ("894813d0a3113c97ab601b0f65afb9543d96ec32", "6143db25b8035a2cfe5ece34345176d9", "4d442c155f9a95258f613a89be957be9"),
    };

    ILogger logger = NullLogger.Instance;

    var sw = Stopwatch.StartNew();
    PlayPlayKeyEmulator emu;
    try
    {
        emu = new PlayPlayKeyEmulator(spotifyDll, logger);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"FAIL — PlayPlayKeyEmulator init failed: {ex.Message}");
        return 3;
    }
    Console.WriteLine($"  emulator init in {sw.ElapsedMilliseconds} ms");

    using var emuOwned = emu;

    int failures = 0;
    foreach (var v in vectors)
    {
        var fileIdBytes = Convert.FromHexString(v.FileId);
        var contentId16 = new byte[16];
        Buffer.BlockCopy(fileIdBytes, 0, contentId16, 0, 16);
        var obf = Convert.FromHexString(v.Obf);

        sw.Restart();
        byte[] aes;
        try
        {
            aes = emu.DeriveAesKey(obf, contentId16);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"  ERR   {v.FileId[..8]}…  {ex.Message}");
            failures++;
            continue;
        }
        var elapsed = sw.ElapsedMilliseconds;

        var actual = Convert.ToHexString(aes).ToLowerInvariant();
        if (string.Equals(actual, v.Expected, StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine($"  PASS  {v.FileId[..8]}…  ({elapsed} ms)");
        }
        else
        {
            Console.Error.WriteLine($"  FAIL  {v.FileId[..8]}…  ({elapsed} ms)");
            Console.Error.WriteLine($"        expected={v.Expected}");
            Console.Error.WriteLine($"        actual  ={actual}");
            failures++;
        }
    }

    Console.WriteLine();
    if (failures == 0)
    {
        Console.WriteLine($"OK — {vectors.Length}/{vectors.Length} vectors passed.");
        return 0;
    }
    Console.Error.WriteLine($"FAIL — {failures}/{vectors.Length} vectors failed.");
    return 1;
}

static string? LocateSpotifyDll()
{
    var pinnedSha = "9CAFE1CAD176024485F8840B72F6747D5B87885B0423B1DF005ADF088EF80CE8";
    var candidates = new List<string>
    {
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Wavee", "PlayPlay", "Spotify.dll"),
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Spotify", "Spotify.dll"),
    };
    foreach (var c in candidates)
    {
        if (!File.Exists(c)) continue;
        try
        {
            var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(c)));
            if (string.Equals(hash, pinnedSha, StringComparison.OrdinalIgnoreCase))
                return c;
        }
        catch { }
    }
    return null;
}
