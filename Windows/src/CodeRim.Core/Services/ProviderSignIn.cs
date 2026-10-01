using System.Text.RegularExpressions;
using CodeRim.Core.Domain;

namespace CodeRim.Core.Services;

public enum SignInKind
{
    /// <summary>A terminal window runs the provider's own login command.</summary>
    Terminal,
    /// <summary>The provider's login or key page opens in the default browser.</summary>
    Browser,
    /// <summary>An API key or cookie is entered in the provider's settings page.</summary>
    Settings,
}

public sealed record SignInPlan(SignInKind Kind, string Name, string? Command, Uri? Url, string Note)
{
    public bool OpensSettings => Kind == SignInKind.Settings;
}

/// <summary>
/// How adding a provider gets the user signed in, so there is no second "Set up" step:
/// a tool with a login command runs it, a website session opens its login page, and a
/// key goes to the provider's settings. Nothing here reads or stores a credential.
/// </summary>
public static class ProviderSignIn
{
    // Fixed commands only; nothing from the user or a provider is ever placed in a command line.
    private static readonly Dictionary<string, (string Command, string Note)> Terminal = new(StringComparer.Ordinal)
    {
        ["codex"] = ("codex login", "A terminal window runs `codex login`. Finish it in the browser and CodeRim connects on its own."),
        ["copilot"] = ("gh auth login --web", "A terminal window opens GitHub's sign-in: copy the code, approve it in the browser, and CodeRim connects on its own."),
        ["cursor"] = ("cursor-agent login", "A terminal window runs `cursor-agent login`. Finish it in the browser and CodeRim connects on its own."),
        ["grok"] = ("grok login", "A terminal window runs `grok login`. Finish it in the browser and CodeRim connects on its own."),
        ["opencode"] = ("opencode auth login", "A terminal window runs `opencode auth login`. Choose OpenCode Go there and CodeRim connects on its own."),
        ["gemini-cli"] = ("gemini", "A terminal window starts the Gemini CLI. Sign in with Google there and CodeRim connects on its own."),
        ["vertexai"] = ("gcloud auth application-default login", "A terminal window runs Google Cloud's sign-in. Finish it in the browser and CodeRim connects on its own."),
    };
    private static readonly Dictionary<string, (string Url, string Note)> Browser = new(StringComparer.Ordinal)
    {
        ["gemini"] = ("https://antigravity.google", "Install Antigravity and sign in with your Google account. CodeRim connects on its own."),
        ["glm"] = ("https://z.ai/manage-apikey/apikey-list", "Create a GLM Coding Plan key on Z.ai and add it to Claude Code's settings.json, ZCode or OpenCode. CodeRim detects it there."),
        ["ollama"] = ("https://ollama.com/settings/keys", "Create a key on ollama.com, then set OLLAMA_API_KEY. CodeRim detects it."),
    };
    private static readonly Regex CookieKey = new("COOKIE|SESSION", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SecretKey = new("KEY|TOKEN|SECRET|PASSWORD", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static SignInPlan For(string id)
    {
        var definition = ProviderCatalog.Find(id);
        var name = definition?.Name ?? id;
        if (Terminal.TryGetValue(id, out var terminal))
            return new(SignInKind.Terminal, name, terminal.Command, null, terminal.Note);
        if (Browser.TryGetValue(id, out var page))
            return new(SignInKind.Browser, name, null, new Uri(page.Url), page.Note);
        var keys = definition?.EnvironmentKeys ?? [];
        var url = ProviderAccountLinks.UsagePage(id);
        if (url is not null && (keys.Any(CookieKey.IsMatch) || !keys.Any(SecretKey.IsMatch)))
            return new(SignInKind.Browser, name, null, url, $"Sign in on the {name} website in your browser. CodeRim reads the signed-in session and connects on its own.");
        return new(SignInKind.Settings, name, null, null, $"Enter your {name} key or session in its settings. {definition?.Summary}".Trim());
    }

    /// <summary>The login command is safe to hand to a shell: plain words, dashes and dots only.</summary>
    public static bool IsPlainCommand(string command) =>
        command.Length is > 0 and <= 80 && command.All(c => char.IsAsciiLetterOrDigit(c) || c is ' ' or '-' or '_' or '.' or '/');
}

/// <summary>
/// Adding a provider is connecting it. Read what is already there; otherwise start the
/// sign-in once and keep watching until the account appears.
/// </summary>
public sealed class ProviderConnector
{
    public enum Phase { Checking, Waiting, NeedsKey, Connected, Failed }
    public sealed record State(Phase Phase, string Message);

    private readonly Func<string, Task<bool>> connects;
    private readonly Func<SignInPlan, bool> launch;
    private readonly TimeSpan poll, patience;
    private readonly Dictionary<string, State> states = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CancellationTokenSource> running = new(StringComparer.Ordinal);
    private readonly object gate = new();
    public event Action? Changed;

    public ProviderConnector(Func<string, Task<bool>> connects, Func<SignInPlan, bool> launch, TimeSpan? poll = null, TimeSpan? patience = null)
    {
        this.connects = connects; this.launch = launch;
        this.poll = poll ?? TimeSpan.FromSeconds(4); this.patience = patience ?? TimeSpan.FromMinutes(10);
    }

    public State? StateOf(string id) { lock (gate) return states.GetValueOrDefault(id); }

    public void Cancel(string id)
    {
        lock (gate) { if (running.Remove(id, out var cancellation)) cancellation.Cancel(); states.Remove(id); }
        Changed?.Invoke();
    }

    public void Begin(string id) => _ = RunAsync(id);

    public async Task RunAsync(string id)
    {
        CancellationTokenSource cancellation = new();
        lock (gate) { if (running.Remove(id, out var previous)) previous.Cancel(); running[id] = cancellation; }
        var token = cancellation.Token;
        void Set(Phase phase, string message = "")
        {
            lock (gate) { if (token.IsCancellationRequested) return; states[id] = new(phase, message); }
            Changed?.Invoke();
        }
        try
        {
            Set(Phase.Checking);
            if (await connects(id)) { Set(Phase.Connected); return; }
            token.ThrowIfCancellationRequested();
            var plan = ProviderSignIn.For(id);
            if (plan.OpensSettings) { Set(Phase.NeedsKey, plan.Note); return; }
            if (!launch(plan)) { Set(Phase.Failed, plan.Note + " It could not be opened automatically."); return; }
            Set(Phase.Waiting, plan.Note);
            var deadline = DateTime.UtcNow + patience;
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(poll, token);
                if (await connects(id)) { Set(Phase.Connected); return; }
            }
            Set(Phase.Failed, "Sign-in was not detected. Choose Sign in to try again.");
        }
        catch (OperationCanceledException) { }
        finally { lock (gate) { if (running.GetValueOrDefault(id) == cancellation) running.Remove(id); } cancellation.Dispose(); }
    }
}
