using CodeRim.Core.Domain;

namespace CodeRim.Windows.Views;

internal static partial class NotchPopover
{
    private static string StatusMessage(string id, ProviderReading? reading) => reading?.State switch
    {
        null or ReadingState.Loading or ReadingState.Stale => "Waiting for the first reading…",
        ReadingState.Ready or ReadingState.Partial => "Connected; this account did not report usage or a quota.",
        ReadingState.NeedsAuth => id switch
        {
            "claude" => "Sign in to Claude Code to read your usage",
            "cursor" => "Sign in to Cursor in the editor",
            "codex" => "Sign in to Codex to read your usage",
            "gemini" => "Sign in to Antigravity to read your usage",
            "glm" => "Set up a GLM Coding Plan key for a coding tool to read your usage",
            "grok" => "Run grok login to read your Grok Build usage",
            "copilot" => "Sign in with GitHub CLI to read your Copilot usage",
            "opencode" => "Connect the Go plan in OpenCode to read your usage",
            "commandcode" => "Sign in with the Command Code app to read your usage",
            "ollama" => "Enter an Ollama API key in Settings, or export OLLAMA_API_KEY",
            "ollama-local" => "Start Ollama to monitor your local models",
            _ => "Sign in to " + (ProviderCatalog.Find(id)?.Name ?? id) + " to read your usage"
        },
        // Windows exposes a distinct disabled state. Keep its actionable reason
        // instead of suggesting that a user who turned limits off must sign in.
        ReadingState.Disabled => reading.Message ?? "Account limits are turned off in Settings.",
        ReadingState.Unsupported => reading.Message ?? "Usage is not supported by this connection.",
        _ => "Couldn't read usage — " + (reading.Message ?? "Usage is currently unavailable.")
    };
}
