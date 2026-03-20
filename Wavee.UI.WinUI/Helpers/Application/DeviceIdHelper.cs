using System;
using System.IO;

namespace Wavee.UI.WinUI.Helpers.Application;

internal static class DeviceIdHelper
{
    public static string GetOrCreateDeviceId()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Wavee");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "device_id.txt");
        if (File.Exists(path))
        {
            var existing = File.ReadAllText(path).Trim();
            if (!string.IsNullOrEmpty(existing)) return existing;
        }
        var id = Guid.NewGuid().ToString("N");
        File.WriteAllText(path, id);
        return id;
    }
}
