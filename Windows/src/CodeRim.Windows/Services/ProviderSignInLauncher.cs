using System.ComponentModel;
using System.Diagnostics;
using CodeRim.Core.Services;

namespace CodeRim.Windows.Services;

/// <summary>Opens the sign-in a <see cref="SignInPlan"/> describes. Never reads or stores a credential.</summary>
internal static class ProviderSignInLauncher
{
    internal static bool Launch(SignInPlan plan)
    {
        try
        {
            switch (plan.Kind)
            {
                case SignInKind.Browser when plan.Url is { Scheme: "https" } url:
                    using (Process.Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true })) { }
                    return true;
                case SignInKind.Terminal when plan.Command is { } command && ProviderSignIn.IsPlainCommand(command):
                    // The command is from a fixed table and only plain words; the title keeps letters and spaces.
                    var title = new string(plan.Name.Where(c => char.IsLetterOrDigit(c) || c == ' ').ToArray());
                    var script = $"Write-Host 'Signing in to {title} for CodeRim. When it finishes, return to CodeRim: it connects on its own.'; {command}";
                    using (Process.Start(new ProcessStartInfo("powershell.exe") { UseShellExecute = true,
                        ArgumentList = { "-NoLogo", "-NoExit", "-Command", script } })) { }
                    return true;
                default:
                    return false;
            }
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException or PlatformNotSupportedException) { return false; }
    }
}
