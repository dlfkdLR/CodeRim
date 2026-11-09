using System.Text;
using System.Text.Json;
using CodeRim.Windows.Services;

var root = Path.Combine(AppContext.BaseDirectory, "coderim-saved-probe-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var previous = Environment.GetEnvironmentVariable("CODERIM_DATA_DIR");
Environment.SetEnvironmentVariable("CODERIM_DATA_DIR", root);
try
{
    foreach (var fails in new[] { false, true })
        foreach (var mutation in new[] { "unchanged", "remove", "sign-in" })
            await SuspendedProbe(fails, mutation);
    CompareExchangeRejectsAWriteAfterSnapshot();
    Console.WriteLine("Saved-account probe checks passed (7 scenarios).");
}
finally
{
    Environment.SetEnvironmentVariable("CODERIM_DATA_DIR", previous);
    Directory.Delete(root, recursive: true);
}

static async Task SuspendedProbe(bool fails, string mutation)
{
    var vault = new CredentialVault(); var oldPaneAccounts = new SavedAccounts(vault);
    var original = Login("1"); var renewed = Login("2"); var newer = Login("3");
    Save(vault, original);
    var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var captured = oldPaneAccounts.Read("codex").Single();
    var read = SavedAccountLimitReader.ReadAsync(captured, "synthetic-never-launched", async (_, _, environment, _) =>
    {
        File.WriteAllText(Path.Combine(environment["CODEX_HOME"]!, "auth.json"), renewed.Credential);
        entered.SetResult();
        await resume.Task;
        if (fails) throw new IOException("Synthetic failed read after renewal.");
        return JsonSerializer.SerializeToElement(new { rateLimits = new { } });
    });
    await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
    // Closing a pane leaves its read alive; reopening creates an independent
    // SavedAccounts object, so its local busy state does not block this mutation.
    var reopenedPaneAccounts = new SavedAccounts(vault);
    if (mutation == "remove") reopenedPaneAccounts.Remove("codex", original.Identity.Id);
    if (mutation == "sign-in") Save(vault, newer);
    resume.SetResult();
    string? rotated;
    try { rotated = (await read).RotatedCredential; Check(!fails, "rotated failure was swallowed"); }
    catch (SavedAccountReadException error) { rotated = error.RotatedCredential; Check(fails, "unexpected probe failure"); }
    Check(rotated == renewed.Credential, "the probe lost its rotated credential");
    var stored = oldPaneAccounts.StoreRotated(captured, rotated!);
    Check(stored == (mutation == "unchanged"), "stale rotation was accepted or unchanged rotation was lost");
    var saved = reopenedPaneAccounts.Read("codex").SingleOrDefault();
    Check(saved?.Credential == (mutation == "remove" ? null : mutation == "sign-in" ? newer.Credential : renewed.Credential),
        "a suspended probe overwrote newer state or restored a removed account");
}

static void CompareExchangeRejectsAWriteAfterSnapshot()
{
    var vault = new CredentialVault(); var accounts = new SavedAccounts(vault);
    var original = Login("1"); var renewed = Login("2"); var newer = Login("3");
    Save(vault, original);
    vault.BeforeCompareExchange = () => Save(vault, newer);
    Check(!accounts.StoreRotated(original, renewed.Credential), "a late write bypassed CAS");
    Check(accounts.Read("codex").Single().Credential == newer.Credential, "CAS lost the newer login");
}

static SavedLogin Login(string revision)
{
    var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
    {
        sub = "synthetic-subject", email = "synthetic@example.invalid"
    }))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    var credential = JsonSerializer.Serialize(new
    {
        auth_mode = "chatgpt",
        tokens = new { access_token = "access-" + revision, refresh_token = "refresh-" + revision,
            id_token = "e30." + payload + ".signature", account_id = "synthetic-workspace" }
    });
    return SavedAccounts.ParseCredential("codex", credential);
}
static void Save(CredentialVault vault, SavedLogin login) => vault.Save("accounts:codex", JsonSerializer.Serialize(new[] { login }));
static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
