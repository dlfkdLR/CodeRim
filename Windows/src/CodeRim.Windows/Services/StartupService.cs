using Microsoft.Win32;
using System.IO;

namespace CodeRim.Windows.Services;

internal static class StartupService
{
    internal const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    internal const string ApprovalKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    internal const string ValueName = "CodeRim";
    internal sealed record Registration(object? Value, RegistryValueKind Kind);
    internal sealed record Status(bool Enabled, bool CanChange, string Text, bool ShowSystemSettings = false);

    internal static Registration ReadRegistration()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        var value = key?.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        return new(value, value is null ? RegistryValueKind.String : key!.GetValueKind(ValueName));
    }

    internal static void Restore(Registration registration)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        if (registration.Value is null) key.DeleteValue(ValueName, throwOnMissingValue: false);
        else key.SetValue(ValueName, registration.Value, registration.Kind);
    }

    internal static Status ReadStatus()
    {
        try
        {
            var registration = ReadRegistration();
            if (registration.Value is null) return new(false, true, "Disabled");
            if (registration.Value is not string command || registration.Kind != RegistryValueKind.String
                || Environment.ProcessPath is not { } processPath || !string.Equals(command.Trim(), "\"" + processPath + "\"", StringComparison.OrdinalIgnoreCase))
                return new(false, true, "Registered to another installation", true);
            using var approval = Registry.CurrentUser.OpenSubKey(ApprovalKey);
            var value = approval?.GetValue(ValueName);
            // Read compatibility states only. Windows owns this approval value;
            // never erase or rewrite a user's Task Manager/Settings decision.
            return value switch
            {
                null => new(true, true, "Enabled"),
                byte[] { Length: >= 4 } bytes when bytes[0] == 2 && bytes[1] == 0 && bytes[2] == 0 && bytes[3] == 0 => new(true, true, "Enabled"),
                byte[] { Length: >= 4 } bytes when bytes[0] == 3 && bytes[1] == 0 && bytes[2] == 0 && bytes[3] == 0 => new(false, false, "Disabled in Windows Settings", true),
                _ => new(false, false, "Check Windows startup settings", true)
            };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { return new(false, false, "Startup status unavailable", true); }
    }

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
