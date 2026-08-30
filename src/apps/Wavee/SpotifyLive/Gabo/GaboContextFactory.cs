using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using Wavee.SpotifyLive.Gabo;

namespace Wavee.SpotifyLive;

public static class GaboContextFactory
{
    const string ClientIdHex = "65b708073fc0480ea92a077233ca87bd";

    public static GaboContext Create(byte[]? installationId = null, byte[]? appSessionId = null)
    {
        installationId ??= LoadOrCreateInstallationId();
        appSessionId ??= RandomNumberGenerator.GetBytes(16);
        return new GaboContext(
            ClientIdBytes: Convert.FromHexString(ClientIdHex),
            InstallationIdBytes: installationId,
            AppSessionIdBytes: appSessionId,
            AppVersionString: SpotifyClientIdentity.DesktopSemver,
            AppVersionCode: long.Parse(SpotifyClientIdentity.AppVersionHeader, System.Globalization.CultureInfo.InvariantCulture),
            PlatformType: "windows",
            DeviceManufacturer: "Microsoft Corporation",
            DeviceModel: "PC laptop",
            DeviceIdString: GetMachineSid(),
            OsVersion: GetOsVersion());
    }

    static byte[] LoadOrCreateInstallationId()
    {
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Wavee", "gabo_installation_id");
            if (File.Exists(path))
            {
                var hex = File.ReadAllText(path).Trim();
                if (hex.Length == 32) return Convert.FromHexString(hex);
            }
            var id = RandomNumberGenerator.GetBytes(16);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, Convert.ToHexString(id).ToLowerInvariant());
            return id;
        }
        catch { return RandomNumberGenerator.GetBytes(16); }
    }

    static string GetMachineSid()
    {
        if (!OperatingSystem.IsWindows()) return "S-1-5-21-0-0-0-0";
        try
        {
            var nt = WindowsIdentity.GetCurrent().User;
            return nt?.Value ?? "S-1-5-21-0-0-0-0";
        }
        catch { return "S-1-5-21-0-0-0-0"; }
    }

    static string GetOsVersion()
    {
        var v = Environment.OSVersion.Version;
        return $"{v.Major}.{v.Minor}.{v.Build}";
    }
}
