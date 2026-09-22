using System.Text;
using CodeRim.Core.Services;
namespace CodeRim.Core.Tests;

public sealed class AtomicCredentialFilesTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "coderim-vault-fixture-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
    private static byte[] Bytes(string value) => Encoding.UTF8.GetBytes(value);
    [Fact]
    public async Task IndependentInstancesCommitWholeValuesUnderConcurrentSavesAndReads()
    {
        var first = new AtomicCredentialFiles(root); var second = new AtomicCredentialFiles(root);
        first.Write("provider", Bytes("initial"));
        await Task.WhenAll(Enumerable.Range(0, 8).Select(worker => Task.Run(() =>
        {
            var store = worker % 2 == 0 ? first : second;
            for (var i = 0; i < 15; i++)
            {
                var value = new string((char)('A' + worker), 65536);
                store.Write("provider", Bytes(value));
                var found = Encoding.UTF8.GetString(store.Read("provider")!);
                Assert.Equal(65536, found.Length); Assert.All(found, c => Assert.Equal(found[0], c));
            }
        }, TestContext.Current.CancellationToken)));
        Assert.Empty(Directory.EnumerateFiles(root, "*.tmp"));
    }
    [Fact]
    public async Task ManyIndependentWritersQueueBeforeTheExternalLockTimeout()
    {
        var initial = new AtomicCredentialFiles(root);
        initial.Write("provider", new byte[262144]);
        using var start = new ManualResetEventSlim();
        var writers = Enumerable.Range(0, 48).Select(worker => Task.Factory.StartNew(() =>
        {
            var store = new AtomicCredentialFiles(root);
            var bytes = Enumerable.Repeat((byte)worker, 262144).ToArray();
            start.Wait(TestContext.Current.CancellationToken);
            for (var i = 0; i < 8; i++)
            {
                store.Write("provider", bytes);
                var actual = store.Read("provider")!;
                Assert.Equal(bytes.Length, actual.Length);
                Assert.True(actual.All(value => value == actual[0]));
            }
        }, TestContext.Current.CancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
        start.Set();
        await Task.WhenAll(writers);
        Assert.Empty(Directory.EnumerateFiles(root, "*.tmp"));
    }
    [Fact]
    public async Task FailedExternalLockDoesNotPoisonLaterLocalCommits()
    {
        var store = new AtomicCredentialFiles(root); store.Write("provider", Bytes("before"));
        using (var external = new FileStream(Path.Combine(root, ".commit.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            await Assert.ThrowsAsync<IOException>(() => Task.Run(() => store.Write("provider", Bytes("rejected")),
                TestContext.Current.CancellationToken));
        }
        var second = new AtomicCredentialFiles(root);
        Assert.Equal("before", Encoding.UTF8.GetString(second.Read("provider")!));
        second.Write("provider", Bytes("after"));
        Assert.Equal("after", Encoding.UTF8.GetString(store.Read("provider")!));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DelayedRefreshCannotOverwriteReplacementOrResurrectDeletedAccount(bool delete)
    {
        var store = new AtomicCredentialFiles(root); var other = new AtomicCredentialFiles(root);
        store.Write("provider", Bytes("account-A"));
        var old = AtomicCredentialFiles.Version(store.Read("provider")!);
        if (delete) other.Delete("provider"); else other.Write("provider", Bytes("account-B"));
        Assert.False(store.CompareExchange("provider", old, Bytes("rotated-account-A")));
        Assert.Equal(delete ? null : "account-B", other.Read("provider") is { } value ? Encoding.UTF8.GetString(value) : null);
    }
    [Fact]
    public async Task ConcurrentRefreshCommitsUseOneSharedCompareExchangeBoundary()
    {
        var store = new AtomicCredentialFiles(root); store.Write("provider", Bytes("old"));
        var version = AtomicCredentialFiles.Version(store.Read("provider")!);
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 12).Select(i => Task.Run(() =>
            new AtomicCredentialFiles(root).CompareExchange("provider", version, Bytes("rotated-" + i)), TestContext.Current.CancellationToken)));
        Assert.Single(outcomes, x => x); Assert.StartsWith("rotated-", Encoding.UTF8.GetString(store.Read("provider")!));
    }
    [Fact]
    public async Task RefreshLeaseAllowsAccountEditsAndWaitCanBeCancelled()
    {
        var first = new AtomicCredentialFiles(root); var second = new AtomicCredentialFiles(root);
        using var lease = await first.AcquireRefreshAsync("factory", TestContext.Current.CancellationToken);
        first.Write("provider", Bytes("new-account")); Assert.Equal("new-account", Encoding.UTF8.GetString(second.Read("provider")!));
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken); cancel.CancelAfter(100);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second.AcquireRefreshAsync("factory", cancel.Token));
        using var different = await second.AcquireRefreshAsync("groq", TestContext.Current.CancellationToken);
        Assert.True(different.CanWrite);
    }
    [Fact]
    public async Task ConcurrentDeleteAndLoadReturnMissingOrWholeEntry()
    {
        var first = new AtomicCredentialFiles(root); var second = new AtomicCredentialFiles(root);
        var text = new string('Q', 8192);
        await Task.WhenAll(Task.Run(() =>
        {
            for (var i = 0; i < 100; i++) { first.Write("provider", Bytes(text)); first.Delete("provider"); }
        }, TestContext.Current.CancellationToken), Task.Run(() =>
        {
            for (var i = 0; i < 150; i++) if (second.Read("provider") is { } value) Assert.Equal(text, Encoding.UTF8.GetString(value));
        }, TestContext.Current.CancellationToken));
    }
    [Fact]
    public async Task ConcurrentFirstImportsCannotOverwriteAnAlreadyCreatedEntry()
    {
        var stores = Enumerable.Range(0, 12).Select(_ => new AtomicCredentialFiles(root)).ToArray();
        var outcomes = await Task.WhenAll(stores.Select((store, i) => Task.Run(() =>
            store.CompareExchange("new-profile", null, Bytes("import-" + i)), TestContext.Current.CancellationToken)));
        Assert.Single(outcomes, won => won);
        var existing = stores[0].Read("new-profile")!;
        Assert.False(stores[0].CompareExchange("new-profile", null, Bytes("late-import")));
        Assert.Equal(existing, stores[0].Read("new-profile"));
    }
    [Fact]
    public void SizeAndEntryPathAreBounded()
    {
        var store = new AtomicCredentialFiles(root);
        Assert.Throws<InvalidDataException>(() => store.Write("provider", new byte[AtomicCredentialFiles.MaximumBytes + 1]));
        store.Write("../outside", Bytes("fixture"));
        Assert.Single(Directory.EnumerateFiles(root, "*.bin")); Assert.Equal("fixture", Encoding.UTF8.GetString(store.Read("../outside")!));
        Assert.Null(store.Read("missing"));
    }
}
