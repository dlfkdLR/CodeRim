using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using CodeRim.Core.Domain;
using CodeRim.Core.Services;
using CodeRim.Windows.Services;
using CodeRim.Windows.ViewModels;

namespace CodeRim.Windows.Views;

internal static partial class NativeSmoke
{
    private static async Task LocalTokenRegression(DashboardStore store, AppSettingsStore settings, CredentialVault vault, string directory)
    {
        var original = store.Usage.GetValueOrDefault("codex");
        var quota = store.Readings.GetValueOrDefault("codex");
        Window? fixture = null; Exception? failure = null; var cleanup = new List<Exception>();
        var states = new List<object>();
        try
        {
            fixture = new Window { Width = 320, Height = 500, ShowInTaskbar = false };
            fixture.Show();
            var snapshot = UsageSnapshot.Empty with { Today = new(608595, 544279, 1931), UpdatedAt = DateTimeOffset.Now };
            (string State, UsageSnapshot? Snapshot, string Text)[] cases = [
                ("loading", null, "Loading…"), ("unavailable", UsageSnapshot.Empty, "Unavailable"),
                ("error", snapshot with { Quality = DataQuality.Error }, "Unavailable"),
                ("zero", UsageSnapshot.Empty with { Quality = DataQuality.Exact }, "0 tokens"),
                ("partial", snapshot with { Quality = DataQuality.Partial }, TokenFormatter.Format(610526, TokenNumberStyle.Detailed) + " tokens"),
                ("stale", snapshot with { Quality = DataQuality.Stale }, TokenFormatter.Format(610526, TokenNumberStyle.Detailed) + " tokens (stale)"),
                ("large", snapshot with { Quality = DataQuality.Stale, Today = new(long.MaxValue, 0, 0) }, TokenFormatter.Format(long.MaxValue, TokenNumberStyle.Detailed) + " tokens (stale)")];
            foreach (var item in cases)
            {
                if (item.Snapshot is null) store.Usage.Remove("codex"); else store.Usage["codex"] = item.Snapshot;
                var card = NotchPopover.Create("codex", store, settings.Current with { NumberStyle = TokenNumberStyle.Detailed }, _ => { }, 500);
                fixture.Content = card; await Idle();
                var line = Descendants<TextBlock>(card).Single(x => AutomationProperties.GetAutomationId(x) == "notch.tokens.codex");
                var scope = Descendants<TextBlock>(card).Single(x => AutomationProperties.GetAutomationId(x) == "notch.tokens.scope.codex");
                File.WriteAllText(Path.Combine(directory, "windows-local-tokens-" + item.State + "-render.json"), JsonSerializer.Serialize(new
                {
                    item.State, expected = "Today  " + item.Text, actual = line.Text,
                    range = new System.Windows.Documents.TextRange(line.ContentStart, line.ContentEnd).Text,
                    runs = line.Inlines.OfType<System.Windows.Documents.Run>().Select(run => run.Text).ToArray(),
                    snapshot = item.Snapshot?.Quality, stored = store.Usage.GetValueOrDefault("codex")?.Quality,
                    culture = CultureInfo.CurrentCulture.Name, line.ActualWidth, line.ActualHeight
                }, JsonOptions));
                Capture(card, Path.Combine(directory, "windows-local-tokens-" + item.State + ".png"));
                Require(RenderedText(line) == "Today  " + item.Text, "Local token state does not match the Mac reference: " + item.State);
                Require(scope.Text == LocalTokenPresentation.Scope && (string?)scope.ToolTip == LocalTokenPresentation.ScopeHelp,
                    "Local token scope or help text is missing.");
                var lineBounds = line.TransformToAncestor(card).TransformBounds(new Rect(line.RenderSize));
                var scopeBounds = scope.TransformToAncestor(card).TransformBounds(new Rect(scope.RenderSize));
                Require(lineBounds.Width > 0 && scopeBounds.Width > 0 && lineBounds.Right <= card.ActualWidth + 1
                    && scopeBounds.Right <= card.ActualWidth + 1 && scopeBounds.Top >= lineBounds.Bottom,
                    "Local token lines overlap or extend outside the card: " + item.State);
                Require(lineBounds.Width / line.ActualWidth >= .85, "Local token text shrank below the reference minimum.");
                Require(ReferenceEquals(quota, store.Readings.GetValueOrDefault("codex")), "Local token presentation replaced account quotas.");
                states.Add(new { item.State, Text = RenderedText(line), scope = scope.Text, lineBounds, scopeBounds });
            }
            fixture.Content = NotchPopover.Create("copilot", store, settings.Current, _ => { }); await Idle();
            Require(!Descendants<TextBlock>(fixture).Any(x => AutomationProperties.GetAutomationId(x).StartsWith("notch.tokens.", StringComparison.Ordinal)),
                "A provider without local transcripts acquired a fabricated local-token row.");
            File.WriteAllText(Path.Combine(directory, "windows-local-token-states.json"), JsonSerializer.Serialize(new { completed = true, states }, JsonOptions));
        }
        catch (Exception error) when (error is not OutOfMemoryException) { failure = error; }
        finally
        {
            try { fixture?.Close(); } catch (Exception error) when (error is not OutOfMemoryException) { cleanup.Add(error); }
            if (original is null) store.Usage.Remove("codex"); else store.Usage["codex"] = original;
        }
        if (failure is not null && cleanup.Count > 0) throw new AggregateException("Local token fixture and cleanup failed.", new[] { failure }.Concat(cleanup));
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        if (cleanup.Count > 0) throw new AggregateException("Local token fixture cleanup failed.", cleanup);
        await LocalTokenRefreshRegression(settings, vault, directory);
    }

