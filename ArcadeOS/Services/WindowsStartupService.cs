using System.IO;
using Microsoft.Win32;
namespace ArcadeOS.Services;

public sealed class WindowsStartupService(string executablePath, string registryPath = @"Software\Microsoft\Windows\CurrentVersion\Run")
{
    private const string ValueName = "Arkade";
    private string Command => "\"" + Path.GetFullPath(executablePath) + "\"";
    public bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(registryPath);
            return string.Equals(key?.GetValue(ValueName) as string, Command, StringComparison.OrdinalIgnoreCase);
        }
    }
    public void SetEnabled(bool enabled)
    {
        if (enabled)
        {
            if (!File.Exists(executablePath) || !Path.GetExtension(executablePath).Equals(".exe", StringComparison.OrdinalIgnoreCase))
                throw new FileNotFoundException("The installed Arkade executable could not be found.", executablePath);
            using var key = Registry.CurrentUser.CreateSubKey(registryPath, true)
                ?? throw new IOException("Windows startup preferences could not be opened.");
            key.SetValue(ValueName, Command, RegistryValueKind.String);
        }
        else
        {
            using var key = Registry.CurrentUser.OpenSubKey(registryPath, true);
            key?.DeleteValue(ValueName, false);
        }
    }
}
