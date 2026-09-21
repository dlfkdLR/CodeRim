using System.IO;
using System.Windows.Automation;
using System.Windows.Controls;
using CodeRim.Core.Domain;
using CodeRim.Windows.Services;
using CodeRim.Windows.ViewModels;
namespace CodeRim.Windows.Views;
internal static partial class NativeSmoke
{
    private static async Task NativeSourceRegression(DashboardWindow dashboard, DashboardStore store, AppSettingsStore settings, CredentialVault vault, string directory)
    {
        using var connections = new ProviderConnections(vault);
        var providers = settings.Current.EnabledProviders;
        var alias = Environment.GetEnvironmentVariable("ANTIGRAVITY_OAUTH_CREDENTIALS_JSON");
        var groqAlias = Environment.GetEnvironmentVariable("GROQ_SESSION_JWT");
        var factoryAlias = Environment.GetEnvironmentVariable("FACTORY_COOKIE");
        try
        {
            vault.Save("provider:amp", "synthetic-api-fallback");
            vault.Save("setting:amp:AMP_USAGE_SOURCE", "cli");
            vault.Save("setting:amp:AMP_EXECUTABLE", Path.Combine(AppContext.BaseDirectory, "TestResults", "missing-amp.exe"));
            Require(!connections.CanCache("amp") && connections.Scope("amp") is { Length: > 0 }, "CLI needs a volatile display scope without persisted cache");
            var firstScope = connections.Scope("amp");
            var failed = await connections.FetchAsync("amp", settings.Current, CancellationToken.None);
            Require(failed.State == ReadingState.Error && failed.Windows.Count == 0, "Missing CLI silently fell back to saved API credentials");
            Require(connections.Scope("amp") == firstScope, "CLI display scope changed between quota polls");
            vault.Save("setting:amp:AMP_EXECUTABLE", Path.Combine(AppContext.BaseDirectory, "TestResults", "other-missing-amp.exe"));
            Require(connections.Scope("amp") != firstScope, "Changing CLI source did not invalidate display identity");
            settings.Save(settings.Current with { EnabledProviders = [..providers, "amp"] }); dashboard.Navigate("amp"); await Idle();
            var source = Descendants<ComboBox>(dashboard).Single(x => x.Items.Contains("CLI") && x.Items.Contains("API"));
            Require(Equals(source.SelectedItem, "CLI"), "Amp source picker ignored stored source");
            source.SelectedItem = "API"; await Idle();
            Require(vault.Load("setting:amp:AMP_USAGE_SOURCE") == "api" && connections.CanCache("amp"), "Amp source selection did not update the connector");
            Require(connections.Scope("amp") != firstScope, "API and CLI share a scope");
            Environment.SetEnvironmentVariable("ANTIGRAVITY_OAUTH_CREDENTIALS_JSON", """{"accessToken":"synthetic-one"}""");
            var scope = connections.Scope("gemini");
            Environment.SetEnvironmentVariable("ANTIGRAVITY_OAUTH_CREDENTIALS_JSON", """{"accessToken":"synthetic-two"}""");
            Require(scope is not null && scope != connections.Scope("gemini"), "Antigravity JSON alias does not invalidate account scope");
            var localPath = Path.Combine(AppContext.BaseDirectory, "TestResults", "windsurf-native.vscdb");
            Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
            using (var database = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = localPath, Pooling = false }.ToString()))
            {
                database.Open(); using var command = database.CreateCommand();
                command.CommandText = "CREATE TABLE IF NOT EXISTS ItemTable(key TEXT PRIMARY KEY,value BLOB); INSERT OR REPLACE INTO ItemTable VALUES('windsurf.settings.cachedPlanInfo',$value)";
                command.Parameters.AddWithValue("$value", """{"planName":"Local fixture","quotaUsage":{"dailyRemainingPercent":50}}""");
                command.ExecuteNonQuery();
            }
            vault.Save("provider:windsurf", "synthetic-web-fallback");
            vault.Save("setting:windsurf:WINDSURF_USAGE_SOURCE", "local");
            vault.Save("setting:windsurf:WINDSURF_CACHE_PATH", localPath);
            var local = await connections.FetchAsync("windsurf", settings.Current, CancellationToken.None);
            Require(local.State == ReadingState.Stale && local.Headline?.UsedPercent == 50 && local.UpdatedAt is null, "Windsurf local source did not keep cached quota provenance");
            Require(!connections.CanCache("windsurf") && connections.Scope("windsurf") is { Length: > 0 }, "Windsurf local source needs a volatile display scope");
            settings.Save(settings.Current with { EnabledProviders = [..providers, "windsurf"] }); dashboard.Navigate("windsurf"); await Idle();
            var windSource = Descendants<ComboBox>(dashboard).Single(x => x.Items.Contains("Web") && x.Items.Contains("Local"));
            Require(Equals(windSource.SelectedItem, "Local"), "Windsurf source picker ignored stored source");
            windSource.SelectedItem = "Web"; await Idle();
            Require(connections.CanCache("windsurf"), "Windsurf source picker did not switch the connector");
            File.Delete(localPath);
            Environment.SetEnvironmentVariable("GROQ_SESSION_JWT", "header.first.signature");
            var groqScope = connections.Scope("groq");
            Environment.SetEnvironmentVariable("GROQ_SESSION_JWT", "header.second.signature");
            Require(groqScope is not null && groqScope != connections.Scope("groq"), "Groq console session does not invalidate account scope");
            settings.Save(settings.Current with { EnabledProviders = [..providers, "groq"] }); dashboard.Navigate("groq"); await Idle();
            Require(Descendants<TextBlock>(dashboard).Any(x => x.Text == "Console session JWT, session JSON, or enterprise API key"), "Groq settings hide the console session connection");
            Require(Descendants<Button>(dashboard).Any(x => Equals(x.Content, "Import from Firefox…")), "Groq Firefox connection is absent");
            Capture(dashboard, Path.Combine(directory, "windows-groq-connection.png"));
            Environment.SetEnvironmentVariable("FACTORY_COOKIE", "session=first-fixture");
            var factoryScope = connections.Scope("factory");
            Environment.SetEnvironmentVariable("FACTORY_COOKIE", "session=second-fixture");
            Require(factoryScope is not null && factoryScope != connections.Scope("factory"), "Factory session does not invalidate account scope");
            settings.Save(settings.Current with { EnabledProviders = [..providers, "factory"] }); dashboard.Navigate("factory"); await Idle();
            Require(Descendants<TextBlock>(dashboard).Any(x => x.Text == "Factory API key, Authorization bearer, or Cookie header"), "Factory settings hide the cookie and Authorization connections");
            Require(Descendants<Button>(dashboard).Any(x => Equals(x.Content, "Import from Firefox…")), "Factory Firefox connection is absent");
            Capture(dashboard, Path.Combine(directory, "windows-factory-connection.png"));
            Require(!connections.CanCache("jetbrains") && connections.Scope("jetbrains") is { Length: > 0 }, "Local JetBrains display has no volatile source scope");
        }
        finally
        {
            Environment.SetEnvironmentVariable("ANTIGRAVITY_OAUTH_CREDENTIALS_JSON", alias);
            Environment.SetEnvironmentVariable("GROQ_SESSION_JWT", groqAlias);
            Environment.SetEnvironmentVariable("FACTORY_COOKIE", factoryAlias);
            vault.Delete("provider:windsurf"); vault.Delete("setting:windsurf:WINDSURF_USAGE_SOURCE"); vault.Delete("setting:windsurf:WINDSURF_CACHE_PATH");
            vault.Delete("provider:amp"); vault.Delete("setting:amp:AMP_USAGE_SOURCE"); vault.Delete("setting:amp:AMP_EXECUTABLE");
            settings.Save(settings.Current with { EnabledProviders = providers }); dashboard.Navigate("usage"); await Idle();
        }
    }
}
