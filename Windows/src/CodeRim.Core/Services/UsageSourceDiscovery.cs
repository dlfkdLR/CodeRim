namespace CodeRim.Core.Services;

internal static class UsageSourceDiscovery
{
    internal static SourceDiscoveryResult Discover(IReadOnlyList<string> roots, bool requireCompleteSources,
        int maximumSourceCount, long maximumSourceBytes, long maximumTotalSourceBytes, CancellationToken cancellationToken)
    {
        var sources = new List<string>();
        var identities = new HashSet<SourceFileIdentity>();
        var totalSourceBytes = 0L;
        var resourceLimitReached = false;
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = !requireCompleteSources,
            AttributesToSkip = FileAttributes.Hidden
                | FileAttributes.System
                | FileAttributes.ReparsePoint,
            MatchCasing = MatchCasing.CaseInsensitive,
            MaxRecursionDepth = 64,
            ReturnSpecialDirectories = false
        };

        foreach (var root in roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (requireCompleteSources)
            {
                try
                {
                    if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("Local session root is a reparse point.");
                }
                catch (Exception error) when (error is DirectoryNotFoundException or FileNotFoundException) { continue; }
            }
            if (!Directory.Exists(root) || (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            {
                continue;
            }

            var rootPrefix = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
            foreach (var candidate in Directory.EnumerateFiles(root, "*.jsonl", options).Take(maximumSourceCount + 1).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var fullPath = Path.GetFullPath(candidate);
                if (!fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (sources.Count >= maximumSourceCount)
                {
                    resourceLimitReached = true;
                    break;
                }

                var info = new FileInfo(fullPath);
                if (info.Length > maximumSourceBytes)
                {
                    resourceLimitReached = true;
                    continue;
                }
                var identity = SourceFileIdentity.TryRead(fullPath);
                if (identity is { } value && !identities.Add(value))
                {
                    continue;
                }
                if (info.Length > maximumTotalSourceBytes - totalSourceBytes)
                {
                    resourceLimitReached = true;
                    continue;
                }

                sources.Add(fullPath);
                totalSourceBytes += info.Length;
            }
        }

        sources.Sort(StringComparer.OrdinalIgnoreCase);
        return new SourceDiscoveryResult(sources, resourceLimitReached);
    }

}

internal sealed record SourceDiscoveryResult(List<string> Sources, bool ResourceLimitReached);