    private static async Task LocalTokenRefreshRegression(AppSettingsStore settings, CredentialVault vault, string directory)
    {
        var before = settings.Current; var previousHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        var home = Path.Combine(Path.GetTempPath(), "coderim-local-token-" + Guid.NewGuid().ToString("N"));
        var previousCompanion = File.Exists(CompanionFile.SnapshotPath) ? File.ReadAllBytes(CompanionFile.SnapshotPath) : null;
        DashboardStore? store = null; NotchWindow? notch = null; Exception? failure = null; var cleanup = new List<Exception>();
        try
        {
            Directory.CreateDirectory(Path.Combine(home, "sessions")); Environment.SetEnvironmentVariable("CODEX_HOME", home);
            var database = Path.Combine(home, "statistics.sqlite");
            var source = Path.Combine(home, "sessions", "local-tokens.jsonl");
            var timestamp = DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture);
            string Row(int input, int output) => JsonSerializer.Serialize(new { type = "event_msg", timestamp,
                payload = new { type = "token_count", info = new { total_token_usage = new { input_tokens = input, cached_input_tokens = 0, output_tokens = output },
                    last_token_usage = new { input_tokens = input, cached_input_tokens = 0, output_tokens = output } } } }) + "\n";
            File.WriteAllText(source, Row(100, 10));
            settings.Save(before with { EnabledProviders = ["codex"], AccountLimitsEnabled = false, ProfileSyncEnabled = false,
                Visibility = NotchVisibility.AlwaysShow, CompletionSound = false, PeekOnCompletion = false, ReduceMotion = true, NumberStyle = TokenNumberStyle.Detailed });
            store = new DashboardStore(settings, vault, usageRepository: new UsageRepository(database));
            var quota = new ProviderReading("codex", ReadingState.NeedsAuth, [], Message: "Synthetic unavailable quota");
            store.Readings["codex"] = quota;
            var quotaRequests = 0;
            store.ReadingUpdated += _ => quotaRequests++;
            notch = new NotchWindow(store, settings, _ => { }); notch.ApplyVisibility(); notch.OpenProvider("codex"); await Idle();
            string Tokens() => RenderedText(Descendants<TextBlock>(notch.PopupContent!).Single(x => AutomationProperties.GetAutomationId(x) == "notch.tokens.codex"));
            Require(store.Usage["codex"].Quality == DataQuality.Unavailable && Tokens() == "Today  Unavailable",
                "A newly opened empty local repository presented an unmeasured zero before scanning.");
            await store.RefreshLocalAsync(); await Idle();
            Require(notch.PopupIsOpen && Tokens() == "Today  110 tokens", "Transcript refresh did not update the open production popup.");
            File.AppendAllText(source, Row(120, 15)); store.Invalidate([source]);
            await store.RefreshLocalAsync(); await Idle();
            Require(Tokens() == "Today  135 tokens" && quotaRequests == 0 && ReferenceEquals(quota, store.Readings["codex"]),
                "Local file changes waited for or replaced account quotas.");
            File.Move(database, database + ".saved"); Directory.CreateDirectory(database);
            await store.RefreshLocalAsync(); await Idle();
            Require(store.Usage["codex"].Quality == DataQuality.Stale && Tokens() == "Today  135 tokens (stale)",
                "A real SQLite open failure presented retained local tokens as fresh.");
            store.Usage.Remove("codex"); await store.RefreshLocalAsync(); await Idle();
            Require(store.Usage["codex"].Quality == DataQuality.Error && Tokens() == "Today  Unavailable",
                "A first-read failure left local tokens loading or fabricated zero.");
            Directory.Delete(database); File.Move(database + ".saved", database);
            await store.RefreshLocalAsync(); await Idle();
            Require(Tokens() == "Today  135 tokens", "Local usage did not recover after the database became readable.");
            await store.ClearLocalHistoryAsync("codex"); await Idle();
            Require(Tokens() == "Today  Unavailable" && File.ReadAllText(source) == Row(100, 10) + Row(120, 15),
                "Clear left a permanent loading row or changed the original transcript.");
            File.WriteAllText(Path.Combine(directory, "windows-local-token-refresh.json"), JsonSerializer.Serialize(new { completed = true, quotaRequests,
                checks = new List<string> { "Production popup follows local JSONL changes without quota refresh", "SQLite failure distinguishes stale and unavailable",
                    "Database recovery restores current tokens", "Clear finishes loading without modifying transcripts" } }, JsonOptions));
        }
        catch (Exception error) when (error is not OutOfMemoryException) { failure = error; }
        finally
        {
            try { notch?.Close(); } catch (Exception error) when (error is not OutOfMemoryException) { cleanup.Add(error); }
            try { store?.Dispose(); } catch (Exception error) when (error is not OutOfMemoryException) { cleanup.Add(error); }
            try { settings.Save(before); } catch (Exception error) when (error is not OutOfMemoryException) { cleanup.Add(error); }
            try { Environment.SetEnvironmentVariable("CODEX_HOME", previousHome); } catch (Exception error) when (error is not OutOfMemoryException) { cleanup.Add(error); }
            try { if (previousCompanion is null) File.Delete(CompanionFile.SnapshotPath); else File.WriteAllBytes(CompanionFile.SnapshotPath, previousCompanion); }
            catch (Exception error) when (error is not OutOfMemoryException) { cleanup.Add(error); }
            try { if (Directory.Exists(home)) Directory.Delete(home, true); } catch (Exception error) when (error is not OutOfMemoryException) { cleanup.Add(error); }
        }
        if (failure is not null && cleanup.Count > 0) throw new AggregateException("Local token refresh and cleanup failed.", new[] { failure }.Concat(cleanup));
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        if (cleanup.Count > 0) throw new AggregateException("Local token refresh cleanup failed.", cleanup);
    }

    // The native CI capture confirmed that the colored Run content is visible
    // while TextBlock.Text remains empty. Read the rendered text container.
    private static string RenderedText(TextBlock line) => new System.Windows.Documents.TextRange(line.ContentStart, line.ContentEnd).Text;
}
