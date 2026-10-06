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
    /// <summary>CodeRim runs the sign-in itself: a GitHub device code or a Google consent.</summary>
    InApp,
    /// <summary>Nothing to launch: the note says what to do elsewhere, and the connection is watched.</summary>
    Guidance,
}

public sealed record SignInPlan(SignInKind Kind, string Name, string? Command, Uri? Url, string Note)
{
    public bool OpensSettings => Kind == SignInKind.Settings;
    public InAppKind? InApp { get; init; }
    /// <summary>Where to get the command a terminal sign-in runs, when it is not installed.</summary>
    public Uri? InstallUrl { get; init; }
    /// <summary>Steps the terminal window prints before the tool starts, one per line.</summary>
    public string Hint { get; init; } = "";
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
        ["cursor"] = ("cursor-agent login", "A terminal window runs `cursor-agent login`; or install the Cursor editor and sign in there. CodeRim connects on its own."),
        ["kiro"] = ("kiro-cli login", "A terminal window runs `kiro-cli login`. Finish it in your browser and CodeRim connects on its own."),
        ["grok"] = ("grok login", "A terminal window runs `grok login`. Finish it in the browser and CodeRim connects on its own."),
        ["opencode"] = ("opencode auth login", "A terminal window runs `opencode auth login`. Choose OpenCode Go there and CodeRim connects on its own."),
        ["gemini-cli"] = ("gemini", "Personal Google accounts (including AI Pro and Ultra) can no longer sign in to Gemini CLI — choose Use Antigravity instead. For a Workspace or education account, a terminal window starts the Gemini CLI: choose Sign in with Google, then type /quit."),
        ["vertexai"] = ("gcloud auth application-default login", "A terminal window runs Google Cloud's sign-in. Finish it in the browser and CodeRim connects on its own."),
    };
    private static readonly Dictionary<string, (string Url, string Note)> Browser = new(StringComparer.Ordinal)
    {
    };
    private static readonly Dictionary<string, string> InstallPages = new(StringComparer.Ordinal)
    {
        ["codex"] = "https://github.com/openai/codex", ["cursor"] = "https://cursor.com/cli", ["opencode"] = "https://opencode.ai",
        ["gemini-cli"] = "https://github.com/google-gemini/gemini-cli#quickstart", ["kiro"] = "https://kiro.dev/cli/", ["grok"] = "https://github.com/xai-org/grok-build", ["vertexai"] = "https://cloud.google.com/sdk/docs/install",
    };
    private static readonly Dictionary<string, string> KeyNotes = new(StringComparer.Ordinal)
    {
        ["glm"] = "Paste your GLM Coding Plan API key below (create one at z.ai › API keys). CodeRim also finds a key Claude Code, ZCode or OpenCode already uses.",
        ["ollama"] = "Paste an Ollama API key below (create one at ollama.com › Settings › Keys).",
    };

    /// <summary>Finds the Antigravity app's OAuth client; replaceable in tests.</summary>
    public static Func<GoogleOAuthClient?> AntigravityClient { get; set; } =
        () => AntigravityOAuthClientLocator.Discover(AntigravityOAuthClientLocator.CandidatePaths(Environment.GetEnvironmentVariable));
    /// <summary>Whether the Gemini CLI is set to an API key instead of Google sign-in; replaceable in tests.</summary>
    public static Func<bool> GeminiUsesKey { get; set; } = () => GeminiAuthentication.UsesKey(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
    private const string GeminiKeyNote = "Your Gemini CLI is set to an API key, which CodeRim cannot read usage from. In the terminal: type /auth, choose Sign in with Google, finish in your browser, then type /quit. Personal Google accounts can no longer sign in through the Gemini CLI; if Google refuses, choose Use Antigravity instead.";
    private const string GeminiKeyHint = "Gemini is set to an API key, but CodeRim reads the Google sign-in.\n1. Type /auth and press Enter.\n2. Choose Sign in with Google and finish in your browser.\n3. When it says Authentication succeeded, type /quit.\nIf Google says this client is no longer supported for individuals, personal accounts moved to Antigravity: close this window and choose Use Antigravity in CodeRim instead.";
    private const string GeminiHint = "1. Choose Sign in with Google and finish in your browser.\n2. When it says Authentication succeeded, type /quit.\nIf Google says this client is no longer supported for individuals, personal accounts moved to Antigravity: close this window and choose Use Antigravity in CodeRim instead.";
    private static readonly Regex CookieKey = new("COOKIE|SESSION", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SecretKey = new("KEY|TOKEN|SECRET|PASSWORD", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static SignInPlan For(string id)
    {
        var definition = ProviderCatalog.Find(id);
        var name = definition?.Name ?? id;
        if (id == "copilot")
            return new(SignInKind.InApp, "GitHub", null, null,
                "CodeRim shows a short code and opens GitHub. Enter the code, approve, and it connects — no GitHub CLI needed.") { InApp = InAppKind.GitHubDevice };
        if (id == "gemini")
            return AntigravityClient() is not null
                ? new(SignInKind.InApp, "Antigravity", null, null,
                    "Your browser opens Google's sign-in. Choose the account you use with Antigravity and allow access; CodeRim connects on its own. The Antigravity app does not need to be running.") { InApp = InAppKind.AntigravityGoogle }
                : new(SignInKind.Browser, "Antigravity", null, new Uri("https://antigravity.google/download"),
                    "Install the Antigravity app from this page — CodeRim signs in with its Google sign-in. Then choose Sign in again here and pick your Google account.");
        if (id == "jetbrains")
            return new(SignInKind.Guidance, name, null, null, "Install a JetBrains IDE, sign in to JetBrains AI there and use it once. CodeRim reads the IDE's own quota file.");
        if (id == "gemini-cli")
        {
            var key = GeminiUsesKey();
            return new(SignInKind.Terminal, name, "gemini", null, key ? GeminiKeyNote : Terminal[id].Note)
                { InstallUrl = new Uri(InstallPages[id]), Hint = key ? GeminiKeyHint : GeminiHint };
        }
        if (KeyNotes.TryGetValue(id, out var keyNote)) return new(SignInKind.Settings, name, null, null, keyNote);
        if (Terminal.TryGetValue(id, out var terminal))
            return new(SignInKind.Terminal, name, terminal.Command, null, terminal.Note)
                { InstallUrl = InstallPages.TryGetValue(id, out var install) ? new Uri(install) : null };
        if (Browser.TryGetValue(id, out var page))
            return new(SignInKind.Browser, name, null, new Uri(page.Url), page.Note);
        var keys = definition?.EnvironmentKeys ?? [];
        var url = ProviderAccountLinks.UsagePage(id);
        if (url is not null && (keys.Any(CookieKey.IsMatch) || !keys.Any(SecretKey.IsMatch)))
            return new(SignInKind.Browser, name, null, url, $"Sign in on the {name} website in Firefox or a Chromium browser, then choose Import from Firefox… or Import from Chromium profile… on this page. CodeRim connects as soon as the session is imported.");
        return new(SignInKind.Settings, name, null, null, $"Enter your {name} key or session in its settings. {definition?.Summary}".Trim());
    }

    /// <summary>
    /// Why a terminal sign-in cannot start: its command is not installed. Looked up on PATH with
    /// the Windows executable extensions, so an empty terminal never opens.
    /// </summary>
    public static string? MissingTool(SignInPlan plan, Func<string, bool>? installed = null)
    {
        if (plan.Kind != SignInKind.Terminal || plan.Command is not { } command) return null;
        var tool = command.Split(' ', 2)[0];
        if ((installed ?? IsInstalled)(tool)) return null;
        var page = plan.InstallUrl is null ? " Install it, then choose Sign in again." : " Its install page is opening; after installing, choose Sign in again.";
        return $"{plan.Name} needs the `{tool}` command, which is not installed on this PC.{page}";
    }

    public static bool IsInstalled(string tool)
    {
        if (tool.Length == 0) return false;
        if (Path.IsPathFullyQualified(tool)) return File.Exists(tool);
        var extensions = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT;.PS1").Split(';', StringSplitOptions.RemoveEmptyEntries).Prepend("")
            : [""];
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var extra = new[] { Path.Combine(home, ".local", "bin"), Path.Combine(home, "AppData", "Roaming", "npm"), Path.Combine(home, "scoop", "shims"),
            Path.Combine(home, ".grok", "bin") };
        foreach (var directory in CurrentPath().Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).Concat(extra))
            foreach (var extension in extensions)
                try { if (File.Exists(Path.Combine(directory, tool + extension))) return true; }
                catch (ArgumentException) { }
        return false;
    }

    /// <summary>
    /// The PATH a new terminal gets now. CodeRim's own copy is fixed at its start, so a CLI installed
    /// afterwards would otherwise look missing until CodeRim restarts.
    /// </summary>
    public static string CurrentPath()
    {
        var process = Environment.GetEnvironmentVariable("PATH") ?? "";
        if (!OperatingSystem.IsWindows()) return process;
        var stored = new[] { EnvironmentVariableTarget.Machine, EnvironmentVariableTarget.User }
            .Select(target => { try { return Environment.GetEnvironmentVariable("PATH", target); } catch (System.Security.SecurityException) { return null; } })
            .Where(value => !string.IsNullOrEmpty(value));
        return string.Join(Path.PathSeparator, stored.Append(process));
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
    private readonly Func<string, SignInPlan> plan;
    private readonly Func<SignInPlan, string?> preflight;
    private readonly Action<SignInPlan> openInstallPage;
    private readonly Func<InAppKind, Action<string>, CancellationToken, Task<InAppOutcome>> runInApp;
    private readonly Func<string, string?> blocker;
    private readonly TimeSpan poll, patience;
    private readonly Dictionary<string, State> states = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CancellationTokenSource> running = new(StringComparer.Ordinal);
    private readonly object gate = new();
    public event Action? Changed;

    public ProviderConnector(Func<string, Task<bool>> connects, Func<SignInPlan, bool> launch, TimeSpan? poll = null, TimeSpan? patience = null,
        Func<string, SignInPlan>? plan = null, Func<SignInPlan, string?>? preflight = null, Action<SignInPlan>? openInstallPage = null,
        Func<InAppKind, Action<string>, CancellationToken, Task<InAppOutcome>>? runInApp = null, Func<string, string?>? blocker = null)
    {
        this.connects = connects; this.launch = launch;
        this.plan = plan ?? ProviderSignIn.For;
        this.preflight = preflight ?? (p => ProviderSignIn.MissingTool(p));
        this.openInstallPage = openInstallPage ?? (_ => { });
        this.runInApp = runInApp ?? ((_, _, _) => Task.FromResult(InAppOutcome.Failed("This sign-in is not available here.")));
        this.blocker = blocker ?? (_ => null);
        this.poll = poll ?? TimeSpan.FromSeconds(4); this.patience = patience ?? TimeSpan.FromMinutes(10);
    }

    public State? StateOf(string id) { lock (gate) return states.GetValueOrDefault(id); }

    public void Cancel(string id)
    {
        lock (gate) { if (running.Remove(id, out var cancellation)) cancellation.Cancel(); states.Remove(id); }
        Changed?.Invoke();
    }

    public void Begin(string id) => _ = RunAsync(id);

    // Awaits keep the caller's context on purpose: the window's store and sign-in callbacks expect its UI thread.
    public async Task RunAsync(string id)
    {
        CancellationTokenSource cancellation = new();
        lock (gate) { if (running.Remove(id, out var previous)) previous.Cancel(); running[id] = cancellation; }
        var token = cancellation.Token;
        // Only the run that currently owns the provider may write its state: a cancelled or
        // superseded run resuming from an await must never report Connected afterwards.
        void Set(Phase phase, string message = "")
        {
            lock (gate)
            {
                if (token.IsCancellationRequested || running.GetValueOrDefault(id) != cancellation) return;
                states[id] = new(phase, message);
            }
            Changed?.Invoke();
        }
        try
        {
            Set(Phase.Checking);
            if (await connects(id)) { Set(Phase.Connected); return; }
            token.ThrowIfCancellationRequested();
            var signIn = plan(id);
            if (signIn.OpensSettings) { Set(Phase.NeedsKey, signIn.Note); return; }
            if (signIn.Kind == SignInKind.InApp && signIn.InApp is { } kind)
            {
                Set(Phase.Waiting, signIn.Note);
                var outcome = await runInApp(kind, note => Set(Phase.Waiting, note), token);
                token.ThrowIfCancellationRequested();
                if (!outcome.SignedIn) { Set(Phase.Failed, outcome.Reason ?? "Sign-in did not finish. Choose Sign in to try again."); return; }
                if (await connects(id)) { Set(Phase.Connected); return; }
            }
            else if (signIn.Kind == SignInKind.Guidance) Set(Phase.Waiting, signIn.Note);
            else
            {
                if (preflight(signIn) is { } problem) { openInstallPage(signIn); Set(Phase.Failed, problem); return; }
                if (!launch(signIn)) { Set(Phase.Failed, signIn.Note + " It could not be opened automatically."); return; }
                Set(Phase.Waiting, signIn.Note);
            }
            var deadline = DateTime.UtcNow + patience;
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(poll, token);
                if (await connects(id)) { Set(Phase.Connected); return; }
                token.ThrowIfCancellationRequested();
                if (blocker(id) is { } reason) { Set(Phase.Failed, reason); return; }
            }
            Set(Phase.Failed, "Sign-in was not detected. Choose Sign in to try again.");
        }
        catch (OperationCanceledException) { }
        // Whatever goes wrong inside a sign-in ends it with a reason instead of leaving it waiting forever.
        catch (Exception e) when (e is not OutOfMemoryException) { Set(Phase.Failed, "Sign-in stopped: " + e.Message + " Choose Try again."); }
        finally { lock (gate) { if (running.GetValueOrDefault(id) == cancellation) running.Remove(id); } cancellation.Dispose(); }
    }
}
