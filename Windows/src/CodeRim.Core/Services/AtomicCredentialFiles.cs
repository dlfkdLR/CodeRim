using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace CodeRim.Core.Services;

/// <summary>Cooperating processes serialize byte-entry commits in a caller-protected directory.</summary>
public sealed class AtomicCredentialFiles
{
    public const int MaximumBytes = 1_048_576;
    private readonly string root;
    public AtomicCredentialFiles(string directory)
    {
        root = Path.GetFullPath(directory); Directory.CreateDirectory(root); Check(root);
    }
    public byte[]? Read(string key)
    {
        using var lease = Acquire(); return ReadUnlocked(PathFor(key));
    }
    public void Write(string key, byte[] value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length > MaximumBytes) throw new InvalidDataException("Credential entry is too large.");
        using var lease = Acquire(); WriteUnlocked(PathFor(key), value);
    }
    public bool CompareExchange(string key, string expectedVersion, byte[] value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length > MaximumBytes) throw new InvalidDataException("Credential entry is too large.");
        using var lease = Acquire();
        var path = PathFor(key); var current = ReadUnlocked(path);
        if (current is null || Version(current) != expectedVersion) return false;
        WriteUnlocked(path, value); return true;
    }
    public void Delete(string key)
    {
        using var lease = Acquire(); var path = PathFor(key); Check(path); File.Delete(path);
    }
    public static string Version(byte[] value) => Convert.ToHexString(SHA256.HashData(value));
    // Never delete lock files: replacing their inode would split the commit boundary.
    public async Task<FileStream> AcquireRefreshAsync(string key, CancellationToken token)
    {
        var path = PathFor("refresh:" + key) + ".lock";
        var timer = Stopwatch.StartNew();
        while (true)
        {
            token.ThrowIfCancellationRequested(); Check(root); Check(path);
            try { return OpenLock(path); }
            catch (IOException) when (timer.Elapsed < TimeSpan.FromSeconds(45))
            { await Task.Delay(25, token).ConfigureAwait(false); }
        }
    }
    private FileStream Acquire()
    {
        var path = Path.Combine(root, ".commit.lock"); var timer = Stopwatch.StartNew();
        while (true)
        {
            Check(root); Check(path);
            try { return OpenLock(path); }
            catch (IOException) when (timer.Elapsed < TimeSpan.FromSeconds(2)) { Thread.Sleep(5); }
        }
    }
    private static FileStream OpenLock(string path) => new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    private string PathFor(string key) => Path.Combine(root, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))) + ".bin");
    private static void Check(string path)
    {
        if (Path.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Linked credential entries are not supported.");
    }
    private static byte[]? ReadUnlocked(string path)
    {
        Check(path);
        try
        {
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (input.Length > MaximumBytes) throw new InvalidDataException("Credential entry is too large.");
            using var output = new MemoryStream();
            var buffer = new byte[16384]; int read;
            while ((read = input.Read(buffer)) > 0)
            {
                if (output.Length + read > MaximumBytes) throw new InvalidDataException("Credential entry is too large.");
                output.Write(buffer, 0, read);
            }
            return output.ToArray();
        }
        catch (FileNotFoundException) { return null; }
    }
    private static void WriteUnlocked(string path, byte[] value)
    {
        Check(path); var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(value); stream.Flush(true); }
            Check(path); File.Move(temporary, path, true);
        }
        finally { File.Delete(temporary); }
    }
}
