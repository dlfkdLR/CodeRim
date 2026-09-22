using System.IO;
using CodeRim.Core.Services;

namespace CodeRim.Windows.Services;
internal static class CliInstaller
{
    internal static string Install() => Install(
        () => Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.User) ?? "",
        value => Environment.SetEnvironmentVariable("Path", value, EnvironmentVariableTarget.User));

    internal static string Install(Func<string> readPath, Action<string> writePath)
    {
        var source = Path.Combine(AppContext.BaseDirectory, "CodeRimCLI.exe");
        if (!File.Exists(source)) throw new FileNotFoundException("Use the complete Windows release package.");
        var directory = Path.Combine(AppContext.BaseDirectory, "bin");
        // Relative to the shipped CLI: no user-controlled path is inserted in
        // batch syntax, and reinstalling the app keeps this wrapper working.
        var wrapper = Path.Combine(directory, "coderim.cmd");
        const string expected = "@echo off\n\"%~dp0..\\CodeRimCLI.exe\" %*\n";
        var alreadyShipped = File.Exists(wrapper) && File.ReadAllText(wrapper).Replace("\r\n", "\n", StringComparison.Ordinal) == expected;
        if (!alreadyShipped)
        {
            if (File.Exists(Path.Combine(AppContext.BaseDirectory, ".coderim-install.json")))
                throw new IOException("The managed CLI wrapper needs repair from the complete signed package.");
            Directory.CreateDirectory(directory);
            File.WriteAllText(wrapper, expected.Replace("\n", "\r\n", StringComparison.Ordinal));
        }
        var current = readPath();
        var entries = current.Split(';', StringSplitOptions.RemoveEmptyEntries);
        var retained = entries.Where(x => !string.Equals(x.TrimEnd('\\'), directory.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
        writePath(string.Join(';', retained.Prepend(directory)));
        return directory;
    }
}
