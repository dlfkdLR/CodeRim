using System.Runtime.InteropServices;

namespace CodeRim.Core.Services;

/// <summary>
/// Whether CodeRim runs from its Microsoft Store (MSIX) package. The Store installs into a protected
/// WindowsApps folder, updates the app itself, and reaches the CLI through execution aliases, so a
/// few behaviours differ from the MSI installation.
/// </summary>
public static class PackagedApp
{
    /// <summary>The application id in the package manifest; the Run entry starts it by this name.</summary>
    public const string ApplicationId = "CodeRim";
    /// <summary>The CLI alias name. It matches the MSI file name so Claude integration recognises both.</summary>
    public const string CliAlias = "CodeRimCLI.exe";

    private static readonly Lazy<string?> family = new(ReadFamilyName);

    /// <summary>The package family name, or null outside a package.</summary>
    public static string? FamilyName => family.Value;
    public static bool IsPackaged => FamilyName is not null;

    /// <summary>The per-user execution alias Windows creates for a packaged command, reachable from any process.</summary>
    public static string AliasPath(string alias) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", alias);

    /// <summary>The command that starts the packaged app at sign-in; a package's own files cannot be launched by path.</summary>
    public static string? StartupCommand => FamilyName is { } name ? $"explorer.exe shell:AppsFolder\\{name}!{ApplicationId}" : null;

    private static string? ReadFamilyName()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(8)) return null;
        try
        {
            var length = 0;
            // APPMODEL_ERROR_NO_PACKAGE (15700) means an ordinary, unpackaged process.
            if (GetCurrentPackageFamilyName(ref length, null) != 122 /* ERROR_INSUFFICIENT_BUFFER */ || length is <= 0 or > 256) return null;
            var buffer = new char[length];
            return GetCurrentPackageFamilyName(ref length, buffer) == 0 ? new string(buffer, 0, Math.Max(0, length - 1)) : null;
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException) { return null; }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int GetCurrentPackageFamilyName(ref int packageFamilyNameLength, [Out] char[]? packageFamilyName);
}
