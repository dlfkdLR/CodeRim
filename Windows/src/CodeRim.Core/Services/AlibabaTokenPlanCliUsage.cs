using System.Collections;
using System.Text;
using System.Text.Json;
using CodeRim.Core.Domain;

namespace CodeRim.Core.Services;

/// <summary>Read-only Bailian CLI quota. The output does not identify its active account.</summary>
public static class AlibabaTokenPlanCliUsage
{
    public static string? Source(string? value) => value?.Trim().ToLowerInvariant() switch
    { null or "" or "auto" => "auto", "cli" => "cli", "web" => "web", _ => null };
    public static string? Region(string? value) => value?.Trim().ToLowerInvariant() switch
    { null or "" or "intl" => "intl", "cn" => "cn", "intl-personal" => "intl-personal", "cn-personal" => "cn-personal", _ => null };
    public static string[] Arguments(string region)
    {
        var selected = Region(region) ?? throw new ArgumentException("Select a Token Plan region.", nameof(region));
        var china = selected.StartsWith("cn", StringComparison.Ordinal);
        return ["usage", "token-plan", "--console-region", china ? "cn-beijing" : "ap-southeast-1",
            "--console-site", china ? "domestic" : "international", "--output", "json"];
    }
    private static readonly HashSet<string> EnvironmentKeys = new(StringComparer.OrdinalIgnoreCase)
    { "PATH", "HOME", "LANG", "LC_ALL", "LC_CTYPE", "TZ", "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "NO_PROXY",
        "SystemRoot", "WINDIR", "USERPROFILE", "APPDATA", "LOCALAPPDATA", "TEMP", "TMP" };
    public static IReadOnlyDictionary<string, string?> ChildEnvironment(IDictionary environment)
    {
        var result = new Dictionary<string, string?>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (DictionaryEntry pair in environment)
            if (pair.Key is string key && pair.Value is string value && EnvironmentKeys.Contains(key)) result[key] = value;
        result["NO_COLOR"] = "1";
        return result;
    }
    public static string? ResolveExecutable(string? configured, string? path, string? localApplicationData)
    {
        if (!string.IsNullOrWhiteSpace(configured)) return CanonicalExecutable(configured.Trim());
        if (!string.IsNullOrWhiteSpace(localApplicationData) && Path.IsPathFullyQualified(localApplicationData)
            && CanonicalExecutable(Path.Combine(localApplicationData, "bailian-cli", "bin", "bl.exe")) is { } installed) return installed;
        foreach (var directory in (path ?? "").Split(Path.PathSeparator))
        {
            var absolute = directory.Trim().Trim('"');
            if (!Path.IsPathFullyQualified(absolute)) continue;
            if (CanonicalExecutable(Path.Combine(absolute, "bl.exe")) is { } executable) return executable;
        }
        return null;
    }
    private static string? CanonicalExecutable(string path)
    {
        try
        {
            if (!Path.IsPathFullyQualified(path) || !Path.GetFileName(path).Equals("bl.exe", StringComparison.OrdinalIgnoreCase)) return null;
            path = Path.GetFullPath(path);
            // Official installations use bin/current junctions. Bind the resolved target,
            // including parent links, so changing an installed version changes our scope.
            for (var links = 0; links < 32; links++)
            {
                var root = Path.GetPathRoot(path)!; var current = root; var redirected = false;
                var parts = path[root.Length..].Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                for (var i = 0; i < parts.Length; i++)
                {
                    current = Path.Combine(current, parts[i]);
                    FileSystemInfo info = i == parts.Length - 1 ? new FileInfo(current) : new DirectoryInfo(current);
                    if (!info.Exists) return null;
                    if ((info.Attributes & FileAttributes.ReparsePoint) == 0) continue;
                    var target = info.ResolveLinkTarget(true)?.FullName ?? throw new IOException("The selected CLI link could not be resolved.");
                    path = Path.GetFullPath(Path.Combine([target, ..parts.Skip(i + 1)]));
                    redirected = true; break;
                }
                if (!redirected) return File.Exists(current) ? current : null;
            }
            return null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { return null; }
    }
    public static async Task<ProviderReading> ReadAsync(string executable, string region, CancellationToken cancellationToken = default)
    {
        var arguments = Arguments(region);
        try
        {
            var result = await BoundedProcess.RunIsolatedResultAsync(executable, arguments,
                ChildEnvironment(Environment.GetEnvironmentVariables()), TimeSpan.FromSeconds(15), 65536, cancellationToken).ConfigureAwait(false);
            return Parse(result);
        }
        catch (OperationCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Failure("The Bailian usage command timed out. Check its sign-in and retry.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or JsonException or DecoderFallbackException)
        { return Failure("The Bailian usage command could not run. Check the selected bl.exe and its sign-in."); }
    }
    public static ProviderReading Parse(ProcessResult result)
    {
        if (result.ExitCode != 0) return Failure("The Bailian usage command failed. Sign in with bl login, then refresh.");
        if (Encoding.UTF8.GetByteCount(result.Output) > 65536 || Encoding.UTF8.GetByteCount(result.Error) > 65536)
            return Failure("The Bailian usage output exceeded its limit.");
        try
        {
            using var document = JsonDocument.Parse(result.Output, new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return Failure("Bailian returned no recognized quota.");
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in root.EnumerateObject())
                if (!names.Add(field.Name)) return Failure("Bailian returned ambiguous quota fields.");
            var windows = new List<LimitWindow>();
            void Add(string prefix, string id, string name, int duration)
            {
                if (!root.TryGetProperty(prefix + "Percentage", out var element) || element.ValueKind != JsonValueKind.Number
                    || !element.TryGetDouble(out var ratio) || !double.IsFinite(ratio) || ratio < 0 || ratio > 1) return;
                DateTimeOffset? reset = null;
                if (root.TryGetProperty(prefix + "ResetTime", out var date) && date.ValueKind == JsonValueKind.Number
                    && date.TryGetDouble(out var milliseconds) && double.IsFinite(milliseconds) && milliseconds > 0 && milliseconds <= 253402300799999)
                    reset = DateTimeOffset.FromUnixTimeMilliseconds((long)milliseconds);
                windows.Add(new(id, name, ratio * 100, reset, duration));
            }
            Add("per5Hour", "five-hour", "5-hour limit", 300);
            Add("per1Week", "weekly", "Weekly limit", 10080);
            return windows.Count == 0 ? Failure("Bailian returned no recognized quota.")
                : new("alibabatokenplan", ReadingState.Ready, windows, DateTimeOffset.UtcNow, "Source: CLI · active Bailian sign-in", "Token Plan");
        }
        catch (JsonException) { return Failure("The Bailian quota response could not be read."); }
    }
    private static ProviderReading Failure(string message) => new("alibabatokenplan", ReadingState.Error, [], Message: message);
}
