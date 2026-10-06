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
                    using (Process.Start(new ProcessStartInfo("powershell.exe") { UseShellExecute = true,
                        // npm installs these CLIs as .ps1 shims, which the default Windows 11 policy refuses to run;
                        // the bypass applies to this window only.
                        ArgumentList = { "-NoLogo", "-NoExit", "-ExecutionPolicy", "Bypass", "-Command", TerminalScript(title, command, plan.Hint) } })) { }
                    return true;
                default:
                    return false;
            }
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException or PlatformNotSupportedException) { return false; }
    }

    /// <summary>The terminal script: highlighted steps, a pause so they are read, the command, then how it ended.</summary>
    internal static string TerminalScript(string title, string command, string hint)
    {
        var script = new System.Text.StringBuilder();
        // Pick up tools installed after CodeRim started: a new terminal reads the stored PATH, not CodeRim's copy.
        script.Append("$env:Path = [Environment]::GetEnvironmentVariable('Path','Machine') + ';' + [Environment]::GetEnvironmentVariable('Path','User') + ';' + $env:Path; ");
        script.Append(System.Globalization.CultureInfo.InvariantCulture, $"Write-Host 'Signing in to {title} for CodeRim. When it finishes, return to CodeRim: it connects on its own.'; ");
        // Hint text is fixed in CodeRim; keep it to characters that cannot leave a single-quoted string anyway.
        var lines = hint.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => new string(line.Where(c => char.IsLetterOrDigit(c) || " .,:/-".Contains(c)).ToArray()).Trim())
            .Where(line => line.Length > 0).ToArray();
        if (lines.Length > 0)
        {
            script.Append("Write-Host ''; Write-Host '=== DO THIS IN THIS WINDOW ===' -ForegroundColor Yellow; ");
            foreach (var line in lines) script.Append(System.Globalization.CultureInfo.InvariantCulture, $"Write-Host '{line}' -ForegroundColor Yellow; ");
            script.Append("Write-Host ''; [void](Read-Host 'Press Enter to start'); ");
        }
        script.Append(System.Globalization.CultureInfo.InvariantCulture, $"Write-Host ''; {command}; $code = $LASTEXITCODE; Write-Host ''; ");
        script.Append("if ($code -eq 0 -or $null -eq $code) { Write-Host 'Done. Return to CodeRim: it connects on its own.' } ");
        script.Append("else { Write-Host \"That did not finish (exit $code). Return to CodeRim and choose Try again.\" }");
        return script.ToString();
    }
}
