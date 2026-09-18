using Microsoft.Win32;

namespace CodeRim.Windows.Services;

internal static class StartupService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "CodeRim";

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
            ?? Registry.CurrentUser.CreateSubKey(RunKey, writable: true)
            ?? throw new InvalidOperationException("The Windows startup registry key is unavailable.");
        if (enabled)
        {
            var processPath = Environment.ProcessPath
                ?? throw new InvalidOperationException("The CodeRim executable path is unavailable.");
            var command = $"\"{processPath}\"";
            if (command.Length > 260)
            {
                throw new InvalidOperationException("The CodeRim executable path is too long for startup registration.");
            }
            key.SetValue(ValueName, command);
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
