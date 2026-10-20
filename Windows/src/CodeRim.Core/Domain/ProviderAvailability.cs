namespace CodeRim.Core.Domain;

// These reference providers remove their snapshot on every failed fetch
// (NotchProvider.isVisibleWhenAbsent == false), independently of a locally
// detected Account. Other providers retain their existing stale-data policy.
public static class ProviderAvailability
{
    public static bool HidesWhenAbsent(string id) => id is
        "copilot" or "cursor" or "grok" or "commandcode" or "opencode"
        or "glm" or "ollama" or "gemini" or "ollama-local";

    // Use the stored phase, before clock-based Evaluated() changes Ready to
    // Stale. Startup placeholders/archives require a detected local account;
    // a successful fetch may create a cell even without account metadata.
    public static bool ShowsInNotch(string id, ProviderReading? reading, bool accountDetected)
        => !HidesWhenAbsent(id) || reading?.State switch
        {
            ReadingState.Ready or ReadingState.Partial => true,
            null or ReadingState.Loading or ReadingState.Stale => accountDetected,
            _ => false
        };
}
