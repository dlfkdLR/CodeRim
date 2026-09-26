using System.Text;

namespace CodeRim.Core.Domain;

// Display-only metadata belongs to the reading that produced it. It is never
// used as a credential, ownership key or a destination supplied by a response.
public sealed record ProviderAccountMetadata(string? Label, string Source,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? Region = null)
{
    public static string? DisplayText(string? value)
    {
        var text = value?.Trim();
        return string.IsNullOrEmpty(text) || Encoding.UTF8.GetByteCount(text) > 256 || text.Any(char.IsControl) ? null : text;
    }
    public void Validate()
    {
        if (DisplayText(Source) != Source || string.IsNullOrEmpty(Source)
            || Label is not null && DisplayText(Label) != Label || Region is not (null or "global" or "bigmodel-cn"))
            throw new InvalidDataException("Invalid provider account metadata.");
    }
}

public static class ProviderAccountLinks
{
    // Frozen Mac descriptor destinations (CodexBar 51ed16bdd3), plus the native
    // Copilot and GLM account destinations. Never turn an API response into a link.
    public static Uri? UsagePage(string id, string? region = null) => id == "glm"
        ? new Uri(region == "bigmodel-cn" ? "https://open.bigmodel.cn/usage" : "https://z.ai/manage-apikey/apikey-list")
        : Addresses.GetValueOrDefault(id) is { } address ? new Uri(address) : null;
    private static readonly Dictionary<string, string> Addresses = new(StringComparer.Ordinal)
    {
        ["copilot"] = "https://github.com/settings/copilot",
        ["cursor"] = "https://cursor.com/dashboard",
        ["grok"] = "https://grok.com/?_s=usage",
        ["commandcode"] = "https://commandcode.ai",
        ["ollama"] = "https://ollama.com/settings",
        ["opencode"] = "https://opencode.ai",
        ["t3chat"] = "https://t3.chat/settings/customization",
        ["xai"] = "https://console.x.ai",
        ["venice"] = "https://venice.ai/settings/api",
        ["openai"] = "https://platform.openai.com/usage",
        ["manus"] = "https://manus.im",
        ["crof"] = "https://crof.ai/dashboard",
        ["clawrouter"] = "https://clawrouter.openclaw.ai/dashboard/access",
        ["deepgram"] = "https://console.deepgram.com/project/",
        ["qoder"] = "https://qoder.com/account/usage",
        ["clinepass"] = "https://app.cline.bot/dashboard/subscription?personal=true",
        ["perplexity"] = "https://www.perplexity.ai/account/usage",
        ["openrouter"] = "https://openrouter.ai/activity",
        ["poe"] = "https://poe.com/api/keys"
    };
}
