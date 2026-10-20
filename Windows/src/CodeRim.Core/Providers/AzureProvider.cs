using System.Text.Json;
using CodeRim.Core.Domain;
using static CodeRim.Core.Providers.ProviderParsers;
namespace CodeRim.Core.Providers;
public sealed partial class NativeProviders
{
    private static string? AzureDeployment(Func<string, string?> setting) => setting("AZURE_OPENAI_DEPLOYMENT_NAME") ?? setting("AZURE_OPENAI_DEPLOYMENT");
    private static string AzureVersion(Func<string, string?> setting) => string.IsNullOrWhiteSpace(setting("AZURE_OPENAI_API_VERSION")) ? "2024-10-21" : setting("AZURE_OPENAI_API_VERSION")!.Trim();
    private static string AzureEndpoint(Func<string, string?> setting)
    {
        var endpoint = ManagementBase(setting("AZURE_OPENAI_ENDPOINT") ?? "");
        if (!endpoint.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Azure requires HTTPS.");
        var deployment = AzureDeployment(setting);
        if (deployment is not { Length: > 0 and <= 256 } || deployment.Any(char.IsControl)) throw new InvalidDataException("Set the Azure deployment name.");
        var version = AzureVersion(setting);
        if (version.Equals("v1", StringComparison.OrdinalIgnoreCase))
        {
            if (!endpoint.EndsWith("/openai/v1", StringComparison.OrdinalIgnoreCase)) endpoint += endpoint.EndsWith("/openai", StringComparison.OrdinalIgnoreCase) ? "/v1" : "/openai/v1";
            return endpoint + "/chat/completions";
        }
        if (!endpoint.EndsWith("/openai", StringComparison.OrdinalIgnoreCase)) endpoint += "/openai";
        return endpoint + "/deployments/" + Uri.EscapeDataString(deployment) + "/chat/completions?api-version=" + Uri.EscapeDataString(version);
    }
    private static string AzureBody(Func<string, string?> setting)
    {
        var messages = new[] { new { role = "user", content = "ping" } };
        return AzureVersion(setting).Equals("v1", StringComparison.OrdinalIgnoreCase)
            ? JsonSerializer.Serialize(new { messages, model = AzureDeployment(setting), max_completion_tokens = 64 })
            : JsonSerializer.Serialize(new { messages, max_tokens = 1 });
    }
    private static ProviderReading ParseAzure(JsonElement response, string? deployment)
    {
        if (response.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid Azure response.");
        var model = Text(response, "model");
        return new("azureopenai", ReadingState.Ready, [new("deployment", "Deployment verified", DisplayValue: model ?? deployment)],
            DateTimeOffset.UtcNow, "Paid connection validation; no quota counters are reported.", deployment);
    }
}
