using System.Buffers;
using System.Security.Cryptography;
using CodeRim.Core.Domain;
using CodeRim.Core.Parsing;

namespace CodeRim.Core.Services;

internal static class UsageSourceReader
{
    internal static UsageSourceReadResult Read(string path, int maximumEvents, long maximumBytes,
        string provider, byte[]? projectKey, bool requireCompleteSources, CancellationToken cancellationToken)
    {
        var session = new UsageLogSession(path, maximumEvents, provider, projectKey, requireCompleteSources);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan);
        var identity = SourceFileIdentity.TryRead(stream.SafeFileHandle);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var line in BoundedLineReader.Read(stream, maximumBytes, cancellationToken, hash))
            if (!session.Consume(line.Bytes, line.Oversized)) break;
        var parsed = session.Complete();
        return new UsageSourceReadResult(parsed.Events, parsed.Partial, parsed.ResourceLimitReached,
            parsed.ResourceLimitReached ? null : hash.GetHashAndReset(), identity, parsed.Details)
            { UncountedUsage = parsed.UncountedUsage };
    }

    internal static bool VerifyPrefix(string path, long length, UsageSourceReadResult parsed, CancellationToken cancellationToken)
    {
        if (parsed.PrefixHash is null) return false;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan);
        if (SourceFileIdentity.TryRead(stream.SafeFileHandle) != parsed.Identity || stream.Length < length) return false;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            var remaining = length;
            while (remaining > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                if (read == 0) return false;
                hash.AppendData(buffer, 0, read);
                remaining -= read;
            }
            return CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), parsed.PrefixHash)
                && SourceFileIdentity.TryRead(path) == parsed.Identity;
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

}

internal sealed record UsageSourceReadResult(IReadOnlyList<UsageEvent> Events, bool Partial, bool ResourceLimitReached,
    byte[]? PrefixHash, SourceFileIdentity? Identity, SessionDetails? Details)
{
    public bool UncountedUsage { get; init; }
}
