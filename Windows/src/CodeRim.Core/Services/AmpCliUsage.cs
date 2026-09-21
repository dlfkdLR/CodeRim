using System.Text.Json;
using System.Text.RegularExpressions;
using CodeRim.Core.Domain;
using CodeRim.Core.Providers;

namespace CodeRim.Core.Services;
public static class AmpCliUsage
{
    private static readonly string[] AuthenticationMessages = ["not logged in", "not authenticated", "please sign in", "please log in", "ampcode.com/login", "run amp login"];
    public static string? Source(string? value) => value?.Trim().ToLowerInvariant() switch
    { null or "" or "api" => "api", "cli" => "cli", "web" => "web", _ => null };
    public static async Task<ProviderReading> ReadAsync(string executable, CancellationToken cancellationToken = default)
    {
        var result = await BoundedProcess.RunResultAsync(executable, ["usage"], timeout: TimeSpan.FromSeconds(15),
            maximumBytes: 262144, environment: new Dictionary<string, string?> { ["NO_COLOR"] = "1", ["AMP_API_KEY"] = null },
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return Parse(result);
    }
    public static ProviderReading Parse(ProcessResult result)
    {
        try { return ParseOutput(result); }
        catch (Exception error) when (error is InvalidDataException or JsonException or RegexMatchTimeoutException)
        { return new("amp", ReadingState.Error, [], Message: "The Amp usage response could not be read."); }
    }
    private static ProviderReading ParseOutput(ProcessResult result)
    {
        var raw = string.IsNullOrWhiteSpace(result.Output) ? result.Error : result.Output;
        if (raw.Length > 131072 || result.Error.Length > 131072)
            return new("amp", ReadingState.Error, [], Message: "Amp returned more output than expected.");
        if (result.ExitCode == 0)
        {
            var data = JsonSerializer.SerializeToElement(new { ok = true, result = new { displayText = raw } });
            var reading = NativeProviders.Parse("amp", new Dictionary<string, JsonElement> { ["main"] = data });
            if (reading.Windows.Count > 0) return reading;
        }
        var message = Regex.Replace(raw + "\n" + result.Error, @"\x1B\[[0-?]*[ -/]*[@-~]", "", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200)).Replace(((char)96).ToString(), "", StringComparison.Ordinal);
        if (AuthenticationMessages.Any(marker => message.Contains(marker, StringComparison.OrdinalIgnoreCase)))
            return new("amp", ReadingState.NeedsAuth, [], Message: "Sign in with amp login, then refresh.");
        return new("amp", result.ExitCode == 0 ? ReadingState.Unavailable : ReadingState.Error, [],
            Message: result.ExitCode == 0 ? "The Amp CLI returned no recognized usage data." : "The Amp usage command failed. Check Amp and refresh.");
    }
}
