namespace CodeRim.Windows.Services;

// Link the production account/probe code without WPF, DPAPI, or real CLI sign-in.
internal sealed class CredentialVault
{
    internal sealed record Snapshot(string Value, string Version);
    private readonly Dictionary<string, Snapshot> entries = [];
    private int revision;
    internal Action? BeforeCompareExchange { get; set; }
    public string? Load(string id) => LoadVersioned(id)?.Value;
    internal Snapshot? LoadVersioned(string id) => entries.GetValueOrDefault(id);
    public void Save(string id, string value) => entries[id] = new(value, (++revision).ToString(System.Globalization.CultureInfo.InvariantCulture));
    internal bool SaveIfUnchanged(string id, string? expectedVersion, string value)
    {
        BeforeCompareExchange?.Invoke();
        if (LoadVersioned(id)?.Version != expectedVersion) return false;
        Save(id, value); return true;
    }
    internal static void RestrictDirectory(string path)
    {
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
}
internal sealed class AccountOperationBusyException : InvalidOperationException;
internal sealed class AccountOperationLease : IDisposable
{
    internal static AccountOperationLease Acquire() => new();
    public void Dispose() { }
}
internal static class IsolatedAccountSignIn
{
    internal static Task<SavedLogin> RunAsync(string provider, string executable, CancellationToken token)
        => throw new InvalidOperationException("Synthetic fixture must never launch a sign-in.");
    internal static IReadOnlyDictionary<string, string?> EnvironmentFor(string provider, string root)
        => new Dictionary<string, string?> { ["CODEX_HOME"] = root };
}
