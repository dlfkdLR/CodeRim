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
        Directory.CreateDirectory(directory);
        // Relative to the shipped CLI: no user-controlled path is inserted in
        // batch syntax, and reinstalling the app keeps this wrapper working.
        File.WriteAllText(Path.Combine(directory, "coderim.cmd"), "@echo off\r\n\"%~dp0..\\CodeRimCLI.exe\" %*\r\n");
        var current = readPath();
        var entries = current.Split(';', StringSplitOptions.RemoveEmptyEntries);
        var retained = entries.Where(x => !string.Equals(x.TrimEnd('\\'), directory.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
        writePath(string.Join(';', retained.Prepend(directory)));
        return directory;
    }
}
